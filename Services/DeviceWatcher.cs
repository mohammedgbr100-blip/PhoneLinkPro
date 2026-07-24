using System.Windows.Threading;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>
/// Polls the device list in the background so the UI reacts to plug/unplug/authorize
/// without a manual refresh. Raises events on the UI thread and keeps AppState in sync.
/// </summary>
public static class DeviceWatcher
{
    /// <summary>Raised (UI thread) whenever the set of devices or a device state changes.</summary>
    public static event Action<List<AdbDevice>>? DevicesChanged;

    private static DispatcherTimer? _timer;
    private static string _lastSignature = "\0";
    private static bool _polling;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    /// <summary>Force the next poll to report even if nothing changed (used by manual refresh).</summary>
    public static void Invalidate() => _lastSignature = "\0";

    private static async Task PollAsync()
    {
        if (_polling || !ToolLocator.AdbAvailable) return;
        _polling = true;
        try
        {
            var devices = await AdbService.GetDevicesAsync();
            var sig = Signature(devices);
            if (sig == _lastSignature) return;
            _lastSignature = sig;

            SyncAppState(devices);
            DevicesChanged?.Invoke(devices);
        }
        catch (Exception ex)
        {
            Log.Warn("DeviceWatcher poll failed: " + ex.Message);
        }
        finally { _polling = false; }
    }

    /// <summary>
    /// The de-duplication key for a poll result. Identical signatures suppress the
    /// DevicesChanged event. Extracted verbatim for regression tests.
    /// </summary>
    internal static string Signature(List<AdbDevice> devices)
        => string.Join("|", devices.Select(d => $"{d.Serial}:{d.State}"));

    /// <summary>What a poll result implies for the currently selected device.</summary>
    internal enum CurrentDeviceAction { None, Disconnect, UpdateState, SelectFirst }

    /// <summary>
    /// Pure decision half of <see cref="SyncAppState"/>: works out what should happen to
    /// the current device without performing any state change or notification.
    /// Extracted so the selection rules are testable without a UI thread.
    /// </summary>
    internal static (CurrentDeviceAction Action, AdbDevice? Target) DecideCurrentDevice(
        AdbDevice? current, List<AdbDevice> devices)
    {
        if (current != null)
        {
            var match = devices.FirstOrDefault(d => d.Serial == current.Serial);
            if (match == null) return (CurrentDeviceAction.Disconnect, null);
            if (match.State != current.State) return (CurrentDeviceAction.UpdateState, match);
            return (CurrentDeviceAction.None, null);
        }

        var first = devices.FirstOrDefault(d => d.IsAuthorized);
        return first != null
            ? (CurrentDeviceAction.SelectFirst, first)
            : (CurrentDeviceAction.None, null);
    }

    private static void SyncAppState(List<AdbDevice> devices)
    {
        var cur = AppState.CurrentDevice;
        var (action, target) = DecideCurrentDevice(cur, devices);

        switch (action)
        {
            case CurrentDeviceAction.Disconnect:
                AppState.SetCurrent(null);
                Toast.Show($"انقطع الاتصال بالجهاز {cur!.DisplayName}", ok: false);
                break;

            case CurrentDeviceAction.UpdateState:
                cur!.State = target!.State;
                AppState.SetCurrent(cur);
                break;

            case CurrentDeviceAction.SelectFirst:
                AppState.SetCurrent(target);
                Toast.Show($"تم اكتشاف جهاز: {target!.DisplayName}");
                break;
        }
    }
}
