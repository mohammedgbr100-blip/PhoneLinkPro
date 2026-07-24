using System.Windows;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Windows;

public partial class PolicyWindow : Window
{
    public PolicyWindow()
    {
        InitializeComponent();
        PolicyBox.Text = PolicyText.Privacy;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
