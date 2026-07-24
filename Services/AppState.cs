using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>Shared, app-wide state: the currently selected device.</summary>
public static class AppState
{
    public static AdbDevice? CurrentDevice { get; set; }

    public static string? CurrentSerial => CurrentDevice?.Serial;

    /// <summary>Raised whenever the selected device changes, so views can refresh.</summary>
    public static event Action? CurrentDeviceChanged;

    public static void SetCurrent(AdbDevice? dev)
    {
        CurrentDevice = dev;
        CurrentDeviceChanged?.Invoke();
    }
}
