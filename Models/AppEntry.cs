using System.Windows.Media;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Models;

/// <summary>A package on the device, presented with a friendly name and a colored monogram.</summary>
public class AppEntry
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xC9, 0xA2, 0x4B), // gold
        Color.FromRgb(0x5B, 0x8D, 0xEF), // blue
        Color.FromRgb(0x5E, 0xC8, 0xA8), // teal
        Color.FromRgb(0xB0, 0x78, 0xD9), // purple
        Color.FromRgb(0xE0, 0x87, 0x6B), // coral
        Color.FromRgb(0x8F, 0xB2, 0x5A), // olive
        Color.FromRgb(0x6B, 0xB2, 0xCF), // sky
        Color.FromRgb(0xD9, 0xA0, 0x6B), // amber
    };

    public string Package { get; }
    public string PrettyName { get; }
    public string Monogram { get; }
    public Brush BadgeBg { get; }
    public Brush BadgeFg { get; }

    /// <summary>Number of tints available for package badges.</summary>
    internal static int PaletteSize => Palette.Length;

    /// <summary>The badge monogram for a display name. Extracted verbatim for regression tests.</summary>
    internal static string MonogramOf(string prettyName)
        => prettyName.Length > 0 ? prettyName.Substring(0, 1).ToUpper() : "?";

    /// <summary>
    /// Palette slot for a package.
    /// <para>
    /// Uses <see cref="StableHash.Fnv1a32"/> over the package's UTF-8 bytes. String.GetHashCode
    /// was randomised per process on .NET Core, so a package's badge colour changed on every
    /// restart (P3-01); FNV-1a is seed-free and therefore identical across processes,
    /// machines and .NET versions.
    /// </para>
    /// The palette size and the 0..PaletteSize-1 index range are unchanged; the specific
    /// colour a package maps to is a deliberate one-time change.
    /// </summary>
    internal static int BadgeIndex(string package)
        => (int)(StableHash.Fnv1a32(package) % (uint)Palette.Length);

    public AppEntry(string package)
    {
        Package = package;
        PrettyName = AppNames.Pretty(package);
        Monogram = MonogramOf(PrettyName);

        // Deterministic tint per package so the list stays stable across refreshes.
        var c = Palette[BadgeIndex(package)];
        var fg = new SolidColorBrush(c);
        var bg = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        fg.Freeze(); bg.Freeze();
        BadgeFg = fg;
        BadgeBg = bg;
    }
}
