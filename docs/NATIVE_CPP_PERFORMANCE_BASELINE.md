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

## Semantic mixed-scene performance checkpoint

The first d3b1 rendering checkpoint installs one immutable pointer-free scene
and renders analytic, retained-path, positioned-glyph, and upload-backed image
commands through one public native entry point. The compiler preserves order
with one native command buffer/submission for a six-command, four-family
fixture whose analytic and path families each have two distinct resources.
Analytic, path, glyph, and image use distinct retained buffer domains and share
the encoder. Analytic commands use one scene-wide packed vertex/index page;
path commands use one aggregate fill/segment page, retained atlas and
vertex/index payload with per-command index ranges. Glyph and image still need
equivalent distinct-content pages.

The real pinned WebScene `02823bf` / Dawn `710c33013` / Metal provider test
renders an analytic→path→glyph→image→path→analytic stream to a 64 by 48 GPU
canvas, waits for the native submission token, and presents an IOSurface marked
GPU-complete. The repeated analytic command is separated by all other buffer
domains and references a second immutable resource with different geometry and
color. A second path resource has a different transform and magenta color. The
native compiler expands analytic resources into one packed analytic GPU page,
combines both path resources into one retained path/atlas page, and records
per-command ranges, so all six passes remain ordered in one encoder and one
queue submission without overwriting an earlier payload. Exact interior pixel
checks observe the dark clear color plus red and cyan analytic regions, green
and magenta path regions, blue glyph, and yellow image regions in
display-list order. The generated native checkpoint image is
`artifacts/progpu-native/build/progpu-native-semantic-scene.ppm` (with a local
PNG inspection conversion beside it). The managed typed builder separately
writes all four typed family payloads into caller-owned memory with exactly
zero managed bytes over 10,000 builds. A second render of the identical scene
retains the snapshot hash and scene-wide analytic/path pages by immutable scene
hash and DPI, issues no vertex, index, image-texture, or path/glyph coverage
upload, and submits the same six ordered passes in one command buffer. The
analytic page owns separate WebGPU buffers. The path page aggregates all tiles
before encoding and carries a full scene-hash GPU-ownership marker, so later
standalone path use is detected and restored rather than relying on a colliding
32-bit revision.
Changed-page compilation is O(C + A) time and O(Ca + A) storage for C scene
commands, Ca analytic commands, and A expanded vertices/indices; stable replay
performs O(C) dispatch with O(1) engine-owned auxiliary storage and zero
analytic payload upload.
Changed path-page assembly and compilation is O(C + P + S + K) time with
O(P + S + K) retained CPU/GPU storage for P fills, S segments, and K expanded
draw/coverage bytes. Stable path replay is O(C) dispatch and zero path,
coverage, vertex, or index upload.

The six-command matched benchmark retains the same 384-item pixel contract:
284 of 518,400 pixels exceed 3/255, maximum difference is 68/255, and mean
absolute difference is 0.003582658179/255. Across the stable later-power-state
cluster `run-{4,5,6}.json`, median p95 native/managed values are 2.6744/2.6334
ms end to end (native 1.6% higher), 0.1844/0.1975 ms submission (native 6.6%
lower), and 2.5414/2.5237 ms GPU-completion wait (within 0.8%). Both routes
allocate 0 managed bytes per measured frame; every native frame reports six
draws/family entries, one submission, and zero stable vertex, index, texture,
or coverage upload. `run-3.json` captured the machine transition into the
higher completion-wait state; both renderers then moved together, so the
matched stable cluster rather than that one-sided transition sample is used for
the comparison. All six raw distributions remain under
`artifacts/progpu-native/performance/semantic-path-packed-page/`.

The same hardware gate then installs a structurally valid scene whose fourth
image draw contains a non-finite opacity. Whole-scene value preflight rejects
it before submission, preserves the prior submission token, and the subsequent
IOSurface pixel verification proves that no earlier draw or clear reached the
target.

The managed/native application harness exposes `--semantic-scene` for the same
substitution boundary at 960 by 540. It retains four quadrant-local families
through the managed production visual tree and installs the equivalent
six-resource pointer-free native snapshot once. Every measured native frame
must report six ordered draws, six family entries, one command buffer/queue
submission, and zero stable vertex, index, image, and coverage uploads. Both
measured routes allocate exactly zero managed bytes after warm-up. The native,
managed, and 64-times-amplified difference captures are written as
`semantic-scene-{native,managed,difference-64x}.ppm`.

The representative 384-item Apple M3 Pro gate changes 284 of 518,400 pixels
beyond 3/255, with maximum 68/255 and mean absolute difference 0.003582658/255
per channel, confined to independent path/glyph edge-coverage ties. The mixed
budget is maximum 96/255, no more than 0.1% of pixels beyond 3/255, and mean no
more than 0.005/255; stricter single-family contracts are unchanged. macOS,
Linux, and runnable Windows native build lanes execute this mode as an
integration smoke.

Before the distinct-resource split, three independent paired 600-frame
synchronized runs after 120 warm-ups produced these optimized median p95
values:

| Metric | Native C++ | Managed compositor | Native delta |
| --- | ---: | ---: | ---: |
| CPU encode/upload/submit | 0.1577 ms | 0.2222 ms | 29.0% lower |
| GPU-completion wait | 1.5331 ms | 1.5284 ms | within 0.4% |
| Synchronized end to end | 1.6817 ms | 1.7417 ms | 3.4% lower |
| Stable managed allocation | 0 B/frame | 0 B/frame | equal |

The matched pre-optimization distributions reported 21,504 stable vertex and
2,304 stable index upload bytes and a median native/managed end-to-end p95
ratio of 1.068. The optimized distributions report zero for both uploads and a
ratio of 0.966. Absolute timings varied with machine load, so the paired ratio
and retained-upload counters are the regression signals.

The earlier analytic-only packed-page checkpoint repeated the same
three-by-600 protocol with two distinct analytic resources and five ordered
draws. Its median p95 values were:

| Metric | Native C++ | Managed compositor | Native delta |
| --- | ---: | ---: | ---: |
| CPU encode/upload/submit | 0.2416 ms | 0.2685 ms | 10.0% lower |
| GPU-completion wait | 1.5436 ms | 1.5385 ms | within 0.4% |
| Synchronized end to end | 1.7963 ms | 1.7797 ms | within 1.0% |
| Stable managed allocation | 0 B/frame | 0 B/frame | equal |

Every run reports five commands, five draws, five family entries, one
submission, and zero stable vertex, index, texture, or coverage upload. The
native end-to-end samples span both sides of managed across the three runs, so
the 0.9% median difference is classified as on par rather than a regression or
improvement. Raw reports are under
`artifacts/progpu-native/performance/semantic-analytic-packed-page/run-{1,2,3}.json`.

Correlated final-binary Time Profiler, Allocations plus VM Tracker, and Metal
System Trace captures completed before and after. In the Metal capture, native
submission p95 changed from 1.0546 ms versus managed 0.3757 ms to 0.2981 ms
versus managed 0.3293 ms. Optimized completion waits are 1.5340 and 1.5341 ms,
which confirms that GPU-complete work is on par after removing the native CPU
upload deficit. Both Metal traces contain zero command-buffer errors and have
the same 15,941,632-byte peak `currentAllocatedSize`; the whole-process
Allocations/VM traces are retained for ownership inspection and are not
misattributed to one renderer.

Retained ignored evidence:

- `semantic-scene-1b7578c5/paired-sync-{1,2,3}.json` and
  `semantic-scene-1b7578c5/analytic-cache-after/run-{1,2,3}.json`;
- before/after Time Profiler, Metal System Trace, and Allocations plus VM
  Tracker captures and compact table exports under
  `artifacts/progpu-native/performance/semantic-scene-1b7578c5/instruments/`;
- inspected semantic native, managed, and 64-times difference PNGs under
  `artifacts/progpu-native/differential/`.

This checkpoint does not close d3b1. Identical analytic repeats coalesce;
distinct analytic and path payloads share retained packed pages without a
flush. Distinct glyph and image payloads still require retained atlas/buffer
and texture pages. Stable native-allocation counters also remain open.
Whole-scene preflight checks a maximum 16,384 draw passes, 256 MiB of
expanded vertices, 64 MiB of indices, 256 MiB each of textures and aligned
coverage staging, and 512 MiB across those compiled domains. Accumulation uses
checked 64-bit arithmetic and runs in O(C + V) time for C commands and V typed
values with O(1) budget storage. A valid 16,385-draw stream fails with
`OUT_OF_MEMORY` before encoder creation, preserves the submission timeline,
and leaves the target unchanged. State resources and isolated layers remain
d3b2.

## Root-group blend/compositing supplement

This slice appends all 29 `GpuBlendMode` values to the retained native draw
state. Exact Porter-Duff/coefficient equations remain a one-pass
fixed-function WebGPU composite. The 15 destination-aware modes resolve a
bounded retained source and use one static `GroupBlend.wgsl` pipeline. The
managed reference was also optimized: it now selects its advanced mode through
a 32-byte uniform rather than allocating a newly substituted WGSL string on
every frame, caches the pipeline pointer, and uses a non-boxing texture-usage
test. Stable managed allocation fell from 30,235.2 bytes/frame to exactly zero.

All six retained families pass both the `SrcAtop` fixed-function and `Overlay`
destination-aware routes on Apple M3 Pro / Metal. `SrcAtop` is byte-exact for
solid, path, glyph, and image scenes; analytic coverage stays at maximum
51/255 and mean 0.007761/255, and independent geometry has one 204/255 edge-tie
pixel. `Overlay` is byte-exact for solid, path, glyph, and image scenes;
analytic coverage differs by at most 1/255 with mean 0.000147/255, while
geometry has one 4/255 pixel and mean 0.0000034/255. A separate solid sweep is
byte-exact for every one of the 29 modes. Both renderers allocate zero managed
bytes per stable frame in every case.

Three independent synchronized 600-frame Overlay runs after 120 warm-ups,
using 384 retained solid rectangles at 960 by 540, produced these median p95
values:

| Metric | Native C++ | Managed compositor | Native delta |
| --- | ---: | ---: | ---: |
| CPU submission | 0.0624 ms | 0.3164 ms | 80.3% lower |
| GPU-completion wait portion | 3.0495 ms | 4.5704 ms | 33.3% lower |
| Synchronized end to end | 3.0892 ms | 4.7911 ms | 35.5% lower |
| Stable managed allocation | 0 B/frame | 0 B/frame | equal |

The completion difference is a graph difference, not a language-speed claim.
Native stable replay retains the already resolved group source and submits one
advanced composite. The current managed command path still resolves its
bounded texture draw, evaluates the advanced blend into ping-pong output, and
copies the final texture back to the caller-owned target. It is therefore the
next managed-compositor optimization candidate; removing those passes requires
a retained semantic group/backdrop contract rather than bypassing correctness.

The representative solid readback is byte-exact after quantizing the uniform
backdrop through the target's `Rgba8Unorm` representation before nonlinear
blend evaluation. Native and managed hashes are identical, and the inspected
64-times difference image is black.

Final-binary Time Profiler, Allocations/VM Tracker, and Metal System Trace
captures all completed. The Metal trace contains `ProGPU native advanced
group-blend composite pass`, `ProGPU native retained group replay encoder`, and
managed `Offscreen Compositor Encoder` labels; it reports 2,783 submissions,
3,196 completions, zero command-buffer errors, zero compiler spills, zero
drawable waits/hang signals, and a 22,528,000-byte peak combined-process Metal
allocation. The allocation export recorded 16,928,416 persistent heap bytes
and 116,162,560 persistent anonymous-VM bytes for the whole instrumented .NET,
wgpu-native, Metal, and tool process; those totals are not attributed to one
renderer. All 766 observed Metal resources were deallocated before capture
end.

Retained ignored evidence:

- `group-blend-matrix/*.json` for the 12 fixed/advanced family gates;
- `group-blend-distributions/overlay-solid-sync-600-run-{1,2,3}.json`;
- valid Allocations, Time Profiler, and Metal System Trace bundles plus compact
  exports under `artifacts/progpu-native/traces/group-blend-20260813/`;
- native, managed, and 64-times difference PNGs under
  `artifacts/progpu-native/differential/png/`.

## Pooled frame-group opacity checkpoint

The append-only 40-byte draw-state record adds true group opacity and an
optional caller-owned group revision. Every native family can render into one
transparent target-format texture and composite that premultiplied result once
over the clear color. The outer rectangular clip is applied to the composite,
not to family content. The engine retains one texture/view/bind group per
compositor, reallocates it only on size change, and publishes separate layer
metrics so existing per-family records remain ABI-compatible.

The exact WebScene/Dawn/Metal provider test draws two identical overlapping
opaque rectangles into a 25% group, then changes only group opacity to 50%.
The first frame reports one content pass, one composite pass, one allocation,
and no cache hit. The second reports zero family draws/uploads, zero content
passes, one composite pass, the same allocation count, and a cache hit. Its
224-byte quad update is the only payload change; a following unchanged replay
uploads zero bytes. The final IOSurface pixel is the once-composited 50% group,
not the 75% result that per-primitive alpha would produce.

Short Release differential gates (two warmups, four measured frames) exercise
all six families with retained group replay on Apple M3 Pro/Metal. These runs
are functional gates, not final performance distributions:

| Family | Pixel result | Stable native/managed allocation | Layer state |
|---|---|---:|---|
| solid rectangles | byte-exact | 0 / 0 B per frame | one 2,073,600-byte texture, cache hit |
| indexed analytic | existing bounded AA differential; mean `0.004361/255` | 0 / 0 B per frame | same |
| indexed geometry | byte-exact | 0 / 0 B per frame | same |
| retained paths | byte-exact | 0 / 0 B per frame | same |
| positioned glyphs | byte-exact | 0 / 0 B per frame | same |
| retained RGBA image | byte-exact | 0 / 0 B per frame | same |

Three paired 300-frame synchronized 384-solid runs after 60 warmups produced
these median p95 values:

| Metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| CPU encode/upload/submit | 0.1748 ms | 0.2207 ms | -20.8% |
| GPU-completion wait portion | 3.0459 ms | 3.0465 ms | on par |
| end-to-end synchronized | 3.0912 ms | 3.1303 ms | -1.2% |
| stable managed allocation | 0 B/frame | 0 B/frame | equal |

All three runs are byte-exact with native/managed hash
`90172B40F34BDA56`. Valid final-binary 2,000-frame Time Profiler and
Allocations/VM Tracker traces, plus a synchronized 200-frame Metal System
Trace, exited zero. The Metal trace records the native pooled texture,
retained-group replay encoder, group composite pass and managed offscreen
encoder labels, 1,587 submissions, 2,522 completions, zero command-buffer
errors, and 16.92 MiB peak combined-process `currentAllocatedSize`. That
residency remains shared and is not attributed to either renderer. The
Allocations capture contains both Allocations and VM Tracker tracks.

Retained ignored evidence:

- `group-opacity/sync-{1,2,3}.json` and six per-family gate JSON files under
  `artifacts/progpu-native/benchmarks/`;
- final-binary Time Profiler, Allocations/VM Tracker, and Metal System Trace
  bundles with zero-exit TOCs and exported Metal tables under
  `artifacts/progpu-native/traces/group-opacity-20260813/`;
- byte-exact native/managed group-opacity screenshots and the black amplified
  difference image under `artifacts/progpu-native/differential/`.

## Common group-mask functional checkpoint

The additive common-mask ABI is exercised after full warm-up across all six
native frame families with a borrowed sampled texture, an analytic rounded
rectangle, and a retained two-node vector clip chain. The vector chain
intersects a transformed cubic ellipse and subtracts an independently
sheared/rotated cubic ellipse. These short two-warmup/four-frame Release runs
are correctness and retained-state gates, not final performance distributions.

| Family | Sampled mask max / mean | Rounded mask max / mean | Vector chain max / mean |
|---|---:|---:|---:|
| solid rectangles | `1 / 0.064451` | `1 / 0.011426` | `58 / 0.042608` |
| indexed analytic | `33 / 0.019187` | `51 / 0.041802` | `48 / 0.021660` |
| indexed geometry | `91 / 0.010416` | `204 / 0.020250` | `204 / 0.009975` |
| retained paths | `1 / 0.026072` | `1 / 0.004321` | `47 / 0.017094` |
| positioned glyphs | `1 / 0.001176` | `1 / 0.001303` | `36 / 0.001020` |
| retained RGBA image | `1 / 0.041750` | byte-exact | `59 / 0.034652` |

Values are maximum and mean absolute 8-bit channel differences against the
managed compositor. The one high geometry value is the already bounded single
edge-ownership pixel; mask quantization introduces no pixel beyond the
three-channel-value common-mask tolerance. Both paths allocate one retained
960-by-540 RGBA layer (2,073,600 bytes), then report one composite pass, a
content-cache hit, no content pass, a mask bind-group cache hit, zero stable
mask/uniform upload, and zero managed allocation per frame. The vector route
additionally reports a clip-cache hit, two retained paths, zero stable clip
passes, zero clip/path/coverage upload, and 2,603,776 retained mask bytes (one
1,024-square R8 atlas plus three 960-by-540 R8 textures). Its independently
rasterized AA edge is bounded to 64/255 per channel, fewer than one percent of
pixels beyond 3/255, and mean absolute error below 0.075/255 per channel; the
amplified difference remains restricted to the two clip boundaries.

The harness separately mutates only mask state while retaining the family
content revision. Texture/rounded mutation uploads exactly one 96-byte mask
uniform and no family content. Vector revision mutation rerasterizes and
recomposes the two-node mask while preserving the family layer; restoring and
replaying the unchanged revision reports a clip-cache hit with zero clip passes
or uploads.

Three paired 300-frame synchronized runs after 60 warmups produced these
median p95 values:

| Mask | Metric | Native C++ | Managed compositor | Native delta |
|---|---|---:|---:|---:|
| sampled texture | CPU submit | 0.0990 ms | 0.4581 ms | -78.4% |
| sampled texture | GPU-completion wait | 3.0382 ms | 4.5299 ms | -32.9% |
| sampled texture | end to end | 3.1038 ms | 4.7161 ms | -34.2% |
| analytic rounded | CPU submit | 0.0971 ms | 0.3646 ms | -73.4% |
| analytic rounded | GPU-completion wait | 3.0395 ms | 3.0496 ms | on par |
| analytic rounded | end to end | 3.0915 ms | 3.3347 ms | -7.3% |

Native measured zero managed bytes per synchronized frame. The managed
interval measured 2,328 bytes because its current completion observer allocates
under `PollDevice(wait: true)`; the asynchronous renderer-only path is zero for
both. The sampled-mask managed path currently rasterizes an intermediate R8
mask, explaining why two of its three GPU-wait runs form the slower mode. This
is a managed-path optimization target, not attributed to the native renderer.

Matched native, managed, and 64-times-amplified difference PNGs were inspected
for both masks. Both maximum channel differences are one. The sampled mask has
mean absolute difference `0.058975/255`; the rounded mask has
`0.011220/255`.

Valid final-binary Instruments captures cover the sampled-mask workload. The
5,000-frame Time Profiler run exits zero and shows native retained-group
preparation/composite below wgpu-native render-pass and queue costs; no new
per-frame mask upload or bind-group creation appears. The 2,000-frame
Allocations capture exits zero and contains both Allocations and VM Tracker
tracks. The 200-frame synchronized Metal System Trace exits zero, records
native and managed compositor labels, 2,379 submissions, 3,339 completions,
zero command-buffer errors, and a 15.34 MiB peak combined-process Metal
`currentAllocatedSize`. That shared-process peak is not attributed to either
renderer.

Retained ignored evidence:

- six synchronized JSON distributions under
  `artifacts/progpu-native/benchmarks/group-masks/`;
- matched PNGs under
  `artifacts/progpu-native/differential/group-masks/`;
- Time Profiler, Allocations/VM Tracker, Metal System Trace, exported TOCs,
  sampled stacks, labels, submissions, completions, errors, and Metal residency
  under `artifacts/progpu-native/traces/group-masks-20260813/`.

### Retained vector clip-chain distribution and profile

Three independent paired 300-frame synchronized runs, each after 60 warmups,
used the 384-solid scene and the same two-node transformed intersect/difference
clip chain as the functional matrix. Median p95 values across the three runs
were:

| Metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| CPU encode/upload/submit | 0.0613 ms | 0.4201 ms | -85.4% |
| GPU-completion wait portion | 3.0259 ms | 4.5256 ms | -33.1% |
| end-to-end synchronized | 3.0576 ms | 4.7042 ms | -35.0% |

Native measured zero managed bytes per synchronized frame. The managed
completion-observer interval measured a median 2,380.4 bytes per frame; this is
the existing `PollDevice(wait: true)` observation cost and is not attributed to
the asynchronous renderer-only path. The native stable replay retained the
composed clip texture and reported one final composite pass, zero content
passes, zero clip raster/composition passes, zero clip/path/coverage uploads,
and a clip-cache hit. The GPU-completion portions converge in the fastest run
(3.0259 ms native and 3.0747 ms managed) because both paths share the same
queue and device. The managed distribution also contains a slower completion
mode; the measured native advantage is therefore reported as a three-run
median rather than inferred from a single trace.

The 518,400-pixel long-run comparison has maximum channel difference 57,
2,294 pixels (0.443%) beyond 3/255, and mean absolute channel difference
`0.037058/255`. Native, managed, and 64-times-amplified difference images were
inspected; differences remain confined to independently rasterized clip edges
and the existing one-byte primitive edge.

Matched final-binary Instruments captures used the same Release binary. The
5,000-frame Time Profiler run, 2,000-frame Allocations/VM Tracker run, and
synchronized 200-frame Metal System Trace all exited zero. The Metal trace
records one retained native clip atlas, two accumulation textures, one node
mask, one composition pass, and one coverage pass allocation rather than
per-frame resource creation; it contains 2,434 submission rows, 3,368
completion rows, zero command-buffer-error rows, and a 19.78 MiB peak
combined-process Metal `currentAllocatedSize`. The shared-process peak is not
attributed to either renderer. The synchronized trace itself measured p95
submission/completion/end-to-end values of 0.1288/3.0333/3.1195 ms native and
0.4427/4.5307/4.7786 ms managed.

Retained ignored evidence:

- `vector-clips/sync-{1,2,3}.json` under
  `artifacts/progpu-native/benchmarks/`;
- native, managed, and amplified-difference PNGs under
  `artifacts/progpu-native/differential/`;
- Time Profiler, Allocations/VM Tracker, Metal System Trace, exported TOCs,
  sampled/aggregated stacks, labels, submissions, completions, errors, and
  Metal residency under
  `artifacts/progpu-native/traces/vector-clips-20260813/`.

### Retained Gaussian group-effect distribution and profile

The next native checkpoint applies one anisotropic Gaussian blur to the pooled
result of all six frame families. Effect and family-content revisions are
independent: an effect-only mutation reuses family pixels and records two
separable compute passes, while a stable revision reuses the blurred texture
and records no compute pass. Both native and managed implementations embed the
same production horizontal/vertical WGSL resources.

Three independent paired 300-frame synchronized runs followed 60 warmups on
the same Apple M3 Pro/Metal device. The representative scene used 384 solid
rectangles and sigma 6. Median p95 values were:

| Workload and metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| Stable CPU submit | 0.2328 ms | 0.2997 ms | -22.3% |
| Stable GPU-completion wait | 3.0517 ms | 3.0311 ms | +0.7% |
| Stable end to end | 3.1402 ms | 3.1025 ms | +1.2% |
| Recomputed CPU submit | 0.1756 ms | 0.3877 ms | -54.7% |
| Recomputed GPU-completion wait | 6.0621 ms | 6.0538 ms | +0.1% |
| Recomputed end to end | 6.1507 ms | 6.3140 ms | -2.6% |

The stable GPU-complete paths are intentionally on par: both reuse the final
blur output, target the same queue/device, and perform one final texture
composite. Recomputed completion is also on par because both execute the same
two memory-bandwidth-dominated kernels. The native advantage is at the CPU
boundary: it reuses retained family content and encodes both compute passes and
the composite in one command buffer. The managed effect path still performs
its existing source, compute, and main-composite submissions.

Native measured zero managed bytes in both submission and completion intervals.
The recomputed managed submission initially measured 2,552 bytes/frame.
Replacing two per-frame blur command-label marshalling allocations with static
UTF-8 spans reduced that to 2,328 bytes/frame (-8.8%) without dropping Metal
labels; its completion observer measured zero additional bytes in this runner.
The shared Gaussian shaders now derive tap weights from two `exp` evaluations
and a multiplicative recurrence instead of one `exp` per tap. Matched
before/after distributions show no repeatable completion-time shift because
the workload is dominated by texture reads and queue/GPU scheduling; the
change reduces shader transcendental instruction count but is not presented as
a measured latency win.

All six 518,400-pixel differentials pass. Solid rectangles, retained paths,
positioned glyphs, and retained images are byte-exact. Analytic primitives have
maximum difference 7/255 and mean absolute difference `0.006746/255`;
indexed geometry has maximum difference 8/255 and mean `0.000183/255`, confined
to the independently rasterized source edges. The inspected sigma-6 solid
native/managed images are byte-identical and their 64-times difference image
is black.

Final-binary Xcode Allocations/VM Tracker, Time Profiler, and synchronized Metal
System Trace captures are retained with this checkpoint. All three workloads
exited zero. The Allocations trace recorded Heap and VM allocation mode, but
this command-line export exposes no allocation table on this macOS build, so
the per-interval benchmark counters above remain the allocation evidence. The
Metal trace contains both managed and native horizontal/vertical Gaussian
labels, 5,236 submission rows, 6,405 completion rows, zero command-buffer-error
rows, and a 25.359 MiB peak combined-process Metal `currentAllocatedSize`.
That shared value is not attributed to either renderer.

Retained ignored evidence:

- six final 300-frame JSON distributions plus the allocation-split and
  before/after recurrence runs under `artifacts/progpu-native/benchmarks/`;
- native, managed, and 64-times-amplified difference PNGs under
  `artifacts/progpu-native/differential/`;
- Time Profiler, Allocations/VM Tracker, Metal System Trace, exported TOCs,
  samples, labels, submissions, completions, errors, and Metal residency under
  `artifacts/progpu-native/profiles/group-gaussian-final-20260813/`.

### Retained drop-shadow group-effect distribution and profile

The next checkpoint composes a source-alpha drop shadow over the pooled result
of every native frame family. A changed effect records horizontal/vertical
blur plus one offset/tint/source-over compute pass. A stable content/effect
revision records no compute pass and performs one final group composite. The
native path retains two full-target effect textures (`8*W*H` bytes); the new
composition pass reuses the first texture as its output rather than allocating
a third full-target surface.

Three independent paired 600-frame synchronized runs followed 120 warmups on
the same Apple M3 Pro/Metal device. The representative scene used 384 solid
rectangles, sigma 2, offset `(7.5, 5.25)`, and RGBA color
`(0.08, 0.16, 0.32, 0.72)`. Median p95 values were:

| Workload and metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| Stable CPU submit | 0.1654 ms | 0.2102 ms | -21.3% |
| Stable GPU-completion wait | 3.0497 ms | 3.0534 ms | -0.1% |
| Stable end to end | 3.1209 ms | 3.1441 ms | -0.7% |
| Recomputed CPU submit | 0.3240 ms | 0.6851 ms | -52.7% |
| Recomputed GPU-completion wait | 6.0644 ms | 6.0684 ms | -0.1% |
| Recomputed end to end | 6.1592 ms | 6.3440 ms | -2.9% |

The GPU-complete paths are deliberately on par because both implementations
run equivalent separable blur and source-over work on the same queue/device.
The native advantage remains CPU encoding: retained content and bindings are
reused and the three effect passes plus final composite are encoded without
managed effect-object traversal. Native submission measured zero managed bytes
per frame. Replacing the managed shadow encoder/buffer label allocations with
static UTF-8 spans reduced recomputed managed submission from 2,552 to 2,328
bytes/frame (-8.8%) while preserving Metal labels. The shadow kernels now use
two `exp` evaluations and the same multiplicative Gaussian recurrence as the
group-blur kernels; no latency improvement is claimed independently of the
matched final effect comparison.

All six 518,400-pixel differentials pass. Solid rectangles, paths, glyphs, and
images differ by at most 2/255 with no pixel beyond 3/255. Analytic primitives
have maximum difference 52/255, 573 pixels (0.111%) beyond 3/255, and mean
absolute difference `0.076091/255`. Indexed geometry has one independent
raster-edge tie at 204/255 and mean `0.037806/255`. The representative solid
comparison has maximum difference 1/255 and mean `0.041095/255`; native,
managed, and 64-times-amplified difference images were inspected.

The Ubuntu ARM64 `llvmpipe` Vulkan gate resolves analytic source-coverage
edge ties differently and measured maximum `52/255`, 571 pixels (0.110%)
beyond `3/255`, and mean `0.110293/255`. The cross-architecture contract keeps
the same `64/255` maximum and 1% changed-pixel limits while allowing
`0.125/255` mean only for analytic-source drop shadows; all other drop-shadow
families retain the stricter `0.100/255` mean limit. This is an edge-AA budget,
not a relaxation of the shared blur or source-over composition contract.

Final-binary Time Profiler, Allocations plus VM Tracker, and Metal System Trace
workloads all exited zero. The Metal trace contains native horizontal,
vertical, drop-shadow composition, and final-composite labels; 6,028 submission
rows, 7,208 completion rows, zero command-buffer-error rows, and a 23.812 MiB
peak combined-process Metal `currentAllocatedSize`. The Allocations trace again
contains heap/VM instrumentation but exposes no allocation table through this
command-line TOC, so the benchmark's interval counters remain the managed
allocation evidence. The trace-observed memory is shared between both renderers
and is not attributed to either implementation.

Retained ignored evidence:

- six-family JSON plus stable/recomputed paired distributions under
  `artifacts/progpu-native/benchmarks/group-drop-shadow-20260813/` and
  `artifacts/progpu-native/benchmarks/group-drop-shadow-final-20260813/`;
- native, managed, and 64-times-amplified difference PNGs under
  `artifacts/progpu-native/differential/`;
- Time Profiler, Allocations/VM Tracker, Metal System Trace, exported TOCs,
  labels, submissions, completions, errors, and Metal residency under
  `artifacts/progpu-native/profiles/group-drop-shadow-final-20260813/`.

### Bounded retained effect-chain distribution and profile

The next checkpoint evaluates an immutable two-node Gaussian-blur then
source-alpha drop-shadow chain as the representative member of the new
one-to-eight-node bounded lane. A changed chain encodes five compute passes;
stable content and chain revisions encode none. Three full-target RGBA8
intermediates (`12*W*H` bytes) are reused without binding any one texture as a
sampled input and storage output in the same pass. The independently nested
managed comparator applies the same inner-to-outer effect order.

Three independent paired 600-frame synchronized runs followed 120 warmups on
the same Apple M3 Pro/Metal device. Median p95 values were:

| Workload and metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| Stable CPU submit | 0.1805 ms | 0.2138 ms | -15.6% |
| Stable GPU-completion wait | 3.0537 ms | 3.0607 ms | -0.2% |
| Stable end to end | 3.1504 ms | 3.1711 ms | -0.7% |
| Recomputed CPU submit | 0.3022 ms | 0.4455 ms | -32.2% |
| Recomputed GPU-completion wait | 6.0873 ms | 6.0815 ms | +0.1% |
| Recomputed end to end | 6.2272 ms | 6.2672 ms | -0.6% |

Both paths measured zero managed bytes per frame after warmup. GPU completion
remains on par because the same queue executes equivalent five-pass,
bandwidth-dominated work; the useful separation is the native submission
boundary. A longer 5,000-frame recompute run reported 0.2868/0.3864 ms native/
managed submission p95 and 6.1636/6.1524 ms end-to-end p95, reinforcing the
CPU-bound difference without claiming GPU-complete superiority.

All six 518,400-pixel differentials pass. Solid and path output differ by at
most 2/255, analytic and indexed geometry by at most 8/255, and image output by
at most 1/255. No solid, path, glyph, or image pixel exceeds 3/255; analytic
has 261 such edge pixels and geometry has 21. Mean absolute channel difference
ranges from `0.010371/255` for images through `0.092420/255` for analytic
coverage, below the explicit `0.125/255` chain bound. The representative
native, managed, and amplified-difference images were inspected.

The cross-platform gate retains that `0.125/255` mean bound for five families
and uses `0.130/255` for independently rasterized analytic source coverage.
Linux arm64 llvmpipe measured `0.125165/255`, maximum `7/255`, with only 218 of
518,400 pixels above `3/255`; Linux x64 llvmpipe measured `0.114635/255` for
the same case. The architecture-specific edge-tie allowance does not relax the
maximum-difference or one-percent changed-pixel gates.

Final-binary Time Profiler and synchronized Metal System Trace captures exited
zero. The Metal trace contains native bounded-chain horizontal, vertical, and
drop-shadow labels plus the managed `Offscreen Compositor Encoder`; it records
7,349 submission rows, 8,762 completion rows, zero command-buffer-error rows,
and a 32.281 MiB peak combined-process Metal `currentAllocatedSize`. That
residency includes profiler overhead and both renderers, so it is not attributed
to either implementation. The Xcode Allocations launch and attach routes both
left the target suspended before recording on this host; those failed trace
bundles were removed and no Instruments heap claim is made. The 5,000-frame
final-binary interval counter remains the allocation evidence. This is an open
profiling-gate limitation, so the PR milestone stays at evidence-running until
a valid Allocations/VM capture or equivalent Xcode fix is available.

Retained ignored evidence:

- six-family differential JSON and three stable/recomputed distributions under
  `artifacts/progpu-native/effect-chain/`;
- native, managed, and 64-times-amplified chain screenshots under
  `artifacts/progpu-native/differential/`;
- valid Time Profiler and Metal System Trace bundles, exported TOCs/tables,
  labels, submissions, completions, errors, residency, and the long allocation
  counter under `artifacts/progpu-native/profiles/effect-chain-20260813/`.

## Common draw-state supplement

The ABI-v3 append-only draw-state increment applies primitive opacity and one
logical target clip across solid rectangles, analytic primitives, retained
geometry, paths, positioned glyphs, uploaded images, same-device external
images, and externally masked images. A legacy frame prefix still renders with
opacity one and the full target. The WebScene/Dawn/Metal provider gate exercises
an unknown flag rejection, an empty clip that clears/submits without a draw,
and a `1.5` DPI logical clip converted to a physical scissor.

All seven matched Release differentials pass. Paths and glyphs are byte-exact.
The retained/external image lanes differ only on 528 one-pixel clip-perimeter
pixels because the managed compiler clips the quad and recomputes boundary UVs,
while native leaves interpolation unchanged and uses fixed-function scissoring;
their mean absolute channel error remains at most `0.04473/255`. The 384-solid
representative is byte-exact (`F2A587875EA36087`) and both asynchronous stable
render paths allocate zero managed bytes per frame.

Three paired 300-frame synchronized runs on the Apple M3 Pro/Metal device,
after 60 warmups per run, produced these median p95 values:

| Metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| CPU encode/upload/submit p95 | 0.3028 ms | 0.4483 ms | -32.5% |
| GPU-completion wait portion p95 | 3.0585 ms | 3.0507 ms | +0.3% |
| End-to-end synchronized p95 | 3.1789 ms | 3.3112 ms | -4.0% |

The completion portion is deliberately reported separately: both paths submit
to the same queue and are effectively on par once queue/GPU scheduling
dominates. Native retains the CPU submission advantage and a modest end-to-end
p95 advantage without a pixel difference. Native measured zero managed bytes
in the synchronized interval; managed measured 2,328 bytes per frame because
the interval includes its current `PollDevice(wait: true)` completion-observer
path. The asynchronous renderer-only suite is zero-allocation for both.

State-only opacity mutation is actively gated. Under an unchanged content
revision, geometry/path update only packed brush bytes, glyphs update only
instance alpha from retained source alpha, and images update four vertices.
The test rejects any geometry/index rebuild, path/glyph rerasterization,
coverage staging, outline upload, or source-texture upload; the following
unchanged replay must report zero payload upload and zero managed allocation.

Final-binary Time Profiler, Metal System Trace, and Allocations/VM Tracker
captures all completed for the same draw-state workload. The Metal trace
contains `ProGPU native solid rectangle pass`, `ProGPU native frame encoder`,
and managed `Offscreen Compositor Encoder` labels, reports zero command-buffer
errors, and reaches 13,254,656 bytes (12.64 MiB) peak combined-process Metal
`currentAllocatedSize`. That shared-process residency is not attributable to
one renderer. The Allocations/VM Tracker capture is valid and retains its UI
tracks, but this Instruments version exposes no allocation-table export schema;
therefore no native-heap delta is claimed from it.

Retained ignored evidence:

- `draw-state-sync-{1,2,3}.json` and per-family `draw-state-*.json` under
  `artifacts/progpu-native/benchmarks/`;
- matched Time Profiler, Metal System Trace, and Allocations/VM Tracker bundles
  plus exported TOCs and Metal tables under
  `artifacts/progpu-native/traces/draw-state-20260812/`;
- `progpu-native-webscene-draw-state.png` under
  `artifacts/progpu-native/sample/`;
- native, managed, and amplified texture-boundary differential images under
  `artifacts/progpu-native/differential/`.

## Retained RGBA-image GPU-complete supplement

The third Tranche B increment uploads one deterministic 192-by-128
straight-alpha RGBA8 image, scales it into a 960-by-540 physical target, and
compares the native C++ renderer against the managed compositor using the same
production `Texture.wgsl`. Image and content revisions are independent. After
warmup, the native metrics report zero texture, vertex, index, and uniform
upload bytes while still encoding and submitting the target pass.

An initial synchronized 3,000-frame alternating run explained the apparent
GPU-complete parity: native/managed submission p95 was 0.2374/0.2742 ms, while
completion-wait p95 was 3.0754/3.0757 ms. The same wgpu-native Metal queue and
same texture shader therefore dominated total p95 at 3.2460/3.2775 ms; this was
not evidence that managed CPU submission equaled native submission.

Profiling then removed the dummy image-mask resource path for ordinary images.
Both renderers now select the unmasked shader entry point. The C++ pipeline
uses two bind groups instead of three and no longer owns or binds a sentinel
mask texture, mask uniform buffer, or mask bind group. Two matched final
3,000-frame synchronized runs produced:

| Run | Native submission p95 | Managed submission p95 | Native completion p95 | Managed completion p95 | Native total p95 | Managed total p95 |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 0.2183 ms | 0.2640 ms | 3.0628 ms | 3.0661 ms | 3.1977 ms | 3.2374 ms |
| 2 | 0.2059 ms | 0.2655 ms | 3.0584 ms | 3.0665 ms | 3.1794 ms | 3.2430 ms |

Relative to the initial run, submission p95 fell by 8.0% in C++ and 3.7% in
managed code in run 1; run 2 confirmed the direction. GPU completion remains
intentionally on par because both sides submit the same one-quad workload to
the same queue. Native total p95 is now about 1.2–2.0% lower, while native
submission p95 is 17–22% lower.

DPI-1 and Retina DPI-2 readbacks are byte-exact over 518,400 pixels: maximum
channel difference zero and identical FNV-1a hashes
`ACB0C7F2152178C5`. The initial upload is 98,304 bytes, texture generation is
one, and the stable replay assertion rejects any later resource upload.

The matched final-binary Time Profiler trace exited zero and contains sampled
CPU stacks. Its instrumented grouped 3,000-frame run reported native/managed
submission p95 0.3620/1.5775 ms; these perturbed values are retained only as
profiling evidence, not substituted for clean timings. The Allocations trace
also exited zero, but this Xcode template exposed no allocation table on the
host, so no native-heap claim is made. A Metal System Trace launch completed
the workload but produced only a `RunIssues.storedata` bundle that `xctrace
export` rejected as missing its template; the synchronized benchmark's direct
wgpu/Metal resource snapshot reported 11,206,656 combined bytes. This failed
trace is retained as diagnostic evidence rather than represented as a valid
Metal capture.

Retained ignored evidence:

- clean synchronized JSON in `/tmp/progpu-native-image-sync-two-groups.json`
  and `/tmp/progpu-native-image-sync-two-groups-2.json`;
- `image-time-final.trace`, exported TOC, and instrumented JSON output;
- `image-allocations.trace` and exported TOC;
- the failed `image-metal-valid.trace` issue bundle;
- native, managed, and exact-zero 64-times-amplified difference PNG images
  under `artifacts/progpu-native/differential/`.

### Same-device external image-view supplement

The follow-up image lane binds the managed benchmark's existing WebGPU texture
view directly in C++. It therefore measures the same final shader and target
without allocating a second native image texture or copying its 98,304-byte
RGBA payload across the managed/native boundary. A clean synchronized
3,000-frame alternating run produced:

| Metric | Native external view | Managed compositor |
|---|---:|---:|
| Submission mean | 0.0716 ms | 0.0856 ms |
| Submission p95 | 0.2027 ms | 0.2410 ms |
| Completion-wait mean | 1.9106 ms | 1.9254 ms |
| Completion-wait p95 | 3.0564 ms | 3.0505 ms |
| Total mean | 1.9824 ms | 2.0111 ms |
| Total p95 | 3.1544 ms | 3.1719 ms |

The total-p95 native/managed ratio is 0.9945, while native submission p95 is
15.9% lower. Both paths report zero managed allocation per warmed measured
frame. Native texture-upload metrics are zero from the first native submission,
texture generation remains one, and all 518,400 pixels are byte-exact with hash
`ACB0C7F2152178C5`. The direct wgpu/Metal resource snapshot is 11,091,968
bytes, 114,688 bytes below the upload-backed lane. GPU completion remains
intentionally close because both paths submit the same one-quad `Texture.wgsl`
work to the same Dawn/Metal queue; the zero-copy benefit is lower CPU submission
and removal of the duplicate texture/upload resource.

This result covers only a same-device WebGPU view. ABI v3 now publishes the
pinned wgpu-native submission index and supports allocation-free poll/wait of
that consumer token, but it does not yet claim zero-copy native decoder import,
IOSurface/DXGI/DMA-BUF ownership, browser external textures, or cross-API
producer-fence acquisition.
Retained ignored evidence is
`artifacts/progpu-native/benchmarks/external-image-sync-final.json` plus the
native, managed, and amplified-difference captures under
`artifacts/progpu-native/differential/`. No temporary profiling file remains
under `/tmp`.

### Same-device external image-mask supplement

The ABI-v2 mask lane binds both the existing source and mask WebGPU views in
C++ and reuses production `Texture.wgsl`. It avoids the managed compositor's
intermediate R8 opacity-mask render target and submits one masked image draw.
A synchronized 3,000-frame alternating Apple M3 Pro/Metal run produced:

| Metric | Native direct mask | Managed opacity-mask layer |
|---|---:|---:|
| Submission mean | 0.0972 ms | 0.1893 ms |
| Submission p95 | 0.3400 ms | 0.5735 ms |
| Completion-wait mean | 2.0256 ms | 2.0969 ms |
| Completion-wait p95 | 3.1194 ms | 4.5895 ms |
| Total mean | 2.1230 ms | 2.2864 ms |
| Total p95 | 3.4063 ms | 4.7935 ms |

Native submission p95 is 40.7% lower and total p95 is 28.9% lower for this
fixture. Warm measured native frames allocate zero managed bytes and report
zero texture, vertex, index, and uniform uploads. The managed opacity-mask
path retains a separate offscreen pass, so this result is specific to direct
image masking and is not generalized to arbitrary nested layer effects.

The direct lane samples the original linear-filtered mask, whereas the managed
reference first quantizes that sample through an R8 intermediate. Across
518,400 pixels, maximum channel difference is 1/255, no pixel exceeds the
3/255 edge tolerance, and mean absolute channel difference is 0.0381/255.
Native/managed hashes are `F2CC379B7484336F` and `E6BE6F3DFA337817`.
Native, managed, and 64-times-amplified difference captures are retained under
`artifacts/progpu-native/differential/`.

### ABI-v3 submission timeline and matched mask optimization

ABI v3 submits with the pinned wgpu-native indexed-submit extension. The
managed owner retrieves the opaque token and waits through the native device
poll only when the caller explicitly requests synchronization. Three matched
3,000-frame Apple M3 Pro/Metal runs, each with 120 warmup frames and alternating
renderer order, produced these median-of-run values:

| Metric | Native ABI-v3 timeline | Managed queue wait |
|---|---:|---:|
| Submission p95 | 0.1308 ms | 0.2755 ms |
| GPU-complete mean | 1.6385 ms | 1.9681 ms |
| GPU-complete p95 | 2.9122 ms | 3.1496 ms |
| Managed allocation | 0 B/frame | 0 B/frame |

The native submission p95 is 52.5% lower, GPU-complete mean is 16.7% lower,
and GPU-complete p95 is 7.5% lower for the median run. The direct mask remains
zero-upload on stable replay and retains the same maximum 1/255 channel delta
with no pixel beyond tolerance.

Allocation tracing also found that the managed mask-bounds path constructed a
`GeneralTransform` reference object once per frame. Replacing it with direct
four-corner `Matrix4x4` value math changed the matched unsynchronized managed
path from 24.13 B/frame to 0 B/frame without changing output. A focused
10,000-iteration allocation regression now protects the helper. The retained
trace is
`artifacts/progpu-native/profiles/managed-mask-allocation-before.nettrace`;
the three final JSON reports and native/managed/difference screenshots are
under `artifacts/progpu-native/benchmarks/` and
`artifacts/progpu-native/differential/`. Temporary `/tmp` trace conversions
were removed after analysis.

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
