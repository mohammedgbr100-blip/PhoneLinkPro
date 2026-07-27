using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 4E — <see cref="AppSettings"/>'s draft/save/reset semantics for the one real editable
/// preference (default transfer destination), and proof that consent stays immutable from every
/// path except its own dedicated setter. Every test redirects storage to an isolated temp
/// directory via <see cref="AppSettings.ResetForTests"/>; the real %AppData%\PhoneLinkPro
/// profile is never touched. Repair batch: a failing save also logs via <see cref="Log"/>, which
/// defaults to the real profile — <see cref="Log.DirOverride"/> is redirected too. In the global
/// collection because both statics are process-wide.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class AppSettingsPreferenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public AppSettingsPreferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plp-tests-appsettings-" + Guid.NewGuid().ToString("N"));
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-appsettings-log-" + Guid.NewGuid().ToString("N"));
        AppSettings.ResetForTests(_dir);
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        AppSettings.ResetForTests(null);
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultTransferDestination_DefaultsToDownload()
        => Assert.Equal(StandardRemoteDestination.Download, AppSettings.DefaultTransferDestination);

    [Fact]
    public void TrySave_PersistsAndReturnsTrue()
    {
        var ok = AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Pictures);

        Assert.True(ok);
        Assert.Equal(StandardRemoteDestination.Pictures, AppSettings.DefaultTransferDestination);
    }

    [Fact]
    public void TrySave_PersistsAcrossReload()
    {
        AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Movies);

        AppSettings.ResetForTests(_dir); // drop cached state; force a reload from the same disk directory

        Assert.Equal(StandardRemoteDestination.Movies, AppSettings.DefaultTransferDestination);
    }

    [Fact] // Batch 4E §6 — a failed write never leaves a half-applied in-memory value
    public void TrySave_ToALockedFile_RollsBackInMemoryValue()
    {
        AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Documents);
        var file = Path.Combine(_dir, "settings.json");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ok = AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Pictures);

        Assert.False(ok);
        Assert.Equal(StandardRemoteDestination.Documents, AppSettings.DefaultTransferDestination);
    }

    // ---------------------------------------------------------------- TrySetOnboardingSeen (repair batch §2)

    [Fact] // 1: successful tour reset
    public void TrySetOnboardingSeen_PersistsAndReturnsTrue()
    {
        AppSettings.OnboardingSeen = true;

        var ok = AppSettings.TrySetOnboardingSeen(false);

        Assert.True(ok);
        Assert.False(AppSettings.OnboardingSeen);
    }

    [Fact] // 2: persistence failure
    public void TrySetOnboardingSeen_ToALockedFile_ReturnsFalse()
    {
        AppSettings.OnboardingSeen = true;
        var file = Path.Combine(_dir, "settings.json");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ok = AppSettings.TrySetOnboardingSeen(false);

        Assert.False(ok);
    }

    [Fact] // 3: rollback after failure
    public void TrySetOnboardingSeen_ToALockedFile_RollsBackInMemoryValue()
    {
        AppSettings.OnboardingSeen = true;
        var file = Path.Combine(_dir, "settings.json");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        AppSettings.TrySetOnboardingSeen(false);

        Assert.True(AppSettings.OnboardingSeen); // still true — the failed write never took effect
    }

    [Fact] // 4: consent/version/destination preservation
    public void TrySetOnboardingSeen_PreservesConsentVersionAndDestination()
    {
        AppSettings.ConsentAccepted = true;
        var versionBefore = AppSettings.ConsentVersion;
        AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Pictures);

        AppSettings.TrySetOnboardingSeen(false);

        Assert.True(AppSettings.ConsentAccepted);
        Assert.Equal(versionBefore, AppSettings.ConsentVersion);
        Assert.Equal(StandardRemoteDestination.Pictures, AppSettings.DefaultTransferDestination);
    }

    [Fact] // 6: next isolated startup observes OnboardingSeen=false
    public void TrySetOnboardingSeen_PersistsAcrossReload()
    {
        AppSettings.OnboardingSeen = true;
        AppSettings.TrySetOnboardingSeen(false);

        AppSettings.ResetForTests(_dir); // drop cached state; force a reload from the same disk directory

        Assert.False(AppSettings.OnboardingSeen);
    }

    [Fact]
    public void ResetDefaultTransferDestination_ReturnsToDownload()
    {
        AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Music);

        var ok = AppSettings.ResetDefaultTransferDestination();

        Assert.True(ok);
        Assert.Equal(StandardRemoteDestination.Download, AppSettings.DefaultTransferDestination);
    }

    [Fact] // Batch 4E §7 — reset scope is exactly one preference
    public void ResetDefaultTransferDestination_NeverTouchesConsentOrOnboarding()
    {
        AppSettings.ConsentAccepted = true;
        AppSettings.OnboardingSeen = true;

        AppSettings.ResetDefaultTransferDestination();

        Assert.True(AppSettings.ConsentAccepted);
        Assert.True(AppSettings.OnboardingSeen);
    }

    [Theory] // Batch 4E §4 — unsupported/unknown values fail safe to the documented default
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]                        // whitespace-only
    [InlineData("NotARealDestinationValue")]
    [InlineData("SomeFutureDestinationName")]   // a renamed/future value not yet supported
    public void ResolveDestination_FallsBackSafelyForUnsupportedValues(string? raw)
        => Assert.Equal(StandardRemoteDestination.Download, AppSettings.ResolveDestination(raw));

    // Repair batch §6/§8 — a bare numeric string must never resolve to a real destination, even
    // though Enum.TryParse alone would happily accept it as the underlying numeric value (e.g.
    // "1" -> Documents). That directly contradicts the schema's whole reason for storing the
    // enum's NAME rather than its number: forward/backward compatibility across a value being
    // reordered or removed. Every one of these must fall back to Download, never a real value.
    [Theory]
    [InlineData("1")]   // would be Documents under raw Enum.TryParse
    [InlineData("0")]   // would be Download under raw Enum.TryParse — right value, wrong reason
    [InlineData("2")]   // would be Pictures
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("999")] // not even a defined numeric value
    public void ResolveDestination_RejectsNumericFormsEvenWhenTheyWouldParse(string raw)
        => Assert.Equal(StandardRemoteDestination.Download, AppSettings.ResolveDestination(raw));

    [Theory] // repair batch §6 — every real enum name is accepted, case-insensitively
    [InlineData("Download", StandardRemoteDestination.Download)]
    [InlineData("download", StandardRemoteDestination.Download)]
    [InlineData("DOWNLOAD", StandardRemoteDestination.Download)]
    [InlineData("Documents", StandardRemoteDestination.Documents)]
    [InlineData("documents", StandardRemoteDestination.Documents)]
    [InlineData("DOCUMENTS", StandardRemoteDestination.Documents)]
    [InlineData("Pictures", StandardRemoteDestination.Pictures)]
    [InlineData("pictures", StandardRemoteDestination.Pictures)]
    [InlineData("Movies", StandardRemoteDestination.Movies)]
    [InlineData("movies", StandardRemoteDestination.Movies)]
    [InlineData("Music", StandardRemoteDestination.Music)]
    [InlineData("music", StandardRemoteDestination.Music)]
    public void ResolveDestination_AcceptsEveryRealNameCaseInsensitively(string raw, StandardRemoteDestination expected)
        => Assert.Equal(expected, AppSettings.ResolveDestination(raw));

    [Fact]
    public void ResolveDestination_AcceptsEveryRealEnumName()
    {
        foreach (StandardRemoteDestination d in Enum.GetValues<StandardRemoteDestination>())
            Assert.Equal(d, AppSettings.ResolveDestination(d.ToString()));
    }

    [Fact] // Batch 4E §8 — consent is immutable from every path except its own setter
    public void ConsentAccepted_UnaffectedByDestinationOperations()
    {
        AppSettings.ConsentAccepted = true;
        var versionBefore = AppSettings.ConsentVersion;

        AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Pictures);
        AppSettings.ResetDefaultTransferDestination();

        Assert.True(AppSettings.ConsentAccepted);
        Assert.Equal(versionBefore, AppSettings.ConsentVersion);
    }

    [Fact] // Batch 4E §13 item 7 — no accessor ever persists a device serial or local/remote path
    public void PersistedFile_NeverContainsAPathOrSerialLookingValue()
    {
        AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Documents);
        AppSettings.ConsentAccepted = true;
        AppSettings.OnboardingSeen = true;

        var text = File.ReadAllText(Path.Combine(_dir, "settings.json"));

        Assert.DoesNotContain(":\\", text);      // no Windows drive path
        Assert.DoesNotContain("/sdcard", text);  // no Android remote path
        Assert.DoesNotContain("adb", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scrcpy", text, StringComparison.OrdinalIgnoreCase);
    }
}
