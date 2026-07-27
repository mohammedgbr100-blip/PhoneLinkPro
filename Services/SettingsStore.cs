using System.IO;
using System.Text.Json;

namespace ScrcpyDeck.Services;

/// <summary>
/// Persistence primitives behind <see cref="AppSettings"/>, kept separate so the
/// load/save/consent rules can be exercised against a temporary directory without
/// touching (or even reading) the real %AppData%\PhoneLinkPro folder.
/// </summary>
internal static class SettingsStore
{
    internal sealed class Data
    {
        public bool ConsentAccepted { get; set; }
        public string? ConsentVersion { get; set; }
        public bool OnboardingSeen { get; set; }

        /// <summary>
        /// Enum name of the default file-transfer destination category (Batch 4E). Stored as
        /// a string (not the numeric enum value) so an unrecognized/renamed future value fails
        /// safely instead of silently mapping to the wrong destination; null means "not yet
        /// chosen" and resolves to <see cref="StandardRemoteDestination.Download"/> via
        /// <see cref="AppSettings.ResolveDestination"/>.
        /// </summary>
        public string? DefaultTransferDestination { get; set; }
    }

    /// <summary>
    /// The policy revision the user must have accepted for consent to count.
    /// <para>
    /// This is deliberately derived from <see cref="PolicyText.Version"/> rather than being
    /// a second independent constant: the two previously drifted apart (PLP-CONSENT-01), so
    /// an amended policy was never re-presented. Editing the policy version now
    /// automatically re-prompts every user exactly once.
    /// </para>
    /// </summary>
    internal static string CurrentConsentVersion => PolicyText.Version;

    /// <summary>
    /// How many times <see cref="Save"/> has actually executed (repair batch §4): a direct,
    /// unambiguous proof that a code path never wrote settings, independent of file mtime/hash
    /// checks which could theoretically be inconclusive if content happened to round-trip
    /// identically.
    /// </summary>
    internal static int SaveCallCountForTests => Volatile.Read(ref _saveCallCount);
    private static int _saveCallCount;
    internal static void ResetSaveCallCountForTests() => Volatile.Write(ref _saveCallCount, 0);

    /// <summary>
    /// Serializes every Load/Save against this file so concurrent callers (a UI setter racing
    /// the Settings page's own draft save) can never interleave a partial read with a partial
    /// write. Reentrant on the same thread, so Load calling into this from inside a Save is safe.
    /// </summary>
    private static readonly object FileGate = new();

    /// <summary>Reads settings from <paramref name="filePath"/>; defaults on any failure.</summary>
    internal static Data Load(string filePath)
    {
        lock (FileGate)
        {
            string? text = null;
            try
            {
                if (!File.Exists(filePath)) return new Data();
                text = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<Data>(text) ?? new Data();
            }
            catch (JsonException)
            {
                // Genuinely malformed content (not a locked/inaccessible file): preserve one
                // bounded diagnostic copy before falling back, so the corrupt content is not
                // lost to the very next successful save. Overwrites any previous backup — this
                // is a single fixed-name file, never an accumulating trail.
                BackupCorruptFile(filePath, text);
                return new Data();
            }
            catch
            {
                return new Data();
            }
        }
    }

    /// <summary>
    /// Atomically writes <paramref name="data"/>: serialize to a temp file in the same
    /// directory, then replace the final path in one move. The temp file never survives a
    /// success or a failure. Returns whether the write succeeded; never throws.
    /// </summary>
    internal static bool Save(string dir, string filePath, Data data)
    {
        Interlocked.Increment(ref _saveCallCount);
        lock (FileGate)
        {
            string? tempPath = null;
            try
            {
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                tempPath = Path.Combine(dir, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
                tempPath = null; // moved away — nothing left to clean up
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"SettingsStore: save failed ({ex.GetType().Name}).");
                return false;
            }
            finally
            {
                if (tempPath != null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>Consent counts only when it was granted against the current policy version.</summary>
    internal static bool IsConsentValid(Data data)
        => data.ConsentAccepted && data.ConsentVersion == CurrentConsentVersion;

    private static void BackupCorruptFile(string filePath, string? text)
    {
        try
        {
            var backupPath = filePath + ".corrupt.bak";
            if (text != null) File.WriteAllText(backupPath, text);
            else if (File.Exists(filePath)) File.Copy(filePath, backupPath, overwrite: true);
        }
        catch { /* best-effort diagnostic only — never block the fallback to defaults */ }
    }
}
