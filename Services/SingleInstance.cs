using System.Threading;

namespace ScrcpyDeck.Services;

/// <summary>What this process turned out to be when it tried to become THE instance.</summary>
public enum InstanceRole
{
    /// <summary>First instance: created the mutex and owns it.</summary>
    Primary,
    /// <summary>The previous owner crashed or leaked; ownership was recovered safely.</summary>
    PrimaryRecovered,
    /// <summary>Another live instance owns the mutex; this process must hand off and exit.</summary>
    Secondary,
    /// <summary>The mutex could not be used at all; the app runs rather than bricking (logged).</summary>
    PrimaryFailOpen,
}

/// <summary>
/// One PhoneLink Pro instance per Windows user session (P3-02).
///
/// <para><b>Mechanism.</b> A named mutex WITHOUT a <c>Global\</c> prefix, which Windows scopes
/// to the current logon session: exactly the desired "per user session" boundary, needs no
/// administrator rights, and is the very name Inno Setup's <c>AppMutex</c> checks so the
/// installer knows the app is running. A second launch signals the named
/// <see cref="ShowRequestEventName"/> event and exits; the primary listens and restores its
/// window — so the second click FEELS like it opened the app.</para>
///
/// <para><b>Crash recovery.</b> If the owner process dies, Windows destroys the mutex (fresh
/// acquire) or reports it abandoned (recovered acquire); both paths make the next launch the
/// primary. Every failure path fails OPEN: a broken mutex must never keep a user out of the
/// app.</para>
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// Session-scoped mutex name. Must stay in sync with <c>AppMutex</c> in
    /// installer\PhoneLinkPro.iss (a drift test guards this).
    /// </summary>
    public const string MutexName = "PhoneLinkPro.SingleInstance";

    /// <summary>Auto-reset event a secondary sets to ask the primary to show itself.</summary>
    public const string ShowRequestEventName = "PhoneLinkPro.ShowRequest";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static RegisteredWaitHandle? _showWait;

    /// <summary>True when startup may continue for the given role.</summary>
    public static bool ShouldProceed(InstanceRole role) => role != InstanceRole.Secondary;

    /// <summary>Tries to become the single instance. Never throws.</summary>
    public static InstanceRole Acquire(string? mutexName = null)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, mutexName ?? MutexName, out var createdNew);
            if (createdNew) return InstanceRole.Primary;

            // The name exists. Either a live instance owns it (WaitOne fails fast), the
            // previous owner released-but-leaked it (WaitOne acquires), or the owner
            // crashed while holding it (AbandonedMutexException = we now own it).
            try
            {
                return _mutex.WaitOne(TimeSpan.Zero)
                    ? InstanceRole.PrimaryRecovered
                    : InstanceRole.Secondary;
            }
            catch (AbandonedMutexException)
            {
                return InstanceRole.PrimaryRecovered;
            }
        }
        catch (Exception ex)
        {
            // ACL or kernel-object weirdness must never brick startup: run, but say so.
            Log.Warn($"SingleInstance: mutex unavailable ({ex.GetType().Name}); continuing without it.");
            return InstanceRole.PrimaryFailOpen;
        }
    }

    /// <summary>
    /// Secondary path: wake the primary instance's window. Deliberately writes nothing to
    /// the shared log — the primary logs the received request, so the log has exactly one
    /// writer (P3-02 requirement).
    /// </summary>
    public static void NotifyExistingInstance(string? eventName = null)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName ?? ShowRequestEventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch
        {
            // The primary may be mid-shutdown; there is nothing useful to do.
        }
    }

    /// <summary>
    /// Primary path: create the show-request event and invoke <paramref name="onShowRequested"/>
    /// every time a secondary launch asks for the window. The callback runs on a thread-pool
    /// thread — the caller dispatches to the UI thread.
    /// </summary>
    public static void StartShowRequestListener(Action onShowRequested, string? eventName = null)
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                eventName ?? ShowRequestEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, timedOut) => { if (!timedOut) onShowRequested(); },
                state: null,
                millisecondsTimeOutInterval: -1,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"SingleInstance: show-request listener unavailable ({ex.GetType().Name}).");
        }
    }

    /// <summary>Releases the mutex and the listener. Part of orderly shutdown; never throws.</summary>
    public static void Release()
    {
        try { _showWait?.Unregister(null); } catch { }
        _showWait = null;

        try { _showEvent?.Dispose(); } catch { }
        _showEvent = null;

        var mutex = _mutex;
        _mutex = null;
        if (mutex == null) return;
        try { mutex.ReleaseMutex(); } catch { /* not owned (Secondary/FailOpen): fine */ }
        try { mutex.Dispose(); } catch { }
    }
}
