namespace ScrcpyDeck.Services;

/// <summary>
/// Composes the Arabic user-facing text for integrity states (Part F rules): specific but
/// safe. Never a hash value, never an absolute/personal path (only the manifest-relative
/// name), never a claim of malware or of being "fully protected", never a stack trace.
/// </summary>
public static class EngineIntegrityMessages
{
    private const string RepairAdvice =
        "يُرجى إعادة تثبيت PhoneLink Pro من حزمة تثبيت موثوقة لإصلاح ملفات البرنامج.";

    /// <summary>The full user-facing message for a verification result.</summary>
    public static string For(EngineIntegrityResult result) => result.Status switch
    {
        EngineIntegrityStatus.Valid =>
            "ملفات البرنامج سليمة.",

        EngineIntegrityStatus.MissingFile =>
            WithFile("أحد ملفات البرنامج المطلوبة مفقود", result) + "\n" + RepairAdvice,

        EngineIntegrityStatus.HashMismatch =>
            WithFile("أحد ملفات البرنامج المطلوبة تم تغييره أو أنه تالف", result) + "\n" + RepairAdvice,

        EngineIntegrityStatus.UnreadableFile =>
            WithFile("تعذّرت قراءة أحد ملفات البرنامج المطلوبة", result) +
            "\nأغلق البرامج الأخرى ثم أعد المحاولة، وإذا استمرت المشكلة " + RepairAdvice,

        EngineIntegrityStatus.VerificationCancelled or EngineIntegrityStatus.InternalError =>
            "تعذّر إكمال التحقق من ملفات البرنامج. أعد المحاولة، وإذا استمرت المشكلة " + RepairAdvice,

        // MissingManifestData / UnexpectedDuplicatePath: the trusted data itself is unusable.
        _ =>
            "بيانات التحقق من ملفات البرنامج غير مكتملة. " + RepairAdvice,
    };

    /// <summary>
    /// Deduplication key for user notifications: one dialog per DISTINCT integrity state
    /// (status + affected file), never one per watcher poll or per click.
    /// </summary>
    public static string NotificationKey(EngineIntegrityResult result)
        => $"{result.Status}|{result.AffectedPath}";

    private static string WithFile(string prefix, EngineIntegrityResult result)
        => string.IsNullOrEmpty(result.AffectedPath)
            ? prefix + "."
            : $"{prefix}:\n{result.AffectedPath}";
}
