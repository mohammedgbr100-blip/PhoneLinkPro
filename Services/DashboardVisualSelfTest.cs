using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Runtime proof for the Dashboard 2.0 visual layer (Batch 4A; repaired after the independent
/// pre-commit review found several of the original assertions vacuous or too weak). Everything
/// here needs a real, rendered visual tree — exactly why it lives in the PLP_SELFTEST harness
/// alongside ShellRenderSelfTest and the Batch 3B/3C/3D dashboard self-tests, and not in xUnit
/// (which cannot construct a real DashboardView — see those classes' remarks for why).
///
/// <para>
/// Device fixtures go through the same DeviceWatcher.DiscoveryProvider seam the other dashboard
/// self-tests already use; enrichment is short-circuited via EnrichOverrideForTests. The one
/// scenario that needs a mirror "session" (F) drives ScrcpyService through its own test seams
/// (Launcher/EngineFileCheck/IntegrityCheck) with an in-memory fake process handle that never
/// touches disk or starts anything — no real adb/bridge/scrcpy process is ever started by this
/// self-test.
/// </para>
/// </summary>
internal static class DashboardVisualSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    internal static Result Run()
    {
        var lines = new StringBuilder();
        var failures = new List<string>();
        var total = 0;

        void Expect(string name, bool condition)
        {
            total++;
            if (!condition) failures.Add(name);
        }

        var wasSuppressed = DashboardView.SuppressRefreshForHarness;
        var wasEnrich = DashboardView.EnrichOverrideForTests;
        var appStateBefore = AppState.CurrentDevice;
        DashboardView.SuppressRefreshForHarness = true;
        DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;

        // ---- 2: zero WPF binding errors anywhere in this run (B1's Boolean-to-Brush error,
        // above all) — captured for the WHOLE run, not just one scenario, so a regression
        // introduced by any later change here is caught too.
        var bindingErrors = new List<string>();
        var listener = new BindingErrorListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.Refresh();

        try
        {
            // ---- A: zero devices — empty state, and the lower list/heading stay hidden ----
            {
                var view = new DashboardView();
                Load(view);
                var ex = Raise(new List<AdbDevice>());
                Expect("A: no exception rendering zero devices", ex == null);
                Expect("A: HeroEmpty visible with no devices", view.HeroEmpty.Visibility == Visibility.Visible);
                Expect("A: HeroLive collapsed with no devices", view.HeroLive.Visibility == Visibility.Collapsed);
                Expect("A: OthersHeading hidden with no devices", view.OthersHeading.Visibility == Visibility.Collapsed);
                Expect("A: DeviceList hidden with no devices", view.DeviceList.Visibility == Visibility.Collapsed);
                Unload(view);
            }

            // ---- B: multiple devices — hero not duplicated into the lower list (M2 repair) ----
            {
                var view = new DashboardView();
                Load(view);
                var devices = new List<AdbDevice>
                {
                    new() { Serial = "s1", State = "device", Model = "Pixel 8" },
                    new() { Serial = "s2", State = "unauthorized" },
                    new() { Serial = "s3", State = "offline", Model = "Tab S9" },
                };
                var ex = Raise(devices);
                Expect("B: no exception rendering multiple devices", ex == null);
                Expect("B: HeroLive visible once a device is selected",
                    view.HeroLive.Visibility == Visibility.Visible);
                Expect("B: DeviceList excludes the hero device and holds exactly the other two",
                    view.DeviceList.Items.Count == 2);
                Expect("B: the hero's own serial is not among the lower-list items",
                    view.DeviceList.ItemsSource is IEnumerable<AdbDevice> others
                    && !others.Any(d => d.Serial == "s1"));
                Expect("B: OthersHeading visible when other devices remain",
                    view.OthersHeading.Visibility == Visibility.Visible);
                Unload(view);
            }

            // ---- C: an unusually long device identity does not blow out the hero's layout ----
            {
                var view = new DashboardView();
                Load(view);
                var longName = new string('غ', 200);
                var ex = Raise(new List<AdbDevice>
                {
                    new() { Serial = "s-long-" + new string('9', 120), State = "device", Model = longName },
                });
                Expect("C: no exception rendering a long device identity", ex == null);

                Settle(view);
                Expect("C: hero panel stays within a sane bounded width after layout",
                    view.HeroLive.ActualWidth > 0 && view.HeroLive.ActualWidth < 2000);
                Expect("C: the device-name TextBlock did not expand past the hero's own width",
                    view.HeroDeviceName.ActualWidth <= view.HeroLive.ActualWidth);
                Unload(view);
            }

            // ---- D: primary interactive controls expose a meaningful accessible name ----
            {
                var view = new DashboardView();
                Load(view);
                Raise(new List<AdbDevice> { new() { Serial = "s1", State = "device", Model = "Pixel 8" } });
                Settle(view);

                Expect("D: RefreshBtn has a non-empty AutomationProperties.Name",
                    !string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.RefreshBtn)));
                Expect("D: HeroRevealBtn has a non-empty AutomationProperties.Name",
                    !string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.HeroRevealBtn)));
                Expect("D: HeroStartBtn has a non-empty AutomationProperties.Name",
                    !string.IsNullOrWhiteSpace(AutomationProperties.GetName(view.HeroStartBtn)));
                Expect("D: HeroRevealBtn keeps the approved 30x30 icon-button target size",
                    Math.Abs(view.HeroRevealBtn.ActualWidth - 30) < 0.5 &&
                    Math.Abs(view.HeroRevealBtn.ActualHeight - 30) < 0.5);
                Expect("D: HeroStartBtn meets the strengthened 44-48 DIP primary-action target height",
                    view.HeroStartBtn.ActualHeight >= 44 && view.HeroStartBtn.ActualHeight <= 48);
                Unload(view);
            }

            // ---- E: this batch introduces no additional DeviceWatcher subscription ----
            {
                var baseline = DeviceWatcherSubscriberCount();
                var view = new DashboardView();
                Load(view);
                Expect("E: exactly one DeviceWatcher subscriber after Loaded",
                    DeviceWatcherSubscriberCount() == baseline + 1);
                Load(view);   // duplicate Loaded must not duplicate the subscription
                Expect("E: a duplicate Loaded does not duplicate the DeviceWatcher subscription",
                    DeviceWatcherSubscriberCount() == baseline + 1);
                Unload(view);
                Expect("E: back to baseline after Unloaded", DeviceWatcherSubscriberCount() == baseline);
            }

            // ---- F: mirror/live-state repair (ScrcpyService.SessionsChanged repaints only the
            // hero's live/offline state) combined with the M2 reveal-persistence repair — reveal
            // must survive every routine repaint of the SAME device identity, and reset only when
            // that identity actually changes.
            {
                var baselineSessions = ScrcpyServiceSubscriberCount();
                var view = new DashboardView();
                Load(view);
                Expect("F: exactly one SessionsChanged subscriber after Loaded",
                    ScrcpyServiceSubscriberCount() == baselineSessions + 1);
                Load(view);   // duplicate Loaded must not duplicate the subscription
                Expect("F: a duplicate Loaded does not duplicate the SessionsChanged subscription",
                    ScrcpyServiceSubscriberCount() == baselineSessions + 1);

                const string serial = "s-live-1";
                var deviceA = new AdbDevice { Serial = serial, State = "device", Model = "Pixel 8" };
                Raise(new List<AdbDevice> { deviceA });
                Settle(view);

                view.HeroRevealBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("F1: revealing the serial shows the raw value", view.HeroSerial.Text == serial);

                // (2) routine same-device-object refresh — reveal must survive.
                Raise(new List<AdbDevice> { deviceA });
                Settle(view);
                Expect("F2: reveal survives a routine same-object refresh", view.HeroSerial.Text == serial);

                // (4) a NEW AdbDevice instance carrying the same serial — e.g. metadata
                // enrichment or a fresh DeviceWatcher poll producing a distinct object for the
                // same physical device. Identity is judged by serial, not reference.
                var deviceA2 = new AdbDevice
                { Serial = serial, State = "device", Model = "Pixel 8", Battery = 55 };
                Raise(new List<AdbDevice> { deviceA2 });
                Settle(view);
                Expect("F3: reveal survives a same-serial replacement AdbDevice instance",
                    view.HeroSerial.Text == serial);

                try
                {
                    // ResetForTests also clears _shuttingDown: an earlier self-test section in
                    // this same PLP_SELFTEST run constructs and closes a real MainWindow, whose
                    // Closing handler calls AppLifecycle.Shutdown() -> ScrcpyService.BeginShutdown()
                    // — production behavior, correctly triggered — which would otherwise poison
                    // every later Launch() call in this process with MirrorLaunchStatus.ShuttingDown.
                    ScrcpyService.ResetForTests();
                    ScrcpyService.EngineFileCheck = () => Array.Empty<string>();
                    ScrcpyService.IntegrityCheck = () => true;
                    ScrcpyService.StartupWindow = TimeSpan.FromMilliseconds(1);
                    ScrcpyService.Launcher = new NeverExitingLauncher();

                    var launch = ScrcpyService.Launch(new ScrcpyOptions { Serial = serial });
                    Expect("F: the fake session launch started", launch.Started);

                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    while (ScrcpyService.ActiveCount == 0 && DateTime.UtcNow < deadline)
                    {
                        Pump();
                        System.Threading.Thread.Sleep(5);
                    }
                    Pump();

                    Expect("F4: starting a mirror session repaints the hero to the live state",
                        view.HeroStateText.Text == "يتم بث الشاشة الآن");
                    Expect("F5: the mirror-start repaint does not reset the serial-reveal state",
                        view.HeroSerial.Text == serial);

                    ScrcpyService.Stop(serial);
                    var stopDeadline = DateTime.UtcNow.AddSeconds(2);
                    while (ScrcpyService.ActiveCount > 0 && DateTime.UtcNow < stopDeadline)
                    {
                        Pump();
                        System.Threading.Thread.Sleep(5);
                    }
                    Pump();

                    Expect("F6: stopping the mirror session repaints the hero away from the live state",
                        view.HeroStateText.Text != "يتم بث الشاشة الآن");
                    Expect("F7: the mirror-stop repaint does not reset the serial-reveal state",
                        view.HeroSerial.Text == serial);

                    var textBeforeUnload = view.HeroStateText.Text;

                    Unload(view);
                    ScrcpyService.Launch(new ScrcpyOptions { Serial = serial });
                    Pump();
                    Expect("F: an unloaded view is not repainted by a later session change",
                        view.HeroStateText.Text == textBeforeUnload);
                }
                finally
                {
                    ScrcpyService.ResetForTests();
                }

                Expect("F: SessionsChanged subscribers return to baseline after Unloaded",
                    ScrcpyServiceSubscriberCount() == baselineSessions);
            }

            // ---- F2: reveal resets only on an actual identity change — a different device
            // becoming hero masks it again; the hero disappearing entirely (and a later device
            // reappearing) never resurrects a stale reveal (M2 repair).
            {
                var view = new DashboardView();
                Load(view);

                const string serial1 = "s-identity-1";
                const string serial2 = "s-identity-2";
                Raise(new List<AdbDevice> { new() { Serial = serial1, State = "device", Model = "Pixel 8" } });
                Settle(view);
                view.HeroRevealBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("F2a: revealing the first hero's serial works", view.HeroSerial.Text == serial1);

                // (11) a different device becomes hero -> (12) reveal returns to masked.
                Raise(new List<AdbDevice> { new() { Serial = serial2, State = "device", Model = "Pixel 7" } });
                Settle(view);
                Expect("F2b: a different hero serial resets the reveal toggle to masked",
                    view.HeroSerial.Text == ShellLayout.MaskSerial(serial2));

                view.HeroRevealBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("F2c: re-revealing the new hero's serial works", view.HeroSerial.Text == serial2);

                // (13) hero removed entirely -> (14) reveal state is cleared, so a later,
                // unrelated hero never inherits it.
                Raise(new List<AdbDevice>());
                Settle(view);
                Expect("F2d: an empty snapshot shows the empty hero variant",
                    view.HeroEmpty.Visibility == Visibility.Visible);

                Raise(new List<AdbDevice> { new() { Serial = serial2, State = "device", Model = "Pixel 7" } });
                Settle(view);
                Expect("F2e: reveal is cleared once the hero was removed, even for a serial seen before",
                    view.HeroSerial.Text == ShellLayout.MaskSerial(serial2));

                Unload(view);
            }

            // ---- G: other-device rows never render as "selected" (M1 repair — the hero above
            // is the only current/selected device presentation; ApplyOtherDevicesList always
            // excludes the hero's own serial, so no lower row could ever legitimately claim to be
            // "selected"). Every row gets the same subtle default border, real and undisguised by
            // any local BorderBrush value; IsMouseOver hover still resolves to the stronger
            // border via the Style trigger that remains.
            {
                var view = new DashboardView();
                Load(view);
                Raise(new List<AdbDevice>
                {
                    new() { Serial = "hero-1", State = "device", Model = "Pixel 8" },
                    new() { Serial = "row-a", State = "device", Model = "Pixel 7" },
                    new() { Serial = "row-b", State = "unauthorized" },
                });
                Settle(view);
                Pump();

                var rowA = FindRowBorder(view, 0);
                var rowB = FindRowBorder(view, 1);
                Expect("G: both row containers are realized", rowA != null && rowB != null);

                if (rowA != null && rowB != null)
                {
                    var subtle = ((SolidColorBrush)view.FindResource("Brush.Border.Subtle")).Color;
                    var rowAColor = (rowA.BorderBrush as SolidColorBrush)?.Color;
                    var rowBColor = (rowB.BorderBrush as SolidColorBrush)?.Color;

                    Expect("G: every other-device row uses the default subtle BorderBrush " +
                        "(no row can ever be the dead 'selected' state)", rowAColor == subtle && rowBColor == subtle);
                    Expect("G: no local BorderBrush value masks the row's style triggers",
                        rowA.ReadLocalValue(Border.BorderBrushProperty) == DependencyProperty.UnsetValue &&
                        rowB.ReadLocalValue(Border.BorderBrushProperty) == DependencyProperty.UnsetValue);

                    var strong = ((SolidColorBrush)view.FindResource("Brush.Border.Strong")).Color;
                    ForceMouseOver(rowA, true);
                    Expect("G: IsMouseOver repaints a row to the stronger hover BorderBrush",
                        (rowA.BorderBrush as SolidColorBrush)?.Color == strong);
                    Expect("G: an un-hovered row is unaffected by another row's hover state",
                        (rowB.BorderBrush as SolidColorBrush)?.Color == subtle);
                    ForceMouseOver(rowA, false);
                    Expect("G: clearing IsMouseOver returns the row to the default subtle BorderBrush",
                        (rowA.BorderBrush as SolidColorBrush)?.Color == subtle);
                }
                Unload(view);
            }

            // ---- H: single authorized device — hero visible, no duplicated row, start enabled ----
            {
                var view = new DashboardView();
                Load(view);
                Raise(new List<AdbDevice> { new() { Serial = "solo-1", State = "device", Model = "Pixel 8" } });
                Settle(view);

                Expect("H: hero visible for the sole device", view.HeroLive.Visibility == Visibility.Visible);
                Expect("H: lower list holds no rows (the sole device is not duplicated)",
                    view.DeviceList.Items.Count == 0);
                Expect("H: lower-list heading hidden for the sole device",
                    view.OthersHeading.Visibility == Visibility.Collapsed);
                Expect("H: HeroStartBtn is enabled for an authorized device", view.HeroStartBtn.IsEnabled);
                Unload(view);
            }

            // ---- I: unauthorized device — M4 repair: truthful name, no duplicated serial ----
            {
                var view = new DashboardView();
                Load(view);
                const string serial = "R7BX22CD4ZE9";
                Raise(new List<AdbDevice> { new() { Serial = serial, State = "unauthorized" } });
                Settle(view);

                Expect("I: HeroUnauthorizedHint visible", view.HeroUnauthorizedHint.Visibility == Visibility.Visible);
                Expect("I: HeroStartBtn disabled for an unauthorized device", !view.HeroStartBtn.IsEnabled);

                var masked = ShellLayout.MaskSerial(serial);
                Expect("I: the hero device-name line does not repeat the (masked or raw) serial",
                    view.HeroDeviceName.Text != masked && view.HeroDeviceName.Text != serial);
                Expect("I: the hero serial line shows the masked serial exactly once",
                    view.HeroSerial.Text == masked);
                Expect("I: a non-current fallback device is not labelled \"الجهاز الحالي\"",
                    view.HeroDeviceLabel.Text != "الجهاز الحالي");
                Unload(view);
            }

            // ---- J: M3 repair — device-list rows render the masked serial, never the raw one ----
            {
                var view = new DashboardView();
                Load(view);
                const string rawSerial = "R9ZZ00112233AB";
                Raise(new List<AdbDevice>
                {
                    new() { Serial = "hero-x", State = "device", Model = "Pixel 8" },
                    new() { Serial = rawSerial, State = "unauthorized" },
                });
                Settle(view);
                Pump();

                var row = FindRowBorder(view, 0);
                Expect("J: the other device's row container is realized", row != null);
                if (row != null)
                {
                    var rowText = GetRenderedText(row);
                    var masked = ShellLayout.MaskSerial(rawSerial);
                    Expect("J: the masked serial is present in the rendered row", rowText.Contains(masked));
                    Expect("J: the raw fixture serial is absent from the rendered row",
                        !rowText.Contains(rawSerial));
                }
                Unload(view);
            }

            // ---- K: M4 repair — behavioral geometry proof that the other-device row's identity
            // stack is physically anchored beside the device icon, not beside the action button,
            // under this Dashboard's inherited FlowDirection="RightToLeft". WPF's RTL mirror is
            // a RENDER-time effect (it only takes hold once a visual is connected to a real
            // PresentationSource/compositor) — every other scenario in this file measures a
            // bare, windowless DashboardView, which is enough for exceptions/bindings/automation
            // properties but is NOT enough here: TransformToVisual on an unrendered tree reports
            // pre-mirror, arrange-time coordinates that silently disagree with what a screenshot
            // actually shows. So this scenario hosts the view in a real (offscreen) Window, the
            // same way DashboardSnapshotToolMode does, and waits for an actual CompositionTarget
            // render pass before measuring — only then do the icon/identity/action bounds
            // reflect the true, physical, on-screen layout rather than the XAML literal alone.
            {
                var window = new Window
                {
                    Width = 1200,
                    Height = 820,
                    Left = -6000,
                    Top = 100,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };
                var view = new DashboardView();
                window.Content = view;

                try
                {
                    window.Show();
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    SettleWindow(window);
                    Raise(new List<AdbDevice>
                    {
                        new() { Serial = "geo-hero", State = "device", Model = "Pixel 8" },
                        new() { Serial = "geo-row-a", State = "device", Model = "Pixel 7" },
                    });
                    SettleWindow(window);
                    WaitForRealRender(window);

                    var container = view.DeviceList.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
                    Expect("K: the row container is realized", container != null);
                    if (container == null) throw new InvalidOperationException("K: row container was not realized");

                    var rowBorder = FindVisualChild<Border>(container);
                    Expect("K: the row's own root Border is realized", rowBorder != null);
                    if (rowBorder == null) throw new InvalidOperationException("K: row root Border was not realized");

                    var grid = rowBorder.Child as Grid;
                    Expect("K: the row's content Grid is realized", grid != null);
                    if (grid == null) throw new InvalidOperationException("K: row content Grid was not realized");

                    Border? iconBorder = null;
                    StackPanel? identityPanel = null;
                    Button? actionButton = null;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(grid); i++)
                    {
                        switch (VisualTreeHelper.GetChild(grid, i))
                        {
                            case Border b when Grid.GetColumn(b) == 0: iconBorder = b; break;
                            case StackPanel sp when Grid.GetColumn(sp) == 1: identityPanel = sp; break;
                            case Button btn when Grid.GetColumn(btn) == 2: actionButton = btn; break;
                        }
                    }

                    Expect("K: the device-icon Border (Grid.Column=0) is realized", iconBorder != null);
                    Expect("K: the identity StackPanel (Grid.Column=1) is realized", identityPanel != null);
                    Expect("K: the action Button (Grid.Column=2) is realized", actionButton != null);
                    if (iconBorder == null || identityPanel == null || actionButton == null)
                        throw new InvalidOperationException("K: one of icon/identity/action row elements was not realized");

                    // Measured relative to the top-level Window: with a real render pass behind
                    // it, this now reflects true physical (on-screen, left-to-right) coordinates.
                    Rect BoundsIn(FrameworkElement element) =>
                        element.TransformToVisual(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

                    var iconBounds = BoundsIn(iconBorder);
                    var identityBounds = BoundsIn(identityPanel);
                    var actionBounds = BoundsIn(actionButton);

                    var identityToIconGap = iconBounds.Left - identityBounds.Right;
                    var actionToIdentityGap = identityBounds.Left - actionBounds.Right;

                    Expect("K1: the identity stack's centre sits physically to the left of the " +
                        "icon's centre (the icon is the rightmost element under RTL)",
                        identityBounds.Left + identityBounds.Width / 2 < iconBounds.Left + iconBounds.Width / 2);

                    Expect("K2: the identity stack's right edge sits close to the icon's left edge " +
                        "— a compact, bounded gap, not stranded hundreds of DIP away (the wrong " +
                        "pre-repair layout produced a gap far outside this bound)",
                        identityToIconGap >= 0 && identityToIconGap <= 48);

                    Expect("K3: the identity stack sits far from the action button rather than " +
                        "adjacent to it (the wrong pre-repair layout stranded it right beside the " +
                        "button instead of beside the icon)",
                        actionToIdentityGap > 100);

                    lines.AppendLine($"  K geometry (DIP, physical/on-screen): icon=[{iconBounds.Left:F1}," +
                        $"{iconBounds.Right:F1}] identity=[{identityBounds.Left:F1},{identityBounds.Right:F1}] " +
                        $"action=[{actionBounds.Left:F1},{actionBounds.Right:F1}] " +
                        $"identity-icon gap={identityToIconGap:F1} action-identity gap={actionToIdentityGap:F1}");

                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                }
                finally
                {
                    try { window.Close(); } catch { /* teardown is best effort */ }
                }
            }

            Expect("2: zero WPF binding errors across every scenario above (B1 repair)",
                bindingErrors.Count == 0);
            if (bindingErrors.Count > 0)
                lines.AppendLine("  binding errors: " + string.Join(" | ", bindingErrors.Take(5)));

            lines.AppendLine("  scenarios: empty, multi-device+hero-exclusion, long-identity, " +
                             "automation-names, device-watcher-subscription, mirror-live-state+reveal-persistence, " +
                             "reveal-identity-change, other-device-row-borders, single-device, " +
                             "unauthorized-fallback, masked-list-serial, physical-rtl-row-geometry — " +
                             "see failures (if any) below");
        }
        catch (Exception ex)
        {
            failures.Add($"unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            PresentationTraceSources.Refresh();

            DashboardView.SuppressRefreshForHarness = wasSuppressed;
            DashboardView.EnrichOverrideForTests = wasEnrich;
            AppState.SetCurrent(appStateBefore);
            DeviceWatcher.ResetForTests();
            ScrcpyService.ResetForTests();
        }

        var passed = failures.Count == 0;
        var report = $"dashboard-visual: {total - failures.Count}/{total} visual assertions passed" +
                     Environment.NewLine + lines.ToString().TrimEnd() +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    private static Exception? Raise(List<AdbDevice> devices)
    {
        try
        {
            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
            Pump();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            DeviceWatcher.Stop();
        }
    }

    private static int DeviceWatcherSubscriberCount()
    {
        var field = typeof(DeviceWatcher).GetField("DevicesChanged", BindingFlags.NonPublic | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static int ScrcpyServiceSubscriberCount()
    {
        var field = typeof(ScrcpyService).GetField("SessionsChanged",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void Load(DashboardView view)
        => view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

    private static void Unload(DashboardView view)
        => view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

    private static void Settle(DashboardView view)
    {
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        view.UpdateLayout();
    }

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>Same purpose as <see cref="Settle(DashboardView)"/> but for scenario K's
    /// window-hosted view — measures/arranges the actual Window, not just the bare UserControl.</summary>
    private static void SettleWindow(Window window)
    {
        window.UpdateLayout();
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }

    /// <summary>Blocks (via a re-entrant Dispatcher frame, not a real await) until an actual
    /// CompositionTarget render pass has completed for <paramref name="window"/> — required
    /// because WPF's FlowDirection mirror is a render-time effect that a bare Measure/Arrange
    /// pass never triggers, and scenario K's geometry proof is meaningless without it.</summary>
    private static void WaitForRealRender(Window window)
    {
        var rendered = false;
        EventHandler? onRendering = null;
        onRendering = (_, _) => { rendered = true; CompositionTarget.Rendering -= onRendering; };
        CompositionTarget.Rendering += onRendering;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!rendered && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        if (!rendered) CompositionTarget.Rendering -= onRendering;
    }

    /// <summary>The outer row <see cref="Border"/> for a realized DeviceList item — the first
    /// Border found in visual-tree pre-order under its container, which is exactly the
    /// DataTemplate's own root element.</summary>
    private static Border? FindRowBorder(DashboardView view, int index)
    {
        var container = view.DeviceList.ItemContainerGenerator.ContainerFromIndex(index);
        return container == null ? null : FindVisualChild<Border>(container);
    }

    /// <summary>
    /// Forces a real UIElement.IsMouseOver value without an actual OS mouse or a rendered
    /// window (this harness's DashboardView instances are never attached to a HWND, so real hit
    /// testing is unavailable) — sets the property through its own internal
    /// DependencyPropertyKey exactly like the real MouseDevice does, which still fires normal
    /// property-changed notifications and re-evaluates every IsMouseOver Style trigger. This is
    /// the honest replacement for the old SetValue(SelectedSerialProperty, ...) technique the
    /// dead selected-row proof used to rely on (M1 repair).
    /// </summary>
    private static void ForceMouseOver(UIElement element, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement)
            .GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        element.SetValue(key, value);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>Concatenates every TextBlock.Text under a visual subtree, for asserting on what
    /// a row actually renders rather than what its source bindings merely reference.</summary>
    private static string GetRenderedText(DependencyObject root)
    {
        var sb = new StringBuilder();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock tb) sb.Append(tb.Text).Append(' ');
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return sb.ToString();
    }

    /// <summary>Collects WPF data-binding diagnostic lines (errors only, at the configured
    /// TraceSource level) instead of letting them go to Debug output unreviewed.</summary>
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

    /// <summary>A process handle that never exits on its own (Batch 4A scenario F) — lets
    /// ScrcpyService.Launch survive its startup window into a real "Running" session, entirely
    /// in memory, so SessionsChanged can be exercised without starting anything.</summary>
    private sealed class NeverExitingHandle : IProcessHandle
    {
        public int Id => -1;
        public bool HasExited => false;
        public int? TryGetExitCode() => null;
        public event EventHandler? Exited { add { } remove { } }
        public void BeginWatching() { }
        public string Diagnostics => "";
        public void Kill(bool entireTree) { }
        public void Dispose() { }
    }

    private sealed class NeverExitingLauncher : IProcessLauncher
    {
        public IProcessHandle Start(ProcessCommand command) => new NeverExitingHandle();
    }
}
