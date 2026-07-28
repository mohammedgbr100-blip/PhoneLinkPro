using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Repair batch (independent review, M1/M2) — release.ps1's own quality-gate contract. These are
/// source-text checks against the script (hence the <c>Script_</c> prefix — release.ps1 itself is
/// never executed by these tests, which would mean a real Release build, publish, and a possible
/// signing attempt; that full pipeline is exercised manually/by CI, not by xUnit). They exist so a
/// future edit that quietly drops the analyzer gate, the Release PLP_SELFTEST step, or loosens
/// what it checks is caught here rather than only at the next real release.
/// </summary>
public sealed class ReleaseScriptTests
{
    private static string ReleaseScript => TestPaths.ReadRepoFile("release.ps1");

    [Fact] // [REGRESSION] the test project must be built with analyzer warnings as errors as
           // part of the release pipeline, in addition to the project-level <TreatWarningsAsErrors>
    public void Script_BuildsTheTestProjectWithWarningsAsErrors()
    {
        var script = ReleaseScript;
        Assert.Contains(
            "dotnet build 'tests\\PhoneLinkPro.Tests\\PhoneLinkPro.Tests.csproj' -c Release -warnaserror",
            script, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] TreatWarningsAsErrors must still be a structural, project-level gate —
           // not something only release.ps1 happens to pass on the command line
    public void TestProjectFile_HasTreatWarningsAsErrors()
    {
        var csproj = TestPaths.ReadRepoFile("tests", "PhoneLinkPro.Tests", "PhoneLinkPro.Tests.csproj");
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<NoWarn>", csproj, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] release.ps1 must run the Release build under PLP_SELFTEST
    public void Script_RunsReleasePlpSelfTest()
    {
        var script = ReleaseScript;
        Assert.Contains("bin\\Release\\net9.0-windows\\PhoneLinkPro.exe", script, StringComparison.Ordinal);
        Assert.Contains("$psi.EnvironmentVariables['PLP_SELFTEST'] = '1'", script, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] the self-test step must fail on a nonzero exit code and on a timeout,
           // not merely log a warning and continue packaging
    public void Script_ReleasePlpSelfTestFailsClosedOnExitCodeAndTimeout()
    {
        var script = ReleaseScript;
        var stepStart = script.IndexOf("Release PLP_SELFTEST (deterministic runtime gate)", StringComparison.Ordinal);
        Assert.True(stepStart >= 0, "the Release PLP_SELFTEST step must still exist");
        var nextStep = script.IndexOf("# ---------- 6)", stepStart, StringComparison.Ordinal);
        Assert.True(nextStep > stepStart, "the step must still precede publish (step 6)");
        var stepBody = script[stepStart..nextStep];

        Assert.Contains("WaitForExit(", stepBody, StringComparison.Ordinal);
        Assert.Contains("did not exit within", stepBody, StringComparison.Ordinal);
        Assert.Contains("-ne 0) { Fail \"Release PLP_SELFTEST exited", stepBody, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] the gate must check the EXACT totals the independent review named —
           // not just an overall exit code, so a suite silently shrinking is still caught
    public void Script_ReleasePlpSelfTestChecksExactTotals()
    {
        var script = ReleaseScript;
        Assert.Contains("'dashboard-ordering:\\s*42/42'", script, StringComparison.Ordinal);
        Assert.Contains("'dashboard-ordering-stress:\\s*1100/1100'", script, StringComparison.Ordinal);
        Assert.Contains("-ne 1920) {", script, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] the Release PLP_SELFTEST step must run after tests and before publish
    public void Script_ReleasePlpSelfTestRunsAfterTestsAndBeforePublish()
    {
        var script = ReleaseScript;
        var testsStep = script.IndexOf("# ---------- 5) tests", StringComparison.Ordinal);
        var selfTestStep = script.IndexOf("Release PLP_SELFTEST (deterministic runtime gate)", StringComparison.Ordinal);
        var publishStep = script.IndexOf("# ---------- 6) publish", StringComparison.Ordinal);

        Assert.True(testsStep >= 0 && selfTestStep > testsStep && publishStep > selfTestStep,
            "the Release PLP_SELFTEST step must run strictly between tests and publish");
    }

    [Fact] // [REGRESSION] only PLP_SELFTEST may be set for the child process — any stray PLP_*
           // already present must be stripped first, so it can never combine into a conflict
    public void Script_StripsStrayPlpVariablesBeforeSettingOnlyPlpSelfTest()
    {
        var script = ReleaseScript;
        var stepStart = script.IndexOf("Release PLP_SELFTEST (deterministic runtime gate)", StringComparison.Ordinal);
        Assert.True(stepStart >= 0);
        var nextStep = script.IndexOf("# ---------- 6)", stepStart, StringComparison.Ordinal);
        var stepBody = script[stepStart..nextStep];

        Assert.Contains("-like 'PLP_*'", stepBody, StringComparison.Ordinal);
        Assert.Contains("EnvironmentVariables.Remove(", stepBody, StringComparison.Ordinal);
    }
}
