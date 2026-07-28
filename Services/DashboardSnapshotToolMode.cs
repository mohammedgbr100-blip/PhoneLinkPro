using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, bounded design-QA tool mode (Batch 4A — mirrors <see cref="ShellSnapshotToolMode"/>
/// exactly): when <c>PLP_TOOL_MODE=dashboard-snapshot</c> is set EXACTLY, the app renders the
/// new Dashboard 2.0 UserControl offscreen for a fixed set of deterministic device fixtures
/// and window widths, then exits with a meaningful code instead of running normally.
///
/// <list type="bullet">
///   <item><description>Dispatched from <c>App.OnStartup</c> BEFORE single-instance
///   acquisition, consent, integrity verification and any engine service — exactly like
///   ShellSnapshotToolMode, so this mode never starts ADB or the device watcher for real and
///   never disturbs a running normal instance.</description></item>
///   <item><description>Device fixtures are injected through the same
///   <see cref="DeviceWatcher.DiscoveryProvider"/> seam the existing self-tests already use
///   (DashboardMissedUpdateOrderingSelfTest, DashboardStaleContinuationSelfTest) — never real
///   adb — and enrichment is short-circuited via <see cref="DashboardView.EnrichOverrideForTests"/>
///   since every fixture already carries its model/battery/resolution.</description></item>
///   <item><description>Reuses <see cref="ShellSnapshotToolMode"/>'s output-directory validation
///   and env var names (<c>PLP_TOOL_MODE</c> / <c>PLP_SNAPSHOT_OUT</c>) with a distinct mode
///   value, so a normal launch or a shell-snapshot run is never affected.</description></item>
/// </list>
/// </summary>
internal static class DashboardSnapshotToolMode
{
    internal const string ModeValue = "dashboard-snapshot";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    /// <summary>Content-area widths the Dashboard must render cleanly at (Phase 4).</summary>
    private const double NarrowWidth = 920;   // ShellLayout.PreferredMinWidth
    private const double StandardWidth = 1200;
    private const double WideWidth = 1440;    // ShellLayout.PreferredWidth
    private const double UltraWideWidth = 1920;   // Batch 4A M1 repair: 1920x1080-equivalent content width
    private const double Height = 820;

    // Repair batch 5B (§4): the former IsModeRequested()/IsRequested() dispatch pair is gone —
    // AutomatedModeClassifier is the one place that reads PLP_TOOL_MODE; App.OnStartup dispatches
    // on its resolved AutomatedMode, never on a per-tool re-check of the raw environment variable.

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

    /// <summary>Runs every fixture scenario and returns the process exit code. Never throws.</summary>
    internal static async Task<int> RunAsync()
    {
        var watchdog = new System.Threading.Timer(
            _ => Environment.Exit(ExitFailure), null, (int)WatchdogBudget.TotalMilliseconds,
            System.Threading.Timeout.Infinite);

        var wasSuppressed = DashboardView.SuppressRefreshForHarness;
        var wasEnrich = DashboardView.EnrichOverrideForTests;
        DashboardView.SuppressRefreshForHarness = true;
        DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;   // fixtures are pre-enriched

        try
        {
            var count = 0;
            foreach (var (name, width, makeDevices) in Scenarios())
            {
                // Independent fixture per scenario: no selection/state carried over from
                // whatever the previous capture left behind.
                AppState.SetCurrent(null);
                DeviceWatcher.ResetForTests();

                var ok = await CaptureAsync(name, width, makeDevices());
                if (!ok) return Fail($"capture failed for scenario '{name}'");
                count++;
            }

            Report($"OK: {count} snapshot(s) written to {_outputDirectory}");
            return ExitSuccess;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            watchdog.Dispose();
            DashboardView.SuppressRefreshForHarness = wasSuppressed;
            DashboardView.EnrichOverrideForTests = wasEnrich;
            DeviceWatcher.ResetForTests();
        }
    }

    // ---------------- fixtures ----------------

    private static AdbDevice Usb(string serial, string brand, string model, string android,
        string resolution, int battery)
        => new()
        {
            Serial = serial, State = "device", Brand = brand, Model = model,
            AndroidVersion = android, Resolution = resolution, Battery = battery,
        };

    private static AdbDevice Unauthorized(string serial)
        => new() { Serial = serial, State = "unauthorized" };

    private static AdbDevice Offline(string serial, string brand, string model)
        => new() { Serial = serial, State = "offline", Brand = brand, Model = model };

    /// <summary>
    /// Each entry is a FACTORY, not a shared instance: DeviceWatcher.SyncSelection's
    /// UpdateState case (existing, untouched production behavior) mutates AppState.CurrentDevice's
    /// .State field in place rather than swapping the reference, so reusing one AdbDevice
    /// instance's identity (by serial) across independently-captured scenarios would let an
    /// earlier scenario's state leak into a later one purely as a test-fixture artifact. Fresh
    /// objects per scenario, plus resetting AppState/DeviceWatcher before each capture below,
    /// keeps every render an independent, deterministic fixture.
    /// </summary>
    private static IEnumerable<(string Name, double Width, Func<List<AdbDevice>> Devices)> Scenarios()
    {
        List<AdbDevice> Authorized() => new()
        {
            Usb("R5CX10AB9XD", "Samsung", "Galaxy S24 Ultra", "16", "3120x1440", 87),
        };
        List<AdbDevice> Multi() => new()
        {
            Usb("R5CX10AB9XD", "Samsung", "Galaxy S24 Ultra", "16", "3120x1440", 87),
            Unauthorized("192.168.1.42:5555"),
            Offline("R52W99YYZT", "Samsung", "Galaxy Tab S9"),
        };

        yield return ("dashboard-empty", StandardWidth, () => new List<AdbDevice>());
        yield return ("dashboard-authorized-usb", StandardWidth, Authorized);
        yield return ("dashboard-unauthorized", StandardWidth,
            () => new List<AdbDevice> { Unauthorized("R7BX22CD4ZE") });
        yield return ("dashboard-multiple-devices", StandardWidth, Multi);
        yield return ("dashboard-narrow", NarrowWidth, Multi);
        yield return ("dashboard-wide-desktop", WideWidth, Authorized);
        yield return ("dashboard-ultrawide-desktop", UltraWideWidth, Authorized);
    }

    // ---------------- capture ----------------

    private static async Task<bool> CaptureAsync(string name, double width, List<AdbDevice> devices)
    {
        var view = new DashboardView();
        var window = new Window
        {
            Width = width,
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

            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
            Settle(window);
            DeviceWatcher.Stop();

            if (!await WaitForRenderAsync(window)) return false;

            var w = window.ActualWidth;
            var h = window.ActualHeight;
            if (w < 1 || h < 1) return false;

            var bitmap = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(_outputDirectory!, $"{name}-w{width:F0}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        }
        finally
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            try { window.Close(); } catch { /* teardown is best effort */ }
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
        Log.Info("DashboardSnapshot: " + text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plp_dashboard_snapshot.txt"), text);
        }
        catch { /* the exit code is the contract; the report file is best-effort */ }
    }
}
