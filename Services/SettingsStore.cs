using System.IO;
using System.Text.Json;

namespace ScrcpyDeck.Services;

/// <summary>
/// Persistence primitives behind <see cref="AppSettings"/>, kept separate so the
/// load/save/consent rules can be exercised against a temporary directory without
/// touching (or even reading) the real %AppData%\PhoneLinkPro folder.
/// Behaviour is identical to the logic that previously lived inside AppSettings.
/// </summary>
internal static class SettingsStore
{
    internal sealed class Data
    {
        public bool ConsentAccepted { get; set; }
        public string? ConsentVersion { get; set; }
        public bool OnboardingSeen { get; set; }
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

    /// <summary>Reads settings from <paramref name="filePath"/>; defaults on any failure.</summary>
    internal static Data Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(filePath)) ?? new Data();
        }
        catch { }
        return new Data();
    }

    /// <summary>Writes settings to <paramref name="filePath"/>; swallows any failure.</summary>
    internal static void Save(string dir, string filePath, Data data)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Consent counts only when it was granted against the current policy version.</summary>
    internal static bool IsConsentValid(Data data)
        => data.ConsentAccepted && data.ConsentVersion == CurrentConsentVersion;
}
