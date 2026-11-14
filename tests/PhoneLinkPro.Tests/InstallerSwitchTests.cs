using System.Text.RegularExpressions;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Public-release audit: the uninstaller's /REMOVESETTINGS switch was matched with
/// <c>Pos('/REMOVESETTINGS', GetCmdTail) &gt; 0</c>, so <c>/REMOVESETTINGS=0</c> (and
/// <c>/REMOVESETTINGSX=1</c>, or the text inside another argument) deleted the user's
/// %AppData%\PhoneLinkPro. The behaviour itself is tested against real Inno Setup processes by
/// installer\test-installer-switches.ps1 (a decision matrix plus a real install/uninstall),
/// which needs Inno Setup and is therefore a gate in build-installer.bat and release.ps1 rather
/// than an xUnit test. These source guards run everywhere (CI included) and keep that
/// arrangement from being undone: the shipped script must use the shared, tested switch logic,
/// the old substring scan must not come back, and the installer must not be buildable without
/// the behavioural gate passing first.
/// </summary>
public sealed class InstallerSwitchTests
{
    private static string Iss() => TestPaths.ReadRepoFile("installer", "PhoneLinkPro.iss");
    private static string Switches() => TestPaths.ReadRepoFile("installer", "InstallerSwitches.iss");
    private static string Gate() => TestPaths.ReadRepoFile("installer", "test-installer-switches.ps1");

    [Fact]
    public void ShippedInstaller_IncludesTheSharedSwitchLogic()
    {
        Assert.Contains("#include \"InstallerSwitches.iss\"", Iss());
    }

    [Fact]
    public void ShippedUninstaller_RemovesSettingsOnlyThroughTheTestedPolicy()
    {
        var iss = Iss();
        Assert.Contains("ApplySettingsRemovalPolicy(ExpandConstant('{userappdata}\\PhoneLinkPro'))", iss);
        // The only DelTree of user data lives in the shared (tested) policy, not in the script.
        Assert.DoesNotContain("DelTree", iss);
        Assert.DoesNotContain("RemoveSettingsRequested", iss);
    }

    [Fact]
    public void ShippedSetup_DecidesDowngradesThroughTheTestedSwitch()
    {
        var iss = Iss();
        Assert.Contains("if not DowngradeAllowed() then", iss);
        Assert.DoesNotContain("{param:ALLOWDOWNGRADE", iss);
        Assert.Contains("ExpandConstant('{param:ALLOWDOWNGRADE|0}') = '1'", Switches());
    }

    [Fact]
    public void RemoveSettings_IsMatchedPerArgument_NeverBySubstringOfTheCommandLine()
    {
        var both = Iss() + Switches();
        Assert.DoesNotContain("GetCmdTail", both);
        Assert.DoesNotMatch(@"Pos\(\s*'/REMOVESETTINGS'", both);

        var switches = Switches();
        Assert.Contains("for I := 1 to ParamCount do", switches);
        Assert.Contains("RemoveSettingsArgDecision(ParamStr(I))", switches);
        // Only the bare switch or an explicit =1 means "remove"; every other value keeps.
        Assert.Contains("if A = RemoveSettingsSwitch then", switches);
        Assert.Matches(@"Length\(RemoveSettingsSwitch\) \+ 2, Length\(A\)\) = '1' then", switches);
    }

    [Theory] // both harnesses exercise the shipped logic, not a copy of it
    [InlineData("SwitchProbe.iss")]
    [InlineData("UninstallProbe.iss")]
    public void Harnesses_IncludeTheShippedSwitchLogic(string harness)
    {
        var source = TestPaths.ReadRepoFile("installer", "tests", harness);
        Assert.Contains("#include \"..\\InstallerSwitches.iss\"", source);
        Assert.DoesNotContain("function RemoveSettingsRequested", source);
        Assert.DoesNotContain("function DowngradeAllowed", source);
    }

    [Fact] // the uninstall probe must never be able to delete anything but the test's own folder
    public void UninstallProbe_ActsOnlyOnTheTestsOwnMarkedFolder()
    {
        var probe = TestPaths.ReadRepoFile("installer", "tests", "UninstallProbe.iss");
        Assert.DoesNotContain("{userappdata}", probe);
        Assert.Contains("CreateUninstallRegKey=no", probe);
        Assert.Contains("Pos('PLP-SWITCH-TESTS-', UpperCase(SettingsDir)) = 0", probe);
        Assert.Contains("ProbeSentinel", probe);
    }

    [Theory] // the regression cases stay in the gate
    [InlineData("Args = '/REMOVESETTINGS=0';", "Remove = '0'")]
    [InlineData("Args = '/REMOVESETTINGS';", "Remove = '1'")]
    [InlineData("Args = '/REMOVESETTINGS=1';", "Remove = '1'")]
    [InlineData("Args = '';", "Remove = '0'")]
    [InlineData("Args = '/REMOVESETTINGSX=1';", "Remove = '0'")]
    [InlineData("Args = '/ALLOWDOWNGRADE=1';", "Downgrade = '1'")]
    [InlineData("Args = '/ALLOWDOWNGRADE=0';", "Downgrade = '0'")]
    public void Gate_KeepsTheDecisionRegressionCases(string args, string expectation)
    {
        var line = Gate().Split('\n').FirstOrDefault(l => l.Contains(args) && l.Contains("Remove ="));
        Assert.NotNull(line);
        Assert.Contains(expectation, line);
    }

    [Theory] // both values go through the REAL uninstaller, not only the decision probe
    [InlineData("Args = '/REMOVESETTINGS=0';", "Kept = 'True'")]
    [InlineData("Args = '/REMOVESETTINGS=1';", "Kept = 'False'")]
    [InlineData("Args = '/REMOVESETTINGS';", "Kept = 'False'")]
    [InlineData("Args = '';", "Kept = 'True'")]
    public void Gate_KeepsTheRealUninstallerCases(string args, string expectation)
    {
        var line = Gate().Split('\n').FirstOrDefault(l => l.Contains(args) && l.Contains("Kept ="));
        Assert.NotNull(line);
        Assert.Contains(expectation, line);
    }

    [Fact]
    public void BuildInstallerBat_RunsTheSwitchGateBeforeCompiling()
    {
        var bat = TestPaths.ReadRepoFile("installer", "build-installer.bat");
        var gate = bat.IndexOf("test-installer-switches.ps1", StringComparison.Ordinal);
        var compile = bat.IndexOf("%ISCC% PhoneLinkPro.iss", StringComparison.Ordinal);
        Assert.True(gate >= 0, "build-installer.bat does not run the switch gate");
        Assert.True(compile > gate, "the switch gate must run before the installer is compiled");
    }

    [Fact]
    public void ReleasePipeline_RunsTheSwitchGateBeforeCompiling()
    {
        var release = TestPaths.ReadRepoFile("release.ps1");
        var gate = release.IndexOf("installer\\test-installer-switches.ps1", StringComparison.Ordinal);
        var compile = release.IndexOf("& $iscc 'installer\\PhoneLinkPro.iss'", StringComparison.Ordinal);
        Assert.True(gate >= 0, "release.ps1 does not run the switch gate");
        Assert.True(compile > gate, "the switch gate must run before the installer is compiled");
        Assert.Matches(new Regex(@"test-installer-switches\.ps1[^\n]*\n[^\n]*\$LASTEXITCODE -ne 0"), release);
    }
}
