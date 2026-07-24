using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>
/// Owns screen-mirroring sessions: at most one active session per Android device, with an
/// explicit state machine, exactly-once process disposal, and bounded startup-failure
/// detection.
/// <para>
/// Locking rule: <see cref="_gate"/> guards the registry only. Process termination and
/// change notifications always happen AFTER the lock is released, so a slow kill or a UI
/// dispatch can never block another device's session.
/// </para>
/// </summary>
public static class ScrcpyService
{
    /// <summary>Key used when a launch has no explicit serial (adb's implicit single device).</summary>
    internal const string DefaultDeviceKey = "(default)";

    /// <summary>
    /// How long a freshly started engine is watched for an early crash. The window is not a
    /// sleep: it completes as soon as the process exits, and only elapses in full when the
    /// engine is still alive.
    /// </summary>
    internal static TimeSpan StartupWindow { get; set; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Swappable for tests; production uses the real System.Diagnostics.Process.</summary>
    internal static IProcessLauncher Launcher { get; set; } = SystemProcessLauncher.Instance;

    /// <summary>
    /// Which mandatory engine files are missing. Swappable for tests, which run without the
    /// bundled engine in their output directory; production always uses the real check.
    /// </summary>
    internal static Func<IReadOnlyList<string>> EngineFileCheck { get; set; }
        = ToolLocator.MissingEngineFiles;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static bool _shuttingDown;

    /// <summary>Raised (UI thread when an Application exists) whenever session state changes.</summary>
    public static event Action? SessionsChanged;

    // ---------------- presets ----------------

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

    // ---------------- shutdown gate ----------------

    /// <summary>After this, every launch is refused. Set by <see cref="AppLifecycle"/>.</summary>
    internal static void BeginShutdown()
    {
        lock (_gate) _shuttingDown = true;
    }

    /// <summary>Test hook: restores the service to a clean, accepting state.</summary>
    internal static void ResetForTests()
    {
        List<Session> stale;
        lock (_gate)
        {
            stale = _sessions.Values.ToList();
            _sessions.Clear();
            _shuttingDown = false;
        }
        foreach (var s in stale) s.TerminateAndDispose();
        Launcher = SystemProcessLauncher.Instance;
        EngineFileCheck = ToolLocator.MissingEngineFiles;
        StartupWindow = TimeSpan.FromMilliseconds(1200);
    }

    // ---------------- queries ----------------

    /// <summary>Number of sessions currently Starting or Running.</summary>
    public static int ActiveCount
    {
        get { lock (_gate) return _sessions.Values.Count(s => s.Snapshot().IsActive); }
    }

    /// <summary>True when the given device already has a Starting or Running session.</summary>
    public static bool IsActive(string? serial)
    {
        var key = KeyFor(serial);
        lock (_gate)
            return _sessions.TryGetValue(key, out var s) && s.Snapshot().IsActive;
    }

    /// <summary>The current session for a device, or null.</summary>
    public static MirrorSessionInfo? GetSession(string? serial)
    {
        var key = KeyFor(serial);
        lock (_gate)
            return _sessions.TryGetValue(key, out var s) ? s.Snapshot() : null;
    }

    /// <summary>Read-only snapshot of every tracked session.</summary>
    public static IReadOnlyList<MirrorSessionInfo> Snapshot()
    {
        lock (_gate) return _sessions.Values.Select(s => s.Snapshot()).ToList();
    }

    private static string KeyFor(string? serial)
        => string.IsNullOrWhiteSpace(serial) ? DefaultDeviceKey : serial.Trim();

    // ---------------- launch ----------------

    /// <summary>
    /// Starts a mirroring session. Returns immediately with a Starting session; the startup
    /// window is observed in the background so the UI never blocks.
    /// </summary>
    public static MirrorLaunchResult Launch(ScrcpyOptions options)
    {
        // Default a branded window title so the mirror window never shows the engine name.
        if (string.IsNullOrWhiteSpace(options.WindowTitle))
            options.WindowTitle = AppState.CurrentDevice?.DisplayName ?? "PhoneLink Pro";

        // 1-2) the engine and its mandatory bundled files must be present.
        var missing = EngineFileCheck();
        if (missing.Count > 0)
        {
            Log.Error($"Engine files missing: {string.Join(", ", missing)}");
            return new MirrorLaunchResult(MirrorLaunchStatus.EngineMissing, null,
                "تعذّر بدء العرض: ملفات المحرّك غير مكتملة. أعد تثبيت البرنامج.");
        }

        // 3) validate the serial through the Batch 1B path (null = adb's implicit device).
        if (!string.IsNullOrWhiteSpace(options.Serial) &&
            !AdbInput.TryValidateSerial(options.Serial, out var serialError))
        {
            return new MirrorLaunchResult(MirrorLaunchStatus.InvalidDevice, null, serialError);
        }

        var key = KeyFor(options.Serial);
        Session session;

        // 4-5) reserve the device atomically, so a double-click cannot start twice.
        lock (_gate)
        {
            if (_shuttingDown)
                return new MirrorLaunchResult(MirrorLaunchStatus.ShuttingDown, null,
                    "جارٍ إغلاق البرنامج.");

            if (_sessions.TryGetValue(key, out var existing) && existing.Snapshot().IsActive)
                return new MirrorLaunchResult(MirrorLaunchStatus.AlreadyActive, existing.Snapshot(),
                    "يوجد بث نشط لهذا الجهاز بالفعل.");

            session = new Session(key);
            _sessions[key] = session;
        }

        NotifyChanged();

        // 6) launch through the structured ArgumentList API.
        var command = new ProcessCommand(
            ToolLocator.ScrcpyPath,
            options.BuildArgumentList(),
            Environment: EngineEnv(),
            RedactedDescription: $"engine start ({key})");

        try
        {
            session.Attach(Launcher.Start(command));
        }
        catch (Exception ex)
        {
            Log.Error($"Engine start failed for {key}", ex);
            session.MarkFailed("لم يبدأ المحرّك.");
            Remove(session);
            NotifyChanged();
            return new MirrorLaunchResult(MirrorLaunchStatus.StartFailed, session.Snapshot(),
                "تعذّر بدء عرض الشاشة. تأكد من توصيل الجهاز وإعادة المحاولة.");
        }

        // 7) observe the startup window in the background.
        _ = ObserveStartupAsync(session);

        return new MirrorLaunchResult(MirrorLaunchStatus.Started, session.Snapshot(),
            "جارٍ بدء البث...");
    }

    /// <summary>
    /// Waits for either an early exit or the startup window to elapse. Event-driven: the
    /// wait completes the moment the engine exits, so a crash is reported immediately.
    /// </summary>
    private static async Task ObserveStartupAsync(Session session)
    {
        var exitedEarly = await session.WaitForExitAsync(StartupWindow);

        if (!exitedEarly)
        {
            // Survived startup: Starting -> Running, then keep watching for the eventual
            // exit (the user closing the mirror window, or the engine ending on its own).
            if (session.MarkRunning()) NotifyChanged();

            await session.WaitForExitAsync();

            session.MarkExited(session.ExitCodeOrNull());
            Remove(session);
            session.TerminateAndDispose();
            NotifyChanged();
            return;
        }

        var code = session.ExitCodeOrNull();
        if (code is null or 0)
        {
            // A clean instant exit is an ordinary end-of-session, not a failure.
            session.MarkExited(code);
        }
        else
        {
            var diagnostics = Sanitize(session.Diagnostics);
            Log.Error($"Engine exited during startup (code {code}) for {session.Serial}. " +
                      $"Diagnostics: {diagnostics}");
            session.MarkFailed($"exit {code}");
            Toast.Show("تعذّر بدء عرض الشاشة لهذا الجهاز. تحقّق من الاتصال وأعد المحاولة.", ok: false);
        }

        Remove(session);
        session.TerminateAndDispose();
        NotifyChanged();
    }

    /// <summary>
    /// Strips machine-specific paths out of engine output before it reaches the log, so a
    /// support log never carries the user's profile directory.
    /// </summary>
    internal static string Sanitize(string diagnostics)
    {
        if (string.IsNullOrWhiteSpace(diagnostics)) return "(none)";

        var text = diagnostics;
        foreach (var (path, token) in new[]
                 {
                     (ToolLocator.BaseDir, "<app>"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>"),
                 })
        {
            if (!string.IsNullOrEmpty(path))
                text = text.Replace(path, token, StringComparison.OrdinalIgnoreCase);
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > SystemProcessHandle.MaxDiagnosticChars
            ? text[..SystemProcessHandle.MaxDiagnosticChars]
            : text;
    }

    // ---------------- stop ----------------

    /// <summary>Stops the session for one device. Safe to call repeatedly.</summary>
    public static void Stop(string? serial)
    {
        var key = KeyFor(serial);
        Session? session;
        lock (_gate) _sessions.TryGetValue(key, out session);
        if (session == null) return;

        session.MarkStopping();
        NotifyChanged();

        // Termination happens outside the lock.
        session.TerminateAndDispose();
        Remove(session);
        NotifyChanged();
    }

    /// <summary>Stop every running mirror session.</summary>
    public static void StopAll()
    {
        List<Session> all;
        lock (_gate) all = _sessions.Values.ToList();
        if (all.Count == 0) return;

        foreach (var s in all) s.MarkStopping();
        NotifyChanged();

        foreach (var s in all)
        {
            s.TerminateAndDispose();
            Remove(s);
        }
        NotifyChanged();
    }

    private static void Remove(Session session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Serial, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(session.Serial);
        }
    }

    private static void NotifyChanged()
    {
        var app = System.Windows.Application.Current;
        if (app == null) { SessionsChanged?.Invoke(); return; }   // headless (tests / self-test)
        if (app.Dispatcher.CheckAccess()) SessionsChanged?.Invoke();
        else app.Dispatcher.BeginInvoke(() => SessionsChanged?.Invoke());
    }

    // ================================================================
    //  session
    // ================================================================

    /// <summary>
    /// One device's session. All mutable state is guarded by its own lock; the process
    /// handle is disposed exactly once regardless of how many paths race to terminate it.
    /// </summary>
    private sealed class Session
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private IProcessHandle? _handle;
        private MirrorSessionState _state = MirrorSessionState.Starting;
        private DateTimeOffset? _exitedAt;
        private int? _exitCode;
        private string? _failure;
        private int _disposed;

        public Session(string serial)
        {
            Serial = serial;
            Id = Guid.NewGuid();
            StartedAt = DateTimeOffset.UtcNow;
        }

        public string Serial { get; }
        public Guid Id { get; }
        public DateTimeOffset StartedAt { get; }

        public string Diagnostics
        {
            get { lock (_sync) return _handle?.Diagnostics ?? ""; }
        }

        public MirrorSessionInfo Snapshot()
        {
            lock (_sync)
                return new MirrorSessionInfo(Id, Serial, _state, StartedAt, _exitedAt, _exitCode, _failure);
        }

        /// <summary>
        /// Binds the process and subscribes to its exit BEFORE checking HasExited, so a
        /// process that dies instantly can never be missed by the subscription race.
        /// </summary>
        public void Attach(IProcessHandle handle)
        {
            lock (_sync) _handle = handle;

            handle.Exited += (_, _) => OnProcessExited();
            handle.BeginWatching();

            // The process may have exited between Start and the subscription above.
            if (handle.HasExited) OnProcessExited();
        }

        private void OnProcessExited()
        {
            lock (_sync)
            {
                if (_state is MirrorSessionState.Exited or MirrorSessionState.Failed) return;
                _exitCode ??= _handle?.TryGetExitCode();
            }
            _exited.TrySetResult(true);
        }

        /// <summary>True when the process exited inside <paramref name="window"/>.</summary>
        public async Task<bool> WaitForExitAsync(TimeSpan window)
        {
            var completed = await Task.WhenAny(_exited.Task, Task.Delay(window));
            return completed == _exited.Task;
        }

        /// <summary>Completes when the process exits, however long that takes.</summary>
        public Task WaitForExitAsync() => _exited.Task;

        public int? ExitCodeOrNull()
        {
            lock (_sync) return _exitCode ??= _handle?.TryGetExitCode();
        }

        /// <summary>Starting → Running. Returns false when the session already moved on.</summary>
        public bool MarkRunning()
        {
            lock (_sync)
            {
                if (_state != MirrorSessionState.Starting) return false;
                _state = MirrorSessionState.Running;
                return true;
            }
        }

        public void MarkStopping()
        {
            lock (_sync)
            {
                if (_state is MirrorSessionState.Exited or MirrorSessionState.Failed) return;
                _state = MirrorSessionState.Stopping;
            }
        }

        public void MarkExited(int? code)
        {
            lock (_sync)
            {
                if (_state is MirrorSessionState.Exited or MirrorSessionState.Failed) return;
                _state = MirrorSessionState.Exited;
                _exitCode ??= code;
                _exitedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkFailed(string reason)
        {
            lock (_sync)
            {
                if (_state is MirrorSessionState.Exited or MirrorSessionState.Failed) return;
                _state = MirrorSessionState.Failed;
                _failure = reason;
                _exitedAt = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Kills the process tree if needed and disposes the handle exactly once. Tolerates
        /// an already-exited process, access denial, an invalid handle and repeated calls.
        /// </summary>
        public void TerminateAndDispose()
        {
            // Kill AND Dispose are claimed together by whichever caller arrives first, so a
            // stop racing the exit watcher can never touch an already-disposed handle.
            var owner = Interlocked.Exchange(ref _disposed, 1) == 0;

            IProcessHandle? handle;
            lock (_sync) handle = _handle;

            if (owner && handle != null)
            {
                handle.Kill(entireTree: true);
                var code = handle.TryGetExitCode();
                lock (_sync) _exitCode ??= code;
            }

            // State settling is idempotent and safe for every caller.
            lock (_sync)
            {
                if (_state is not (MirrorSessionState.Exited or MirrorSessionState.Failed))
                {
                    _state = MirrorSessionState.Exited;
                    _exitedAt = DateTimeOffset.UtcNow;
                }
            }

            _exited.TrySetResult(true);

            if (owner)
            {
                try { handle?.Dispose(); } catch { }
            }
        }
    }
}
