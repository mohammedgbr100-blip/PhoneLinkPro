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

    [Fact] // [REGRESSION] repair batch 5B (§2): a non-blank, unrecognized PLP_TOOL_MODE value
           // must fail closed — never silently resolve to a normal interactive launch
    public void Classify_UnknownToolModeValue_FailsClosed()
    {
        var result = AutomatedModeClassifier.Classify(
            name => name == "PLP_TOOL_MODE" ? "not-a-real-mode" : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
        Assert.True(result.UnknownToolMode);
        Assert.Equal("not-a-real-mode", result.UnknownToolModeValue);
    }

    [Fact] // empty PLP_TOOL_MODE is indistinguishable from absent: normal rules apply
    public void Classify_EmptyToolModeValue_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_TOOL_MODE" ? "" : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
        Assert.False(result.UnknownToolMode);
    }

    [Fact] // [REGRESSION] repair batch 5B (§2): whitespace-only PLP_TOOL_MODE is treated exactly
           // like absent/empty — never as "unknown" — the same blank-is-unset contract
           // PLP_INTEGRITY_TOOL already uses via IsNullOrWhiteSpace
    public void Classify_WhitespaceOnlyToolModeValue_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_TOOL_MODE" ? "   " : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
        Assert.False(result.UnknownToolMode);
    }

    [Fact] // [REGRESSION] an unknown PLP_TOOL_MODE together with PLP_SELFTEST must still fail
           // closed as Unknown, not silently resolve to SelfTest
    public void Classify_UnknownToolModePlusSelfTest_FailsClosedAsUnknown()
    {
        var result = AutomatedModeClassifier.Classify(name => name switch
        {
            "PLP_SELFTEST" => "1",
            "PLP_TOOL_MODE" => "not-a-real-mode",
            _ => null,
        });

        Assert.True(result.UnknownToolMode);
        Assert.False(result.Conflict);
        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.Contains(AutomatedMode.SelfTest, result.DetectedSignals);
    }

    [Fact] // [REGRESSION] an unknown PLP_TOOL_MODE together with PLP_INTEGRITY_TOOL must still
           // fail closed as Unknown, not silently resolve to IntegrityTool
    public void Classify_UnknownToolModePlusIntegrityTool_FailsClosedAsUnknown()
    {
        var result = AutomatedModeClassifier.Classify(name => name switch
        {
            "PLP_INTEGRITY_TOOL" => "verify",
            "PLP_TOOL_MODE" => "not-a-real-mode",
            _ => null,
        });

        Assert.True(result.UnknownToolMode);
        Assert.False(result.Conflict);
        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.Contains(AutomatedMode.IntegrityTool, result.DetectedSignals);
    }

    [Fact] // an unrelated, real, elsewhere-used PLP_ variable (MainWindow's own post-startup
           // navigation selector) must never be mistaken for an automated-mode signal
    public void Classify_PlpStartAlone_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_START" ? "Devices" : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
        Assert.False(result.UnknownToolMode);
        Assert.Empty(result.DetectedSignals);
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

    [Fact] // [REGRESSION] repair batch 5B (§3): the classifier must agree EXACTLY with
           // IntegrityToolMode.TryRun's own IsNullOrWhiteSpace("not requested") contract — a
           // whitespace-only value must never classify as IntegrityTool (which used to let
           // TryRun silently decline to run anything while still returning exit code 0)
    public void Classify_WhitespaceOnlyIntegrityToolValue_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_INTEGRITY_TOOL" ? "   " : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
        Assert.False(result.UnknownToolMode);
    }

    [Fact] // empty PLP_INTEGRITY_TOOL is indistinguishable from absent: normal rules apply
    public void Classify_EmptyIntegrityToolValue_IsNotAutomated()
    {
        var result = AutomatedModeClassifier.Classify(name => name == "PLP_INTEGRITY_TOOL" ? "" : null);

        Assert.Equal(AutomatedMode.None, result.Mode);
        Assert.False(result.Conflict);
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

    [Fact] // surrounding whitespace around an otherwise-known value does not defeat matching
    public void Classify_ToolModeValue_TrimsSurroundingWhitespace()
    {
        var result = AutomatedModeClassifier.Classify(
            name => name == "PLP_TOOL_MODE" ? "  shell-snapshot  " : null);
        Assert.Equal(AutomatedMode.ShellSnapshot, result.Mode);
        Assert.False(result.UnknownToolMode);
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

    [Fact] // [REGRESSION] repair batch 5B (§2): the unknown-tool-mode path must also install
           // isolation before Shutdown(3) — same reasoning as the conflict path above
    public void Source_UnknownToolModePathInstallsIsolationBeforeShuttingDown()
    {
        var code = TestPaths.ReadRepoFile("App.xaml.cs");

        var branch = code.IndexOf("if (classification.UnknownToolMode)", StringComparison.Ordinal);
        Assert.True(branch >= 0, "the unknown-tool-mode branch must exist");
        var install = code.IndexOf("InstallLogIsolation(\"unknown-tool-mode\");", branch, StringComparison.Ordinal);
        var shutdown = code.IndexOf("Shutdown(3);", branch, StringComparison.Ordinal);

        Assert.True(install > branch, "the unknown-tool-mode branch must install isolation");
        Assert.True(shutdown > install, "isolation must be installed before Shutdown(3)");
    }

    [Fact] // [REGRESSION] the unknown-tool-mode branch must be reached even when it is set
           // alongside a known signal (PLP_SELFTEST/PLP_INTEGRITY_TOOL) — checked ahead of the
           // resolved-mode assignment, exactly like the conflict branch
    public void Source_UnknownToolModeBranchPrecedesResolvedModeAssignment()
    {
        var code = TestPaths.ReadRepoFile("App.xaml.cs");

        var branch = code.IndexOf("if (classification.UnknownToolMode)", StringComparison.Ordinal);
        var assign = code.IndexOf("_automatedMode = classification.Mode;", StringComparison.Ordinal);

        Assert.True(branch >= 0 && assign > branch,
            "the unknown-tool-mode branch must precede the resolved-mode assignment");
    }

    [Fact] // [REGRESSION] repair batch 5B (§4): AutomatedModeClassifier must remain the ONLY
           // production place that reads PLP_TOOL_MODE — a direct per-tool re-check (the old
           // IsRequested()/IsModeRequested() dispatch pattern this batch removed) must never
           // reappear anywhere else in Services/, or a tool mode could activate independently of
           // (and inconsistently with) the centralized classifier's own fail-closed/whitespace
           // rules.
    public void Source_OnlyTheClassifierReadsPlpToolModeAnywhereInServices()
    {
        var servicesDir = Path.Combine(TestPaths.RepoRoot(), "Services");
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(servicesDir, "*.cs"))
        {
            if (Path.GetFileName(file) == "AutomatedMode.cs") continue;   // the one authorized reader

            var code = File.ReadAllText(file);
            if (code.Contains("GetEnvironmentVariable(ShellSnapshotToolMode.ModeVariable)", StringComparison.Ordinal) ||
                code.Contains("GetEnvironmentVariable(\"PLP_TOOL_MODE\")", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "PLP_TOOL_MODE must be read only by AutomatedModeClassifier; found direct reads in: " +
            string.Join(", ", offenders));
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

    [Fact] // [REGRESSION] repair batch 5B (§2): a real process launched with an unrecognized,
           // non-blank PLP_TOOL_MODE must fail closed (never fall through to a normal launch —
           // no real shell, no real device watcher, no real log write) and exit 3
    public void RealLogDirectory_IsUnchangedWithAnUnknownToolMode()
    {
        var before = SnapshotRealLogDirectory();

        var exitCode = RunApp(TimeSpan.FromSeconds(30), ("PLP_TOOL_MODE", "not-a-real-mode"));

        var after = SnapshotRealLogDirectory();
        Assert.Equal(3, exitCode); // the documented unknown-tool-mode exit code
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
