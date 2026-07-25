namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden installer maintenance command: <c>PhoneLinkPro.exe --prepare-update</c> (REL-02).
///
/// <para>Run by the installer before upgrade/uninstall, AFTER Inno's AppMutex prompt has
/// made the user close the app (so mirror sessions already ended via the app's own orderly
/// shutdown). Its single job is the leftover bundled ADB server: stop it — but ONLY when the
/// Batch 1C ownership policy proves by full image path that the server is our bundled
/// bridge. Foreign or ambiguous servers (Android Studio etc.) are never touched, and an
/// unverified bridge binary is never executed (the Batch 1E integrity gate enforces this
/// inside AdbService).</para>
///
/// <para>No window is ever shown; the whole run is time-bounded; the exit code tells the
/// installer exactly what happened.</para>
/// </summary>
internal static class MaintenanceMode
{
    internal const string Argument = "--prepare-update";

    // Exit codes (documented in installer\PhoneLinkPro.iss and Security\INTEGRITY.md):
    /// <summary>Nothing needed, or the owned server was stopped. Safe to proceed.</summary>
    internal const int ExitClean = 0;
    /// <summary>A FOREIGN adb server is running; it was left untouched (it does not lock our files).</summary>
    internal const int ExitForeignAdb = 10;
    /// <summary>Server ownership could not be proven; nothing was touched.</summary>
    internal const int ExitAmbiguousAdb = 11;
    /// <summary>Engine integrity is invalid: the bridge was NOT executed. Files are not locked by us.</summary>
    internal const int ExitIntegrityInvalid = 12;
    /// <summary>Timeout or unexpected error; nothing further was attempted.</summary>
    internal const int ExitError = 13;

    /// <summary>Whole-run ceiling.</summary>
    internal static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(8);

    // Seams so every branch is testable without processes, adb or engine files.
    internal static Func<AdbOwnershipDecision> OwnershipProvider { get; set; }
        = AdbServerOwnership.Evaluate;
    internal static Func<CancellationToken, Task<EngineIntegrityResult>> IntegrityGate { get; set; }
        = EngineIntegrityVerifier.VerifyAsync;
    internal static Func<Task> KillOwnedServer { get; set; }
        = () => AdbService.KillServerAsync();

    internal static void ResetForTests()
    {
        Budget = TimeSpan.FromSeconds(8);
        OwnershipProvider = AdbServerOwnership.Evaluate;
        IntegrityGate = EngineIntegrityVerifier.VerifyAsync;
        KillOwnedServer = () => AdbService.KillServerAsync();
    }

    /// <summary>
    /// Runs maintenance when <paramref name="args"/> request it. Returns true when the app
    /// must exit with <paramref name="exitCode"/> instead of starting the UI.
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = ExitClean;
        if (args == null || !args.Contains(Argument, StringComparer.OrdinalIgnoreCase))
            return false;

        try
        {
            var work = RunCoreAsync();
            exitCode = work.Wait(Budget) ? work.Result : ExitError;
        }
        catch (Exception ex)
        {
            Log.Warn($"Maintenance: failed ({ex.GetType().Name}).");
            exitCode = ExitError;
        }

        Log.Info($"Maintenance: {Argument} finished with exit code {exitCode}.");
        return true;
    }

    /// <summary>The decision core, isolated for deterministic tests.</summary>
    internal static async Task<int> RunCoreAsync()
    {
        var decision = OwnershipProvider();
        switch (decision)
        {
            case AdbOwnershipDecision.NothingToStop:
                return ExitClean;

            case AdbOwnershipDecision.NotOurs:
                Log.Info("Maintenance: a foreign ADB server is running; leaving it untouched.");
                return ExitForeignAdb;

            case AdbOwnershipDecision.Ambiguous:
                Log.Warn("Maintenance: ADB server ownership is ambiguous; leaving it untouched.");
                return ExitAmbiguousAdb;
        }

        // OwnedByUs: stopping it means executing our bridge, which requires the Batch 1E
        // integrity verification to pass first. AdbService enforces the same gate; checking
        // here gives the installer a precise, distinguishable exit code.
        using var cts = new CancellationTokenSource(Budget);
        var integrity = await IntegrityGate(cts.Token).ConfigureAwait(false);
        if (!integrity.IsValid)
        {
            Log.Warn($"Maintenance: engine integrity {integrity.Status}; bridge not executed.");
            return ExitIntegrityInvalid;
        }

        await KillOwnedServer().ConfigureAwait(false);
        Log.Info("Maintenance: owned bundled ADB server stopped.");
        return ExitClean;
    }
}
