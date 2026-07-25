namespace ScrcpyDeck.Services;

/// <summary>Lifecycle states of a screen-mirroring session.</summary>
public enum MirrorSessionState
{
    /// <summary>Reserved and launching; the startup window has not elapsed yet.</summary>
    Starting,
    /// <summary>The engine survived the startup window and is mirroring.</summary>
    Running,
    /// <summary>A stop was requested; termination is in progress.</summary>
    Stopping,
    /// <summary>The engine exited (manually closed, stopped, or finished).</summary>
    Exited,
    /// <summary>The engine never started, or died during the startup window.</summary>
    Failed,
}

/// <summary>
/// Immutable snapshot of a session. This is what leaves the service: it carries no live
/// <see cref="System.Diagnostics.Process"/>, so a consumer can hold it after the process is
/// disposed without any risk of ObjectDisposedException.
/// </summary>
public sealed record MirrorSessionInfo(
    Guid Id,
    string Serial,
    MirrorSessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    int? ExitCode,
    string? FailureReason)
{
    /// <summary>True while the session occupies its device (Starting or Running).</summary>
    public bool IsActive => State is MirrorSessionState.Starting or MirrorSessionState.Running;

    /// <summary>True once the session can never run again.</summary>
    public bool IsTerminal => State is MirrorSessionState.Exited or MirrorSessionState.Failed;
}

/// <summary>Why a launch attempt did not produce a running session.</summary>
public enum MirrorLaunchStatus
{
    /// <summary>A session was created and is Starting or Running.</summary>
    Started,
    /// <summary>The device already has an active session.</summary>
    AlreadyActive,
    /// <summary>The serial failed Batch 1B validation.</summary>
    InvalidDevice,
    /// <summary>A required bundled engine file is missing.</summary>
    EngineMissing,
    /// <summary>The process could not be started at all.</summary>
    StartFailed,
    /// <summary>The engine exited with a non-zero code inside the startup window.</summary>
    FailedToStart,
    /// <summary>The application is shutting down; new sessions are refused.</summary>
    ShuttingDown,
    /// <summary>
    /// The bundled engine has not passed integrity verification (SEC-01); no session was
    /// created and no process was started.
    /// </summary>
    IntegrityBlocked,
}

/// <summary>Outcome of a launch attempt, with an Arabic message ready for a Toast.</summary>
public sealed record MirrorLaunchResult(
    MirrorLaunchStatus Status,
    MirrorSessionInfo? Session,
    string Message)
{
    public bool Started => Status == MirrorLaunchStatus.Started;
}
