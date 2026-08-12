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

## Indexed analytic Tranche A supplement

The next native increment adds one indexed general-vector draw for mixed
rectangles, ellipses, and circular rounded rectangles with fill/stroke and an
independent affine transform. It does not change the exact rectangle fast-path
baseline above.

The final Release gate used 512 deterministic mixed primitives on the same
Apple M3 Pro/Metal device. A 5,000-iteration Time Profiler capture reported:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU submission | 0.1011 ms | 0.8792 ms |
| p50 CPU submission | 0.0915 ms | 0.6484 ms |
| p95 CPU submission | 0.1510 ms | 1.9627 ms |
| Worst observed submission | 1.0920 ms | 6.0511 ms |
| Managed allocation total | 7,632 bytes | 11,640,168 bytes |
| Managed allocation / frame | 1.5264 bytes | 2,328.0336 bytes |

The allocation-instrumented 2,000-iteration run reported native/managed p95
submission of 0.1745/2.0198 ms and managed allocations of 3.36/2,328.084 bytes
per frame. The bounded 200-frame synchronized Metal System Trace completed
without a WebGPU validation or process failure. These CPU timings include
command recording and submission, not isolated GPU execution or presentation.

The 512-primitive mixed readback differs only at the rectangle shader
specialization boundary: maximum channel difference 75, 1,922 of 518,400
pixels above 3/255, and mean absolute channel difference 0.025058. At the
supported 4,096-primitive sample boundary those values are 89, 10,338 pixels,
and 0.123854. Ellipse-only and rounded-rectangle-only 4,096-primitive gates
have maximum difference 1 and no pixel above 3/255. The original specialized
solid-rectangle path remains byte-exact.

A DPI-2 differential also passes: 4,096 mixed primitives have maximum channel
difference 83, 5,149 pixels above 3/255, and mean absolute difference 0.056588;
the DPI-2 rectangle fast path and ellipse-only path have maximum difference 1
and no pixel above 3/255. This gate uses 480 by 270 logical units rendered into
the same 960 by 540 physical target.

Additional ignored evidence:

- `native-managed-analytic-time-profiler.trace` and `.json`
- `native-managed-analytic-allocations.trace` and `.json`
- `native-managed-analytic-metal-short.trace` and `.json`

## Indexed geometry Tranche A supplement

The next native increment adds flat-cap lines, filled triangles and
quadrilaterals to the same general-vector pipeline. It includes ordinary
source-space strokes under exact affine outline transformation, one-device-
pixel hairlines, and positive fixed-device strokes. The representative scene
contains 512 deterministic mixed records and submits one indexed draw.

The clean 5,000-iteration Release run on the same Apple M3 Pro/Metal device
reported:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU encode/upload/submit | 0.1229 ms | 1.1416 ms |
| p50 CPU encode/upload/submit | 0.1010 ms | 0.8724 ms |
| p95 CPU encode/upload/submit | 0.2443 ms | 2.2348 ms |
| Worst observed submission | 4.2316 ms | 12.4438 ms |
| Managed allocation total | 12,720 bytes | 11,640,000 bytes |
| Managed allocation / frame | 2.544 bytes | 2,328 bytes |

This is about 9.3 times lower mean CPU submission time and 9.1 times lower p95
for this slice. A separate corrected 1,000-iteration `--sync` run places an
individual device-completion wait inside each renderer's timed interval:
native/managed mean was 1.4140/2.1932 ms and p95 was 1.4622/2.4186 ms. Native
therefore remains about 1.55 times faster by mean and 1.65 times faster by p95
after draining each render independently. This synchronized measurement still
does not include window presentation.

The 512-record readbacks are byte-identical (`7CB04E83AC4674B8` for both).
The 4,096-record and DPI-2 mixed scenes are also byte-identical. The short
96-record CI layout differs at exactly one triangle edge-ownership pixel:
maximum 204/255, one pixel above 3/255, and mean absolute channel difference
0.000179. Hairline, fixed-device, and ordinary anisotropic/sheared line
isolates are byte-exact.

Matched Instruments captures used the same Release workload and .NET 10.0.5.
The 5,000-frame Time Profiler run reported native/managed mean 0.0890/0.9904 ms
and p95 0.1087/2.0990 ms. The 2,000-frame Allocations run reported
7.752/2,328 bytes of managed allocation per frame; its timing is profiler-
perturbed. The 200-frame synchronized Metal System Trace identifies both
`ProGPU native indexed geometry pass` and `Offscreen Compositor Encoder`, has
no command-buffer error row, and reports a combined-process peak Metal
`currentAllocatedSize` of 13.047 MiB. That memory number is not separable by
renderer.

Additional ignored evidence:

- `native-managed-geometry-time-profiler.trace` and exported TOC;
- `native-managed-geometry-allocations.trace` and exported TOC;
- `native-managed-geometry-metal.trace`, exported TOC, labels, command-buffer
  errors, and allocation-size table;
- native, managed, and absolute-difference PPM/PNG images under
  `artifacts/progpu-native/differential/`.

## Indexed Bezier curve Tranche A supplement

The next native increment adds quadratic and cubic Bezier records to the same
indexed geometry batch. The deterministic 512-record scene alternates both
curve orders and covers direct GPU hairline/fixed-device evaluation plus the
adaptive exact-outline route for ordinary anisotropic/sheared strokes.

The clean 5,000-iteration Release run on the same Apple M3 Pro/Metal device
reported:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU encode/upload/submit | 0.6077 ms | 1.4740 ms |
| p50 CPU encode/upload/submit | 0.5987 ms | 1.0487 ms |
| p95 CPU encode/upload/submit | 0.6857 ms | 4.0898 ms |
| Worst observed submission | 1.1144 ms | 5.8367 ms |
| Managed allocation total | 5,904 bytes | 11,640,000 bytes |
| Managed allocation / frame | 1.1808 bytes | 2,328 bytes |

This is about 2.43 times lower mean CPU submission time and 5.96 times lower
p95 for the curve slice. A separate 1,000-iteration synchronized run drains
each renderer within its measured interval: native/managed mean was
3.8158/4.4721 ms and p95 was 6.7292/7.4126 ms. Native is about 1.17 times
faster by mean and 1.10 times by p95 when GPU completion dominates. The
combined native+managed process reported 23,314,432 bytes (22.23 MiB) from
Metal `currentAllocatedSize`; this is not separable per renderer.

The 512-curve readback has maximum channel difference 1/255, no pixel above
3/255, mean absolute channel difference 0.000000482, and hashes
`2AF3B48986292CB2`/`68FC62AC0D4A68BB`. The difference is one channel value in
the entire image. A 4,096-curve DPI-2 stress has maximum 3/255, no pixel above
tolerance, and mean absolute difference 0.000324.

Matched Instruments captures used the same Release binary. The 5,000-frame
Time Profiler run reported native/managed mean 0.6259/1.4395 ms and p95
0.7068/4.0695 ms. The 2,000-frame Allocations run reported 3.048/2,328 bytes
of managed allocation per frame. The 200-frame synchronized Metal trace
reported native/managed mean 3.5193/4.0674 ms and p95 6.5500/7.1007 ms,
identified `ProGPU native indexed geometry pass` and
`Offscreen Compositor Encoder`, and contained no command-buffer error row.

Additional ignored evidence:

- `native-managed-curves-time-profiler.trace` plus JSON and exported TOC;
- `native-managed-curves-allocations.trace` plus JSON and exported TOC;
- `native-managed-curves-metal.trace` plus JSON, exported TOC, labels,
  command-buffer errors, and memory schema;
- curve native, managed, and absolute-difference PPM/PNG images under
  `artifacts/progpu-native/differential/`.
