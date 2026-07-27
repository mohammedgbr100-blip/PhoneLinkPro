using System.Globalization;
using System.Text.RegularExpressions;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>High-level wrapper around adb.exe for everything the UI needs.</summary>
public static class AdbService
{
    /// <summary>
    /// Integrity gate consulted before ANY bundled-binary launch (SEC-01): the bridge is
    /// never executed until the embedded manifest has confirmed the engine files. The first
    /// call triggers the shared verification; later calls return the cached result.
    /// Swappable for tests.
    /// </summary>
    internal static Func<CancellationToken, Task<EngineIntegrityResult>> IntegrityGate { get; set; }
        = EngineIntegrityVerifier.VerifyAsync;

    /// <summary>Restores the production gate (test hook).</summary>
    internal static void ResetIntegrityGateForTests()
        => IntegrityGate = EngineIntegrityVerifier.VerifyAsync;

    /// <summary>
    /// Test-only seam at the exact boundary where the real bridge process would be launched —
    /// where <see cref="Adb"/> would otherwise call <see cref="ProcessRunner.RunAsync(string, IReadOnlyList{string}, int, CancellationToken)"/>.
    /// Deliberately placed here, never above <see cref="PushAsync"/>/<see cref="PullAsync"/>: a
    /// direct test of those methods still exercises the full production call chain
    /// (PushAsync/PullAsync → AdbForDevice → Adb → integrity gate → this boundary) — including
    /// argument construction, the "-s" serial prefix and the integrity check — without ever
    /// starting adb.exe. The override receives the exact same arguments, timeout and
    /// <see cref="CancellationToken"/> the real <see cref="ProcessRunner.RunAsync(string, IReadOnlyList{string}, int, CancellationToken)"/>
    /// call would have received. Null (the production default) means "use the real runner."
    /// </summary>
    internal static Func<string, IReadOnlyList<string>, int, CancellationToken, Task<ProcessResult>>?
        ProcessRunOverrideForTests;

    /// <summary>Restores the production process-run boundary (test hook).</summary>
    internal static void ResetProcessRunOverrideForTests() => ProcessRunOverrideForTests = null;

    /// <summary>
    /// Result for a call refused by the integrity gate. StartFailed (not InvalidInput): the
    /// tool genuinely could not be started — validation semantics are untouched. The safe
    /// diagnostic goes to FailureDetail; no hash and no absolute path is ever carried.
    /// </summary>
    private static ProcessResult BlockedByIntegrity(EngineIntegrityResult integrity)
        => integrity.Status == EngineIntegrityStatus.VerificationCancelled
            ? new ProcessResult(ProcessOutcome.Cancelled, null, "", "")
            : new ProcessResult(ProcessOutcome.StartFailed, null, "",
                "تعذّر تشغيل أدوات الاتصال بالجهاز: ملفات البرنامج بحاجة إلى إصلاح.",
                FailureDetail: IntegrityDiagnostic(integrity));

    private static string IntegrityDiagnostic(EngineIntegrityResult integrity)
        => ($"engine integrity: {integrity.Status} {integrity.AffectedPath}").TrimEnd();

    private static async Task<ProcessResult> Adb(IReadOnlyList<string> args, int timeout = 20000, CancellationToken ct = default)
    {
        // The integrity gate itself is never cancelled by a caller's token — it is a fixed,
        // fast, local check, not part of the potentially-long transfer the caller may abandon.
        var integrity = await IntegrityGate(CancellationToken.None);
        if (!integrity.IsValid) return BlockedByIntegrity(integrity);

        var run = ProcessRunOverrideForTests ?? ProcessRunner.RunAsync;
        return await run(ToolLocator.AdbPath, args, timeout, ct);
    }

    /// <summary>Runs a per-device command, validating the serial before launching anything.</summary>
    private static Task<ProcessResult> AdbForDevice(
        string serial, IReadOnlyList<string> args, int timeout = 20000, CancellationToken ct = default)
    {
        if (!AdbInput.TryValidateSerial(serial, out var error))
            return Task.FromResult(ProcessResult.Invalid(error));

        return Adb(WithSerialArgs(serial, args), timeout, ct);
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

    /// <summary>
    /// Queries connected devices via `adb devices -l`, reporting WHY a query failed rather
    /// than flattening every failure into an empty list (DEV-01).
    /// </summary>
    public static async Task<DeviceDiscoveryResult> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        // A cancelled caller is reported as Cancelled before anything else is consulted.
        if (ct.IsCancellationRequested)
            return DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.Cancelled, null);

        // SEC-01: the bridge is not launched until its integrity set is verified. The
        // cached gate result makes this instant on every poll, so a watcher tick during an
        // integrity failure costs nothing and starts no process.
        var integrity = await IntegrityGate(ct);
        if (!integrity.IsValid)
        {
            return integrity.Status == EngineIntegrityStatus.VerificationCancelled
                ? DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.Cancelled, null)
                : DeviceDiscoveryResult.Failure(
                    DeviceDiscoveryOutcome.IntegrityBlocked, IntegrityDiagnostic(integrity));
        }

        var res = await ProcessRunner.RunAsync(
            new ProcessCommand(ToolLocator.AdbPath, DevicesArgs, 12000), ct);

        switch (res.Outcome)
        {
            case ProcessOutcome.TimedOut:
                return DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.TimedOut, "adb devices timed out");

            case ProcessOutcome.Cancelled:
                return DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.Cancelled, null);

            case ProcessOutcome.StartFailed:
            case ProcessOutcome.InvalidInput:
                return DeviceDiscoveryResult.Failure(
                    DeviceDiscoveryOutcome.StartFailed, res.FailureDetail ?? "adb could not be started");

            case ProcessOutcome.Exited when res.ExitCode != 0:
                return DeviceDiscoveryResult.Failure(
                    DeviceDiscoveryOutcome.AdbError,
                    $"adb exited with {res.ExitCode}: {Truncate(res.StdErr)}");
        }

        // Exit code 0. Distinguish "understood the output" from "cannot make sense of it".
        if (!LooksLikeDeviceListing(res.StdOut))
            return DeviceDiscoveryResult.Failure(
                DeviceDiscoveryOutcome.Unusable, $"unrecognised adb output: {Truncate(res.StdOut)}");

        return DeviceDiscoveryResult.Success(ParseDeviceList(res.StdOut));
    }

    private static string Truncate(string s)
    {
        var t = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 200 ? t : t[..200];
    }

    /// <summary>
    /// True when stdout is recognisably a `devices -l` listing: it either carries the
    /// header, is empty/banner-only, or yields at least one device line. Anything else
    /// (a stray error page, a wall of prose) is reported as Unusable rather than silently
    /// becoming "no devices".
    /// </summary>
    internal static bool LooksLikeDeviceListing(string stdOut)
    {
        // Tolerate a null despite the non-nullable signature (defensive: adb output flows
        // in from process capture). Hoisting the coalesce keeps flow analysis satisfied
        // for the ParseDeviceList call below (was CS8604).
        var text = stdOut ?? "";

        var meaningful = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsHeader(line)) return true;
            if (IsBanner(line)) continue;
            meaningful++;
        }

        // Nothing but blanks/banners is a legitimate "server just started, no devices yet".
        if (meaningful == 0) return true;

        return ParseDeviceList(text).Count > 0;
    }

    // ---------------- device-list parsing (DEV-03) ----------------

    private static bool IsHeader(string line)
        => line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase);

    /// <summary>adb prefixes daemon banners with '*'. They are never devices.</summary>
    private static bool IsBanner(string line) => line.StartsWith('*');

    /// <summary>
    /// Shape check for the first token of a device line. Diagnostic text can put a real
    /// state word in the second column — "error: device offline" is the classic case — so
    /// the serial itself must also look plausible: it needs an alphanumeric character and
    /// must not begin or end with a separator (a trailing ':' means it was a label, not a
    /// serial).
    /// </summary>
    private static bool LooksLikeSerial(string serial)
    {
        if (serial.Length == 0) return false;
        if (!serial.Any(char.IsLetterOrDigit)) return false;

        const string separators = ":.-_";
        return !separators.Contains(serial[0]) && !separators.Contains(serial[^1]);
    }

    /// <summary>
    /// States adb is known to report. An allow-list is used so arbitrary prose cannot
    /// masquerade as a device.
    /// </summary>
    private static readonly HashSet<string> KnownStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "device", "unauthorized", "offline", "bootloader", "recovery", "sideload",
        "host", "authorizing", "connecting", "rescue", "unknown", "no",
    };

    /// <summary>Metadata keys adb appends in `-l` mode; strong evidence a line is a device.</summary>
    private static readonly Regex MetadataPattern =
        new(@"\b(product|model|device|transport_id):\S+", RegexOptions.Compiled);

    /// <summary>
    /// Parses the stdout of `adb devices -l`.
    ///
    /// <para><b>Rules.</b> The header and every '*' banner line are ignored. A line becomes a
    /// device only when it has at least two whitespace-separated tokens AND either its state
    /// token is a known adb state, or the line carries adb's `-l` metadata (product:/model:/
    /// device:/transport_id:). Everything else — error text, prose, partial lines — is
    /// discarded, so no phantom device can be produced.</para>
    ///
    /// <para><b>Unknown states.</b> A structurally valid device line whose state adb has not
    /// used before is RETAINED with that state verbatim, provided the line carries `-l`
    /// metadata. This keeps a future adb state visible to the user (AdbDevice.StateLabel
    /// falls through to the raw value) instead of silently hiding a connected phone.</para>
    ///
    /// <para><b>Duplicates.</b> The first record for a serial wins and fixes its position and
    /// state; metadata missing from it is backfilled from later duplicates.</para>
    /// </summary>
    internal static List<AdbDevice> ParseDeviceList(string stdOut)
    {
        var list = new List<AdbDevice>();
        var bySerial = new Dictionary<string, AdbDevice>(StringComparer.Ordinal);

        foreach (var raw in (stdOut ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsHeader(line)) continue;
            if (IsBanner(line)) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            var hasMetadata = MetadataPattern.IsMatch(line);

            // Structural gate: a known state, or adb's own -l metadata on the line.
            if (!KnownStates.Contains(state) && !hasMetadata) continue;

            // A serial that could never come from adb is noise, whatever else the line says.
            if (!AdbInput.TryValidateSerial(serial, out _)) continue;
            if (!LooksLikeSerial(serial)) continue;

            var model = "";
            var m = Regex.Match(line, @"model:(\S+)");
            if (m.Success) model = m.Groups[1].Value.Replace('_', ' ');

            if (bySerial.TryGetValue(serial, out var existing))
            {
                // Duplicate: keep the first record, backfill anything it was missing.
                if (existing.Model.Length == 0 && model.Length > 0) existing.Model = model;
                continue;
            }

            var dev = new AdbDevice { Serial = serial, State = state, Model = model };
            bySerial[serial] = dev;
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

        return PairCoreAsync(normalized, code.Trim());
    }

    private static async Task<ProcessResult> PairCoreAsync(string normalizedEndpoint, string code)
    {
        var integrity = await IntegrityGate(CancellationToken.None);
        if (!integrity.IsValid) return BlockedByIntegrity(integrity);

        var command = new ProcessCommand(
            ToolLocator.AdbPath,
            PairArgs(normalizedEndpoint, code),
            15000,
            RedactedDescription: $"adb pair {normalizedEndpoint} ******");

        return await ProcessRunner.RunAsync(command);
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

    /// <summary>Bounded ceiling for one push/pull attempt — generous for a large file, never unbounded.</summary>
    internal const int TransferTimeoutMs = 300000;

    public static Task<ProcessResult> PushAsync(string serial, string local, string remote, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote))
            return Task.FromResult(ProcessResult.Invalid("المسار غير صالح."));

        return AdbForDevice(serial, PushArgs(local, remote), TransferTimeoutMs, ct);
    }

    public static Task<ProcessResult> PullAsync(string serial, string remote, string local, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote))
            return Task.FromResult(ProcessResult.Invalid("المسار غير صالح."));

        return AdbForDevice(serial, PullArgs(remote, local), TransferTimeoutMs, ct);
    }

    // ---------------- helpers ----------------
    internal static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
