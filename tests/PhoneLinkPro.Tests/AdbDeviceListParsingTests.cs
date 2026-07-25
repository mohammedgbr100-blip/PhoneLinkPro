using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 1 — `adb devices -l` parsing (<see cref="AdbService.ParseDeviceList"/>).
///
/// Classification:
///   [REGRESSION]      pins behaviour that is correct today and must not change.
///   [CHARACTERIZATION] pins behaviour that is WRONG today; it documents the defect so a
///                      later batch changes it deliberately. These are NOT approvals.
/// </summary>
public class AdbDeviceListParsingTests
{
    // ---------------------------------------------------------------- happy paths

    [Fact] // [REGRESSION]
    public void SingleAuthorizedDevice_IsParsed()
    {
        const string output = """
            List of devices attached
            R58M12ABCDE            device usb:1-4 product:a52qnaxx model:SM_A525F device:a52q transport_id:3
            """;

        var devices = AdbService.ParseDeviceList(output);

        var d = Assert.Single(devices);
        Assert.Equal("R58M12ABCDE", d.Serial);
        Assert.Equal("device", d.State);
        Assert.True(d.IsAuthorized);
        // model:SM_A525F -> underscores become spaces
        Assert.Equal("SM A525F", d.Model);
        Assert.False(d.IsWireless);
    }

    [Fact] // [REGRESSION]
    public void MultipleDevices_AreAllParsedInOrder()
    {
        const string output = """
            List of devices attached
            R58M12ABCDE            device usb:1-4 product:a52qnaxx model:SM_A525F device:a52q
            emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64
            192.168.1.42:5555      device product:lineage_x model:Pixel_5 device:redfin
            """;

        var devices = AdbService.ParseDeviceList(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(new[] { "R58M12ABCDE", "emulator-5554", "192.168.1.42:5555" },
            devices.Select(d => d.Serial));
        Assert.All(devices, d => Assert.Equal("device", d.State));
    }

    [Theory] // [REGRESSION] — non-"device" states are preserved verbatim
    [InlineData("unauthorized")]
    [InlineData("offline")]
    [InlineData("bootloader")]
    [InlineData("recovery")]
    [InlineData("sideload")]
    [InlineData("authorizing")]
    [InlineData("no")]          // adb prints "no permissions"; parser keeps the first token
    public void NonReadyStates_ArePreservedAndNotAuthorized(string state)
    {
        var output = $"List of devices attached\nR58M12ABCDE            {state}\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal(state, d.State);
        Assert.False(d.IsAuthorized);
    }

    [Fact] // [REGRESSION] — a known-but-unmapped state still reaches the UI verbatim
    public void KnownButUnmappedState_IsPassedThroughUnchanged()
    {
        const string output = "List of devices attached\nSERIAL123   rescue\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("rescue", d.State);
        Assert.False(d.IsAuthorized);
        // StateLabel falls through to the raw state for anything unmapped.
        Assert.Equal("rescue", d.StateLabel);
    }

    [Fact] // [REGRESSION]
    public void TcpIpSerial_IsDetectedAsWireless()
    {
        const string output = "List of devices attached\n192.168.1.42:5555   device model:Pixel_5\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("192.168.1.42:5555", d.Serial);
        Assert.True(d.IsWireless);
        Assert.Equal("Wi-Fi", d.ConnectionLabel);
    }

    [Fact] // [REGRESSION]
    public void UsbSerial_IsNotWireless()
    {
        const string output = "List of devices attached\nR58M12ABCDE   device\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.False(d.IsWireless);
        Assert.Equal("USB", d.ConnectionLabel);
    }

    [Fact] // [REGRESSION] — metadata other than model: is ignored, model: is extracted
    public void ModelProductDeviceMetadata_OnlyModelIsExtracted()
    {
        const string output =
            "List of devices attached\n" +
            "R58M12ABCDE  device usb:1-4 product:a52qnaxx model:Galaxy_A52 device:a52q transport_id:3\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("Galaxy A52", d.Model);
        Assert.Equal("", d.Brand);           // brand only arrives via enrichment
        Assert.Equal("Galaxy A52", d.DisplayName);
    }

    [Fact] // [REGRESSION]
    public void DeviceWithoutModelMetadata_FallsBackToSerialForDisplay()
    {
        const string output = "List of devices attached\nR58M12ABCDE   device\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("", d.Model);
        Assert.Equal("R58M12ABCDE", d.DisplayName);
    }

    // ---------------------------------------------------------------- empty / trivial input

    [Theory] // [REGRESSION]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \n  \n")]
    [InlineData("\r\n\r\n")]
    public void BlankOutput_YieldsNoDevices(string output)
        => Assert.Empty(AdbService.ParseDeviceList(output));

    [Fact] // [REGRESSION]
    public void HeaderOnly_YieldsNoDevices()
        => Assert.Empty(AdbService.ParseDeviceList("List of devices attached\n"));

    [Fact] // [REGRESSION]
    public void HeaderWithCrLf_YieldsNoDevices()
        => Assert.Empty(AdbService.ParseDeviceList("List of devices attached\r\n"));

    [Fact] // [REGRESSION] — a serial with no state column is skipped
    public void LineWithOnlyOneToken_IsSkipped()
        => Assert.Empty(AdbService.ParseDeviceList("List of devices attached\nR58M12ABCDE\n"));

    // ---------------------------------------------------------------- malformed input

    [Fact] // DEV-03 FIXED — prose no longer becomes a device
    public void MalformedLine_WithTwoTokens_IsIgnored()
        => Assert.Empty(AdbService.ParseDeviceList("List of devices attached\nthis is not a device line\n"));

    [Fact] // [REGRESSION] — tab-separated output still parses
    public void TabSeparatedColumns_AreParsed()
    {
        const string output = "List of devices attached\nR58M12ABCDE\tdevice\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("R58M12ABCDE", d.Serial);
        Assert.Equal("device", d.State);
    }

    // ================================================================
    //  DEV-03 — FIXED: banners and noise never become devices
    // ================================================================
    // The Batch 1A characterization tests that pinned phantom `*` devices are REPLACED by
    // the regression tests below.

    [Fact] // DEV-03 — the test that was skipped in 1A/1B/1C, now active
    public void DEV03_DaemonBannerIsIgnored()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            R58M12ABCDE            device usb:1-4 model:SM_A525F
            """;

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("R58M12ABCDE", d.Serial);
        Assert.Equal("device", d.State);
    }

    [Fact] // DEV-03 — banner-only output means no devices, not phantoms
    public void DEV03_BannerOnlyOutput_YieldsNoDevices()
        => Assert.Empty(AdbService.ParseDeviceList("""
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            """));

    [Fact] // DEV-03 — a daemon RESTART banner is also ignored
    public void DEV03_DaemonRestartBanner_IsIgnored()
        => Assert.Empty(AdbService.ParseDeviceList("""
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            * server version (41) restarting
            List of devices attached
            """));

    [Fact] // DEV-03 — adb handshake error text is not a device
    public void DEV03_AdbErrorText_IsIgnored()
        => Assert.Empty(AdbService.ParseDeviceList(
            "adb server version (41) doesn't match this client (39); killing...\n"));

    [Theory] // DEV-03 — assorted diagnostic noise must never produce a device
    [InlineData("error: device offline")]
    [InlineData("adb: error: failed to get feature set")]
    [InlineData("cannot connect to daemon at tcp:5037: connection refused")]
    [InlineData("* failed to start daemon")]
    [InlineData("could not read ok from ADB Server")]
    [InlineData("Android Debug Bridge version 1.0.41")]
    public void DEV03_DiagnosticNoise_YieldsNoDevices(string noise)
        => Assert.Empty(AdbService.ParseDeviceList(noise + "\n"));

    [Fact] // DEV-03 — no device may ever carry the serial "*"
    public void DEV03_NoPhantomStarDeviceIsEverProduced()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            * some future banner nobody has seen
            List of devices attached
            R58M12ABCDE   device
            """;

        var devices = AdbService.ParseDeviceList(output);

        Assert.DoesNotContain(devices, d => d.Serial == "*");
        Assert.Single(devices);
    }

    [Fact] // DEV-03 — a real device still survives alongside heavy noise
    public void DEV03_RealDeviceSurvivesSurroundingNoise()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            adb: error: something went wrong
            List of devices attached
            R58M12ABCDE            device usb:1-4 product:a52q model:SM_A525F transport_id:3
            cannot connect to something
            """;

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("R58M12ABCDE", d.Serial);
        Assert.Equal("SM A525F", d.Model);
    }

    // ---------------------------------------------------------------- unknown states

    [Fact] // DEV-03 — a future adb state on a structurally valid -l line is RETAINED
    public void UnknownState_WithMetadata_IsRetainedVerbatim()
    {
        const string output =
            "List of devices attached\n" +
            "R58M12ABCDE   some-future-state usb:1-4 product:a52q model:SM_A525F transport_id:3\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("some-future-state", d.State);
        Assert.False(d.IsAuthorized);
        Assert.Equal("some-future-state", d.StateLabel);   // shown to the user as-is
    }

    [Fact] // DEV-03 — an unknown state WITHOUT metadata is not trusted
    public void UnknownState_WithoutMetadata_IsIgnored()
        => Assert.Empty(AdbService.ParseDeviceList(
            "List of devices attached\nR58M12ABCDE   some-future-state\n"));

    // ---------------------------------------------------------------- duplicates

    [Fact] // DEV-03 — duplicates reconcile deterministically: first wins, metadata backfilled
    public void DuplicateSerial_IsReconciledToOneDevice()
    {
        const string output = """
            List of devices attached
            R58M12ABCDE   device
            R58M12ABCDE   device usb:1-4 model:SM_A525F
            """;

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("R58M12ABCDE", d.Serial);
        Assert.Equal("device", d.State);
        Assert.Equal("SM A525F", d.Model);      // backfilled from the later duplicate
    }

    [Fact] // DEV-03 — the FIRST state wins, deterministically
    public void DuplicateSerial_KeepsTheFirstState()
    {
        const string output = """
            List of devices attached
            R58M12ABCDE   unauthorized
            R58M12ABCDE   device
            """;

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("unauthorized", d.State);
    }

    [Fact] // DEV-03 — the parser must never throw on arbitrary input
    public void ParserNeverThrowsOnArbitraryText()
    {
        string[] hostile =
        [
            "", "\0", "\r\n\r\n", "   ", new string('x', 10_000),
            "* \0 banner", "device", "a b c d e f g h", "::::", "\t\t\t",
            "List of devices attached\n\0\0\0\n", "😀 😀", "---",
        ];

        foreach (var text in hostile)
        {
            var ex = Record.Exception(() => AdbService.ParseDeviceList(text));
            Assert.Null(ex);
        }
    }
}
