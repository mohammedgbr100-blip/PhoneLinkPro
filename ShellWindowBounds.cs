using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScrcpyDeck.Services;

namespace ScrcpyDeck;

/// <summary>
/// Keeps the shell window inside the monitor work area.
///
/// <para>
/// Two problems are solved here. At STARTUP the opening size comes from
/// <see cref="ShellLayout.MeasureWindow"/> so a small work area — a 1366×768 panel at 150%
/// scaling is only ~911×464 DIP — never produces a window larger than the screen. When
/// MAXIMISED, a WindowChrome window is otherwise sized by Windows to the monitor rectangle
/// plus the frame, which draws roughly 8 DIP past every edge and tucks the status strip under
/// the taskbar; MaxWidth/MaxHeight are recomputed from the current monitor's work area when
/// the window enters the maximised state, and cleared back to unconstrained when it restores.
/// </para>
/// </summary>
internal static class ShellWindowBounds
{
    /// <summary>
    /// Self-test hook: substitutes the work area so the reference displays can be exercised
    /// on any developer machine. Null in production, where SystemParameters.WorkArea is used
    /// exactly as before.
    /// </summary>
    internal static Func<Rect>? WorkAreaOverride;

    /// <summary>The work area the shell sizes itself against, in DIP.</summary>
    internal static Rect WorkArea => WorkAreaOverride?.Invoke() ?? SystemParameters.WorkArea;


    /// <summary>
    /// Applies the work-area-aware opening size and position. Minimums are set BEFORE the
    /// size, so WPF cannot coerce the window back up to a minimum larger than the screen.
    /// </summary>
    internal static void ApplyStartupBounds(Window window)
    {
        var area = WorkArea;
        var metrics = ShellLayout.MeasureWindow(area.Width, area.Height);

        window.MinWidth = metrics.MinWidth;
        window.MinHeight = metrics.MinHeight;
        window.Width = metrics.Width;
        window.Height = metrics.Height;

        // Position explicitly: CenterScreen centres on the primary screen's work area, which
        // is not necessarily the one measured above (and not the one the self-test injects).
        var (left, top) = ShellLayout.CenterWithin(
            area.Left, area.Top, area.Width, area.Height, metrics.Width, metrics.Height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
    }

    /// <summary>
    /// Constrains the MAXIMISED size to the work area of the monitor the window is on.
    /// <para>
    /// A WindowChrome window is otherwise sized by Windows to the monitor rectangle plus the
    /// frame — about 8 DIP past every edge, which tucks the status strip under the taskbar.
    /// MaxWidth/MaxHeight is used rather than a WM_GETMINMAXINFO hook because WPF's own
    /// handling of that message runs after any hook added at SourceInitialized and overwrites
    /// it; the WPF constraint is deterministic. The constraint is lifted on restore so a
    /// normal window can still be dragged and sized freely, including across monitors.
    /// </para>
    /// </summary>
    internal static void ApplyMaximizeConstraint(Window window)
    {
        if (window.WindowState != WindowState.Maximized)
        {
            window.MaxWidth = double.PositiveInfinity;
            window.MaxHeight = double.PositiveInfinity;
            return;
        }

        var area = MonitorWorkArea(window) ?? WorkArea;
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
    }

    /// <summary>
    /// Work area of the monitor this window occupies, in DIP. Falls back to null when the
    /// window has no handle yet; the injected override always wins so the self-test can
    /// simulate a display.
    /// </summary>
    private static Rect? MonitorWorkArea(Window window)
    {
        if (WorkAreaOverride != null) return WorkAreaOverride();

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return null;

        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return null;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        // GetMonitorInfo reports PHYSICAL pixels; the window is laid out in DIP.
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(0, 0,
            (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX,
            (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY);
    }

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
