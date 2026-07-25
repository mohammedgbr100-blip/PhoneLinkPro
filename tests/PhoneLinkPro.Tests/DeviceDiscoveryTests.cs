using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// DEV-01 — the discovery result model and the "is this really a device listing?" gate.
/// </summary>
public class DeviceDiscoveryTests
{
    // ---------------------------------------------------------------- result model

    [Fact]
    public void SuccessResult_IsAuthoritative()
    {
        var r = DeviceDiscoveryResult.Success([new AdbDevice { Serial = "A", State = "device" }]);

        Assert.Equal(DeviceDiscoveryOutcome.Success, r.Outcome);
        Assert.True(r.IsSuccess);
        Assert.False(r.IsUnknown);
        Assert.Single(r.Devices);
        Assert.Null(r.Diagnostic);
    }

    [Fact] // the distinction DEV-01 is about
    public void EmptySuccess_MeansNoDevices_NotUnknown()
    {
        var r = DeviceDiscoveryResult.Success([]);

        Assert.True(r.IsSuccess);
        Assert.Empty(r.Devices);
        Assert.False(r.IsUnknown);
    }

    [Theory]
    [InlineData(DeviceDiscoveryOutcome.TimedOut)]
    [InlineData(DeviceDiscoveryOutcome.Cancelled)]
    [InlineData(DeviceDiscoveryOutcome.StartFailed)]
    [InlineData(DeviceDiscoveryOutcome.AdbError)]
    [InlineData(DeviceDiscoveryOutcome.Unusable)]
    public void FailureResults_AreUnknownAndCarryNoDevices(DeviceDiscoveryOutcome outcome)
    {
        var r = DeviceDiscoveryResult.Failure(outcome, "why");

        Assert.True(r.IsUnknown);
        Assert.False(r.IsSuccess);
        Assert.Empty(r.Devices);
        Assert.Equal("why", r.Diagnostic);
    }

    [Fact]
    public void Results_CarryATimestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var r = DeviceDiscoveryResult.Success([]);

        Assert.True(r.Timestamp >= before);
        Assert.True(r.Timestamp <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    // ---------------------------------------------------------------- output classification

    [Theory] // recognisable listings
    [InlineData("List of devices attached\n")]
    [InlineData("List of devices attached\nR58M12ABCDE\tdevice\n")]
    [InlineData("")]                                            // server started, nothing yet
    [InlineData("\n\n")]
    [InlineData("* daemon not running; starting now at tcp:5037\n* daemon started successfully\n")]
    [InlineData("R58M12ABCDE   device\n")]                      // header suppressed but valid
    public void RecognisableOutput_IsTreatedAsAListing(string stdout)
        => Assert.True(AdbService.LooksLikeDeviceListing(stdout));

    [Theory] // output that cannot be understood must NOT silently mean "no devices"
    [InlineData("adb server version (41) doesn't match this client (39); killing...")]
    [InlineData("error: could not install smartsocket listener")]
    [InlineData("some entirely unexpected wall of text that is not a listing at all")]
    public void UnrecognisableOutput_IsNotTreatedAsAListing(string stdout)
        => Assert.False(AdbService.LooksLikeDeviceListing(stdout));

    [Fact] // the gate must never throw
    public void Classification_NeverThrows()
    {
        foreach (var text in new[] { "", "\0", new string('x', 5000), "😀", "---" })
        {
            var ex = Record.Exception(() => AdbService.LooksLikeDeviceListing(text));
            Assert.Null(ex);
        }
    }

    // ---------------------------------------------------------------- real call safety

    [Fact] // adb is absent from the test output; the call must report StartFailed, not throw
    public async Task DiscoverDevices_WithoutAdb_ReportsFailureRatherThanEmpty()
    {
        var result = await AdbService.DiscoverDevicesAsync();

        Assert.True(result.IsUnknown, $"expected a failure outcome, got {result.Outcome}");
        Assert.Empty(result.Devices);
        Assert.NotEqual(DeviceDiscoveryOutcome.Success, result.Outcome);
    }

    [Fact] // an already-cancelled token is reported as Cancelled, not as an error
    public async Task DiscoverDevices_WithCancelledToken_ReportsCancelledOrStartFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await AdbService.DiscoverDevicesAsync(cts.Token);

        Assert.True(result.IsUnknown);
        Assert.Contains(result.Outcome,
            new[] { DeviceDiscoveryOutcome.Cancelled, DeviceDiscoveryOutcome.StartFailed });
    }
}
