using System.Diagnostics;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 6 — <see cref="ProcessRunner"/> against real, short-lived child processes.
///
/// Isolation contract:
///   * every child is cmd.exe / powershell.exe with a bounded lifetime;
///   * <see cref="Log"/> is redirected to a temp folder so no test writes into
///     %AppData%\PhoneLinkPro (ProcessRunner logs on timeout and on failure);
///   * no network, no device, no elevation;
///   * an orphan sweep asserts nothing is left running.
/// </summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly string Cmd =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    private static readonly string PowerShell =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly string _logDir;
    private readonly string? _previousOverride;

    public ProcessRunnerTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-log-" + Guid.NewGuid().ToString("N"));
        _previousOverride = Log.DirOverride;
        Log.DirOverride = _logDir;      // keep ProcessRunner's Log.Warn/Error out of AppData
    }

    public void Dispose()
    {
        Log.DirOverride = _previousOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    /// <summary>Runs a PowerShell snippet with UTF-8 output, so Arabic survives the pipe.</summary>
    private static string Ps(string script) =>
        $"-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=" +
        $"[System.Text.Encoding]::UTF8; {script}\"";

    // ---------------------------------------------------------------- exit codes

    [Fact] // [REGRESSION]
    public async Task ZeroExitCode_ReportsSuccess()
    {
        var r = await ProcessRunner.RunAsync(Cmd, "/c exit 0", 15000);

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Success);
    }

    [Fact] // [REGRESSION]
    public async Task NonZeroExitCode_ReportsFailureAndPreservesTheCode()
    {
        var r = await ProcessRunner.RunAsync(Cmd, "/c exit 7", 15000);

        Assert.Equal(7, r.ExitCode);
        Assert.False(r.Success);
    }

    // ---------------------------------------------------------------- stream capture

    [Fact] // [REGRESSION]
    public async Task StdOut_IsCapturedAndTrimmed()
    {
        var r = await ProcessRunner.RunAsync(Cmd, "/c echo hello-stdout", 15000);

        Assert.Equal("hello-stdout", r.StdOut);
        Assert.Equal("", r.StdErr);
    }

    [Fact] // [REGRESSION]
    public async Task StdErr_IsCapturedSeparately()
    {
        var r = await ProcessRunner.RunAsync(Cmd, "/c echo hello-stderr 1>&2", 15000);

        Assert.Equal("", r.StdOut);
        Assert.Equal("hello-stderr", r.StdErr);
    }

    [Fact] // [REGRESSION] — both streams captured without interleaving corruption
    public async Task StdOutAndStdErr_AreCapturedSimultaneously()
    {
        var r = await ProcessRunner.RunAsync(Cmd, "/c echo out-line & echo err-line 1>&2", 15000);

        Assert.Equal("out-line", r.StdOut);
        Assert.Equal("err-line", r.StdErr);
    }

    [Fact] // [REGRESSION] — Output prefers StdOut, falls back to StdErr
    public async Task OutputProperty_PrefersStdOutThenFallsBackToStdErr()
    {
        var withOut = await ProcessRunner.RunAsync(Cmd, "/c echo has-stdout", 15000);
        Assert.Equal("has-stdout", withOut.Output);

        var onlyErr = await ProcessRunner.RunAsync(Cmd, "/c echo only-stderr 1>&2", 15000);
        Assert.Equal("only-stderr", onlyErr.Output);
    }

    [Fact] // [REGRESSION] — multi-line output keeps line order
    public async Task MultiLineOutput_PreservesOrder()
    {
        var r = await ProcessRunner.RunAsync(Cmd, "/c echo one& echo two& echo three", 15000);

        Assert.Equal(new[] { "one", "two", "three" },
            r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    // ---------------------------------------------------------------- encoding

    [Fact] // [REGRESSION] — ProcessRunner forces UTF-8 on both streams
    public async Task ArabicStdOut_SurvivesAsUtf8()
    {
        var r = await ProcessRunner.RunAsync(PowerShell, Ps("Write-Output 'مرحبا بالعالم'"), 30000);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("مرحبا بالعالم", r.StdOut);
    }

    [Fact] // [REGRESSION]
    public async Task ArabicStdErr_SurvivesAsUtf8()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell,
            Ps("[Console]::Error.WriteLine('خطأ في التنفيذ')"),
            30000);

        // Exit code 0 proves the snippet actually ran: a PowerShell parse/runtime error
        // would exit non-zero AND echo the offending source line (including this Arabic
        // text) into stderr, which would make a bare Contains() assertion pass falsely.
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("خطأ في التنفيذ", r.StdErr);
        Assert.Equal("", r.StdOut);
    }

    [Fact] // [REGRESSION] — mixed scripts round-trip
    public async Task MixedUnicodeOutput_SurvivesAsUtf8()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("Write-Output 'Galaxy — هاتف — 日本語'"), 30000);

        Assert.Equal("Galaxy — هاتف — 日本語", r.StdOut);
    }

    // ---------------------------------------------------------------- bounded volume

    [Fact] // [REGRESSION] — a large but bounded payload is captured completely
    public async Task LargeBoundedOutput_IsCapturedCompletely()
    {
        // 500 lines of 100 chars ≈ 50 KB, well inside the pipe-drain path.
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("1..500 | ForEach-Object { 'x' * 100 }"), 60000);

        Assert.Equal(0, r.ExitCode);
        var lines = r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(500, lines.Count);
        Assert.All(lines, l => Assert.Equal(100, l.Length));
    }

    // ---------------------------------------------------------------- timeout

    [Fact] // [REGRESSION] — a hung child is killed and reported
    public async Task Timeout_KillsTheChildAndReturnsTheArabicTimeoutMessage()
    {
        var sw = Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1500);
        sw.Stop();

        Assert.Equal(-1, r.ExitCode);
        Assert.False(r.Success);
        Assert.Contains("انتهت مهلة تنفيذ الأمر", r.StdErr);
        // Returned promptly rather than waiting out the child.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact] // [REGRESSION] — the killed child is really gone
    public async Task Timeout_LeavesNoRunningChildProcess()
    {
        var before = Process.GetProcessesByName("powershell").Select(p => p.Id).ToHashSet();

        await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1500);

        // Give the OS a moment to reap, then confirm no new powershell survived.
        for (var i = 0; i < 20; i++)
        {
            var leaked = Process.GetProcessesByName("powershell")
                .Where(p => !before.Contains(p.Id))
                .Where(p => { try { return !p.HasExited; } catch { return false; } })
                .ToList();
            if (leaked.Count == 0) return;
            await Task.Delay(100);
        }

        var still = Process.GetProcessesByName("powershell").Where(p => !before.Contains(p.Id)).ToList();
        Assert.True(still.Count == 0, $"{still.Count} child process(es) leaked after timeout");
    }

    // ---------------------------------------------------------------- cancellation

    [Fact] // [CHARACTERIZATION] PROC-03 — external cancellation is reported as a timeout
    public async Task PROC03_ExternalCancellation_IsIndistinguishableFromTimeout()
    {
        using var cts = new CancellationTokenSource();
        var task = ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 60000, cts.Token);
        cts.CancelAfter(300);

        var r = await task;

        // The caller asked to cancel, but the result claims the command timed out.
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("انتهت مهلة تنفيذ الأمر", r.StdErr);
    }

    [Fact] // [REGRESSION] — an already-cancelled token still terminates promptly
    public async Task PreCancelledToken_ReturnsPromptly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 60000, cts.Token);

        Assert.Equal(-1, r.ExitCode);
    }

    // ---------------------------------------------------------------- failure modes

    [Fact] // [REGRESSION] — a missing executable does not throw
    public async Task MissingExecutable_ReturnsFailureInsteadOfThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid() + ".exe");

        var r = await ProcessRunner.RunAsync(missing, "--version", 5000);

        Assert.Equal(-1, r.ExitCode);
        Assert.False(r.Success);
        Assert.NotEqual("", r.StdErr);
    }

    // ================================================================
    //  PROC-03 — timeout and start-failure are indistinguishable
    // ================================================================
    // Both paths return ExitCode -1. A caller cannot tell "the tool is missing/failed to
    // start" from "the tool ran too long", so the UI cannot give the right advice.
    // Pinned here; the fix (distinct codes or a typed result) belongs to a later batch.

    [Fact] // [CHARACTERIZATION] PROC-03
    public async Task PROC03_TimeoutAndStartFailure_ShareExitCodeMinusOne()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".exe");

        var startFailure = await ProcessRunner.RunAsync(missing, "", 5000);
        var timedOut = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1200);

        // Identical exit codes for two completely different failures.
        Assert.Equal(-1, startFailure.ExitCode);
        Assert.Equal(-1, timedOut.ExitCode);
        Assert.Equal(startFailure.ExitCode, timedOut.ExitCode);

        // Only the stderr text differs, and only in a human-readable way.
        Assert.DoesNotContain("انتهت مهلة", startFailure.StdErr);
        Assert.Contains("انتهت مهلة", timedOut.StdErr);
    }

    [Fact] // [CHARACTERIZATION] PROC-03 — a real tool can legitimately exit with -1 too
    public async Task PROC03_GenuineExitCodeMinusOne_CollidesWithTheFailureSentinel()
    {
        // 0xFFFFFFFF is returned to .NET as -1, colliding with the sentinel.
        var r = await ProcessRunner.RunAsync(Cmd, "/c exit -1", 15000);

        Assert.Equal(-1, r.ExitCode);
        // Indistinguishable from timeout/start-failure by exit code alone.
        Assert.Equal("", r.StdErr);
    }

    [Fact(Skip = "PROC-03: timeout, start failure and a genuine -1 exit all surface as " +
                 "ExitCode -1, so callers cannot react correctly. Asserts the CORRECT " +
                 "post-fix behaviour (distinguishable outcomes). Target batch: 1B.")]
    public async Task PROC03_FailureModesShouldBeDistinguishable_TargetBatch1B()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".exe");

        var startFailure = await ProcessRunner.RunAsync(missing, "", 5000);
        var timedOut = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1200);

        Assert.NotEqual(startFailure.ExitCode, timedOut.ExitCode);
    }

    // ================================================================
    //  PROC-04 — output drain is not guaranteed before the result is built
    // ================================================================
    // RunAsync awaits WaitForExitAsync(token), which completes when the process object
    // signals exit. The asynchronous OutputDataReceived / ErrorDataReceived callbacks are
    // NOT guaranteed to have flushed at that moment (the parameterless WaitForExit() is
    // what waits for stream EOF). Trailing output can therefore be missing.
    //
    // This is a race, so it is characterized as "usually complete" rather than asserted
    // to fail — a flaky test would be worse than none. The skipped test below states the
    // guarantee that should hold after the fix.

    [Fact] // [CHARACTERIZATION] PROC-04
    public async Task PROC04_OutputImmediatelyBeforeExit_IsUsuallyButNotProvablyComplete()
    {
        // Small payload written right before exit: in practice this normally arrives,
        // but nothing in RunAsync guarantees it.
        var r = await ProcessRunner.RunAsync(Cmd, "/c echo final-line", 15000);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("final-line", r.StdOut);
    }

    [Fact(Skip = "PROC-04: RunAsync awaits WaitForExitAsync(token), which does not wait for " +
                 "the async stdout/stderr callbacks to drain, so trailing output can be lost. " +
                 "A deterministic assertion needs the drain fix (await the parameterless " +
                 "WaitForExit / stream-EOF tasks) before it can be written without flakiness. " +
                 "Target batch: 1B.")]
    public async Task PROC04_AllOutputMustBeDrainedBeforeReturning_TargetBatch1B()
    {
        // After the fix this must hold for a burst written immediately before exit.
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("1..2000 | ForEach-Object { \"line-$_\" }"), 60000);

        var lines = r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(2000, lines.Count);
    }

    // ---------------------------------------------------------------- orphan sweep

    [Fact] // [REGRESSION] — the suite itself must not leak processes
    public async Task RunAsync_DoesNotLeakChildProcesses()
    {
        var before = Process.GetProcessesByName("cmd").Select(p => p.Id).ToHashSet();

        for (var i = 0; i < 5; i++)
            await ProcessRunner.RunAsync(Cmd, "/c exit 0", 15000);

        var leaked = Process.GetProcessesByName("cmd")
            .Where(p => !before.Contains(p.Id))
            .Where(p => { try { return !p.HasExited; } catch { return false; } })
            .ToList();

        Assert.Empty(leaked);
    }
}
