using System;
using System.Diagnostics;

namespace ProGPU.Backend;

internal static class ProGpuBackendDiagnostics
{
    private const string EnvironmentVariable = "PROGPU_BACKEND_DIAGNOSTICS";

    public static bool IsEnabled
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void WriteLine(string message)
    {
        if (IsEnabled)
        {
            Console.WriteLine(message);
        }
    }

    // Called only after both local and device-domain pipeline cache misses.
    // Capture opt-in once so disabling diagnostics during a slow call cannot
    // leave an unmatched start. Disabled calls format nothing and read no clock.
    public static long? BeginPipelineCreation(string kind, string key, string entry, string? fragment = null)
    {
        if (!IsEnabled)
            return null;
        Console.WriteLine($"[PIPELINE] begin kind={kind}; key={key}; entry={entry}; fragment={fragment}");
        return Stopwatch.GetTimestamp();
    }

    public static void EndPipelineCreation(long? started, string kind, string key, bool returned, bool hasHandle)
    {
        if (!started.HasValue)
            return;
        double elapsed = Stopwatch.GetElapsedTime(started.Value).TotalMilliseconds;
        // A returned handle is not shader validation or GPU completion evidence.
        Console.WriteLine(FormattableString.Invariant(
            $"[PIPELINE] end kind={kind}; key={key}; returned={returned}; hasHandle={hasHandle}; wallMs={elapsed:0.000}"));
    }
}
