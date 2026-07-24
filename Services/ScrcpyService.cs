using System.Diagnostics;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>Launches mirroring sessions from option sets or presets (engine identity hidden via env vars).</summary>
public static class ScrcpyService
{
    /// <summary>Quality presets exposed as one-click buttons.</summary>
    public static ScrcpyOptions Preset(string name, string? serial) => name switch
    {
        "low" => new ScrcpyOptions { Serial = serial, MaxSize = 1024, VideoBitRate = "4M", MaxFps = 30 },
        "medium" => new ScrcpyOptions { Serial = serial, MaxSize = 1280, VideoBitRate = "8M", MaxFps = 60 },
        "high" => new ScrcpyOptions { Serial = serial, MaxSize = 1920, VideoBitRate = "16M", MaxFps = 60 },
        "ultra" => new ScrcpyOptions { Serial = serial, MaxSize = 2560, VideoBitRate = "32M", MaxFps = 60 },
        _ => new ScrcpyOptions { Serial = serial }
    };

    /// <summary>Env vars that point the engine at our renamed bridge + server and brand the
    /// mirror window (icon), keeping the underlying engine identity hidden.</summary>
    private static Dictionary<string, string> EngineEnv()
    {
        var env = new Dictionary<string, string>
        {
            ["ADB"] = ToolLocator.AdbPath,
            ["SCRCPY_SERVER_PATH"] = ToolLocator.ServerPath,
        };
        var icon = System.IO.Path.Combine(ToolLocator.BaseDir, "Assets", "app.png");
        if (System.IO.File.Exists(icon)) env["SCRCPY_ICON_PATH"] = icon;
        return env;
    }

    public static Process? Launch(ScrcpyOptions options)
    {
        // Default a branded window title so the mirror window never shows the engine name.
        if (string.IsNullOrWhiteSpace(options.WindowTitle))
            options.WindowTitle = AppState.CurrentDevice?.DisplayName ?? "PhoneLink Pro";
        var p = ProcessRunner.Launch(ToolLocator.ScrcpyPath, options.BuildArgumentList(), EngineEnv());
        Track(p, options.Serial);
        return p;
    }

    // ---------------- session tracking ----------------

    private static readonly List<Process> _sessions = new();

    /// <summary>Raised (UI thread) whenever a mirror session starts or ends.</summary>
    public static event Action? SessionsChanged;

    public static int ActiveCount
    {
        get { lock (_sessions) { Prune(); return _sessions.Count; } }
    }

    private static void Track(Process? p, string? serial)
    {
        if (p == null) return;
        lock (_sessions) { Prune(); _sessions.Add(p); }
        Log.Info($"Mirror session started (pid {p.Id}, device {serial ?? "default"})");
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                lock (_sessions) _sessions.Remove(p);
                Log.Info($"Mirror session ended (pid {p.Id})");
                RaiseSessionsChanged();
            };
        }
        catch { }
        RaiseSessionsChanged();
    }

    /// <summary>Stop every running mirror session (kills the engine process trees).</summary>
    public static void StopAll()
    {
        List<Process> copy;
        lock (_sessions) { copy = new List<Process>(_sessions); _sessions.Clear(); }
        foreach (var p in copy)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Warn("StopAll: " + ex.Message); }
        }
        RaiseSessionsChanged();
    }

    private static void Prune()
    {
        _sessions.RemoveAll(p => { try { return p.HasExited; } catch { return true; } });
    }

    private static void RaiseSessionsChanged()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) SessionsChanged?.Invoke();
        else app.Dispatcher.BeginInvoke(() => SessionsChanged?.Invoke());
    }
}
