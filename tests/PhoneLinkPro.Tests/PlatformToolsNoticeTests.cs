using System.Diagnostics;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Public-release audit (licensing): the bundled adb.exe is Google's prebuilt from Android SDK
/// Platform-Tools 36.0.0 (build 13206524) and statically links third-party libraries whose
/// notices are only in that package's NOTICE.txt. Until that exact file is reproduced as
/// Licenses\platform-tools-NOTICE.txt the release pipeline must refuse to mark a build as
/// publicly releasable, and the file may only come from the official package whose adb files
/// are proven byte-identical to the shipped engine. The fetch script is never run by the build
/// or by these tests against the network: obtaining the package is governed by the Android SDK
/// License Agreement and needs the publisher's explicit decision.
/// </summary>
public sealed class PlatformToolsNoticeTests
{
    private const string NoticePath = "Licenses\\platform-tools-NOTICE.txt";
    private static string FetchScript() => TestPaths.ReadRepoFile("Security", "fetch-platform-tools-notice.ps1");
    private static string ReleaseScript() => TestPaths.ReadRepoFile("release.ps1");

    // ---------------------------------------------------------------- release gate

    [Fact]
    public void Release_IsNotPubliclyReleasableWithoutThePlatformToolsNotice()
    {
        var script = ReleaseScript();
        Assert.Contains($"Test-Path '{NoticePath}'", script);
        Assert.Contains("$releasable = $signed -and ($distributionBlockers.Count -eq 0)", script);
        Assert.Contains("releasable            = $releasable", script);
        Assert.Contains("distributionBlockers  = $distributionBlockers", script);
        Assert.Contains("if (-not $releasable) {", script);
    }

    [Fact] // independent review: a SIGNED build that is still blocked for licensing must not get
           // release-looking file names (before this, only the -UNSIGNED suffix existed)
    public void Release_BlockedBuildsCarryABlockedSuffixEvenWhenSigned()
    {
        var script = ReleaseScript();
        var releasable = script.IndexOf("$releasable = $signed -and ($distributionBlockers.Count -eq 0)", StringComparison.Ordinal);
        var suffix = script.IndexOf("elseif (-not $releasable) { $suffix = '-BLOCKED' }", StringComparison.Ordinal);
        var firstUse = script.IndexOf("PhoneLinkPro-Setup-v$version$suffix.exe", StringComparison.Ordinal);
        Assert.True(releasable >= 0 && suffix > releasable, "the -BLOCKED suffix must be decided from $releasable");
        Assert.True(firstUse > suffix, "the suffix must be decided before any artifact is named");
        Assert.Contains("if (-not $signed) { $suffix = '-UNSIGNED' }", script);
    }

    [Fact] // the build never downloads anything: the fetch is a separate, deliberate step
    public void FetchScript_IsNeverRunByTheBuildOrTheReleasePipeline()
    {
        Assert.DoesNotMatch(@"-File\s+'?\.?\\?Security\\fetch-platform-tools-notice", ReleaseScript());
        Assert.DoesNotContain("fetch-platform-tools-notice", TestPaths.ReadRepoFile("ScrcpyDeck.csproj"));
        Assert.DoesNotContain("fetch-platform-tools-notice", TestPaths.ReadRepoFile("installer", "build-installer.bat"));
    }

    // ---------------------------------------------------------------- fetch script contract

    [Fact]
    public void FetchScript_PinsTheExactOfficialPackage()
    {
        var script = FetchScript();
        Assert.Contains("https://dl.google.com/android/repository/platform-tools_r36.0.0-win.zip", script);
        // SHA-256 pinned by scrcpy v3.3.4 app/deps/adb_windows.sh; size from Google's server.
        Assert.Contains("12c2841f354e92a0eb2fd7bf6f0f9bf8538abce7bd6b060ac8349d6f6a61107c", script);
        Assert.Contains("7138784", script);
        Assert.Contains("Pkg.Revision=36.0.0", script);
    }

    [Theory] // the NOTICE is only accepted from a package whose adb files ARE the shipped engine
    [InlineData("engine/PhoneLink.Bridge.exe", "adb.exe")]
    [InlineData("engine/AdbWinApi.dll", "AdbWinApi.dll")]
    [InlineData("engine/AdbWinUsbApi.dll", "AdbWinUsbApi.dll")]
    public void FetchScript_ProvesThePackagedAdbFilesAreTheShippedEngine(string manifestPath, string packageFile)
    {
        var script = FetchScript();
        Assert.Contains("Resources\\engine.manifest", script);
        Assert.Contains($"'{manifestPath}' = '{packageFile}'", script);
    }

    [Fact]
    public void FetchScript_RequiresAnExplicitSdkTermsDecisionBeforeAnyDownload()
    {
        var script = FetchScript();
        var gate = script.IndexOf("if (-not $AcceptAndroidSdkLicense)", StringComparison.Ordinal);
        var download = script.IndexOf("Invoke-WebRequest", StringComparison.Ordinal);
        Assert.True(gate >= 0, "the script must require -AcceptAndroidSdkLicense");
        Assert.True(download > gate, "the terms decision must come before the download");
    }

    // ---------------------------------------------------------------- fetch script behaviour (offline)

    [Fact]
    public void FetchScript_WithoutTheTermsDecision_ExitsBeforeDownloading()
    {
        var output = Path.Combine(Path.GetTempPath(), "plp-notice-test-" + Guid.NewGuid().ToString("N") + ".txt");
        var (code, text) = RunFetch($"-OutFile \"{output}\"");
        Assert.Equal(2, code);
        Assert.Contains("Android SDK License Agreement", text);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void FetchScript_RejectsAPackageThatIsNotTheOfficialOne_AndWritesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plp-notice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fakeZip = Path.Combine(dir, "platform-tools_r36.0.0-win.zip");
            File.WriteAllBytes(fakeZip, new byte[7138784]); // right size, wrong content
            var output = Path.Combine(dir, "NOTICE-out.txt");

            var (code, text) = RunFetch($"-ZipPath \"{fakeZip}\" -OutFile \"{output}\"");

            Assert.Equal(1, code);
            Assert.Contains("SHA-256 mismatch", text);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (int Code, string Output) RunFetch(string arguments)
    {
        var script = Path.Combine(TestPaths.RepoRoot(), "Security", "fetch-platform-tools-notice.ps1");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "fetch script did not exit within 60s");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
