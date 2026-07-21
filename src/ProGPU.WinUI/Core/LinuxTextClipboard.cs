using System;
using System.Diagnostics;

namespace Microsoft.UI.Xaml;

/// <summary>Explicit Linux plain-text fallback for Wayland and X11 command-line providers.</summary>
internal static class LinuxTextClipboard
{
    public static bool TrySet(string text)
    {
        if (!OperatingSystem.IsLinux()) return false;
        return TryWrite("wl-copy", Array.Empty<string>(), text) ||
            TryWrite("xclip", ["-selection", "clipboard"], text) ||
            TryWrite("xsel", ["--clipboard", "--input"], text);
    }

    public static bool TryGet(out string text)
    {
        text = string.Empty;
        if (!OperatingSystem.IsLinux()) return false;
        return TryRead("wl-paste", ["--no-newline"], out text) ||
            TryRead("xclip", ["-selection", "clipboard", "-out"], out text) ||
            TryRead("xsel", ["--clipboard", "--output"], out text);
    }

    private static bool TryWrite(string fileName, string[] arguments, string text)
    {
        try
        {
            using Process? process = Start(fileName, arguments, redirectInput: true);
            if (process is null) return false;
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryRead(string fileName, string[] arguments, out string text)
    {
        text = string.Empty;
        try
        {
            using Process? process = Start(fileName, arguments, redirectInput: false);
            if (process is null) return false;
            string value = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return false;
            text = value;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Process? Start(string fileName, string[] arguments, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = !redirectInput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo);
    }
}
