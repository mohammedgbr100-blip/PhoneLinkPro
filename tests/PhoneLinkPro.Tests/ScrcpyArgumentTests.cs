using ScrcpyDeck.Models;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// CMD-01 — <see cref="ScrcpyOptions.BuildArgumentList"/>.
///
/// Batch 1B replaced the concatenated command line with an ordered argv collection that is
/// handed to ProcessStartInfo.ArgumentList element by element. Every user-controlled value
/// must therefore occupy exactly one element and stay literal.
///
/// The Batch 1A characterization tests that pinned the unsafe concatenated output have been
/// REPLACED (not deleted) by the CMD01_* tests at the bottom, which assert the safe argv.
/// </summary>
public class ScrcpyArgumentTests
{
    private static IReadOnlyList<string> Args(ScrcpyOptions o) => o.BuildArgumentList();

    // ---------------------------------------------------------------- basics

    [Fact]
    public void NoOptions_ProducesEmptyArgv()
        => Assert.Empty(Args(new ScrcpyOptions()));

    [Fact]
    public void UsbSerial_IsOneArgument()
        => Assert.Equal(["-s", "R58M12ABCDE"], Args(new ScrcpyOptions { Serial = "R58M12ABCDE" }));

    [Fact]
    public void TcpIpSerial_IsOneArgument()
        => Assert.Equal(["-s", "192.168.1.42:5555"],
            Args(new ScrcpyOptions { Serial = "192.168.1.42:5555" }));

    [Fact]
    public void WhitespaceSerial_IsOmitted()
        => Assert.Empty(Args(new ScrcpyOptions { Serial = "   " }));

    // ---------------------------------------------------------------- quality

    [Fact]
    public void MaxSizeBitrateAndFps_AreEmittedInOrder()
        => Assert.Equal(["-m", "1920", "-b", "16M", "--max-fps", "60"],
            Args(new ScrcpyOptions { MaxSize = 1920, VideoBitRate = "16M", MaxFps = 60 }));

    [Fact]
    public void ZeroMaxSizeAndFps_AreOmitted()
        => Assert.Empty(Args(new ScrcpyOptions { MaxSize = 0, MaxFps = 0 }));

    [Fact]
    public void NegativeMaxSize_IsOmitted()
        => Assert.Empty(Args(new ScrcpyOptions { MaxSize = -5 }));

    // ---------------------------------------------------------------- switches

    [Fact]
    public void Fullscreen_EmitsDashF()
        => Assert.Equal(["-f"], Args(new ScrcpyOptions { Fullscreen = true }));

    [Fact]
    public void StayAwake_EmitsDashW()
        => Assert.Equal(["-w"], Args(new ScrcpyOptions { StayAwake = true }));

    [Fact]
    public void TurnScreenOff_EmitsCapitalS()
        => Assert.Equal(["-S"], Args(new ScrcpyOptions { TurnScreenOff = true }));

    [Fact]
    public void NoAudio_EmitsNoAudio()
        => Assert.Equal(["--no-audio"], Args(new ScrcpyOptions { NoAudio = true }));

    [Fact]
    public void NoAudioAndMic_NoAudioTakesPrecedence()
        => Assert.Equal(["--no-audio"],
            Args(new ScrcpyOptions { NoAudio = true, MicAsSource = true }));

    [Fact]
    public void MicAsSource_AloneEmitsAudioSource()
        => Assert.Equal(["--audio-source=mic"], Args(new ScrcpyOptions { MicAsSource = true }));

    [Fact]
    public void PowerOffOnClose_IsEmitted()
        => Assert.Equal(["--power-off-on-close"], Args(new ScrcpyOptions { PowerOffOnClose = true }));

    [Fact]
    public void RemainingSwitches_AreEmittedInDeclaredOrder()
        => Assert.Equal(
            ["--always-on-top", "--window-borderless", "-t", "--no-control",
             "--disable-screensaver", "--keyboard=uhid", "--print-fps"],
            Args(new ScrcpyOptions
            {
                AlwaysOnTop = true,
                Borderless = true,
                ShowTouches = true,
                NoControl = true,
                DisableScreensaver = true,
                UhidKeyboard = true,
                PrintFps = true,
            }));

    // ---------------------------------------------------------------- display

    [Fact]
    public void DisplayId_IsEmitted()
        => Assert.Equal(["--display-id", "2"], Args(new ScrcpyOptions { DisplayId = 2 }));

    [Fact]
    public void NewDisplay_EmptyString_EmitsBareFlag()
        => Assert.Equal(["--new-display"], Args(new ScrcpyOptions { NewDisplay = "" }));

    [Fact]
    public void NewDisplay_WithSize_EmitsEqualsForm()
        => Assert.Equal(["--new-display=1920x1080"],
            Args(new ScrcpyOptions { NewDisplay = "1920x1080" }));

    // ---------------------------------------------------------------- recording

    [Fact]
    public void Recording_PathIsOneUnquotedArgument()
        => Assert.Equal(["--record", @"C:\videos\out.mp4"],
            Args(new ScrcpyOptions { Record = true, RecordPath = @"C:\videos\out.mp4" }));

    [Fact]
    public void RecordingPathWithSpaces_StaysOneArgument()
        => Assert.Equal(["--record", @"C:\my videos\session one.mp4"],
            Args(new ScrcpyOptions { Record = true, RecordPath = @"C:\my videos\session one.mp4" }));

    [Fact]
    public void ArabicRecordingPath_StaysOneLiteralArgument()
        => Assert.Equal(["--record", @"C:\تسجيلات\جلسة أولى.mp4"],
            Args(new ScrcpyOptions { Record = true, RecordPath = @"C:\تسجيلات\جلسة أولى.mp4" }));

    [Fact]
    public void RecordWithoutPath_EmitsNothing()
        => Assert.Empty(Args(new ScrcpyOptions { Record = true }));

    [Fact]
    public void NoPlayback_RequiresRecording()
    {
        Assert.Empty(Args(new ScrcpyOptions { NoPlaybackWhileRecording = true }));

        Assert.Equal(["--record", "o.mp4", "--no-playback"],
            Args(new ScrcpyOptions
            {
                Record = true,
                RecordPath = "o.mp4",
                NoPlaybackWhileRecording = true,
            }));
    }

    [Fact] // a trailing backslash used to escape the closing quote; now it is just data
    public void TrailingBackslashInPath_IsOneLiteralArgument()
        => Assert.Equal(["--record", @"C:\videos\"],
            Args(new ScrcpyOptions { Record = true, RecordPath = @"C:\videos\" }));

    // ---------------------------------------------------------------- window title

    [Fact]
    public void WindowTitle_IsOneUnquotedArgument()
        => Assert.Equal(["--window-title", "Galaxy A52"],
            Args(new ScrcpyOptions { WindowTitle = "Galaxy A52" }));

    [Fact]
    public void ArabicWindowTitle_StaysOneLiteralArgument()
        => Assert.Equal(["--window-title", "هاتف محمد"],
            Args(new ScrcpyOptions { WindowTitle = "هاتف محمد" }));

    [Fact]
    public void WindowTitleWithSpaces_StaysOneArgument()
        => Assert.Equal(["--window-title", "Samsung Galaxy A52 5G"],
            Args(new ScrcpyOptions { WindowTitle = "Samsung Galaxy A52 5G" }));

    // ---------------------------------------------------------------- combination

    [Fact]
    public void TypicalLaunch_ProducesStableOrdering()
        => Assert.Equal(
            ["-s", "192.168.1.42:5555", "-m", "1280", "-b", "8M", "--max-fps", "60",
             "--no-audio", "-f", "--window-title", "هاتف محمد", "-w",
             "--record", @"C:\out\clip.mp4"],
            Args(new ScrcpyOptions
            {
                Serial = "192.168.1.42:5555",
                MaxSize = 1280,
                VideoBitRate = "8M",
                MaxFps = 60,
                NoAudio = true,
                Fullscreen = true,
                StayAwake = true,
                WindowTitle = "هاتف محمد",
                Record = true,
                RecordPath = @"C:\out\clip.mp4",
            }));

    // ================================================================
    //  CMD-01 — hostile input must stay literal data
    // ================================================================
    // These replace the Batch 1A characterization tests that pinned the unsafe
    // concatenated output, and the Skip()ped tests that asserted the target behaviour.

    [Fact] // CMD-01 — was: quote broke out of the quoted argument
    public void CMD01_QuoteInWindowTitle_DoesNotInjectArguments()
    {
        var argv = Args(new ScrcpyOptions { WindowTitle = "pwn\" --record \"C:\\evil.mp4" });

        // Exactly two elements: the flag and the title, verbatim.
        Assert.Equal(["--window-title", "pwn\" --record \"C:\\evil.mp4"], argv);
        Assert.DoesNotContain("--record", argv);
    }

    [Fact] // CMD-01 — was: injected --no-control survived as a separate argument
    public void CMD01_QuoteInRecordPath_DoesNotInjectArguments()
    {
        var argv = Args(new ScrcpyOptions
        {
            Record = true,
            RecordPath = "out.mp4\" --no-control --turn-screen-off \"x.mp4",
        });

        Assert.Equal(["--record", "out.mp4\" --no-control --turn-screen-off \"x.mp4"], argv);
        Assert.DoesNotContain("--no-control", argv);
    }

    [Fact] // CMD-01 — was: whitespace split the serial into extra arguments
    public void CMD01_SpacesInSerial_DoNotInjectArguments()
    {
        var argv = Args(new ScrcpyOptions { Serial = "abc --no-control" });

        Assert.Equal(["-s", "abc --no-control"], argv);
        Assert.DoesNotContain("--no-control", argv);
    }

    [Fact] // CMD-01 — was: bitrate accepted injected text
    public void CMD01_InjectedTextInBitrate_StaysOneArgument()
    {
        var argv = Args(new ScrcpyOptions { VideoBitRate = "8M --no-control" });

        Assert.Equal(["-b", "8M --no-control"], argv);
        Assert.DoesNotContain("--no-control", argv);
    }

    [Fact] // CMD-01 — text that merely LOOKS like an option is still a single value
    public void CMD01_OptionLookingTitle_IsStillOneValue()
    {
        var argv = Args(new ScrcpyOptions { WindowTitle = "--record" });

        Assert.Equal(["--window-title", "--record"], argv);
        Assert.Equal(2, argv.Count);
        // It occupies the VALUE slot, immediately after its flag.
        Assert.Equal("--window-title", argv[0]);
    }

    [Theory] // CMD-01 — shell metacharacters are inert data, not syntax
    [InlineData("a & b")]
    [InlineData("a && b")]
    [InlineData("a | b")]
    [InlineData("a ; b")]
    [InlineData("a > out.txt")]
    [InlineData("%USERPROFILE%")]
    [InlineData("$(whoami)")]
    [InlineData("`whoami`")]
    [InlineData("a (b) c")]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("a\\b\\")]
    [InlineData("جهاز & هاتف")]
    public void CMD01_ShellMetacharactersInTitle_StayLiteral(string title)
    {
        var argv = Args(new ScrcpyOptions { WindowTitle = title });

        Assert.Equal(["--window-title", title], argv);
    }

    [Theory] // CMD-01 — the same for recording paths
    [InlineData(@"C:\out\a & b.mp4")]
    [InlineData(@"C:\out\a;b.mp4")]
    [InlineData(@"C:\out\a`b.mp4")]
    [InlineData(@"C:\out\a""b.mp4")]
    [InlineData(@"C:\تسجيلات\ملف (1).mp4")]
    [InlineData(@"C:\out\%TEMP%.mp4")]
    public void CMD01_ShellMetacharactersInRecordPath_StayLiteral(string path)
    {
        var argv = Args(new ScrcpyOptions { Record = true, RecordPath = path });

        Assert.Equal(["--record", path], argv);
    }

    [Fact] // CMD-01 — a value is never split, however hostile
    public void CMD01_EveryUserValueOccupiesExactlyOneElement()
    {
        var argv = Args(new ScrcpyOptions
        {
            Serial = "1.2.3.4:5555",
            WindowTitle = "t\" --no-control \"x",
            Record = true,
            RecordPath = "p\" --no-audio \"q.mp4",
        });

        Assert.Equal(
            ["-s", "1.2.3.4:5555",
             "--window-title", "t\" --no-control \"x",
             "--record", "p\" --no-audio \"q.mp4"], argv);
        Assert.DoesNotContain("--no-control", argv);
        Assert.DoesNotContain("--no-audio", argv);
    }
}
