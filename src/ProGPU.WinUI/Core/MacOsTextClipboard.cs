using System;
using System.Diagnostics;

namespace Microsoft.UI.Xaml;

/// <summary>Explicit macOS plain-text adapter backed by the platform pasteboard tools.</summary>
internal static class MacOsTextClipboard
{
    public static bool TrySet(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            using Process? process = Start("pbcopy", redirectInput: true);
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

    public static bool TryGet(out string text)
    {
        text = string.Empty;
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            using Process? process = Start("pbpaste", redirectInput: false);
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

    private static Process? Start(string fileName, bool redirectInput)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = !redirectInput,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
    }
}
