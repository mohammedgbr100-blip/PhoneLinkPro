using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 7 — DeviceWatcher pure logic: the change signature and the current-device
/// selection rules. DispatcherTimer cadence is deliberately NOT tested.
/// </summary>
public class DeviceWatcherLogicTests
{
    private static AdbDevice Dev(string serial, string state = "device", string model = "")
        => new() { Serial = serial, State = state, Model = model };

    // ---------------------------------------------------------------- signature

    [Fact] // [REGRESSION]
    public void Signature_EncodesSerialAndStatePairs()
    {
        var sig = DeviceWatcher.Signature([Dev("A"), Dev("B", "offline")]);

        Assert.Equal("A:device|B:offline", sig);
    }

    [Fact] // [REGRESSION]
    public void Signature_OfEmptyList_IsEmptyString()
        => Assert.Equal("", DeviceWatcher.Signature([]));

    [Fact] // [REGRESSION] — identical device sets de-duplicate
    public void Signature_IsStableForTheSameDevices()
    {
        var a = DeviceWatcher.Signature([Dev("A"), Dev("B")]);
        var b = DeviceWatcher.Signature([Dev("A"), Dev("B")]);

        Assert.Equal(a, b);
    }

    [Fact] // [REGRESSION] — a state transition changes the signature
    public void Signature_ChangesWhenAStateChanges()
    {
        var before = DeviceWatcher.Signature([Dev("A", "unauthorized")]);
        var after = DeviceWatcher.Signature([Dev("A", "device")]);

        Assert.NotEqual(before, after);
    }

    [Fact] // [REGRESSION] — additions and removals change the signature
    public void Signature_ChangesOnAdditionAndRemoval()
    {
        var one = DeviceWatcher.Signature([Dev("A")]);
        var two = DeviceWatcher.Signature([Dev("A"), Dev("B")]);

        Assert.NotEqual(one, two);
        Assert.Equal(one, DeviceWatcher.Signature([Dev("A")]));
    }

    [Fact] // [CHARACTERIZATION] — signature is order-sensitive, so a reordered but
           // otherwise identical device list re-fires DevicesChanged.
    public void Signature_IsOrderSensitive()
    {
        var ab = DeviceWatcher.Signature([Dev("A"), Dev("B")]);
        var ba = DeviceWatcher.Signature([Dev("B"), Dev("A")]);

        Assert.NotEqual(ab, ba);
    }

    [Fact] // [CHARACTERIZATION] — model/battery/version changes do NOT change the
           // signature, so enrichment-only updates never reach the UI via this path.
    public void Signature_IgnoresEverythingExceptSerialAndState()
    {
        var lean = DeviceWatcher.Signature([Dev("A")]);
        var rich = DeviceWatcher.Signature([Dev("A", "device", "Galaxy A52")]);

        Assert.Equal(lean, rich);
    }

    // ================================================================
    //  DEV-01 / DEV-02 — poll-loop weaknesses
    // ================================================================
    // The brief attributes two DeviceWatcher defects to this area:
    //   * a failed/empty poll is treated as "no devices attached";
    //   * a change can be missed while a previous poll is still running.
    // Both are characterized below. They are NOT fixed in Batch 1A.

    [Fact] // [CHARACTERIZATION] DEV-01/DEV-02 — an empty result is indistinguishable
           // from a genuine "nothing attached" result.
    public void DEV01_EmptyPollResult_LooksIdenticalToNoDevicesAttached()
    {
        // AdbService.GetDevicesAsync returns an empty list both when adb reports no
        // devices AND when adb produced no parsable stdout (tool missing, server dead,
        // command timed out — RunAsync swallows all of these into an empty StdOut).
        var genuinelyEmpty = AdbService.ParseDeviceList("List of devices attached\n");
        var failedCall = AdbService.ParseDeviceList("");   // what a timeout/crash yields

        Assert.Empty(genuinelyEmpty);
        Assert.Empty(failedCall);
        Assert.Equal(DeviceWatcher.Signature(genuinelyEmpty), DeviceWatcher.Signature(failedCall));

        // Consequence: a transient adb failure reports "device disconnected" to the user.
        var (action, _) = DeviceWatcher.DecideCurrentDevice(Dev("A"), failedCall);
        Assert.Equal(DeviceWatcher.CurrentDeviceAction.Disconnect, action);
    }

    [Fact(Skip = "DEV-01: a failed or timed-out adb call is parsed as an empty device list " +
                 "and reported to the user as a disconnection. Asserts the CORRECT post-fix " +
                 "behaviour (poll failure must be distinguishable from an empty result and " +
                 "must not clear the current device). Target batch: 1D.")]
    public void DEV01_PollFailureMustNotBeTreatedAsDisconnection_TargetBatch1D()
    {
        // After the fix, a failed poll must not produce a Disconnect decision.
        var failed = AdbService.ParseDeviceList("");

        var (action, _) = DeviceWatcher.DecideCurrentDevice(Dev("A"), failed);

        Assert.NotEqual(DeviceWatcher.CurrentDeviceAction.Disconnect, action);
    }

    [Fact(Skip = "DEV-02: PollAsync returns immediately when a previous poll is still in " +
                 "flight (_polling guard) and no re-poll is queued, so a device change that " +
                 "lands during a slow poll is not observed until the next 4s tick — or is " +
                 "lost entirely if the signature has already advanced. Verifying this needs " +
                 "the poll loop to be injectable, which is a Batch 1D change. Target batch: 1D.")]
    public void DEV02_ChangeArrivingDuringAnInFlightPollMustNotBeLost_TargetBatch1D()
    {
        // Requires an injectable poll source; DeviceWatcher currently calls the static
        // AdbService directly and drives itself from a DispatcherTimer.
        Assert.Fail("Not implementable until the poll loop is decoupled in Batch 1D.");
    }

    // ---------------------------------------------------------------- selection rules

    [Fact] // [REGRESSION] — first authorized device is auto-selected when none is current
    public void NoCurrentDevice_SelectsFirstAuthorized()
    {
        var devices = new List<AdbDevice> { Dev("A", "unauthorized"), Dev("B"), Dev("C") };

        var (action, target) = DeviceWatcher.DecideCurrentDevice(null, devices);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.SelectFirst, action);
        Assert.Equal("B", target!.Serial);
    }

    [Fact] // [REGRESSION] — nothing authorized means nothing selected
    public void NoCurrentDevice_AndNoneAuthorized_DoesNothing()
    {
        var devices = new List<AdbDevice> { Dev("A", "unauthorized"), Dev("B", "offline") };

        var (action, target) = DeviceWatcher.DecideCurrentDevice(null, devices);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.None, action);
        Assert.Null(target);
    }

    [Fact] // [REGRESSION]
    public void NoCurrentDevice_AndEmptyList_DoesNothing()
    {
        var (action, _) = DeviceWatcher.DecideCurrentDevice(null, []);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.None, action);
    }

    [Fact] // [REGRESSION] — the current device vanishing is a disconnection
    public void CurrentDeviceRemoved_ReportsDisconnect()
    {
        var (action, target) = DeviceWatcher.DecideCurrentDevice(Dev("A"), [Dev("B")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.Disconnect, action);
        Assert.Null(target);
    }

    [Fact] // [REGRESSION] — a state change on the current device is propagated
    public void CurrentDeviceStateChanged_ReportsUpdateState()
    {
        var current = Dev("A", "device");

        var (action, target) = DeviceWatcher.DecideCurrentDevice(current, [Dev("A", "offline")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.UpdateState, action);
        Assert.Equal("offline", target!.State);
    }

    [Fact] // [REGRESSION] — an unchanged current device produces no action
    public void CurrentDeviceUnchanged_DoesNothing()
    {
        var (action, _) = DeviceWatcher.DecideCurrentDevice(Dev("A"), [Dev("A")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.None, action);
    }

    [Fact] // [CHARACTERIZATION] — the current device is kept even after it becomes
           // unauthorized/offline; only removal clears it.
    public void CurrentDeviceGoingUnauthorized_IsKeptNotCleared()
    {
        var (action, target) = DeviceWatcher.DecideCurrentDevice(Dev("A"), [Dev("A", "unauthorized")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.UpdateState, action);
        Assert.Equal("unauthorized", target!.State);
    }

    [Fact] // [CHARACTERIZATION] — selection matches on serial only, so a wireless device
           // that reconnects on a different port is treated as a different device.
    public void WirelessDeviceOnANewPort_IsTreatedAsADifferentDevice()
    {
        var current = Dev("192.168.1.42:5555");

        var (action, _) = DeviceWatcher.DecideCurrentDevice(current, [Dev("192.168.1.42:41237")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.Disconnect, action);
    }

    [Fact] // [CHARACTERIZATION] DEV-03 knock-on — phantom banner devices are not
           // authorized, so they never become the selected device.
    public void DEV03_PhantomBannerDevices_AreNeverAutoSelected()
    {
        var devices = AdbService.ParseDeviceList(
            "* daemon not running; starting now at tcp:5037\n" +
            "* daemon started successfully\n" +
            "List of devices attached\n" +
            "R58M12ABCDE  device\n");

        var (action, target) = DeviceWatcher.DecideCurrentDevice(null, devices);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.SelectFirst, action);
        Assert.Equal("R58M12ABCDE", target!.Serial);
    }
}
