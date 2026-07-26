using System.Windows;

namespace ScrcpyDeck;

/// <summary>
/// Shell-level attached state for the Direction D chrome. <see cref="IsCompactProperty"/>
/// is set once on the window (from its measured width, see ShellLayout) and inherits down
/// the tree so styles can restyle themselves with plain property triggers — no code-behind
/// per control.
/// </summary>
public static class ShellChrome
{
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.RegisterAttached(
            "IsCompact",
            typeof(bool),
            typeof(ShellChrome),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject obj) => (bool)obj.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject obj, bool value) => obj.SetValue(IsCompactProperty, value);
}
