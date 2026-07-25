using System.Reflection;

namespace ScrcpyDeck.Services;

/// <summary>
/// The one place UI code reads the product version and name from (REL-03). Values come from
/// the compiled assembly metadata, which itself is stamped from Directory.Build.props — so
/// no window, dialog or log line ever carries its own version literal again.
/// </summary>
public static class AppVersionInfo
{
    private static readonly Assembly App = typeof(AppVersionInfo).Assembly;

    /// <summary>Product version as released, e.g. "1.1.0" (InformationalVersion).</summary>
    public static string ProductVersion { get; } =
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? App.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Compact "major.minor" form for tight UI spots, e.g. "1.1".</summary>
    public static string ShortVersion
    {
        get
        {
            var parts = ProductVersion.Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : ProductVersion;
        }
    }

    /// <summary>User-facing product name from assembly metadata ("PhoneLink Pro").</summary>
    public static string ProductName { get; } =
        App.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "PhoneLink Pro";
}
