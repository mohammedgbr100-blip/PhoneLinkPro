using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// CMD-02 — adb argument construction and input validation.
///
/// AUDIT-ID CORRECTION: in Batch 1A these tests were named CMD01_*. The canonical mapping
/// is CMD-01 = scrcpy arguments, CMD-02 = adb arguments/validation, so they are renamed here.
///
/// Batch 1B replaced the concatenated adb command line with argv delivered through
/// ProcessStartInfo.ArgumentList. The Batch 1A characterization tests that pinned the unsafe
/// concatenated strings are REPLACED by the CMD02_* tests asserting safe argv.
/// </summary>
public class AdbCommandConstructionTests
{
    // ---------------------------------------------------------------- server / listing

    [Fact]
    public void DevicesCommand_IsDevicesDashL()
        => Assert.Equal(["devices", "-l"], AdbService.DevicesArgs);

    [Fact]
    public void ServerCommands_AreStartAndKill()
    {
        Assert.Equal(["start-server"], AdbService.StartServerArgs);
        Assert.Equal(["kill-server"], AdbService.KillServerArgs);
    }

    // ---------------------------------------------------------------- -s wrapping

    [Fact]
    public void PerDeviceCommand_PrefixesDashSAndSerialAsSeparateElements()
        => Assert.Equal(["-s", "R58M12ABCDE", "shell", "wm", "size"],
            AdbService.WithSerialArgs("R58M12ABCDE", AdbService.WmSizeArgs));

    [Fact]
    public void TcpIpSerial_IsWrappedTheSameWay()
        => Assert.Equal(["-s", "192.168.1.42:5555", "shell", "dumpsys", "battery"],
            AdbService.WithSerialArgs("192.168.1.42:5555", AdbService.BatteryArgs));

    [Fact]
    public void EmulatorSerial_IsWrappedTheSameWay()
        => Assert.Equal(["-s", "emulator-5554", "devices", "-l"],
            AdbService.WithSerialArgs("emulator-5554", AdbService.DevicesArgs));

    [Fact] // the getprop probe is one shell script argument, with fixed content
    public void PropsCommand_IsShellPlusOneFixedScript()
    {
        Assert.Equal(2, AdbService.PropsArgs.Length);
        Assert.Equal("shell", AdbService.PropsArgs[0]);
        Assert.Equal(
            "getprop ro.product.model; getprop ro.product.brand; getprop ro.build.version.release",
            AdbService.PropsArgs[1]);
    }

    [Fact] // fixed command tokens are separate elements, not a joined string
    public void FixedShellCommands_UseSeparateTokens()
    {
        Assert.Equal(["shell", "wm", "size"], AdbService.WmSizeArgs);
        Assert.Equal(["shell", "dumpsys", "battery"], AdbService.BatteryArgs);
        Assert.Equal(["shell", "ip", "-f", "inet", "addr", "show", "wlan0"], AdbService.WifiAddrArgs);
        Assert.Equal(["shell", "ip", "route"], AdbService.WifiRouteArgs);
    }

    // ---------------------------------------------------------------- wireless

    [Fact]
    public void TcpipCommand_UsesThePort()
    {
        Assert.Equal(["tcpip", "5555"], AdbService.TcpipArgs(5555));
        Assert.Equal(["tcpip", "5037"], AdbService.TcpipArgs(5037));
    }

    [Fact]
    public void ConnectAndDisconnect_UseTheEndpointAsOneElement()
    {
        Assert.Equal(["connect", "192.168.1.42:5555"], AdbService.ConnectArgs("192.168.1.42:5555"));
        Assert.Equal(["disconnect", "192.168.1.42:5555"],
            AdbService.DisconnectArgs("192.168.1.42:5555"));
    }

    [Fact]
    public void PairCommand_KeepsEndpointAndCodeSeparate()
        => Assert.Equal(["pair", "192.168.1.42:37123", "123456"],
            AdbService.PairArgs("192.168.1.42:37123", "123456"));

    // ---------------------------------------------------------------- packages

    [Theory]
    [InlineData("-3")]
    [InlineData("-s")]
    public void ListPackagesCommand_AppendsScopeAsItsOwnElement(string scope)
        => Assert.Equal(["shell", "pm", "list", "packages", scope],
            AdbService.ListPackagesArgs(scope));

    [Fact]
    public void ListPackagesCommand_OmitsEmptyScope()
        => Assert.Equal(["shell", "pm", "list", "packages"], AdbService.ListPackagesArgs(""));

    [Fact]
    public void PackageLifecycleCommands_UseSeparateTokens()
    {
        Assert.Equal(["uninstall", "com.example.app"], AdbService.UninstallArgs("com.example.app"));
        Assert.Equal(["shell", "pm", "disable-user", "--user", "0", "com.example.app"],
            AdbService.DisableArgs("com.example.app"));
        Assert.Equal(["shell", "pm", "enable", "com.example.app"],
            AdbService.EnableArgs("com.example.app"));
        Assert.Equal(["shell", "pm", "clear", "com.example.app"],
            AdbService.ClearArgs("com.example.app"));
        Assert.Equal(["shell", "am", "force-stop", "com.example.app"],
            AdbService.ForceStopArgs("com.example.app"));
    }

    // ---------------------------------------------------------------- install / transfer

    [Fact]
    public void InstallCommand_PathIsOneUnquotedElement()
        => Assert.Equal(["install", "-r", @"C:\downloads\app.apk"],
            AdbService.InstallArgs(@"C:\downloads\app.apk"));

    [Fact]
    public void InstallCommand_WithSpaces_StaysOneElement()
        => Assert.Equal(["install", "-r", @"C:\my downloads\my app.apk"],
            AdbService.InstallArgs(@"C:\my downloads\my app.apk"));

    [Fact]
    public void InstallCommand_WithArabicPath_StaysOneLiteralElement()
        => Assert.Equal(["install", "-r", @"C:\تنزيلات\تطبيق.apk"],
            AdbService.InstallArgs(@"C:\تنزيلات\تطبيق.apk"));

    [Theory] // local paths with punctuation that a shell would treat as syntax
    [InlineData(@"C:\files\a & b.apk")]
    [InlineData(@"C:\files\a (1).apk")]
    [InlineData(@"C:\files\a[1].apk")]
    [InlineData(@"C:\files\a;b.apk")]
    [InlineData(@"C:\ملفات\تطبيق (نسخة).apk")]
    public void InstallCommand_LocalPathsStayLiteral(string path)
        => Assert.Equal(["install", "-r", path], AdbService.InstallArgs(path));

    [Fact]
    public void PushCommand_KeepsBothPathsSeparate()
        => Assert.Equal(["push", @"C:\local file.txt", "/sdcard/Download/remote file.txt"],
            AdbService.PushArgs(@"C:\local file.txt", "/sdcard/Download/remote file.txt"));

    [Fact]
    public void PullCommand_KeepsBothPathsSeparate()
        => Assert.Equal(["pull", "/sdcard/DCIM/صورة.jpg", @"C:\صور\صورة.jpg"],
            AdbService.PullArgs("/sdcard/DCIM/صورة.jpg", @"C:\صور\صورة.jpg"));

    [Fact]
    public void PushCommand_ArabicRemotePath_StaysOneLiteralElement()
        => Assert.Equal(["push", @"C:\ملف.txt", "/sdcard/مجلد/ملف.txt"],
            AdbService.PushArgs(@"C:\ملف.txt", "/sdcard/مجلد/ملف.txt"));

    [Theory] // remote push/pull paths are plain adb arguments, never shell text
    [InlineData("/sdcard/a b.txt")]
    [InlineData("/sdcard/a&b.txt")]
    [InlineData("/sdcard/a;rm -rf .txt")]
    [InlineData("/sdcard/$(whoami).txt")]
    [InlineData("/sdcard/`id`.txt")]
    public void PushCommand_HostileRemotePathsStayLiteral(string remote)
        => Assert.Equal(["push", "local.txt", remote], AdbService.PushArgs("local.txt", remote));

    // ================================================================
    //  CMD-02 — hostile input cannot inject adb arguments
    // ================================================================

    [Fact] // CMD-02 — was: package with spaces split into extra arguments
    public void CMD02_PackageWithSpaces_StaysOneElement()
    {
        var argv = AdbService.UninstallArgs("com.example.app --user 0");

        Assert.Equal(["uninstall", "com.example.app --user 0"], argv);
        Assert.DoesNotContain("--user", argv);
    }

    [Fact] // CMD-02 — was: serial with spaces injected into the per-device command
    public void CMD02_SerialWithSpaces_StaysOneElement()
    {
        var argv = AdbService.WithSerialArgs("abc kill-server", AdbService.DevicesArgs);

        Assert.Equal(["-s", "abc kill-server", "devices", "-l"], argv);
        Assert.DoesNotContain("kill-server", argv);
    }

    [Fact] // CMD-02 — was: quote broke out of the quoted apk path
    public void CMD02_QuoteInApkPath_StaysOneElement()
    {
        var argv = AdbService.InstallArgs("a.apk\" -g \"b.apk");

        Assert.Equal(["install", "-r", "a.apk\" -g \"b.apk"], argv);
        Assert.DoesNotContain("-g", argv);
    }

    [Fact] // CMD-02 — was: quote broke out of the quoted push path
    public void CMD02_QuoteInPushPath_StaysOneElement()
    {
        var argv = AdbService.PushArgs("f.txt\" \"/data/local/tmp/evil", "/sdcard/x.txt");

        Assert.Equal(["push", "f.txt\" \"/data/local/tmp/evil", "/sdcard/x.txt"], argv);
        Assert.Equal(3, argv.Count);
    }

    [Fact] // CMD-02 — was: shell metacharacters reached the device shell as syntax
    public void CMD02_ShellMetacharactersInPackage_StayOneElement()
    {
        var argv = AdbService.ClearArgs("com.example.app; rm -rf /sdcard/DCIM");

        // `pm clear` receives one argument; the ';' is data, not a shell separator.
        Assert.Equal(["shell", "pm", "clear", "com.example.app; rm -rf /sdcard/DCIM"], argv);
        Assert.Equal(4, argv.Count);
    }

    // NOTE: the builders above are deliberately dumb — they never mutate their input.
    // Hostile values are rejected before a process is launched; see AdbInputValidationTests
    // and AdbServiceValidationTests.
}
