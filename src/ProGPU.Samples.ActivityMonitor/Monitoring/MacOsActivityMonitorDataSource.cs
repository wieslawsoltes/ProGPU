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
        DateTimeOffset? startTime = null;
        try
        {
            using Process nativeProcess = Process.GetProcessById(processId);
            startTime = nativeProcess.StartTime;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return new ProcessDetails(
            process.ProcessId,
            process.ParentProcessId,
            process.Name,
            process.User,
            process.ExecutablePath,
            commandLine.Trim(),
            startTime,
            process);
    }

    public ValueTask<ProcessActionResult> TerminateProcessAsync(
        int processId,
        ProcessTerminationMode mode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (processId <= 0)
        {
            return ValueTask.FromResult(new ProcessActionResult(false, "Select a valid process first."));
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
        long diskRead = 0;
        long diskWritten = 0;
        long networkReceived = 0;
        long networkSent = 0;
        int threads = 0;
        foreach (ProcessSnapshot process in processes)
        {
            diskRead += process.DiskReadBytes;
            diskWritten += process.DiskWrittenBytes;
            networkReceived += process.NetworkReceivedBytes;
            networkSent += process.NetworkSentBytes;
            threads += process.ThreadCount;
        }

        var system = new SystemSnapshot(
            cpu.User,
            cpu.System,
            cpu.Idle,
            memory.Physical,
            memory.Used,
            memory.Cached,
            memory.SwapUsed,
            diskRead,
            diskWritten,
            networkReceived,
            networkSent,
            processes.Count,
            threads);

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
                        processMetadata = new PsProcessMetadata(0, string.Empty, 0, string.Empty);
                    }
                    string executablePath = processMetadata.ExecutablePath;
                    string name = NormalizeProcessName(nativeProcess.ProcessName, executablePath);
                    TimeSpan cpuTime = nativeProcess.TotalProcessorTime;
                    double cpuPercent = processMetadata.PsCpuPercent;
                    if (_previousProcesses.TryGetValue(processId, out PreviousProcessSample previous))
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
                    if (_previousProcesses.TryGetValue(processId, out previous))
                    {
                        wakeUpRate = Math.Max(0, wakeUps - previous.WakeUps) / elapsedSeconds;
                    }

                    NetworkCounters network = _networkByProcess.GetValueOrDefault(processId);
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
                        name,
                        processMetadata.User,
                        cpuPercent,
                        cpuTime,
                        threadCount,
                        memoryBytes,
                        virtualMemoryBytes,
                        diskRead,
                        diskWritten,
                        network.Received,
                        network.Sent,
                        energyImpact,
                        energyImpact,
                        wakeUps,
                        portCount,
                        0,
                        TimeSpan.Zero,
                        false,
                        false,
                        kind,
                        executablePath,
                        isApplication));

                    _previousProcesses[processId] = new PreviousProcessSample(cpuTime, wakeUps);
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

        return snapshots;
    }

    private CpuPercentages CaptureCpuPercentages()
    {
        var info = new HostCpuLoadInfoData();
        uint count = 4;
        if (host_statistics(mach_host_self(), HostCpuLoadInfo, ref info, ref count) != 0)
        {
            return new CpuPercentages(0, 0, 100);
        }

        var current = new CpuTicks(info.User, info.System, info.Idle, info.Nice);
        CpuTicks previous = _previousCpuTicks ?? current;
        _previousCpuTicks = current;

        ulong userDelta = current.User - previous.User;
        ulong systemDelta = current.System - previous.System;
        ulong idleDelta = current.Idle - previous.Idle;
        ulong niceDelta = current.Nice - previous.Nice;
        double total = Math.Max(1, userDelta + systemDelta + idleDelta + niceDelta);
        double user = (userDelta + niceDelta) * 100 / total;
        double system = systemDelta * 100 / total;
        return new CpuPercentages(user, system, Math.Max(0, 100 - user - system));
    }

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
            }
        }

        long cached = Math.Max(0, (inactivePages + speculativePages + fileBackedPages) * pageSize);
        long available = Math.Max(0, (freePages + inactivePages + speculativePages) * pageSize);
        long used = Math.Max(0, physical - Math.Min(physical, available));
        return new MemoryCounters(physical, used, cached, swapUsed);
    }

    private static Dictionary<int, PsProcessMetadata> CaptureProcessMetadata(
        CancellationToken cancellationToken)
    {
        string output = RunCommand(
            "/bin/ps",
            ["-axo", "pid=,ppid=,user=,%cpu=,comm="],
            cancellationToken);
        var result = new Dictionary<int, PsProcessMetadata>();
        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            string[] fields = line.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5 ||
                !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentProcessId))
            {
                continue;
            }

            double.TryParse(
                fields[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double cpuPercent);
            result[processId] = new PsProcessMetadata(
                parentProcessId,
                fields[2],
                cpuPercent,
                fields[4]);
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
            process.StartInfo.ArgumentList.Add("-L");
            process.StartInfo.ArgumentList.Add("0");
            process.StartInfo.ArgumentList.Add("-x");
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-J");
            process.StartInfo.ArgumentList.Add("bytes_in,bytes_out");

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
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
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
        if (fields.Length < 3 || fields[0].Length == 0)
        {
            return;
        }

        int dot = fields[0].LastIndexOf('.');
        if (dot < 0 ||
            !int.TryParse(fields[0].AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId) ||
            !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long received) ||
            !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sent))
        {
            return;
        }

        _networkByProcess[processId] = new NetworkCounters(received, sent);
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = CreateCommand(fileName, arguments);
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static async Task<string> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateCommand(fileName, arguments);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return await output.ConfigureAwait(false);
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

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pid_rusage(int pid, int flavor, ref RusageInfoV2 buffer);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint mach_host_self();

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
        string User,
        double PsCpuPercent,
        string ExecutablePath);

    private readonly record struct PreviousProcessSample(TimeSpan CpuTime, long WakeUps);
    private readonly record struct NetworkCounters(long Received, long Sent);
    private readonly record struct CpuTicks(ulong User, ulong System, ulong Idle, ulong Nice);
    private readonly record struct CpuPercentages(double User, double System, double Idle);
    private readonly record struct MemoryCounters(long Physical, long Used, long Cached, long SwapUsed);
}
