namespace ScrcpyDeck.Services;

/// <summary>
/// Turns Android package ids into friendly display names. Known popular apps map to their
/// real names; everything else falls back to a cleaned-up last segment of the package id.
/// (True labels live inside each APK's resources, which would require pulling every APK.)
/// </summary>
public static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.whatsapp"] = "WhatsApp",
        ["com.whatsapp.w4b"] = "WhatsApp Business",
        ["com.instagram.android"] = "Instagram",
        ["com.facebook.katana"] = "Facebook",
        ["com.facebook.orca"] = "Messenger",
        ["com.facebook.lite"] = "Facebook Lite",
        ["org.telegram.messenger"] = "Telegram",
        ["com.zhiliaoapp.musically"] = "TikTok",
        ["com.ss.android.ugc.trill"] = "TikTok",
        ["com.snapchat.android"] = "Snapchat",
        ["com.google.android.youtube"] = "YouTube",
        ["com.twitter.android"] = "X (Twitter)",
        ["com.android.chrome"] = "Chrome",
        ["com.google.android.gm"] = "Gmail",
        ["com.google.android.apps.maps"] = "Google Maps",
        ["com.google.android.apps.photos"] = "Google Photos",
        ["com.android.vending"] = "Google Play",
        ["com.google.android.googlequicksearchbox"] = "Google",
        ["com.netflix.mediaclient"] = "Netflix",
        ["com.spotify.music"] = "Spotify",
        ["com.viber.voip"] = "Viber",
        ["com.imo.android.imoim"] = "imo",
        ["com.lemon.lvoverseas"] = "CapCut",
        ["com.discord"] = "Discord",
        ["com.microsoft.teams"] = "Microsoft Teams",
        ["us.zoom.videomeetings"] = "Zoom",
        ["com.skype.raider"] = "Skype",
        ["com.amazon.mShop.android.shopping"] = "Amazon",
        ["com.alibaba.aliexpresshd"] = "AliExpress",
        ["com.sec.android.app.camera"] = "الكاميرا (Samsung)",
        ["com.sec.android.gallery3d"] = "المعرض (Samsung)",
        ["com.samsung.android.messaging"] = "الرسائل (Samsung)",
        ["com.android.settings"] = "الإعدادات",
        ["com.miui.gallery"] = "المعرض (Xiaomi)",
    };

    /// <summary>Generic trailing segments that say nothing about the app itself.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "android", "client", "mobile", "main", "free", "beta", "lite", "pro", "ui", "os", "apps"
    };

    public static string Pretty(string package)
    {
        if (Known.TryGetValue(package, out var name)) return name;

        var parts = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return package;

        var pick = parts[^1];
        if (parts.Length >= 2 && Generic.Contains(pick)) pick = parts[^2];

        pick = pick.Replace('_', ' ');
        return pick.Length <= 1 ? package : char.ToUpper(pick[0]) + pick.Substring(1);
    }
}
