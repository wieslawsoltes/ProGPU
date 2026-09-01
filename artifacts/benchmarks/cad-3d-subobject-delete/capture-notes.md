# ProGPU.CAD modern-MESH subobject-deletion measurements

Captured on macOS 26.6 with Xcode `xctrace` 16.0 (17E202), .NET
10.0.5, and the final Release binaries identified by `final-release.json`:

- benchmark: `fb13c47867f381fe86514bf26bb8852de3efe2bddb745301d2730c637f34761d`
- ProGPU.CAD: `b815a2c4a9cf4403612b9749a53c1c4d1577467b502a044760c2053825a408f0`
- acceptance JSON: `bc56c4e2b0b38587a3b94f62af6a063ebc19bf5dae905d025dff969826a25618`

The uninstrumented acceptance run used a 128-by-128 modern-MESH grid with
16,641 control vertices and 16,384 authored faces, selecting 1,024 evenly
distributed faces. Across 24 iterations, deletion plus the pre/post snapshot
and Mesh3D scene compilations measured 451.6070/467.5489/471.1918 ms
p50/p95/p99 and 202,986,371 managed bytes per operation. Exact retained
Undo+Redo measured 0.0255/0.0272/0.0294 ms and 496 managed bytes per operation.

Allocations and Time Profiler each launched the same four-lane edit benchmark
with the 128-by-128/1,024-face workload, two warmups, and 16 iterations.
Allocations/VM Tracker reported 18,267,072 persistent and 2,046,108,880 total
heap-plus-anonymous-VM bytes across process startup and all lanes. Time
Profiler retained samples with zero potential hangs and zero hang risks. Both
targets exited zero; exports and target logs are under `instruments-final/`.

The first Metal System Trace target also exited zero, but Xcode did not finish
finalizing its 3.29 GB system-wide trace within the bounded helper window. The
helper deleted that incomplete trace. The retained `metal.log` and
`metal-target.log` record the attempt. A bounded retry launched the same grid
and selected-face workload with one warmup and four iterations. It exited zero,
exported successfully, and found zero target resource allocations, current
allocated bytes, application submissions, drawable waits, compiler spills,
hangs, and command-buffer errors. Its 6,891 completion rows are unrelated
system activity because the target made no application submission. Compact
exports, summary, manifest, and target log are under
`instruments-metal-retry/`; its 143 MB raw trace was removed after export.
