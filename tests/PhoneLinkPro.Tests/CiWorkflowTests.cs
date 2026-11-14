using System.Text.RegularExpressions;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Public-release audit: GitHub-hosted CI is deliberately deferred until the repository is
/// public (the private-repository Actions minutes are exhausted). These source checks keep
/// .github/workflows/ci.yml in its reviewed state: it can never run a job on a private (or
/// internal) repository, the first public run can be started by hand, it uses the standard
/// GitHub-hosted Windows runner (not a paid larger runner), it verifies the downloaded scrcpy
/// archive AND every extracted engine file before building, and its actions are pinned.
/// </summary>
public sealed class CiWorkflowTests
{
    private static string Workflow() => TestPaths.ReadRepoFile(".github", "workflows", "ci.yml");

    [Fact]
    public void Workflow_CanBeStartedManually()
    {
        Assert.Matches(new Regex(@"^on:\s*$[\s\S]*^\s{2}workflow_dispatch:\s*$", RegexOptions.Multiline), Workflow());
    }

    [Fact] // a private or internal repository never gets a job, whatever the trigger
    public void Job_RunsOnlyWhenTheRepositoryIsPublic()
    {
        var workflow = Workflow();
        Assert.Contains("if: github.event.repository.visibility == 'public'", workflow);
        // the guard sits on the (only) job, before any step
        var job = workflow.IndexOf("  build-test:", StringComparison.Ordinal);
        var guard = workflow.IndexOf("if: github.event.repository.visibility == 'public'", StringComparison.Ordinal);
        var steps = workflow.IndexOf("    steps:", StringComparison.Ordinal);
        Assert.True(job >= 0 && guard > job && steps > guard, "the visibility guard must be on the job, before its steps");
        Assert.Equal(1, Regex.Matches(workflow, @"^\s{2}[A-Za-z0-9_-]+:\s*$", RegexOptions.Multiline)
            .Count(m => workflow.IndexOf("jobs:", StringComparison.Ordinal) < m.Index));
    }

    [Fact] // standard GitHub-hosted runner, not a larger (paid) runner label or runner group
    public void Job_UsesTheStandardHostedWindowsRunner()
    {
        var workflow = Workflow();
        var runsOn = Regex.Matches(workflow, @"^\s+runs-on:\s*(.+)$", RegexOptions.Multiline);
        Assert.Single(runsOn);
        // A plain label (no runner-group mapping, no larger-runner label such as *-8-cores).
        Assert.Equal("windows-latest", runsOn[0].Groups[1].Value.Trim());
        Assert.DoesNotMatch(@"\d+-cores?", workflow);
    }

    [Fact] // the pinned archive hash is the one the README tells people to check
    public void Workflow_VerifiesTheArchiveAgainstTheDocumentedHash()
    {
        var workflow = Workflow();
        var readme = TestPaths.ReadRepoFile("README.md");
        var pinned = Regex.Match(workflow, @"SCRCPY_ZIP_SHA256:\s*([0-9a-f]{64})").Groups[1].Value;
        Assert.Equal(64, pinned.Length);
        Assert.Contains(pinned.ToUpperInvariant(), readme);
        Assert.Contains("throw \"scrcpy zip SHA-256 mismatch", workflow);
    }

    [Fact] // every extracted engine file is checked against the manifest before the build
    public void Workflow_VerifiesEveryExtractedEngineFileBeforeBuilding()
    {
        var workflow = Workflow();
        var verify = workflow.IndexOf("name: Verify extracted engine files against Resources/engine.manifest", StringComparison.Ordinal);
        var build = workflow.IndexOf("name: Build (Release, warnings as errors)", StringComparison.Ordinal);
        Assert.True(verify >= 0, "the engine-file verification step is missing");
        Assert.True(build > verify, "engine files must be verified before the build copies them");
        Assert.Contains("'engine/PhoneLink.Engine.exe' = 'scrcpy.exe'", workflow);
        Assert.Contains("'engine/PhoneLink.Bridge.exe' = 'adb.exe'", workflow);
        Assert.Contains("'engine/core.dat' = 'scrcpy-server'", workflow);
        Assert.Contains("UNLISTED", workflow); // a DLL the build would copy but the manifest lacks
        Assert.Contains("throw \"$failed engine file(s) failed verification\"", workflow);
    }

    [Fact] // third-party actions are pinned to full commit SHAs
    public void Actions_ArePinnedToCommitShas()
    {
        var uses = Regex.Matches(Workflow(), @"uses:\s*(\S+)");
        Assert.NotEmpty(uses);
        foreach (Match u in uses)
            Assert.Matches(@"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$", u.Groups[1].Value);
    }

    [Fact]
    public void Workflow_HasReadOnlyTokenAndATimeout()
    {
        var workflow = Workflow();
        Assert.Matches(new Regex(@"^permissions:\s*\n\s+contents:\s*read\s*$", RegexOptions.Multiline), workflow);
        Assert.Matches(@"timeout-minutes:\s*\d+", workflow);
    }
}
