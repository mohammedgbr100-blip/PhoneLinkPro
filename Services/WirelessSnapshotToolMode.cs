using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, bounded design-QA tool mode (Batch 4C — mirrors <see cref="DashboardSnapshotToolMode"/>
/// exactly): when <c>PLP_TOOL_MODE=wireless-snapshot</c> is set EXACTLY, the app renders the
/// wireless-pairing UserControl offscreen for a fixed set of deterministic scenarios and exits
/// with a meaningful code instead of running normally.
///
/// <list type="bullet">
///   <item><description>Dispatched from <c>App.OnStartup</c> BEFORE single-instance acquisition,
///   consent, integrity verification and any engine service, so it never starts ADB or the
///   device watcher for real and never disturbs a running normal instance.</description></item>
///   <item><description>Pair/Connect outcomes are injected through
///   <see cref="WirelessView.PairOverrideForTests"/>/<see cref="WirelessView.ConnectOverrideForTests"/>
///   and device fixtures through the same <see cref="DeviceWatcher.DiscoveryProvider"/> seam
///   <see cref="DashboardSnapshotToolMode"/> already uses — never real adb.</description></item>
/// </list>
/// </summary>
internal static class WirelessSnapshotToolMode
{
    internal const string ModeValue = "wireless-snapshot";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    private const double NarrowWidth = 920;   // ShellLayout.PreferredMinWidth
    private const double StandardWidth = 1200;
    private const double WideWidth = 1440;    // ShellLayout.PreferredWidth
    // Tall enough that the full page (tools banner through the privacy note) renders without
    // the inner ScrollViewer needing to scroll — every card must be visible in the capture.
    private const double Height = 1500;

    internal static bool IsRequested()
        => string.Equals(
            Environment.GetEnvironmentVariable(ShellSnapshotToolMode.ModeVariable)?.Trim(),
            ModeValue, StringComparison.OrdinalIgnoreCase);

    private static string? _outputDirectory;

    internal static int Prepare()
    {
        var directory = ShellSnapshotToolMode.ValidateOutputDirectory(
            Environment.GetEnvironmentVariable(ShellSnapshotToolMode.OutVariable), out var error);
        if (directory == null) return Fail(error);

        try { Directory.CreateDirectory(directory); }
        catch (Exception ex)
        {
            return Fail($"cannot create output directory '{directory}': {ex.GetType().Name}: {ex.Message}");
        }

        _outputDirectory = directory;
        return ExitSuccess;
    }

    internal static async Task<int> RunAsync()
    {
        var watchdog = new System.Threading.Timer(
            _ => Environment.Exit(ExitFailure), null, (int)WatchdogBudget.TotalMilliseconds,
            System.Threading.Timeout.Infinite);

        var bindingErrors = new List<string>();
        var listener = new BindingErrorListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.Refresh();

        try
        {
            var count = 0;
            foreach (var scenario in Scenarios())
            {
                AppState.SetCurrent(null);
                DeviceWatcher.ResetForTests();
                WirelessView.AdbAvailableOverrideForTests = null;
                WirelessView.PairOverrideForTests = null;
                WirelessView.ConnectOverrideForTests = null;
                WirelessView.DiscoverDevicesOverrideForTests = null;

                var ok = await CaptureAsync(scenario);
                if (!ok) return Fail($"capture failed for scenario '{scenario.Name}'");
                count++;
            }

            if (bindingErrors.Count > 0)
                return Fail($"{bindingErrors.Count} WPF binding/resource error(s): " + string.Join(" | ", bindingErrors.Take(5)));

            Report($"OK: {count} snapshot(s) written to {_outputDirectory}");
            return ExitSuccess;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            PresentationTraceSources.Refresh();

            WirelessView.AdbAvailableOverrideForTests = null;
            WirelessView.PairOverrideForTests = null;
            WirelessView.ConnectOverrideForTests = null;
            WirelessView.DiscoverDevicesOverrideForTests = null;

            watchdog.Dispose();
            DeviceWatcher.ResetForTests();
        }
    }

    // ---------------- fixtures ----------------

    private static AdbDevice AuthorizedWireless(string endpoint) =>
        new() { Serial = endpoint, State = "device", Brand = "Samsung", Model = "Galaxy S24 Ultra", Battery = 91 };

    private sealed record Scenario(
        string Name, double Width,
        bool ToolsUnavailable = false,
        string? PairHost = null, string? PairPort = null, string? PairCode = null, bool ClickPair = false,
        Func<Task<ProcessResult>>? PairResult = null,
        string? ConnectHost = null, string? ConnectPort = null, bool ClickConnect = false,
        Func<Task<ProcessResult>>? ConnectResult = null,
        List<AdbDevice>? Devices = null);

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("wireless-tools-unavailable", StandardWidth, ToolsUnavailable: true);

        yield return new Scenario("wireless-instructions", StandardWidth);

        yield return new Scenario("wireless-validation-errors", StandardWidth,
            PairHost: "not-an-ip", PairPort: "abc", PairCode: "12", ClickPair: true,
            ConnectHost: "also-not-an-ip", ConnectPort: "999999", ClickConnect: true);

        yield return new Scenario("wireless-ready-to-pair", StandardWidth,
            PairHost: "192.168.1.42", PairPort: "37123", PairCode: "246813");

        yield return new Scenario("wireless-pairing", StandardWidth,
            PairHost: "192.168.1.42", PairPort: "37123", PairCode: "246813", ClickPair: true,
            PairResult: () => new TaskCompletionSource<ProcessResult>().Task);   // never completes

        yield return new Scenario("wireless-paired-needs-connect", StandardWidth,
            PairHost: "192.168.1.42", PairPort: "37123", PairCode: "246813", ClickPair: true,
            PairResult: () => Task.FromResult(
                new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", "")));

        yield return new Scenario("wireless-connect-failed", StandardWidth,
            ConnectHost: "192.168.1.42", ConnectPort: "5555", ClickConnect: true,
            ConnectResult: () => Task.FromResult(
                new ProcessResult(ProcessOutcome.Exited, 1,
                    "failed to connect to 192.168.1.42:5555: Connection refused", "")));

        yield return new Scenario("wireless-connected", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.42:5555") });

        // Batch 4C hardening: same model, same state, colliding ShellLayout.MaskSerial output
        // ("192.•••••55" for both) — this is the genuine collision path the disambiguator token
        // exists for, not merely two devices that happen to differ some other way.
        yield return new Scenario("wireless-multiple-devices", StandardWidth,
            Devices: new List<AdbDevice>
            {
                AuthorizedWireless("192.168.1.42:5555"),
                AuthorizedWireless("192.168.1.55:5555"),
            });

        yield return new Scenario("wireless-narrow", NarrowWidth);

        yield return new Scenario("wireless-wide", WideWidth);
    }

    // ---------------- capture ----------------

    private static async Task<bool> CaptureAsync(Scenario scenario)
    {
        if (scenario.ToolsUnavailable) WirelessView.AdbAvailableOverrideForTests = () => false;
        if (scenario.PairResult != null) WirelessView.PairOverrideForTests = (_, _) => scenario.PairResult();
        if (scenario.ConnectResult != null) WirelessView.ConnectOverrideForTests = _ => scenario.ConnectResult();

        if (scenario.Devices != null)
        {
            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(scenario.Devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
        }

        var view = new WirelessView();
        var window = new Window
        {
            Width = scenario.Width,
            Height = Height,
            Left = OffscreenLeft,
            Top = OffscreenTop,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = (Brush)Application.Current.FindResource("Brush.App.Background"),
            Content = view,
        };

        try
        {
            window.Show();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Settle(window);
            if (scenario.Devices != null) DeviceWatcher.Stop();

            if (scenario.PairHost != null) view.PairHostBox.Text = scenario.PairHost;
            if (scenario.PairPort != null) view.PairPortBox.Text = scenario.PairPort;
            if (scenario.PairCode != null) view.PairCodeBox.Password = scenario.PairCode;
            if (scenario.ConnectHost != null) view.ConnectHostBox.Text = scenario.ConnectHost;
            if (scenario.ConnectPort != null) view.ConnectPortBox.Text = scenario.ConnectPort;
            Settle(window);

            if (scenario.ClickPair) view.PairBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (scenario.ClickConnect) view.ConnectBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(window);
            // A second settle pass lets the (already-completed, non-pairing) async continuations
            // that were posted to the dispatcher finish repainting before capture.
            Settle(window);

            if (!await WaitForRenderAsync(window)) return false;

            var w = window.ActualWidth;
            var h = window.ActualHeight;
            if (w < 1 || h < 1) return false;

            var bitmap = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(_outputDirectory!, $"{scenario.Name}-w{scenario.Width:F0}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        }
        finally
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            try { window.Close(); } catch { /* teardown is best effort */ }
            WirelessView.AdbAvailableOverrideForTests = null;
            WirelessView.PairOverrideForTests = null;
            WirelessView.ConnectOverrideForTests = null;
        }
    }

    private static async Task<bool> WaitForRenderAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? onRendering = null;
        onRendering = (_, _) => { CompositionTarget.Rendering -= onRendering; rendered.TrySetResult(true); };
        CompositionTarget.Rendering += onRendering;

        var finished = await Task.WhenAny(rendered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != rendered.Task) { CompositionTarget.Rendering -= onRendering; return false; }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        return true;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }

    // ---------------- reporting ----------------

    private static int Fail(string message)
    {
        Report("FAIL: " + message);
        return ExitFailure;
    }

    private static void Report(string text)
    {
        Log.Info("WirelessSnapshot: " + text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plp_wireless_snapshot.txt"), text);
        }
        catch { /* the exit code is the contract; the report file is best-effort */ }
    }

    /// <summary>Same technique <see cref="DeviceSetupWizardSnapshotToolMode"/> uses to capture
    /// WPF's own binding/resource/converter trace output as data instead of Debug-output noise.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly List<string> _sink;
        public BindingErrorListener(List<string> sink) => _sink = sink;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message)) _sink.Add(message);
        }
    }
}
