using System.Text.RegularExpressions;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3A — WPF design foundation and app shell.
///
/// Follows the repository's established idiom for XAML coverage: source-drift checks over
/// the tracked files (xUnit runs MTA, so no WPF visual tree is constructed here — window
/// construction stays with the PLP_SELFTEST harness, which builds MainWindow on a real
/// STA thread and therefore resolves every StaticResource these files reference).
/// </summary>
public sealed class DesignFoundationTests
{
    private static string Tokens => TestPaths.ReadRepoFile("Themes", "DesignTokens.xaml");
    private static string Styles => TestPaths.ReadRepoFile("Themes", "ShellStyles.xaml");
    private static string Icons => TestPaths.ReadRepoFile("Themes", "Icons.xaml");
    private static string Shell => TestPaths.ReadRepoFile("MainWindow.xaml");
    private static string App => TestPaths.ReadRepoFile("App.xaml");

    // ---------------------------------------------------------------- semantic tokens

    [Theory] // every required semantic token group has its key
    [InlineData("Brush.App.Background")]
    [InlineData("Brush.Shell.Background")]
    [InlineData("Brush.Surface.Raised")]
    [InlineData("Brush.Surface.RaisedAlt")]
    [InlineData("Brush.Surface.Inset")]
    [InlineData("Brush.Surface.Hover")]
    [InlineData("Brush.Surface.Selected")]
    [InlineData("Brush.DeviceSpace.Surface")]
    [InlineData("Brush.Border.Subtle")]
    [InlineData("Brush.Border.Strong")]
    [InlineData("Brush.Text.Primary")]
    [InlineData("Brush.Text.Secondary")]
    [InlineData("Brush.Text.Muted")]
    [InlineData("Brush.Text.Disabled")]
    [InlineData("Brush.Accent.Primary")]
    [InlineData("Brush.Accent.Bright")]
    [InlineData("Brush.Accent.Fill")]
    [InlineData("Brush.State.Live")]
    [InlineData("Brush.State.Success")]
    [InlineData("Brush.State.Warning")]
    [InlineData("Brush.State.Danger")]
    [InlineData("Brush.Focus.Outline")]
    [InlineData("Brush.Overlay.Scrim")]
    [InlineData("Radius.Control")]
    [InlineData("Radius.Card")]
    [InlineData("Radius.Dialog")]
    [InlineData("Space.Medium")]
    [InlineData("Size.ControlHeight")]
    [InlineData("Size.SidebarWidth")]
    [InlineData("Size.SidebarCompactWidth")]
    [InlineData("Size.Icon")]
    [InlineData("Font.UI")]
    [InlineData("Font.Mono")]
    [InlineData("FontSize.PageTitle")]
    [InlineData("FontSize.Body")]
    [InlineData("Opacity.Disabled")]
    public void Tokens_RequiredSemanticKeysExist(string key)
        => Assert.Contains($"x:Key=\"{key}\"", Tokens);

    [Theory] // approved Direction D values are pinned (drift = deliberate design change)
    [InlineData("#0C0D10")]   // app background
    [InlineData("#0A0B0E")]   // shell background
    [InlineData("#C9A24B")]   // gold brand
    [InlineData("#46D5C8")]   // teal live
    [InlineData("#5FBF6E")]   // success
    [InlineData("#E0A93B")]   // warning
    [InlineData("#E06B5E")]   // danger
    [InlineData("#EDEBE5")]   // primary text
    public void Tokens_PinApprovedDirectionDPalette(string hex)
        => Assert.Contains(hex, Tokens);

    [Fact] // resource names describe purpose, never the underlying technology
    public void FoundationResourceNames_ContainNoTechnologyNames()
    {
        foreach (var file in new[] { Tokens, Styles, Icons })
        {
            var keys = Regex.Matches(file, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
            foreach (var key in keys)
            {
                Assert.DoesNotContain("adb", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("scrcpy", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ffmpeg", key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("engine", key, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact] // the foundation is merged app-wide, with the legacy theme still present for
           // the not-yet-migrated feature pages
    public void App_MergesFoundationDictionariesAndKeepsLegacyTheme()
    {
        Assert.Contains("Themes/DesignTokens.xaml", App);
        Assert.Contains("Themes/Icons.xaml", App);
        Assert.Contains("Themes/ShellStyles.xaml", App);
        Assert.Contains("Themes/Theme.xaml", App);
    }

    // ---------------------------------------------------------------- reusable styles

    [Theory] // the reusable control set promised by Batch 3A
    [InlineData("Button.Primary")]
    [InlineData("Button.Ghost")]
    [InlineData("Button.GhostDanger")]
    [InlineData("Button.Danger")]
    [InlineData("Button.Icon")]
    [InlineData("Button.Caption")]
    [InlineData("Nav.Item")]
    [InlineData("Nav.Action")]
    [InlineData("Card.Surface")]
    [InlineData("Pill.Brand")]
    [InlineData("Pill.Live")]
    [InlineData("Indicator.Dot")]
    [InlineData("Divider.Horizontal")]
    [InlineData("Divider.Vertical")]
    [InlineData("Shell.Icon")]
    [InlineData("Shell.FocusVisual")]
    public void Styles_RequiredReusableKeysExist(string key)
        => Assert.Contains($"x:Key=\"{key}\"", Styles);

    [Fact] // disabled buttons go neutral (Direction D final polish): the danger button's
           // disabled trigger must strip the red fill
    public void DangerButton_DisabledStateIsNeutral()
    {
        var danger = Regex.Match(Styles,
            "x:Key=\"Button.Danger\".*?</Style>", RegexOptions.Singleline).Value;
        Assert.Contains("IsEnabled", danger);
        Assert.Contains("Brush.Text.Muted", danger);
        Assert.Contains("Brush.Border.Subtle", danger);
    }

    [Fact] // keyboard focus is visible: nav + buttons opt into the shell focus visual
    public void Styles_ApplyVisibleFocusTreatment()
    {
        Assert.Contains("FocusVisualStyle", Styles);
        Assert.Contains("Brush.Focus.Outline", Styles);
    }

    // ---------------------------------------------------------------- app shell

    [Theory] // the resolved user-facing terminology, exactly as approved
    [InlineData("لوحة الأجهزة")]
    [InlineData("بث الشاشة")]
    [InlineData("الاتصال اللاسلكي")]
    [InlineData("المشغّل المتقدم")]
    [InlineData("إدارة التطبيقات")]
    [InlineData("نقل الملفات")]
    [InlineData("حول البرنامج والدعم")]
    [InlineData("الإعدادات")]
    public void Shell_UsesTheApprovedNavigationTerminology(string label)
        => Assert.Contains(label, Shell);

    [Theory] // every existing page destination keeps its navigation element name, so
             // SelectNav / PLP_START and Nav_Checked keep working unchanged
    [InlineData("NavDashboard")]
    [InlineData("NavMirror")]
    [InlineData("NavWireless")]
    [InlineData("NavLauncher")]
    [InlineData("NavApps")]
    [InlineData("NavFiles")]
    public void Shell_KeepsEveryPageNavigationElement(string name)
        => Assert.Contains($"x:Name=\"{name}\"", Shell);

    [Fact] // the code-behind still maps every page key to its view (no destination lost)
    public void Shell_NavigateStillMapsEveryPage()
    {
        var code = TestPaths.ReadRepoFile("MainWindow.xaml.cs");
        foreach (var page in new[] { "Dashboard", "Mirror", "Wireless", "Launcher", "Apps", "Files" })
            Assert.Contains($"\"{page}\"", code);
        foreach (var view in new[] { "DashboardView", "MirrorView", "WirelessView", "LauncherView", "AppsView", "TransfersView" })
            Assert.Contains($"new {view}()", code);
    }

    [Fact] // second-independent-review: onboarding must never re-advertise drag-and-drop, which
           // TransfersView intentionally never implements (file selection is a picker dialog and
           // the remote path is always typed)
    public void Onboarding_NeverAdvertisesDragAndDropForFileTransfer()
    {
        var code = TestPaths.ReadRepoFile("Windows", "OnboardingWindow.xaml.cs");
        Assert.DoesNotContain("بالسحب والإفلات", code);
        Assert.Contains("استخدم «نقل الملفات»", code);
    }

    [Fact] // RTL-first shell with explicit LTR islands for technical values
    public void Shell_IsRtlWithExplicitLtrIslands()
    {
        Assert.Contains("FlowDirection=\"RightToLeft\"", Shell);
        // version badge, strip version, device count and serial are LTR islands
        var ltrCount = Regex.Matches(Shell, "FlowDirection=\"LeftToRight\"").Count;
        Assert.True(ltrCount >= 4, $"expected ≥4 explicit LTR islands in the shell, found {ltrCount}");
        // the mono text role is itself an LTR island for every future technical value
        Assert.Contains("FlowDirection", Styles);
    }

    [Fact] // icon-only surfaces carry accessible names + Arabic tooltips
    public void Shell_IconOnlyControlsHaveAccessibleNames()
    {
        foreach (var name in new[] { "BtnClose", "BtnMax", "BtnMin", "BtnHelp", "BtnAbout" })
        {
            var element = Regex.Match(Shell, $"x:Name=\"{name}\".*?</Button>", RegexOptions.Singleline).Value;
            Assert.Contains("AutomationProperties.Name", element);
            Assert.Contains("ToolTip", element);
        }
        // nav items keep an accessible name + tooltip even when compact mode hides labels
        foreach (var name in new[] { "NavDashboard", "NavMirror", "NavWireless",
                                     "NavLauncher", "NavApps", "NavFiles", "NavAboutItem", "NavSettings" })
        {
            var element = Regex.Match(Shell, $"x:Name=\"{name}\"[^>]*", RegexOptions.Singleline).Value;
            Assert.Contains("AutomationProperties.Name", element);
            Assert.Contains("ToolTip", element);
        }
    }

    [Fact] // shell chrome heights come from tokens, not scattered literals
    public void Shell_UsesTokenizedChromeMetrics()
    {
        Assert.Contains("GridLength.TitleBar", Shell);
        Assert.Contains("GridLength.StatusStrip", Shell);
        Assert.Contains("Size.SidebarWidth", Shell);
        Assert.Contains("Size.SidebarCompactWidth", Shell);
    }
}

/// <summary>Pure shell-layout decisions (compact threshold + serial masking).</summary>
public sealed class ShellLayoutTests
{
    // ---------------------------------------------------------------- compact threshold

    [Theory] // approved reference sizes: 1440×900 full sidebar, 1366×768 compact rail
    [InlineData(1440, false)]
    [InlineData(1920, false)]
    [InlineData(1366, true)]
    [InlineData(1120, true)]
    [InlineData(920, true)]
    public void CompactMode_MatchesApprovedReferenceSizes(double width, bool compact)
        => Assert.Equal(compact, ShellLayout.IsCompactWidth(width));

    [Fact] // boundary: the threshold itself is full mode; just below is compact
    public void CompactMode_BoundaryBehavesDeterministically()
    {
        Assert.False(ShellLayout.IsCompactWidth(ShellLayout.CompactWidthThreshold));
        Assert.True(ShellLayout.IsCompactWidth(ShellLayout.CompactWidthThreshold - 0.5));
    }

    [Fact] // the threshold sits strictly between the two approved reference widths
    public void CompactThreshold_SitsBetweenTheReferenceSizes()
        => Assert.InRange(ShellLayout.CompactWidthThreshold, 1367, 1440);

    // ---------------------------------------------------------------- serial masking

    [Fact] // Direction D masking shape: first four + bullets + last two
    public void MaskSerial_MasksTheMiddleOfARealSerial()
        => Assert.Equal("R5CX•••••XD", ShellLayout.MaskSerial("R5CX10ABCXD"));

    [Fact] // wireless serials (ip:port) are masked the same way
    public void MaskSerial_MasksWirelessSerials()
        => Assert.Equal("192.•••••55", ShellLayout.MaskSerial("192.168.1.20:5555"));

    [Theory] // values too short to meaningfully mask pass through unchanged
    [InlineData("ABC", "ABC")]
    [InlineData("ABCDEFG", "ABCDEFG")]
    public void MaskSerial_ShortValuesPassThrough(string input, string expected)
        => Assert.Equal(expected, ShellLayout.MaskSerial(input));

    [Theory] // never throws on degenerate input
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskSerial_DegenerateInputYieldsEmpty(string? input)
        => Assert.Equal("", ShellLayout.MaskSerial(input));

    [Fact] // masked output never leaks the middle of the serial
    public void MaskSerial_NeverContainsTheHiddenMiddle()
    {
        var masked = ShellLayout.MaskSerial("R5CX10ABCXD");
        Assert.DoesNotContain("10ABC", masked);
        Assert.Contains("•••••", masked);
    }

    // ------------------------------------------------- safe device label (B3)
    // AdbDevice.DisplayName falls back to the RAW serial when the model is unknown — the
    // normal state for an unauthorized device and before enrichment completes. The shell
    // must never render that fallback unmasked.

    [Fact] // USB serial fallback is masked, not shown raw
    public void SafeDeviceLabel_MasksUsbSerialFallback()
        => Assert.Equal("R5CX•••••XD",
            ShellLayout.SafeDeviceLabel("", "R5CX10ABCXD", "R5CX10ABCXD"));

    [Fact] // wireless ip:port fallback is masked too — no LAN address on screen
    public void SafeDeviceLabel_MasksWirelessFallback()
        => Assert.Equal("192.•••••55",
            ShellLayout.SafeDeviceLabel("", "192.168.1.20:5555", "192.168.1.20:5555"));

    [Fact] // a real model name is preserved verbatim, brand included
    public void SafeDeviceLabel_PreservesRealModelName()
        => Assert.Equal("Samsung SM-A536B",
            ShellLayout.SafeDeviceLabel("SM-A536B", "Samsung SM-A536B", "R5CX10ABCXD"));

    [Theory] // null / blank / whitespace models all take the masked path
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SafeDeviceLabel_BlankModelFallsBackToMasked(string? model)
        => Assert.Equal("R5CX•••••XD",
            ShellLayout.SafeDeviceLabel(model, "R5CX10ABCXD", "R5CX10ABCXD"));

    [Fact] // the label the compact tile tooltip is built from never leaks the hidden middle
    public void SafeDeviceLabel_CompactTooltipNeverLeaksTheHiddenMiddle()
    {
        var label = ShellLayout.SafeDeviceLabel("", "R5CX10ABCXD", "R5CX10ABCXD");
        var tooltip = $"{label} — بانتظار الإذن على الهاتف";
        Assert.DoesNotContain("10ABC", tooltip);
        Assert.Contains("•••••", tooltip);
    }

    [Fact] // degenerate input never throws and never yields null
    public void SafeDeviceLabel_DegenerateInputYieldsEmpty()
        => Assert.Equal("", ShellLayout.SafeDeviceLabel(null, null, null));
}
