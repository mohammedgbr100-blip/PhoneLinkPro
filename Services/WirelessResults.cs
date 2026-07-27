using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>
/// Narrow, truthful outcomes for a Pair attempt. Never carries raw adb stdout/stderr — only
/// <see cref="WirelessOperations.ClassifyPair"/>'s own fixed/templated Arabic text reaches the UI.
/// </summary>
public enum WirelessPairOutcome { Success, Failed, ToolsUnavailable, InvalidInput, Cancelled }

/// <summary>
/// A Pair result the UI may safely bind to. <see cref="Success"/> means the pairing HANDSHAKE
/// succeeded — it never implies the phone is connected (Batch 4C Phase 2/5 truthfulness rule).
/// </summary>
public sealed record WirelessPairResult(WirelessPairOutcome Outcome, string Message)
{
    public bool Success => Outcome == WirelessPairOutcome.Success;
}

/// <summary>
/// Narrow, truthful outcomes for a Connect attempt. <see cref="CommandSucceeded"/> reports only
/// that `adb connect` itself succeeded — the caller must still reconcile against real
/// DeviceWatcher/adb device state before ever claiming "connected" (Batch 4C Phase 5).
/// </summary>
public enum WirelessConnectOutcome { CommandSucceeded, Failed, ToolsUnavailable, InvalidInput, Cancelled }

public sealed record WirelessConnectResult(WirelessConnectOutcome Outcome, string Message)
{
    public bool CommandSucceeded => Outcome == WirelessConnectOutcome.CommandSucceeded;
}

/// <summary>
/// Pure, testable helpers shared by the wireless page: separate host/port validation (reusing
/// <see cref="AdbInput"/> rather than re-implementing endpoint parsing) and safe classification
/// of a <see cref="ProcessResult"/> into a narrow, UI-safe result. Introduces no adb call of its
/// own — <see cref="AdbService.PairAsync"/>/<see cref="AdbService.ConnectAsync"/> still own the
/// only production process launches.
/// </summary>
public static class WirelessOperations
{
    // ---------------------------------------------------------------- host/port

    /// <summary>
    /// Validates a bare host for a dedicated host field — the pairing/connection PORT has its
    /// own field, so a host string that itself carries a port (as `adb connect`'s combined
    /// "host:port" form does) is rejected here rather than silently accepted. Reuses
    /// <see cref="AdbInput.TryValidateEndpoint"/> for the actual address parsing/normalization
    /// (IPv4, bracketed IPv6) so no second address-parsing implementation exists.
    /// <para>
    /// M2 repair: an IPv6 zone/scope identifier (the "%eth0"/"%3" suffix RFC 4007 defines for
    /// link-local addresses) is rejected outright, bracketed or not — this workflow's narrow
    /// product contract is "standard IPv6 without a zone identifier" only. The rejection lives
    /// here, in this dedicated wireless-page layer, rather than inside
    /// <see cref="AdbInput.TryValidateEndpoint"/> itself, so no unrelated AdbInput consumer
    /// (device-serial validation, per-device commands) is affected by this page's narrower
    /// contract. This also sidesteps ever needing this workflow to depend on exactly how
    /// <see cref="IPAddress.TryParse"/> resolves a "%"-suffixed literal on a given platform — the
    /// zone text is never passed on to be normalized, silently rewritten or silently dropped.
    /// </para>
    /// </summary>
    public static bool TryValidateHost(string? host, out string normalizedHost, out string error)
    {
        normalizedHost = "";
        error = "";

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "أدخل عنوان IP للهاتف.";
            return false;
        }

        var text = host.Trim();

        if (text.Contains('%'))
        {
            error = "عناوين IPv6 المرتبطة بواجهة شبكة محدّدة (zone/scope) غير مدعومة في هذا الإجراء.";
            return false;
        }

        if (CarriesItsOwnPort(text))
        {
            error = "أدخل عنوان IP فقط بدون رقم المنفذ — للمنفذ حقل مستقل.";
            return false;
        }

        return AdbInput.TryValidateEndpoint(text, out normalizedHost, out error);
    }

    /// <summary>True when a host string already embeds a "host:port" or "[host]:port" suffix.</summary>
    private static bool CarriesItsOwnPort(string text)
    {
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            return close >= 0 && close + 1 < text.Length && text[close + 1] == ':';
        }

        // A bare address (IPv4 or unbracketed IPv6) never carries a port of its own.
        if (IPAddress.TryParse(text, out _)) return false;

        return text.Contains(':');
    }

    /// <summary>
    /// Combines an already-validated host with an independently-validated port into the
    /// combined endpoint form <see cref="AdbInput.TryValidateEndpoint"/> (and therefore
    /// <see cref="AdbService.PairAsync"/>/<see cref="AdbService.ConnectAsync"/>) accept.
    /// </summary>
    public static bool TryBuildEndpoint(
        string? host, int port, out string endpoint, out string error)
    {
        endpoint = "";
        if (!TryValidateHost(host, out var normalizedHost, out error)) return false;
        if (!AdbInput.TryValidatePort(port, out error)) return false;

        endpoint = $"{normalizedHost}:{port}";
        return true;
    }

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Which wireless device the connected-devices card should treat as selected, given the
    /// live snapshot and whatever was previously selected. Never silently prefers a device the
    /// user has not chosen when more than one is present: a still-present selection is kept (by
    /// ordinal serial match — the same equality rule <see cref="DeviceWatcher"/> uses
    /// throughout), a single remaining candidate is a safe default, and two or more unselected
    /// candidates resolve to no selection at all rather than guessing.
    /// </summary>
    public static string? DecideWirelessSelection(IReadOnlyList<Models.AdbDevice> wireless, string? currentSelection)
    {
        if (currentSelection != null && wireless.Any(d => string.Equals(d.Serial, currentSelection, StringComparison.Ordinal)))
            return currentSelection;

        return wireless.Count == 1 ? wireless[0].Serial : null;
    }

    // ---------------------------------------------------------------- classification

    /// <summary>
    /// Negative-marker-precedence repair: adb's real failure text can land on EITHER stream —
    /// stdout for the ordinary case, but stderr for some failure paths (and some adb builds mix
    /// the two) — while the other stream still carries an unrelated positive-looking phrase. A
    /// classifier that reads stdout alone can therefore be fooled by a genuinely-failed attempt
    /// whose stderr carries the real "Failed"/"failed to connect" text. This helper evaluates a
    /// marker predicate against stdout and stderr independently and returns true if either
    /// stream matches, so negative evidence from stderr is never silently ignored just because
    /// stdout happened to look positive.
    /// </summary>
    private static bool AnyStreamContains(ProcessResult r, Func<string, bool> looksLike)
        => looksLike(r.StdOut ?? "") || looksLike(r.StdErr ?? "");

    /// <summary>adb's own real success phrase for a pairing handshake.</summary>
    internal static bool LooksPairSuccessful(ProcessResult r)
        => AnyStreamContains(r, s => s.Contains("Successfully", StringComparison.OrdinalIgnoreCase));

    /// <summary>adb's own real failure phrase for a pairing handshake ("Failed: ...").</summary>
    internal static bool LooksPairFailed(ProcessResult r)
        => AnyStreamContains(r, s => s.Contains("Failed", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// adb's own real success phrase for a connect attempt ("connected to ..." / "already
    /// connected to ..."). Deliberately narrower than a bare Contains("connected") check, which
    /// would also match "disconnected".
    /// </summary>
    internal static bool LooksConnectSuccessful(ProcessResult r)
        => AnyStreamContains(r, s => s.Contains("connected to", StringComparison.OrdinalIgnoreCase));

    /// <summary>adb's own real failure phrases for a connect attempt.</summary>
    internal static bool LooksConnectFailed(ProcessResult r)
        => AnyStreamContains(r, text =>
            text.Contains("failed to connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("disconnected", StringComparison.OrdinalIgnoreCase));

    private const string PairFailedMessage =
        "فشل الإقران. تحقّق من رمز الإقران ومنفذ الإقران وأن الجهازين على نفس الشبكة.";

    private const string ConnectFailedMessage =
        "فشل الاتصال بالعنوان المحدد. تحقّق من العنوان والمنفذ وأن الهاتف على نفس الشبكة.";

    /// <summary>
    /// Classifies a raw <see cref="ProcessResult"/> from <see cref="AdbService.PairAsync"/> into
    /// a narrow, safe result. Never forwards raw stdout/stderr to the caller — every branch is
    /// this method's own fixed Arabic text, except <see cref="ProcessOutcome.InvalidInput"/>,
    /// whose Message is already this app's own validation text (never adb output).
    /// <para>
    /// M1 repair: decision order is (1) a start failure/timeout/cancellation is always a
    /// failure, (2) any non-zero <see cref="ProcessResult.ExitCode"/> is a failure regardless of
    /// what stdout/stderr claim, (3) a negative marker on EITHER stdout OR stderr outranks a
    /// positive one — negative-marker precedence is never limited to stdout alone (M-negative-
    /// marker repair), (4) a positive marker is trusted only at exit 0 with no negative marker
    /// present on either stream, (5) empty or unrecognized output at exit 0 fails closed rather
    /// than guessing.
    /// </para>
    /// </summary>
    public static WirelessPairResult ClassifyPair(ProcessResult r)
    {
        if (r.Outcome == ProcessOutcome.InvalidInput)
            return new WirelessPairResult(WirelessPairOutcome.InvalidInput, r.Message);
        if (r.Outcome == ProcessOutcome.Cancelled)
            return new WirelessPairResult(WirelessPairOutcome.Cancelled, "تم إلغاء عملية الإقران.");
        if (r.Outcome == ProcessOutcome.StartFailed)
            return new WirelessPairResult(WirelessPairOutcome.ToolsUnavailable, "تعذّر تشغيل أداة الاتصال بالجهاز.");
        if (r.Outcome == ProcessOutcome.TimedOut)
            return new WirelessPairResult(WirelessPairOutcome.Failed, "انتهت مهلة الإقران. تحقق من الشبكة وحاول مجدداً.");

        // Only ProcessOutcome.Exited reaches here. A non-zero exit is always a failure, whatever
        // stdout/stderr claim — negative markers on either stream outrank positive, positive is
        // trusted only at exit 0, and unrecognized/empty output at exit 0 fails closed (falls
        // through to the default).
        if (r.Outcome != ProcessOutcome.Exited || r.ExitCode != 0)
            return new WirelessPairResult(WirelessPairOutcome.Failed, PairFailedMessage);

        if (LooksPairFailed(r))
            return new WirelessPairResult(WirelessPairOutcome.Failed, PairFailedMessage);

        if (LooksPairSuccessful(r))
            return new WirelessPairResult(WirelessPairOutcome.Success, "تم إقران هذا الكمبيوتر بالهاتف بنجاح.");

        return new WirelessPairResult(WirelessPairOutcome.Failed, PairFailedMessage);
    }

    /// <summary>
    /// Classifies a raw <see cref="ProcessResult"/> from <see cref="AdbService.ConnectAsync"/>.
    /// <see cref="WirelessPairOutcome.Success"/>'s Connect counterpart is deliberately named
    /// <see cref="WirelessConnectOutcome.CommandSucceeded"/> rather than "Connected" — the
    /// caller must still reconcile against real device state before showing a connected device.
    /// Same M1 decision order as <see cref="ClassifyPair"/> — see its remarks.
    /// </summary>
    public static WirelessConnectResult ClassifyConnect(ProcessResult r)
    {
        if (r.Outcome == ProcessOutcome.InvalidInput)
            return new WirelessConnectResult(WirelessConnectOutcome.InvalidInput, r.Message);
        if (r.Outcome == ProcessOutcome.Cancelled)
            return new WirelessConnectResult(WirelessConnectOutcome.Cancelled, "تم إلغاء عملية الاتصال.");
        if (r.Outcome == ProcessOutcome.StartFailed)
            return new WirelessConnectResult(WirelessConnectOutcome.ToolsUnavailable, "تعذّر تشغيل أداة الاتصال بالجهاز.");
        if (r.Outcome == ProcessOutcome.TimedOut)
            return new WirelessConnectResult(WirelessConnectOutcome.Failed, "انتهت مهلة الاتصال. تحقق من الشبكة وحاول مجدداً.");

        if (r.Outcome != ProcessOutcome.Exited || r.ExitCode != 0)
            return new WirelessConnectResult(WirelessConnectOutcome.Failed, ConnectFailedMessage);

        if (LooksConnectFailed(r))
            return new WirelessConnectResult(WirelessConnectOutcome.Failed, ConnectFailedMessage);

        if (LooksConnectSuccessful(r))
            return new WirelessConnectResult(WirelessConnectOutcome.CommandSucceeded, "تم إرسال أمر الاتصال.");

        return new WirelessConnectResult(WirelessConnectOutcome.Failed, ConnectFailedMessage);
    }
}

/// <summary>
/// One row the connected-wireless-devices card renders. <see cref="DisambiguatorToken"/> is only
/// ever non-null when another device in the SAME snapshot would otherwise render an identical
/// <see cref="BaseLabel"/>+<see cref="Masked"/> pair (Batch 4C M5) — most rows never need one.
/// </summary>
public sealed record WirelessDeviceRow(
    AdbDevice Device, string BaseLabel, string Masked, string? DisambiguatorToken)
{
    /// <summary>Safe accessible name: base label, masked endpoint, and — only on a genuine
    /// collision — the derived token. Never the raw serial/endpoint.</summary>
    public string AccessibleName => DisambiguatorToken == null
        ? $"{BaseLabel} — {Masked}"
        : $"{BaseLabel} — {Masked} — {DisambiguatorToken}";
}

/// <summary>
/// Collision-safe display identity for the wireless-devices list (Batch 4C M5 repair).
/// <see cref="ShellLayout.MaskSerial"/> keeps only the first 4 and last 2 characters, so two
/// different endpoints on the same /24 subnet with the same last two digits —
/// <c>192.168.1.42:5555</c> and <c>192.168.1.55:5555</c> — both mask to the identical
/// "192.•••••55". Combined with an identical (or absent) model name, two real, distinct,
/// authorized devices could render as visually indistinguishable rows. This type never exposes
/// a raw endpoint to close that gap — instead it appends a short, deterministic
/// disambiguation token derived from the device's own serial, computed fresh in memory on
/// every render and never persisted or logged, and only on rows that actually collide. This is
/// a display-collision fix, not a security/anonymization guarantee — see
/// <see cref="DisambiguatorToken"/> for the precise claim.
/// </summary>
public static class WirelessDeviceDisplay
{
    /// <summary>The safe base label: a real model/brand name when known, otherwise a truthful
    /// generic Arabic fallback — never the raw serial (mirrors DeviceSetupWizardWindow's own
    /// device rows via <see cref="ShellLayout.DeviceNameOrFallback"/>).</summary>
    public static string BaseLabel(AdbDevice device)
        => ShellLayout.DeviceNameOrFallback(device.Model, device.DisplayName, device.State);

    /// <summary>
    /// A short, deterministic disambiguation token derived from the raw serial via SHA-256 —
    /// the same serial always yields the same token (so a replacement <see cref="AdbDevice"/>
    /// object for the same physical device keeps the same displayed identity). Its sole purpose
    /// is to make two colliding visible rows distinguishable; it is transient (computed in
    /// memory only, never persisted, logged, or used as this app's actual device identity — the
    /// raw serial remains that) and truncated to a few hex characters, so it carries far too
    /// little entropy to be a cryptographic anonymization guarantee. It does not directly
    /// display the raw endpoint, but this is not a claim that the endpoint cannot be recovered
    /// by other means (e.g. a short-token collision search) — only that this UI never shows it.
    /// </summary>
    internal static string DisambiguatorToken(string serial, int hexChars = 6)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serial));
        var chars = Math.Clamp(hexChars, 2, hash.Length * 2);
        return Convert.ToHexString(hash)[..chars];
    }

    /// <summary>
    /// Builds one display row per device, in the SAME order as <paramref name="devices"/>
    /// (selection/ordering elsewhere stays ordinal-by-serial and is unaffected by this). A
    /// disambiguator token is attached only to rows whose base label + masked endpoint would
    /// otherwise be identical to another row's in this same call; on the (astronomically
    /// unlikely) event that two colliding rows also share the same token prefix, the token is
    /// deterministically extended until distinct.
    /// </summary>
    public static IReadOnlyList<WirelessDeviceRow> BuildRows(IReadOnlyList<AdbDevice> devices)
    {
        var visible = devices.Select(d => (BaseLabel: BaseLabel(d), Masked: ShellLayout.MaskSerial(d.Serial))).ToList();
        var counts = visible.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());

        var rows = new List<WirelessDeviceRow>(devices.Count);
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var (baseLabel, masked) = visible[i];

            if (counts[(baseLabel, masked)] <= 1)
            {
                rows.Add(new WirelessDeviceRow(device, baseLabel, masked, null));
                continue;
            }

            var hexChars = 6;
            var token = DisambiguatorToken(device.Serial, hexChars);
            while (hexChars < 16 &&
                   rows.Any(r => r.BaseLabel == baseLabel && r.Masked == masked && r.DisambiguatorToken == token))
                token = DisambiguatorToken(device.Serial, ++hexChars);

            rows.Add(new WirelessDeviceRow(device, baseLabel, masked, token));
        }

        return rows;
    }
}
