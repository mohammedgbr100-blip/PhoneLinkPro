using System.IO;
using System.Text.Json;

namespace ScrcpyDeck.Services;

/// <summary>Lightweight persisted settings (consent + onboarding flags) in %AppData%\PhoneLinkPro.</summary>
public static class AppSettings
{
    private class Data
    {
        public bool ConsentAccepted { get; set; }
        public string? ConsentVersion { get; set; }
        public bool OnboardingSeen { get; set; }
    }

    private const string CurrentConsentVersion = "1.0";

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneLinkPro");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static Data _data = Load();

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch { }
        return new Data();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>True only when the user accepted the current policy version.</summary>
    public static bool ConsentAccepted
    {
        get => _data.ConsentAccepted && _data.ConsentVersion == CurrentConsentVersion;
        set
        {
            _data.ConsentAccepted = value;
            _data.ConsentVersion = value ? CurrentConsentVersion : null;
            Save();
        }
    }

    public static bool OnboardingSeen
    {
        get => _data.OnboardingSeen;
        set { _data.OnboardingSeen = value; Save(); }
    }
}
