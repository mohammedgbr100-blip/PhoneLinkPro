using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 5 — the exact adb argument strings the service builds today.
///
/// These strings are passed straight to ProcessStartInfo.Arguments. Pinning them here
/// gives the Batch 1B ArgumentList migration an exact before/after boundary. Hostile
/// fixtures document current injection exposure; they do not endorse it.
/// </summary>
public class AdbCommandConstructionTests
{
    // ---------------------------------------------------------------- server / listing

    [Fact] // [REGRESSION]
    public void DevicesCommand_IsDevicesDashL()
        => Assert.Equal("devices -l", AdbService.DevicesArgs);

    [Fact] // [REGRESSION]
    public void ServerCommands_AreStartAndKill()
    {
        Assert.Equal("start-server", AdbService.StartServerArgs);
        Assert.Equal("kill-server", AdbService.KillServerArgs);
    }

    // ---------------------------------------------------------------- -s wrapping

    [Fact] // [REGRESSION]
    public void PerDeviceCommand_IsPrefixedWithDashSAndSerial()
        => Assert.Equal("-s R58M12ABCDE shell wm size",
            AdbService.WithSerialArgs("R58M12ABCDE", AdbService.WmSizeArgs));

    [Fact] // [REGRESSION]
    public void TcpIpSerial_IsWrappedTheSameWay()
        => Assert.Equal("-s 192.168.1.42:5555 shell dumpsys battery",
            AdbService.WithSerialArgs("192.168.1.42:5555", AdbService.BatteryArgs));

    [Fact] // [REGRESSION] — the getprop probe keeps its inner quoting
    public void PropsCommand_KeepsInnerQuotes()
        => Assert.Equal(
            "shell \"getprop ro.product.model; getprop ro.product.brand; getprop ro.build.version.release\"",
            AdbService.PropsArgs);

    // ---------------------------------------------------------------- wireless

    [Fact] // [REGRESSION]
    public void TcpipCommand_UsesThePort()
    {
        Assert.Equal("tcpip 5555", AdbService.TcpipArgs(5555));
        Assert.Equal("tcpip 5037", AdbService.TcpipArgs(5037));
    }

    [Fact] // [REGRESSION]
    public void ConnectAndDisconnect_UseTheEndpoint()
    {
        Assert.Equal("connect 192.168.1.42:5555", AdbService.ConnectArgs("192.168.1.42:5555"));
        Assert.Equal("disconnect 192.168.1.42:5555", AdbService.DisconnectArgs("192.168.1.42:5555"));
    }

    [Fact] // [REGRESSION]
    public void PairCommand_IncludesEndpointAndCode()
        => Assert.Equal("pair 192.168.1.42:37somethingelse 123456",
            AdbService.PairArgs("192.168.1.42:37somethingelse", "123456"));

    // ---------------------------------------------------------------- packages

    [Theory] // [REGRESSION] — scope is appended and the result trimmed
    [InlineData("-3", "shell pm list packages -3")]
    [InlineData("-s", "shell pm list packages -s")]
    [InlineData("", "shell pm list packages")]
    public void ListPackagesCommand_AppendsScope(string scope, string expected)
        => Assert.Equal(expected, AdbService.ListPackagesArgs(scope));

    [Fact] // [REGRESSION]
    public void PackageLifecycleCommands_MatchCurrentStrings()
    {
        Assert.Equal("uninstall com.example.app", AdbService.UninstallArgs("com.example.app"));
        Assert.Equal("shell pm disable-user --user 0 com.example.app",
            AdbService.DisableArgs("com.example.app"));
        Assert.Equal("shell pm enable com.example.app", AdbService.EnableArgs("com.example.app"));
        Assert.Equal("shell pm clear com.example.app", AdbService.ClearArgs("com.example.app"));
        Assert.Equal("shell am force-stop com.example.app",
            AdbService.ForceStopArgs("com.example.app"));
    }

    // ---------------------------------------------------------------- install / transfer

    [Fact] // [REGRESSION] — install quotes the apk path
    public void InstallCommand_QuotesThePath()
        => Assert.Equal("install -r \"C:\\downloads\\app.apk\"",
            AdbService.InstallArgs(@"C:\downloads\app.apk"));

    [Fact] // [REGRESSION]
    public void InstallCommand_WithSpaces_IsQuoted()
        => Assert.Equal("install -r \"C:\\my downloads\\my app.apk\"",
            AdbService.InstallArgs(@"C:\my downloads\my app.apk"));

    [Fact] // [REGRESSION] — Arabic local path round-trips
    public void InstallCommand_WithArabicPath_IsQuotedVerbatim()
        => Assert.Equal("install -r \"C:\\تنزيلات\\تطبيق.apk\"",
            AdbService.InstallArgs(@"C:\تنزيلات\تطبيق.apk"));

    [Fact] // [REGRESSION]
    public void PushCommand_QuotesBothSides()
        => Assert.Equal("push \"C:\\local file.txt\" \"/sdcard/Download/remote file.txt\"",
            AdbService.PushArgs(@"C:\local file.txt", "/sdcard/Download/remote file.txt"));

    [Fact] // [REGRESSION]
    public void PullCommand_QuotesBothSides()
        => Assert.Equal("pull \"/sdcard/DCIM/صورة.jpg\" \"C:\\صور\\صورة.jpg\"",
            AdbService.PullArgs("/sdcard/DCIM/صورة.jpg", @"C:\صور\صورة.jpg"));

    [Fact] // [REGRESSION] — Arabic remote path round-trips
    public void PushCommand_WithArabicRemotePath_IsQuotedVerbatim()
        => Assert.Equal("push \"C:\\ملف.txt\" \"/sdcard/مجلد/ملف.txt\"",
            AdbService.PushArgs(@"C:\ملف.txt", "/sdcard/مجلد/ملف.txt"));

    // ================================================================
    //  CMD-01 (adb surface) — unescaped interpolation
    // ================================================================
    // Same root cause as the scrcpy builder: values are concatenated into one command
    // line. Package ids and serials are not quoted at all; paths are wrapped in literal
    // quotes that a quote inside the value terminates early.

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_PackageWithSpaces_SplitsIntoExtraArguments()
    {
        // An unusual package input flows straight through unquoted.
        var args = AdbService.UninstallArgs("com.example.app --user 0");

        Assert.Equal("uninstall com.example.app --user 0", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_SerialWithSpaces_InjectsIntoThePerDeviceCommand()
    {
        var args = AdbService.WithSerialArgs("abc kill-server", AdbService.DevicesArgs);

        Assert.Equal("-s abc kill-server devices -l", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_QuoteInApkPath_BreaksOutOfTheQuotedArgument()
    {
        var args = AdbService.InstallArgs("a.apk\" -g \"b.apk");

        Assert.Equal("install -r \"a.apk\" -g \"b.apk\"", args);
        Assert.Contains(" -g ", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_QuoteInPushPath_BreaksOutOfTheQuotedArgument()
    {
        var args = AdbService.PushArgs("f.txt\" \"/data/local/tmp/evil", "/sdcard/x.txt");

        Assert.Equal("push \"f.txt\" \"/data/local/tmp/evil\" \"/sdcard/x.txt\"", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — shell metacharacters reach the device shell
    public void CMD01_ShellMetacharactersInPackage_ArePassedThrough()
    {
        var args = AdbService.ClearArgs("com.example.app; rm -rf /sdcard/DCIM");

        // adb shell hands this to the device's shell, which honours ';'.
        Assert.Equal("shell pm clear com.example.app; rm -rf /sdcard/DCIM", args);
    }

    [Fact(Skip = "CMD-01: adb arguments are built by string concatenation, so package ids " +
                 "and serials containing whitespace or quotes inject extra arguments. " +
                 "Asserts the CORRECT post-fix behaviour (ArgumentList / validation). " +
                 "Target batch: 1B.")]
    public void CMD01_PackageInputShouldNotInjectArguments_TargetBatch1B()
    {
        var args = AdbService.UninstallArgs("com.example.app --user 0");

        Assert.DoesNotContain("--user", args);
    }

    [Fact(Skip = "CMD-01: apk path is interpolated into the command line unescaped. " +
                 "Asserts the CORRECT post-fix behaviour. Target batch: 1B.")]
    public void CMD01_ApkPathShouldNotInjectArguments_TargetBatch1B()
    {
        var args = AdbService.InstallArgs("a.apk\" -g \"b.apk");

        Assert.DoesNotContain(" -g ", args);
    }
}
