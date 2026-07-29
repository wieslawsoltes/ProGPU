# ProGPU Activity Monitor

A standalone ProGPU.WinUI desktop sample that recreates the macOS Activity Monitor
information architecture and interaction model. It includes live CPU, Memory, Energy,
Disk, and Network views; typed sortable columns; process search; history graphs; process
inspection; graceful quit; and force quit.

## Architecture

The UI depends only on `IActivityMonitorDataSource`. Its point-in-time snapshot, process
details, and process-action contracts are platform-neutral so Windows and Linux providers
can be added without changing the view or controller.

The initial `MacOsActivityMonitorDataSource` uses public operating-system interfaces:

- `Process` and `ps` for process identity, ownership, threads, CPU time, and memory.
- Mach host statistics and `vm_stat` for CPU and memory summaries.
- `proc_pid_rusage` for per-process disk I/O and wakeups.
- a persistent `nettop` observer for per-process network counters.
- `pmset` for portable-Mac power state.
- POSIX signals for quit and force-quit actions.

Data refreshes every two seconds away from the UI thread, then publishes an immutable
snapshot on the dispatcher. Missing or inaccessible process data is represented by neutral
values instead of failing the whole sample. Energy Impact is an explicit estimate derived
from sampled CPU and wakeup activity; private Activity Monitor energy and App Nap APIs are
not used.

## Clean-room behavior references

The sample was designed from the supplied screenshots and Apple's published behavior
descriptions. No Activity Monitor implementation source was copied or adapted.

- [Activity Monitor User Guide](https://support.apple.com/guide/activity-monitor/welcome/mac)
- [View memory usage](https://support.apple.com/en-ca/guide/activity-monitor/actmntr1004/mac)
- [View energy consumption](https://support.apple.com/en-gb/guide/activity-monitor/actmntr43697/mac)
- [View disk activity](https://support.apple.com/en-ca/guide/activity-monitor/actmntr1005/mac)
- [View network activity](https://support.apple.com/en-ie/guide/activity-monitor/actmntr1006/mac)

The layout adopts the observable five-pane navigation, process table, search, process
actions, and pane-specific summary graphs. Native-only internals and private metrics are
rejected in favor of typed public APIs and clearly labeled estimates.

## Run

From the repository root:

```bash
dotnet run --project src/ProGPU.Samples.ActivityMonitor/ProGPU.Samples.ActivityMonitor.csproj
```

To verify the provider without opening a window:

```bash
dotnet run --project src/ProGPU.Samples.ActivityMonitor/ProGPU.Samples.ActivityMonitor.csproj -- --snapshot
```
