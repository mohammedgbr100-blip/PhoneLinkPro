using System.ComponentModel;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Models;

/// <summary>A connected Android device (USB or Wi-Fi) with live details.</summary>
public class AdbDevice : INotifyPropertyChanged
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";       // device / unauthorized / offline
    public string Model { get; set; } = "";
    public string Brand { get; set; } = "";
    public string AndroidVersion { get; set; } = "";
    public string Resolution { get; set; } = "";
    public int Battery { get; set; } = -1;

    private bool _detailsLoaded;
    public bool DetailsLoaded
    {
        get => _detailsLoaded;
        set { _detailsLoaded = value; OnChanged(nameof(DetailsLoaded)); }
    }

    /// <summary>
    /// True when the serial is a genuine wireless "host:port" endpoint — IPv4 or bracketed
    /// IPv6. Batch 4C M3 repair: the previous <c>Serial.Contains(':') &amp;&amp; Serial.Contains('.')</c>
    /// heuristic missed every bracketed IPv6 endpoint (no dot ever appears in one), silently
    /// mislabeling it as a USB device. <see cref="AdbInput.LooksLikeWirelessEndpoint"/> is the
    /// one shared, pure endpoint parser both this and the wireless-pairing page rely on.
    /// </summary>
    public bool IsWireless => AdbInput.LooksLikeWirelessEndpoint(Serial);

    public bool IsAuthorized => State == "device";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model)
            ? Serial
            : (string.IsNullOrWhiteSpace(Brand) ? Model : $"{Brand} {Model}");

    public string ConnectionLabel => IsWireless ? "Wi-Fi" : "USB";

    public string StateLabel => State switch
    {
        "device" => "متصل وجاهز",
        "unauthorized" => "بانتظار الإذن على الهاتف",
        "offline" => "غير متصل",
        _ => State
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
