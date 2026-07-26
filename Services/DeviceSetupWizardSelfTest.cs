using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Windows;

namespace ScrcpyDeck.Services;

/// <summary>
/// Runtime proof for the Batch 4B device-setup wizard, following the same pattern as
/// <see cref="DashboardVisualSelfTest"/> and for the same reason: xUnit runs MTA and cannot
/// construct a real WPF window, so behavioral coverage that needs an actual visual tree lives
/// in the PLP_SELFTEST harness instead. Device fixtures are passed straight into the wizard's
/// constructor or pushed through <see cref="DeviceWatcher"/>'s existing test seam
/// (<see cref="DeviceWatcher.DiscoveryProvider"/>) — never real adb.
/// </summary>
internal static class DeviceSetupWizardSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    internal static Result Run()
    {
        var failures = new List<string>();
        var total = 0;
        void Expect(string name, bool condition)
        {
            total++;
            if (!condition) failures.Add(name);
        }

        var appStateBefore = AppState.CurrentDevice;

        // Section 16: captured for the WHOLE run (every scenario below), so a missing resource,
        // bad binding, converter exception or RelativeSource failure anywhere in the wizard is
        // caught structurally rather than relying on a scenario happening to assert on it.
        var bindingErrors = new List<string>();
        var listener = new BindingErrorListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.Refresh();

        try
        {
            // ---- A: no device ----
            {
                var w = Make(new List<AdbDevice>());
                Expect("A: stage is NoDevice", w.StageForTests == DeviceSetupStage.NoDevice);
                Expect("A: Finish is disabled with no device", !w.FinishBtn.IsEnabled);
                Expect("A: a truthful status message is shown", !string.IsNullOrWhiteSpace(w.StatusText.Text));
                Expect("A: multi-device panel hidden for zero devices", w.MultiDevicePanel.Visibility == Visibility.Collapsed);
                Expect("A: no step is confirmed with no device", Enumerable.Range(0, 5).All(i => !w.StepConfirmedForTests(i)));
                Expect("A: step 0 (connect the phone) is the current instruction", w.StepIndexForTests == 0);
                CloseWindow(w);
            }

            // ---- B: unauthorized device ----
            {
                const string serial = "R7BX22CD4ZE9";
                var dev = new AdbDevice { Serial = serial, State = "unauthorized" };
                var w = Make(new List<AdbDevice> { dev });
                Expect("B: stage is Unauthorized", w.StageForTests == DeviceSetupStage.Unauthorized);
                Expect("B: initial navigation opens at the authorize step", w.StepIndexForTests == 3);
                Expect("B: Finish stays disabled while unauthorized", !w.FinishBtn.IsEnabled);
                Expect("B: status text never shows the raw serial", !w.StatusText.Text.Contains(serial));
                // M2: adb reporting "unauthorized" already proves USB debugging is functioning
                // enough to attempt the handshake — steps 0-2 are confirmed, computer
                // authorization (step 3) is not.
                Expect("B: step 0 (device observed) is confirmed", w.StepConfirmedForTests(0));
                Expect("B: step 1 (developer options) is confirmed", w.StepConfirmedForTests(1));
                Expect("B: step 2 (USB debugging) is confirmed", w.StepConfirmedForTests(2));
                Expect("B: step 3 (computer authorization) is NOT confirmed while unauthorized", !w.StepConfirmedForTests(3));
                CloseWindow(w);
            }

            // ---- C: offline / not-ready device — never presented as authorized ----
            {
                var dev = new AdbDevice { Serial = "R9OFFLINE0011", State = "offline", Model = "Tab" };
                var w = Make(new List<AdbDevice> { dev });
                Expect("C: stage is Offline", w.StageForTests == DeviceSetupStage.Offline);
                Expect("C: Finish stays disabled while offline", !w.FinishBtn.IsEnabled);
                Expect("C: status text does not claim success",
                    !w.StatusText.Text.Contains("جاهز للبدء") && !w.StatusText.Text.Contains("مصرّح له"));
                // M2 repair: Offline (and every other unrecognized/bootloader/recovery/sideload
                // state DeviceSetupEvaluator folds into it) may confirm ONLY that a device was
                // observed — never USB debugging or computer authorization.
                Expect("C: step 0 (device observed) IS confirmed while offline", w.StepConfirmedForTests(0));
                Expect("C: step 1 (developer options) is NOT confirmed while offline", !w.StepConfirmedForTests(1));
                Expect("C: step 2 (USB debugging) is NOT confirmed while offline", !w.StepConfirmedForTests(2));
                Expect("C: step 3 (computer authorization) is NOT confirmed while offline", !w.StepConfirmedForTests(3));
                Expect("C: initial navigation opens on the developer-options step, not \"connect your phone\"",
                    w.StepIndexForTests == 1);
                CloseWindow(w);
            }

            // ---- D: authorized/ready device ----
            {
                const string serial = "R5CX10AB9XD";
                var dev = new AdbDevice { Serial = serial, State = "device", Model = "Galaxy S24", Brand = "Samsung" };
                var w = Make(new List<AdbDevice> { dev });
                Expect("D: stage is Ready", w.StageForTests == DeviceSetupStage.Ready);
                Expect("D: initial navigation opens at the verify step", w.StepIndexForTests == 4);
                Expect("D: Finish is enabled for a ready device", w.FinishBtn.IsEnabled);
                Expect("D: the ready card shows the masked serial",
                    w.ReadySerial.Text == ShellLayout.MaskSerial(serial));
                Expect("D: the ready card never shows the raw serial",
                    !w.ReadySerial.Text.Contains(serial));
                Expect("D: the ready card shows the real model, not a fabricated one",
                    w.ReadyDeviceName.Text.Contains("Galaxy S24"));
                Expect("D: steps 0-3 are all confirmed when ready",
                    w.StepConfirmedForTests(0) && w.StepConfirmedForTests(1)
                    && w.StepConfirmedForTests(2) && w.StepConfirmedForTests(3));
                // M2/minor repair: the verify/finish step is the action itself — it must never
                // read "تم" while the wizard is still open, only "الحالي".
                Expect("D: step 4 (verify/finish) is never confirmed before Finish is clicked",
                    !w.StepConfirmedForTests(4));
                Expect("D: step 4's word is \"الحالي\", never \"تم\", before Finish",
                    w.StepWordsForTests[4] == "الحالي");
                CloseWindow(w);
            }

            // ---- E: multiple devices — visible selector, no silent choice, masked rows ----
            {
                const string s1 = "s-multi-hero-1", s2 = "s-multi-other-2";
                var d1 = new AdbDevice { Serial = s1, State = "device", Model = "Pixel 8" };
                var d2 = new AdbDevice { Serial = s2, State = "unauthorized" };
                var w = Make(new List<AdbDevice> { d1, d2 });

                Expect("E: multi-device panel is shown for 2 devices", w.MultiDevicePanel.Visibility == Visibility.Visible);
                Expect("E: exactly 2 selector rows", w.DeviceSelectorList.Children.Count == 2);
                Expect("E: the authorized device is preferred by default", w.StageForTests == DeviceSetupStage.Ready);

                foreach (var child in w.DeviceSelectorList.Children)
                {
                    var radio = child as RadioButton;
                    Expect("E: every selector row is a RadioButton", radio != null);
                    if (radio == null) continue;

                    // M1 repair: Content is now a structured row (TextBlocks), not a plain
                    // string — render its full text to check for a leaked raw serial.
                    var rendered = GetRenderedText(radio);
                    Expect("E: a selector row never shows a raw serial",
                        !rendered.Contains(s1) && !rendered.Contains(s2));
                    var name = System.Windows.Automation.AutomationProperties.GetName(radio);
                    Expect("E: a selector row's accessible name never shows a raw serial",
                        !name.Contains(s1) && !name.Contains(s2));
                }

                var other = (RadioButton)w.DeviceSelectorList.Children[1];
                other.IsChecked = true;   // explicit user selection, not a silent choice
                Expect("E: selecting a different device updates the evaluated stage",
                    w.StageForTests == DeviceSetupStage.Unauthorized);
                CloseWindow(w);
            }

            // ---- F: back/next carousel transitions ----
            {
                var w = Make(new List<AdbDevice>());
                Expect("F: Back disabled at the first step", !w.BackBtn.IsEnabled);
                Expect("F: Next enabled at the first step", w.NextBtn.IsEnabled);

                Click(w.NextBtn);
                Expect("F: Next advances one step", w.StepIndexForTests == 1);
                Expect("F: Back becomes enabled after advancing", w.BackBtn.IsEnabled);

                for (var i = 0; i < 5; i++) Click(w.NextBtn);
                Expect("F: Next never advances past the last step", w.StepIndexForTests == 4);
                Expect("F: Next disabled at the last step", !w.NextBtn.IsEnabled);

                Click(w.BackBtn);
                Expect("F: Back moves one step back", w.StepIndexForTests == 3);
                CloseWindow(w);
            }

            // ---- G: Finish produces a truthful Result only when truly ready (M4) ----
            {
                const string serial = "s-finish-ready-1";
                var dev = new AdbDevice { Serial = serial, State = "device", Model = "Pixel 8" };
                var w = Make(new List<AdbDevice> { dev });
                Expect("G: Finish is enabled before clicking", w.FinishBtn.IsEnabled);
                Expect("G: no Result exists before Finish is clicked", w.Result == null);
                Click(w.FinishBtn);
                Expect("G: Finish produces a Result naming the ready device",
                    w.Result?.SelectedDevice.Serial == serial);
                Expect("G: Finish no longer sets AppState itself — that is now the caller's job",
                    AppState.CurrentSerial != serial);
            }

            // ---- H: manufacturer guide — manual only, never fabricated from a known brand ----
            {
                var dev = new AdbDevice { Serial = "s-brand-1", State = "device", Model = "Galaxy S24", Brand = "Samsung" };
                var w = Make(new List<AdbDevice> { dev });
                Expect("H: the manufacturer selector defaults to the generic guide even though " +
                    "this device's real brand (Samsung) is already known",
                    w.ManufacturerCombo.SelectedIndex == ManufacturerGuides.All.Count - 1);

                var before = BulletTexts(w.DevOptionsBullets);
                Expect("H: default instructions match the generic guide",
                    before.SequenceEqual(ManufacturerGuides.Default.DeveloperOptions.Bullets.Select(b => "•  " + b)));

                w.ManufacturerCombo.SelectedIndex = 0;   // user explicitly picks the first guide
                var after = BulletTexts(w.DevOptionsBullets);
                Expect("H: selecting a manufacturer changes the instructional content",
                    !after.SequenceEqual(before));
                Expect("H: the selected guide's own text is shown verbatim",
                    after.SequenceEqual(ManufacturerGuides.All[0].DeveloperOptions.Bullets.Select(b => "•  " + b)));
                CloseWindow(w);
            }

            // ---- I: DeviceWatcher subscription lifecycle ----
            {
                var baseline = DeviceWatcherSubscriberCount();
                var w = Make(new List<AdbDevice>());
                Expect("I: exactly one subscriber after Loaded", DeviceWatcherSubscriberCount() == baseline + 1);

                w.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));   // duplicate Loaded
                Expect("I: a duplicate Loaded does not duplicate the subscription",
                    DeviceWatcherSubscriberCount() == baseline + 1);

                var stageBeforeClose = w.StageForTests;
                CloseWindow(w);
                Expect("I: subscriber count returns to baseline after Close",
                    DeviceWatcherSubscriberCount() == baseline);

                // A closed wizard must ignore a DevicesChanged event that arrives afterward.
                Raise(new List<AdbDevice> { new() { Serial = "ghost-after-close", State = "device" } });
                Expect("I: a closed wizard's stage is never mutated by a later DevicesChanged event",
                    w.StageForTests == stageBeforeClose);
            }

            // ---- J: local ADB tools unavailable — fails closed, never blames the phone (section 7) ----
            {
                var wasOverride = DeviceSetupWizardWindow.AdbAvailableOverrideForTests;
                DeviceSetupWizardWindow.AdbAvailableOverrideForTests = () => false;
                try
                {
                    var dev = new AdbDevice { Serial = "s-tools-unavailable-1", State = "device" };
                    var w = Make(new List<AdbDevice> { dev });
                    Expect("J: stage is ToolsUnavailable regardless of a real device being present",
                        w.StageForTests == DeviceSetupStage.ToolsUnavailable);
                    Expect("J: no step is confirmed when tools are unavailable",
                        Enumerable.Range(0, 5).All(i => !w.StepConfirmedForTests(i)));
                    Expect("J: Verify is disabled", !w.VerifyBtn.IsEnabled);
                    Expect("J: Next is disabled", !w.NextBtn.IsEnabled);
                    Expect("J: Finish is disabled", !w.FinishBtn.IsEnabled);
                    Expect("J: the recovery/support action remains available",
                        w.SupportLinkBtn.Visibility == Visibility.Visible);
                    Expect("J: the banner explicitly denies blaming USB debugging on the phone",
                        w.StatusText.Text.Contains("ليست مشكلة في تصحيح USB"));
                    Expect("J: the banner blames local program files instead",
                        w.StatusText.Text.Contains("ملفات البرنامج"));
                    CloseWindow(w);
                }
                finally
                {
                    DeviceSetupWizardWindow.AdbAvailableOverrideForTests = wasOverride;
                }
            }

            // ---- K: manual navigation is never overridden by a later render (M3) ----
            {
                var d1 = new AdbDevice { Serial = "s-manual-nav-unauth", State = "unauthorized" };
                var d2 = new AdbDevice { Serial = "s-manual-nav-ready", State = "device", Model = "Pixel 8" };
                var w = Make(new List<AdbDevice> { d1, d2 }, focusSerial: d1.Serial);

                Expect("K1: Unauthorized initially opens at the authorization instruction (step 3)",
                    w.StepIndexForTests == 3);

                Click(w.BackBtn);
                Click(w.BackBtn);
                Expect("K2: two Back clicks land on step 1", w.StepIndexForTests == 1);

                Raise(new List<AdbDevice> { d1, d2 });
                Expect("K3: a replacement DeviceWatcher snapshot does not move the reading position",
                    w.StepIndexForTests == 1);

                var d2Row = w.DeviceSelectorList.Children.OfType<RadioButton>()
                    .FirstOrDefault(r => (string?)r.Tag == d2.Serial);
                Expect("K4: the ready device's selector row is realized", d2Row != null);
                if (d2Row != null)
                {
                    d2Row.IsChecked = true;
                    Expect("K4: selecting the ready device updates the evaluated stage",
                        w.StageForTests == DeviceSetupStage.Ready);
                    Expect("K4: selecting a different device does not move the reading position",
                        w.StepIndexForTests == 1);
                    Expect("K5: Finish stays enabled for the now-focused ready device even while an earlier step is showing",
                        w.FinishBtn.IsEnabled);
                }
                CloseWindow(w);
            }

            // ---- L: Verify uses exactly one injected discovery call; never repaints after Close (section 7) ----
            {
                var wasOverride = DeviceSetupWizardWindow.DiscoverDevicesOverrideForTests;
                var callCount = 0;
                var tcs = new TaskCompletionSource<DeviceDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                DeviceSetupWizardWindow.DiscoverDevicesOverrideForTests = () =>
                {
                    callCount++;
                    return tcs.Task;
                };
                try
                {
                    var dev = new AdbDevice { Serial = "s-verify-1", State = "unauthorized" };
                    var w = Make(new List<AdbDevice> { dev });
                    var stepBeforeVerify = w.StepIndexForTests;

                    Click(w.VerifyBtn);
                    Expect("L1: Verify disables itself while the discovery call is in flight", !w.VerifyBtn.IsEnabled);
                    Expect("L2: exactly one discovery call was made for one click", callCount == 1);

                    // The window closes WHILE the discovery call is still in flight; the eventual
                    // continuation must never repaint a torn-down window.
                    CloseWindow(w);
                    tcs.SetResult(DeviceDiscoveryResult.Success(new List<AdbDevice>
                        { new() { Serial = "s-verify-2", State = "device", Model = "Pixel 9" } }));
                    Pump();

                    Expect("L3: a closed window's stage is never mutated by a Verify continuation that resolves after Close",
                        w.StageForTests == DeviceSetupStage.Unauthorized);
                    Expect("L4: Verify never moves the reading position, even once it resolves",
                        w.StepIndexForTests == stepBeforeVerify);
                }
                finally
                {
                    DeviceSetupWizardWindow.DiscoverDevicesOverrideForTests = wasOverride;
                }
            }

            // ---- M: real-rendered geometry proof (B1 title/close, B2 ComboBox, M1 wireless bidi) ----
            // WPF's RTL mirror and text-shaping are render/layout-time effects; a bare
            // construct-and-close window (as every scenario above uses) is enough for state and
            // exceptions but not for physical geometry, so these scenarios host a real, actually
            // shown (offscreen) window and wait for a genuine render pass first.
            {
                foreach (var width in new[] { 920.0, 980.0, 1080.0 })
                {
                    var w = MakeShown(new List<AdbDevice>(), width);
                    try
                    {
                        Rect BoundsIn(FrameworkElement el) =>
                            el.TransformToVisual(w).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));

                        var titleBounds = BoundsIn(w.TitleText);
                        var closeBounds = BoundsIn(w.CloseBtn);
                        Expect($"M-B1: title and close button do not overlap at width={width}",
                            !titleBounds.IntersectsWith(closeBounds));
                    }
                    finally { CloseWindow(w); }
                }

                // B2: the dark ComboBox style — bring the developer-options step into view first.
                {
                    var w = MakeShown(new List<AdbDevice>(), 980);
                    try
                    {
                        Click(w.NextBtn);
                        SettleWindow(w);
                        WaitForRealRender(w);

                        Expect("M-B2: ManufacturerCombo is actually visible on the developer-options step",
                            w.ManufacturerCombo.Visibility == Visibility.Visible && w.ManufacturerCombo.ActualHeight > 0);

                        var expectedBg = ((SolidColorBrush)w.FindResource("Brush.Surface.Inset")).Color;
                        var expectedFg = ((SolidColorBrush)w.FindResource("Brush.Text.Primary")).Color;
                        Expect("M-B2: ComboBox background is the dark surface brush, not the default light system chrome",
                            w.ManufacturerCombo.Background is SolidColorBrush bg && bg.Color == expectedBg);
                        Expect("M-B2: ComboBox foreground is readable on that dark background",
                            w.ManufacturerCombo.Foreground is SolidColorBrush fg && fg.Color == expectedFg);
                        Expect("M-B2: ComboBox meets the established minimum control height",
                            w.ManufacturerCombo.ActualHeight >= 35.5);

                        var contentSite = FindVisualChild<ContentPresenter>(w.ManufacturerCombo);
                        Expect("M-B2: the selected-item ContentPresenter is realized", contentSite != null);
                        if (contentSite != null)
                        {
                            Expect("M-B2: the selected text's rendered height is neither zero nor clipped by the ComboBox's own bounds",
                                contentSite.ActualHeight > 0 && contentSite.ActualHeight <= w.ManufacturerCombo.ActualHeight);
                        }
                    }
                    finally { CloseWindow(w); }
                }

                // M1: the exact wireless fixture from the review — "192.•••••55" must render in
                // logical LTR order, never bidi-reversed to "55•••••.192".
                {
                    const string wirelessSerial = "192.168.1.42:5555";
                    var authorized = new AdbDevice { Serial = "s-wireless-hero", State = "device", Model = "Pixel 8" };
                    var wireless = new AdbDevice { Serial = wirelessSerial, State = "unauthorized" };
                    var w = MakeShown(new List<AdbDevice> { authorized, wireless }, 980);
                    try
                    {
                        var masked = ShellLayout.MaskSerial(wirelessSerial);
                        Expect("M-M1: the fixture reproduces the reviewed shape (digits/dots masked)",
                            masked == "192.•••••55");

                        var row = w.DeviceSelectorList.Children.OfType<RadioButton>()
                            .FirstOrDefault(r => (string?)r.Tag == wirelessSerial);
                        Expect("M-M1: the wireless device's selector row is realized", row != null);
                        if (row != null)
                        {
                            var maskedBlock = FindVisualChild<TextBlock>(row);
                            Expect("M-M1: the masked-endpoint TextBlock is realized", maskedBlock != null);
                            if (maskedBlock != null)
                            {
                                Expect("M-M1: the masked endpoint keeps its own explicit LTR FlowDirection",
                                    maskedBlock.FlowDirection == FlowDirection.LeftToRight);

                                var firstChar = CharacterBounds(maskedBlock, 0);
                                var lastChar = CharacterBounds(maskedBlock, masked.Length - 1);
                                Expect("M-M1: the masked endpoint renders in logical LTR order " +
                                    "(the first logical character sits physically to the left of the last)",
                                    firstChar.Left < lastChar.Left);
                            }
                        }
                    }
                    finally { CloseWindow(w); }
                }
            }

            // ---- N: Escape closes only the open ComboBox dropdown, never the wizard behind it ----
            {
                var w = MakeShown(new List<AdbDevice>(), 980);
                try
                {
                    Click(w.NextBtn);
                    SettleWindow(w);
                    w.ManufacturerCombo.IsDropDownOpen = true;

                    SendEscape(w);
                    Expect("N1: Escape closes an open ComboBox dropdown", !w.ManufacturerCombo.IsDropDownOpen);
                    Expect("N1: the wizard itself stays open", w.IsVisible);

                    SendEscape(w);
                    Expect("N2: an ordinary Escape (dropdown already closed) closes the wizard", !w.IsVisible);
                }
                finally { CloseWindow(w); }
            }

            // ---- O: exactly one step ever reports "الحالي", table-driven across every live state ----
            {
                var cases = new (string Name, bool ToolsAvailable, AdbDevice? Device)[]
                {
                    ("tools-unavailable", false, new AdbDevice { Serial = "s-o-1", State = "device" }),
                    ("no-device", true, null),
                    ("unauthorized", true, new AdbDevice { Serial = "s-o-2", State = "unauthorized" }),
                    ("offline", true, new AdbDevice { Serial = "s-o-3", State = "offline" }),
                    ("bootloader", true, new AdbDevice { Serial = "s-o-4", State = "bootloader" }),
                    ("recovery", true, new AdbDevice { Serial = "s-o-5", State = "recovery" }),
                    ("sideload", true, new AdbDevice { Serial = "s-o-6", State = "sideload" }),
                    ("authorizing", true, new AdbDevice { Serial = "s-o-7", State = "authorizing" }),
                    ("malformed-empty-state", true, new AdbDevice { Serial = "s-o-8", State = "" }),
                    ("ready", true, new AdbDevice { Serial = "s-o-9", State = "device" }),
                };

                foreach (var (name, toolsAvailable, device) in cases)
                {
                    var wasOverride = DeviceSetupWizardWindow.AdbAvailableOverrideForTests;
                    DeviceSetupWizardWindow.AdbAvailableOverrideForTests = () => toolsAvailable;
                    try
                    {
                        var devices = device == null ? new List<AdbDevice>() : new List<AdbDevice> { device };
                        var w = Make(devices);
                        var currentCount = w.StepWordsForTests.Count(word => word == "الحالي");
                        Expect($"O: exactly one step is \"الحالي\" in state '{name}'", currentCount == 1);
                        CloseWindow(w);
                    }
                    finally
                    {
                        DeviceSetupWizardWindow.AdbAvailableOverrideForTests = wasOverride;
                    }
                }
            }

            // Requirement 2 ("no real ADB/scrcpy/bridge/engine process starts in tests") is
            // guaranteed structurally, not by process-enumeration here: this repo's own security
            // policy confines process enumeration to AdbServerOwnership.cs alone (see
            // SecurityGuardRemovalTests.ProcessEnumeration_ExistsOnlyInTheAdbOwnershipPolicy), and
            // scenarios J/L above prove Verify/ToolsUnavailable's only device-state entry points
            // through an injected seam, never real adb.

            Expect("section 16: zero WPF binding/resource errors across every scenario above",
                bindingErrors.Count == 0);
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

            AppState.SetCurrent(appStateBefore);
            DeviceWatcher.ResetForTests();
            DeviceSetupWizardWindow.AdbAvailableOverrideForTests = null;
            DeviceSetupWizardWindow.DiscoverDevicesOverrideForTests = null;
        }

        var passed = failures.Count == 0;
        var report = $"setup-wizard: {total - failures.Count}/{total} assertions passed" +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures)) +
                     (bindingErrors.Count > 0
                         ? Environment.NewLine + "  binding errors: " + string.Join(" | ", bindingErrors.Take(5))
                         : "");
        return new Result(passed, total, failures.Count, report);
    }

    // ---------------- helpers ----------------

    private static DeviceSetupWizardWindow Make(List<AdbDevice> devices, string? focusSerial = null)
    {
        var w = new DeviceSetupWizardWindow(devices, focusSerial);
        w.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return w;
    }

    /// <summary>Actually shows the window (offscreen) and waits for a real render pass — needed
    /// for every geometry/style assertion, since RTL mirroring and text shaping are render/layout
    /// time effects that a bare construct-and-close window never triggers.</summary>
    private static DeviceSetupWizardWindow MakeShown(List<AdbDevice> devices, double width, string? focusSerial = null)
    {
        var w = new DeviceSetupWizardWindow(devices, focusSerial)
        {
            Width = width,
            Left = OffscreenLeft,
            Top = OffscreenTop,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        w.Show();
        SettleWindow(w);
        WaitForRealRender(w);
        return w;
    }

    private static void CloseWindow(DeviceSetupWizardWindow w)
    {
        try { w.Close(); } catch { /* teardown is best effort */ }
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>Invokes the Window's own PreviewKeyDown handler with a real Escape KeyEventArgs,
    /// the same way WPF's input system would dispatch one — used instead of a public API because
    /// the wizard deliberately exposes no "send a key" surface of its own.</summary>
    private static void SendEscape(DeviceSetupWizardWindow w)
    {
        var method = typeof(DeviceSetupWizardWindow).GetMethod(
            "Window_PreviewKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var source = PresentationSource.FromVisual(w);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        method.Invoke(w, new object[] { w, args });
    }

    private static List<string> BulletTexts(ItemsControl host)
        => host.Items.OfType<TextBlock>().Select(t => t.Text).ToList();

    /// <summary>Same DiscoveryProvider-seam technique <see cref="DashboardVisualSelfTest"/> uses
    /// to drive a real DeviceWatcher.DevicesChanged event without any adb process.</summary>
    private static void Raise(List<AdbDevice> devices)
    {
        try
        {
            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
            Pump();
        }
        finally
        {
            DeviceWatcher.Stop();
        }
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

    /// <summary>Blocks (via a re-entrant Dispatcher frame) until a real CompositionTarget render
    /// pass has completed — mirrors <c>DashboardVisualSelfTest.WaitForRealRender</c>, needed for
    /// the same reason: FlowDirection mirroring is a render-time effect a Measure/Arrange pass
    /// alone never triggers.</summary>
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

    /// <summary>Concatenates every TextBlock.Text under a visual subtree, for asserting on what a
    /// row actually renders rather than what its source content object merely references.</summary>
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

    /// <summary>The rendered bounding rect (in the TextBlock's own coordinate space) of the
    /// character at logical index <paramref name="charIndex"/> — the standard WPF technique for
    /// proving physical glyph order rather than trusting the source string's own ordering.</summary>
    private static Rect CharacterBounds(TextBlock block, int charIndex)
    {
        var pointer = block.ContentStart.GetPositionAtOffset(charIndex) ?? block.ContentStart;
        return pointer.GetCharacterRect(LogicalDirection.Forward);
    }

    private static int DeviceWatcherSubscriberCount()
    {
        var field = typeof(DeviceWatcher).GetField("DevicesChanged", BindingFlags.NonPublic | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>Same technique <see cref="DashboardVisualSelfTest"/> uses to capture WPF's own
    /// binding/resource/converter trace output as data instead of Debug-output noise.</summary>
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
