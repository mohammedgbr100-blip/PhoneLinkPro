# Contributing to PhoneLink Pro

<div dir="rtl">

شكراً لاهتمامك بالمساهمة! المساهمات بالعربية أو الإنجليزية مرحّب بها.

## قبل أن تبدأ

1. افتح **Issue** يصف المشكلة أو الميزة قبل أي تغيير كبير، لنتفق على الاتجاه.
2. جهّز بيئة البناء كما في قسم «البناء من المصدر» في [`README.md`](README.md)
   (بما في ذلك مجلد `..\scrcpy`).

## سير العمل

1. اعمل Fork ثم فرعاً جديداً: `git checkout -b fix/short-description`.
2. اكتب اختباراً يغطّي التغيير في `tests/PhoneLinkPro.Tests` كلما أمكن.
3. تأكّد أن البناء والاختبارات تمرّ بلا تحذيرات:

   ```bat
   dotnet build -c Release -warnaserror
   dotnet test tests\PhoneLinkPro.Tests
   ```

4. استخدم رسائل commit بأسلوب [Conventional Commits](https://www.conventionalcommits.org/)
   مثل `fix(wireless): ...` أو `feat(apps): ...`.
5. افتح Pull Request يشرح **ماذا** تغيّر و**لماذا**، مع لقطة شاشة لأي تغيير في الواجهة.

## قواعد مهمة

- **الخصوصية أولاً:** لا تُسجّل أبداً أرقاماً تسلسلية كاملة، أو رموز إقران، أو مسارات شخصية —
  استخدم `LogSanitizer`. التطبيق لا يتصل بالإنترنت ويجب أن يبقى كذلك.
- **ملفات المحرّك:** لا تُودِع أي ملفات من scrcpy/adb في المستودع. إن غيّرت نسخة المحرّك، حدّث
  `Resources\engine.manifest` عبر `update-engine-manifest.bat` ووضّح ذلك في الـ PR.
- **نصوص الواجهة** بالعربية الواضحة وغير التقنية.
- لا أسرار ولا شهادات توقيع في المستودع (انظر `Security\SIGNING.md`).

بمساهمتك فإنك توافق على ترخيص مساهمتك بموجب [ترخيص MIT](LICENSE) الخاص بالمشروع.

</div>
