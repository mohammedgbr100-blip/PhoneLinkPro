using System.Runtime.InteropServices;

namespace ScrcpyDeck.Services;

/// <summary>
/// Builds the small, privacy-safe text a user may copy from About to hand to support (Batch 4F
/// §4/§6). Every field is an explicit, allowlisted read — never a serialized settings object, a
/// log excerpt, or device/network state. The result is bounded and newline-normalized before it
/// ever reaches the clipboard.
///
/// <para><b>Exact output contract (repair batch §7).</b> <see cref="Build"/> always returns
/// exactly <see cref="LineCount"/> CRLF-separated lines: one fixed header, then one line per
/// entry in <see cref="FieldLabels"/>, in that exact order, each shaped
/// <c>"{label}: {value}"</c>. No field is ever conditionally omitted (a missing/unavailable
/// value renders as <c>"-"</c> instead of disappearing), so the line count and label set can
/// never silently grow, shrink, or reorder without a test catching it.</para>
/// </summary>
public static class SupportSummary
{
    /// <summary>Hard ceiling on the copied text; nothing this method builds should ever approach
    /// it, but the bound is enforced regardless of how the fields above grow.</summary>
    internal const int MaxLength = 2000;

    /// <summary>The fixed, non-field header line — always line 1, never treated as a labelled
    /// field (it carries no "label: value" shape).</summary>
    internal const string Header = "PhoneLink Pro — ملخص دعم آمن";

    /// <summary>
    /// The exact, closed, ordered set of field labels <see cref="Build"/> may ever emit — one
    /// line per label, in this order, nothing added and nothing missing. This IS the allowlist:
    /// a future field must be added here deliberately, which is exactly what the repair-batch
    /// exact-contract test guards against happening by accident.
    /// </summary>
    internal static readonly IReadOnlyList<string> FieldLabels = new[]
    {
        "إصدار التطبيق",
        EngineManifest.FormatVersionLabelArabic,
        "المعمارية",
        "حالة أدوات المحرّك",
        "حالة سلامة الملفات",
        "نظام التشغيل",
    };

    internal static Func<EngineIntegrityResult?> IntegrityProvider { get; set; } =
        () => EngineIntegrityVerifier.LastCompleted;
    internal static Func<IReadOnlyList<string>> MissingEngineFilesProvider { get; set; } =
        ToolLocator.MissingEngineFiles;
    internal static Func<string> OsDescriptionProvider { get; set; } =
        () => RuntimeInformation.OSDescription;

    internal static void ResetForTests()
    {
        IntegrityProvider = () => EngineIntegrityVerifier.LastCompleted;
        MissingEngineFilesProvider = ToolLocator.MissingEngineFiles;
        OsDescriptionProvider = () => RuntimeInformation.OSDescription;
    }

    /// <summary>Truthful, human-readable state label for the engine tools row.</summary>
    public static string ToolsStateLabel(IReadOnlyList<string> missing) =>
        missing.Count == 0 ? "متاحة" : $"غير مكتملة ({missing.Count})";

    /// <summary>Truthful, human-readable state label for the integrity row.</summary>
    public static string IntegrityStateLabel(EngineIntegrityResult? result) =>
        result == null ? "لم يتم التحقق بعد"
        : result.IsValid ? "تم التحقق"
        : "فشل التحقق";

    /// <summary>
    /// Builds the summary from the explicit allowlist above only: app version, the truthful
    /// engine MANIFEST-FORMAT version (never labelled as scrcpy's version or an "engine
    /// package"/"release" version — see <see cref="EngineManifest.FormatVersionLabelArabic"/>),
    /// process architecture, tools/integrity state, and OS description (already an established
    /// safe field — see <see cref="SupportBundle"/>). Deliberately excludes anything that could
    /// identify the user, their device, or their files: no username, no profile/settings/log
    /// path, no device serial/model, no IP address, no transfer path, no pairing code, no adb
    /// output, no stack trace, no uptime, no environment dump.
    /// </summary>
    public static string Build()
    {
        var missing = SafeGet(MissingEngineFilesProvider, (IReadOnlyList<string>)Array.Empty<string>());
        var integrity = SafeGet(IntegrityProvider, (EngineIntegrityResult?)null);
        var os = SafeGet(OsDescriptionProvider, "");

        var values = new[]
        {
            AppVersionInfo.ProductVersion,
            "v" + EngineManifest.Version,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ToolsStateLabel(missing),
            IntegrityStateLabel(integrity),
            string.IsNullOrWhiteSpace(os) ? "-" : os,
        };

        var lines = new List<string> { Header };
        for (var i = 0; i < FieldLabels.Count; i++)
            lines.Add($"{FieldLabels[i]}: {values[i]}");

        var text = string.Join("\r\n", lines);
        return Bound(NormalizeLineEndings(text));
    }

    private static string NormalizeLineEndings(string s)
        => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private static string Bound(string s) => s.Length <= MaxLength ? s : s[..MaxLength];

    private static T SafeGet<T>(Func<T> provider, T fallback)
    {
        try { return provider(); } catch { return fallback; }
    }
}
