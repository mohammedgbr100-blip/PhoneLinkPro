using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>Which way a single file transfer moved. Carried by <see cref="TransferItemSummary"/>
/// so a mixed-history results list can still label each row truthfully.</summary>
public enum FileTransferDirection { Send, Receive }

/// <summary>
/// Narrow, truthful outcomes for one `adb push`/`adb pull` attempt. Mirrors the shape of
/// <see cref="WirelessPairOutcome"/> (Batch 4C) deliberately: same decision discipline, same
/// "never carry raw adb output" rule.
/// </summary>
public enum FileTransferOutcome { Success, Failed, ToolsUnavailable, InvalidInput, Cancelled }

/// <summary>
/// A push/pull result the UI may safely bind to. <see cref="Message"/> is always this app's own
/// fixed/templated Arabic text — never raw adb stdout/stderr.
/// </summary>
public sealed record FileTransferResult(FileTransferOutcome Outcome, string Message)
{
    public bool Success => Outcome == FileTransferOutcome.Success;
}

/// <summary>
/// One row of a batch (or single-item) transfer's current-session results list. Carries only a
/// safe file NAME (never a full local path) and this app's own safe message text.
/// </summary>
public sealed record TransferItemSummary(
    FileTransferDirection Direction, string SafeFileName, bool Success, string Message);

/// <summary>The fixed, truthful set of standard Android shared-storage send destinations.</summary>
public enum StandardRemoteDestination { Download, Documents, Pictures, Movies, Music }

/// <summary>
/// Pure, testable helpers for the guided file-transfer page: local/remote path validation,
/// standard destination construction, device-selection policy and safe classification of a
/// <see cref="ProcessResult"/> from `adb push`/`adb pull` into a narrow, UI-safe result.
/// Introduces no adb call of its own — <see cref="AdbService.PushAsync"/>/<see cref="AdbService.PullAsync"/>
/// still own the only production process launches.
/// </summary>
public static class TransferOperations
{
    // ---------------------------------------------------------------- standard destinations

    /// <summary>
    /// Fixed Android shared-storage roots for the standard destinations, built from internal
    /// constants only — never from user text (Phase 6: "construct paths from internal constants
    /// rather than user text" for standard send destinations).
    /// </summary>
    public static readonly IReadOnlyDictionary<StandardRemoteDestination, string> StandardDestinationPaths =
        new Dictionary<StandardRemoteDestination, string>
        {
            [StandardRemoteDestination.Download] = "/sdcard/Download",
            [StandardRemoteDestination.Documents] = "/sdcard/Documents",
            [StandardRemoteDestination.Pictures] = "/sdcard/Pictures",
            [StandardRemoteDestination.Movies] = "/sdcard/Movies",
            [StandardRemoteDestination.Music] = "/sdcard/Music",
        };

    /// <summary>Approved Arabic label for each standard destination (UI display only).</summary>
    public static readonly IReadOnlyDictionary<StandardRemoteDestination, string> StandardDestinationLabels =
        new Dictionary<StandardRemoteDestination, string>
        {
            [StandardRemoteDestination.Download] = "التنزيلات",
            [StandardRemoteDestination.Documents] = "المستندات",
            [StandardRemoteDestination.Pictures] = "الصور",
            [StandardRemoteDestination.Movies] = "الفيديوهات",
            [StandardRemoteDestination.Music] = "الموسيقى",
        };

    // ---------------------------------------------------------------- remote (Android) path validation

    /// <summary>The only shared-storage roots this product accepts a remote path under.</summary>
    private static readonly string[] AcceptedRemoteRoots = ["/sdcard/", "/storage/emulated/0/"];

    /// <summary>
    /// Shell-metacharacters rejected everywhere in a remote path. Defense in depth only —
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> already makes local command-
    /// line injection structurally impossible, and `adb push`/`adb pull` never spawn a remote
    /// shell (unlike `adb shell`) — but a value this restrictive can never be misread as an adb
    /// OPTION or carry a construct meaningful to any shell either.
    /// </summary>
    private static readonly char[] ForbiddenPathChars = [';', '|', '&', '$', '`', '<', '>', '\\', '~', '^', '*', '?', '"', '\''];

    private static readonly Regex WindowsDrivePattern = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    /// <summary>
    /// Validates an absolute Android shared-storage path. Used both for a Receive path (must
    /// eventually name an exact file — never invented by remote browsing, always typed by the
    /// user) and for an optional custom Send destination directory: the syntax rules are
    /// identical, so one pure validator serves both (Phase 6).
    /// <para>
    /// Never trims or rewrites the meaningful content of a valid path — only surrounding
    /// whitespace is trimmed. An invalid path is rejected outright, never silently repaired.
    /// </para>
    /// </summary>
    public static bool TryValidateRemotePath(string? path, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "أدخل المسار الكامل للملف على الهاتف.";
            return false;
        }

        var text = path.Trim();

        if (text.Length > 4096)
        {
            error = "المسار طويل بشكل غير معتاد.";
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                error = "المسار يحتوي على محارف غير صالحة.";
                return false;
            }
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            error = "لا يمكن استخدام رابط أو معرّف URI كمسار على الهاتف.";
            return false;
        }

        if (WindowsDrivePattern.IsMatch(text))
        {
            error = "هذا مسار خاص بـ Windows، وليس مساراً على الهاتف.";
            return false;
        }

        if (text[0] != '/')
        {
            error = "يجب أن يكون المسار مطلقاً ويبدأ بـ /.";
            return false;
        }

        if (text.Split('/').Any(segment => segment == ".."))
        {
            error = "لا يمكن استخدام مسارات صعود (..) في المسار.";
            return false;
        }

        if (text.IndexOfAny(ForbiddenPathChars) >= 0)
        {
            error = "المسار يحتوي على رموز غير مسموح بها.";
            return false;
        }

        var root = AcceptedRemoteRoots.FirstOrDefault(r => text.StartsWith(r, StringComparison.Ordinal));
        if (root == null)
        {
            error = "يجب أن يبدأ المسار بـ /sdcard/ أو /storage/emulated/0/.";
            return false;
        }

        if (text.Length == root.Length)
        {
            error = "أدخل مساراً لملف محدد، وليس المجلد الجذر فقط.";
            return false;
        }

        normalized = text;
        return true;
    }

    // ---------------------------------------------------------------- local (Windows) path validation

    /// <summary>
    /// Pure syntactic check for a local file about to be sent. Existence is checked separately,
    /// at the moment the transfer actually begins (I/O, not pure) — this only rejects a path that
    /// could never be a real file regardless of disk state.
    /// </summary>
    public static bool TryValidateLocalSendPath(string? path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "لم يتم اختيار ملف.";
            return false;
        }

        foreach (var c in path)
        {
            if (char.IsControl(c))
            {
                error = "مسار الملف يحتوي على محارف غير صالحة.";
                return false;
            }
        }

        if (!Path.IsPathRooted(path))
        {
            error = "يجب اختيار الملف عبر نافذة اختيار الملفات.";
            return false;
        }

        return true;
    }

    /// <summary>Safe display name for a local file — never the full path (Phase 5/10).</summary>
    public static string SafeFileName(string localPath) => Path.GetFileName(localPath);

    /// <summary>
    /// Derives a safe local file name suggestion from a validated remote path's last segment,
    /// with every character Windows cannot use in a file name replaced — remote path TEXT is
    /// untrusted product content, never assumed to already be a legal Windows file name (Phase 5:
    /// "do not infer a local file name from unsafe remote path text without sanitizing it").
    /// </summary>
    public static string SuggestLocalFileName(string remotePath)
    {
        var last = remotePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        if (last.Length == 0) return "ملف_من_الهاتف";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = last.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "ملف_من_الهاتف" : sanitized;
    }

    // ---------------------------------------------------------------- batch aggregation

    /// <summary>
    /// Truthfully summarizes a batch (or a cancelled partial batch — cancellation never rolls
    /// back an item already reported here) into succeeded/failed counts. Never claims the whole
    /// batch succeeded when at least one item failed.
    /// </summary>
    public static (int Succeeded, int Failed) SummarizeBatch(IReadOnlyList<TransferItemSummary> items)
        => (items.Count(i => i.Success), items.Count(i => !i.Success));

    // ---------------------------------------------------------------- device selection

    /// <summary>
    /// Which device the transfer page should treat as selected, given the live authorized-device
    /// snapshot, whatever was previously explicitly selected, and — for the very first render
    /// only — <see cref="AppState.CurrentDevice"/>. Same policy as
    /// <see cref="WirelessOperations.DecideWirelessSelection"/> (Batch 4C): a still-present
    /// selection is kept (ordinal serial match), a single remaining candidate is a safe default,
    /// and two-or-more unselected candidates resolve to no selection — never guessed.
    /// </summary>
    public static string? DecideDeviceSelection(IReadOnlyList<AdbDevice> authorized, string? currentSelection)
    {
        if (currentSelection != null && authorized.Any(d => string.Equals(d.Serial, currentSelection, StringComparison.Ordinal)))
            return currentSelection;

        return authorized.Count == 1 ? authorized[0].Serial : null;
    }

    // ---------------------------------------------------------------- classification

    /// <summary>
    /// Normalized evidence check across BOTH stdout and stderr (Batch 4C negative-marker-
    /// precedence repair, reused here from the start): adb's real diagnostic text for a failed
    /// push/pull can land on either stream.
    /// </summary>
    private static bool AnyStreamContains(ProcessResult r, Func<string, bool> looksLike)
        => looksLike(r.StdOut ?? "") || looksLike(r.StdErr ?? "");

    /// <summary>adb's own real failure phrases for a push/pull.</summary>
    internal static bool LooksTransferFailed(ProcessResult r) => AnyStreamContains(r, s =>
        s.Contains("error:", StringComparison.OrdinalIgnoreCase)
        || s.Contains("failed to copy", StringComparison.OrdinalIgnoreCase)
        || s.Contains("no such file", StringComparison.OrdinalIgnoreCase)
        || s.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
        || s.Contains("cannot stat", StringComparison.OrdinalIgnoreCase)
        || s.Contains("0 files pushed", StringComparison.OrdinalIgnoreCase)
        || s.Contains("0 files pulled", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// adb's own real success phrase for a push/pull — "N file(s) pushed"/"N file(s) pulled"
    /// with N ≥ 1. Deliberately never treats a bare "100%" progress marker as proof of success
    /// (Phase 3): that text can appear on stdout even when the process goes on to exit non-zero.
    /// </summary>
    private static readonly Regex TransferSucceededPattern =
        new(@"[1-9]\d*\s+files?\s+(pushed|pulled)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool LooksTransferSucceeded(ProcessResult r)
        => AnyStreamContains(r, s => TransferSucceededPattern.IsMatch(s));

    private const string SendFailedMessage = "تعذّر رفع الملف إلى الهاتف. تحقّق من المساحة المتاحة والصلاحيات ثم أعد المحاولة.";
    private const string ReceiveFailedMessage = "تعذّر سحب الملف من الهاتف. تحقّق من صحة المسار ووجود الملف ثم أعد المحاولة.";

    /// <summary>
    /// Classifies a raw <see cref="ProcessResult"/> from <see cref="AdbService.PushAsync"/>.
    /// Decision order mirrors <see cref="WirelessOperations.ClassifyPair"/>: (1) a start
    /// failure/timeout/cancellation is always a failure/its own outcome, (2) any non-zero
    /// <see cref="ProcessResult.ExitCode"/> is a failure regardless of what stdout/stderr claim —
    /// a bare "100%" progress line is never read as success on its own, (3) a negative marker on
    /// either stream outranks a positive one, (4) a positive marker is trusted only at exit 0 with
    /// no negative marker present, (5) empty or unrecognized output at exit 0 fails closed.
    /// Never forwards raw stdout/stderr — every branch is this method's own fixed Arabic text.
    /// </summary>
    public static FileTransferResult ClassifyPush(ProcessResult r) => Classify(r, SendFailedMessage,
        "تم رفع الملف إلى الهاتف بنجاح.");

    /// <summary>Classifies a raw <see cref="ProcessResult"/> from <see cref="AdbService.PullAsync"/>. Same decision order as <see cref="ClassifyPush"/>.</summary>
    public static FileTransferResult ClassifyPull(ProcessResult r) => Classify(r, ReceiveFailedMessage,
        "تم سحب الملف من الهاتف بنجاح.");

    private static FileTransferResult Classify(ProcessResult r, string failedMessage, string successMessage)
    {
        if (r.Outcome == ProcessOutcome.InvalidInput)
            return new FileTransferResult(FileTransferOutcome.InvalidInput, r.Message);
        if (r.Outcome == ProcessOutcome.Cancelled)
            return new FileTransferResult(FileTransferOutcome.Cancelled, "تم إلغاء عملية النقل.");
        if (r.Outcome == ProcessOutcome.StartFailed)
            return new FileTransferResult(FileTransferOutcome.ToolsUnavailable, "تعذّر تشغيل أداة الاتصال بالجهاز.");
        if (r.Outcome == ProcessOutcome.TimedOut)
            return new FileTransferResult(FileTransferOutcome.Failed, "انتهت مهلة النقل. تحقق من الاتصال بالجهاز وحاول مجدداً.");

        // Only ProcessOutcome.Exited reaches here. A non-zero exit is always a failure, whatever
        // stdout/stderr claim (never trusts a bare "100%" progress marker on its own) — negative
        // markers on either stream outrank positive, positive is trusted only at exit 0, and
        // unrecognized/empty output at exit 0 fails closed (falls through to the default).
        if (r.Outcome != ProcessOutcome.Exited || r.ExitCode != 0)
            return new FileTransferResult(FileTransferOutcome.Failed, failedMessage);

        if (LooksTransferFailed(r))
            return new FileTransferResult(FileTransferOutcome.Failed, failedMessage);

        if (LooksTransferSucceeded(r))
            return new FileTransferResult(FileTransferOutcome.Success, successMessage);

        return new FileTransferResult(FileTransferOutcome.Failed, failedMessage);
    }
}
