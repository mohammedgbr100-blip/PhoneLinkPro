using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Independent-review follow-up (Batch 4D, second review) — behaviorally protects the
/// CancellationToken/timeout/argument chain from <see cref="AdbService.PushAsync"/> and
/// <see cref="AdbService.PullAsync"/> all the way down to the final process-run boundary
/// (<see cref="AdbService.ProcessRunOverrideForTests"/>, which sits exactly where <c>Adb()</c>
/// would otherwise call <see cref="ProcessRunner.RunAsync(string, IReadOnlyList{string}, int, CancellationToken)"/>).
///
/// <para>These tests invoke the REAL <see cref="AdbService.PushAsync"/>/<see cref="AdbService.PullAsync"/>
/// — unlike <c>TransfersSelfTest</c>'s view-level scenarios, which intercept at
/// <c>TransfersView.PushOverrideForTests</c>/<c>PullOverrideForTests</c> and therefore only prove
/// page-to-service propagation. This file proves the complementary half: service-to-runner
/// propagation through <c>AdbForDevice</c> → <c>Adb</c> → the final boundary, including the
/// integrity gate and the "-s" serial-prefixing/argument construction. No real adb.exe process
/// is ever started — the seam fully replaces the final <see cref="ProcessRunner"/> call.</para>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class AdbServiceTransferPropagationTests : IDisposable
{
    private static readonly EngineIntegrityResult ValidResult =
        new(EngineIntegrityStatus.Valid, null, null, 11, TimeSpan.Zero);

    private static readonly EngineIntegrityResult TamperedResult =
        new(EngineIntegrityStatus.HashMismatch, "engine/SDL2.dll", null, 11, TimeSpan.Zero);

    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public AdbServiceTransferPropagationTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-propagation-" + Guid.NewGuid().ToString("N"));
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;

        AdbService.ResetIntegrityGateForTests();
        AdbService.ResetProcessRunOverrideForTests();
        AdbService.IntegrityGate = _ => Task.FromResult(ValidResult);
    }

    public void Dispose()
    {
        AdbService.ResetProcessRunOverrideForTests();
        AdbService.ResetIntegrityGateForTests();
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { /* best effort */ }
    }

    private string ReadLogs()
    {
        if (!Directory.Exists(_logDir)) return "";
        return string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
    }

    // ---------------------------------------------------------------- Push

    [Fact] // exact argv, exact token, exact timeout reach the final runner boundary for Push
    public async Task PushAsync_PropagatesExactArgumentsTokenAndTimeout()
    {
        const string serial = "R5CX10PROPAGATE";
        const string local = @"C:\Users\test\distinctive-local-file.txt";
        const string remote = "/sdcard/Download/distinctive-remote-file.txt";

        string? capturedExe = null;
        IReadOnlyList<string>? capturedArgs = null;
        int capturedTimeout = -1;
        CancellationToken capturedToken = default;

        AdbService.ProcessRunOverrideForTests = (exe, args, timeout, ct) =>
        {
            capturedExe = exe;
            capturedArgs = args;
            capturedTimeout = timeout;
            capturedToken = ct;
            return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
        };

        using var cts = new CancellationTokenSource();
        var result = await AdbService.PushAsync(serial, local, remote, cts.Token);

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.NotNull(capturedExe);
        Assert.NotNull(capturedArgs);

        // ArgumentList entries stay separate — never concatenated into one string.
        Assert.Equal(5, capturedArgs!.Count);
        Assert.Equal("-s", capturedArgs[0]);
        Assert.Equal(serial, capturedArgs[1]);
        Assert.Equal("push", capturedArgs[2]);
        Assert.Equal(local, capturedArgs[3]);
        Assert.Equal(remote, capturedArgs[4]);

        // The exact operation token reaches the final boundary (hardcoded literal — this must
        // NOT be read back from AdbService.TransferTimeoutMs, or a mutated constant would still
        // pass this assertion against itself).
        Assert.Equal(300000, capturedTimeout);
        Assert.True(capturedToken == cts.Token);
        Assert.False(capturedToken.IsCancellationRequested);
    }

    [Fact] // cancelling the caller's source is observed at the final runner boundary for Push
    public async Task PushAsync_CancellingTheSourceCancelsTheRunnerObservedToken()
    {
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        AdbService.ProcessRunOverrideForTests = (_, _, _, ct) => { capturedToken = ct; return tcs.Task; };

        using var cts = new CancellationTokenSource();
        var pushTask = AdbService.PushAsync("R5CX10PROPAGATE", @"C:\a.txt", "/sdcard/Download/a.txt", cts.Token);

        Assert.False(capturedToken.IsCancellationRequested);

        cts.Cancel();
        Assert.True(capturedToken.IsCancellationRequested);

        tcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
        var result = await pushTask;
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
    }

    // ---------------------------------------------------------------- Pull

    [Fact] // exact argv, exact token, exact timeout reach the final runner boundary for Pull
    public async Task PullAsync_PropagatesExactArgumentsTokenAndTimeout()
    {
        const string serial = "R5CX10PROPAGATE2";
        const string remote = "/sdcard/DCIM/Camera/distinctive-remote.jpg";
        const string local = @"C:\Users\test\distinctive-local.jpg";

        string? capturedExe = null;
        IReadOnlyList<string>? capturedArgs = null;
        int capturedTimeout = -1;
        CancellationToken capturedToken = default;

        AdbService.ProcessRunOverrideForTests = (exe, args, timeout, ct) =>
        {
            capturedExe = exe;
            capturedArgs = args;
            capturedTimeout = timeout;
            capturedToken = ct;
            return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", ""));
        };

        using var cts = new CancellationTokenSource();
        var result = await AdbService.PullAsync(serial, remote, local, cts.Token);

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.NotNull(capturedExe);
        Assert.NotNull(capturedArgs);

        Assert.Equal(5, capturedArgs!.Count);
        Assert.Equal("-s", capturedArgs[0]);
        Assert.Equal(serial, capturedArgs[1]);
        Assert.Equal("pull", capturedArgs[2]);
        Assert.Equal(remote, capturedArgs[3]);
        Assert.Equal(local, capturedArgs[4]);

        Assert.Equal(300000, capturedTimeout);
        Assert.True(capturedToken == cts.Token);
        Assert.False(capturedToken.IsCancellationRequested);
    }

    [Fact] // cancelling the caller's source is observed at the final runner boundary for Pull
    public async Task PullAsync_CancellingTheSourceCancelsTheRunnerObservedToken()
    {
        CancellationToken capturedToken = default;
        var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        AdbService.ProcessRunOverrideForTests = (_, _, _, ct) => { capturedToken = ct; return tcs.Task; };

        using var cts = new CancellationTokenSource();
        var pullTask = AdbService.PullAsync("R5CX10PROPAGATE2", "/sdcard/a.jpg", @"C:\a.jpg", cts.Token);

        Assert.False(capturedToken.IsCancellationRequested);

        cts.Cancel();
        Assert.True(capturedToken.IsCancellationRequested);

        tcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
        var result = await pullTask;
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
    }

    // ---------------------------------------------------------------- integrity gating still runs first

    [Fact] // the integrity gate still runs BEFORE the final-runner seam — no bypass introduced
    public async Task IntegrityFailure_NeverReachesTheFinalRunnerSeam()
    {
        AdbService.IntegrityGate = _ => Task.FromResult(TamperedResult);
        var seamCalls = 0;
        AdbService.ProcessRunOverrideForTests = (_, _, _, _) =>
        {
            seamCalls++;
            return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
        };

        var result = await AdbService.PushAsync("R5CX10PROPAGATE", @"C:\a.txt", "/sdcard/Download/a.txt");

        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
        Assert.Equal(0, seamCalls);
    }

    // ---------------------------------------------------------------- privacy

    [Fact] // the test seam itself never writes the raw local/remote path to disk
    public async Task ProcessRunOverride_NeverLogsRawPaths()
    {
        const string local = @"C:\Users\SECRETUSER\Desktop\private-file.txt";
        const string remote = "/sdcard/Download/private-file.txt";

        AdbService.ProcessRunOverrideForTests = (_, _, _, _) =>
            Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));

        await AdbService.PushAsync("R5CX10PROPAGATE", local, remote);

        var logs = ReadLogs();
        Assert.DoesNotContain("SECRETUSER", logs);
        Assert.DoesNotContain(local, logs);
    }
}
