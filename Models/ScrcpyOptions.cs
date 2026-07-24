using System.Globalization;

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

    /// <summary>
    /// Build the scrcpy arguments as separate argv elements.
    /// <para>
    /// Every user-controlled value (serial, window title, recording path) is emitted as
    /// exactly ONE element and handed to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// unmodified, so quotes, spaces, ampersands, semicolons, percent signs, parentheses,
    /// Arabic and any other Unicode stay literal data and can never become extra options.
    /// No quoting is performed here — the runtime does the Windows encoding.
    /// </para>
    /// The option order is identical to the previous string builder.
    /// </summary>
    public IReadOnlyList<string> BuildArgumentList()
    {
        var a = new List<string>();
        void Flag(string f) => a.Add(f);
        void Opt(string f, string v) { a.Add(f); a.Add(v); }

        if (!string.IsNullOrWhiteSpace(Serial)) Opt("-s", Serial!);

        if (MaxSize is > 0) Opt("-m", MaxSize.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(VideoBitRate)) Opt("-b", VideoBitRate!);
        if (MaxFps is > 0) Opt("--max-fps", MaxFps.Value.ToString(CultureInfo.InvariantCulture));

        if (NoAudio) Flag("--no-audio");
        else if (MicAsSource) Flag("--audio-source=mic");

        if (Fullscreen) Flag("-f");
        if (AlwaysOnTop) Flag("--always-on-top");
        if (Borderless) Flag("--window-borderless");
        if (!string.IsNullOrWhiteSpace(WindowTitle)) Opt("--window-title", WindowTitle!);

        if (TurnScreenOff) Flag("-S");
        if (StayAwake) Flag("-w");
        if (ShowTouches) Flag("-t");
        if (NoControl) Flag("--no-control");
        if (PowerOffOnClose) Flag("--power-off-on-close");
        if (DisableScreensaver) Flag("--disable-screensaver");
        if (UhidKeyboard) Flag("--keyboard=uhid");

        if (DisplayId is > 0) Opt("--display-id", DisplayId.Value.ToString(CultureInfo.InvariantCulture));
        if (NewDisplay != null)
            a.Add(NewDisplay.Length == 0 ? "--new-display" : $"--new-display={NewDisplay}");

        if (Record && !string.IsNullOrWhiteSpace(RecordPath))
        {
            Opt("--record", RecordPath!);
            if (NoPlaybackWhileRecording) Flag("--no-playback");
        }

        if (PrintFps) Flag("--print-fps");

        return a;
    }
}
