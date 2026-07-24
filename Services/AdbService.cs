using System.Globalization;
using System.Text.RegularExpressions;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>High-level wrapper around adb.exe for everything the UI needs.</summary>
public static class AdbService
{
    private static Task<ProcessResult> Adb(IReadOnlyList<string> args, int timeout = 20000)
        => ProcessRunner.RunAsync(ToolLocator.AdbPath, args, timeout);

    /// <summary>Runs a per-device command, validating the serial before launching anything.</summary>
    private static Task<ProcessResult> AdbForDevice(
        string serial, IReadOnlyList<string> args, int timeout = 20000)
    {
        if (!AdbInput.TryValidateSerial(serial, out var error))
            return Task.FromResult(ProcessResult.Invalid(error));

        return Adb(WithSerialArgs(serial, args), timeout);
    }

    // ---------------- argument construction (pure, testable) ----------------
    // Every element below becomes one ProcessStartInfo.ArgumentList entry, so no value can
    // ever introduce an additional adb argument.

    internal static IReadOnlyList<string> WithSerialArgs(string serial, IReadOnlyList<string> args)
    {
        var list = new List<string>(args.Count + 2) { "-s", serial };
        list.AddRange(args);
        return list;
    }

    internal static readonly string[] DevicesArgs = ["devices", "-l"];
    internal static readonly string[] StartServerArgs = ["start-server"];
    internal static readonly string[] KillServerArgs = ["kill-server"];

    internal static IReadOnlyList<string> TcpipArgs(int port)
        => ["tcpip", port.ToString(CultureInfo.InvariantCulture)];
    internal static IReadOnlyList<string> ConnectArgs(string endpoint) => ["connect", endpoint];
    internal static IReadOnlyList<string> DisconnectArgs(string endpoint) => ["disconnect", endpoint];
    /// <summary>`adb disconnect` with no target drops every wireless connection.</summary>
    internal static readonly string[] DisconnectAllArgs = ["disconnect"];
    internal static IReadOnlyList<string> PairArgs(string endpoint, string code) => ["pair", endpoint, code];

    // `adb shell` hands its arguments to the device's sh, so fixed command tokens are used
    // wherever possible and only validated/quoted values are interpolated.
    internal static readonly string[] PropsArgs =
        ["shell", "getprop ro.product.model; getprop ro.product.brand; getprop ro.build.version.release"];
    internal static readonly string[] WmSizeArgs = ["shell", "wm", "size"];
    internal static readonly string[] BatteryArgs = ["shell", "dumpsys", "battery"];
    internal static readonly string[] WifiAddrArgs = ["shell", "ip", "-f", "inet", "addr", "show", "wlan0"];
    internal static readonly string[] WifiRouteArgs = ["shell", "ip", "route"];

    internal static IReadOnlyList<string> ListPackagesArgs(string scope)
        => string.IsNullOrWhiteSpace(scope)
            ? ["shell", "pm", "list", "packages"]
            : ["shell", "pm", "list", "packages", scope];

    internal static IReadOnlyList<string> UninstallArgs(string pkg) => ["uninstall", pkg];
    internal static IReadOnlyList<string> DisableArgs(string pkg)
        => ["shell", "pm", "disable-user", "--user", "0", pkg];
    internal static IReadOnlyList<string> EnableArgs(string pkg) => ["shell", "pm", "enable", pkg];
    internal static IReadOnlyList<string> ClearArgs(string pkg) => ["shell", "pm", "clear", pkg];
    internal static IReadOnlyList<string> ForceStopArgs(string pkg) => ["shell", "am", "force-stop", pkg];
    internal static IReadOnlyList<string> InstallArgs(string apkPath) => ["install", "-r", apkPath];
    internal static IReadOnlyList<string> PushArgs(string local, string remote) => ["push", local, remote];
    internal static IReadOnlyList<string> PullArgs(string remote, string local) => ["pull", remote, local];

    // ---------------- server ----------------

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

        var props = await AdbForDevice(dev.Serial, PropsArgs, 9000);
        ApplyProps(dev, props.StdOut);

        var size = await AdbForDevice(dev.Serial, WmSizeArgs, 6000);
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
        var res = await AdbForDevice(serial, BatteryArgs, 6000);
        return ParseBatteryLevel(res.StdOut);
    }

    /// <summary>Read the device's Wi-Fi (wlan0) IPv4 address, for wireless pairing.</summary>
    public static async Task<string?> GetWifiIpAsync(string serial)
    {
        var res = await AdbForDevice(serial, WifiAddrArgs, 6000);
        var ip = ParseWifiIpFromAddr(res.StdOut);
        if (ip != null) return ip;

        // Fallback for older devices.
        var res2 = await AdbForDevice(serial, WifiRouteArgs, 6000);
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
    {
        if (!AdbInput.TryValidatePort(port, out var portError))
            return Task.FromResult(ProcessResult.Invalid(portError));

        return AdbForDevice(serial, TcpipArgs(port), 10000);
    }

    public static Task<ProcessResult> ConnectAsync(string endpoint)
    {
        if (!AdbInput.TryValidateEndpoint(endpoint, out var normalized, out var error))
            return Task.FromResult(ProcessResult.Invalid(error));

        return Adb(ConnectArgs(normalized), 12000);
    }

    /// <summary>
    /// Disconnects a wireless device. An empty endpoint keeps the previous behaviour of
    /// `adb disconnect` with no argument, which drops every wireless connection.
    /// </summary>
    public static Task<ProcessResult> DisconnectAsync(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return Adb(DisconnectAllArgs, 8000);

        if (!AdbInput.TryValidateEndpoint(endpoint, out var normalized, out var error))
            return Task.FromResult(ProcessResult.Invalid(error));

        return Adb(DisconnectArgs(normalized), 8000);
    }

    /// <summary>
    /// Pairs with a device. The pairing code is a shared secret: the command is logged
    /// with the code redacted so it never reaches the log file.
    /// </summary>
    public static Task<ProcessResult> PairAsync(string endpoint, string code)
    {
        if (!AdbInput.TryValidateEndpoint(endpoint, out var normalized, out var error))
            return Task.FromResult(ProcessResult.Invalid(error));
        if (!AdbInput.TryValidatePairingCode(code, out var codeError))
            return Task.FromResult(ProcessResult.Invalid(codeError));

        var command = new ProcessCommand(
            ToolLocator.AdbPath,
            PairArgs(normalized, code.Trim()),
            15000,
            RedactedDescription: $"adb pair {normalized} ******");

        return ProcessRunner.RunAsync(command);
    }

    // ---------------- App management (pm) ----------------

    public static async Task<List<string>> ListPackagesAsync(string serial, string scope)
    {
        // scope: "-3" third-party, "-s" system, "" all
        var res = await AdbForDevice(serial, ListPackagesArgs(scope), 15000);
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

    /// <summary>Runs a package-scoped command after validating both serial and package id.</summary>
    private static Task<ProcessResult> PackageCommand(
        string serial, string pkg, Func<string, IReadOnlyList<string>> build, int timeout)
    {
        if (!AdbInput.TryValidatePackage(pkg, out var error))
            return Task.FromResult(ProcessResult.Invalid(error));

        return AdbForDevice(serial, build(pkg), timeout);
    }

    public static Task<ProcessResult> UninstallAsync(string serial, string pkg)
        => PackageCommand(serial, pkg, UninstallArgs, 20000);

    public static Task<ProcessResult> DisableAsync(string serial, string pkg)
        => PackageCommand(serial, pkg, DisableArgs, 12000);

    public static Task<ProcessResult> EnableAsync(string serial, string pkg)
        => PackageCommand(serial, pkg, EnableArgs, 12000);

    public static Task<ProcessResult> ClearAsync(string serial, string pkg)
        => PackageCommand(serial, pkg, ClearArgs, 12000);

    public static Task<ProcessResult> ForceStopAsync(string serial, string pkg)
        => PackageCommand(serial, pkg, ForceStopArgs, 8000);

    public static Task<ProcessResult> InstallAsync(string serial, string apkPath)
    {
        if (string.IsNullOrWhiteSpace(apkPath))
            return Task.FromResult(ProcessResult.Invalid("لم يتم اختيار ملف."));

        return AdbForDevice(serial, InstallArgs(apkPath), 120000);
    }

    // ---------------- File transfer ----------------

    public static Task<ProcessResult> PushAsync(string serial, string local, string remote)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote))
            return Task.FromResult(ProcessResult.Invalid("المسار غير صالح."));

        return AdbForDevice(serial, PushArgs(local, remote), 300000);
    }

    public static Task<ProcessResult> PullAsync(string serial, string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote))
            return Task.FromResult(ProcessResult.Invalid("المسار غير صالح."));

        return AdbForDevice(serial, PullArgs(remote, local), 300000);
    }

    // ---------------- helpers ----------------
    internal static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
