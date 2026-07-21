using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Microsoft.UI.Xaml;

/// <summary>Win32 multi-format clipboard transport for Unicode text, RTF, and CF_HTML.</summary>
internal static class WindowsRichClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const string RtfFormatName = "Rich Text Format";
    private const string HtmlFormatName = "HTML Format";

    public static bool TrySet(RichClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!OperatingSystem.IsWindows() || !TryOpenClipboard()) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            uint rtfFormat = RegisterClipboardFormat(RtfFormatName);
            uint htmlFormat = RegisterClipboardFormat(HtmlFormatName);
            bool plainSet = TrySetData(CfUnicodeText, NullTerminate(Encoding.Unicode.GetBytes(payload.PlainText), 2));
            bool rtfSet = rtfFormat != 0 && TrySetData(rtfFormat, NullTerminate(Encoding.ASCII.GetBytes(payload.Rtf), 1));
            bool htmlSet = htmlFormat != 0 && TrySetData(htmlFormat, BuildCfHtml(payload.Html));
            return plainSet && rtfSet && htmlSet;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsWindows() || !TryOpenClipboard()) return false;
        try
        {
            return EmptyClipboard() &&
                TrySetData(CfUnicodeText, NullTerminate(Encoding.Unicode.GetBytes(text), 2));
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TryGet(out RichClipboardPayload? payload)
    {
        payload = null;
        if (!OperatingSystem.IsWindows() || !TryOpenClipboard()) return false;
        try
        {
            uint rtfFormat = RegisterClipboardFormat(RtfFormatName);
            uint htmlFormat = RegisterClipboardFormat(HtmlFormatName);
            string rtf = rtfFormat == 0 ? string.Empty : ReadString(rtfFormat, Encoding.ASCII);
            string html = htmlFormat == 0 ? string.Empty : DecodeCfHtml(ReadBytes(htmlFormat));
            if (rtf.Length == 0 && html.Length == 0) return false;
            string plainText = ReadUnicodeText();
            payload = new RichClipboardPayload(plainText, rtf, html);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TryGetText(out string text)
    {
        text = string.Empty;
        if (!OperatingSystem.IsWindows() || !TryOpenClipboard()) return false;
        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText)) return false;
            text = ReadUnicodeText();
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    private static bool TrySetData(uint format, byte[] bytes)
    {
        nint memory = GlobalAlloc(GmemMoveable, checked((nuint)bytes.Length));
        if (memory == 0) return false;
        nint destination = GlobalLock(memory);
        if (destination == 0)
        {
            GlobalFree(memory);
            return false;
        }
        Marshal.Copy(bytes, 0, destination, bytes.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) != 0) return true;
        GlobalFree(memory);
        return false;
    }

    private static string ReadUnicodeText()
    {
        byte[] bytes = ReadBytes(CfUnicodeText);
        int length = bytes.Length;
        while (length >= 2 && bytes[length - 1] == 0 && bytes[length - 2] == 0) length -= 2;
        return length == 0 ? string.Empty : Encoding.Unicode.GetString(bytes, 0, length);
    }

    private static string ReadString(uint format, Encoding encoding)
    {
        byte[] bytes = ReadBytes(format);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return length == 0 ? string.Empty : encoding.GetString(bytes, 0, length);
    }

    private static byte[] ReadBytes(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return Array.Empty<byte>();
        nint memory = GetClipboardData(format);
        if (memory == 0) return Array.Empty<byte>();
        nuint size = GlobalSize(memory);
        if (size == 0 || size > int.MaxValue) return Array.Empty<byte>();
        nint source = GlobalLock(memory);
        if (source == 0) return Array.Empty<byte>();
        try
        {
            var result = new byte[(int)size];
            Marshal.Copy(source, result, 0, result.Length);
            return result;
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    internal static byte[] BuildCfHtml(string fragment)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string template = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        string emptyHeader = string.Format(CultureInfo.InvariantCulture, template, 0, 0, 0, 0);
        int startHtml = Encoding.ASCII.GetByteCount(emptyHeader);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        string header = string.Format(CultureInfo.InvariantCulture, template, startHtml, endHtml, startFragment, endFragment);
        return NullTerminate(Encoding.UTF8.GetBytes(header + prefix + fragment + suffix), 1);
    }

    internal static string DecodeCfHtml(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        string value = Encoding.UTF8.GetString(bytes, 0, length);
        if (TryReadOffset(value, "StartFragment:", out int start) &&
            TryReadOffset(value, "EndFragment:", out int end) &&
            start >= 0 && end >= start && end <= length)
            return Encoding.UTF8.GetString(bytes, start, end - start);
        return value;
    }

    private static bool TryReadOffset(string value, string name, out int result)
    {
        int start = value.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            result = 0;
            return false;
        }
        start += name.Length;
        int end = start;
        while (end < value.Length && char.IsDigit(value[end])) end++;
        return int.TryParse(value.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static byte[] NullTerminate(byte[] bytes, int count)
    {
        var result = new byte[bytes.Length + count];
        bytes.CopyTo(result, 0);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint memory);
}
