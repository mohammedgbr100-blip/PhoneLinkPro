using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// PLP_SELFTEST must never be able to write to the real %AppData%\PhoneLinkPro\logs directory.
///
/// <para>
/// Repair batch (independent review): log isolation is no longer PLP_SELFTEST-specific — every
/// automated mode shares the same <c>InstallLogIsolation</c>/<c>RestoreAutomatedLogIsolation</c>
/// machinery in App.xaml.cs, driven by <see cref="ScrcpyDeck.Services.AutomatedModeClassifier"/>.
/// The general contract (classification and isolation precede the first Log.* call; the conflict
/// path also installs isolation before shutting down; OnExit restores only after
/// AppLifecycle.Shutdown has run; and the REAL log directory is provably byte-identical before
/// and after both a complete self-test process and a conflicting-modes process) is covered by
/// <c>AutomatedModeTests</c> — including two tests that actually spawn the built exe as a real
/// child process, which is the only way to prove the process-LIFETIME claim rather than just
/// source ordering.
/// </para>
///
/// <para>
/// What remains here is the one self-test-specific detail not implied by the general contract:
/// its OWN internal fail-closed re-check (defense in depth — a belt-and-suspenders guard in case
/// a future refactor ever separates classification from the self-test branch again) must still
/// run before a single self-test window is constructed.
/// </para>
/// </summary>
public sealed class PlpSelfTestLogIsolationTests
{
    [Fact] // [REGRESSION] the self-test path fails closed instead of proceeding when isolation
           // was somehow skipped, and does so before constructing a single self-test window
    public void Source_SelfTestBlockFailsClosedBeforeAnyWindowIsConstructed()
    {
        var code = TestPaths.ReadRepoFile("App.xaml.cs");

        var selfTestBranch = code.IndexOf(
            "if (_automatedMode == AutomatedMode.SelfTest)", StringComparison.Ordinal);
        Assert.True(selfTestBranch >= 0, "the self-test branch must still exist");

        var guard = code.IndexOf(
            "if (_automatedLogDir == null || Log.DirOverride != _automatedLogDir)",
            selfTestBranch, StringComparison.Ordinal);
        Assert.True(guard > selfTestBranch, "the self-test branch's own fail-closed guard must still exist");

        var guardShutdown = code.IndexOf("Shutdown(1);", guard, StringComparison.Ordinal);
        var windowConstruction = code.IndexOf(
            "new Windows.ConsentWindow()", selfTestBranch, StringComparison.Ordinal);

        Assert.True(guardShutdown > guard && guardShutdown < windowConstruction,
            "the fail-closed guard must abort before any self-test window is constructed");
    }
}
