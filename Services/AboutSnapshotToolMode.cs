using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    // About is a resizable DIALOG (its natural size is ~560x760, not a full shell page) whose
    // body scrolls; every scenario's full scrollable content — hero through the advanced-
    // diagnostics action and the footer — must be captured in one shot, since the snapshot tool
    // cannot perform a scroll gesture. The window is kept at this one fixed, modest height for
    // every scenario — see the remarks on <see cref="ComposeFullContentBitmap"/> for why growing
    // the WINDOW itself is not how the full content gets captured (Windows silently caps a
    // window's actual height at roughly the primary screen's height, even when the window is
    // positioned entirely off-screen — independent review, B2 repair).
    private const double WindowHeight = 760;

    // Documented capture-height ceiling (independent review, B2): the composed bitmap's total
    // height (header + full content extent + footer) is measured, never guessed — but the
    // measurement's result still needs a ceiling, so a runaway future layout regression (content
    // that grows without bound, ExtentHeight reporting something nonsensical) fails the capture
    // LOUDLY instead of silently producing a many-thousand-pixel image. Today's tallest real
    // scenario composes to well under 1600px; the ceiling below is comfortably above that with
    // headroom for reasonable future content growth — it is not today's exact output height
    // treated as the proof.
    private const double MaximumCaptureHeight = 2200;

    // Tolerance for the structural-completeness checks below: sub-pixel/DIP rounding from WPF's
    // own layout pass, never a guess at content height.
    private const double ContentTolerance = 1.0;

    /// <summary>Set by <see cref="CaptureAsync"/> immediately before it returns false, so
    /// <see cref="RunAsync"/> can report exactly which check failed instead of only "capture
    /// failed".</summary>
    private static string? _lastCaptureFailure;

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

                ToolLocator.MissingFilesOverrideForTests = null;
                AboutWindow.ResetForTests();
                SafeLinkLauncher.ResetForTests();
                ClipboardService.ResetForTests();
                NoticesWindow.ContentProviderForTests = null;

                EngineIntegrityVerifier.ResetForTests();
                EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
                EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;

                _lastCaptureFailure = null;
                var ok = await CaptureAsync(scenario);
                if (!ok)
                    return Fail($"capture failed for scenario '{scenario.Name}'" +
                                 (_lastCaptureFailure != null ? $": {_lastCaptureFailure}" : ""));
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
            Height = WindowHeight,
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

            RenderTargetBitmap bitmap;
            if (ReferenceEquals(captureTarget, window))
            {
                if (!TryComposeFullContentBitmap(window, out var composed, out var failure))
                {
                    _lastCaptureFailure = failure;
                    return false;
                }
                bitmap = composed!;

                // Independent post-capture proof: verify the ACTUALLY PRODUCED bitmap's height
                // against the expected full-content height, computed fresh here rather than
                // trusted from whichever path produced `bitmap`. This is deliberately NOT inside
                // TryComposeFullContentBitmap — it exists so that even a future regression which
                // bypasses composition entirely (reverting to a simple, small, clipped capture of
                // `window` itself) still fails here instead of silently succeeding.
                if (!CapturedBitmapHeightMatchesExpectedForTests(window, bitmap, out var heightFailure))
                {
                    _lastCaptureFailure = heightFailure;
                    return false;
                }
            }
            else
            {
                // Child dialogs (Policy/Notices) have no BodyScroll of their own and are small
                // enough that the window-height ceiling GrowToFitContent used to work around
                // never applies to them; their own natural, already-correct sizing is captured
                // directly, exactly as before.
                var w2 = captureTarget.ActualWidth;
                var h2 = captureTarget.ActualHeight;
                if (w2 < 1 || h2 < 1) return false;

                bitmap = new RenderTargetBitmap((int)w2, (int)h2, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(captureTarget);
            }

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

    /// <summary>
    /// Composes the About window's COMPLETE content — header, the full scrollable body extent,
    /// and the footer — into one bitmap, without ever needing the window itself to grow tall
    /// enough to show all of it.
    ///
    /// <para>
    /// Root cause (independent review, B2): the previous design tried to make this true by
    /// growing <c>window.Height</c> until <c>BodyScroll</c> needed no scrolling. That measures
    /// correctly, but Windows silently caps a window's actual on-screen height at roughly the
    /// PRIMARY MONITOR'S height — confirmed by direct measurement: on a 1440px-tall primary
    /// display, repeated growth attempts past ~1460px left <c>window.ActualHeight</c> frozen
    /// while <c>window.Height</c> itself kept climbing, even though the window was positioned
    /// entirely off-screen (<see cref="OffscreenLeft"/> = -6000). No amount of settling or
    /// retrying works around a hard OS ceiling; any content taller than roughly the primary
    /// screen's height could never be captured this way, on any machine with a shorter screen
    /// than the content.
    /// </para>
    ///
    /// <para>
    /// The fix sidesteps the window entirely: <c>BodyScroll.Content</c> (the StackPanel holding
    /// every card) is measured and arranged at its own full natural height regardless of the
    /// ScrollViewer's viewport — that is exactly what makes <c>ExtentHeight</c> a real number in
    /// the first place. Rendering that content FrameworkElement directly via its own
    /// <see cref="RenderTargetBitmap"/> captures it at full height, unclipped, with no
    /// dependency on window or screen size at all. The header and footer — both Grid rows OUTSIDE
    /// the ScrollViewer, always fully visible regardless of body scrolling — are cropped from a
    /// normal render of the (small, on-screen-height) window itself. The three pieces are then
    /// stacked into one final bitmap: top chrome, full content, bottom chrome.
    /// </para>
    /// </summary>
    private static bool TryComposeFullContentBitmap(
        AboutWindow window, out RenderTargetBitmap? bitmap, out string? failureReason)
    {
        bitmap = null;
        failureReason = null;

        if (window.BodyScroll.Content is not FrameworkElement content)
        {
            failureReason = "BodyScroll.Content is not a FrameworkElement";
            return false;
        }

        var scrollTop = window.BodyScroll.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
        var footerTop = scrollTop + window.BodyScroll.ActualHeight;
        var footerHeight = window.ActualHeight - footerTop;
        var contentHeight = content.ActualHeight;
        var totalHeight = scrollTop + contentHeight + footerHeight;
        var width = window.ActualWidth;

        if (width < 1 || scrollTop < 0 || footerHeight < 0)
        {
            failureReason = $"unexpected window geometry (width={width:F1}, scrollTop={scrollTop:F1}, footerHeight={footerHeight:F1})";
            return false;
        }
        if (totalHeight > MaximumCaptureHeight)
        {
            failureReason = $"content requires {totalHeight:F0}px, which exceeds the documented maximum " +
                             $"capture height of {MaximumCaptureHeight:F0}px — failing rather than silently clipping";
            return false;
        }

        // Footer completeness proof: the close button must lie inside the (small, on-screen-
        // height) window that the footer crop below is taken from.
        if (!ControlIsWithinWindowForTests(window.FooterCloseBtn, window, width, window.ActualHeight))
        {
            failureReason = "the footer close button lies outside the window bounds the footer crop is taken from";
            return false;
        }
        // Content completeness proof: the advanced-diagnostics action must lie inside the
        // content panel that is about to be rendered directly at full height.
        if (!ControlIsWithinWindowForTests(window.ExportDiagnosticsBtn, content, content.ActualWidth, contentHeight))
        {
            failureReason = "ExportDiagnosticsBtn lies outside the full-height content panel";
            return false;
        }

        const double dpi = 96;
        var windowBitmap = new RenderTargetBitmap((int)width, (int)window.ActualHeight, dpi, dpi, PixelFormats.Pbgra32);
        windowBitmap.Render(window);

        var contentBitmap = new RenderTargetBitmap((int)width, (int)contentHeight, dpi, dpi, PixelFormats.Pbgra32);
        contentBitmap.Render(content);

        var compose = new DrawingVisual();
        using (var dc = compose.RenderOpen())
        {
            if (scrollTop > 0)
                dc.DrawImage(
                    new CroppedBitmap(windowBitmap, new Int32Rect(0, 0, (int)width, (int)scrollTop)),
                    new Rect(0, 0, width, scrollTop));

            // AboutWindow is FlowDirection="RightToLeft". Rendering the whole Window applies
            // WPF's own RTL mirror exactly once, at the point FlowDirection is declared — the
            // header/footer crops above/below (taken from that normal window render) are
            // already correctly mirrored. Rendering `content` as an independent, isolated root
            // (its own RenderTargetBitmap, not nested under the Window's render) makes WPF
            // apply that SAME mirror a second time relative to content's own width, so it must
            // be counter-mirrored back here at composite time — confirmed empirically: without
            // this, every card's text renders as a horizontal mirror image.
            dc.PushTransform(new ScaleTransform(-1, 1, width / 2, 0));
            dc.DrawImage(contentBitmap, new Rect(0, scrollTop, width, contentHeight));
            dc.Pop();

            if (footerHeight > 0)
                dc.DrawImage(
                    new CroppedBitmap(windowBitmap, new Int32Rect(0, (int)footerTop, (int)width, (int)footerHeight)),
                    new Rect(0, scrollTop + contentHeight, width, footerHeight));
        }

        var composed = new RenderTargetBitmap((int)width, (int)totalHeight, dpi, dpi, PixelFormats.Pbgra32);
        composed.Render(compose);
        bitmap = composed;
        return true;
    }

    /// <summary>
    /// Independent proof that the FINAL bitmap actually produced for a main-window scenario is
    /// as tall as the full content genuinely requires — computed fresh from the live window/
    /// content, not reused from whatever <see cref="TryComposeFullContentBitmap"/> itself
    /// calculated. Deliberately kept as a SEPARATE check (not folded into that method) so a
    /// future change that stops calling it entirely — reverting to a simple, small, clipped
    /// capture of the window — still fails here instead of silently succeeding.
    /// </summary>
    private static bool CapturedBitmapHeightMatchesExpectedForTests(
        AboutWindow window, RenderTargetBitmap bitmap, out string? failureReason)
    {
        failureReason = null;
        if (window.BodyScroll.Content is not FrameworkElement content)
        {
            failureReason = "BodyScroll.Content is not a FrameworkElement";
            return false;
        }

        var scrollTop = window.BodyScroll.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
        var footerHeight = window.ActualHeight - (scrollTop + window.BodyScroll.ActualHeight);
        var expectedHeight = scrollTop + content.ActualHeight + footerHeight;

        if (Math.Abs(bitmap.PixelHeight - expectedHeight) > ContentTolerance + 1)
        {
            failureReason = $"captured bitmap height ({bitmap.PixelHeight}px) does not match the expected " +
                             $"full-content height ({expectedHeight:F0}px)";
            return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="control"/>'s full bounds lie within the rectangle
    /// (0,0)-(<paramref name="width"/>,<paramref name="height"/>), measured relative to
    /// <paramref name="root"/> via the real visual tree transform. Transforms BOTH diagonal
    /// corners and takes their min/max rather than assuming "top-left plus size equals bottom-
    /// right" — AboutWindow is FlowDirection="RightToLeft", which WPF implements as a mirror
    /// transform (effectively ScaleX=-1) applied to the whole visual tree, so a control's local
    /// (0,0) can transform to what is visually its top-RIGHT corner; a naive corner-plus-size
    /// calculation silently produces nonsense bounds under that mirror.
    /// </summary>
    private static bool ControlIsWithinWindowForTests(
        FrameworkElement control, Visual root, double width, double height)
    {
        var transform = control.TransformToAncestor(root);
        var corner1 = transform.Transform(new Point(0, 0));
        var corner2 = transform.Transform(new Point(control.ActualWidth, control.ActualHeight));
        var minX = Math.Min(corner1.X, corner2.X);
        var minY = Math.Min(corner1.Y, corner2.Y);
        var maxX = Math.Max(corner1.X, corner2.X);
        var maxY = Math.Max(corner1.Y, corner2.Y);
        return minX >= -ContentTolerance && minY >= -ContentTolerance
               && maxX <= width + ContentTolerance && maxY <= height + ContentTolerance;
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
