namespace ProGPU.Samples.ActivityMonitor.Monitoring;

/// <summary>
/// Platform-neutral source for point-in-time Activity Monitor data.
/// Implementations own sampling and delta calculations; callers own returned snapshots.
/// </summary>
public interface IActivityMonitorDataSource : IAsyncDisposable
{
    string PlatformName { get; }

    ValueTask<ActivitySnapshot> CaptureAsync(
        ActivityCaptureOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record ActivityCaptureOptions(
    bool IncludeProcesses = true,
    bool IncludeSystemHistory = true);

public sealed record ActivitySnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessSnapshot> Processes,
    SystemSnapshot System);

public sealed record ProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string User,
    double CpuPercent,
    TimeSpan CpuTime,
    int ThreadCount,
    long MemoryBytes,
    long VirtualMemoryBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes,
    double EnergyImpact,
    bool IsApplication);

public sealed record SystemSnapshot(
    double UserCpuPercent,
    double SystemCpuPercent,
    double IdleCpuPercent,
    long PhysicalMemoryBytes,
    long UsedMemoryBytes,
    long CachedMemoryBytes,
    long SwapUsedBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes,
    int ProcessCount,
    int ThreadCount);
