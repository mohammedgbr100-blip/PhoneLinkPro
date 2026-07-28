using System.Windows;
using System.Windows.Threading;
using ScrcpyDeck.Services;

namespace ScrcpyDeck;

public partial class App : Application
{
    /// <summary>
    /// Repair batch (independent review): which automated mode, if any, this process is
    /// running as — classified once, immediately after <see cref="MaintenanceMode"/> handling,
    /// before the first <see cref="Log"/> call. <see cref="AutomatedMode.None"/> for a normal
    /// interactive launch. Held as an instance field (not a local) so <see cref="OnExit"/> can
    /// keep log isolation installed through <see cref="AppLifecycle.Shutdown"/> and the base
    /// WPF shutdown path, rather than relying on any single method's own local scope.
    /// </summary>
    private AutomatedMode _automatedMode = AutomatedMode.None;

    /// <summary>The isolated temporary log root installed for this process; null for a normal
    /// interactive launch.</summary>
    private string? _automatedLogDir;

    /// <summary>Log.DirOverride's value before automated-mode isolation was installed (always
    /// null in practice — nothing sets it earlier — but captured rather than assumed).</summary>
    private string? _previousLogDirOverride;

    /// <summary>
    /// True once <see cref="InstallLogIsolation"/> has installed a temporary Log.DirOverride for
    /// this process. Tracked independently of <see cref="_automatedMode"/> (which stays
    /// <see cref="AutomatedMode.None"/> for a conflicting-modes run — there is no single
    /// resolved mode to name) so <see cref="OnExit"/> still knows to restore/clean up even on
    /// that fail-closed path, where AppLifecycle.Shutdown's own "Shutdown: begin/complete"
    /// logging still needs somewhere isolated to go.
    /// </summary>
    private bool _logIsolationInstalled;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Installer maintenance command (REL-02): runs headless cleanup and exits with a
        // meaningful code. Checked FIRST so no window, watcher or log-noise ever starts.
        if (MaintenanceMode.TryRun(e.Args, out var maintenanceExit))
        {
            Shutdown(maintenanceExit);
            return;
        }

        // Repair batch (independent review, B1/M1 in the original Batch 5A review — now fixed
        // for EVERY hidden automated mode, not only PLP_SELFTEST): classification happens here,
        // immediately after MaintenanceMode and before the very first Log.* call below, so no
        // automated process — self-test or any snapshot/tool mode — can ever have its startup
        // line (or anything logged during its run, or its own AppLifecycle.Shutdown/OnExit
        // logging) reach the real %AppData%\PhoneLinkPro\logs directory. A single explicit
        // known-mode allowlist (AutomatedModeClassifier) replaces the eight independent
        // environment-variable checks this method used to scatter across itself; conflicting
        // signals (e.g. PLP_SELFTEST=1 together with PLP_TOOL_MODE=about-snapshot) fail closed
        // before touching any log, real or isolated.
        var classification = AutomatedModeClassifier.Classify(Environment.GetEnvironmentVariable);
        if (classification.Conflict)
        {
            // Fail closed: never guess which mode "wins". Isolation is still installed (tagged
            // "conflict") before Shutdown(2) below, because Shutdown() synchronously triggers
            // OnExit -> AppLifecycle.Shutdown(), which logs its own "Shutdown: begin/complete" —
            // that still needs somewhere other than the real profile to land. The visible
            // diagnostic goes to the same kind of temp file every other hidden tool mode uses.
            InstallLogIsolation("conflict");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plp_automated_mode_conflict.txt"),
                "FAIL: conflicting automated-mode environment variables: " +
                string.Join(", ", classification.DetectedSignals));
            Shutdown(2);
            return;
        }

        // Repair batch 5B (§2): a non-blank, unrecognized PLP_TOOL_MODE value must never fall
        // through to a normal interactive launch — that would start the real shell, the real
        // device watcher and real engine integrity/adb work under what the caller believed was a
        // bounded, isolated tool-mode run. Isolation is installed and the diagnostic written
        // BEFORE Shutdown(3) for exactly the same reason as the conflict branch above:
        // Shutdown() synchronously triggers OnExit -> AppLifecycle.Shutdown(), whose own logging
        // still needs somewhere isolated to land even though no single mode was resolved.
        if (classification.UnknownToolMode)
        {
            InstallLogIsolation("unknown-tool-mode");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plp_automated_mode_unknown.txt"),
                $"FAIL: unrecognized {ShellSnapshotToolMode.ModeVariable} value: " +
                $"'{classification.UnknownToolModeValue}'");
            Shutdown(3);
            return;
        }

        _automatedMode = classification.Mode;
        if (_automatedMode != AutomatedMode.None)
        {
            InstallLogIsolation(_automatedMode.ToString());
            // Repair batch 5B (§7): every automated mode is a bounded, non-interactive run that
            // must never actually start the real bundled adb/scrcpy binaries — most already
            // guarantee this per-scenario (DeviceWatcher.DiscoveryProvider overrides, etc.); this
            // is the structural safety net underneath all of them. Never restored: the process
            // exits at the end of this automated run either way.
            ProcessRunner.BundledEngineLaunchesAreBlockedForTests = true;
        }

        Log.Info($"PhoneLink Pro starting (v{AppVersionInfo.ProductVersion})");
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Hidden build tool: verify or regenerate the engine integrity manifest, then exit.
        // Used by the Release publish gate and update-engine-manifest.bat; never runs for
        // end users (the variable is not set outside the build).
        if (_automatedMode == AutomatedMode.IntegrityTool)
        {
            IntegrityToolMode.TryRun(out var toolExitCode);
            Shutdown(toolExitCode);
            return;
        }

        // Hidden design-QA tool: render the shell offscreen at the approved reference sizes
        // and exit with a meaningful code. Checked HERE — ahead of the single-instance gate,
        // consent, integrity verification and every engine service — so it never starts ADB
        // or the device watcher, never claims engine-service ownership, and never disturbs a
        // running normal instance. Activates ONLY on the exact value; see
        // ShellSnapshotToolMode for the validation and watchdog contract.
        if (_automatedMode == AutomatedMode.ShellSnapshot)
        {
            // The capture closes the shell when it is done. Under the default
            // OnLastWindowClose that would shut the app down with code 0 BEFORE the real
            // exit code is applied, making a failed capture look successful.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var prepared = ShellSnapshotToolMode.Prepare();
            if (prepared != ShellSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            // StartupUri still creates the shell, but its constructor has already parked it
            // offscreen (ShellSnapshotToolMode.IsActive), so nothing flashes on the desktop.
            // ApplicationIdle runs after that window exists and is shown.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await ShellSnapshotToolMode.RunAsync(() => MainWindow);
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Hidden design-QA tool (Batch 4A): render the Dashboard 2.0 UserControl offscreen for
        // a fixed set of device fixtures, then exit with a meaningful code. Same placement and
        // reasoning as ShellSnapshotToolMode above — ahead of every real service. StartupUri
        // still auto-creates MainWindow regardless of this branch (exactly as it does for
        // PLP_SELFTEST below); SuppressStartupForHarness makes its Loaded handler a no-op, so
        // it never reaches consent/integrity/engine startup — this tool mode never touches it
        // beyond that.
        if (_automatedMode == AutomatedMode.DashboardSnapshot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            var prepared = DashboardSnapshotToolMode.Prepare();
            if (prepared != DashboardSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await DashboardSnapshotToolMode.RunAsync();
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Hidden design-QA tool (Batch 4B): same placement/reasoning as the Dashboard snapshot
        // mode above — renders the device-setup wizard offscreen for a fixed set of scenarios,
        // ahead of every real service, and never touches MainWindow's own startup path.
        if (_automatedMode == AutomatedMode.SetupWizardSnapshot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            var prepared = DeviceSetupWizardSnapshotToolMode.Prepare();
            if (prepared != DeviceSetupWizardSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await DeviceSetupWizardSnapshotToolMode.RunAsync();
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Hidden self-test: construct every window to catch XAML/resource errors, then verify
        // the RENDERED design contract (Theme.xaml's implicit TextBlock style outranks
        // inheritance, so XAML alone cannot prove a control paints the right colour), then
        // exit. Bypasses the single-instance gate deliberately: it starts no watcher and no
        // ADB, so a developer's open app must not fail the build tooling.
        if (_automatedMode == AutomatedMode.SelfTest)
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plp_selftest.txt");

            // Fail closed: if the isolation installed above is somehow not in effect — the
            // override slipped, or a future refactor moved this block ahead of it — abort
            // before constructing a single window or running a single self-test, rather than
            // ever risk a write landing in the real user log directory. The diagnostic below
            // goes to the temp-file the harness always uses, never Log.*.
            if (_automatedLogDir == null || Log.DirOverride != _automatedLogDir)
            {
                System.IO.File.WriteAllText(log,
                    "FAIL: PLP_SELFTEST log isolation was not installed before startup; aborting " +
                    "before touching the real profile.");
                Shutdown(1);
                return;
            }

            // The assertions below open and close windows; the default OnLastWindowClose
            // would end the run at the first teardown.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // No shell built below may run the startup path: the self-test starts no watcher,
            // no integrity verification and no ADB, exactly as before.
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            // Batch 4E: TransfersView/SettingsView now read AppSettings on construction.
            // Redirect it to an isolated directory for the whole self-test run so no scenario
            // below — Transfers or Settings — ever reads or writes the real
            // %AppData%\PhoneLinkPro\settings.json.
            var settingsTestDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "plp_selftest_settings_" + Guid.NewGuid().ToString("N"));
            AppSettings.ResetForTests(settingsTestDir);

            // Batch 4F repair: a construction-time regression in ANY window below (not just
            // About) must never be able to reach a real Process.Start or a real clipboard write
            // just from this smoke-construction pass — AboutSelfTest.Run() installs its own more
            // specific seams later, but this first pass runs before it does.
            SafeLinkLauncher.ProcessStartOverrideForTests = _ => false;
            ClipboardService.SetTextOverrideForTests = _ => false;
            try
            {
                // Constructed to catch XAML/resource errors, then CLOSED: the self-test must
                // not leave a MainWindow subscribed to the static service events, or the
                // lifecycle assertions below would measure the harness instead of the shell.
                var constructed = new List<Window>
                {
                    new Windows.ConsentWindow(),
                    new Windows.OnboardingWindow(),
                    new Windows.AboutWindow(),
                    new Windows.PolicyWindow(),
                    new Windows.NoticesWindow(),
                    new Windows.DeviceSetupWizardWindow(),
                    new MainWindow(),
                };
                foreach (var window in constructed)
                {
                    try { window.Close(); } catch { /* teardown is best effort */ }
                }

                var design = ShellRenderSelfTest.Run();
                var fit = ShellFitSelfTest.Run();
                var lifecycle = ShellLifecycleSelfTest.Run();
                // Batch 3B: proves DashboardView releases its DeviceWatcher subscription.
                var dashboard = DashboardLifecycleSelfTest.Run();
                // Batch 3C: proves a stale async continuation is discarded, not applied.
                var dashboardStale = DashboardStaleContinuationSelfTest.Run();
                // Batch 3D: proves an older queued device update can never overwrite a newer one.
                var dashboardOrdering = DashboardMissedUpdateOrderingSelfTest.Run();
                // Batch 5A: repeats the two previously-flaky ordering scenarios (C and E) many
                // times with the same deterministic causal synchronization, as a stability gate
                // against a regression to guessed dispatcher-pump counts.
                var dashboardOrderingStress = DashboardMissedUpdateOrderingSelfTest.RunOrderingStressGate();
                // Batch 4A: proves the Dashboard 2.0 visual layer renders every fixture without
                // exception and exposes the accessibility contract Phase 5 requires.
                var dashboardVisual = DashboardVisualSelfTest.Run();
                // Batch 4B: proves the device-setup wizard renders every real ADB state
                // truthfully, masks serials, gates Finish correctly and cleans up its
                // DeviceWatcher subscription.
                var setupWizard = DeviceSetupWizardSelfTest.Run();
                // Batch 4C: proves the guided wireless-pairing page's truthful state model
                // (pair/connect success never claims "connected" without reconciliation),
                // single-flight Pair/Connect guards, stale-completion-after-unload safety,
                // pairing-code hygiene and DeviceWatcher subscription lifecycle.
                var wireless = WirelessSelfTest.Run();
                // Batch 4D: proves the guided file-transfers page's truthful state model (exit-
                // code-aware push/pull classification, negative-before-positive evidence, no raw
                // adb output or full local path ever reaching the UI), device targeting/selection
                // policy, single-flight Send/Receive/Verify gating, sequential batch + partial-
                // failure reporting, cancellation semantics and DeviceWatcher subscription
                // lifecycle.
                var transfers = TransfersSelfTest.Run();
                // Batch 4E: proves the settings page's truthful draft/Save/Cancel model, atomic
                // single-flight persistence with stale-completion immunity, reset scope (only the
                // default transfer destination — never consent/onboarding/logs), corrupt/missing/
                // unknown-field/unsupported-enum recovery, sensitive-field exclusion and the
                // read-only engine-status card.
                var settings = SettingsSelfTest.Run();
                // Batch 4F: proves the About & Support page's truthful engine-status/consent
                // states, fixed-link allowlist wiring, safe support-summary privacy bound,
                // single-instance policy/notices dialogs, and side-effect-free construction.
                var about = AboutSelfTest.Run();
                var passed = design.Passed && fit.Passed && lifecycle.Passed
                             && dashboard.Passed && dashboardStale.Passed && dashboardOrdering.Passed
                             && dashboardOrderingStress.Passed
                             && dashboardVisual.Passed && setupWizard.Passed && wireless.Passed
                             && transfers.Passed && settings.Passed && about.Passed;

                System.IO.File.WriteAllText(log, string.Join(Environment.NewLine, new[]
                {
                    "OK: all windows constructed",
                    (design.Passed ? "OK: " : "FAIL: ") + design.Report,
                    (fit.Passed ? "OK: " : "FAIL: ") + fit.Report,
                    (lifecycle.Passed ? "OK: " : "FAIL: ") + lifecycle.Report,
                    (dashboard.Passed ? "OK: " : "FAIL: ") + dashboard.Report,
                    (dashboardStale.Passed ? "OK: " : "FAIL: ") + dashboardStale.Report,
                    (dashboardOrdering.Passed ? "OK: " : "FAIL: ") + dashboardOrdering.Report,
                    (dashboardOrderingStress.Passed ? "OK: " : "FAIL: ") + dashboardOrderingStress.Report,
                    (dashboardVisual.Passed ? "OK: " : "FAIL: ") + dashboardVisual.Report,
                    (setupWizard.Passed ? "OK: " : "FAIL: ") + setupWizard.Report,
                    (wireless.Passed ? "OK: " : "FAIL: ") + wireless.Report,
                    (transfers.Passed ? "OK: " : "FAIL: ") + transfers.Report,
                    (settings.Passed ? "OK: " : "FAIL: ") + settings.Report,
                    (about.Passed ? "OK: " : "FAIL: ") + about.Report,
                }));
                Shutdown(passed ? 0 : 1);
                return;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(log, "FAIL: " + ex);
                Shutdown(1);
                return;
            }
            finally
            {
                // Log.DirOverride is deliberately NOT restored here: Shutdown(...) above already
                // ran OnExit synchronously (AppLifecycle.Shutdown's own logging needs isolation
                // to still be active when it runs), and OnExit's own finally is what restores
                // and cleans up the automated log root — see OnExit below. Restoring it here too
                // would race against/duplicate that single source of truth.
                AppSettings.ResetForTests(null);
                try { System.IO.Directory.Delete(settingsTestDir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Hidden design-QA tool (Batch 4C): same placement/reasoning as the Dashboard/setup-
        // wizard snapshot modes above — renders the wireless-pairing UserControl offscreen for
        // a fixed set of deterministic scenarios, ahead of every real service, and never touches
        // MainWindow's own startup path.
        if (_automatedMode == AutomatedMode.WirelessSnapshot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            var prepared = WirelessSnapshotToolMode.Prepare();
            if (prepared != WirelessSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await WirelessSnapshotToolMode.RunAsync();
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Hidden design-QA tool (Batch 4D): same placement/reasoning as the Wireless snapshot
        // mode above — renders the guided file-transfers UserControl offscreen for a fixed set
        // of deterministic scenarios, ahead of every real service, and never touches MainWindow's
        // own startup path.
        if (_automatedMode == AutomatedMode.TransfersSnapshot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            var prepared = TransfersSnapshotToolMode.Prepare();
            if (prepared != TransfersSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await TransfersSnapshotToolMode.RunAsync();
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Hidden design-QA tool (Batch 4E): same placement/reasoning as the Transfers snapshot
        // mode above — renders the settings page offscreen for a fixed set of deterministic
        // scenarios, ahead of every real service, and never touches MainWindow's own startup path.
        if (_automatedMode == AutomatedMode.SettingsSnapshot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            var prepared = SettingsSnapshotToolMode.Prepare();
            if (prepared != SettingsSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await SettingsSnapshotToolMode.RunAsync();
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Hidden design-QA tool (Batch 4F): same placement/reasoning as the Settings snapshot
        // mode above — renders the About &amp; Support window offscreen for a fixed set of
        // deterministic scenarios, ahead of every real service, and never touches MainWindow's
        // own startup path.
        if (_automatedMode == AutomatedMode.AboutSnapshot)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScrcpyDeck.MainWindow.SuppressStartupForHarness = true;

            var prepared = AboutSnapshotToolMode.Prepare();
            if (prepared != AboutSnapshotToolMode.ExitSuccess)
            {
                Shutdown(prepared);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var exitCode = await AboutSnapshotToolMode.RunAsync();
                Shutdown(exitCode);
            }), DispatcherPriority.ApplicationIdle);
            return;
        }

        // Single instance per user session (P3-02). A second launch wakes the first
        // window and exits before ANY watcher, verification, ADB call or log writer of its
        // own is created.
        var role = SingleInstance.Acquire();
        if (!SingleInstance.ShouldProceed(role))
        {
            SingleInstance.NotifyExistingInstance();
            Shutdown(0);
            return;
        }
        if (role == InstanceRole.PrimaryRecovered)
            Log.Info("SingleInstance: recovered ownership from a crashed previous instance.");
        SingleInstance.StartShowRequestListener(() => Dispatcher.BeginInvoke(RestoreMainWindow));

        // Begin engine integrity verification in the background so the result is usually
        // ready before MainWindow needs it; MainWindow awaits this same shared run.
        _ = EngineIntegrityVerifier.VerifyAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Runs once; safe even if MainWindow already triggered it. AppLifecycle.Shutdown()'s
        // own logging ("Shutdown: begin/complete" etc.) must still be isolated for an automated
        // mode, so it runs FIRST, while Log.DirOverride is still the automated root installed in
        // OnStartup — restoration happens only in the finally below, after that logging is done.
        try
        {
            AppLifecycle.Shutdown();
        }
        finally
        {
            RestoreAutomatedLogIsolation();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Installs an isolated temporary Log.DirOverride for this process, tagged with
    /// <paramref name="tag"/> (the automated mode's name, or "conflict") for easier diagnosis.
    /// Captures the previous override so <see cref="RestoreAutomatedLogIsolation"/> can put it
    /// back exactly.
    /// </summary>
    private void InstallLogIsolation(string tag)
    {
        _previousLogDirOverride = Log.DirOverride;
        _automatedLogDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"plp_automated_{tag}_{Guid.NewGuid():N}");
        Log.DirOverride = _automatedLogDir;
        _logIsolationInstalled = true;
    }

    /// <summary>
    /// Restores Log.DirOverride to whatever it was before OnStartup installed automated-mode
    /// isolation, and deletes the temporary automated log root. Idempotent by design — clears
    /// <see cref="_logIsolationInstalled"/> once done, so a second OnExit call (defensive; WPF
    /// does not guarantee exactly-once) is a safe no-op rather than relying on
    /// AppLifecycle.Shutdown's own one-shot guard to avoid a double restore or a double
    /// directory-delete attempt.
    /// </summary>
    private void RestoreAutomatedLogIsolation()
    {
        if (!_logIsolationInstalled) return;

        Log.DirOverride = _previousLogDirOverride;
        if (_automatedLogDir != null)
            try { System.IO.Directory.Delete(_automatedLogDir, recursive: true); } catch { /* best effort */ }

        _logIsolationInstalled = false;
        _automatedLogDir = null;
    }

    /// <summary>Brings the existing main window to the front (second-launch handoff).</summary>
    private void RestoreMainWindow()
    {
        Log.Info("SingleInstance: show request received from a second launch.");
        var window = MainWindow;
        if (window == null) return;

        try
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            // Windows only grants foreground to the active process reliably via the
            // topmost toggle; it is reset immediately.
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
        catch (Exception ex)
        {
            Log.Warn($"SingleInstance: window restore failed ({ex.GetType().Name}).");
        }
    }

    // ---------------- global exception policy (LOG-01) ----------------

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var decision = GlobalExceptionPolicy.RegisterUiException(e.Exception);

        // Every branch below is a CLASSIFIED outcome: continue (recoverable), continue
        // silently (deduplicated repeat), or orderly shutdown. Handled is set so WPF never
        // shows its own crash dialog on top of ours; a fatal decision still ends the app —
        // through AppLifecycle's bounded shutdown, never an abrupt process kill.
        e.Handled = true;

        switch (decision.Disposition)
        {
            case ExceptionDisposition.ContinueWithDialog:
                MessageBox.Show(decision.UserMessage, "PhoneLink Pro",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                break;

            case ExceptionDisposition.ContinueSilently:
                break;

            case ExceptionDisposition.FatalShutdown:
                if (decision.ShowDialog)
                    MessageBox.Show(decision.UserMessage, "PhoneLink Pro",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                AppLifecycle.Shutdown();
                Shutdown(1);
                break;
        }
    }

    /// <summary>
    /// Unobserved background-task exceptions never terminate the app: the task is already
    /// dead, application state is unaffected, so the policy logs (deduplicated) and observes.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        GlobalExceptionPolicy.RegisterBackgroundException(e.Exception.GetBaseException());
        e.SetObserved();
    }

    /// <summary>
    /// The CLR is terminating: nothing can prevent it. Log with full context and attempt the
    /// bounded orderly cleanup so child processes and the mutex are not orphaned.
    /// </summary>
    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            GlobalExceptionPolicy.RegisterUiException(ex);
        else
            Log.Error("Fatal non-exception object on AppDomain.UnhandledException");

        try { AppLifecycle.Shutdown(); } catch { }
    }
}
