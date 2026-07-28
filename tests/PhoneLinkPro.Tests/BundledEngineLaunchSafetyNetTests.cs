using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Repair batch 5B (§7) — the structural safety net that refuses a launch of exactly the bundled
/// Bridge (adb) / Engine (scrcpy) executables from an automated or xUnit quality-gate path, even
/// if some future test forgets to override a higher-level seam. Installed for the WHOLE xUnit
/// process by TestProcessLogIsolation's module initializer (proven separately in
/// TestProcessLogIsolationTests), so it is already active here — these tests only prove its
/// actual blocking behavior at the two real choke points (ProcessRunner.RunAsync and
/// SystemProcessLauncher.Start), and that it leaves every OTHER executable alone.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class BundledEngineLaunchSafetyNetTests : IDisposable
{
    public BundledEngineLaunchSafetyNetTests() => ProcessRunner.ResetBlockedBundledEngineLaunchCountForTests();

    public void Dispose() => ProcessRunner.ResetBlockedBundledEngineLaunchCountForTests();

    [Fact]
    public void SafetyNet_IsActiveForThisProcess()
        => Assert.True(ProcessRunner.BundledEngineLaunchesAreBlockedForTests);

    [Fact] // [REGRESSION] a direct ProcessRunner.RunAsync call against the real bundled adb path
           // must be refused, never actually started, even with no other seam involved
    public async Task RunAsync_RefusesTheBundledAdbPath()
    {
        var before = ProcessRunner.BlockedBundledEngineLaunchCountForTests;

        var result = await ProcessRunner.RunAsync(ToolLocator.AdbPath, AdbService.DevicesArgs, 5000);

        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
        Assert.Contains("safety net", result.FailureDetail);
        Assert.Equal(before + 1, ProcessRunner.BlockedBundledEngineLaunchCountForTests);
    }

    [Fact] // [REGRESSION] a direct ProcessRunner.RunAsync call against the real bundled scrcpy
           // (engine) path must likewise be refused
    public async Task RunAsync_RefusesTheBundledScrcpyPath()
    {
        var before = ProcessRunner.BlockedBundledEngineLaunchCountForTests;

        var result = await ProcessRunner.RunAsync(ToolLocator.ScrcpyPath, Array.Empty<string>(), 5000);

        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
        Assert.Equal(before + 1, ProcessRunner.BlockedBundledEngineLaunchCountForTests);
    }

    [Fact] // the safety net checks the exact bundled paths only — an unrelated executable (that
           // merely happens not to exist) is refused for the ordinary reason, not this seam
    public async Task RunAsync_DoesNotBlockAnUnrelatedExecutable()
    {
        var before = ProcessRunner.BlockedBundledEngineLaunchCountForTests;
        var unrelated = Path.Combine(Path.GetTempPath(), "not-adb-or-scrcpy-" + Guid.NewGuid() + ".exe");

        var result = await ProcessRunner.RunAsync(unrelated, Array.Empty<string>(), 5000);

        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
        Assert.DoesNotContain("safety net", result.FailureDetail ?? "");
        Assert.Equal(before, ProcessRunner.BlockedBundledEngineLaunchCountForTests);
    }

    [Fact] // [REGRESSION] the REAL scrcpy-engine launch path (SystemProcessLauncher, what
           // ScrcpyService.Launcher defaults to in production) must also refuse the bundled paths
           // — ProcessRunner.Launch() is not what ScrcpyService actually calls
    public void SystemProcessLauncher_RefusesTheBundledScrcpyPath()
    {
        var before = ProcessRunner.BlockedBundledEngineLaunchCountForTests;
        var command = new ProcessCommand(ToolLocator.ScrcpyPath, Array.Empty<string>());

        var ex = Record.Exception(() => SystemProcessLauncher.Instance.Start(command));

        Assert.NotNull(ex);
        Assert.Equal(before + 1, ProcessRunner.BlockedBundledEngineLaunchCountForTests);
    }

    [Fact]
    public void SystemProcessLauncher_RefusesTheBundledAdbPath()
    {
        var before = ProcessRunner.BlockedBundledEngineLaunchCountForTests;
        var command = new ProcessCommand(ToolLocator.AdbPath, Array.Empty<string>());

        var ex = Record.Exception(() => SystemProcessLauncher.Instance.Start(command));

        Assert.NotNull(ex);
        Assert.Equal(before + 1, ProcessRunner.BlockedBundledEngineLaunchCountForTests);
    }

    [Fact] // production behavior is unchanged: the flag defaults to blocking only under this
           // safety net, never introduces a new public/production-reachable switch
    public void SafetyNetFlag_IsInternalNotPublic()
    {
        var field = typeof(ProcessRunner).GetProperty(
            "BundledEngineLaunchesAreBlockedForTests",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(field);
        Assert.False(field!.GetMethod!.IsPublic);
    }
}
