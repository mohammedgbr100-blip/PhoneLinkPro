using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScrcpyDeck.Windows;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, bounded design-QA tool mode (Batch 4F — mirrors <see cref="SettingsSnapshotToolMode"/>):
/// when <c>PLP_TOOL_MODE=about-snapshot</c> is set EXACTLY, the app renders the About &amp; Support
/// window offscreen for a fixed set of truthful, deterministic scenarios and exits with a
/// meaningful code instead of running normally. Dispatched from <c>App.OnStartup</c> ahead of every
/// real service, exactly like every earlier snapshot tool mode — it never starts ADB, the device
/// watcher, or a real integrity re-verification beyond the deterministic fixtures each scenario
/// injects through the same test seams <see cref="AboutSelfTest"/> uses.
/// </summary>
internal static class AboutSnapshotToolMode
{
    internal const string ModeValue = "about-snapshot";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    private const double NarrowWidth = 920;
    private const double StandardWidth = 1200;
    private const double WideWidth = 1440;

    // Taller than a real desktop work area on purpose: About is a resizable DIALOG (its natural
    // size is ~560x760, not a full shell page), so every scenario's full scrollable content —
    // hero through the footer — is captured in one shot for truthful side-by-side QA, instead of
    // requiring a scroll gesture the snapshot tool cannot perform.
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
            _scratchDirectory = Path.Combine(Path.GetTempPath(), "plp_about_snapshot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratchDirectory);
        }
        catch (Exception ex)
        {
            return Fail($"cannot create scratch directory: {ex.GetType().Name}: {ex.Message}");
        }

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

        var savedIntegrityBaseDir = EngineIntegrityVerifier.BaseDirProvider;
        var savedIntegrityManifest = EngineIntegrityVerifier.ManifestTextProvider;
        var savedMissingFiles = ToolLocator.MissingFilesOverrideForTests;
        var previousLogOverride = Log.DirOverride;

        try
        {
            var count = 0;
            foreach (var scenario in OrderedScenarios())
            {
                var scenarioDir = Path.Combine(_scratchDirectory!, "scn_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scenarioDir);
                AppSettings.ResetForTests(scenarioDir);
                Log.DirOverride = Path.Combine(scenarioDir, "logs");

                if (Log.DirOverride == null)
                    return Fail("log isolation was not established for scenario '" + scenario.Name +
                                "' — refusing to capture, which could append to the real application log.");

                ToolLocator.MissingFilesOverrideForTests = null;
                AboutWindow.ResetForTests();
                SafeLinkLauncher.ResetForTests();
                ClipboardService.ResetForTests();
                NoticesWindow.ContentProviderForTests = null;

                EngineIntegrityVerifier.ResetForTests();
                EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
                EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;

                var ok = await CaptureAsync(scenario);
                if (!ok) return Fail($"capture failed for scenario '{scenario.Name}'");
                count++;

                try { Directory.Delete(scenarioDir, recursive: true); } catch { /* best effort */ }
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

            ToolLocator.MissingFilesOverrideForTests = savedMissingFiles;
            AboutWindow.ResetForTests();
            SafeLinkLauncher.ResetForTests();
            ClipboardService.ResetForTests();
            NoticesWindow.ContentProviderForTests = null;

            EngineIntegrityVerifier.ResetForTests();
            EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
            EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;

            AppSettings.ResetForTests(null);
            Log.DirOverride = previousLogOverride;
            watchdog.Dispose();
            try { if (_scratchDirectory != null) Directory.Delete(_scratchDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------- fixtures ----------------

    private sealed record Scenario(
        string Name, double Width,
        bool ToolsUnavailable = false,
        bool IntegrityFailed = false,
        bool ConsentNotAccepted = false,
        bool LinkFailed = false,
        bool CopySuccess = false,
        bool CopyFailed = false,
        bool OpenPolicyDialog = false,
        bool OpenNoticesDialog = false);

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("about-default", StandardWidth);
        yield return new Scenario("about-tools-unavailable", StandardWidth, ToolsUnavailable: true);
        yield return new Scenario("about-integrity-failed", StandardWidth, IntegrityFailed: true);
        yield return new Scenario("about-consent-not-accepted", StandardWidth, ConsentNotAccepted: true);
        yield return new Scenario("about-link-failed", StandardWidth, LinkFailed: true);
        yield return new Scenario("about-copy-success", StandardWidth, CopySuccess: true);
        yield return new Scenario("about-copy-failed", StandardWidth, CopyFailed: true);
        yield return new Scenario("about-policy-dialog", StandardWidth, OpenPolicyDialog: true);
        yield return new Scenario("about-notices-dialog", StandardWidth, OpenNoticesDialog: true);
        yield return new Scenario("about-narrow", NarrowWidth);
        yield return new Scenario("about-wide", WideWidth);
    }

    /// <summary>Order-independence proof, same contract as every earlier snapshot tool mode.</summary>
    private static List<Scenario> OrderedScenarios()
    {
        var list = Scenarios().ToList();
        var order = Environment.GetEnvironmentVariable("PLP_SNAPSHOT_SCENARIO_ORDER");
        if (string.Equals(order, "reversed", StringComparison.OrdinalIgnoreCase))
        {
            list.Reverse();
        }
        else if (order != null && order.StartsWith("rotate:", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(order["rotate:".Length..], out var n) && list.Count > 0)
        {
            n = ((n % list.Count) + list.Count) % list.Count;
            list = list.Skip(n).Concat(list.Take(n)).ToList();
        }
        return list;
    }

    // ---------------- capture ----------------

    private static async Task<bool> CaptureAsync(Scenario scenario)
    {
        if (scenario.ToolsUnavailable)
            ToolLocator.MissingFilesOverrideForTests = new[] { "PhoneLink.Engine.exe", "core.dat" };

        if (scenario.IntegrityFailed)
        {
            EngineIntegrityVerifier.ResetForTests();
            EngineIntegrityVerifier.ManifestTextProvider = () => null; // deterministic MissingManifestData
            await EngineIntegrityVerifier.VerifyAsync();
        }

        if (!scenario.ConsentNotAccepted)
            AppSettings.ConsentAccepted = true; // default scenario shows the "accepted" truthful state

        if (scenario.LinkFailed)
            SafeLinkLauncher.ProcessStartOverrideForTests = _ => false;
        if (scenario.CopySuccess)
            ClipboardService.SetTextOverrideForTests = _ => true;
        if (scenario.CopyFailed)
            ClipboardService.SetTextOverrideForTests = _ => false;

        var window = new AboutWindow
        {
            Width = scenario.Width,
            Height = Height,
            Left = OffscreenLeft,
            Top = OffscreenTop,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
        };

        Window? childDialog = null;
        try
        {
            window.Show();
            Settle(window);

            if (scenario.LinkFailed)
            {
                window.EmailBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Settle(window);
            }
            if (scenario.CopySuccess || scenario.CopyFailed)
            {
                window.CopySummaryBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Settle(window);
            }

            Window captureTarget = window;
            if (scenario.OpenPolicyDialog)
            {
                AboutWindow.PolicyShowOverrideForTests = w =>
                {
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = OffscreenLeft; w.Top = OffscreenTop;
                    w.ShowInTaskbar = false;
                    w.Show();
                };
                window.PolicyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Settle(window);
                childDialog = Application.Current.Windows.OfType<PolicyWindow>().LastOrDefault();
                if (childDialog != null) captureTarget = childDialog;
            }
            if (scenario.OpenNoticesDialog)
            {
                // Repair batch §10: deliberately NO ContentProviderForTests override here — the
                // snapshot must prove the real production path (reading the actual
                // THIRD-PARTY-NOTICES.txt shipped beside the executable), not fixture text.
                AboutWindow.NoticesShowOverrideForTests = w =>
                {
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = OffscreenLeft; w.Top = OffscreenTop;
                    w.ShowInTaskbar = false;
                    w.Show();
                };
                window.NoticesBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Settle(window);
                childDialog = Application.Current.Windows.OfType<NoticesWindow>().LastOrDefault();
                if (childDialog != null) captureTarget = childDialog;
            }

            Settle(captureTarget);
            if (!await WaitForRenderAsync(captureTarget)) return false;

            var w2 = captureTarget.ActualWidth;
            var h2 = captureTarget.ActualHeight;
            if (w2 < 1 || h2 < 1) return false;

            var bitmap = new RenderTargetBitmap((int)w2, (int)h2, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(captureTarget);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(_outputDirectory!, $"{scenario.Name}-w{scenario.Width:F0}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        }
        finally
        {
            try { childDialog?.Close(); } catch { /* teardown is best effort */ }
            try { window.Close(); } catch { /* teardown is best effort */ }
            AboutWindow.PolicyShowOverrideForTests = null;
            AboutWindow.NoticesShowOverrideForTests = null;
            SafeLinkLauncher.ProcessStartOverrideForTests = null;
            ClipboardService.SetTextOverrideForTests = null;
            NoticesWindow.ContentProviderForTests = null;
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
        Log.Info("AboutSnapshot: " + text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plp_about_snapshot.txt"), text);
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
