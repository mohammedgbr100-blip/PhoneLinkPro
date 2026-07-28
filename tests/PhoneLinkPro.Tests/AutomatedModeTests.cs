using System.Diagnostics;
using System.Security.Cryptography;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Repair batch (independent review) — the single automated-mode classification that replaced
/// eight independent environment-variable checks scattered across App.OnStartup, and the log
/// isolation it drives.
///
/// <para>
/// <see cref="AutomatedModeClassifier"/> itself is pure and safely unit-testable in-process. The
/// PROCESS-LIFETIME claim — that isolation stays installed through AppLifecycle.Shutdown and
/// OnExit, not just "the source is ordered correctly" — is NOT provable by constructing
/// ScrcpyDeck.App in-process (a real WPF Application is a process-wide singleton every other test
/// class would then contend with, the same reason every other App-level/window-level behavioral
/// proof in this repo lives under PLP_SELFTEST instead). It IS provable by actually launching the
/// built exe as a real child process and checking the real log directory's hash before and
/// after — which is exactly what <see cref="RealLogDirectory_IsUnchangedAcrossACompleteSelfTestProcess"/>
/// and <see cref="RealLogDirectory_IsUnchangedWhenAutomatedModesConflict"/> do below. These are
/// the two tests in this file that spawn a subprocess; everything else here is in-process and
/// fast.
/// </para>
/// </summary>
public sealed class AutomatedModeTests
{
    // ---------------------------------------------------------------- AutomatedModeClassifier
    // (pure, in-process, no subprocess)

    [Fact]
    public void Classify_NormalEnvironment_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(_ => null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
        Assert.Empty(result.DetectedSignals);
    }

    [Fact]
    public void Classify_UnrelatedPlpPrefixedVariable_IsNotAutomated()
    {
        // Explicit allowlist, never a prefix wildcard: a developer's own unrelated PLP_-prefixed
        // variable must never silently enable automated behavior.
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_SOMETHING_ELSE" ? "1" : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
    }

    [Fact]
    public void Classify_UnknownToolModeValue_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(
            name => name == "PLP_TOOL_MODE" ? "not-a-real-mode" : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
    }

    [Fact]
    public void Classify_SelfTest_IsRecognized()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_SELFTEST" ? "1" : null);
        Assert.Equal(AutomatedMode.SelfTest, result.Mode);
        Assert.False(result.Conflict);
    }

    [Fact]
    public void Classify_IntegrityTool_IsRecognized()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_INTEGRITY_TOOL" ? "verify" : null);
        Assert.Equal(AutomatedMode.IntegrityTool, result.Mode);
    }

    [Theory] // every known snapshot mode must classify as automated — none silently falls through
    [InlineData("shell-snapshot", nameof(AutomatedMode.ShellSnapshot))]
    [InlineData("dashboard-snapshot", nameof(AutomatedMode.DashboardSnapshot))]
    [InlineData("setup-wizard-snapshot", nameof(AutomatedMode.SetupWizardSnapshot))]
    [InlineData("wireless-snapshot", nameof(AutomatedMode.WirelessSnapshot))]
    [InlineData("transfers-snapshot", nameof(AutomatedMode.TransfersSnapshot))]
    [InlineData("settings-snapshot", nameof(AutomatedMode.SettingsSnapshot))]
    [InlineData("about-snapshot", nameof(AutomatedMode.AboutSnapshot))]
    public void Classify_EveryKnownSnapshotMode_IsRecognized(string toolModeValue, string expectedModeName)
    {
        var result = AutomatedModeClassifier.Classify(
            name => name == "PLP_TOOL_MODE" ? toolModeValue : null);

        Assert.Equal(expectedModeName, result.Mode.ToString());
        Assert.False(result.Conflict);
    }

    [Fact] // matches every tool mode's own case-insensitive comparison against its ModeValue
    public void Classify_ToolModeValue_IsCaseInsensitive()
    {
        var result = AutomatedModeClassifier.Classify(
            name => name == "PLP_TOOL_MODE" ? "ABOUT-SNAPSHOT" : null);
        Assert.Equal(AutomatedMode.AboutSnapshot, result.Mode);
    }

    [Fact] // PLP_SELFTEST together with a snapshot tool mode is a conflict, not a precedence pick
    public void Classify_SelfTestAndToolMode_IsAConflict()
    {
        var result = AutomatedModeClassifier.Classify(name => name switch
        {
            "PLP_SELFTEST" => "1",
            "PLP_TOOL_MODE" => "about-snapshot",
            _ => null,
        });

        Assert.True(result.Conflict);
        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.Contains(AutomatedMode.SelfTest, result.DetectedSignals);
        Assert.Contains(AutomatedMode.AboutSnapshot, result.DetectedSignals);
    }

    [Fact] // the integrity tool together with self-test is also a conflict
    public void Classify_IntegrityToolAndSelfTest_IsAConflict()
    {
        var result = AutomatedModeClassifier.Classify(name => name switch
        {
            "PLP_SELFTEST" => "1",
            "PLP_INTEGRITY_TOOL" => "verify",
            _ => null,
        });

        Assert.True(result.Conflict);
    }

    // ---------------------------------------------------------------- App.xaml.cs source order
    // (supplementary source guard — see the two process-lifetime tests below for the actual
    // behavioral proof)

    [Fact] // [REGRESSION] classification, and log isolation for a resolved mode, must both occur
           // before the first Log.* call in OnStartup
    public void Source_ClassificationAndIsolationPrecedeTheFirstLogCall()
    {
        var code = TestPaths.ReadRepoFile("App.xaml.cs");

        var classify = code.IndexOf(
            "AutomatedModeClassifier.Classify(Environment.GetEnvironmentVariable)", StringComparison.Ordinal);
        var install = code.IndexOf("InstallLogIsolation(_automatedMode.ToString());", StringComparison.Ordinal);
        var firstLogCall = code.IndexOf("Log.Info($\"PhoneLink Pro starting", StringComparison.Ordinal);

        Assert.True(classify >= 0, "classification call must still exist");
        Assert.True(install >= 0, "the resolved-mode isolation install must still exist");
        Assert.True(firstLogCall >= 0, "the startup log line must still exist");
        Assert.True(classify < install && install < firstLogCall,
            "classification and isolation must both precede the first Log.* call");
    }

    [Fact] // [REGRESSION] the conflict path must also install isolation before Shutdown(2) —
           // Shutdown() synchronously triggers OnExit -> AppLifecycle.Shutdown(), whose own
           // logging needs somewhere isolated to go even when no single mode was resolved
    public void Source_ConflictPathInstallsIsolationBeforeShuttingDown()
    {
        var code = TestPaths.ReadRepoFile("App.xaml.cs");

        var conflictBranch = code.IndexOf("if (classification.Conflict)", StringComparison.Ordinal);
        Assert.True(conflictBranch >= 0, "the conflict branch must still exist");
        var install = code.IndexOf("InstallLogIsolation(\"conflict\");", conflictBranch, StringComparison.Ordinal);
        var shutdown = code.IndexOf("Shutdown(2);", conflictBranch, StringComparison.Ordinal);

        Assert.True(install > conflictBranch, "the conflict branch must install isolation");
        Assert.True(shutdown > install, "isolation must be installed before Shutdown(2)");
    }

    [Fact] // [REGRESSION] OnExit must run AppLifecycle.Shutdown() BEFORE restoring isolation —
           // otherwise its own shutdown logging reaches the real profile
    public void Source_OnExitRestoresIsolationOnlyAfterAppLifecycleShutdown()
    {
        var code = TestPaths.ReadRepoFile("App.xaml.cs");
        var onExitStart = code.IndexOf("protected override void OnExit(", StringComparison.Ordinal);
        Assert.True(onExitStart >= 0);
        var onExitEnd = code.IndexOf("private void InstallLogIsolation", onExitStart, StringComparison.Ordinal);
        Assert.True(onExitEnd > onExitStart);
        var onExitBody = code[onExitStart..onExitEnd];

        var shutdownCall = onExitBody.IndexOf("AppLifecycle.Shutdown();", StringComparison.Ordinal);
        var restoreCall = onExitBody.IndexOf("RestoreAutomatedLogIsolation();", StringComparison.Ordinal);
        Assert.True(shutdownCall >= 0 && restoreCall > shutdownCall,
            "AppLifecycle.Shutdown() must run before RestoreAutomatedLogIsolation()");
        Assert.Contains("finally", onExitBody[..restoreCall], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- process-lifetime proof
    // (spawns the real built exe — the only way to prove OnExit-time shutdown logging, not just
    // startup logging, stays isolated)

    [Fact]
    public void RealLogDirectory_IsUnchangedAcrossACompleteSelfTestProcess()
    {
        var before = SnapshotRealLogDirectory();

        var exitCode = RunApp(TimeSpan.FromSeconds(60), ("PLP_SELFTEST", "1"));

        var after = SnapshotRealLogDirectory();
        Assert.Equal(0, exitCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public void RealLogDirectory_IsUnchangedWhenAutomatedModesConflict()
    {
        var before = SnapshotRealLogDirectory();

        var exitCode = RunApp(TimeSpan.FromSeconds(30),
            ("PLP_SELFTEST", "1"), ("PLP_TOOL_MODE", "about-snapshot"));

        var after = SnapshotRealLogDirectory();
        Assert.Equal(2, exitCode); // the documented conflict exit code
        Assert.Equal(before, after);
    }

    /// <summary>SHA-256 of every *.log file's contents in the real profile's log directory,
    /// keyed by filename — comparable before/after a child-process run regardless of whether the
    /// directory even exists yet on a fresh machine.</summary>
    private static IReadOnlyDictionary<string, string> SnapshotRealLogDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneLinkPro", "logs");
        if (!Directory.Exists(dir)) return new Dictionary<string, string>();

        return Directory.GetFiles(dir, "*.log").ToDictionary(
            f => Path.GetFileName(f)!,
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
    }

    private static int RunApp(TimeSpan timeout, params (string Name, string Value)[] environmentVariables)
    {
        var exe = Path.Combine(TestPaths.RepoRoot(), "bin", BuildConfiguration(), "net9.0-windows", "PhoneLinkPro.exe");
        Assert.True(File.Exists(exe), $"built app exe not found at '{exe}'");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("PLP_", StringComparison.Ordinal)).ToList())
            psi.Environment.Remove(key);
        foreach (var (name, value) in environmentVariables)
            psi.Environment[name] = value;

        using var process = Process.Start(psi)!;
        var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            Assert.Fail($"app process did not exit within the bounded {timeout} timeout");
        }
        return process.ExitCode;
    }

    /// <summary>"Debug" or "Release" — the test host's own build configuration, which the app
    /// project (a ProjectReference dependency, always built first) shares.</summary>
    private static string BuildConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
}
