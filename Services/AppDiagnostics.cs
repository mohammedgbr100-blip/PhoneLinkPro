namespace ScrcpyDeck.Services;

/// <summary>One structured failure, kept in memory for the support bundle (LOG-02).</summary>
public sealed record FailureRecord(
    DateTimeOffset AtUtc,
    string Category,
    string CorrelationId,
    string Summary);

/// <summary>
/// Small ambient diagnostics state: what the app is currently doing (for exception context)
/// and a bounded ring of recent structured failures (for the support bundle). Everything
/// stored here is already sanitized — no serials, no user paths, no secrets.
/// </summary>
public static class AppDiagnostics
{
    private const int MaxFailures = 20;

    private static readonly object _gate = new();
    private static readonly Queue<FailureRecord> _failures = new();
    private static string? _currentOperation;

    /// <summary>Coarse current operation, e.g. "page:Dashboard". Null when unknown.</summary>
    public static string? CurrentOperation
    {
        get { lock (_gate) return _currentOperation; }
        set { lock (_gate) _currentOperation = value; }
    }

    /// <summary>Records one failure summary; the ring keeps only the most recent entries.</summary>
    public static void RecordFailure(string category, string correlationId, string summary)
    {
        lock (_gate)
        {
            _failures.Enqueue(new FailureRecord(
                DateTimeOffset.UtcNow, category, correlationId, summary));
            while (_failures.Count > MaxFailures) _failures.Dequeue();
        }
    }

    /// <summary>Snapshot of recent failures, oldest first.</summary>
    public static IReadOnlyList<FailureRecord> RecentFailures
    {
        get { lock (_gate) return _failures.ToList(); }
    }

    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _failures.Clear();
            _currentOperation = null;
        }
    }
}
