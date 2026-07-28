using System.Runtime.CompilerServices;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Repair batch (independent review, M3): the full xUnit test process must never write to the
/// real %AppData%\PhoneLinkPro\logs directory — not even a single stray <c>Log.Warn</c> from a
/// test that doesn't itself redirect <see cref="Log.DirOverride"/>, and not from a race between
/// two test classes running in parallel (xUnit parallelizes across collections by default; only
/// <see cref="GlobalStateCollection"/> members are serialized against each other).
///
/// <para>
/// A <see cref="ModuleInitializerAttribute"/> method is the narrowest reliable bootstrap for
/// this: the CLR guarantees it runs once, before any other code in this assembly — including
/// every test class's static/instance construction — the moment the module is loaded, which for
/// a test assembly is before the xUnit runner can invoke a single test. That is earlier and
/// simpler than a custom xUnit <c>ITestFramework</c> (xUnit 2.9.2 here, not xUnit v3, where a
/// framework-level hook would be the more invasive alternative) and does not depend on any
/// individual test class's constructor running first.
/// </para>
///
/// <para>
/// Once this installs the process-wide isolated root, every existing test that itself does
/// <c>var previous = Log.DirOverride; Log.DirOverride = someTestDir; ...; Log.DirOverride =
/// previous;</c> (see <see cref="GlobalStateCollection"/>'s own remarks) automatically restores
/// to THIS isolated root instead of null/real — no per-test-class change was needed for that.
/// </para>
/// </summary>
internal static class TestProcessLogIsolation
{
    /// <summary>The one isolated log root for this entire test process.</summary>
    internal static string? IsolatedLogDir { get; private set; }

    [ModuleInitializer]
    internal static void Install()
    {
        // Fail closed: if the temp path or directory creation itself is somehow unavailable,
        // let this throw and abort the whole test run rather than silently falling back to
        // Log's own default (%AppData%\PhoneLinkPro\logs).
        var dir = Path.Combine(Path.GetTempPath(), "plp_xunit_logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Log.DirOverride = dir;
        IsolatedLogDir = dir;

        // Repair batch 5B (§7): the same structural safety net App.OnStartup installs for every
        // automated mode, installed here for the WHOLE xUnit test process instead — so a test
        // that forgets to override AdbService.ProcessRunOverrideForTests/DeviceWatcher.
        // DiscoveryProvider/ScrcpyService.Launcher still cannot start the real bundled adb/scrcpy
        // binaries. Never restored: it must stay in effect for every test in this process.
        ProcessRunner.BundledEngineLaunchesAreBlockedForTests = true;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        if (IsolatedLogDir == null) return;
        try { Directory.Delete(IsolatedLogDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Fail-closed proof that <see cref="TestProcessLogIsolation.Install"/> actually ran and
/// is still in effect — not merely that the module initializer exists in source.</summary>
public sealed class TestProcessLogIsolationTests
{
    [Fact]
    public void ProcessWideLogIsolation_IsInstalledAndOutsideTheRealProfileRoot()
    {
        var realProfileRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneLinkPro");

        Assert.NotNull(TestProcessLogIsolation.IsolatedLogDir);
        Assert.StartsWith(Path.GetTempPath(), TestProcessLogIsolation.IsolatedLogDir!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(Log.DirOverride);
        Assert.DoesNotContain(realProfileRoot, Log.DirOverride!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // [REGRESSION] repair batch 5B (§7): the bundled-engine safety net must be installed
           // for the whole xUnit process, not merely mentioned in a comment
    public void BundledEngineLaunchSafetyNet_IsInstalledForTheWholeProcess()
        => Assert.True(ProcessRunner.BundledEngineLaunchesAreBlockedForTests);
}
