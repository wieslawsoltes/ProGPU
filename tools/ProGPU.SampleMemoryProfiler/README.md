# ProGPU memory profiler

`ProGPU.SampleMemoryProfiler` combines four memory views that must not be
conflated:

- .NET runtime counters, including live GC heap, committed GC memory,
  fragmentation, allocation rate, and collection counts;
- process working set, private bytes, virtual bytes, threads, and CPU time;
- macOS physical-footprint and VM-region accounting for Metal/AGX,
  `VM_ALLOCATE`, IOSurface, malloc zones, and stacks;
- optional native allocator/object summaries plus the ProGPU/Metal/wgpu
  counters emitted by a benchmark result.

Capture a running process:

```bash
dotnet run --project tools/ProGPU.SampleMemoryProfiler -c Release -- \
  capture --pid 12345 --duration 30 --interval 2 \
  --native-heap --output artifacts/memory/capture.json
```

To merge the application-side GPU resource counters and Metal
`currentAllocatedSize`, add:

```bash
--benchmark-json artifacts/memory/controlcatalog.json
```

The capture produces JSON for automated regression gates and a Markdown
summary beside it. Repeated samples distinguish stable high-water residency
from monotonic growth; a single process footprint number cannot establish a
leak. VM-region growth rows report resident and dirty bytes separately:
resident bytes describe the working set, while dirty bytes describe modified
pages and must not be mislabeled as additional residency.
Use `--no-runtime-counters` for a native non-.NET target; process, `vmmap`, and
native-heap collection remain enabled while the inapplicable EventPipe
connection is skipped.

On macOS, `vmmap` suspends and inspects the process and can itself pressure
purgeable Metal/AGX residency. Treat the first successful sample as the
pre-inspection active snapshot. A large later reduction in
`owned unmapped (graphics)` with stable dirty bytes is evidence of reclaimable
driver high-water state, not negative application allocation. Use the paired
Instruments and application-side resource ledgers for ownership; do not add
physical footprint, IOSurface, IOAccelerator, and
`MTLDevice.currentAllocatedSize` as though they were disjoint allocations.

For a matched Avalonia ProGPU/Skia capture, including benchmark telemetry,
native heap attribution, sampled VM residency, and a forced-GC dump, run:

```bash
tools/profile-avalonia-managed-memory.sh artifacts/avalonia-memory
```

The wrapper accepts the `PROGPU_AVALONIA_MANAGED_*` and
`PROGPU_AVALONIA_MEMORY_CAPTURE_*` variables printed by its `--help` output.
It launches fresh processes, waits for the benchmark's diagnostic hold, and
keeps each backend's artifacts separate. By default it also uses
`dotnet-dump` to emit a root-filtered `live-heap-report.txt`. This is the
retained-object ledger: the ordinary `dotnet-gcdump report` can include
unreachable objects that have not yet been reclaimed and must not be labeled
as retained memory by itself. The temporary heap dump is deleted immediately
after the compact live report is written. Raw `.gcdump` files are also removed
by default; set `PROGPU_AVALONIA_KEEP_GCDUMPS=1` only when interactive graph
inspection is required.

On macOS with Xcode installed, capture matched Allocations, Time Profiler, and
Metal System Trace runs with:

```bash
progpu-memory instruments \
  --output artifacts/instruments/controlcatalog \
  --duration 20 \
  --window 5 \
  --allocation-details \
  --cleanup-traces \
  --cleanup-exports \
  --env PROGPU_AVALONIA_DIRECT_PRESENTATION=1 \
  --launch ~/.dotnet/dotnet \
  integration/AvaloniaSourceControlCatalog/bin/Release/net10.0/AvaloniaSourceControlCatalog.dll \
  --page Composition
```

Each Instruments template launches a fresh process so the workloads remain
comparable. `--window` retains only the final capture interval and prevents
long Metal System Trace finalization from producing multi-gigabyte bundles.
`--allocation-details` additionally exports the Allocations List and groups
live rows by native/VM category, responsible caller, and library, including
the first and last allocation timestamps. This distinguishes a one-time
driver reservation burst from per-frame growth. The list can be tens of
megabytes, so use it for attribution captures and pair it with
`--cleanup-exports` after the compact summary has been written.
Every `xctrace` record/export sequence receives a unique task-owned `TMPDIR`.
The tool always removes that directory after the supported tables have been
exported, including timeout and failure paths. Some Xcode services ignore the
overridden directory, so the tool also snapshots the system temporary
directory before each sequential capture and deletes only new
`instruments*.ktrace` identities that appeared during it. These private files
otherwise live outside the requested output directory and can consume
gigabytes without being covered by `--cleanup-traces`.
`--cleanup-traces` deletes each exact `.trace` bundle only after its TOC and
supported XML tables have been exported. The manifest records whether the raw
trace was retained and how many bytes were reclaimed, so compact profiling
evidence can be shipped without stale paths or manual cleanup.
`--cleanup-exports` performs a second, later cleanup after
`instruments-summary.json` and `instruments-summary.md` have been generated.
It deletes each exact TOC and exported XML table while retaining the summaries,
logs, and manifest; the manifest records both export retention and reclaimed
bytes. Use it when the resolved summaries are sufficient shipping evidence.
Recording finalization is bounded to the requested capture duration plus two
minutes. If `xctrace` or its launched application does not terminate, the tool
stops that exact process tree and removes the incomplete trace bundle instead
of leaving a hung profiler, application, or unusable raw trace.
Repeat `--env NAME=value` to configure the launched workload; the manifest
records only environment-variable names, not their values. Launch mode also
redirects each target's standard output to `<template>-target.log` and records
that path in the manifest, so an apparently valid trace cannot substitute for
the expected benchmark or smoke-test result. The command retains
the `.trace` bundles unless `--cleanup-traces` is selected. Logs, TOCs,
exported Time Profiler samples and hang diagnostics, Metal allocation,
command-buffer, command-buffer-error, compiler-spill, drawable-wait, and
`MTLDevice.currentAllocatedSize` tables, plus a JSON manifest, remain as
compact evidence unless `--cleanup-exports` is selected. Use
`--templates allocations,time,metal` to select a subset. Instrumented FPS is
not a benchmark result; correlate these traces with an uninstrumented
`capture` time series and the application's benchmark JSON.

For a GUI application that cannot be launched reliably through the
command-line Instruments bootstrap, start it normally and attach to its exact
PID:

```bash
progpu-memory instruments \
  --output artifacts/instruments/native-gui \
  --duration 20 \
  --window 5 \
  --cleanup-traces \
  --cleanup-exports \
  --attach 12345
```

Attach mode keeps the same process alive across the selected sequential
templates and does not accept `--env`; configure the process at launch. The
manifest records the target mode, target PID or name, and executable arguments
when applicable.

On macOS, `VM: Dispatch continuations` is a per-process libdispatch
virtual-address reservation. Its Allocations byte count is not physical
residency. The compact summary calls this out explicitly; use the paired
`capture`/`vmmap` resident and dirty columns to determine the actual working
set. The libdispatch allocator size and its unsupported allocator-selection
environment variable are operating-system implementation details, so the
shipping profiler reports them but does not mutate them.

The command also writes `instruments-summary.json` and
`instruments-summary.md`. They resolve Xcode's cross-row XML references and
report Metal resource bytes grouped by resource type and attributed stack
owner (`wgpu-native`, window system, Metal driver, or other), live-at-capture
residency, `currentAllocatedSize`, drawable waits, compiler spills,
potential hangs, hang risks, command-buffer errors, submissions, and
completions. Metal resource totals count only explicit `Allocation` rows.
Live-at-capture residency is determined by pairing explicit `Deallocation`
rows by resource ID, rather than interpreting Instruments' live-duration
sentinel as a normal lifetime. Existing export directories can be summarized
again without recording:

```bash
progpu-memory instruments-summary artifacts/instruments/controlcatalog
```

The project is also packable as the `ProGPU.MemoryProfiler` .NET tool with the
command name `progpu-memory`:

```bash
dotnet tool install --global ProGPU.MemoryProfiler
progpu-memory capture --pid 12345 --duration 30 --interval 2 \
  --native-heap --output artifacts/memory/capture.json
```

For a native first-party comparison that excludes .NET, Avalonia, Dawn, and
ProGPU, build and profile the repository's minimal SwiftUI `MTKView` clear
loop:

```bash
tools/profile-swiftui-metal-memory.sh \
  artifacts/swiftui-metal-memory
```

The wrapper runs matched default and diagnostic libdispatch-malloc lanes with
a physical `vmmap`/native-heap time series followed by attached Allocations,
Time Profiler, and Metal System Trace captures. It removes raw traces, exports,
Xcode scratch, and the compiled probe after compact summaries are written. The
malloc lane is diagnostic only and must not be used as a shipping
configuration.
