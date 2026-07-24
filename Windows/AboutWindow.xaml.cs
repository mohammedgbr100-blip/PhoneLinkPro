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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
