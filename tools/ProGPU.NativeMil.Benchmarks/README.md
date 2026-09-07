# Native MIL viewport sideband component workload

Build in Release with the repository SDK. Put the native library and its WebGPU
dependency on the normal runtime-loader search path (for example
`DYLD_LIBRARY_PATH` on macOS, `LD_LIBRARY_PATH` on Linux, or `PATH` on Windows).
No adapter or GPU device is created by this workload.

```sh
dotnet build tools/ProGPU.NativeMil.Benchmarks -c Release
dotnet tools/ProGPU.NativeMil.Benchmarks/bin/Release/net10.0/ProGPU.NativeMil.Benchmarks.dll 60000 100
dotnet tools/ProGPU.NativeMil.Benchmarks/bin/Release/net10.0/ProGPU.NativeMil.Benchmarks.dll 60000 100 --retained
```

Arguments are vertex count (positive multiple of three), iterations per sample,
and optional `--retained`. Use 3, 3000, and 60000 vertices for small, medium, and
large payloads. The tool warms 128 updates, then reports nine batch means, their
median/maximum, managed thread allocations, owned snapshot bytes, and native
resource-generation delta. These are **not individual-update p95/p99** values.

The unconditional mode calls the native entry point on every update. The retained
mode uses `NativeMilViewport3DSnapshot` to avoid unchanged native bindings.
Compare the same Release binaries/runtime, payload, power/thermal state, and
iteration count; preserve the old native binary when evaluating the pre-change
generation churn. The updated native implementation independently recognizes
bindings identical to its validated owned state, so unconditional mode on the
new binary also has zero generation changes and skips per-element validation.
Changed payloads still run all validators before replacing owned state.

One snapshot owns O(B) payload memory per viewport. Capture happens at initial
or changed binding and is not in the unchanged-update measurement. Producer-side
WPF flattening, scene compilation, GPU uploads, rasterization, frame pacing, and
device loss are excluded. This workload cannot establish an application FPS
improvement. Use native pixel gates and the LibreWPF retained-session integration
smoke for correctness. Run Time Profiler and Allocations around matched modes on
macOS; GPU/Metal timing is inapplicable here because no device is initialized.
