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

## Capped line and Bezier supplement

The next increment adds independent square, round, and triangle start/end caps
to lines and quadratic/cubic curves while retaining flat as the zero-cost
default. The representative scene contains 512 alternating quadratic/cubic
records, round start caps, triangle end caps, all three stroke-transform modes,
and one indexed draw.

The differential now compares the second fully warmed submission. This was
made explicit after fresh-process stress showed that reading the very first
Metal pipeline draw could alternate between two readback hashes even though
the compiled C++ vertex/index/brush payload was byte-stable. With a real warm
submission, 30 fresh processes retained payload hash `4DCC2E4F746E2ECF` and
native image hash `45609835E15B3FB6` with zero failures.

The clean 5,000-iteration Release run on the same Apple M3 Pro/Metal device
reported:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU encode/upload/submit | 0.8337 ms | 1.7121 ms |
| p50 CPU encode/upload/submit | 0.8206 ms | 1.3330 ms |
| p95 CPU encode/upload/submit | 0.9513 ms | 5.3582 ms |
| Worst observed submission | 2.2052 ms | 8.3111 ms |
| Managed allocation total | 2,256 bytes | 11,640,000 bytes |
| Managed allocation / frame | 0.4512 bytes | 2,328 bytes |

Native is about 2.05 times faster by mean and 5.63 times faster by p95 on the
queue-submission path. Five separate 1,000-frame synchronized runs produced a
median native/managed mean of 3.9701/4.3314 ms, so native remained about 8.3%
faster by mean. Their median p95 was 6.8863/6.5002 ms, leaving native about
5.9% slower at the synchronized tail; this remains an explicit optimization
target rather than being hidden by the much stronger asynchronous result.

The readback has maximum channel difference 1/255, zero pixels above 3/255,
four total channel values of absolute difference, and mean absolute channel
difference 0.000001929. The managed hash is `6BBBC8184210C766`.
The uninstrumented combined-process Metal snapshot was 25,968,640 bytes
(24.77 MiB).

A valid final-binary Time Profiler trace and a valid 200-frame synchronized
Metal System Trace both exited zero. The Metal trace identifies
`ProGPU native indexed geometry pass` and `Offscreen Compositor Encoder`, has
no command-buffer error row, and reached 30,048,256 bytes (28.66 MiB) peak
`currentAllocatedSize` with Instruments overhead. Xcode Allocations launch and
attach modes repeatedly suspended or failed to finalize on this host; those
invalid traces were moved to Trash and no native-allocation-table claim is
made. The exact same cap implementation is still covered by the benchmark's
managed allocation counter, the native sanitizer build, and checked-capacity
C++ tests. A successful Allocations/VM Tracker capture remains a qualification
item before integration.

Retained ignored evidence:

- `native-managed-capped-curves-time-profiler-valid.trace` and exported TOC;
- `native-managed-capped-curves-metal-valid.trace`, exported TOC, labels,
  submissions, completions, GPU intervals, command-buffer errors, and memory;
- `capped-curves-async-5000.json` plus five synchronized JSON runs;
- native, managed, and 64-times-amplified difference PNG images under
  `artifacts/progpu-native/differential/`.

## Retained positioned-glyph supplement

The second Tranche B increment keeps Unicode/OpenType shaping and line layout
as reusable managed CPU results, transfers 96 positioned Inter glyphs and 42
unique analytic outlines once, dispatches production `GlyphRasterizer.wgsl`
into a native-owned R8 atlas, and composites one instanced draw through
production `Text.wgsl`. The cold frame transfers 54,096 outline bytes, 9,216
instance bytes, and uses 247,808 bytes of aligned coverage staging. The DPI-1
fixture exercises the managed small-text 0.25-physical-pixel raster phase. A stable
content revision performs none of those transfers or compute dispatches.

An unbounded asynchronous run made native look 118 times faster at p95
(0.0757 versus 8.9396 ms), but that number includes uneven queue
back-pressure and is not used as the primary CPU claim. The benchmark now
offers `--drain-each-pair`: it submits both renderers in alternating order,
measures each submission, then drains the shared queue outside both measured
intervals. A 3,000-pair Release run on Apple M3 Pro measured:

| Bounded CPU submission | Native C++ | Managed compositor |
|---|---:|---:|
| Mean | 0.1477 ms | 0.2216 ms |
| p50 | 0.1193 ms | 0.1869 ms |
| p95 | 0.3403 ms | 0.4698 ms |
| Managed allocation / frame | 0 bytes | 0 bytes |

Native p95 is 27.6% lower on this bounded submission measurement. The C++
stable hit allocates no glyph vectors and performs no GPU payload upload.

A separate 3,000-frame synchronized run splits the complete interval into
submission and completion phases:

| GPU-complete phase (p95) | Native C++ | Managed compositor |
|---|---:|---:|
| Submission | 0.4067 ms | 0.5554 ms |
| Shared completion wait | 6.4215 ms | 6.4150 ms |
| Total | 6.6634 ms | 6.7586 ms |

The nearly identical completion waits explain why total GPU-complete time is
on par: after retained CPU preparation, both implementations execute the same
coverage/text shaders on the same Metal queue. The result is expected and is
not evidence that managed scene preparation costs equal native preparation.

Enabling the managed whole-scene cache was also measured and rejected for this
isolated retained `DrawingVisual`: with the same bounded queue, managed p95 was
0.2640 ms median with the extra cache versus 0.2220 ms with its existing
retained command cache across three alternating 1,000-pair runs (18.9% worse).
The accepted managed optimization instead makes the 224-byte
frame uniform use the incremental upload shadow; unchanged frames now issue
zero scene-buffer copy operations. Native frame/analytic uniforms similarly
skip unchanged queue writes.

DPI-1 output is byte-exact across all 518,400 pixels with matching
`60F5020BAF0150F4` hashes. The 64-times difference image is entirely black.
DPI-2 is also byte-exact with matching `1306F12A59D53014` hashes.

A separate capacity gate duplicates 1,024 independently keyed DPI-2 Inter
outlines. After seeding a real 1024-square resource, it transactionally grows
the native glyph atlas to 2048 square, publishes generation `2` and growth
count `1`, then proves the next retained replay
performs zero rasterization, coverage staging, outline upload, and instance
upload without changing the generation. Native and managed readbacks remain
byte-exact across all 518,400 pixels with matching `1747555C290A2CC4` hashes.

Final-binary Time Profiler, Allocations/VM Tracker, and synchronized Metal
System Trace captures all completed. The 5,000-pair bounded Time Profiler
workload reported 0.2067/0.2934 ms native/managed p95. The Metal trace contains the
native glyph atlas, coverage pass, positioned-glyph pass, and managed
`Offscreen Compositor Encoder` labels, has zero command-buffer-error rows, and
peaks at 14,139,392 bytes (13.48 MiB) combined-process Metal
`currentAllocatedSize`. That shared-process residency is not attributable to
one renderer. The successful Allocations run preserves its tables; the
benchmark counter under instrumentation reported 4.128/0 managed bytes per
native/managed call, again not a native-heap measurement.

Retained ignored evidence:

- bounded-pair, asynchronous, synchronized, Time Profiler, Allocations, and
  Metal JSON runs under
  `artifacts/progpu-native/profiles/glyphs-quarter-phase-20260812/`;
- matched Time Profiler, Allocations/VM Tracker, and Metal System Trace bundles
  plus exported TOCs/tables in the same directory;
- `glyphs-native.png`, `glyphs-managed.png`, and the exact-zero 64-times
  difference image under `artifacts/progpu-native/differential/`.

## Dashed strokes and retained GPU-complete replay supplement

The seventh native increment adds reusable odd/even dash styles, continuous
open/closed contour walking, separate source/dash caps, transform-correct
normal/fixed/hairline placement, and dashed adaptive splines. The matched
96-polyline scene uses four-point contours, odd `[1.75, 0.9, 0.45]` intervals,
negative phase, round dash caps, and the production WebGPU vector shader.

The first synchronized profile explained why native was initially only on par
with managed: both emitted 170,880 vertices, submitted the same fragment work,
and waited on the same device. CPU expansion also performed an approximate
quadratic triangle-adjacency search for each cap/join. Native measured about
8.50 ms mean / 9.26 ms p95 versus managed 8.16 / 8.83 ms, while managed rebuilt
the retained dashed object graph and allocated 569,472 bytes per frame.

The correction is shared rather than native-only:

- both compilers use constant-time topology edge masks;
- every positive-width round cap is one affine analytic quad (shape 24), not
  eight triangle-SDF quads;
- managed span polylines lazily retain their source and dashed graphs, making
  stable replay allocation-free without eager O(N) recording objects;
- a nonzero native `content_revision` retains compiled CPU geometry and the
  last vertex/index/brush GPU upload. Stable calls still update dimensions/DPI,
  clear, encode the pass, and submit; a changed revision recompiles fully.

The cap change reduces the matched batch from 170,880 to 31,776 vertices
(-81.4%). The benchmark resource snapshot falls from 71,794,688 to 18,399,232
combined Metal bytes (-74.4%). Five paired 1,000-frame synchronized Release
runs, alternating grouped order, produced these median values:

| Metric | Native C++ retained | Managed retained |
|---|---:|---:|
| Mean GPU-complete frame | 1.5055 ms | 2.1268 ms |
| p95 GPU-complete frame | 2.6953 ms | 4.2849 ms |
| Stable managed allocation / frame | 7–9 bytes* | 0 bytes |

Native is 29.2% faster by median mean and 37.1% faster by median p95 values.
One of five noisy synchronized runs favored managed by 7.5% at p95, so this is
not presented as isolated GPU execution superiority: both paths intentionally
run the same 31,776 vertices/47,664 indices. The repeatable gain is retained
CPU compilation/upload work outside the shared GPU draw. A 5,000-frame
queue-path run measured 0.4435/1.7712 ms native/managed p95 (75.0% lower native
p95). `*`The small native-call managed count is intermittent wrapper/runtime activity;
the C++ retained hit performs no geometry allocation or upload.

Readback remains near-exact: maximum channel difference 1/255, zero pixels over
3/255, and mean absolute channel difference 0.00153164. Native and managed
screenshots contain the same stroke coverage; the 64-times difference image
shows only single-byte antialias ties.

The final synchronized Time Profiler trace reduces inclusive native
`append_polyline` samples from 312 in the uncached trace to one and removes
`polyline_capacity` from sampled steady-state stacks. The final Metal System
Trace contains both `ProGPU native indexed geometry pass` and
`Offscreen Compositor Encoder`, has zero command-buffer error rows, and reports
a 20.61 MiB peak combined-process `currentAllocatedSize` under profiling. The
Allocations template again cannot attach because macOS marks the launched .NET
target restricted while SIP is enabled; the failed trace and exact diagnostic
are retained rather than converted into a memory claim.

Retained ignored evidence:

- five synchronized JSON runs and `dashes-async-96-retained-5000.json`;
- pre/post retained Time Profiler traces and exported tables;
- `native-managed-dashes-retained-metal-20260812.trace`, labels, submission,
  completion, error, and current-allocation exports;
- native, managed, and 64-times-amplified difference PNG images under
  `artifacts/progpu-native/differential/`.

## Retained path-atlas GPU-complete supplement

The first Tranche B increment transfers analytic line/quadratic/cubic/resolved
arc segments once, dispatches the production `PathRasterizer.wgsl` from C++,
copies aligned R8 coverage into a native-owned 1024-square atlas, and draws all
instances through the production vector shader. The initial 96-instance cubic
circle workload reuses segment ranges and 64-phase keys; equal keys share one
tile. A stable content revision skips coverage compute and every path,
vertex/index, and brush upload.

A 1,000-frame alternating synchronized Release run on the Apple M3 Pro
measured 1.9444 ms mean / 3.1912 ms p95 native and 1.9995 / 3.2672 ms managed.
Native p95 was 2.3% lower, but the important result is that GPU-complete time is
deliberately close: both paths submit the same 384 vertices/576 indices, sample
equivalent retained coverage, and wait on the same Metal queue. Language choice
cannot make identical GPU execution materially faster. The native benefit is
outside that shared floor: a 1,000-frame asynchronous run measured 0.0450 ms
mean / 0.0845 ms p95 native versus 0.4892 / 1.8930 ms managed by eliminating
managed scene traversal and command compilation from the native submission.

The 960 by 540 DPI-1 readbacks are byte-exact: maximum channel difference zero,
zero differing pixels, and identical `B0DE03008302AB83` FNV-1a hashes. The
separate DPI-2/480-by-270 logical scene is also byte-exact with matching
`2E73084B06A13A6E` hashes, validating physical projection without increasing
ordinary-path atlas resolution relative to the managed compositor.

The final 96-instance cold batch produced 49 unique phase/scale tiles, uploaded
4,112 bytes of path records/uniforms/segments, and used a 727,552-byte aligned
coverage staging buffer; all three are zero on stable replay. Final-binary Time
Profiler and 200-frame synchronized Metal System Trace captures completed
successfully. The Metal trace contains zero command-buffer-error rows and peaks
at 10.47 MiB combined-process `currentAllocatedSize`. This wgpu-native build
publishes internal command-buffer labels rather than ProGPU pass labels to the
Metal table, so the trace is used for error, scheduling, and residency evidence
only. The Allocations template again aborted the instrumented .NET process
(`SIGABRT`, exit 6) before allocation tables were produced; the failed trace is
retained as a diagnostic and no Instruments allocation claim is made.

A separate 1,024-path capacity gate uses independently keyed copies of the
same analytic cubic outline at fixed scale. After seeding a real 1024-square
resource, it transactionally grows the native path atlas to 2048 square,
publishes generation `2`, and proves that the next replay
preserves that generation while issuing zero rasterization, coverage staging,
path, vertex, index, and brush uploads. Its native and managed readbacks are
byte-exact with matching `D83B9C7BC4E00501` hashes.

Retained ignored evidence:

- `paths-native.png`, `paths-managed.png`, and the 64-times-amplified exact-zero
  difference image under `artifacts/progpu-native/differential/`;
- `native-managed-paths-time-20260812.trace`,
  `native-managed-paths-metal-20260812.trace`, exported tables, and the failed
  `native-managed-paths-allocations-20260812.trace` diagnostic;
- JSON output from the 1,000-frame asynchronous and synchronized path runs.

## Adaptive rational-spline supplement

The sixth native increment evaluates B-spline/NURBS control points, knots, and
optional rational weights from borrowed arenas, selects the managed
10/25/50/100 screen-size subdivision policy, and feeds the sampled contour to
the shared connected-stroke compiler. The representative scene contains 512
six-control-point rational cubic splines, all transform/stroke/join modes, open
and closed contours, and one indexed draw. Native and managed output contain
116,204/116,208 vector vertices respectively.

The optimized repeated 5,000-iteration Release run on the same Apple M3
Pro/Metal device reported:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU encode/upload/submit | 2.0981 ms | 2.6349 ms |
| p50 CPU encode/upload/submit | 2.0772 ms | 2.3908 ms |
| p95 CPU encode/upload/submit | 2.3278 ms | 4.8132 ms |
| Worst observed submission | 3.8352 ms | 6.5160 ms |
| Managed allocation total | 2,352 bytes | 11,640,000 bytes |
| Managed allocation / frame | 0.4704 bytes | 2,328 bytes |

Native is about 20.4% faster by mean and 51.6% faster by p95 on the queue path.
An immediately preceding 5,000-frame run measured 2.2214/2.7231 ms mean and
2.7015/4.8389 ms p95, but included one isolated 34.47 ms native frame; the
repeat's 3.84 ms maximum did not reproduce it. The isolated stall is retained
as evidence rather than omitted.

Five paired 1,000-frame synchronized runs showed high system/GPU scheduling
variance. The median per-run native/managed ratio was 1.011 for mean and 1.005
for p95—within 1.1% rather than a demonstrated GPU-complete improvement. The
range was 0.948–1.138 for mean and 0.989–1.229 for p95, so a longer controlled
Metal interval is required before making a spline GPU-time superiority claim.

The 512-spline image differs at one raster edge pixel: maximum channel
difference 17/255, one pixel above 3/255, total absolute channel difference 57,
and mean absolute channel difference 0.00002749. All nine forced
stroke-transform/join combinations pass; six have maximum difference 1/255 or
less, fixed-device round is byte-exact, and ordinary miter has two isolated
edge pixels above 3/255 with mean absolute difference 0.00005642.

Final-binary Time Profiler and 200-frame synchronized Metal System Trace
captures both exited zero. The Metal trace contains `ProGPU native indexed
geometry pass` and `Offscreen Compositor Encoder`, no command-buffer error row,
and a 52,133,888-byte (49.72 MiB) peak combined-process Metal
`currentAllocatedSize`; it cannot attribute that shared-process residency to
one renderer. Xcode Allocations remains unavailable on this host as documented
for the preceding slices, so no native-heap Instruments claim is made.

Retained ignored evidence:

- two optimized 5,000-frame JSON runs and five synchronized 1,000-frame JSON
  runs;
- all nine forced stroke-mode/join JSON differentials;
- `native-managed-splines-time-profiler-valid.trace` and exported TOC;
- `native-managed-splines-metal-valid.trace`, exported TOC, labels,
  command-buffer errors, and allocation-size table;
- native, managed, and 64-times-amplified difference PNG images under
  `artifacts/progpu-native/differential/`.

## Connected solid-polyline supplement

The fifth native increment accepts one borrowed point arena and a compact
descriptor span for open and closed solid polylines. The representative scene
contains 512 four-point contours, mixes all three stroke-transform modes and
all three join kinds, and submits the resulting bodies, caps, and joins as one
indexed draw. The native implementation retains neither caller span.

The clean 5,000-iteration Release run on the same Apple M3 Pro/Metal device
reported:

| Metric | Native C++ | Managed compositor |
|---|---:|---:|
| Mean CPU encode/upload/submit | 0.5223 ms | 1.2462 ms |
| p50 CPU encode/upload/submit | 0.5169 ms | 1.0434 ms |
| p95 CPU encode/upload/submit | 0.5933 ms | 2.5393 ms |
| Worst observed submission | 0.8441 ms | 4.1782 ms |
| Managed allocation total | 0 bytes | 11,640,000 bytes |
| Managed allocation / frame | 0 bytes | 2,328 bytes |

Native is about 2.39 times faster by mean and 4.28 times faster by p95 on the
queue-submission path. Five independent 1,000-frame synchronized runs, each
waiting for device completion inside the measured interval, produced median
native/managed means of 2.6396/3.5481 ms and median p95 values of
3.8879/4.8371 ms. Native therefore remained about 25.6% faster by synchronized
mean and 19.6% faster by synchronized p95.

The 512-contour readback has maximum channel difference 1/255, zero pixels
above 3/255, four total channel values of absolute difference, and mean
absolute channel difference 0.000001929. The compiled native payload hash is
`E30B42132C4EA863`; native/managed image hashes are
`6FE076D0C2EA196C`/`E6F641221BDC0C34`. Forced 96-contour differentials for all
nine stroke-mode/join combinations also pass the same tolerance; six are
byte-exact and the remaining three differ by at most 1/255.

Matched final-binary Time Profiler and synchronized Metal System Trace captures
both exited zero. The Metal trace contains both `ProGPU native indexed geometry
pass` and `Offscreen Compositor Encoder`, has no command-buffer error row, and
reports 21,299,200 bytes (20.31 MiB) peak combined-process Metal
`currentAllocatedSize`; the memory value is not separable by renderer. As with
the cap slice, Xcode Allocations launch/attach is not producing a valid trace on
this host, so no Instruments native-heap claim is made. The benchmark's managed
allocation counter reports zero native-call managed bytes over the clean
5,000-frame run, and ASan/UBSan passes with macOS leak detection disabled
because this ASan runtime explicitly does not support `detect_leaks`.

Retained ignored evidence:

- `polylines-async-5000.json` plus five synchronized JSON runs;
- `native-managed-polylines-time-profiler-valid.trace` and exported TOC;
- `native-managed-polylines-metal-valid.trace`, exported TOC, labels,
  command-buffer errors, and allocation-size table;
- native, managed, and 64-times-amplified difference PNG images under
  `artifacts/progpu-native/differential/`.
