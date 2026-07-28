using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
///
/// Repair batch: a failing <see cref="SettingsStore.Save"/> logs via <see cref="Log"/>, which
/// defaults to the REAL %AppData%\PhoneLinkPro\logs folder — several tests below deliberately
/// force a failure, so <see cref="Log.DirOverride"/> is redirected here too. In the global
/// collection because Log.DirOverride is process-wide state.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;
    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public SettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plp-tests-settings-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "settings.json");
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-settings-log-" + Guid.NewGuid().ToString("N"));
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
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

    [Fact] // [REGRESSION] — a failing write must be swallowed, not thrown, and reported as failure
    public void Save_ToAnInvalidPath_DoesNotThrow()
    {
        // A path containing a character illegal on Windows.
        var badDir = Path.Combine(_dir, "in|valid");
        var badFile = Path.Combine(badDir, "settings.json");

        bool ok = true;
        var ex = Record.Exception(
            () => ok = SettingsStore.Save(badDir, badFile, new SettingsStore.Data()));

        Assert.Null(ex);
        Assert.False(ok);
    }

    [Fact] // [REGRESSION] — a locked file must be swallowed, not thrown, and reported as failure
    public void Save_WhenFileIsLocked_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        using var hold = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.None);

        bool ok = true;
        var ex = Record.Exception(
            () => ok = SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true }));

        Assert.Null(ex);
        Assert.False(ok);
    }

    [Fact] // Batch 4E §5 — a failed write against a locked target leaves no temp file behind
    public void Save_WhenFileIsLocked_LeavesNoTemporaryFile()
    {
        Directory.CreateDirectory(_dir);
        using var hold = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.None);

        SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact] // Batch 4E §5 — a successful write leaves no temp file behind
    public void Save_OnSuccess_LeavesNoTemporaryFile()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(File.Exists(_file));
    }

    [Fact] // Batch 4E §5 — a successful write reports success
    public void Save_OnSuccess_ReturnsTrue()
        => Assert.True(SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = true }));

    [Fact] // [REGRESSION] — an unreadable file degrades to defaults
    public void Load_WhenFileIsLocked_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, """{ "OnboardingSeen": true }""");

        using var hold = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);

        var data = SettingsStore.Load(_file);

        Assert.False(data.OnboardingSeen);
    }

    // ---------------------------------------------------------------- atomic-write structural guard (§5)

    [Fact] // Batch 4E §5 — a behavioral concurrency test alone cannot distinguish "atomic
           // temp-file-then-replace" from "a naive direct write that happens to fail closed
           // under a sharing violation" (both leave one valid file behind). This structural
           // guard proves the actual mechanism in source, mirroring the existing
           // ShellSnapshotToolModeTests/DeviceSetupWizardSourceTests source-drift pattern.
    public void Save_SourceUsesAtomicTempFileThenReplace()
    {
        var code = TestPaths.ReadRepoFile("Services", "SettingsStore.cs");

        Assert.Contains(".tmp", code);
        Assert.Contains("File.Move", code);
    }

    // ---------------------------------------------------------------- Open Log Folder source guard (repair §3)

    [Fact] // repair batch §3 — production opens the log folder through exactly one approved
           // Process.Start call, always via the shell, never a raw command interpreter
    public void Source_SettingsViewOpenLogFolder_UsesOnlyOneProcessStartCall()
    {
        // Source-only, honestly named as such: OpenLogFolderReal has no injectable seam for the
        // ProcessStartInfo itself (only OpenPathOverrideForTests, which REPLACES the whole call —
        // the real Process.Start is never behaviorally exercised by any test), so this can only
        // prove the structural shape, not the runtime PSI. A comment could satisfy a raw
        // "UseShellExecute = true" text search without the real call being safe, so that specific
        // check has been removed rather than kept as a false behavioral proof.
        var code = TestPaths.ReadRepoFile("Views", "SettingsView.xaml.cs");

        var occurrences = System.Text.RegularExpressions.Regex.Matches(code, @"Process\.Start\(").Count;
        Assert.Equal(1, occurrences);
        Assert.DoesNotContain("cmd.exe", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", code, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- corrupt-file backup (§4)

    [Fact] // Batch 4E §4 — malformed JSON is preserved as one bounded diagnostic backup
    public void CorruptJson_IsBackedUpBeforeFallingBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        const string corrupt = "{ this is not valid json";
        File.WriteAllText(_file, corrupt);

        SettingsStore.Load(_file);

        var backup = _file + ".corrupt.bak";
        Assert.True(File.Exists(backup));
        Assert.Equal(corrupt, File.ReadAllText(backup));
    }

    [Fact] // Batch 4E §4 — the backup is a single fixed name, never an accumulating trail
    public void CorruptJson_BackupOverwritesRatherThanAccumulates()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "first corrupt content {{{");
        SettingsStore.Load(_file);

        File.WriteAllText(_file, "second corrupt content [[[");
        SettingsStore.Load(_file);

        var backup = _file + ".corrupt.bak";
        Assert.Equal("second corrupt content [[[", File.ReadAllText(backup));
        Assert.Single(Directory.GetFiles(_dir, "*.corrupt.bak"));
    }

    [Fact] // Batch 4E §4 — a locked (not malformed) file is never mistaken for corruption
    public void LockedFile_NeverProducesACorruptBackup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, """{ "OnboardingSeen": true }""");
        using var hold = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);

        SettingsStore.Load(_file);

        Assert.False(File.Exists(_file + ".corrupt.bak"));
    }

    // ---------------------------------------------------------------- default transfer destination (§3/§9)

    [Fact] // Batch 4E — unset by default
    public void DefaultTransferDestination_IsNullByDefault()
        => Assert.Null(SettingsStore.Load(_file).DefaultTransferDestination);

    [Fact] // Batch 4E — round-trips through save/load like every other field
    public void DefaultTransferDestination_RoundTrips()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data { DefaultTransferDestination = "Pictures" });

        Assert.Equal("Pictures", SettingsStore.Load(_file).DefaultTransferDestination);
    }

    // ---------------------------------------------------------------- concurrent write ordering (§5)

    [Fact] // Batch 4E §5 — concurrent writers never corrupt the file; the result is one complete write
    public void ConcurrentSaves_NeverProduceACorruptOrPartialFile()
    {
        var barrier = new Barrier(2);
        void WriteOnboarding(bool value)
        {
            barrier.SignalAndWait();
            SettingsStore.Save(_dir, _file, new SettingsStore.Data { OnboardingSeen = value });
        }

        Parallel.Invoke(() => WriteOnboarding(true), () => WriteOnboarding(false));

        // Whichever writer's file "won", the result must be a single, fully-formed, parseable
        // document — never an interleaved/truncated write from two concurrent writers.
        var text = File.ReadAllText(_file);
        using var doc = JsonDocument.Parse(text); // throws on any interleaved/partial content
        Assert.True(doc.RootElement.TryGetProperty("OnboardingSeen", out _));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
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

    // ---------------------------------------------------------------- exact schema (repair batch §5)

    private static readonly string[] ExpectedSchemaKeys =
        { "ConsentAccepted", "ConsentVersion", "DefaultTransferDestination", "OnboardingSeen" };

    /// <summary>
    /// xUnit-level counterpart to SettingsSelfTest scenario S: this must catch a schema drift
    /// even when xUnit alone runs (no WPF harness), and even when the added field's value is
    /// null — a JSON writer can still emit a null-valued property, and that is still a schema
    /// change that must be caught, not silently accepted because "there's no real value yet".
    /// </summary>
    [Fact] // repair batch §5 — the persisted key set is exactly the four known fields
    public void SerializedShape_HasExactlyTheFourKnownKeys_NoMoreNoFewer()
    {
        SettingsStore.Save(_dir, _file, new SettingsStore.Data
        {
            ConsentAccepted = true,
            ConsentVersion = "1.1",
            OnboardingSeen = true,
            DefaultTransferDestination = "Pictures",
        });

        using var doc = JsonDocument.Parse(File.ReadAllText(_file));
        var actualKeys = doc.RootElement.EnumerateObject().Select(p => p.Name)
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var expectedKeys = ExpectedSchemaKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedKeys, actualKeys);
    }

    [Fact] // repair batch §5 — a null-valued extra field is still a schema change, still caught
    public void SerializedShape_RejectsAnAdditionalField_EvenWhenItsValueIsNull()
    {
        // Simulates what a future accidental field addition would look like on disk: the real
        // Data type cannot express this directly (that's the point — this test would need
        // updating the moment someone adds a field), so the raw JSON is hand-written here.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file,
            """{"ConsentAccepted":true,"ConsentVersion":"1.1","OnboardingSeen":true,"DefaultTransferDestination":null,"DeviceSerial":null}""");

        using var doc = JsonDocument.Parse(File.ReadAllText(_file));
        var actualKeys = doc.RootElement.EnumerateObject().Select(p => p.Name)
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var expectedKeys = ExpectedSchemaKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.NotEqual(expectedKeys, actualKeys);
    }

    // ---------------------------------------------------------------- RTL spacer-column layout
    // (Batch 5A supplementary guard — visual regression protection only, not full DPI proof;
    // shares its extraction/assertion logic with AboutWindow's identical status Grid via
    // XamlSpacerColumnGuard, since both files carry the same Auto/24/Auto shape)

    [Fact] // [REGRESSION] the engine-status Grid must keep a dedicated positive-width spacer
           // column between the two status groups, not fall back to an outer-edge margin
    public void SettingsViewXaml_EngineStatusGrid_HasADedicatedSpacerColumn()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "SettingsView.xaml");
        var grid = XamlSpacerColumnGuard.ExtractStatusGrid(xaml, "EngineVersionLabelText");
        XamlSpacerColumnGuard.AssertHasDedicatedSpacerColumn(grid);
    }
}
