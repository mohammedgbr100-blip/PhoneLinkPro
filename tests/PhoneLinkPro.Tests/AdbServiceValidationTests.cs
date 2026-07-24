using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// CMD-02 — the service layer must reject invalid input BEFORE launching adb, and must
/// never write a pairing code to the log.
///
/// These tests run with no device attached and with the engine binaries absent from the
/// test output, so a rejected call is proven by the <see cref="ProcessOutcome.InvalidInput"/>
/// outcome (a call that did reach adb would report StartFailed instead).
/// </summary>
public sealed class AdbServiceValidationTests : IDisposable
{
    private readonly string _logDir;
    private readonly string? _previousOverride;

    public AdbServiceValidationTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-adblog-" + Guid.NewGuid().ToString("N"));
        _previousOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        Log.DirOverride = _previousOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string ReadLogs()
    {
        if (!Directory.Exists(_logDir)) return "";
        return string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
    }

    // ---------------------------------------------------------------- serial gating

    [Theory]
    [InlineData("abc kill-server")]
    [InlineData("-s")]
    [InlineData("abc;rm")]
    [InlineData("")]
    public async Task InvalidSerial_IsRejectedWithoutLaunchingAdb(string serial)
    {
        var r = await AdbService.ClearAsync(serial, "com.example.app");

        Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
        Assert.False(r.Success);
        Assert.Null(r.ExitCode);
        Assert.NotEqual("", r.Output);      // a useful Arabic reason
    }

    // ---------------------------------------------------------------- package gating

    [Theory]
    [InlineData("com.example.app; rm -rf /sdcard")]
    [InlineData("com.example.app --user 0")]
    [InlineData("com..example")]
    [InlineData("")]
    public async Task InvalidPackage_IsRejectedWithoutLaunchingAdb(string pkg)
    {
        foreach (var call in new Func<Task<ProcessResult>>[]
        {
            () => AdbService.UninstallAsync("R58M12ABCDE", pkg),
            () => AdbService.DisableAsync("R58M12ABCDE", pkg),
            () => AdbService.EnableAsync("R58M12ABCDE", pkg),
            () => AdbService.ClearAsync("R58M12ABCDE", pkg),
            () => AdbService.ForceStopAsync("R58M12ABCDE", pkg),
        })
        {
            var r = await call();
            Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
            Assert.False(r.Success);
        }
    }

    // ---------------------------------------------------------------- endpoint gating

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("192.168.1.42:0")]
    [InlineData("192.168.1.42:65536")]
    [InlineData("192.168.1.42 && id")]
    [InlineData("")]
    public async Task InvalidEndpoint_IsRejectedForConnect(string endpoint)
    {
        var connect = await AdbService.ConnectAsync(endpoint);

        Assert.Equal(ProcessOutcome.InvalidInput, connect.Outcome);
    }

    [Theory] // a non-empty but malformed endpoint is still rejected for disconnect
    [InlineData("not-an-ip")]
    [InlineData("192.168.1.42:0")]
    [InlineData("192.168.1.42 && id")]
    public async Task InvalidEndpoint_IsRejectedForDisconnect(string endpoint)
    {
        var disconnect = await AdbService.DisconnectAsync(endpoint);

        Assert.Equal(ProcessOutcome.InvalidInput, disconnect.Outcome);
    }

    [Fact] // PRESERVED BEHAVIOUR: an empty box means "disconnect every wireless device",
           // which is what `adb disconnect` with no argument does. Validation must not
           // break it, so this call is allowed through to adb.
    public void DisconnectAll_UsesTheNoArgumentForm()
        => Assert.Equal(["disconnect"], AdbService.DisconnectAllArgs);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyEndpoint_ForDisconnect_IsNotRejectedAsInvalid(string endpoint)
    {
        var r = await AdbService.DisconnectAsync(endpoint);

        // It reaches adb (which is absent in the test output), so it is a start failure —
        // crucially NOT InvalidInput, proving the "disconnect all" path is still available.
        Assert.NotEqual(ProcessOutcome.InvalidInput, r.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public async Task InvalidPort_IsRejectedForTcpip(int port)
    {
        var r = await AdbService.TcpipAsync("R58M12ABCDE", port);

        Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
        Assert.Contains("المنفذ", r.Output);
    }

    // ---------------------------------------------------------------- pairing

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("")]
    public async Task InvalidPairingCode_IsRejectedWithoutLaunchingAdb(string code)
    {
        var r = await AdbService.PairAsync("192.168.1.42:37123", code);

        Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task InvalidPairingEndpoint_IsRejectedBeforeTheCodeIsEvenChecked()
    {
        var r = await AdbService.PairAsync("nonsense", "123456");

        Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
    }

    [Fact] // the pairing code is a shared secret and must never be written to disk
    public async Task PairingCode_NeverAppearsInTheLog()
    {
        const string secret = "246813";

        // A valid code with an endpoint that cannot be reached: the command is attempted,
        // so the redacted description is what gets logged.
        await AdbService.PairAsync("192.168.1.42:37123", secret);

        // Also exercise the rejection path.
        await AdbService.PairAsync("192.168.1.42:37123", "99");

        var logs = ReadLogs();
        Assert.DoesNotContain(secret, logs);
    }

    [Fact] // the redacted description masks the code but keeps the endpoint for diagnostics
    public void PairCommand_RedactedDescriptionHidesTheCode()
    {
        var command = new ProcessCommand(
            "adb.exe",
            AdbService.PairArgs("192.168.1.42:37123", "246813"),
            15000,
            RedactedDescription: "adb pair 192.168.1.42:37123 ******");

        Assert.DoesNotContain("246813", command.LogText);
        Assert.Contains("192.168.1.42:37123", command.LogText);
    }

    [Fact] // without redaction the argument list would leak into the log text
    public void ProcessCommand_WithoutRedaction_LogsItsArguments()
    {
        var command = new ProcessCommand("adb.exe", ["devices", "-l"]);

        Assert.Contains("devices", command.LogText);
        Assert.Contains("-l", command.LogText);
    }

    // ---------------------------------------------------------------- transfer gating

    [Theory]
    [InlineData("", "/sdcard/x.txt")]
    [InlineData("C:\\a.txt", "")]
    public async Task EmptyTransferPaths_AreRejected(string local, string remote)
    {
        var push = await AdbService.PushAsync("R58M12ABCDE", local, remote);
        Assert.Equal(ProcessOutcome.InvalidInput, push.Outcome);
    }

    [Fact]
    public async Task EmptyApkPath_IsRejected()
    {
        var r = await AdbService.InstallAsync("R58M12ABCDE", "");

        Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
    }

    // ---------------------------------------------------------------- error surface

    [Fact] // users must never see a stack trace
    public async Task ValidationFailure_MessageIsArabicAndCarriesNoStackTrace()
    {
        var r = await AdbService.ConnectAsync("not-an-ip");

        Assert.Equal(ProcessOutcome.InvalidInput, r.Outcome);
        Assert.DoesNotContain("Exception", r.Output);
        Assert.DoesNotContain("   at ", r.Output);
        Assert.Contains("عنوان", r.Output);
    }
}
