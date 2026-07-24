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
        => Adb(WithSerialArgs(serial, args), timeout);

    // ---------------- argument construction (pure, testable) ----------------
    // These build the exact argument strings the async wrappers below pass to adb.
    // Extracted verbatim so the regression suite can pin current behaviour.

    internal static string WithSerialArgs(string serial, string args) => $"-s {serial} {args}";

    internal const string DevicesArgs = "devices -l";
    internal const string StartServerArgs = "start-server";
    internal const string KillServerArgs = "kill-server";

    internal static string TcpipArgs(int port) => $"tcpip {port}";
    internal static string ConnectArgs(string ipPort) => $"connect {ipPort}";
    internal static string DisconnectArgs(string ipPort) => $"disconnect {ipPort}";
    internal static string PairArgs(string ipPort, string code) => $"pair {ipPort} {code}";

    internal static string PropsArgs =>
        "shell \"getprop ro.product.model; getprop ro.product.brand; getprop ro.build.version.release\"";
    internal const string WmSizeArgs = "shell wm size";
    internal const string BatteryArgs = "shell dumpsys battery";
    internal const string WifiAddrArgs = "shell ip -f inet addr show wlan0";
    internal const string WifiRouteArgs = "shell ip route";

    internal static string ListPackagesArgs(string scope) => $"shell pm list packages {scope}".Trim();
    internal static string UninstallArgs(string pkg) => $"uninstall {pkg}";
    internal static string DisableArgs(string pkg) => $"shell pm disable-user --user 0 {pkg}";
    internal static string EnableArgs(string pkg) => $"shell pm enable {pkg}";
    internal static string ClearArgs(string pkg) => $"shell pm clear {pkg}";
    internal static string ForceStopArgs(string pkg) => $"shell am force-stop {pkg}";
    internal static string InstallArgs(string apkPath) => $"install -r \"{apkPath}\"";
    internal static string PushArgs(string local, string remote) => $"push \"{local}\" \"{remote}\"";
    internal static string PullArgs(string remote, string local) => $"pull \"{remote}\" \"{local}\"";

    public static Task StartServerAsync() => Adb(StartServerArgs, 15000);
    public static Task KillServerAsync() => Adb(KillServerArgs, 8000);

    /// <summary>List connected devices via `adb devices -l` (without enriched details).</summary>
    public static async Task<List<AdbDevice>> GetDevicesAsync()
    {
        var res = await Adb(DevicesArgs, 12000);
        return ParseDeviceList(res.StdOut);
    }

    /// <summary>Parses the stdout of `adb devices -l`. Extracted verbatim for regression tests.</summary>
    internal static List<AdbDevice> ParseDeviceList(string stdOut)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in stdOut.Split('\n'))
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

        var props = await Adb(dev.Serial, PropsArgs, 9000);
        ApplyProps(dev, props.StdOut);

        var size = await Adb(dev.Serial, WmSizeArgs, 6000);
        var resolution = ParseResolution(size.StdOut);
        if (resolution != null) dev.Resolution = resolution;

        dev.Battery = await GetBatteryAsync(dev.Serial);
        dev.DetailsLoaded = true;
    }

    /// <summary>Applies `getprop` output to a device. Extracted verbatim for regression tests.</summary>
    internal static void ApplyProps(AdbDevice dev, string stdOut)
    {
        var lines = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 1 && lines[0].Trim().Length > 0) dev.Model = lines[0].Trim();
        if (lines.Length >= 2) dev.Brand = Capitalize(lines[1].Trim());
        if (lines.Length >= 3) dev.AndroidVersion = lines[2].Trim();
    }

    /// <summary>Parses `wm size` output; null when no WxH is present.</summary>
    internal static string? ParseResolution(string stdOut)
    {
        var sm = Regex.Match(stdOut, @"(\d+x\d+)");
        return sm.Success ? sm.Groups[1].Value : null;
    }

    /// <summary>Parses `dumpsys battery` output; -1 when no level is present.</summary>
    internal static int ParseBatteryLevel(string stdOut)
    {
        var m = Regex.Match(stdOut, @"level:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    public static async Task<int> GetBatteryAsync(string serial)
    {
        var res = await Adb(serial, BatteryArgs, 6000);
        return ParseBatteryLevel(res.StdOut);
    }

    /// <summary>Read the device's Wi-Fi (wlan0) IPv4 address, for wireless pairing.</summary>
    public static async Task<string?> GetWifiIpAsync(string serial)
    {
        var res = await Adb(serial, WifiAddrArgs, 6000);
        var ip = ParseWifiIpFromAddr(res.StdOut);
        if (ip != null) return ip;

        // Fallback for older devices.
        var res2 = await Adb(serial, WifiRouteArgs, 6000);
        return ParseWifiIpFromRoute(res2.StdOut);
    }

    /// <summary>Parses `ip -f inet addr show wlan0` output.</summary>
    internal static string? ParseWifiIpFromAddr(string stdOut)
    {
        var m = Regex.Match(stdOut, @"inet\s+(\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Parses `ip route` output (older-device fallback).</summary>
    internal static string? ParseWifiIpFromRoute(string stdOut)
    {
        var m2 = Regex.Match(stdOut, @"wlan0.*?src\s+(\d+\.\d+\.\d+\.\d+)");
        return m2.Success ? m2.Groups[1].Value : null;
    }

    // ---------------- Wireless connection ----------------
    public static Task<ProcessResult> TcpipAsync(string serial, int port = 5555)
        => Adb(serial, TcpipArgs(port), 10000);

    public static Task<ProcessResult> ConnectAsync(string ipPort)
        => Adb(ConnectArgs(ipPort), 12000);

    public static Task<ProcessResult> DisconnectAsync(string ipPort)
        => Adb(DisconnectArgs(ipPort), 8000);

    public static Task<ProcessResult> PairAsync(string ipPort, string code)
        => Adb(PairArgs(ipPort, code), 15000);

    // ---------------- App management (pm) ----------------
    public static async Task<List<string>> ListPackagesAsync(string serial, string scope)
    {
        // scope: "-3" third-party, "-s" system, "" all
        var res = await Adb(serial, ListPackagesArgs(scope), 15000);
        return ParsePackages(res.StdOut);
    }

    /// <summary>Parses `pm list packages` output. Extracted verbatim for regression tests.</summary>
    internal static List<string> ParsePackages(string stdOut)
    {
        var pkgs = new List<string>();
        foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("package:")) pkgs.Add(t.Substring("package:".Length).Trim());
        }
        pkgs.Sort(StringComparer.OrdinalIgnoreCase);
        return pkgs;
    }

    public static Task<ProcessResult> UninstallAsync(string serial, string pkg)
        => Adb(serial, UninstallArgs(pkg), 20000);

    public static Task<ProcessResult> DisableAsync(string serial, string pkg)
        => Adb(serial, DisableArgs(pkg), 12000);

    public static Task<ProcessResult> EnableAsync(string serial, string pkg)
        => Adb(serial, EnableArgs(pkg), 12000);

    public static Task<ProcessResult> ClearAsync(string serial, string pkg)
        => Adb(serial, ClearArgs(pkg), 12000);

    public static Task<ProcessResult> ForceStopAsync(string serial, string pkg)
        => Adb(serial, ForceStopArgs(pkg), 8000);

    public static Task<ProcessResult> InstallAsync(string serial, string apkPath)
        => Adb(serial, InstallArgs(apkPath), 120000);

    // ---------------- File transfer ----------------
    public static Task<ProcessResult> PushAsync(string serial, string local, string remote)
        => Adb(serial, PushArgs(local, remote), 300000);

    public static Task<ProcessResult> PullAsync(string serial, string remote, string local)
        => Adb(serial, PullArgs(remote, local), 300000);

    // ---------------- helpers ----------------
    internal static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
