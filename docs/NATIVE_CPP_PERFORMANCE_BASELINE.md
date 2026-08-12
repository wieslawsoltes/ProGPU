# ProGPU native C++ renderer performance baseline

Status: initial solid-rectangle tranche; not a whole-engine substitution claim
Baseline source: `v0.1.0-preview.48` (`d63f5cfa`) plus the native-renderer
branch at the time of capture

## Question and scope

This baseline asks whether one batch-oriented C ABI call can compile and submit
the same retained solid-rectangle scene through the new C++ renderer without a
pixel or CPU-submission regression relative to the managed compositor.

It measures only warm solid-rectangle compilation, buffer upload, render-pass
encoding, and queue submission. It does not measure full application frame
latency, cold startup, layout, text, paths, textures, presentation, device
loss, or the future semantic-scene update protocol. GPU completion is not
included unless `--sync` is specified.

## Environment

- Hardware: Apple M3 Pro, arm64
- Graphics backend: Metal through pinned wgpu-native
- Operating system reported by the workload: macOS 26.6.0
- Runtime: .NET 10.0.5
- Target: `Rgba8Unorm`, 960 by 540 physical pixels, DPI scale 1
- Scene: 384 deterministic opaque rectangles
- wgpu-native revision: `33133da4ec5a0174cb21539ef2d3346f75200411`
- WebGPU headers revision: `aef5e428a1fdab2ea770581ae7c95d8779984e0a`
- Managed binding/native package: Silk.NET.WebGPU 2.23.0
- Build: Release, with one validation state and device shared by both renderers

The benchmark warms both shader/pipeline paths, renders both implementations
to separate textures on the same device, reads both targets once for the
correctness gate, then alternates native-first and managed-first measurement
order to reduce drift.

## Reproduction

Build the pinned native dependency, C++ library, tests, pure C++ sample,
managed sample, and short differential gate:

```sh
./eng/build-progpu-native.sh
```

Run a clean longer timing workload:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run \
    --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
    -c Release -- \
    --rectangles 384 --warmup 60 --iterations 20000
```

The macOS Instruments captures used the same benchmark under the installed
`Time Profiler`, `Allocations`/VM Tracker, and `Metal System Trace` templates.
Raw trace bundles and their exact JSON workload output are retained under
ignored `artifacts/progpu-native/profiles/`.

## Correctness

Both renderers produced all 518,400 pixels with:

- maximum channel difference: 0;
- pixels over tolerance: 0;
- native FNV-1a 64: `BF5AB8421C934959`;
- managed FNV-1a 64: `BF5AB8421C934959`.

A separate 4,096-rectangle boundary run also produced exact output parity with
hash `ECF5363B17C1B1BE`.

## Measurements

The clean Time Profiler workload completed 20,000 alternating samples:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU submission | 0.0628 ms | 0.3115 ms |
| p50 CPU submission | 0.0607 ms | 0.1410 ms |
| p95 CPU submission | 0.0800 ms | 1.4194 ms |
| Worst observed submission | 1.5605 ms | 18.0285 ms |
| Managed allocation total | 33,072 bytes | 46,560,168 bytes |
| Managed allocation / frame | 1.6536 bytes | 2,328.0084 bytes |

The allocation-instrumented run completed the same 20,000 logical samples but
was intentionally slower under instrumentation: native p95 was 0.3624 ms and
managed p95 was 1.5452 ms. These values are evidence of profiler perturbation,
not clean timing results.

The bounded 200-frame Metal System Trace identified native command buffers as
`ProGPU native solid rectangle pass` and managed command buffers as
`Offscreen Compositor Encoder`. The combined process workload peaked at about
12.64 MiB of reported Metal current allocated size and returned to about
1.16 MiB during teardown. That combined number is not per-renderer residency.

## Interpretation

The initial batch boundary is viable: it preserves exact pixels and, for this
specific warm rectangle workload, reduces CPU submission tail time and managed
allocation substantially. The remaining nonzero native managed-allocation
average is small periodic runtime/P/Invoke-side activity; it is not yet valid
to describe the full call path as strictly zero-allocation.

These results do not prove that C++ can replace the complete managed renderer
without regression. That conclusion remains gated on wider primitive, path,
text, texture, effect, presentation, startup, memory-lifetime, device-loss, and
cross-platform evidence defined in
[`NATIVE_CPP_ENGINE_SPECIFICATION.md`](NATIVE_CPP_ENGINE_SPECIFICATION.md).

## Retained artifacts

- `native-managed-rectangles-time-profiler.trace` and `.json`
- `native-managed-rectangles-allocations.trace` and `.json`
- `native-managed-rectangles-metal-short.trace` and `.json`

All trace bundles are ignored build artifacts because they are large and tied
to one machine. The JSON files beside them preserve the exact measured output.
