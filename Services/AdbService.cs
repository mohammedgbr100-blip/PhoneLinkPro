using System.IO;
using System.Text.RegularExpressions;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>High-level wrapper around adb.exe for everything the UI needs.</summary>
public static class AdbService
{
    private static Task<ProcessResult> Adb(string args, int timeout = 20000)
        => ProcessRunner.RunAsync(ToolLocator.AdbPath, args, timeout);

    /// <summary>Wraps a per-device command with -s [serial].</summary>
    private static Task<ProcessResult> Adb(string serial, string args, int timeout = 20000)
        => Adb($"-s {serial} {args}", timeout);

    public static Task StartServerAsync() => Adb("start-server", 15000);
    public static Task KillServerAsync() => Adb("kill-server", 8000);

    /// <summary>List connected devices via `adb devices -l` (without enriched details).</summary>
    public static async Task<List<AdbDevice>> GetDevicesAsync()
    {
        var res = await Adb("devices -l", 12000);
        var list = new List<AdbDevice>();
        foreach (var raw in res.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices")) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var dev = new AdbDevice { Serial = parts[0], State = parts[1] };

            // Pull the "model:XXX" / "device:XXX" hints adb already prints.
            var m = Regex.Match(line, @"model:(\S+)");
            if (m.Success) dev.Model = m.Groups[1].Value.Replace('_', ' ');

            list.Add(dev);
        }
        return list;
    }

    /// <summary>Fetch model / brand / android version / resolution / battery for a device.</summary>
    public static async Task EnrichAsync(AdbDevice dev)
    {
        if (!dev.IsAuthorized) return;

        var props = await Adb(dev.Serial,
            "shell \"getprop ro.product.model; getprop ro.product.brand; getprop ro.build.version.release\"",
            9000);
        var lines = props.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 1 && lines[0].Trim().Length > 0) dev.Model = lines[0].Trim();
        if (lines.Length >= 2) dev.Brand = Capitalize(lines[1].Trim());
        if (lines.Length >= 3) dev.AndroidVersion = lines[2].Trim();

        var size = await Adb(dev.Serial, "shell wm size", 6000);
        var sm = Regex.Match(size.StdOut, @"(\d+x\d+)");
        if (sm.Success) dev.Resolution = sm.Groups[1].Value;

        dev.Battery = await GetBatteryAsync(dev.Serial);
        dev.DetailsLoaded = true;
    }

    public static async Task<int> GetBatteryAsync(string serial)
    {
        var res = await Adb(serial, "shell dumpsys battery", 6000);
        var m = Regex.Match(res.StdOut, @"level:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    /// <summary>Read the device's Wi-Fi (wlan0) IPv4 address, for wireless pairing.</summary>
    public static async Task<string?> GetWifiIpAsync(string serial)
    {
        var res = await Adb(serial, "shell ip -f inet addr show wlan0", 6000);
        var m = Regex.Match(res.StdOut, @"inet\s+(\d+\.\d+\.\d+\.\d+)");
        if (m.Success) return m.Groups[1].Value;

        // Fallback for older devices.
        var res2 = await Adb(serial, "shell ip route", 6000);
        var m2 = Regex.Match(res2.StdOut, @"wlan0.*?src\s+(\d+\.\d+\.\d+\.\d+)");
        return m2.Success ? m2.Groups[1].Value : null;
    }

    // ---------------- Wireless connection ----------------
    public static Task<ProcessResult> TcpipAsync(string serial, int port = 5555)
        => Adb(serial, $"tcpip {port}", 10000);

    public static Task<ProcessResult> ConnectAsync(string ipPort)
        => Adb($"connect {ipPort}", 12000);

    public static Task<ProcessResult> DisconnectAsync(string ipPort)
        => Adb($"disconnect {ipPort}", 8000);

    public static Task<ProcessResult> PairAsync(string ipPort, string code)
        => Adb($"pair {ipPort} {code}", 15000);

    // ---------------- App management (pm) ----------------
    public static async Task<List<string>> ListPackagesAsync(string serial, string scope)
    {
        // scope: "-3" third-party, "-s" system, "" all
        var res = await Adb(serial, $"shell pm list packages {scope}".Trim(), 15000);
        var pkgs = new List<string>();
        foreach (var line in res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("package:")) pkgs.Add(t.Substring("package:".Length).Trim());
        }
        pkgs.Sort(StringComparer.OrdinalIgnoreCase);
        return pkgs;
    }

    public static Task<ProcessResult> UninstallAsync(string serial, string pkg)
        => Adb(serial, $"uninstall {pkg}", 20000);

    public static Task<ProcessResult> DisableAsync(string serial, string pkg)
        => Adb(serial, $"shell pm disable-user --user 0 {pkg}", 12000);

    public static Task<ProcessResult> EnableAsync(string serial, string pkg)
        => Adb(serial, $"shell pm enable {pkg}", 12000);

    public static Task<ProcessResult> ClearAsync(string serial, string pkg)
        => Adb(serial, $"shell pm clear {pkg}", 12000);

    public static Task<ProcessResult> ForceStopAsync(string serial, string pkg)
        => Adb(serial, $"shell am force-stop {pkg}", 8000);

    public static Task<ProcessResult> InstallAsync(string serial, string apkPath)
        => Adb(serial, $"install -r \"{apkPath}\"", 120000);

    // ---------------- File transfer ----------------
    public static Task<ProcessResult> PushAsync(string serial, string local, string remote)
        => Adb(serial, $"push \"{local}\" \"{remote}\"", 300000);

    public static Task<ProcessResult> PullAsync(string serial, string remote, string local)
        => Adb(serial, $"pull \"{remote}\" \"{local}\"", 300000);

    // ---------------- helpers ----------------
    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
