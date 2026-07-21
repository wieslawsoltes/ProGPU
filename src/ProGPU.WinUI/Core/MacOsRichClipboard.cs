using System;
using System.Diagnostics;
using System.Text;

namespace Microsoft.UI.Xaml;

/// <summary>
/// Typed macOS pasteboard adapter. JXA is used only at the explicit clipboard boundary;
/// editor layout, rendering, and input hot paths remain platform-neutral and reflection-free.
/// </summary>
internal static class MacOsRichClipboard
{
    private const string SetScript = """
        ObjC.import('AppKit');
        function run() {
          const inputData = $.NSFileHandle.fileHandleWithStandardInput.readDataToEndOfFile;
          const input = ObjC.unwrap($.NSString.alloc.initWithDataEncoding(inputData, $.NSUTF8StringEncoding));
          const lines = input.split('\n');
          const decode = value => $.NSData.alloc.initWithBase64EncodedStringOptions($(value || ''), 0);
          const pasteboard = $.NSPasteboard.generalPasteboard;
          pasteboard.clearContents;
          pasteboard.setDataForType(decode(lines[0]), $.NSPasteboardTypeString);
          pasteboard.setDataForType(decode(lines[1]), $.NSPasteboardTypeRTF);
          pasteboard.setDataForType(decode(lines[2]), $.NSPasteboardTypeHTML);
          return 'ok';
        }
        """;

    private const string GetScript = """
        ObjC.import('AppKit');
        function run() {
          const pasteboard = $.NSPasteboard.generalPasteboard;
          const encode = type => {
            const data = pasteboard.dataForType(type);
            return data ? ObjC.unwrap(data.base64EncodedStringWithOptions(0)) : '';
          };
          return [encode($.NSPasteboardTypeString), encode($.NSPasteboardTypeRTF), encode($.NSPasteboardTypeHTML)].join('\n');
        }
        """;

    public static bool TrySet(RichClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!OperatingSystem.IsMacOS()) return false;
        string input = string.Join('\n',
            Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.PlainText)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Rtf)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Html)));
        return TryRun(SetScript, input, out _);
    }

    public static bool TryGet(out RichClipboardPayload? payload)
    {
        payload = null;
        if (!OperatingSystem.IsMacOS() || !TryRun(GetScript, string.Empty, out string output)) return false;
        string[] values = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (values.Length < 2) return false;
        try
        {
            string plainText = Decode(values[0]);
            string rtf = Decode(values[1]);
            string html = values.Length > 2 ? Decode(values[2]) : string.Empty;
            if (rtf.Length == 0 && html.Length == 0) return false;
            payload = new RichClipboardPayload(plainText, rtf, html);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Decode(string value) => value.Length == 0
        ? string.Empty
        : Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static bool TryRun(string script, string input, out string output)
    {
        output = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("JavaScript");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);
            using Process? process = Process.Start(startInfo);
            if (process is null) return false;
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
