using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ScrcpyDeck.Services;

/// <summary>How an engine integrity verification ended.</summary>
public enum EngineIntegrityStatus
{
    /// <summary>Every expected file exists and matches its expected SHA-256.</summary>
    Valid,
    /// <summary>The embedded manifest is missing, empty, or not parseable as trusted data.</summary>
    MissingManifestData,
    /// <summary>An expected engine file does not exist.</summary>
    MissingFile,
    /// <summary>An engine file's SHA-256 differs from the manifest.</summary>
    HashMismatch,
    /// <summary>An engine file exists but could not be read (locked, access denied, ...).</summary>
    UnreadableFile,
    /// <summary>The manifest lists the same normalized path twice.</summary>
    UnexpectedDuplicatePath,
    /// <summary>The caller cancelled before verification finished. Never cached.</summary>
    VerificationCancelled,
    /// <summary>An unexpected error interrupted verification.</summary>
    InternalError,
}

/// <summary>
/// Immutable outcome of one verification run. <see cref="AffectedPath"/> is always the
/// manifest-relative path (safe for UI); expected/actual hashes are written to the log only
/// and are never carried here, so no code path can surface them to a user by accident.
/// </summary>
public sealed record EngineIntegrityResult(
    EngineIntegrityStatus Status,
    string? AffectedPath,
    string? Detail,
    int ExpectedFileCount,
    TimeSpan Duration)
{
    public bool IsValid => Status == EngineIntegrityStatus.Valid;
}

/// <summary>
/// Verifies the bundled engine files against the SHA-256 manifest embedded in this assembly
/// (SEC-01 replacement: no process scanning, no anti-debug checks, no FailFast — the ONLY
/// thing examined is our own engine directory, and the only consequence of a failure is that
/// engine binaries are not launched).
///
/// <para><b>Trust boundary.</b> The expected manifest rides inside the application assembly
/// as read-only trusted data, never as a loose file beside the (user-writable) engine
/// directory. An attacker who can replace BOTH the application assembly and the engine files
/// can of course bypass any local check — that gap is closed by Authenticode signing (a later
/// batch), not by this class, and nothing here claims tamper-proofness.</para>
///
/// <para><b>Concurrency.</b> Single-flight: concurrent callers share one in-flight run.
/// The first completed non-cancelled result is cached for the rest of the process — the app
/// verifies once before first engine/ADB use — until <see cref="RetryAsync"/> deliberately
/// clears it (repair/reinstall flow). No lock is ever held while a file is being read.</para>
/// </summary>
public static class EngineIntegrityVerifier
{
    /// <summary>LogicalName of the embedded manifest resource (see ScrcpyDeck.csproj).</summary>
    internal const string ResourceName = "PhoneLinkPro.engine.manifest";

    /// <summary>Base directory the manifest's engine/ paths are resolved under. Test seam.</summary>
    internal static Func<string> BaseDirProvider { get; set; } = () => AppContext.BaseDirectory;

    /// <summary>Source of the trusted manifest text. Test seam; production reads the resource.</summary>
    internal static Func<string?> ManifestTextProvider { get; set; } = ReadEmbeddedManifest;

    /// <summary>How many verification runs actually executed (single-flight proof for tests).</summary>
    internal static int RunCount => Volatile.Read(ref _runCount);

    private static readonly object _gate = new();
    private static Task<EngineIntegrityResult>? _inFlight;
    private static EngineIntegrityResult? _completed;
    private static string? _lastLoggedKey;
    private static int _runCount;

    /// <summary>True only after a completed run found every engine file intact.</summary>
    public static bool IsVerifiedValid
    {
        get { lock (_gate) return _completed?.IsValid == true; }
    }

    /// <summary>The last completed (non-cancelled) result, or null before the first run.</summary>
    public static EngineIntegrityResult? LastCompleted
    {
        get { lock (_gate) return _completed; }
    }

    /// <summary>
    /// Verifies the engine files, sharing one in-flight run between concurrent callers and
    /// returning the cached result on every later call. <paramref name="ct"/> lets a caller
    /// stop waiting (and lets the initiating caller abort the run itself); a cancelled run is
    /// reported as <see cref="EngineIntegrityStatus.VerificationCancelled"/> and never cached.
    /// </summary>
    public static async Task<EngineIntegrityResult> VerifyAsync(CancellationToken ct = default)
    {
        // An already-cancelled caller never starts (or joins) a run.
        if (ct.IsCancellationRequested)
            return new EngineIntegrityResult(
                EngineIntegrityStatus.VerificationCancelled, null, null, 0, TimeSpan.Zero);

        Task<EngineIntegrityResult> run;
        lock (_gate)
        {
            if (_completed is { } done) return done;
            run = _inFlight ??= RunVerificationAsync(ct);
        }

        try
        {
            return await run.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // This caller stopped waiting; the shared run itself continues for the others
            // unless it was the initiating caller's token that fired.
            return new EngineIntegrityResult(
                EngineIntegrityStatus.VerificationCancelled, null, null, 0, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Deliberate re-check after the user repaired or reinstalled files: clears the cached
    /// result and verifies again. Failures are therefore never cached "permanently" — every
    /// Retry re-reads the real files.
    /// </summary>
    public static Task<EngineIntegrityResult> RetryAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inFlight == null) _completed = null;
            _lastLoggedKey = null;   // a retry outcome is always worth one fresh log line
        }
        Log.Info("EngineIntegrity: retry requested");
        return VerifyAsync(ct);
    }

    /// <summary>Test hook: pristine state and production providers.</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _inFlight = null;
            _completed = null;
            _lastLoggedKey = null;
        }
        Volatile.Write(ref _runCount, 0);
        BaseDirProvider = () => AppContext.BaseDirectory;
        ManifestTextProvider = ReadEmbeddedManifest;
    }

    /// <summary>Reads the manifest embedded in this assembly; null when absent.</summary>
    internal static string? ReadEmbeddedManifest()
    {
        try
        {
            using var stream = typeof(EngineIntegrityVerifier).Assembly
                .GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    // ---------------- the run ----------------

    private static Task<EngineIntegrityResult> RunVerificationAsync(CancellationToken ct)
        => Task.Run(async () =>
        {
            EngineIntegrityResult result;
            var sw = Stopwatch.StartNew();
            try
            {
                Interlocked.Increment(ref _runCount);
                result = await VerifyAllFilesAsync(sw, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new EngineIntegrityResult(
                    EngineIntegrityStatus.VerificationCancelled, null, null, 0, sw.Elapsed);
            }
            catch (Exception ex)
            {
                result = new EngineIntegrityResult(
                    EngineIntegrityStatus.InternalError, null, ex.GetType().Name, 0, sw.Elapsed);
            }

            lock (_gate)
            {
                // A cancelled run proves nothing about the files: never cache it.
                if (result.Status != EngineIntegrityStatus.VerificationCancelled)
                    _completed = result;
                _inFlight = null;
            }

            LogResult(result);
            return result;
        });

    private static async Task<EngineIntegrityResult> VerifyAllFilesAsync(
        Stopwatch sw, CancellationToken ct)
    {
        var problem = EngineManifest.TryParse(ManifestTextProvider(), out var entries, out var offender);
        if (problem != EngineManifestProblem.None)
        {
            return problem == EngineManifestProblem.DuplicatePath
                ? Fail(EngineIntegrityStatus.UnexpectedDuplicatePath, offender, problem.ToString())
                : Fail(EngineIntegrityStatus.MissingManifestData, null, problem.ToString());
        }

        var baseDir = Path.GetFullPath(BaseDirProvider());
        var engineRoot = Path.GetFullPath(Path.Combine(baseDir, EngineManifest.EngineDirName))
                         + Path.DirectorySeparatorChar;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            // Containment: the parser already enforces canonical engine/ paths; this guard
            // re-proves it on the RESOLVED path so nothing outside engine\ is ever read.
            var fullPath = Path.GetFullPath(Path.Combine(
                baseDir, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(engineRoot, StringComparison.OrdinalIgnoreCase))
                return Fail(EngineIntegrityStatus.MissingManifestData, entry.Path,
                    "entry resolves outside the engine directory");

            if (!File.Exists(fullPath))
                return Fail(EngineIntegrityStatus.MissingFile, entry.Path, null);

            string actual;
            try
            {
                actual = await HashFileSha256Async(fullPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Fail(EngineIntegrityStatus.MissingFile, entry.Path, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException or NotSupportedException)
            {
                // Safe exception CATEGORY only — no OS message, no absolute path.
                return Fail(EngineIntegrityStatus.UnreadableFile, entry.Path, ex.GetType().Name);
            }

            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Hashes go to the diagnostic log ONLY, never onto the result.
                Log.Warn($"EngineIntegrity: hash mismatch for {entry.Path}: " +
                         $"expected {entry.Sha256}, actual {actual}");
                return Fail(EngineIntegrityStatus.HashMismatch, entry.Path, null);
            }
        }

        return new EngineIntegrityResult(
            EngineIntegrityStatus.Valid, null, null, entries.Count, sw.Elapsed);

        EngineIntegrityResult Fail(EngineIntegrityStatus status, string? path, string? detail)
            => new(status, path, detail, entries.Count, sw.Elapsed);
    }

    /// <summary>Streaming asynchronous SHA-256; large binaries are never loaded whole.</summary>
    private static async Task<string> HashFileSha256Async(string fullPath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 17, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    // ---------------- diagnostics (Part G) ----------------

    /// <summary>
    /// Logs one line per DISTINCT outcome (status + affected file), so a failure repeated by
    /// polls or clicks cannot flood the log. Hash values for mismatches are logged at the
    /// detection site; nothing here contains user paths, pairing codes or file contents.
    /// </summary>
    private static void LogResult(EngineIntegrityResult result)
    {
        var key = $"{result.Status}|{result.AffectedPath}";
        lock (_gate)
        {
            if (key == _lastLoggedKey) return;
            _lastLoggedKey = key;
        }

        var version = typeof(EngineIntegrityVerifier).Assembly.GetName().Version;
        var line = $"EngineIntegrity: {result.Status} " +
                   $"(app v{version}, manifest v{EngineManifest.Version}, " +
                   $"files={result.ExpectedFileCount}, {result.Duration.TotalMilliseconds:F0}ms)" +
                   (result.AffectedPath != null ? $" file={result.AffectedPath}" : "") +
                   (result.Detail != null ? $" detail={result.Detail}" : "");

        if (result.IsValid) Log.Info(line);
        else
        {
            Log.Warn(line);
            // Feed the support bundle's "recent failures" section (relative path only).
            AppDiagnostics.RecordFailure("EngineIntegrity", "integrity",
                $"{result.Status} {result.AffectedPath}".TrimEnd());
        }
    }
}
