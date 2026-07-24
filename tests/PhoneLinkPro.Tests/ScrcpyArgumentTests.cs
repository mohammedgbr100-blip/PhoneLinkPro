using ScrcpyDeck.Models;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 4 — <see cref="ScrcpyOptions.BuildArguments"/>.
///
/// This is the before/after safety boundary for the Batch 1B ArgumentList migration:
/// every expected string here is the argument line produced TODAY. When the migration
/// lands, the correct-behaviour equivalents are the [Fact(Skip=...)] CMD-01 tests.
/// </summary>
public class ScrcpyArgumentTests
{
    // ---------------------------------------------------------------- basics

    [Fact] // [REGRESSION]
    public void NoOptions_ProducesEmptyArgumentString()
        => Assert.Equal("", new ScrcpyOptions().BuildArguments());

    [Fact] // [REGRESSION]
    public void UsbSerial_EmitsDashS()
        => Assert.Equal("-s R58M12ABCDE",
            new ScrcpyOptions { Serial = "R58M12ABCDE" }.BuildArguments());

    [Fact] // [REGRESSION]
    public void TcpIpSerial_EmitsDashS()
        => Assert.Equal("-s 192.168.1.42:5555",
            new ScrcpyOptions { Serial = "192.168.1.42:5555" }.BuildArguments());

    [Fact] // [REGRESSION]
    public void WhitespaceSerial_IsOmitted()
        => Assert.Equal("", new ScrcpyOptions { Serial = "   " }.BuildArguments());

    // ---------------------------------------------------------------- quality

    [Fact] // [REGRESSION]
    public void MaxSizeBitrateAndFps_AreEmittedInOrder()
    {
        var args = new ScrcpyOptions { MaxSize = 1920, VideoBitRate = "16M", MaxFps = 60 }
            .BuildArguments();

        Assert.Equal("-m 1920 -b 16M --max-fps 60", args);
    }

    [Fact] // [REGRESSION] — zero means "unlimited" and is omitted
    public void ZeroMaxSizeAndFps_AreOmitted()
        => Assert.Equal("", new ScrcpyOptions { MaxSize = 0, MaxFps = 0 }.BuildArguments());

    [Fact] // [REGRESSION] — negative values are treated as unset
    public void NegativeMaxSize_IsOmitted()
        => Assert.Equal("", new ScrcpyOptions { MaxSize = -5 }.BuildArguments());

    // ---------------------------------------------------------------- switches

    [Fact] // [REGRESSION]
    public void Fullscreen_EmitsDashF()
        => Assert.Equal("-f", new ScrcpyOptions { Fullscreen = true }.BuildArguments());

    [Fact] // [REGRESSION]
    public void StayAwake_EmitsDashW()
        => Assert.Equal("-w", new ScrcpyOptions { StayAwake = true }.BuildArguments());

    [Fact] // [REGRESSION]
    public void TurnScreenOff_EmitsCapitalS()
        => Assert.Equal("-S", new ScrcpyOptions { TurnScreenOff = true }.BuildArguments());

    [Fact] // [REGRESSION]
    public void NoAudio_EmitsNoAudio()
        => Assert.Equal("--no-audio", new ScrcpyOptions { NoAudio = true }.BuildArguments());

    [Fact] // [REGRESSION] — NoAudio wins over MicAsSource
    public void NoAudioAndMic_NoAudioTakesPrecedence()
        => Assert.Equal("--no-audio",
            new ScrcpyOptions { NoAudio = true, MicAsSource = true }.BuildArguments());

    [Fact] // [REGRESSION]
    public void MicAsSource_AloneEmitsAudioSource()
        => Assert.Equal("--audio-source=mic",
            new ScrcpyOptions { MicAsSource = true }.BuildArguments());

    [Fact] // [REGRESSION]
    public void PowerOffOnClose_IsEmitted()
        => Assert.Equal("--power-off-on-close",
            new ScrcpyOptions { PowerOffOnClose = true }.BuildArguments());

    [Fact] // [REGRESSION]
    public void RemainingSwitches_AreEmittedInDeclaredOrder()
    {
        var args = new ScrcpyOptions
        {
            AlwaysOnTop = true,
            Borderless = true,
            ShowTouches = true,
            NoControl = true,
            DisableScreensaver = true,
            UhidKeyboard = true,
            PrintFps = true,
        }.BuildArguments();

        Assert.Equal(
            "--always-on-top --window-borderless -t --no-control " +
            "--disable-screensaver --keyboard=uhid --print-fps", args);
    }

    // ---------------------------------------------------------------- display

    [Fact] // [REGRESSION]
    public void DisplayId_IsEmitted()
        => Assert.Equal("--display-id 2", new ScrcpyOptions { DisplayId = 2 }.BuildArguments());

    [Fact] // [REGRESSION] — empty string means "default virtual display"
    public void NewDisplay_EmptyString_EmitsBareFlag()
        => Assert.Equal("--new-display", new ScrcpyOptions { NewDisplay = "" }.BuildArguments());

    [Fact] // [REGRESSION]
    public void NewDisplay_WithSize_EmitsEqualsForm()
        => Assert.Equal("--new-display=1920x1080",
            new ScrcpyOptions { NewDisplay = "1920x1080" }.BuildArguments());

    // ---------------------------------------------------------------- recording

    [Fact] // [REGRESSION]
    public void Recording_QuotesThePath()
        => Assert.Equal("--record \"C:\\videos\\out.mp4\"",
            new ScrcpyOptions { Record = true, RecordPath = @"C:\videos\out.mp4" }.BuildArguments());

    [Fact] // [REGRESSION]
    public void RecordingPathWithSpaces_IsQuoted()
        => Assert.Equal("--record \"C:\\my videos\\session one.mp4\"",
            new ScrcpyOptions { Record = true, RecordPath = @"C:\my videos\session one.mp4" }
                .BuildArguments());

    [Fact] // [REGRESSION] — Arabic path round-trips unchanged inside the quotes
    public void ArabicRecordingPath_IsQuotedVerbatim()
        => Assert.Equal("--record \"C:\\تسجيلات\\جلسة أولى.mp4\"",
            new ScrcpyOptions { Record = true, RecordPath = @"C:\تسجيلات\جلسة أولى.mp4" }
                .BuildArguments());

    [Fact] // [REGRESSION] — Record without a path emits nothing
    public void RecordWithoutPath_EmitsNothing()
        => Assert.Equal("", new ScrcpyOptions { Record = true }.BuildArguments());

    [Fact] // [REGRESSION] — no-playback only applies while recording
    public void NoPlayback_RequiresRecording()
    {
        Assert.Equal("", new ScrcpyOptions { NoPlaybackWhileRecording = true }.BuildArguments());

        Assert.Equal("--record \"o.mp4\" --no-playback",
            new ScrcpyOptions { Record = true, RecordPath = "o.mp4", NoPlaybackWhileRecording = true }
                .BuildArguments());
    }

    // ---------------------------------------------------------------- window title

    [Fact] // [REGRESSION]
    public void WindowTitle_IsQuoted()
        => Assert.Equal("--window-title \"Galaxy A52\"",
            new ScrcpyOptions { WindowTitle = "Galaxy A52" }.BuildArguments());

    [Fact] // [REGRESSION] — Arabic title round-trips unchanged
    public void ArabicWindowTitle_IsQuotedVerbatim()
        => Assert.Equal("--window-title \"هاتف محمد\"",
            new ScrcpyOptions { WindowTitle = "هاتف محمد" }.BuildArguments());

    [Fact] // [REGRESSION]
    public void WindowTitleWithSpaces_IsQuoted()
        => Assert.Equal("--window-title \"Samsung Galaxy A52 5G\"",
            new ScrcpyOptions { WindowTitle = "Samsung Galaxy A52 5G" }.BuildArguments());

    // ---------------------------------------------------------------- realistic combination

    [Fact] // [REGRESSION] — full ordering contract for a typical launch
    public void TypicalLaunch_ProducesStableOrdering()
    {
        var args = new ScrcpyOptions
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
        }.BuildArguments();

        Assert.Equal(
            "-s 192.168.1.42:5555 -m 1280 -b 8M --max-fps 60 --no-audio -f " +
            "--window-title \"هاتف محمد\" -w --record \"C:\\out\\clip.mp4\"", args);
    }

    // ================================================================
    //  CMD-01 — argument injection via unescaped interpolation
    // ================================================================
    // BuildArguments() concatenates a single command line and wraps user-controlled values
    // in literal double quotes. Any value that itself contains a double quote terminates
    // the quoted run early, so the remainder is parsed by scrcpy as additional arguments.
    // The resulting string is handed to ProcessStartInfo.Arguments verbatim.
    //
    // These tests PIN THE UNSAFE OUTPUT so the Batch 1B ArgumentList migration has an
    // exact before/after boundary. They assert what the code does, NOT that it is safe.

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_QuoteInWindowTitle_BreaksOutOfTheQuotedArgument()
    {
        var args = new ScrcpyOptions { WindowTitle = "pwn\" --record \"C:\\evil.mp4" }
            .BuildArguments();

        // The injected --record survives as a separate argument.
        Assert.Equal("--window-title \"pwn\" --record \"C:\\evil.mp4\"", args);
        Assert.Contains("--record", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_QuoteInRecordPath_InjectsAdditionalFlags()
    {
        var args = new ScrcpyOptions
        {
            Record = true,
            RecordPath = "out.mp4\" --no-control --turn-screen-off \"x.mp4",
        }.BuildArguments();

        Assert.Equal("--record \"out.mp4\" --no-control --turn-screen-off \"x.mp4\"", args);
        Assert.Contains("--no-control", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_SpacesInSerial_SplitIntoSeparateArguments()
    {
        // Serial is not quoted at all, so whitespace splits it.
        var args = new ScrcpyOptions { Serial = "abc --no-control" }.BuildArguments();

        Assert.Equal("-s abc --no-control", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — unsafe, fix in Batch 1B
    public void CMD01_UnquotedBitrateAndDisplayId_AcceptInjectedText()
    {
        var args = new ScrcpyOptions { VideoBitRate = "8M --no-control" }.BuildArguments();

        Assert.Equal("-b 8M --no-control", args);
    }

    [Fact] // [CHARACTERIZATION] CMD-01 — trailing backslash before the closing quote
    public void CMD01_TrailingBackslashInPath_EscapesTheClosingQuote()
    {
        var args = new ScrcpyOptions { Record = true, RecordPath = @"C:\videos\" }
            .BuildArguments();

        // Produces --record "C:\videos\" — the closing quote is escaped by the backslash
        // under Windows command-line parsing rules, so the argument never terminates.
        Assert.Equal(@"--record ""C:\videos\""", args);
    }

    [Fact(Skip = "CMD-01: scrcpy arguments are built by string concatenation, so a quote " +
                 "in a user-controlled value injects additional arguments. This asserts the " +
                 "CORRECT post-fix behaviour (values passed via ArgumentList / properly " +
                 "escaped) and must be un-skipped by the migration. Target batch: 1B.")]
    public void CMD01_QuoteInWindowTitleShouldNotInjectArguments_TargetBatch1B()
    {
        var args = new ScrcpyOptions { WindowTitle = "pwn\" --record \"C:\\evil.mp4" }
            .BuildArguments();

        // After the fix the title must remain exactly one argument.
        Assert.DoesNotContain("--record", args);
    }

    [Fact(Skip = "CMD-01: record path is interpolated into the command line unescaped. " +
                 "Asserts the CORRECT post-fix behaviour. Target batch: 1B.")]
    public void CMD01_QuoteInRecordPathShouldNotInjectArguments_TargetBatch1B()
    {
        var args = new ScrcpyOptions
        {
            Record = true,
            RecordPath = "out.mp4\" --no-control --turn-screen-off \"x.mp4",
        }.BuildArguments();

        Assert.DoesNotContain("--no-control", args);
    }

    [Fact(Skip = "CMD-01: serial is interpolated unquoted, so whitespace splits it into " +
                 "extra arguments. Asserts the CORRECT post-fix behaviour. Target batch: 1B.")]
    public void CMD01_SerialWithSpacesShouldNotInjectArguments_TargetBatch1B()
    {
        var args = new ScrcpyOptions { Serial = "abc --no-control" }.BuildArguments();

        Assert.DoesNotContain("--no-control", args);
    }
}
