using System.IO;

namespace ScrcpyDeck.Services;

/// <summary>Lightweight persisted settings (consent + onboarding flags) in %AppData%\PhoneLinkPro.</summary>
public static class AppSettings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneLinkPro");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static SettingsStore.Data _data = SettingsStore.Load(FilePath);

    private static void Save() => SettingsStore.Save(Dir, FilePath, _data);

    /// <summary>True only when the user accepted the current policy version.</summary>
    public static bool ConsentAccepted
    {
        get => SettingsStore.IsConsentValid(_data);
        set
        {
            _data.ConsentAccepted = value;
            _data.ConsentVersion = value ? SettingsStore.CurrentConsentVersion : null;
            Save();
        }
    }

    public static bool OnboardingSeen
    {
        get => _data.OnboardingSeen;
        set { _data.OnboardingSeen = value; Save(); }
    }
}
