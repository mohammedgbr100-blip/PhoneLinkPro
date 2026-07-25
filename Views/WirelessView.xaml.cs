using System.Windows;
using System.Windows.Controls;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class WirelessView : UserControl, IRefreshable
{
    public WirelessView() => InitializeComponent();

    public void OnShown() { }

    private void Log(string msg)
    {
        LogBox.AppendText("\n" + msg);
        LogBox.ScrollToEnd();
    }

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        AutoBtn.IsEnabled = false;
        try
        {
            LogBox.Text = "بدء التحويل التلقائي...";
            var discovery = await AdbService.DiscoverDevicesAsync();
            if (!discovery.IsSuccess)
            {
                Log("✗ تعذّر قراءة قائمة الأجهزة. أعد المحاولة بعد قليل.");
                return;
            }
            var usb = discovery.Devices.FirstOrDefault(d => d.IsAuthorized && !d.IsWireless);
            if (usb == null)
            {
                Log("✗ لم يُعثر على جهاز USB متصل ومصرّح. وصّل الكابل ووافق على تصحيح USB.");
                return;
            }

            Log($"• الجهاز: {usb.Serial}");
            Log("• جارٍ قراءة عنوان IP للهاتف...");
            var ip = await AdbService.GetWifiIpAsync(usb.Serial);
            if (string.IsNullOrEmpty(ip))
            {
                Log("✗ تعذّر قراءة عنوان IP. تأكد أن Wi-Fi مفعّل على الهاتف.");
                return;
            }
            Log($"• عنوان الهاتف: {ip}");

            Log("• تفعيل الوضع اللاسلكي على الجهاز...");
            var tcp = await AdbService.TcpipAsync(usb.Serial, 5555);
            if (!tcp.Success && !(tcp.Outcome == ProcessOutcome.Exited && tcp.StdOut.Contains("restarting")))
                Log("  ملاحظة: " + tcp.Output);

            await Task.Delay(1500);

            var target = $"{ip}:5555";
            Log($"• الاتصال بـ {target} ...");
            var con = await AdbService.ConnectAsync(target);
            Log("  " + con.Output);

            // adb connect exits 0 even when it fails, so its own stdout decides — but only
            // when the process actually ran. Any other outcome is a failure by definition.
            if (con.Outcome == ProcessOutcome.Exited && con.StdOut.Contains("connected"))
            {
                Log("✓ تم الاتصال لاسلكياً! يمكنك الآن نزع الكابل.");
                IpBox.Text = ip;
                await SelectWirelessDevice(target);
            }
            else
            {
                Log("✗ فشل الاتصال. جرّب الاتصال اليدوي بالعنوان أعلاه.");
                IpBox.Text = ip;
            }
        }
        finally { AutoBtn.IsEnabled = true; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var ip = IpBox.Text.Trim();
        var port = string.IsNullOrWhiteSpace(PortBox.Text) ? "5555" : PortBox.Text.Trim();
        if (string.IsNullOrEmpty(ip)) { Log("✗ أدخل عنوان IP."); return; }

        var target = ip.Contains(':') ? ip : $"{ip}:{port}";
        Log($"• الاتصال بـ {target} ...");
        var res = await AdbService.ConnectAsync(target);
        Log("  " + res.Output);
        if (res.Outcome == ProcessOutcome.Exited && res.StdOut.Contains("connected"))
            await SelectWirelessDevice(target);
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var ip = IpBox.Text.Trim();
        var port = string.IsNullOrWhiteSpace(PortBox.Text) ? "5555" : PortBox.Text.Trim();
        var target = string.IsNullOrEmpty(ip) ? "" : (ip.Contains(':') ? ip : $"{ip}:{port}");
        var res = await AdbService.DisconnectAsync(target);
        Log("• قطع الاتصال: " + res.Output);
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var ip = PairIpBox.Text.Trim();
        var code = PairCodeBox.Text.Trim();
        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(code))
        {
            Log("✗ أدخل عنوان الإقران والرمز.");
            return;
        }
        // The pairing code is a secret: it is never written to the log or the UI.
        Log($"• إقران مع {ip} ...");
        var res = await AdbService.PairAsync(ip, code);
        Log("  " + res.Output);
        if (res.Outcome == ProcessOutcome.Exited && res.StdOut.Contains("Successfully"))
            Log("✓ تم الإقران. الآن استخدم «اتصال يدوي» بعنوان الاتصال (المنفذ يختلف عن منفذ الإقران).");
    }

    private async Task SelectWirelessDevice(string target)
    {
        var discovery = await AdbService.DiscoverDevicesAsync();
        var dev = discovery.Devices.FirstOrDefault(d => d.Serial == target);
        if (dev != null)
        {
            await AdbService.EnrichAsync(dev);
            AppState.SetCurrent(dev);
        }
    }
}
