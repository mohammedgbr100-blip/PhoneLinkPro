using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Runtime proof for the Batch 4C guided wireless-pairing page, following the same pattern as
/// <see cref="DashboardLifecycleSelfTest"/>/<see cref="DeviceSetupWizardSelfTest"/>: xUnit runs
/// MTA and cannot construct a real WPF control tree, so behavioral coverage that needs an actual
/// visual tree lives in the PLP_SELFTEST harness instead. Every Pair/Connect/discovery call in
/// every scenario below is intercepted through <see cref="WirelessView.PairOverrideForTests"/> /
/// <see cref="WirelessView.ConnectOverrideForTests"/> / <see cref="WirelessView.DiscoverDevicesOverrideForTests"/>
/// — this build's own output directory carries the REAL engine binaries (unlike the xUnit test
/// host), so an un-intercepted call here would start a real PhoneLink.Bridge.exe.
/// </summary>
internal static class WirelessSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    /// <summary>
    /// Independent-review repair: pins the expected assertion count from every scenario BELOW
    /// this pin so a scenario accidentally deleted (or an Expect quietly dropped) fails loudly
    /// here instead of just silently lowering the reported denominator.
    /// </summary>
    private const int ExpectedScenarioAssertions = 102;

    /// <summary>
    /// Supplementary — NOT a claim of tamper-proofness on its own — named-scenario guard: every
    /// letter below must own at least one assertion whose name starts with "{id}: ". Combined
    /// with <see cref="ExpectedScenarioAssertions"/> (a raw count, which a deleted scenario could
    /// coincidentally still hit if enough OTHER assertions were added the same time), this catches
    /// the specific case of one whole named scenario disappearing while the total assertion count
    /// happens to still add up some other way.
    /// </summary>
    private static readonly string[] ScenarioIds =
    {
        "A", "BANNER", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S",
    };

    internal static Result Run()
    {
        var failures = new List<string>();
        var assertionNames = new List<string>();
        var total = 0;
        void Expect(string name, bool condition)
        {
            total++;
            assertionNames.Add(name);
            if (!condition) failures.Add(name);
        }

        var bindingErrors = new List<string>();
        var listener = new BindingErrorListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.Refresh();

        var wasAdbAvailable = WirelessView.AdbAvailableOverrideForTests;
        var wasPair = WirelessView.PairOverrideForTests;
        var wasConnect = WirelessView.ConnectOverrideForTests;
        var wasDiscover = WirelessView.DiscoverDevicesOverrideForTests;

        try
        {
            // ---- A: tools unavailable ----
            {
                WirelessView.AdbAvailableOverrideForTests = () => false;
                var v = Make();
                Expect("A: tools-unavailable banner is visible", v.ToolsUnavailableBanner.Visibility == Visibility.Visible);
                Expect("A: Pair is disabled", !v.PairBtn.IsEnabled);
                Expect("A: Connect is disabled", !v.ConnectBtn.IsEnabled);
                Unload(v);
                WirelessView.AdbAvailableOverrideForTests = () => true;
            }

            // ---- BANNER: M4 — ToolsUnavailable banner wraps instead of clipping at narrow width ----
            // Hosted-geometry proof: a horizontal StackPanel offers its children infinite
            // available width on the stacking axis during measure, so a TextWrapping="Wrap"
            // TextBlock inside one never actually wraps and is clipped when arranged back into
            // the real (bounded) width. This can only be proven with a REAL, bounded layout pass
            // — constructing the view in isolation (as every other scenario here does) never
            // measures/arranges against a finite width at all.
            {
                WirelessView.AdbAvailableOverrideForTests = () => false;
                var v = new WirelessView();
                var window = new Window
                {
                    Width = 920, Height = 500,
                    Left = -6000, Top = 100,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false, ShowActivated = false,
                    Content = v,
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    Pump();
                    window.UpdateLayout();

                    Expect("BANNER: tools-unavailable banner visible at narrow (920 DIP) width",
                        v.ToolsUnavailableBanner.Visibility == Visibility.Visible);
                    Expect("BANNER: the full message is present in the visual tree, not truncated",
                        v.ToolsUnavailableText.Text.Length > 100);

                    var textBounds = v.ToolsUnavailableText
                        .TransformToAncestor(v.ToolsUnavailableBanner)
                        .TransformBounds(new Rect(new Point(0, 0), v.ToolsUnavailableText.RenderSize));
                    var bannerWidth = v.ToolsUnavailableBanner.ActualWidth;

                    Expect("BANNER: text right edge stays within the banner's bounds (no horizontal clipping)",
                        bannerWidth > 0 && textBounds.Right <= bannerWidth + 1.0);
                    Expect("BANNER: text left edge stays within the banner's bounds",
                        textBounds.Left >= -1.0);
                    Expect("BANNER: text wraps onto more than one line (grows vertically instead of clipping)",
                        v.ToolsUnavailableText.ActualHeight > 30);
                    Expect("BANNER: icon and text occupy distinct, non-overlapping Grid columns",
                        System.Windows.Controls.Grid.GetColumn(v.ToolsUnavailableIcon)
                        != System.Windows.Controls.Grid.GetColumn(v.ToolsUnavailableText));
                }
                finally
                {
                    try { window.Close(); } catch { /* teardown is best effort */ }
                    WirelessView.AdbAvailableOverrideForTests = () => true;
                }
            }

            // ---- B: ready-to-pair gating — Connect independent of Pair fields ----
            {
                var v = Make();
                Expect("B: Pair disabled with empty fields", !v.PairBtn.IsEnabled);
                Expect("B: Connect disabled with empty fields", !v.ConnectBtn.IsEnabled);

                v.PairHostBox.Text = "192.168.1.42";
                v.PairPortBox.Text = "37123";
                v.PairCodeBox.Password = "246813";
                Expect("B: Pair enabled once host/port/code are all valid", v.PairBtn.IsEnabled);
                Expect("B: Connect stays disabled — pairing inputs never leak into it", !v.ConnectBtn.IsEnabled);

                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                Expect("B: Connect enabled once its own host/port are valid", v.ConnectBtn.IsEnabled);
                Unload(v);
            }

            // ---- C: pair success never claims the device is connected ----
            {
                WirelessView.PairOverrideForTests = (_, _) => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", ""));
                var v = Make();
                v.PairHostBox.Text = "192.168.1.42";
                v.PairPortBox.Text = "37123";
                v.PairCodeBox.Password = "246813";
                ClickAndPump(v.PairBtn);

                Expect("C: pair result recorded as success", v.LastPairResultForTests?.Success == true);
                Expect("C: pairing code cleared after success", v.PairCodeBox.Password == "");
                Expect("C: status text never claims connected", !v.StatusText.Text.Contains("متصل ومصرَّح"));
                Expect("C: connect card auto-filled with the paired host for convenience",
                    v.ConnectHostBox.Text == "192.168.1.42");
                Unload(v);
                WirelessView.PairOverrideForTests = null;
            }

            // ---- D: pairing code never appears in the automation tree, win or lose ----
            {
                const string secret = "135790";
                WirelessView.PairOverrideForTests = (_, _) => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 1, "Failed: Incorrect pairing code", ""));
                var v = Make();
                v.PairHostBox.Text = "192.168.1.42";
                v.PairPortBox.Text = "37123";
                v.PairCodeBox.Password = secret;
                ClickAndPump(v.PairBtn);

                Expect("D: pair recorded as failed", v.LastPairResultForTests?.Success == false);
                Expect("D: pairing code cleared after failure", v.PairCodeBox.Password == "");
                Expect("D: failure message never contains the code", !v.LastPairResultForTests!.Message.Contains(secret));
                Expect("D: status text never contains the code", !v.StatusText.Text.Contains(secret));
                Expect("D: PairCodeBox never carries an automation Name with the code",
                    !(AutomationProperties.GetName(v.PairCodeBox) ?? "").Contains(secret));
                Unload(v);
                WirelessView.PairOverrideForTests = null;
            }

            // ---- E: connect command success WITHOUT a matching device never claims connected ----
            {
                WirelessView.ConnectOverrideForTests = _ => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", ""));
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                ClickAndPump(v.ConnectBtn);

                Expect("E: connect command recorded as succeeded", v.LastConnectResultForTests?.CommandSucceeded == true);
                Expect("E: Verify button appears", v.VerifyBtn.Visibility == Visibility.Visible);
                Expect("E: status text does not claim the device is connected/authorized yet",
                    !v.StatusText.Text.Contains("مصرَّح له"));
                Unload(v);
                WirelessView.ConnectOverrideForTests = null;
            }

            // ---- F: connect command success WITH a matching authorized device reconciles truthfully ----
            // Independent-review repair: the fixed dispatcher-pump assumption is replaced with a
            // bounded wait for the EXACT condition each step depends on, with an explicit timeout
            // and a failure message — never a blind pump count, never Thread.Sleep.
            {
                const string endpoint = "192.168.1.42:5555";
                WirelessView.ConnectOverrideForTests = _ => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "connected to " + endpoint, ""));
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("F: the device fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                v.ConnectBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("F: the Connect attempt records a result within the bound",
                    WaitUntil(() => v.LastConnectResultForTests != null));

                Expect("F: status text now reports the device connected and authorized",
                    v.StatusText.Text.Contains("مصرَّح له"));
                Expect("F: connected device is auto-selected (only one candidate)",
                    v.SelectedWirelessSerialForTests == endpoint);
                Expect("F: GoToDashboard is enabled for an authorized selection", v.GoToDashboardBtn.IsEnabled);
                Unload(v);
                WirelessView.ConnectOverrideForTests = null;
                DeviceWatcher.Stop();
            }

            // ---- G: multiple wireless devices — never silently chosen ----
            {
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = "192.168.1.42:5555", State = "device" },
                        new() { Serial = "192.168.1.55:5555", State = "unauthorized" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("G: both device fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();

                var v = Make();
                Expect("G: no wireless device is auto-selected when more than one is present",
                    v.SelectedWirelessSerialForTests == null);
                Expect("G: GoToDashboard stays disabled with no explicit selection", !v.GoToDashboardBtn.IsEnabled);
                Unload(v);
            }

            // ---- H: double activation starts exactly one Pair operation ----
            // The override deliberately does NOT complete synchronously (a TaskCompletionSource,
            // resolved only after both clicks have been raised) — awaiting an already-completed
            // Task never actually suspends the async method, so a same-tick double click could
            // not otherwise land while the operation is genuinely still "in flight".
            {
                var callCount = 0;
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.PairOverrideForTests = (_, _) => { callCount++; return tcs.Task; };
                var v = Make();
                v.PairHostBox.Text = "192.168.1.42";
                v.PairPortBox.Text = "37123";
                v.PairCodeBox.Password = "246813";
                v.PairBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                v.PairBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // rapid re-activation while busy
                Pump();

                Expect("H: exactly one Pair call reached the service despite two rapid clicks", callCount == 1);
                tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", ""));
                Expect("H: the pending Pair call settles within the bound", WaitUntil(() => !v.PairBusyForTests));
                Unload(v);
                WirelessView.PairOverrideForTests = null;
            }

            // ---- I: double activation starts exactly one Connect operation ----
            {
                var callCount = 0;
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.ConnectOverrideForTests = _ => { callCount++; return tcs.Task; };
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                v.ConnectBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                v.ConnectBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // rapid re-activation while busy
                Pump();

                Expect("I: exactly one Connect call reached the service despite two rapid clicks", callCount == 1);
                tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", ""));
                Expect("I: the pending Connect call settles within the bound", WaitUntil(() => !v.ConnectBusyForTests));
                Unload(v);
                WirelessView.ConnectOverrideForTests = null;
            }

            // ---- J: stale Pair completion after unload cannot repaint ----
            {
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.PairOverrideForTests = (_, _) => tcs.Task;
                var v = Make();
                v.PairHostBox.Text = "192.168.1.42";
                v.PairPortBox.Text = "37123";
                v.PairCodeBox.Password = "246813";
                v.PairBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("J: Pair is busy while the call is in flight", v.PairBusyForTests);

                Unload(v);   // unloads WHILE the pair call is still pending
                Exception? thrown = null;
                try
                {
                    tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to x", ""));
                    Pump();
                }
                catch (Exception ex) { thrown = ex; }

                Expect("J: a stale Pair completion after unload throws nothing", thrown == null);
                Expect("J: a stale Pair completion after unload does not repaint (still no recorded result)",
                    v.LastPairResultForTests == null);
                WirelessView.PairOverrideForTests = null;
            }

            // ---- K: stale Connect completion after unload cannot repaint ----
            {
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.ConnectOverrideForTests = _ => tcs.Task;
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                v.ConnectBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("K: Connect is busy while the call is in flight", v.ConnectBusyForTests);

                Unload(v);
                Exception? thrown = null;
                try
                {
                    tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", ""));
                    Pump();
                }
                catch (Exception ex) { thrown = ex; }

                Expect("K: a stale Connect completion after unload throws nothing", thrown == null);
                Expect("K: a stale Connect completion after unload does not repaint (still no recorded result)",
                    v.LastConnectResultForTests == null);
                WirelessView.ConnectOverrideForTests = null;
            }

            // ---- L: pairing code cleared on unload even with nothing submitted ----
            {
                var v = Make();
                v.PairCodeBox.Password = "999999";
                Unload(v);
                Expect("L: pairing code cleared on unload", v.PairCodeBox.Password == "");
            }

            // ---- M: DeviceWatcher subscription balance ----
            {
                var baseline = SubscriberCount();
                var v = Make();
                Expect("M: subscriber count after Loaded", SubscriberCount() == baseline + 1);

                Load(v);   // a repeated Loaded without an intervening Unloaded adds nothing
                Expect("M: repeated Loaded does not duplicate the subscription", SubscriberCount() == baseline + 1);

                Unload(v);
                Expect("M: subscriber count back to baseline after Unloaded", SubscriberCount() == baseline);

                for (var cycle = 1; cycle <= 3; cycle++)
                {
                    var cycled = new WirelessView();
                    Load(cycled);
                    Expect($"M: cycle {cycle} subscribed exactly once", SubscriberCount() == baseline + 1);
                    Unload(cycled);
                    Expect($"M: cycle {cycle} unsubscribed cleanly", SubscriberCount() == baseline);
                }
            }

            // ---- N: Verify — one activation, disables before awaiting, updates visible state ----
            {
                WirelessView.ConnectOverrideForTests = _ => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", ""));
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                ClickAndPump(v.ConnectBtn);
                WirelessView.ConnectOverrideForTests = null;
                Expect("N: Verify button visible after a connect command succeeds",
                    v.VerifyBtn.Visibility == Visibility.Visible);

                var callCount = 0;
                var tcs = new TaskCompletionSource<DeviceDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.DiscoverDevicesOverrideForTests = () => { callCount++; return tcs.Task; };

                v.VerifyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("N: exactly one discovery call reached the service", callCount == 1);
                Expect("N: Verify disables itself before its awaited call resolves", !v.VerifyBtn.IsEnabled);
                Expect("N: Verify is busy while the call is in flight", v.VerifyBusyForTests);

                v.VerifyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // rapid re-activation while busy
                Pump();
                Expect("N: a rapid second Verify activation while busy starts no second call", callCount == 1);

                const string endpoint = "192.168.1.42:5555";
                tcs.SetResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                Expect("N: the pending Verify call settles within the bound", WaitUntil(() => !v.VerifyBusyForTests));
                Expect("N: VerifyBtn re-enabled once the call settles", v.VerifyBtn.IsEnabled);
                Expect("N: the returned snapshot updates the visible device state",
                    v.DevicesForTests.Any(d => d.Serial == endpoint));
                Expect("N: status text reflects the newly-visible authorized device",
                    v.StatusText.Text.Contains("مصرَّح له"));

                Unload(v);
                WirelessView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- O: Verify failure restores controls and shows a truthful bounded error ----
            {
                WirelessView.ConnectOverrideForTests = _ => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", ""));
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                ClickAndPump(v.ConnectBtn);
                WirelessView.ConnectOverrideForTests = null;

                const string rawDiagnostic = "adb devices timed out (internal diagnostic)";
                WirelessView.DiscoverDevicesOverrideForTests = () => Task.FromResult(
                    DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.TimedOut, rawDiagnostic));
                ClickAndPump(v.VerifyBtn);

                Expect("O: Verify controls restored (not busy) after a failed query", !v.VerifyBusyForTests);
                Expect("O: VerifyBtn re-enabled after a failed query", v.VerifyBtn.IsEnabled);
                Expect("O: a truthful bounded error is recorded", !string.IsNullOrWhiteSpace(v.LastVerifyErrorForTests));
                Expect("O: the verify error never leaks the raw diagnostic",
                    v.LastVerifyErrorForTests != null && !v.LastVerifyErrorForTests.Contains(rawDiagnostic));
                Expect("O: the verify error is shown in the connect card", v.ConnectResultText.Text == v.LastVerifyErrorForTests);

                Unload(v);
                WirelessView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- P: stale Verify completion after unload cannot repaint ----
            {
                WirelessView.ConnectOverrideForTests = _ => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", ""));
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                ClickAndPump(v.ConnectBtn);
                WirelessView.ConnectOverrideForTests = null;

                var tcs = new TaskCompletionSource<DeviceDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.DiscoverDevicesOverrideForTests = () => tcs.Task;
                v.VerifyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("P: Verify is busy while the call is in flight", v.VerifyBusyForTests);

                Unload(v);
                Exception? thrown = null;
                const string staleSerial = "192.168.1.99:5555";
                try
                {
                    tcs.SetResult(DeviceDiscoveryResult.Success(
                        new List<AdbDevice> { new() { Serial = staleSerial, State = "device" } }));
                    Pump();
                }
                catch (Exception ex) { thrown = ex; }

                Expect("P: a stale Verify completion after unload throws nothing", thrown == null);
                Expect("P: a stale Verify completion after unload does not repaint the device list",
                    !v.DevicesForTests.Any(d => d.Serial == staleSerial));

                WirelessView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- Q: Verify and Connect proceed independently without corrupting shared state ----
            // Verify has its OWN busy gate rather than sharing one with Pair/Connect (it queries
            // device state; it does not mutate a pairing/connection attempt), so this proves the
            // "explicitly define and test safe independent behavior" contract rather than a
            // shared gate: an unrelated Connect attempt may start while a Verify call from an
            // earlier connect is still in flight, and both settle without throwing or leaving
            // the device list corrupted.
            {
                const string endpoint = "192.168.1.42:5555";
                WirelessView.ConnectOverrideForTests = _ => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "connected to " + endpoint, ""));
                var v = Make();
                v.ConnectHostBox.Text = "192.168.1.42";
                v.ConnectPortBox.Text = "5555";
                ClickAndPump(v.ConnectBtn);   // makes VerifyBtn visible

                var verifyTcs = new TaskCompletionSource<DeviceDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.DiscoverDevicesOverrideForTests = () => verifyTcs.Task;
                v.VerifyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("Q: Verify is busy", v.VerifyBusyForTests);

                var connectTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WirelessView.ConnectOverrideForTests = _ => connectTcs.Task;
                v.ConnectBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // independent, unrelated attempt
                Pump();
                Expect("Q: an unrelated Connect attempt can start while Verify is still in flight", v.ConnectBusyForTests);

                Exception? thrown = null;
                try
                {
                    verifyTcs.SetResult(DeviceDiscoveryResult.Success(
                        new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                    connectTcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "connected to " + endpoint, ""));
                    Expect("Q: both operations settle within the bound",
                        WaitUntil(() => !v.VerifyBusyForTests && !v.ConnectBusyForTests));
                }
                catch (Exception ex) { thrown = ex; }

                Expect("Q: interleaved Verify/Connect completion throws nothing", thrown == null);
                Expect("Q: shared device state stays consistent afterward",
                    v.DevicesForTests.Any(d => d.Serial == endpoint));

                Unload(v);
                WirelessView.ConnectOverrideForTests = null;
                WirelessView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- R: ToolsUnavailable gates the Dashboard transition too, not only Pair/Connect ----
            // Independent-review repair: proves, from the REAL visual tree of a hosted MainWindow
            // (not just the production-rule source read), that an authorized wireless device
            // being present does not let the transition slip through when local tools are
            // unavailable — the click handler's own defense-in-depth (checked again here via a
            // programmatically raised Click, which bypasses IsEnabled entirely) must still refuse
            // to navigate, must never touch AppState, and must never start a mirror session.
            {
                const string endpoint = "192.168.1.42:5555";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("R: the authorized wireless device fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var previousDevice = AppState.CurrentDevice;
                var baselineActiveSessions = ScrcpyService.ActiveCount;
                AppState.SetCurrent(null);
                WirelessView.AdbAvailableOverrideForTests = () => false;

                var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = -6000, Top = 100 };
                try
                {
                    window.Show();
                    Pump();
                    window.NavWireless.IsChecked = true;   // navigates the real shell to the Wireless page
                    Pump();

                    Expect("R: the hosted MainWindow actually navigated to the Wireless page for this scenario",
                        window.PageHost.Content is WirelessView);

                    if (window.PageHost.Content is WirelessView v)
                    {
                        DeviceWatcher.RequestRefresh();
                        Pump();

                        // Otherwise-valid field data — proves the disable is the ToolsUnavailable
                        // gate itself, not merely empty/invalid fields.
                        v.PairHostBox.Text = "192.168.1.42";
                        v.PairPortBox.Text = "37123";
                        v.PairCodeBox.Password = "246813";
                        v.ConnectHostBox.Text = "192.168.1.42";
                        v.ConnectPortBox.Text = "5555";
                        Pump();

                        Expect("R: Pair disabled when tools are unavailable, even with otherwise-valid fields and a device present",
                            !v.PairBtn.IsEnabled);
                        Expect("R: Connect disabled when tools are unavailable, even with otherwise-valid fields and a device present",
                            !v.ConnectBtn.IsEnabled);
                        Expect("R: Verify disabled when tools are unavailable with a device present",
                            !v.VerifyBtn.IsEnabled);
                        Expect("R: Dashboard transition disabled when tools are unavailable with an authorized device present",
                            !v.GoToDashboardBtn.IsEnabled);

                        var helpText = AutomationProperties.GetHelpText(v.GoToDashboardBtn) ?? "";
                        var toolTip = v.GoToDashboardBtn.ToolTip as string ?? "";
                        Expect("R: the transition HelpText/ToolTip explains the local-tools blocker",
                            helpText.Contains("أدوات الاتصال بالجهاز غير متوفرة")
                            || toolTip.Contains("أدوات الاتصال بالجهاز غير متوفرة"));

                        v.GoToDashboardBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // bypasses IsEnabled
                        Pump();

                        Expect("R: a programmatically raised transition click does not navigate away from Wireless",
                            window.PageHost.Content is WirelessView);
                        Expect("R: AppState does not change from a gated transition click (proves ApplySetupResult never ran)",
                            AppState.CurrentDevice == null);
                        Expect("R: no mirror session starts from a gated transition click",
                            ScrcpyService.ActiveCount == baselineActiveSessions);
                    }
                }
                finally
                {
                    try { window.Close(); } catch { /* teardown is best effort */ }
                    Pump();
                    WirelessView.AdbAvailableOverrideForTests = () => true;
                    AppState.SetCurrent(previousDevice);
                    DeviceWatcher.Stop();
                }
            }

            // ---- S: rendered + accessible proof that colliding wireless identities stay distinguishable ----
            // Independent-review repair: the pure WirelessDeviceDisplay.BuildRows unit tests prove
            // the DATA is correct, but not that the REAL visual tree (RadioButton content,
            // TextBlocks, AutomationProperties.Name) actually renders that distinction. Uses
            // exactly the reviewed colliding pair — same brand/model, both authorized, identical
            // ShellLayout.MaskSerial output — with no preselected device.
            {
                const string serialA = "192.168.1.42:5555";
                const string serialB = "192.168.1.55:5555";
                List<AdbDevice> MakeColliding() => new()
                {
                    new() { Serial = serialA, State = "device", Brand = "Samsung", Model = "Galaxy S24 Ultra" },
                    new() { Serial = serialB, State = "device", Brand = "Samsung", Model = "Galaxy S24 Ultra" },
                };

                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(MakeColliding()));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("S: both colliding device fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();

                var v = Make();
                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                Expect("S: exactly two device RadioButtons are rendered", radios.Count == 2);

                if (radios.Count == 2)
                {
                    var rows = radios
                        .Select(r => (Radio: r, Texts: (r.Content as StackPanel)?.Children.OfType<TextBlock>()
                            .Select(t => t.Text ?? "").ToList()))
                        .ToList();

                    Expect("S: each row's content is a StackPanel of TextBlocks", rows.All(r => r.Texts != null));

                    if (rows.All(r => r.Texts != null))
                    {
                        var baseLabels = rows.Select(r => r.Texts![0]).ToList();
                        var masked = rows.Select(r => r.Texts![1]).ToList();
                        // Base label + masked endpoint + [token] + state: a colliding row always
                        // carries the extra token, so every row here must have 4 text runs.
                        Expect("S: each row carries an additional safe disambiguation token (4 text runs, not 3)",
                            rows.All(r => r.Texts!.Count == 4));

                        Expect("S: the visible base/model labels are equal across the colliding rows",
                            baseLabels.Count == 2 && baseLabels[0] == baseLabels[1] && baseLabels[0] != "");
                        Expect("S: the ordinary masked endpoint text is equal across the colliding rows",
                            masked.Count == 2 && masked[0] == masked[1] && masked[0] != "");

                        var tokens = rows.Select(r => r.Texts!.Count == 4 ? r.Texts![2] : null).ToList();
                        Expect("S: the two disambiguation tokens differ", tokens.Count == 2 && tokens[0] != null &&
                            tokens[1] != null && tokens[0] != tokens[1]);

                        var fullIdentities = rows.Select(r => string.Join("|", r.Texts!)).ToList();
                        Expect("S: the complete visible row identities differ",
                            fullIdentities.Count == 2 && fullIdentities[0] != fullIdentities[1]);

                        var accessibleNames = radios.Select(r => AutomationProperties.GetName(r) ?? "").ToList();
                        Expect("S: AutomationProperties.Name differs between the colliding rows",
                            accessibleNames.Count == 2 && accessibleNames[0] != "" && accessibleNames[1] != ""
                            && accessibleNames[0] != accessibleNames[1]);
                        Expect("S: neither accessible name contains either raw endpoint",
                            accessibleNames.All(n => !n.Contains(serialA) && !n.Contains(serialB)));
                        Expect("S: neither rendered TextBlock contains either raw endpoint",
                            rows.All(r => r.Texts!.All(t => !t.Contains(serialA) && !t.Contains(serialB))));
                    }
                }
                Unload(v);

                // ---- reversing device order preserves each serial's own token ----
                var forwardToken = WirelessDeviceDisplay.BuildRows(MakeColliding())
                    .Single(r => r.Device.Serial == serialA).DisambiguatorToken;
                var reversedInput = MakeColliding();
                reversedInput.Reverse();
                var reversedToken = WirelessDeviceDisplay.BuildRows(reversedInput)
                    .Single(r => r.Device.Serial == serialA).DisambiguatorToken;
                Expect("S: reversing device order preserves each serial's disambiguation token",
                    forwardToken != null && forwardToken == reversedToken);

                // ---- replacement AdbDevice objects with the same serials keep the same token ----
                var replacement = new List<AdbDevice>
                {
                    new() { Serial = serialA, State = "unauthorized" },
                    new() { Serial = serialB, State = "unauthorized" },
                };
                var replacementToken = WirelessDeviceDisplay.BuildRows(replacement)
                    .Single(r => r.Device.Serial == serialA).DisambiguatorToken;
                Expect("S: replacing both AdbDevice objects with new objects carrying the same serials preserves each token",
                    replacementToken == forwardToken);

                // ---- selection stays keyed on the true ordinal serial, never the token ----
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(MakeColliding()));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("S: fixtures visible again before the selection check",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();

                var v2 = Make();
                Expect("S: no wireless device is preselected when two colliding candidates are present",
                    v2.SelectedWirelessSerialForTests == null);
                var radiosAgain = v2.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                if (radiosAgain.Count == 2)
                {
                    radiosAgain[1].IsChecked = true;
                    Pump();
                    Expect("S: selection is based on the true ordinal serial, not the disambiguation token",
                        v2.SelectedWirelessSerialForTests == serialB);
                }
                Unload(v2);
            }

            // "No real adb/bridge/engine process starts anywhere above" is proven by
            // construction, not by a runtime process scan — every scenario above routes its
            // Pair/Connect/discovery call through the WirelessView.*OverrideForTests seams,
            // never AdbService's real calls. A self-scan here would itself violate the
            // ADB-ownership policy's "process enumeration exists only in AdbServerOwnership.cs"
            // invariant (SecurityGuardRemovalTests.ProcessEnumeration_ExistsOnlyInTheAdbOwnershipPolicy).

            // Independent-review repair: pins the assertion count from every scenario above (A
            // through S — captured BEFORE the binding-error check and the pin check below are
            // themselves counted, so neither this assertion nor the next ones can include
            // itself) so an accidentally-deleted scenario, or a silently-removed Expect call,
            // fails this check loudly instead of just quietly lowering the reported denominator.
            var scenarioAssertions = total;
            Expect($"exactly {ExpectedScenarioAssertions} scenario assertions ran (delete-scenario guard)",
                scenarioAssertions == ExpectedScenarioAssertions);

            // Supplementary named-scenario guard (see ScenarioIds remarks): catches one whole
            // scenario disappearing even in the unlikely event the raw count above still matched.
            // This is a second, independent signal, not a claim that the pair together make the
            // suite tamper-proof.
            var missingScenarios = ScenarioIds.Where(id => !assertionNames.Any(n => n.StartsWith(id + ":", StringComparison.Ordinal))).ToList();
            Expect($"all {ScenarioIds.Length} named runtime scenarios are still present: " +
                   (missingScenarios.Count == 0 ? "none missing" : "missing " + string.Join(", ", missingScenarios)),
                missingScenarios.Count == 0);

            // Independent-review repair: the WPF binding/resource-error check now participates
            // in the ordinary total/failure arithmetic as a real counted assertion — previously
            // it added an entry to `failures` without ever incrementing `total`, which could
            // print an impossible "42/43" (more failures than the fixed total accounted for).
            var bindingSummary = bindingErrors.Count == 0
                ? "no WPF binding/resource errors"
                : $"no WPF binding/resource errors — found {bindingErrors.Count}: " + string.Join(" | ", bindingErrors.Take(5));
            Expect(bindingSummary, bindingErrors.Count == 0);
        }
        catch (Exception ex)
        {
            failures.Add($"unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            WirelessView.AdbAvailableOverrideForTests = wasAdbAvailable;
            WirelessView.PairOverrideForTests = wasPair;
            WirelessView.ConnectOverrideForTests = wasConnect;
            WirelessView.DiscoverDevicesOverrideForTests = wasDiscover;
            DeviceWatcher.ResetForTests();

            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            PresentationTraceSources.Refresh();
        }

        var passed = failures.Count == 0;
        var report = $"wireless: {total - failures.Count}/{total} assertions passed" +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));
        return new Result(passed, total, failures.Count, report);
    }

    // ---------------- helpers ----------------

    private static WirelessView Make()
    {
        var v = new WirelessView();
        Load(v);
        return v;
    }

    private static void Load(WirelessView v) => v.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    private static void Unload(WirelessView v) => v.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

    private static void ClickAndPump(System.Windows.Controls.Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();
    }

    private static int SubscriberCount()
    {
        var field = typeof(DeviceWatcher).GetField("DevicesChanged", BindingFlags.NonPublic | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>
    /// Independent-review repair: waits for the EXACT condition a scenario depends on, pumping
    /// the dispatcher so pending continuations can run, instead of assuming a fixed number of
    /// pumps is always enough. Bounded by <paramref name="timeout"/> (default 2s, generous for a
    /// synchronous in-memory fixture); returns false — never hangs, never throws — the moment
    /// the deadline passes without the condition becoming true, so a caller's own Expect reports
    /// a clear, named failure rather than the test silently hanging or false-passing.
    /// </summary>
    private static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (true)
        {
            if (condition()) return true;
            if (DateTime.UtcNow >= deadline) return condition();

            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>Same technique <see cref="DeviceSetupWizardSelfTest"/> uses to capture WPF's own
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
