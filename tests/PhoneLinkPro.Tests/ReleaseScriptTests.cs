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

    [Fact] // [REGRESSION] repair batch 5B (§5): the gate must validate the EXACT, UNIQUE suite
           // contract via SelfTestReportValidator.ps1 — not merely sum every matching
           // denominator, which could let a duplicated suite and an omitted suite with the same
           // denominator silently cancel out
    public void Script_ReleasePlpSelfTestUsesTheExactUniqueSuiteValidator()
    {
        var script = ReleaseScript;
        Assert.Contains(". (Join-Path $PSScriptRoot 'SelfTestReportValidator.ps1')", script, StringComparison.Ordinal);
        Assert.Contains("Test-SelfTestReport -Report $selfTestReport", script, StringComparison.Ordinal);
        Assert.Contains("if (-not $selfTestValidation.IsValid)", script, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] the validator file itself must define every required suite exactly
           // once, keyed by name (a dictionary, not only a running sum) — see
           // SelfTestReportValidatorTests for the full behavioral proof of the contract
    public void Validator_DefinesEveryRequiredSuiteWithItsExactDenominator()
    {
        var validator = TestPaths.ReadRepoFile("SelfTestReportValidator.ps1");

        Assert.Contains("'design'                    = 6", validator, StringComparison.Ordinal);
        Assert.Contains("'fit'                       = 6", validator, StringComparison.Ordinal);
        Assert.Contains("'lifecycle'                 = 25", validator, StringComparison.Ordinal);
        Assert.Contains("'dashboard'                 = 14", validator, StringComparison.Ordinal);
        Assert.Contains("'dashboard-stale'           = 19", validator, StringComparison.Ordinal);
        Assert.Contains("'dashboard-ordering'        = 55", validator, StringComparison.Ordinal);
        Assert.Contains("'dashboard-visual'          = 89", validator, StringComparison.Ordinal);
        Assert.Contains("'setup-wizard'              = 107", validator, StringComparison.Ordinal);
        Assert.Contains("'wireless'                  = 105", validator, StringComparison.Ordinal);
        Assert.Contains("'transfers'                 = 179", validator, StringComparison.Ordinal);
        Assert.Contains("'settings'                  = 144", validator, StringComparison.Ordinal);
        Assert.Contains("'about'                     = 84", validator, StringComparison.Ordinal);
        Assert.Contains("'dashboard-ordering-stress' = 1100", validator, StringComparison.Ordinal);

        // A duplicate suite, an unknown suite, and a FAIL line must all be rejected explicitly —
        // not merely "ignored" (which would let a duplicate/omission pair silently cancel out).
        Assert.Contains("duplicate suite", validator, StringComparison.Ordinal);
        Assert.Contains("unknown suite", validator, StringComparison.Ordinal);
        Assert.Contains("FAIL line present", validator, StringComparison.Ordinal);
        Assert.Contains("missing required suite", validator, StringComparison.Ordinal);
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
