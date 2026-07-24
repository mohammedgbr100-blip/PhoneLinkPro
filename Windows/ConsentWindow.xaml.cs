using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Windows;

public partial class ConsentWindow : Window
{
    public ConsentWindow()
    {
        InitializeComponent();
        PolicyBox.Text = PolicyText.Privacy;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo_hero.png");
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

    private void Agree_Changed(object sender, RoutedEventArgs e)
        => AcceptBtn.IsEnabled = AgreeCheck.IsChecked == true;

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
