using System.Windows;
using System.Windows.Input;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Windows;

/// <summary>Read-only policy viewer. Never mutates <see cref="AppSettings"/> consent state —
/// it only displays the same <see cref="PolicyText"/> the acceptance flow shows (Batch 4F §7).</summary>
public partial class PolicyWindow : Window
{
    public PolicyWindow()
    {
        InitializeComponent();
        PolicyBox.Text = PolicyText.Privacy;
        PreviewKeyDown += PolicyWindow_PreviewKeyDown;
    }

    internal string BodyForTests => PolicyBox.Text;

    private void PolicyWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
