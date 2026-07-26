namespace ScrcpyDeck.Services;

/// <summary>
/// Pure shell-layout decisions (Batch 3A), kept free of WPF types so they are directly
/// testable. Visual baseline: approved Direction D design baseline — Batch 2B, approved
/// 2026-07-25 — where the full 248px sidebar is shown at the 1440×900 reference size and
/// the 64px icon rail at 1366×768.
/// </summary>
public static class ShellLayout
{
    /// <summary>
    /// Window widths strictly below this use the compact 64px icon rail. Chosen between
    /// the two approved reference sizes (1366 compact / 1440 full) so a 1366×768 laptop —
    /// where the window is clamped below 1400 — always gets the approved compact rail.
    /// </summary>
    public const double CompactWidthThreshold = 1400;

    /// <summary>True when a window of this width must use the compact icon rail.</summary>
    public static bool IsCompactWidth(double windowWidth) => windowWidth < CompactWidthThreshold;

    // ---------------------------------------------------------------- adaptive sizing

    /// <summary>Preferred opening size — the 1440×900 design reference.</summary>
    public const double PreferredWidth = 1440;
    public const double PreferredHeight = 900;

    /// <summary>
    /// Preferred lower bound. This is a PREFERENCE, not a floor: on a work area smaller than
    /// this the minimum shrinks with it, because a minimum larger than the screen is what
    /// pushes chrome off the edge (a 1366×768 panel at 150% scaling is only ~911×464 DIP).
    /// </summary>
    public const double PreferredMinWidth = 920;
    public const double PreferredMinHeight = 560;

    /// <summary>Breathing room left around the window so it never touches the work edge.</summary>
    public const double WorkAreaMargin = 8;

    /// <summary>Opening size and lower bound for a shell on a work area of this size.</summary>
    public readonly record struct WindowMetrics(
        double Width, double Height, double MinWidth, double MinHeight);

    /// <summary>
    /// Chooses the window size for the available work area, in DIP.
    /// <para>
    /// Both the size and the minimum are capped by the work area, so the shell always fits
    /// on screen: the caller can apply MinWidth/MinHeight first and Width/Height second
    /// without WPF coercing the size back up beyond the screen.
    /// </para>
    /// </summary>
    public static WindowMetrics MeasureWindow(double workAreaWidth, double workAreaHeight)
    {
        var availableWidth = Math.Max(0, workAreaWidth - WorkAreaMargin);
        var availableHeight = Math.Max(0, workAreaHeight - WorkAreaMargin);

        var width = Math.Min(PreferredWidth, availableWidth);
        var height = Math.Min(PreferredHeight, availableHeight);

        return new WindowMetrics(
            width, height,
            Math.Min(PreferredMinWidth, width),
            Math.Min(PreferredMinHeight, height));
    }

    /// <summary>Top-left that centres a window inside a work area, never outside it.</summary>
    public static (double Left, double Top) CenterWithin(
        double workAreaLeft, double workAreaTop, double workAreaWidth, double workAreaHeight,
        double windowWidth, double windowHeight)
        => (workAreaLeft + Math.Max(0, (workAreaWidth - windowWidth) / 2),
            workAreaTop + Math.Max(0, (workAreaHeight - windowHeight) / 2));

    /// <summary>
    /// True when a window of this size at this position is wholly inside the work area.
    /// Used by the shell self-test to prove the startup bounds on every reference display.
    /// </summary>
    public static bool FitsWithin(
        double workAreaLeft, double workAreaTop, double workAreaWidth, double workAreaHeight,
        double left, double top, double width, double height)
        => left >= workAreaLeft - Epsilon
           && top >= workAreaTop - Epsilon
           && left + width <= workAreaLeft + workAreaWidth + Epsilon
           && top + height <= workAreaTop + workAreaHeight + Epsilon;

    /// <summary>Sub-pixel tolerance: WPF layout rounding must not fail a containment check.</summary>
    private const double Epsilon = 0.5;

    // ---------------------------------------------------------------- vertical budget

    /// <summary>Fixed shell chrome rows, mirroring GridLength.TitleBar / GridLength.StatusStrip.</summary>
    public const double TitleBarHeight = 44;
    public const double StatusStripHeight = 30;

    /// <summary>Outer 1px border, top and bottom.</summary>
    public const double ChromeBorderHeight = 2;

    /// <summary>Least content height that still leaves the page host usable.</summary>
    public const double MinimumContentHeight = 240;

    /// <summary>
    /// Shortest window that keeps the title bar, the status strip and a usable content host.
    /// Both chrome rows are fixed, so anything above this leaves the caption controls, the
    /// navigation rail and the status strip fully visible.
    /// </summary>
    public static double MinimumUsableHeight
        => TitleBarHeight + StatusStripHeight + ChromeBorderHeight + MinimumContentHeight;

    /// <summary>Height left for the page host inside a window of this height.</summary>
    public static double ContentHeightFor(double windowHeight)
        => Math.Max(0, windowHeight - TitleBarHeight - StatusStripHeight - ChromeBorderHeight);

    /// <summary>
    /// Work-area height in DIP for a physical panel at a DPI scale, minus the taskbar.
    /// The taskbar is measured in DIP (it scales with the display), so the usable height
    /// shrinks faster than the raw pixel count suggests: 768px at 150% is 512 DIP of screen
    /// but only ~464 DIP of work area.
    /// </summary>
    public static (double Width, double Height) WorkAreaFor(
        double physicalWidth, double physicalHeight, double dpiScale, double taskbarDip = 48)
        => (physicalWidth / dpiScale, (physicalHeight / dpiScale) - taskbarDip);

    /// <summary>
    /// Masks a device serial for at-a-glance display (Direction D rule: serials are masked
    /// by default with an explicit reveal affordance). Keeps the first four and last two
    /// characters: "R5CX10ABCXD" → "R5CX•••••XD". Values too short to meaningfully mask
    /// are returned unchanged; null/whitespace becomes an empty string.
    /// </summary>
    public static string MaskSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return "";
        var s = serial.Trim();
        if (s.Length <= 7) return s;
        return s[..4] + "•••••" + s[^2..];
    }

    /// <summary>
    /// The device label the shell is allowed to show.
    /// <para>
    /// <c>AdbDevice.DisplayName</c> falls back to the RAW serial whenever the model is not
    /// known — which is the normal state for an <c>unauthorized</c> device (details cannot
    /// be read before the user authorises on the handset) and for the whole window between
    /// connection and enrichment. For a wireless device that fallback is the bare
    /// <c>ip:port</c>. Showing it would defeat the masking rule above, so the fallback is
    /// masked here: the shell never renders an unmasked serial outside the explicit reveal
    /// control. A real model name is always preserved verbatim.
    /// </para>
    /// </summary>
    /// <param name="model">The device model, empty when not yet known.</param>
    /// <param name="displayName">The enriched label to use when <paramref name="model"/> is known.</param>
    /// <param name="serial">The raw serial, masked when it would otherwise be shown.</param>
    public static string SafeDeviceLabel(string? model, string? displayName, string? serial)
        => string.IsNullOrWhiteSpace(model)
            ? MaskSerial(serial)
            : (displayName ?? "").Trim();
}
