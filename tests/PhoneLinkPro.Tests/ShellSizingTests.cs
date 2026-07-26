using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3A — work-area-aware window sizing.
///
/// The decisions are pure, so every reference display is exercised here from PHYSICAL pixels
/// and a DPI SCALE rather than from a DIP number that quietly assumes 100% scaling. A
/// 1366-pixel panel is 1366 DIP at 100% but only ~911 DIP at 150%, and that is the case a
/// fixed 920 DIP minimum used to push off the screen.
/// </summary>
public sealed class ShellSizingTests
{
    /// <summary>name, physical width, physical height, DPI scale.</summary>
    public static TheoryData<string, double, double, double> ReferenceDisplays => new()
    {
        { "1366x768 @100%", 1366, 768, 1.00 },
        { "1366x768 @125%", 1366, 768, 1.25 },
        { "1366x768 @150%", 1366, 768, 1.50 },
        { "1440x900 @100%", 1440, 900, 1.00 },
        { "1440x900 @150%", 1440, 900, 1.50 },
    };

    // ---------------------------------------------------------------- DIP conversion

    [Fact] // the taskbar scales with the display, so usable height falls faster than pixels
    public void WorkAreaFor_ConvertsPhysicalPixelsAndTaskbarToDip()
    {
        var (width, height) = ShellLayout.WorkAreaFor(1366, 768, 1.50);
        Assert.Equal(910.67, width, 2);
        Assert.Equal(464.00, height, 2);
    }

    [Fact] // 100% scaling is the identity case minus the taskbar
    public void WorkAreaFor_AtHundredPercentOnlyRemovesTheTaskbar()
    {
        var (width, height) = ShellLayout.WorkAreaFor(1366, 768, 1.00);
        Assert.Equal(1366, width, 2);
        Assert.Equal(720, height, 2);
    }

    // ---------------------------------------------------------------- fits everywhere

    [Theory] // THE requirement: the shell fits inside the work area on every reference display
    [MemberData(nameof(ReferenceDisplays))]
    public void Window_FitsInsideTheWorkArea(string name, double physicalWidth, double physicalHeight, double scale)
    {
        var (workWidth, workHeight) = ShellLayout.WorkAreaFor(physicalWidth, physicalHeight, scale);
        var metrics = ShellLayout.MeasureWindow(workWidth, workHeight);
        var (left, top) = ShellLayout.CenterWithin(0, 0, workWidth, workHeight, metrics.Width, metrics.Height);

        Assert.True(
            ShellLayout.FitsWithin(0, 0, workWidth, workHeight, left, top, metrics.Width, metrics.Height),
            $"{name}: {metrics.Width:F1}x{metrics.Height:F1} at {left:F1},{top:F1} " +
            $"escapes work area {workWidth:F1}x{workHeight:F1}");
    }

    [Theory] // the minimum can never be the thing that pushes chrome off the screen
    [MemberData(nameof(ReferenceDisplays))]
    public void Minimum_NeverExceedsTheWorkArea(string name, double physicalWidth, double physicalHeight, double scale)
    {
        var (workWidth, workHeight) = ShellLayout.WorkAreaFor(physicalWidth, physicalHeight, scale);
        var metrics = ShellLayout.MeasureWindow(workWidth, workHeight);

        Assert.True(metrics.MinWidth <= workWidth, $"{name}: MinWidth {metrics.MinWidth} > work {workWidth}");
        Assert.True(metrics.MinHeight <= workHeight, $"{name}: MinHeight {metrics.MinHeight} > work {workHeight}");
        Assert.True(metrics.MinWidth <= metrics.Width, $"{name}: MinWidth exceeds Width");
        Assert.True(metrics.MinHeight <= metrics.Height, $"{name}: MinHeight exceeds Height");
    }

    [Theory] // title bar, status strip and a usable content host all survive
    [MemberData(nameof(ReferenceDisplays))]
    public void Window_KeepsChromeAndAUsableContentHost(string name, double physicalWidth, double physicalHeight, double scale)
    {
        var (workWidth, workHeight) = ShellLayout.WorkAreaFor(physicalWidth, physicalHeight, scale);
        var metrics = ShellLayout.MeasureWindow(workWidth, workHeight);

        Assert.True(metrics.Height >= ShellLayout.MinimumUsableHeight,
            $"{name}: {metrics.Height:F1} DIP leaves no usable content host");
        Assert.True(ShellLayout.ContentHeightFor(metrics.Height) >= ShellLayout.MinimumContentHeight,
            $"{name}: content host below the minimum");
    }

    [Theory] // the compact rail engages exactly where the design says it should
    [InlineData(1366, 768, 1.00, true)]
    [InlineData(1366, 768, 1.25, true)]
    [InlineData(1366, 768, 1.50, true)]
    [InlineData(1440, 900, 1.00, false)]   // 1432 DIP wide: full sidebar
    [InlineData(1440, 900, 1.50, true)]
    public void CompactRail_MatchesTheReferenceDisplays(
        double physicalWidth, double physicalHeight, double scale, bool compact)
    {
        var (workWidth, workHeight) = ShellLayout.WorkAreaFor(physicalWidth, physicalHeight, scale);
        var metrics = ShellLayout.MeasureWindow(workWidth, workHeight);
        Assert.Equal(compact, ShellLayout.IsCompactWidth(metrics.Width));
    }

    // ---------------------------------------------------------------- preferences kept

    [Fact] // a roomy desktop still opens at the 1440x900 design reference
    public void LargeWorkArea_UsesThePreferredSize()
    {
        var metrics = ShellLayout.MeasureWindow(2560, 1392);
        Assert.Equal(ShellLayout.PreferredWidth, metrics.Width);
        Assert.Equal(ShellLayout.PreferredHeight, metrics.Height);
        Assert.Equal(ShellLayout.PreferredMinWidth, metrics.MinWidth);
        Assert.Equal(ShellLayout.PreferredMinHeight, metrics.MinHeight);
    }

    [Fact] // a work area smaller than the preferred minimum shrinks the minimum with it
    public void SmallWorkArea_ShrinksTheMinimum()
    {
        var metrics = ShellLayout.MeasureWindow(910.67, 464);
        Assert.True(metrics.MinWidth < ShellLayout.PreferredMinWidth);
        Assert.True(metrics.MinHeight < ShellLayout.PreferredMinHeight);
        Assert.Equal(metrics.Width, metrics.MinWidth);
        Assert.Equal(metrics.Height, metrics.MinHeight);
    }

    [Theory] // degenerate work areas never produce a negative or oversized window
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(-100, -100)]
    public void DegenerateWorkArea_StaysNonNegative(double workWidth, double workHeight)
    {
        var metrics = ShellLayout.MeasureWindow(workWidth, workHeight);
        Assert.True(metrics.Width >= 0 && metrics.Height >= 0);
        Assert.True(metrics.MinWidth <= metrics.Width && metrics.MinHeight <= metrics.Height);
    }

    // ---------------------------------------------------------------- no oscillation

    [Theory] // sizing depends ONLY on the work area, so a resize can never feed back into it
    [MemberData(nameof(ReferenceDisplays))]
    public void Sizing_IsDeterministicAndIndependentOfCurrentWindowSize(
        string name, double physicalWidth, double physicalHeight, double scale)
    {
        var (workWidth, workHeight) = ShellLayout.WorkAreaFor(physicalWidth, physicalHeight, scale);
        var first = ShellLayout.MeasureWindow(workWidth, workHeight);
        var second = ShellLayout.MeasureWindow(workWidth, workHeight);
        Assert.Equal(first, second);

        // Re-measuring against the produced size must not flip the rail mode back and forth.
        var mode = ShellLayout.IsCompactWidth(first.Width);
        Assert.Equal(mode, ShellLayout.IsCompactWidth(second.Width));
    }

    [Theory] // no reference display lands on the threshold, where a 1px jitter would flap
    [MemberData(nameof(ReferenceDisplays))]
    public void ReferenceWidths_AreNotOnTheCompactThreshold(
        string name, double physicalWidth, double physicalHeight, double scale)
    {
        var (workWidth, workHeight) = ShellLayout.WorkAreaFor(physicalWidth, physicalHeight, scale);
        var metrics = ShellLayout.MeasureWindow(workWidth, workHeight);
        Assert.True(Math.Abs(metrics.Width - ShellLayout.CompactWidthThreshold) > 2,
            $"{name}: opening width {metrics.Width:F1} sits on the compact threshold");
    }

    // ---------------------------------------------------------------- centring

    [Fact] // centring inside a work area that starts away from the origin
    public void CenterWithin_RespectsTheWorkAreaOrigin()
    {
        var (left, top) = ShellLayout.CenterWithin(100, 50, 1000, 800, 800, 600);
        Assert.Equal(200, left);
        Assert.Equal(150, top);
    }

    [Fact] // an oversized window is pinned to the origin, never given a negative offset
    public void CenterWithin_NeverPlacesAWindowBeforeTheWorkArea()
    {
        var (left, top) = ShellLayout.CenterWithin(0, 0, 500, 400, 900, 700);
        Assert.Equal(0, left);
        Assert.Equal(0, top);
    }

    [Fact] // containment tolerates sub-pixel layout rounding but not a real overflow
    public void FitsWithin_IsToleranceBounded()
    {
        Assert.True(ShellLayout.FitsWithin(0, 0, 1000, 800, 0, 0, 1000.2, 800.2));
        Assert.False(ShellLayout.FitsWithin(0, 0, 1000, 800, 0, 0, 1008, 800));
        Assert.False(ShellLayout.FitsWithin(0, 0, 1000, 800, -8, 0, 1000, 800));
    }

    // ---------------------------------------------------------------- vertical budget

    [Fact] // the fixed chrome rows match the shell's GridLength tokens
    public void ChromeBudget_MatchesTheShellRows()
    {
        Assert.Equal(44, ShellLayout.TitleBarHeight);
        Assert.Equal(30, ShellLayout.StatusStripHeight);
        Assert.Equal(316, ShellLayout.MinimumUsableHeight);
    }

    [Theory] // content height is whatever the fixed rows leave behind
    [InlineData(456, 380)]
    [InlineData(900, 824)]
    [InlineData(40, 0)]
    public void ContentHeightFor_SubtractsTheFixedRows(double windowHeight, double expected)
        => Assert.Equal(expected, ShellLayout.ContentHeightFor(windowHeight));
}
