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

The current d3b1 rendering checkpoint installs one immutable pointer-free scene
and renders analytic, retained-path, positioned-glyph, and upload-backed image
commands through one public native entry point. The compiler preserves order
with one target render pass, command buffer, and submission for an
eight-command, four-family fixture in which every family has two distinct
resources. Analytic commands use one scene-wide packed vertex/index page;
paths use one aggregate fill/segment page, retained atlas, and vertex/index
payload with per-command index ranges; glyphs use one aggregate
outline/segment/instance page and atlas with per-command instance ranges; and
images retain one immutable texture/bind-group pair per draw while sharing one
quad vertex page and common index buffer. Path/glyph coverage compute and copy
work is encoded before the single target render pass when a page changes.

The real pinned WebScene `02823bf` / Dawn `710c33013` / Metal provider test
renders an analytic→path→glyph→image→path→glyph→image→analytic stream to a
64 by 48 GPU canvas, waits for the native submission token, and presents an
IOSurface marked GPU-complete. Each repeated family references a second
immutable payload: cyan analytic geometry, a magenta transformed path, an
orange positioned glyph, and a cyan image. The native compiler retains all
four aggregate pages and their per-command ranges, so all eight draws remain
ordered in one target render pass and queue submission without overwriting an
earlier payload. Exact interior pixel checks observe the dark clear color plus
both analytic, path, glyph, and image regions in display-list order. The
generated native checkpoint image is
`artifacts/progpu-native/build/progpu-native-semantic-scene.ppm` (with a local
PNG inspection conversion beside it). The managed typed builder separately
writes all four typed family payloads into caller-owned memory with exactly
zero managed bytes over 10,000 builds. A second render of the identical scene
retains the snapshot hash and all four pages by immutable scene hash and DPI,
issues no vertex, index, image-texture, uniform, or path/glyph coverage upload,
and submits the same eight draws in one render pass. Path and glyph pages carry
full scene-hash GPU-ownership markers, so later standalone family use is
detected and restored rather than relying on a colliding 32-bit revision. Image
page replacement constructs and uploads every texture, view, bind group, and
vertex buffer before releasing the preceding immutable page.
Changed-page compilation is O(C + A) time and O(Ca + A) storage for C scene
commands, Ca analytic commands, and A expanded vertices/indices; stable replay
performs O(C) dispatch with O(1) engine-owned auxiliary storage and zero
analytic payload upload.
Changed path-page assembly and compilation is O(C + P + S + K) time with
O(P + S + K) retained CPU/GPU storage for P fills, S segments, and K expanded
draw/coverage bytes. Stable path replay is O(C) dispatch and zero path,
coverage, vertex, or index upload.

Changed glyph-page assembly and compilation is O(C + O + S + G + K) time and
storage for O outlines, S segments, G positioned instances, and K
atlas/coverage bytes. Changed image-page compilation is O(C + I + B) time and
O(I + B) retained storage for I draws and B texture/quad bytes. Stable replay
of either page has O(C) scene validation, O(1) WebGPU command recording through
one retained render-bundle execution, and zero payload upload.

The eight-command matched benchmark retains the same 384-item pixel contract:
284 of 518,400 pixels exceed 3/255, maximum difference is 68/255, and mean
absolute difference is 0.003582658179/255. Three independent alternating
600-frame synchronized runs after 120 warm-ups, using the exact SHA-256-matched
current native dylib, produce these median p95 values:

| Metric | Native C++ | Managed compositor | Native delta |
| --- | ---: | ---: | ---: |
| CPU encode/submit | 0.2797 ms | 0.7746 ms | 63.9% lower |
| GPU-completion wait | 1.3330 ms | 1.3340 ms | within 0.1% |
| Synchronized end to end | 1.6044 ms | 2.1029 ms | 23.7% lower |
| Stable managed allocation | 0 B/frame | 0 B/frame | equal |

Every native frame reports eight draws/family entries, one render pass and
submission, and zero stable vertex, index, texture, uniform, or coverage
upload. The render pass executes one retained WebGPU bundle rather than issuing
eight per-command WebGPU recording sequences on every frame. The exact reports
are under `semantic-render-bundle-exact-run{1,2,3}/results.json`. Their native
and benchmark-output dylibs both have SHA-256
`96d0af862d3f9ff093eb4655169f626213e00f462437ce47a08f47a41ba87027`.
Earlier measurements made before the benchmark project copied the current
CMake output are retained only as rejected evidence and are not used for
comparison. The project links the current local native build with
`CopyToOutputDirectory=PreserveNewest`, and the measured output dylib hash must
match the CMake artifact before a result is accepted.

The same hardware gate then installs a structurally valid scene whose fourth
image draw contains a non-finite opacity. Whole-scene value preflight rejects
it before submission, preserves the prior submission token, and the subsequent
IOSurface pixel verification proves that no earlier draw or clear reached the
target.

The managed/native application harness exposes `--semantic-scene` for the same
substitution boundary at 960 by 540. It retains four quadrant-local families
through the managed production visual tree and installs the equivalent
eight-resource pointer-free native snapshot once. Every measured native frame
must report eight ordered draws/family entries, one target render pass and
command-buffer/queue submission, and zero stable vertex, index, image, uniform,
and coverage uploads. Both
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

The exact render-bundle dylib was also captured with a 600-frame alternating
Time Profiler run. Its instrumented native/managed p95 is 0.2755/0.7852 ms for
submission, 1.3338/1.3332 ms for GPU wait, and 1.5975/2.1087 ms end to end.
The perturbed values are profiling evidence only. Native sampled stacks enter
`wgpu_core::command::bundle::RenderBundle::execute` beneath
`progpu_native_engine_render_scene`; no native per-draw
`wgpu_render_pass_draw*` recording stack is present after warm-up. The managed
route still shows `wgpu_render_pass_draw_indexed` growing its command vector.
The valid trace, TOC, table export, and exact instrumented JSON are under
`semantic-render-bundle-final/instruments/`.

A fresh post-bundle Metal System Trace completed from the same exact dylib. It
contains the `ProGPU retained semantic bundle replay pass` label, 4,569
command-buffer submission rows, 4,581 completion rows, zero command-buffer
error rows, 611 resource-allocation rows, and the same 16,072,704-byte peak
combined-process Metal `currentAllocatedSize`. Retained bundle execution
changes CPU command recording, not shaders, textures, passes, draw order, or
GPU completion. Its trace, TOC, table exports, and instrumented JSON are stored
beside the Time Profiler evidence. The Allocations template again produced
only `RunIssues.storedata` on this host; the hung process and invalid temporary
bundle were removed, and no total native-heap claim is made.

The earlier correlated before/after traces remain retained as historical
evidence. Their optimized Metal capture reported native/managed submission p95
of 0.2981/0.3293 ms, equal 1.5340/1.5341 ms completion waits, zero command-buffer
errors, and a 15,941,632-byte peak `currentAllocatedSize`.

Retained ignored evidence:

- `semantic-scene-1b7578c5/paired-sync-{1,2,3}.json` and
  `semantic-scene-1b7578c5/analytic-cache-after/run-{1,2,3}.json`;
- before/after Time Profiler, Metal System Trace, and Allocations plus VM
  Tracker captures and compact table exports under
  `artifacts/progpu-native/performance/semantic-scene-1b7578c5/instruments/`;
- exact single-pass Time Profiler and Metal System Trace bundles, table exports,
  and JSON under
  `artifacts/progpu-native/performance/semantic-single-pass-final/instruments/`;
- exact retained-bundle three-run JSON and Time Profiler evidence under
  `artifacts/progpu-native/performance/semantic-render-bundle-exact-run{1,2,3}/`
  and `semantic-render-bundle-final/instruments/`;
- inspected semantic native, managed, and 64-times difference PNGs under
  `artifacts/progpu-native/differential/`.

This checkpoint completes distinct retained analytic, path, glyph, and image
pages, single-render-pass stable replay, and elimination of per-command native
WebGPU recording through one retained bundle. D3b1 remains open because a
separately attributable counter for allocations below the remaining
pass/submit boundary is still required; the failed Allocations capture is not
treated as proof.
Whole-scene preflight checks a maximum 16,384 draw passes, 256 MiB of
expanded vertices, 64 MiB of indices, 256 MiB each of textures and aligned
coverage staging, and 512 MiB across those compiled domains. Accumulation uses
checked 64-bit arithmetic and runs in O(C + V) time for C commands and V typed
values with O(1) budget storage. A valid 16,385-draw stream fails with
`OUT_OF_MEMORY` before encoder creation, preserves the submission timeline,
and leaves the target unchanged. Layer descriptors reuse the fixed 64-entry
structural scope stack while separately limiting simultaneously materialized
layers to 16, cap peak layer pixels at 256 MiB, and include that peak in the
same 512 MiB total; they do not allocate a texture until layer rendering is
implemented.

## Semantic save/restore state checkpoint

The first M2.4d3b2 checkpoint adds a 64-byte pointer-free semantic state and a
typed allocation-free .NET builder entry point. Absolute affine transforms and
opacity now flow through save/restore scopes and per-draw overrides across
analytic, retained-path, positioned-glyph, and retained-image commands. The
compiler composes and bakes those values only when immutable family pages
change; stable replay retains the same bundle spans and reports zero vertex,
index, texture, uniform, and coverage uploads. Isolated layers remain
intentionally unsupported by rendering and keep d3b2 unchecked.

The retained rectangle-clip increment partitions adjacent draws by effective
physical scissor and records one immutable render bundle per span. Stable
replay sets those scissors in the single current target pass and executes the
retained spans without geometry growth, mask textures, or native command
re-recording. This follows the WebGPU render-bundle state contract: bundle
execution clears pipeline/binding/buffer state, while scissor remains pass
state. Changed-scene compilation is O(C) for C commands; stable pass encoding
is O(K) for K clip spans, bounded by drawable commands and equal to one for an
unclipped scene.

The warnings-as-errors native build, ASan/UBSan local/provider suites, and 25
focused managed interop tests in both Debug and Release pass. The real pinned
WebScene/Dawn/Metal provider test also passes with eleven commands, nine
semantic draw records, eight emitted ordered draws, eight family switches, one
submission, and zero stable retained uploads. Its lower row is generated from
top-row source coordinates by `Save(state)` with translation `(0,20)` and
opacity `0.5`; its logical target clip `(8,20,48,16)` trims the analytic left
edge and image right edge. A final analytic draw with an empty per-draw clip is
skipped while the packed analytic-page cursor remains aligned. Exact observed
BGRA interior pixels remain:

| Sample | BGRA |
|---|---:|
| clear | `10,8,5,255` |
| transformed half-opacity path | `132,4,130,255` |
| transformed half-opacity glyph | `5,68,130,255` |
| transformed half-opacity image | `132,131,3,255` |
| transformed half-opacity analytic | `132,131,3,255` |

The inspected clip provider capture is retained at
`artifacts/progpu-native/build/progpu-native-semantic-scene-clip.png` with
SHA-256 `8518b5cc8dec81c892ef44143cf5070ff9db8929484c38fd229b7b6d384ebd03`.
This is functional retained-state evidence, not yet a new matched C++/managed
state-bearing performance distribution; a matched state-bearing benchmark and
profiler comparison remain required before the state/layer milestone can be
checked.

### Isolated-layer descriptor and budget checkpoint

This d3b2 increment defines an exact 64-byte pointer-free inline layer
descriptor and an allocation-free typed .NET builder overload. It retains
logical bounds, one-time restore opacity/blend, backdrop and force-isolation
flags, typed mask/effect resource indices, and independent content/composite
revisions. Empty legacy push payloads remain valid default layers. Typed builds
perform zero managed allocation across 10,000 iterations.

Scene validation rejects non-exact payloads, unknown flags, non-canonical
disabled bounds, negative or non-finite extents, invalid opacity/blend,
missing or mistyped mask/effect indices, nonzero reserved fields, and a seventeenth live
layer. Frame preflight converts bounds to physical pixels, tracks the nested
live-byte peak in fixed storage, caps it at 256 MiB, and combines it with the
existing 512 MiB whole-scene budget. The real Dawn/Metal provider accepts one
typed descriptor and reaches `UNSUPPORTED` without submission at 64×48; the
same full-target descriptor at 65,536×65,536 returns `OUT_OF_MEMORY` before
submission and preserves the previous GPU token. This is validation/budget
evidence only: no layer pixel output or performance milestone is claimed yet.

The typed resource follow-up adds an exact 104-byte pointer-free analytic
rounded-rectangle mask and a 16-byte effect-chain header backed by one to eight
exact 56-byte Gaussian-blur/drop-shadow records in the resource auxiliary arena.
Native validation rejects unknown kinds, non-exact spans, flags/reserved values,
singular or non-finite mask transforms, invalid radii/opacity, malformed chain
counts/revisions, invalid effect parameters, and layer references to the wrong
resource kind. The .NET builder writes the same stream from caller-owned spans
with exactly 0 managed bytes allocated across 10,000 complete mask/effect/layer
builds after warm-up. This checkpoint types and validates ownership only; GPU
execution was intentionally evaluated in the following checkpoint rather than
claimed from validation alone.

### Retained bounded fixed-function layer execution checkpoint

The next increments materialize bounded and unbounded group opacity,
`FORCE_ISOLATION`, and every blend with an exact fixed-function coefficient
equation. Changed scenes compile ordered retained bundle, transparent-clear,
and composite operations. Two reusable textures are enough for the current
nested depth-two fixture even though it contains three materialized
occurrences. Each physical depth slot retains the maximum dimensions needed at
that depth; sequential scopes reuse depth zero safely because geometry is
target-local and their four-vertex composite quads occupy distinct ranges in
one retained GPU page. Layer-free semantic scenes keep the earlier single-pass
path.

The real Dawn/Metal fixture renders an opaque red analytic rectangle before the
layer, an outer translated 50% green retained path bounded to 28x16 physical
pixels, a nested 50% blue retained glyph bounded to 16x16, a sequential 25%
magenta retained image in a different 16x16 extent at the reused outer depth,
and an opaque yellow analytic rectangle after pop inside a direct-folded
unit-opacity `SrcOver` scope. It proves all four semantic draw pipelines against
bounded target-local projections, state scoping/restoration, parent-child
extent intersection, left-edge crop, nested premultiplied composition,
different-origin same-depth reuse, and that a non-isolating layer adds no
materialized pass. Representative BGRA pixels are:

| Sample | BGRA |
|---|---:|
| clear | `10,8,5,255` |
| before layer | `0,0,255,255` |
| outer 50% | `5,131,3,255` |
| nested 50% × 50% | `71,6,4,255` |
| sequential 25% `Plus` at reused depth | `74,8,69,255` |
| after pop | `0,255,255,255` |

Both changed and stable frames use one queue submission. The stable frame
reports zero vertex, index, texture, uniform, and coverage-staging uploads;
layer metrics report three content passes, three composites, a retained cache
hit, unchanged allocation count, and depth-two pooled residency of exactly
2,816 bytes (28x16 plus 16x16 RGBA8),
and zero layer vertex/uniform upload. The inspected capture is
`artifacts/progpu-native/build/progpu-native-semantic-layers.png` with SHA-256
`a12415a253827d15b0ccd223c65a827abe9cd9c40344c80b7c6ad25ccc5dafaf`.
The same logical fixture also passes at DPI 2 after its immutable glyph resource
is regenerated at physical raster scale 2: the pool grows exactly to 11,264
bytes (56x32 plus 32x32 RGBA8), stable replay again uploads zero bytes, and the
inspected `progpu-native-semantic-layers-2x.png` capture has SHA-256
`6a2bddb6e366128238a315a43cb6d13606bd78454d0ff1d864a24589f7103f16`.
This is functional and retained-resource evidence, not yet the required
matched managed/native nested-layer distribution or Instruments comparison.
Advanced destination-sampling blend modes and backdrop input remain explicitly
unsupported.

### Retained semantic rounded-mask execution checkpoint

The analytic rounded-mask resource now executes in the retained nested-layer
replay. The real Dawn/Metal fixture materializes a 40x32 bounded parent and a
32x24 masked child, draws an opaque analytic rectangle into the child, then
composites through an 8-pixel-radius mask into the nonzero-origin parent before
the parent returns to the root. This specifically validates global-to-parent-
local mask coordinates instead of covering only the root-target special case.

The changed frame uses two content passes, two composites, one submission, and
8,192 bytes of depth-two RGBA8 pool storage. The mask adds one retained 96-byte
uniform and bind group. Stable replay keeps their generation unchanged and
reports exactly zero vertex, index, texture, uniform, mask-uniform, and coverage
upload with one submission. The inspected provider capture is
`artifacts/progpu-native/build/progpu-native-semantic-masked-layer.ppm` with
SHA-256 `bdfdbea152c64c6f409de48438c232c87b6290a5ed7db8cd00bc08abfc5c93dc`.
Corner, top-center, center, and clear pixels are asserted from the GPU-complete
IOSurface. This is functional/retention evidence; the matched managed/native
mask distribution and Instruments comparison remain part of the aggregate
mask/effect evidence item.

### Retained semantic effect-chain execution checkpoint

The typed effect-chain resource now executes before mask/opacity composition in
the retained nested-layer replay. The real Dawn/Metal fixture uses a 48x40
bounded parent and 32x24 child, runs Gaussian blur followed by a source-alpha
drop shadow, applies an 8-pixel rounded mask, resumes an independent green draw
in the parent, and finally composites the parent into the root. A separate
oversized physical sigma is rejected before encoder creation and leaves the
submission timeline unchanged.

The changed frame records two content passes, two composites, five compute
passes, and one queue submission. Its base layer pool is 10,752 bytes; the
effected depth adds three reusable RGBA8 intermediates totaling 9,216 bytes.
Five 256-byte-aligned parameter records upload as one 1,280-byte retained
uniform page. The stable frame retains texture/binding generations, replays the
independent outer content, and composites the cached inner effect output. It
reports three executed draws, one content pass, two composites, zero effect
passes, and zero vertex, index, texture, uniform, mask-uniform, effect-uniform,
or coverage upload. The GPU-complete IOSurface asserts the clear background,
clipped rounded corner, blurred source interior, and post-child parent
continuation.
The inspected capture is
`artifacts/progpu-native/build/progpu-native-semantic-mask-effects.ppm` with
SHA-256 `b81fe250284b650763a36f97186dd7424b5c7c348bccb8d7b838c9aa60579e88`.
This is functional, ordering, restoration, and retention evidence. The matched
managed/native mask-effect distributions and correlated Instruments evidence
are recorded in the stable semantic effect-output replay checkpoint below.

Three additional state-free 384-item regression runs (600 synchronized paired
frames after 120 warm-ups) used byte-identical CMake and benchmark dylibs at
SHA-256 `8428aaec5aed39480f7e4be8f780f71e80733504cf384e3ebd89c09a33c9fba9`.
Their median p95 values were:

| Metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| CPU submission | 0.2885 ms | 0.5000 ms | 42.3% lower |
| GPU-completion wait | 3.0865 ms | 3.0907 ms | within 0.2% |
| Synchronized end to end | 3.3314 ms | 3.5831 ms | 7.0% lower |
| Stable managed allocation | 0 B/frame | 0 B/frame | equal |

Pixel parity is unchanged at 284 of 518,400 pixels above 3/255, maximum
68/255, and mean `0.003582658179/255`. The completion-wait cluster is materially
higher than the earlier accepted low-noise baseline for both routes, so these
runs are retained as state-change regression corroboration and do not replace
the accepted baseline. Raw reports are under
`artifacts/progpu-native/performance/semantic-state-run{1,2,3}/results.json`.

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

## Retained semantic-material supplement

The semantic scene ABI now retains solid, linear, radial, two-point conical,
and sweep brushes as typed pointer-free resources. Commands refer to a compact
brush map; scene compilation packs only referenced brushes and their exact
gradient-stop ranges into one GPU material page. State opacity is folded into
the packed variants. An unchanged replay uploads neither brushes nor stops.
The existing production vector shader remains the sole GPU evaluator, so this
slice changes material ownership and upload behavior rather than the gradient
algorithm or output quality.

The representative Release workload contains 384 deterministic mixed semantic
items at 960 by 540 physical pixels. It maps all analytic and path commands
through retained solid brushes while preserving the glyph and image families.
The real Dawn/Metal and Chromium fixtures additionally replace source-local
magenta analytic colors with retained red and blue solids and render a retained
path with a green-to-yellow linear gradient inside a bounded backdrop effect.

Three alternating 600-frame synchronized runs, each after 120 warm-up frames,
produced these median p95 values on the Apple M3 Pro/Metal environment:

| Stable p95, median of three runs | Native C++ | Managed | Native delta |
|---|---:|---:|---:|
| CPU submission | 0.1415 ms | 0.2312 ms | 38.8% lower |
| GPU completion wait | 1.2812 ms | 1.2856 ms | within 0.4% |
| Synchronized end to end | 1.4107 ms | 1.5323 ms | 7.9% lower |
| Managed allocation after warm-up | 0 B/frame | 0 B/frame | equal |

Three 3,000-frame drain-after-each-pair runs isolate the queue path. Median
native/managed p95 was 0.1492/0.4272 ms, or 65.1% lower for native. The
synchronized result is intentionally described as GPU-complete parity: both
routes still execute equivalent rasterization, texture sampling, and bandwidth
on the same Metal queue. The measured native gain is the reduced CPU material
resolution and submission work outside that shared GPU floor.

Stable native metrics report one submission, zero brush upload bytes, zero
gradient-stop upload bytes, and zero other retained payload uploads. The mixed
readback has maximum channel difference 68/255, 284 of 518,400 pixels above
3/255, and mean absolute difference 0.003582658/255, unchanged from the
pre-material semantic-scene contract. The inspected captures are:

- `artifacts/progpu-native/differential/semantic-materials-native.png`;
- `artifacts/progpu-native/differential/semantic-materials-managed.png`;
- `artifacts/progpu-native/differential/semantic-materials-difference-64x.png`.

Matched final-binary Time Profiler, Allocations/VM Tracker, and Metal System
Trace captures all exited successfully. Time Profiler samples retained
`RenderBundle::execute` and queue submission in the native steady path. Metal
System Trace contains zero command-buffer-error rows and reports a peak
combined-process `currentAllocatedSize` of 16,089,088 bytes (15.34 MiB); that
shared-process number is not attributable to one renderer. Allocations/VM
Tracker produced valid statistics and region tracks, while the benchmark's
per-thread counter independently reports zero managed allocation after
warm-up; no unsupported native-heap total is inferred from the trace.

Raw evidence is retained under
`artifacts/progpu-native/performance/semantic-materials-20260813/`, including
the six JSON distributions, all three `.trace` bundles, exported Time Profiler
and Metal tables, benchmark stdout, and trace TOCs. The browser screenshot is
`artifacts/progpu-native/browser-evidence/progpu-native-browser-webgpu.png`.

## Retained semantic text-style supplement

The mixed semantic snapshot now stores positioned-text presentation separately
from shaping and glyph geometry. A command references one exact 32-byte
`GpuTextStyle`-compatible record containing straight color plus
grayscale/aliased/ClearType mode. Scene compilation deduplicates
`(resource, style, state opacity)` variants into one storage-buffer page, keeps
the shaped positions and atlas records unchanged, and uploads the page only
when the immutable scene hash changes. Stable replay reports zero text-style,
outline, instance, coverage, vertex, index, texture, and uniform upload.

The clean-room design used the following primary references only for public
behavior and architectural comparison:

- [Skia `SkPaint` overview](https://skia.org/docs/user/api/skpaint_overview/)
  separates reusable draw color/style from text blobs and canvas save state;
- [Direct2D and DirectWrite text rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  preserves cached glyph positions while applying a brush and rendering mode
  at the glyph-run boundary;
- [HarfBuzz glyphs and rendering](https://harfbuzz.github.io/glyphs-and-rendering.html)
  makes positioned glyphs the shaping output and leaves presentation to the
  renderer;
- [WebRender's text-run renderer](https://searchfox.org/mozilla-central/source/gfx/wr/webrender/src/renderer/mod.rs)
  distinguishes alpha, subpixel, and color-bitmap shader modes;
- [Vello's glyph-rendering plan](https://github.com/linebender/vello/issues/204)
  treats transform-aware glyph caching and rendering quality as a separate
  concern from layout.

ProGPU adopts the shared separation of positioned glyph identity, cached
coverage, and late presentation. It rejects a new shaping/layout layer, a
per-command GPU buffer, and source-language object graphs.

The browser fixture exposed and fixed a wasm32-only ABI defect during this
gate: retained semantic outlines had incorrectly used platform-sized `size_t`
storage. The cache now retains the canonical uint64 pointer-free scene record;
64-bit hosts reinterpret it after layout assertions, while wasm32 performs one
checked narrowing translation immediately before GPU execution. Chromium and
real Dawn/Metal both pass styled first-frame upload, zero-upload stable replay,
and malformed/non-finite style rejection.

Three alternating 600-frame synchronized runs after 120 warm-up frames on the
same Apple M3 Pro/Metal host produced these median p95 values:

| Stable p95, median of three runs | Native C++ | Managed | Native delta |
|---|---:|---:|---:|
| CPU submission | 0.1604 ms | 0.3219 ms | 50.2% lower |
| GPU completion wait | 3.0507 ms | 3.0443 ms | within 0.3% |
| Synchronized end to end | 3.1461 ms | 3.2331 ms | 2.7% lower |
| Managed allocation after warm-up | 0 B/frame | 0 B/frame | equal |

All three runs report zero stable text-style upload. Pixel evidence remains at
maximum channel difference 68/255, 284 of 518,400 pixels above 3/255, and mean
absolute difference 0.003582658/255. The native, managed, and amplified
difference images are
`artifacts/progpu-native/differential/semantic-text-styles-*.png`; raw JSON is
under
`artifacts/progpu-native/performance/semantic-text-styles-20260814/`.

## Retained intrinsic-color glyph supplement

The next text checkpoint adds decoded straight-alpha RGBA8 color glyphs
without moving font parsing, shaping, SVG interpretation, or compressed-image
decoding into C++. One exact 48-byte pointer-free bitmap record carries a
checked auxiliary-pixel offset, dimensions, row stride, bearing, and optional
logical render extent. Native scene compilation concatenates referenced pixel
arenas once, packs all referenced records into one bounded RGBA atlas, and
sets the existing production `Text.wgsl` color-glyph flag on its instanced
quad. The fragment path preserves intrinsic RGB and multiplies sampled alpha
by retained text-style/state alpha.

The clean-room design is grounded in the OpenType public contracts and the
same Skia, DirectWrite/Direct2D, WebRender, Vello, and HarfBuzz architecture
research recorded above:

- [OpenType 1.9.1 table inventory](https://learn.microsoft.com/en-us/typography/opentype/spec/otff)
  distinguishes COLR/CPAL, CBDT/CBLC, `sbix`, and SVG representations;
- [COLR 1.9.1](https://learn.microsoft.com/en-us/typography/opentype/spec/colr)
  defines post-layout vector paint compositions and foreground-color layers;
- [CPAL](https://learn.microsoft.com/en-us/typography/opentype/otspec183/cpal)
  specifies straight, non-premultiplied sRGB BGRA palette entries;
- [`sbix`](https://learn.microsoft.com/en-us/typography/opentype/spec/sbix)
  specifies size-selected standard-format bitmap glyph strikes;
- [OpenType SVG](https://learn.microsoft.com/en-us/typography/opentype/otspec184/svg)
  defines current-color and CPAL-backed SVG presentation;
- [DirectWrite color glyph runs](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_2/ns-dwrite_2-dwrite_color_glyph_run)
  expose presentation runs only after layout.

ProGPU adopts post-layout lowering: CBDT/CBLC/`sbix` and rasterized SVG results
cross the native boundary only as decoded pixels plus metrics, while COLR/CPAL
and vector SVG layers reuse retained path, brush, transform, layer, blend, and
effect records. It rejects C++ font parsers, external codecs, one texture per
glyph, compressed data in the render ABI, and per-frame object graphs.

Warnings-as-errors, ASan/UBSan, real Dawn/Metal, and Chromium WebGPU gates
require an exact 16-byte first atlas upload and zero stable color-atlas,
instance, and coverage upload. The shared fixture also lowers two ordered
COLR/SVG-style vector layers through retained path/brush resources and lowers
strikethrough plus underline through retained analytic resources. The Dawn
provider maps the presented IOSurface only after GPU completion, verifies all
four intrinsic-color quadrants including translucent alpha, both ordered
vector-layer colors, and both decorations, then writes
`artifacts/progpu-native/build-sanitized-provider/progpu-native-semantic-color-glyph.ppm`.
This fixture proves correctness and retention; matched managed/native
performance distributions remain part of the next expanded semantic benchmark
before any color-glyph speed claim.

## Retained brush-coordinate and procedural-noise supplement

The semantic material ABI now accepts production brush kind 7 instead of
silently substituting a gradient or solid. Its compact fields preserve the
managed `PerlinNoiseBrush` contract: base frequency, stitch period, tile size,
normalized seed, at most 255 octaves, fractal/turbulence selection, and the
same affine brush-coordinate transform consumed by `Vector.wgsl`. A fallback
record has no table storage. An exact record owns precisely 512 validated
permutation/gradient entries even though `StopCount` continues to mean octave
count; native compilation remaps that physical range once while retaining the
logical octave count. This fixes the former gradient-only stop-count
assumption and keeps unchanged replay at zero upload.

The clean-room behavior is based on primary public contracts:

- [W3C Filter Effects `feTurbulence`](https://www.w3.org/TR/filter-effects-1/#feTurbulenceElement)
  defines base frequency, seed, octaves, stitch behavior, and fractal versus
  turbulence accumulation;
- [Direct2D turbulence](https://learn.microsoft.com/en-us/windows/win32/direct2d/turbulence)
  confirms DIP-space frequency/offset semantics, bounded octave accumulation,
  seed, and stitchable output;
- [Skia coordinate spaces](https://skia.org/docs/user/coordinates/)
  demonstrates a shader-local matrix independent from geometry transforms;
- [Vello `DrawGlyphs::brush_transform`](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html#method.brush_transform)
  likewise keeps paint transforms separate from the global run transform.

ProGPU adopts explicit brush-local coordinates and retained bounded tables. It
rejects per-frame noise textures, a C++ source-language brush graph, an
unbounded octave loop, and interpreting the 512 physical table entries as 512
octaves. A CPU-only exact-table test covers truncation, reserved values,
deduplication, remapping, and the 255-octave bound; managed construction remains
allocation-free. The shared real Dawn/Metal and browser fixture additionally
proves a non-identity transformed linear gradient plus transformed fallback
noise through the production vector shader.

## Retained cubic image-sampling supplement

The semantic image command now preserves nearest, linear, and caller-selected
two-parameter cubic reconstruction without changing the established 88-byte
image record. Cubic draws append one exact 16-byte pointer-free suffix carrying
the `B` and `C` coefficients. Native preflight rejects a missing, oversized,
reserved, non-finite, or unreasonably large suffix before allocating a texture.
The packed image page encodes cubic selection and coefficients in the existing
production texture-vertex ABI; `Texture.wgsl` then performs a fixed 4 by 4
Mitchell-Netravali reconstruction with clamped integer `textureLoad` accesses.
Nearest and linear draws retain their hardware sampler paths and existing ABI.

The clean-room design used these primary public contracts:

- [Mitchell and Netravali, “Reconstruction Filters in Computer Graphics”](https://doi.org/10.1145/54852.378514)
  defines the bounded two-parameter piecewise cubic reconstruction family;
- [Skia `SkCubicResampler`](https://api.skia.org/structSkCubicResampler.html)
  exposes `B`/`C` as the public sampling contract, including Mitchell and
  Catmull-Rom presets;
- [Direct2D interpolation modes](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_interpolation_mode)
  distinguish nearest, linear, cubic, and high-quality cubic at the image draw
  boundary;
- [WebGPU textures and samplers](https://www.w3.org/TR/webgpu/#textures)
  define immutable texture storage, filtering samplers, and explicit texture
  access used by the native/browser implementation;
- [Vello retained scenes](https://docs.rs/vello/latest/vello/struct.Scene.html)
  retain image identity and transforms instead of rebuilding an image object
  graph for replay.

ProGPU adopts the shared explicit sampling contract and retained image
identity. It rejects an opaque quality enum for cubic coefficients, runtime
shader generation, a per-frame texture, and a CPU resize fallback. Cubic work
is fixed `O(16)` samples and `O(1)` private storage per fragment; stream
validation and command compilation remain `O(1)` per image, with one texture
upload on the first immutable-scene render and zero upload on stable replay.

Focused validation passes 32 managed native-interop tests with zero allocation
over 10,000 cubic stream builds, warnings-as-errors C++ layout/validation
tests, ASan/UBSan internal tests, real Dawn/Metal provider rendering, and the
Emscripten/Chromium WebGPU contract. The Metal provider capture at
`artifacts/progpu-native/build-sanitized-provider/progpu-native-webscene-provider.ppm`
contains the transformed four-color cubic image and validates mixed interior
samples. The browser gate requires exactly 16 image-upload bytes on first
render and zero vertex/texture upload on stable replay before its final GPU
readback.

## Retained fused image-color supplement

Semantic images may now append one exact 96-byte straight-RGBA 4x5 color
matrix after their optional cubic record. This is sufficient to represent the
managed renderer's fused brightness, contrast, saturation, grayscale, sepia,
invert, luminance-to-alpha, and explicit matrix operations because each is an
affine transform and the production path clamps only after the fused chain.
The managed/native boundary composes those operations once; C++ validates the
twenty finite bounded coefficients and reserved fields before GPU allocation.

The existing production `Texture.wgsl` owns a dedicated semantic entry point.
It performs nearest, linear, or the same fixed 4 by 4 cubic sampling, converts
premultiplied input to straight color when requested, evaluates five fixed dot
products, clamps once, and applies retained state opacity at composition. The
matrix is uploaded into one retained 96-byte uniform per affected immutable
image and the bind group is cached with that image page. Unchanged replay has
zero matrix, texture, vertex, and frame-uniform upload. No intermediate texture,
compute pass, CPU pixel conversion, runtime shader generation, or per-frame
object graph is introduced.

The clean-room behavior uses these primary public contracts:

- [W3C Filter Effects `feColorMatrix`](https://www.w3.org/TR/filter-effects-1/#feColorMatrixElement)
  defines the 4x5 RGBA transform and its saturation, hue, and
  luminance-to-alpha specializations;
- [Direct2D color-matrix effect](https://learn.microsoft.com/en-us/windows/win32/direct2d/color-matrix)
  defines straight versus premultiplied alpha handling, a 5x4 public matrix,
  output clamping, and channel-combination use cases;
- [Skia `SkColorFilters::Matrix`](https://api.skia.org/classSkColorFilters.html)
  exposes the equivalent row-major twenty-float public filter boundary;
- [Peniko `ImageBrush`](https://docs.rs/peniko/latest/peniko/struct.ImageBrush.html)
  keeps retained image identity/sampling separate from later renderer-specific
  color processing.

The shared Dawn/Metal fixture combines Mitchell-Netravali sampling and an
RGB-luminance matrix in one draw, maps the completed IOSurface, and requires
equal nontrivial output channels. The browser fixture executes the same scene,
requires the 16-byte source upload plus at least the 96-byte matrix on first
family use, then requires exactly zero texture, vertex, and uniform upload on
stable replay. Managed interop validation now passes 33 tests, including zero
allocation across 10,000 combined cubic/matrix stream builds; all four local
C++ warnings/sanitizer/provider tests and Chromium WebGPU pass.

## Stable semantic effect-output replay checkpoint

The aggregate semantic layer/effect benchmark exposed a retained-replay defect:
the output-cache lookup happened only at `PopLayer`, after the native renderer
had already replayed the immutable child scene into an isolated content target.
The later cache hit correctly avoided the five Gaussian/drop-shadow compute
passes, but discarded that newly rendered content. Stable native replay still
executed eight child draws plus one composite and one content pass. Three
matched 300-frame runs therefore measured a median p95 GPU-completion wait of
`4.5367 ms`, versus `3.0479 ms` for managed, even though the base semantic
scene without the cached effect layer was on par.

Bundle compilation now associates each non-backdrop effect layer's push record
with its exact pop/effect operation identity. Replay performs the cache lookup
at push. A hit skips the bounded nested span, preserves the open parent pass
when possible, and composites the retained effect output directly. A root-first
cached layer lazily opens its parent pass before the composite. Backdrop effects
remain unskippable because their result depends on current destination pixels;
advanced blends still close the parent pass and acquire their destination input.
The key contains the immutable scene hash, effect operation, effect texture
generation, and physical extent, so no texture from a stale scene, resized
target, or replaced resource domain can be reused.

Compilation remains `O(C)` for `C` scene commands and adds one fixed-width
annotation per materialized effect layer. A cache hit scans `O(S)` retained
replay records for the skipped subtree but performs `O(1)` cache state work,
zero child WebGPU encoding, zero child GPU draws, zero content passes, and zero
effect passes. Nested skip state is bounded by the existing maximum layer depth
and allocates nothing per frame. Frame metrics now report executed draws and
actual content passes rather than compiled child draws that were skipped.

The clean-room decision used these primary public contracts:

- [Win2D `CacheOutput`](https://microsoft.github.io/Win2D/WinUI2/html/P_Microsoft_Graphics_Canvas_Effects_CompositeEffect_CacheOutput.htm)
  explicitly retains an effect result until its source is invalidated;
- [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html#caching)
  caches unchanged picture/slice output and redraws it into the parent;
- [Skia `saveLayer`](https://api.skia.org/classSkCanvas.html)
  establishes the required offscreen-content, restore-effect, then parent-blend
  ordering, but does not by itself justify re-rendering immutable content;
- [Direct2D effect shader linking](https://learn.microsoft.com/en-us/windows/win32/direct2d/effect-shader-linking)
  reduces compatible effect passes, while identifying multi-sample Gaussian
  blur as a complex-input boundary that still needs an intermediate;
- [Vello retained scenes](https://docs.rs/vello/latest/vello/struct.Scene.html)
  retain command/resource identity but do not promise a reusable filtered
  layer output, so ProGPU keeps explicit cache ownership and invalidation.

SkParagraph/DirectWrite/HarfBuzz shaping and layout reuse were also checked and
remain orthogonal: the change neither reshapes text nor changes glyph-resource
identity. ProGPU adopts output identity and fail-closed invalidation from the
relevant engines, while rejecting heuristic cache keys, destination-dependent
backdrop reuse, per-frame effect graphs, and runtime shader generation.

After the correction, three independent paired 300-frame synchronized runs
followed 120 warmups on the Apple M3 Pro/Metal device. Their median p95 values
were:

| Metric | Native C++ | Managed compositor | Native delta |
|---|---:|---:|---:|
| CPU submission | 0.1208 ms | 0.2728 ms | 55.7% lower |
| GPU-completion wait | 3.0388 ms | 3.0622 ms | 0.8% lower / on par |
| Synchronized end to end | 3.1231 ms | 3.2478 ms | 3.8% lower |
| Stable managed allocation | 0 B/frame | 0 B/frame | equal |

Every stable frame now reports one composite draw, zero content passes, zero
effect passes, one submission, and zero vertex/index/texture/uniform/coverage
upload. Pixel evidence is unchanged: maximum channel difference `7/255`, 64 of
518,400 pixels above `3/255`, and mean absolute difference `0.053851/255`.

Final-binary Instruments captures used the same optimized dylib and exact
semantic layer/effect workload. The 600-frame Time Profiler run measured p95
submission/completion/end-to-end values of `0.1138/3.0358/3.1026 ms` native and
`0.2279/3.0511/3.2006 ms` managed. The 2,000-frame Allocations capture exited
zero and contains both Allocations and VM Tracker tracks. The synchronized
200-frame Metal System Trace exited zero, recorded 2,103 submissions and 3,140
completions, zero command-buffer errors and zero hang rows, and a 36,683,776-byte
peak combined-process Metal allocation. Two 32-byte compiler-spill events occur
during warmup pipeline creation; none is attributed to stable replay. The
shared-process residency is not attributed to either renderer.

Warnings-as-errors C++, all four real Dawn/Metal tests, all four ASan/UBSan
provider tests, and the Emscripten/Chromium WebGPU gate pass. Raw distributions,
trace bundles, TOCs/table exports, and exact trace JSON are retained under
`artifacts/progpu-native/performance/semantic-effect-cache-skip/`; the browser
image and JSON contract are under `artifacts/progpu-native/browser-evidence/`.

## Granular path/glyph/image execution module checkpoint

The former 2,052-line frame-family raster translation unit is now three typed
implementation files: retained path execution (833 lines), positioned-glyph
execution (816 lines), and RGBA/external-image execution (413 lines). The
public C ABI, private entry contract, engine ownership, resource lifetime, and
all WebGPU commands are unchanged. Exact function-body SHA-256 values before
and after the mechanical split are respectively
`9569a32ac472d317efdb568bf9d5fbb4d202b04c065940b67c7b7652320f8ed1`,
`54ba4cd245a6d3635fd2e5504cfb5829acd1812a9808b6af9853d4e654b2a7c9`,
and `1fa9dd19723de90e2b63219b8d86790c2b82e557e8de983e60e3e4b80451c37a`.

Three paired semantic layer/effect regression runs retained one executed draw,
zero content/effect passes, zero stable upload/allocation, and the exact native
and managed image hashes. Their median p95 submission was `0.1559/0.2677 ms`
native/managed, completion wait `4.5195/4.5495 ms`, and synchronized end to end
`4.5626/4.6965 ms`. Both renderers entered the same slower Metal scheduling
regime in two runs; the split is therefore classified as on par, not a speedup.
The real Emscripten/Chromium WebGPU gate also passes and retains screenshot
SHA-256 `32d330540a4ef89c4b75b0e6c9cb15b37e957f67d9bf841cadb687d6869e9502`.

The former 2,756-line layer/effect unit is likewise separated into pooled
layer/mask resource ownership (755 lines), effect resource/dispatch ownership
(1,268 lines), and layer composition/cache ownership (818 lines). Exact body
hashes before and after are
`4893044d3443b7e97fd11da38bf387364d2ae2515a8f5efa46bb30d025cd40c3`,
`cf85fe8a54cba1f330e33bbaea3de8afd4484f73b14a75ea52bc3fa34233e853`,
and `f4128cc0b9899652d185570e1a93a0c373adda69146f08aca8927b7c6b67a872`.
The only new linkage is through typed declarations in the existing private
replay seam; no public export is added.

Three post-split paired runs again preserved the exact images, one stable draw,
zero content/effect passes, and zero stable allocation/upload. Median p95 was
`0.1990/0.3261 ms` submission, `4.5259/4.5405 ms` completion wait, and
`4.5787/4.6849 ms` synchronized end to end for native/managed. Warnings-as-
errors, ASan/UBSan, real Dawn/Metal, and Emscripten/Chromium WebGPU pass with
the three new translation units.

## Granular semantic execution module checkpoint

The former 3,653-line semantic execution unit is now separated into immutable
scene updates (126 lines), packed-page render-bundle draw encoding (322 lines),
and scene compilation/replay orchestration (3,248 lines). The first two are
small ownership units; the remaining render orchestrator stays intentionally
whole until its state-machine lifetime boundaries can be split without moving
or duplicating cleanup paths. The public C ABI, retained scene format, WebGPU
commands, cache ownership, and resource release order are unchanged.

The update function body is byte-identical before and after the split with
SHA-256
`b8937b65c6d686173784833c995655c248919b05cf61e46dd4926683247eb5fc`.
The four draw encoders are byte-identical and hash to
`b16c6f942a65cf5069ae8c84cde8090cad6a486b2408f7efed59366e350bd0ad`;
the only additions are typed, private wrappers that bind their existing command
adapter. After normalizing those four wrapper call names, render replay is
byte-identical with SHA-256
`dfe869d65fb660d2ffb5458b3ea93e90c2b3438135eaffbca66bde26073495ef`.

Three paired retained layer/effect runs preserve the exact native and managed
image hashes, one stable draw, zero content/effect passes, zero stable upload,
and zero managed allocation per frame. Median p95 native/managed results are
`0.1242/0.2595 ms` submission, `3.0329/3.0683 ms` completion wait, and
`3.0953/3.2401 ms` synchronized end to end. Pixel evidence remains bounded at
maximum channel difference `7/255`, 64 of 518,400 pixels above `3/255`, and
mean absolute difference `0.053851/255`. Warnings-as-errors, all four
ASan/UBSan provider tests, exact Dawn/Metal headers, 52 managed native/shader
tests, and the real Emscripten/Chromium WebGPU gate pass with the split files.
Raw distributions are retained under
`artifacts/progpu-native/performance/semantic-module-split/`.

## Representative desktop substitution checkpoint

`ProGPU.Samples.Desktop --native-renderer` now opens on one immutable semantic
scene instead of an isolated frame-family demonstration. Its eight commands
and nine resources combine analytic geometry, a retained cubic path, positioned
glyphs, a cubic color-processed RGBA image, a translated clipped state, a
bounded rounded mask, and a two-node blur/drop-shadow chain. One changed scene
build copies the pointer-free arenas transactionally; unchanged rendering is
one C ABI scene call, one cached composite draw, zero retained uploads, and
zero managed allocation per frame. The prior family-specific modes remain
available through the mode button for focused inspection.

The local Release macOS launch stayed live without diagnostics and reported a
stable `0.123 ms` C ABI/submission sample, `0 B` managed allocation, one draw,
zero upload, and the expected `8/9` command/resource contract. The inspected
window capture is retained at
`artifacts/progpu-native/sample-evidence/native-representative-scene-macos.png`
with SHA-256
`d8e300d2658865ec0ec58a971ac61a78f23f24d2782e270853e2ed81be13ae58`.
This is automated implementation evidence, not the required final user manual
approval.

## Representative aggregate qualification checkpoint

The runnable native build entry points now include the retained semantic
layer/effect workload in addition to the base mixed scene. This is the widest
matched final-scene gate: eight ordered analytic, path, positioned-glyph, and
image draws are wrapped in one rounded mask and a retained Gaussian-blur then
drop-shadow chain. Linux, macOS, and Windows execute the same command through
their Vulkan, Metal, and D3D12 wgpu-native backends respectively; the build
fails on a backend mismatch, readback failure, retained upload, allocation, or
the established edge-difference budget.

A local Apple M3 Pro/Metal Release run used 60 warm-up and 300 alternating
synchronized frames. Native/managed p95 submission was `0.1582/0.3174 ms`,
completion wait `3.0311/3.0588 ms`, and end to end `3.1221/3.3210 ms`.
Native submission was 50.2% lower and synchronized p95 was 6.0% lower; both
paths allocated `0 B/frame`. Stable native replay reported one composite draw,
one submission, zero child/effect passes, and zero vertex, index, texture,
uniform, coverage, brush, gradient-stop, text-style, or color-glyph upload.
GPU completion remains intentionally on par because both routes execute the
same retained blur/shadow and composition work on the same Metal queue.

The 518,400-pixel differential has maximum channel difference `13/255`, 143
pixels above `3/255`, and mean absolute channel difference
`0.026700424/255`, within the independent intermediate-edge contract. Ignored
evidence is retained at
`artifacts/progpu-native/performance/final-representative/semantic-layer-effects-macos.json`.
The inspected native, managed, and 64-times-amplified difference PNG hashes are
respectively
`64b2f7ae0f2206d91019fc11adb5de0c2553c58a76bec6da8599cadc735b766c`,
`2741a8777851f65138eb9045abd9d93698716dd6b1066805b18d89efdcd1ecb5`,
and
`4dd3a5bcbaea650c477cfd72ca19cfc5c3055f43f6cd383ef60e87b40198676d`.
Cross-platform backend images and adapter records are produced by exact-head CI;
manual sample approval remains intentionally held.

## Typed Dawn and mobile distribution checkpoint

The public managed substitution path now creates the provider-resolved C++
renderer over a typed `DawnGpuContext`. A package-neutral three-handle record
crosses the assembly boundary, while a process-lifetime Dawn module supplies
the exact procedure resolver. Every stable render call branches once on the
engine's immutable backend kind and otherwise uses the same C ABI records;
there is no per-frame delegate, reflection, handle adapter, or allocation.

The local Apple M3 Pro hardware smoke uses WebScene provider `02823bf8` and
Dawn `710c3301`: `.NET -> Dawn -> progpu_native_dawn -> Metal` renders 18
vertices in one draw and one submission, then verifies known readback pixels.
It forces a real device loss, observes the typed notification, recreates the
C++ engine on a replacement Dawn device, and reproduces the byte-identical
capture in one draw and one submission.
The evidence image is
`artifacts/progpu-native/sample/progpu-native-managed-dawn.png`; its PPM
SHA-256 is
`ec9498caab8b64cf7f42e04ccd7c303c8e681c6a3061117651ab8ed9381d863e`.
Readback is test-only and is not part of the production zero-copy path.

The ordinary `ProGPU.Samples.Desktop` Dawn/Metal process also opens the full
representative native page through this adapter. Its inspected 1280x828 frame
reports the `8/9` scene contract, one stable draw, zero upload, `0 B` managed
allocation, and a sampled C-ABI-plus-submit time of `0.188 ms`. The evidence is
`artifacts/progpu-native/sample-evidence/native-dawn-representative-desktop.png`
with SHA-256
`99bee4d10b9298e1f9329ad591051cf5646b4a020143b5a21d63a4e722d5ff48`.
This automated launch evidence does not replace the user's final manual review.

The mobile build gate compiles the unchanged provider-resolved renderer against
stable WebGPU header `01addc4b`: Android API 30 arm64-v8a/x86_64 shared objects
have no Dawn, WebGPU, wgpu, or shared-libc++ dependency; the iOS static
XCFramework contains an arm64 device slice and arm64/x64 simulator slice.
Package verification checks both native assets and the exact
`ProGPU.Backend.Native` dependency. This is build/link/package evidence, not a
mobile GPU latency claim; physical-device performance remains a manual gate.
