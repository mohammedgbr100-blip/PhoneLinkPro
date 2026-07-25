using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// REL-02 (Batch 1F) — the installer maintenance command (--prepare-update): path-proven
/// owned ADB servers are stopped, foreign/ambiguous servers are never touched, an
/// unverified bridge is never executed, every branch has a distinct exit code, and the run
/// is time-bounded. All branches are driven through seams — no process is ever started.
///
/// In the global collection: MaintenanceMode's seams are process-wide.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class MaintenanceModeTests : IDisposable
{
    private static readonly EngineIntegrityResult Valid =
        new(EngineIntegrityStatus.Valid, null, null, 11, TimeSpan.Zero);

    private static readonly EngineIntegrityResult Tampered =
        new(EngineIntegrityStatus.HashMismatch, "engine/SDL2.dll", null, 11, TimeSpan.Zero);

    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public MaintenanceModeTests()
    {
        MaintenanceMode.ResetForTests();
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-maint-" + Guid.NewGuid().ToString("N"));
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        MaintenanceMode.ResetForTests();
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- argument gate

    [Fact] // a normal launch is untouched by maintenance logic
    public void TryRun_WithoutTheArgument_DoesNothing()
    {
        Assert.False(MaintenanceMode.TryRun(new[] { "" }, out _));
        Assert.False(MaintenanceMode.TryRun(Array.Empty<string>(), out _));
        Assert.False(MaintenanceMode.TryRun(null!, out _));
    }

    [Fact] // the installer's exact invocation is recognized (case-insensitively)
    public void TryRun_WithTheArgument_RunsAndReturnsAnExitCode()
    {
        var killCalls = 0;
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.NothingToStop;
        MaintenanceMode.KillOwnedServer = () => { killCalls++; return Task.CompletedTask; };

        Assert.True(MaintenanceMode.TryRun(new[] { "--PREPARE-UPDATE" }, out var exit));
        Assert.Equal(MaintenanceMode.ExitClean, exit);
        Assert.Equal(0, killCalls);
    }

    // ---------------------------------------------------------------- decision branches

    [Fact] // app not running, no server: nothing to do — success
    public async Task NothingToStop_ExitsClean()
    {
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.NothingToStop;
        Assert.Equal(MaintenanceMode.ExitClean, await MaintenanceMode.RunCoreAsync());
    }

    [Fact] // an owned server with valid integrity is stopped
    public async Task OwnedServer_WithValidIntegrity_IsStopped()
    {
        var killCalls = 0;
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.OwnedByUs;
        MaintenanceMode.IntegrityGate = _ => Task.FromResult(Valid);
        MaintenanceMode.KillOwnedServer = () => { killCalls++; return Task.CompletedTask; };

        Assert.Equal(MaintenanceMode.ExitClean, await MaintenanceMode.RunCoreAsync());
        Assert.Equal(1, killCalls);
    }

    [Fact] // Android Studio's server: never touched, honestly reported
    public async Task ForeignServer_IsLeftAloneWithDistinctExitCode()
    {
        var killCalls = 0;
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.NotOurs;
        MaintenanceMode.KillOwnedServer = () => { killCalls++; return Task.CompletedTask; };

        Assert.Equal(MaintenanceMode.ExitForeignAdb, await MaintenanceMode.RunCoreAsync());
        Assert.Equal(0, killCalls);
    }

    [Fact] // unprovable ownership: fail safe, touch nothing
    public async Task AmbiguousServer_IsLeftAloneWithDistinctExitCode()
    {
        var killCalls = 0;
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.Ambiguous;
        MaintenanceMode.KillOwnedServer = () => { killCalls++; return Task.CompletedTask; };

        Assert.Equal(MaintenanceMode.ExitAmbiguousAdb, await MaintenanceMode.RunCoreAsync());
        Assert.Equal(0, killCalls);
    }

    [Fact] // integrity invalid: the bridge is NEVER executed — distinct exit code
    public async Task OwnedServer_WithInvalidIntegrity_DoesNotExecuteTheBridge()
    {
        var killCalls = 0;
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.OwnedByUs;
        MaintenanceMode.IntegrityGate = _ => Task.FromResult(Tampered);
        MaintenanceMode.KillOwnedServer = () => { killCalls++; return Task.CompletedTask; };

        Assert.Equal(MaintenanceMode.ExitIntegrityInvalid, await MaintenanceMode.RunCoreAsync());
        Assert.Equal(0, killCalls);
    }

    // ---------------------------------------------------------------- bounds + robustness

    [Fact] // a wedged kill cannot hang the installer: the budget forces the error code
    public void HangingCleanup_IsTimeBoundedWithErrorCode()
    {
        MaintenanceMode.Budget = TimeSpan.FromMilliseconds(300);
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.OwnedByUs;
        MaintenanceMode.IntegrityGate = _ => Task.FromResult(Valid);
        MaintenanceMode.KillOwnedServer = () => Task.Delay(TimeSpan.FromSeconds(30));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(MaintenanceMode.TryRun(new[] { MaintenanceMode.Argument }, out var exit));
        sw.Stop();

        Assert.Equal(MaintenanceMode.ExitError, exit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact] // a throwing provider becomes the error exit code, never an unhandled crash
    public void ThrowingProvider_BecomesErrorExitCode()
    {
        MaintenanceMode.OwnershipProvider = () => throw new InvalidOperationException("boom");

        Assert.True(MaintenanceMode.TryRun(new[] { MaintenanceMode.Argument }, out var exit));
        Assert.Equal(MaintenanceMode.ExitError, exit);
    }

    [Fact] // every documented exit code is distinct (the installer branches on them)
    public void ExitCodes_AreDistinct()
    {
        var codes = new[]
        {
            MaintenanceMode.ExitClean, MaintenanceMode.ExitForeignAdb,
            MaintenanceMode.ExitAmbiguousAdb, MaintenanceMode.ExitIntegrityInvalid,
            MaintenanceMode.ExitError,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    [Fact] // the run is fully logged for the support bundle
    public async Task Maintenance_LogsItsDecisions()
    {
        MaintenanceMode.OwnershipProvider = () => AdbOwnershipDecision.NotOurs;
        await MaintenanceMode.RunCoreAsync();

        var logs = string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
        Assert.Contains("foreign ADB server", logs);
    }
}
