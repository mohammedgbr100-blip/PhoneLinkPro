namespace ScrcpyDeck.Services;

/// <summary>One instructional card: a title and the ordered steps under it.</summary>
public sealed record ManufacturerGuideStep(string Title, IReadOnlyList<string> Bullets);

/// <summary>
/// One manufacturer's guidance for the two phone-side steps the setup wizard cannot verify
/// directly (enabling Developer options, then USB debugging). Wording is deliberately
/// conservative — menu paths shift between Android/OEM-skin versions — and nothing here is
/// auto-selected from a detected device (Batch 4B Phase 4: "without pretending it was
/// automatically detected").
/// </summary>
public sealed record ManufacturerGuide(
    string Id,
    string DisplayName,
    ManufacturerGuideStep DeveloperOptions,
    ManufacturerGuideStep UsbDebugging);

/// <summary>
/// Maintainable data table backing the wizard's manufacturer-guide selector (Batch 4B Phase 4)
/// — one place to edit instead of six near-duplicate XAML blocks. Pure data: no device
/// detection, no network access, no bundled web content.
/// </summary>
public static class ManufacturerGuides
{
    public static readonly IReadOnlyList<ManufacturerGuide> All = new[]
    {
        new ManufacturerGuide(
            "samsung", "Samsung",
            new ManufacturerGuideStep("تفعيل خيارات المطوّر — Samsung", new[]
            {
                "افتح الإعدادات ← حول الهاتف (About phone)",
                "ادخل إلى «معلومات البرنامج» (Software information)",
                "اضغط على «رقم البناء» (Build number) عدة مرات متتالية حتى تظهر رسالة تفعيل وضع المطوّر",
                "قد يُطلب رمز القفل للتأكيد — المسار قد يختلف قليلاً حسب إصدار One UI",
            }),
            new ManufacturerGuideStep("تفعيل تصحيح USB — Samsung", new[]
            {
                "ارجع إلى الإعدادات ← خيارات المطوّرين (Developer options)",
                "فعّل «تصحيح USB» (USB debugging)",
                "وافق على رسالة التأكيد إن ظهرت",
            })),

        new ManufacturerGuide(
            "xiaomi", "Xiaomi / Redmi / POCO",
            new ManufacturerGuideStep("تفعيل خيارات المطوّر — Xiaomi / Redmi / POCO", new[]
            {
                "افتح الإعدادات ← حول الهاتف (About phone)",
                "اضغط على «إصدار MIUI» أو «إصدار HyperOS» عدة مرات متتالية",
                "قد يُطلب رمز القفل أو حساب Mi للتأكيد — المسار يختلف بين MIUI وHyperOS",
            }),
            new ManufacturerGuideStep("تفعيل تصحيح USB — Xiaomi / Redmi / POCO", new[]
            {
                "افتح الإعدادات ← إعدادات إضافية ← خيارات المطوّرين",
                "فعّل «تصحيح USB» (USB debugging)",
                "قد تحتاج أيضاً لتفعيل «تثبيت عبر USB» حسب الإصدار",
            })),

        new ManufacturerGuide(
            "pixel", "Google Pixel / أندرويد قياسي",
            new ManufacturerGuideStep("تفعيل خيارات المطوّر — Pixel / أندرويد قياسي", new[]
            {
                "افتح الإعدادات ← حول الهاتف (About phone)",
                "اضغط على «رقم البناء» (Build number) عدة مرات متتالية حتى تظهر رسالة تفعيل وضع المطوّر",
            }),
            new ManufacturerGuideStep("تفعيل تصحيح USB — Pixel / أندرويد قياسي", new[]
            {
                "ارجع إلى الإعدادات ← نظام ← خيارات المطوّرين (System ← Developer options)",
                "فعّل «تصحيح USB» (USB debugging)",
            })),

        new ManufacturerGuide(
            "oppo", "OPPO / Realme / OnePlus",
            new ManufacturerGuideStep("تفعيل خيارات المطوّر — OPPO / Realme / OnePlus", new[]
            {
                "افتح الإعدادات ← حول الهاتف (About phone) أو «معلومات الجهاز»",
                "اضغط على «رقم الإصدار» (Version number أو Build number) عدة مرات متتالية",
                "المسار الدقيق يختلف بين ColorOS وRealme UI وOxygenOS",
            }),
            new ManufacturerGuideStep("تفعيل تصحيح USB — OPPO / Realme / OnePlus", new[]
            {
                "افتح الإعدادات ← إعدادات إضافية ← خيارات المطوّرين",
                "فعّل «تصحيح USB» (USB debugging)",
            })),

        new ManufacturerGuide(
            "huawei", "Huawei / Honor",
            new ManufacturerGuideStep("تفعيل خيارات المطوّر — Huawei / Honor", new[]
            {
                "افتح الإعدادات ← حول الهاتف (About phone)",
                "اضغط على «رقم الإصدار» (Build number) عدة مرات متتالية",
                "بعض أجهزة Huawei الحديثة لا تدعم خدمات جوجل — قد تختلف بعض الخطوات اللاحقة",
            }),
            new ManufacturerGuideStep("تفعيل تصحيح USB — Huawei / Honor", new[]
            {
                "افتح الإعدادات ← أنظمة وتحديثات ← خيارات المطوّرين",
                "فعّل «تصحيح USB» (USB debugging)",
            })),

        new ManufacturerGuide(
            "other", "جهاز أندرويد آخر",
            new ManufacturerGuideStep("تفعيل خيارات المطوّر (عام)", new[]
            {
                "افتح الإعدادات ← حول الهاتف / معلومات الجهاز",
                "ابحث عن «رقم البناء» أو «رقم الإصدار» (Build number)",
                "اضغط عليه عدة مرات متتالية حتى تظهر رسالة تفعيل وضع المطوّر",
                "التسمية الدقيقة تختلف حسب الشركة المصنّعة وإصدار أندرويد",
            }),
            new ManufacturerGuideStep("تفعيل تصحيح USB (عام)", new[]
            {
                "ارجع إلى الإعدادات وابحث عن «خيارات المطوّرين» (Developer options)",
                "فعّل «تصحيح USB» (USB debugging) ووافق على أي تأكيد يظهر",
            })),
    };

    /// <summary>The generic, manufacturer-neutral guide — the wizard's default until the user
    /// deliberately picks one, since a device's brand is not reliably known before it is
    /// authorized (Phase 3: "do not infer manufacturer before authorization").</summary>
    public static ManufacturerGuide Default => All[^1];

    public static ManufacturerGuide ById(string? id)
        => All.FirstOrDefault(g => g.Id == id) ?? Default;
}
