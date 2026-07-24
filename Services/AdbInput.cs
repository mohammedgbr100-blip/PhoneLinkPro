using System.Globalization;
using System.Net;

namespace ScrcpyDeck.Services;

/// <summary>
/// Validation for the values that reach adb.
/// <para>
/// Arguments are delivered through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
/// so injection into the local Windows command line is already structurally impossible.
/// These rules are defence in depth: they stop a value from being read as an adb OPTION
/// (anything starting with '-'), keep control characters out of the protocol, and guard the
/// one place adb genuinely spawns a remote shell.
/// </para>
/// Rules stay deliberately permissive so real-world Samsung, Xiaomi, emulator, TCP/IP and
/// IPv6 serials keep working.
/// </summary>
public static class AdbInput
{
    // ---------------------------------------------------------------- serial

    /// <summary>
    /// Validates a device serial. Accepts USB serials, "emulator-5554", "IPv4:port" and
    /// bracketed IPv6 endpoints such as "[fe80::1%wlan0]:5555".
    /// </summary>
    public static bool TryValidateSerial(string? serial, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(serial))
        {
            error = "لم يتم تحديد جهاز.";
            return false;
        }

        if (serial.Length > 256)
        {
            error = "معرّف الجهاز طويل بشكل غير معتاد.";
            return false;
        }

        // A leading '-' would be parsed by adb as an option rather than a serial.
        if (serial[0] == '-')
        {
            error = "معرّف الجهاز غير صالح.";
            return false;
        }

        foreach (var c in serial)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                error = "معرّف الجهاز يحتوي على محارف غير صالحة.";
                return false;
            }
            // Letters/digits plus the punctuation that legitimately appears in serials.
            // Everything else — quotes, shell operators, backslashes — is rejected.
            var ok = char.IsLetterOrDigit(c) || c is '.' or ':' or '_' or '-' or '@'
                     or '%' or '[' or ']';
            if (!ok)
            {
                error = "معرّف الجهاز يحتوي على محارف غير صالحة.";
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- package name

    /// <summary>
    /// Validates an Android package id: dot-separated segments, each starting with a letter
    /// or underscore and continuing with letters, digits or underscores. Unicode letters are
    /// allowed because Java identifiers permit them and some OEM builds ship such ids.
    /// </summary>
    public static bool TryValidatePackage(string? package, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(package))
        {
            error = "اسم الحزمة فارغ.";
            return false;
        }

        if (package.Length > 512)
        {
            error = "اسم الحزمة طويل بشكل غير معتاد.";
            return false;
        }

        if (package[0] == '-')
        {
            error = "اسم الحزمة غير صالح.";
            return false;
        }

        // Reject leading/trailing separators outright so Split cannot hide empty segments.
        if (package[0] == '.' || package[^1] == '.')
        {
            error = "اسم الحزمة غير صالح.";
            return false;
        }

        foreach (var segment in package.Split('.'))
        {
            if (segment.Length == 0)
            {
                error = "اسم الحزمة يحتوي على مقطع فارغ.";
                return false;
            }

            if (!char.IsLetter(segment[0]) && segment[0] != '_')
            {
                error = "اسم الحزمة غير صالح.";
                return false;
            }

            foreach (var c in segment)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    error = "اسم الحزمة يحتوي على محارف غير صالحة.";
                    return false;
                }
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- endpoints

    /// <summary>Validates a port in the usable range 1-65535.</summary>
    public static bool TryValidatePort(int port, out string error)
    {
        error = "";
        if (port is < 1 or > 65535)
        {
            error = "رقم المنفذ يجب أن يكون بين 1 و 65535.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates an "address" or "address:port" endpoint using <see cref="IPAddress.TryParse"/>.
    /// Returns the normalized endpoint (bracketing IPv6 when a port is present).
    /// </summary>
    public static bool TryValidateEndpoint(string? endpoint, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            error = "أدخل عنوان IP.";
            return false;
        }

        var text = endpoint.Trim();

        // Bracketed IPv6, optionally with a port: [::1] or [::1]:5555
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                error = "عنوان IP غير صالح.";
                return false;
            }

            var host = text[1..close];
            if (!IPAddress.TryParse(host, out _))
            {
                error = "عنوان IP غير صالح.";
                return false;
            }

            var rest = text[(close + 1)..];
            if (rest.Length == 0) { normalized = $"[{host}]"; return true; }
            if (rest[0] != ':')
            {
                error = "عنوان IP غير صالح.";
                return false;
            }
            if (!TryPort(rest[1..], out var v6Port, out error)) return false;

            normalized = $"[{host}]:{v6Port}";
            return true;
        }

        // Bare address with no port (IPv4 or unbracketed IPv6).
        if (IPAddress.TryParse(text, out var bare))
        {
            normalized = bare.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{bare}]"
                : bare.ToString();
            return true;
        }

        // IPv4:port — the only unbracketed form that may carry a port.
        var lastColon = text.LastIndexOf(':');
        if (lastColon > 0)
        {
            var host = text[..lastColon];
            if (IPAddress.TryParse(host, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (!TryPort(text[(lastColon + 1)..], out var port, out error)) return false;
                normalized = $"{ip}:{port}";
                return true;
            }
        }

        error = "عنوان IP غير صالح.";
        return false;
    }

    private static bool TryPort(string text, out int port, out string error)
    {
        port = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            error = "رقم المنفذ غير صالح.";
            return false;
        }
        return TryValidatePort(port, out error);
    }

    // ---------------------------------------------------------------- pairing code

    /// <summary>
    /// Validates a wireless-debugging pairing code. Android shows a six-digit code.
    /// The code itself is a shared secret and is never logged.
    /// </summary>
    public static bool TryValidatePairingCode(string? code, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "أدخل رمز الإقران.";
            return false;
        }

        var trimmed = code.Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsAsciiDigit))
        {
            error = "رمز الإقران يجب أن يتكوّن من ٦ أرقام.";
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- android shell quoting

    /// <summary>
    /// Quotes a value for the ANDROID-side shell.
    /// <para>
    /// `adb shell &lt;args&gt;` joins its arguments and hands the result to the device's sh, so
    /// for that one path a remote-shell escape is unavoidable. Single quotes are the only
    /// construct sh treats fully literally: everything inside is inert, and an embedded
    /// single quote is emitted as the standard '\'' break-out sequence.
    /// </para>
    /// This protects the REMOTE shell only; the local command line is already safe because
    /// arguments go through ArgumentList.
    /// </summary>
    public static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
