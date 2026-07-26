using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>
/// Truthful, derived-only progress state for the first-run device-setup wizard (Batch 4B).
/// Every value here is computed from data the app already has (<see cref="ToolLocator"/>,
/// <see cref="AdbDevice"/> snapshots from <see cref="AdbService"/>/<see cref="DeviceWatcher"/>)
/// — this type introduces no new discovery, no new adb call and no polling of its own.
/// </summary>
public enum DeviceSetupStage
{
    /// <summary>The bundled engine/bridge tools are missing; the phone cannot be inspected
    /// at all, and USB debugging must not be blamed for a problem that is local to this PC.</summary>
    ToolsUnavailable,

    /// <summary>Tools are available but no device is visible to adb at all.</summary>
    NoDevice,

    /// <summary>A device is visible but the user has not approved this computer on the phone yet.</summary>
    Unauthorized,

    /// <summary>A device was seen but is not currently usable (adb reports it offline, or in a
    /// state this app does not treat as ready). Never presented as authorized or successful.</summary>
    Offline,

    /// <summary>A device is authorized and ready to mirror.</summary>
    Ready,
}

/// <summary>Pure evaluation rules shared by the wizard window and its tests.</summary>
public static class DeviceSetupEvaluator
{
    /// <summary>
    /// Computes the truthful stage for one focused device (or the absence of one). Never
    /// infers progress the data does not support: an authorized device is Ready, an
    /// "unauthorized" adb state is Unauthorized, and every other known adb state (offline,
    /// bootloader, recovery, sideload, host, authorizing, connecting, rescue, unknown, "no")
    /// is reported as Offline — "seen, but not usable right now" — rather than guessing.
    /// </summary>
    public static DeviceSetupStage Evaluate(bool toolsAvailable, AdbDevice? focused)
    {
        if (!toolsAvailable) return DeviceSetupStage.ToolsUnavailable;
        if (focused == null) return DeviceSetupStage.NoDevice;
        if (focused.IsAuthorized) return DeviceSetupStage.Ready;
        if (focused.State == "unauthorized") return DeviceSetupStage.Unauthorized;
        return DeviceSetupStage.Offline;
    }

    /// <summary>
    /// Chooses which device the wizard focuses on absent an explicit user pick: the same
    /// "prefer authorized, else the first" rule <see cref="DeviceWatcher"/> and
    /// <see cref="ScrcpyDeck.Views.DashboardView"/> already use for hero selection, so the
    /// wizard never disagrees with the rest of the app about which device is "current".
    /// </summary>
    public static AdbDevice? PreferredDevice(IReadOnlyList<AdbDevice> devices, string? preferredSerial)
    {
        if (devices.Count == 0) return null;
        if (preferredSerial != null)
        {
            var match = devices.FirstOrDefault(d => d.Serial == preferredSerial);
            if (match != null) return match;
        }
        return devices.FirstOrDefault(d => d.IsAuthorized) ?? devices[0];
    }
}
