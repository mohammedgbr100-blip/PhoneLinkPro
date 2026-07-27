using System.IO;

namespace ScrcpyDeck.Services;

/// <summary>
/// Persisted application preferences (consent + onboarding flags, and the Batch 4E settings
/// page's one real preference) in %AppData%\PhoneLinkPro. Every read/write is serialized
/// through <see cref="SettingsStore"/>'s atomic temp-file-then-replace writer under a single
/// lock, so concurrent callers (a startup flag flip racing the Settings page's own Save) can
/// never interleave or corrupt the file.
/// </summary>
public static class AppSettings
{
    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneLinkPro");

    /// <summary>
    /// Test-only redirection of the settings directory. Null in production, so the application
    /// always persists to %AppData%\PhoneLinkPro\settings.json exactly as before; the regression
    /// and self-test suites redirect this to an isolated temp directory so they never read or
    /// write the real user profile.
    /// </summary>
    internal static string? DirOverrideForTests;

    private static string Dir => DirOverrideForTests ?? DefaultDir;
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly object _lock = new();
    private static SettingsStore.Data? _data;

    /// <summary>Loads on first touch only; a test reset drops this so the next access reloads.</summary>
    private static SettingsStore.Data Data => _data ??= SettingsStore.Load(FilePath);

    private static bool Save() => SettingsStore.Save(Dir, FilePath, Data);

    /// <summary>
    /// Test hook: redirect storage to an isolated directory and discard cached in-memory state
    /// so the next access reloads from there. <paramref name="dir"/> null restores production
    /// behavior. Never called outside PLP_SELFTEST / xUnit.
    /// </summary>
    internal static void ResetForTests(string? dir)
    {
        lock (_lock)
        {
            DirOverrideForTests = dir;
            _data = null;
        }
    }

    /// <summary>True only when the user accepted the current policy version. Immutable from the
    /// Settings page (§8) — only the consent window itself ever sets this.</summary>
    public static bool ConsentAccepted
    {
        get { lock (_lock) return SettingsStore.IsConsentValid(Data); }
        set
        {
            lock (_lock)
            {
                Data.ConsentAccepted = value;
                Data.ConsentVersion = value ? SettingsStore.CurrentConsentVersion : null;
                Save();
            }
        }
    }

    /// <summary>The policy version the user actually accepted, for read-only display.</summary>
    public static string? ConsentVersion { get { lock (_lock) return Data.ConsentVersion; } }

    public static bool OnboardingSeen
    {
        get { lock (_lock) return Data.OnboardingSeen; }
        set { lock (_lock) { Data.OnboardingSeen = value; Save(); } }
    }

    /// <summary>
    /// Truthful counterpart to the <see cref="OnboardingSeen"/> setter (Batch 4E repair): the
    /// Settings page's "show the tour again" action needs to know whether the write actually
    /// succeeded, not just fire-and-forget it the way startup's "mark seen" write does. Rolls
    /// the in-memory value back on a failed write, mirroring
    /// <see cref="TrySaveDefaultTransferDestination"/>.
    /// </summary>
    public static bool TrySetOnboardingSeen(bool value)
    {
        lock (_lock)
        {
            var previous = Data.OnboardingSeen;
            Data.OnboardingSeen = value;
            if (Save()) return true;
            Data.OnboardingSeen = previous;
            return false;
        }
    }

    /// <summary>
    /// The default destination category the file-transfers page preselects. Falls back to
    /// <see cref="StandardRemoteDestination.Download"/> for an unset or unrecognized stored value.
    /// </summary>
    public static StandardRemoteDestination DefaultTransferDestination
    {
        get { lock (_lock) return ResolveDestination(Data.DefaultTransferDestination); }
    }

    /// <summary>
    /// Validates, applies and atomically persists a new default destination in one step (the
    /// Settings page's Save action). Rolls the in-memory value back on a failed write so a
    /// half-applied change can never be read back as if it had succeeded.
    /// </summary>
    public static bool TrySaveDefaultTransferDestination(StandardRemoteDestination value)
    {
        if (!Enum.IsDefined(value)) return false;
        lock (_lock)
        {
            var previous = Data.DefaultTransferDestination;
            Data.DefaultTransferDestination = value.ToString();
            if (Save()) return true;
            Data.DefaultTransferDestination = previous;
            return false;
        }
    }

    /// <summary>Resets the one reset-eligible preference to its default. Never touches consent,
    /// onboarding or logs (§7).</summary>
    public static bool ResetDefaultTransferDestination()
        => TrySaveDefaultTransferDestination(StandardRemoteDestination.Download);

    /// <summary>
    /// Unsupported/unknown stored values fail safe to the documented default (Download).
    /// <para>
    /// Deliberately does NOT use <see cref="Enum.TryParse{TEnum}(string?,out TEnum)"/> directly:
    /// it also accepts the underlying numeric value ("1" parses as <c>Documents</c>), which
    /// contradicts the whole point of storing the enum's NAME (§3 of SettingsStore.Data) — a
    /// harmless-looking stored string like "1" must never silently resolve to a real destination.
    /// Comparing against the real declared names first, case-insensitively, accepts every
    /// supported name and rejects every numeric or unrecognized form.
    /// </para>
    /// </summary>
    internal static StandardRemoteDestination ResolveDestination(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return StandardRemoteDestination.Download;

        foreach (var name in Enum.GetNames<StandardRemoteDestination>())
        {
            if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<StandardRemoteDestination>(name);
        }
        return StandardRemoteDestination.Download;
    }
}
