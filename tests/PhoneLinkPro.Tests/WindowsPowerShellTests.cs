using System.Diagnostics;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// A Windows PowerShell 5.1 child must not inherit PowerShell 7's module path. The first public CI
/// run failed this way: its test host descends from the `pwsh` step shell, and Get-FileHash vanished
/// inside the platform-tools fetch script. These tests reproduce that inherited path on any machine.
/// </summary>
public class WindowsPowerShellTests
{
    // SHA-256 of zero bytes.
    private const string EmptySha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    [Fact]
    public async Task WithOwnModulePath_KeepsGetFileHashWorking_WhenAPowerShell7ModulePathIsInherited()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plp-ps7-modules-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(dir, "Microsoft.PowerShell.Utility");
        Directory.CreateDirectory(module);
        try
        {
            // Stand-in for PowerShell 7's own Microsoft.PowerShell.Utility: same name, cannot load in 5.1.
            File.WriteAllText(Path.Combine(module, "Microsoft.PowerShell.Utility.psd1"),
                "@{ ModuleVersion = '7.0.0.0'; GUID = '1da87e53-152b-403e-98dc-74d7b4d63d59'; " +
                "PowerShellVersion = '7.0'; CompatiblePSEditions = @('Core'); " +
                "FunctionsToExport = @('Get-FileHash'); CmdletsToExport = @() }");

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("(Get-FileHash -Algorithm SHA256 -InputStream ([IO.MemoryStream]::new())).Hash");
            psi.Environment.TryGetValue("PSModulePath", out var inherited);
            psi.Environment["PSModulePath"] = dir + Path.PathSeparator + inherited;

            psi.WithOwnModulePath();

            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                await p.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(p.ExitCode == 0, "Get-FileHash failed: " + stderr);
            Assert.Equal(EmptySha256, stdout.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FetchScriptLauncher_UsesItsOwnModulePath()
    {
        var source = TestPaths.ReadRepoFile("tests/PhoneLinkPro.Tests/PlatformToolsNoticeTests.cs");
        Assert.Contains(".WithOwnModulePath()", source);
    }
}
