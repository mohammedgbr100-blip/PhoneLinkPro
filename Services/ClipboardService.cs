namespace ScrcpyDeck.Services;

/// <summary>
/// Thin seam over <see cref="System.Windows.Clipboard"/> so tests never touch the user's real
/// clipboard. Reports success only when the underlying call actually completed without throwing.
/// </summary>
public static class ClipboardService
{
    internal static Func<string, bool>? SetTextOverrideForTests;

    public static bool TrySetText(string text)
    {
        var op = SetTextOverrideForTests ?? RealSetText;
        try
        {
            return op(text);
        }
        catch
        {
            return false;
        }
    }

    private static bool RealSetText(string text)
    {
        System.Windows.Clipboard.SetText(text);
        return true;
    }

    internal static void ResetForTests() => SetTextOverrideForTests = null;
}
