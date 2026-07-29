using System.Globalization;

namespace ProGPU.Samples.ActivityMonitor.Presentation;

internal static class ActivityMetricFormatter
{
    public static string Bytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] suffixes = ["bytes", "KB", "MB", "GB", "TB", "PB", "EB"];
        int suffix = 0;
        while (value >= 1000 && suffix < suffixes.Length - 1)
        {
            value /= 1000;
            suffix++;
        }

        int decimals = suffix == 0 ? 0 : value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return $"{value.ToString($"N{decimals}", CultureInfo.CurrentCulture)} {suffixes[suffix]}";
    }

    public static string Percent(double value) =>
        $"{Math.Max(0, value).ToString("N1", CultureInfo.CurrentCulture)}";

    public static string Count(long value) =>
        Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture);

    public static string Duration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }
        return $"{value.Minutes}:{value.Seconds:00}.{value.Milliseconds / 10:00}";
    }
}
