using System.Diagnostics;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// PROC-03 (output drain) and PROC-04 (outcome model) against real, short-lived children.
///
/// AUDIT-ID CORRECTION: Batch 1A had these two swapped — it used PROC-03 for the outcome
/// model and PROC-04 for the drain. The canonical mapping used here is
/// PROC-03 = stdout/stderr fully drained before returning,
/// PROC-04 = timeout / cancellation / start failure / genuine exit are distinguishable.
///
/// Isolation contract: bounded-lifetime children only, Log redirected to a temp folder,
/// no network, no device, no elevation, and an orphan sweep.
/// </summary>
[Collection(GlobalStateCollection.Name)]
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
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        Log.DirOverride = _previousOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    /// <summary>A PowerShell snippet with UTF-8 output, as a single argv element.</summary>
    private static string[] Ps(string script) =>
    [
        "-NoProfile", "-NonInteractive", "-Command",
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + script,
    ];

    private static string[] CmdC(string command) => ["/c", command];

    // ---------------------------------------------------------------- exit codes

    [Fact]
    public async Task ZeroExitCode_ReportsExitedAndSuccess()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("exit 0"), 15000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Success);
    }

    [Fact]
    public async Task NonZeroExitCode_ReportsExitedAndPreservesTheCode()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("exit 7"), 15000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        Assert.Equal(7, r.ExitCode);
        Assert.False(r.Success);
    }

    // ---------------------------------------------------------------- stream capture

    [Fact]
    public async Task StdOut_IsCapturedAndTrimmed()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("echo hello-stdout"), 15000);

        Assert.Equal("hello-stdout", r.StdOut);
        Assert.Equal("", r.StdErr);
    }

    [Fact]
    public async Task StdErr_IsCapturedSeparately()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("echo hello-stderr 1>&2"), 15000);

        Assert.Equal("", r.StdOut);
        Assert.Equal("hello-stderr", r.StdErr);
    }

    [Fact]
    public async Task StdOutAndStdErr_AreCapturedSimultaneously()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("echo out-line & echo err-line 1>&2"), 15000);

        Assert.Equal("out-line", r.StdOut);
        Assert.Equal("err-line", r.StdErr);
    }

    [Fact]
    public async Task OutputProperty_PrefersStdOutThenFallsBackToStdErr()
    {
        var withOut = await ProcessRunner.RunAsync(Cmd, CmdC("echo has-stdout"), 15000);
        Assert.Equal("has-stdout", withOut.Output);

        var onlyErr = await ProcessRunner.RunAsync(Cmd, CmdC("echo only-stderr 1>&2"), 15000);
        Assert.Equal("only-stderr", onlyErr.Output);
    }

    [Fact]
    public async Task MultiLineOutput_PreservesOrder()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("echo one& echo two& echo three"), 15000);

        Assert.Equal(["one", "two", "three"],
            r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    // ---------------------------------------------------------------- encoding

    [Fact]
    public async Task ArabicStdOut_SurvivesAsUtf8()
    {
        var r = await ProcessRunner.RunAsync(PowerShell, Ps("Write-Output 'مرحبا بالعالم'"), 30000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("مرحبا بالعالم", r.StdOut);
    }

    [Fact]
    public async Task ArabicStdErr_SurvivesAsUtf8()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("[Console]::Error.WriteLine('خطأ في التنفيذ')"), 30000);

        // Exit code 0 proves the snippet actually ran: a PowerShell error would exit
        // non-zero AND echo the offending source line (including this Arabic text) into
        // stderr, which would make a bare Contains() assertion pass falsely.
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("خطأ في التنفيذ", r.StdErr);
        Assert.Equal("", r.StdOut);
    }

    [Fact]
    public async Task MixedUnicodeOutput_SurvivesAsUtf8()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("Write-Output 'Galaxy — هاتف — 日本語'"), 30000);

        Assert.Equal("Galaxy — هاتف — 日本語", r.StdOut);
    }

    [Theory] // arguments reach the child as separate argv entries, without shell interpretation
    [InlineData("a b & c; d | e")]
    [InlineData("quote\"inside")]
    [InlineData("$(whoami)")]
    [InlineData("`whoami`")]
    [InlineData("%USERPROFILE%")]
    [InlineData("هاتف محمد")]
    [InlineData("--record")]
    public async Task ArgumentsAreDeliveredLiterallyAsSingleArgv(string hostile)
    {
        // A script file is used because `powershell -Command` folds trailing arguments into
        // the command text instead of binding them to $args; -File binds them properly.
        Directory.CreateDirectory(_logDir);
        var script = Path.Combine(_logDir, "echoargs.ps1");
        File.WriteAllText(script,
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\r\n" +
            "Write-Output $args.Count\r\n" +
            "foreach ($a in $args) { Write-Output $a }\r\n",
            new System.Text.UTF8Encoding(false));

        var r = await ProcessRunner.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
             hostile, "tail"],
            30000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        var lines = r.StdOut.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Exactly two arguments arrived; the hostile one was not split or reinterpreted.
        Assert.Equal("2", lines[0].Trim());
        Assert.Equal(hostile, lines[1]);
        Assert.Equal("tail", lines[2]);
    }

    // ================================================================
    //  PROC-03 — stdout/stderr must be fully drained before returning
    // ================================================================

    [Fact] // PROC-03 — the exact case that was previously unassertable
    public async Task PROC03_LargeBurstWrittenImmediatelyBeforeExit_IsFullyCaptured()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("1..2000 | ForEach-Object { \"line-$_\" }"), 60000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        var lines = r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(2000, lines.Count);
        Assert.Equal("line-1", lines[0]);
        Assert.Equal("line-2000", lines[^1]);   // the tail is present, not truncated
        Assert.False(r.OutputTruncated);
    }

    [Fact] // PROC-03 — the final line before exit is never lost
    public async Task PROC03_FinalLineBeforeExit_IsCaptured()
    {
        for (var i = 0; i < 10; i++)
        {
            var r = await ProcessRunner.RunAsync(Cmd, CmdC("echo final-line"), 15000);
            Assert.Equal("final-line", r.StdOut);
        }
    }

    [Fact] // PROC-03 — both streams drain, neither starves the other
    public async Task PROC03_BothStreamsDrainUnderLoad()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell,
            Ps("1..300 | ForEach-Object { \"out-$_\"; [Console]::Error.WriteLine(\"err-$_\") }"),
            60000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        Assert.Equal(300, r.StdOut.Split('\n').Count(l => l.Trim().StartsWith("out-")));
        Assert.Equal(300, r.StdErr.Split('\n').Count(l => l.Trim().StartsWith("err-")));
    }

    [Fact] // PROC-03 — large bounded output stays supported without deadlock
    public async Task PROC03_LargeBoundedOutput_IsCapturedCompletely()
    {
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("1..500 | ForEach-Object { 'x' * 100 }"), 60000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        var lines = r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(500, lines.Count);
        Assert.All(lines, l => Assert.Equal(100, l.Length));
        Assert.False(r.OutputTruncated);
    }

    [Fact] // PROC-03 — output beyond the documented cap is dropped and flagged
    public async Task PROC03_OutputBeyondTheCap_IsTruncatedAndFlagged()
    {
        // ~1.2 M characters, comfortably past MaxCapturedChars (1 M).
        var r = await ProcessRunner.RunAsync(
            PowerShell, Ps("1..1200 | ForEach-Object { 'y' * 1000 }"), 120000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        Assert.True(r.OutputTruncated);
        // Policy: the head is retained, bounded by the cap.
        Assert.True(r.StdOut.Length <= ProcessRunner.MaxCapturedChars,
            $"captured {r.StdOut.Length} > cap {ProcessRunner.MaxCapturedChars}");
        Assert.StartsWith("yyy", r.StdOut);
    }

    [Fact] // PROC-03 — a normal command is never flagged as truncated
    public async Task PROC03_SmallOutput_IsNotFlaggedAsTruncated()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("echo small"), 15000);

        Assert.False(r.OutputTruncated);
    }

    // ================================================================
    //  PROC-04 — every failure mode is distinguishable
    // ================================================================

    [Fact] // PROC-04 — the four outcomes are all distinct
    public async Task PROC04_TimeoutCancellationStartFailureAndExit_AreAllDistinguishable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".exe");

        var exited = await ProcessRunner.RunAsync(Cmd, CmdC("exit 3"), 15000);
        var startFailed = await ProcessRunner.RunAsync(missing, [], 5000);
        var timedOut = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1200);

        using var cts = new CancellationTokenSource();
        var cancelTask = ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 60000, cts.Token);
        cts.CancelAfter(300);
        var cancelled = await cancelTask;

        Assert.Equal(ProcessOutcome.Exited, exited.Outcome);
        Assert.Equal(ProcessOutcome.StartFailed, startFailed.Outcome);
        Assert.Equal(ProcessOutcome.TimedOut, timedOut.Outcome);
        Assert.Equal(ProcessOutcome.Cancelled, cancelled.Outcome);

        // All four differ from each other.
        var outcomes = new[] { exited.Outcome, startFailed.Outcome, timedOut.Outcome, cancelled.Outcome };
        Assert.Equal(4, outcomes.Distinct().Count());
    }

    [Fact] // PROC-04 — a genuine -1 exit is no longer confused with a failure
    public async Task PROC04_GenuineExitCodeMinusOne_IsReportedAsARealExit()
    {
        var r = await ProcessRunner.RunAsync(Cmd, CmdC("exit -1"), 15000);

        Assert.Equal(ProcessOutcome.Exited, r.Outcome);
        Assert.Equal(-1, r.ExitCode);
        Assert.False(r.Success);

        // A start failure with the same "-1 feel" is a different outcome entirely.
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".exe");
        var startFailed = await ProcessRunner.RunAsync(missing, [], 5000);

        Assert.NotEqual(r.Outcome, startFailed.Outcome);
        Assert.Null(startFailed.ExitCode);
    }

    [Fact] // PROC-04 — ExitCode is only meaningful for a real exit
    public async Task PROC04_ExitCodeIsNullForEveryNonExitOutcome()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".exe");

        Assert.Null((await ProcessRunner.RunAsync(missing, [], 5000)).ExitCode);
        Assert.Null((await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1200)).ExitCode);
    }

    [Fact] // PROC-04 — callers never need to parse Arabic text to classify a failure
    public async Task PROC04_FailureTypeIsAvailableWithoutParsingText()
    {
        var timedOut = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1200);

        Assert.Equal(ProcessOutcome.TimedOut, timedOut.Outcome);
        // The Arabic message exists for the UI, but classification does not depend on it.
        Assert.NotEqual("", timedOut.Message);
    }

    [Fact] // PROC-04 — start failure keeps a safe diagnostic, not a stack trace
    public async Task PROC04_StartFailure_RetainsSafeDiagnosticContext()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".exe");

        var r = await ProcessRunner.RunAsync(missing, ["--version"], 5000);

        Assert.Equal(ProcessOutcome.StartFailed, r.Outcome);
        Assert.False(r.Success);
        Assert.NotNull(r.FailureDetail);
        Assert.NotEqual("", r.FailureDetail);

        // The user-facing message stays clean.
        Assert.DoesNotContain("   at ", r.Message);
        Assert.Contains("تعذّر", r.Message);
    }

    [Fact] // PROC-04 — cancellation and timeout are not the same thing
    public async Task PROC04_CancellationIsNotReportedAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        var task = ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 60000, cts.Token);
        cts.CancelAfter(300);

        var r = await task;

        Assert.Equal(ProcessOutcome.Cancelled, r.Outcome);
        Assert.NotEqual(ProcessOutcome.TimedOut, r.Outcome);
    }

    [Fact]
    public async Task PreCancelledToken_ReturnsPromptlyAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 60000, cts.Token);

        Assert.Equal(ProcessOutcome.Cancelled, r.Outcome);
    }

    // ---------------------------------------------------------------- timeout behaviour

    [Fact]
    public async Task Timeout_ReturnsPromptlyWithATimedOutOutcome()
    {
        var sw = Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync(PowerShell, Ps("Start-Sleep -Seconds 30"), 1500);
        sw.Stop();

        Assert.Equal(ProcessOutcome.TimedOut, r.Outcome);
        Assert.False(r.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    // Liveness is proven with a heartbeat file rather than by counting powershell PIDs:
    // a global PID count is polluted by any other test (or any unrelated shell on the
    // machine) that happens to run concurrently, which makes such a test both flaky and
    // capable of passing for the wrong reason.

    /// <summary>Writes a script that rewrites <c>$path</c> every 150 ms, forever.</summary>
    private string WriteHeartbeatScript()
    {
        Directory.CreateDirectory(_logDir);
        var script = Path.Combine(_logDir, "heartbeat.ps1");
        File.WriteAllText(script,
            "param($path)\r\n" +
            "while ($true) {\r\n" +
            "  Set-Content -LiteralPath $path -Value ([DateTime]::UtcNow.Ticks)\r\n" +
            "  Start-Sleep -Milliseconds 150\r\n" +
            "}\r\n",
            new System.Text.UTF8Encoding(false));
        return script;
    }

    /// <summary>Asserts the heartbeat file stops changing, i.e. its writer is dead.</summary>
    private static async Task AssertHeartbeatStopped(string beatFile, string what)
    {
        Assert.True(File.Exists(beatFile), $"{what} never started, so the test proves nothing");

        // Allow any in-flight write to land, then require two identical samples.
        await Task.Delay(600);
        var first = File.ReadAllText(beatFile);
        await Task.Delay(1200);
        var second = File.ReadAllText(beatFile);

        Assert.True(first == second, $"{what} is still running after the process was killed");
    }

    [Fact]
    public async Task Timeout_LeavesNoRunningChildProcess()
    {
        var script = WriteHeartbeatScript();
        var beat = Path.Combine(_logDir, "beat-timeout.txt");

        var r = await ProcessRunner.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, beat],
            2000);

        Assert.Equal(ProcessOutcome.TimedOut, r.Outcome);
        await AssertHeartbeatStopped(beat, "the timed-out child");
    }

    [Fact] // the whole tree goes, not just the direct child
    public async Task Timeout_KillsTheProcessTree()
    {
        var heartbeat = WriteHeartbeatScript();
        var beat = Path.Combine(_logDir, "beat-tree.txt");

        // Parent starts a detached grandchild heartbeat, then blocks well past the timeout.
        var parent = Path.Combine(_logDir, "parent.ps1");
        File.WriteAllText(parent,
            "param($ps, $script, $path)\r\n" +
            "Start-Process -NoNewWindow -FilePath $ps -ArgumentList " +
            "'-NoProfile','-ExecutionPolicy','Bypass','-File',$script,$path\r\n" +
            "Start-Sleep -Seconds 60\r\n",
            new System.Text.UTF8Encoding(false));

        var r = await ProcessRunner.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", parent,
             PowerShell, heartbeat, beat],
            4000);

        Assert.Equal(ProcessOutcome.TimedOut, r.Outcome);
        await AssertHeartbeatStopped(beat, "the grandchild");
    }

    [Fact]
    public async Task Cancellation_LeavesNoRunningChildProcess()
    {
        var script = WriteHeartbeatScript();
        var beat = Path.Combine(_logDir, "beat-cancel.txt");

        using var cts = new CancellationTokenSource();
        var task = ProcessRunner.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, beat],
            60000, cts.Token);
        cts.CancelAfter(1500);

        var r = await task;

        Assert.Equal(ProcessOutcome.Cancelled, r.Outcome);
        await AssertHeartbeatStopped(beat, "the cancelled child");
    }

    // ---------------------------------------------------------------- failure modes

    [Fact]
    public async Task MissingExecutable_ReportsStartFailedInsteadOfThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid() + ".exe");

        var r = await ProcessRunner.RunAsync(missing, ["--version"], 5000);

        Assert.Equal(ProcessOutcome.StartFailed, r.Outcome);
        Assert.False(r.Success);
        Assert.Null(r.ExitCode);
    }

    // ---------------------------------------------------------------- orphan sweep

    [Fact]
    public async Task RunAsync_DoesNotLeakChildProcesses()
    {
        var before = Process.GetProcessesByName("cmd").Select(p => p.Id).ToHashSet();

        for (var i = 0; i < 5; i++)
            await ProcessRunner.RunAsync(Cmd, CmdC("exit 0"), 15000);

        var leaked = Process.GetProcessesByName("cmd")
            .Where(p => !before.Contains(p.Id))
            .Where(p => { try { return !p.HasExited; } catch { return false; } })
            .ToList();

        Assert.Empty(leaked);
    }
}
