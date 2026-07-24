using System.Text;

namespace ScrcpyDeck.Models;

/// <summary>All scrcpy launch options the advanced launcher exposes.</summary>
public class ScrcpyOptions
{
    public string? Serial { get; set; }

    // Quality
    public int? MaxSize { get; set; }        // -m  (0 / null = unlimited)
    public string? VideoBitRate { get; set; } // -b  e.g. "16M"
    public int? MaxFps { get; set; }         // --max-fps

    // Audio
    public bool NoAudio { get; set; }
    public bool MicAsSource { get; set; }    // --audio-source=mic

    // Window
    public bool Fullscreen { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool Borderless { get; set; }
    public string? WindowTitle { get; set; }

    // Control / screen
    public bool TurnScreenOff { get; set; }  // -S
    public bool StayAwake { get; set; }      // -w
    public bool ShowTouches { get; set; }    // -t
    public bool NoControl { get; set; }
    public bool PowerOffOnClose { get; set; }
    public bool DisableScreensaver { get; set; }
    public bool UhidKeyboard { get; set; }   // --keyboard=uhid

    // Display
    public int? DisplayId { get; set; }      // --display-id
    public string? NewDisplay { get; set; }  // --new-display=WxH (empty string = default virtual display)

    // Recording
    public bool Record { get; set; }
    public string? RecordPath { get; set; }
    public bool NoPlaybackWhileRecording { get; set; } // --no-window / no-display style

    // Perf
    public bool PrintFps { get; set; }

    /// <summary>Build the full scrcpy argument string.</summary>
    public string BuildArguments()
    {
        var a = new StringBuilder();
        void Flag(string f) => a.Append(f).Append(' ');
        void Opt(string f, string v) => a.Append(f).Append(' ').Append(v).Append(' ');

        if (!string.IsNullOrWhiteSpace(Serial)) Opt("-s", Serial!);

        if (MaxSize is > 0) Opt("-m", MaxSize.ToString()!);
        if (!string.IsNullOrWhiteSpace(VideoBitRate)) Opt("-b", VideoBitRate!);
        if (MaxFps is > 0) Opt("--max-fps", MaxFps.ToString()!);

        if (NoAudio) Flag("--no-audio");
        else if (MicAsSource) Flag("--audio-source=mic");

        if (Fullscreen) Flag("-f");
        if (AlwaysOnTop) Flag("--always-on-top");
        if (Borderless) Flag("--window-borderless");
        if (!string.IsNullOrWhiteSpace(WindowTitle)) Opt("--window-title", $"\"{WindowTitle}\"");

        if (TurnScreenOff) Flag("-S");
        if (StayAwake) Flag("-w");
        if (ShowTouches) Flag("-t");
        if (NoControl) Flag("--no-control");
        if (PowerOffOnClose) Flag("--power-off-on-close");
        if (DisableScreensaver) Flag("--disable-screensaver");
        if (UhidKeyboard) Flag("--keyboard=uhid");

        if (DisplayId is > 0) Opt("--display-id", DisplayId.ToString()!);
        if (NewDisplay != null)
            a.Append(NewDisplay.Length == 0 ? "--new-display" : $"--new-display={NewDisplay}").Append(' ');

        if (Record && !string.IsNullOrWhiteSpace(RecordPath))
        {
            Opt("--record", $"\"{RecordPath}\"");
            if (NoPlaybackWhileRecording) Flag("--no-playback");
        }

        if (PrintFps) Flag("--print-fps");

        return a.ToString().Trim();
    }
}
