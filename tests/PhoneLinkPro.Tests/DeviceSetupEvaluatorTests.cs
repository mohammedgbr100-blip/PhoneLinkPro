using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>Batch 4B — pure evaluation rules behind the device-setup wizard.</summary>
public sealed class DeviceSetupEvaluatorTests
{
    [Fact]
    public void Evaluate_ToolsUnavailable_WinsOverAnyDeviceState()
    {
        var device = new AdbDevice { Serial = "s1", State = "device" };
        Assert.Equal(DeviceSetupStage.ToolsUnavailable, DeviceSetupEvaluator.Evaluate(false, device));
        Assert.Equal(DeviceSetupStage.ToolsUnavailable, DeviceSetupEvaluator.Evaluate(false, null));
    }

    [Fact]
    public void Evaluate_NoFocusedDevice_IsNoDevice()
        => Assert.Equal(DeviceSetupStage.NoDevice, DeviceSetupEvaluator.Evaluate(true, null));

    [Fact]
    public void Evaluate_AuthorizedDevice_IsReady()
    {
        var device = new AdbDevice { Serial = "s1", State = "device" };
        Assert.Equal(DeviceSetupStage.Ready, DeviceSetupEvaluator.Evaluate(true, device));
    }

    [Fact]
    public void Evaluate_UnauthorizedDevice_IsUnauthorized()
    {
        var device = new AdbDevice { Serial = "s1", State = "unauthorized" };
        Assert.Equal(DeviceSetupStage.Unauthorized, DeviceSetupEvaluator.Evaluate(true, device));
    }

    [Theory] // never fabricated as Ready or Unauthorized — every other known adb state is Offline
    [InlineData("offline")]
    [InlineData("bootloader")]
    [InlineData("recovery")]
    [InlineData("sideload")]
    [InlineData("host")]
    [InlineData("authorizing")]
    [InlineData("connecting")]
    [InlineData("rescue")]
    [InlineData("unknown")]
    [InlineData("no")]
    [InlineData("some-future-adb-state")]
    public void Evaluate_AnyNonReadyNonUnauthorizedState_IsOffline(string state)
    {
        var device = new AdbDevice { Serial = "s1", State = state };
        Assert.Equal(DeviceSetupStage.Offline, DeviceSetupEvaluator.Evaluate(true, device));
    }

    [Fact]
    public void PreferredDevice_EmptyList_ReturnsNull()
        => Assert.Null(DeviceSetupEvaluator.PreferredDevice(new List<AdbDevice>(), null));

    [Fact]
    public void PreferredDevice_NoExplicitSerial_PrefersAuthorized()
    {
        var unauthorized = new AdbDevice { Serial = "s1", State = "unauthorized" };
        var authorized = new AdbDevice { Serial = "s2", State = "device" };
        var devices = new List<AdbDevice> { unauthorized, authorized };

        Assert.Same(authorized, DeviceSetupEvaluator.PreferredDevice(devices, null));
    }

    [Fact]
    public void PreferredDevice_NoAuthorizedDevice_FallsBackToFirst()
    {
        var first = new AdbDevice { Serial = "s1", State = "unauthorized" };
        var second = new AdbDevice { Serial = "s2", State = "offline" };
        var devices = new List<AdbDevice> { first, second };

        Assert.Same(first, DeviceSetupEvaluator.PreferredDevice(devices, null));
    }

    [Fact]
    public void PreferredDevice_ExplicitSerial_IsHonoredEvenIfNotAuthorized()
    {
        var authorized = new AdbDevice { Serial = "s1", State = "device" };
        var target = new AdbDevice { Serial = "s2", State = "unauthorized" };
        var devices = new List<AdbDevice> { authorized, target };

        Assert.Same(target, DeviceSetupEvaluator.PreferredDevice(devices, "s2"));
    }

    [Fact]
    public void PreferredDevice_UnknownExplicitSerial_FallsBackToPreferredRule()
    {
        var authorized = new AdbDevice { Serial = "s1", State = "device" };
        var devices = new List<AdbDevice> { authorized };

        Assert.Same(authorized, DeviceSetupEvaluator.PreferredDevice(devices, "does-not-exist"));
    }
}
