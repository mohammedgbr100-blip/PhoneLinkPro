using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 3 — enrichment parsing: getprop / wm size / dumpsys battery / wlan0 address.
/// Known weaknesses are characterized, not repaired.
/// </summary>
public class DeviceEnrichmentParsingTests
{
    private static AdbDevice Device() => new() { Serial = "R58M12ABCDE", State = "device" };

    // ---------------------------------------------------------------- getprop

    [Fact] // [REGRESSION]
    public void Props_ModelBrandAndVersion_AreApplied()
    {
        var d = Device();

        AdbService.ApplyProps(d, "SM-A525F\nsamsung\n13\n");

        Assert.Equal("SM-A525F", d.Model);
        Assert.Equal("Samsung", d.Brand);       // capitalized
        Assert.Equal("13", d.AndroidVersion);
        Assert.Equal("Samsung SM-A525F", d.DisplayName);
    }

    [Fact] // [REGRESSION] — CRLF from the adb pipe is trimmed
    public void Props_WithCarriageReturns_AreTrimmed()
    {
        var d = Device();

        AdbService.ApplyProps(d, "SM-A525F\r\nsamsung\r\n13\r\n");

        Assert.Equal("SM-A525F", d.Model);
        Assert.Equal("Samsung", d.Brand);
        Assert.Equal("13", d.AndroidVersion);
    }

    [Fact] // [REGRESSION] — a pre-existing model survives empty getprop output
    public void Props_EmptyOutput_LeavesExistingValuesUntouched()
    {
        var d = Device();
        d.Model = "From devices -l";

        AdbService.ApplyProps(d, "");

        Assert.Equal("From devices -l", d.Model);
        Assert.Equal("", d.Brand);
        Assert.Equal("", d.AndroidVersion);
    }

    [Fact] // [CHARACTERIZATION] — blank lines are dropped before positional assignment,
           // so a device with an empty brand shifts the version into the brand slot.
    public void Props_WithMissingMiddleValue_ShiftsRemainingFieldsUp()
    {
        var d = Device();

        // getprop printed: model, <empty brand>, release
        AdbService.ApplyProps(d, "SM-A525F\n\n13\n");

        Assert.Equal("SM-A525F", d.Model);
        // DEFECT-SHAPED: "13" lands in Brand because the empty line was removed.
        Assert.Equal("13", d.Brand);
        Assert.Equal("", d.AndroidVersion);
    }

    [Fact] // [CHARACTERIZATION] — extra leading output shifts every field
    public void Props_WithUnexpectedLeadingLine_MisassignsAllFields()
    {
        var d = Device();

        AdbService.ApplyProps(d, "WARNING: linker warning\nSM-A525F\nsamsung\n13\n");

        Assert.Equal("WARNING: linker warning", d.Model);
        Assert.Equal("SM-A525F", d.Brand);
        Assert.Equal("samsung", d.AndroidVersion);
    }

    [Fact] // [REGRESSION]
    public void Props_OnlyModelPresent_AssignsModelOnly()
    {
        var d = Device();

        AdbService.ApplyProps(d, "Pixel 7\n");

        Assert.Equal("Pixel 7", d.Model);
        Assert.Equal("", d.Brand);
        Assert.Equal("", d.AndroidVersion);
    }

    [Theory] // [REGRESSION] — Capitalize only touches the first character
    [InlineData("samsung", "Samsung")]
    [InlineData("xiaomi", "Xiaomi")]
    [InlineData("OnePlus", "OnePlus")]
    [InlineData("", "")]
    [InlineData("a", "A")]
    public void Capitalize_UppercasesFirstCharacterOnly(string input, string expected)
        => Assert.Equal(expected, AdbService.Capitalize(input));

    // ---------------------------------------------------------------- wm size

    [Fact] // [REGRESSION]
    public void Resolution_PhysicalSize_IsParsed()
        => Assert.Equal("1080x2400", AdbService.ParseResolution("Physical size: 1080x2400\n"));

    [Fact] // [CHARACTERIZATION] — when a device is overridden, adb prints BOTH lines and
           // the regex takes the FIRST match (physical), not the effective override size.
    public void Resolution_WithOverride_ReturnsPhysicalNotOverride()
    {
        const string output = "Physical size: 1440x3040\nOverride size: 1080x2280\n";

        Assert.Equal("1440x3040", AdbService.ParseResolution(output));
    }

    [Fact] // [REGRESSION]
    public void Resolution_NoMatch_ReturnsNull()
        => Assert.Null(AdbService.ParseResolution("cmd: Failure calling service window\n"));

    [Fact] // [REGRESSION]
    public void Resolution_EmptyOutput_ReturnsNull()
        => Assert.Null(AdbService.ParseResolution(""));

    // ---------------------------------------------------------------- battery

    [Fact] // [REGRESSION]
    public void Battery_StandardDumpsys_IsParsed()
    {
        const string output = """
            Current Battery Service state:
              AC powered: false
              USB powered: true
              status: 2
              health: 2
              level: 87
              scale: 100
            """;

        Assert.Equal(87, AdbService.ParseBatteryLevel(output));
    }

    [Theory] // [REGRESSION] — OEM spacing variants still parse
    [InlineData("level: 42", 42)]
    [InlineData("level:42", 42)]
    [InlineData("  level:   7", 7)]
    [InlineData("level: 100", 100)]
    [InlineData("level: 0", 0)]
    public void Battery_SpacingVariants_AreParsed(string output, int expected)
        => Assert.Equal(expected, AdbService.ParseBatteryLevel(output));

    [Fact] // [REGRESSION]
    public void Battery_NoLevel_ReturnsMinusOne()
        => Assert.Equal(-1, AdbService.ParseBatteryLevel("Permission denial\n"));

    [Fact] // [REGRESSION]
    public void Battery_EmptyOutput_ReturnsMinusOne()
        => Assert.Equal(-1, AdbService.ParseBatteryLevel(""));

    [Fact] // [CHARACTERIZATION] — an OEM line such as "Max charging level: 80" appears
           // before the real level, and the first regex match wins.
    public void Battery_OemPrefixedLevelLine_WinsOverTheRealLevel()
    {
        const string output = """
            Current Battery Service state:
              Max charging level: 80
              level: 55
            """;

        // DEFECT-SHAPED: reports the OEM cap (80), not the actual charge (55).
        Assert.Equal(80, AdbService.ParseBatteryLevel(output));
    }

    [Fact] // [CHARACTERIZATION] — an out-of-range integer throws instead of returning -1
    public void Battery_OverlongNumber_ThrowsOverflow()
    {
        // int.Parse is unguarded, so absurd output crashes the enrichment path.
        Assert.Throws<OverflowException>(
            () => AdbService.ParseBatteryLevel("level: 99999999999999999999\n"));
    }

    // ---------------------------------------------------------------- wlan0 address

    [Fact] // [REGRESSION]
    public void WifiIp_FromAddrShow_IsParsed()
    {
        const string output =
            "35: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500\n" +
            "    inet 192.168.1.42/24 brd 192.168.1.255 scope global wlan0\n";

        Assert.Equal("192.168.1.42", AdbService.ParseWifiIpFromAddr(output));
    }

    [Fact] // [REGRESSION]
    public void WifiIp_FromAddrShow_NoMatch_ReturnsNull()
        => Assert.Null(AdbService.ParseWifiIpFromAddr("Device \"wlan0\" does not exist.\n"));

    [Fact] // [REGRESSION]
    public void WifiIp_FromRouteFallback_IsParsed()
    {
        const string output =
            "192.168.1.0/24 dev wlan0 proto kernel scope link src 192.168.1.42\n";

        Assert.Equal("192.168.1.42", AdbService.ParseWifiIpFromRoute(output));
    }

    [Fact] // [REGRESSION]
    public void WifiIp_FromRouteFallback_NoMatch_ReturnsNull()
        => Assert.Null(AdbService.ParseWifiIpFromRoute("default via 10.0.0.1 dev rmnet0\n"));
}
