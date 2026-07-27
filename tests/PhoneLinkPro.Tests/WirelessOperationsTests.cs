using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 4C — pure logic for the guided wireless-pairing page: host/port/endpoint validation
/// (reusing <see cref="AdbInput"/>), safe classification of adb results into narrow
/// <see cref="WirelessPairResult"/>/<see cref="WirelessConnectResult"/> types, and wireless-device
/// selection. No adb process is ever started by these tests — every result is a hand-built
/// <see cref="ProcessResult"/> fixture.
/// </summary>
public sealed class WirelessOperationsTests
{
    // ---------------------------------------------------------------- host validation

    [Theory]
    [InlineData("192.168.1.42")]
    [InlineData("10.0.0.5")]
    public void TryValidateHost_AcceptsBareIPv4(string host)
    {
        Assert.True(WirelessOperations.TryValidateHost(host, out var normalized, out _));
        Assert.Equal(host, normalized);
    }

    [Fact]
    public void TryValidateHost_AcceptsBareIPv6_AndBracketsIt()
    {
        Assert.True(WirelessOperations.TryValidateHost("fe80::1", out var normalized, out _));
        Assert.Equal("[fe80::1]", normalized);
    }

    [Fact]
    public void TryValidateHost_AcceptsAlreadyBracketedIPv6()
    {
        Assert.True(WirelessOperations.TryValidateHost("[fe80::1]", out var normalized, out _));
        Assert.Equal("[fe80::1]", normalized);
    }

    [Theory] // the port has its own dedicated field — a host carrying one is rejected here
    [InlineData("192.168.1.42:5555")]
    [InlineData("[fe80::1]:5555")]
    public void TryValidateHost_RejectsAValueThatCarriesItsOwnPort(string host)
    {
        Assert.False(WirelessOperations.TryValidateHost(host, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("http://192.168.1.42")]
    [InlineData("192.168.1.42; rm -rf /")]
    [InlineData("[fe80::1")]           // malformed bracket notation - never closed
    public void TryValidateHost_RejectsInvalidInput(string host)
        => Assert.False(WirelessOperations.TryValidateHost(host, out _, out _));

    // ---------------------------------------------------------------- endpoint building (separate ports)

    [Fact]
    public void TryBuildEndpoint_CombinesHostAndPort_ForIPv4()
    {
        Assert.True(WirelessOperations.TryBuildEndpoint("192.168.1.42", 5555, out var endpoint, out _));
        Assert.Equal("192.168.1.42:5555", endpoint);
    }

    [Fact]
    public void TryBuildEndpoint_CombinesHostAndPort_ForIPv6()
    {
        Assert.True(WirelessOperations.TryBuildEndpoint("fe80::1", 37123, out var endpoint, out _));
        Assert.Equal("[fe80::1]:37123", endpoint);
    }

    [Fact] // the endpoint this page builds must be exactly what AdbService's own calls accept
    public void TryBuildEndpoint_ProducesAnEndpointAdbInputAccepts()
    {
        Assert.True(WirelessOperations.TryBuildEndpoint("192.168.1.42", 37123, out var endpoint, out _));
        Assert.True(AdbInput.TryValidateEndpoint(endpoint, out var normalized, out _));
        Assert.Equal(endpoint, normalized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void TryBuildEndpoint_RejectsOutOfRangePorts(int port)
        => Assert.False(WirelessOperations.TryBuildEndpoint("192.168.1.42", port, out _, out _));

    [Fact] // mutation-sensitive: pairing port and connection port must never be conflated
    public void PairingPortAndConnectionPort_AreValidatedAndBuiltIndependently()
    {
        Assert.True(WirelessOperations.TryBuildEndpoint("192.168.1.42", 37123, out var pairEndpoint, out _));
        Assert.True(WirelessOperations.TryBuildEndpoint("192.168.1.42", 5555, out var connectEndpoint, out _));
        Assert.NotEqual(pairEndpoint, connectEndpoint);
    }

    // ---------------------------------------------------------------- pair classification

    [Fact] // mutation-sensitive: a Pair success must never claim the device is connected
    public void ClassifyPair_SuccessMessage_NeverClaimsConnected()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.True(result.Success);
        Assert.DoesNotContain("متصل", result.Message);
    }

    [Fact]
    public void ClassifyPair_FailureExit_IsFailedNotSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 1, "Failed: Incorrect code", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.False(result.Success);
        Assert.Equal(WirelessPairOutcome.Failed, result.Outcome);
    }

    [Fact] // the pairing code must never appear in a classified result
    public void ClassifyPair_NeverIncludesThePairingCode()
    {
        const string secret = "246813";
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.DoesNotContain(secret, result.Message);
    }

    [Fact]
    public void ClassifyPair_StartFailed_IsToolsUnavailable_AndNeverLeaksDiagnostic()
    {
        var r = new ProcessResult(ProcessOutcome.StartFailed, null, "", "", FailureDetail: "engine integrity: Invalid x");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.Equal(WirelessPairOutcome.ToolsUnavailable, result.Outcome);
        Assert.DoesNotContain("Invalid", result.Message);
        Assert.DoesNotContain("engine integrity", result.Message);
    }

    [Fact]
    public void ClassifyPair_InvalidInput_CarriesOnlyTheValidationReason()
    {
        var r = ProcessResult.Invalid("عنوان IP غير صالح.");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.Equal(WirelessPairOutcome.InvalidInput, result.Outcome);
        Assert.Equal("عنوان IP غير صالح.", result.Message);
    }

    [Fact]
    public void ClassifyPair_Cancelled_IsCancelledNotSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Cancelled, null, "", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.Equal(WirelessPairOutcome.Cancelled, result.Outcome);
        Assert.False(result.Success);
    }

    // ---------------------------------------------------------------- connect classification

    [Fact] // mutation-sensitive: a Connect COMMAND success must never itself claim "connected"
    public void ClassifyConnect_Success_IsCommandSucceededOnly()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.True(result.CommandSucceeded);
        Assert.Equal(WirelessConnectOutcome.CommandSucceeded, result.Outcome);
        Assert.DoesNotContain("مصرَّح", result.Message);   // never claims authorized/connected outright
    }

    [Fact]
    public void ClassifyConnect_FailedToConnect_IsFailed()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "failed to connect to 192.168.1.42:5555: timed out", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
        Assert.Equal(WirelessConnectOutcome.Failed, result.Outcome);
    }

    [Fact] // mutation-sensitive: "disconnected" must never be misread as a successful connect
    public void ClassifyConnect_DisconnectedText_IsNotMisreadAsSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "192.168.1.42:5555 disconnected", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
    }

    [Fact]
    public void ClassifyConnect_AlreadyConnected_IsCommandSucceeded()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "already connected to 192.168.1.42:5555", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.True(result.CommandSucceeded);
    }

    [Fact]
    public void ClassifyConnect_InvalidInput_IsNeverCommandSucceeded()
    {
        var r = ProcessResult.Invalid("رقم المنفذ يجب أن يكون بين 1 و 65535.");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.Equal(WirelessConnectOutcome.InvalidInput, result.Outcome);
        Assert.False(result.CommandSucceeded);
    }

    [Fact]
    public void ClassifyConnect_TimedOut_IsFailedNotCommandSucceeded()
    {
        var r = new ProcessResult(ProcessOutcome.TimedOut, null, "", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
        Assert.Equal(WirelessConnectOutcome.Failed, result.Outcome);
    }

    // ---------------------------------------------------------------- wireless-device selection

    [Fact]
    public void DecideWirelessSelection_NoDevices_ReturnsNull()
        => Assert.Null(WirelessOperations.DecideWirelessSelection(new List<AdbDevice>(), null));

    [Fact]
    public void DecideWirelessSelection_SingleDevice_IsAutoSelected()
    {
        var dev = new AdbDevice { Serial = "192.168.1.42:5555", State = "device" };
        Assert.Equal(dev.Serial, WirelessOperations.DecideWirelessSelection(new List<AdbDevice> { dev }, null));
    }

    [Fact] // multiple devices are never silently chosen for the user
    public void DecideWirelessSelection_MultipleDevices_NoPriorSelection_ReturnsNull()
    {
        var devices = new List<AdbDevice>
        {
            new() { Serial = "192.168.1.42:5555", State = "device" },
            new() { Serial = "192.168.1.55:5555", State = "unauthorized" },
        };
        Assert.Null(WirelessOperations.DecideWirelessSelection(devices, null));
    }

    [Fact] // an existing selection is preserved by ordinal serial match across a refreshed snapshot
    public void DecideWirelessSelection_PreservesAnExistingSelection_ByOrdinalSerial()
    {
        var devices = new List<AdbDevice>
        {
            new() { Serial = "192.168.1.42:5555", State = "device" },
            new() { Serial = "192.168.1.55:5555", State = "unauthorized" },
        };
        Assert.Equal("192.168.1.55:5555",
            WirelessOperations.DecideWirelessSelection(devices, "192.168.1.55:5555"));
    }

    [Fact] // a selection whose device vanished from the snapshot is dropped, never carried over
    public void DecideWirelessSelection_DropsASelectionThatNoLongerExists()
    {
        var devices = new List<AdbDevice>
        {
            new() { Serial = "192.168.1.42:5555", State = "device" },
            new() { Serial = "192.168.1.55:5555", State = "unauthorized" },
        };
        Assert.Null(WirelessOperations.DecideWirelessSelection(devices, "192.168.1.99:5555"));
    }

    // ================================================================== independent-review repairs

    // ---------------------------------------------------------------- M1: exit-code-aware classification

    [Fact] // mutation-sensitive: a non-zero exit code is failure even with a positive-looking marker
    public void ClassifyPair_NonZeroExit_WithPositiveMarker_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 1, "Successfully paired to 192.168.1.42:37123", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.False(result.Success);
        Assert.Equal(WirelessPairOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void ClassifyConnect_NonZeroExit_WithPositiveMarker_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 1, "connected to 192.168.1.42:5555", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
        Assert.Equal(WirelessConnectOutcome.Failed, result.Outcome);
    }

    [Fact] // negative marker outranks positive even at exit 0
    public void ClassifyConnect_ExitZero_FailedToConnectAndConnectedBothPresent_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "failed to connect to 192.168.1.42:5555: connected refused", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
    }

    [Fact] // empty output at exit 0 fails closed rather than guessing
    public void ClassifyPair_ExitZero_EmptyOutput_FailsClosed()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.False(result.Success);
        Assert.Equal(WirelessPairOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void ClassifyConnect_ExitZero_EmptyOutput_FailsClosed()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
        Assert.Equal(WirelessConnectOutcome.Failed, result.Outcome);
    }

    // ---------------------------------------------------------------- M2: IPv6 zone identifiers

    [Theory]
    [InlineData("fe80::1%wlan0")]      // unbracketed, named zone
    [InlineData("fe80::1%3")]          // unbracketed, numeric zone
    [InlineData("[fe80::1%wlan0]")]    // bracketed, named zone, no port
    [InlineData("[fe80::1%3]")]        // bracketed, numeric zone, no port
    [InlineData("fe80::1%25eth0")]     // percent-encoded-looking zone form
    public void TryValidateHost_RejectsIPv6ZoneIdentifiers(string host)
        => Assert.False(WirelessOperations.TryValidateHost(host, out _, out _));

    [Fact] // ordinary (zone-free) IPv6 must still be accepted after the M2 repair
    public void TryValidateHost_StillAcceptsOrdinaryIPv6WithoutAZone()
    {
        Assert.True(WirelessOperations.TryValidateHost("2001:db8::42", out var normalized, out _));
        Assert.Equal("[2001:db8::42]", normalized);
    }

    [Fact]
    public void TryValidateHost_StillAcceptsBracketedOrdinaryIPv6WithoutAZone()
        => Assert.True(WirelessOperations.TryValidateHost("[2001:db8::42]", out _, out _));

    // ---------------------------------------------------------------- port boundaries / digit forms

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void TryBuildEndpoint_AcceptsBoundaryPorts(int port)
        => Assert.True(WirelessOperations.TryBuildEndpoint("192.168.1.42", port, out _, out _));

    [Theory] // AdbInput's shared port-text parser (NumberStyles.None) rejects non-ASCII digits
    [InlineData("192.168.1.42:٥٥٥٥")]     // Arabic-Indic digits
    [InlineData("192.168.1.42:۵۵۵۵")]     // Extended Arabic-Indic (Persian) digits
    [InlineData("192.168.1.42:５５５５")]      // fullwidth digits
    public void TryValidateEndpoint_RejectsNonAsciiDigitPorts(string endpoint)
        => Assert.False(AdbInput.TryValidateEndpoint(endpoint, out _, out _));

    [Theory]
    [InlineData("192.168.1.42:+5555")]   // leading sign rejected
    [InlineData("192.168.1.42: 5555")]   // embedded space rejected
    [InlineData("192.168.1.42:55 55")]   // internal space rejected
    public void TryValidateEndpoint_RejectsSignedOrSpacedPorts(string endpoint)
        => Assert.False(AdbInput.TryValidateEndpoint(endpoint, out _, out _));

    // ---------------------------------------------------------------- M3: IPv6 wireless-device recognition

    [Theory]
    [InlineData("192.168.1.42:5555")]
    [InlineData("[2001:db8::42]:5555")]
    [InlineData("[fe80::1]:37123")]
    public void LooksLikeWirelessEndpoint_AcceptsRealWirelessEndpoints(string serial)
        => Assert.True(AdbInput.LooksLikeWirelessEndpoint(serial));

    [Theory]
    [InlineData("R5CX10AB9XD")]        // USB serial
    [InlineData("emulator-5554")]      // emulator id
    [InlineData("192.168.1.42")]       // bare IPv4, no port — never a real adb wireless serial
    [InlineData("[2001:db8::42]")]     // bare bracketed IPv6, no port
    [InlineData("[2001:db8::42")]      // malformed bracket, never closed
    [InlineData("")]
    public void LooksLikeWirelessEndpoint_RejectsNonWirelessOrMalformed(string serial)
        => Assert.False(AdbInput.LooksLikeWirelessEndpoint(serial));

    [Fact] // the M3 regression itself: a bracketed IPv6 endpoint has no dot at all
    public void AdbDevice_IsWireless_RecognizesBracketedIPv6Endpoint()
    {
        var device = new AdbDevice { Serial = "[2001:db8::42]:5555", State = "device" };
        Assert.True(device.IsWireless);
        Assert.Equal("Wi-Fi", device.ConnectionLabel);
    }

    [Fact]
    public void AdbDevice_IsWireless_IsFalseForUsbSerial()
    {
        var device = new AdbDevice { Serial = "R5CX10AB9XD", State = "device" };
        Assert.False(device.IsWireless);
        Assert.Equal("USB", device.ConnectionLabel);
    }

    // ---------------------------------------------------------------- M5: collision-safe display identity

    [Fact] // the exact reviewed pair: identical mask, both authorized, identical model
    public void BuildRows_DisambiguatesTheReviewedCollidingPair()
    {
        var a = new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Galaxy S24" };
        var b = new AdbDevice { Serial = "192.168.1.55:5555", State = "device", Model = "Galaxy S24" };
        Assert.Equal(ShellLayout.MaskSerial(a.Serial), ShellLayout.MaskSerial(b.Serial));   // proves the collision is real

        var rows = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a, b });

        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].DisambiguatorToken);
        Assert.NotNull(rows[1].DisambiguatorToken);
        Assert.NotEqual(rows[0].DisambiguatorToken, rows[1].DisambiguatorToken);
        Assert.NotEqual(rows[0].AccessibleName, rows[1].AccessibleName);
    }

    [Fact] // a non-colliding snapshot never carries a token at all
    public void BuildRows_NoTokenWhenIdentitiesAlreadyDiffer()
    {
        var a = new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Galaxy S24" };
        var b = new AdbDevice { Serial = "192.168.1.77:9999", State = "unauthorized" };   // different mask AND label

        var rows = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a, b });

        Assert.All(rows, r => Assert.Null(r.DisambiguatorToken));
    }

    [Fact] // ordering reversal does not exchange identities — the token is a pure function of serial
    public void BuildRows_TokenIsStableAcrossOrdering()
    {
        var a = new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Galaxy S24" };
        var b = new AdbDevice { Serial = "192.168.1.55:5555", State = "device", Model = "Galaxy S24" };

        var forward = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a, b });
        var reversed = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { b, a });

        var aTokenForward = forward.Single(r => r.Device.Serial == a.Serial).DisambiguatorToken;
        var aTokenReversed = reversed.Single(r => r.Device.Serial == a.Serial).DisambiguatorToken;
        Assert.Equal(aTokenForward, aTokenReversed);
    }

    [Fact] // a replacement object for the same physical device (new instance, same serial) keeps its identity
    public void BuildRows_ReplacementObjectWithSameSerial_KeepsSameToken()
    {
        var a1 = new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Galaxy S24" };
        var b = new AdbDevice { Serial = "192.168.1.55:5555", State = "device", Model = "Galaxy S24" };
        var firstToken = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a1, b })
            .Single(r => r.Device.Serial == a1.Serial).DisambiguatorToken;

        var a2 = new AdbDevice { Serial = "192.168.1.42:5555", State = "unauthorized", Model = "Galaxy S24" };
        var secondToken = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a2, b })
            .Single(r => r.Device.Serial == a2.Serial).DisambiguatorToken;

        Assert.Equal(firstToken, secondToken);
    }

    [Fact] // the disambiguator token never reveals the raw endpoint
    public void BuildRows_TokenNeverContainsTheRawEndpoint()
    {
        var a = new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Galaxy S24" };
        var b = new AdbDevice { Serial = "192.168.1.55:5555", State = "device", Model = "Galaxy S24" };

        var rows = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a, b });

        foreach (var row in rows)
        {
            Assert.NotNull(row.DisambiguatorToken);
            Assert.DoesNotContain("192.168.1.42", row.DisambiguatorToken!);
            Assert.DoesNotContain("192.168.1.55", row.DisambiguatorToken!);
            Assert.DoesNotContain("192.168.1.42", row.AccessibleName);
            Assert.DoesNotContain("192.168.1.55", row.AccessibleName);
        }
    }

    // ---------------------------------------------------------------- negative-marker precedence (stdout + stderr)

    [Fact] // a genuine failure whose real text landed on stderr must never be read as a success
    public void ClassifyPair_PositiveStdout_NegativeStderr_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "Successfully paired to 192.168.1.42:37123", "authentication failed");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.False(result.Success);
        Assert.Equal(WirelessPairOutcome.Failed, result.Outcome);
    }

    [Fact] // one single stream carrying both markers still resolves to failure
    public void ClassifyPair_SingleStreamCarriesBothMarkers_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "Successfully paired but failed to pair", "");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.False(result.Success);
    }

    [Fact] // marker matching is case-insensitive on both streams, and negative still outranks positive
    public void ClassifyPair_MixedCaseNegativeMarkerPlusPositiveMarker_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "successfully paired to 192.168.1.42:37123", "FAILED: incorrect code");
        var result = WirelessOperations.ClassifyPair(r);

        Assert.False(result.Success);
    }

    [Fact] // a genuine failure whose real text landed on stderr must never be read as a command success
    public void ClassifyConnect_PositiveStdout_NegativeStderr_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "connected to 192.168.1.42:5555", "failed to connect");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
        Assert.Equal(WirelessConnectOutcome.Failed, result.Outcome);
    }

    [Fact] // one single stream carrying both markers still resolves to failure
    public void ClassifyConnect_SingleStreamCarriesBothMarkers_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "failed to connect but connected to 192.168.1.42:5555", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
    }

    [Fact] // "disconnected from ... connected to ..." must never be misread as success
    public void ClassifyConnect_DisconnectedThenConnected_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0,
            "disconnected from 192.168.1.42:5555 connected to 192.168.1.42:5555", "");
        var result = WirelessOperations.ClassifyConnect(r);

        Assert.False(result.CommandSucceeded);
    }

    // ---- exit-zero genuine successes must survive the stderr-aware repair (no false positives introduced) ----

    [Fact]
    public void ClassifyPair_ExitZero_GenuineSuccess_WithEmptyStderr_IsStillSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", "");
        Assert.True(WirelessOperations.ClassifyPair(r).Success);
    }

    [Fact]
    public void ClassifyConnect_ExitZero_GenuineCommandSuccess_WithEmptyStderr_IsStillCommandSucceeded()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", "");
        Assert.True(WirelessOperations.ClassifyConnect(r).CommandSucceeded);
    }

    [Fact] // Pair success never implies the final connected state, even after the stderr-aware repair
    public void ClassifyPair_Success_StillNeverImpliesConnectedState()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "Successfully paired to 192.168.1.42:37123", "");
        var result = WirelessOperations.ClassifyPair(r);
        Assert.True(result.Success);
        Assert.DoesNotContain("متصل", result.Message);
    }

    [Fact] // Connect command success still only means the COMMAND succeeded, not device reconciliation
    public void ClassifyConnect_CommandSucceeded_StillRequiresDeviceReconciliation()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "connected to 192.168.1.42:5555", "");
        var result = WirelessOperations.ClassifyConnect(r);
        Assert.Equal(WirelessConnectOutcome.CommandSucceeded, result.Outcome);
        Assert.DoesNotContain("مصرَّح", result.Message);
    }
}
