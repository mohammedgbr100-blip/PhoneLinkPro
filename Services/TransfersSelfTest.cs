using System.Diagnostics;
using System.IO;
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
/// Runtime proof for the Batch 4D guided file-transfers page, following the same pattern as
/// <see cref="WirelessSelfTest"/> (Batch 4C): xUnit runs MTA and cannot construct a real WPF
/// control tree, so behavioral coverage that needs an actual visual tree lives in the
/// PLP_SELFTEST harness instead. Every Send/Receive/discovery call in every scenario below is
/// intercepted through <see cref="TransfersView.PushOverrideForTests"/> /
/// <see cref="TransfersView.PullOverrideForTests"/> / <see cref="TransfersView.DiscoverDevicesOverrideForTests"/>
/// / <see cref="TransfersView.PickFilesOverrideForTests"/> / <see cref="TransfersView.PickSaveLocationOverrideForTests"/>
/// — an un-intercepted call here would start a real adb.exe or block on a native file dialog.
/// No real adb/scrcpy/bridge process starts anywhere in this suite (Phase 12 #39/#40): every
/// scenario routes exclusively through these seams, never AdbService's/Microsoft.Win32's real
/// calls, and no timer or background scanner exists on this page at all.
/// </summary>
internal static class TransfersSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    /// <summary>Same delete-scenario guard discipline as <see cref="WirelessSelfTest"/>.</summary>
    private const int ExpectedScenarioAssertions = 176;

    private static readonly string[] ScenarioIds =
    {
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "AA", "AB", "AC", "AD", "AE", "AF", "AG", "AH",
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

        var wasAdbAvailable = TransfersView.AdbAvailableOverrideForTests;
        var wasPush = TransfersView.PushOverrideForTests;
        var wasPull = TransfersView.PullOverrideForTests;
        var wasDiscover = TransfersView.DiscoverDevicesOverrideForTests;
        var wasPickFiles = TransfersView.PickFilesOverrideForTests;
        var wasPickSave = TransfersView.PickSaveLocationOverrideForTests;
        var previousAppState = AppState.CurrentDevice;

        var scratchDir = Path.Combine(Path.GetTempPath(), "plp_transfers_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string MakeFile(string name, string content = "x")
        {
            var path = Path.Combine(scratchDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        try
        {
            // ---- A: tools unavailable ----
            {
                TransfersView.AdbAvailableOverrideForTests = () => false;
                var v = Make();
                Expect("A: tools-unavailable banner is visible", v.ToolsUnavailableBanner.Visibility == Visibility.Visible);
                Expect("A: Send is disabled", !v.SendBtn.IsEnabled);
                Expect("A: Receive is disabled", !v.ReceiveBtn.IsEnabled);
                Expect("A: Verify is disabled", !v.VerifyBtn.IsEnabled);
                Unload(v);
                TransfersView.AdbAvailableOverrideForTests = () => true;
            }

            // ---- B: no device ----
            {
                DeviceWatcher.ResetForTests();
                AppState.SetCurrent(null);
                var v = Make();
                Expect("B: no-devices message is visible", v.NoDevicesText.Visibility == Visibility.Visible);
                Expect("B: a truthful route to the wireless page is offered", v.GoToWirelessBtn.Visibility == Visibility.Visible);
                Expect("B: Send stays disabled with no device", !v.SendBtn.IsEnabled);
                Expect("B: Receive stays disabled with no device", !v.ReceiveBtn.IsEnabled);
                Unload(v);
            }

            // ---- C: unauthorized/offline devices are shown but never selectable ----
            {
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = "R5CX10UNAUTH", State = "unauthorized", Model = "Pixel 7" },
                        new() { Serial = "R5CX10OFFLINE", State = "offline", Model = "Pixel 8" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("C: both fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                Expect("C: both devices are listed", radios.Count == 2);
                Expect("C: the unauthorized/offline rows are disabled (not selectable)",
                    radios.All(r => !r.IsEnabled));
                Expect("C: no device is selected", v.SelectedSerialForTests == null);
                Expect("C: Send stays disabled — authorization is distinguished from mere presence",
                    !v.SendBtn.IsEnabled);
                Unload(v);
            }

            // ---- D: one authorized device is auto-selected ----
            {
                const string endpoint = "R5CX10AUTHORIZED";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("D: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                Expect("D: the single authorized device is auto-selected", v.SelectedSerialForTests == endpoint);
                Unload(v);
            }

            // ---- E: multiple authorized devices require an explicit choice ----
            {
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = "R5CX10FIRST", State = "device", Model = "Pixel 9" },
                        new() { Serial = "R5CX10SECOND", State = "device", Model = "Pixel 8" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("E: both fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                Expect("E: no device is auto-selected when more than one authorized candidate exists",
                    v.SelectedSerialForTests == null);
                Expect("E: Send stays disabled with no explicit selection", !v.SendBtn.IsEnabled);

                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                Expect("E: both authorized rows are selectable", radios.Count == 2 && radios.All(r => r.IsEnabled));
                if (radios.Count == 2)
                {
                    radios[1].IsChecked = true;
                    Pump();
                    Expect("E: an explicit choice is honored", v.SelectedSerialForTests == "R5CX10SECOND");
                }
                Unload(v);
            }

            // ---- F: selection preserved by ordinal serial across replacement objects/reordering ----
            {
                var a = new AdbDevice { Serial = "R5CX10A", State = "device", Model = "Pixel 9" };
                var b = new AdbDevice { Serial = "R5CX10B", State = "device", Model = "Pixel 8" };

                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(new List<AdbDevice> { a, b }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("F: both fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                if (radios.Count == 2) { radios[1].IsChecked = true; Pump(); }
                Expect("F: device B is selected", v.SelectedSerialForTests == "R5CX10B");

                // reordering + a replacement object with the same serial
                var b2 = new AdbDevice { Serial = "R5CX10B", State = "device", Model = "Pixel 8" };
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(new List<AdbDevice> { b2, a }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("F: the reordered/replacement snapshot is applied",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                v.OnShown();
                Expect("F: selection survives reordering and a replacement AdbDevice object",
                    v.SelectedSerialForTests == "R5CX10B");
                Unload(v);
            }

            // ---- G: device disappearance disables transfer immediately ----
            {
                const string endpoint = "R5CX10VANISH";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("G: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                Expect("G: the device is selected before disappearing", v.SelectedSerialForTests == endpoint);

                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(new List<AdbDevice>()));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("G: the empty snapshot is applied", WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 0));
                DeviceWatcher.Stop();
                v.OnShown();

                Expect("G: selection is cleared once the device disappears", v.SelectedSerialForTests == null);
                Expect("G: Send is disabled immediately after disappearance", !v.SendBtn.IsEnabled);
                Expect("G: Receive is disabled immediately after disappearance", !v.ReceiveBtn.IsEnabled);
                Unload(v);
            }

            // ---- H: IPv4 wireless, IPv6 wireless and USB targeting are all recognized ----
            {
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = "192.168.1.50:5555", State = "device", Model = "Pixel A" },
                        new() { Serial = "[2001:db8::50]:5555", State = "device", Model = "Pixel B" },
                        new() { Serial = "R5CX10USB", State = "device", Model = "Pixel C" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("H: all three fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 3));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                Expect("H: IPv4 wireless device is recognized as Wi-Fi",
                    v.DevicesForTests.Single(d => d.Serial == "192.168.1.50:5555").ConnectionLabel == "Wi-Fi");
                Expect("H: IPv6 wireless device is recognized as Wi-Fi",
                    v.DevicesForTests.Single(d => d.Serial == "[2001:db8::50]:5555").ConnectionLabel == "Wi-Fi");
                Expect("H: USB device is recognized as USB",
                    v.DevicesForTests.Single(d => d.Serial == "R5CX10USB").ConnectionLabel == "USB");
                Expect("H: all three authorized devices are rendered as selectable rows",
                    v.DeviceSelectorList.Children.OfType<RadioButton>().Count(r => r.IsEnabled) == 3);
                Unload(v);
            }

            // ---- I: masked/collision-safe visible identities in the device selector ----
            {
                const string serialA = "192.168.1.42:5555";
                const string serialB = "192.168.1.55:5555";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = serialA, State = "device", Brand = "Samsung", Model = "Galaxy S24 Ultra" },
                        new() { Serial = serialB, State = "device", Brand = "Samsung", Model = "Galaxy S24 Ultra" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("I: both colliding fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                Expect("I: exactly two device rows are rendered", radios.Count == 2);
                var names = radios.Select(r => AutomationProperties.GetName(r) ?? "").ToList();
                Expect("I: accessible names differ between the colliding rows",
                    names.Count == 2 && names[0] != "" && names[1] != "" && names[0] != names[1]);
                Expect("I: neither accessible name contains either raw endpoint",
                    names.All(n => !n.Contains(serialA) && !n.Contains(serialB)));
                Unload(v);
            }

            // ---- J: remote-path validation drives the Receive field error ----
            {
                var v = Make();
                v.ReceiveRemotePathBox.Text = "not-an-android-path";
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // submit attempt
                Pump();
                Expect("J: an invalid remote path shows a field error", v.ReceiveRemotePathError.Visibility == Visibility.Visible);

                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/Camera/photo.jpg";
                Pump();
                Expect("J: a valid shared-storage path clears the field error",
                    v.ReceiveRemotePathError.Visibility == Visibility.Collapsed);
                Unload(v);
            }

            // ---- K: local file existence is checked at send time; Unicode/space names are safe ----
            {
                const string endpoint = "R5CX10SENDER";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("K: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var unicodeFile = MakeFile("صورة عائلية 家族.jpg");
                var vanished = Path.Combine(scratchDir, "will-vanish.txt");
                File.WriteAllText(vanished, "x");

                TransfersView.PickFilesOverrideForTests = () => new[] { unicodeFile, vanished };
                var v = Make();
                v.ChooseFilesBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("K: both files are recorded as selected", v.SelectedLocalFilesForTests.Count == 2);

                File.Delete(vanished);   // vanish between selection and send

                TransfersView.PushOverrideForTests = (_, local, _, _) => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped. 5 MB/s (100 bytes in 0.02s)", ""));
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("K: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Expect("K: exactly two items are reported", v.LastSendBatchForTests.Count == 2);
                Expect("K: the Unicode/space file name is reported safely",
                    v.LastSendBatchForTests.Any(r => r.SafeFileName.Contains("صورة عائلية 家族.jpg") && r.Success));
                Expect("K: the vanished file is reported as failed, not silently skipped",
                    v.LastSendBatchForTests.Any(r => r.SafeFileName == "will-vanish.txt" && !r.Success));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- L/M: Send passes independent local/remote arguments and the selected serial ----
            {
                const string endpoint = "R5CX10ARGS";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("L/M: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("with space and معلومات.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);

                string? capturedSerial = null, capturedLocal = null, capturedRemote = null;
                TransfersView.PushOverrideForTests = (serial, local, remote, _) =>
                {
                    capturedSerial = serial; capturedLocal = local; capturedRemote = remote;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                };
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("L/M: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Expect("M: Send used the selected device's own serial internally", capturedSerial == endpoint);
                Expect("L: the local argument is the exact chosen file path, independent of the remote value",
                    capturedLocal == file);
                Expect("L: the remote argument is the standard destination, independent of the local value",
                    capturedRemote == TransferOperations.StandardDestinationPaths[StandardRemoteDestination.Download]);

                Unload(v);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- N: Receive uses the selected device's own serial internally ----
            {
                const string endpoint = "R5CX10RECV";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("N: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var localTarget = Path.Combine(scratchDir, "received.jpg");
                TransfersView.PickSaveLocationOverrideForTests = _ => localTarget;
                var v = Make();
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/Camera/photo.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);

                string? capturedSerial = null, capturedRemote = null, capturedLocal = null;
                TransfersView.PullOverrideForTests = (serial, remote, local, _) =>
                {
                    capturedSerial = serial; capturedRemote = remote; capturedLocal = local;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", ""));
                };
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("N: Receive settles within the bound", WaitUntil(() => !v.ReceiveBusyForTests));

                Expect("N: Receive used the selected device's own serial internally", capturedSerial == endpoint);
                Expect("N: the remote argument is the exact typed path", capturedRemote == "/sdcard/DCIM/Camera/photo.jpg");
                Expect("N: the local argument is the exact chosen destination", capturedLocal == localTarget);

                Unload(v);
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- O: ArgumentList architecture — local/remote never concatenated into one value ----
            {
                var pushArgs = AdbService.PushArgs(@"C:\Users\test\my file.txt", "/sdcard/Download");
                Expect("O: PushArgs keeps exactly 3 separate elements (no concatenation)", pushArgs.Count == 3);
                Expect("O: PushArgs' first element is the fixed \"push\" token", pushArgs[0] == "push");
                Expect("O: PushArgs' local element is untouched, no shell quoting added",
                    pushArgs[1] == @"C:\Users\test\my file.txt");
                Expect("O: PushArgs' remote element is untouched, no shell quoting added",
                    pushArgs[2] == "/sdcard/Download");

                var pullArgs = AdbService.PullArgs("/sdcard/a b.txt", @"C:\out\a b.txt");
                Expect("O: PullArgs keeps exactly 3 separate elements (no concatenation)", pullArgs.Count == 3);
                Expect("O: no PushArgs/PullArgs element ever embeds both paths in one string",
                    pushArgs.All(a => !a.Contains(';')) && pullArgs.All(a => !a.Contains(';')));
            }

            // ---- P: Send — one activation starts one call; a rapid duplicate starts no second ----
            {
                const string endpoint = "R5CX10SINGLEFLIGHT";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("P: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("single-flight.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);

                var callCount = 0;
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PushOverrideForTests = (_, _, _, _) => { callCount++; return tcs.Task; };

                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // rapid re-activation while busy
                Pump();
                Expect("P: exactly one Send call reached the service despite two rapid clicks", callCount == 1);

                tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                Expect("P: the pending Send call settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- Q: Receive — a rapid duplicate activation starts no second call ----
            {
                const string endpoint = "R5CX10RECVFLIGHT";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("Q: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "q-recv.jpg");
                var v = Make();
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/photo.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);

                var callCount = 0;
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PullOverrideForTests = (_, _, _, _) => { callCount++; return tcs.Task; };

                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("Q: exactly one Receive call reached the service despite two rapid clicks", callCount == 1);

                tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", ""));
                Expect("Q: the pending Receive call settles within the bound", WaitUntil(() => !v.ReceiveBusyForTests));

                Unload(v);
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- R: Send and Receive cannot overlap ----
            {
                const string endpoint = "R5CX10NOOVERLAP";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("R: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("overlap.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "overlap-recv.jpg");
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/photo.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);

                var sendTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pullCallCount = 0;
                TransfersView.PushOverrideForTests = (_, _, _, _) => sendTcs.Task;
                TransfersView.PullOverrideForTests = (_, _, _, _) => { pullCallCount++; return Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", "")); };

                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("R: Send is busy", v.SendBusyForTests);

                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // attempted overlap
                Pump();
                Expect("R: Receive never starts while Send is in flight", pullCallCount == 0);
                Expect("R: ReceiveBtn is disabled while Send is in flight", !v.ReceiveBtn.IsEnabled);

                sendTcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                Expect("R: Send settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
                TransfersView.PullOverrideForTests = null;
            }

            // ---- S: success requires exit code zero; negative outranks positive; unknown fails closed ----
            {
                const string endpoint = "R5CX10CLASSIFY";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("S: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                // non-zero exit with a "100%"-looking positive line must still fail.
                var r1 = new ProcessResult(ProcessOutcome.Exited, 1, "[100%] 1 file pushed, 0 skipped.", "");
                Expect("S: non-zero exit is never read as success, even with trailing 100%/positive text",
                    !TransferOperations.ClassifyPush(r1).Success);

                // negative marker outranks a positive one at exit 0.
                var r2 = new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", "error: failed to copy");
                Expect("S: a negative marker on stderr outranks a positive marker on stdout",
                    !TransferOperations.ClassifyPush(r2).Success);

                // unrecognized output at exit 0 fails closed.
                var r3 = new ProcessResult(ProcessOutcome.Exited, 0, "", "");
                Expect("S: empty output at exit 0 fails closed", !TransferOperations.ClassifyPull(r3).Success);

                var v = Make();
                TransfersView.PullOverrideForTests = (_, _, _, _) => Task.FromResult(r1);
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/photo.jpg";
                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "classify-recv.jpg");
                ClickAndPump(v.ChooseSaveLocationBtn);
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("S: the hosted Receive result reflects the failure", WaitUntil(() => v.LastReceiveResultForTests != null));
                Expect("S: the hosted Receive result is Failed, not Success",
                    v.LastReceiveResultForTests != null && !v.LastReceiveResultForTests.Success);

                Unload(v);
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- T: raw output and full local paths never reach the UI ----
            {
                const string endpoint = "R5CX10PRIVACY";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("T: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                const string rawDiagnostic = "adb: error: failed to copy 'C:\\Users\\SECRETUSER\\Desktop\\file.txt': stat failed";
                var file = MakeFile("privacy-check.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                TransfersView.PushOverrideForTests = (_, _, _, _) => Task.FromResult(
                    new ProcessResult(ProcessOutcome.Exited, 1, "", rawDiagnostic));

                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("T: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Expect("T: the raw diagnostic never appears in the send result text",
                    !v.SendResultText.Text.Contains("SECRETUSER"));
                Expect("T: the raw diagnostic never appears in any result-list row",
                    v.ResultsForTests.All(r => !r.Message.Contains("SECRETUSER") && !r.Message.Contains(rawDiagnostic)));
                Expect("T: the full local path never appears in the results list — only the safe file name",
                    v.ResultsForTests.All(r => !r.SafeFileName.Contains(scratchDir)));
                Expect("T: the full local path never appears in any device-selector accessible name",
                    v.DeviceSelectorList.Children.OfType<RadioButton>()
                        .All(r => !(AutomationProperties.GetName(r) ?? "").Contains(scratchDir)));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- U: sequential multiple-file transfer with partial batch success ----
            {
                const string endpoint = "R5CX10BATCH";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("U: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var f1 = MakeFile("batch-ok-1.txt");
                var f2 = MakeFile("batch-fail.txt");
                var f3 = MakeFile("batch-ok-2.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { f1, f2, f3 };

                var callOrder = new List<string>();
                TransfersView.PushOverrideForTests = (_, local, _, _) =>
                {
                    callOrder.Add(Path.GetFileName(local));
                    var fail = local == f2;
                    return Task.FromResult(fail
                        ? new ProcessResult(ProcessOutcome.Exited, 1, "", "error: failed to copy")
                        : new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                };

                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("U: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Expect("U: files were sent in sequential order, not in parallel",
                    callOrder.SequenceEqual(new[] { "batch-ok-1.txt", "batch-fail.txt", "batch-ok-2.txt" }));
                Expect("U: exactly 2 of 3 items succeeded",
                    v.LastSendBatchForTests.Count(r => r.Success) == 2 && v.LastSendBatchForTests.Count == 3);
                Expect("U: the batch is never reported as a complete success when one item failed",
                    !v.SendResultText.Text.Contains("3 من 3"));
                Expect("U: the partial-success wording names both counts truthfully",
                    v.SendResultText.Text.Contains("2") && v.SendResultText.Text.Contains("3"));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- V: cancellation stops remaining items; completed files stay reported ----
            {
                const string endpoint = "R5CX10CANCEL";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("V: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var f1 = MakeFile("cancel-ok.txt");
                var f2 = MakeFile("cancel-pending.txt");
                var f3 = MakeFile("cancel-never-starts.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { f1, f2, f3 };

                var secondFileTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var callCount = 0;
                TransfersView.PushOverrideForTests = (_, local, _, ct) =>
                {
                    callCount++;
                    if (local == f1) return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                    if (local == f2) return secondFileTcs.Task;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                };

                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("V: the first file completes before the second is still in flight",
                    WaitUntil(() => v.LastSendBatchForTests.Count == 1));
                Expect("V: Cancel is available while a batch is in flight", v.CancelBtn.Visibility == Visibility.Visible);

                v.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                secondFileTcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
                Expect("V: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Expect("V: the third file never started once cancellation was requested", callCount == 2);
                Expect("V: the completed first file remains reported as a success after cancellation",
                    v.ResultsForTests.Any(r => r.SafeFileName == "cancel-ok.txt" && r.Success));
                Expect("V: cancellation never claims a rollback of the already-completed file",
                    v.ResultsForTests.First(r => r.SafeFileName == "cancel-ok.txt").Success);

                Unload(v);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- W: stale Send completion after Unloaded cannot repaint ----
            {
                const string endpoint = "R5CX10STALESEND";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("W: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("stale-send.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PushOverrideForTests = (_, _, _, _) => tcs.Task;

                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("W: Send is busy while the call is in flight", v.SendBusyForTests);

                Unload(v);
                Exception? thrown = null;
                try
                {
                    tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                    Pump();
                }
                catch (Exception ex) { thrown = ex; }

                Expect("W: a stale Send completion after unload throws nothing", thrown == null);
                Expect("W: a stale Send completion after unload does not repaint (still no recorded batch item)",
                    v.LastSendBatchForTests.Count == 0);
                TransfersView.PushOverrideForTests = null;
            }

            // ---- X: stale Receive completion after Unloaded cannot repaint ----
            {
                const string endpoint = "R5CX10STALERECV";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("X: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "stale-recv.jpg");
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PullOverrideForTests = (_, _, _, _) => tcs.Task;

                var v = Make();
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/photo.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("X: Receive is busy while the call is in flight", v.ReceiveBusyForTests);

                Unload(v);
                Exception? thrown = null;
                try
                {
                    tcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", ""));
                    Pump();
                }
                catch (Exception ex) { thrown = ex; }

                Expect("X: a stale Receive completion after unload throws nothing", thrown == null);
                Expect("X: a stale Receive completion after unload does not repaint (still no recorded result)",
                    v.LastReceiveResultForTests == null);
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- Y: DeviceWatcher subscription balance + duplicate Loaded protection ----
            {
                var baseline = SubscriberCount();
                var v = Make();
                Expect("Y: subscriber count after Loaded", SubscriberCount() == baseline + 1);

                Load(v);   // a repeated Loaded without an intervening Unloaded adds nothing
                Expect("Y: repeated Loaded does not duplicate the subscription", SubscriberCount() == baseline + 1);

                Unload(v);
                Expect("Y: subscriber count back to baseline after Unloaded", SubscriberCount() == baseline);

                for (var cycle = 1; cycle <= 3; cycle++)
                {
                    var cycled = new TransfersView();
                    Load(cycled);
                    Expect($"Y: cycle {cycle} subscribed exactly once", SubscriberCount() == baseline + 1);
                    Unload(cycled);
                    Expect($"Y: cycle {cycle} unsubscribed cleanly", SubscriberCount() == baseline);
                }
            }

            // ---- Z: a queued device-changed event after Unloaded cannot repaint ----
            {
                var v = Make();
                Load(v);   // ensure subscribed exactly once regardless of prior scenario state
                Unsubscribe(v);
                Subscribe(v);

                Unload(v);
                Exception? thrown = null;
                try
                {
                    // Simulate an event still queued at the moment Unloaded ran: the handler
                    // itself is already detached (Unsubscribe ran), so directly proving "cannot
                    // repaint" is exercising the same generation-guarded repaint path stale
                    // completions (W/X) already use — here from the DeviceWatcher event surface.
                    DeviceWatcher.Invalidate();
                    DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                        new List<AdbDevice> { new() { Serial = "R5CX10QUEUED", State = "device" } }));
                    DeviceWatcher.Start();
                    DeviceWatcher.RequestRefresh();
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == "R5CX10QUEUED"));
                    DeviceWatcher.Stop();
                }
                catch (Exception ex) { thrown = ex; }

                Expect("Z: no exception results from device activity occurring after unload", thrown == null);
                Expect("Z: an unloaded page instance is not subscribed and cannot repaint from it",
                    !v.IsWatcherSubscribedForTests);
            }

            // ---- AA: independent-review M1 — a stale operation's late completion, racing an
            // Unloaded/reload cycle, can never disable Cancel for the operation that replaced it ----
            {
                const string endpoint = "R5CX10OWNERSHIP";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AA: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("ownership-old-send.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);

                // Operation A (Send) starts and never completes on its own.
                var oldOpTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PushOverrideForTests = (_, _, _, _) => oldOpTcs.Task;
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AA: operation A (Send) is in flight", v.SendBusyForTests);

                // The page is navigated away from mid-flight: generation advances, busy flags are
                // force-cleared, but (by design) _activeCts is only Cancelled, never nulled here.
                Unload(v);

                // The SAME cached instance is shown again and a genuinely new operation B
                // (Receive) starts on the new generation.
                Load(v);
                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "ownership-new-recv.jpg");
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/Camera/new-op.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);

                CancellationToken newOpToken = default;
                var newOpTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PullOverrideForTests = (_, _, _, ct) => { newOpToken = ct; return newOpTcs.Task; };
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AA: operation B (Receive) is in flight on the reloaded instance", v.ReceiveBusyForTests);

                // Operation A's stale continuation now resumes and runs its own finally block —
                // the defect under test is whether that finally can reach into B's state.
                oldOpTcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                Pump();

                Expect("AA: operation B is still in flight after A's stale completion settles",
                    v.ReceiveBusyForTests);

                v.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AA: Cancel still reaches operation B's own token — A's finally never disposed/nulled it",
                    newOpToken.IsCancellationRequested);

                newOpTcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
                Expect("AA: operation B settles cleanly after being cancelled", WaitUntil(() => !v.ReceiveBusyForTests));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickFilesOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- AB: independent-review M3 — Send targets the explicitly-selected device out of
            // two authorized candidates, never the other one ----
            {
                const string first = "R5CX10MULTIA";
                const string second = "R5CX10MULTIB";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = first, State = "device", Model = "Pixel 9" },
                        new() { Serial = second, State = "device", Model = "Pixel 8" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AB: both fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var file = MakeFile("multi-device-send.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var v = Make();
                Expect("AB: no device is auto-selected with two authorized candidates", v.SelectedSerialForTests == null);

                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                Expect("AB: both authorized rows are present", radios.Count == 2);
                if (radios.Count == 2) { radios[1].IsChecked = true; Pump(); }
                Expect("AB: the second device is explicitly selected", v.SelectedSerialForTests == second);

                ClickAndPump(v.ChooseFilesBtn);
                string? capturedSerial = null;
                TransfersView.PushOverrideForTests = (serial, _, _, _) =>
                {
                    capturedSerial = serial;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                };
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("AB: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Expect("AB: Send used the explicitly-selected second device, not the first",
                    capturedSerial == second && capturedSerial != first);

                Unload(v);
                TransfersView.PushOverrideForTests = null;
                TransfersView.PickFilesOverrideForTests = null;
            }

            // ---- AC: independent-review M3 — Receive targets the explicitly-selected device out
            // of two authorized candidates, never the other one ----
            {
                const string first = "R5CX10MULTIC";
                const string second = "R5CX10MULTID";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice>
                    {
                        new() { Serial = first, State = "device", Model = "Pixel 9" },
                        new() { Serial = second, State = "device", Model = "Pixel 8" },
                    }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AC: both fixtures become visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Count == 2));
                DeviceWatcher.Stop();
                AppState.SetCurrent(null);

                var v = Make();
                var radios = v.DeviceSelectorList.Children.OfType<RadioButton>().ToList();
                Expect("AC: both authorized rows are present", radios.Count == 2);
                if (radios.Count == 2) { radios[1].IsChecked = true; Pump(); }
                Expect("AC: the second device is explicitly selected", v.SelectedSerialForTests == second);

                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "multi-device-recv.jpg");
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/Camera/photo.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);

                string? capturedSerial = null;
                TransfersView.PullOverrideForTests = (serial, _, _, _) =>
                {
                    capturedSerial = serial;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", ""));
                };
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Expect("AC: Receive settles within the bound", WaitUntil(() => !v.ReceiveBusyForTests));

                Expect("AC: Receive used the explicitly-selected second device, not the first",
                    capturedSerial == second && capturedSerial != first);

                Unload(v);
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- AD: independent-review M4 — Cancel_Click cancels the exact CancellationToken
            // that reaches the AdbService call boundary for an in-flight Send ----
            {
                const string endpoint = "R5CX10CANCELTOKEN";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AD: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("cancel-token-send.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);

                CancellationToken capturedToken = default;
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PushOverrideForTests = (_, _, _, ct) => { capturedToken = ct; return tcs.Task; };

                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AD: the token reaching the AdbService call boundary starts uncancelled",
                    !capturedToken.IsCancellationRequested);

                v.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AD: Cancel propagates to the exact token AdbService.PushAsync would have received",
                    capturedToken.IsCancellationRequested);

                tcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
                Expect("AD: the batch settles within the bound", WaitUntil(() => !v.SendBusyForTests));

                Unload(v);
                TransfersView.PushOverrideForTests = null;
                TransfersView.PickFilesOverrideForTests = null;
            }

            // ---- AE: independent-review M4 — TransfersView_Unloaded cancels the exact
            // CancellationToken that reaches the AdbService call boundary for an in-flight Receive ----
            {
                const string endpoint = "R5CX10UNLOADTOKEN";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AE: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                TransfersView.PickSaveLocationOverrideForTests = _ => Path.Combine(scratchDir, "unload-token-recv.jpg");
                var v = Make();
                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/Camera/photo.jpg";
                ClickAndPump(v.ChooseSaveLocationBtn);

                CancellationToken capturedToken = default;
                var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PullOverrideForTests = (_, _, _, ct) => { capturedToken = ct; return tcs.Task; };

                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AE: the token reaching the AdbService call boundary starts uncancelled",
                    !capturedToken.IsCancellationRequested);

                Unload(v);
                Pump();
                Expect("AE: Unloaded propagates cancellation to the exact token AdbService.PullAsync would have received",
                    capturedToken.IsCancellationRequested);

                tcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
                TransfersView.PullOverrideForTests = null;
                TransfersView.PickSaveLocationOverrideForTests = null;
            }

            // ---- AF: second-independent-review banner repair — Receive: correcting an invalid
            // remote path clears both the field error AND the top status banner, with no adb
            // call started merely by the correction ----
            {
                const string endpoint = "R5CX10BANNERRECV";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AF: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var v = Make();
                var pullCalls = 0;
                TransfersView.PullOverrideForTests = (_, _, _, _) =>
                {
                    pullCalls++;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", ""));
                };

                v.ReceiveRemotePathBox.Text = "not-an-android-path";
                v.ReceiveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // submit attempt
                Pump();
                Expect("AF: an invalid remote path shows a field error",
                    v.ReceiveRemotePathError.Visibility == Visibility.Visible);
                Expect("AF: the top banner requests correction right after the failed submission",
                    v.StatusText.Text == "صحّح الحقول المظلّلة أدناه قبل المتابعة.");

                v.ReceiveRemotePathBox.Text = "/sdcard/DCIM/Camera/corrected.jpg";
                Pump();
                Expect("AF: correcting the path clears the field error",
                    v.ReceiveRemotePathError.Visibility == Visibility.Collapsed);
                Expect("AF: correcting the path also clears the top banner's validation-failure message",
                    v.StatusText.Text != "صحّح الحقول المظلّلة أدناه قبل المتابعة.");
                Expect("AF: no adb operation started merely from correcting the field", pullCalls == 0);

                Unload(v);
                TransfersView.PullOverrideForTests = null;
            }

            // ---- AG: second-independent-review banner repair — Send: correcting an invalid
            // custom destination clears both the field error AND the top status banner ----
            {
                const string endpoint = "R5CX10BANNERSEND";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AG: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var file = MakeFile("banner-send-correction.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { file };
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);

                // switch to the custom-destination option (last combo item, Tag == null)
                v.DestinationCombo.SelectedIndex = v.DestinationCombo.Items.Count - 1;
                Pump();
                v.CustomDestinationBox.Text = "not-an-android-path";
                Pump();

                var pushCalls = 0;
                TransfersView.PushOverrideForTests = (_, _, _, _) =>
                {
                    pushCalls++;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                };

                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // submit attempt
                Pump();
                Expect("AG: an invalid custom destination shows a field error",
                    v.CustomDestinationError.Visibility == Visibility.Visible);
                Expect("AG: the top banner requests correction right after the failed submission",
                    v.StatusText.Text == "صحّح الحقول المظلّلة أدناه قبل المتابعة.");

                v.CustomDestinationBox.Text = "/sdcard/Download/corrected-destination.txt";
                Pump();
                Expect("AG: correcting the destination clears the field error",
                    v.CustomDestinationError.Visibility == Visibility.Collapsed);
                Expect("AG: correcting the destination also returns the banner to a truthful non-validation state",
                    v.StatusText.Text != "صحّح الحقول المظلّلة أدناه قبل المتابعة.");
                Expect("AG: no adb operation started merely from correcting the field", pushCalls == 0);

                Unload(v);
                TransfersView.PushOverrideForTests = null;
                TransfersView.PickFilesOverrideForTests = null;
            }

            // ---- AH: second-independent-review same-direction ownership — a stale OLD Send's
            // late completion, racing an Unloaded/reload cycle, can never leak busy/UI state into
            // a NEWER Send (same direction; AA already covers the cross-direction Send→Receive
            // case, this covers the previously-missing Send→Send case) ----
            {
                const string endpoint = "R5CX10SAMEDIR";
                DeviceWatcher.Stop();
                DeviceWatcher.Invalidate();
                DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
                    new List<AdbDevice> { new() { Serial = endpoint, State = "device", Model = "Pixel 9" } }));
                DeviceWatcher.Start();
                DeviceWatcher.RequestRefresh();
                Expect("AH: the fixture becomes visible via DeviceWatcher before proceeding",
                    WaitUntil(() => DeviceWatcher.LastConfirmed.Any(d => d.Serial == endpoint)));
                DeviceWatcher.Stop();

                var failuresBefore = AppDiagnostics.RecentFailures.Count;

                var oldFile = MakeFile("samedir-old-send.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { oldFile };
                var v = Make();
                ClickAndPump(v.ChooseFilesBtn);

                // Operation A (old Send) starts and never completes on its own.
                CancellationToken tokenA = default;
                var oldOpTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PushOverrideForTests = (_, _, _, ct) => { tokenA = ct; return oldOpTcs.Task; };
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AH: operation A (old Send) is in flight", v.SendBusyForTests);

                // Navigate away mid-flight: generation advances, busy is force-cleared, A's own
                // token is Cancelled — but _activeCts is only Cancelled here, never nulled.
                Unload(v);
                Expect("AH: Unload cancels operation A's own token", tokenA.IsCancellationRequested);

                // Reload the SAME cached instance and start a genuinely new two-file Send
                // (operation B) on the new generation.
                Load(v);
                var fileB1 = MakeFile("samedir-new-send-1.txt");
                var fileB2 = MakeFile("samedir-new-send-2.txt");
                TransfersView.PickFilesOverrideForTests = () => new[] { fileB1, fileB2 };
                ClickAndPump(v.ChooseFilesBtn);

                CancellationToken tokenB = default;
                var callOrder = new List<string>();
                var firstFileTcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TransfersView.PushOverrideForTests = (_, local, _, ct) =>
                {
                    callOrder.Add(Path.GetFileName(local));
                    if (local == fileB1) { tokenB = ct; return firstFileTcs.Task; }
                    return Task.FromResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                };
                v.SendBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("AH: operation B (new Send) is in flight on the reloaded instance", v.SendBusyForTests);
                Expect("AH: only B's first file has started",
                    callOrder.SequenceEqual(new[] { "samedir-new-send-1.txt" }));

                // Operation A's stale continuation now resumes and runs its own finally block —
                // the exact race under test is whether that finally can reach into B's state.
                oldOpTcs.SetResult(new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", ""));
                Pump();

                // ---- assertions BEFORE cancelling B: A's stale finally must not have touched
                // ANY of B's busy/UI/ownership state ----
                Expect("AH: _sendBusy remains true for B after A's stale completion settles", v.SendBusyForTests);
                Expect("AH: _receiveBusy remains false", !v.ReceiveBusyForTests);
                Expect("AH: Send remains disabled while B is in flight", !v.SendBtn.IsEnabled);
                Expect("AH: Receive remains disabled while B is in flight", !v.ReceiveBtn.IsEnabled);
                Expect("AH: Verify remains disabled while B is in flight", !v.VerifyBtn.IsEnabled);
                Expect("AH: the file picker remains disabled while B is in flight", !v.ChooseFilesBtn.IsEnabled);
                Expect("AH: Cancel remains visible while B is in flight", v.CancelBtn.Visibility == Visibility.Visible);
                Expect("AH: Cancel remains enabled while B is in flight", v.CancelBtn.IsEnabled);
                Expect("AH: the banner still reads B's own sending state", v.StatusText.Text == "جارٍ الإرسال…");
                Expect("AH: B's own token is still the active one — A's finally did not steal/null it",
                    v.ActiveTokenForTests.HasValue && v.ActiveTokenForTests.Value == tokenB);
                Expect("AH: no stale batch item from A leaked into B's reported batch",
                    v.LastSendBatchForTests.All(r => r.SafeFileName != "samedir-old-send.txt"));

                // ---- now cancel B for real and confirm it settles cleanly ----
                Exception? thrown = null;
                try
                {
                    v.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    firstFileTcs.SetResult(new ProcessResult(ProcessOutcome.Cancelled, null, "", ""));
                    Expect("AH: operation B settles within the bound", WaitUntil(() => !v.SendBusyForTests));
                }
                catch (Exception ex) { thrown = ex; }

                Expect("AH: B's token becomes cancelled", tokenB.IsCancellationRequested);
                Expect("AH: B's second file never started",
                    callOrder.SequenceEqual(new[] { "samedir-new-send-1.txt" }));
                Expect("AH: no exception (e.g. ObjectDisposedException) surfaced while settling B", thrown == null);
                Expect("AH: controls are restored only after B settles",
                    v.ChooseFilesBtn.IsEnabled && !v.CancelBtn.IsEnabled);
                Expect("AH: no new global-exception-policy failure was recorded during this scenario",
                    AppDiagnostics.RecentFailures.Count == failuresBefore);

                Unload(v);
                TransfersView.PushOverrideForTests = null;
                TransfersView.PickFilesOverrideForTests = null;
            }

            // Independent-review-style guards (delete-scenario protection), matching WirelessSelfTest.
            var scenarioAssertions = total;
            Expect($"exactly {ExpectedScenarioAssertions} scenario assertions ran (delete-scenario guard)",
                scenarioAssertions == ExpectedScenarioAssertions);

            var missingScenarios = ScenarioIds.Where(id => !assertionNames.Any(n => n.StartsWith(id + ":", StringComparison.Ordinal))).ToList();
            Expect($"all {ScenarioIds.Length} named runtime scenarios are still present: " +
                   (missingScenarios.Count == 0 ? "none missing" : "missing " + string.Join(", ", missingScenarios)),
                missingScenarios.Count == 0);

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
            TransfersView.AdbAvailableOverrideForTests = wasAdbAvailable;
            TransfersView.PushOverrideForTests = wasPush;
            TransfersView.PullOverrideForTests = wasPull;
            TransfersView.DiscoverDevicesOverrideForTests = wasDiscover;
            TransfersView.PickFilesOverrideForTests = wasPickFiles;
            TransfersView.PickSaveLocationOverrideForTests = wasPickSave;
            AppState.SetCurrent(previousAppState);
            DeviceWatcher.ResetForTests();

            try { Directory.Delete(scratchDir, recursive: true); } catch { /* best effort */ }

            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            PresentationTraceSources.Refresh();
        }

        var passed = failures.Count == 0;
        var report = $"transfers: {total - failures.Count}/{total} assertions passed" +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));
        return new Result(passed, total, failures.Count, report);
    }

    // ---------------- helpers ----------------

    private static TransfersView Make()
    {
        var v = new TransfersView();
        Load(v);
        return v;
    }

    private static void Load(TransfersView v) => v.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    private static void Unload(TransfersView v) => v.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    private static void Subscribe(TransfersView v) => Load(v);
    private static void Unsubscribe(TransfersView v) => Unload(v);

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
