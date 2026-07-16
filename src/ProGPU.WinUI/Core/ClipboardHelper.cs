using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Diagnostics;
using System.IO;

namespace Microsoft.UI.Xaml;

public static class ClipboardHelper
{
    /// <summary>Optional synchronous platform seam used by hosts without process access.</summary>
    public static Action<string>? PlatformSetText { get; set; }

    /// <summary>Optional synchronous platform seam used by hosts without process access.</summary>
    public static Func<string>? PlatformGetText { get; set; }

    public static void SetText(string text)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (PlatformSetText is { } platformSetText)
        {
            platformSetText(text);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pbcopy",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start pbcopy process.");
            }

            using (var writer = process.StandardInput)
            {
                writer.Write(text);
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"pbcopy exited with code {process.ExitCode}. Error: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClipboardHelper] Error setting clipboard text: {ex.Message}");
            throw;
        }
    }

    public static string GetText()
    {
        if (PlatformGetText is { } platformGetText) return platformGetText() ?? string.Empty;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pbpaste",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start pbpaste process.");
            }

            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"pbpaste exited with code {process.ExitCode}. Error: {error}");
            }

            return text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ClipboardHelper] Error getting clipboard text: {ex.Message}");
            return string.Empty;
        }
    }
}
