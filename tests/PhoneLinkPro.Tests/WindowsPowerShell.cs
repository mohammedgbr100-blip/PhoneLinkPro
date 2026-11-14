using System.Diagnostics;

namespace PhoneLinkPro.Tests;

internal static class WindowsPowerShell
{
    /// <summary>
    /// Lets a Windows PowerShell 5.1 child build its own module path. When the test host descends
    /// from PowerShell 7 (the GitHub Actions `pwsh` step shell, or a contributor's pwsh terminal),
    /// the inherited PSModulePath lists PowerShell 7's module folders first; 5.1 then resolves
    /// Microsoft.PowerShell.Utility from there, cannot load it, and Get-FileHash disappears.
    /// Without the variable, 5.1 uses its default user, Program Files and $PSHOME module folders.
    /// </summary>
    internal static ProcessStartInfo WithOwnModulePath(this ProcessStartInfo psi)
    {
        psi.Environment.Remove("PSModulePath");
        return psi;
    }
}
