using System.Diagnostics;

namespace ScrcpyDeck.Services;

/// <summary>The closed set of destinations the About/Support page may ever open.</summary>
public enum SafeLinkDestination
{
    Email,
    Telegram,
    Instagram,
    TikTok,
}

/// <summary>
/// Truthful outcome of one <see cref="SafeLinkLauncher.TryOpen"/> call (repair batch §8).
/// <see cref="Busy"/> means no external action happened at all — a launch was already in
/// flight — and must never be reported to the user as success.
/// </summary>
public enum SafeLinkOutcome
{
    /// <summary>The OS shell was asked to open the destination and accepted the request.</summary>
    Launched,
    /// <summary>A duplicate activation while another launch was already in flight; nothing was
    /// started this call.</summary>
    Busy,
    /// <summary>Defense-in-depth: the allowlisted entry's scheme was not https/mailto. Unreachable
    /// for real allowlist entries — <see cref="SafeLinkLauncher"/>'s static constructor already
    /// validates every entry at load time.</summary>
    Rejected,
    /// <summary>The OS shell was asked to open the destination and the attempt failed.</summary>
    Failed,
}

/// <summary>
/// Fixed-link launcher (Batch 4F §5): every destination this app can open comes from a closed,
/// compiled-in allowlist keyed by <see cref="SafeLinkDestination"/> — never from a text box, a
/// visible URL string, or any other caller-supplied value. Only <c>https</c> and <c>mailto</c>
/// are ever accepted, validated with <see cref="Uri.TryCreate(string,UriKind,out Uri)"/>, and the
/// destination is opened through the OS shell association (<c>UseShellExecute = true</c>) —
/// never a command interpreter or a string-built command line.
///
/// <para><b>Test seam placement (repair batch §3).</b> <see cref="ProcessStartOverrideForTests"/>
/// sits at the FINAL process boundary: production always builds the real
/// <see cref="ProcessStartInfo"/> (exact allowlisted URI as <see cref="ProcessStartInfo.FileName"/>,
/// <see cref="ProcessStartInfo.UseShellExecute"/> = true, empty <see cref="ProcessStartInfo.Arguments"/>/
/// <see cref="ProcessStartInfo.ArgumentList"/>, no <see cref="ProcessStartInfo.Verb"/>) and hands it
/// to the seam — so a test asserting on that object proves the REAL launch path was exercised,
/// not a bypass of it. A source-text search for "UseShellExecute = true" cannot catch a mutation
/// that reroutes through <c>cmd.exe</c> or PowerShell; asserting on the actual
/// <see cref="ProcessStartInfo"/> the seam receives can.</para>
/// </summary>
public static class SafeLinkLauncher
{
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { Uri.UriSchemeHttps, Uri.UriSchemeMailto };

    private static readonly IReadOnlyDictionary<SafeLinkDestination, Uri> Allowlist =
        BuildAllowlist();

    /// <summary>
    /// Test seam at the final process boundary only. Production default is the real
    /// <see cref="Process.Start(ProcessStartInfo)"/>; a test that sets this never launches a real
    /// browser or mail client, but every step BEFORE this call (allowlist lookup, scheme check,
    /// <see cref="ProcessStartInfo"/> construction) still runs for real.
    /// </summary>
    internal static Func<ProcessStartInfo, bool>? ProcessStartOverrideForTests;

    // 0 = idle, 1 = a launch is in flight. Interlocked so a reentrant/overlapping call (not just
    // a genuinely concurrent thread) is detected atomically rather than via a check-then-set race.
    private static int _launching;

    /// <summary>The approved URI for a destination. Exposed for display (host/address text) —
    /// never for composing a new, caller-controlled URI.</summary>
    public static Uri GetDestination(SafeLinkDestination destination) => Allowlist[destination];

    /// <summary>
    /// Opens one of the fixed, approved destinations. Single-flight: a duplicate activation while
    /// a launch is already underway does not start a second process and is reported truthfully as
    /// <see cref="SafeLinkOutcome.Busy"/> — never as <see cref="SafeLinkOutcome.Launched"/>. Never
    /// throws — every failure path returns <see cref="SafeLinkOutcome.Failed"/>.
    /// </summary>
    public static SafeLinkOutcome TryOpen(SafeLinkDestination destination)
    {
        if (Interlocked.CompareExchange(ref _launching, 1, 0) != 0)
            return SafeLinkOutcome.Busy;

        try
        {
            var uri = Allowlist[destination];
            if (!AllowedSchemes.Contains(uri.Scheme)) return SafeLinkOutcome.Rejected; // unreachable for real entries; defense in depth

            var psi = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            var start = ProcessStartOverrideForTests ?? RealStart;
            return start(psi) ? SafeLinkOutcome.Launched : SafeLinkOutcome.Failed;
        }
        catch
        {
            return SafeLinkOutcome.Failed;
        }
        finally
        {
            Volatile.Write(ref _launching, 0);
        }
    }

    /// <summary>
    /// Pure validator used to prove the scheme/shape rules independently of the allowlist
    /// contents: parses with <see cref="Uri.TryCreate(string,UriKind,out Uri)"/> and accepts only
    /// an absolute URI whose scheme is <c>https</c> or <c>mailto</c>. Rejects <c>file:</c>,
    /// <c>javascript:</c>, <c>data:</c>, relative paths, and UNC paths (which parse as
    /// <c>file:</c>). Exercised directly by tests; also used to self-check the allowlist below.
    /// </summary>
    internal static bool IsAllowedAbsoluteUri(string raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)) return false;
        if (!AllowedSchemes.Contains(parsed.Scheme)) return false;
        uri = parsed;
        return true;
    }

    private static bool RealStart(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<SafeLinkDestination, Uri> BuildAllowlist()
    {
        var raw = new Dictionary<SafeLinkDestination, string>
        {
            [SafeLinkDestination.Email] = "mailto:mohammedgbr5@gmail.com?subject=PhoneLink%20Pro%20-%20Support",
            [SafeLinkDestination.Telegram] = "https://t.me/m_s_73kg",
            [SafeLinkDestination.Instagram] = "https://www.instagram.com/m_s_73kg/",
            [SafeLinkDestination.TikTok] = "https://www.tiktok.com/@mohammed_sattar",
        };

        var result = new Dictionary<SafeLinkDestination, Uri>();
        foreach (var (destination, value) in raw)
        {
            // Fail loud at class load if a hardcoded entry is ever malformed or off-allowlist —
            // the compiled-in list must never silently accept a bad scheme.
            if (!IsAllowedAbsoluteUri(value, out var uri))
                throw new InvalidOperationException($"SafeLinkLauncher: invalid allowlist entry for {destination}");
            result[destination] = uri!;
        }
        return result;
    }

    internal static void ResetForTests()
    {
        ProcessStartOverrideForTests = null;
        Volatile.Write(ref _launching, 0);
    }
}
