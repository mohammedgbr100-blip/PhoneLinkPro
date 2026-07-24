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

    [Fact] // [REGRESSION]
    public void UnknownState_IsPassedThroughUnchanged()
    {
        const string output = "List of devices attached\nSERIAL123   some-future-state\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("some-future-state", d.State);
        Assert.False(d.IsAuthorized);
        // StateLabel falls through to the raw state for anything unmapped.
        Assert.Equal("some-future-state", d.StateLabel);
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

    [Fact] // [CHARACTERIZATION] — any 2+ token line becomes a device today
    public void MalformedLine_WithTwoTokens_IsAcceptedAsADevice()
    {
        const string output = "List of devices attached\nthis is not a device line\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        // Current behaviour: blindly takes token[0] as serial and token[1] as state.
        Assert.Equal("this", d.Serial);
        Assert.Equal("is", d.State);
    }

    [Fact] // [REGRESSION] — tab-separated output still parses
    public void TabSeparatedColumns_AreParsed()
    {
        const string output = "List of devices attached\nR58M12ABCDE\tdevice\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("R58M12ABCDE", d.Serial);
        Assert.Equal("device", d.State);
    }

    // ================================================================
    //  DEV-03 — daemon banner lines are parsed as phantom devices
    // ================================================================
    // `adb devices -l` emits a start-up banner the first time the daemon launches.
    // ParseDeviceList only skips the "List of devices" header, so each banner line that
    // happens to contain two or more whitespace-separated tokens becomes a fake device.
    //
    // These tests PIN THE DEFECT so Batch 1D can change it deliberately. They assert the
    // wrong-but-current output; they do not assert that it is correct.

    [Fact] // [CHARACTERIZATION] DEV-03 — replace in Batch 1D
    public void DEV03_DaemonNotRunningBanner_IsParsedAsPhantomDevice()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            R58M12ABCDE            device usb:1-4 model:SM_A525F
            """;

        var devices = AdbService.ParseDeviceList(output);

        // DEFECT: three devices are reported when only one is attached.
        Assert.Equal(3, devices.Count);

        Assert.Equal("*", devices[0].Serial);
        Assert.Equal("daemon", devices[0].State);
        Assert.Equal("*", devices[1].Serial);
        Assert.Equal("daemon", devices[1].State);

        Assert.Equal("R58M12ABCDE", devices[2].Serial);
        Assert.Equal("device", devices[2].State);

        // The phantoms are not "authorized", so they do not become the selected device,
        // but they DO reach the UI list and DO change the DeviceWatcher signature.
        Assert.Equal(1, devices.Count(d => d.IsAuthorized));
    }

    [Fact] // [CHARACTERIZATION] DEV-03 — replace in Batch 1D
    public void DEV03_BannerOnlyOutput_ReportsPhantomDevicesInsteadOfNone()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            """;

        var devices = AdbService.ParseDeviceList(output);

        // DEFECT: should be empty; currently two phantom entries.
        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal("*", d.Serial));
    }

    [Fact] // [CHARACTERIZATION] DEV-03 — replace in Batch 1D
    public void DEV03_AdbErrorText_IsParsedAsPhantomDevice()
    {
        // Text adb can emit into stdout on a bad server handshake.
        const string output = "adb server version (41) doesn't match this client (39); killing...\n";

        var d = Assert.Single(AdbService.ParseDeviceList(output));

        Assert.Equal("adb", d.Serial);
        Assert.Equal("server", d.State);
    }

    [Fact(Skip = "DEV-03: daemon banner lines are parsed as phantom devices. " +
                 "This asserts the CORRECT post-fix behaviour and must be un-skipped " +
                 "when the parser is hardened. Target batch: 1D.")]
    public void DEV03_DaemonBannerShouldBeIgnored_TargetBatch1D()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            R58M12ABCDE            device usb:1-4 model:SM_A525F
            """;

        var devices = AdbService.ParseDeviceList(output);

        var d = Assert.Single(devices);
        Assert.Equal("R58M12ABCDE", d.Serial);
    }
}
