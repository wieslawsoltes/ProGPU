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

    ValueTask<ProcessDetails?> GetProcessDetailsAsync(
        int processId,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessActionResult> TerminateProcessAsync(
        int processId,
        DateTimeOffset expectedStartTime,
        ProcessTerminationMode mode,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessReportResult> SampleProcessAsync(
        int processId,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessActionResult> RunDiagnosticAsync(
        ActivityDiagnosticKind kind,
        int? processId = null,
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
    int ProcessGroupId,
    string Name,
    string User,
    DateTimeOffset? StartTime,
    double CpuPercent,
    TimeSpan CpuTime,
    int ThreadCount,
    long MemoryBytes,
    long VirtualMemoryBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    long NetworkReceivedBytes,
    long NetworkSentBytes,
    long NetworkReceivedPackets,
    long NetworkSentPackets,
    double EnergyImpact,
    long IdleWakeUps,
    int PortCount,
    double? GpuPercent,
    TimeSpan? GpuTime,
    bool AppNap,
    bool PreventingSleep,
    string Kind,
    string ExecutablePath,
    bool IsApplication);

public sealed record SystemSnapshot(
    double UserCpuPercent,
    double SystemCpuPercent,
    double IdleCpuPercent,
    long PhysicalMemoryBytes,
    long UsedMemoryBytes,
    long CachedMemoryBytes,
    long AppMemoryBytes,
    long WiredMemoryBytes,
    long CompressedMemoryBytes,
    long SwapUsedBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    long DiskReadOperations,
    long DiskWriteOperations,
    long NetworkReceivedBytes,
    long NetworkSentBytes,
    long NetworkReceivedPackets,
    long NetworkSentPackets,
    int ProcessCount,
    int ThreadCount,
    BatterySnapshot Battery);

public sealed record BatterySnapshot(
    bool IsPresent,
    double ChargePercent,
    bool IsCharging,
    string PowerSource,
    string TimeRemaining);

public sealed record ProcessDetails(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string User,
    string ExecutablePath,
    string CommandLine,
    DateTimeOffset? StartTime,
    ProcessSnapshot Snapshot,
    IReadOnlyList<string> OpenFilesAndPorts);

public enum ProcessTerminationMode
{
    Quit,
    ForceQuit
}

public sealed record ProcessActionResult(bool Succeeded, string Message);

public sealed record ProcessReportResult(
    bool Succeeded,
    string Message,
    string Report);

public enum ActivityDiagnosticKind
{
    Spindump,
    SystemDiagnostics
}
