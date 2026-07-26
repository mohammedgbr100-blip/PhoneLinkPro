using System.Text.RegularExpressions;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 4B — source-drift guards for the device-setup wizard, in the repository's established
/// idiom (see <see cref="DesignFoundationTests"/>): xUnit runs MTA and cannot construct a real
/// WPF window, so these checks work over the tracked source text, and the runtime behavior they
/// can't prove (masking, subscription lifecycle, rendering) is covered by
/// <c>DeviceSetupWizardSelfTest</c> in the PLP_SELFTEST harness instead.
/// </summary>
public sealed class DeviceSetupWizardSourceTests
{
    private static string WizardXaml => TestPaths.ReadRepoFile("Windows", "DeviceSetupWizardWindow.xaml");
    private static string WizardCode => TestPaths.ReadRepoFile("Windows", "DeviceSetupWizardWindow.xaml.cs");
    private static string DashboardCode => TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
    private static string AppCode => TestPaths.ReadRepoFile("App.xaml.cs");

    [Fact]
    public void Xaml_NeverBindsTheRawSerialProperty()
        // A plain {Binding Serial} (no MaskSerial converter) would leak the raw serial the
        // instant a device list is bound to it — the same class of bug Batch 4A's
        // SafeDeviceLabelConverter/MaskSerialConverter were introduced to close.
        => Assert.DoesNotMatch(new Regex(@"\{Binding\s+Serial\s*\}"), WizardXaml);

    [Fact]
    public void CodeBehind_NeverConcatenatesTheRawSerialIntoDisplayText()
    {
        // Every user-visible serial in the code-behind goes through ShellLayout.MaskSerial;
        // ".Serial" should only ever appear as an equality/lookup key, an argument to
        // MaskSerial, or a Tag/AutomationProperties plumbing value — never interpolated
        // directly into a string shown to the user.
        Assert.DoesNotContain("dev.Serial}\"", WizardCode);
        Assert.DoesNotContain("{dev.Serial}", WizardCode);
        Assert.DoesNotContain("+ dev.Serial", WizardCode);
    }

    [Fact]
    public void CodeBehind_IntroducesNoNewPollingTimer()
    {
        foreach (var forbidden in new[]
        {
            "DispatcherTimer", "System.Threading.Timer", "System.Timers.Timer", "PeriodicTimer",
            "new Timer(", "System.Windows.Threading.DispatcherTimer",
        })
        {
            Assert.False(WizardCode.Contains(forbidden),
                $"the wizard must rely on DeviceWatcher's existing push events, never a timer of its own ('{forbidden}' found)");
        }
    }

    [Fact]
    public void CodeBehind_UsesANamedHandlerForDeviceWatcherSubscription()
    {
        Assert.Contains("DeviceWatcher.DevicesChanged += OnDevicesChanged;", WizardCode);
        Assert.Contains("DeviceWatcher.DevicesChanged -= OnDevicesChanged;", WizardCode);
    }

    [Fact]
    public void CodeBehind_ChecksClosedBeforeHandlingADevicesChangedEvent()
    {
        var idx = WizardCode.IndexOf("private void OnDevicesChanged", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(300, WizardCode.Length - idx));
        Assert.Contains("_closed", body);
    }

    [Fact]
    public void CodeBehind_VerifyActionCallsTheExistingDiscoveryServiceExactlyOnce()
        => Assert.Single(Regex.Matches(WizardCode, @"AdbService\.DiscoverDevicesAsync\(\)"));

    [Fact]
    public void CodeBehind_NeverCallsScrcpyOrEngineLaunchDirectly()
    {
        Assert.DoesNotContain("ScrcpyService.Launch", WizardCode);
        Assert.DoesNotContain("ProcessRunner.", WizardCode);
    }

    [Fact]
    public void Dashboard_HelpClickOpensTheWizardThroughExactlyOneHandler()
    {
        Assert.Single(Regex.Matches(DashboardCode, "void Help_Click"));
        Assert.Contains("new Windows.DeviceSetupWizardWindow(", DashboardCode);
        Assert.Single(Regex.Matches(DashboardCode, "new Windows.DeviceSetupWizardWindow\\("));
    }

    [Fact]
    public void Dashboard_HelpClickDoesNotCallDiscoveryDirectly()
    {
        var idx = DashboardCode.IndexOf("private void Help_Click", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var end = DashboardCode.IndexOf("\n    }", idx, StringComparison.Ordinal);
        var body = DashboardCode.Substring(idx, end - idx);
        Assert.DoesNotContain("DiscoverDevicesAsync", body);
    }

    [Fact]
    public void App_WiresTheSetupWizardSelfTestExactlyOnce()
        => Assert.Single(Regex.Matches(AppCode, "DeviceSetupWizardSelfTest.Run\\(\\)"));

    [Fact]
    public void App_WiresTheSetupWizardSnapshotToolModeExactlyOnce()
    {
        Assert.Single(Regex.Matches(AppCode, "DeviceSetupWizardSnapshotToolMode.IsRequested\\(\\)"));
        Assert.Single(Regex.Matches(AppCode, "DeviceSetupWizardSnapshotToolMode.Prepare\\(\\)"));
        Assert.Single(Regex.Matches(AppCode, "DeviceSetupWizardSnapshotToolMode.RunAsync\\(\\)"));
    }

    [Fact]
    public void App_ConstructsTheWizardInTheSelfTestWindowList()
        => Assert.Contains("new Windows.DeviceSetupWizardWindow()", AppCode);

    [Fact] // Hard constraint: no bypass wording anywhere near the wizard's own text
    public void Xaml_NeverClaimsAuthorizationCanBeBypassed()
    {
        foreach (var forbidden in new[] { "تجاوز", "bypass", "bypasses", "تخطي التفعيل" })
            Assert.DoesNotContain(forbidden, WizardXaml);
    }

    [Fact]
    public void CodeBehind_StatusTextNeverClaimsBypass()
    {
        foreach (var forbidden in new[] { "تجاوز", "bypass" })
            Assert.DoesNotContain(forbidden, WizardCode);
    }

    // ==================== Batch 4B repair — B1/B2/M1-M5 and section-14 guards ====================

    [Fact] // B1: title and close button live in their own explicit Grid cells, not mirrored
           // HorizontalAlignment, so RTL can never place the close button over the title.
    public void Xaml_TitleBarUsesExplicitGridColumnsForTitleAndClose()
    {
        Assert.Contains("x:Name=\"TitleText\" Grid.Column=\"0\"", WizardXaml);
        Assert.Contains("x:Name=\"CloseBtn\" Grid.Column=\"1\"", WizardXaml);
        Assert.DoesNotContain("<Canvas", WizardXaml);
    }

    [Fact] // B2: the manufacturer ComboBox uses a bounded local dark style, not the default
           // light system chrome.
    public void Xaml_ManufacturerComboHasExplicitDarkStyle()
    {
        Assert.Contains("x:Key=\"ComboBox.SetupWizard\"", WizardXaml);
        Assert.Contains("Style=\"{StaticResource ComboBox.SetupWizard}\"", WizardXaml);
        Assert.Contains("x:Key=\"ComboBoxItem.SetupWizard\"", WizardXaml);
    }

    [Fact] // M1: the masked serial/endpoint is rendered in its own explicit LTR mono TextBlock,
           // never concatenated into one plain RTL-inheriting string.
    public void CodeBehind_WirelessMaskedEndpointUsesAnExplicitLtrMonoTextBlock()
    {
        var idx = WizardCode.IndexOf("private void BuildDeviceSelector", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(1400, WizardCode.Length - idx));
        Assert.Contains("FindResource(\"Text.Mono\")", body);
        Assert.DoesNotContain("$\"{masked} —", body);   // the old single-string concatenation bug
    }

    [Fact] // M2: Offline may confirm ONLY that a device was observed — never USB debugging.
    public void CodeBehind_OfflineNeverConfirmsUsbDebugging()
    {
        var idx = WizardCode.IndexOf("private bool StepConfirmed(int step)", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(700, WizardCode.Length - idx));
        Assert.Contains("StepDevOptions or StepUsbDebug => _stage is DeviceSetupStage.Unauthorized or DeviceSetupStage.Ready", body);
        Assert.DoesNotContain("StepDevOptions or StepUsbDebug =>\n            _stage is DeviceSetupStage.Unauthorized or DeviceSetupStage.Offline or DeviceSetupStage.Ready", body);
    }

    [Fact] // minor: the verify/finish step can never read "تم" before Finish actually runs.
    public void CodeBehind_VerifyStepIsNeverConfirmedBeforeFinish()
    {
        var idx = WizardCode.IndexOf("private bool StepConfirmed(int step)", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(700, WizardCode.Length - idx));
        Assert.Contains("StepAuthorize => _stage == DeviceSetupStage.Ready", body);
        Assert.DoesNotContain("StepAuthorize or StepVerify", body);
    }

    [Fact] // M3: auto-advance runs exactly once (initial navigation), never on every render.
    public void CodeBehind_AutoAdvanceRespectsManualNavigation()
    {
        Assert.DoesNotContain("MaybeAutoAdvance", WizardCode);
        Assert.Contains("_initialNavigationDone", WizardCode);
        Assert.Contains("InitialStepIndexFor", WizardCode);
    }

    [Fact] // section 7: Verify goes through exactly one injected discovery seam, defaulting to
           // the real AdbService call.
    public void CodeBehind_VerifyUsesOneInjectedDiscoverySeam()
    {
        Assert.Contains("DiscoverDevicesOverrideForTests", WizardCode);
        Assert.Single(Regex.Matches(WizardCode, @"AdbService\.DiscoverDevicesAsync\(\)"));
    }

    [Fact] // section 7: ToolsUnavailable disables Verify and Next, not only Finish.
    public void CodeBehind_ToolsUnavailableDisablesVerifyAndNext()
    {
        var idx = WizardCode.IndexOf("private void RenderFooter()", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(1200, WizardCode.Length - idx));
        Assert.Contains("toolsUnavailable", body);
        Assert.Contains("NextBtn.IsEnabled = !toolsUnavailable", body);
        Assert.Contains("VerifyBtn.IsEnabled = !toolsUnavailable", body);
    }

    [Fact] // section 10: Escape closes an open ComboBox dropdown before it can close the wizard.
    public void CodeBehind_EscapeClosesOpenComboBoxDropdownBeforeClosingWindow()
    {
        var idx = WizardCode.IndexOf("private void Window_PreviewKeyDown", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(800, WizardCode.Length - idx));
        Assert.Contains("ManufacturerCombo.IsDropDownOpen", body);
        Assert.Contains("e.Handled = true", body);
    }

    [Fact] // M4: Finish no longer sets AppState itself — the caller applies the returned Result.
    public void CodeBehind_FinishDoesNotSetAppStateDirectly()
    {
        var idx = WizardCode.IndexOf("private void Finish_Click", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var body = WizardCode.Substring(idx, Math.Min(500, WizardCode.Length - idx));
        Assert.DoesNotContain("AppState.SetCurrent", body);
        Assert.Contains("Result = new SetupWizardResult", body);
    }

    [Fact] // M4: a successful modal result is applied to the Dashboard through exactly one path.
    public void Dashboard_AppliesASuccessfulSetupResultImmediately()
    {
        Assert.Contains("ShowDialog()", DashboardCode);
        Assert.Contains("wizard.Result", DashboardCode);
        Assert.Contains("ApplySetupResult", DashboardCode);
    }

    [Fact] // M5: the Dashboard button opens the wizard with truthful, non-tutorial wording.
    public void Dashboard_SetupButtonUsesTruthfulWording()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        Assert.Contains("إعداد الهاتف خطوة بخطوة", xaml);
        Assert.DoesNotContain("شاهد الشرح التعليمي", xaml);
    }

    [Fact] // section 11: no artifact of the removed, write-only SetupWizardSeen flag remains.
    public void NoSetupWizardSeenArtifactRemains()
    {
        var appSettingsCode = TestPaths.ReadRepoFile("Services", "AppSettings.cs");
        var settingsStoreCode = TestPaths.ReadRepoFile("Services", "SettingsStore.cs");
        var appCode = TestPaths.ReadRepoFile("App.xaml.cs");

        foreach (var code in new[] { appSettingsCode, settingsStoreCode, appCode, WizardCode })
        {
            Assert.DoesNotContain("SetupWizardSeen", code);
            Assert.DoesNotContain("SuppressPersistenceForHarness", code);
        }
    }

    [Fact] // section 14: no raw serial ever reaches an accessible name.
    public void CodeBehind_DeviceSelectorAccessibleNameUsesMaskedValueOnly()
    {
        var idx = WizardCode.IndexOf("AutomationProperties.SetName(radio,", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var line = WizardCode.Substring(idx, Math.Min(120, WizardCode.Length - idx));
        Assert.Contains("masked", line);
        Assert.DoesNotContain("dev.Serial", line);
    }

    [Fact] // section 14: App.xaml.cs still wires exactly one self-test call and one tool-mode
           // dispatch for this wizard (guards against a duplicated or moved entry point).
    public void App_HasExactlyOneSetupWizardSelfTestAndOneToolModeDispatch()
    {
        Assert.Single(Regex.Matches(AppCode, "DeviceSetupWizardSelfTest.Run\\(\\)"));
        Assert.Single(Regex.Matches(AppCode, "DeviceSetupWizardSnapshotToolMode.RunAsync\\(\\)"));
    }
}
