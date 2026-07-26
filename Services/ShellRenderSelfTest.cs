using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScrcpyDeck.Services;

/// <summary>
/// Runtime design assertions for the PLP_SELFTEST harness.
///
/// <para>
/// These verify RENDERED values, not source text. Theme.xaml is merged last and carries an
/// implicit TextBlock style, which outranks property inheritance — so a shell control can
/// declare the right Foreground in XAML and still paint the wrong colour. Only a realised
/// visual tree can tell the difference, which is why this runs on the STA self-test thread
/// (xUnit runs MTA and cannot host it).
/// </para>
///
/// <para>
/// Every control is shown far offscreen and the dispatcher is pumped, because a
/// ContentPresenter does not inflate its generated TextBlock until a real render pass has
/// happened — <c>UpdateLayout()</c> alone is not enough.
/// </para>
/// </summary>
internal static class ShellRenderSelfTest
{
    /// <summary>Outcome of one self-test run; <paramref name="Report"/> is human-readable.</summary>
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    // Expected rendered colours (approved Direction D baseline).
    private const string NavUnchecked = "#FFB4B6BF";   // Brush.Text.Secondary
    private const string NavChecked = "#FFC9A24B";     // Brush.Accent.Primary
    private const string DangerEnabled = "#FFE06B5E";  // Brush.State.Danger
    private const string DangerDisabled = "#FF8A8D96"; // Brush.Text.Muted
    private const string PrimaryText = "#FF16130A";    // Brush.Text.OnAccent

    /// <summary>The only implicit (Type-keyed) styles the application may carry.</summary>
    private const string ExpectedImplicitStyles = "textblock:theme.xaml,tooltip:shellstyles.xaml";

    internal static Result Run()
    {
        var failures = new List<string>();
        var measurements = new List<string>();
        var total = 0;

        void Expect(string name, string expected, Brush? actual)
        {
            total++;
            var value = (actual as SolidColorBrush)?.Color.ToString() ?? "<none>";
            measurements.Add($"{name}={value}");
            if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{name}: expected {expected}, got {value}");
        }

        // ---- 1 + 2: navigation label tracks the control Foreground in both states ----
        var nav = new RadioButton
        {
            Style = (Style)Application.Current.FindResource("Nav.Item"),
            Tag = Application.Current.FindResource("Icon.Device"),
            Content = "لوحة الأجهزة",
        };
        var navHost = ShowOffscreen(nav);
        try
        {
            Expect("nav.unchecked", NavUnchecked, FindDescendant<TextBlock>(nav)?.Foreground);
            nav.IsChecked = true;
            Settle(navHost);
            Expect("nav.checked", NavChecked, FindDescendant<TextBlock>(nav)?.Foreground);
        }
        finally { Close(navHost); }

        // ---- 3 + 4: ghost-danger button content follows enabled/disabled triggers ----
        var danger = new Button
        {
            Style = (Style)Application.Current.FindResource("Button.GhostDanger"),
            Content = "إعادة الفحص",
        };
        var dangerHost = ShowOffscreen(danger);
        try
        {
            Expect("ghostDanger.enabled", DangerEnabled, FindDescendant<TextBlock>(danger)?.Foreground);
            danger.IsEnabled = false;
            Settle(dangerHost);
            Expect("ghostDanger.disabled", DangerDisabled, FindDescendant<TextBlock>(danger)?.Foreground);
        }
        finally { Close(dangerHost); }

        // ---- 5: primary button content is legible on the gold fill ----
        var primary = new Button
        {
            Style = (Style)Application.Current.FindResource("Button.Primary"),
            Content = "ابدأ",
        };
        var primaryHost = ShowOffscreen(primary);
        try
        {
            Expect("primary", PrimaryText, FindDescendant<TextBlock>(primary)?.Foreground);
        }
        finally { Close(primaryHost); }

        // ---- 6: structural guard on implicit (Type-keyed) application resources ----
        total++;
        var implicitStyles = EnumerateImplicitStyles();
        measurements.Add($"implicitStyles=[{implicitStyles}]");
        if (!string.Equals(implicitStyles, ExpectedImplicitStyles, StringComparison.Ordinal))
            failures.Add($"implicitStyles: expected [{ExpectedImplicitStyles}], got [{implicitStyles}]");

        var passed = failures.Count == 0;
        var report = $"design: {total - failures.Count}/{total} runtime assertions passed" +
                     Environment.NewLine + "  " + string.Join(" ", measurements) +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    /// <summary>
    /// Every implicit style carried by the merged application dictionaries, as
    /// "type:file" pairs in a stable order. A new implicit style anywhere in the resource
    /// graph changes this string — which is exactly the drift that made the original
    /// Foreground defect invisible.
    /// </summary>
    private static string EnumerateImplicitStyles()
    {
        var found = new List<string>();
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            var source = dictionary.Source?.OriginalString ?? "(inline)";
            var file = System.IO.Path.GetFileName(source).ToLowerInvariant();
            foreach (var key in dictionary.Keys)
                if (key is Type type)
                    found.Add($"{type.Name.ToLowerInvariant()}:{file}");
        }
        found.Sort(StringComparer.Ordinal);
        return string.Join(",", found);
    }

    // ---------------- realisation helpers ----------------

    private static Window ShowOffscreen(UIElement content)
    {
        var window = new Window
        {
            Width = 420,
            Height = 240,
            Left = -6000,
            Top = 200,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = Brushes.Black,
            Content = content,
        };
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>
    /// Drains the dispatcher so the window handle sizes and every ContentPresenter inflates.
    /// The self-test runs before Application.Run, so a nested frame is the only pump.
    /// </summary>
    private static void Settle(Window window)
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

    private static void Close(Window window)
    {
        try { window.Close(); } catch { /* teardown is best effort */ }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
