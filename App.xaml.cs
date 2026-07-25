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

        // Hidden self-test: construct every window to catch XAML/resource errors, then exit.
        // Bypasses the single-instance gate deliberately: it starts no watcher and no ADB,
        // so a developer's open app must not fail the build tooling.
        if (Environment.GetEnvironmentVariable("PLP_SELFTEST") == "1")
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plp_selftest.txt");
            try
            {
                _ = new Windows.ConsentWindow();
                _ = new Windows.OnboardingWindow();
                _ = new Windows.AboutWindow();
                _ = new Windows.PolicyWindow();
                _ = new MainWindow();
                System.IO.File.WriteAllText(log, "OK: all windows constructed");
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(log, "FAIL: " + ex);
            }
            Shutdown();
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
