using System.IO;
using System.Windows;
using System.Windows.Input;

namespace ScrcpyDeck.Windows;

/// <summary>
/// Read-only third-party notices viewer (Batch 4F §8). Shows exactly the notices text actually
/// present in the repository/package (<c>THIRD-PARTY-NOTICES.txt</c> beside the executable) —
/// never invented, never claimed complete beyond what that file states. Opening this window has
/// no side effects: it only reads one fixed local file and displays it.
/// </summary>
public partial class NoticesWindow : Window
{
    internal const string MissingMessage =
        "تعذّر العثور على ملف إشعارات الطرف الثالث ضمن نسخة التثبيت الحالية — الإشعارات المتاحة غير مكتملة.";
    internal const string AvailableMessage =
        "الإشعارات المعروضة أدناه هي المتاحة فعلياً ضمن نسخة التثبيت الحالية.";

    /// <summary>Test seam: production reads the real file beside the executable.</summary>
    internal static Func<string?>? ContentProviderForTests;

    public NoticesWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadContent();
        PreviewKeyDown += NoticesWindow_PreviewKeyDown;
    }

    internal string BodyForTests => NoticesBox.Text;
    internal string StatusForTests => StatusText.Text;

    private void NoticesWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void LoadContent()
    {
        var provider = ContentProviderForTests ?? ReadRealNotices;
        string? content;
        try { content = provider(); }
        catch { content = null; }

        if (string.IsNullOrWhiteSpace(content))
        {
            NoticesBox.Text = "";
            StatusText.Text = MissingMessage;
        }
        else
        {
            NoticesBox.Text = content;
            StatusText.Text = AvailableMessage;
        }
    }

    private static string? ReadRealNotices()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
