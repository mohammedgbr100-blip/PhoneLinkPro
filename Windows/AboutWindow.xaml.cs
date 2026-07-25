using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "الإصدار " + AppVersionInfo.ProductVersion;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                Logo.Source = bmp;
            }
        }
        catch { }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    private void Policy_Click(object sender, RoutedEventArgs e)
    {
        var w = new PolicyWindow { Owner = this };
        w.ShowDialog();
    }

    /// <summary>Opens the third-party notices shipped next to the executable (licence requirement).</summary>
    private void Notices_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "تعذّر العثور على ملف إشعارات الطرف الثالث.", "PhoneLink Pro",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    /// <summary>
    /// Exports the sanitized support bundle (LOG-02) to a user-chosen path. Never throws
    /// through the UI: every outcome ends in a Toast.
    /// </summary>
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "حفظ ملف التشخيص",
            FileName = $"PhoneLinkPro-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "ZIP|*.zip",
            DefaultExt = ".zip",
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportDiagnosticsBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => SupportBundle.Export(dialog.FileName));
            Toast.Show(result.Success
                    ? "تم حفظ ملف التشخيص بنجاح."
                    : "تعذّر إنشاء ملف التشخيص. جرّب مساراً آخر أو تحقق من المساحة المتوفرة.",
                ok: result.Success);
        }
        finally
        {
            ExportDiagnosticsBtn.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
