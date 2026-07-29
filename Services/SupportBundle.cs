using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>Outcome of a support-bundle export. Never thrown — always returned.</summary>
public sealed record SupportBundleResult(bool Success, string? ZipPath, string? FailureCategory);

/// <summary>
/// Safe support-bundle export (LOG-02): one ZIP a user can hand to support without leaking
/// anything private.
///
/// <para><b>Included:</b> a human-readable and a machine-readable summary (versions, OS,
/// runtime, engine component names/file-versions, integrity STATUS, masked device state,
/// non-sensitive settings flags, recent structured failure summaries) plus recent logs,
/// sanitized line-by-line at export time — so even historical lines written before redaction
/// existed leave the machine masked.</para>
///
/// <para><b>Never included:</b> pairing codes, full serials, user or phone files,
/// screenshots, recordings, APKs, keys/certificates, engine binaries, or engine hash values
/// (the summary carries the integrity STATUS only; local logs keep just a shortened hash
/// prefix, because PRIV-01 sanitizes every entry before it is written).</para>
/// </summary>
public static class SupportBundle
{
    /// <summary>Total budget for the logs/ section of the bundle.</summary>
    internal static long MaxTotalLogBytes { get; set; } = 3 * 1024 * 1024;

    // Provider seams: tests exercise the whole exporter against temp data only.
    internal static Func<string> LogDirProvider { get; set; } = () => Log.Dir;
    internal static Func<string> EngineDirProvider { get; set; } =
        () => Path.Combine(AppContext.BaseDirectory, EngineManifest.EngineDirName);
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal static Func<IReadOnlyList<AdbDevice>> DevicesProvider { get; set; } =
        () => DeviceWatcher.LastConfirmed;
    internal static Func<EngineIntegrityResult?> IntegrityProvider { get; set; } =
        () => EngineIntegrityVerifier.LastCompleted;
    internal static Func<string?> ManifestProvider { get; set; } =
        EngineIntegrityVerifier.ReadEmbeddedManifest;
    internal static Func<(bool ConsentAccepted, bool OnboardingSeen)> SettingsProvider { get; set; } =
        () => (AppSettings.ConsentAccepted, AppSettings.OnboardingSeen);

    /// <summary>Fixed entry timestamp: bundle structure is deterministic for equal inputs.</summary>
    private static readonly DateTimeOffset FixedEntryTime =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static void ResetForTests()
    {
        MaxTotalLogBytes = 3 * 1024 * 1024;
        LogDirProvider = () => Log.Dir;
        EngineDirProvider = () => Path.Combine(AppContext.BaseDirectory, EngineManifest.EngineDirName);
        Clock = () => DateTimeOffset.UtcNow;
        DevicesProvider = () => DeviceWatcher.LastConfirmed;
        IntegrityProvider = () => EngineIntegrityVerifier.LastCompleted;
        ManifestProvider = EngineIntegrityVerifier.ReadEmbeddedManifest;
        SettingsProvider = () => (AppSettings.ConsentAccepted, AppSettings.OnboardingSeen);
    }

    /// <summary>Exports the bundle to <paramref name="zipPath"/>. Never throws.</summary>
    public static SupportBundleResult Export(string zipPath)
    {
        try
        {
            var model = Collect();

            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddText(zip, "diagnostics-summary.txt", BuildSummaryText(model));
                AddText(zip, "diagnostics-summary.json", BuildSummaryJson(model));
                AddLogs(zip);
            }

            Log.Info("SupportBundle: exported successfully.");
            return new SupportBundleResult(true, zipPath, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"SupportBundle: export failed ({ex.GetType().Name}).");
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return new SupportBundleResult(false, null, ex.GetType().Name);
        }
    }

    // ---------------- data collection ----------------

    internal sealed record EngineComponent(string Name, string FileVersion);

    internal sealed record BundleModel(
        string ProductVersion,
        string GeneratedUtc,
        string Os,
        string Runtime,
        string Architecture,
        int EngineManifestVersion,
        int ExpectedEngineFiles,
        string IntegrityStatus,
        string? IntegrityAffectedFile,
        IReadOnlyList<EngineComponent> EngineComponents,
        IReadOnlyList<string> Devices,
        bool ConsentAccepted,
        bool OnboardingSeen,
        IReadOnlyList<string> RecentFailures);

    private static BundleModel Collect()
    {
        var integrity = SafeGet(IntegrityProvider, (EngineIntegrityResult?)null);
        var settings = SafeGet(SettingsProvider, (false, false));

        EngineManifest.TryParse(SafeGet(ManifestProvider, (string?)null), out var entries, out _);

        return new BundleModel(
            ProductVersion: AppVersionInfo.ProductVersion,
            GeneratedUtc: Clock().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            Os: RuntimeInformation.OSDescription,
            Runtime: RuntimeInformation.FrameworkDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            EngineManifestVersion: EngineManifest.Version,
            ExpectedEngineFiles: entries.Count,
            IntegrityStatus: integrity?.Status.ToString() ?? "NotVerifiedYet",
            IntegrityAffectedFile: integrity?.AffectedPath,
            EngineComponents: CollectEngineComponents(entries),
            Devices: SafeGet(DevicesProvider, (IReadOnlyList<AdbDevice>)Array.Empty<AdbDevice>())
                .Select(d => $"{LogSanitizer.MaskSerial(d.Serial)} state={d.State}")
                .ToList(),
            ConsentAccepted: settings.Item1,
            OnboardingSeen: settings.Item2,
            RecentFailures: AppDiagnostics.RecentFailures
                .Select(f => $"{f.AtUtc:HH:mm:ss'Z'} [{f.CorrelationId}] {f.Category}: {f.Summary}")
                .ToList());
    }

    /// <summary>Expected component NAMES (from the manifest) + their installed file versions.
    /// Deliberately no hash values here (LOG-02 privacy rule).</summary>
    private static List<EngineComponent> CollectEngineComponents(
        IReadOnlyList<EngineManifestEntry> entries)
    {
        var engineDir = SafeGet(EngineDirProvider, "");
        var components = new List<EngineComponent>();
        foreach (var entry in entries)
        {
            var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
            var version = "-";
            try
            {
                var full = Path.Combine(engineDir, name);
                if (engineDir.Length > 0 && File.Exists(full))
                    version = System.Diagnostics.FileVersionInfo.GetVersionInfo(full).FileVersion ?? "-";
            }
            catch { }
            components.Add(new EngineComponent(name, version));
        }
        return components;
    }

    private static T SafeGet<T>(Func<T> provider, T fallback)
    {
        try { return provider(); } catch { return fallback; }
    }

    // ---------------- rendering ----------------

    internal static string BuildSummaryText(BundleModel m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PhoneLink Pro — diagnostics summary");
        sb.AppendLine($"Generated (UTC):     {m.GeneratedUtc}");
        sb.AppendLine($"App version:         {m.ProductVersion}");
        sb.AppendLine($"Windows:             {m.Os}");
        sb.AppendLine($"Runtime:             {m.Runtime}");
        sb.AppendLine($"Architecture:        {m.Architecture}");
        sb.AppendLine($"Engine manifest:     v{m.EngineManifestVersion} ({m.ExpectedEngineFiles} expected files)");
        sb.AppendLine($"Engine integrity:    {m.IntegrityStatus}" +
                      (m.IntegrityAffectedFile != null ? $" ({m.IntegrityAffectedFile})" : ""));
        sb.AppendLine($"Consent accepted:    {m.ConsentAccepted}");
        sb.AppendLine($"Onboarding seen:     {m.OnboardingSeen}");

        sb.AppendLine();
        sb.AppendLine("Engine components (name, file version):");
        foreach (var c in m.EngineComponents) sb.AppendLine($"  {c.Name}  {c.FileVersion}");

        sb.AppendLine();
        sb.AppendLine("Devices (masked serials):");
        if (m.Devices.Count == 0) sb.AppendLine("  (none)");
        foreach (var d in m.Devices) sb.AppendLine("  " + d);

        sb.AppendLine();
        sb.AppendLine("Recent failures:");
        if (m.RecentFailures.Count == 0) sb.AppendLine("  (none)");
        foreach (var f in m.RecentFailures) sb.AppendLine("  " + f);

        return sb.ToString();
    }

    internal static string BuildSummaryJson(BundleModel m)
        => JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true });

    // ---------------- log section ----------------

    private static void AddLogs(ZipArchive zip)
    {
        var dir = SafeGet(LogDirProvider, "");
        if (dir.Length == 0 || !Directory.Exists(dir)) return;

        var remaining = MaxTotalLogBytes;

        // Newest first (the date is embedded in the name), but archive entries are added in
        // deterministic name order below.
        var picked = new List<(string Name, string Content)>();
        foreach (var file in Directory.GetFiles(dir, "*.log")
                     .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            if (remaining <= 0) break;
            var name = Path.GetFileName(file);
            try
            {
                var (content, truncated) = ReadTail(file, remaining);
                remaining -= Encoding.UTF8.GetByteCount(content);

                var sanitized = SanitizeLines(content);
                if (truncated)
                    sanitized = "# [truncated by support-bundle size limit]\r\n" + sanitized;
                picked.Add((name, sanitized));
            }
            catch (Exception ex)
            {
                // Locked or unreadable log: note it instead of failing the whole export.
                picked.Add((name + ".unavailable.txt",
                    $"log file could not be read: {ex.GetType().Name}"));
            }
        }

        foreach (var (name, content) in picked.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            AddText(zip, "logs/" + name, content);
    }

    /// <summary>Reads at most <paramref name="budget"/> bytes from the file's tail.</summary>
    private static (string Content, bool Truncated) ReadTail(string file, long budget)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= budget)
        {
            using var whole = new StreamReader(fs, Encoding.UTF8);
            return (whole.ReadToEnd(), false);
        }

        fs.Seek(-budget, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var text = reader.ReadToEnd();
        var firstBreak = text.IndexOf('\n');   // drop the partial first line
        return (firstBreak >= 0 ? text[(firstBreak + 1)..] : text, true);
    }

    private static string SanitizeLines(string content)
    {
        var sb = new StringBuilder(content.Length);
        foreach (var line in content.Split('\n'))
            sb.Append(LogSanitizer.Sanitize(line.TrimEnd('\r'))).Append("\r\n");
        return sb.ToString();
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedEntryTime;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
