using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>How a device-discovery query ended.</summary>
public enum DeviceDiscoveryOutcome
{
    /// <summary>adb answered. The device list is authoritative — including when it is empty.</summary>
    Success,
    /// <summary>adb did not answer in time. Device state is UNKNOWN.</summary>
    TimedOut,
    /// <summary>The caller cancelled (normally an orderly shutdown). State is UNKNOWN.</summary>
    Cancelled,
    /// <summary>adb could not be launched at all. State is UNKNOWN.</summary>
    StartFailed,
    /// <summary>adb ran but reported a non-zero exit code. State is UNKNOWN.</summary>
    AdbError,
    /// <summary>adb exited cleanly but its output could not be understood. State is UNKNOWN.</summary>
    Unusable,
}

/// <summary>
/// Immutable outcome of one device query.
/// <para>
/// The distinction that matters: <see cref="DeviceDiscoveryOutcome.Success"/> with zero
/// devices means the user genuinely has nothing plugged in, while every other outcome means
/// the device state is unknown and the last confirmed snapshot must be preserved. Collapsing
/// the two was DEV-01.
/// </para>
/// </summary>
public sealed record DeviceDiscoveryResult(
    DeviceDiscoveryOutcome Outcome,
    IReadOnlyList<AdbDevice> Devices,
    string? Diagnostic,
    DateTimeOffset Timestamp)
{
    /// <summary>True when the device list is authoritative.</summary>
    public bool IsSuccess => Outcome == DeviceDiscoveryOutcome.Success;

    /// <summary>True when device state could not be determined.</summary>
    public bool IsUnknown => !IsSuccess;

    public static DeviceDiscoveryResult Success(IReadOnlyList<AdbDevice> devices)
        => new(DeviceDiscoveryOutcome.Success, devices, null, DateTimeOffset.UtcNow);

    public static DeviceDiscoveryResult Failure(DeviceDiscoveryOutcome outcome, string? diagnostic)
        => new(outcome, Array.Empty<AdbDevice>(), diagnostic, DateTimeOffset.UtcNow);
}
