using System.Text;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Public-release audit — policy 1.2 (MIT relicensing) upgrade path, exercised end to end through
/// <see cref="AppSettings"/>, the exact API MainWindow's first-run flow calls
/// (<c>if (!AppSettings.ConsentAccepted) … AppSettings.ConsentAccepted = true;</c>).
///
/// <para>The on-disk fixtures are written byte-for-byte the way the shipped v1.2.0 app persisted
/// them (indented System.Text.Json of <see cref="SettingsStore.Data"/>), so these tests prove the
/// real upgrade: an existing user is prompted again, acceptance is persisted as the current
/// version, nothing else in the file is lost, and a declined prompt writes nothing.</para>
///
/// Storage is redirected to an isolated temp directory; the real %AppData% is never touched.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class PolicyUpgradeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;
    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public PolicyUpgradeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plp-tests-policy-upgrade-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "settings.json");
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-policy-upgrade-log-" + Guid.NewGuid().ToString("N"));
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
        AppSettings.ResetForTests(_dir);
    }

    public void Dispose()
    {
        AppSettings.ResetForTests(null);
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    /// <summary>Writes a settings.json exactly as an earlier release wrote it, then simulates the
    /// next launch (fresh process: nothing cached in memory).</summary>
    private void WriteLegacySettings(string consentVersionJson, bool onboardingSeen, string destinationJson)
    {
        Directory.CreateDirectory(_dir);
        var json =
            "{\n" +
            "  \"ConsentAccepted\": true,\n" +
            $"  \"ConsentVersion\": {consentVersionJson},\n" +
            $"  \"OnboardingSeen\": {(onboardingSeen ? "true" : "false")},\n" +
            $"  \"DefaultTransferDestination\": {destinationJson}\n" +
            "}";
        File.WriteAllText(_file, json.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        AppSettings.ResetForTests(_dir);
    }

    private void SimulateNextLaunch() => AppSettings.ResetForTests(_dir);

    [Fact]
    public void CurrentPolicyIsVersion12()
        => Assert.Equal("1.2", PolicyText.Version);

    [Theory] // every earlier consent state an installed user can have on disk
    [InlineData("\"1.1\"")]
    [InlineData("\"1.0\"")]
    [InlineData("null")]
    public void ExistingUserWithAnOlderPolicy_IsPromptedAgainOnNextLaunch(string storedVersion)
    {
        WriteLegacySettings(storedVersion, onboardingSeen: true, destinationJson: "\"Pictures\"");

        Assert.False(AppSettings.ConsentAccepted); // MainWindow shows ConsentWindow
    }

    [Fact]
    public void AcceptingTheUpdatedPolicy_PersistsVersion12_AndKeepsEveryOtherSetting()
    {
        WriteLegacySettings("\"1.1\"", onboardingSeen: true, destinationJson: "\"Pictures\"");
        Assert.False(AppSettings.ConsentAccepted);

        AppSettings.ConsentAccepted = true;    // what MainWindow does after "أوافق"

        SimulateNextLaunch();
        Assert.True(AppSettings.ConsentAccepted);                       // not prompted again
        Assert.Equal("1.2", AppSettings.ConsentVersion);
        Assert.True(AppSettings.OnboardingSeen);                        // tutorial not re-shown
        Assert.Equal(StandardRemoteDestination.Pictures, AppSettings.DefaultTransferDestination);
        Assert.False(File.Exists(_file + ".corrupt.bak"));              // read cleanly, no fallback
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));                // atomic write left no temp
    }

    [Fact]
    public void AcceptedUpdatedPolicy_StaysAcceptedAcrossRepeatedLaunches_WithoutRewritingTheFile()
    {
        WriteLegacySettings("\"1.1\"", onboardingSeen: true, destinationJson: "\"Documents\"");
        AppSettings.ConsentAccepted = true;
        var afterAccept = File.ReadAllBytes(_file);

        SettingsStore.ResetSaveCallCountForTests();
        for (var launch = 0; launch < 3; launch++)
        {
            SimulateNextLaunch();
            Assert.True(AppSettings.ConsentAccepted);
        }

        Assert.Equal(0, SettingsStore.SaveCallCountForTests);   // reading consent never writes
        Assert.Equal(afterAccept, File.ReadAllBytes(_file));
    }

    [Fact]
    public void DecliningTheUpdatedPolicy_WritesNothing_AndPromptsAgainNextTime()
    {
        WriteLegacySettings("\"1.1\"", onboardingSeen: true, destinationJson: "\"Music\"");
        var before = File.ReadAllBytes(_file);
        SettingsStore.ResetSaveCallCountForTests();

        // Decline path: MainWindow only READS ConsentAccepted, then shuts the app down.
        Assert.False(AppSettings.ConsentAccepted);

        Assert.Equal(0, SettingsStore.SaveCallCountForTests);
        Assert.Equal(before, File.ReadAllBytes(_file));          // the old settings are intact

        SimulateNextLaunch();
        Assert.False(AppSettings.ConsentAccepted);
        Assert.Equal(StandardRemoteDestination.Music, AppSettings.DefaultTransferDestination);
    }

    [Fact]
    public void FreshInstall_IsPrompted_AndAcceptanceKeepsTheTutorialPending()
    {
        Assert.False(File.Exists(_file));
        Assert.False(AppSettings.ConsentAccepted);

        AppSettings.ConsentAccepted = true;

        SimulateNextLaunch();
        Assert.True(AppSettings.ConsentAccepted);
        Assert.Equal("1.2", AppSettings.ConsentVersion);
        Assert.False(AppSettings.OnboardingSeen);                // first-run tutorial still shows
        Assert.Equal(StandardRemoteDestination.Download, AppSettings.DefaultTransferDestination);
    }

    [Fact] // the first-run flow asks for consent before anything else starts, and records acceptance
    public void Source_MainWindowGatesStartupOnCurrentPolicyConsent()
    {
        var code = TestPaths.ReadRepoFile("MainWindow.xaml.cs");

        var gate = code.IndexOf("if (!AppSettings.ConsentAccepted)", StringComparison.Ordinal);
        var record = code.IndexOf("AppSettings.ConsentAccepted = true;", StringComparison.Ordinal);
        var engine = code.IndexOf("EngineIntegrityVerifier.VerifyAsync()", StringComparison.Ordinal);
        var onboarding = code.IndexOf("if (!AppSettings.OnboardingSeen)", StringComparison.Ordinal);

        Assert.True(gate >= 0 && record > gate, "consent must be checked and then recorded");
        Assert.True(engine > record, "the engine must not start before the policy is accepted");
        Assert.True(onboarding > record, "the tutorial must come after the policy is accepted");
    }
}
