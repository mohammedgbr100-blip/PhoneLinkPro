using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 10 — non-UI smoke coverage.
///
/// Window construction stays with the existing PLP_SELFTEST harness (App.xaml.cs), which
/// runs on a real STA WPF thread. xUnit runs MTA, so constructing Windows here would be
/// fragile; no pixel-based UI assertions are added.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class SmokeTests
{
    [Fact] // [REGRESSION] — engine binaries are looked up under engine\ with neutral names
    public void ToolLocator_ResolvesEngineFilesUnderTheEngineFolder()
    {
        Assert.EndsWith(Path.Combine("engine", "PhoneLink.Bridge.exe"), ToolLocator.AdbPath);
        Assert.EndsWith(Path.Combine("engine", "PhoneLink.Engine.exe"), ToolLocator.ScrcpyPath);
        Assert.EndsWith(Path.Combine("engine", "core.dat"), ToolLocator.ServerPath);
    }

    [Fact] // [REGRESSION] — paths are absolute and rooted at the app base directory
    public void ToolLocator_ReturnsAbsolutePaths()
    {
        Assert.True(Path.IsPathRooted(ToolLocator.AdbPath));
        Assert.True(Path.IsPathRooted(ToolLocator.ScrcpyPath));
        Assert.True(Path.IsPathRooted(ToolLocator.ServerPath));
    }

    [Fact] // [REGRESSION] — availability probes must not throw when the engine is absent
    public void ToolLocator_AvailabilityChecksDoNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            _ = ToolLocator.AdbAvailable;
            _ = ToolLocator.ScrcpyAvailable;
        });

        Assert.Null(ex);
    }

    [Fact] // [REGRESSION] — presets keep their documented quality ladder
    public void ScrcpyService_PresetsProduceTheExpectedArguments()
    {
        Assert.Equal(["-s", "ABC", "-m", "1024", "-b", "4M", "--max-fps", "30"],
            ScrcpyService.Preset("low", "ABC").BuildArgumentList());
        Assert.Equal(["-s", "ABC", "-m", "1280", "-b", "8M", "--max-fps", "60"],
            ScrcpyService.Preset("medium", "ABC").BuildArgumentList());
        Assert.Equal(["-s", "ABC", "-m", "1920", "-b", "16M", "--max-fps", "60"],
            ScrcpyService.Preset("high", "ABC").BuildArgumentList());
        Assert.Equal(["-s", "ABC", "-m", "2560", "-b", "32M", "--max-fps", "60"],
            ScrcpyService.Preset("ultra", "ABC").BuildArgumentList());
    }

    [Fact] // [REGRESSION] — an unknown preset degrades to serial-only
    public void ScrcpyService_UnknownPresetFallsBackToSerialOnly()
        => Assert.Equal(["-s", "ABC"], ScrcpyService.Preset("nonexistent", "ABC").BuildArgumentList());

    [Fact] // [REGRESSION] — no sessions are running in a fresh process
    public void ScrcpyService_StartsWithNoActiveSessions()
        => Assert.Equal(0, ScrcpyService.ActiveCount);

    [Fact] // [REGRESSION] — the policy text the consent window renders is present
    public void PolicyText_IsPopulatedAndMentionsThirdPartyComponents()
    {
        Assert.False(string.IsNullOrWhiteSpace(PolicyText.Privacy));
        Assert.Equal("PhoneLink Pro", PolicyText.Product);

        // Batch 0 added the third-party open-source disclosure; keep it from regressing.
        Assert.Contains("THIRD-PARTY-NOTICES.txt", PolicyText.Privacy);
        Assert.Contains("LGPL", PolicyText.Privacy);
    }

    [Theory] // [REGRESSION] — device state labels used by the UI
    [InlineData("device", "متصل وجاهز")]
    [InlineData("unauthorized", "بانتظار الإذن على الهاتف")]
    [InlineData("offline", "غير متصل")]
    public void AdbDevice_StateLabelsAreMapped(string state, string expected)
        => Assert.Equal(expected, new AdbDevice { State = state }.StateLabel);

    [Fact] // [REGRESSION] — Toast is a no-op without a WPF Application, so headless
           // callers (including these tests) never crash.
    public void Toast_WithoutAnApplication_DoesNotThrow()
    {
        var ex = Record.Exception(() => Toast.Show("headless"));

        Assert.Null(ex);
    }

    [Fact] // [REGRESSION] — logging is best-effort and never throws
    public void Log_WritesToTheOverriddenDirectoryAndNeverThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plp-tests-log-" + Guid.NewGuid().ToString("N"));
        var previous = Log.DirOverride;
        Log.DirOverride = dir;
        try
        {
            var ex = Record.Exception(() =>
            {
                Log.Info("smoke info");
                Log.Warn("smoke warn");
                Log.Error("smoke error", new InvalidOperationException("boom"));
            });

            Assert.Null(ex);
            Assert.True(Directory.Exists(dir));
            Assert.NotEmpty(Directory.GetFiles(dir, "app-*.log"));
        }
        finally
        {
            Log.DirOverride = previous;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
