using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScrcpyDeck.Services;

/// <summary>
/// Proves the real shell fits inside the work area of every reference display.
///
/// <para>
/// This builds an actual MainWindow and reads its realised bounds — it is not a calculation
/// over a synthetic rectangle. The display is what is simulated: each scenario starts from
/// PHYSICAL pixels and a DPI SCALE, converts to WPF DIP, subtracts the taskbar, and injects
/// the result through <c>ShellWindowBounds.WorkAreaOverride</c>. The window is then shown,
/// the dispatcher pumped, and Left/Top/Right/Bottom compared against that work area.
/// </para>
///
/// <para>
/// A 1366 DIP-wide window would prove nothing: 1366 physical pixels at 150% is only ~911
/// DIP, which is the case that actually breaks. Every scenario below records all three
/// quantities so the distinction stays visible in the report.
/// </para>
/// </summary>
internal static class ShellFitSelfTest
{
    /// <summary>A reference display: physical panel size plus the Windows scaling factor.</summary>
    internal readonly record struct Scenario(string Name, double PhysicalWidth, double PhysicalHeight, double Scale);

    internal static readonly Scenario[] References =
    {
        new("1366x768 @100%", 1366, 768, 1.00),
        new("1366x768 @125%", 1366, 768, 1.25),
        new("1366x768 @150%", 1366, 768, 1.50),
        new("1440x900 @100%", 1440, 900, 1.00),
        new("1440x900 @150%", 1440, 900, 1.50),
    };

    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    internal static Result Run()
    {
        var lines = new StringBuilder();
        var failures = new List<string>();
        var total = 0;

        var systemDpi = VisualTreeHelper.GetDpi(new System.Windows.Controls.Border());
        lines.AppendLine($"  host DPI scale = {systemDpi.DpiScaleX:0.00}x  " +
                         $"host work area = {SystemParameters.WorkArea.Width:F1}x{SystemParameters.WorkArea.Height:F1} DIP");

        foreach (var scenario in References)
        {
            total++;
            var (dipWidth, dipHeight) = ShellLayout.WorkAreaFor(
                scenario.PhysicalWidth, scenario.PhysicalHeight, scenario.Scale);

            // Anchor the simulated work area at the host's origin so the window really can be
            // placed there; only the SIZE is what the scenario is testing.
            var host = SystemParameters.WorkArea;
            var area = new Rect(host.Left, host.Top, dipWidth, dipHeight);

            ShellWindowBounds.WorkAreaOverride = () => area;
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.Show();
                Pump(window);

                var left = window.Left;
                var top = window.Top;
                var right = left + window.ActualWidth;
                var bottom = top + window.ActualHeight;

                var fits = ShellLayout.FitsWithin(
                    area.Left, area.Top, area.Width, area.Height,
                    left, top, window.ActualWidth, window.ActualHeight);
                var compact = ShellChrome.GetIsCompact(window);

                lines.AppendLine(
                    $"  {scenario.Name,-16} {scenario.PhysicalWidth:F0}x{scenario.PhysicalHeight:F0}px " +
                    $"@{scenario.Scale:0.00}x -> work {area.Width:F1}x{area.Height:F1} DIP | " +
                    $"win {window.ActualWidth:F1}x{window.ActualHeight:F1} DIP at " +
                    $"L{left:F1} T{top:F1} R{right:F1} B{bottom:F1} | " +
                    $"workR{area.Right:F1} workB{area.Bottom:F1} | " +
                    $"rail={(compact ? "compact" : "full")} | {(fits ? "FITS" : "OVERFLOWS")}");

                if (!fits)
                    failures.Add($"{scenario.Name}: window {left:F1},{top:F1} {window.ActualWidth:F1}x{window.ActualHeight:F1} " +
                                 $"escapes work area {area}");

                // Chrome that must survive a short work area.
                if (window.ActualHeight < ShellLayout.MinimumUsableHeight)
                    failures.Add($"{scenario.Name}: window height {window.ActualHeight:F1} leaves no usable content host");
            }
            catch (Exception ex)
            {
                failures.Add($"{scenario.Name}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { window?.Close(); } catch { }
                ShellWindowBounds.WorkAreaOverride = null;
            }
        }

        // ---- maximised bounds are constrained to the work area (ShellWindowBounds) ----
        // A WindowChrome window is otherwise sized to the monitor plus the frame, drawing
        // about 8 DIP past every edge and tucking the status strip under the taskbar.
        total++;
        MainWindow? maximised = null;
        try
        {
            var real = SystemParameters.WorkArea;   // no override: the host's own work area
            maximised = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            maximised.Show();
            Pump(maximised);
            maximised.WindowState = WindowState.Maximized;
            Pump(maximised);

            // Window.Left/Top report the RESTORE position while maximised, so only the size
            // is meaningful here — and size is what the requirement is about.
            var fits = maximised.ActualWidth <= real.Width + 0.5
                       && maximised.ActualHeight <= real.Height + 0.5;

            lines.AppendLine(
                $"  maximised        win {maximised.ActualWidth:F1}x{maximised.ActualHeight:F1} DIP | " +
                $"work {real.Width:F1}x{real.Height:F1} DIP | " +
                $"overhang {maximised.ActualWidth - real.Width:F1}x{maximised.ActualHeight - real.Height:F1} | " +
                $"{(fits ? "FITS" : "OVERFLOWS")}");

            if (!fits)
                failures.Add($"maximised window {maximised.ActualWidth:F1}x{maximised.ActualHeight:F1} " +
                             $"exceeds work area {real.Width:F1}x{real.Height:F1}");
        }
        catch (Exception ex)
        {
            failures.Add($"maximise check: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { maximised?.Close(); } catch { }
        }

        var passed = failures.Count == 0;
        var report = $"fit: {total - failures.Count}/{total} reference displays fit inside the work area" +
                     Environment.NewLine + lines.ToString().TrimEnd() +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }
}
