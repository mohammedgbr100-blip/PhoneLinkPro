using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, bounded design-QA tool mode (Batch 4E — mirrors <see cref="TransfersSnapshotToolMode"/>
/// exactly): when <c>PLP_TOOL_MODE=settings-snapshot</c> is set EXACTLY, the app renders the
/// settings page offscreen for a fixed set of deterministic scenarios and exits with a
/// meaningful code instead of running normally.
///
/// <list type="bullet">
///   <item><description>Dispatched from <c>App.OnStartup</c> BEFORE single-instance acquisition,
///   consent, integrity verification and any engine service, so it never starts ADB or the
///   device watcher for real and never disturbs a running normal instance.</description></item>
///   <item><description><see cref="AppSettings"/> is redirected to an isolated scratch
///   directory for the whole run (never the real %AppData%\PhoneLinkPro\settings.json); Save
///   outcomes are injected through <see cref="SettingsView.SaveOverrideForTests"/> only for the
///   scenarios that need controllable timing (saving/failed) — every other scenario lets the
///   real atomic <see cref="SettingsStore"/> path run, since settings persistence has no
///   external process to fake.</description></item>
///   <item><description>The "tools unavailable" scenario uses
///   <see cref="ToolLocator.MissingFilesOverrideForTests"/> rather than deleting real engine
///   files from the developer's build output; the "integrity failed" scenario uses the existing
///   <see cref="EngineIntegrityVerifier"/> test seams the same way <see cref="EngineIntegrityGatingTests"/>
///   does.</description></item>
/// </list>
/// </summary>
internal static class SettingsSnapshotToolMode
{
    internal const string ModeValue = "settings-snapshot";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    private const double NarrowWidth = 920;
    private const double StandardWidth = 1200;
    private const double WideWidth = 1440;
    private const double Height = 1200;

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
            _scratchDirectory = Path.Combine(Path.GetTempPath(), "plp_settings_snapshot_" + Guid.NewGuid().ToString("N"));
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
        var previousLogOverride = Log.DirOverride;

        try
        {
            var count = 0;
            foreach (var scenario in OrderedScenarios())
            {
                // Repair batch §6: every scenario gets its OWN fresh directory — reusing one
                // shared scratch directory let an earlier scenario's real persisted write (e.g.
                // "settings-saved" writing Documents) leak into every later scenario's initial
                // state, which independent review caught (settings-reset-confirmation was
                // silently rendering "Documents" instead of the truthful Download default).
                var scenarioDir = Path.Combine(_scratchDirectory!, "scn_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scenarioDir);
                AppSettings.ResetForTests(scenarioDir);
                Log.DirOverride = Path.Combine(scenarioDir, "logs"); // repair batch §7

                // Profile-isolation checkpoint: fails fast, BEFORE this scenario captures
                // anything, if the redirection above was ever removed or bypassed.
                if (Log.DirOverride == null)
                    return Fail("log isolation was not established for scenario '" + scenario.Name +
                                "' — refusing to capture, which could append to the real application log.");

                ToolLocator.MissingFilesOverrideForTests = null;
                SettingsView.SaveOverrideForTests = null;
                SettingsView.ResetOverrideForTests = null;
                SettingsView.SetOnboardingSeenOverrideForTests = null;
                SettingsView.ClearLogsOverrideForTests = null;
                SettingsView.OpenPathOverrideForTests = null;

                // The integrity verifier caches its result process-wide until reset — without
                // this, a forced failure in one scenario would silently leak into every later
                // scenario's read-only status card.
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

            SettingsView.SaveOverrideForTests = null;
            SettingsView.ResetOverrideForTests = null;
            SettingsView.SetOnboardingSeenOverrideForTests = null;
            SettingsView.ClearLogsOverrideForTests = null;
            SettingsView.OpenPathOverrideForTests = null;
            ToolLocator.MissingFilesOverrideForTests = null;

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
        StandardRemoteDestination? PersistedDestination = null,
        StandardRemoteDestination? DraftDestination = null,
        bool ClickSave = false,
        bool SaveNeverCompletes = false,
        bool ForceSaveFailure = false,
        bool ToolsUnavailable = false,
        bool IntegrityFailed = false,
        bool OpenResetConfirm = false);

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("settings-default", StandardWidth);

        yield return new Scenario("settings-unsaved", StandardWidth,
            DraftDestination: StandardRemoteDestination.Pictures);

        yield return new Scenario("settings-saving", StandardWidth,
            DraftDestination: StandardRemoteDestination.Movies, ClickSave: true, SaveNeverCompletes: true);

        yield return new Scenario("settings-saved", StandardWidth,
            PersistedDestination: StandardRemoteDestination.Documents);

        yield return new Scenario("settings-save-failed", StandardWidth,
            DraftDestination: StandardRemoteDestination.Music, ClickSave: true, ForceSaveFailure: true);

        yield return new Scenario("settings-tools-unavailable", StandardWidth, ToolsUnavailable: true);

        yield return new Scenario("settings-integrity-failed", StandardWidth, IntegrityFailed: true);

        yield return new Scenario("settings-reset-confirmation", StandardWidth, OpenResetConfirm: true);

        yield return new Scenario("settings-narrow", NarrowWidth);

        yield return new Scenario("settings-wide", WideWidth);
    }

    /// <summary>
    /// Repair batch §12 (order-independence proof): execution order is controllable via
    /// <c>PLP_SNAPSHOT_SCENARIO_ORDER=reversed</c> or <c>rotate:N</c>. Output is keyed entirely
    /// by scenario NAME (never position), and every scenario now gets its own fresh directory, so
    /// the resulting set of PNGs must be byte-identical to a normal-order run regardless of this.
    /// </summary>
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
        if (scenario.PersistedDestination is { } persisted)
            AppSettings.TrySaveDefaultTransferDestination(persisted);

        if (scenario.ToolsUnavailable)
            ToolLocator.MissingFilesOverrideForTests = new[] { "PhoneLink.Engine.exe", "core.dat" };

        if (scenario.IntegrityFailed)
        {
            EngineIntegrityVerifier.ResetForTests();
            EngineIntegrityVerifier.ManifestTextProvider = () => null; // deterministic MissingManifestData
            await EngineIntegrityVerifier.VerifyAsync();
        }

        var view = new SettingsView();
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
            view.OnShown();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Settle(window);

            if (scenario.DraftDestination is { } draft) SelectDestination(view, draft);
            Settle(window);

            if (scenario.ClickSave)
            {
                if (scenario.SaveNeverCompletes)
                    SettingsView.SaveOverrideForTests = _ => new TaskCompletionSource<bool>().Task;
                else if (scenario.ForceSaveFailure)
                    SettingsView.SaveOverrideForTests = _ => Task.FromResult(false);

                view.SaveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Settle(window);

            if (scenario.OpenResetConfirm)
                view.ResetPreferencesBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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
            SettingsView.SaveOverrideForTests = null;
        }
    }

    private static void SelectDestination(SettingsView view, StandardRemoteDestination destination)
    {
        var index = 0;
        foreach (StandardRemoteDestination d in Enum.GetValues<StandardRemoteDestination>())
        {
            if (d == destination) { view.DestinationCombo.SelectedIndex = index; return; }
            index++;
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
        Log.Info("SettingsSnapshot: " + text);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "plp_settings_snapshot.txt"), text);
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
