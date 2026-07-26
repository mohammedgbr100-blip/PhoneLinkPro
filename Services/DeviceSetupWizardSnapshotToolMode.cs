using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Windows;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, bounded design-QA tool mode (Batch 4B — mirrors <see cref="DashboardSnapshotToolMode"/>
/// exactly): when <c>PLP_TOOL_MODE=setup-wizard-snapshot</c> is set EXACTLY, the app renders the
/// device-setup wizard offscreen for a fixed set of deterministic scenarios and exits with a
/// meaningful code instead of running normally.
///
/// <list type="bullet">
///   <item><description>Dispatched from <c>App.OnStartup</c> BEFORE single-instance
///   acquisition, consent, integrity verification and any engine service, so it never starts
///   ADB or the device watcher for real and never disturbs a running normal instance.</description></item>
///   <item><description>Device fixtures are injected straight into the wizard's own constructor
///   — never real adb.</description></item>
/// </list>
/// </summary>
internal static class DeviceSetupWizardSnapshotToolMode
{
    internal const string ModeValue = "setup-wizard-snapshot";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    private const double NarrowWidth = 920;   // ShellLayout.PreferredMinWidth
    private const double WideWidth = 1080;

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

        // Section 16: instrumented directly here too, not inferred from PLP_SELFTEST — a missing
        // resource or bad binding can depend on a specific scenario's fixture data (e.g. the
        // ToolsUnavailable render) that only this tool mode's own scenario list exercises.
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

            watchdog.Dispose();
            DeviceWatcher.ResetForTests();
        }
    }

    // ---------------- fixtures ----------------

    private sealed record Scenario(string Name, double Width, List<AdbDevice> Devices, int NextClicks,
        bool ToolsUnavailable = false);

    private static AdbDevice Authorized(string serial) =>
        new() { Serial = serial, State = "device", Brand = "Samsung", Model = "Galaxy S24 Ultra", Battery = 87 };
    private static AdbDevice Unauthorized(string serial) => new() { Serial = serial, State = "unauthorized" };
    private static AdbDevice Offline(string serial) => new() { Serial = serial, State = "offline", Model = "Tab S9" };

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("setup-tools-unavailable", NarrowWidth + 60, new List<AdbDevice>(), 0,
            ToolsUnavailable: true);
        yield return new Scenario("setup-no-device", NarrowWidth + 60, new List<AdbDevice>(), 0);
        yield return new Scenario("setup-dev-options-instruction", NarrowWidth + 60, new List<AdbDevice>(), 1);
        yield return new Scenario("setup-usb-debugging-instruction", NarrowWidth + 60, new List<AdbDevice>(), 2);
        yield return new Scenario("setup-unauthorized", NarrowWidth + 60,
            new List<AdbDevice> { Unauthorized("R7BX22CD4ZE") }, 0);
        yield return new Scenario("setup-authorized-success", NarrowWidth + 60,
            new List<AdbDevice> { Authorized("R5CX10AB9XD") }, 0);
        yield return new Scenario("setup-multiple-devices", NarrowWidth + 60,
            new List<AdbDevice> { Authorized("R5CX10AB9XD"), Unauthorized("192.168.1.42:5555"), Offline("R52W99YYZT") }, 0);
        yield return new Scenario("setup-narrow", NarrowWidth,
            new List<AdbDevice> { Authorized("R5CX10AB9XD"), Unauthorized("192.168.1.42:5555") }, 0);
        yield return new Scenario("setup-wide", WideWidth,
            new List<AdbDevice> { Authorized("R5CX10AB9XD") }, 0);
    }

    // ---------------- capture ----------------

    private static async Task<bool> CaptureAsync(Scenario scenario)
    {
        // The 9th scenario needs a truthful ToolsUnavailable render without the local engine
        // files actually being missing from this build's own output directory — set the seam
        // BEFORE construction, since the constructor's own RenderState() runs synchronously.
        var wasOverride = DeviceSetupWizardWindow.AdbAvailableOverrideForTests;
        if (scenario.ToolsUnavailable) DeviceSetupWizardWindow.AdbAvailableOverrideForTests = () => false;

        var window = new DeviceSetupWizardWindow(scenario.Devices)
        {
            Width = scenario.Width,
            Left = OffscreenLeft,
            Top = OffscreenTop,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
        };

        try
        {
            window.Show();
            for (var i = 0; i < scenario.NextClicks; i++)
                window.NextBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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
            try { window.Close(); } catch { /* teardown is best effort */ }
            DeviceSetupWizardWindow.AdbAvailableOverrideForTests = wasOverride;
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
        Log.Info("SetupWizardSnapshot: " + text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plp_setup_wizard_snapshot.txt"), text);
        }
        catch { /* the exit code is the contract; the report file is best-effort */ }
    }

    /// <summary>Same technique <see cref="DashboardVisualSelfTest"/> and
    /// <see cref="DeviceSetupWizardSelfTest"/> use to capture WPF's own binding/resource/converter
    /// trace output as data instead of Debug-output noise.</summary>
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
