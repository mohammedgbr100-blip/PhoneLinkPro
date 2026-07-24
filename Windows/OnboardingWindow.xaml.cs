using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScrcpyDeck.Windows;

public partial class OnboardingWindow : Window
{
    private record Step(string Title, string Desc, string[] Bullets);

    private readonly Step[] _steps =
    {
        new("مرحباً بك في PhoneLink Pro",
            "هذا البرنامج يتيح لك التحكم بهاتف الأندرويد وعرض شاشته على حاسوبك بسهولة تامة — بدون أي خبرة تقنية.",
            new[] { "عرض شاشة هاتفك على الحاسوب", "التحكم بالماوس ولوحة المفاتيح", "نقل الملفات وإدارة التطبيقات" }),
        new("جهّز هاتفك (خطوة لمرة واحدة)",
            "نحتاج تفعيل «وضع المطوّر» على هاتفك أولاً — الأمر سهل جداً:",
            new[] { "افتح: الإعدادات ← حول الهاتف", "اضغط على «رقم الإصدار» (Build number) سبع مرات متتالية", "ستظهر رسالة: «أصبحت مطوّراً الآن»" }),
        new("فعّل «تصحيح USB»",
            "بعد تفعيل وضع المطوّر، فعّل خيار التصحيح:",
            new[] { "افتح: الإعدادات ← خيارات المطوّرين", "فعّل خيار «تصحيح USB» (USB debugging)", "وافق على أي رسالة تأكيد تظهر" }),
        new("وصّل الهاتف بالحاسوب",
            "استخدم كابل USB (يُفضّل الأصلي):",
            new[] { "صِل الكابل بين الهاتف والحاسوب", "ستظهر على الهاتف رسالة: «السماح بتصحيح USB؟»", "اضغط «سماح» / «موافق»", "فعّل «السماح دائماً» لتجنّب تكرار السؤال" }),
        new("ابدأ العرض!",
            "كل شيء جاهز الآن:",
            new[] { "افتح تبويب «لوحة الأجهزة»", "اضغط زر «تحديث» ليظهر هاتفك", "اضغط «تحديد وبثّ» — وستظهر شاشة هاتفك على الحاسوب!" }),
        new("الاتصال اللاسلكي (اختياري)",
            "تريد التحكم بدون كابل؟",
            new[] { "افتح تبويب «الاتصال اللاسلكي»", "اضغط «تفعيل الاتصال اللاسلكي» مرة واحدة", "يمكنك بعدها نزع الكابل والتحكم عبر Wi-Fi" }),
        new("أنت جاهز 🎉",
            "يمكنك فتح هذا الشرح في أي وقت من زر «؟» في أعلى النافذة.",
            new[] { "جرّب مستويات الجودة المختلفة في «بث الشاشة»", "استخدم «نقل الملفات» بالسحب والإفلات", "لأي مساعدة، تواصل مع المطوّر ENG. Mohammed Sattar" }),
    };

    private int _i;

    public OnboardingWindow()
    {
        InitializeComponent();
        BuildDots();
        Show(0);
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        for (int k = 0; k < _steps.Length; k++)
        {
            Dots.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(4, 0, 4, 0),
                Fill = (Brush)FindResource("Muted")
            });
        }
    }

    private void Show(int index)
    {
        _i = Math.Clamp(index, 0, _steps.Length - 1);
        var s = _steps[_i];
        StepNum.Text = (_i + 1).ToString();
        StepTitle.Text = s.Title;
        StepDesc.Text = s.Desc;
        StepBullets.ItemsSource = s.Bullets;

        BackBtn.Visibility = _i == 0 ? Visibility.Hidden : Visibility.Visible;
        NextBtn.Content = _i == _steps.Length - 1 ? "ابدأ الآن" : "التالي";

        for (int k = 0; k < Dots.Children.Count; k++)
            ((Ellipse)Dots.Children[k]).Fill = (Brush)FindResource(k == _i ? "Adb" : "Muted");
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_i == _steps.Length - 1) { Close(); return; }
        Show(_i + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(_i - 1);
    private void Skip_Click(object sender, RoutedEventArgs e) => Close();
}
