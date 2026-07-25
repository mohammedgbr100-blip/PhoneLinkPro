using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>LOG-02 (Batch 1F) — the redaction rules used by logs and the support bundle.</summary>
public sealed class LogSanitizerTests
{
    // ---------------------------------------------------------------- serial masking

    [Fact]
    public void MaskSerial_KeepsShapeButHidesTheValue()
    {
        var masked = LogSanitizer.MaskSerial("R58M12ABCDE");

        Assert.StartsWith("R58…#", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("R58M12ABCDE", masked);
        Assert.Matches("#[0-9a-f]{8}$", masked);
    }

    [Fact] // stable: the same serial always masks to the same tag (correlating reports works)
    public void MaskSerial_IsDeterministic()
        => Assert.Equal(LogSanitizer.MaskSerial("R58M12ABCDE"), LogSanitizer.MaskSerial("R58M12ABCDE"));

    [Fact]
    public void MaskSerial_HandlesNullAndShortValues()
    {
        Assert.Equal("(none)", LogSanitizer.MaskSerial(null));
        Assert.Equal("(none)", LogSanitizer.MaskSerial("  "));
        Assert.StartsWith("ab…#", LogSanitizer.MaskSerial("ab"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- text pipeline

    [Fact] // user-profile paths never leave the machine
    public void Sanitize_MasksUserProfilePaths()
    {
        var text = @"failed on C:\Users\testuser\Videos\clip.mp4 and D:\Users\Ali Hassan\x.txt";

        var clean = LogSanitizer.Sanitize(text);

        Assert.DoesNotContain("testuser", clean);
        Assert.DoesNotContain("Ali", clean);
        Assert.Contains(@"C:\Users\<user>", clean);
    }

    [Fact] // the REAL profile path is masked even when it is not on drive C
    public void Sanitize_MasksTheActualProfilePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clean = LogSanitizer.Sanitize($"wrote log under {profile}\\AppData\\Roaming");

        Assert.DoesNotContain(profile, clean);
    }

    [Fact] // device serials in historical log lines are masked on export
    public void Sanitize_MasksSerialLikeTokens()
    {
        var clean = LogSanitizer.Sanitize("engine start (R58M12ABCDE) for device R58M12ABCDE");

        Assert.DoesNotContain("R58M12ABCDE", clean);
        Assert.Contains("R58…#", clean);
    }

    [Fact] // network addresses keep their shape but lose the host
    public void Sanitize_MasksIpAddresses()
    {
        var clean = LogSanitizer.Sanitize("connect 192.168.1.42:5555 failed");

        Assert.DoesNotContain("192.168.1.42", clean);
        Assert.Contains("192.168.*.*:5555", clean);
    }

    [Fact] // a six-digit token on a pairing line is a pairing code — scrubbed on export
    public void Sanitize_ScrubsPairingCodesOnPairLines()
    {
        var clean = LogSanitizer.Sanitize("adb pair 192.168.1.42:37123 code 246813 rejected");

        Assert.DoesNotContain("246813", clean);
        Assert.Contains("******", clean);
    }

    [Fact] // long hashes (integrity log lines) are shortened, not exported whole
    public void Sanitize_ShortensLongHexValues()
    {
        var hash = new string('a', 64);
        var clean = LogSanitizer.Sanitize($"expected {hash}");

        Assert.DoesNotContain(hash, clean);
        Assert.Contains("aaaaaaaa…", clean);
    }

    [Theory] // ordinary technical tokens survive untouched — the output must stay useful
    [InlineData("Shutdown: complete in 42ms")]
    [InlineData("EngineIntegrity: Valid (app v1.1.0, manifest v1, files=11, 34ms)")]
    [InlineData("DeviceWatcher: resumed")]
    public void Sanitize_LeavesOrdinaryDiagnosticsAlone(string line)
        => Assert.Equal(line, LogSanitizer.Sanitize(line));

    [Fact]
    public void Sanitize_HandlesEmptyInput()
        => Assert.Equal("", LogSanitizer.Sanitize(null));
}
