# PhoneLink Pro

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)

> **English summary:** PhoneLink Pro is a Windows (WPF / .NET 9) desktop app with an Arabic-first UI
> for mirroring and controlling Android phones, wireless pairing, file transfer and app management.
> It is a friendly front-end built on top of [scrcpy](https://github.com/Genymobile/scrcpy) and
> Android `adb`. Source code is MIT-licensed; bundled third-party components keep their own licenses.

<div dir="rtl">

**PhoneLink Pro** — لوحة تحكم احترافية للأندرويد على ويندوز. تتحكّم بهاتفك وتعرض شاشته
على حاسوبك بضغطة زر، وتنقل الملفات وتدير التطبيقات — بواجهة عربية بسيطة دون أي خبرة تقنية.

مبني فوق المشروعين مفتوحي المصدر [scrcpy](https://github.com/Genymobile/scrcpy) و
[Android SDK Platform-Tools (adb)](https://developer.android.com/tools/releases/platform-tools).

**تصميم وتطوير: ENG. Mohammed Sattar** · © 2026 · مرخّص بموجب [MIT](LICENSE).

---

## المميزات

| القسم | الوظيفة |
|---|---|
| 🖥️ لوحة الأجهزة | كشف تلقائي للأجهزة (USB + Wi-Fi) مع الموديل والنسخة والدقة والبطارية |
| ▶️ بث الشاشة | 4 مستويات جودة جاهزة + خيارات سريعة |
| 📡 الاتصال اللاسلكي | تحويل تلقائي من الكابل إلى Wi-Fi + اتصال يدوي + إقران |
| ⚙️ خيارات متقدمة | تحكّم كامل بكل خيارات العرض |
| 📦 إدارة التطبيقات | عرض/تثبيت/حذف/تعطيل التطبيقات |
| 📁 نقل الملفات | رفع بالسحب والإفلات + سحب مع مؤشر تحميل |

يبدأ التطبيق بـ **سياسة خصوصية** يوافق عليها المستخدم، ثم **شرح تعليمي** خطوة بخطوة
(متاح دائماً من زر «؟»). البرنامج يعمل محلياً بالكامل ولا يرسل أي بيانات إلى الإنترنت.

---

## التشغيل (للمستخدم)

> **لا توجد نسخة ثنائية (مثبّت) منشورة بعد.** المثبّت معلّق إلى حين استكمال مراجعة تراخيص
> المكوّنات المرفقة؛ إلى ذلك الحين ابنِ البرنامج من المصدر (القسم التالي).

عند صدور أول نسخة: نزّل المثبّت **`PhoneLinkPro-Setup-v<الإصدار>.exe`** من صفحة
[Releases](../../releases) ← التالي ← تثبيت.

ثم من داخل البرنامج: وصّل الهاتف عبر USB وفعّل «تصحيح USB»، ثم لوحة الأجهزة ← تحديد وبثّ.

> قد تكون النسخ **غير موقّعة رقمياً**، وعندها يُظهر Windows SmartScreen تحذيراً عند التشغيل الأول.

---

## البناء من المصدر (للمطوّر)

### المتطلبات

- Windows 10/11 x64
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- (للمثبّت فقط) [Inno Setup 6](https://jrsoftware.org/isinfo.php)
- **حزمة المحرّك:** ملفات scrcpy لا تُودَع في هذا المستودع. نزّل
  [`scrcpy-win64-v3.3.4.zip`](https://github.com/Genymobile/scrcpy/releases/tag/v3.3.4)
  وفكّ ضغطها في مجلد اسمه `scrcpy` **بجانب** مجلد المستودع:

```text
<أي مجلد>\
├─ PhoneLinkPro\   ← هذا المستودع
└─ scrcpy\         ← محتوى scrcpy-win64-v3.3.4 (scrcpy.exe, adb.exe, scrcpy-server, *.dll)
```

تحقّق من الملف المضغوط قبل فكّه — يجب أن تطابق البصمة القيمة الرسمية في
[`SHA256SUMS.txt`](https://github.com/Genymobile/scrcpy/releases/download/v3.3.4/SHA256SUMS.txt):

```powershell
(Get-FileHash .\scrcpy-win64-v3.3.4.zip -Algorithm SHA256).Hash
# المتوقّع: D8A155B7C180B7CA4CDADD40712B8750B63F3AAB48CB5B8A2A39AC2D0D4C5D38
```

يتحقق التطبيق من بصمات SHA-256 لملفات المحرّك مقابل `Resources\engine.manifest`. إن استخدمت
نسخة scrcpy مختلفة، شغّل `update-engine-manifest.bat` ثم أعد البناء (انظر `Security\INTEGRITY.md`).

### الأوامر

نفّذ الأوامر من جذر المستودع:

| الأمر | الغرض |
|---|---|
| `dotnet build ScrcpyDeck.csproj -c Release -warnaserror` | بناء Release (كما في CI) |
| `dotnet test tests\PhoneLinkPro.Tests -c Release` | كل الاختبارات |
| `dotnet publish ScrcpyDeck.csproj -c Release -r win-x64 --self-contained true` | نشر + فحص سلامة المحرّك |
| `build-run.bat` | بناء وتشغيل نسخة التطوير (Debug) |
| `publish.bat` | نشر نظيف self-contained |
| `installer\build-installer.bat` | اختبار مفاتيح المثبّت ثم بناء `dist\PhoneLinkPro-Setup.exe` (يتطلب Inno Setup 6) |
| `powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1` | خط الإصدار الكامل |

> ملاحظة: بعض الاختبارات تشغّل التطبيق الفعلي في وضع اختبار ذاتي مخفي (`PLP_SELFTEST`) وتنشئ
> نوافذ WPF حقيقية؛ شغّلها في جلسة Windows عادية بسطح مكتب نشط.

للتفاصيل الكاملة (المعمارية، سلامة المحرّك، سلوك المثبّت، خط الإصدار) راجع
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## المساهمة والأمان

- المساهمات مرحّب بها — راجع [`CONTRIBUTING.md`](CONTRIBUTING.md).
- للإبلاغ عن ثغرة أمنية راجع [`SECURITY.md`](SECURITY.md) (لا تفتح Issue عاماً).

## الترخيص

- شيفرة PhoneLink Pro: [MIT](LICENSE).
- اسم «PhoneLink Pro» وشعار M+S يخصّان المطوّر ولا يشملهما ترخيص MIT.
- المكوّنات المرفقة (scrcpy، adb، FFmpeg، dav1d، SDL2، libusb، ومكتبات تشغيل .NET) لا يشملها
  ترخيص MIT وتخضع لتراخيصها الخاصة —
  راجع [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) ومجلد [`Licenses`](Licenses).

---

## سجل التغييرات

### غير مُصدَر
- المشروع أصبح مفتوح المصدر بترخيص MIT؛ تحديث نص السياسة إلى الإصدار 1.2 (يُطلب من المستخدم الموافقة مجدداً).

### v1.2.0
- **إعادة بناء كاملة للواجهة (2.0)**: صدفة تطبيق جديدة بتنقّل جانبي وتصميم موحّد، ولوحة أجهزة مُعاد تصميمها ببطاقة جهاز رئيسية وحالة حيّة دون أي إعادة رسم قديمة.
- **معالج إعداد الجهاز**: خطوات إرشادية لتفعيل وضع المطوّر وتصحيح USB والتصريح، مع كشف حالة كل خطوة تلقائياً.
- **الاتصال اللاسلكي الموجّه**: إقران واتصال Wi-Fi بخطوات مبسّطة، رسائل فشل صادقة، والتحقّق من حالة الجهاز الفعلية قبل عرضه كمتصل — دون حفظ أي رمز إقران في السجلات.
- **نقل ملفات موجّه وآمن**: إرسال وسحب مع مؤشر انشغال وعدّاد للملفات وإلغاء آمن، مع تأكيد نظام Windows قبل استبدال ملف محلي عند السحب.
- **صفحة إعدادات مُعاد بناؤها**: حفظ ذري للتفضيلات، نسخة تشخيصية محدودة عند تلف الملف، وتنظيف سجلات مقيّد النطاق.
- **نافذة «حول» ودعم**: معلومات المنتج والتواصل، وحزمة دعم قابلة للتصدير بعد تعقيم أي مسار شخصي.
- **صلابة وموثوقية**: فحص سلامة ملفات المحرّك عند الإقلاع، أوضاع تشغيل آلية معزولة تفشل بأمان عند أي قيمة غير معروفة (`AutomatedModeClassifier`)، وبوابات جودة حتمية للإصدار (اختبارات وحدات وتحققات تشغيلية شاملة قبل كل نشر).

### v1.1.0
- **مراقبة حيّة للأجهزة**: اكتشاف التوصيل/الفصل/التصريح تلقائياً كل ٤ ثوانٍ دون زر «تحديث» (`DeviceWatcher`).
- **زر «إيقاف البث»** داخل صفحة بث الشاشة، مع عدّاد الجلسات النشطة (تتبّع جلسات المحرّك في `ScrcpyService`).
- **إدارة التطبيقات**: أسماء ودّية للتطبيقات المشهورة + مونوجرام ملوّن لكل تطبيق، والبحث يشمل الاسم والحزمة.
- **إشعارات Toast** غير حاجبة بدل نوافذ MessageBox (تبقى نوافذ التأكيد للعمليات الخطرة فقط).
- **سجلّ أخطاء (Log)** في `%AppData%\PhoneLinkPro\logs\` (يحتفظ بآخر ٧ أيام) لتشخيص مشاكل المستخدمين.
- جلب تفاصيل الأجهزة (الموديل/الدقة/البطارية) **بالتوازي** بدل التسلسل — أسرع مع عدة أجهزة.

### v1.0.0
الإصدار الأول.

</div>
