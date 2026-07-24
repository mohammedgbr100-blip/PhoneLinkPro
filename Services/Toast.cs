using System.Windows;

namespace ScrcpyDeck.Services;

/// <summary>Non-blocking notifications: any code calls Show(), MainWindow renders them.</summary>
public static class Toast
{
    /// <summary>(message, success) — raised on the UI thread.</summary>
    public static event Action<string, bool>? Requested;

    public static void Show(string message, bool ok = true)
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) Requested?.Invoke(message, ok);
        else app.Dispatcher.BeginInvoke(() => Requested?.Invoke(message, ok));
    }
}
