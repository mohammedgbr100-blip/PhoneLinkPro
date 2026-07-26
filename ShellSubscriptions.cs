namespace ScrcpyDeck;

/// <summary>
/// Symmetrical lifetime for the long-lived events the shell listens to.
///
/// <para>
/// The services the shell observes (device watcher, session service, app state, toasts) all
/// expose STATIC events, so every handler roots its window for the life of the process. One
/// place that pairs each subscribe with its unsubscribe — rather than scattered <c>+=</c>
/// calls and a hopeful comment — makes "subscribe once, release on close" checkable instead
/// of assumed. The services themselves are untouched.
/// </para>
/// </summary>
internal sealed class ShellSubscriptions : IDisposable
{
    private readonly List<Action> _detach = new();
    private bool _disposed;

    /// <summary>Live subscriptions held by this window; 0 once disposed.</summary>
    internal int Count => _detach.Count;

    /// <summary>True once released, so a second Dispose cannot double-unsubscribe.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Subscribes now and records how to undo it. Attaching after disposal is refused, so a
    /// late Loaded cannot resurrect a closed window's handlers.
    /// </summary>
    internal void Add(Action attach, Action detach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(detach);
        if (_disposed) return;

        attach();
        _detach.Add(detach);
    }

    /// <summary>Releases every subscription, newest first. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (var i = _detach.Count - 1; i >= 0; i--)
        {
            // One failing detach must never strand the others.
            try { _detach[i](); } catch { }
        }
        _detach.Clear();
    }

    /// <summary>
    /// Pure guard for shell event handlers: may this callback still touch the visual tree?
    /// A static event can fire between a window's Closing and its Closed, and again while
    /// the application shuts down, so handlers check this before dereferencing any element.
    /// </summary>
    internal static bool CanDispatch(
        bool windowClosed, bool applicationShuttingDown, bool dispatcherShutdownStarted)
        => !windowClosed && !applicationShuttingDown && !dispatcherShutdownStarted;
}
