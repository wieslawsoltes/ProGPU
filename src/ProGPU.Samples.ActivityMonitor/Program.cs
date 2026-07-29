using Microsoft.UI.Xaml;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace ProGPU.Samples.ActivityMonitor;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--snapshot", StringComparer.Ordinal))
        {
            await PrintSnapshotAsync();
            return;
        }

        GlfwWindowing.Use();
        GlfwInput.RegisterPlatform();

        AppBuilder<App>
            .Configure()
            .WithTitle("Activity Monitor")
            .WithSize(1440, 900)
            .Build()
            .Run(args);
    }

    private static async Task PrintSnapshotAsync()
    {
        await using IActivityMonitorDataSource source = ActivityMonitorDataSourceFactory.Create();
        _ = await source.CaptureAsync(new ActivityCaptureOptions());
        await Task.Delay(250);
        ActivitySnapshot snapshot = await source.CaptureAsync(new ActivityCaptureOptions());
        Console.WriteLine(
            $"{source.PlatformName}: {snapshot.Processes.Count} processes, " +
            $"{snapshot.System.ThreadCount} threads, " +
            $"CPU {snapshot.System.UserCpuPercent + snapshot.System.SystemCpuPercent:F1}%, " +
            $"memory {snapshot.System.UsedMemoryBytes:N0}/{snapshot.System.PhysicalMemoryBytes:N0} bytes");

        foreach (ProcessSnapshot process in snapshot.Processes
                     .OrderByDescending(item => item.CpuPercent)
                     .Take(5))
        {
            Console.WriteLine(
                $"{process.ProcessId,7} {process.CpuPercent,7:F1}% " +
                $"{process.MemoryBytes,14:N0} {process.User,-18} {process.Name}");
        }
    }
}
