using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Shared source-text guard for the engine-status Grid's RTL spacer column, used by both
/// <c>AboutTests</c> and <c>SettingsTests</c> (AboutWindow.xaml and SettingsView.xaml carry an
/// identical Auto/24/Auto status Grid). A lightweight structural extraction (find the enclosing
/// &lt;Grid&gt; precisely, the same technique the repo's other source guards use for C# method
/// bodies) rather than a raw whole-file string search, so the assertions below can only be
/// satisfied by the actual enclosing Grid, not by an unrelated line anywhere in the file.
///
/// This is visual regression protection only — it proves the XAML still declares the intended
/// column shape, not that WPF renders it at the intended physical spacing under every DPI.
/// </summary>
internal static class XamlSpacerColumnGuard
{
    /// <summary>
    /// Extracts the smallest enclosing &lt;Grid&gt;...&lt;/Grid&gt; block that contains
    /// <paramref name="anchorName"/> (an x:Name inside one of the two status groups).
    ///
    /// <para>
    /// Repair (independent review, §12): a plain <c>LastIndexOf("&lt;Grid", ...)</c> would match
    /// <c>&lt;Grid.ColumnDefinitions&gt;</c> — a property-element on the SAME enclosing Grid,
    /// not a nested Grid, but still textually starting with the literal substring "&lt;Grid" —
    /// and land there instead of the real <c>&lt;Grid ...&gt;</c> opening tag if it happens to
    /// sit closer to the anchor. The regex below requires the character immediately after
    /// "&lt;Grid" to be whitespace or '&gt;' (an actual element-tag boundary), which
    /// "&lt;Grid." (a property element) never satisfies.
    /// </para>
    /// </summary>
    internal static string ExtractStatusGrid(string xaml, string anchorName)
    {
        var anchorIdx = xaml.IndexOf(anchorName, StringComparison.Ordinal);
        Assert.True(anchorIdx > 0, $"'{anchorName}' must still exist");

        var opens = System.Text.RegularExpressions.Regex.Matches(xaml[..anchorIdx], @"<Grid[\s>]");
        Assert.True(opens.Count > 0, $"'{anchorName}' must be inside a <Grid> element");
        var gridStart = opens[^1].Index;

        var gridEnd = xaml.IndexOf("</Grid>", gridStart, StringComparison.Ordinal);
        Assert.True(gridEnd > gridStart, "the enclosing Grid must have a matching close tag");

        return xaml[gridStart..(gridEnd + "</Grid>".Length)];
    }

    /// <summary>
    /// Asserts the extracted Grid: (1) declares a three-column Auto/&lt;positive&gt;/Auto shape,
    /// (2) places its two status-group panels in columns 0 and 2 — never touching column 1 — and
    /// (3) does not additionally rely on the old outer-edge <c>Margin="0,0,24,0"</c> as the gap
    /// (the spacer column IS the gap now, not a margin stacked on top of it).
    /// </summary>
    internal static void AssertHasDedicatedSpacerColumn(string grid)
    {
        var colDefsStart = grid.IndexOf("<Grid.ColumnDefinitions>", StringComparison.Ordinal);
        var colDefsEnd = grid.IndexOf("</Grid.ColumnDefinitions>", StringComparison.Ordinal);
        Assert.True(colDefsStart >= 0 && colDefsEnd > colDefsStart, "the Grid must declare ColumnDefinitions");
        var colDefs = grid[colDefsStart..colDefsEnd];

        // Repair (independent review, §12): a plain `Width="..."` pattern also matches inside
        // `MinWidth="..."` (regex has no notion of attribute boundaries — "MinWidth=" simply
        // CONTAINS the substring "Width="). The negative lookbehind requires the character
        // immediately before "Width" to be anything other than a letter, so "MinWidth=" can
        // never satisfy it while a real standalone `Width="..."` always does.
        var widths = System.Text.RegularExpressions.Regex.Matches(colDefs, @"(?<![A-Za-z])Width=""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, widths.Count);
        Assert.Equal("Auto", widths[0]);
        Assert.Equal("Auto", widths[2]);
        Assert.True(double.TryParse(widths[1], out var spacerWidth), $"spacer column width '{widths[1]}' must be numeric");
        Assert.True(spacerWidth > 0, $"spacer column width must be positive, was {spacerWidth}");

        Assert.Contains("Grid.Column=\"0\"", grid, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=\"1\"", grid, StringComparison.Ordinal);

        Assert.DoesNotContain("Margin=\"0,0,24,0\"", grid, StringComparison.Ordinal);
    }
}
