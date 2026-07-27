using System.Windows;
using System.Windows.Threading;
using ScrcpyDeck.Services;

namespace ScrcpyDeck;

public partial class App : Application
{
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

        Log.Info($"PhoneLink Pro starting (v{AppVersionInfo.ProductVersion})");
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Hidden build tool: verify or regenerate the engine integrity manifest, then exit.
        // Used by the Release publish gate and update-engine-manifest.bat; never runs for
        // end users (the variable is not set outside the build).
        if (IntegrityToolMode.TryRun(out var toolExitCode))
        {
            Shutdown(toolExitCode);
            return;
        }

        // Hidden design-QA tool: render the shell offscreen at the approved reference sizes
        // and exit with a meaningful code. Checked HERE — ahead of the single-instance gate,
        // consent, integrity verification and every engine service — so it never starts ADB
        // or the device watcher, never claims engine-service ownership, and never disturbs a
        // running normal instance. Activates ONLY on the exact value; see
        // ShellSnapshotToolMode for the validation and watchdog contract.
        if (ShellSnapshotToolMode.IsRequested())
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
        if (DashboardSnapshotToolMode.IsRequested())
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
        if (DeviceSetupWizardSnapshotToolMode.IsRequested())
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
        if (Environment.GetEnvironmentVariable("PLP_SELFTEST") == "1")
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plp_selftest.txt");
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
                AppSettings.ResetForTests(null);
                try { System.IO.Directory.Delete(settingsTestDir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Hidden design-QA tool (Batch 4C): same placement/reasoning as the Dashboard/setup-
        // wizard snapshot modes above — renders the wireless-pairing UserControl offscreen for
        // a fixed set of deterministic scenarios, ahead of every real service, and never touches
        // MainWindow's own startup path.
        if (WirelessSnapshotToolMode.IsRequested())
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
        if (TransfersSnapshotToolMode.IsRequested())
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
        if (SettingsSnapshotToolMode.IsRequested())
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
        if (AboutSnapshotToolMode.IsRequested())
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
        // Runs once; safe even if MainWindow already triggered it.
        AppLifecycle.Shutdown();
        base.OnExit(e);
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
