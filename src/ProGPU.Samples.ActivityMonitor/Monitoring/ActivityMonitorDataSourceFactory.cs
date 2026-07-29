namespace ProGPU.Samples.ActivityMonitor.Monitoring;

public static class ActivityMonitorDataSourceFactory
{
    public static IActivityMonitorDataSource Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsActivityMonitorDataSource();
        }

        throw new PlatformNotSupportedException(
            "The Activity Monitor sample currently provides live telemetry on macOS. " +
            "Windows and Linux providers can be added through IActivityMonitorDataSource.");
    }
}
