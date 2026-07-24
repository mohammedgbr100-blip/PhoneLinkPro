using System.Text.Json;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 8 — settings persistence (<see cref="SettingsStore"/>).
///
/// Every test runs against a unique temp directory. The real
/// %AppData%\PhoneLinkPro folder is never read or written: these tests deliberately
/// exercise SettingsStore rather than AppSettings, because touching AppSettings would
/// run its static initializer and read the user's real settings file.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plp-tests-settings-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- defaults

    [Fact] // [REGRESSION]
    public void MissingFile_YieldsDefaults()
    {
        var data = SettingsStore.Load(_file);

        Assert.False(data.ConsentAccepted);
        Assert.Null(data.ConsentVersion);
        Assert.False(data.OnboardingSeen);
    }

    [Fact] // [REGRESSION] — loading must not create the directory as a side effect
    public void MissingFile_DoesNotCreateAnything()
    {
        SettingsStore.Load(_file);

        Assert.False(Directory.Exists(_dir));
    }

    [Fact] // [REGRESSION]
    public void DefaultData_IsNotConsented()
        => Assert.False(SettingsStore.IsConsentValid(new SettingsStore.Data()));

    // ---------------------------------------------------------------- round trip

    [Fact] // [REGRESSION]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var original = new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = SettingsStore.CurrentConsentVersion,
            OnboardingSeen = true,
        };

        SettingsStore.Save(_dir, _file, original);
        var loaded = SettingsStore.Load(_file);

        Assert.True(loaded.ConsentAccepted);
        Assert.Equal(SettingsStore.CurrentConsentVersion, loaded.ConsentVersion);
        Assert.True(loaded.OnboardingSeen);
    }

    [Fact] // [REGRESSION] — Save creates the directory when absent
    public void Save_CreatesTheDirectory()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true });

        Assert.True(File.Exists(_file));
    }

    [Fact] // [REGRESSION] — written as indented JSON
    public void Save_WritesIndentedJson()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true });

        var text = File.ReadAllText(_file);
        Assert.Contains("\n", text);
        Assert.Contains("\"OnboardingSeen\"", text);
    }

    [Fact] // [REGRESSION] — a later save preserves unrelated values
    public void Save_PreservesExistingValues()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = SettingsStore.CurrentConsentVersion,
            OnboardingSeen = false,
        });

        var data = SettingsStore.Load(_file);
        data.OnboardingSeen = true;              // change one field
        SettingsStore.Save(_dir, _file, data);

        var reloaded = SettingsStore.Load(_file);
        Assert.True(reloaded.ConsentAccepted);   // untouched field survived
        Assert.Equal(SettingsStore.CurrentConsentVersion, reloaded.ConsentVersion);
        Assert.True(reloaded.OnboardingSeen);
    }

    // ---------------------------------------------------------------- corrupt input

    [Theory] // [REGRESSION] — corrupt content must degrade to defaults, never throw
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{ \"ConsentAccepted\": \"not-a-bool\" }")]
    [InlineData("\0\0\0")]
    public void CorruptJson_FallsBackToDefaults(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, content);

        var data = SettingsStore.Load(_file);

        Assert.False(data.ConsentAccepted);
        Assert.False(data.OnboardingSeen);
    }

    [Fact] // [REGRESSION] — literal JSON null deserializes to defaults
    public void JsonNull_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "null");

        var data = SettingsStore.Load(_file);

        Assert.False(data.ConsentAccepted);
    }

    [Fact] // [REGRESSION] — unknown properties are ignored, known ones still load
    public void UnknownProperties_AreIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            """{ "OnboardingSeen": true, "SomeFutureSetting": 42 }""");

        var data = SettingsStore.Load(_file);

        Assert.True(data.OnboardingSeen);
    }

    // ---------------------------------------------------------------- write failure

    [Fact] // [REGRESSION] — a failing write must be swallowed, not thrown
    public void Save_ToAnInvalidPath_DoesNotThrow()
    {
        // A path containing a character illegal on Windows.
        var badDir = Path.Combine(_dir, "in|valid");
        var badFile = Path.Combine(badDir, "settings.json");

        var ex = Record.Exception(
            () => SettingsStore.Save(badDir, badFile, new SettingsStore.Data()));

        Assert.Null(ex);
    }

    [Fact] // [REGRESSION] — a locked file must be swallowed, not thrown
    public void Save_WhenFileIsLocked_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        using var hold = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.None);

        var ex = Record.Exception(
            () => SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true }));

        Assert.Null(ex);
    }

    [Fact] // [REGRESSION] — an unreadable file degrades to defaults
    public void Load_WhenFileIsLocked_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, """{ "OnboardingSeen": true }""");

        using var hold = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);

        var data = SettingsStore.Load(_file);

        Assert.False(data.OnboardingSeen);
    }

    // ---------------------------------------------------------------- consent gating

    [Fact] // [REGRESSION] — consent counts only for the current version
    public void Consent_IsValidOnlyForTheCurrentVersion()
    {
        Assert.True(SettingsStore.IsConsentValid(new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = SettingsStore.CurrentConsentVersion,
        }));

        Assert.False(SettingsStore.IsConsentValid(new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = "0.9",
        }));

        Assert.False(SettingsStore.IsConsentValid(new SettingsStore.Data
        {
            ConsentAccepted = false,
            ConsentVersion = SettingsStore.CurrentConsentVersion,
        }));

        Assert.False(SettingsStore.IsConsentValid(new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = null,
        }));
    }

    // ================================================================
    //  PLP-CONSENT-01 — consent must track the policy version
    // ================================================================
    // Previously SettingsStore had its own "1.0" constant, unrelated to PolicyText.Version.
    // Batch 0 amended the policy to 1.1 but existing users were never re-prompted. Batch 1B
    // makes PolicyText.Version the single authoritative source.

    [Fact] // PLP-CONSENT-01 — one authoritative version, no drift possible
    public void PLPCONSENT01_ConsentVersionTracksThePolicyVersion()
        => Assert.Equal(PolicyText.Version, SettingsStore.CurrentConsentVersion);

    [Fact] // PLP-CONSENT-01 — the amended policy is the current one
    public void PLPCONSENT01_CurrentPolicyVersionIsTheAmendedOne()
        => Assert.Equal("1.1", SettingsStore.CurrentConsentVersion);

    [Fact] // PLP-CONSENT-01 — a user who accepted 1.0 is prompted again
    public void PLPCONSENT01_UserWhoAcceptedTheOldPolicyIsReprompted()
    {
        var existing = new SettingsStore.Data { ConsentAccepted = true, ConsentVersion = "1.0" };

        Assert.False(SettingsStore.IsConsentValid(existing));
    }

    [Fact] // PLP-CONSENT-01 — a user who accepted 1.1 is NOT prompted repeatedly
    public void PLPCONSENT01_UserWhoAcceptedTheCurrentPolicyIsNotReprompted()
    {
        var current = new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = PolicyText.Version,
        };

        Assert.True(SettingsStore.IsConsentValid(current));
    }

    [Fact] // PLP-CONSENT-01 — acceptance survives a save/load round trip
    public void PLPCONSENT01_AcceptedVersionIsPreservedAcrossReload()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = SettingsStore.CurrentConsentVersion,
        });

        var reloaded = SettingsStore.Load(_file);

        Assert.Equal(PolicyText.Version, reloaded.ConsentVersion);
        Assert.True(SettingsStore.IsConsentValid(reloaded));
    }

    [Fact] // PLP-CONSENT-01 — a fresh install gets the current policy, not a legacy one
    public void PLPCONSENT01_FreshInstallIsNotConsented()
    {
        var fresh = SettingsStore.Load(_file);   // no file exists

        Assert.False(SettingsStore.IsConsentValid(fresh));
        Assert.Null(fresh.ConsentVersion);
    }

    [Fact] // PLP-CONSENT-01 — corrupt settings fail safe (prompt again, never auto-accept)
    public void PLPCONSENT01_CorruptSettingsFailSafeToNotConsented()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ this is not json");

        var data = SettingsStore.Load(_file);

        Assert.False(SettingsStore.IsConsentValid(data));
    }

    [Fact] // PLP-CONSENT-01 — a future/unknown version does not count as consent
    public void PLPCONSENT01_UnknownVersionIsNotConsent()
    {
        var odd = new SettingsStore.Data { ConsentAccepted = true, ConsentVersion = "9.9" };

        Assert.False(SettingsStore.IsConsentValid(odd));
    }

    // ---------------------------------------------------------------- serializer shape

    [Fact] // [REGRESSION] — the on-disk shape other tools may read
    public void SerializedShape_ContainsTheExpectedProperties()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = "1.0",
            OnboardingSeen = true,
        });

        using var doc = JsonDocument.Parse(File.ReadAllText(_file));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ConsentAccepted").GetBoolean());
        Assert.Equal("1.0", root.GetProperty("ConsentVersion").GetString());
        Assert.True(root.GetProperty("OnboardingSeen").GetBoolean());
    }
}
