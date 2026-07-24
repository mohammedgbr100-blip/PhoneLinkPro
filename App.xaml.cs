using System.Windows;
using System.Windows.Threading;

namespace ScrcpyDeck;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.Log.Info($"PhoneLink Pro starting (v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version})");
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Services.Log.Error("Fatal unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Services.Log.Error("Unobserved task exception", a.Exception);
            a.SetObserved();
        };
        ScrcpyDeck.Services.SecurityGuard.ArmIfRelease();

        // Hidden self-test: construct every window to catch XAML/resource errors, then exit.
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
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            "حدث خطأ غير متوقع:\n\n" + e.Exception.Message,
            "PhoneLink Pro",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
