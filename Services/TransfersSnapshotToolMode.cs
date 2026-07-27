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
/// Hidden, bounded design-QA tool mode (Batch 4D — mirrors <see cref="WirelessSnapshotToolMode"/>
/// exactly): when <c>PLP_TOOL_MODE=transfers-snapshot</c> is set EXACTLY, the app renders the
/// file-transfers UserControl offscreen for a fixed set of deterministic scenarios and exits with
/// a meaningful code instead of running normally.
///
/// <list type="bullet">
///   <item><description>Dispatched from <c>App.OnStartup</c> BEFORE single-instance acquisition,
///   consent, integrity verification and any engine service, so it never starts ADB or the
///   device watcher for real and never disturbs a running normal instance.</description></item>
///   <item><description>Push/Pull/file-dialog outcomes are injected through
///   <see cref="TransfersView.PushOverrideForTests"/>/<see cref="TransfersView.PullOverrideForTests"/>/
///   <see cref="TransfersView.PickFilesOverrideForTests"/>/<see cref="TransfersView.PickSaveLocationOverrideForTests"/>
///   and device fixtures through the same <see cref="DeviceWatcher.DiscoveryProvider"/> seam
///   <see cref="WirelessSnapshotToolMode"/> already uses — never real adb, never a real native
///   file dialog.</description></item>
/// </list>
/// </summary>
internal static class TransfersSnapshotToolMode
{
    internal const string ModeValue = "transfers-snapshot";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    private const double NarrowWidth = 920;
    private const double StandardWidth = 1200;
    private const double WideWidth = 1440;
    private const double Height = 1500;

    internal static bool IsRequested()
        => string.Equals(
            Environment.GetEnvironmentVariable(ShellSnapshotToolMode.ModeVariable)?.Trim(),
            ModeValue, StringComparison.OrdinalIgnoreCase);

    private static string? _outputDirectory;
    private static string? _scratchDirectory;

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

        try
        {
            _scratchDirectory = Path.Combine(Path.GetTempPath(), "plp_transfers_snapshot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratchDirectory);
        }
        catch (Exception ex)
        {
            return Fail($"cannot create scratch directory: {ex.GetType().Name}: {ex.Message}");
        }

        // Batch 4E: TransfersView now reads AppSettings.DefaultTransferDestination on
        // construction. Redirect it to this scratch directory so a snapshot run never reads or
        // writes the real %AppData%\PhoneLinkPro\settings.json.
        AppSettings.ResetForTests(_scratchDirectory);

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
                TransfersView.AdbAvailableOverrideForTests = null;
                TransfersView.PushOverrideForTests = null;
                TransfersView.PullOverrideForTests = null;
                TransfersView.DiscoverDevicesOverrideForTests = null;
                TransfersView.PickFilesOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;

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

            TransfersView.AdbAvailableOverrideForTests = null;
            TransfersView.PushOverrideForTests = null;
            TransfersView.PullOverrideForTests = null;
            TransfersView.DiscoverDevicesOverrideForTests = null;
            TransfersView.PickFilesOverrideForTests = null;
            TransfersView.PickSaveLocationOverrideForTests = null;

            watchdog.Dispose();
            DeviceWatcher.ResetForTests();
            AppSettings.ResetForTests(null);
            try { if (_scratchDirectory != null) Directory.Delete(_scratchDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------- fixtures ----------------

    private static AdbDevice AuthorizedWireless(string endpoint, string model = "Galaxy S24 Ultra") =>
        new() { Serial = endpoint, State = "device", Brand = "Samsung", Model = model, Battery = 91 };

    private static string ScratchFile(string name)
    {
        var path = Path.Combine(_scratchDirectory!, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private sealed record Scenario(
        string Name, double Width,
        bool ToolsUnavailable = false,
        List<AdbDevice>? Devices = null,
        string[]? LocalFileNames = null,
        bool ClickChooseFiles = false,
        Func<string, Task<ProcessResult>>? PushResultFor = null,
        bool ClickSend = false,
        string? ReceiveRemotePath = null,
        bool ClickChooseSaveLocation = false,
        Func<Task<ProcessResult>>? PullResult = null,
        bool ClickReceive = false);

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("transfers-tools-unavailable", StandardWidth, ToolsUnavailable: true);

        yield return new Scenario("transfers-no-device", StandardWidth, Devices: new List<AdbDevice>());

        yield return new Scenario("transfers-ready", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.40:5555") });

        yield return new Scenario("transfers-send-selection", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.41:5555") },
            LocalFileNames: new[] { "عرض تقديمي.pdf", "photo with spaces.jpg" },
            ClickChooseFiles: true);

        yield return new Scenario("transfers-sending", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.42:5555") },
            LocalFileNames: new[] { "large-video.mp4" },
            ClickChooseFiles: true, ClickSend: true,
            PushResultFor: _ => new TaskCompletionSource<ProcessResult>().Task);   // never completes

        yield return new Scenario("transfers-send-success", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.43:5555") },
            LocalFileNames: new[] { "document.docx" },
            ClickChooseFiles: true, ClickSend: true,
            PushResultFor: _ => Task.FromResult(
                new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped. 12 MB/s (2048000 bytes in 0.16s)", "")));

        yield return new Scenario("transfers-partial-failure", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.44:5555") },
            LocalFileNames: new[] { "ok-file.txt", "denied-file.txt" },
            ClickChooseFiles: true, ClickSend: true,
            PushResultFor: local => Task.FromResult(local.Contains("denied")
                ? new ProcessResult(ProcessOutcome.Exited, 1, "", "adb: error: failed to copy: Permission denied")
                : new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", "")));

        yield return new Scenario("transfers-receive-validation", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.45:5555") },
            ReceiveRemotePath: "not-a-valid-android-path",
            ClickReceive: true);

        yield return new Scenario("transfers-receiving", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.46:5555") },
            ReceiveRemotePath: "/sdcard/DCIM/Camera/IMG_2024.jpg",
            ClickChooseSaveLocation: true, ClickReceive: true,
            PullResult: () => new TaskCompletionSource<ProcessResult>().Task);   // never completes

        yield return new Scenario("transfers-receive-success", StandardWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.47:5555") },
            ReceiveRemotePath: "/sdcard/DCIM/Camera/IMG_2024.jpg",
            ClickChooseSaveLocation: true, ClickReceive: true,
            PullResult: () => Task.FromResult(
                new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped. 9 MB/s (1024000 bytes in 0.11s)", "")));

        // Batch 4C-consistent collision fixture: same model/brand/state, colliding
        // ShellLayout.MaskSerial output — proves the disambiguator token carries over here too.
        yield return new Scenario("transfers-multiple-devices", StandardWidth,
            Devices: new List<AdbDevice>
            {
                AuthorizedWireless("192.168.1.42:5555"),
                AuthorizedWireless("192.168.1.55:5555"),
            });

        yield return new Scenario("transfers-narrow", NarrowWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.48:5555") });

        yield return new Scenario("transfers-wide", WideWidth,
            Devices: new List<AdbDevice> { AuthorizedWireless("192.168.1.49:5555") });
    }

    // ---------------- capture ----------------

    private static async Task<bool> CaptureAsync(Scenario scenario)
    {
        if (scenario.ToolsUnavailable) TransfersView.AdbAvailableOverrideForTests = () => false;

        if (scenario.Devices != null)
        {
            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(scenario.Devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
        }

        var view = new TransfersView();
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

            if (scenario.LocalFileNames != null)
            {
                var files = scenario.LocalFileNames.Select(ScratchFile).ToArray();
                TransfersView.PickFilesOverrideForTests = () => files;
            }
            if (scenario.ClickChooseFiles) view.ChooseFilesBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(window);

            if (scenario.ReceiveRemotePath != null) view.ReceiveRemotePathBox.Text = scenario.ReceiveRemotePath;
            if (scenario.ClickChooseSaveLocation)
            {
                var localTarget = ScratchFile("saved-from-phone.jpg");
                TransfersView.PickSaveLocationOverrideForTests = _ => localTarget;
                view.ChooseSaveLocationBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Settle(window);

            if (scenario.PushResultFor != null) TransfersView.PushOverrideForTests = (_, local, _, _) => scenario.PushResultFor(local);
            if (scenario.PullResult != null) TransfersView.PullOverrideForTests = (_, _, _, _) => scenario.PullResult();

            if (scenario.ClickSend) view.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (scenario.ClickReceive) view.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(window);
            // A second settle pass lets already-completed (non-pending) async continuations
            // finish repainting before capture.
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
            TransfersView.AdbAvailableOverrideForTests = null;
            TransfersView.PushOverrideForTests = null;
            TransfersView.PullOverrideForTests = null;
            TransfersView.PickFilesOverrideForTests = null;
            TransfersView.PickSaveLocationOverrideForTests = null;
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
        Log.Info("TransfersSnapshot: " + text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plp_transfers_snapshot.txt"), text);
        }
        catch { /* the exit code is the contract; the report file is best-effort */ }
    }

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
