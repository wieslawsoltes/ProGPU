using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ProGPU.Samples.ActivityMonitor.Monitoring;

/// <summary>
/// macOS implementation backed by typed libproc/Mach calls and bounded system utilities.
/// A capture is O(P) for P visible processes and keeps only O(P) delta state.
/// </summary>
internal sealed class MacOsActivityMonitorDataSource : IActivityMonitorDataSource
{
    private const int RusageInfoVersion2 = 2;
    private const int HostCpuLoadInfo = 3;
    private const int SignalTerminate = 15;
    private const int SignalKill = 9;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<int, NetworkCounters> _networkByProcess = new();
    private readonly Dictionary<int, PreviousProcessSample> _previousProcesses = new();
    private readonly Task _networkTask;
    private CpuTicks? _previousCpuTicks;
    private DateTimeOffset? _previousCaptureTime;
    private bool _disposed;

    public MacOsActivityMonitorDataSource()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("This data source requires macOS.");
        }

        _networkTask = Task.Run(() => ObserveNetworkAsync(_disposeCts.Token));
    }

    public string PlatformName => "macOS";

    public async ValueTask<ActivitySnapshot> CaptureAsync(
        ActivityCaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => CaptureCore(options, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask<ProcessDetails?> GetProcessDetailsAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActivitySnapshot snapshot = await CaptureAsync(
            new ActivityCaptureOptions(),
            cancellationToken).ConfigureAwait(false);
        ProcessSnapshot? process = snapshot.Processes.FirstOrDefault(item => item.ProcessId == processId);
        if (process is null)
        {
            return null;
        }

        string commandLine = await RunCommandAsync(
            "/bin/ps",
            ["-p", processId.ToString(CultureInfo.InvariantCulture), "-o", "command="],
            cancellationToken).ConfigureAwait(false);
        string openFilesOutput = await RunCommandAsync(
            "/usr/sbin/lsof",
            [
                "-n",
                "-P",
                "-p",
                processId.ToString(CultureInfo.InvariantCulture)
            ],
            cancellationToken).ConfigureAwait(false);
        string[] openFilesAndPorts = openFilesOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Take(200)
            .ToArray();
        return new ProcessDetails(
            process.ProcessId,
            process.ParentProcessId,
            process.Name,
            process.User,
            process.ExecutablePath,
            commandLine.Trim(),
            process.StartTime,
            process,
            openFilesAndPorts);
    }

    public ValueTask<ProcessActionResult> TerminateProcessAsync(
        int processId,
        DateTimeOffset expectedStartTime,
        ProcessTerminationMode mode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (processId <= 0)
        {
            return ValueTask.FromResult(new ProcessActionResult(false, "Select a valid process first."));
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            DateTimeOffset actualStartTime = process.StartTime;
            if (actualStartTime.ToUniversalTime() != expectedStartTime.ToUniversalTime())
            {
                return ValueTask.FromResult(new ProcessActionResult(
                    false,
                    $"Process {processId} exited and its identifier was reused. No signal was sent."));
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(new ProcessActionResult(
                false,
                $"Process {processId} is no longer available. No signal was sent."));
        }

        int signal = mode == ProcessTerminationMode.ForceQuit ? SignalKill : SignalTerminate;
        int result = kill(processId, signal);
        if (result == 0)
        {
            string action = mode == ProcessTerminationMode.ForceQuit ? "Force Quit" : "Quit";
            return ValueTask.FromResult(new ProcessActionResult(true, $"{action} signal sent to process {processId}."));
        }

        int error = Marshal.GetLastPInvokeError();
        return ValueTask.FromResult(new ProcessActionResult(
            false,
            $"Could not signal process {processId} (errno {error})."));
    }

    public async ValueTask<ProcessReportResult> SampleProcessAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (processId <= 0)
        {
            return new ProcessReportResult(
                false,
                "Select a valid process first.",
                string.Empty);
        }

        CommandResult result = await RunCommandResultAsync(
            "/usr/bin/sample",
            [
                processId.ToString(CultureInfo.InvariantCulture),
                "1",
                "1"
            ],
            cancellationToken).ConfigureAwait(false);
        string report = result.StandardOutput.Length > 64_000
            ? result.StandardOutput[..64_000] + "\n… report truncated …"
            : result.StandardOutput;
        return result.ExitCode == 0 && report.Length > 0
            ? new ProcessReportResult(true, "Process sample captured.", report)
            : new ProcessReportResult(
                false,
                result.StandardError.Length > 0
                    ? result.StandardError.Trim()
                    : $"sample exited with code {result.ExitCode}.",
                report);
    }

    public ValueTask<ProcessActionResult> RunDiagnosticAsync(
        ActivityDiagnosticKind kind,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string timestamp = DateTimeOffset.Now.ToString(
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture);
            string fileName;
            string[] arguments;
            string outputDescription;
            switch (kind)
            {
                case ActivityDiagnosticKind.Spindump:
                    if (processId is null or <= 0)
                    {
                        return ValueTask.FromResult(new ProcessActionResult(
                            false,
                            "Select a process before running Spindump."));
                    }
                    fileName = "/usr/sbin/spindump";
                    string spindumpPath = Path.Combine(
                        Path.GetTempPath(),
                        $"ActivityMonitor-spindump-{processId}-{timestamp}.txt");
                    arguments =
                    [
                        processId.Value.ToString(CultureInfo.InvariantCulture),
                        "10",
                        "10",
                        "-file",
                        spindumpPath
                    ];
                    outputDescription = $"Spindump started. Report: {spindumpPath}";
                    break;
                case ActivityDiagnosticKind.SystemDiagnostics:
                    fileName = "/usr/bin/sysdiagnose";
                    string diagnosticDirectory = Path.GetTempPath();
                    arguments = ["-f", diagnosticDirectory];
                    outputDescription =
                        $"System Diagnostics started. Archive will be written to {diagnosticDirectory}.";
                    break;
                default:
                    return ValueTask.FromResult(new ProcessActionResult(
                        false,
                        "Unsupported diagnostic."));
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            Process? process = Process.Start(startInfo);
            process?.Dispose();
            return ValueTask.FromResult(new ProcessActionResult(
                process is not null,
                process is not null
                    ? outputDescription
                    : $"Could not start {Path.GetFileName(fileName)}."));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(new ProcessActionResult(
                false,
                exception.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        try
        {
            await _networkTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _disposeCts.Dispose();
        _captureGate.Dispose();
    }

    private ActivitySnapshot CaptureCore(
        ActivityCaptureOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        Dictionary<int, PsProcessMetadata> metadata = CaptureProcessMetadata(cancellationToken);
        var processes = options.IncludeProcesses
            ? CaptureProcesses(metadata, capturedAt, cancellationToken)
            : [];

        CpuPercentages cpu = CaptureCpuPercentages();
        MemoryCounters memory = CaptureMemoryCounters(cancellationToken);
        BatterySnapshot battery = CaptureBattery(cancellationToken);
        long processDiskRead = 0;
        long processDiskWritten = 0;
        long processNetworkReceived = 0;
        long processNetworkSent = 0;
        int threads = 0;
        foreach (ProcessSnapshot process in processes)
        {
            processDiskRead += process.DiskReadBytes;
            processDiskWritten += process.DiskWrittenBytes;
            processNetworkReceived += process.NetworkReceivedBytes;
            processNetworkSent += process.NetworkSentBytes;
            threads += process.ThreadCount;
        }
        IoCounters disk = CaptureDiskCounters(cancellationToken);
        IoCounters network = CaptureNetworkCounters(cancellationToken);

        var system = new SystemSnapshot(
            cpu.User,
            cpu.System,
            cpu.Idle,
            memory.Physical,
            memory.Used,
            memory.Cached,
            memory.App,
            memory.Wired,
            memory.Compressed,
            memory.SwapUsed,
            disk.ReadBytes > 0 ? disk.ReadBytes : processDiskRead,
            disk.WrittenBytes > 0 ? disk.WrittenBytes : processDiskWritten,
            disk.ReadOperations,
            disk.WriteOperations,
            network.ReadBytes > 0 ? network.ReadBytes : processNetworkReceived,
            network.WrittenBytes > 0 ? network.WrittenBytes : processNetworkSent,
            network.ReadOperations,
            network.WriteOperations,
            processes.Count,
            threads,
            battery);

        _previousCaptureTime = capturedAt;
        return new ActivitySnapshot(capturedAt, processes, system);
    }

    private List<ProcessSnapshot> CaptureProcesses(
        IReadOnlyDictionary<int, PsProcessMetadata> metadata,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<ProcessSnapshot>(metadata.Count);
        var liveProcessIds = new HashSet<int>();
        double elapsedSeconds = Math.Max(
            0.001,
            (capturedAt - (_previousCaptureTime ?? capturedAt)).TotalSeconds);

        foreach (Process nativeProcess in Process.GetProcesses())
        {
            using (nativeProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int processId = nativeProcess.Id;
                    liveProcessIds.Add(processId);
                    if (!metadata.TryGetValue(processId, out PsProcessMetadata processMetadata))
                    {
                        processMetadata = new PsProcessMetadata(0, 0, string.Empty, 0, string.Empty);
                    }
                    string executablePath = processMetadata.ExecutablePath;
                    string name = NormalizeProcessName(nativeProcess.ProcessName, executablePath);
                    DateTimeOffset? startTime = SafeRead<DateTimeOffset?>(
                        () => nativeProcess.StartTime);
                    TimeSpan cpuTime = nativeProcess.TotalProcessorTime;
                    double cpuPercent = processMetadata.PsCpuPercent;
                    bool isSameProcess =
                        _previousProcesses.TryGetValue(processId, out PreviousProcessSample previous) &&
                        startTime.HasValue &&
                        previous.StartTime.ToUniversalTime() == startTime.Value.ToUniversalTime();
                    if (isSameProcess)
                    {
                        cpuPercent = Math.Max(
                            0,
                            (cpuTime - previous.CpuTime).TotalSeconds / elapsedSeconds * 100);
                    }

                    RusageInfoV2 usage = default;
                    bool hasUsage = proc_pid_rusage(
                        processId,
                        RusageInfoVersion2,
                        ref usage) == 0;
                    long wakeUps = hasUsage
                        ? SaturatingLong(usage.PackageIdleWakeUps + usage.InterruptWakeUps)
                        : 0;
                    double wakeUpRate = 0;
                    if (isSameProcess)
                    {
                        wakeUpRate = Math.Max(0, wakeUps - previous.WakeUps) / elapsedSeconds;
                    }

                    if (!isSameProcess)
                    {
                        _networkByProcess.TryRemove(processId, out _);
                    }
                    NetworkCounters network = isSameProcess
                        ? _networkByProcess.GetValueOrDefault(processId)
                        : default;
                    long memoryBytes = SafeRead(() => nativeProcess.WorkingSet64);
                    long virtualMemoryBytes = SafeRead(() => nativeProcess.VirtualMemorySize64);
                    int threadCount = SafeRead(() => nativeProcess.Threads.Count);
                    int portCount = SafeRead(() => nativeProcess.HandleCount);
                    long diskRead = hasUsage ? SaturatingLong(usage.DiskIoBytesRead) : 0;
                    long diskWritten = hasUsage ? SaturatingLong(usage.DiskIoBytesWritten) : 0;
                    double energyImpact = Math.Max(0, cpuPercent * 0.72 + wakeUpRate * 0.08);
                    bool isApplication = IsApplication(executablePath);
                    string kind = IsAppleProcess(executablePath) ? "Apple" : "Other";

                    snapshots.Add(new ProcessSnapshot(
                        processId,
                        processMetadata.ParentProcessId,
                        processMetadata.ProcessGroupId,
                        name,
                        processMetadata.User,
                        startTime,
                        cpuPercent,
                        cpuTime,
                        threadCount,
                        memoryBytes,
                        virtualMemoryBytes,
                        diskRead,
                        diskWritten,
                        network.Received,
                        network.Sent,
                        network.ReceivedPackets,
                        network.SentPackets,
                        energyImpact,
                        wakeUps,
                        portCount,
                        null,
                        null,
                        null,
                        null,
                        kind,
                        executablePath,
                        isApplication));

                    if (startTime.HasValue)
                    {
                        _previousProcesses[processId] = new PreviousProcessSample(
                            startTime.Value,
                            cpuTime,
                            wakeUps);
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or
                    NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // A process may exit or become inaccessible between enumeration and sampling.
                }
            }
        }

        foreach (int staleProcessId in _previousProcesses.Keys.Where(id => !liveProcessIds.Contains(id)).ToArray())
        {
            _previousProcesses.Remove(staleProcessId);
        }
        foreach (int staleProcessId in _networkByProcess.Keys.Where(id => !liveProcessIds.Contains(id)))
        {
            _networkByProcess.TryRemove(staleProcessId, out _);
        }

        return snapshots;
    }

    private CpuPercentages CaptureCpuPercentages()
    {
        var info = new HostCpuLoadInfoData();
        uint count = 4;
        uint host = mach_host_self();
        try
        {
            if (host_statistics(host, HostCpuLoadInfo, ref info, ref count) != 0)
            {
                return new CpuPercentages(0, 0, 100);
            }
        }
        finally
        {
            _ = mach_port_deallocate(mach_task_self(), host);
        }

        var current = new CpuTicks(info.User, info.System, info.Idle, info.Nice);
        CpuTicks previous = _previousCpuTicks ?? current;
        _previousCpuTicks = current;

        ulong userDelta = ComputeTickDelta(current.User, previous.User);
        ulong systemDelta = ComputeTickDelta(current.System, previous.System);
        ulong idleDelta = ComputeTickDelta(current.Idle, previous.Idle);
        ulong niceDelta = ComputeTickDelta(current.Nice, previous.Nice);
        double total = Math.Max(1, userDelta + systemDelta + idleDelta + niceDelta);
        double user = (userDelta + niceDelta) * 100 / total;
        double system = systemDelta * 100 / total;
        return new CpuPercentages(user, system, Math.Max(0, 100 - user - system));
    }

    internal static ulong ComputeTickDelta(uint current, uint previous) =>
        current >= previous
            ? current - previous
            : (ulong)uint.MaxValue - previous + current + 1;

    private static MemoryCounters CaptureMemoryCounters(CancellationToken cancellationToken)
    {
        string output = RunCommand(
            "/usr/bin/vm_stat",
            [],
            cancellationToken);
        long pageSize = 4096;
        long freePages = 0;
        long inactivePages = 0;
        long speculativePages = 0;
        long fileBackedPages = 0;
        long anonymousPages = 0;
        long wiredPages = 0;
        long compressedPages = 0;
        long physical = GetSystemUInt64("hw.memsize");
        long swapUsed = GetSwapUsed();

        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Mach Virtual Memory Statistics:", StringComparison.Ordinal))
            {
                int marker = line.IndexOf("page size of ", StringComparison.Ordinal);
                int suffix = line.IndexOf(" bytes", StringComparison.Ordinal);
                if (marker >= 0 && suffix > marker &&
                    long.TryParse(
                        line.AsSpan(marker + "page size of ".Length, suffix - marker - "page size of ".Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long parsedPageSize))
                {
                    pageSize = parsedPageSize;
                }
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string key = line[..separator];
            string valueText = line[(separator + 1)..].Trim().TrimEnd('.');
            if (!long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                continue;
            }

            switch (key)
            {
                case "Pages free":
                    freePages = value;
                    break;
                case "Pages inactive":
                    inactivePages = value;
                    break;
                case "Pages speculative":
                    speculativePages = value;
                    break;
                case "File-backed pages":
                    fileBackedPages = value;
                    break;
                case "Anonymous pages":
                    anonymousPages = value;
                    break;
                case "Pages wired down":
                    wiredPages = value;
                    break;
                case "Pages occupied by compressor":
                    compressedPages = value;
                    break;
            }
        }

        long cached = ComputeCachedMemoryBytes(fileBackedPages, pageSize, physical);
        long available = Math.Max(0, (freePages + inactivePages + speculativePages) * pageSize);
        long used = Math.Max(0, physical - Math.Min(physical, available));
        return new MemoryCounters(
            physical,
            used,
            cached,
            Math.Max(0, anonymousPages * pageSize),
            Math.Max(0, wiredPages * pageSize),
            Math.Max(0, compressedPages * pageSize),
            swapUsed);
    }

    internal static long ComputeCachedMemoryBytes(
        long fileBackedPages,
        long pageSize,
        long physicalMemoryBytes)
    {
        if (fileBackedPages <= 0 || pageSize <= 0 || physicalMemoryBytes <= 0)
        {
            return 0;
        }

        long cachedBytes = fileBackedPages > long.MaxValue / pageSize
            ? long.MaxValue
            : fileBackedPages * pageSize;
        return Math.Min(cachedBytes, physicalMemoryBytes);
    }

    private static BatterySnapshot CaptureBattery(CancellationToken cancellationToken)
    {
        string output = RunCommand("/usr/bin/pmset", ["-g", "batt"], cancellationToken);
        string powerSource = output.Contains("'Battery Power'", StringComparison.Ordinal)
            ? "Battery"
            : "AC Power";
        string batteryLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains('%', StringComparison.Ordinal)) ?? string.Empty;
        int percentMarker = batteryLine.IndexOf('%');
        double charge = 0;
        if (percentMarker > 0)
        {
            int start = percentMarker - 1;
            while (start >= 0 && char.IsAsciiDigit(batteryLine[start]))
            {
                start--;
            }
            double.TryParse(
                batteryLine.AsSpan(start + 1, percentMarker - start - 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out charge);
        }

        bool charging = batteryLine.Contains("charging", StringComparison.OrdinalIgnoreCase) &&
                        !batteryLine.Contains("not charging", StringComparison.OrdinalIgnoreCase);
        string remaining = "Calculating";
        string[] parts = batteryLine.Split(';', StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (part.Contains("remaining", StringComparison.OrdinalIgnoreCase))
            {
                remaining = part.Replace(" remaining", string.Empty, StringComparison.OrdinalIgnoreCase);
                break;
            }
        }

        return new BatterySnapshot(
            batteryLine.Length > 0,
            Math.Clamp(charge, 0, 100),
            charging,
            powerSource,
            remaining);
    }

    private static Dictionary<int, PsProcessMetadata> CaptureProcessMetadata(
        CancellationToken cancellationToken)
    {
        string output = RunCommand(
            "/bin/ps",
            ["-axo", "pid=,ppid=,pgid=,user=,%cpu=,comm="],
            cancellationToken);
        var result = new Dictionary<int, PsProcessMetadata>();
        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            string[] fields = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6 ||
                !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentProcessId) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int processGroupId))
            {
                continue;
            }

            double.TryParse(
                fields[4],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double cpuPercent);
            result[processId] = new PsProcessMetadata(
                parentProcessId,
                processGroupId,
                fields[3],
                cpuPercent,
                fields[5]);
        }
        return result;
    }

    private async Task ObserveNetworkAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/nettop",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-P");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("-n");
            process.StartInfo.ArgumentList.Add("-L");
            process.StartInfo.ArgumentList.Add("0");
            process.StartInfo.ArgumentList.Add("-x");
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-J");
            process.StartInfo.ArgumentList.Add("packets_in,bytes_in,packets_out,bytes_out");

            try
            {
                process.Start();
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }
                    ParseNetworkLine(line);
                }
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or InvalidOperationException or
                System.ComponentModel.Win32Exception or IOException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                TryTerminateProcess(process);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ParseNetworkLine(string line)
    {
        string[] fields = line.Split(',');
        if (fields.Length < 5 || fields[0].Length == 0)
        {
            return;
        }

        int dot = fields[0].LastIndexOf('.');
        if (dot < 0 ||
            !int.TryParse(fields[0].AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ||
            !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long receivedPackets) ||
            !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long received) ||
            !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sentPackets) ||
            !long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sent))
        {
            return;
        }

        _networkByProcess[processId] = new NetworkCounters(
            received,
            sent,
            receivedPackets,
            sentPackets);
    }

    private static string NormalizeProcessName(string processName, string executablePath)
    {
        if (executablePath.Length == 0)
        {
            return processName;
        }

        int appMarker = executablePath.IndexOf(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase);
        if (appMarker > 0)
        {
            int slash = executablePath.LastIndexOf('/', appMarker - 1);
            return executablePath[(slash + 1)..appMarker];
        }

        return Path.GetFileName(executablePath);
    }

    private static bool IsApplication(string executablePath) =>
        executablePath.Contains(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppleProcess(string executablePath) =>
        executablePath.StartsWith("/System/", StringComparison.Ordinal) ||
        executablePath.StartsWith("/usr/", StringComparison.Ordinal) ||
        executablePath.StartsWith("/bin/", StringComparison.Ordinal) ||
        executablePath.StartsWith("/sbin/", StringComparison.Ordinal);

    private static IoCounters CaptureDiskCounters(CancellationToken cancellationToken)
    {
        try
        {
            string output = RunCommand(
                "/usr/sbin/ioreg",
                ["-r", "-c", "IOBlockStorageDriver", "-k", "Statistics", "-l"],
                cancellationToken);
            return new IoCounters(
                ExtractCounterSum(output, "\"Bytes (Read)\"="),
                ExtractCounterSum(output, "\"Bytes (Write)\"="),
                ExtractCounterSum(output, "\"Operations (Read)\"="),
                ExtractCounterSum(output, "\"Operations (Write)\"="));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }

    private static IoCounters CaptureNetworkCounters(CancellationToken cancellationToken)
    {
        try
        {
            string output = RunCommand(
                "/usr/sbin/netstat",
                ["-ibn"],
                cancellationToken);
            long receivedBytes = 0;
            long sentBytes = 0;
            long receivedPackets = 0;
            long sentPackets = 0;
            var interfaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in output.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] columns = line.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (columns.Length < 10 ||
                    !columns[2].StartsWith("<Link#", StringComparison.Ordinal) ||
                    columns[0] == "lo0" ||
                    columns[0].EndsWith('*') ||
                    !interfaces.Add(columns[0]))
                {
                    continue;
                }

                int counters = columns.Length - 7;
                if (!long.TryParse(columns[counters], out long inputPackets) ||
                    !long.TryParse(columns[counters + 2], out long inputBytes) ||
                    !long.TryParse(columns[counters + 3], out long outputPackets) ||
                    !long.TryParse(columns[counters + 5], out long outputBytes))
                {
                    continue;
                }

                receivedPackets = SaturatingAdd(receivedPackets, inputPackets);
                receivedBytes = SaturatingAdd(receivedBytes, inputBytes);
                sentPackets = SaturatingAdd(sentPackets, outputPackets);
                sentBytes = SaturatingAdd(sentBytes, outputBytes);
            }

            return new IoCounters(
                receivedBytes,
                sentBytes,
                receivedPackets,
                sentPackets);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }

    internal static long ExtractCounterSum(string output, string marker)
    {
        long total = 0;
        int searchStart = 0;
        while (searchStart < output.Length)
        {
            int markerIndex = output.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                break;
            }

            int valueStart = markerIndex + marker.Length;
            int valueEnd = valueStart;
            while (valueEnd < output.Length && char.IsAsciiDigit(output[valueEnd]))
            {
                valueEnd++;
            }
            if (long.TryParse(
                    output.AsSpan(valueStart, valueEnd - valueStart),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long value))
            {
                total = SaturatingAdd(total, value);
            }
            searchStart = Math.Max(valueEnd, valueStart + 1);
        }
        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private static T SafeRead<T>(Func<T> reader, T fallback = default!)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
            System.ComponentModel.Win32Exception)
        {
            return fallback;
        }
    }

    private static long SaturatingLong(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static long GetSystemUInt64(string name)
    {
        ulong value = 0;
        nuint size = (nuint)sizeof(ulong);
        if (sysctlbyname(name, ref value, ref size, IntPtr.Zero, 0) != 0)
        {
            return 0;
        }
        return SaturatingLong(value);
    }

    private static long GetSwapUsed()
    {
        var usage = new XswUsage();
        nuint size = (nuint)Marshal.SizeOf<XswUsage>();
        if (sysctlbyname("vm.swapusage", ref usage, ref size, IntPtr.Zero, 0) != 0)
        {
            return 0;
        }
        return SaturatingLong(usage.Used);
    }

    private static string RunCommand(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunCommandResultAsync(fileName, arguments, cancellationToken)
            .GetAwaiter()
            .GetResult()
            .StandardOutput;

    private static async Task<string> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => (await RunCommandResultAsync(
                fileName,
                arguments,
                cancellationToken).ConfigureAwait(false))
            .StandardOutput;

    private static async Task<CommandResult> RunCommandResultAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateCommand(fileName, arguments);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        process.Start();
        using CancellationTokenRegistration registration =
            timeout.Token.Register(() => TryTerminateProcess(process));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            Task exit = process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error, exit).ConfigureAwait(false);
            return new CommandResult(
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} did not complete within {CommandTimeout.TotalSeconds:N0} seconds.");
        }
        finally
        {
            TryTerminateProcess(process);
        }
    }

    private static Process CreateCommand(string fileName, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        return process;
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (process.Id > 0 && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            NotSupportedException or
            System.ComponentModel.Win32Exception)
        {
        }
    }

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pid_rusage(int pid, int flavor, ref RusageInfoV2 buffer);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint mach_host_self();

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint mach_task_self();

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int mach_port_deallocate(uint task, uint name);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int host_statistics(
        uint host,
        int flavor,
        ref HostCpuLoadInfoData info,
        ref uint count);

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int sysctlbyname(
        string name,
        ref ulong oldValue,
        ref nuint oldLength,
        IntPtr newValue,
        nuint newLength);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int sysctlbyname(
        string name,
        ref XswUsage oldValue,
        ref nuint oldLength,
        IntPtr newValue,
        nuint newLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct HostCpuLoadInfoData
    {
        public uint User;
        public uint System;
        public uint Idle;
        public uint Nice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XswUsage
    {
        public ulong Total;
        public ulong Available;
        public ulong Used;
        public uint PageSize;
        [MarshalAs(UnmanagedType.I1)]
        public bool Encrypted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RusageInfoV2
    {
        public fixed byte Uuid[16];
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PackageIdleWakeUps;
        public ulong InterruptWakeUps;
        public ulong PageIns;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysicalFootprint;
        public ulong ProcessStartAbsoluteTime;
        public ulong ProcessExitAbsoluteTime;
        public ulong ChildUserTime;
        public ulong ChildSystemTime;
        public ulong ChildPackageIdleWakeUps;
        public ulong ChildInterruptWakeUps;
        public ulong ChildPageIns;
        public ulong ChildElapsedAbsoluteTime;
        public ulong DiskIoBytesRead;
        public ulong DiskIoBytesWritten;
    }

    private readonly record struct PsProcessMetadata(
        int ParentProcessId,
        int ProcessGroupId,
        string User,
        double PsCpuPercent,
        string ExecutablePath);

    private readonly record struct CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private readonly record struct IoCounters(
        long ReadBytes,
        long WrittenBytes,
        long ReadOperations,
        long WriteOperations);

    private readonly record struct PreviousProcessSample(
        DateTimeOffset StartTime,
        TimeSpan CpuTime,
        long WakeUps);
    private readonly record struct NetworkCounters(
        long Received,
        long Sent,
        long ReceivedPackets,
        long SentPackets);
    private readonly record struct CpuTicks(uint User, uint System, uint Idle, uint Nice);
    private readonly record struct CpuPercentages(double User, double System, double Idle);
    private readonly record struct MemoryCounters(
        long Physical,
        long Used,
        long Cached,
        long App,
        long Wired,
        long Compressed,
        long SwapUsed);
}
