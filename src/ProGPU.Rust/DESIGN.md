# Rust port design and evidence

The authoritative algorithms are the original ProGPU files listed in README.md.
External sources below inform boundaries and tests only; no external engine code
is copied or translated into the implementation.

## Architecture research (2026-11-08)

* [Skia canvas](https://skia.org/docs/user/api/skcanvas_overview/) separates
  matrix/clip state from draw paint. [Skia text overview](https://skia.org/docs/dev/design/text_overview/)
  separates text processing stages. Adopt those boundaries already present in
  ProGPU: immutable source geometry/text and explicit replay state.
* [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal)
  offers reusable layout and lower-level glyph rendering. Preserve ProGPU's
  positioned glyph ownership independently of rendering; no platform shaper
  substitution or implicit equivalence between grayscale and ClearType.
* [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  distinguish device-independent and dependent resources. Keep CPU geometry
  after device loss, invalidate GPU realizations, and reject cross-device replay.
* [Win2D custom hosts](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/using-win2d-without-built-in-controls)
  leave resize/DPI/device-loss policy to lower-level consumers. Adapt this to a
  winit host separate from the renderer, including zero-size/suspended windows.
* [WebRender](https://github.com/servo/webrender/wiki) provides a retained rendering
  reference. Retain ProGPU's compiled generations and ordered batches, with no
  geometry upload on stable replay. Visibility and atlas eviction must preserve
  source identity and painter order when those families are ported.
* [Vello](https://github.com/linebender/vello) is a compute-oriented rendering
  reference; [Parley](https://github.com/linebender/parley) is a separate text layout
  library. Preserve ProGPU's existing GPU rasterization and reusable CPU layout
  boundary; do not substitute either engine for the owned implementation.
* [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  distinguish script-dependent shaping from simple advances. Full ProGPU Unicode,
  fallback and OpenType behavior remains required; a character-to-glyph loop will
  not stand in for shaping.

Startup must allocate only requested pipeline families. Geometry compilation may
run off-thread without a GPU; realization occurs on demand and batches uploads.
Future glyph cache keys must retain selected face, variations, hinting, DPI,
phase, raster policy and generation. Worker preparation must not mutate published
generations. Resource eviction cannot invalidate an encoded frame. Culling must
operate on original bounds/clip semantics, not approximate shader envelopes.
These are port requirements, not claims that pending families are implemented.

## Retained analytic execution

`Renderer` compiles the original general vector entry points with the native
56-byte vertex layout and straight-alpha blend factors. The default brush,
gradient buffer and required one-texel atlas binding are the only support
resources. Public mesh recording admits only analytic primitive families;
the placeholder atlas cannot silently render an unported path or glyph.

Each prepared scene owns its vertex/index buffers and its exact original
224-byte projection/DPI uniform through a bind group. No mutable uniform is
shared between prepared frames. Renderer identity prevents use of a scene with
another renderer's inferred pipeline layout, even on the same device. wgpu's
retained resource handles own dependencies through command submission; ordinary
Rust drops do not force premature buffer destruction. GPU publication rejects
overflowed geometry while the CPU geometry port keeps the original arithmetic.

Path compute uses a typed execution preference. Automatic and forced compute
select the canonical compute kernel after checking required device limits.
Forced alternative GPU, intrinsic-SIMD CPU and scalar-reference routes fail
explicitly until implemented and qualified; no approximation is selected.

## Initial validation

`cargo test --no-default-features`: two geometry regressions and canonical Naga
validation of all six initial shader compositions pass. The explicit native
oracle test executes the unchanged C++ analytic header: 288 cases and 36,864
output bytes match exactly, covering all three primitive kinds, AA/aliased edges,
zero extents, strokes, affine/reflected frames and invalid dimensions.
`cargo clippy --no-default-features --all-targets -- -D warnings` passes.

The new implementation does not alter managed/native source or shaders; their
behavior and existing qualification gates remain unchanged. Rendering changes
in this port still require matched CPU records and GPU image evidence before
being declared parity-qualified. No performance improvement is claimed.

On Apple M3 Pro/Metal, all six shader modules validate on the actual device.
The analytic GPU test checks all three families, original rectangle boundaries,
2,752 source-over reference pixels, immutable frame preparation, cross-renderer
rejection, and full-frame cold/warm equality after owner retirement. The winit
sample presents four frames and exits successfully. Strict all-feature/all-target
Clippy passes.

Initial Release baseline (`cargo run --release --locked --example retained_benchmark`):

```text
primitives=4096 warmup=120 frames=480 pipeline_ms=297.042 preparation_ms=0.394
cpu_submit_us p50=18.209 p95=26.584 p99=56.833 serial_fps=2267.19
cold_geometry_bytes=1015808 cold_uniform_bytes=224 draws_per_frame=1
warm_geometry_upload_bytes=0 warm_uniform_upload_bytes=0
```

Submission timings exclude the explicit wait; serial FPS includes it. Upload
counts describe the actual immutable payload and upload-free replay route, not
GPU timestamps or allocator measurements. This is a single Rust baseline, not
matched managed/native performance qualification or an optimization claim.

`cargo package --locked --allow-dirty` produces and verifies a standalone crate
containing the canonical shader files (58 files, 608.5 KiB uncompressed at this
increment). The initial allow-dirty verification was triggered by a generated
native dependency license. Follow-up archive inspection found that the unanchored
`LICENSE` pattern had included that nested file; the package allowlist is now
anchored at the repository root. Package verification builds from the extracted crate, with no paths
back into the checkout. Nothing has been published to crates.io.

The existing managed renderer gates passed locally using the exact SDK from
`global.json`, installed in an isolated temporary directory: 6,478 passed and 13
platform skips (6,491 total), and 230/230 headless tests. The first parallel build
attempt collided on a shared generated runtimeconfig; the renderer suite was
rerun sequentially after headless completed and passed. No gate was weakened.

## Independent path masks

The path port retains exact 48-byte line/quadratic/cubic/arc/rational records and
the original finite/reserved/positive-weight checks. Source bounds, parameter
bits, two-axis scale and fractional origin are retained explicitly. Whole-batch
preflight bounds CPU payloads and GPU storage before GPU mutation. All paths use
the canonical single-path operation-zero entry; Boolean/signed programs and
linear-only specialization remain independent pending features.

Native extent grouping is ported with original source-order tie breaks, 256-byte
binding alignment, exact bound byte lengths and the 65,535 Z-slice split. One
compute pass writes packed coverage into one buffer; copies to owned R8 textures
share the same command buffer/submission. Row pitch is 256-byte aligned and
source placement is 512-byte aligned. Warm consumers retain these textures;
cross-generation atlas reuse and eviction are still pending. The retained scene
integration below adds within-generation sharing and direct atlas population.

Validation: an original-C++ differential control compares 65,540 dispatch records
and 3,146,752 bytes, including the Z-limit split and original signed-zero bits.
GPU controls compare 9,545 R8 pixels against an independent 8x8 sample oracle
for all six segment kinds, nonzero/even-odd, reversed winding, two-axis scale,
fractional origins, unequal output extents, and a true two-arc circle. Invalid
last requests reject the whole batch and unavailable forced routes reject.
The compute owner is dropped before mask readback to verify retained outputs.
These authored controls do not replace the remaining original source/application
and package pixel inventories.


## Mixed retained scenes and path atlases

`Scene` retains immutable `Path` source arrays and ordered analytic/path commands.
Compilation runs without a GPU and preserves source coordinates, maximum/minimum
singular-scale arithmetic, 1/64 phase quantization, four-texel raster padding,
source bounds, fill rule, and the complete sampling/contrast identity. Integer
translations and paint changes can share one raster; different phase, scale or
sampling metadata cannot. The temporary identity map never outlives its strong
source leases. Analytic padding keeps the native 1.5/minimum-scale formula.

The original shelf allocator starts at 1024, grows to at most 8192 without
repacking old tiles, and preserves two-texel margins and the 4092 tile limit.
The compute batch writes directly into the final atlas using the same encoder
and submission as rasterization. Prepared scenes own their exact atlas binding;
later preparations cannot replace textures or uniforms referenced by old scenes.
Warm frames submit one painter-ordered draw with no path compute or uploads.
The optional compute pipeline stays uninitialized for analytic-only scenes.
CPU generations are reusable only at their exact captured DPI. A raw path mesh
cannot accidentally be prepared against the analytic placeholder texture.

Exact texel loads require the original complete four-corner integer mapping and
power-of-two projection proof. Other admitted frames retain the canonical
filtered route. Explicit grayscale contrast preserves all thirteen raw values
and remains separate from binary sampling and ordinary coverage.

Validation added at this increment:

- Original native helpers: 528 affine/phase/projection cases, 19,584 exact bytes,
  including reflections, shears, fractional translations, half-phase ties,
  singular and nonfinite rejection. Existing 288 analytic cases and 65,540
  dispatch cases remain mandatory.
- Mixed GPU scenes: 12,288 independently sampled/composited pixels across DPI
  1, 1.25 and 2, preserving overlapping alpha and command order, followed by
  complete cold/warm equality. A later prepared atlas is retired before replay
  of the earlier generation. Mismatched DPI and placeholder path replay reject.
- Every texel in a 1024-square atlas matches the independently checked mask
  inventory, including margins and unused space, after compute-owner retirement.
- All 13 explicit contrast values: 53,248 output pixels checked against exact
  rectangular area intersections and a double background-domain transfer oracle.
  Only final shader/unorm rounding tolerances are admitted. This does not qualify
  source font policy, original Microsoft pixels, or native packages.
- The mixed winit sample presents four Metal frames: 120 commands, 29,760 geometry
  bytes, 224 frame-uniform bytes, three unique path rasters, one 1 MiB atlas,
  and one draw. Resize/DPI rebuilds the actual frozen generation.

Strict all-feature/all-target Clippy, CPU-only/default tests and the explicit
Metal GPU controls pass locally. On the preceding independent-mask commit,
Linux and Windows portable/package lanes and Linux Vulkan/Xvfb passed; the macOS
hosted job remained queued. Those receipts do not validate later commits.

Release measurements on Apple M3 Pro/Metal, run sequentially without the GPU test
suite, use 4,096 commands, 120 warmups and 480 measured serial frames:

| Workload | Pipeline ms | CPU preparation ms | Submit p50/p95/p99 µs | Serial FPS |
| --- | ---: | ---: | --- | ---: |
| Analytic baseline | 17.497 | 0.479 | 133.041 / 249.875 / 443.292 | 1531.41 |
| 2,048 analytic + 2,048 path draws | 13.450 | 5.349 | 134.917 / 179.208 / 239.792 | 1419.36 |

Both upload 1,015,808 geometry bytes and 224 frame-uniform bytes once and submit
one draw per frame. The mixed workload rasterizes one shared path into a 1 MiB
atlas during preparation, then performs zero warm path rasterizations. Preparation
includes first-use pipeline creation but excludes a completion wait; submission
quantiles exclude the wait and serial FPS includes it. Driver caches are not
cleared. An earlier run overlapped GPU tests and is excluded from this table.
The later submission timings differ substantially from the initial baseline;
a matched host/profile investigation remains required for performance
qualification. These are reproducible workloads and measured receipts, not
cross-language comparisons, GPU timestamps, or speedup claims.

## Owned font data and native SFNT compatibility

Font architecture research extends the earlier cross-engine review:

- [Skia font-manager data/collection APIs](https://api.skia.org/classSkFontMgr.html)
  and [HarfBuzz face ownership](https://harfbuzz.github.io/harfbuzz-hb-face.html)
  separate source data and selected faces from text execution. Adapt that boundary
  to `Arc<[u8]>` with validated private ranges and owner-tied borrowed tables.
  No external parser implementation is used.
- [DirectWrite collections](https://learn.microsoft.com/en-us/windows/win32/directwrite/font-enumeration)
  and [Win2D file font sets](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_Text_CanvasFontSet__ctor.htm)
  distinguish a file's faces from system discovery. Retain explicit face selection;
  do not eagerly enumerate system fonts or silently select a fallback font.
- [Parley FontContext](https://docs.rs/parley/latest/parley/struct.FontContext.html)
  keeps database/source caching separate. Here the immutable face shares source
  bytes; a system catalog and its cache remain separate future ports. Construction
  requires neither a window nor GPU initialization, and faces are Send + Sync.
- [WebRender font templates and instances](https://github.com/servo/webrender/blob/main/webrender_api/src/font.rs)
  separate original source/index from size, render options and variations.
  Preserve that separation: the face currently exposes original design units,
  not a fabricated device instance, DPI policy or hinted generation.
- [Vello glyph-run state](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html)
  keeps size, hinting, variations, transforms and brush state explicit. Retain
  ProGPU's corresponding original contracts when GPU text replay is ported;
  nominal cmap mapping must never be presented as Unicode/OpenType shaping.

The architecture comparisons inform ownership boundaries only. Implementation
comes directly from the original `progpu_native_sfnt.cpp` and
`progpu_native_sfnt_standalone.cpp` at this branch's source revision. Ordinary
native compatibility includes last-valid duplicate table selection, symbol map
precedence, conditional 12/13/4 fallback, exact format-14 default/nondefault
selection, repeated final horizontal advances, absent-bearing zero, raw signed
metrics, short/long loca and empty glyph slots. These ordinary semantics do not
replace stricter raw-inventory checks required by future gasp/VDMX queries.
WOFF normalization, outlines, interpreter execution, variable instances, shaping,
layout and rasterization remain pending independent layers.

`SfntFace` retains one immutable byte generation through clones and selected
collection views. No public mutable source or dangling self-referential slice is
introduced. All reads are checked; private retained spans are admitted only
within the owner. Construction performs O(T + C + S) work for directory/table scans,
cmap records and the selected format-4 endpoint inventory, with O(1) face storage
excluding the shared bytes. Metrics
and cmap queries allocate nothing. Group cmap lookup keeps the original binary
search. Format 4 retains original first-contact selection; a proven sorted
inventory with at least 16 segments uses a lower-bound search, while shorter or
unsorted inventories retain the original ordered scan. Standalone extraction
keeps native bounded O(T² + B) work (T <= 4096), sorted effective tags, copied
checksums, four-byte zero padding, and CFF/CFF2 OTTO selection. It publishes a
new owned buffer only after complete sizing and allocation admission.

Validation: 2,518 inputs compare 3,364,916 exact output bytes against the unchanged
original C++ loader and standalone writer. Inputs include every truncation of an
authored synthetic face, 2,048 deterministic mutations, explicit cmap and symbol
branches, collection selection, duplicate tables, and all ten existing authored
scan-font files. The transcript includes errors, selected tables, raw metrics,
nominal/variation mapping, glyph bytes and complete standalone output. Separate
CPU tests prove source retention, selected-view identity, Send + Sync, and the
absence of source mutation or parser panic across the malformed corpus. Both
CPU-only and all-feature builds pass strict Clippy/tests. The original native
oracle is now mandatory in Linux/macOS Rust CI.

Reproducible font workload:

```sh
cargo run --release --locked --no-default-features --example font_benchmark -- tests/Shared/ProGPU-Authored-Scan-1.ttf
clang++ -std=c++20 -O3 -DNDEBUG -Wall -Wextra -Werror -Isrc/ProGPU.Native/include src/ProGPU.Rust/tests/native_font_benchmark.cpp src/ProGPU.Native/src/Text/progpu_native_sfnt.cpp -o /tmp/progpu-native-font-benchmark
/tmp/progpu-native-font-benchmark tests/Shared/ProGPU-Authored-Scan-1.ttf
```

Both run 66,560 nominal cmap + horizontal metric queries per round, five warmups
and forty measured rounds. File I/O/source storage acquisition is outside timing;
Rust owns a shared face while native borrows its input buffer. Apple M3 Pro local
Release observations, sequential runs, no GPU activity:

| Input / implementation | First parse µs | Round p50 / p95 / p99 µs | Checksum |
| --- | ---: | --- | --- |
| Authored Scan-1 / Rust | 15.458 | 362.375 / 1006.250 / 1010.583 | 288488074189568764 |
| Authored Scan-1 / original C++ | 0.875 | 318.250 / 910.916 / 925.792 | 288488074189568764 |
| Local Helvetica TTC face 0 / Rust | 53.125 | 5965.125 / 9742.750 / 11144.333 | 359704000785784050 |
| Local Helvetica TTC face 0 / original C++ | 1.417 | 4440.333 / 4584.125 / 8252.917 | 359704000785784050 |

Authored input is 20,952 bytes/11 tables; the locally installed collection is
2,369,592 bytes/20 selected tables and is not redistributed. Identical checksums
confirm the matched workload's output. Rust is slower in these observations;
performance parity is not established. First-call timings include process/cache
startup differences, and no improvement or regression attribution is claimed.
A matched Instruments investigation remains required before optimization claims;
these receipts do not qualify complete shaping, text rendering, or frame times.

### Profile-guided format-4 lookup

Instruments Time Profiler on the same Release benchmark and system font found
245/262 initial Rust leaf samples in cmap lookup; the original C++ control had
215/239 there. The selected format-4 inventory contains 187 segments. Merely
iterating a prevalidated byte slice did not improve measured time and was
rejected. The accepted change validates endpoint ordering once on the selected
immutable source and uses lower-bound search for sorted inventories of at least
16 segments. Every shorter or unsorted inventory keeps the original ordered
scan. Duplicate endpoints still select the first original slot; invalid starts,
indirect range addresses, zero glyphs and unsigned delta wrapping keep their
native behavior. No original query bounds check or fallback gate is removed.

The original oracle now additionally checks every BMP code point for 42 distinct
inventories: 2,752,512 exact mappings. Cases cover 1/2/3/15/16/17/187 segments,
sorted/duplicate/unsorted ends, direct/indirect/zero glyph slots, and invalid
start/range data. All existing 2,518 full-face transcripts still match.

A sequential uninstrumented before/after run (same workload/checksums) recorded:

| Workload | Before p50/p95/p99 µs | Guarded search p50/p95/p99 µs |
| --- | --- | --- |
| Helvetica face 0, 187 segments | 5538.125 / 5602.084 / 5701.541 | 1122.791 / 1206.625 / 1213.458 |
| Authored Scan-1, original short scan | 349.500 / 368.125 / 375.167 | 368.708 / 394.375 / 440.500 |

The large-inventory median falls substantially; the small case is about 5.5%
slower in this observation, so this is not a universal performance improvement.
The final isolated Instruments run recorded 60 samples, with 39 cmap-lookup leaf
samples. Short statistical traces support the lookup-work diagnosis, not complete
text/frame performance or precise attribution of first-parse latency.

The compact exported frame tables and exact input/binary/source SHA-256 receipts
are retained in `perf/font-5bd03cfc1-time-profiler.json`. Raw traces, sample XML,
discarded binaries and the old package extraction were removed after validation
at the user's request to limit disk use. Reusable build dependencies and the
active SDK remain available. No GPU/allocator optimization is claimed by this
CPU-only change; complete rendering and platform gates remain mandatory.


## Browser host and validation

The browser host follows the original repository's native browser approach:
`src/ProGPU.Native/browser/test.mjs` supplies the Playwright/Chromium/WebGPU gate
pattern; the Rust host uses wasm-bindgen and an actual HTML canvas surface.
`examples/support/sample_scene.rs` is shared verbatim with the winit host.
No source shader, pixel arithmetic or native browser gate changes are needed.
`wgpu::SurfaceTarget::Canvas`, asynchronous mapping callbacks and browser promises
keep preparation/readback off blocking native polling paths. A serialized host
queue prevents concurrent mutable Rust borrows while JavaScript awaits GPU work.
Canvas CSS layout is independent of its physical backing size.

The local Rust 1.90 / Chromium 151.0.7922.34 run reported BrowserWebGpu with a CPU
adapter (ANGLE SwiftShader requested). At both DPR 1 and 2, all six modules
compiled and 65,536 independently calculated authored pixels matched: three
64×64 painter-order scenes at DPI 1/1.25/2 plus thirteen 64×64 explicit contrast
frames. UNORM blend comparisons retain the native test tolerances (two levels
for painter-order blending, one for contrast). Every warm image is byte-identical
to its cold image after a later prepared generation retires, and after the
replayed generation is dropped following submission. Canvas replay retains
29,760 geometry bytes, 224 uniform bytes, three unique paths, a 1 MiB atlas and
one draw; ten additional presentations leave these payload counts unchanged.
Both viewport axes and physical DPI are checked after resize. Zero extents stop
presentation and a subsequent valid frame resumes it. No GPU/page/console errors
are accepted. The test explicitly awaits asynchronous predicates; treating a
Promise as a truthy wait predicate is not a valid presentation gate.

The full runner is `eng/progpu-test-rust-browser.sh`; only small PNGs and JSON
receipts are retained by CI for seven days. Installed browser/tool dependencies
remain reusable, with browser garbage collection disabled by this runner.
Generated bindings, WASM, browser packages and package verification extractions
are not source assets. This is current Rust-subset browser qualification only;
full text, images, effects, source applications and native package gates remain
independent work.


## Original simple TrueType outlines

`font/outline.rs` directly ports `progpu_native_true_type_glyph.cpp` and
`progpu_native_true_type_path.cpp`. The existing owned face remains the source
boundary: a decoded `SimpleGlyph` retains the exact face generation and borrows
its original instruction bytes. An allocation-free whole-record inspection
precedes fallible output allocation. All original expanded flag bits, signed
coordinate accumulation, endpoint ordering, repeat counts and instruction lengths
remain intact. The 16-bit endpoint inventory bounds coordinates to signed i32,
including the 65,536-point extreme. Empty and composite record classification
stays independent; this increment does not fabricate composite expansion.

Contour lowering preserves original start selection, first/last off-curve
midpoints, closure order, single-point no-segment behavior, complete optional
position inventories and exact 48-byte line/quadratic records. It performs no
hinting, flattening, source placement, winding repair or raster-policy inference.
The cross-engine ownership/instance boundaries described above still apply;
only original ProGPU code supplies implementation behavior.

The unchanged original C++ decoder/writer matches 18,073 inputs and 10,407,780
transcript bytes. The inventory includes every on/off pattern of lengths 1..8,
all 256 raw flag values with compressed/expanded runs crossing contour bounds,
truncations and mutations, the maximum original point count, the original font
malformed corpus and every glyph in all ten authored fonts. Both original and
explicit alternate positions produce exact path bytes. Default tests verify
source retention after face retirement and atomic rejection. Chromium additionally
executes the production WASM decoder against an independently authored three-point
control: exact points, flags, endpoints, instructions and 96 quadratic-record
bytes, with truncated-glyph rejection. The existing 131,072 browser pixel checks
remain green; this CPU outline control is not complete browser text rendering.

A matched Release CPU baseline allocates decoded point/contour/segment outputs
for every simple glyph, then hashes every original point/flag and segment word.
Both implementations use five warmups and forty measured rounds, excluding file
I/O, face construction and initial glyph selection. C++ uses its unchanged
caller-buffer API; Rust returns an owned generation. Apple M3 Pro / Rust 1.90,
sequential local runs:

| Input / implementation | Simple glyphs / points / segments | Round p50 / p95 / p99 µs | Checksum |
| --- | --- | --- | --- |
| Authored Scan-1 / Rust | 440 / 1815 / 1650 | 182.167 / 307.667 / 311.667 | 10113802853148328976 |
| Authored Scan-1 / C++ | 440 / 1815 / 1650 | 171.583 / 188.584 / 213.667 | 10113802853148328976 |
| Local Helvetica face 0 / Rust | 1246 / 34202 / 25309 | 904.875 / 1056.083 / 1159.875 | 13577311101863472582 |
| Local Helvetica face 0 / C++ | 1246 / 34202 / 25309 | 966.834 / 1401.833 / 1591.833 | 13577311101863472582 |

These are baseline observations, not optimization attribution or whole-stack
performance parity. The owned and caller-buffer APIs differ in allocation and
ownership bookkeeping. Composite, varied, hinted and shaped text workloads are
still required. Reproduce with `cargo run --release --locked --no-default-features
--example outline_benchmark -- FONT_PATH [FACE_INDEX]`; build
`tests/native_outline_benchmark.cpp` with C++20/O3 and the unchanged SFNT, simple
glyph and path translation units. Temporary oracle inputs/binaries clean up on
both success and failure; the one-off benchmark binary is removed after recording
these compact results. No new raw profiler traces are generated for this port.


### Linux browser compositor and device-loss gate

The first Linux x64 browser job reached initialization but then exceeded its
original 180-second deadline. Ubuntu 24.04 ARM64 reproduced the failure. API and
browser diagnostics located the cause: Chromium could not allocate a canvas
shared-image backing for its WebGPU swap buffer, destroyed the device, and then
rejected `popErrorScope` with `Instance dropped in popErrorScope`. The wgpu 30
web backend converts a rejected scope promise to `None`; a successful-looking
scope pop alone therefore does not establish shader compilation. Retaining the
instance/adapter and driving animation frames continuously did not fix this
host problem; the animation-loop experiment was discarded.

The Linux test host explicitly enables Vulkan compositing, selects Chromium's
SwiftShader Vulkan driver and disables native Vulkan surfaces for headless
presentation. This follows the [Chrome headless WebGPU setup](https://developer.chrome.com/blog/supercharge-web-ai-testing)
and [Chromium SwiftShader switches](https://chromium.googlesource.com/chromium/src/+/HEAD/docs/gpu/swiftshader.md).
The headless ARM64 offscreen/readback checks then passed on the same Chromium
151 binary, within the unchanged deadline. However, visual inspection of the
screenshots found a black presented canvas. Offscreen success does not qualify
browser presentation. Linux x64 CI also still failed its resize gate. No shader
source, shader compiler option, pixel tolerance, provider or original
native/managed gate changes follow from either result.

The browser gate now decodes the actual compositor PNG and requires the canvas
background, analytic rectangle/ellipse and computed curved path at both pixel
densities. These checks pass on macOS. Running the same Linux Chromium binary
and Vulkan/SwiftShader flags in headed mode under Xvfb also passes both DPRs,
all 131,072 offscreen pixels, six shader modules, WASM outlines, resize and
retained lifetime controls within the original deadline. Both presented PNGs
were visually inspected. The sampled RGBA pixels at each DPR are background
`6,9,15,255`, rectangle `33,77,197,255`, ellipse `45,77,197,255` and computed
path `219,81,59,255`, identical to the macOS control.

Linux CI therefore explicitly selects `PROGPU_RUST_BROWSER_HEADED=1` under
Xvfb. This ARM64 result does not replace the separate hosted x64 lane, and
headless Linux remains unqualified. Receipts record the actual headless mode,
with no automatic fallback or skipped pixel assertions. Failed reruns remove
stale prior success receipts/screenshots and write a failed receipt when the
test catches an error. Neither the browser version nor shader options changed.

Browser shader validation now also checks `get_compilation_info` for every module.
The host retains its instance/adapter lifetime and records device loss explicitly;
lost devices reject subsequent draw/preparation instead of counting invalid
presentations. Full device recreation remains pending. Receipts include actual
platform, architecture, browser, adapter and launch flags. Detailed exploratory
API interception and discarded host experiments are not production code.

### Composite TrueType records and recursive unhinted outlines

The original component parser and recursive outline expander are ported from
`progpu_native_true_type_composite.cpp`, its internal header, and
`progpu_native_true_type_outline.cpp`. `CompositeGlyph` retains ordered raw flags,
original signed/unsigned arguments, F2Dot14 matrix entries and the final
component's instruction payload. It retains the selected immutable face;
record parsing does not fabricate child validity or execute instructions.
`GlyphOutline` owns expanded points and canonical path records with the same
source generation. Whole-tree requirements are validated before publication,
and the maximum simple point/contour scratch is reused across children.

Expansion preserves original order, parent/child point attachment, scaled offsets,
round-to-even translation, explicit transform operations and line-only endpoint
handling. The original depth limit is 32; cyclic, out-of-range and over-depth
children are empty, while malformed visited children fail. Invalid attachment
components follow the original omission route. The root itself requires a valid
glyph slot. No hinting, variation or source raster policy is inferred.

The source-arithmetic oracle links the original C++ files unchanged and compares
72,993 inputs / 15,498,308 bytes: all 65,536 raw flags, transform precedence,
signed/unsigned argument widths, point attachment, fractional nested transforms,
depth boundaries/cycles, malformed/truncated records and all authored font glyphs.
Refactored shared simple decoding/lowering also retains all 18,073 original
simple-outline comparisons / 10,407,780 exact bytes.

**Compiler-default qualification remains open.** Deeper fractional transforms
revealed a one-bit difference at input 65,771 with default ARM64 Clang `-O2`
(native `0x40f5a8bf`, Rust `0x40f5a8be`).
Assembly confirms multiply/add contraction in the native transform, while Rust
uses separate scalar operations. As in the existing geometry oracle, the new
source-arithmetic control uses `-ffp-contract=off` only for its isolated C++ oracle.
No native source, package build, driver, shader or compiler default is changed.
The original failing control remains reproducible with
`PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test --no-default-features --test composite
-- --ignored --nocapture`. Source-arithmetic success is not compiler-default
native package or C# pixel qualification; that gate remains mandatory.

The expanded benchmark mode (`outline_benchmark FONT_PATH 0 --expanded`) decodes
all glyph slots, allocates the complete generation and hashes every expanded
float point and path word. The original C++ caller-buffer path performs matching
measurement/allocation/decoding/hash work. Five warmups precede forty measured
rounds; I/O and face creation are excluded. Sequential Apple ARM64 Release/Rust
and Clang C++20/O3 runs, with **default native contraction retained**:

| Input / implementation | Glyphs / points / segments | Round p50 / p95 / p99 µs | Checksum |
| --- | --- | --- | --- |
| Authored Scan-1 / Rust | 441 / 1815 / 1650 | 145.291 / 154.083 / 155.792 | 12432019274016934652 |
| Authored Scan-1 / C++ | 441 / 1815 / 1650 | 310.541 / 496.875 / 565.916 | 12432019274016934652 |
| Local Helvetica face 0 / Rust | 2252 / 72668 / 52795 | 2737.833 / 3139.958 / 3449.958 | 3026803012000123258 |
| Local Helvetica face 0 / C++ | 2252 / 72668 / 52795 | 2582.500 / 2715.584 / 2771.166 | 3026803012000123258 |

These inputs have matching checksums despite the separate deep-fractional control
failure. They are baseline observations, not proof of full performance parity or
optimization attribution. The larger input is approximately 6% slower at p50.
The native benchmark now links component/outline translation units as well as
SFNT/simple/path sources. Temporary binaries and oracle inputs are removed after
recording; no raw profiling trace is needed for this baseline.

The WASM bridge also decodes a composite half-scale fixture with scaled offsets
and round-to-even ties, preserves its exact 96 path bytes after source retirement,
and rejects truncation. The macOS Chromium run passes this control at both DPRs,
together with the unchanged 131,072 GPU pixels and actual compositor assertions.
Strict native/WASM Clippy, CPU/all-feature tests and extracted package build pass;
the package contains 80 files, 838.8 KiB (192.1 KiB compressed).

Hosted Linux x64 at `2a27c3f00` passes DPR-1 actual presentation and both offscreen
pixel suites, then times out capturing the DPR-2 screenshot. It is still not a
passing browser lane. The capture host now pauses its animation loop after resize
and ten-frame replay validation, waits for any already-entered frame and two
browser animation turns, then captures the last presented surface. This avoids
continuous submissions during capture; pixel assertions, timeout and shader code
remain unchanged. The revised capture passes locally; hosted verification remains
required. The successful ARM64 VM run and temporary dependencies were cleaned up,
retaining only compact receipts/PNGs and restoring the VM's original suspended
state (approximately 870 MiB reclaimed across guest tools and shared inputs).

### CFF1 owned containers and Type 2 outline execution

Original sources are `progpu_native_cff_index.cpp`, `cff_dictionary`,
`cff_fd_select`, `cff_private`, `cff1`, `cff_type2`, `cff_type2_operators` and
`cff_outline` under the same native Text directory (full `progpu_native_` prefix).
No third-party font interpreter is used. The Rust `font::cff::Font` retains the
selected SFNT generation and validated table span. INDEX offsets and FDSelect
formats 0/3/4 remain encoded: O(1) item/direct selection and O(log ranges) range
selection, without expanded offset/selection arrays. CFF2 INDEX and strict top
metadata are available, but CFF2 container/variation execution remains pending.

DICT parsing retains the original 48-value limit, 96-character real payload,
exact decimal powers through 22, malformed-number cursor advancement, CFF1
last-applicable operators, private missing/malformed metadata policy and CFF2
strict arity/duplicate/matrix admission. The exact 1/2048 real fixture stays exact;
original range/extent checks and unrecognized operator behavior are not broadened.

CFF1 Type 2 evaluation preserves the original 513 operands, 32 transient values,
96-stem bound, ten-level subroutine limit, local/global bias boundaries, optional
width consumption, hint-mask skipping, deterministic glyph-seeded random state,
stack/arithmetic/flex/alternating-curve operators, implicit closure and explicit
endchar admission. These stem/mask operations do not implement a hinted raster
policy. Count-only evaluation validates the entire program before allocating a
private output generation; a second identical execution publishes immutable
line/cubic records with source ownership. Optional six-double transforms apply
before float narrowing, including the closing edge. Invalid programs and
unrepresentable coordinates cannot publish a partial outline.

The linked-original oracle compares 122,411 inputs / 5,688,767 exact output bytes.
Coverage includes every DICT first/payload byte pair, malformed reals, all INDEX
widths, FDSelect formats, CID/local/private selection and owner retirement; every
Type 2 operator byte with multiple operand counts; depth/bias/stack/stem bounds;
transient, random, arithmetic and closure controls; raw, identity, fractional,
zero and invalid transforms; and deterministic malformed/truncated programs.
The oracle uses the established separate-operation arithmetic contract, with
`PROGPU_RUST_NATIVE_DEFAULT_FP=1` available for the independent default-compiler
control. Existing native provider/package/source-pixel gates remain mandatory.

The independent ARM64 compiler-default CFF run does **not** pass: input 66,824
(DICT real-number mode, a long repeated-nine decimal payload) differs in binary64
digit accumulation. This is the same separate-operation versus contracted
multiply/add qualification boundary, not a relaxed numeric tolerance. Reproduce
with `PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test --no-default-features --test cff
-- --ignored --nocapture`. Default native/C# numeric and package qualification
remains open despite the passing source-arithmetic and ordinary font controls.

An authored CFF rectangle is decoded and mapped through the actual production
path compiler, GPU atlas and retained renderer at three DPIs, including painter
order and lifetime controls. Metal passes the added explicit GPU test. Chromium
WASM validates the original 192 closed-path bytes and truncation rejection, then
renders the same font-derived paths through WebGPU. The full browser suite now
checks 155,648 authored pixels at DPR 1/2, all six canonical shader compositions,
TrueType and CFF CPU controls, cold/warm retirement, resize and actual compositor
pixels. Local macOS passes. The preceding `9f9a72ec0` hosted x64/Xvfb browser,
Linux GPU and Linux/Windows portable lanes all passed; hosted macOS was cancelled
before starting and remains a separate gate. The new CFF commit requires fresh CI.

CFF baseline: Rust 1.90 Release and Apple Clang 21.0.0 C++20/O3 on ARM64, five
warmups and forty sequential measured rounds. File I/O and face construction are
excluded. All glyphs are evaluated, allocated and hashed; CFF exposes path
records rather than a separate decoded point inventory. The owned Rust API
performs two executions; the original native public caller-buffer sequence
requires measurement followed by a decode call which itself preflights (three
executions total). These are actual API workload measurements, not isolated
interpreter throughput or optimization attribution. Native default floating
contraction remains enabled in the benchmark.

| Local input / implementation | Glyphs / segments | Round p50 / p95 / p99 µs | Checksum |
| --- | --- | --- | --- |
| NotoSerifYezidi-Regular / Rust | 56 / 758 | 113.917 / 146.833 / 189.041 | 1999901585277320871 |
| NotoSerifYezidi-Regular / C++ | 56 / 758 | 223.625 / 244.333 / 254.500 | 1999901585277320871 |
| STIXGeneral / Rust | 3302 / 66255 | 4785.958 / 4984.791 / 7075.125 | 5211491498807598615 |
| STIXGeneral / C++ | 3302 / 66255 | 5848.958 / 6192.166 / 6332.500 | 5211491498807598615 |

Input SHA-256: NotoSerifYezidi-Regular (8664 bytes)
`f8a3c04d1a0a872e2a522d07f1280369c30c680c40099eaf4471ee7c389ebde9`;
STIXGeneral (369156 bytes)
`5add3f3f2bd7fd897d2fa5ccbe468607c52111dc44cdfaaf2d851a574f5357a7`.
Measured Rust binary (509888 bytes)
`01e9a3d79481eb71e3f367126f47b1724475422ff7eef007429375d5c6ae0450`;
native binary (110984 bytes)
`6c2f4d544de28183bc17df4ebb393371160018fb0e98f1a708d72067139c8c51`.
Fonts remain local and are not added to the crate. Reproduce with
`outline_benchmark FONT_PATH 0 --cff`; the native benchmark links the original
SFNT/TrueType/CFF translation units, including native CFF2/item-variation support
required by those unchanged files. Temporary binaries and oracle input dumps are
removed after verification; compact results above replace raw benchmark output.

Strict native/WASM Clippy, all-feature and CPU-only tests pass locally. The
extracted package builds on the native and WASM targets: 89 files, 924.5 KiB
(211.5 KiB compressed). Browser bindings, tool dependencies and generated outputs
remain outside the crate inventory.

## Original fvar and avar metadata

`font/variations.rs` ports only the original
`src/ProGPU.Native/src/Text/progpu_native_font_variations.cpp`. The public axis
iterator borrows the immutable face, allocates nothing and validates the full
declared extent before exposing records. It retains raw tags, fixed endpoints,
flags, name IDs, strides and the native empty behavior for undersized records.
Bulk inventory preserves reversed endpoints; selected-axis queries reject them,
matching the two original APIs rather than silently sanitizing the source.

Fixed normalization clamps signed 16.16 input, computes against widened original
endpoints and rounds initial ties away from zero. Its legacy avar route retains
the original version/count admission and endpoint behavior. Floating design
normalization promotes the exact input before using original fixed endpoints,
without an intermediate 16.16 quantization. Its strict avar v1 route validates
all maps, including unselected later axes, ordered input coordinates, range,
endpoints and the explicit zero mapping. Interpolated ties round to even. Neither
route executes variable outlines or implies a source font raster policy.

The original linked C++ differential test covers 112,629 inputs and 13,049,224
exact bytes: all signed F2DOT14 inputs across a nonlinear map, every byte mutation
of two-axis records/maps, truncations, reversed/extreme endpoint combinations,
random float bit patterns, nonfinite input and missing/invalid axes. Three normal
CPU tests independently check public contracts, ownership and both tie rules.
Linux/macOS CI requires the original-source oracle; ordinary consumers need no
C++ compiler. Temporary input streams and executables retire on success/failure.

The independent ARM64 Clang default-contraction control remains **failing** at
case 17,481: native normalized coordinate -14831 versus Rust -14832. This is a
one-unit final F2DOT14 difference from contracted avar interpolation. The passing
source-arithmetic oracle uses the same isolated `-ffp-contract=off` policy as
the existing geometry, composite and CFF controls. Reproduce the original
default gate with `PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test
--no-default-features --test variations -- --ignored --nocapture`. No original
source, compiler default, package, tolerance or shader was altered. Full native
and managed numeric/pixel qualification remains open.

The browser bridge runs ten independent fixed/design rounding and rejection
checks per DPR alongside the existing 155,648-pixel GPU suite. Local Chromium
passes both DPRs, all six shaders, font controls, resize and actual compositor
pixels. Strict native/WASM Clippy and all-feature/CPU-only tests pass locally.
The package verification contains 92 files (approximately 952 KiB), and both its
native and WASM builds pass. Verified extraction directories are removed while
active reusable compilation dependencies and compact receipts are retained.

The preceding CFF checkpoint `950efd342` passed hosted Linux/Windows portable,
Linux Vulkan/winit and the full 155,648-pixel x64/Xvfb browser lane in run
42515743204. Hosted macOS was cancelled before execution; local macOS evidence
does not replace that gate. Item variation stores, CFF2 execution, variable
TrueType outlines and the remaining full text stack are still pending.

## Item variation stores and delta-index maps

`font/item_variation.rs` ports the original
`src/ProGPU.Native/src/Text/progpu_native_item_variation_store.cpp` without a
foreign font implementation. Both public views borrow validated immutable bytes;
private layout fields prohibit caller-forged extents. Parsing, lookup and scalar
evaluation allocate nothing. Checked row/region products preserve rejection on
32-bit WebAssembly as well as native targets. Every declared subtable and row
extent is validated before publication, including later unselected data.

The implementation retains original region order, F2DOT14 scaling, neutral and
invalid region-tuple rules, exact peak/boundary branches and zero-scalar early
exit. Delta rows preserve long/short signed encodings, original accumulation
order, zero-offset subtables, zero for absent item indices and skipped invalid
region references. Supplied region scalars retain the original raw arithmetic.
CFF2 blend selection is a separate stricter route: zero item/word counts and
every selected region index must pass before a position is queried. These rules
are not generalized into the ordinary delta route. Format-zero/one index maps
retain encoded entry widths, raw format-bit handling, last-entry repetition,
empty-map saturation and original outer/inner narrowing.

The linked-original source-arithmetic oracle compares 157,958 inputs and
6,892,604 exact bytes: every signed coordinate with both delta encodings;
two-axis region tuple permutations; malformed/truncated complete stores;
missing/invalid selection and short coordinate/scalar arrays; all 256 entry
formats, both map count widths and invalid format controls; and zero/nonzero
store offsets. Three normal CPU tests independently cover values, complete
preflight, unrepresentable offsets and different ordinary/blend admission.
Temporary native executables and input streams are removed by scope cleanup.

The independent ARM64 default compiler remains a separate failing gate. At
input 66,082, native delta accumulation produces `0x419f7d41` and Rust produces
`0x419f7d42`. Reproduce with `PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test
--no-default-features --test item_variation -- --ignored --nocapture`. Only the
isolated source-arithmetic executable uses `-ffp-contract=off`; original native
source/packages/defaults, pixel gates and tolerances remain unchanged. This
milestone does not claim full compiler-default or managed/native numeric parity.

The WASM browser control additionally checks a region scalar, encoded delta,
CFF2 selected scalar and rejection of a truncated later subtable. It brings the
variation check count to twelve per DPR without reducing any existing font,
shader, 155,648-pixel or actual-presentation checks. CFF2 container/evaluation
integration and variable TrueType outlines remain pending; these APIs alone do
not implement variable-font rendering.

## Owned CFF2 containers and variable Type 2 outlines

`font/cff/font2.rs` ports the original `progpu_native_cff2.cpp`; the existing
Rust Type 2 evaluator gains the original CFF2 branches from
`progpu_native_cff_type2.cpp`, `progpu_native_cff_type2_operators.cpp` and
`progpu_native_cff_outline.cpp`. No foreign font-engine source or native bridge
is used. CFF1 and CFF2 retain distinct public owners and admission rules while
sharing the original line/cubic writer and curve operators. The previously
documented immutable source/instance separation remains: owned CFF2 outputs keep
the source generation and the exact supplied normalized coordinate vector.

Opening retains the exact 2.0/five-byte header, 32-bit INDEX inventories, global
subroutine limit, optional expected glyph count, required FontDICT inventory,
one-versus-multiple FDSelect rule, complete private-range validation, bounded
variation store and every blend-region subtable. The original font matrix must
agree with the selected head units per em. Selected private metadata separately
retains first-operator vsindex, duplicate rejection, unknown-operator behavior,
513 operands and local-subroutine limits. Opening does not eagerly reject an
unselected private program that the original API defers to glyph selection.

CFF2 execution retains implicit root/subroutine termination, forbidden explicit
return/endchar, no CFF1 width consumption, the four admitted escaped flex
operators, ten-level subroutine depth and the 513-operand/96-stem bounds.
Variation selection occurs at most once before the first blend. At most 512
selected region scalars are cached in each evaluation; blends retain source
value/delta order, float scalar promotion, double accumulation and finite-result
rejection. All output is preflighted before fallible allocation and immutable
publication. The Rust `Result` maps the original oversized-charstring guard's
false-with-error-none result to `InvalidGlyph`; the oracle records the original
success flag independently and normalizes only that absent error code. It does
not admit the rejected program or weaken output comparison.

The combined unchanged-native oracle passes 198,745 CFF1/CFF2 inputs and
15,992,461 exact bytes. The added CFF2 portion contributes 76,334 inputs and
10,303,694 bytes, including all signed normalized coordinates, every regular and
escaped operator byte with multiple stack lengths, blend sizes and region limits,
vsindex ordering, multiple values/regions, private programs, local/global depth,
bias limits, malformed containers, matrix admission and oversized charstrings.
The targeted ARM64 Clang **default-compiler** CFF2 control also passes all of
those added cases: `PROGPU_RUST_CFF2_ONLY=1 PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo
test --no-default-features --test cff -- --ignored --nocapture`. This does not
resolve the independently retained CFF1/composite/avar/item-store contraction
gaps or qualify every real variable font. Ordinary CPU tests independently check
source lifetime, coordinate ownership, blended values and admission boundaries.

An authored variable CFF2 rectangle is evaluated at half peak and passed to the
actual retained path compiler, atlas and renderer at three physical DPIs. The
added Metal test checks original pixels, painter order and resource lifetime.
The browser adds an independent original 96-byte blended outline and missing
coordinate rejection, plus the same variable paths at three DPIs. This expands
the full pixel inventory from 155,648 to 180,224 while preserving every prior
font, shader, contrast, retirement, resize and actual-compositor check. The
test's fixed count was updated after the first run correctly detected the larger
inventory. No pixel tolerance or prior case was removed.

Full text replay, source policies, broader font/provider/package qualification,
variable TrueType outlines, CFF2 workload performance baselines and device-loss
recovery remain pending. This change makes no performance-optimization claim;
the borrowed CFF2 variation-store getter currently revalidates its immutable
encoded inventory without allocation. Profile representative final workloads
before changing that policy or claiming throughput improvements.

Local validation passes strict all-target/all-feature and WASM Clippy, ordinary
all-feature and CPU-only tests, the combined source oracle, the separate default
CFF2 oracle, Metal variable-path pixels and the full Chromium suite. The extracted
97-file package (about 1 MiB, 234.9 KiB compressed) builds for native and WASM.
Temporary oracle files and verified package extractions are removed; compact
receipts and active reusable dependencies remain. Hosted checks for the new
commit are independent of these local results.

## Original gvar metadata, packed runs and interpolation

`font/gvar.rs` and `font/gvar/font.rs` port the original
`progpu_native_gvar.cpp`, `progpu_native_gvar_packed.cpp`,
`progpu_native_gvar_tuples.cpp` and `progpu_native_gvar_interpolate.cpp`.
The table owner retains its exact selected SFNT generation. Missing gvar remains
distinct from a malformed present table; original axis/glyph counts, encoded
offset widths, shared tuples and selected glyph-offset checks are preserved.
The port does not introduce a stricter version or unknown-flag policy. Entire
tuple-header and declared-payload extents are checked before allocating owned
descriptors and start/peak/end regions. Implicit regions retain original signed
peaks, and raw tuple flags survive. Actual packed payload application is separate.

Packed point/delta parsers validate complete requested runs before publishing
borrowed views. Their exact-size iterators allocate nothing and preserve original
prefix consumption, duplicate cumulative points, signed byte/word deltas and
zero-run precedence. The single-zero all-points marker stays distinct from an
encoded two-byte zero count. Caller-buffer copies validate capacity before any
write and preserve unused output tails. Contour interpolation validates every
capacity and contour endpoint before mutation, preserves untouched contours,
uses original circular touched-point order and retains exact equal-position,
clamping, signed-zero and nonfinite arithmetic branches. No hinting or raster
policy is inferred. Tuple scalars retain the native shorter-coordinate behavior,
which remains distinct from item-store coordinate-capacity rejection.

The unchanged-native source-arithmetic oracle passes 186,770 inputs and
10,907,644 exact bytes. Its packed/IUP portion covers 116,668 inputs: all first
and run-control byte combinations, zero/byte/word encodings, maximum cumulative
point runs, short capacities, output tails, all eight-point touched masks across
all contour partitions, later-contour rejection and raw nonfinite/zero bits.
The metadata portion adds all 65,536 tuple flag patterns, shared and embedded
peaks, intermediate regions, both glyph-offset encodings, missing/truncated/
mutated tables, empty glyph slots, ownership and scalar-coordinate truncation.
Normal CPU tests independently check expected public values and transactional
failure behavior. Linux/macOS CI requires the original-source oracle.

The separate ARM64 Clang default-contraction control still fails at input 82,932
in interpolation: native `0xbfed5a8b` versus Rust `0xbfed5a88`. Reproduce using
`PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test --no-default-features --test gvar
-- --ignored --nocapture`. The passing isolated source-arithmetic executable
uses the established separate-operation policy; no native source/package/compiler
default, shader, tolerance or original pixel gate was changed. Full numeric
qualification remains open.

The browser bridge adds eight gvar controls per DPR: original packed points,
delta zero-run precedence, both empty/all-point encodings, interpolation,
complete-contour rejection, exact tuple metadata and truncation rejection. All
existing 180,224 authored pixels, CFF2 controls and actual presentation assertions
remain mandatory. Full gvar payload accumulation, phantom metrics, varied
composites/outline replay, hinting and the remaining text stack are still pending.

Hosted CFF2 checkpoint `a2b44584e` passes Linux/Windows portable and Linux GPU
lanes. Its browser lane passed both offscreen inventories but failed the final
DPR-2 compositor assertion with transparent-black canvas pixels, followed by a
device-loss message during teardown (run 42517720607, job 128844366798). This is
an open presentation gate, not a successful browser qualification. Hosted macOS
was cancelled before execution. Local passing controls remain separate.

The browser presentation harness now stops continuous animation, drains the
JavaScript ownership queue, submits one final retained frame and waits for that
specific GPU queue-completion callback before the original two compositor frame
boundaries and screenshot. Each request owns a distinct atomic signal so an
earlier callback cannot complete a later request. Missing final presentation,
observed device loss, the original deadline and all screenshot pixel assertions
remain failures. There is no readback substitute, fallback, retry-to-pass or
driver/compiler change. The receipt records completion separately from pixels.
Local Chromium passes both DPRs with the new barrier. The hosted transparent
canvas failure remains open until the new lane provides actual evidence; queue
completion alone does not prove compositor correctness.

## Original gvar payload application

`font/gvar/apply.rs` ports the original `progpu_native_gvar_payload.cpp`,
`progpu_native_gvar_apply.cpp`, `progpu_native_gvar_composite.cpp` and the
horizontal pair route in `progpu_native_gvar_phantom.cpp`. Public operations
return owned numeric outputs after complete validation; `SimpleGlyph` also
offers `varied_points`. No font engine, hinting policy or native FFI is introduced.
Immutable payload descriptors retain encoded point and delta spans. Concrete
iterators avoid expanded per-tuple point/delta arrays and dynamic dispatch.
All declared tuple payloads validate before publication, even inactive or later
tuples. Private/shared explicit empty sets remain distinct from all-points.
Trailing payload bytes retain their original ignored policy.

Simple sparse duplicate deltas sum before scalar multiplication. Each tuple's
IUP uses positions already changed by preceding tuples, following the original
implementation. Component and horizontal phantom duplicate deltas instead scale
and accumulate individually in source order. Phantom slots never move geometry;
horizontal metrics remain raw left/right deltas, without HVAR precedence or the
independent stricter vertical query. The owned API uses the original per-call
scratch capacity: a zero-tuple glyph with nonempty shared point numbers still
fails against zero slots. Counts below four retain the horizontal query's
unconditional zero result. All source bytes and original point inventories stay
immutable.

The expanded unchanged-native oracle passes **256,904 inputs / 15,924,368 exact
bytes**, including 70,134 new application cases. It covers every signed 16-bit
coordinate for ordered full/sparse/duplicate tuples; shared/private point sets;
phantom-only and out-of-range references; differing item counts; inactive/later
malformed payloads; zero-tuple shared sets; and truncation/byte mutations. The
native harness compiles the actual original payload, application, component and
phantom units plus their original dependencies. The ARM64 compiler-default
arithmetic gate documented above remains open.

Chromium adds four payload controls per DPR (twelve gvar controls total): exact
sparse/IUP positions, component/phantom values, inactive tuples and rejection of
an inactive malformed payload. Both DPRs pass with all **180,224** existing
offscreen pixels, six canonical shaders, queue completion and actual compositor
pixels. Native/all-feature and WASM strict Clippy and CPU tests pass locally.
These are functional ports; no application speedup or full text parity is claimed.
Recursive varied-outline expansion and workload performance remain next steps.

Hosted capture checkpoint `98594ac35` subsequently passed Linux and Windows
portable lanes, Linux Vulkan/winit, and the complete browser suite (run
42518804549, browser job 128847597231). The browser receipt includes both queue
completion signals, both original canvas-pixel inventories, and 90,112 offscreen
pixels per DPR. This supplies the previously missing hosted presentation evidence
for that checkpoint; it does not qualify headless Linux or other browser/hardware
combinations. Hosted macOS remained queued when this milestone was recorded.

## Recursive variable TrueType outlines

`SfntFace::varied_glyph_outline` and `VariedGlyphOutline` port the original
`progpu_native_true_type_varied_requirements.cpp` and
`progpu_native_true_type_varied_outline.cpp`. The immutable result retains the
selected SFNT owner and exact requested coordinates (extra coordinates survive
ownership but only original gvar axes execute). The existing original component
parser, contour writer, transform arithmetic and recursion machinery are shared
with unvaried expansion; ordinary unvaried admission remains independent.

Whole-tree measurement includes every original child, even children later omitted
by invalid point attachment. It preserves the depth-32/cycle/out-of-range empty
routes, checked aggregate component storage, original tuple-header validation and
maximum simple/component packed scratch capacities. Those maxima matter for
admission: a zero-tuple child's shared point list may fit scratch supplied by a
sibling, including an omitted sibling. The standalone operation still uses its
own zero capacity. Header errors in skipped children fail preflight; their packed
payloads are decoded only when output traversal admits them. An ordinary unit
test explicitly preserves both sides of that distinction.

Each admitted simple glyph varies its original positions before canonical contour
lowering. Component deltas add to original XY arguments before any selected scaled
offset transform, followed by original ties-to-even rounding. Point attachment
ignores component translation deltas and follows the varied native route's
independent child-root measurement. Parent/child rollback, original transform
order, line-record padding and final exact point/segment counts survive. Output
limits are checked before appending each simple child; no partial geometry is
published. All source instructions remain unexecuted.

The combined gvar oracle passes **462,131 inputs / 51,251,832 exact bytes**.
The varied-outline addition contributes **205,227 inputs / 35,327,464 bytes**:
all original composite flags with and without variation data, every signed
16-bit coordinate on a nested attached fixture, empty/cyclic/deep children,
whole-tree scratch admission, missing/short coordinates, truncation/mutation and
the actual GPU font fixture. The unchanged unvaried oracle still passes all
**72,993 inputs / 15,498,308 bytes**. Source fixtures are shared between test
drivers without changing the native sources or removing original cases.

The targeted native default-contraction control remains separate and fails at
varied input 14: native `0x41cbe4a9`, Rust `0x41cbe4a8`. Reproduce using
`PROGPU_RUST_VARIED_ONLY=1 PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test --locked
--no-default-features --test gvar -- --ignored --nocapture`. The source-arithmetic
oracle retains the existing isolated executable policy. No native package,
compiler default, shader, driver or pixel tolerance was changed.

An authored two-glyph TrueType font varies both a simple rectangle's width and
its parent's scaled XY offsets. Half-peak execution produces exact physical
design bounds `[4.25, 4.5, 20.75, 20.5]`. The production varied API feeds ordinary
retained GPU paths after caller source retirement. Metal validates all three
DPIs, cold/warm reuse, painter order and resource ownership. Chromium adds the
same three-DPI cases, bringing its full mandatory inventory to **204,800
offscreen pixels** across both DPRs, alongside the twelve gvar CPU controls per
DPR, original six shaders, resize, queue completion and actual compositor pixels.
Both DPRs pass locally. Payload checkpoint `a66439c7e` also passes hosted Linux/
Windows portable, Vulkan/winit and browser lanes (Rust run 42519799824); its
hosted macOS lane was cancelled. The new varied-path hosted checks remain pending.

The matched `outline_benchmark FONT 0 --varied 8192` workload applies 8192 to
every original axis, enumerates every glyph slot and hashes original point and
path words. Both implementations include public preflight, scratch/output
allocation, full decoding and hashing; input I/O and font opening are excluded.
Five warmups precede forty measured rounds. Rust owns published source/coordinate
generations; original C++ caller scratch is allocated per public call. These are
public API baselines, not isolated algorithm speed or full performance parity.
The C++ benchmark retains **default compiler contraction**, unlike the separate
source-arithmetic oracle above.

| Local input | Rust p50 / p95 / p99 µs | Original C++ p50 / p95 / p99 µs | Identical output |
| --- | --- | --- | --- |
| SFNSMono, 1,632 glyphs | 4446.125 / 4593.958 / 4608.000 | 4801.125 / 5038.208 / 5099.000 | 49,117 points, 39,549 segments; checksum 13411200118553759853 |
| NewYork, 1,811 glyphs | 6919.500 / 7573.042 / 7708.458 | 7900.375 / 8045.000 / 8151.625 | 87,519 points, 69,402 segments; checksum 15093604794901869438 |

Measured on local ARM64 macOS with Rust 1.90.0 release and Apple Clang 21.0.0,
`-std=c++20 -O2 -Wall -Wextra -Werror`. Input SHA-256:
SFNSMono `55caaed55254a28ac793847e8976be16c5ba7cbad1ec2ee2d5d86d4e6b3fa0c1`;
NewYork `296ac5e08bea4da51b7c46eb9647f69be0f56077a9c35b4bc9f433d7e8734771`.
Binary SHA-256: Rust
`9cdafc4c443ddf632c0d75b3cb70727354a8f41d12895d77ab5f708934a87649`;
native `60640abe7d95c36b99a021b2a45e4d8058d2055872053d80573a939e21ed00fd`.
The native adapter compiles the original SFNT, font-variation, item-store,
TrueType/static/varied, gvar metadata/packed/IUP/payload/simple/component and CFF
translation units. System font binaries are not redistributed. Matching these
workloads does not close the independent authored compiler-default difference.
No optimization or overall speedup claim follows from this initial baseline.

Remaining text work includes HVAR/VVAR and device metrics, hinting, Unicode,
shaping, fallback, paragraph layout, glyph atlases and retained text replay.
The broader rendering, image, material, clip, layer, effect and transport parity
gates remain unchanged. The crate is an incremental port, not a full-stack release.

## Horizontal variation metrics and prepared source ownership

`font/horizontal.rs` ports the original `progpu_native_hvar.cpp` and
`Font/progpu_native_font_horizontal_metrics.cpp`. Legacy advance-only lookup
retains its original 12-byte minimum header, ignored version field and disabled
zero-store result. Missing/disabled HVAR is `None`, distinct from an active store
returning zero. Original axis capacity, implicit indices, repeated last map entry,
empty-map behavior, null subtables and missing-item zero results are preserved.
The independent source-bearing preparation requires the complete version-1.0
20-byte header and nonzero store, validating its LSB map before the legacy advance
parser. The original unused right-bearing offset is not newly interpreted.

`HorizontalAdvanceInstance` and `HorizontalMetricsInstance` own the selected
immutable face, exact coordinate request, encoded store/map layouts and complete
cached region scalars. They cannot be applied to unrelated font storage. Private
owned item-store/map wrappers restore validated borrowed views without reparsing
or expanding encoded entries at every query. Public scalar slices are immutable.
LSB absence remains separate from a present zero result; the original finite
result check remains at that boundary. Rust publishes an owned result or error,
without exposing partially initialized caller scratch or mutable source aliases.

The design advance APIs keep original base selection and evaluation order:
original hmtx metrics when admitted, otherwise half the original positive units
per em. Empty coordinate requests return the base before validating variation
tables. The explicit HVAR-only route retains the original no-phantom behavior;
the full route gives active HVAR precedence over gvar, including over malformed
unused gvar. Only absent/disabled HVAR selects original right-minus-left phantom
deltas. Item counts use original simple points or component descriptors followed
by four phantom slots; absent glyf/loca retains four. No hinting, shaping, origin
selection or raster-policy admission is inferred.

The unchanged-native source-arithmetic oracle passes **205,966 inputs /
22,637,340 exact bytes**, covering all signed 16-bit coordinates, all glyph indices,
raw map formats, direct/prepared outputs and scalar bits, complete truncation and
byte mutation, legacy/strict header differences, empty-coordinate bypass, base
fallback, HVAR/gvar precedence, and absent/empty/invalid-duplicate hmtx selection.
All original **157,958 item-store cases / 6,892,604 bytes** still pass. The C++
driver links the actual original HVAR, horizontal metric, gvar phantom and parser
dependencies. It never substitutes a font algorithm. Linux/macOS CI requires the
new oracle alongside all earlier font suites.

The independent ARM64 compiler-default control exposes input 68,165: native
`0x4265506b`, Rust `0x4265506c` in a raw HVAR delta. Reproduce with
`PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test --locked --no-default-features
--test horizontal -- --ignored --nocapture`. The ordinary source-arithmetic
control retains its isolated separate-operation policy. Native package/compiler
defaults, shaders, drivers, original pixels and previous numeric gates stay
unchanged. This is not full source/managed/native metric qualification.

Eight new browser controls per DPR check source-owned prepared values, direct
advances, repeated maps, explicit bearing absence, legacy zero-store acceptance,
strict rejection, empty-request bypass and coordinate capacity. They pass with
all **204,800** existing offscreen pixels, six canonical shaders, both original
canvas captures, queue completion and resize. Strict native/WASM lints and
CPU/all-feature tests pass locally. Recursive-outline checkpoint `8c7ad0efb`
also passes hosted Linux/Windows portable, Vulkan/winit and full browser lanes
(run 42520804177); hosted macOS was cancelled. Current-head CI remains separate.

### Profile-guided prepared base selection

The initial prepared metric baseline matched native checksums but showed a slower
SFNSMono median (Rust 69.417 µs versus native 57.750 µs). Instruments Time Profiler
then found 1,711 of 2,576 Rust samples inside base-advance resolution, including
1,461 leaf samples there. The implementation was repeatedly resolving immutable
hmtx/header selection for every prepared glyph query. A matched native profile
also identified original table/header lookup costs; no native code was changed.

Prepared Rust instances now capture that exact base-selection decision once,
alongside their already frozen source. The descriptor retains either original
half-em fallback or the original selected hmtx span and complete-metric count.
Query-time checked reads preserve repeated last advance and truncated-slot errors.
Invalid base metadata remains a later query error, never a new preparation
failure. Missing and present-empty tables remain distinct even for an offset-zero
empty table; invalid later duplicate ranges still select the last valid original
entry. Native differential controls plus an independent unit test cover these
boundaries. The direct route uses the same decision logic without a persistent
instance. No arithmetic, source bytes or original error gate is changed.

The same 100,000-round Instruments workload recorded 1,104 samples after caching;
the repeated base-resolution frame disappeared. The complete before/native/after
frame summaries and exact input/binary hashes are retained in
`perf/horizontal-metrics-time-profiler.json`. These statistical traces support
the lookup-cost diagnosis, not GPU, allocation or full-frame qualification.
The profiler harness adds an optional `PROGPU_RUST_PROFILE_ROUNDS` mode; it is
disabled in ordinary timing runs. Timings vary across short runs, so the final
comparison uses the same benchmark harness for both Rust binaries.

Final sequential uninstrumented observations, five warmups and forty measured
rounds, every glyph at normalized coordinate 8192 on every original axis:

| Local font | Rust before p50 / p95 / p99 µs | Cached Rust p50 / p95 / p99 µs | Original C++ p50 / p95 / p99 µs |
| --- | --- | --- | --- |
| SFNSMono, 1,632 glyphs | 25.834 / 60.291 / 106.791 | 11.166 / 11.291 / 11.333 | 25.458 / 25.667 / 28.791 |
| NewYork, 1,811 glyphs | 48.292 / 55.958 / 72.042 | 25.583 / 29.833 / 33.916 | 44.875 / 46.125 / 47.833 |

All three binaries produce identical checksums: SFNSMono `971861562991243685`
(two axes/two regions), NewYork `719888288547038463` (three axes/five regions).
The measured workload includes prepared full-advance lookup, optional LSB lookup
and hashing. Font opening, instance preparation and a direct/prepared Rust
consistency pass precede timing. Active HVAR needs no per-glyph phantom scratch;
the native adapter preserves the original full public query. C++ uses Apple
Clang 21.0.0 `-std=c++20 -O2 -Wall -Wextra -Werror` with its original contraction
defaults; Rust uses 1.90.0 release. Input hashes are the same SFNSMono/NewYork
hashes recorded above. The compact profiler receipt retains the initial,
intermediate and final timing observations and all measured binary hashes.
This improvement is scoped to these prepared CPU metric workloads. It does not
close the independent compiler-default fixture difference or establish full
text/frame performance parity. Run the Rust workload with
`cargo run --locked --release --no-default-features --example metrics_benchmark
-- FONT_PATH 0 8192`.

## Static vertical metrics and independent VVAR

This milestone ports the unchanged original
`Text/Font/progpu_native_font_vertical_metrics.cpp` and
`Text/progpu_native_vvar.cpp`. Static queries retain vhea/vmtx field values,
repeated final advances, optional missing top bearings, original positive glyf
header bounds and VORG's original binary search (including its existing malformed
ordering/version behavior). Vertical origins keep VORG precedence, then actual
bearing plus glyf maximum, then original horizontal-metric fallback with negative
half values rounded down. Invalid glyph indices keep their separate original
fallback. No outline reconstruction, hinted placement, CFF control-bound origin
or automatic variation application is introduced.

Neutral VVAR preparation is deliberately stricter than legacy HVAR. It requires
exact normalized coordinate counts/ranges, validates original fvar admission,
rejects every duplicate/out-of-range selected-face VVAR directory record, checks
all region tuples and subtable region indices, and validates every encoded map
entry before narrowing. Format-one maps, reserved format bits, empty explicit
maps and invalid unused references reject. Null subtables and the original
no-variation sentinel stay valid. Missing optional mappings differ from an
explicit zero delta; an absent VVAR still requires a valid glyph inventory and
normalized request. This does not change generic item-store or HVAR acceptance.

`VerticalMetricsInstance` owns the exact selected face, original coordinates and
private cached region scalars. Its public immutable views cannot substitute a
foreign source or alter a prepared scalar. Rust's exclusive borrows prevent
caller-buffer aliasing; `variations_into` retains the original complete dry pass,
capacity/error order, unchanged destination on failure and untouched caller tail.
The static origin API and neutral delta API remain separate. In particular,
these results do not apply VVAR vOrg to CFF2 source placement or admit sideways
source drawing; original provider/package/source-pixel gates remain required.

The unchanged-native source-arithmetic oracle passes **338,184 inputs /
108,080,984 exact bytes**. It covers every signed coordinate, all glyph indices,
all signed ascent values with bounded/empty glyph origins, exact scalar/delta
bits, atomic batch outputs/tails, raw map formats, null/sentinel references,
complete truncation/mutation, strict malformed unused records, zero region offsets,
collection face selection and original unsorted/duplicate/truncated VORG lookup.
Three focused ownership/rollback/static-fallback tests also pass. The independent
ARM64 compiler-default control fails at input **68,732**, native `0x40c5e44a`
versus Rust `0x40c5e448` in a VVAR delta. Reproduce with
`PROGPU_RUST_NATIVE_DEFAULT_FP=1 cargo test --locked --no-default-features
--test vertical -- --ignored --nocapture`. The ordinary isolated oracle uses its
existing separate-operation policy; no native package/compiler default, shader,
driver, output tolerance or original qualification gate was changed.

Native and WASM strict Clippy, CPU/all-feature tests, extracted native and WASM
package checks pass locally. Ten new browser controls per DPR validate retained
VVAR values, independent static VORG, absent mappings/tables, strict request and
raw-directory rejection, complete unused-region validation and batch prefix/tail.
Both browser DPRs pass all **204,800** original offscreen pixels, six canonical
shaders, queue completion, resize and actual canvas screenshot assertions. This
is metadata validation plus existing rendered-path coverage, not vertical text
rendering qualification. HVAR checkpoint `7c012a4a6` passes hosted Linux/Windows,
Vulkan/winit and full browser lanes in run 42522247278; macOS remained queued at
this milestone. New-head CI remains independent.

Initial matched local workload: every glyph, static height/origin plus prepared
neutral VVAR lookup and hashing; five warmups and forty timed rounds. Opening,
I/O, preparation and Rust single/batch consistency checks precede timing. Rust
1.90.0 release and unchanged Apple Clang 21.0.0 default contraction are used.

| Selected local face 0 | Rust p50 / p95 / p99 µs | Original C++ p50 / p95 / p99 µs | Matching checksum |
| --- | --- | --- | --- |
| ADTNumeric, 311 glyphs, 3 axes / 24 regions | 25.833 / 26.333 / 27.666 | 21.250 / 21.417 / 24.875 | 6560198863156398706 |
| CJKSymbolsFallback, 1,103 glyphs, 2 axes / 5 regions | 102.291 / 105.375 / 114.125 | 75.542 / 131.083 / 148.958 | 576141269095100997 |

These are initial observations: Rust p50 is slower on both workloads. No
optimization or full performance parity is claimed. Compact binary/input/source
hashes and exact output receipts are retained in `perf/vertical-metrics-baseline.json`;
the original local font files are not redistributed. Performance investigation
must use Instruments before making an optimization claim. Temporary benchmark
executables, inventory/results scratch and verified package extraction are removed
once their compact evidence has been retained.

## Original simple UTF-16 glyph runs and advances

The new `text` module ports the owned original
`Text/progpu_native_sfnt_simple_glyph_shaper.cpp`, paired with
`ProGPU.Text/SfntSimpleGlyphShaper.cs`. Both existing implementations already
provide this contract, so this Rust addition changes neither source algorithm nor
its managed/native policies. It introduces no font engine dependency, FFI renderer
or external implementation. Full OpenType shaping, bidi, Unicode properties,
fallback, paragraph layout and text-atlas replay remain required future work.

`SimpleGlyphRun` freezes the exact selected face and original UTF-16 source with
its complete cluster/glyph arrays. The allocation-free buffer form retains whole
mapping preflight, original 65,536-glyph capacity, unchanged outputs on failure and
untouched tails. Supplementary pairs occupy one glyph and two cluster entries;
unpaired surrogates retain their numeric code unit. Only the original C0/C1 control
inventory maps to the supplied blank; U+00AD selects a nonzero supplied hyphen or
the blank. Empty/control-only runs retain their original missing-cmap admission.

Advance metrics are per occurrence rather than glyph-ID indexed. Classification
visits every original code-unit position, preserving first-mapped-position control
ownership, invalid cluster-slot skipping, independent low-surrogate visits and
noncontrol advances for unmapped slots. The numeric route keeps unsigned width or
height selection, left-associated original multiply/multiply/divide, zero-upem
substitution, finite-input admission, negative scales and exact ties-to-even i32
bounds. No hinting, position, ink origin or outline rotation is inferred. A complete
numeric pass precedes output publication. Original native capacity/error precedence
and scratch behavior remain explicit: scratch is classified before numeric failure,
while advance output and both caller tails remain unchanged.

The UTF-16 walker depends on the preceding unit's pair consumption. Cluster writes
depend on accumulated glyph count; control classification depends on the first
source occurrence. These ordered data dependencies retain their original linear
walks. The native and Rust buffer APIs allocate no heap storage in these walks;
owned construction allocates its three bounded arrays once and retains the shared
font generation. Numeric preflight preserves early error ordering and atomic
publication; the exact floating primitive is Rust's ties-to-even intrinsic.

The original C++ source oracle passes **30,086 cases / 55,356,876 exact bytes**
using **unchanged compiler defaults**, including every BMP code unit, every
supplementary surrogate pair and independent low-half read, all capacity edges,
invalid/missing fonts, original control/hyphen mapping, shared clusters, scratch
and output tails, all 16-bit metric values, adjacent floating ties, intermediate
overflow/underflow and deterministic raw IEEE requests. Four focused ownership,
publication, capacity and source-admission tests pass.

A second control builds and executes the actual original managed helper, without
transcribing its implementation. It passes **10,683 controls / 35,996,228 exact
bytes** for UTF-16 decoding, owned glyph-run creation and valid advance/tail values.
Its explicit authored cmap callback matches the independently qualified font
fixture; it does not claim C# font-parser qualification. Advance cases assign one
ID per occurrence so the managed glyph-ID callback selects exactly the supplied
native occurrence metric. Native-invalid requests are excluded from this paired
valid-output comparison: C#'s partial writes/exceptions and native whole-output
preflight are separate original contracts. Native failure/scratch semantics remain
covered by the complete C++ oracle. The .NET 10.0.403 control runs in a disposable
project linking the original source. Linux/macOS CI installs that SDK and requires
both oracles; source-path triggers cover the managed helper as well as native.

Strict native/WASM Clippy, CPU/all-feature tests and extracted native/WASM package
checks pass. Ten browser controls per DPR verify owned UTF-16 arrays, exact
horizontal/sideways advance values, scratch/tails, missing-cmap controls, soft
hyphens, first-cluster ownership, finite/overflow rejection and complete capacity
admission. Both DPRs also pass all **204,800** existing offscreen pixels, six
canonical shaders, queue completion, resize and actual canvas screenshot pixels.
These text checks do not imply shaped-text rendering. Vertical checkpoint
`990a45b64` passes hosted Linux/Windows portable, Vulkan/winit and browser lanes in
run 42523397293; hosted macOS was cancelled. New-head CI is separate.

The matched Release baseline measures repeated original buffer mapping, prealigned
advance calculation and hashing with reused arrays/scratch. Font I/O/opening and
initial per-occurrence metric gathering precede timing. The 14-unit authored
pattern includes Latin letters, space, soft hyphen, controls, a supplementary pair,
an unpaired surrogate, ZWJ and a Hebrew code unit. This exercises simple mapping,
not script shaping. Each round uses font size 13.25 and scale 1.25, five warmups
and forty measurements; C++ retains original Apple Clang 21.0.0 defaults, Rust
uses 1.90.0 release. No per-round heap allocation appears in either buffer path.

| Local face 0 / UTF-16 units | Rust p50 / p95 / p99 µs | Original C++ p50 / p95 / p99 µs | Matching checksum |
| --- | --- | --- | --- |
| SFNSMono / 14 | 1.833 / 1.875 / 1.917 | 0.334 / 0.375 / 0.375 | 6137714686561589816 |
| SFNSMono / 3,584 | 105.042 / 114.584 / 226.792 | 91.542 / 144.375 / 202.708 | 14569572735245613093 |
| SFNSMono / 28,672 | 850.667 / 1036.792 / 1132.416 | 732.209 / 776.958 / 805.167 | 1884019004303862565 |
| Helvetica / 14 | 0.458 / 0.459 / 0.541 | 0.542 / 0.584 / 0.584 | 8526167976510404703 |
| Helvetica / 3,584 | 113.709 / 114.208 / 128.666 | 136.417 / 140.959 / 159.875 | 11230045730393425189 |
| Helvetica / 28,672 | 915.042 / 1868.042 / 2248.166 | 1102.042 / 1181.208 / 1210.875 | 4417436596732891941 |

The scratch prefix is one byte per glyph: 13, 3,328 and 26,624 bytes respectively.
These initial observations retain both regressions and tail variability. Rust
medians are slower on SFNSMono and faster on Helvetica; no optimization or full
performance parity is claimed. Instruments investigation remains required before
any optimization claim. Exact input/binary/source hashes and all outputs are kept
in `perf/simple-text-baseline.json`; original local fonts are not redistributed.
Temporary oracle/managed projects clean themselves, and benchmark binaries,
duplicate scratch receipts and verified package extraction are removed after
retaining compact evidence.

Extracted-package validation uses fresh disposable native and WASM target
directories for this milestone. The first shared-target attempt reused an older
compiled library (new tests could not resolve `progpu::text`) despite the archive
containing the new module. That attempt is not qualification evidence. Fresh
native all-feature/all-target and WASM browser checks both pass; their complete
build directories are removed afterward. Future local archive checks must avoid
shared-target freshness assumptions with normalized archive mtimes.

## Strict Unicode decoding, properties and initial script runs

The Rust `text::unicode` module ports `progpu_native_unicode.cpp` and the original
category/directional lookup sources. Strict UTF-8 and UTF-16 decoding uses Rust's
checked standard-library iterators, retaining the original whole-input validation
before capacity admission or writes. Invalid encodings leave the complete output
unchanged. Source positions remain byte offsets for UTF-8 and code-unit offsets
for UTF-16; supplementary scalars retain their original lengths. The independent
simple-glyph API still accepts original unpaired surrogate code units.

`UnicodeScalar` retains the original 16-byte layout and zero reserved byte;
`ScriptRun` retains its 20-byte layout. Script itemization preserves leading
Common/Inherited attachment to the first following resolved script, later
attachment to the active script, arbitrary original raw tags, saturated source
ends and zero-clamped reversed spans. It validates every code point before output
mutation and does not fabricate cluster boundaries or additional tag/order rules.
Buffer APIs allocate no scratch; `DecodedText` retains an immutable Arc source,
its encoding kind and owned scalar generation. Runs can be produced separately.

The generator projects only nine selected numeric inventories from the two
checked-in native headers, retaining exact source hashes, dimensions and order.
It validates range ordering, widths, tag references, category/CCC bounds, packed
bidi fields and decimal-zero inventories. It makes no network request or Unicode
version upgrade. Script/CCC/directional facts inherit the original managed
generators' Unicode 17 provenance, including Unicode data sourced through
HarfBuzz; categories/digits retain the independent original .NET 10 snapshot.
These are property facts, not a copied shaping engine. No Indic/USE syllable
machine or foreign algorithm is added to the Rust implementation.

The unchanged C++ oracle passes **552,778 inputs / 137,213,300 exact bytes** under
original compiler defaults. Coverage includes all Unicode values and invalid
boundary values, every valid UTF-8/UTF-16 scalar, all two-byte input combinations,
all single UTF-16 units, surrogate/continuation boundary combinations, malformed
late sequences, error-before-capacity ordering, all source lengths, raw script
patterns, saturated/reversed spans, untouched tails and reserved fields. Focused
Rust tests check ownership, exact record layouts and atomic failure.

An independent disposable .NET 10.0.403 control links the actual original
`OpenTypeScriptResolver`, `UnicodeShapingProperties`, bidi data and required
source dependencies. It compares **1,114,132 raw values / 40,108,752 exact bytes**
for script, CCC, bidi class, category, mirrors, vertical forms and bracket
presence/pair/kind. It does not substitute rewritten C# algorithms. Unused original
syllable-machine dependencies compile to satisfy the unchanged helper, but are
never called or ported. Decimal values retain their exhaustive C++ control.
Both original implementations already supply these contracts; this milestone
changes only the new Rust implementation and its test/build/documentation paths.

Fourteen browser controls per DPR verify exact UTF records and script ranges,
strict malformed input rejection, aliases, CCC, invalid scalar defaults and
directional/bracket/digit metadata. The initial browser expectation incorrectly
mapped ASCII '(' to FE35; the original table maps full-width FF08 and leaves ASCII
unchanged. The corrected assertion explicitly checks both. Both local DPRs pass
all 204,800 existing offscreen pixels, six shaders, resize, final queue completion
and real canvas presentation. This property layer does not implement full shaping,
normalization, bidi resolution, grapheme/line breaking or layout.

Hosted simple-text checkpoint `abc0be335` passes Linux/Windows portable and
Vulkan/winit controls; macOS was cancelled. Its browser run 42524949419 passes
both offscreen inventories but loses the device at final DPR 2 capture with
"A valid external Instance reference no longer exists." This is an open hosted
presentation gate despite earlier successful hosted checkpoints and the current
local browser pass. No shader, driver, compiler, assertion or timeout is changed
to turn that failure into a pass.

The matched Release baseline uses strict decoding, original script itemization
and the same scalar/run checksum. Inputs and reusable output vectors are allocated
before timing; five warmups precede forty rounds, measured serially after all
compilation/tests finish. C++ uses original Apple Clang
21.0.0 defaults, Rust uses 1.90.0 release. All twelve workloads have matching
checksums. Representative 4,096-repeat workloads are:

| Pattern / encoding / source units | Rust p50 / p95 / p99 µs | C++ p50 / p95 / p99 µs |
| --- | --- | --- |
| ASCII / UTF-8 / 73,728 | 1497.083 / 1537.416 / 1585.083 | 1514.334 / 1589.667 / 1635.541 |
| ASCII / UTF-16 / 73,728 | 1514.833 / 1582.833 / 1589.458 | 1454.625 / 1488.083 / 1530.000 |
| Mixed / UTF-8 / 159,744 | 2061.334 / 2094.500 / 2116.333 | 2104.042 / 2201.000 / 2255.917 |
| Mixed / UTF-16 / 86,016 | 2061.250 / 2103.750 / 2135.125 | 1844.042 / 1905.000 / 1982.250 |

Mixed input includes Latin, combining marks, Arabic, Japanese and supplementary
scalars; it measures decoding and script classification, not language shaping.
These are baseline observations, including slower UTF-16 medians. No optimization
or full performance parity is claimed; Instruments profiling precedes further
optimization. `perf/unicode-baseline.json` retains all twelve results and exact
input/source/binary hashes. Native reproduction links `native_unicode_benchmark.cpp`
and the unchanged `progpu_native_unicode.cpp` with `-std=c++20 -O2 -Wall -Wextra
-Werror`; it accepts the same `ascii|mixed 8|16 REPEATS` arguments as Rust.

Strict native/WASM Clippy, the all-feature test suite, all five Unicode tests
(including both source oracles), deterministic data checks and fresh extracted
native all-target/WASM browser package checks pass. Temporary source-control
projects clean themselves. Verified duplicate package targets and temporary
benchmark executables are removed after retaining compact receipts.
This checkpoint removes 1,008,130,295 logical bytes of duplicate package/build and
benchmark artifacts; active toolchains/browser dependencies and compact evidence
remain. The archive has 141 files, approximately 1.5 MiB before compression.

## Original bidi level resolution

`text::unicode::bidi` ports the complete original
`Bidi/progpu_native_unicode_bidi.cpp`, itself ProGPU's native port of
`Bidi/Uax9Resolver.cs`. Its phases retain original isolate pairing, paragraph
inference, explicit levels/overrides, isolating run construction, W1–W7, ordered
N0 bracket resolution, N1/N2, I1/I2, L1 and retained formatting-control levels.
The existing architecture research above keeps this result on the CPU, reusable
and independent of GPU realization. The normative boundary is
[UAX #9 revision 51](https://www.unicode.org/reports/tr9/tr9-51.html): logical
levels, line reordering and glyph shaping/rendering are separate operations.
No foreign resolver implementation is copied or invoked.

The buffer API preflights all four scratch capacities and output capacity before
mutation. It keeps exact source indices/lengths and untouched tails, and does not
invent code-point/range/paragraph-request admission absent from the original.
Requests zero/one select direction; every other signed byte invokes original
inference. Fixed explicit and bracket stacks retain capacities 127 and 63;
explicit levels retain the original 125 bound. Bracket overflow abandons N0 for
that sequence without partial fixup; pairs remain sorted by opening position and
canonical 2329/232A equivalence is retained. Scratch storage has N units, 4N
indices, N runs and floor(N/2) pairs. Time retains the original O(N²) worst case
for nested FSI/context searches; scratch is O(N), with no per-resolution heap
allocation in the caller-buffer route.

`BidiWorkspace` allocates reusable scratch separately and rejects oversize
resolution without growing during a call. `BidiParagraph` owns the original
`DecodedText` plus logical scalar levels. Its strict encoded-input policy stays
separate from raw scalar resolution. The eight-byte output record retains its
zero reserved byte. Native/C# scratch implementation types are represented with
safe typed Rust records; no unsafe code, borrowed foreign resources or FFI shim
is introduced.

The exact source oracle exposed an int8 narrowing corner: raw admitted sequences
whose isolates cross a paragraph separator can revisit levels during implicit
resolution. A fixture with nested FSI/RLE, a separator and trailing controls
requires original signed-byte wrapping. Explicit `wrapping_add` preserves C++
and unchecked C# behavior in debug and Release instead of adding a new panic,
clamp or rejection. This is raw source parity, not a claim about correct P1
paragraph splitting; the higher-level paragraph/line stage remains required.

**168,077 C++ controls / 257,616,416 exact bytes** pass with original compiler
defaults. The comparison includes every output and scratch field/tail, all 23
bidi-class triples in three directions, all ASCII pairs, every paired bracket,
canonical bracket equivalents and NSMs, depths through 260, overflow/reset and
unmatched controls, all signed-byte requests, all original source lengths,
invalid raw scalar values, all independent capacity failures and seeded mixed
sequences. **102,541 controls / 28,430,576 exact bytes** also match the actual
original managed `Uax9Resolver`. Its own permissive UTF-16 decoder runs unchanged;
raw native source ranges are qualified separately. Existing managed/native
providers already supply this resolver and remain unchanged.

Independent official Unicode 17 fixtures pass **91,707 character cases** and
**770,241 bidi-type/direction cases** for resolved levels and, where specified,
paragraph levels. `eng/progpu-test-rust-bidi-conformance.py` downloads only the
versioned data into a disposable directory and checks SHA-256 before execution:

| Official source | SHA-256 |
| --- | --- |
| [BidiCharacterTest.txt](https://www.unicode.org/Public/17.0.0/ucd/BidiCharacterTest.txt) | `a3e6e905ab5afbe318a96df5401d0372a04cd73ef139ab5e3cf0ae241c255488` |
| [BidiTest.txt](https://www.unicode.org/Public/17.0.0/ucd/BidiTest.txt) | `888bdfc8090652272d1f859cdb00ae659e2dc6c26740be61ef1d03998a687620` |

The runner checks every supplied level except fixture-defined X9 `x` slots. Those
retained control slots have independent exact C++/C# coverage. It does not assert
L2 visual-order fields or claim full bidi/text conformance. P1 splitting, line
wrapping, cluster-preserving visual reordering, inline direction policies,
normalization, grapheme/line breaking, shaping/layout and actual text pixels
remain separate port work. Linux/macOS CI requires both source comparisons and
the pinned independent level check; the distributable runner also accepts local
fixture paths without requiring network access from the crate.

Fifteen browser controls at each DPR verify exact source ownership and eight-byte
records, explicit/inferred directions, mixed scripts, surrogate lengths, isolates,
N0 brackets/NSMs, retained controls, deep embeddings, empty input and strict
encoded-source rejection. All existing 204,800 offscreen pixels, canonical shader
modules, resize, queue completion and actual canvas screenshots still pass after
the final implementation change. Native/WASM strict lints, all-feature tests,
complete Unicode property oracles and official level fixtures also pass.
Unicode checkpoint `bbc5fdf25` passes hosted Linux/Windows, Vulkan/winit and browser
run 42526865494; macOS was cancelled. The preceding `abc0be335`
DPR 2 device-loss failure remains evidence of a hosted reliability gap and is not
erased by a subsequent pass. New-head CI remains separate.

### Bidi performance investigation

The original buffer workload uses five warmups and forty measured rounds of
resolution plus identical source-level hashing. UTF decoding, input allocation,
output and scratch allocation precede timing. C++ uses Apple Clang 21.0.0
`-std=c++20 -O2 -Wall -Wextra -Werror` with unchanged defaults; Rust uses 1.90.0
Release. All nine workloads retain matching paragraph/level checksums and exact
scratch extents. Initial results and variation are preserved in
`perf/bidi-baseline.json`.

Matched six-second Xcode Instruments Time Profiler recordings of the same mixed
65,536-unit workload identified property search inside the resolver. Exported
instruction samples map to the exact measured Release binaries: 806/4,792 samples
before and 918/5,999 after fall in the remaining binary-lookup region (16.8% and
15.3%). The traces also record E/P-core migration, so raw sample counts are not
elapsed-time comparisons. A narrowed-slice search candidate gave inconsistent
results across repeated interleaved runs and was rejected.

The retained improvement is a **128-byte typed ASCII bidi table computed at
compile time from the same frozen original packed inventory**. It adds no dynamic
cache, allocation, initialization race or new Unicode policy. Non-ASCII lookup
and all resolver rule ordering remain unchanged. Complete C++/C# property and
bidi comparisons, official level conformance and browser tests were rerun after
this change. Three interleaved before/after/C++ trials give these medians of the
individual p50 measurements:

| Input / units | Initial Rust µs | Final Rust µs | Original C++ µs |
| --- | --- | --- | --- |
| ASCII / 36,864 | 724.834 | 575.375 | 687.500 |
| Mixed / 65,536 | 1407.125 | 1315.125 | 1300.083 |
| Isolates / 45,056 | 1056.750 | 953.125 | 994.708 |

These matched CPU workloads improve about 21%, 7% and 10% respectively; mixed
Rust remains slightly slower than C++. They do not establish frame latency,
allocator/GPU behavior, universal script performance or full-stack parity.
`perf/bidi-time-profiler.json` preserves both exported profile summaries, exact
binary/source hashes, all interleaved measurements and the rejected experiment.
The final independent nine-workload receipt is `perf/bidi-final-baseline.json`.
Temporary traces, exports, binaries and duplicate package trees are removed after
retaining these compact receipts.

Fresh extracted native all-feature/all-target and WASM browser package checks pass
using independent empty target directories. The archive contains 152 files, about
1.6 MiB before compression (391.8 KiB compressed). This checkpoint removes
1,050,545,477 logical bytes of temporary trace/fixture/benchmark and
duplicate package/build artifacts after preserving the compact evidence above.

## Original grapheme segmentation

`text::unicode::grapheme` ports the original native Unicode 17 UAX #29 revision-47
boundary state without introducing a foreign segmentation implementation.
[UAX #29 revision 47](https://www.unicode.org/reports/tr29/tr29-47.html) separates
grapheme, word and sentence boundaries; line breaking remains a separate stage.
The CPU preparation/immutable generation boundary established by the architecture
research above continues here. Segmentation never normalizes, shapes, reorders or
realizes a glyph, and retained callers can preserve one captured generation.

The original two policies stay explicit: `Unicode17` retains GB9c Indic conjunct
joining; `ManagedCompatible` reproduces the native shaping-only path which omits
GB9c. The latter is not a promise to follow arbitrary future/platform StringInfo
versions. Original managed `OriginalGraphemePolicy` is tested independently and
unchanged. Its malformed UTF-16 rejection and empty `[0]` boundary inventory stay
separate from the native raw-scalar contract and empty cluster inventory.

Three added numeric inventories project the exact original grapheme, extended
pictographic and Indic conjunct ranges. All twelve projected arrays have checked
extents, order and property domains; generator provenance and original source
hashes remain in `unicode/data.rs`. The added tables contain 4,287, 1,353 and 1,515
u32 values (28,620 numeric bytes) from the original official Unicode 17 facts.
No newer host Unicode library replaces these records.

The bounded state preserves CR/LF/control ordering, Hangul composition classes,
Extend/ZWJ/SpacingMark/Prepend, separate Indic linker/consonant flags, pictographic
ZWJ readiness and regional-indicator parity across cluster boundaries. Two
original ordered scans count before writing caller output. Complete capacity
failure and unused tails remain unchanged. Raw scalar values and source ranges
keep original admission; cluster source extents union actual indices with
saturated endpoints, including reversed/noncontiguous records. Strict UTF-16
starts validate the complete source before capacity checking and write zero to
supplementary low-surrogate slots. Scratch is O(1); time is O(N log R) for N
scalars and fixed R property ranges. `GraphemeText` owns the strict decoded source,
clusters and selected policy without retained borrowed font/GPU dependencies.

The native original-default-compiler oracle passes **1,888,801 controls /
84,956,820 exact bytes**: every property value through U+110010, invalid high
boundaries, all 22 representative-class/state triples, source-length saturation
and unions, long flag/emoji/Indic chains, seeded mixed raw sequences, both policies,
capacity/untouched-tail cases and every single UTF-16 value with malformed contexts.
The actual original C# `OriginalGraphemePolicy` passes **89,921 controls /
1,172,196 bytes**, including all single code units, ASCII pairs and representative
class/state triples. These source checks do not substitute a guessed managed
runtime policy or a Rust reference library.

The independent official
[GraphemeBreakTest.txt](https://www.unicode.org/Public/17.0.0/ucd/auxiliary/GraphemeBreakTest.txt)
passes **766 cases**, each checked as UTF-8 clusters, UTF-16 clusters and strict
UTF-16 starts. The temporary-download runner checks SHA-256
`e2d134d2c52919bace503ebb6a551c1855fe1a1faec18478c78fff254a1793ec` before execution
and removes the fixture on exit. This checks default extended graphemes only;
word/line segmentation, fallback, shaping, layout, retained caret/hit interaction
and text rendering remain separate required work. Both source comparisons and
the independent pinned conformance run are mandatory in the Linux/macOS CI lane.

Sixteen browser checks per DPR exercise original 16-byte records/source ownership,
code-unit starts, emoji ZWJ/flags, Hangul, CRLF/control precedence, Indic policy
separation and strict malformed-input rejection. Both DPRs pass all 204,800
existing offscreen pixels, six shaders, resize and actual canvas presentation
locally. Native/WASM strict Clippy and the all-feature suite also pass.
The preceding bidi hosted run 42528983430 repeats final DPR 2 device loss after
both offscreen inventories pass. This is an unresolved hosted presentation gate;
local passes, unchanged screenshot checks and CPU correctness do not qualify it.

### Grapheme performance baseline

The matched Rust/C++ workloads reuse strict decoded sources, cluster output and
UTF-16-start buffers. Decoding/allocation are outside timing; each iteration runs
the original two-pass segmentation or strict UTF-16-start API and hashes identical
output fields. Five warmups and forty measured rounds run serially after builds,
tests and browser execution finish. All 27 pattern/policy/size combinations match
checksums and cluster counts with O(1) scratch. C++ uses original Apple Clang 21
`-std=c++20 -O2 -Wall -Wextra -Werror` defaults; Rust uses 1.90.0 Release.

| Input / API / units | Rust p50 / p95 / p99 µs | Original C++ p50 / p95 / p99 µs |
| --- | --- | --- |
| ascii / clusters / 36864 | 2032.083 / 2090.291 / 2122.958 | 1938.625 / 2022.209 / 2099.041 |
| ascii / managed / 36864 | 1680.250 / 1720.959 / 1801.375 | 1738.375 / 1808.584 / 1832.542 |
| ascii / starts / 36864 | 1039.459 / 1197.459 / 1350.458 | 956.166 / 1141.500 / 1182.292 |
| mixed / clusters / 43008 | 2801.000 / 3018.542 / 3221.625 | 2603.375 / 2670.125 / 2696.417 |
| mixed / managed / 43008 | 2426.209 / 2510.625 / 2528.625 | 2334.917 / 2429.542 / 2457.291 |
| mixed / starts / 43008 | 1390.792 / 1499.542 / 1560.042 | 1273.166 / 1337.209 / 1354.000 |
| emoji / clusters / 43008 | 2132.833 / 2217.250 / 2272.083 | 1861.916 / 1953.542 / 2005.834 |
| emoji / managed / 43008 | 1803.625 / 1918.042 / 2048.791 | 1659.958 / 1741.084 / 1815.209 |
| emoji / starts / 43008 | 1129.791 / 1171.708 / 1200.917 | 921.208 / 955.292 / 992.958 |

These are baseline observations, not an optimization or complete performance
parity claim. `perf/grapheme-baseline.json` preserves all measurements and exact
input/source/binary hashes. Grapheme clustering does not measure fallback,
shaping, layout, interaction or rendering; further optimization requires matched
Instruments investigation. The existing unhinted/default-compiler arithmetic and
provider/package/source pixel gates remain independent and unchanged.

Fresh extracted native all-feature/all-target and WASM browser package checks pass
using independent empty target directories. The crate contains 160 files, about
1.7 MiB before compression (417.9 KiB compressed). This checkpoint removes
1,027,441,950 logical bytes of duplicate package/build trees, downloaded
fixtures and temporary benchmark executables; compact receipts remain.

## Browser GPU-process stall investigation

Grapheme checkpoint `c814583b2` passes hosted Linux/Windows and Vulkan/winit in
run 42529822673; macOS is cancelled. Browser job 128881331351 repeats the final
DPR 2 device loss after all source and 204,800 offscreen pixel checks pass. The
retained failed receipt shows five frames before the warm loop, fifteen after
its ten draws and twenty after resize, with no final completion. This excludes
an unbounded animation-frame inventory as an explanation for that receipt.

The pinned Chromium 151.0.7922.34 / Node 24.22.2 workload was reproduced on the
existing four-CPU, 6-GiB Ubuntu 24.04.5 ARM64 VM. An uninstrumented run failed
while requesting vector shader compilation info. Chromium reported GPU-process
exit 512; the crash dump's exception thread 67703 is named `GpuWatchdog` and its
exception code is `0xffffffff`. The subsequent device-loss string is a consequence,
not a diagnosis of a dropped Rust owner. The [Dawn device-lost contract source](https://dawn.googlesource.com/dawn/+/8a134682e4f49704ec8c7fc6c8510296dc014323/src/dawn/wire/client/Device.cpp)
uses the same message when its event manager shuts down; that message alone
cannot distinguish the cause. No foreign implementation is incorporated.

A separate instrumented rerun passed both DPRs. A brief debugger sample during
its DPR 2 warmup found SwiftShader's LLVM `computeKnownBitsFromOperator` /
`computeKnownBits` on an active thread; observed submissions nine and ten completed
after 5,685.4 ms. This implicates compilation latency for investigation, but does
not establish the hosted x64 cause or an application fix. Debugger attachment and
extra completion observations change timing, so that run is not performance or
uninstrumented reliability qualification. A final run of the checked-in optional
diagnostics, without the debugger, passes both DPRs on macOS and Linux ARM64.
The original hosted failure remains an open gate.

`browser/diagnostics.mjs` adds bounded test-only observations under the explicit
`PROGPU_RUST_BROWSER_DIAGNOSTICS=1` switch. It records at most 128 recent events,
64 slow/error events, aggregate submissions/completions and actual queue pending
counts. Weak queue keys and plain records avoid retaining GPU resource owners.
It preserves original arguments, return values, submissions and errors; it adds
completion observers without awaiting or retrying GPU work. The
[WebGPU queue completion contract](https://www.w3.org/TR/webgpu/#dom-gpuqueue-onsubmittedworkdone)
guides those observations. There is no descriptor, shader, compiler flag, driver,
pixel tolerance, fallback or deadline change. Default tests have no GPU observer.
Failure receipts now preserve the active phase and console errors even before an
offscreen report can be returned. CI retains Chromium stderr for the original
gate and, only after failure, runs the diagnostic observation into a separate
artifact subdirectory. The original step/job remains failed regardless of the
observation's result; this is not a retry-to-green policy.

`perf/browser-c814-investigation.json` retains the exact WASM/crash hashes, original
hosted receipt, compact successful diagnostic samples and attribution limits.
Temporary VM browser/tool copies, raw dumps and exploratory scripts are removed
after preserving that evidence; the VM is restored to its initial suspended state.

Cleanup removed 924,357,135 guest logical bytes and 21,229,655
host logical bytes of diagnostic payloads, tools and raw crash files. The VM's
suspended state was verified after cleanup. Local diagnostic browser runs pass
all existing pixel and source checks; they do not close hosted reliability.


## Original Unicode line-break resolution

`text::unicode::line_break` ports the complete owned native worker and the
independent original managed resolver's default behavior. The normative reference
is [Unicode 17 UAX #14 revision 55](https://www.unicode.org/reports/tr14/tr14-55.html).
This stage produces logical break opportunities, while width fitting and actual
line selection remain higher-level work. Foreign line/word engines are not used.

Six numeric inventories project the original line classes, Pi/Pf quotation
categories, mark categories, East Asian flags, unassigned ranges and pinned EDIT
symbol AL/ID overrides. The generator now validates eighteen original arrays,
including dimensions, sorted non-overlapping ranges, property domains and source
hashes. The existing extended-pictographic inventory stays shared with graphemes.

The resolver retains LB5–LB30b precedence and original raw-versus-resolved class
choices: CRLF/hard boundaries, space and ZWSP context, CM/ZWJ inheritance, glue,
quotation source bases, numeric lookaround, initial/Hebrew hyphens, Hangul,
Brahmic dotted-circle/virama context, raw regional-indicator parity and unassigned
pictographic emoji bases. `Unicode17` is the public default and the managed
resolver's policy. `EditSelection` retains the original native profile's exact
symbol inventory and omission of LB8a only; it does not claim full EDIT word
selection or platform source qualification.

Safe disjoint slices retain allocation-free calls with one class byte per input
scalar and one boundary byte per scalar. Both capacities are checked before
writes. As in the original implementations, an invalid later scalar leaves the
already-resolved class prefix, but break output remains untouched. Tails and
all original source records remain unchanged. Empty inputs write nothing; the
last boundary of a nonempty valid input is mandatory. `LineBreakText` owns the
exact strict decoded source, resolved classes, boundaries and policy. Context
walks retain original worst-case quadratic time; no linear-time claim is made.

Default-compiler native controls pass **2,262,599 cases / 155,775,200 exact bytes**;
original C# controls pass **1,688,365 cases / 80,115,862 exact bytes**. They cover
all properties through U+110010 plus high invalid values, available class triples
and quotation/EA/mark distinctions, every BMP value in contextual input, original
noncontiguous/saturated/zero source metadata, both profiles, invalid-prefix
mutation, capacities/tails, long contexts and seeded mixed sequences. Native
sources and the original managed resolver/data compile unchanged in temporary
harnesses, removed after execution. No floating-point compiler override is used.

All **19,338 official Unicode 17 LineBreakTest cases** pass independently for
UTF-8 and UTF-16. The runner downloads only the temporary official fixture from
[Unicode](https://www.unicode.org/Public/17.0.0/ucd/auxiliary/LineBreakTest.txt),
requires SHA-256 `e69884e0dde6a8724873f885d68c52dc14518abf9ae4ca9e2283b8773db3b752`,
and deletes it on success/failure. Fourteen WASM checks per DPR cover exact
classes/boundaries/source positions, policy separation, supplementary flags and
emoji, hard breaks, numeric and Hangul context, and strict invalid encoding.
The local original browser gate passes both DPRs, all 204,800 offscreen pixels,
six shaders, resize and actual presented canvas pixels without diagnostics.
Native all-feature tests and strict native/WASM Clippy pass.

The preceding `8e5702a52` hosted run 42530979071 passes Linux/Windows and Vulkan/
winit; macOS is cancelled. Both original browser and separate diagnostic runs
time out at final DPR 2 completion, with all offscreen controls passed. The
diagnostic has 174 submissions, 170 completions and four pending at failure;
submissions 157–167 complete around 22.36 seconds, 168–169 around 26.77 seconds,
and 170 around 28.95 seconds. Neither run reports a device loss. This is a queue
stall observation, separate from earlier loss receipts; no root cause or fix is
claimed. `perf/browser-8e5702-hosted.json` retains compact exact artifact evidence.
No shader, compiler, launch flag, deadline, pixel or original failure gate changes.


The reproducible `eng/progpu-benchmark-rust-line-break.py` baseline uses five
warmups and forty measured rounds of resolution plus identical class/boundary
hashing, with strict decoding and all array allocation outside timing. Release
Rust 1.90.0, Apple Clang 21 default arithmetic and original .NET 10.0.403 JIT
settings are preserved. All eighteen Rust/C++ workloads match checksums; the
nine default-policy managed workloads match too. The native-only EDIT profile
has no fabricated managed equivalent. Compact source/input/binary hashes and all
quantiles are retained in `perf/line-break-baseline.json`.

| Default-policy workload / UTF-16 units | Rust p50 / p95 / p99 µs | C++ p50 / p95 / p99 µs | C# p50 / p95 / p99 µs |
| --- | --- | --- | --- |
| ASCII / 61,440 | 3457.209 / 3590.541 / 3612.375 | 2800.417 / 2948.500 / 3171.625 | 4436.600 / 18826.100 / 19702.900 |
| Mixed / 43,008 | 2351.167 / 2514.541 / 2619.167 | 2084.375 / 2236.333 / 2272.791 | 3166.000 / 12868.200 / 13113.400 |
| Emoji / 43,008 | 1863.291 / 1977.375 / 2012.583 | 1604.542 / 1716.125 / 1723.291 | 2104.400 / 9005.300 / 9168.300 |

These Rust medians are slower than C++; managed runs show broad JIT transitions.
This is a baseline with default process/JIT settings, not steady-state managed
parity, an optimization claim, or complete-stack frame evidence. Original
worst-case context complexity remains. Subsequent optimization requires matched
Instruments investigation, and steady-state managed comparisons need dedicated
warm-state controls. Temporary baseline sources, binaries and managed build
outputs (1,049,759 bytes) are deleted automatically after recording evidence.

The distributable crate passes fresh extracted native all-feature/all-target and
WASM browser checks with an initially empty independent target directory.
Cleanup removes **503,521,654 logical bytes** of package verification
outputs, duplicate benchmark binaries and managed build trees; compact evidence
and active shared dependencies remain. Source/fixture harnesses also clean their
own temporary files, whose unmeasured sizes are not added to that total.


## Original canonical normalization and plan ownership

`text::unicode::normalization` ports the original native normalization worker and
shares the exact `src/ProGPU.Text/UnicodeNormalizationData.bin` resource with
C# and C++. The 450,920-byte plan has 13,253 decomposition records, 36,567 scalar
slots and 12,133 composition pairs; SHA-256 is
`da3f8b751ac289987e400e4ea7daacd66ab912dbf868569fb50ce0fd8d9d7dff`.
The original `eng/ProGPU.UnicodeNormalizationGenerator/Program.cs` derives these
facts from .NET canonical FormD/FormC, and `UnicodeNormalizationPlan.cs` loads
that resource. No foreign normalizer implementation or runtime FFI is added.
The normative reference is [UAX #15 revision 57](https://www.unicode.org/reports/tr15/tr15-57.html).
Compatibility forms are outside this original canonical API.

Borrowed `NormalizationData` validates exact little-endian extents, strictly
ordered decomposition/pair keys, scalar validity and every referenced component.
It preserves original admission of overlapping component spans, zero-length
records and unreferenced scalar slots. An empty present decomposition differs
from absence. `NormalizationPlan` owns immutable Arc bytes and validated offsets;
views borrow that exact owner. The process-wide default is validated once from
the shared embedded bytes and requires no host file access.

The capacity pass checks the complete original scalar inventory and maximum
FormD output, rejecting u32 count overflow before mutation. Safe disjoint borrows
rule out input/output overlap. Output records recompute original CCC/script and
zero reserved bytes while retaining exact source coordinates. Stable canonical
ordering preserves equal-class order and stops at starters. Composition preserves
original blocked-pair state and source union arithmetic: u64 far ends and a u16
length clamp, without first saturating endpoints to u32. Custom plan components
are consumed once, never recursively expanded. After composition, intermediate
records between written count and decomposition capacity stay exactly as the
native compaction leaves them; only the later unused capacity is untouched.
Validation/capacity errors leave the complete output untouched.

`NormalizedText` owns normalized records and the original strict decoded source,
which stays unchanged through cloning and plan disposal. The normalizer does not
apply the managed shaper's per-grapheme normalization decisions, Hangul/Indic/USE
exceptions, default-ignorable handling or glyph mapping. Those original callers
remain necessary follow-on integration, as do source layout/pixel gates.

The first exhaustive managed comparison identified exactly four differing
invocations, all duplicate FormD/FormC controls of U+FFFE: native accepts the
valid scalar; .NET's canonical call rejects it. The original ProGPU generator
already catches ArgumentException from that call. The explicit managed entry
points preserve that admission before output/capacity mutation, while default
native normalization stays unchanged. Other noncharacters remain admitted;
this is not a category-based approximation. The original runtime behavior is
also documented by [Microsoft's canonical call contract](https://learn.microsoft.com/en-us/dotnet/api/system.string.normalize?view=net-10.0)
and its [ICU input validation](https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Globalization/Normalization.Icu.cs.html).
Those references explain the observed admission fact; their normalization engine
code is not ported. Shared-plan repertoire and arbitrary host/runtime versions
remain distinct; explicit managed admission does not promise future/platform
normalization tables or qualify the complete managed shaping call chain.

Default-compiler C++ differential tests pass **3,668,863 controls / 248,877,000
exact bytes**. They cover every scalar/invalid boundary, every original composed
pair in both orders, both canonical forms, invalid forms, capacities, complete
intermediate/tail records, long reordered contexts, source metadata, malformed
custom plans and a proven u32 output-count overflow. Source harnesses compile
the original C++ normalization/property files unchanged and delete temporary
inputs/binaries afterward.

The actual original managed plan loader and the .NET canonical call used by its
generator/shaper pass **2,227,607 controls / 18,078,292 bytes** after explicit
managed admission. The full original plan arrays, every valid Unicode scalar,
combining/Hangul/supplementary triples and invalid/noncharacter contexts are
compared. This is local .NET 10.0.403 canonical-operation evidence; source-range
semantics are independently checked against C++, and full shaping is separate.

All **20,034 official Unicode 17 NormalizationTest cases** pass as **400,680**
checks across all five fixture columns, both UTF encodings, and canonical
composition/decomposition. The runner downloads the
[official fixture](https://www.unicode.org/Public/17.0.0/ucd/NormalizationTest.txt),
requires SHA-256 `5019ffd530751a741900c849c0e010332f142a3612234639bd200b82138a87db`,
and deletes it afterward. It does not claim compatibility normalization.
Twenty browser controls per DPR pass exact normalized/source records, stable
ordering, Hangul, supplementary decomposition, explicit input-policy separation,
custom-plan zero-length/unused-slot semantics and wasm32 extent rejection.
The original local browser gate also passes all 204,800 offscreen pixels, six
shaders, resize and presented canvases at both DPRs. Native tests pass 58 cases
(30 optional source/GPU controls ignored by that aggregate); the normalization
oracles run separately. Strict native/WASM Clippy passes.

The preceding line-break head `3f0897ded` now passes hosted Linux, Windows,
Vulkan/winit and the original browser gate in run 42532056344. macOS remains
queued at this observation. This successful browser run does not establish a
fix for the retained intermittent stalls/losses on earlier heads.


The reproducible normalization baseline uses five warmups and forty measured
rounds per process. All eighteen workloads match output counts and code-point
checksums across Rust, original C++ and the original .NET canonical operation.
Rust/C++ use reused source/16-byte output records and include original source
metadata/property reconstruction; decoding and allocation precede timing. The
managed operation produces a string without those source records, and its
runtime allocation remains inside timing. These different API responsibilities
are explicit; results are not equivalent full-shaping benchmarks.

| Large workload / UTF-16 units | Rust p50 / p95 / p99 µs | C++ p50 / p95 / p99 µs | C# p50 / p95 / p99 µs |
| --- | --- | --- | --- |
| ASCII NFC / 36,864 | 1898.334 / 1960.916 / 2028.083 | 1328.500 / 1356.875 / 1372.084 | 50.700 / 57.200 / 64.400 |
| Composed NFD / 20,480 | 1048.459 / 1093.917 / 1118.583 | 728.500 / 805.083 / 833.458 | 166.900 / 193.800 / 234.500 |
| Composed NFC / 20,480 | 1477.584 / 1503.792 / 1528.500 | 1051.958 / 1064.083 / 1087.375 | 109.000 / 114.500 / 118.100 |
| Reordered NFC / 24,576 | 1476.625 / 1619.542 / 1660.500 | 1116.292 / 1157.625 / 1200.750 | 261.900 / 291.400 / 316.600 |

The receipt records active background compiler work (an unrelated Rust compiler
at about 80% CPU and Roslyn at about 13%) and host load; these are baseline
observations, not isolated performance qualification or an improvement claim.
The unrelated work was not interrupted. Rust is slower than C++ in these measured
controls, and the managed string operation is materially faster. Further work
requires matched Instruments evidence and complete source-record/consumer
measurements. Managed allocated-byte medians are zero for ASCII, 65,656 for the
large composed NFD workload, 49,272 for composed NFC and 36,984 for reordered NFC.
The source/input/binary hashes, all quantiles and exact methodology remain in
`perf/normalization-baseline.json`; temporary benchmark files are removed.

Fresh extracted native all-feature/all-target and WASM browser checks pass with
an initially empty independent target directory. The archived normalization
resource matches the original bytes exactly. Cleanup removes **507,610,844
logical bytes** of package checks and duplicate benchmark builds, retaining
compact receipts and active shared dependencies. Source/fixture harnesses also
delete their temporary data; those unmeasured bytes are not counted here.


## Original common OpenType layout and GDEF boundaries

`font::layout` ports the two unchanged original native files
`Shaping/progpu_native_open_type_layout.cpp` and
`Shaping/progpu_native_open_type_gdef.cpp`. The actual managed
`OpenTypeTextShaper.FindCoverage`, `GetGlyphClass` and `OpenTypeGdefPolicy`
provide independent execution controls. The [OpenType common formats](https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2)
and [GDEF specification](https://learn.microsoft.com/en-us/typography/opentype/spec/gdef)
inform table organization and independently authored fixtures, without importing
implementation code. The earlier cross-engine research still governs retained
CPU shaping data, lazy initialization and separate GPU rasterization.

Coverage format 1 admits sorted duplicate glyph IDs and returns the original
inclusive binary-search midpoint, including its exact duplicate index. Format 2
requires ordered disjoint ranges and contiguous coverage ordinals through 65535.
Class definitions retain every raw class value; only GDEF interpretation maps
values above Component to Unclassified. Whole inventory validation is O(N),
coverage/range queries O(log N), dense class queries O(1), with O(1) extra storage.

Common GSUB/GPOS headers retain versions 1.0/1.1, nonzero top-level list offsets,
complete array extents and optional variation header extent. Individual lookup
records remain lazy. Every declared subtable offset and the optional filtering
set validate before a lookup is published. Unknown types and flags remain raw;
absence of a mark-filtering set preserves the original 0xffff sentinel. Deeper
script/feature records, feature selection, accelerators and execution are pending.

GDEF admits original versions 1.0/1.2/1.3, independent optional class definitions
and every mark-set coverage. AttachList/LigCaretList are outside the original API;
the item store receives its original eight-byte extent check only. Missing mark
sets return false. The exact 40-tuple blocklist stays an explicit independent
policy, never an automatic parsing fallback. Initialization is linear in the
complete class and mark coverage inventories; queries allocate nothing and do not
repeat validation. All offsets are bounded safely on wasm32 as well as native.

Owned wrappers retain either Arc table bytes or the exact selected SfntFace and
its original table span. Views borrow their owner; private fields prevent forged
unvalidated metadata. Clones preserve the original generation with O(1) shared
ownership; no self-referential pointers, unsafe blocks, FFI or runtime reflection
occur in the Rust library. The managed test alone uses reflection to bind actual
private span delegates, since those source helpers have no public testing API.

Local comparisons pass 23,597 native records / 6,415,296 queries / 31,360,632
exact output bytes and 442 managed records / 5,374,312 queries / 21,497,576 bytes.
Native controls include 20,000 deterministic mutations, every prefix of the
fixture inventory, extreme offsets, all 65,536 glyph IDs, optional/undefined data,
transactional lookup rejection and blocklist neighbors. Managed controls compile
the actual original ProGPU.Text project and compare native-admitted inventories:
its private helpers have weaker malformed-data admission, which is not relabeled
as equivalent validation. Temporary outputs use isolated directories and retire
automatically, including failures.

The browser executes 26 new controls at each DPR, including wasm32 overflow,
duplicate indices, malformed later records, raw flags, missing data, exact GDEF
policy and owner-clone retirement. The unchanged original browser gate passes
204,800 offscreen pixels, six canonical shaders, resize and both actual presented
canvases. Local all-feature tests pass 60 with 32 explicit source/GPU controls
ignored by that aggregate; both new differential controls run separately and pass.
Native all-target and WASM strict Clippy pass. Fresh extracted native/WASM package
validation is recorded in the compact receipt; this does not qualify full shaping.

The baseline driver compiles original native methods with default -O2 settings
and actual managed methods with default .NET settings. Eight workloads cover both
coverage/class formats at 256 and 4096 records, each with 65,536 permuted glyph
queries, five warmups and forty measured rounds. Validation, allocation and output
hashing are outside the timer; all three implementations produce identical hashes.
Managed delegate call overhead is inside its timer; no measured query allocation
occurs. This is not a matched full-shaper or steady-state managed JIT measurement.
The host was heavily loaded (89.38/35.94/22.46) with compiler processes present;
these timings are diagnostic evidence only, not optimization qualification.

At 4096 records, Rust/C++/C# p50 microseconds are respectively 3534.584/2716.958/
4099.400 for coverage format 1; 3645.084/3595.583/4222.200 for coverage format 2;
83.083/90.292/780.500 for dense classes; and 3660.375/4270.083/4143.200 for ranged
classes. No optimization is claimed; repeat isolation and Instruments investigation
remain required before drawing performance conclusions. The compact
`perf/layout-baseline.json` records source/binary/input hashes and full percentiles.

The earlier normalization commit's hosted Linux managed oracle fails at case
129941, byte 1283146. The input is U+105C9, native/frozen NFD U+105D2 U+0307
(three UTF-16 units), while that .NET runtime returned two units. Native comparison
passed, and browser/Windows/GPU passed; macOS was cancelled. The exact runtime
repertoire has not yet been isolated. The failure remains mandatory and visible;
no runtime override, exclusion or shader/compiler change hides it.


## Observed runtime normalization repertoire

The unchanged original `eng/ProGPU.UnicodeNormalizationGenerator/Program.cs`
was rebuilt and run on macOS (.NET SDK 10.0.403) and the existing Ubuntu ARM64
VM (SDK 10.0.400, runtime 10.0.11, installed ICU 74.2-1ubuntu3.1), without
selecting another globalization backend or altering runtime defaults. The new
`eng/progpu-audit-rust-normalization-runtime.py` reproduces this read-only audit,
compares complete decomposition/composition inventories, and deletes temporary
builds and generated plans. Compact receipts retain all differing facts and hashes.

macOS reproduces the exact frozen resource (13,253 decomposition records,
36,567 scalar slots, 12,133 composition pairs, SHA-256
`da3f8b751ac289987e400e4ea7daacd66ab912dbf868569fb50ce0fd8d9d7dff`).
Ubuntu generates 450,264 bytes, 13,233 decomposition records, 36,523 scalar slots,
and 12,113 pairs, SHA-256
`3657473d5565b37d68549b431f2acc50c5d0080147403356c3eff8c9428e40d5`.
Its exact 20 absent decomposition records and 20 absent composition pairs account
for all inventory differences; the first is U+105C9. [Unicode 16's Todhri specification](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-8/)
independently documents U+105C9's decomposition to U+105D2 U+0307.

This isolates a local runtime-data difference. It does not prove the exact hosted
x64 runtime inventory, canonical ordering across newer combining characters, or
full managed shaping compatibility. The frozen native plan remains unchanged;
the original hosted managed comparison still fails. A failure-only CI audit now
records its actual runtime inventory without replacing or relaxing any oracle.
Explicit immutable runtime-profile support and full differential qualification
remain work to do. Do not select a profile merely from an OS label or silently
remove the newer records from the original native plan. The VM is restored to its
prior suspended state and both guest/host diagnostic work directories are removed.


## Explicit immutable managed normalization profiles

`ManagedNormalizationProfile` identifies Unicode 15.1, 16.0 or 17.0 source facts;
selection is explicit and immutable, independent of OS names, SDK versions or
runtime callbacks. `NormalizedText::with_managed_profile` retains that identity
alongside its original source and derived records. Caller-buffer capacity and
normalization APIs allocate nothing, while sharing the frozen native resource.
Ordinary native/custom/frozen-managed entry points retain their behavior.

The original ProGPU algorithm is shared through statically dispatched private
source views. Source profile facts govern decomposition, stable canonical ordering
and composition blocking. Published records restore the original ProGPU combining
class inventory, keeping script metadata, reserved bytes, source indices and
clamped range unions. This distinction matters for source-unassigned combining
characters: their ordering class can be zero while ProGPU's recorded class is
nonzero. Composition's original intermediate tail and untouched capacity tail are
preserved; invalid scalar/U+FFFE or capacity failures remain atomic.

The [Unicode normalization versioning contract](https://www.unicode.org/reports/tr15/)
and [.NET globalization documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu)
inform the explicit source boundary. ProGPU's original native algorithm and
managed canonical operation remain the implementations under comparison. No
foreign normalizer, ICU bridge or alternate compiler/runtime is introduced.

`eng/progpu-generate-rust-normalization-profiles.py` verifies SHA-256 for the
UnicodeData files for all three versions. It checks every original decomposition
against recursively expanded factual mappings, verifies the complete original
Unicode 17 class inventory, and reconstructs the exact 450,264-byte older plan
observed from the unchanged managed generator. The old profile's absent keys and
source class inventories are compact static facts; no second full plan is shipped.
Data terms are bundled in UNICODE-LICENSE.txt, and package metadata identifies
both MIT code and Unicode-3.0 facts. The generator's check is mandatory in CI.

The original managed oracle now observes six canonical witnesses to identify one
candidate profile, rejecting unknown/ambiguous results. That observation does not
qualify the profile: every original input remains, plus every scalar embedded
between combining marks and all 12,133 original composition pairs, including
pairs absent from the older source. The unchanged managed plan reader and runtime
String.Normalize execute the original source operation. Full comparison passes
4,476,001 controls / 51,423,694 bytes on macOS SDK 10.0.403 (Unicode17_0), and
4,476,001 / 51,423,570 on Ubuntu ARM64 SDK 10.0.400, runtime 10.0.11, ICU 74.2
(Unicode15_1). The latter used its existing Rust 1.97.1; local macOS uses MSRV
1.90.0, while hosted MSRV/source/platform qualification remains separate.

The original native oracle is unchanged and passes 3,668,863 controls /
248,877,000 bytes on both hosts after sharing the algorithm. The original default
Unicode 17 gate also remains and passes its 400,680 checks. The added official
source-profile fixtures pass 19,074 / 19,965 / 20,034 cases, totaling 1,181,460
UTF-8/UTF-16 canonical checks, with all five columns and both canonical forms.
The three downloaded fixtures are hash-pinned and retired automatically.

The browser passes 36 normalization controls per DPR, including new decomposition
admission, older/newer combining-class ordering, preserved original metadata,
unknown profile rejection and strict source admission. All six canonical shaders,
204,800 offscreen pixels, resize and both actual presented canvases still pass
locally. All-feature tests pass 61 with 32 explicit source/GPU controls ignored
by that aggregate; native/managed controls ran separately. Strict native/WASM
Clippy and fresh extracted package checks remain part of this checkpoint.

The previous hosted commit 3119468ae now supplies independent x64 evidence:
SDK 10.0.403/runtime 10.0.14/ICU 74.2 generates the same older-plan hash and exact
20/20 differences. Its original managed oracle stays failed in that history.
OpenType C++/C# controls pass there. Both original and diagnostic browser gates
fail with DPR 2 device loss; no browser fix is claimed by normalization work.
The new explicit API addresses source-data selection, not full source-shaper
policy or original provider/package/pixel qualification.

The performance driver builds pre-change commit 3119468ae and current Rust in
separate empty temporary target directories, preserving their actual binaries.
It compares both native routes, three explicit profiles, original C++ and actual
managed String.Normalize on 18 common-character workloads, five warmups and forty
measured rounds. Every scalar count and code-point hash matches. Source/output
allocation is outside Rust/C++ timing; managed string allocation remains inside.
Large native medians vary from -3.8% to +1.0% relative to the prior implementation
in this sample; no optimization or statistical equivalence is claimed. Profiles
add roughly 13–30% over the current native medians on these workloads. Rust remains
slower than the original C++ and managed string operation. Full matching shaping
records and steady-state JIT remain separate measurements.

Representative ASCII NFC p50 microseconds (36,864 UTF-16 units) are 1938.208 before,
1957.125 current native, 2186.875/2151.958/2172.584 for profiles 15.1/16/17,
1345.375 C++, and 51.100 C#. Reordered NFC (24,576 units) is 1512.166/1454.125,
1685.000/1659.125/1642.875, 1123.500, and 274.700 respectively. Load was
4.90/17.99/18.32 with the owned Roslyn server at 17.3% CPU, so this is a diagnostic
baseline. Compact hashes, percentiles and allocation evidence are retained in
`perf/normalization-profiles-baseline.json`. No optimization was made; future
performance changes still require Instruments and representative final workloads.

Fresh extracted archives pass native all-feature/all-target and WebAssembly browser
checks from an empty target directory, with exact runtime/profile/resource/license
bytes. Profile work retires 1,060,694,677 measured logical bytes across the fresh
archive, factual fixtures, benchmark builds and Ubuntu guest/host work directories.
An additional package-specific release clean reports 27.8 MiB, excluded from that
exact total. The Ubuntu VM is restored to its initial suspended state; active
dependency caches, current browser evidence and the distributable remain available.
Compact validation and cleanup receipts are retained alongside the baseline.

Hosted follow-up `1ff410d04`, Rust run 42537564226, passes the complete Linux
portable lane with MSRV 1.90.0: the runtime witnesses select Unicode15_1 and all
4,476,001 managed controls / 51,423,570 bytes match. The unchanged native gate,
default conformance and all three official profile inventories also pass. Windows
portable and Linux GPU/winit pass; original and diagnostic browser gates still
fail with final DPR 2 device loss. macOS remains queued at observation. This
qualifies the explicit source profile on that hosted lane; it does not change
historical frozen-plan results, Windows managed admission or browser reliability.

## Original OpenType lookup filter and context preparation

The authoritative sources are
`src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_digest.cpp`, its common
layout implementation and `src/ProGPU.Text/OpenTypeTextShaper.cs`. The Rust port
preserves native's five masks (shifts 2/4/0/6/10) and managed's independent three
masks (4/0/6), including inclusive range wrapping, reversed-range no-op and
short-circuit membership/intersection. `GlyphSetDigest::managed()` retains exact
managed state; native can reject additional false positives, so query results
from the two contracts must not be treated as interchangeable.

The prior cross-engine CPU shaping/GPU realization research still applies. The
[OpenType common formats](https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2)
inform authored coverage/context fixtures, while the [HarfBuzz feature overview](https://harfbuzz.github.io/shaping-opentype-features.html)
reinforces separating feature selection and script policy from lookup data.
Foreign implementation is not imported. The port prepares reusable immutable CPU
data; it introduces no new rendering, GPU fallback, font-runtime or platform bridge.

Digest parsing deliberately retains the original weaker raw coverage admission:
unordered format-1 values and noncontiguous/overlapping format-2 indices still
contribute approximate sets. Reversed ranges disable the native optional digest.
Exact leading coverage and context requirements use the independent strict
coverage validator. The original raw offset-2 digest interpretation is preserved;
successful preparation does not prove a lookup type has a semantic leading
coverage or approve its use by an executor. Format-3 context preparation remains
independent, as does full source shaping qualification.

Each lookup resolves at most one extension, retaining raw type/flag values and
original unsupported cases. Invalid lookup headers remain errors; malformed or
unsupported optional payloads return absence. Context preparation validates every
subtable, coverage and lookup-record extent before either caller buffer changes.
It retains source backtrack/input/lookahead order, raw lookup-record contents,
nonzero input sections, unused output tails and the original capacity error.
No substituted glyphs or positioning records are fabricated at this stage.

`PreparedContext` owns the selected `OwnedLayoutTable`, ordered spans and compact
coverage offsets/digests. The actual selected `SfntFace` generation survives caller
disposal without copying its table bytes. Borrowed exact coverages are reconstructed
in constant time from privately validated immutable offsets, without reparsing.
Caller-buffer preparation allocates nothing. Owned preparation uses O(S+C) storage
for S subtables and C coverage requirements; it reserves both inventories before
publication. Set insertion/query is O(1), raw collection and whole validation are
linear in visited source records, and exact membership retains O(log R) coverage
search. Overlapping source references retain the original repeated visits. These
tiny per-glyph dependent mask reductions are not a whole-buffer compute fallback.
Ordered glyph traversal, GDEF filtering, feature selection and GSUB/GPOS execution
remain unfinished consumers.

The original default-compiler C++ oracle passes **39,859 records / 1,683,424 glyph
queries / 57,233,864 exact bytes**. Controls include 20,000 deterministic mutations,
every fixture prefix, extension types, later malformed coverage, capacity failures,
unchanged tails, full glyph inventories and direct intersection queries. The actual
original managed assembly passes **240 records / 15,728,640 glyph queries /
201,347,072 bytes**, comparing its three masks, membership/intersection and exact
context membership on the complete admitted corpus. Test-only generic delegates
invoke the compiled private implementations; reflection does not enter the crate.
The C# parser's weaker malformed admission remains distinct from native validation.

Additional tests compare all 65,536 singleton masks and boundary-spanning ranges
against explicit sets, prove separate false-positive policies, preserve owners
after clone/drop and exercise complete preflight. All-feature tests pass 63 with
34 explicit source/GPU controls ignored by the aggregate; both new source controls
run separately and pass. Strict native/WASM Clippy passes. Fifteen new controls per
DPR pass in the real browser alongside 204,800 offscreen pixels, six canonical
shaders, resize and both presented canvases. Fresh extracted native all-feature/
all-target and WASM checks pass from empty targets, with exact production bytes.

The baseline measures ten authored sparse/dense list/range inventories, each with
65,536 permuted queries, five warmups and forty rounds. Rust/native and Rust/managed
hashes match within each distinct contract. At 4,096 list records, p50 microseconds
are 90.625 Rust-native / 90.625 C++, and 78.000 Rust-managed / 129.200 C#. For 4,096
range records they are 86.667 / 89.584 and 73.000 / 140.800. Managed typed delegate
overhead remains in each query, and the single cold preparation observation includes
first JIT work; it is not a stable startup comparison. Background load was
5.47/8.94/11.93 with compiler activity. These are diagnostic baselines, not an
optimization, steady-state JIT, full-shaping or cross-platform performance claim.
Future performance changes still require matched Instruments investigation.
`eng/progpu-benchmark-rust-digest.py` and compact `perf/digest-*.json` receipts retain
inputs, sources, binaries, timings, validation and cleanup. Source-oracle directories,
the extracted package, temporary managed/native benchmark builds and unused local
benchmark executables are removed; active dependencies and current browser/package
evidence remain.

## Original native OpenType feature selection

The complete selection implementation comes from the original
`src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_features.cpp`, with
`OpenTypeTextShaper.cs` used as an independent managed execution reference. The
[OpenType common-table specification](https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2)
informs ScriptList/LangSys/FeatureVariations fixtures. The [feature-tag contract](https://learn.microsoft.com/en-us/typography/opentype/spec/featuretags)
and [HarfBuzz feature overview](https://harfbuzz.github.io/shaping-opentype-features.html)
reinforce separating explicit lookup selection from script-specific activation,
glyph eligibility and full execution. Earlier cross-engine research continues to
govern immutable CPU preparation and later GPU realization. No foreign engine
implementation or production reflection is added.

`SelectionRequest` carries raw script/language tags, requested tags and original
signed F2DOT14 coordinates. Native fallback selects the first exact script, then
uppercase DFLT; an exact language falls back to default LangSys. Missing selection
remains empty. Required features survive exclusions. Bulk APIs sort deduplicated
IDs; the separate single-feature API excludes required features and retains source
order. Queries reject an invalid target ID before traversing source tables.

The original capacity contract is intentionally not stronger validation: it checks
selected feature extents and bounds the sum by lookup count, but does not inspect
individual IDs. Excluding features still requires the unexcluded capacity. A later
invalid ID can leave the original unsorted written prefix on failure. The single-
feature API first requires full lookup-count capacity, then preserves its original
partial-write behavior. Unused tails are unchanged. Safe Rust borrows prevent an
input/output alias, while complete owned candidate construction prevents publishing
any partially selected result.

Feature variations retain ordered first-match conditions and signed inclusive
axis bounds. Invalid conditions do not match. A matching null substitution stops
search; an unsupported substitution version continues to the next record, while a
truncated selected substitution disables substitutions. Duplicate feature entries
select the first original record. An invalid alternate pointer skips that feature
entirely; it must not fall back to the original table. An alternate with a readable
header but truncated lookup inventory remains an error. All arithmetic uses checked
offsets, including the wasm32 paths, without clamping coordinates or changing source
conditions.

`LookupSelection` retains the exact `OwnedLayoutTable` and selected font generation,
requested/excluded tag inventories, raw coordinates and final sorted lookup IDs.
Its immutable result survives caller source/option disposal. Independent zero and
negative coordinate selections retain different original variation results; no
process-wide coordinate or policy cache is introduced. Caller-buffer operations
allocate nothing, while owned construction uses O(F+E+A+L) storage for feature,
exclusion, axis and selected-lookup inventories. Original linear tag searches and
duplicate scans retain their quadratic worst cases. Per-feature decisions depend
on ordered source and output state; this layer is CPU shaping preparation, not a
parallel rendering fallback.

The unchanged default-compiler C++ oracle passes **50,956 records / 7,157,396 exact
bytes**, including 25,000 deterministic mutations, every prefix, missing/default/
duplicate scripts, required/excluded features, raw coordinates, malformed variation
records, capacity order and every output tail. The actual compiled C# enabled-lookup
method passes **1,728 shared-policy records / 43,248 bytes**, using actual TtfFont
instances and original WithVariations normalization. Every requested normalized
coordinate is read back through the original font query and compared before lookup
results. Test-only typed delegates call the actual private method; no translated
managed oracle or fabricated font-normalization callback is used.

That managed comparison covers the common admitted policy, not the full current
managed selector. C# separately supports lowercase-default/Latin fallback, different
duplicate history, language-name mapping, global-feature eligibility, feature-value
precedence and malformed-source behavior. Those policies are covered by the later managed-selection milestone below; staged
shaping plans and GSUB/GPOS execution remain work to port. The native API is explicitly documented as
native policy rather than relabeled as current complete managed shaping.

Sixteen browser controls per DPR preserve sorted and source-order results, required
features, capacity/failure prefixes, retained options and 32-bit extreme variation
offsets. They pass alongside 204,800 offscreen pixels, all six canonical shaders,
resize and both actual presented canvases. Strict native/WASM Clippy passes; fresh
extracted native all-target/all-feature and WASM checks pass from empty targets with
exact production/resource/license bytes. All-feature tests pass 65 with 36 explicit
source/GPU controls ignored by the aggregate; source controls run separately.

Eight baseline workloads match lookup counts/hashes for Rust, original C++ and the
actual managed enabled-lookup method. Five warmups and forty rounds retain original
compiler/JIT defaults. Rust/C++ reuse output buffers and perform no timed allocation;
managed execution additionally produces value/ownership metadata, attempts optional
accelerators and allocates. Its measured allocation is reported, but the work is not
equivalent and cannot establish a cross-language speedup. At 512 features, native
selection returns 1,536 IDs in p50 144.458 µs Rust / 83.750 µs C++; the managed method
is 2649.800 µs with 8,750,688 allocated bytes per call. With a selected variation,
1,535 IDs take 145.333 / 96.334 / 2795.100 µs respectively. Rust is slower in the
larger native workloads. Load was 36.80/38.70/25.28 with compiler activity, so these
are diagnostic observations, not stable regression or full-shaping qualification.
Further performance work requires matched Instruments and representative final
shaping workloads. Reproduction: `eng/progpu-benchmark-rust-selection.py`; compact
inputs, binary/source hashes, quantiles and cleanup are in `perf/selection-*.json`.

Hosted follow-up for the preceding lookup-filter commit `40c695980` (Rust run
42538980147) passes Linux's complete source/oracle/package lane, Windows portable
and Linux GPU/winit. The actual 57,233,864-byte native and 201,347,072-byte managed
filter comparisons pass there, followed by all normalization gates. Original and
diagnostic browser gates still report final DPR 2 device loss; macOS is cancelled.
This does not qualify the current selection changes or close browser reliability.


## Current managed feature-selection policy

`ManagedLookupSelection` ports original `GetRawEnabledLookupIndices` selection and
its optional `TryCreateRawLookupDigest` result from `ProGPU.Text/OpenTypeTextShaper.cs`.
It intentionally keeps raw managed admission separate from native `LayoutTable`.
Source ordering, first/last duplicate decisions and partial acceptance differ; a
strict native parser cannot silently substitute for the original managed policy.
The selected actual font generation and complete request are immutable and retained.

Settings preserve four UTF-16 code units, including non-ASCII and unpaired units,
and the original arbitrary signed value. Last-value filtering still visits every
original matching duplicate. Required features are inserted first and protected;
global features retain their independent precedence. Global eligibility includes
random/directional tags and Jamo only under the exact script string `hang`. Font
feature tags preserve original ASCII replacement of high bytes. Script conversion
retains original full code-unit shifts, while language mapping retains invariant
case/underscore normalization and original default-language fallback. An exhaustive
65,536-unit language witness includes Kelvin-sign folding and surrogate inputs.

Script selection preserves last exact/DFLT/dflt/latn matches in original fallback
order. Invalid language headers produce empty allowed sets; truncated feature arrays
produce unrestricted sets while preserving required metadata. Cross-list extents
retain the original empty-set rule. Readable prefixes of feature lookup arrays are
retained, including IDs outside the lookup count. Invalid optional digest data does
not discard a selected ID. Managed reversed coverage ranges contribute no bits;
that is distinct from stricter native digest admission. The source's possible raw
indexing exception is mapped to atomic `InvalidFace`, never a Rust bounds panic.

Managed variation use includes a matching null substitution. First matching record
and original wrong-version/invalid-extent decisions remain intact; last duplicate
alternate wins. Out-of-range relative entries are omitted, preserving any earlier
entry; an inserted unreadable alternate skips that feature. No fallback is invented.
Raw signed F2Dot14 coordinates are frozen separately from source bytes. The actual
C# control constructs original TtfFont/WithVariations instances and checks original
normalized coordinates before comparing outputs. No native normalization stub or
production reflection bridge is introduced.

Construction follows the original O(S*F + R + L log L + D) expected time for S
settings, F feature records, R visited lookup references, L unique lookup IDs and
D visited digest records; script/language/variation inventories add linear scans.
Standard hash maps preserve original keyed policies without per-feature strings.
Owned storage is O(S+A+L) plus original script/language units and shared source bytes,
with O(F+L+V) temporary allowed/position/substitution entries. All publication is
atomic and fallible capacities are checked. This is reusable CPU shaping preparation;
no GPU work, per-pixel CPU fallback or foreign implementation is introduced.

The unchanged actual managed method passes **327,015 records / 27,595,740 exact
bytes**: winning ID/value/tag/required metadata, variation use and all digest masks.
Every prefix, 25,000 deterministic mutations, all mapped language families, script
fallbacks/duplicates, disabled/negative/extreme values and full UTF-16 witness are
retained. Native policy tests remain independent. Twenty browser checks per DPR
pass alongside 204,800 offscreen pixels, six unchanged canonical shaders, resize
and both actual presented canvases.

Eight baseline workloads compare all selected metadata hashes, using five warmups
and forty rounds with original compiler/JIT defaults. At 512 static features,
1,317 IDs measure p50/p95/p99 1889.542/2013.250/2099.875 µs Rust and
2715.100/3237.700/3956.700 µs C#; C# allocates 7,552,320 bytes per call. The varied
case's 1,316 IDs measures 1870.084/1974.916/2024.834 µs and
2482.000/3219.600/3813.200 µs; C# allocates 7,552,512 bytes. Rust retains/copies its
request; C# additionally attempts context preparation. Fixtures have empty lookup
payloads, so both optional accelerators are absent. Hashing/input preparation are
outside timing; Rust allocation totals are not measured. Load was 3.54/13.15/17.89
with background compiler work. These are diagnostic construction baselines, not a
steady-state JIT, stable speedup, full-shaping or frame-performance qualification.
Further optimization requires matched Instruments evidence. Reproduce with
`eng/progpu-benchmark-rust-managed-features.py`; compact receipts retain source,
input and binary hashes without disposable binaries.

Managed context preparation has distinct weak coverage admission and ordered glyph
eligibility; the later context milestone below covers that independent accelerator.
Complete shaping defaults,
GSUB/GPOS execution, fallback, layout and GPU text are not supplied by this milestone.
No original compiler, shader, source/application or package/pixel gate is relaxed.

Hosted follow-up for preceding commit `8e217efee` (run 42540414364) passes full Linux
source/package, Windows portable, Linux GPU/winit and the original browser gate.
The exact original native/shared managed selection oracles pass 50,956/1,728 records
there. macOS is cancelled. Browser passes 16 selection checks and 102,400 offscreen
pixels per DPR with actual final presentation. Earlier original/diagnostic device
loss receipts remain: one successful run does not establish browser reliability.

Strict native all-target/all-feature and WASM Clippy pass. Aggregate tests pass
67 with 37 explicit source/GPU controls ignored; CPU-only passes 65 with 30 ignored.
The current managed source control runs separately and passes. An extracted crate
passes native all-target/all-feature and WASM checks from an independent empty
target, with 86 exact production/resource/license files and original manifest.
Temporary package/benchmark outputs and unused benchmark binaries remove
**601,359,039 measured logical bytes**; source-control auto-cleanup is additional.
Active caches and compact receipts remain.


## Managed context preparation and ordered filtering

The current C# `TryCreateRawContextRequirements`, `RawCoverageMatcher.Contains`,
`RawContextRequirements.MayMatch` and their actual GlyphSubstitutionBuffer traversal
are the implementation sources. Native `PreparedContext` remains separate: managed
preparation admits raw unsorted lists, overlapping/noncontiguous ranges and missing
lookup-record payloads. Only reversed range endpoints are rejected. Empty coverage
sets remain real occurrences. The original extension type interpretation, raw
lookup flags and complete coverage-array admission are preserved. Malformed or
unsupported optional preparation returns absence, without invalidating selection.

`PreparedManagedContext` owns the exact table/font generation and private validated
coverage descriptors. The view keeps original list/range order, ignores the same
range coverage indices, and uses source linear search through four items and source
inclusive binary search above four. It does not replace a raw managed matcher with
native strict coverage parsing or sort input to manufacture a match. Original
backtrack/input/lookahead order remains in public read-only descriptors.

`ManagedLookupSelection` prepares original optional contexts for each selected
lookup. A sparse ordered inventory retains only present contexts, and lookup by
selected position uses binary search. No separate context allocation is made when
none is supported. Source bytes and complete selected request retain the same owner;
no per-lookup byte copy or independently inferred font identity is introduced.

`ManagedContextInput` borrows exactly the source fields used by this accelerator:
glyph ID, original code point, substitution byte, raw script-shaper/feature-mask
bytes and USE syllable. It captures active manual-joiner/restriction/syllable state.
The original all-glyph digest builds once in O(N); an existing conservative source
digest can be supplied explicitly and must cover current glyph IDs. This projection
is not a fabricated complete GlyphRecord, paragraph or font/source generation.

The ordered algorithm first rejects disjoint coverage digests. Flags masked by
0xFF1E then retain the original conservative true result, leaving GDEF filtering to
the exact executor. Other flags walk original positions and backtrack/input/lookahead
requirements. Input preserves ZWNJ as a barrier and optionally ZWJ; context optionally
preserves ZWNJ and skips ZWJ. Original Mongolian/tag controls, substituted ignorables
and explicit Hangul mask visibility remain distinct. A syllable mismatch terminates
traversal. The first input position is read through the original indexer: it returns
0xffff outside the active syllable, including its possible coverage match, rather
than skipping or rejecting the position unconditionally. An early differential
failure exposed this detail; the correction and full original corpus are retained.

Preparation is O(S+C+G), for subtables, coverage occurrences and their source glyph/
range records; storage O(S+C) retains private offsets/digests instead of copied raw
coverage arrays. Exact membership is O(1) for at most four items and O(log R) above.
Query allocation is zero. Ordered matching preserves original worst-case repeated
context walks (quadratic in glyph count for adversarial skip sequences); no new
algorithmic shortcut or CPU raster fallback is introduced. Real executor/GDEF
filtering, staged shaper state and complete glyph mutation remain separate work.

The actual unchanged C# methods pass **56,996 records / 22,558,608 coverage queries /
1,191,586 ordered matches / 95,829,796 exact bytes**. The control constructs actual
original GlyphRecord lists and GlyphSubstitutionBuffer instances, sets original
private traversal state and invokes original methods through typed test delegates.
It does not translate the managed oracle or fabricate eligibility callbacks.
Coverage controls include full ushort query inventories, all original fixture
prefixes, 25,000 mutations, zero/extreme offsets, duplicate/unsorted/overlapping
records, absent record payloads and raw flags. Every code-point value through 0x110010, including invalid values, runs
through original ordered traversal. Visibility/mask/syllable byte inventories,
manual joiners, conservative digests and the original first-position sentinel are
retained. The preceding 327,015-record full feature-policy oracle remains required.

Seventy browser checks per DPR cover GSUB/GPOS and extension routes, exact/mismatched
order, source joiner/visibility/syllable policies, raw flags, first-position sentinel,
four/five-item membership, weak record admission and extreme 32-bit offsets. They
pass alongside all prior controls, 204,800 offscreen pixels, six unchanged canonical
shaders, resize and both actual presented canvases. No deadline, shader, compiler,
driver or original pixel assertion is relaxed.

Eighteen baseline workloads match complete serialized descriptors, digest masks,
coverage queries and match result bytes. Five warmups precede forty measured rounds;
query rounds contain 100 calls, with glyph/digest construction and all reflection
transport/serialization outside timing. Original compiler/JIT defaults remain.
At 4,096 repeated glyphs, ordered-end p50/p95/p99 µs are
38.939/40.303/40.945 Rust and 29.047/91.718/92.963 C#; reversed order is
39.119/39.557/39.808 and 29.791/91.957/93.607. **Rust's large ordered-search median is
slower; performance parity is not established.** A 4,096-ignorable traversal measures
7.825/8.055/8.231 and 23.975/24.842/25.419. Two 4,096-item coverages prepare in p50
4.625 µs Rust and 18.500 µs C#; C# allocates 16,792 bytes for those copied arrays.
All measured C# queries allocate zero bytes; Rust queries contain no allocations.
Rust preparation owns shared original bytes while C# copies raw coverage values.
Load was 3.65/8.45/15.35 with active compilers. These are diagnostic baselines, not
steady-state JIT, stable speedup or final shaping/frame qualification. Optimization
requires matched Instruments and representative final workloads. Reproduction:
`eng/progpu-benchmark-rust-managed-context.py`; `perf/managed-context-*.json` retains
compact source/input/binary hashes and quantiles without disposable binaries.

Hosted preceding `8c588eef2` run 42542282080 passes full Linux source/package,
Windows portable and GPU/winit. Its actual managed selector passes 327,015 records
and 27,595,740 bytes there. Both original and diagnostic browser runs fail final
DPR 2 with `A valid external Instance reference no longer exists`; macOS is cancelled.
The earlier original browser success remains recorded. Intermittent browser loss
is unresolved, and this new context work requires its own hosted qualification.

Strict native all-target/all-feature and WASM Clippy pass. Aggregates pass 69 tests
with 38 explicit source/GPU controls ignored, and CPU-only passes 67 with 31 ignored.
Both current managed source controls run separately and pass. A fresh extracted
crate passes native all-target/all-feature and WASM checks from an independent empty
target, with 87 exact production/resource/license files and original manifest.
Temporary package/benchmark outputs and unused benchmark binaries remove
**606,578,719 measured logical bytes**; source-oracle auto-cleanup is additional.
Active caches and compact receipts remain.


## Native GSUB execution and original mutation state

The Rust executor directly ports `progpu_native_open_type_gsub.cpp` and its private
metadata helpers. `ShapingGlyph` preserves the original 32-byte record: full u32
glyph/code-point/flag fields, signed cluster and four signed position/advance words.
`GsubOptions` exposes explicit caller policy; `GsubState` preserves count, applied
state, optional context boundary and original u32 random state even on failure.
No font discovery, script selection, source normalization or staged defaults are
inferred by this low-level API. Validated borrowed views keep actual table/GDEF
bytes alive, and the owned common table retains its original font generation.

All eight native types execute: delta/array singles, multiple including deletion,
alternates, ordered ligatures, contextual and chained formats 1–3, extensions and
reverse chaining. The original 64-level nested lookup bound remains. Complete raw
lookup arrays preflight; individual substitutions validate their own data/capacity.
Later malformed context records retain prior substitutions and dependency writes,
with the original top-level applied state and unpublished context boundary. A
nested no-match can follow a deletion: the following extension subtable still uses
the original initialized storage slot. A dedicated C++ comparison preserves that
case, including the stale output outside the new live count. Rust bounds-checks
physical storage, rejecting capacity/offset overflow instead of permitting an
out-of-bounds native access. Unused tails are neither cleared nor reconstructed.

Ligatures merge complete adjacent source clusters, including skipped marks, before
removing ordered components. Original fallback component, Arabic stretch and complex
substitution flags retain their exact masks and bounded values. Provenance independently
writes i32::MIN to advance_x. Context dependency flags mark only nonminimum signed
clusters. Context end adjustment follows the original mutated count once at the
top-level application. Random alternates retain wrapping u32 multiplication and
whole-buffer dependency marking at top-level depth only. Native mark-filtering sets
have original precedence over the attachment type; this differs from current C#.

Bulk and lookup-at preserve their distinct digest/admission behavior. Optional
prepared context filters apply only at top-level depth. Exact contextual coverage
uses the original exclusive-high binary search and raw order, independently of the
strict public Coverage parser. Flags, syllable boundaries, input/backtrack/lookahead
order and all malformed-record decisions remain source-defined. No rendering,
shader, compiler or driver changes accompany execution.

The original default-compiler C++ passes **117,973 records / 60,322,260 exact result
and full-capacity storage bytes**. Cases include all formats/extensions, GDEF and
Unicode mark fallback, all accelerator combinations, capacity/count extremes,
every selected fixture prefix, 25,000 byte mutations, 10,000 mixed record inputs,
recursion, positive/negative clusters, raw flag/syllable state, random wrap,
300-component metadata clamping and nested failure/no-match continuation.
Five focused ordinary tests independently assert mutation/error contracts.

The actual C# `ApplyNestedLookup`, reverse subtable executor and original
GlyphSubstitutionBuffer pass **3,968 shared-policy records / 975,232 exact bytes**.
Comparison includes live IDs, code points, signed clusters, public dependency flags,
all advances/offsets and substituted/ligated/multiplied/component metadata. The test
uses actual compiled original code through typed reflection delegates. It does not
translate the managed executor. Current managed default-ignorable traversal,
malformed-source recovery, one-component ligature marking, already-ligated multiple
expansion and full staged shaping remain separate contracts to port; this milestone
makes no full current-managed GSUB claim.

Native and Rust baselines cover eight operations at 32/512/4096 glyphs, with five
warmups and forty measured rounds. Parsing, buffer allocation/reset, transport and
complete-output serialization are outside timing. Both executors allocate nothing
during lookup execution. Original repeated buffer shifts/context searches and random
whole-run marking retain their worst-case quadratic behavior. Exact serialized
state and full storage match in all 24 workloads. Timings are diagnostic, with
mixed regressions and background compiler load recorded; this is not optimization,
managed steady-state, whole-shaper or frame-performance qualification. Any future
optimization requires matched Instruments evidence. Reproduction and compact
source/input/binary hashes: `eng/progpu-benchmark-rust-gsub.py`, `perf/gsub-*.json`.

The preceding context commit `99b2f9093` passes hosted Linux full source/package,
Windows, GPU/winit and original Chromium WebGPU run 42544464172. Its browser checks
both presented DPRs, 204,800 offscreen pixels and 70 managed context controls per DPR.
The diagnostic rerun is skipped. Earlier device loss remains recorded and unresolved;
a successful checkpoint does not establish browser reliability. New GSUB work has
its own browser and hosted gates.

Thirty-seven browser controls per DPR pass all GSUB types/context formats,
extensions, GDEF precedence, overflow, recursive/no-match continuation, exact
metadata and capacity failures. All existing 204,800 pixels, six shaders, resize
and both presented canvases pass. Strict native/WASM Clippy passes; aggregates
pass 74 all-feature tests (40 explicitly ignored) and 72 CPU-only (33 ignored).
Seven focused/source tests run separately and pass. Fresh independent extracted
native all-target/all-feature and WASM checks pass, comparing 90 exact production,
resource and license files plus the original manifest.

| 4,096-glyph workload | Rust p50 / p95 / p99 µs | C++ p50 / p95 / p99 µs |
| --- | --- | --- |
| single | 68.625 / 75.291 / 76.166 | 62.166 / 62.833 / 68.584 |
| multiple | 817.167 / 889.375 / 896.083 | 800.667 / 850.958 / 856.709 |
| delete | 792.875 / 846.541 / 875.250 | 936.459 / 977.208 / 984.208 |
| ligature | 1276.917 / 1318.875 / 1334.750 | 1119.125 / 1153.292 / 1168.667 |
| context | 89.167 / 98.667 / 100.750 | 86.542 / 97.166 / 101.583 |
| chain | 116.250 / 125.125 / 130.625 | 117.000 / 122.208 / 122.750 |
| reverse | 93.042 / 99.125 / 102.500 | 94.708 / 99.917 / 101.792 |
| random | 1991.125 / 2115.417 / 2161.167 | 2889.458 / 3054.333 / 3106.750 |

Baseline load was [58.1796875, 50.83642578125, 33.0771484375]; compiler processes are recorded in the
receipt. Single, multiple, ligature and context medians are slower than C++ in
this run. No stable speedup or performance parity is claimed. Cleanup removed
**539,368,324 measured logical bytes** of package/benchmark
scratch, unused executables and the temporary helper; source-oracle scratch
removal is additional. Active dependency caches and compact evidence remain.


## Current managed GSUB execution and complete records

This milestone ports the original C# `ApplyNestedLookup` and separate
`ApplyReverseChainingSubtable`, with their original buffer operations. It does
not route through the native executor: their malformed reads, first-alternate
behavior, default-ignorable filtering and mutation metadata differ.
`ManagedShapingGlyph` retains every original field rather than projecting into
native packed flags. The buffer owns the selected font generation and raw GDEF
admission, including the original table-length blocklist. A table can own raw
bytes or retain its actual selected GSUB generation.

The raw current managed parser keeps inclusive binary search and selected-record
reads, including original unsorted/range behavior, partial arrays and typed
source failures. Context formats 1–3 preserve ordered matching/preflight,
sequence indices beyond the original input count, no-op dependency writes and
successful earlier records when a later target is unavailable. Reverse
subtables preserve backtrack/input/lookahead dependency and later read ordering.
The original mark filtering set and attachment class both apply. Depth and mark
coverage restore after source errors; prior public record/state writes remain.
One-component ligatures still set Ligated and clear Multiplied. Already-ligated
multiple substitutions insert later outputs after the actual skipped marks.
Ordinary expansion retains the original reverse insertion order. Adjacent
clusters merge using original signed values; all other private metadata survives.

The actual C# class enum uses byte storage. Raw GDEF classes narrow before the
zero fallback test, so 256 falls back while 259 becomes Mark. Rune fallback
rejects invalid scalars, while the native frozen classifier treats them as
unassigned. The no-filter fast path still avoids classification. The new
`ManagedMarkProfile` has explicit Unicode 15.1/16/17 facts, generated from
hash-pinned UnicodeData inventories. The complete frozen category mark inventory
matches Unicode 16. This runtime's 1,114,129 original classifications match 16,
independently of its witnessed Unicode 17 normalization behavior. CI probes its
actual original runtime and requires a complete unique profile match.

The unchanged current source passes **74,981 cases / 80,999,436 exact bytes** of
return values, live count, context/random/mark/depth state, every live class and
all 31 record fields. The corpus includes all nested types, separate reverse
subtables, all context formats, extensions, truncations, 25,000 byte mutations,
12,000 mixed-record cases, all GDEF prefixes, blocklisted lengths and raw classes.
Original private methods execute through typed delegates; reflection is only
field transport and setup. No foreign shaping implementation supplies results.

Two additional mutations create branching recursion. The original source limits
depth to 64 but does not limit total calls. Both actual C# controls remain in the
original operation after a three-second observation, and their owned processes
are terminated and reaped. These are **not counted as exact-output comparisons**.
Rust adds a caller-selectable nested-call budget, default 65,536, returning
Capacity on exhaustion with earlier writes retained and private state restored.
The ordinary single-branch 64-depth no-op is independently tested. Extensions
have a separate 64-hop safety bound; illegal self-extensions reject rather than
recursing until stack failure. Neither bound modifies the original C# producer.
These are scoped safety differences, not full adversarial-workload qualification.

**44 browser controls per DPR** cover full field retention, already-ligated
expansion/deletion, first alternates, all context formats/extensions, joiners,
reverse substitutions, missing later targets, mark-set-plus-attachment policy,
byte GDEF narrowing, invalid scalars, explicit mark profiles and work-budget
restoration. Existing 37 native GSUB controls, all six canonical shaders,
204,800 offscreen pixels, actual DPR 1/2 presentation and resize still pass.

The current execution baseline has 24 workloads, 32/512/4,096 records and eight
operations. Original glyph-10 positions are visited in descending order so both
executors receive identical operations despite expansion/deletion. Full records,
classes and final state match for each workload. Buffer allocation/reset and
source parsing are untimed; actual mutation allocations remain timed. C# typed
delegate overhead is inside its timer and its default JIT behavior is unchanged.
These are diagnostic measurements, not optimization or complete shaper/frame
qualification. Top-level required/feature-value/random-alternate dispatch,
staged script policy, GPOS, fallback, full layout, hinting and GPU text remain.

The preceding `20aac151c` checkpoint passed hosted Linux source/package,
Windows portable, Vulkan/winit and the original browser gate (run 42546692435).
macOS remained queued at observation. Earlier intermittent hosted browser device
loss remains unresolved; these managed changes require their own hosted result.

The 4,096-record baselines below use microseconds (median / p95 / p99);
load average at capture was 6.43 / 7.71 / 14.11.

| Operation | Rust | Original C# |
| --- | --- | --- |
| single | 13.250 / 15.375 / 25.833 | 83.792 / 106.959 / 110.042 |
| multiple | 11,038.084 / 11,651.625 / 11,989.292 | 11,031.166 / 11,295.042 / 11,395.625 |
| delete | 1,176.917 / 1,321.584 / 1,406.250 | 1,371.458 / 1,738.000 / 2,010.708 |
| ligature | 1,233.416 / 1,358.334 / 1,411.333 | 1,494.959 / 1,584.834 / 1,598.875 |
| context | 87.750 / 102.041 / 123.542 | 345.042 / 385.458 / 401.833 |
| chain | 132.917 / 154.625 / 200.250 | 413.041 / 634.250 / 771.750 |
| reverse | 94.375 / 94.750 / 127.375 | 243.959 / 269.625 / 318.458 |
| alternate | 11.083 / 11.125 / 11.125 | 64.625 / 74.250 / 74.458 |

Large multiple expansion remains roughly tied and its Rust p95/p99 are higher.
Original C# measured 32,768 allocated bytes for this workload and 24,576 for
deletion; other listed operations measured zero. Rust allocation counts are not
instrumented here: its production multiple replacement allocates the temporary
`Vec<u16>` for nonempty sequences. No allocation-free expansion claim is made.


## Current managed feature ranges and complete raw stage dispatch

`ManagedFeatureOptions` owns the source feature inventory, optional baseline,
explicit set and ranged requests. It preserves default-list identity separately
from a custom list with null explicit tags. The source's ResolveExplicitFeatureTags
and IsFeatureExplicitAt are intentionally different operations. Baseline selection
uses the last matching setting; if absent, any range for that tag suppresses
fallback to Features, even outside that range. Negative clusters map to zero;
ordered matching half-open ranges overwrite earlier values and clamp unsigned
values only at the original i32 boundary. Ordinary admission rejects zero, while
alternate dispatch rejects nonpositive values. Four UTF-16 units retain ordinal
identity; ranged tag bytes decode independently to Latin-1 units.

Per-record admission retains separate early Hangul and Indic branches before
fraction, Khmer, USE and Arabic-action policy. Explicitness does not bypass
original script masks. The independent _hasFractionActions marker remains buffer
state rather than being inferred from record bytes. WithFeatures preserves the
original default order, last override value and explicit override tag set.

All 42 original UseSubstitutionStage identities are ported. Feature membership,
manual joiners and syllable restriction remain independent queries: a tag can
have a syllable policy even when it does not belong to a particular stage.
The complete original raw ApplySubstitutions route now runs through
`ManagedSubstitutionPlan`: actual immutable selection, owned options and shared
GSUB/font generation, with no copied table or foreign engine bridge. The source
production plan already selects this raw route. Script preparation and the
ordered sequence of script stages are still separate work.

Stage execution preserves source order: stage/MayEnable filters, optional lookup
digest and context filter, then manual-joiner selection. Reverse classification
and alternate classification inspect the first extension only. Reverse visits
are descending and restore mark/syllable state only on success, following the
original absence of a top-level finally. Nested calls keep their independent
finally restoration. Forward dispatch selects each original syllable, checks
per-position digest and feature policy, resets the context boundary, skips only
positive inserted counts, then honors the returned context continuation. Deletion
retains its original forward index progression. The buffer digest accumulates
new IDs without removing old ones after each selected lookup. Required features
bypass optional per-glyph feature/mask checks.

Alternate traversal keeps subtable order outside glyph order, signed values,
per-cluster ranges and independent required values. Random mode is selected by
original rand/65535 metadata. It marks the whole dependency before checking
subtables and advances the wrapping PRNG before checking the selected output's
bounds. Failure to read that output does not rewind random state. Alternate
lookup flags do not acquire ordinary glyph filtering. Neither top-level extension
route recursively unwraps extension chains.

The sealed `ManagedContextRecord` trait permits the exact same generic matcher
to borrow compact projections or complete ManagedShapingGlyph records, with no
intermediate vector or virtual dispatch. The actual original context regression
still passes **56,996 records / 22,558,608 coverage queries / 1,191,586 ordered
matches / 95,829,796 bytes**. The separate original managed nested executor still
passes **74,981 cases / 80,999,436 bytes**, plus its complete Rune inventory and
two separately reported work-bound witnesses.

The new actual C# oracle builds the original plan and invokes the original raw
stage method. It passes **262,144 feature-policy queries, 1,974 stage-policy
queries and 26,753 complete stage executions / 46,818,828 bytes**. All live record
fields and actual context/random/mark/depth/manual/syllable/fraction state are
compared, including error returns. Inventories cover all stage identities,
baseline/explicit/range variants, all lookup forms, overlapping feature-selected
lookup sequences, default-ignorable and script metadata, required bypass,
12,000 payload mutations and a 131,073-glyph run. No work-bound cases were excluded
from this dispatch corpus. The extra Rust budget remains per root invocation,
so ordinary large buffers are independent of the recursion-work bound.

**145 additional browser checks per DPR** exercise all stage memberships,
independent syllable policies, full stage results, original UTF-16 tag identity,
ranged/explicit precedence, signed ordinary/alternate values, newly inserted
output skipping, forward deletion, random state, accumulated buffer digests,
Hangul and fraction policy, required masks and retained reverse failure state.
The previous 44 managed and 37 native GSUB controls, all six canonical shaders,
204,800 offscreen pixels and actual DPR 1/2 presentation/resize still pass.

The full-stage diagnostic baseline compares 24 workloads (32, 512 and 4,096
records; single, multiple, delete, ligature, context, chain, reverse and random
alternate) against original cached C# plans. Five warmups precede 40 rounds.
Plan/font construction, buffer reset/preallocation and serialization are untimed;
actual production allocations and typed delegate overhead remain timed. Every
record/state output matches. Capture load average was **128.46 / 47.94 / 27.65**:
host contention makes these diagnostic observations unsuitable for comparative
performance qualification. No optimization or full shaping/frame claim follows.

| 4,096-record operation | Rust median / p95 / p99 (µs) | Original C# median / p95 / p99 (µs) |
| --- | --- | --- |
| single | 83.583 / 88.333 / 100.708 | 750.541 / 808.500 / 885.750 |
| multiple | 6,886.000 / 6,966.583 / 6,986.667 | 7,075.167 / 7,548.083 / 7,618.375 |
| delete | 1,745.459 / 1,785.084 / 1,811.709 | 2,098.583 / 2,194.208 / 2,228.417 |
| ligature | 1,911.708 / 2,045.500 / 2,100.125 | 2,246.167 / 2,280.084 / 2,488.125 |
| context | 29.958 / 30.334 / 30.375 | 237.125 / 269.666 / 282.917 |
| chain | 272.666 / 282.792 / 326.042 | 986.209 / 1,072.875 / 1,109.542 |
| reverse | 161.333 / 198.167 / 204.208 | 644.875 / 687.417 / 730.250 |
| alternate | 54.250 / 54.334 / 54.458 | 627.417 / 661.875 / 688.625 |

Actual C# measured allocation is 32,768 bytes for the large multiple workload and
24,576 for deletion; the other listed operations measure zero. Rust allocation
counts remain uninstrumented; nonempty multiple substitutions still allocate
replacement vectors. Full script preparation/reordering, GPOS, hinting, fallback,
paragraph layout, GPU text and the other rendering subsystems remain in scope.

The preceding 3224d543e commit passed original hosted Linux source/package,
Windows portable, Vulkan/winit and browser (run 42549809545); macOS was cancelled.
The hosted original Rune probe also matches Unicode 16 independently of Linux's
Unicode 15.1 normalization repertoire. Earlier browser device losses/stalls and
all default-compiler/font/provider/pixel gaps remain open. This new dispatch
checkpoint still requires its own hosted qualification.


## Current managed raw GPOS and attachment execution (2026-11-09)

`ManagedGposTable` owns exact raw bytes or an actual selected `SfntFace` GPOS
generation. `ManagedPositionBuffer` owns already-prepared full managed records,
its original font/GDEF generation, independently selected Rune mark repertoire
and original direction (including Unspecified). It intentionally does not infer
metric initialization, glyph removal, script preparation or source placement.
The earlier cross-engine CPU shaping/GPU realization research remains applicable:
these are reusable CPU positioning records; no third-party engine implementation
or foreign shaping runtime is incorporated.

All nine original raw lookup types are implemented: single formats 1/2, pair
formats 1/2, cursive, mark-to-base/ligature/mark, contextual and chained contextual
formats 1/2/3, and the original one-level extension route. Shared original weak
coverage/class reads remain separate from strict native table admission. The
complete 31-field records, exact ordered filtering, original byte GDEF class cast,
non-ignorable Rune fallback, mark set and attachment class interaction, zero
value formats, ignored reserved bits, ordered subtable/record dispatch and partial
mutation/error state are preserved. Context success marks its entire original
dependency range; an empty positioning-record list succeeds. Invalid positions
retain the original no-match result. Every exit restores private mark coverage.

GPOS has no original GSUB-style 64-level recursion rule. The Rust executor uses
an explicit contextual call stack and matches actual C# execution through 257
nested lookups. A leaf keeps its frame on the stack without a heap allocation.
An independent default budget of 65,536 lookup attempts per root rejects cyclic
or excessive work with `Capacity`, preserving earlier record/dependency writes;
callers can select a different explicit budget. It is a Rust untrusted-input
boundary, not a claim that the original C# producer has the same limit. The
source corpus contains no budget-excluded comparisons. Context work retains the
original ordered scans, with O(depth) explicit call storage.

Attachments preserve the separate run direction and lookup RTL flag, original
major/minor-axis adjustment order, ligature component identity and every signed
short saturation. Cursive reversal clears each original edge before descent and
reverses saved offsets in unwind order. Resolution retains the original three-state
cycle rule, source iteration order and each individual advance addition; it does
not combine saturated sums. Explicit paths avoid recursive stack overflow and
reuse owned scratch; allocation failure precedes attachment mutation. The original
range scans can be quadratic for mark graphs; no complexity or arithmetic shortcut
is substituted. A 100,000-record Rust chain and original cyclic-state controls pass.
Late mark advance clearing preserves its separate optional offset adjustment.

Validation invokes the actual unchanged C# `ApplyRawPositionLookupAt`,
`ResolveAttachmentOffsets`, `ZeroMarkAdvancesLate` and classification methods via
typed delegates. Reflection performs setup and transport only. The real empty
position-buffer constructor establishes original GDEF policy; then the supplied
already-prepared records are installed without executing metric initialization.
**63,557 cases / 72,241,212 full record/state bytes** match, including 30,000
mutated/truncated leaf tables, 5,000 cursive topologies, 10,000 independent
attachment/late-zero cases, every contextual format and all direction values.
The fixed browser inventory contains **80 original C# outputs**, serialized from
the actual oracle and rechecked byte-for-byte on every source-oracle run. Chromium
WASM additionally checks bounded cyclic execution and private-state restoration:
**81 checks per DPR**, alongside all existing shader, text, GSUB, resize and actual
presentation controls. This is CPU GPOS in the browser; GPU text remains pending.

`eng/progpu-benchmark-rust-managed-gpos.py` captures 30 complete-output matched
workloads at 32, 512 and 4,096 records. Five warmups precede 40 rounds. Raw lookups
execute at every position, followed by attachment resolution where applicable;
separate resolve and late-zero workloads execute their original buffer methods.
Font/table construction, buffer resets and serialization are untimed. Production
allocation and C# typed delegate overhead remain timed; reusable scratch is warm.
This is a diagnostic initial baseline, not an optimization or complete shaping/
frame qualification. Rust allocation counts are not instrumented; contextual
call stacks allocate, while cursive/resolution scratch is retained by the buffer.

| 4,096-record workload | Rust median / p95 / p99 (µs) | Original C# median / p95 / p99 (µs) |
| --- | --- | --- |
| single | 88.708 / 158.333 / 235.875 | 558.334 / 685.125 / 810.167 |
| pair | 149.084 / 181.417 / 211.625 | 759.292 / 844.667 / 981.166 |
| cursive | 202.083 / 268.125 / 273.125 | 847.375 / 983.208 / 1,118.291 |
| base | 112.000 / 135.334 / 160.416 | 541.083 / 617.750 / 669.500 |
| ligature | 119.333 / 311.334 / 320.875 | 626.334 / 772.917 / 1,350.458 |
| mark | 107.583 / 259.042 / 278.792 | 498.625 / 755.417 / 951.709 |
| context | 228.667 / 567.542 / 590.125 | 895.250 / 969.875 / 975.459 |
| chain | 276.667 / 285.000 / 306.709 | 912.333 / 962.708 / 1,007.250 |
| resolve | 46.042 / 48.459 / 93.334 | 135.916 / 619.333 / 1,547.875 |
| zero | 38.291 / 38.667 / 38.750 | 87.625 / 259.625 / 1,722.500 |

Capture load average: 4.10 / 5.25 / 11.85. All measured C# steady-state
workloads report zero allocated bytes. Compact source/binary hashes and all
quantiles are retained in `perf/managed-gpos-baseline.json`.

Feature selection/whole-GPOS dispatch, layout variation adjustments, actual
metric/default-ignorable initialization, fallback/legacy kerning, native device
GPOS and hinted point anchors remain separate implementation work. Full script
preparation/reordering, layout, GPU text and remaining rendering subsystems remain
in scope. Original Windows/application/provider/package/pixel gates, all recorded
default-compiler arithmetic gaps and intermittent hosted browser loss remain open.


## Current managed positioning plans and layout variations (2026-11-09)

`ManagedPositioningPlan` composes the original owned feature selection, exact
raw GPOS generation, immutable feature policy and selected normalized instance.
It builds one run digest, retains selected lookup order, applies the per-record
digest and required/ranged feature rules, then executes each lookup's original
variation pass. Return value remains selected `kern`/`dist` presence, even for a
no-match run. Attachment resolution, metric initialization, fallback and paragraph
construction remain explicit independent operations. The per-root recursive-work
bound does not limit ordinary run length: 131,073 original records are compared.

Variation dispatch retains current C# distinctions rather than substituting a
uniform OpenType interpretation. Any first-level pair extension selects the whole
lookup's pair-variation route. Pair variations use ordered linear pair search and
PairSet-relative device offsets; ordinary raw pair positioning keeps its original
binary search. Single and mark variation passes preserve their separate feature,
ignored-glyph and component rules. Mark-to-base skips original mark classes;
mark-to-mark uses the immediate previous record. Cursive, ligature and nested
context variation behavior is unchanged. Reserved value bits remain independent;
advance-Y device values are read for source failures but their result is unused.
`AppendVariation` retains float addition, ties-to-even rounding, short saturation
and the positioned marker even for zero adjustment. Errors preserve prior writes
and restore selected private mark coverage.

`ManagedLayoutVariations` owns the full selected immutable SFNT and exact F2DOT14
coordinate inventory. The current managed GDEF ItemVariationStore reads against
the complete font, including original bytes beyond the declared GDEF extent. Its
metadata parser validates headers, regions and region-index inventories eagerly;
delta rows remain lazy. Missing/unsupported stores, absent subtables and past-end
items return zero as in the source. A delta is read before testing its region
index, including unknown regions. Word/long-word storage, signed row-offset
arithmetic, ordered region products, early zero and ordered floating sums remain
unchanged. Checked inventory multiplication rejects oversized untrusted inputs
without a wasm32 integer panic; this safety bound does not claim that the original
producer has identical overflow behavior. Default compiler arithmetic stays intact.

This API accepts an already-normalized source instance. The actual original C#
`WithVariations` is executed in the oracle, then its real normalized coordinates
and active flag become the explicit Rust input boundary. Invalid source metadata
may cause the original instance creation to fall back to its root; those captures
retain the resulting inactive state. They do not prove that Rust has implemented
C# `WithVariations`, its full fvar/avar/gvar/HVAR/MVAR admission or source fallback.
The strict native variation APIs and their recorded default-FP gaps remain separate.

Validation invokes unchanged `CreatePositioningPlan`, `ApplyPositions`,
`GetLayoutVariationDelta` and optional attachment resolution. Reflection transports
all 31 fields and real constructor-owned GDEF only; typed delegates execute source
methods. **32,008 cases / 760,086 exact float-bit delta queries / 44,353,296 complete
output bytes** pass. Controls include all raw types and selected orders, six
feature policies, all directions, 12,000 mutated/truncated leaf payloads, 4,000
store mutations, 10,000 random normalized captures, 2,000 two/three-axis captures,
mixed extensions, unsorted/duplicate pairs, source whole-font reads and lazy failures.
The 151-entry browser fixture is emitted from actual C# and revalidated byte-for-
byte by the required CI source oracle. WASM retains cloned plans/source owners
through execution and compares every output field. This is browser CPU positioning;
GPU text rendering, native GPOS and complete shaping remain unfinished.

The matched 30-workload baseline executes cached plans at 32/512/4,096 glyphs,
including static/active single, long-word deltas, both pair forms, cursive, mark,
context and chain cases. Every complete output must match actual C#. Five warmups
precede 40 rounds; source instance/plan creation, buffer reset, queries and output
serialization are untimed. Production allocations and typed-delegate overhead
remain measured; Rust allocation counts are not instrumented. High host contention
and JIT/tail behavior remain in the recorded diagnostics. No optimization or stable
speedup is claimed; future optimization still requires Instruments evidence.

The previous `deaf86ae6` hosted Linux source/package, Windows portable and Vulkan/
winit jobs pass, with all 63,557 original raw GPOS cases. macOS was cancelled.
Both the original browser gate and diagnostic retry lose the device during final
DPR 2 capture after GPU-process exit 512. Earlier passing checkpoints do not close
this intermittent failure. Original flags, drivers, deadlines, source/application,
provider/package and pixel requirements remain mandatory.


## Current managed process-neutral variation data (2026-11-09)

`ManagedVariationData` ports the original `OpenTypeVariationData` component instead
of relabeling strict native variation parsing. Admission visits fvar, avar, gvar,
HVAR, GDEF and MVAR in original order. It retains complete selected font ownership,
source ASCII tag replacement, original axis float/fixed bits, hidden flags and the
explicit name-resolver callback dependency. Missing/invalid source data yields no
variation generation, as in `TryCreate`; allocation/capacity failure is independent.
Axis ordering remains deferred to instance creation. gvar admission uses the
original whole-font maxp count read (missing defaults zero), independently of the
native declared-table metric accessor. Other full TtfFont constructor gates remain
separate; metadata admission does not qualify source font construction.

Instances freeze last matching UTF-16 settings, floating clamp/nonfinite behavior,
original 16.16 conversion, wrapping fixed differences, double normalization and
away-from-zero lattice rounding. avar retains its ordered search, direct endpoint
values, original float interpolation and ties-to-even interior rounding. No sort,
endpoint repair or table-version inference is introduced. Settings retain original
clamped/requantized user coordinates separately from normalized key bits. Cached
HVAR region scalars use the original maximum inventory length across all stores;
MVAR and GDEF retain independent scalar evaluation and original zero tails.

The original managed lazy item-store codec is shared with the existing layout
state. HVAR offset/map reads retain whole-font bounds, signed offsets, unsupported
map behavior and final-entry reuse. MVAR keeps its own store, record stride, ASCII
tag semantics and last matching record. Reads of selected delta rows remain lazy;
each source delta precedes region selection. Normalized coordinates are immutable,
not aliases into caller setting buffers. `from_variation_instance` connects these
instances directly to managed GPOS. The 32,008 positioning controls now compute
Rust coordinates and compare them to actual C# captures before plan execution.
Default source fonts expose the complete zero coordinate inventory, not a missing
inventory; active layout adjustment remains a separate predicate.

Gvar retains shared/embedded peaks, intermediate regions, shared/private packed
points, original run overflow checks and the independent tuple payload boundary.
The first query sets the original parsed flag before parsing and fixes item count.
A failed first parse propagates once; later queries observe cached empty state.
An owned mutex preserves this order across cloned instances. Parsed immutable
payloads are shared without changing their source generation. Simple deltas retain
ordered tuple mutation; IUP reads coordinates already modified by earlier tuples.
Duplicate selected points accumulate, phantom slots remain independent, absent
variation returns empty component output, and HVAR keeps phantom-advance precedence.
Caller-invalid contour reads fail at their original operation with earlier tuple
writes retained. Explicit Rust size bounds reject unrepresentable caller counts.

The direct source harness executes unchanged `TryCreate`, `CreateInstance`,
advance/metric/layout queries, simple/component deltas and phantom deltas through
typed delegates. The original name callback dependency is frozen separately and
includes null fallback, NUL and unpaired UTF-16 values; this does not claim source
name-table decoding. **8,772 cases / 8,192,752 exact bytes** cover 6,000 mutated or
truncated metadata inputs, 2,000 arbitrary coordinate/contour controls, extreme
fixed endpoints, nonfinite settings, ordered cache queries, complete axis/name/
setting/key/scalar state, partial mutation and errors. Every original scalar and
coordinate bit is compared. The **96-case / 474,611-byte browser fixture** is emitted
from actual C# and revalidated by mandatory CI; both DPRs retain all existing
positioning, shader, offscreen-pixel, resize and actual presentation gates.

The matched diagnostic baseline contains 48 complete-output workloads: admission
and instance creation for one/two/three axes across eight table configurations.
Five warmups precede 40 rounds. SFNT owner preparation, callback construction,
input transport and output serialization are untimed; production allocations,
Rust replacement drops and C# delegate/GC behavior stay measured. Rust allocation
counts are not instrumented. Short-call clock overhead and host/JIT tails remain;
no optimization or full shaping/frame speedup is claimed. Query throughput still
needs separate measurements, and any optimization requires Instruments evidence.

Remaining work includes the actual TtfFont root/FIFO cache and identity wrapper,
variation metadata application, original source name decoding, complete font
metrics/outline preparation, hinting, script preparation and paragraph/GPU text.
The earlier native default-compiler FP differences are not resolved by this
independent managed comparison. The preceding ddccf5614 hosted Linux source/
package, Windows, Vulkan/winit and original browser gates pass; macOS was cancelled.
That passing browser run does not close the recorded intermittent device loss.
All original platform/provider/package/source/application/pixel gates remain open
where their complete scope has not been demonstrated.


## Current managed name-table policy (2026-11-09)

`SfntFace::managed_names` and `managed_name` port original `SfntFontFace.GetNames`
and `TryGetName` over the selected immutable source face. Ordinary last-valid
SFNT duplicate selection agrees with original managed ParseFace. The complete name
header/record inventory and string-storage origin validate before any name query;
individual wrong-ID or out-of-bounds payloads are skipped. Source format fields do
not become new admission gates, and valid string offsets may point into records.

UTF-16BE follows source replacement decoding, including isolated surrogates and
an odd final byte. Windows and Unicode platforms use that route independently of
encoding ID; Macintosh encoding zero deliberately retains original Latin-1 rather
than a fabricated MacRoman mapping. Other platforms/encodings use UTF-8 replacement
decoding. NUL removal precedes original whitespace trimming. Stable score ordering
precedes ordinal duplicate removal. The original +10 score examines UTF-16 Char
letters above U+024F, not decoded Runes; supplementary letters keep the bonus.
Existing frozen Unicode 16 category data is used with an exhaustive source BMP
witness; native Unicode categories and normalization/mark profiles are unchanged.
Future runtime category changes require an explicit source witness.

Returned Arc strings own their bytes. `ManagedVariationData::from_named_face`
uses the selected face's actual name query and retains tag fallback for absent
names; the explicit callback constructor remains unchanged. The independent source
harness loads original SfntFontFace and executes its methods through typed delegates.
Integration calls the actual TtfFont constructor and VariationAxes, including both
ranked names and missing-name fallback. It does not fabricate the callback result.

The 236,128-case / 12,151,088-byte corpus covers every UTF-16 code unit, every two-byte
UTF-8 and Latin-1 sequence, all one-byte platform paths, selected supplementary/BOM/
malformed prefix cases, 30,000 random strings, 8,000 metadata mutations, all original
truncations, unknown format fields, record-relative storage, a 65,535-record inventory
and duplicate tables. Full ordered names, selected value and actual axis names are
compared. The 93-case / 61,979-byte browser fixture is actual captured source output,
verified by mandatory CI and replayed at both DPRs alongside all original GPU gates.

The initial matched baseline times GetNames and TryGetName separately at 1/8/64/512
records across unique, mixed-encoding and duplicate configurations. Five warmups
precede 40 rounds. Font construction, transport and complete output serialization
remain untimed; production allocation, Rust replacement drops and C# delegates/GC
remain timed. Actual timed outputs must match completely. No allocation reduction,
optimization, full font-cache throughput or shaping/frame parity is claimed; future
optimization still requires Instruments evidence. Full TtfFont root/FIFO identity
and metadata, discovery, varied metrics/outlines and the complete text stack remain
separate. Previous platform/provider/package/pixel and default-compiler gaps persist.

## Current managed font ownership and metadata (2026-11-09)

`ManagedFont` ports original owned `TtfFont` construction, variation selection,
identity, metadata and palette cloning. `Root` owns the selected immutable SFNT,
lazy variation data and original 32-entry FIFO; each cache value owns a `State`
without a root back-reference. Public handles retain both. This preserves strong
cached instance identity, first-cached public settings and survival after caller
root disposal while avoiding the GC cycle that literal Arc translation would make.
Normalized-coordinate equality survives eviction; exact instance identity does
not. Hits do not refresh FIFO order. Original instance normalization runs before
lookup; full metadata preparation runs before insertion or eviction. Concurrent
selection shares the same published snapshot. Explicit controls prove final source
retirement and rejection without cache publication.

Whole-collection SFNT admission precedes selected metadata reads. Every unselected
TTC header/directory remains mandatory. Original required head/bhed and cmap,
whole-font head/hhea/maxp reads, declared OS/2/post/vhea bounds, table-presence
capabilities and selected name fallback remain separate. Original style selection
retains wght rounding/clamping and ital/slnt rules. MVAR evaluates all nine metrics
in original order, including deltas for absent nullable fields before leaving them
absent. Full axis zero inventories on the root and default-root reuse survive
unusual avar mappings. Invalid variation data stays absent; capacity errors remain
errors without freezing lazy state.

Palette cloning preserves the original no-op rules, invalid-index fallback,
ordered packed-ARGB overrides and owned full-source copy. The new root is private
until its selected palette snapshot is replaced consistently in its cache. Cache
eviction retains the original distinction between normalized equality and a
recreated palette snapshot. Original default CPAL reads and clone-time color-count
admission are intentionally different. Color table flags do not execute color
layers or qualify bitmap/CFF consumers.

Horizontal advances retain original hmtx fallback, last-long-metric selection,
HVAR precedence and cached gvar phantom counts. Vertical advances preserve actual
selected ascent/descent fallback and original vmtx behavior; they do not invent
VVAR application. Glyph item counts keep whole-font loca/glyf reads, component
flag precedence and first cached result. Layout/simple/component callbacks share
the actual selected generation. `ManagedPositioningPlan::from_font` consumes that
generation; all 32,008 original positioning cases compare selected coordinates
before complete positioning output. Complete varied outlines, vertical origins,
hinting, compressed/mapped/resident factories, discovery and paragraph/GPU text
remain required work.

The actual C# constructor/WithVariations/WithColorPalette harness compares 10,369
cases and 81,025,616 bytes, including every metadata field, names/palette bits,
settings, normalized coordinates, complete identity/equality matrices and ordered
queries. Invalid source reads retain source errors and partial mutation. Controls
include 6,000 metadata mutations, 2,000 arbitrary float requests, every selected
table truncation, whole-font versus declared extents, late MVAR failures, 32-entry
rotation and malformed selected/unselected TTC faces. The 104-case browser corpus
contains 1,805,194 bytes captured from actual source and independently revalidated
in mandatory CI. Native concurrency/retirement controls complement source identity
comparisons; they do not substitute for original platform or pixel gates.

Architecture references inform ownership only: [SkTypeface](https://api.skia.org/classSkTypeface.html)
keeps intrinsic font identity and cloning; [HarfBuzz font objects](https://harfbuzz.github.io/harfbuzz-hb-font.html)
separate face data from selected font state; [Parley FontContext](https://docs.rs/parley/latest/parley/struct.FontContext.html)
shares font discovery/source caches; [DirectWrite CreateFontFace](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nf-dwrite_3-idwritefontresource-createfontface)
creates an axis-selected owned face; [WebRender](https://github.com/servo/webrender/blob/main/webrender_api/src/font.rs)
separates templates and instances. [Win2D CanvasFontFace](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Text_CanvasFontFace.htm)
retains design-unit properties and [Vello glyph runs](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html)
carry explicit size/hinting/coordinates/transform. No external implementation is
copied. Original ProGPU FIFO/constructor arithmetic remains authoritative; CPU font
management does not initialize a GPU. Future prepared glyph/device caches need
their own exact DPI, phase, hinting and target identities.

The initial 32-workload diagnostic benchmark compares actual complete outputs for
construction, cached selection, FIFO rotation and owned palette cloning across
eight source configurations. First call, five warmups and 40 measured rounds are
reported. Request preparation and serialization are untimed; source/instance
allocations, Rust replacement drops and C# GC remain timed. No allocation or
optimization claim is made, and future optimization requires Instruments evidence.

The preceding 5ba16d417 hosted Linux run passed Rust-versus-C# variation outputs
then failed frozen browser-request equality: 24 width-setting NaNs differed only
in sign after authored `-NaN / 2` generation on x64 versus ARM64. Font and output
bytes matched. Browser request bits are now explicit; all original host-generated
cases remain additional source comparisons. Production arithmetic, published
fixture bytes and full equality gates are unchanged; mismatch diagnostics report
the first offset instead of dumping megabytes. That checkpoint's original and
diagnostic browser runs both lost the device at final DPR 2 capture. Windows and
Vulkan/winit passed; macOS was cancelled. Prior d4d56c4f9 original browser passed
but Linux/macOS were cancelled. Both compact hosted receipts retain these limits.
No original shader/compiler flags, deadlines, provider/package/source/application
or pixel gates are relaxed.

## Current managed nominal mapping and variation selectors (2026-11-09)

`ManagedCharacterMap` ports original `SfntFontFace.TryGetGlyphIndex` and
`TtfFont.TryGetVariationGlyph` over the same immutable selected source. It is
separate from native `SfntFace` mapping because the source contracts differ.
Format 12 keeps lazy visited-record bounds, original closed binary-search slots,
checked group-count conversion and wrapping glyph addition; format 13 retains its
whole-inventory gate. Zero-result fallback depends on actual format-4 presence.
The existing proven immutable format-4 endpoint ordering optimization is reused;
unsorted inventories keep first-contact traversal and exact original endpoints.
No selected source bytes or original native policy change.

Microsoft symbol maps retain original precedence over Unicode subtables. Only
OS/2 version zero and complete original 64-byte storage select a legacy page.
Page zero retains byte-to-F000 remapping; B2/B3 use their independent exact mapping
inventories. Other pages do not silently select the generic byte rule. A direct
nominal hit wins before any remapping. The two immutable data inventories are
captured through original `TtfFont.GetGlyphIndex` using authored identity-PUA fonts
and complete BMP queries, not transcribed from source tables. Every PUA code is
independently removed and re-queried to expose otherwise-hidden fallback. The
66,304 observations per page yield 240/285 sorted entries. Source and output hashes
are retained; mandatory CI reproduces the actual API observations. No foreign
implementation text or table-source structure is introduced.

Format 14 retains the complete cmap span once, as the source's cached table lookup
does. It scans original cmap records and selector records in source order,
including nonstandard platform IDs, duplicates and unsorted selectors. Reads use
whole cmap bounds rather than declared format-14 length. Original signed address
wrapping and query-time rejection remain. The first selected nondefault mapping
can explicitly return glyph zero; default ranges invoke actual nominal mapping
and require a nonzero result. Malformed matching entries keep their original
early exit, while absent matching selectors permit later source subtables. The
independent native format-14 binary-search/zero-rejection contract is unchanged.

`ManagedFont` retains a lazy per-instance ASCII cache, initialized only for codes
below 128. The immutable `State` owns a OnceLock with 128 atomic u32 slots. Bit 16
marks publication; the low 16 bits retain every glyph including zero. One atomic
word owns value and publication, so relaxed access needs no second data race or
release flag. Source failure leaves a slot empty; concurrent duplicate computation
is harmless over immutable source. The payload is 512 bytes instead of the source's
384 bytes across its separate glyph/state arrays; this explicit space tradeoff
avoids unsafe access and uses one atomic read on hits. Rust allocator/header costs
are uninstrumented. Root views and ordinary clones keep the same cache; recreated
or varied instances own new lazy caches. Independent owned character-map views
retain source bytes beyond font retirement without an Arc cycle.

Original query complexity is retained: format-12/13 and symbol searches are
logarithmic, format-4 uses the existing ordered proof or original linear route,
and variation selectors retain ordered linear record scans plus original mapping
search. Warm ASCII lookup is constant-time and allocation-free. Map queries are
dependent byte-address traversals, not independent raster lanes; GPU/SIMD staging
would not replace this original single-query policy. Shaping remains reusable CPU
state, with future glyph visibility, upload, raster and compositor work on the GPU.

The actual compiled C# control exercises the font and its retained source face,
using typed delegates for raw calls. The 8,436 cases / 5,143,568 queries compare
61,756,560 complete bytes: status, original exceptions, glyphs, nominal coverage,
symbol flags and selectors. The corpus covers every BMP value for all original
format-4 inventories, complete legacy-page domains, all 1,536 paired PUA holes,
6,000 cmap mutations, every original seed truncation, malformed counts/offsets,
overflowing glyph sums, whole/partial OS/2, duplicate/overlapping groups and ordered
UVS admission and 1,024/4,096 extra directory records. Eight-thread controls prove concurrent zero/nonzero cache values,
failed-query nonpublication and source retirement. The 88-case / 455,992-byte
browser capture is actual source output and retains all original shader, pixel,
resize and actual-presentation gates.

The 42-workload diagnostic baseline compares actual complete outputs at 128 and
4,096 queries across seven authored sources and three paths: raw nominal, cached
font and selectors. First call includes lazy ASCII state; five warmups precede 40
measured rounds. Construction, request/result allocation and serialization are
outside timing, while actual query work and source first-use allocation remain
inside. No performance optimization or complete shaping/frame claim is made.
Future optimization retains the mandatory Instruments requirement.

The preceding font-ownership research matrix remains in force for Skia/SkParagraph,
DirectWrite/Direct2D/Win2D, WebRender, Vello/Parley and HarfBuzz. This change keeps
font mapping independent of size, device, DPI, phase and atlas generations; it does
not introduce discovery, upload or GPU initialization. Additional primary references
are [OpenType cmap](https://learn.microsoft.com/en-us/typography/opentype/spec/cmap),
[DirectWrite nominal mapping](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphindices),
[HarfBuzz font function contracts](https://harfbuzz.github.io/fonts-and-faces-custom-functions.html)
and [Skia font mapping](https://api.skia.org/classSkFont.html). Their separation of
nominal mapping, variation mapping, shaping and glyph drawing informs API placement;
the original ProGPU code remains authoritative for exact behavior. No external
implementation is copied. The existing Win2D/Direct2D, retained WebRender and
Vello/Parley ownership/cache conclusions remain unchanged.

Hosted e9d85adc1 Linux full source/package, Windows portable, Vulkan/winit and the
original browser gate pass. That Linux run verifies the unchanged published
variation fixture after the explicit NaN-input repair, retaining all original
host-generated requests. macOS was cancelled. These
results do not resolve historical intermittent browser device loss or qualify
this later mapping implementation. Complete script preparation, varied outlines,
vertical origins, hinting, discovery/fallback, paragraphs, interaction and GPU text
remain required alongside the independent rendering/provider/package/pixel gates.

## Bounded hosted browser process observation

At mapping checkpoint 490835d4b, hosted Windows and Vulkan/winit pass, while both
the original and separately instrumented browser runs lose the device during
final DPR 2 queue completion. Chromium exits its GPU process with code 512 before
the WebGPU loss message. The retained diagnostic artifact shows 160 completed
submissions followed by 18 pending submissions, all issued within 34.3 ms. None
of those 18 completes before device loss 22.3 seconds later. Initial DPR 2 canvas
submissions have multi-second completion latency, while the last offscreen
submissions complete in about 2.6 ms. This identifies outstanding canvas/resize
work; it does not prove a driver fault, shader defect, lifetime defect or queue
policy cause. The hosted Linux full source suite and package pass: 8,436 mapping cases,
5,143,568 queries, 61,756,560 exact bytes, all 88 frozen browser cases and both
66,304-query legacy symbol captures. Windows portable/package and Vulkan/winit
also pass; macOS was cancelled. `perf/managed-mapping-hosted.json` retains these
independent results.

The original ten-draw burst, subsequent suspend/resume/resize, final completion,
compositor pixels and 30/180-second deadlines remain intact. Optional diagnostic
stage markers correlate the existing queue observations with those operations.
A second Linux-only observer captures three per-thread CPU/wait-channel snapshots
and one 24-frame-per-thread GDB backtrace. It selects only the spawning test's
unique current descendant GPU process and rechecks its original start-time
identity before attachment. A five-second root-owned timeout bounds GDB; only a
one-MiB text prefix is retained, without a heap/core dump. Missing tools, retired
processes and permission failures are explicit observations, never a pass.

The [WebGPU queue completion contract](https://www.w3.org/TR/webgpu/#dom-gpuqueue-onsubmittedworkdone)
defines which prior submissions the existing completion observer covers.
[GDB attachment](https://sourceware.org/gdb/current/onlinedocs/gdb.html/Attach.html)
and [thread backtraces](https://sourceware.org/gdb/current/onlinedocs/gdb.html/Threads.html)
define the new diagnostic. Attachment stops the process temporarily and therefore
can perturb timing; its result is not performance or original-gate qualification.
No external implementation is copied and no renderer architecture, shader,
compiler/driver setting or existing success criterion changes. The prior required
cross-engine architecture decisions remain unchanged.

## Explicit managed grapheme repertoire and first-element traversal

The original `src/ProGPU.Text/OriginalGraphemePolicy.cs` delegates first-element
length to `StringInfo` and separately rejects malformed UTF-16 for whole-text
capture. Rust now exposes `ManagedGraphemeProfile::Unicode16_0`, independent of
normalization and Rune-category profiles. `GraphemePolicy::ManagedRuntime` retains
that identity in the owned generation. The existing native `ManagedCompatible`
policy still means native Unicode 17 facts with GB9c omitted; it is not an alias
for actual managed runtime data. No operating-system or SDK-name inference selects
a profile. Unknown serialized profile values reject.

The source policy uses Unicode 16 grapheme and Extended_Pictographic facts and
omits GB9c. It is explicitly not full Unicode 16 UAX #29 conformance. The generator
pins all four Unicode 16/17 source-file hashes, verifies the complete existing
native Unicode 17 inventory and checks every reconstructed code point. There are
43 grapheme-class and 689 Extended_Pictographic differences between the inventories.
The facts include reserved emoji ranges. No runtime implementation or external
engine table encoding is copied. Public facts retain the Unicode license.

`managed_next_grapheme_length` walks only the first original cluster and its
following scalar. Unpaired surrogates participate as U+FFFD, each consuming its
original single UTF-16 unit; input bytes are never rewritten. Strict whole-text
capture remains separate and rejects malformed input atomically. Existing owned
text retains the exact original source, logical coordinates, capacity behavior
and policy. The state machine is the original ProGPU native algorithm with the
explicit managed property selection and GB9c omission. CPU traversal has genuine
ordered RI/emoji/preceding-class dependencies and produces reusable text state;
there is no GPU resource, synchronization or readback to amortize. Independent
rendering/upload/raster work retains its original GPU policies.

Initial range-based property selection was slower than original C# in all 14
boundary-walk workloads. The measured optimization uses an authored representation
of exact public facts: 8,704 byte indices select 156 deduplicated 128-byte pages,
with GCB in the low four bits and Extended_Pictographic in bit four. The complete
read-only payload is 28,672 bytes. Managed traversal skips unused GB9c state;
native state updates remain unchanged. This gives O(K) first-cluster traversal
and O(N) complete segmentation, O(1) scratch and no query allocation. It increases
read-only data rather than claiming a memory reduction. Safe slice indexing and
compile-time byte-array size retain bounds checks; no unsafe code is introduced.

The exhaustive original C# control compares 22,256,392 cases / 899,691,464 exact
bytes: every valid scalar in 16 contexts, all 4,194,304 surrogate-unit pairs,
every UTF-16 unit in malformed/mark/prepend contexts, class triples and long
RI/mark/prepend/ZWJ chains. It compares first length, complete strict capture and
every UTF-16 suffix, including starts inside surrogate pairs. The 49-case frozen
browser capture is 2,664 bytes, with one additional unsupported-profile rejection
per DPR. The original native 1,888,801-case, managed 89,921-case and Unicode 17
766-case controls remain mandatory. A separate hosted source job preserves the
existing full-source suite's original deadline. Browser shader, pixel, burst,
resize and actual-presentation gates remain intact.

Matched Release Time Profiler runs use identical 4,096-unit mixed inputs and
six-second windows. Compact complete exported tables, exact binary hashes,
before-source/fact exports and workload are retained in `perf/managed-grapheme-*`.
The first-element traversal dominates both traces; sampling includes core
migration and startup. Separate uninstrumented trials compare complete boundaries
against actual C# for ASCII, mixed, reserved emoji, malformed input, combining,
prepend and RI workloads at 128/4,096 units. Allocation/serialization are outside
timers; actual boundary writes, first-use work and source JIT remain inside.
Profiling is CPU evidence only: no allocator, process-memory, Metal, complete
shaping, scroll-frame or full-stack performance claim follows.

The prior complete cross-engine research matrix remains in force. Additional
primary contracts clarify this narrow extension:

| Reference | Decision for ProGPU |
| --- | --- |
| [Unicode 16 UAX #29](https://www.unicode.org/reports/tr29/tr29-45.html) and [StringInfo first-element API](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.stringinfo.getnexttextelementlength?view=net-10.0) | Explicit source profile and observed original behavior; default Unicode conformance remains independent. |
| [SkParagraph builder interface](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/ParagraphBuilder.h) | UTF-8/UTF-16 segmentation input is distinct from paragraph construction; retain source coordinates and owned reusable generations. Only public interface/comments were examined. |
| [DirectWrite GetGlyphs](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextanalyzer-getglyphs) and [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm) | Shaped glyph/cluster mapping and cached layout are later layers; a grapheme match does not establish their parity. |
| [HarfBuzz cluster contract](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html) | Preserve the distinction between source graphemes and shaping cluster levels; do not fabricate complete shaping records. |
| [Parley retained layout](https://docs.rs/parley/latest/parley/struct.Layout.html) and [Vello glyph drawing](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html) | Keep reusable CPU text generations and font identity separate from GPU drawing, DPI/phase and atlas lifetimes. |
| [WebRender project contract](https://github.com/servo/webrender/blob/main/README.md) and preceding ownership review | Preserve existing retained GPU rendering and independent platform font controls; no device/atlas cache is added by segmentation. |

Startup is static data access; there is no lazy parser, global mutable cache,
eviction or device invalidation. Text ownership, source preparation and worker
reuse remain independent of visibility culling, demand-driven upload, GPU batch
organization, fallback discovery, variable-font state and hinting. Those later
ports and their original provider/application/package/pixel gates remain open.

The three rotating-order timing trials retain exact complete output equality for
all 14 workloads. Median-of-trial Rust p50 is 2.90–9.39 times lower than the
range-based before binary in this capture. Source C# remains faster in nine
workloads, tied at reported precision in one, and slower in four; remaining
Rust/C# median ratios reach 1.28. System load was 20.93/21.13/17.56. This is a
local workload observation with all trial distributions retained, not a stable
cross-machine speedup or completed performance-parity claim. The independent
source, native, Unicode conformance and browser gates pass after the change.

## Complete original managed text preparation

`ManagedGlyphBuffer::from_text` ports the complete original
`OpenTypeTextShaper.GlyphSubstitutionBuffer.Create` path and its original
`ResolveLayoutScript`, normalization, mapping and interior-flag helpers. It is the
input stage for the existing managed GSUB machinery, not a complete shaper.
The buffer retains an immutable `ManagedPreparationSource`: exact selected
`ManagedFont`, original UTF-16, original script, all four cluster levels, all
eight independent flag bits, caller-owned pre/post context, and separately
selected normalization, mark and grapheme profiles. Clones own independent
mutable records and share immutable source owners. Already-mapped constructors
remain metadata-free; no source text is fabricated for them.

The original printable-ASCII route remains separate. Non-ASCII preparation
preserves whole-input UTF-16 admission and NFC testing before script policy;
malformed input disables the whole-input/grapheme normalization route while each
unpaired unit decodes as its original one-unit replacement. U+FFFE therefore
retains the original distinction between well-formed normalization rejection and
malformed-run mapping. Per-grapheme NFC keeps the original null-versus-present
normalized-string decision, including unchanged output: prepend/Hangul character
clusters depend on that decision. Indic composite exceptions, exact viramas,
ZWNJ/ZWJ and following-scalar tests, cross-grapheme USE mark ownership, source
space coverage and previous-glyph ZWJ clusters retain original ordering.

A successful variation selector can publish explicit glyph zero and consume the
selector without updating the preceding-code-point state. Khmer split matras,
Indic mark-first NFD splits and their first-versus-later CharacterCluster flags,
missing-glyph NFD, nonbreaking-hyphen fallback and all original space-fallback
values retain their separate routes. Every appended record keeps the original
GraphemeCluster. Character-level unsafe flags skip only the actual minimum
shaping cluster in each original grapheme range. Both input and emitted records
keep the 1,048,576 bound; a failure publishes no partially prepared buffer.

Raw layout-script selection examines original visited six-byte GSUB records,
without requiring script bodies or lookup lists to be valid. Third-generation
Indic tags take precedence over second-generation tags; the complete original
USE script inventory remains independent. This deliberately does not substitute
the stricter validated layout-plan parser for the source's narrow query.

The Khmer base predicate is captured by executing the original preparation
helper over all 1,112,064 valid scalars: 848 positive observations, encoded as
111 contiguous ranges. The capture harness neither reads nor transcribes a
foreign property table. Mandatory regeneration executes the actual original
helper again and checks the complete generated result. Other preparation rules
are direct ports of original ProGPU-owned functions; public Unicode facts and
original behavioral captures retain their existing provenance boundaries.

The original per-thread list pool is also retained: at most four empty record
arrays, each with actual capacity at most 4,096. Only complete prepared buffers
return storage on drop. Mapped buffers, oversized arrays, failed candidates and
explicit `into_glyphs` transfers keep ordinary ownership. The pool never contains
font, text, context or GPU owners. Thread-local teardown or reentrant borrowing
falls back to normal retirement without locks or callbacks. Tests prove LIFO
reuse, exact capacity/count admission, independent cloned records and source
owner retirement while record storage remains cached.

The full original C# comparison covers 312,627 cases and 873,278,732 exact bytes.
It checks all 31 fields, private lookup state, selected script, actual font
identity and original context. Four full Unicode inventories exercise mapped and
missing glyphs through actual preparation; every UTF-16 unit appears in four
malformed/mark/joiner contexts, followed by script/level/tag, class-triple, mutation
and original input/decomposition-limit controls. Runtime normalization witnesses
select a candidate profile, followed by complete output comparison. The 110-case
83,636-byte frozen browser inventory uses profile-invariant original requests and
replays all three explicit normalization profiles, plus invalid-profile and
truncated-request controls. Earlier GSUB, full-stage and positioning comparisons
remain mandatory. This does not qualify script reordering, full stage sequencing,
position-buffer preparation, fallback, paragraphs, interaction or GPU text.

This extends the preceding cross-engine research decisions. The [HarfBuzz cluster
contract](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html) reinforces
keeping source/grapheme/shaping coordinates distinct; its implementation is not
ported. [String.IsNormalized](https://learn.microsoft.com/en-us/dotnet/api/system.string.isnormalized?view=net-10.0)
and [Unicode normalization](https://www.unicode.org/reports/tr15/) define the
external canonical-form contract, while actual ProGPU observations determine
runtime data identity and admission. The preceding Skia/SkParagraph,
DirectWrite/Direct2D/Win2D, WebRender and Vello/Parley reviews still apply: prepared
CPU text and font identity remain reusable independently of GPU visibility,
upload, batching, device loss, DPI/phase/hinting and atlas invalidation. Ordered
normalization, preceding-glyph and cross-grapheme state remain CPU work; no
parallel raster work is moved from the GPU. The simple ASCII admission scan is
not a substitute for the subsequent original mapping/preparation algorithm.

Matched benchmarks time real owned generation creation and retirement separately.
Font/input construction, explicit normalization witnesses and complete output
serialization are outside timers; creation includes all actual output allocation,
normalization and retained ownership. First-call values follow the necessary
profile witnesses and are not full cold-process startup measurements. Original
C# Dispose and Rust RAII retirement have distinct collector semantics; source GC
allocation counters do not charge deferred collection time. Instruments Time
Profiler compares exact unpooled/pooled Rust binaries with the same workload.
The Allocations template stalled before launched-process entry; both bounded
attach attempts also failed to start recording. Those attempts are retained as
diagnostic failures, not allocation evidence. Complete source outputs are
compared independently, retaining timing distributions and compact CPU profiling
exports. Rust allocation-event, full shaping, scroll-frame, process-memory and
GPU performance qualification remains open.

The initial 36-workload, three-process-trial capture matches complete source
output. Rust creation medians are lower than C# in 21 workloads and higher in 15,
with missing-glyph normalization reaching 2.41 times the C# median. Load was
3.45/3.45/7.94. Pooled/unpooled creation differences are small and mixed
(before/after ratios 0.918–1.081), including timer-resolution effects; no general
pooling speedup is claimed. The pool preserves the original bounded reuse
contract. Allocation recording failed before executing the launched target;
Developer Mode was already enabled. Two subsequent attach attempts also failed
to start within their diagnostic bounds. CPU exports and complete failed-start
observations are retained; no security, compiler, driver or production gate was
changed to obtain a passing result.

### Hosted preparation gate and earlier process observation

At `6838e6023`, the complete managed preparation oracle also passes on hosted
Ubuntu 24.04.5 / .NET 10.0.14: its observed normalization profile is Unicode 15.1,
with 312,627 cases / 873,271,168 exact bytes. The same 110 browser captures remain
unchanged across hosts. Windows portable checks and Vulkan/winit pass separately.

Both hosted browser runs pass their preparation fixtures and all 204,800 offscreen
pixels, then lose the device at DPR 2. The original fails during final capture;
the diagnostic fails earlier during resize, with 160 completed submissions and
15 pending. Its GPU process exits with code 512. Because the observer previously
started only after successful resize, no process/backtrace capture was produced.
The complete receipts and failure timeline are retained in
`perf/browser-6838e-hosted.json`. They do not identify the root cause.

The optional diagnostic now emits a single console marker at the existing warm
canvas burst. Only the separate Linux DPR-2 failure observer consumes it; the
original gate never installs this marker or starts an observer. Observation is
asynchronous and starts at most once, with final capture retained as a fallback.
The existing three CPU/wait-channel samples, same-process-generation checks and
bounded GDB attachment remain unchanged. Attaching can perturb timing, so this
evidence cannot replace original success, performance or pixel gates. No shader,
driver, compiler, draw count, queue sequence or deadline changes. At `9c37a415c`, that earlier marker executes, but discovery returns no eligible
live descendant GPU process. No thread samples or backtrace are captured. Both
original and diagnostic runs still lose the DPR-2 device; this does not identify
a cause. Complete receipts are retained in `perf/browser-9c37a-hosted.json`.
Linux, Windows, managed-source and Vulkan/winit jobs pass; macOS is cancelled.


## Common managed script actions

The direct source is `OpenTypeTextShaper.GlyphSubstitutionBuffer`: beginning
insertion, directional fallback, Hangul, modified combining marks, Hebrew
composition, USE diacritics, Thai/Lao, fractions and stage-state methods. The
public Rust action enum keeps the original planner's decisions explicit without
inventing an incomplete aggregate shaper. Every operation requires the actual
prepared font/text/script/context owner; it mutates the same full records used
by GSUB. Missing metadata rejects before mutation. Original partial writes on a
font or scalar error remain visible. The original Create limit remains separate
from subsequent insertion capacity; actions do not impose a new one-million
record rejection absent from the source methods.

The original stable mark insertion sort retains crossed-cluster updates and
Arabic modifier rotations. Hangul retains coverage-dependent composition,
decomposition, Jamo masks and full record copies; Hebrew retains grapheme groups
and independent mark-feature admission. Thai/Lao retains its initial replacement
and insertion even when a later negative cluster-start index fails. USE shares
normalization scratch and creates the source's fresh default records. Fraction
classification validates raw scalars only when the original traversal visits
them; the private fraction marker remains sticky. All other state and record
fields survive unless the original operation changes them.

The oracle invokes actual original methods through strongly typed delegates;
reflection transports fields only. Authored coverage-hole fonts exercise missing
components and composites. Same-length raw record replacement tests original
mutation behavior across all 31 fields without fabricating a production shaping
result. All modern Hangul syllables and matching Jamo, malformed UTF-16/scalars,
all four cluster levels, contexts, flags, combining/Thai/Hebrew triples and 12,000
mutations are included. Complete valid scalar traversals cover direction fallback,
modified classes and USE normalization. The separate decimal/class capture visits
all 1,114,113 raw values through U+110000; it independently witnesses the frozen
Unicode 16 decimal repertoire. No HarfBuzz-derived generated source tables are
transcribed. Vowel-constraint and complex-script tables remain separate work.

The existing cross-engine comparison applies unchanged: Skia/SkParagraph,
DirectWrite/Direct2D/Win2D, WebRender, Vello/Parley and HarfBuzz all informed the
separation of reusable CPU shaping from retained GPU rendering. This checkpoint
adds no GPU upload, device, atlas, visibility, DPI/hinting, fallback-discovery or
worker-publication policy. [Microsoft's Hangul specification](https://learn.microsoft.com/en-us/typography/script-development/hangul)
clarifies why Jamo features remain distinct from composition; the exact original
ProGPU coverage decisions and masks are preserved. [HarfBuzz's cluster reordering
contract](https://harfbuzz.github.io/reordering-in-levels-0-and-1.html) informs
cluster tests only; no foreign implementation is translated. Original
crossed-cluster behavior remains the authority.

Most actions are linear scans with constant scratch; normalization uses reusable
per-call scratch. Original stable insertion, record shifts and repeated merges
retain worst-case quadratic work. The benchmark times selected production
actions on prepared buffers, excluding creation, typed raw restoration and
serialization. Three rotating-order trials include first-call and warm
percentiles and independently compare complete output. This is a new-feature
baseline, not an optimization claim or a full startup/scroll/memory/GPU result.


The initial 40-workload baseline matches complete C# output in every trial.
Across trial medians, Rust is lower in 36 workloads, higher in two and equal at
reported timer resolution in two. The largest higher ratio is 1.14 for 128-record
missing-font USE decomposition; 4,096-record missing-font fractions are 1.02.
Host load was 29.64/33.78/25.24, so these observations do not establish a stable
speedup or performance parity. Original long-run quadratic behavior remains and
no optimization is claimed. `perf/managed-script-actions-baseline.json` retains
all first/warm timing distributions, source allocation counters and binary/source
hashes. Rust allocation events and end-to-end shaping/rendering qualification
remain open.


## Contextual Arabic joining and Syriac forms

`ManagedScriptAction::ArabicJoining` preserves the original seven-state,
six-column ProGPU state machine, all 13 admitted script spellings and its separate
transparent-glyph path. Both retained UTF-16 contexts use the original bound of
five decoded scalars, counting transparent and malformed scalars; unpaired units
become one replacement scalar. The backward pre-context decoder retains surrogate
pair identity. A context only changes the state or prior action; it never creates
source glyphs or fabricated owners.

Actual source clusters determine interior unsafe-break/concat and safe-tatweel
flags. Signed minimum clusters, existing flags and all unrelated fields survive.
The opt-in concat flag and safe-tatweel flag remain independent; no end-of-text or
beginning-of-text inference replaces the original context traversal. Joining
scans O(N log R) with O(1) scratch for N glyphs and R property ranges; flag visits
cover disjoint gaps apart from shared endpoints and do not rescan every suffix.
No allocations, font queries or GPU work are introduced inside the action.

Property facts are generated independently from hash-pinned public Unicode 17
[ArabicShaping](https://www.unicode.org/Public/17.0.0/ucd/ArabicShaping.txt) and
[DerivedJoiningType](https://www.unicode.org/Public/17.0.0/ucd/extracted/DerivedJoiningType.txt).
The original join-causing/dual column and distinct Alaph/Dalath-Rish columns are
verified by 1,114,114 actual original API observations, including U+110000 and
UINT_MAX. The 527 nonzero ranges are regenerated and source-checked by
`eng/progpu-generate-rust-joining.py`; no HarfBuzz-derived generated source array
is transcribed. Frozen joining facts remain independent of runtime normalization,
Rune digit/mark categories and grapheme profiles.

The existing required Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender,
Vello/Parley and HarfBuzz architecture review still governs the reusable CPU
shaping boundary. [Microsoft Arabic shaping](https://learn.microsoft.com/en-us/typography/script-development/arabic)
and [Syriac shaping](https://learn.microsoft.com/en-us/typography/script-development/syriac)
reinforce distinct contextual analysis, feature substitution and positioning.
Their feature descriptions inform tests, not a replacement for ProGPU's exact
state/action policy. The port preserves independent Syriac fin2/fin3/med2 forms.
Full lookup-stage ordering, complex syllable actions, positioning initialization,
fallback/layout and GPU text remain unfinished.

All preceding 658,040 action cases remain intact. Added controls cover every
five-column word over the seven classes, all script spellings and flag bytes,
signed clusters, empty/transparent/malformed contexts and both sides of the
five-scalar limit. Every raw value through U+110000 is also exercised in complete
records. The expanded corpus matches 736,501 cases / 896,557,796 bytes locally,
including 368 frozen browser cases. The direct modified-class/digit/joining fact
comparison adds independent complete raw-property coverage.


The 16 joining-only matched workloads retain three rotating process trials;
all complete outputs match. Median action times are lower in 15 workloads and
equal at reported resolution in one. Host load is 6.72/8.04/14.74. The retained
baseline is not a change-versus-before optimization claim, allocation-event
measurement or full shaping/frame qualification. Local browser replay passes
1,107 common/joining checks per DPR with all original pixel/resize/presentation
gates. Native tests and strict native/WASM lint checks also pass.


## Original Arabic presentation fallback

The approved original ProGPU-owned `OpenTypeTextShaper.GlyphSubstitutionBuffer`
methods `ApplyArabicFallback` and `ApplyArabicFallbackLigatures` define behavior.
`ManagedArabicFallbackFeatures` snapshots exactly the original five global
feature decisions: last ordinal UTF-16 match, nonzero signed values, absent false.
The explicit `enabled` choice belongs to the eventual complete shaping planner;
this action does not infer a script or silently broaden fallback admission.

Only glyphs still equal to the font's original nominal mapping receive their
selected presentation form. Code points and unrelated metadata remain unchanged.
The three-component, two-component and mark passes run in original order; each
successful replacement advances past its new position. Font aliases preserve
observed rule priority. The original ligature-result query remains after a failed
component match. Existing complete GSUB component traversal and replacement retain
GDEF byte narrowing, Rune fallback, default ignorables, manual joiners, syllable
bounds, source clusters and intervening component fields. Invalid Rune scalars
keep the action's argument-error category and preceding mutations, separately from
font errors. Safe bounded indices justify that narrow error translation.

The source's generated fallback data is HarfBuzz-derived and is not transcribed.
Instead, `eng/progpu-capture-rust-arabic-fallback.py` derives all 76 presentation
bases from hash-pinned public Unicode 17
[UnicodeData](https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt).
The actual original API verifies all 4,456,456 form queries (four actions over the
raw repertoire plus UINT_MAX). Public decomposition substrings, permutations,
form variants and Arabic mark pairs produce 108,697 candidate sequences.
Independent API observations select three triples, 58 ordinary pairs and five
mark pairs. Fonts with colliding input glyphs and individually disabled outputs
witness original priority. A broader output-hole query preserves every public
Arabic ligature and private-use target to reject unobserved results. The capture
fails closed if future aliases make the witnesses ambiguous.

The original outputs include U+F201, U+F211 and U+F2EE; substituting a public
Unicode equivalent would change actual font lookup and is rejected. The checked-in
sparse arrays and test inventory are generated from public facts and API results,
never foreign source encodings. The capture uses 109,380 requests / 4,740,293
records with retained input/output hashes. Every generated byte is checked in CI.
This proves the observed candidate and output witnesses, not unspecified future
source behavior.

The existing mandatory Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender,
Vello/Parley and HarfBuzz research ledger continues to govern the unchanged
reusable CPU text boundary. [Microsoft Arabic shaping](https://learn.microsoft.com/en-us/typography/script-development/arabic)
distinguishes contextual forms, substitutions and positioning; that separation is
adopted while original ProGPU fallback policy remains authoritative. No cache,
DPI/hinting, worker-publication, font-discovery, GPU upload or atlas-generation
policy changes. Complete orchestration remains required before rendered text
qualification.

For N records, F form rows, R ligature rules, K visited component records and Q
font-query cost, selection/traversal costs O(N(log F + R K Q)); original repeated
vector removals and cluster merges retain worst-case quadratic work. Scratch is
constant, with no new per-glyph heap storage. Dependent joining, traversal and
record removal cannot use independent SIMD lanes without changing source order.
The benchmark excludes prepared-buffer creation, raw-record restoration and
serialization, then independently matches all complete outputs. New-feature
baselines do not establish an optimization, allocator-event measurement or full
startup/scroll/frame parity.

The full differential corpus retains every previous common/joining case and adds
all feature masks, duplicate/signed/ordinal feature settings, original action
bytes, nominal and result holes, every component hole, colliding inputs,
three GDEF classes plus byte-wrapped variants, ignored marks and default
ignorables, manual joiners, syllable boundaries, actual preparation/joining
sequences and 12,000 complete-record mutations. It compares all 31 glyph fields,
eight mutable state fields and retained original owner/options/context metadata.
The first run exposed an invalid-Rune error-category mismatch; correcting its
narrow action boundary yields 914,017 cases / 1,018,793,660 exact bytes on macOS.
The 546 frozen browser cases preserve all 368 preceding cases and run under all
three normalization profiles, alongside existing malformed request controls.


The 24 fallback-only workloads match complete output in all three rotating
process trials. Across trial medians Rust is lower in 18 and higher in six;
the largest higher ratio is 2.88 for 128-record ordinary pairs. Skipped-mark
and missing-form workloads also have higher medians. Host load was
20.37/13.16/14.61. These retained gaps require separate profiling; this baseline
is not full performance parity or an optimization claim. Source allocation
counters are retained; Rust allocator events and Instruments qualification remain
open. Local Chromium passes 1,641 script-action checks per DPR, including all
prior cases, plus all original six-shader, 204,800-pixel, resize and final
presentation gates. Ordinary native CI also replays all 546 frozen cases under
three runtime profiles without requiring .NET.


## Prohibited vowel sequences

`ManagedScriptAction::VowelConstraints` ports the approved original
`OpenTypeTextShaper.GlyphSubstitutionBuffer.ApplyVowelConstraints` operation.
Source code points select an exact script-specific two- or three-scalar match;
the operation inserts one new original-constructor record immediately before the
last matched source record and advances by match length plus one. The insertion
uses that record's source cluster, independently of its grapheme cluster. A zero
font glyph remains a real inserted slot. No beginning-of-text, dotted-circle
suppression flag, mark classification or ink-availability shortcut is introduced.
Original buffer options, owners, context and every unrelated field survive.

The existing HarfBuzz-derived generated table is not read or transcribed.
The generator instead reads [Microsoft's public invalid-cluster specification](https://github.com/microsoft/font-tools/blob/d06068439ff23fff99bfd058a5ab44a43183ecd9/USE/IndicShapingInvalidCluster.txt)
and pinned Unicode 17 Scripts/PropertyValueAliases facts. Its own sorted tuple
inventory is verified against every entry returned by original
`UnicodeShapingProperties.GetVowelConstraint`. All 103 entries across 16 scripts
must match. The Microsoft MIT notice is retained with portable LF newlines; Unicode public
facts retain their existing notice. All downloaded bytes are hash-pinned, and
CI regenerates and checks each output.

The original matcher independently supplies 4,863,144 observations: all script
spellings and near misses, malformed raw scalars, cross-script combinations and
every raw third value for the three longer prefixes, including U+110000 and
UINT_MAX. Initial longest-match expectations failed: an existing pair shadows
its longer prefix match. The implementation preserves that source decision by
checking the exact pair before the exact triple. Specification rows that are
shadowed remain in the verified inventory; they are not silently deleted.
Matching uses at most two binary searches with O(log R) work for R fixed rows.
The dependent source traversal and original vector insertions retain O(N²)
worst-case movement and O(1) scratch beyond the owned glyph vector. No SIMD lane
can independently insert a record without changing following source indices.

The required Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender, Vello/Parley
and HarfBuzz architecture comparison remains in the existing research ledger.
[Microsoft's USE specification](https://learn.microsoft.com/en-us/typography/script-development/use)
places cluster validation before later feature/reordering stages. That separation
is adopted, while ProGPU's exact prefix precedence, constructor fields and skip
count remain authoritative. This action changes no font discovery, DPI/hinting,
worker publication, visibility, device generation, atlas cache or GPU upload
policy. CPU shaping remains reusable and separate from retained GPU rendering.

The complete action differential test now compares 1,064,105 cases /
1,150,599,296 bytes on the observed macOS Unicode 17 runtime, retaining every
previous 914,017 case. Added controls cover every rule, all buffer flag bytes,
all cluster levels, signed extreme clusters, glyph coverage holes, exact script
spellings, malformed components, every adjacent rule pair, real normalization
and owner preparation, 12,000 complete-record mutations and 103 long runs.
All 31 glyph fields and original mutable state/owner/context metadata are checked.
The 771 frozen original cases replay under three explicit profiles in native
and browser builds without executing C# at runtime.

Twenty representative action workloads match complete C# output in all three
rotating process trials. Rust's median is lower in all 20 in this capture, with
host load 4.75/9.28/18.52. Preparation, raw restoration and serialization are
outside timers. This is a new-feature baseline, not a before/after optimization,
allocation-event or end-to-end shaping/frame qualification; earlier preparation,
lookup and Arabic fallback performance gaps remain open.

The preceding Arabic fallback hosted run 42595701066 passes managed source,
Windows and Vulkan/winit, while both original and diagnostic browser runs lose
the device during final DPR-2 capture after all 1,641 action checks and offscreen
pixels. Chromium reports GPU process exit code 512. Its failure-only observer
finds zero eligible descendants, so no stack exists. The separate diagnostic
commit now records bounded owned process identities and fixed process-kind flags
on that path, without broadening debugger target admission or changing original
workloads, flags or deadlines. This records missing evidence, not a root cause or
a browser qualification pass; `perf/browser-24f605-hosted.json` retains the reports.

## Original Indic and USE property observations

The next complex-script dependency is the original raw Indic category/position
and USE classification contract. Its approved consumers are
`OpenTypeTextShaper.GlyphSubstitutionBuffer.PrepareIndicShaping`,
`PrepareKhmerShaping`, `PrepareMyanmarShaping` and `PrepareUseShaping`.
The public boundary is `UnicodeShapingProperties.GetIndicProperties` and
`GetUseCategory`. The existing generated providers are HarfBuzz-derived and
are **not** sources for translation: only their provenance headers were read.
No generated arrays, compressed lookup layout, syllable automaton tables or
foreign implementation methods are transcribed.

`managed_script_property_facts.cs` invokes the actual original APIs through
strongly typed delegates. Its complete byte transcript supplies classification
facts and arbitrary-word extraction behavior. Reflection is confined to this
source-only harness, never used by the Rust library. The Rust generator designs
its own sorted non-default ranges with 21-bit start, 21-bit end and a 16-bit
observed value. The 340 Indic and 1,057 USE rows occupy 11,176 static bytes;
point queries use O(log R) time, O(1) scratch, no allocation and no mutable cache.
These are single point queries used by dependent script-stage traversal, not a
new whole-buffer scalar fallback. GPU execution/CPU SIMD policies are unchanged.

`ManagedIndicProperties` is an immutable transparent word with lossless
`from_bits`, `bits`, `category` and `position`. Exhaustive observations establish
the low/high-byte extraction even for unknown caller-authored values. Original
raw Indic queries return category zero/position fourteen (`0x0e00`) for invalid
code points, while the public Unicode query returns zero. Those contracts remain
separate. USE returns zero in both cases. Full-width comparisons reject high-bit
aliases before narrowing; no surrogate becomes an ordinary assigned character.
This module does not infer syllables or mutate glyphs, owners, shaping state,
normalization policy, font caches, device/DPI identity or GPU resources.

The existing Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender,
Vello/Parley and HarfBuzz architecture matrix remains applicable. Additional
primary research is [Microsoft USE character classification](https://learn.microsoft.com/en-us/typography/script-development/use),
[Microsoft Devanagari processing](https://learn.microsoft.com/en-us/typography/script-development/devanagari)
and [Unicode property definitions](https://www.unicode.org/reports/tr44/#IndicSyllabicCategory.txt).
Adopt the separation of classification, syllable validation and later reordering;
retain original ProGPU numeric outcomes instead of relabeling Unicode property
enums or silently adopting the current documentation's Unicode 18 repertoire.
These sources inform contracts only. Future syllable analysis must use original
ProGPU-owned logic or independent public grammar/behavior, not foreign state tables.

The source check compares 2,228,228 raw requests: the complete Unicode domain
including surrogates, the first excluded value, every high-bit domain alias and
three additional unsigned extremes. All 65,536 raw word extractions precede
those records. Native Rust matches all 13,500,440 bytes. The frozen 5,672-case
fixture samples every non-default range boundary, interior and neighboring
value, plus high aliases; it deliberately uses a different record encoding.
The browser repeats the entire source transcript at DPR 1 and 2 and compares
SHA-256, while retaining all previous rendering and text controls. CI rebuilds
and invokes the original source rather than trusting the frozen digest alone.

The 36-workload baseline uses immutable inputs, reused outputs and identical
indirect typed calls, with five warmups and forty samples in three rotating
process trials. All complete outputs match. Rust has 33 lower medians and three
higher: mixed USE raw Indic at 4,096 points is 1.028x; invalid USE at 256/4,096
points is 1.355x/1.356x (about 3.8 ns versus 2.8 ns). Source allocation counters
are zero; Rust allocator events are not measured. Host load is
10.61/8.83/8.15. This is a new-feature observation, not an optimization or full
shaping/layout/frame qualification. Full syllable machines, script reordering,
source plan orchestration and all original provider/package/pixel gates remain.

## Complete original Khmer preparation

`ManagedScriptAction::Khmer` ports the approved original ProGPU-owned
`OpenTypeTextShaper.GlyphSubstitutionBuffer.PrepareKhmerShaping`,
`MarkSyllablesUnsafeToBreak`, `InsertBrokenKhmerDottedCircles`,
`PrepareKhmerSyllable`, `MarkInteriorGlyphFlags` and `MergeCluster` behavior.
The foreign-derived `KhmerSyllableMachineData` arrays and lexer topology are not
read or translated. Instead, an independent ordered parser recognizes the
language observed through the original `FindKhmerSyllables` API. The source-only
harness invokes that method on a real owned original font/buffer and transports
only category inputs and resulting syllable bytes. Reflection stays in the
harness; production is typed safe Rust with no external shaping engine.

[Microsoft's Khmer specification](https://learn.microsoft.com/en-us/typography/script-development/khmer)
informs the separation of bases, subscripts, vowels/signs and later reordering.
The original ProGPU observations refine that public grammar: ordinary letters
admit an optional joiner-bearing modifier; placeholders do not share that
initial allowance. The following modifier and completed subscript groups remain
separate. The source accepts long subscript chains, ordered vowel sections,
joiner-bearing mark runs, a distinct final coeng, and a limited late subscript.
These observed decisions supersede assumptions from a simplified public grammar;
there is no claim of original Microsoft shaping or pixels. The existing
Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender, Vello/Parley and HarfBuzz
architecture research continues to govern CPU text ownership versus GPU replay.

The parser performs direct ordered optional matches, independently of generated
states, transitions or action numbers. A single cached joiner-run endpoint
prevents repeated speculative scans after a failed optional suffix. One immutable
category pass uses O(N) work and O(1) scratch; serials cycle through 1..15 and
each otherwise unclassified category receives its own non-cluster token.
The parser's output is separately checked against the original API for every
sequence through length five over all relevant categories plus a non-cluster,
every raw-byte pair, every raw category in all three positions with two relevant
neighbors, long runs through 4,096 units, and 16,000 deterministic randomized
sequences. All 1,398,562 cases / 9,586,614 bytes match. No inferred automaton or
foreign engine implementation supplies the expected outputs.

The complete stage retains exact `khmr` spelling, raw Indic category defaults,
original script-shaper writes and untouched position metadata. Unsafe flags apply
to records whose signed cluster differs from the syllable minimum, before dotted
insertion. Suppression and font-coverage policies retain their original ordering;
the dotted-circle font query still runs on an empty/no-broken buffer unless the
source suppression flag applies. Inserted records use original constructor
fields and source cluster, and receive the original syllable/shaper/category.
Pre-base vowels and coeng/ro pairs move as complete records. The pair's pref mask,
post-base masks, following cfar masks and source coeng-count behavior survive;
cluster merging extends through equal original neighbor clusters outside the
local range. In-place rotations preserve original remove/insert ordering and all
unrelated bits. Repeated source merges/rotations and insertions retain O(N²)
worst-case work; no CPU raster fallback or shader-policy change is introduced.

The full action corpus retains all prior 1,064,105 cases and adds 99,333 controls:
category combinations, every buffer flag, four cluster levels, extreme signed
clusters, multiple source/font coverage policies, exact script spelling,
consecutive/reversed actions, original normalized/malformed UTF-16 preparation,
all property boundaries/high aliases, 12,000 complete-record mutations and long
serial/subscript/mark runs. Source comparison checks all 31 glyph fields, eight
mutable states, actual source/owner/context metadata, and error continuation.
The final no-update run matches 1,163,438 cases / 1,280,069,720 bytes. Its 1,041
frozen original cases run under all three profiles in native and browser tests.

The matched new-stage baseline covers 28 workloads in three rotating trials with
first execution, five warmups and forty samples. Preparation, raw restoration
and serialization are outside timers; all complete outputs match. The 24 active
workloads have lower Rust medians. Four disabled-script no-ops are at the observed
0..0.042 microsecond resolution (two higher Rust medians, two ties), so relative
latency is unqualified there. Host load is 3.59/3.37/4.42. Source GC counters are
recorded; Rust allocation events, a before/after optimization claim and complete
shaping/layout/frame qualification remain outside this evidence. Full Indic,
Myanmar and USE stages, ordered shaping plans, positioning initialization and
all original provider/package/source/pixel gates remain mandatory.


## Original Myanmar preparation and reordering

The approved original ProGPU-owned `OpenTypeTextShaper.GlyphSubstitutionBuffer`
methods `PrepareMyanmarShaping`, `AssignMyanmarSyllable`,
`MarkSyllablesUnsafeToBreak`, `ReorderMyanmar`, `InsertBrokenMyanmarDottedCircles`,
`ReorderMyanmarSyllable`, `StableSortMyanmarPositions`, `IsMyanmarConsonant` and
`MergeCluster` supply the stage behavior. The source identity is recorded in
`perf/managed-myanmar-validation.json`; no source/shader/compiler policy changes
are made. The foreign-derived `MyanmarSyllableMachineData` arrays and lexer
implementation are not implementation inputs. The shared source-only harness
calls `FindMyanmarSyllables` on an actual original font/buffer and returns only
syllable results. A fixed named list of scalar category/position constants is
also observed; no arrays or machine structure are reflected. Production is
reflection-free safe Rust.

[Microsoft's Myanmar shaping specification](https://learn.microsoft.com/en-us/typography/script-development/myanmar)
informs the independent ordered grammar: optional kinzi, bases, stacked letters,
asat/medial sections, vowels, signs and terminal joiners. Observable original
behavior takes precedence over the document's simplified grammar and Unicode
6.0 scope. In particular, an incomplete kinzi can be broken, explicit stacker
prefixes require a base, terminal halant ends before a joiner, the final medial
group shares one optional asat, and original tone/visarga sections can intermix.
Category 57 has contextual suffix admission even though its single-record token
is non-cluster; arbitrary unknown categories retain original single-record
behavior. No public 31-character cap or foreign engine state numbering is used.
The prior Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender, Vello/Parley and
HarfBuzz research ledger remains applicable to text ownership and later GPU replay;
none supplies implementation text or generated tables here.

The independent parser compares at most three candidate extents: a broken suffix,
a bare base and an optional kinzi/stacker-prefixed base. Every unbounded lookahead
belongs to a consumed candidate; there is no repeated scan of a failed long run.
A complete run uses O(N) work and O(1) scratch, with original serials 1..15.
The source oracle compares all 26-category sequences through length five, all
256-category pairs, every raw category in all three positions around admitted
neighbors, long sections through 4,096 repetitions and 30,000 deterministic random
runs. All 12,981,319 cases / 75,858,807 syllable bytes match. The unchanged Khmer
corpus also passes through the expanded shared transport.

Preparation remains separate from reordering. A false enabled flag is a no-op;
true sets the shaper/category and syllable metadata, leaves positions unchanged,
and ORs unsafe bits only for clusters differing from the signed syllable minimum.
Reordering consumes current post-substitution records, including arbitrary raw
categories, ligated bytes and syllable kinds; it adds no script-spelling check.
Dotted-circle suppression precedes font access, and an unsuppressed query still
runs for empty or non-broken buffers. Original constructor fields and the source
cluster/syllable are retained. Kinzi recognition uses the actual first three
categories; base selection excludes ligated records. Original position state,
variation-selector inheritance, below-vowel/anusvara ordering and complete-record
pre-base reversals are preserved. Cluster merging retains its original equal-
neighbor extension beyond the sorted range.

The stable counting sort retains 16 buckets and original position clamping,
stability and record copies. Its safe O(N) scratch lease replaces the managed
ArrayPool lease without retaining font/text/context generations. A separate
per-thread cache keeps at most one empty allocation with capacity at most 4,096;
it never consumes prepared-buffer pool slots. No RefCell borrow spans shaping,
font callbacks or another lease. Growth occurs at the original sort point after
position changes and cluster merging, so allocation failure does not move ahead
of source mutations. Nested leases remain independent; oversized storage,
reentrant cache borrowing and thread teardown fall back to ordinary retirement.
The ownership test exercises nested leases, preserved records, reuse, reentrant
borrowing and oversized retirement. Sorting itself is linear; repeated original
dotted insertions and neighbor-extended merges retain O(N²) worst-case work.
Sequential grammar, position state and record movement are genuinely dependent;
there is no independent per-lane raster workload being moved off the GPU.

The action corpus adds 161,481 complete-record controls and retains all earlier
1,163,438 cases. It checks 31 record fields, eight private states, actual
font/text/context ownership and mutation/error continuation. It covers raw
category pairs, all syllable kinds, ligation and position bytes, malformed and
high code points, four levels, every buffer flag, source preparation, repeated
and reversed operations, no-ink font coverage, long runs and 16,000 randomized
record mutations. The 1,324,919 cases match 1,481,007,028 bytes on the actual
Unicode 17 macOS runtime; 1,398 frozen cases are shared by native and browser
replay under all three explicit profiles.

The new-stage baseline times 32 workloads in three rotating process trials,
with first action, five warmups and forty samples. Creation, raw restoration and
serialization remain outside timers; complete output/state bytes match in every
trial. Reorder-only workloads receive identical explicitly authored category and
syllable records outside the timer. All 28 active workloads have lower Rust median
times; four disabled no-ops sit at 0.041..0.042 microseconds and do not qualify a
relative speed claim. Host load is 4.71/3.70/3.54. Source GC counters are observed;
Rust allocation events, a before/after optimization claim and full shaping/frame
performance remain open. Full Indic/USE stages, actual plan orchestration,
position initialization, fallback/layout/interaction, GPU text and all original
provider/package/source/pixel qualification remain mandatory.


## Original USE stages with independent recognition

The approved ProGPU-owned `OpenTypeTextShaper.GlyphSubstitutionBuffer` methods
`PrepareUseShaping`, `FindUseSyllables` filtering, `AssignUseSyllable`,
`MarkSyllablesUnsafeToBreak`, `AssignUseRephaEligibility`,
`AssignUseTopographicalActions`, `RecordUseRepha`, `RecordUsePrebase`,
`ReorderUseShaping`, `InsertBrokenUseDottedCircles`, `ReorderUseSyllable`,
`IsUsePostBase`, `IsUseHalant` and `MergeCluster` supply the stage behavior.
The source-only harness invokes original syllable recognition with explicitly
owned identity indices and transports category inputs and original result bytes.
Only byte-literal category names/values are reflected from `UseShapingData`;
no provider arrays are accessed. Generated foreign tables, state numbers and
lexer structure are not used to implement the independent Rust recognizer.

[Microsoft's USE specification](https://learn.microsoft.com/en-us/typography/script-development/use)
supplies public cluster families and ordered components. The original API's
observable behavior refines those contracts: distinct early/late sakot-base
sections, halant versus invisible-stacker/reordering-killer endings, contextual
final modifiers, symbol/general-base ties, permissive incomplete hieroglyph
joiner/segment sequences and broken-cluster grouping. Original behavior takes
precedence over current specification revisions and the suggested 31-character
cap. The prior engine research ledger remains applicable; no external rendering
or shaping engine implementation text, transition table or generated topology is
transcribed. Production uses typed safe Rust and the already qualified original
property API, with no additional runtime engine dependency.

Recognition compares independently parsed cluster-family extents. Long accepted
sections are consumed; a cached segment-begin endpoint bounds repeated failed
hieroglyph prefixes. The complete scan is O(N) with O(1) parser scratch. The
source oracle covers every sequence through length four over 45 categories,
every raw-byte pair, every raw category in all three positions with two relevant
neighbors, long sections through 4,096 repetitions, 30,000 random runs and
10,000 authored public-grammar combinations/mutations. All 5,870,757 cases /
35,631,822 output bytes match; tests use original API outputs as the oracle.

Preparation first writes original shaper/category metadata, then builds the
complete filtered index view before any syllable write. CGJ is omitted;
ZWNJ is omitted only when the next non-CGJ record passes the original Rune mark
query under the explicit managed mark profile. Invalid scalars reject at that
query, leaving earlier classification writes and untouched subsequent metadata.
The sentinel maps recognition's end back to the complete original record range.
Leading and wholly excluded records retain old syllables; excluded records
between included boundaries are assigned with their original containing range.
Unsafe bits use signed cluster minima. Repha eligibility sets the original one
or three leading records without clearing unrelated existing eligibility bytes.
Exact source Arabic-joining script names suppress topographical rewriting; other
scripts retain original whole-syllable initial/medial/final/isolated decisions.

An independent index lease preserves the source pooled-allocation boundary.
It retains at most one empty allocation of 8,192 32-bit indices per thread, with
no font/text/context owners and no RefCell borrow spanning stage work. Nested
leases remain independent; reentrant borrowing, oversized storage and thread
teardown retire normally. The ownership test checks these cases. Index storage
is O(N); ordered filtering, grammar and topology carry genuine sequential state.
No per-pixel workload or GPU shader moves to CPU.

Post-lookup repha recording stops at the first ineligible record and marks only
the first substituted eligible one. Pre-base recording marks the first
substituted record in the syllable. Reordering inserts broken circles after any
leading repha run, preserving the first source record's cluster and Arabic action.
Suppression precedes the font query, and unsuppressed empty/non-broken calls still
query the actual font. Repha moves after cluster merging, while pre-base records
retain the cluster value captured BEFORE merging. This source distinction is
intentional in the port. Neighbor-extended cluster writes, ligation-sensitive
halant barriers, multiple-substitution components and whole-record movement stay
unchanged. Original repeated merging/insertion/movement retains O(N²) worst-case
work; in-place rotations preserve the source remove/insert sequence.

The full action corpus adds 207,963 cases while preserving all 1,324,919 earlier
cases. It checks all 31 glyph fields, eight mutable lookup states, immutable
source/context ownership, failures and continuation. Controls include all raw
category pairs, all syllable bytes, ligation/component state, eligible-prefix
termination, filtered leading/interior/trailing records, malformed/high code
points, source joining scripts, every buffer flag, four levels, repeated and
reversed actions, real source normalization and long runs. The 1,532,882 cases
match 1,715,629,716 bytes on the actual macOS Unicode 17 runtime; 1,910 frozen
cases remain shared by native and browser builds. Full Indic stages, actual
planner integration, positioning initialization, fallback/layout/interaction,
GPU text and all original provider/package/source/pixel gates remain required.


The first USE baseline had four higher Rust medians, all hieroglyph workloads,
under host load 46.22/36.17/21.06. Instruments Time Profiler sampled the actual
4,096-record prepare/reorder workload for six seconds: the generic tail matcher
accounted for 1,651 of 5,640 leaf samples despite the disjoint hieroglyph prefix.
A confirmed hieroglyph prefix now returns its original family result before
unrelated Indic/number/symbol candidate parsing. Failed segment-begin prefixes
retain the prior cached fallback path. The complete parser corpus and full-record
source suite are repeated after this change.

Matched six-second Release profiles retain complete compressed exported tables,
the before parser source, identical workload bytes and exact binary hashes. The
second trace has 5,074 samples with no generic-tail leaf samples on this workload.
CPU-core distribution and startup differ; sample counts are attribution evidence,
not a timing comparison. Independent uninstrumented tests rotate current Rust,
original C# and before Rust in three process-order trials over all 40 workloads;
every complete output matches. All 36 active workloads have lower current Rust
medians than C# in this capture. Hieroglyph after/before median ratios are
0.523–0.571 across both lengths and fonts; four disabled no-ops are clock-limited.
The paired run's host load is 6.14/20.73/19.96. The exact original baseline and
paired samples are retained without discarding slower results. Source GC counters
are observed; no allocator-event, process-footprint, GPU/Metal or end-to-end
shaping claim follows. Full rendering/layout performance remains a separate gate.

The first copied profiler executable lacked its executable mode, so that attempt
produced no qualifying sample. Correcting the owned file mode allowed both full
recordings; an export attempted during finalization was repeated only after the
recording became terminal. The complete tables replace large temporary traces
for disk retention. No source driver, shader, browser flag or compiler default
is changed to obtain these CPU-stage measurements.

## Packed Linux browser-process diagnostics

The retained Myanmar hosted failure reports an owned Chrome descendant with one
nonempty command-line record containing a GPU marker, while the prior observer
required a separate NUL-delimited role argument. It therefore collected no stack.
[Linux proc documentation](https://docs.kernel.org/filesystems/proc.html) and the
primary [cmdline contract](https://man7.org/linux/man-pages/man5/proc_pid_cmdline.5.html)
describe mutable argument storage; a process can overwrite the visible format.
The [exe contract](https://man7.org/linux/man-pages/man5/proc_pid_exe.5.html)
supplies the actual executable link. These public contracts inform an independent
recognizer; no Chromium process-title implementation was read or copied.

The failure-only observer now receives the bundled browser path from Playwright's
public [executablePath API](https://playwright.dev/docs/api/class-browsertype#browser-type-executable-path).
Both separate and packed role admission require the actual executable's matching
device/inode. Packed admission requires one command-line string with the exact
executable path and first complete GPU-role token; arbitrary substring matching,
basename matching and shell-style splitting of values remain excluded. The
observer retains owned ancestry, unique selection and the original start ticks,
adds captured executable identity, and rechecks these before observations and
attachment. A `/proc` snapshot is not a process lifetime lease; concurrent exits
remain unavailable observations. Full command lines and environments are never
retained. The original browser flags, shader/compiler settings and deadlines are
unchanged, and debugger attachment remains confined to a separate diagnostic.

Three synthetic tests pass on macOS Python 3.14 and Linux ARM64 Python 3.12,
including exact/packed forms, paths with spaces, private embedded markers, role
boundaries, unrelated images, missing files, cycles, reparenting and generation
changes during selection/revalidation. An isolated Linux ARM64 Chrome 154 probe
observes the actual packed-executable-prefix route, three thread/CPU samples and
a 23,629-byte GDB result with successful detach/exit zero. Some optimized frames
cannot unwind; the complete compressed receipt retains those limitations. This
existing installed browser differs from hosted Playwright Chromium 151 and the
probe uses a blank page. It validates observer plumbing, not the earlier crash's
cause, original rendering pixels, performance or browser reliability. The prior
hosted failures remain required evidence even when a later original gate passes.

The probe owns a new temporary profile and process group, retires them on exit,
and leaves no guest dependencies or dumps. The Ubuntu VM is returned to its
original suspended state. Compact validation and complete compressed output are
retained in `perf/browser-packed-process-validation.json` and
`perf/browser-packed-process-live.json.gz`.

## Original Indic preparation with independent recognition

`ManagedScriptAction::IndicPreparation` ports the approved ProGPU-owned
`PrepareIndicShaping`, `AssignIndicSyllable` and `MarkSyllablesUnsafeToBreak`
contracts from `src/ProGPU.Text/OpenTypeTextShaper.cs` (SHA-256
`ab2058b7328fb172796fe146f4da5ab5258fd303bfbc47d028c476da357955b1`).
The original `FindIndicSyllables` runs only as a black-box oracle. Its generated
machine arrays and lexer implementation were not read or transcribed for this
port. Reflection transports complete records and literal category names/values;
it does not inspect generated state, transition or action tables.

[Microsoft's Devanagari specification](https://learn.microsoft.com/en-us/typography/script-development/devanagari)
and [Tamil specification](https://learn.microsoft.com/en-us/typography/script-development/tamil)
inform independent syllable families, ordered components and the separation of
font-dependent reordering from classification. Original API observations remain
authoritative, including malformed raw categories and tie decisions. The existing
cross-engine research ledger still applies: reusable CPU shaping precedes demand
driven GPU realization, with immutable font/text generations. This change does
not alter cache publication, source raster policies, DPI, hinting or GPU work.

The recognizer compares independently parsed consonant, vowel, standalone,
symbol and broken extents, retaining the original non-Indic fallback and ties.
Tests observe separate halant/ZWJ/nukta continuations, medial termination, repeated
matras, modifier and Vedic tails, Ra-halant and repha prefixes, and raw category
57's contextual modifier behavior. Category 57 alone remains non-Indic. A failed
matra lookahead caches its actual joiner interval, so repeated standalone joiners
cannot rescan an arbitrarily long suffix. Accepted tails are consumed and the
number of candidates is fixed: recognition is O(N) with O(1) scratch, no heap
allocation and genuine sequential dependencies. A category-visit bound checks
8,192-joiner runs with independent prefix/suffix combinations without a timing
threshold. Existing property lookup contributes O(N log R) work for R ranges.

The source-only corpus compares every sequence through length five over 21
categories, every raw-byte pair, raw bytes in each position with two relevant
neighbors, long components through 4,096 repetitions, fifteen 100,000-repeat
joiner cases, 30,000 random runs and 30,000 authored grammar combinations and
mutations. All **4,768,385 cases / 43,831,648 bytes** match original results.
No generated foreign topology is reproduced in the corpus or production parser.

Disabled preparation performs no mutation. Enabled preparation writes only the
original shaper/category/position fields before assigning complete syllables,
with serials wrapping from fifteen to one. Unsafe-break/concat bits use signed
cluster minima without rewriting cluster values. No insertion, font lookup or
reordering occurs at this stage. Complete source generation/context admission
remains common to the action API. The full source suite retains all 1,532,882
earlier cases and adds 124,765: **1,657,647 cases / 1,931,091,724 bytes** on
macOS Unicode 17. It compares all 31 glyph fields, eight lookup states, source
owners, failures and continuations. The 2,466 frozen cases are replayed under
all three normalization profiles in ordinary native tests and the browser.

The new-stage performance baseline covers 40 complete-output matched workloads
at 128/4,096 records with full and space-only fonts. Three rotating process
trials retain first-call and forty warm samples. Rust medians are lower in
35 of 36 active workloads; space-only 128-record halant/nukta is 1.750 versus
1.666 microseconds (1.0504x). Four disabled no-ops are clock-limited. The host
load is 8.57/11.79/16.78; these are observations, not full-stack speedup claims.
Alternating joiners scale from about 2.54 to 82 microseconds in Rust across the
two sizes. Original C# results and allocation counters remain in the receipt.

Matched six-second Instruments Time Profiler captures use the actual final
4,096-record joiner workload, including buffer creation and restoration. The
Rust executable matches the timing baseline; the C# profile is a fresh Release
build of unchanged source/harness with its distinct assembly hash recorded.
Complete single-workload outputs match again after that rebuild. Full compressed
time-profile exports and input replace temporary traces. Startup, JIT attribution
and CPU-core distributions differ, so sample counts are not timing evidence.
The final parser adds only the test-only visit bound after profiling; its
production algorithm is unchanged. No historical Rust before/after, allocator
event, process-footprint, GPU/Metal or complete shaping qualification is claimed.

Indic initial/final reordering still requires the actual selected font and GSUB
plan; no synthetic feature-presence substitute is admitted. Complete stage
sequencing, metric/default-ignorable preparation, fallback, paragraph layout,
retained interaction, GPU text and original provider/package/source/pixel gates
remain open. Receipts are `perf/managed-indic-{validation,baseline,time-profiler,
package,cleanup}.json`; the mandatory source parser gate is
`eng/progpu-test-rust-indic-syllables.py`.

The following hosted Indic-preparation browser gate completes all 7,401 action
checks per DPR and both offscreen pixel inventories, then loses the device at
the original final DPR-2 capture. Its separate diagnostic reaches DPR-2 resize
and times out after the GPU process exits. The packed-process observer now
selects the actual owned executable/generation and obtains three samples; four
worker threads consume most sampled CPU. Required GDB/timeout tooling is missing,
so the receipt explicitly reports an unavailable backtrace. These observations do not identify
the failure's cause or qualify browser reliability. Complete original and
diagnostic reports and compressed process observations are retained in
`perf/browser-9e0b967-hosted.json` and its referenced gzip.

The workflow now installs GDB and the bounded `timeout` utility only after the
original browser gate fails, before the separate diagnostic. Installation failure
does not suppress the existing process samples. No original workload, deadline,
shader, browser flag, driver or compiler policy changes; a diagnostic pass cannot
replace the original failure. Owned executable/ancestry/start-tick revalidation
still precedes debugger attachment, and no other browser process is admitted.

## Original Indic initial and final reordering

The approved original `OpenTypeTextShaper.cs` methods `InitialReorderIndic`,
`UpdateIndicConsonantPositions`, `WouldSubstitute`, `InsertBrokenIndicDottedCircles`,
`InitialReorderIndicSyllable` and their mark/sort/cluster/mask helpers now share
the Rust selected GSUB plan. A probe retains one mutable original record buffer
across all selected matching lookups; count/first-glyph changes, typed source
errors and allocation order remain observable. Actual Unicode and UTF-16 layout
scripts remain separate. Raw positions, stable order, byte saturation, old/new
cluster merging, Kannada swaps and complete original dotted-circle defaults
survive. Sorting and repeated source merges retain O(N²) worst-case behavior.

`FinalReorderIndicSyllable` and its reph destination helpers retain original
virama restoration, substitution/ligation/multiplication flags, Malayalam base
selection, matra/reph/prebase movement and copy-versus-merge ordering. Init-form
continuation uses an explicit original Unicode 16 Rune profile, independently
matching every raw query and rejecting invalid scalars at the actual query.
All previously recorded public Indic and cross-engine research remains applicable.
No generated machine source or foreign implementation is used.

The complete original-action corpus now matches **1,882,608 cases /
2,194,696,960 bytes** on macOS Unicode 17, retaining all prior 1,657,647 cases.
The four-property control matches 1,114,113 raw points / 17,825,808 bytes. Sixteen
authored GSUB fonts exercise persistent probe mutation, no-change lookup results,
deletion, required/disabled features, extensions and actual GDEF filtering.
The frozen browser corpus contains 4,022 cases; its expanded browser execution
has not yet run. A 66-workload, three-trial complete-output baseline completed
before the implementation-first workflow request; original samples are retained
without claiming optimization or full performance qualification.

At the user's request, remaining exhaustive browser, profiling, fresh-package
and platform validation is deferred until the complete stack is assembled.
Implementation proceeds in larger connected batches with quick compile checks.
This changes work ordering, not parity requirements or the final release gates.

## Implementation-first connected shaping batch (2026-11-09)

This batch follows the user's explicit request to port connected code before
exhaustive validation. Quick all-target/all-feature compilation and strict native
Clippy are the checkpoint gates; complete differential, browser, performance,
Instruments and fresh-package work is deferred. Existing failures remain open.

Approved direct-port provenance, all original repository implementation:

| Source | SHA-256 | Ported surface |
| --- | --- | --- |
| `src/ProGPU.Text/OpenTypeTextShaper.cs` | `ab2058b7328fb172796fe146f4da5ab5258fd303bfbc47d028c476da357955b1` | ShapeCore ordering, script/direction/feature policy, raw feature presence, safe-boundary verification, position construction, legacy kern, fallback marks, combining-cluster reversal and Arabic stretch |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` | TrueType simple/composite parsing, original float contour writer, selected variation integration, cache/no-ink slots, bounds and vertical origins |
| `src/ProGPU.Text/SfntFontFace.cs` | `4639127d2390813ae578d93a5a18cd5d4cd613235ac704b8d5d61a1123393e0a` | Independent raw vertical-origin bounds, including empty-slot success and whole-glyf header reads |
| `src/ProGPU.Vector/PathGeometry.cs` | `a246ffa979b6e21f78a48b84017720d70ece894b61eb976909105da5d365bb88` | Finite start/control/endpoint envelope, not tight curve bounds |

No generated Indic/USE machine implementation or external engine implementation
was inspected or copied. The original stage and outline code above is approved
ProGPU-owned material. Previously recorded public architecture/script research
continues to apply; source algorithms remain authoritative.

`ManagedShapingPlan` owns the actual selected font, requested/effective feature
policies, Unicode/layout script identities and optional GSUB/GPOS plans. Missing
tables do not trigger unrelated variation queries. Feature merging retains first
insertion order with last values through an indexed ordered inventory, preserving
explicit-default identity, per-cluster baselines/ranges and original directional
insertion. Substitution interleaves every original script stage, actual Indic
font probes and Arabic fallback/context reversal. Khmer receives the independent
selected layout tag without mutating prepared source metadata.

`shape_true_type` joins the original stages through immutable owned output. Font
outline caches are per selected instance; no cached geometry points back to its
font cache. A cache lock encloses recursive parsing and original child publication,
with depth/cycle checks and skipped missing components. Geometry retains complete
float points, figures, variation deltas, original contour ordering and no-ink
states. Raw static bounds remain separate from selected-variation figure bounds.
VORG and TrueType bearing/fallback branches retain source integer/float order.
The outline adapter is explicitly TrueType-only; managed CFF/font fallback,
flipped-outline caching and broader font-source factories remain pending.

Position construction retains all record fields, original default-ignorable
removal/visibility, forward cluster repair, space-fallback advances, rounding and
mark clearing. GPOS then runs before conditional legacy kerning, late mark
clearing, attachment resolution, fallback mark stacks, backward ordering and
Arabic stretch. Optional Verify reshapes every safe fragment with remapped ranges,
original requested features and extended pre/post contexts, checking glyph IDs,
code points, clusters and all four position values. It never silently ignores the
flag. Output remains original font-space design units; UI scaling/Y inversion,
paragraph placement and GPU realization are separate consumers.

Complexity follows the original data-dependent CPU stages: feature planning is
expected O(F); outlines are O(points + components + emitted geometry) for visited
cached glyphs, with bounded recursion; metric/fallback passes are linear except
original cluster repairs and mark-skipping/attachment traversals. Legacy kern
format 0 retains binary pair lookup. Stretch is linear in records plus emitted
copies, with original 256-record/run and 1,048,576-total limits. Verification can
reshape many fragments and remains opt-in. These dependency-carrying operations
are not independent SIMD lanes; no GPU workload was moved to CPU. No latency,
allocation, arithmetic parity, browser or release improvement is claimed yet.

Deferred validation must cover full-stage interactions for all script families,
feature/default/range identities, absent/malformed tables, TrueType raw versus
varied geometry/cache behavior, original float compiler differences, default
ignorables, vertical origins, fallback mark stacks, both kern formats/headers,
Arabic stretch limits and every verification boundary/direction. Existing stage
fixtures and canonical shader tests are retained unchanged; the expanded Indic
browser corpus remains unexecuted locally at this checkpoint.

## Managed CFF integration, shaping output and paragraph bidi (2026-11-09)

Implementation-first continuation: all-target/all-feature compilation and strict
native Clippy pass. Exhaustive source differential, browser, benchmark, Instruments
and fresh-package execution remains deferred by user instruction. No new raw
benchmark, trace, package or browser artifacts are generated by this batch.

| Original repository source | SHA-256 | Port surface |
| --- | --- | --- |
| `src/ProGPU.Text/Cff1OutlineSource.cs` | `60aa23e2b007c29ffc7d1063058f489b22c3d94cbab1d8628fa0b973d2e94129` | Managed INDEX/DICT/FDSelect admission, lazy CID subroutines, Type 2 figure/conversion semantics |
| `src/ProGPU.Text/TtfShapingFontFace.cs` | `af8d8a1ddac42255293348a80ef8ae8d81c338377cacf9328c314e09b2637ae5` | Typed owned face adapter, checked metric rounding and glyph/axis range policy |
| `src/ProGPU.Text/Bidi/BidiParagraph.cs` | `0590c6e5dcbf56e36a4b0e2f78644ce34823f9cb4b30572953a5183b0e7234d0` | UTF-16 levels/runs, ASCII fast path, synthetic inline isolates and signed-level L2 ordering |
| `src/ProGPU.Text/Bidi/Uax9Resolver.cs` | `a9862b3ad316ad8a1f0ed7fdb73d1963ce55a4b5d9f8f7bbb9a810328efe9458` | Original raw-surrogate decode consumed by the existing owned level resolver |

`TtfFont.cs` and `OpenTypeTextShaper.cs` retain the exact hashes from the preceding
batch. Their original outline selection/flipped cache and Shape/ShapeDesignUnits
methods provide the integration and scaling contracts. No external font engine
source, fallback implementation or generated shaping machine was used. Existing
public architecture/specification research remains applicable.

CFF1 managed INDEX admits the original first encoded offset above one; native
CFF1/CFF2 retain exact-one admission. Both retain encoded immutable offsets rather
than creating per-index integer arrays. Managed DICT parsing keeps the original
48 operands, 96-character real buffer, ignored nibble, early-termination and
visited-field semantics. FDSelect keeps managed signed-Int32 range admission;
native unsigned admission is unchanged. Per-font CID local-subroutine caches
retain empty results for the original malformed private-data cases. The selected
font root owns this immutable source/cache without reference cycles.

The existing owned Type 2 arithmetic is monomorphized over a typed writer. Native
output keeps finite-coordinate admission, explicit closing edges, strict index
conversion and the original independent CFF2 variation path. Managed output keeps
move-only figures, implicit closure, original float narrowing and unchecked
managed integer conversion at the original operations. Its subroutine offset
addition wraps as Int32, separately from native Int64 admission. Stack/recursion,
stems, operands, random state, flex and ordered curve arithmetic remain shared.
Managed geometry is emitted once, retaining complete figure boundaries.

`glyph_outline`, `glyph_bounds`, `flipped_outline` and `vertical_origin_y` now
select the actual managed TrueType/CFF1 route. Outline and flipped caches retain
separate original generations and empty entries; selected source/geometry survives
cache retirement. Successful empty CFF output remains None. The original bounded
CFF reader delegates rejected programs (including deprecated seac) to a separate
fallback; that fallback is not ported here. Such requests return the explicit
`UnsupportedOutline` error, without claiming complete CFF source parity or silently
publishing missing ink. Existing explicit TrueType entry points remain available.

Scaled output preserves font-space run records and applies screen-space Y
inversion only at publication. Vertical advances/origins are rounded at the
requested size before scaled residual positioning, retaining half-width integer
division and the original operation order. Empty public shape calls bypass plan
and font work. Reusable design-unit writes clear the destination before admission,
retaining the original failed-shape empty destination. The font-face adapter keeps
checked rounded Int32 conversion separate from the shaping core's saturating
Int16 position fields.

Managed paragraph bidi retains original UTF-16 bytes and raw unpaired surrogates.
The ASCII/non-RTL fast path returns original zero levels, while explicit RTL and
non-ASCII use the complete existing level resolver. Higher-level inline directions
insert temporary isolates only; projected levels refer to unchanged source units.
L2 operates on already-broken logical units and preserves original signed-level
and sentinel behavior, returning None for the identity case. It does not infer
line breaks or modify glyph clusters. Resolution retains O(N) owned/scratch data;
L2 costs O(N*L) for at most 255 visited signed levels. Dependencies and ordered
state preclude whole-stage SIMD; no GPU rendering work was moved to CPU.

Deferred validation specifically includes native CFF1/CFF2 regression after typed
writer sharing, managed shifted INDEX/negative DICT/range cases, CID empty-cache
behavior, move-only/implicit-closure/nonfinite figure output, integer conversion,
legacy fallback failures, cache identity/lifetime, vertical fractional scaling,
font-face checked overflow, malformed UTF-16 and isolate insertion within surrogate
pairs, signed L2 levels and source paragraph run order. Existing default compiler
float differences, browser reliability and provider/package/application/pixel gates
remain independent and unresolved. Full wrapping/fallback/interaction, GPU text,
compressed/resident/discovery factories and the rest of the rendering port remain
unfinished.

## Connected basic paragraph and retained interaction port (2026-11-09)

This production batch follows the user's implementation-first instruction:
assemble connected code, compile and lint, and defer exhaustive differential,
browser, benchmark, Instruments and package cycles until more of the stack is
assembled. Nothing in this section claims source or release qualification.

| Original in-repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Text/TextLayout.cs` | `24193ae1b73fb8e28d3871a8d19f829172d254accf34b6a10dccc1bcb650403d` | Basic request/candidate writer, active shaped/ASCII/vertical paths, font partitioning, context, safe breaks, tabs, width/alignment, L1/L2, cluster/row geometry |
| `src/ProGPU.Text/TextInteractionSnapshot.cs` | `8db724368e4de1bf768ae941edf88d99fc5616bbbe846a868e466f9ed0de3444` | Complete retained carets, hits, cluster hits, rows, source points, horizontal/vertical navigation and selection |
| `src/ProGPU.Text/TextEditInteractionSnapshot.cs` | `a4895b53f5425ea889ec2c0b5b699e734c58ab922112ca97d947b446e1ae5808` | Explicit original grapheme ownership, grouping, exact trailing edges, empty-row remapping, query-local missing-interior rejection |
| `src/ProGPU.Text/OriginalGraphemePolicy.cs` | `b1a28832063fa469e11e740f90c5ec6fcba0a0ed7088595bd79c51d2cdf9799c` | Strict whole-source admission followed by original first-element capture before fallback |
| `src/ProGPU.Text/SourceDeviceLineMetrics.cs` | `05a640361056e1e5a2b1d05540e369f61c7265d33e1221cf8c6cbfcb9c59fd31` | Exact immutable font, pixel-em and horizontal-direction validation |
| `src/ProGPU.Text/TtfFont.VerticalDeviceMetrics.cs` | `77db7203a0e7e0d4e54751317ca3e6d2bc22fb78f40438753a79dd74b28f0709` | Raw selected-face directory, VDMX duplicates/extents, raw fvar presence and static/symbol admission |
| `src/ProGPU.Text/SfntVerticalDeviceMetrics.cs` | `72b61520c285d6a9e488a375273c4eb993aa3017667ede70c4eb893fb72efc1f` | Complete group/record/ratio validation, version-zero per-charset defaults and exact first-applicable size selection |

The original owned source is directly ported under handbook A-1. No external
text-layout engine, generated shaping machine or fallback implementation was
read or copied. Existing recorded public architecture/specification research
remains applicable. The legacy unused `GenerateLayoutLegacy` path, texture-atlas
construction, static file discovery and platform fallback factories are outside
this batch; they are not silently represented by another engine.

`ManagedTextLayout` freezes original UTF-16, primary font, request and explicit
Unicode profiles. Its production path partitions by original bidi runs and
actual selected font identity. Missing non-space/tab scalars use a typed
`ManagedFontFallback` provider; default ignorables keep the preceding selected
fallback run. A caller-supplied ordered font slice is an explicit portable
provider, not system discovery qualification. Every emitted glyph retains the
actual selected immutable font. Run prefixes/suffixes use `ManagedTextContext`
views over the original `Arc<[u16]>`, matching source ReadOnlyMemory ownership
without O(N*R) text copies for R font runs. Full-context assignments now use
`.into()`; the previous owner-retention test keeps its exact original assertions.
The separate Verify path retains its original concatenation semantics.

Font runs preserve original paragraph BOT/EOT flags and complete context. Design
output is scaled with the selected font's em; vertical runs keep original
size-rounded metrics. RTL logical ordering reverses cluster groups without
reversing glyphs inside them. Safe wrapping retains source whitespace-before-
overflow selection, preceding safe break and forward emergency safe break.
Tabs keep original interval arithmetic, invalid interval skipping and 0.001
comparison. Measured widths use logical candidate order independently of visual
placement; trailing measurement policy is retained. Wrapped lines reset trailing
space/tab group levels for L1 before original L2. Basic Justify retains the
original left route; the independent styled writer remains unfinished.

Horizontal hard breaks keep whole CRLF ownership, blank rows and trailing rows;
the vertical writer retains its distinct LF-only policy. Alignment, nonfinite
input behavior, unsigned layout dimensions, source Y inversion and sequential
float pen/line accumulation retain original operations. Empty source keeps zero
content/measured size, with an explicit source-metrics empty row when applicable.
`reflow` constructs a complete candidate and leaves the prior generation intact
on failure. Public glyph membership is immutable, so the original defensive
membership check is enforced by ownership rather than rescanning mutable lists.

Interaction freezes advance intervals, not ink bounds or glyph drawing offsets.
Horizontal row membership and source end delimiters are retained; vertical
geometry keeps its separate baseline/metric path. Carets preserve physical order,
both bidi affinities and actual empty-row insertion indices. Original adjacent
duplicate comparison is expressed as linear compaction against the original
predecessor, even when that predecessor was removed, rather than repeated vector
deletion. Point hits select the actual horizontal vertical band before horizontal
distance. Row/source navigation, tie-breaking, half-interval hits, selection
sorting/merging, signed range arithmetic and preferred-X double distances retain
their separate original routes. Immutable snapshots share owned arrays, survive
layout retirement and never query fonts during interaction.

Explicit EDIT captures original well-formed grapheme boundaries before font
fallback, using the requested original Unicode profile. It keeps complete
multi-grapheme shaping intervals, rejects overlapping/unproven owners, and
requires exact original trailing caret geometry. Ordinary navigation remains
unchanged. Missing interior ownership rejects only affected queries; no synthetic
interior caret, source word boundary or reconstructed shaping record is added.

VDMX visits the complete bounded inventory before selecting any record. Raw
directory duplicates/extents and raw fvar presence are independently retained;
malformed later metadata cannot be hidden behind an earlier match or unsupported
face. Version-zero charset termination stays independent, version one shares its
inventory, and all original offsets must identify real group starts. Source line
metrics retain exact font identity and integer pixel em; no nearest record,
scaled extrema, completed-height rounding or ordinary-layout change is admitted.

Complexity: layout retains O(N+G+L) source/glyph/line storage, plus shaping scratch.
Original script shaping and feature-query costs remain independent. Sequential
pen, run, wrap and group dependencies are not SIMD-independent workloads. L2
retains O(G*levels); the original tab scan retains O(stops) per tab. Snapshot
cluster construction and selection sorting are O(G log G); retained navigation
and hit queries remain original linear scans. VDMX is O(records + ratios log
groups), with O(groups) scratch. No GPU raster workload was moved to the CPU,
and there is no new measured performance claim.

Static verification: all targets/features compile and strict native Clippy passes.
Deferred source differential controls include whole positioned layouts, fallback
context/identity and cancellation/errors, all four directions, empty/CRLF/soft
rows, whitespace/tabs, bidi affinities, combining/ligature/multi-font graphemes,
EDIT ambiguity/rejection, malformed UTF-16, source VDMX complete-inventory and
owner admission, float/signed-zero/nonfinite behavior, snapshot lifetimes, and
linear duplicate compaction equivalence. Full browser/platform/text pixels,
throughput, allocation, sustained frames, package/source/application qualification
and historical default-compiler/browser failures remain open. Styled text,
system discovery/resident fallback, GPU text and the rest of the engine remain
unfinished.

## Styled paragraph writer and static raster recommendations (2026-11-09)

This batch continues the requested implementation-first cadence. Native all-target,
all-feature compilation and strict Clippy pass; formatting and whitespace pass.
Complete differential, browser, performance, Instruments and package cycles are
deferred. Neither this batch nor the preceding connected text batches are
parity-qualified.

| Original in-repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Text/StyledTextLayout.cs` | `18412d80ebb95b0f7be9a896e142824b020205df8474fb1c73389c4dfa3a1307` | Complete constructor/validation, style and box maps, paragraph selection, fallback-aware shaping, safe wrapping, visual grouping, placement, tabs, justification and original output records |
| `src/ProGPU.Text/TtfFont.RasterizationRecommendations.cs` | `976729eb9f50a6db3132364b9b01abb2443658193de03df862260393d58d5647` | Exact static TrueType query, raw selected-face directory/duplicate/extent checks and raw fvar exclusion |
| `src/ProGPU.Text/SfntRasterizationRecommendations.cs` | `348b0f7f57905adf0a10b3018f4b56bcb8c50141060f32670f70fd4bec3a5224` | Complete gasp range validation, version-dependent independent flags, exact first-applicable selection and missing-coverage distinction |

These are direct ports of original ProGPU-owned source under handbook A-1. No
third-party text implementation, generated shaping machine or foreign layout
structure was used. The previously recorded public architecture research remains
applicable. The platform discovery/resident font resolver and GPU glyph atlas are
still separate unfinished subsystems.

`text::layout::styled::StyledTextLayout` retains its complete immutable UTF-16,
style partition, inline-box inventory, paragraph request and explicit Unicode
profiles. Original options and whole UTF-16 admission precede style/box/paragraph
preflight and all fallback/shaping callbacks. Style ranges must cover the entire
source without splitting a surrogate pair; empty text has exactly one zero-length
default range. Every U+FFFC has exactly one finite nonnegative inline object, with
caller object order/tag preserved independently of physical output order. Typed
alignment enums exclude undefined values at the Rust API boundary.

The styled writer retains distinct original semantics from basic TextLayout:
LF-only paragraph boundaries, strict source encoding, exact paragraph-start
selection (no previous-record inheritance), NBSP whitespace participation and
four tab alignments. First-line/left/right indents, before/after spacing and
minimum/exact line height preserve source operation order. Line metrics use the
actual selected font, font size and baseline shift; width scaling affects glyph
X offsets, while advances use width times tracking. Baseline shift is applied
separately to output Y. Layout metadata does not itself realize scaled glyph
textures or claim rendered text pixels.

Each paragraph resolves bidi before style/font partitioning. Missing scalars use
the explicit shared fallback boundary; default ignorables retain the preceding
selected font within the original segment. Context views retain the actual
paragraph owner and original BOT/EOT flags. Per-run shaping direction comes from
the resolved level, independently of the requested paragraph base direction.
RTL logical restoration reverses cluster groups, retaining glyph order inside
each group. Wrapped trailing-whitespace groups reset for L1 before original L2.
Source font owners survive the resolver and every output generation.

Safe wrapping retains whitespace/preceding-safe/emergency-forward choices.
Logical tab fitting inspects the remaining logical paragraph field; placement
recomputes tabs over the actual visual line. Custom stops remain strictly ordered
absolute coordinates. Center/right/decimal field measurement preserves first
period/comma/space selection, subsequent-tab termination, rejection of negative
advances and original repeating interval fallback. Tabs move the pen and emit
neither glyph nor object. Justification counts entire non-tab whitespace groups,
including a trailing group, and distributes remaining space only on non-final
lines. Original recorded-width arithmetic is retained even when there are no
expandable gaps. Blank/trailing rows and exact line spacing preserve independent
maximum-bottom tracking.

Rust uses a typed glyph-or-object candidate rather than a nullable font and
sentinel glyph identity. Visual-order arrays reference original candidates, so
line reordering does not duplicate font ownership. Inputs and tab inventories
are immutable owned arrays. `reflow` constructs a complete new generation and
leaves the previous one unchanged on failure. The original StyledTextLayout has
no basic TextInteractionSnapshot producer; this batch does not fabricate such
geometry from glyph output after tabs/justification have been lowered.

Storage is O(T+G+B+L), with complete style/box maps and retained output. Source
run, pen, wrapping and group arithmetic is sequential; filling style maps uses
slice fill, and no raster workload is moved to CPU. Bidi L2 retains O(G*levels).
Tab fields keep the original ordered scan for each eligible custom stop, so
adversarial tab/stop inventories retain that source cost rather than a claimed
universal linear bound. Shaping and platform-provider costs remain independent.
There is no new latency, memory or allocation performance claim.

`read_rasterization_recommendations` validates the full original gasp inventory
in O(R) time/O(1) scratch before publication, including later ranges after a
match. Versions zero/one keep their allowed bits; symmetric smoothing does not
imply grayscale. Missing tables, valid empty inventories and uncovered sizes are
distinct from an explicit zero recommendation. The selected-font query checks
raw directory duplicates/extents and raw fvar presence before static admission;
an invalid ignored fvar record cannot manufacture static capability. The query
keeps the original TrueType-only admission without importing VDMX's separate
symbol-face restriction. It changes no normal font cache, glyph placement,
hinting, raster target, source GDI/GDI+ policy or package qualification.

Deferred controls include complete styled glyph/object/line output and float
bits; mixed font/size/width/tracking/baseline runs; fallback context and ownership;
all style/paragraph/box/UTF-16 rejections before callbacks; bidi, combining and
ligature wrapping; ordinary/NBSP/trailing whitespace justification; decimal and
repeated tabs in logical versus visual order; empty/final rows; indent exhaustion,
exact spacing and overflow behavior; retained generations/reflow; and complete
gasp version/range/duplicate/extent/fvar/missing-versus-zero source inventories.
Browser, GPU text pixels, performance, original source/application/provider and
package/release gates remain open, together with earlier compiler-float and
hosted-browser reliability failures.

## Original glyph GPU transport and raster engine (2026-11-09)

This connected production batch ports the shared GPU engine underneath GlyphAtlas.
It does not substitute generic filled-path rendering for the original glyph
algorithms. The implementation-first workflow continues: all native targets and
features compile under strict Clippy; full source/GPU/browser/performance/profile
and package validation is deferred. No new runtime or pixel qualification follows.

| Original in-repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` | Exact GPU records/uniforms and CompileGpuOutlineData/AppendGpuOutlineData/bounds methods, lines 2922 onward; existing owned outline route |
| `src/ProGPU.Text/GlyphAtlas.cs` | `5fb66ddeba150535a48c760b18a584dd161af38c6fc3ed46accc8e35f3f64582` | Original upload/ring capacities, sparse EnsureGpuGlyph/AppendGpuGlyph, growth/copy/trim, contiguous upload chunks, selected binding contracts, raster pass and texture-copy ownership |
| `src/ProGPU.Text/GlyphAtlas.GpuRasterizer.cs` | `f19bcc92ca35244e967b06a8e39c8740ed92936081e2fc0ad5451e8a1e798c10` | Bounded uniform/coverage slices, oversized immediate route, original compute dispatch/fragment viewport and packed-coverage copies |
| `src/ProGPU.Text/GlyphAtlas.BinaryRasterizer.cs` | `5e23141a06f6fbad83bbf0ad53dfc1edbfd1e42179f5d5ab18065053ef5075ff` | Compute-only admission, 256-segment bound, exact five bindings and ordered horizontal/vertical dispatch |
| `src/ProGPU.Text/GlyphAtlas.HintedGeometry.cs` | `fab0c90753583197b3f057ea8eb9ea98e952e22d78e1576b557d7023dcd8adcc` | Shared selected-record range transport, physical scale/phase boundary and finite record/frame admission; complete hinted-geometry owner/failure-ticket/atlas integration remains pending |
| `src/ProGPU.Backend/GpuCoverageUpload.cs` | `c01cb5c0aad517480e81d6a1df56550d384eb00cddd6a45d4444ab9cbc874648` | Separate 256-byte row and 512-byte placement alignments, exact R8 copy extents and checked source/destination capacities |
| `src/ProGPU.Backend/Shaders/GlyphRasterizer.wgsl` | `50a30bf25cbfdc410b4350ec5a58b436cd5607f0b3326a391f5f87a1b2318971` | Existing canonical module, unchanged, through cs_main/vs_raster_fallback/fs_raster_fallback |
| `src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl` | `1545b475dfa1076d39e02830f3a18606535ea0c7da7f94a93b6af3d47243cd61` | Existing canonical module, unchanged, through both original binary compute entries |

Only original ProGPU-owned implementation and existing repository wgpu API usage
were used. No external engine source or structure, generated shaping machine,
driver replacement, compiler-default alteration or shader rewrite was introduced.
Original binary events, scalar positions, source root parameters, lane visit
order, shared memory, atomics, barriers and explicit smart-curve endpoints remain
in the unchanged canonical module. This does not close the original ARM64 WARP,
Windows/source/application/provider or package/pixel gates.

CPU transport retains exact 32-byte glyph descriptors, 48-byte uniforms and
existing 48-byte segment records. Original managed outline compilation skips
move-only figures, emits lines/quadratics/cubics in order and adds a closing line
only when the actual last point differs from the original start. Empty glyphs
return an all-zero descriptor, even after a preceding destination prefix. Bounds
keep original endpoint comparisons, 1e-6 quadratic extrema and operation order;
cubics retain the original control-point envelope. NaN comparisons and separate
Math.Min/Max propagation are not replaced by a generic path-bounds algorithm.
Whole-font compilation is an explicit API; normal atlas use compiles only the
visible requested glyph and reuses segment scratch.

`GlyphRasterizer` retains exact selected-font reference identity independently
of normalized variation-key equality. Cache keys hold original font owners,
preventing address reuse; record handles carry rasterizer identity and cache
generation. Foreign or trimmed handles fail before encoding. Raw outline upload
copies the selected original segment range and rebases only its descriptor start;
all coordinate and metadata words survive unchanged. Empty records retain actual
slots but cannot be rasterized as fabricated ink.

Original 48-record/896-segment initial capacities grow by 50 percent. Pending
record/segment writes remain contiguous and flush before growth copies and at
the original 256 KiB chunk boundary. Oversized staging is released after use.
Actual payload write counts/bytes, copy submissions, uniform writes, bind-group
creation and raster passes/submissions are exposed as diagnostics, not estimates.
The original 4 MiB allocated-outline budget is trimmed explicitly at the frame
boundary before collecting requests. That reset invalidates source slot handles
without destroying coverage already held by an atlas generation.

The API receives complete physical request batches and preflights all slots,
frames, atlas ranges, packed-byte arithmetic and actual device capacities before
GPU raster mutation. It then streams requests through the original bounded
64 KiB uniform/256 KiB coverage rings with constant staging storage; it does not
allocate a second per-glyph CPU coverage array or a complete-batch uniform copy.
Each normal group uses one compute/render pass and one submission. Uniform slices
remain 256 bytes, with original 48-byte payloads; coverage rows remain 256-byte
aligned, while slice bases independently align to 512. A glyph exceeding the
coverage ring uses its original standalone compute route with one 48-byte uniform,
one exact output buffer and a binary scratch buffer only if needed.

The typed execution preference uses native compute where device limits admit it,
then the original identical per-pixel fragment entry when compute limits are not
admitted. Explicit forced paths fail closed. CPU SIMD/scalar modes are not yet
implemented by this engine and return UnsupportedExecution rather than selecting
another route. Compilation or structural device limits are not original adapter
profile qualification. Binary compilation is lazy; its request requires native
compute, the original bounded segment inventory and prepared physical scale-one,
phase-zero/integer-origin framing. Both horizontal and vertical pipelines are
dispatched in original order with a separate horizontal scratch buffer; binary
never selects the grayscale fragment route.

Compute writes original packed words and performs GPU-only R8 copies after the
pass. The fragment route uses exact atlas-local viewport/scissor and its three-
vertex primitive, with no blending or coverage resampling. Target creation clears
the actual R8 attachment; growth copies the previous base-level region on GPU.
Old textures/buffers/bind groups are dropped as Rust handles without explicit
destroy calls, leaving real encoded dependencies under wgpu submission ownership.
There is no finalizer polling, GPU wait, unmap, readback or RenderLock behavior.
Atlas callers must publish requested residency only after a successful raster
batch; full cache failure-ticket retirement remains their pending integration.

CPU work is O(S) for emitted outline segments plus original lazy font expansion,
O(G) for batch preflight/encoding, bounded by ring capacity for staging. Sparse
font lookup is expected O(1); upload growth has the original geometric behavior.
Curve bounds, source traversal and queue ordering carry dependencies; independent
pixel coverage stays in the unchanged GPU shaders. No CPU fallback or per-pixel
CPU effect was introduced, and no measured latency/throughput/allocation claim
is made for this new engine.

Deferred controls include original complete record/segment/bounds bytes; selected
font identity/variation/palette/cache lifetime; empty glyphs and closing edges;
growth/trim/stale-handle/foreign-owner rejection; all ring/chunk boundaries,
oversized and mixed grayscale/binary batches; 512-byte copy placement; default
compiler float behavior; unchanged binary/source inventories; native-compute and
fragment full pixels; asynchronous failure/device-loss/publication; retained
resource lifetime; cold/warm counters; browser/WASM and full original platform,
provider, source/application and package gates. Dynamic atlas packing, LRU and
residency sets, hinted source generations/failure tickets, color glyph paths,
CPU implementations, final text draw replay and full engine integration remain
unfinished.

## Original dynamic glyph coverage atlas (2026-11-09)

This implementation batch connects `text::glyph::GlyphAtlas` to the preceding
original glyph GPU engine. It follows the requested implementation-first
workflow: native all-target/all-feature and browser-target strict Clippy,
CPU-only compilation, formatting and whitespace checks only. Exhaustive differential, runtime GPU,
browser, performance, profiling and package validation remain deferred. Neither
this connection nor the previous low-level engine is qualified text rendering.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Text/GlyphAtlas.cs` | `5fb66ddeba150535a48c760b18a584dd161af38c6fc3ed46accc8e35f3f64582` | Coverage GlyphInfo, actual font-instance keys, nested batch/frame counters, cache/usage/residency, original raw outline bounds and four-pixel padding, coverage shelf packing/classes, growth/UV refresh, LRU, capacity sentinels and clear |
| `src/ProGPU.Text/GlyphAtlas.GpuRasterizer.cs` | `f19bcc92ca35244e967b06a8e39c8740ed92936081e2fc0ad5451e8a1e798c10` | Atlas-side original ring admission/flush and oversized-immediate scheduling into the shared engine |
| `src/ProGPU.Text/GlyphAtlas.HintedGeometry.cs` | `fab0c90753583197b3f057ea8eb9ea98e952e22d78e1576b557d7023dcd8adcc` | Failure-ticket reservation, exact region deduplication, first-best-waste reuse and whole-pending cache invalidation; native hinted-source acquisition itself remains unported |

No external implementation was read or transcribed. The new files are direct
ports of original ProGPU methods with typed Rust ownership. Existing public
architecture research and source/provider/package gates remain applicable.
Canonical shaders, compiler defaults, drivers and font source bytes are unchanged.

Coverage starts at the original 260-square default and grows by the original
next-power-of-two rule up to its configured maximum. Both allocation axes retain
their independent eight-pixel classes, two-texel perimeter/gaps, exact glyph
coverage extents and ordered same-height shelf search. Growth occurs before LRU
reuse, flushes preceding writes, clears/copies the actual GPU base level, refreshes
cached normalized UVs and increments only the texture revision. Old wgpu texture
clones remain live for retained/submitted consumers; no explicit destroy or CPU
readback/repacking is introduced. Clear invalidates residency and resets shelves
while clearing the same actual attachment rather than manufacturing a new owner.

Keys retain the actual selected `ManagedFont` root/instance, glyph, size identity
and original byte phase. Float key normalization follows original Single equality
for signed zero/NaN without altering source raster coordinates. Lookup uses a hash
index; a separate physical slot vector with a LIFO free list preserves original
dictionary enumeration for equal-frame/equal-waste LRU ties. Allocation excludes
every current-frame entry. Capacity fallback entries retry only on a later frame
when atlas preference is explicit. Spaces, tabs, LF and CR preserve their original
font advance without acquiring a region. Empty grayscale records may emit original
zero coverage; binary admission still requires a nonempty physical record.

Raw atlas bounds visit each original figure start and line/quadratic/cubic control
point after the original scalar X and negated-Y multiplication. They deliberately
do not substitute the tight quadratic GPU-record bounds or the generic path's
finite-point filtering. Source-order and finite signed-zero min/max behavior stay
intact. The current Rust atlas admits finite positive em/scale and checked finite
integer bounds; invalid/zero/negative/overflow behavior of the original unchecked
direct size API remains an explicit parity gap for final controls. Color/bitmap
table presence currently rejects the complete font route, even for an ordinary
glyph in that font, until the original per-glyph bitmap/layer selector is connected.
It never silently substitutes monochrome rendering or fabricates a color result.

Nested batches advance/trim only at the outer boundary. Pending requests stay
bounded by the original uniform/coverage rings, with 256-byte rows and 512-byte
slice starts; oversized compute glyphs flush preceding work and submit immediately.
New-glyph accounting counts actual appended outline slots. Residency capture
deduplicates original first-use keys and reuses caller storage, growing with unique
keys rather than duplicate occurrences. Replay preflights all keys before marking
any frame/use and rejects foreign atlas identities. Texture revision and generation
remain separate consumer contracts; key residency alone does not repair stale UVs.

Failure reservations preallocate enough ticket storage for the entire pending
group plus the active acquisition. Drop guards abandon pending cache claims on an
error, panic unwind or asynchronous cancellation without allocation, font callbacks,
polling or GPU destruction. Tickets deduplicate actual origin and full reserved
extent, and reuse selects the first smallest waste. Upload identities invalidate
after failure; already submitted wgpu resources retain their real lifetime. The
original hinted failure-ticket mechanism is also used for interrupted Rust design-
font acquisitions, a cancellation-specific extension rather than a new successful
rendering policy. Dropping an atlas never asynchronously flushes it: callers use
the explicit batch/flush API before consumer submission.

Required consolidated controls include complete source packing/UV/counter traces;
dictionary free-slot/LRU ties; current-frame protection; capacity retries; duplicate,
foreign, stale and partially missing residency; resize/clear and old-texture replay;
record/segment growth during pending work; boundary/oversized ring scheduling;
no-ink/move-only records; complete outline/bearing/advance/phase bytes; asynchronous
cancellation at every suspension point and injected upload/finish/device loss;
bounded staging and source/font/texture ownership; full native compute/fragment
and browser pixels; cold/warm throughput/allocation/memory and all original
platform/provider/source/application/package gates. Color atlases/decoders,
hinted generation/selection owners, source raster policies, CPU raster modes and
final shaped-text draw integration remain unfinished. No performance claim follows
from static compilation or this implementation milestone.

## Original embedded color glyphs and RGBA atlas routing (2026-11-09)

This connected implementation batch adds original font image selection/metrics,
legacy COLR palette layers, bounded SVG document extraction and the separate
color atlas route. The requested implementation-first cadence remains in force:
strict native/browser-target Clippy, CPU-only compilation, formatting and diff
checks are the current evidence. Full source differential, runtime browser/GPU,
codec, performance, profiling and package controls remain deferred, not waived.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` | BitmapGlyphData/DecodedBitmapGlyphData; TryGetBitmapGlyph, sbix strike/duplicate selection, all owned CBLC index/CBDT PNG-image formats, TryDecodeBitmapGlyph presentation arithmetic, legacy COLR base/layers and TryReadSvgGlyphDocument/bounded decompression |
| `src/ProGPU.Text/GlyphAtlas.cs` | `5fb66ddeba150535a48c760b18a584dd161af38c6fc3ed46accc8e35f3f64582` | TryCreateColorBitmapGlyph, bitmap/COLR/outline acquisition order, separate RGBA budgets/shelves/growth/UV refresh, color-domain LRU filtering and shared residency/counters |
| `src/ProGPU.Backend/GpuTexture.cs` | `0b5e89412acd16e86ae2f86c7e85e8bfa880b85dea196a79c2014cc1928bdb25` | Exact bitmap subrect dimensions, unpadded RGBA row pitch and queue texture-write semantics; generic GpuTexture API remains separate |

Original ProGPU algorithms are directly ported; no external source text or
implementation structure was read/transcribed. Codec libraries are normal
Cargo dependencies, not copied implementations. `image` 0.25.10 (MIT OR Apache-2.0,
MSRV 1.88) is locked with only png/jpeg/bmp/gif/hdr/pnm/tga features; default
formats, Rayon, native codecs and assembly features are not requested. `flate2`
1.1.11 (MIT OR Apache-2.0, MSRV 1.67) is locked with its Rust backend. Package
metadata was checked with `cargo info`; both fit the crate's Rust 1.90 floor.
Only public APIs/features were consulted: [image API](https://docs.rs/image/0.25.10/image/),
[image feature inventory](https://docs.rs/crate/image/0.25.10/features),
[image resource limits](https://docs.rs/image/0.25.10/image/struct.Limits.html) and
[flate2 multi-member gzip API](https://docs.rs/flate2/latest/flate2/read/struct.MultiGzDecoder.html).
The source itself uses an external image codec (StbImageSharp); this dependency
boundary is not a substitute rendering, shaping or font engine.

Embedded selection retains the actual immutable font with a zero-copy encoded
range. sbix precedes CBDT; every table scans its complete ordered strike inventory,
chooses the nearest valid ppem and favors larger ppem at equal distance. Raw
selection retains NaN/infinity comparison behavior. sbix preserves original
strike bounds, the 16-depth duplicate rule, original-glyph cycle rejection and
outermost duplicate origin; its three admitted graphic tags remain PNG/JPEG/TIFF.
CBLC retains all index formats 1–5, original first matching subtable behavior,
sparse order/next-offset rules and fixed-size metrics. CBDT retains images 17–19,
format-17 horizontal admission, signed bearings, nonzero metric dimensions and
actual encoded byte ranges. Unsupported/malformed inventories reject rather than
selecting a substitute outline or nearby record inside these readers. Mapped and
glyph-resident file providers are still unported.

Decoding uses the actual selected payload, never a resized image or tag-based
assumption about its bytes. A failed decode does not try a second strike/table.
The wrapper preserves original positive finite target admission, actual decoded
dimensions, tight straight RGBA output, source sbix/CBDT bearing arithmetic,
ppem-zero scale default and original optional outline-framed sbix presentation.
No ICC/EXIF transform, premultiplication or CPU glyph coverage is introduced.
The unrelated decoder default allocation cap is disabled; managed output-array
capacity remains bounded, and explicit codec resource failures remain Capacity.
Compressed decoding/pixel conversion stays inside the normal codec dependency;
owned strike lookup/metadata traversal has dependent early exits and no per-pixel
loop. Native/WASM codec output, truncation/error behavior, large-image admission,
JPEG reconstruction, 16-bit reduction, HDR conversion and CPU SIMD/performance
remain independent required gates. PSD payload decoding remains unimplemented;
TIFF selection remains metadata and does not imply TIFF decode support. These
differences must not be hidden by weakening the source image corpus.

COLR reads preserve original version-0 base/layer lists, including legacy lists
inside version 1, binary-search selection and whole-font read bounds. A matching
zero-layer base remains distinct from no base; layer order/glyph IDs remain exact.
Each layer uses the actual selected CPAL palette, with original white fallback
for absent/foreground indices. This does not implement COLR-v1 paint programs or
SVG layers. SVG extraction preserves the first matching glyph range, original
offset/length guards, 16 MiB encoded/decoded limits, 8192-byte read chunks, gzip
member expansion and strict UTF-8. XML/path/brush parsing and its cache are still
pending. Atlas use explicitly rejects an available SVG document until that route
is connected; an absent/invalid document can continue to ordinary outlines.

The atlas no longer rejects a whole font merely because color tables exist.
It tries bitmap decode/allocation first, then the original COLR vector sentinel,
then ordinary geometry. A color allocation miss continues the original fallback
sequence without shrinking pixels or adding a new color-capacity sentinel. RGBA
starts at 64 square and grows to 512 by default, independently of R8 coverage.
Both domains use the same original packing algorithm with separate shelves and
matching-color cache filtering. Growth flushes pending raster work before copying
the selected actual texture; only that domain's cached UVs refresh. Global texture
revision and cache generation remain independent. The original LRU fallback to
GlyphInfo dimensions remains separate from strict retained-residency admission;
vector sentinels do not manufacture residency claims. Original mixed sentinel/LRU
source traces are required before qualification.

`GlyphColorTexture` is an opaque RGBA wrapper that cannot be supplied to grayscale
or binary dispatch. Its queue writes use original four-byte pixels/tight rows;
actual write calls/bytes are counted before async validation completion, including
cancellation. Existing clear/copy ownership is shared without exposing color as
coverage. Both textures retain real submission owners and are cleared in place.
Failure tickets include texture domain so equal coordinates in R8/RGBA cannot
alias during unwind/reuse. No image upload synthesizes a glyph compute submission
or increments the original new-outline count. Completed color entries participate
in the same source-instance/frame residency rules as coverage entries.

Remaining consolidated controls include full encoded-range/strike/duplicate/index
and metric bytes; malformed/overlapping/sparse tables; selected palette/variation
identity and resource retention; all codec outputs/errors/limits; original SVG
gzip headers/CRC/members/trailing data and Unicode errors; exact source acquisition
order; bitmap misses, vector sentinels and both atlas live sets; growth/UV/clear/
eviction/cancellation/device loss; actual cold/warm upload counters; source images
and final text pixels on native/browser backends; allocation/memory/performance;
and every original platform/provider/source/application/package gate. Full hinted
owners, SVG geometry/brushes, color-layer drawing, CPU raster modes, final shaped
text rendering and the rest of the full rendering stack remain unfinished.

## Original vector geometry, dash traversal and material records (2026-11-09)

This implementation batch adds the shared production prerequisites for SVG glyph
layers and ordinary vector rendering. It follows the requested implementation-
first cadence: static compilation/lint checks now, full differential/native/GPU/
browser/performance/package validation after stack assembly. No parity or release
qualification follows from these utilities or the retained material records.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Vector/PathGeometry.cs` | `a246ffa979b6e21f78a48b84017720d70ece894b61eb976909105da5d365bb88` | Original ordinary figure/segment families and flags; transforms, finite control bounds and complete Parse/token grammar. Deferred Boolean expression/query classification remains pending. |
| `src/ProGPU.Vector/PrimitivePathGeometry.cs` | `ca099d68853a9d354bc9db4b106f4a71f9cbd0277c694f358b8e03339a709493` | Open-line/rectangle recognition and rectangle/rounded-rectangle/ellipse construction |
| `src/ProGPU.Vector/ArcSegmentGeometry.cs` | `847d2cab48f10fe903eafff7ac56da9a36f4be77ad6932258c45b3ac6b31428e` | Center conversion, endpoint/axis extrema, complete transform routes, shader axes, explicit bounded flattening, subarcs and length-parameterized arc dashes |
| `src/ProGPU.Vector/BezierSegmentGeometry.cs` | `12a9c8f17ced93e202714e9039d67f8e29a7b5d3f148ea9523d431b76184eb19` | Original quadratic/cubic evaluation, ordered splitting, subsegment length admission, bounded length tables and analytic dashed subcurves |
| `src/ProGPU.Vector/DashPattern.cs` | `1ea24ab5b9288ffad072e77d2fb56b10d9ffd594fe9a8b3ea2f37924eaa2c17f` | Double-input construction, odd-pattern duplication, interval repair, original modulo/phase/state rules and two-pass line dash output |
| `src/ProGPU.Vector/Brush.cs` | `c3a745a0321ad92d9cbfeb2c3f1529b8f7b6e7a979836df7dd8b8635c8a053e8` | All non-image material payloads/constructors, gradient policies, bounded path-gradient inventory, full hatch-set admission and exact pen flags/double dash ownership; retained command-interception dispatch is pending |

Only original ProGPU implementation was read/translated. No new dependency or
foreign implementation is introduced. All new files remain under the crate's
unsafe-code prohibition. CPU work here is source parsing, dependent topology/
state traversal and bounded geometry preparation, not pixel rasterization. The
dependent dash phase/count/fill and cumulative length loops preserve source
ordering. Fixed two-component arithmetic retains source expression grouping;
exact default-runtime SIMD/FMA/NaN/signed-zero results and CPU vectorization/
throughput still require the consolidated source and performance gates.

Source paths retain line, quadratic, cubic, rational quadratic/cubic and endpoint
arc families with original controls/weights, stroke/smooth flags, figure fill/
closure and optional cap overrides. Rational constructors retain their original
unstroked default. Managed fill enum values are explicitly converted to the
opposite native shader wire; no raw cast aliases these contracts. `Matrix4`
retains all sixteen original coefficients, with explicit source XY point/direction
expressions; it does not infer source rotation/skew construction history. Ordinary
transforms preserve all flags and weights. Arc failure follows the original line
fallback, not hidden flattening. Boolean reconstruction/classification is neither
represented as an ordinary path nor approximated by bounds.

The parser retains F/M/L/H/V/Q/T/C/S/A/Z, implicit repetitions, independent
last-command/control history, permissive numeric arc flags, move-only figures,
relative origins and implicit closure. Borrowed source slices replace per-token
string allocations; complete tokenization and candidate construction precede
publication. Existing original Unicode category data is reused for BMP char
classification, retaining UTF-16-code-unit admission for supplementary scalars.
Simple command casing includes the original long-s S case. Full original Unicode
version/case/decimal parsing/error behavior remains a differential gate. Rust
errors report UTF-8 byte offsets. Numeric repetition after Z returns an explicit
nonprogress error instead of reproducing the original infinite loop.

Primitives preserve original epsilon and finite-extent admission, explicit fourth
rectangle edge, four clockwise ellipse arcs and zero-only rounded-corner fallback.
Open-line recognition retains cap/stroke/closure checks. Rectangle recognition
includes the implicit closing edge, exact zero-radius arc admission, original
double signed area and tolerance; it does not manufacture rectangle identity for
curves or Boolean paths. Bounds retain finite control envelopes and analytic arc
extrema rather than substituting tight Bezier or painted stroke extents.

Arc math keeps corrected radii, exact antipodal half-turn selection, source
endpoint seeding, in-sweep extrema and the distinct endpoint/axis point expressions.
Transforms preserve similarity, positive-axis and principal-axis branches, full
2D affine admission, determinant/reflection sweep behavior and original large-arc
intent. Explicit flattening keeps original endpoints and the 64-span bound; it
is not implicitly selected by rendering. Polynomial evaluation/splitting keeps
ordered original expressions; subsegments retain flags and their original
32-sample positive-length admission. Length tables use the original clamped
1..256 sample count and source defaults, with bounded Rust stack storage replacing
temporary heap arrays. Runtime Lerp/FMA exactness remains independently unqualified.

Dash construction retains actual double source intervals/offset/thickness before
the original float narrowing, odd inventory duplication, source zero-interval
repair and ordered phase reduction. State admission, normalization and Advance
remain separate. Lines use their original 1e-4 epsilon; curves use 1e-5 traversal
and the same original 1e-4 state/Advance behavior. Count and fill remain separate
ordered passes, and drawn curves use the original sampled-distance parameter
lookup followed by analytic subcurve creation. Failed subcurve creation publishes
no partial segments but retains the original already-counted final state. Source
constructor NaN/infinity behavior remains distinct from later state admission.

For termination, phase normalization rejects a complete interval inventory with
no representable subtraction progress. Traversal rejects only after the initial
partial element plus a full interval cycle makes no distance progress. A single
small interval may round away and continue into a later large interval, preserving
the original terminating route. These explicit failures replace source hangs;
there is no epsilon perturbation, phase modulo shortcut or iteration-budget
truncation. Allocation/count overflow returns Capacity and never partial output.
These termination differences require targeted controls in the consolidated run.

Materials retain straight RGBA, raw opacity, unsorted stops, complete coordinate
frames and independent spread/interpolation. Sweep keeps Repeat as its distinct
default. Radial radius selection retains comparison-based NaN behavior. Path
gradient admission keeps its 2..128 original boundary count, one color per point
and exactly one blend/preset inventory without inventing extra finite checks.
Hatch sets retain contiguous signed offsets/counts, unit tangent tolerance, per-
family spacing/width checks, original ordered period arithmetic, drawn-zero dots
and all-or-nothing snapshot publication. Single/cross hatch and 64-bit tile retain
their independent source identities; negative tile coordinates wrap by original
bit masking. No noise, gradient or hatch pixel loop is added on the CPU.

Pens retain exact -1 hairlines, normal/fixed expansion, all four join/cap values,
independent clipped-Miter and WPF flags, constructor-only miter/offset normalization
and raw later assignments. WPF admission excludes hairline/fixed/MiterOrBevel
without changing ordinary policies. Dash snapshots use immutable Arc storage;
Some(empty) property input stays distinct from the span setter's empty-to-None.
With-brush snapshots share the actual immutable dash generation and copy every
other parameter. Rust borrows expose read-only arrays instead of managed defensive
getter copies. Material generations are immutable Arc owners; mutable framework
brush notifications/capture and retained command interception must be connected
at their original boundary before claiming live-resource behavior.

Remaining gates include complete source path/token/error/primitive/flag records;
all matrix/arc branches and original default-runtime float bits; source dash
construction, two-pass final states, rejection and nonprogress cases; all material
constructor payloads/errors and immutable ownership; exact GPU segment/material
lowering; ordinary/signed/Boolean curves and all source cap/join/dash frames;
SVG XML/reference/gradient/cache and final color glyph integration; cold/warm CPU
and GPU throughput, bounded allocations and compiler SIMD behavior; final browser/
native pixels; and every original source/provider/application/package gate.
SVG XML/cache, Boolean solvers, stroke expansion, material rendering, images,
clips/layers/masks/effects and the rest of the full-stack port remain unfinished.

## Original SVG color layer parsing and font cache (2026-11-09)

The next connected batch uses the shared vector inputs to port SVG glyph layers
and the actual selected-font cache. Native/browser-target strict Clippy, CPU-only
compilation, formatting and whitespace are the current checks. Broad source/XML/
runtime/pixel/performance/package validation remains deferred as requested.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Text/OpenTypeSvgGlyphParser.cs` | `3231d7cb780b162238ce7a7b8cac9094059f6f30a3e2277a649589a2cbd5e28c` | Complete original ID/ancestor/reference traversal, supported shapes, source state, material/stop/coordinate/color parsing and transform-list policy |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` | FontColorLayer, HasColorLayers/GetColorLayers COLR precedence and selected-font GetSvgColorLayers positive/negative cache |
| `src/ProGPU.Text/GlyphAtlas.cs` | `5fb66ddeba150535a48c760b18a584dd161af38c6fc3ed46accc8e35f3f64582` | Original HasColorLayers route now includes successful SVG, retaining the vector sentinel before ordinary outlines |

XML DOM parsing consumes the normal locked `roxmltree` 0.21.2 dependency, licensed
MIT OR Apache-2.0 with MSRV 1.60. Only its std feature is enabled, reusing the
existing memchr lock entry; positions/default features are disabled. Package
metadata was reviewed with cargo info. Public documentation only was consulted:
[DOM API](https://docs.rs/roxmltree/latest/roxmltree/struct.Document.html),
[node traversal](https://docs.rs/roxmltree/latest/roxmltree/struct.Node.html) and
[parser options](https://docs.rs/roxmltree/latest/roxmltree/struct.ParsingOptions.html).
No dependency implementation or external SVG renderer was read/copied. The DOM
does not implement ProGPU geometry/material policies.

DTD processing and external entity resolution are disabled. Because the DOM's
documented policy permits an empty DTD even when allow_dtd is false, an owned
lexical declaration check preserves the original unconditional Prohibit rule;
comments, CDATA and processing instructions do not become false declarations.
The original 16 Mi UTF-16-character cap is retained independently of the earlier
encoded/decoded byte cap. XML errors become the original negative cached result;
source reads outside original guards and allocation failures remain errors.
Exact XML normalization, namespace/name/attribute, character/entity, invalid-input
and resource behavior remain independent differential gates.

Traversal retains the first nonempty ordinal ID, exact glyph-number selection,
root-to-parent inherited state, per-element local-before-parent composition,
source order and the original depth >64 stop. Definitions render only through
their admitted routes. Use resolves the first local-name href and retains actual
node identity in its active-reference set, removing it on success or error. No
external resource loading is introduced. Supported original path/circle/ellipse/
rect/polygon geometry shares the owned vector parser/builders. Rect ignores rx/ry
as the original does; polygon retains its implicit final edge. Source fill-rule
handling and shape-level parse rejection stay distinct from inherited state.

Fill, opacity and fill-opacity retain original inheritance/override/multiplication
order. Solid color names, short/long hex and the original var fallback spelling
remain limited to the source inventory. Gradient references retain actual units-
per-em coordinate defaults, percent arithmetic, original transformed endpoints/
centers/radii, independent spread and stable stop ordering. NaNs compare before
ordinary offsets and equal signed zeros retain source order. Shape/stop alpha
is multiplied exactly at the original stages; brush opacity stays one. Unsupported
SVG families/styles are ignored under the original parser policy, not substituted
with a general external renderer.

Complete 4x4 row-vector composition retains all components and the original
transform-list multiplication order. Public System.Numerics
[Z-rotation contract](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.matrix4x4.createrotationz?view=net-10.0)
was consulted without reading runtime implementation. Rotation/skew use ordinary
host trigonometry with source degree conversion and center composition. This is
explicitly **not original primitive arithmetic proof**: default-runtime matrix,
Vector2 transform, Sin/Cos/Tan, FMA/SIMD, special angles and nonfinite results must
be compared against the original runtime before source admission. No source
transform-history or platform/package qualification flag is enabled. Numeric
parsing/casing and XML library behavior also remain unqualified rather than
relabelled as exact BCL equivalence.

FontColorLayer retains independent glyph/color/optional geometry/optional brush/
SVG-coordinate fields, including zero SVG glyph/color defaults. SVG geometry and
materials own immutable Arc generations; no DOM/string borrow escapes parsing.
Cache scope follows the exact selected immutable font state, with new caches for
variation/palette construction and shared caches for clones of that state. The
original lock encloses lookup, parse and publication; absent SVG tables create no
entries, negative results are retained, and failed reads/allocations publish none.
There is no back-reference cycle into the font root. Legacy COLR still returns
before SVG, including a matching empty layer list; HasColorLayers still recognizes
that empty base. Successful SVG now takes the original atlas vector sentinel,
while missing/invalid SVG can continue to ordinary outlines. Final vector color-
layer drawing remains unfinished; the sentinel does not claim atlas coverage.

Remaining controls include full original XML/UTF-16/resource errors, ID order,
references/cycles/depth/ancestor state, complete geometry/brush/stop bytes, numeric
and named-transform arithmetic, real-font SVG inventories, cache identity and
negative reuse, variation/palette/thread/retention behavior, source atlas routing,
actual final color-layer/text pixels on native/browser backends, allocation and
cold/warm performance, and all original source/provider/application/package gates.

## Original deferred and GPU Boolean geometry (2026-11-09)

This batch connects the original Boolean query, compilation and reconstruction
algorithms to the two canonical GPU kernels. The implementation-first workflow
continues: native/browser-target strict Clippy, CPU-only compilation, formatting
and offline Naga shader validation are the current checks. No new runtime GPU,
browser, differential, performance, profiling or package qualification is claimed.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Vector/PathGeometry.cs` | `a246ffa979b6e21f78a48b84017720d70ece894b61eb976909105da5d365bb88` | Deferred Boolean operation/query kinds, original cached bounds/emptiness semantics and transformed operands retaining query classification |
| `src/ProGPU.Vector/RoundedRectanglePathGeometry.cs` | `6e917cc5e3a6c4efe39a08d6a61ff7741079ee81c506eaa9c911c06304d57ff4` | Complete canonical contour recognition, paired corner radii, convex/center/radius containment, bounds ownership and partial-corner classification |
| `src/ProGPU.Vector/PathOpGeometrySolver.cs` | `548e444604b434000f857400281efc5eb709d0382d0dec5a519d10133b678958` | Deferred classification, output fill rules, immediate results, rational rejection, source records/segments, bounded output sizing, two GPU passes/readback, first-unused forward/reverse reconstruction and complete deferred resolution |
| `src/ProGPU.Vector/PathAtlas.cs` | `8b0c65142b0156f505374f1d20a6feff3eea1ed7bec23a1b8ce6f405e7414ccd` | Original 32-byte GpuPathRecord and explicit managed-to-shader fill encoding only; complete managed PathAtlas remains separate |
| `src/ProGPU.Backend/Shaders/PathOpGeometry.wgsl` | `a47dd7e334686e425b5c5bb24f16d106678578ad9529f74b185976d9f4b75f10` | Unchanged original classification/splitting compute program |
| `src/ProGPU.Backend/Shaders/PathOpRecordFinalizer.wgsl` | `a90fc71f62a6b40274bba6556d26df777ab2781ab36eef416635208b087c1a7d` | Unchanged ordered record finalizer |

Only original ProGPU-owned source was read/ported. Canonical shader files are
included directly; no shader math, roots, atomic ordering, compiler defaults or
driver policy changes are introduced. The named shader inventory now contains
eight modules, including both original Boolean programs. Existing native/browser
module-validation loops consume that inventory; execution of the expanded browser
gate is deferred. No external implementation or new dependency is introduced.

Rounded contour recognition preserves the complete original 4..8-segment walk,
rotation of the first admitted top line, side directions, optional corners,
radius/rotation/sweep/large-arc rejection, pair-sum limits and original scale-based
tolerance. Containment keeps its four-corner convex proof and separate coincident-
center/radius route. Arbitrary curved boundaries are not replaced by bounds.
All source finite, degenerate, tiny-radius, rotated-start and reversed inventories
remain necessary differential controls.

Deferred paths own actual immutable Arc operands and retain all five operations
and seven query kinds. Bounds-only ownership and exact operand results remain
distinct; unknown/empty/nonempty is not collapsed into a boolean guess. Original
empty, contained, strictly contained, disjoint and missing-bounds branch ordering
is retained. Transforming an expression transforms both operands and preserves
its original query kind without reclassification, as the source does. Ordinary
figures remain an independent inventory; primitive recognizers reject deferred
paths. Rust immutable sharing replaces managed mutable snapshot markers; invalid
null/cyclic object graphs and direct raw query-state mutation are not exposed.
Managed object-pool reset/sharing-flag behavior and allocator equivalence remain
separate ownership/performance work, not silently qualified by Arc use.

Immediate reconstruction preserves original intersection/union operand selection,
contained difference's EvenOdd contour pair, empty operand routes and output fill
rules. Rational segments reject before these routes, retaining the solver's
original whole-input gate. Ordinary copies retain every figure/segment flag and
cap override. Public compile_boolean_input applies that same solver admission
explicitly; it does not reproduce the old standalone CompilePath utility's silent
skip of unsupported rational records. Combined operands require resolution first.

Solver compilation retains source figure ordering and its separate inclusion
policy: even unfilled/unstroked figures participate as in the original solver.
Move-only figures emit no records. Quadratic/cubic controls, endpoints, implicit
closure, source arc center/radii/parameter bits and degenerate line fallback are
preserved. Bounds use original control/analytic-arc arithmetic, including original
nonfinite propagation, rather than the ordinary finite envelope helper. Exact
native wire sizes are 32-byte records, 48-byte segments and 16-byte uniforms/header;
fill encoding is explicitly converted from the managed enum.

GPU output capacity retains max(64,15*(A+B)); overflow and actual device binding/
buffer/workgroup limits reject before GPU publication. The geometry dispatch uses
the original ceil((A+B)/64) groups, followed by one finalizer pass, then one copy of
the complete output capacity. The original output counter alone is explicitly
reset, and readback clamps it to the original capacity. Finalizer record semantics
remain independent of the CPU result's original output fill rule. Actual upload,
submission, pass, readback-capacity and input-segment counters are exposed.

BooleanSolver owns its actual wgpu device, queue and pipelines instead of a global
Current-context/weak-table lookup. Automatic and forced native compute use the
canonical path; unsupported fragment/CPU choices fail explicitly because no
equivalent atomic/storage implementation is ported. Input buffers use wgpu's
normal initialized-buffer upload boundary. Validation scopes cover pipeline and
work creation, retaining default compilation options. GPU commands keep their real
buffer/bind-group owners through submission; Rust scope exits do not destroy live
submitted input/output resources.

BooleanWork owns the actual staging buffer and callback state. Browser completion
yields to WebGPU; native async callers must drive Device::poll from their host.
The separate blocking native method uses the original five-second wait bound.
Cancellation clears the stored wake owner and unmaps/cancels only that staging
buffer; Drop does not poll, flush, acquire a rendering lock or explicitly destroy
submitted resources. Callback wakeups run outside the signal lock, and no borrowed
mapped range escapes reconstruction. Mapping/validation failures publish no
geometry. Full cancellation/device-loss/map-deadline controls remain mandatory.

Contour reconstruction preserves the original first-unused inventory scan,
forward-endpoint before reversed-endpoint selection, strict squared distance <0.25,
arc reversal parameter updates and original closure check inside the continuation
loop. It does not discard small or signed-area-cancelled lobes. Unknown raw segment
kinds retain the original no-append behavior in the standalone utility; admitted
solver inputs remain kinds 0..3. Small usage inventories stay on stack, larger
ones use owned temporary storage; no pool-rented bytes escape. CPU reconstruction
retains the original O(n²) dependent walk, while pairwise classification remains
on the GPU. Iterative deferred materialization preserves A-before-B source order
without recursive async futures or cached-query shortcuts.

Required consolidated controls include every original query/empty/bounds/fill
branch, full canonical rounded contour admission, source transport bytes and
default-runtime float semantics, all immediate/rational/degenerate cases, all five
operations on line/curve/arc inventories, real GPU output/counter/finalizer order,
capacity overflow and limits, topology/reversal/closure including tiny/cancelled
lobes, map cancellation/errors/native deadlines, source lifetime/owner isolation,
browser/native pixels, cold/warm throughput and allocations, retained painter
integration, and original source/provider/application/package qualification.
Stroke expansion, material compilation, full retained path atlas/Boolean paint
programs, final color/text/image/layer/effect rendering and release gates remain
unfinished. Offline shader validation is not runtime execution evidence.

## Original stroke geometry and geometric hit queries (2026-11-09)

This connected implementation batch directly ports the original bounded cap/join
writers, line-only retained stroke traversal, and geometric hit-query utilities.
It follows the requested implementation-first sequence: compile/static checks
now, complete original differential/browser/performance qualification after stack
assembly. No external implementation, new dependency, shader change or compiler
policy change is introduced.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Vector/StrokeJoinGeometry.cs` | `6fb7218ec1d12df2b9b1fb9c0777633f1ce74eec90260fd23c6e8ff85cad86ec` | All allocating/writer line/directional joins, generic/WPF/typed-pen policy, miter clipping, reversal, bounded round fans and destination admission |
| `src/ProGPU.Vector/StrokeCapGeometry.cs` | `c47564c76c3eedaf4d759c532f273f1d66b92ef5f70591dbab558d38ca79df81` | All line/directional flat, square, round and triangle caps, exact fan order and capacity preflight |
| `src/ProGPU.Vector/StrokePathGeometry.cs` | `4cf9ae2d680d8eff9f44d1ee187877b16623ebaf194ea10c918da5441a375ea7` | Complete original line-only solid/dashed traversal, run/gap/closed seam state, delayed caps, bodies, shared joins and separate geometry/hit sinks |
| `src/ProGPU.Vector/LineGeometryHitTesting.cs` | `e623cd5508fedbeb924446398970c1caad16693afc7460d8ab418820951fcd3b` | Complete fill/stroke and cap queries, independent degenerate rule, original triangle barycentrics and tolerance |
| `src/ProGPU.Vector/RectangleGeometryHitTesting.cs` | `107814828900c8b3181bc189c084f63b22de3de41911aa6daf8b4632ff599432` | Complete rounded fill and outer-minus-inner stroke query, original signed-radius normalization and boundary comparisons |
| `src/ProGPU.Vector/EllipseGeometryHitTesting.cs` | `8769b0c00a5d731f01bb9262eb6923533fb745f1c5eb9db34b68d723aa7898d7` | Complete fill/stroke queries, original axis/finite admission and empty-inner behavior |
| `src/ProGPU.Vector/PathGeometryHitTesting.cs` | `f29d707f446672efb9e57d657a210febdd724331fe87c2faddff84c63de0b831` | Complete filled-figure sampling, all six segment families, rational weight bounds, tolerance/boundary/even-odd/signed-winding and deferred operand queries |

`stroke.rs` retains the original eight-triangle maximum and 0.0001 epsilon.
Normalization divides each original component by the computed length. Tangents
remain actual directional input; line wrappers subtract their original endpoints.
Generic smooth suppression, WPF smooth-to-Round, WPF exact-reversal extensions,
MiterOrBevel over-limit bevel and independent explicit clipped-Miter policy remain
separate. Typed pen admission rejects the same WPF fixed/hairline/MiterOrBevel
combinations before geometry writes. Raw policy remains on the pen even when its
current join ignores clipping. Typed Rust enums exclude undefined raw values;
this API does not reproduce the cap writer's accidental unknown-enum Round route.

Full destination capacity is checked before any triangle write, including round
and reversal fans. Actual outer points, intersection numerator/denominator order,
miter comparison, clip points and triangle ordering remain original. Allocating
round joins intentionally retain `Distance(joinPoint, previousOuterPoint)` as their
radius, while the writer retains half thickness; they can differ after large-
coordinate rounding. The bounded common writer selects this source distinction
explicitly. Rust uses one stack inventory and one owned final allocation for the
allocating API. Default-runtime SIMD/FMA, MathF/libm bits and allocator/performance
equivalence remain unqualified; host trigonometry is not source primitive proof.

`stroke_path.rs` keeps the original per-figure finite/line-only admission and
one-unit hairline sentinel. It does not invent curve flattening or apply an
implicit transform. The owning API must supply its original flattening tolerance
and source/device expansion frame. Original figure cap overrides are retained in
the data model but are not consumed by this utility, matching its source.
Unstroked segments break runs, dashed gaps reset the original initial phase, and
all source cap/join/body ordering, delayed start caps and closing-edge decisions
are retained. Generic widening ignores smooth metadata; WPF widening passes it
to the shared typed writer. No rounded internal bodies are added behind joins.

The original dash constructor and ordered NormalizeState/Advance arithmetic are
shared with the earlier analytic dash port. The only nonprogress extension is
explicit rejection after an initial partial interval and one whole interval
cycle fail to advance the floating distance; no epsilon is added and the phase
is not perturbed. Existing malformed infinite phase normalization likewise fails
explicitly rather than looping. These are documented rejection differences,
not passing original-source behavior evidence.

The geometry sink preserves original triangle/quad area rejection, winding
normalization and one closed filled figure per emitted polygon. The hit sink
keeps its independent signed-edge epsilon and early-hit short circuit; it is not
replaced by querying the normalized polygon inventory. `WidenedPath { path,
complete }` exposes original partial output when a later figure rejects. Callers
must inspect `complete` before publication. `contains_stroke` returns None for
source rejection and Some(false) for an admitted miss. Allocation errors and
invalid pen policies return errors; all temporary runs/triangles have bounded
stack ownership. No renderer mutation occurs in these utilities.

`hit_test.rs` retains primitive-specific relative tolerance extents, signed-radius
normalization, positive-width/finite gates, inner-boundary exclusion and the
line helper's independent degenerate cap rule. Path queries skip unfilled figures
but retain the original implicit closure regardless of `is_closed`, and do not
filter by stroke participation. They use 16 quadratic or 24 cubic samples and the
original bounded arc flattening, original adjacent-point deduplication, rational
weight/coordinate scale limits, boundary tolerance and signed winding. The hit
cubic evaluator deliberately keeps the original precomputed `tt`/`uu` grouping;
the separate analytic curve/dash polynomial is not substituted. These samples
are the original input-query contract, never a GPU raster coverage replacement.

Deferred fill queries retain the original 256-level gate and visit A then B even
when Boolean short circuit could determine the result. They do not infer hit
geometry from cached bounds/query kinds. Typed immutable ownership excludes raw
null/cyclic object graphs; winding accumulation explicitly preserves unchecked
32-bit wrapping. Source query failure stays distinct from a valid miss.

Required consolidated controls include exact ordered triangles and capacity
rollback; all caps/joins/limits/reversals/smooth metadata; large coordinates,
nonfinite/degenerate/source-float cases; normal/fixed/hairline admission and actual
owning frames; solid/dashed/gap/closed/reversed inventories, phase and delayed caps;
source partial rejection and early-hit order; primitive boundaries and tolerance;
all polynomial/rational/arc sampling and weight limits; Boolean operand/depth
queries; source lifetime, allocations and throughput; shared GPU paint/material
bounds and final full-frame pixels; and every original Windows/source/provider/
application/package gate. None is qualified by successful compilation. Final
material compilation, curved owning-API stroke lowering, retained painter/atlas
integration, color/text/image/layer/effect drawing and release remain unfinished.

## Original material registration and retained GPU integration (2026-11-09)

This batch connects the original non-image brush records to canonical GPU
material evaluation and the existing ordered analytic/path renderer. It follows
the user's implementation-first sequence; native/WASM lint, CPU compilation and
JavaScript syntax are checked now, while source bytes, pixels and performance
remain unqualified. No shader, driver/compiler default or dependency changes.

| Original repository source | SHA-256 | Scope |
| --- | --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` | GpuBrush/GpuGradientStop layout; complete non-image RegisterBrush, equality/auxiliary tails, gradient/path/hatch registration, all noise normalization/table/frequency/stitch helpers and cache limits; source analytic center/path-coordinate material payload |
| `src/ProGPU.Backend/Shaders/RegisteredMaterialCommon.wgsl` | `b71e8b365539be20ff1f6a0a57e1ccdc364e2abec10a777e1fc436761881b10a` | Existing unchanged shared evaluator and original material wire layouts |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` | Existing unchanged analytic/path source brush coordinates, derivatives, solid-color selection and fragment evaluation |

`vector/material.rs` preserves exact 256-byte brushes and 32-byte auxiliary
records with explicit zero padding and compile-time offsets. All eleven original
non-image kinds retain their actual wire values (hatch set 8, tile 9 and path
gradient 10). Brush opacity multiplies original semantic opacity once; no clamp,
premultiplication, coordinate inverse or stop sorting is introduced. Coordinate
transport selects the original six XY components from the retained full matrix.
Independent spread/interpolation and conical outside-color flags remain exact.

Registration preserves source first-equal order, ordinary float equality, inline
colors/offsets, auxiliary equality only for the original six record-backed kinds,
and the tile high word's independent equality. Candidate records are trimmed
after duplicate selection or brush capacity fallback. Limits remain 8,192 brushes
and 65,536 shared records. Ordinary gradients retain the source available-stop
truncation and inline missing-offset defaults. Hatch sets retain all-or-nothing
four-record packing per family, with actual signed dashes and zero-filled unused
slots. Failed hatch capacity returns the original zero index. Path gradients keep
whole-point/color/curve/affine admission before append, paired point/color records,
independent preset versus blend curves and original transparent-solid fallback.
The original affine check deliberately does not inspect M33/M43; no stronger generic
matrix gate is substituted. Typed enums and owned arrays exclude undefined enum
values and null mutable stop arrays; empty arrays retain their actual source route.

Perlin registration retains octave clamping, normalized seed identity, original
tile frequency rounding/stitch data and selection of the original shader fallback
when 512 table records cannot fit. Deduplication precedes table lookup exactly as
in the source. The 16-seed cache clears as a whole only before a missing-seed insert
at capacity, and survives frame-table clear. The original dependent random sequence,
four-channel inventory, full-256 shuffle selection, permutation, component division
and 16-bit gradient quantization remain ordered. Small scratch arrays are stack
owned; final tables have immutable shared ownership. Noise evaluation stays on the
GPU, with no CPU pixel generation or texture approximation.

Seed normalization retains source double truncation, explicit Int32 boundary
branches, signed remainder and final integer range. Rust's NaN-to-zero integer
cast matches the current [.NET documented conversion contract](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/9.0/fp-to-integer).
This public contract reference is not runtime implementation source or execution
evidence. Every original/default-runtime seed, Vector2 length, arithmetic and
quantized-table byte control remains mandatory.

`MaterialTable::snapshot` owns immutable Arc inventories, separate from mutable
frame scratch and retained seed cache. Later clear/reuse cannot mutate an existing
page. Checked allocation errors roll back the current request's record tail;
source capacity behavior itself stays distinct from allocation failure. This
ownership differs from mutable managed list/pool mechanics and requires complete
allocation/reuse/performance controls before equivalence claims.

Scene recording retains actual immutable source brushes and semantic opacity.
Analytic material calls require the actual source center, avoiding reconstruction
from rounded extents or transformed positions; original solid colors and non-solid
center payloads remain distinct. Path materials keep the original white/solid
color payload and use retained source coordinates, never atlas UVs. Material
identity is independent of mask raster identity, so differently painted uses can
share original coverage. The existing painter order and path phase/sampling remain.
Mixed legacy/native vertex-color scenes reserve their existing unit-opacity solid
binding before material registration, so table exhaustion cannot reinterpret old
draws through a non-solid first record. All-material scenes preserve the original
registration order. This explicit integration reservation is not a claim of byte-
identical whole-managed-compositor pages. Legacy-only scenes keep shared default
bindings and incur no material uploads.

CPU callers can reuse `compile_with_materials`; renderer-owned table/cache storage
is likewise reused between preparations. Compilation failures may leave scratch
records but publish no scene. Frozen CPU pages can be prepared on separate actual
devices because they own only records. GPU preparation validates exact integer
material indices and actual device buffer/binding limits before optional path GPU
work, creates independent immutable brush/record buffers and binds them with the
actual frame. A single zero record satisfies an empty auxiliary binding. The
prepared bind group retains the buffers through submission; no explicit destroy,
poll, finalizer lock or global-context lookup is added. Later preparation cannot
rewrite an earlier scene's material storage. Actual material bytes include the
empty binding placeholder; brush/record counts exclude it. Replay still uses one
ordered indexed draw with no material upload or CPU readback.

The shared authored material gallery covers all eleven non-image kinds, semantic
opacity, transformed source coordinates, mixed legacy drawing and repeated painted
paths. winit selects it with `--materials`; the browser uses `?scene=materials`.
Browser selection takes effect on the next serialized resize, retaining the old
prepared generation until replacement. Original default sample/gate inputs remain.
Native/WASM compilation does not prove this new gallery rendered correctly; it is
ready for the consolidated runtime pass, not a substituted source oracle.

Required gates include complete original brush/auxiliary bytes and first-equal
identity, raw/nonfinite/empty/signed-zero inputs, all capacity/fallback/truncation
boundaries, path-gradient admission, every hatch/tile/noise record, seed cache
clear/reuse, original default-runtime float behavior, material/source coordinate
frames and opacity, all gradient duplicates/spreads/color spaces, analytic/path
pixels, CPU/GPU generation retention and cancellation/failure, actual cold/warm
uploads, native/browser DPI/resize/loss, allocator and throughput/frame baselines,
and original source/provider/application/package qualification. Image/effect and
intercepted brushes, full retained stroke/path/SVG/text painting, layers/masks/
compositing, scene cache/archives and platform/release gates remain unfinished.

## Original fill compilation and retained Boolean painting (2026-11-09)

This implementation batch ports original PathAtlas source compilation and
connects ordinary/direct/program fill inputs to the retained renderer. It follows
the user's implementation-first sequence: compile and static checks now, complete
source bytes/pixels/ownership/performance validation after stack assembly. No
original shader, compiler option, driver policy or dependency is changed.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Vector/PathAtlas.cs` | `8b0c65142b0156f505374f1d20a6feff3eea1ed7bec23a1b8ce6f405e7414ccd` | CompilePath/CompileFillPath/CompileStrokeQuery core, original bounds/weights/closure/query metadata, complete hit-definition admission, direct operands and bounded postfix programs; original record/segment/token relocation |
| `src/ProGPU.Vector/PathQueryFigure.cs` | `4477463bc7dc81493ea783d40f9f17dedd3207782143338d63852db0dda0c4db` | Original move point, first/count, closed/filled metadata; Rust indices are CPU-only usize values, not a new native ABI |
| `src/ProGPU.Backend/Shaders/PathRasterizer.wgsl` | `b5e6df242fb18d0799ac326de0d4763a0c5cedfd7a5f4f9be9c4045eccc0a362` | Existing unchanged operation-zero and ordinary Boolean entry points, four-pixel packing and 16-by-16 workgroups |
| `src/ProGPU.Backend/Shaders/PathRasterizerCommon.wgsl` | `b7f0599f533e799c44b11ba1d565e055bd971b80999648511128ad7624002601` | Existing unchanged per-operand fill rules, mask Boolean arithmetic and postfix evaluator |

The earlier cross-engine architecture research remains the design basis: owned
CPU source generations and target/device GPU resources stay separate, retained
replay does not recompile geometry, and coverage evaluation stays on the GPU.
No foreign implementation was consulted or copied. This batch does not replace
the original ProGPU algorithms with another renderer or a CPU Boolean rasterizer.

`vector/fill.rs` retains ordinary versus fill-only figure admission. Fill closes
every admitted nonempty figure regardless of its original closed bit; ordinary
compilation closes only explicitly closed figures. Unstroked source edges still
participate in fill. Original line, quadratic, cubic, rational quadratic/cubic
and endpoint-arc payloads remain analytic. Rational weights preserve the original
positive/finite and coordinate-scale bounds, with the original nested float
maxima and double limit arithmetic. CPU source compilation does not silently
sanitize nonfinite coordinates; the existing native GPU admission remains a
separate gate. Bounds retain original control points and analytic arc extrema,
NaN/signed-zero minimum/maximum policy, and unchanged arc fallback. Failed
center conversion emits a line only for distinct endpoints, except that stroke
queries retain the original degenerate segment and flags.

Stroke queries preserve every figure slot, including move-only/unfilled figures,
source stroke/smooth flags, original end order and the independent implicit-edge
flag of one. Combined paths and per-figure cap overrides reject before compiling
query output. The original one-million-entry admission budget and complete
definition depth limit of 64 remain. Typed enums/immutable operands exclude raw
unknown values and null/cyclic source graphs; this is not native wire admission.
Complete-definition checks retain all original finite points and arc parameters,
and remain independent of fill pixels or source/provider qualification.

Fill-compatible cached exact-operand results retain the original fill-rule or
one-figure check. Cached Empty compiles to no record. Other deferred compilation
returns UnsupportedExecution to synchronous callers. The optional asynchronous
`BooleanSolver::prepare_raster_path` follows the same exact-operand traversal
before invoking original A-before-B reconstruction when required; native hosts
continue to drive device polling for actual readbacks. No failed program is
replaced by a CPU shape approximation. Source invalid-rational compilation
retains the original empty fill-cache result; capacity failures and final native
GPU admission errors are not swallowed as empty results.

Direct ordinary operand pairs retain separate records, bounds and fill rules,
with operation values 1..5. Other eligible expressions compile in original
A-before-B postfix order, with at most 63 instructions and 16 live values.
Cached Empty nodes emit the original empty token; cached exact-operand nodes
retain the original program shortcut, independent of the ordinary compiler's
fill-rule check. Empty ordinary leaves still reject program compilation.
Duplicate leaf occurrences keep separate ordered records and segment copies.
Program overflow retains the explicit reconstruction route. An iterative task
stack avoids recursive Rust call frames without inventing a source depth limit.

Immutable `Path` generations own the compiled records/tokens and ordered segment
inventory. Existing raw Path construction and its finite/weight/bounds/one-million
segment gate remain unchanged. Compiler-created programs additionally validate
their complete record ranges, token tags/zeros, operand indices, operation values
and final stack before publication. Source record bytes remain unchanged. During
batch encoding, only segment offsets and absolute uniform record/program bases
are rebased; leaf tokens retain their relative indices. All request/buffer/device
checks precede GPU resource creation. The existing native extent grouping,
aligned uniform ranges, packed coverage rows and single submission remain;
this is not a claim of byte-identical whole-managed-atlas staging allocation.

The existing single-path pipeline remains the route for ordinary-only batches.
The independent original `cs_main_ordinary` pipeline is created lazily through
`prepare_boolean`; the retained renderer requests it only when a batch contains
Boolean input. Mixed batches use that evaluator, preserving source painter order
in the later retained draw. There is no signed-winding kernel substitution or
linear classification based on bounds/first segments. Existing default compiler
options, R8 ownership, coverage/material identity, target phase and submission
retirement remain. No draw-time CPU coverage, per-path readback or Drop poll is
introduced. Unsupported forced execution preferences remain unsupported.

The shared authored material gallery now fills an open figure containing all six
source segment families, then paints direct subtraction, a nested Boolean
program and a repeated ordinary generation. It exercises integration routes for
the upcoming native/browser run; it is not an original pixel oracle, runtime
receipt or performance result. Default sample/gate scenes are unchanged.

Required consolidated controls include original filled/unfilled/open/closed,
move-only/degenerate, stroke-gap/smooth/cap, rational-limit, arc-fallback and raw
coordinate bytes; source default-runtime arithmetic; complete query budgets;
all cached Boolean classifications/fill-rule combinations; every direct operator,
empty token, duplicate leaf, program count/stack boundary and reconstruction
fallback; relocation in mixed-size batches; cold/warm GPU coverage/material
pixels, generation ownership and cancellation; native/browser DPI/resize/loss;
allocator, throughput, frame and Instruments baselines; and all original source,
provider, application, platform and package gates. Full managed compiled-path
caching, atlas growth/eviction/recovery, signed programs, curved-stroke paint,
clipping/layers/effects/images, final SVG/text painting and release qualification
remain unfinished.

## Original retained solid-stroke painting (2026-11-09)

This batch connects original managed solid-path stroke compilation to the Rust
retained mesh/material renderer. It implements the four painted body families
and their local/affine/fixed/hairline frames together. Complete source byte,
default-runtime, pixel, lifetime and performance validation remains deferred
under the user's implementation-first sequence. Native/WASM/CPU-only strict
lint and formatting are checked now; no shader/compiler/driver defaults change.

| Original repository source | SHA-256 | Scope |
| --- | --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` | Solid CompilePathCommand traversal; stroke-state/thickness admission; tangent selection; analytic/affine line, Bezier and arc vertices; regular/affine/device/degenerate caps/joins; topology masks and thickness encoding |
| `src/ProGPU.Vector/TransformMetrics.cs` | `12271082b95c438ed47d041e961e757e5a0b94bc56334a978f5acc2f17a838de` | Original double maximum singular scale and abs(det)/maximum minimum, float narrowing and positive-finite admission |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` | Existing unchanged analytic/Bezier/arc, topology triangle/strip, fixed/hairline and affine round-cap consumers |

Earlier original StrokeJoinGeometry, StrokeCapGeometry, ArcSegmentGeometry and
BezierSegmentGeometry ports remain shared with their existing provenance. The
cross-engine retained-geometry/resource-domain research recorded above continues
to govern ownership and GPU realization. No foreign implementation was consulted
or copied; no alternate stroke algorithm replaces the original source routes.

`StrokeDraw` owns source path/pen generations, raw semantic opacity, aliased policy
and a complete `StrokeFrame`. Actual final transforms remain separate from original
command transforms used only for legacy pre-scaled width recovery. New recorders
default to source-local widths. None represents the original absent/default
command-transform sentinel; raw source adapters must preserve that distinction.
Matrix input does not witness named transform construction/history. Join policy
validates before width admission. Invalid widths/frames retain no-paint behavior
without material registration; dashed/deferred inputs require separate owning-API
preparation and are not silently painted as solid or empty.

Managed `Matrix4::stroke_scales` deliberately remains separate from native
`geometry::Affine::scales`: the minimum uses abs(det)/maximum, positive-finite
admission and no added small-scale epsilon. Full affine admission keeps the source
XY coefficients, float determinant and residual checks, excluding original
uninspected M33/M43 fields. Hairlines retain the exact -1 sentinel and width basis
one. Fixed positive strokes expand transformed centerlines in device space.
Non-uniform normal strokes expand the original outline before transformation.
Conservative local width checks and fixed encoding retain BitIncrement(1).
Legacy fixed joins retain the pen width where the source descriptor uses it,
independently of recovered body width.

Figure traversal preserves cap overrides, source order, unstroked gap resets,
open-only gap/end caps, degenerate candidates and both closing/seam join routes.
Smooth metadata retains first-edge seam ownership. Tangents select original
controls in order. Compositor directions subtract transformed zero from transformed
direction, preserving translation cancellation rather than substituting linear
coefficients. Generic joins retain synthesized previous/next endpoint subtraction;
WPF joins retain direct tangent transport. The original solid compositor emits
no rational stroke body: it still handles tangents/caps/joins, advances the endpoint
and clears continuation. This source behavior remains; rational fill support is
not relabeled as rational stroke coverage.

Ordinary line bodies preserve four vertices, signed corner payloads and triangle
order. Non-affine quadratic/cubic bodies retain 50 descriptors and 24 strip sections,
with the actual global mesh base embedded in the shader payload. Cubic endpoint
coordinates remain independent color fields. Affine Beziers use original double
device-curvature counts, 0.25 error constant, 24..1024 bounds, polynomial/tangent
expressions and chord fallback. Sections retain their first/interior/last strip
kind even when another section is rejected. Analytic arcs retain transformed axes,
determinant admission, angles, padded bounds and fallback extent. Affine arcs keep
pi/24 bounded sections, signed tangents and exact final endpoint. Only the original
failed-analytic route selects default arc flattening and line emission.

Miter, clipped Miter, MiterOrBevel and WPF reversal/smooth policies remain separate.
Round caps use the original affine semicircle shader. Square/triangle caps and
join fans retain exterior/owned-internal masks, winding, area rejection and 1.5
raster padding. Ordinary triangles receive no extra identity transform; affine
triangles transform actual emitted points. Fixed/hairline adornments retain their
four-descriptor form, global ordinals, full-cap flags, independent miter clipping
and original smooth suppression. Equal round/square degenerate endpoints retain
whole-shape handling, source brush coordinates and local/device expansion; mixed
endpoints keep paired cap calls. Figure cap overrides do not replace the pen's
independent degenerate policy.

Scene recording remains O(1), retaining immutable Arc generations. Compilation
appends strokes at their actual painter position in the same mesh. Descriptor
ordinals include earlier analytic/path commands. Checked indexed append validates
relative indices and reserves both buffers before writing. Allocation failure
discards the candidate; reusable material scratch cannot mutate published pages.
Existing finite vertex/device-buffer admission precedes upload. Prepared replay
remains one ordered indexed draw without geometry/material uploads. No finalizer
lock, explicit GPU destruction, polling, CPU coverage or readback is introduced.

The shared native/browser material gallery adds ordinary, non-uniform, fixed-width
and aliased-hairline curves with cap/smooth-join metadata. It is an authored scene
ready for consolidated runtime execution, not a source oracle or performance
result. Default sample/gate scenes remain unchanged.

Remaining gates include original descriptor/index bytes and mesh-base relocation,
default-runtime SIMD/float behavior, tiny/huge/nonfinite widths/matrices, legacy
frames, fixed/hairline physical DPI, gap/seam/cap overrides, every join/reversal/
miter/smooth case, topology ownership, curve thresholds/fallbacks, degenerate
policies/material coordinates, painter order, lifetime/cancellation, native/browser
pixels, allocator/frame/Instruments evidence and all source/provider/application/
package qualification. Dashed-path preparation and linear dash coverage, full
managed atlas caching/recovery, clips/layers/masks/effects/images, final SVG/text
drawing, archives and platform release work remain unfinished.

## Original dashed-stroke preparation and coverage (2026-11-09)

This batch connects original compositor dash preparation, bounded linear coverage
and immutable geometry caches to the retained renderer. The implementation-first
sequence remains in force: strict native/WASM/CPU compile/lint now; complete source
differential, pixel, browser, performance and package qualification after assembly.
No shader, driver, compiler option, original gate or deadline is changed.

| Original repository source | SHA-256 | Scope |
| --- | --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` | TryCreateDashedStrokePath, line/curve continuation, endpoint cap overrides, closed seam merge, undashed pen construction and linear/general compilation routing |
| `src/ProGPU.Scene/StrokeCoverageGeometry.LinearPath.cs` | `03b3acb331f8abca1169fccfbf3d00da463ad85c81edf0a69433ba612e1d43f9` | Complete linear admission, cyclic/gap run construction, solid ideal bounds and bounded dashed preparation |
| `src/ProGPU.Scene/StrokeCoverageGeometry.OutlineBounds.cs` | `69ee8066d4fb4784ea8afe7a6d6eef74c625efab2e4229167aa67ecea94937e1` | Emitted-body bounds, original source joins, float-narrowed caps/arcs/clipped joins, positive compound pieces and directed terminal caps |
| `src/ProGPU.Scene/StrokeCoverageGeometry.cs` | `02643b208a8c5c31864f58e3ea924c2193a465c920b377ed5792cfcac39ed1a2` | CanPrepareDashPattern and streaming LineBounds reducer, cubic derivative roots and independent round-cap support |
| `src/ProGPU.Scene/RenderCommand.cs` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` | General dash geometry and separately derived-pen cache identities |
| `src/ProGPU.Scene/RenderCommandGeometryCache.LinearDashes.cs` | `4c23e8f8677aa5eaf28c0d6b43b710895ea687c3350e49a17970a054e188b27c` | Immutable topology, exact linear key, negative-result caching, current-brush replacement and fill-versus-stroke ownership |

Earlier original arc/Bezier dash, pen, cap/join and fill ports remain shared.
No foreign implementation was read or copied. Safe SIMD consumes locked `wide`
0.7.33 and `safe_arch` 0.7.4 as ordinary Cargo dependencies; both offer MIT,
Apache-2.0 or Zlib licensing. Wide declares Rust 1.61, below this crate's 1.90
minimum. Only the public [crate contract](https://docs.rs/wide/0.7.33/wide/) and
[f64x2 API](https://docs.rs/wide/0.7.33/wide/struct.f64x2.html) were consulted.
Packed XY/dash-pair arithmetic uses ordinary multiply/add/subtract/divide and
min/max, never fused operations or approximate SIMD trigonometry. The source
scalar root/trigonometric and predecessor-dependent run decisions remain ordered.
SIMD availability follows the dependency's target contract; targets without a
supported vector feature use its documented fallback. No compiler flags change
to manufacture qualification. Exact default-runtime, signed-zero, scalar/SIMD
and cross-target numerical comparisons remain required.

`dashed_stroke` owns analytic line, quadratic, cubic and arc spans. Constant curves
retain the original exact-control admission. Coincident nonconstant curves remain
curves. Unstroked source segments reset phase and active run; every hidden interval
or completed visible interval ends continuation even when its endpoint equals an
older coordinate. First curve spans may continue only in the original visible
phase. Closing edges retain the original independent traversal. Closed seams move
the first run behind the last or close a single run, restore the source smooth
flag and clear endpoint overrides. Open endpoint eligibility preserves phase and
actual traversal; cap overrides require the original coordinate tolerance.

Optional terminal capture owns original point, adjacent source point, dash cap and
source end cap. Nonlinear terminal metadata rejects where the original writer
cannot represent it. General compositor preparation retains its original
capture-disabled policy; normal linear preparation captures and paints the entire
compound fill when needed. Rational dashed segments reject instead of flattening.
The source's partial failure output becomes `None` at the Rust owning publication
boundary; no partially prepared path escapes. The existing Rust full-cycle
nonprogress rejection also bounds line traversal where floating additions cease
to advance; it does not modify progressing intervals or source epsilon arithmetic.

`prepare_linear_stroke` validates all original figures/segments, including
unstroked edges, before owned run allocation. The one-million-record budget is
decimal and remains distinct from the native fill compiler's capacity. Closed
gaps rotate run order; exact constants retain pending smooth metadata and wholly
point-like runs preserve cap policy. Dash preparation uses the original float
edge metric, two-lane double scaled-interval sum/minimum, zero/odd-array rules,
offset narrowing and both density/work bounds. Tiny or overflowing linear edges
reject rather than silently disappearing. Width-zero preparation retains the
original empty-coverage behavior.

Solid ideal bounds remain separate from emitted dashed-outline measurement.
Streaming bounds use O(1) scratch. Cubic controls narrow to float before derivative
roots; root order, invalid-root handling and final float bounds remain unchanged.
Emitted bodies reject collapsed area. MiterOrBevel, generic clipped joins and WPF
source triangles retain their different rules. Materialized source joins skip the
same tiny paint triangles; bounds-only measurement may retain them. Emitted
clipped miters keep the independent float subtraction before the double multiply.
Round caps/arcs preserve original cubic construction and analytic extrema.
Compound pieces retain their anchored positive endpoint-area admission, without
reversal or a contour-area substitute for raster coverage. Terminal caps join the
same nonzero fill so overlapping pieces receive coverage once.

Recording remains O(1). Shared cache owners retain actual immutable source Arcs;
replacing a public source generation invalidates topology and both cache routes.
General geometry keys compare original width, offset, intervals and all endpoint
caps with ordinary equality. Linear keys also retain join/miter/clip/WPF policy
and original NaN-equal failure identity. Brush identity stays out of geometry keys;
the derived pen or fill command retains the current brush and semantic opacity.
Warm matching is O(D) for D intervals and does not traverse/rebuild the source or
emitted geometry. A short CPU-only mutex protects candidate publication and holds
no GPU resource or render lock. Allocation failure leaves the previous candidate
intact; failed linear source admission is retained and never selects the general
route as a fallback. Previously published scene generations remain immutable.

Compilation prepares dashed commands before material mutation or atlas planning.
Spines use the original solid compiler with recovered local widths; terminal
unions use the existing fill compiler and actual path-atlas placement. Complete
target-frame admission precedes extracting XY coefficients. Aliased unions retain
one-sample coverage, ordinary unions retain the existing sample grid. Painter order
and mixed descriptor ordinals are unchanged. A per-compilation O(C) borrowed/owned
command plan holds resolved generations; prepared GPU replay remains one ordered
draw with no new geometry uploads. Standalone mesh compilation rejects fills that
require the scene atlas rather than manufacturing triangle coverage.

The shared native/browser gallery adds normal/fixed/hairline curved dashes and an
exact-period linear terminal-cap union. This is authored integration code for the
later runtime pass, not a source oracle. Strict builds cover all feature sets;
runtime controls, output comparisons and performance are explicitly deferred.
The cleanup receipt records 83 obsolete duplicate incremental directories removed
(449,443,970 logical bytes), retaining newest/recent state and original evidence.

Required consolidated controls include complete original dash phase/run/cap/seam
transcripts, exact curve spans, tiny/nonfinite/constant input, whole-inventory and
density boundaries, both bound families, emitted cubic/triangle records, signed
zero and default-runtime SIMD arithmetic, positive/negative cache identities,
brush/source replacement, legacy/fixed/hairline/aliased frames, actual cold/warm
coverage/material pixels, painter order, cancellation/ownership, browser DPI/loss,
allocator/frame/Instruments baselines and all original provider/application/package
gates. Full managed atlas caching/recovery, signed programs, remaining primitive
coverage APIs, images/clips/layers/masks/effects, final SVG/text drawing, complete
scene invalidation/archives and platform release qualification remain unfinished.

## Original text compositor preparation (2026-11-09)

This batch ports the ordinary layout/positioned text compositor into an owned
CPU plan and same-device atlas realization API. It does not yet connect that
ordered output to the shared Renderer. Original text-specific path raster scale,
phase, aliasing and coverage-transfer metadata remains explicit; replacing it
with ordinary native PathDraw defaults would lose source behavior. Final mixed
text/vector submission is the next integration work, not completed qualification.

| Original repository source | SHA-256 | Scope |
| --- | --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` | CompileTextCommand/CompileGlyphRunCommand ordinary paths, placement/raster-size/flags/styles, color-layer/vector fallback, bounded positioned-path cache, gamma policy and conservative clip admission |
| `src/ProGPU.Scene/IntegerGlyphRasterOrigin.cs` | `31977e52b6250b3d02fe3270d4c18593d31f532a4f39e1a68f564c282e981aa4` | Explicit descriptor and complete selected-occurrence physical-origin preflight; half ties toward positive infinity |
| `src/ProGPU.Scene/GlyphCoverageTransferPolicy.cs` | `070dd2deceac1b25048607203674b21c2c6eb1e3208a342e29de760832ef8a47` | Grayscale-only raw contrast admission and independent negative vector encoding |
| `src/ProGPU.Vector/GlyphInstance.cs` | `31b22cbe71451dab0008b4d0fbae5db8ccec542281f34e488f0848d3013e60fe` | Original 96-byte instance layout and physical atlas texel payload |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` | Bitmap record capability selection before atlas acquisition; no eager pixel decoding in command planning |
| `src/ProGPU.Vector/TransformMetrics.cs` | `12271082b95c438ed47d041e961e757e5a0b94bc56334a978f5acc2f17a838de` | Existing original singular-scale helper and failed-scale fallback of one |
| `src/ProGPU.Backend/Shaders/Text.wgsl` | `432bcabab2d670297ceba2a720de8ca2351ffb4609bd619f62ead1fd7cf84d4a` | Existing unchanged 32-byte shared-style contract and text entry points |
| `src/ProGPU.Backend/Shaders/TextGlyphGeometryCommon.wgsl` | `321a1eed489c77c51c04fb89b5fa96769ad96afd142b4edcef1a492d93f19f25` | Existing unchanged instance/flag/physical-tile consumer |

Only original ProGPU source was read and translated. Earlier owned font, layout,
color/SVG, vector and glyph-atlas implementations remain shared. No new dependency
or shader/compiler/driver change is introduced. Independent outline coordinates
use the existing safe wide f32x4 API, preserving separate source multiplication,
addition, final zero offset and Y-sign order. Predecessor-dependent source traversal,
cache decisions and finite fixed-size transform composition remain ordered.
Default-runtime SIMD/FMA, trigonometric and signed-zero qualification remains open;
Rust trigonometry is not relabeled as proof of original named primitive construction.

TextDraw retains an actual completed ManagedTextLayout or a metadata-free original
font/indices/positions generation. It never synthesizes shaping records, changes
the selected font or rewrites source positions. Explicit range selection keeps
the original positive-count versus minimum-array-length semantics. Descriptor
validation is O(1). IntegerPixel requires the original aliased/atlas-backed,
positive-diagonal complete frame and font capabilities, with no bold, italic,
font transform, rotation or logical/vector raster override. Every selected position,
including no-ink occurrences, preflights before style/path-cache/atlas mutation.
Physical rounding uses floor and a fractional half comparison, avoiding addition
of 0.5 to already integral large floats. Native hinted sources remain separate.

Ordinary placement retains Animated continuous coordinates, quarter-pixel X phase
and ties-even Y at size <=24, integral ties-even placement for larger unrotated
text, and continuous rotated placement. Raster sizing preserves 4..128 clamping,
independent actual DPI/static zoom, original logical-resolution policy and the
complete active transform. Font horizontal scale/skew fall back independently on
nonfinite components. Synthetic italic, two-pass bold, atlas upscale selection,
cached ClearType suppression and size/transformation-dependent vector gamma retain
their original distinctions. Explicit source contrast is immutable and admitted
only for grayscale; its flags and vector encoding remain separate from legacy gamma.

Plans keep ordered atlas requests and vector/color-layer commands. COLR/SVG layers
precede bitmap/ordinary outlines and retain actual layer owners and coordinate
policy; no synthetic bold is added to the separate color-layer route. Bitmap
selection checks the original embedded record without decoding pixels. Layout and
positioned commands retain their different PreferGlyphAtlas/CFF/upscale fallback
rules. Conservative source clipping precedes atlas residency; the complete clip
context also survives in the plan because culling is not a rendering clip.

Vector fallback retains original font outlines, 1/128 local phases, exact source
point expressions and the 4096-entry cache-clear policy. Keys retain actual source
owners and all em/phase/skew/scale/SVG inputs, with original NaN/zero equality.
Arc sizes, rotation/sweep and source flags follow the original specialized glyph
writer, not generic affine arc transformation. SVG brush positioning separately
handles solid/linear/radial materials, opacity, spreads and interpolation; it does
not infer the geometry's italic/horizontal scale as a brush transform. Generic
materials retain their original owner. Retained plans outlive cache eviction.

GlyphInstance and GpuTextStyle have compile-time size/offset checks. Atlas output
uses actual physical texels, bitmap render extents/raster scale, source bearing,
independent basis vectors, bold/italic offsets and original flags. Solid commands
outside retained MVP compilation use shared styles; retained/non-solid commands
keep legacy instance color/mode behavior. GlyphImageMetrics allows CPU emission
without a GPU dependency. Complete destination capacity is checked before writes.

With gpu enabled, TextPainter::resolve runs inside the caller's existing batch.
It preserves atlas/vector transitions, delegates original cache/acquisition to
GlyphAtlas and carries the already resolved integer origin through capacity
fallback without inverse-transform recomposition. It flushes pending coverage
before publishing ResolvedTextPaint, then retains actual R8/RGBA generations,
complete residency and generation/revision counters. Read-only residency admission
shares the original all-or-nothing replay validation. Generation comparison is
required in addition to key presence: eviction and reacquisition of the same key
cannot revive instances containing an older physical slot. The caller still owns
batch end/abandonment on error or cancellation. No finalizer submits, polls, unmaps
an unrelated resource or destroys a submitted texture.

Strict native/all-target/all-feature, WASM browser and CPU-only lint/format checks
cover this preparation batch. Full runtime validation remains deferred under the
implementation-first workflow. Required controls include original command/instance/
style bytes, all physical placement and overflow cases, whole-range no-ink/integer
admission, bitmap failure versus outline selection, color-layer ordering and brush
domains, exact positioned outlines/cache identity, default-runtime SIMD/trig bits,
capacity fallback origins, residency/growth/eviction/cancellation, actual native/
browser pixels and matched allocator/frame/Instruments baselines. The shared
Renderer/text PathAtlas consumer, retained GPU outline-instance builder, hinted
sources, full text masks/clips, styled owning APIs and all original source/provider/
application/package qualification remain unfinished.


## Ordered text renderer and source path placement (2026-11-09)

This batch connects the preceding preparation port to the existing Rust renderer;
its earlier statement that the shared consumer is missing is superseded here.
Original sources inspected directly (SHA-256):

| Original repository source | SHA-256 |
| --- | --- |
| ProGPU.Scene/Compositor.cs | a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd |
| ProGPU.Vector/PathAtlas.cs | 8b0c65142b0156f505374f1d20a6feff3eea1ed7bec23a1b8ce6f405e7414ccd |
| ProGPU.Scene/PathAtlasPixelMapping.cs | 31693992e9902b45bf393ba93fc9b3aa10300041eb5148a49f122b0866b52827 |
| ProGPU.Scene/GlyphCoverageFramePolicy.cs | fb2e5f8ee657b299abcbb5a7775f386f980af75050752590852d2073e17e0518 |
| ProGPU.Backend/Shaders/Text.wgsl | 432bcabab2d670297ceba2a720de8ca2351ffb4609bd619f62ead1fd7cf84d4a |
| ProGPU.Backend/Shaders/TextGlyphGeometryCommon.wgsl | 321a1eed489c77c51c04fb89b5fa96769ad96afd142b4edcef1a492d93f19f25 |

No foreign implementation source, new dependency, shader modification, compiler
flag or driver change is part of this port. Source Compositor RegisterTextStyle
appends one original style per eligible command; the renderer preserves this order
and rebases instances into the complete style page without deduplicating records.

Scene records text in O(1), retaining its actual source/context. Actual target DPI
is supplied during preparation. Every text plan, including full integer-origin
range preflight, completes before atlas acquisition. CPU-only compilation rejects
unresolved text explicitly; text_commands exposes its unchanged inputs. Existing
native analytic/path/stroke commands remain independent. Completed commands form
one vector mesh plus one 96-byte instance stream and ordered contiguous pipeline
ranges. Only adjacent ranges of the same pipeline coalesce. Canonical Text uses
six original vertex-index corners, shared 32-byte styles and both actual atlas
textures; Vector uses its unchanged indexed geometry and material page. One render
pass preserves all interleaving, including source color layers and capacity fallback.

Source text paths keep the original complete affine input, per-axis column lengths,
0.0001 axis admission, conservative rotated scale and explicit raster scale.
Exponent/step quantization reproduces ILogB/ScaleB buckets, including subnormal
steps, without logarithm approximations. Device phases clamp the requested grid
1..64 and use original ties-to-even rounding. Four-pixel padding is computed in
integer space after original float bounds multiplication/double floor/ceiling;
invalid or unsupported capacities reject before publication. Quad far edges use
min/scale + width/scale rather than end/scale. Source local brush coordinates and
original UV additions remain paired. Aliased paths encode shape 1004 and one sample;
explicit transfer stays independent of legacy gamma. Exact texel loads require the
actual direct unit-DPI frame and all four integer-lattice corners. Existing native
power-of-two DPI placement is unchanged. Independent column-length and quad-point
lanes use safe wide SIMD, retaining separate multiplies/adds. Source runtime float
bit compatibility remains an explicit gate.

Within a preparation, repeated actual positioned-geometry owners share one compiled
Path (including negative empty results), allowing mask deduplication by source,
both scale axes, phase and sampling. Owners stay rooted throughout this map; an
allocator-reused pointer cannot alias. This is not the complete managed content-hash
cache/recovery or raster-viewport figure culler. The existing native 4092 tile and
8192 atlas limits remain; unsupported capacity fails instead of changing pixels.

Multiple text plans now enqueue into one owning glyph batch. Existing bounded rings,
oversized requests and growth retain their original flush rules, while scene-level
final publication flushes once after the last plan. The public TextPainter::resolve
still flushes before publishing its standalone resolved generation. Pixel-coordinate
instances survive growth; final texture handles/revisions and whole-scene residency
are captured only after all requests. Replay checks actual atlas identity, generation,
texture revision and every key; same-key reacquisition cannot revive an older slot.
Reprepare after invalidation. Rendering performs no uploads or rasterization.

A renderer preparation takes exclusive ownership of its text state. Cancellation or
failure drops that private candidate, leaving no half-open atlas in the renderer;
wgpu handles retain submitted dependencies without explicit texture destruction.
Previously prepared text then fails its ownership/residency check until recompiled.
Successful publication restores the warmed state. Cache loss on failure is deliberate
and conservative; full retained recovery and per-frame scheduling are later work.

The direct target currently accepts finite 2D affine text and semantic opacity.
Late camera/MVP, non-default retained zoom and clip contexts reject; early clip
culling never pretends to implement a clip. The renderer's existing projection is
not changed to manufacture the separate GlyphCoverageFramePolicy certificate and
its uniform tag stays zero. Hinted/system-source owners, complete text masks/layers,
retained outline-instance builders, styled owning APIs and full managed atlas recovery
remain separate missing subsystems. CPU raster preferences retain explicit unsupported
errors through the existing typed glyph options.

The native --text FONT_PATH and browser local-font selector share one authored
shaped/positioned gallery with vector overlays between text draws. It includes
atlas/vector/gradient, contrast, synthetic styles, affine and eligible integer-origin
rows. Explicit Unicode profiles are sample inputs, never inferred from host names.
No sample fonts or generated binaries are added to the crate. The browser bridge
exposes actual instance/style bytes, glyph upload bytes, raster request counts and
atlas residency, alongside the existing vector metrics.

Native all-target/all-feature, CPU-only and WASM browser strict Clippy plus formatting
and diff checks cover this implementation batch. Full tests, source differential
records/pixels, live native/browser rendering, failures/cancellation/eviction/growth,
default-runtime arithmetic, allocator/frame/performance/Instruments and package gates
remain deferred under the requested implementation-first workflow. Required controls
must include vector/text/vector alpha order, shared and legacy flags, empty/no-ink
slots, style indexing, color/coverage growth after earlier runs, exact per-axis scale
and phase boundaries, repeated-path identities, source transfer, malformed inputs,
real device loss and both cold/warm counters. No runtime or parity pass is claimed.


## Retained images, sampler policy and GPU mip generation (2026-11-09)

The original native retained image contract is the primary draw/admission source;
managed code supplies its separate projective, tile-page, brush-map, sampling-policy
and mip-generator contracts. Original repository sources inspected directly:

| Original source | SHA-256 |
| --- | --- |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_image.cpp | 0fba76f13ece2b5a6b726429951c014997303f0fd2a73c8cd90fffad12c056d4 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp | 3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_image_resources.cpp | 3d4dcfd1105259210d9482160a0a7dbde5f9dca4d186efa84847eb4adb896859 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp | c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae |
| src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp | 85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672 |
| src/ProGPU.Native/src/Backend/progpu_native_image_layer_resources.cpp | 8d8fb8816f1db86563b5ab1551446f928bf4915021f095cfc1b8737d2c16d523 |
| src/ProGPU.Native/src/Backend/progpu_native_image_execution.cpp | 09135c8ead728a45d4e2a65ab9196b2a2a3fbff535b65d8f01816ef37c6b547e |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp | 2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5 |
| src/ProGPU.Scene/Compositor.cs | a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd |
| src/ProGPU.Scene/RenderCommand.cs | dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8 |
| src/ProGPU.Scene/TexturePaintMapping.cs | 6b037db5ab3d705909016b0e62a91dc2dc342ce5c25b4a8d30289f7f348049c0 |
| src/ProGPU.Scene/GpuTextureBrush.cs | 5b25fb14f425758a0523d5b5cc05e16a378b180dc3b561d5b39b400009c68040 |
| src/ProGPU.Vector/TileImageVertices.cs | e40ac2ffa6a38d48fc447cf8361107d4ca0ec5bc565b38cab2522e7c009c5e90 |
| src/ProGPU.Backend/GpuImageSamplingPolicy.cs | 288764ddb3dc20bb782c00b76b46898a310c7803907e34d1b9497f13957794e7 |
| src/ProGPU.Backend/GpuTexture.cs | 0b5e89412acd16e86ae2f86c7e85e8bfa880b85dea196a79c2014cc1928bdb25 |
| src/ProGPU.Backend/Shaders/Texture.wgsl | 5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51 |
| src/ProGPU.Backend/Shaders/TextureImageSamplingCommon.wgsl | 7f8849f2aa74fcb65d99465d87591d839dd2ae92488abb6382d5cc8788ac629a |
| src/ProGPU.Backend/Shaders/MipGenerator.wgsl | 872a81b4bb8c49834f291ea37d6b606f9ecdd27c0213dca5b8351d515d60203c |

No external implementation, new dependency, shader edit, driver replacement or
compiler-flag change is introduced. Texture and TextureImageSamplingCommon remain
canonical, and the original MipGenerator is embedded as the ninth validation module.

Image retains immutable actual strided pixels, dimensions, format and alpha policy.
Native positive extent <=16384, complete last-row byte admission and untouched caller
padding are preserved. RGBA8/BGRA8/R8 uploads do not swizzle, premultiply, decode or
resample pixels. ImageSource distinguishes CPU generations from actual device-bound
GpuImage handles. Wrapping a texture retains its wgpu handle and an independent opaque
identity; it does not acquire explicit destruction ownership or invent a content
snapshot. The producer owns external pixel mutation and must retain valid storage
through consumers. The supplied creating device is checked by renderer identity and
independently by wgpu binding validation. Non-2D, multisample, array and unsupported
format resources reject. This is not the full mutable GpuTexture generation/view,
subresource update, external-provider callback or image-decoder API.

ImageDraw retains native positive source/destination admission, actual complete affine
frame, effective opacity, source alpha-ignore, extended source/address restrictions,
original cubic coefficient bounds and all eleven separate reconstruction modes. Zero
anisotropy canonicalizes to one; only LinearMipmap admits 2..16. Patch batches validate
all 1..65536 records before recording, retaining bounded sample domains, local frames,
fixed colors and all 29 original atlas color blends. Native patch color/opacity rules
are unchanged, including ordinary sampled-patch V metadata one. Those bytes are not
silently rewritten to make an expected clamp picture. Managed command-opacity versus
ancestor-opacity and its other recording admissions remain a distinct adapter gate;
this port does not pretend both producers have the same input policy.

Patch transforms use the native composed-affine expressions. Four transformed point
lanes use safe wide SIMD with separate multiplies/adds; original endpoint additions,
source-normalized UV divisions and ties-to-even physical snapping remain ordered.
The native repeated triangle corners are represented by the identical indexed
0/1/2,0/2/3 inventory. Complete vertex finite/index admission precedes publication.
ImageQuad retains the managed four explicit destination points and original projective
weights, including the shader's unchanged near-zero policy. It does not reconstruct
an inferred homography or change a projective draw into an affine envelope.

ImageBrush ports the original axis-preserving extended fill map, preserving source
origin/scales and GPU clamp/repeat/mirror addressing without CPU tiling. Nonextended
mapping retains the original destination and transform. Its returned command also
passes the native draw admission boundary, which can be narrower than managed raw
recording. TileImageDraw implements original premultiplied zero-origin occupied-page
vertices, actual extent <=2^24, independent per-tap addressing and explicit nearest,
linear or bounded Fant coefficients. It never includes texture-pool padding as source
content or downgrades cubic/mip filters. Full captured-page re-encoding and snapped
hinted-source storage-extension adapters remain separate work.

ImageSamplingPreference uses actual adapter facts: Automatic selects explicit taps
for the original Parallels Display Adapter D3D12 exclusion; forced native rejects
there. ExplicitShader changes only base nearest/linear/Fant coefficients. Mip and
cubic algorithms stay independent. Forced native also rejects occupied-tile draws.
Renderer image preparation requires configure_image_sampling with an actual adapter
receipt; existing CPU-only compile defaults merely encode native sampling and do not
qualify any adapter. Native and browser hosts pass their actual adapter information.

Scene retains an independent image mesh and source/sampler binding table. Only adjacent
ranges with identical actual source and sampler merge; vector/text/image transitions
stay in painter order within one render pass. Texture's canonical retained entry
normalizes straight/premultiplied colors once for One/SrcOver blending. Prepared images
retain their own original pipeline, inferred layouts, uniform/vertex/index buffers,
bindings and texture handles, so another preparation or cache reset cannot substitute
new layouts/resources. No mask/camera/effect/ROP semantics are inferred from an image.

CPU generation identity plus requested mip inventory keys the upload cache. Cache entries
hold weak CPU owners and actual GPU handles; all compiled source owners remain live
through lookup, preventing address-reuse aliasing. Dead uploads prune on subsequent
preparation or trim_image_cache. Prepared/submitted dependencies stay independently
owned. Separate base/full-mip allocations preserve earlier immutable texture generations;
no later draw mutates their content. Samplers retain complete mode/anisotropy/address
identity, including native base clamp versus addressed LOD rules.

Mip filters on CPU inputs lazily allocate the full real chain. The unchanged MipGenerator
samples each previous level into the next using the original fullscreen triangle,
linear sampler, exact floor-halved dimensions, clear/store attachments and one ordered
submission per image chain. No CPU readback, repacking, color conversion or resampling
is added. GPU sources expose their actual existing mip inventory without pretending
missing levels were generated. Per-format pipelines are cached. CPU upload payload,
unique retained texture bytes and actual mip pass counts are reported independently
of vector/text geometry/uniform counters. Warm image replay performs no uploads or
mip generation; full performance claims still require measured controls.

Image preparation takes its complete state into a private candidate and captures GPU
validation through the final publication boundary. Failure or cancellation drops it;
no destructor submits, polls, unmaps or explicitly destroys live textures. Prior image
scenes still own their original pipelines/bindings. GPU error scope completion occurs
before warmed state is restored. Full lost-device/recovery/provider qualification is
not established by these ownership types or static compilation.

The shared native --images/browser ?scene=images gallery uses tiny generated patterns
and covers eleven filters, vector overlays, patch/fixed/atlas-color draws, projective
placement, repeated/mirrored brush mapping and all three occupied-tile modes. It stores
no image assets, traces or benchmark output. Browser metrics expose actual image uploads,
texture bytes and mip passes. Native/all-target/all-feature, WASM browser and CPU-only
strict Clippy, formatting/whitespace/JavaScript syntax checks pass. The existing offline
Naga test validates all nine canonical compositions. This is static shader evidence,
not GPU execution or source pixel qualification.

Consolidated validation remains deferred under the implementation-first request. It
must include original bytes/admission/float controls, every patch blend/filter/address
combination, strided/alpha-zero/premultiplied inputs, projective/snap boundaries, odd and
one-pixel mip extents, poisoned pool padding, batch atomic failure, old-scene replay
after cache replacement, borrowed source mutation/retirement, both sampling paths,
real native/browser pixels, cold/warm upload counts and matched allocator/process/frame/
Instruments measurements. Color matrices, complete image effects/interception, clips,
layers/masks, capture/cache-source policy, codecs, full mutable texture/resource APIs,
platform/provider/application/package and original Microsoft qualification stay open.


## Retained direct mask chains and binary clips (2026-11-09)

This connected implementation batch ports original native per-draw coverage
records and canonical shader consumers. It does not substitute per-draw masking
for source isolated-layer opacity, byte-AA layer composition or Clear semantics.
Only original ProGPU sources were read for implementation; no external engine
source was inspected or included.

| Original repository source | SHA-256 |
| --- | --- |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask.cpp | 6c3b0b22d2e7aac8a1e9d17b4d774d1dc7c8ae090f90766653581bc1003a370a |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp | 2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5 |
| src/ProGPU.Native/src/Backend/progpu_native_draw_state.cpp | 2c59e9107eb66374a7d939f95a7d03c78f7e78ad25e3a84ecf06281227021816 |
| src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp | 7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask_resources.cpp | 66fcc7bc64d8b908788571c31f1bd0b6b671fdea2d25484be2ca22a4091a1158 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_mask_chain_pipeline.cpp | adb675667452729483eba70f80c4f1d1f5c5c3ddb306dc5aeec438c06b5712d1 |
| src/ProGPU.Backend/Shaders/Vector.wgsl | 6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed |
| src/ProGPU.Backend/Shaders/TextMaskCommon.wgsl | 197a780d70286a4ddcccd4edfb522e08a0702298827203098fd768b4e142e2f3 |
| src/ProGPU.Backend/Shaders/Text.wgsl | 432bcabab2d670297ceba2a720de8ca2351ffb4609bd619f62ead1fd7cf84d4a |
| src/ProGPU.Backend/Shaders/SampledMaskCommon.wgsl | 4799e7646ac1cebbef3237ec248edebf3359880159ecdd098b4c9215ad995c79 |
| src/ProGPU.Backend/Shaders/Texture.wgsl | 5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51 |

### Records and physical mapping

`mask::Mask` retains either native rounded/axis-area metadata or original owned,
strided R8 pixels. Descriptor validation preserves positive finite bounds,
nonnegative independent TL/TR/BR/BL radii, opacity 0..1, finite transforms and
the original double determinant/inverse admission. The `Image` owner can retain
extra trailing bytes; only the exact native row-stride*(height-1)+width prefix
is uploaded. This Rust owner is not an encoded native wire record.

`Mask::uniforms` emits the original 96-byte six-vector layout. Ordinary masks
normalize all eight radii with the original shared four-edge ratio, multiply the
complete transform by actual DPI in double, and preserve the original reciprocal
and scalar inverse arithmetic before narrowing. Radius multiplication uses safe
four-lane SIMD. Coverage UV mapping retains the semantic legacy route's double
divisions and the original float DPI*width/height products before widening.
No shader math, sampling, derivatives, root/curve/event logic or GPU constants
change. Uniform publication rejects nonfinite output before GPU mutation.

Axis-area records require exact identity transform, zero radii and opacity one.
They project the original near/far float edges directly with independent SIMD
multiply/add lanes into physical coordinates and keep identity/unit-pixel mask
coordinates. No DPI divide/multiply round trip or rounded-distance substitution
is introduced. The current renderer exposes a scalar-DPI root target; nonzero
viewport/local capture origins and separate X/Y presentation DPI remain unported.
This direct coverage API does not claim the conditional nested AA-group policy
or original Microsoft raster qualification documented in
`docs/native-axis-clip-coverage.md`.

`Scene::push_clip_rect` is explicitly a target-axis binary pixel-center clip.
It preserves the native aliased-composite rule ceil(double(projectedEdge)-.5),
clamps to the u32 domain, intersects original integer half-open intervals and
clips to actual frame dimensions before WebGPU scissor binding. It does not
infer rotated geometry from an envelope or silently select managed rectangle
clip policy. Zero/empty intersections skip draws; pop restores the original
parent state, including the full target scissor. The initial attachment clear
remains outside these scopes; target-storage Clear is separate unfinished work.

### Recording, ordering and ownership

Recording is O(1) per push/pop and shares actual source pixel owners. Underflow
and invalid descriptors leave the recorder unchanged; compilation requires
balanced scopes. `Scene::append` copies a balanced command inventory in O(C),
retaining original owners and painter order. Full scope topology and physical
mapping/device-size admission precede text atlas mutation, including no-ink
text. The renderer's existing `TextPaintContext::clip` rejection remains: scene
coverage scopes are real render controls, not an early text-culling substitute.

Compilation is O(C) for C commands with scopes and O(S) fixed-size states for S
pushes. Pop restores a captured state index in O(1); draw coalescing never crosses
state boundaries. Ordinary recordings take an O(1) scope fast path without an
extra command walk or per-command state allocation. Text expansion preserves
every original scope command around its ordered glyph/vector output.

Direct chains admit four analytic masks, or a primary coverage bitmap followed
by three analytic masks. Coverage after an analytic mask, multiple sampled masks,
and deeper chains reject with UnsupportedExecution; there is no multiplication
reorder or approximate envelope fallback. These limits describe this explicit
bounded consumer, not completion of original composite/offscreen masks.

The renderer lazily creates only the canonical chain pipelines required by
actual masked vector, text or image ranges. Original unmasked entry points remain
in use outside mask scopes. Explicit frame/source layouts are shared structurally
between masked/unmasked variants; image output retains premultiplied blending,
while text/vector retain their original straight-alpha blend. Primary and chain
uniforms remain 96 and 288 bytes and use a common group-2 layout. Clip-only state
changes share immutable mask bindings by exact uniform bits and source/sampler
identity. Fragments evaluate the original ordered primary * continuation product;
there is no CPU raster, offscreen allocation, readback or per-mask submission.

R8 uploads cache by actual immutable CPU owner using weak references, preserving
source stride and nearest/linear clamp sampling. Pruning removes only dead owner
entries; prepared scenes and submissions retain actual views/buffers/pipelines.
Candidate caches are exclusively held during async preparation; errors or
cancellation cannot publish half-prepared state. Old prepared masks retain their
actual pipeline independently of a replaced cache. No destructor submits, polls,
unmaps or explicitly destroys a texture held by live work. Metrics separately
report actual R8 upload/storage bytes, distinct mask bindings and emitted draw
ranges (excluding state records and empty scissors). Replay uploads nothing.

### Samples and deferred qualification

Native `--masks` and browser `?scene=masks` select the same authored gallery:
independent radii, affine masks, nearest/linear padded R8, four-mask chains,
axis-area coverage, nested binary/empty clips, mixed images/vector overlays and
restored unmasked draws. Native `--masks --text FONT_PATH`, or the browser's
explicit font chooser while in mask mode, adds the existing text/vector gallery
under sampled/rounded/binary coverage. Tiny patterns are generated in memory;
there are no new external assets or runtime traces.

Per the implementation-first request, this batch uses native all-target/all-
feature, browser-target and CPU-only strict Clippy plus formatting/whitespace/JS
syntax checks. No shader was changed; the prior nine-module offline Naga result
retains its exact earlier scope. New pipeline runtime creation, original matched
uniform/clip/radius/UV bytes, native/browser full-frame pixels, coverage ordering,
invalid/overflow/cancellation/retirement and cache/performance controls are
explicitly deferred. Required controls include unequal/overflowed radii, tiny
DPI/determinants, adjacent pixel-center edges, repeated scope restore, no-ink text,
odd stride, nearest/linear out-of-bounds samples, mixed draw transitions and warm
replay. Full composite/path/brush/picture masks, layers/effects/ROP/Clear, managed
atlas recovery, resource archives, original provider/package/application pixels
and all prior reliability/performance gates remain open.


## Owned captures and bounded native group effects (2026-11-09)

This connected batch ports the complete original bounded Gaussian/box/drop-shadow
scheduler, physical parameter conversion and canonical compute passes. Explicit
scene capture connects those operations to retained image composition. Automatic
semantic layer traversal, bounds localization, nesting and source effect policies
remain separate unfinished consumers; explicit capture is not their replacement.
Only original repository implementation was used. No external implementation
source or runtime rendering engine was introduced.

| Original repository source | SHA-256 |
| --- | --- |
| src/ProGPU.Native/src/Backend/progpu_native_effect_plan.hpp | 2de624832f22378fe73d3fdea67530059dc0d01a14da8ed69226d312c2b80e18 |
| src/ProGPU.Native/src/Backend/progpu_native_effect_plan.cpp | fb737c514ac7e4979f2b07bc223d80dc1141ee97869b1d0fe5d1780f2e27b23b |
| src/ProGPU.Native/src/Backend/progpu_native_draw_state.cpp | 2c59e9107eb66374a7d939f95a7d03c78f7e78ad25e3a84ecf06281227021816 |
| src/ProGPU.Native/src/Backend/progpu_native_effect_execution.cpp | a2e42e6680497a0477c4bd8a94615b08766f91a3f6fdf57ff6c5de709489ee9f |
| src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp | 7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3 |
| src/ProGPU.Native/src/Backend/progpu_native_layer_composite_execution.cpp | c3605dd11b5551e827f8689c6579bfc584d7219e61fe9c1fcb71f96b82a09f57 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp | 3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp | 2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp | c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae |
| src/ProGPU.Native/include/progpu_native.h | bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441 |
| src/ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl | 7ffc9636c103bddbbf13e7ba5fc253d4e9a02c26478edc5e5a38ef9a81de8834 |
| src/ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl | fc700b1b057b369588960ce485d8bf07a0cb9a6f06e8611f794c1d990f32080c |
| src/ProGPU.Compute/Shaders/GroupDropShadowCompose.wgsl | e57962dfb2ff1b95b68e8ca35f4453856b45a6a8ce234a41caba2f0deeb39238 |

### CPU descriptor and schedule contracts

`EffectChain` owns one through eight immutable `Effect` nodes. Typed variants
preserve the separate Gaussian sigma, box radius and shadow sigma/offset/color
contracts. Gaussian and box values must exceed the original logical .01 threshold
on both axes; shadows independently admit zero sigma. Shadow colors retain the
original straight linear RGBA 0..1 domain. Rust-owned typed descriptors are not
native wire producers: struct-size/reserved/version/revision fields are not
invented, and native transport admission remains separate.

`plan([dpi_x,dpi_y])` ports the semantic presentation route, retaining the original
four independent float multiply/add lanes with safe SIMD. It validates complete
physical sigma/radius/offset output before publication. Gaussian/shadow physical
sigma remains bounded by 128/3; box radius by 128. Gaussian radius is the original
ceil(sigma*3), box radius the original floor(radius), each clamped to 0..128 only
after admission. Parameter records stay exactly 16 and 32 bytes with zero padding.
No zero-radius pass is deleted; the unchanged shaders retain their original
copy branches, transparent out-of-bounds reads, recurrence order, weight sums,
uniform box normalization and explicit bilinear shadow-alpha offsets.

The original schedule selects the first intermediate unequal to the source for
horizontal blur. A Gaussian/box vertical pass reuses the previous source slot
(or the other initial slot); a shadow selects a third distinct source/blur slot
when necessary, then writes its composition into the now-dead horizontal slot.
The original capture remains untouched. `EffectPlan` exposes every source/slot,
parameter byte record, physical DPI, pass count and checked allocation budget.
Planning is O(E) bounded by eight nodes; GPU texel work remains original O(P*R)
per blur axis with radius <=128 and O(P) shadow composition. No CPU pixel loops,
readback, resampling or approximation are introduced.

### GPU resources and execution

`EffectEngine` uses the shared typed execution preference. Automatic/native
compute select the canonical 16x16 kernels after checking actual device limits;
forced alternate GPU/SIMD/scalar routes currently reject. This is capability
admission, not measured adapter qualification. Equivalent fragment and CPU routes
remain required work where applicable under the repository execution policy.
Blur pipelines initialize only on actual use; the separate shadow pipeline only
when a selected node requires it. All three original WGSL files are embedded
unchanged and explicitly included in the package inventory. The canonical module
inventory grows from nine to twelve.

`prepare` requires an actual same-device premultiplied RGBA/BGRA (linear or sRGB)
source. The source view selects mip zero; all intermediates retain original
RGBA8Unorm storage/sample/render usage. Raw R8 or straight-alpha sources reject
rather than receiving an implicit conversion. The original caller-provided target
extent controls padding and transparent boundaries. No automatic effect bounds,
source color-space metadata or content generation is inferred from a handle.

Prepared jobs own distinct candidate textures and exact pipeline/bind-group
handles. They allocate only the schedule's referenced two or three intermediate
slots; unlike the native semantic pool's eager three-slot inventory, an unused
third texture is omitted. This changes allocation ownership/quantity without
changing any original selector, alias, pass or texel operation; memory/performance
and output comparisons remain deferred. The original 256-byte dynamic uniform
slots are preserved (16/32-byte exact payloads), with complete device/buffer/size
admission and checked storage-byte arithmetic. No shared pool can overwrite a
previous prepared result.

`encode` records the original separate ordered compute passes into a caller-owned
same-device encoder. `apply` prepares and submits the complete chain once, with
no CPU GPU-completion wait. Re-encoding a prepared program uploads nothing and
recomputes current source contents into that program's own output; the API does
not claim an immutable content snapshot for an externally mutable source. Each
new apply allocates an independent result. Output caches keyed by actual content
and effect revisions, pool/retirement budgets and allocation/performance tuning
remain open. Bind groups retain all source/intermediate resources through GPU
submission; no destructor destroys a live texture, submits, polls or unmaps.

Metrics report original node/pass/workgroup counts, actual intermediate byte and
allocation counts, padded uniform storage and distinct exact payload bytes.
These counters expose work; they do not establish performance parity.

### Scene capture and final composition

`Renderer::encode` now exposes the existing painter-ordered render pass without
forcing a submission; `render` uses that same implementation. All owner, frame,
finite-clear and glyph-residency gates remain. `Renderer::capture` allocates a
fresh source-format texture at the already-prepared physical frame, clears it to
transparent, encodes original vector/text/image ranges and direct mask/scissor
controls, then optionally encodes the complete effect chain in the same command
buffer and submits once. It never changes source positions, target DPI or mask
coordinate records. The native isolated target's render/sample/copy usages are
retained. Preparation/render and effect resources stay under their actual device.

`CapturedScene` returns the final premultiplied image, actual frame, capture
allocation bytes and optional effect metrics. Earlier source preparation metrics
are separately labeled, not recounted as new uploads. Subsequent image draws
apply final opacity, masks and placement after capture/effects. Every capture has
its own output generation, so later captures do not mutate earlier retained
images. Async cancellation/error drops private candidates without publishing a
partial result; wgpu retains actually submitted dependencies. No readback or
external owner callback is added.

This API takes an explicit physical frame and source geometry/padding. It does
not implement automatic root-to-layer rebasing, semantic outward bounds, nested
layer traversal, conditional AA groups, background initialization, source shader
opacity ordering, target-storage Clear, advanced blends or retained revision
caches. Those original contracts and their package/source/pixel gates remain
mandatory future work. A whole root-sized capture is not advertised as the
bounded native semantic layer compiler.

### Shared gallery and validation scope

Native `--effects` and browser `?scene=effects` share six authored capture cases:
plain, anisotropic Gaussian, fractional box, zero-sigma fractional-offset shadow,
blurred shadow and a complete eight-node mixed chain. Source rounded masks and
alpha overlaps precede capture; final clipped image opacity is applied afterward.
`--effects --text FONT_PATH` or a local browser font selection adds actual owned
shaped text to the captured source. The 264x208 logical fixture explicitly owns
its padding and resolves its physical frame at actual target DPI. The gallery
reports capture/effect bytes, uniform bytes and passes without reading pixels.

Per the requested implementation-first workflow, validation is limited to strict
native all-target/all-feature, browser-target and CPU-only lint/compilation,
format/whitespace/JS syntax, package-file inventory (no archive built), and the
existing offline Naga test over all twelve canonical modules. Complete matched
source parameter/schedule/error/float controls, native/browser full-frame pixels,
zero/asymmetric/maximum radii, long mixed chains, odd extents, fractional offsets,
color/alpha, retirement/cancellation/device loss, warm output caching, allocator/
process/frame performance and original provider/package/application qualification
are deferred. Original browser failures and prior performance gates remain open.

## Bounded nested scene layers and fixed destination blends (2026-11-09)

This connected implementation supersedes the earlier explicit-capture checkpoint's
automatic bounds/nesting gap for the typed isolated subtree API below. Full native
semantic packet/source policy and release qualification remain incomplete.

Original repository sources consulted, SHA-256:

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp` | `2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5` |
| `src/ProGPU.Native/src/Backend/progpu_native_image_layer_resources.cpp` | `8d8fb8816f1db86563b5ab1551446f928bf4915021f095cfc1b8737d2c16d523` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp` | `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_composite_execution.cpp` | `c3605dd11b5551e827f8689c6579bfc584d7219e61fe9c1fcb71f96b82a09f57` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_budget.hpp` | `58c1bf76b9ef70522dd549624c7e8fe30eaf0523aef8965e21691c0977433167` |
| `src/ProGPU.Native/include/progpu_native.h` | `bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441` |
| `src/ProGPU.Backend/Shaders/Texture.wgsl` | `5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
| `src/ProGPU.Backend/Shaders/Text.wgsl` | `432bcabab2d670297ceba2a720de8ca2351ffb4609bd619f62ead1fd7cf84d4a` |

### Retained topology and bounded frames

`LayerDraw` retains an immutable `Arc<Scene>`, optional declared root-logical
bounds, raw group opacity, original typed destination blend, independent nearest/
linear sampling and an optional immutable native effect chain. Recording is O(1)
and validates complete metadata, source balance and retained depth before writer
mutation. Cached maximum depth includes detached child recordings, is preserved
by `append`, and is reset by `clear`. The original maximum of sixteen isolated
target levels is enforced. Owned immutable construction prevents reference cycles;
no weak source, owner callbacks or fabricated content identity is introduced.

Before the first private capture, preparation walks the complete retained tree,
validating scope topology/mask frames, blend availability, physical bounds/effect
plans and allocation counts, including children of empty targets. It limits layer
occurrences and effect passes independently to 16,384. Per-target capture storage
is bounded by the original 256 MiB limit. The original 512 MiB compiled budget is
used conservatively for the sum of all capture/effect allocations; this is not a
claim to have ported the full native retained residency budget or reusable pool.
Other existing geometry/font/device admission remains in each source preparation.
A failed later source preparation publishes no partially prepared root; completed
private GPU submissions may already exist and retain their actual owners.

Declared bounds use the native `presentation_clip_edges` four independent SIMD
lanes, preserving original x+width/y+height expressions, DPI multiplication and
zero-origin addition. Original finite physical edges clamp to the root extent;
`abs(value-round(value)) < 0.0001` snapping precedes outward floor/ceil. The result
intersects the actual parent physical target. Empty/inverted collapsed physical
extents allocate no texture. Missing bounds select the actual parent target; no
paint envelope, source transform, effect padding or positive empty-source extent
is inferred.

Private `RenderFrame` retains both the root frame and actual target origin/size.
The original managed `CreateMaskRenderUniforms` projection scale and translation
map already-compiled world coordinates into the bounded attachment. Vector brush
coordinates, source text positions and image source domains remain unchanged.
Canonical mask shaders add the actual `renderOrigin` to attachment fragment
positions; integer scissors subtract that same origin, clamp to actual attachment
size and preserve empty-state restoration. The unbounded root path retains its
previous exact uniform values. No glyph physical-frame certificate is invented;
its independent original positive-projection/source admission remains unchanged.
This combines the existing native bounds contract with the managed world-space
bounded-pass route; exact cross-implementation floating/pixel equivalence still
requires the original matched controls.

### Ordered source capture and final blend

Preparation traverses child layers before their parent scene. Each child uses
its actual bounded target, paints transparent source contents, applies its full
original effect chain, and submits the capture/effects together using the existing
owned capture API. Final image opacity is applied after the effect output. Outer
mask/clip scopes apply once to the composite; inner scopes stay on source draws.
All parent vector/text/image commands retain their original painter order, including
restoration after an empty or completed layer. Source-over zero-opacity shortcuts
are not applied to other blend modes, since transparent source can still alter
destination pixels.

Parent text is realized after its children have been captured. Subsequent atlas
growth/eviction cannot change an already materialized child image; ordinary parent
text keeps the existing exact final-generation/residency checks. This follows the
managed offscreen materialization boundary, not an assertion that the native
single-encoder layer compiler/recovery policy has been ported. Each preparation
captures again; immutable root replay retains completed output images and performs
no recapture or source upload. Source/effect resources use actual device ownership
and wgpu submission retention, with no explicit destructor GPU destruction, polling,
readback or finalizer submission. Mutable borrowed texture producers still own
their synchronization/content contracts.

`LayerBlend` preserves all 29 native enum identities, separate from the image patch
color-blend enum. `configure_fixed_group_blend` supplies all fourteen admitted
fixed states: SrcOver, Src, Dst, SrcIn, DstIn, SrcOut, DstOut, SrcAtop, DstAtop,
Xor, DstOver, Plus, Clear and Modulate. Color and alpha factors stay independent
for Modulate. Advanced destination-reading modes return `UnsupportedExecution`
during whole-tree preflight. No fallback changes their meaning. Blend identity
participates in ordered draw batching. Lazy fixed-state pipelines use the unchanged
canonical retained premultiplied image entry points, with structurally compatible
uniform/image/mask layouts; prepared scenes retain pipeline handles independently
of cache lifetime. Layer Clear is the original quad blend operator, not the separate
target-storage Clear operation.

`LayerMetrics` counts actual captured subtrees, source and effect allocation bytes,
effect uniform storage/passes, source geometry/uniform/glyph/image/mask uploads and
source draws. Cumulative allocation/work is labeled separately from root metrics;
adding it to root referenced-texture bytes does not produce a unique-residency
measurement. Cached pool/cold-warm performance claims are not made.

### Integration scope and remaining work

Native `--layers` and browser `?scene=layers` use one authored gallery: all fourteen
fixed destination blends, two-level bounded captures, final group opacity,
fractional/nonzero origins, binary and rounded scopes, original painter restoration,
shadow effects, empty captures and optional explicitly loaded masked text. Both
hosts expose subtree preparation counters. The gallery is an integration fixture,
not source or runtime qualification evidence.

Per the requested implementation-first sequencing, this batch runs strict native
all-target/all-feature, browser-target and CPU-only Clippy, formatting, whitespace
and browser JS syntax checks. It changes no canonical shader, dependency version
or compiler/driver flags. Exhaustive source topology/bounds/float/opacity/blend/pixel
controls, live GPU/browser presentation, cancellation/lifetime/device-loss stress,
allocator/process/frame performance and package qualification remain deferred.
Required next compositor work includes original destination-copy advanced blend
passes, complete geometry/brush/picture masks, conditional source AA groups,
background initialization and target-storage Clear, shader/image filters and
source-specific opacity/frame rules, typed retained resource revisions, caches,
residency pools, invalidation and native semantic transport. The original public,
provider/source/package/pixel and historical browser failure gates remain intact.

## Destination-aware layer blending (2026-11-09)

This batch fills the prior typed-layer checkpoint's fifteen advanced-mode gap.
Original repository sources consulted, SHA-256:

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Backend/progpu_native_advanced_blend_execution.cpp` | `44578984717579eb89a2c38ac62f097b5219211b963e8790dcc88092753e7446` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Backend/progpu_native_gpu_records.hpp` | `5f1eda15324be086c0b8880ee432e1244f2797f32f0137dd1d8a636149586311` |
| `src/ProGPU.Native/src/Backend/progpu_native_image_layer_resources.cpp` | `8d8fb8816f1db86563b5ab1551446f928bf4915021f095cfc1b8737d2c16d523` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp` | `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_budget.hpp` | `58c1bf76b9ef70522dd549624c7e8fe30eaf0523aef8965e21691c0977433167` |
| `src/ProGPU.Backend/Shaders/AdvancedBlend.wgsl` | `f4143798b7172e117c7e838efe6e5e87bbed7010f6cb619b1a64693d6adcdb8e` |
| `src/ProGPU.Backend/Shaders/Texture.wgsl` | `5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51` |

### Original records and preparation

The original `AdvancedBlend.wgsl` is embedded unchanged as module thirteen. Its
fifteen destination-aware functions (Multiply through Exclusion, and Overlay
through Luminosity) retain their exact unpremultiplication, branch expressions,
color/alpha terms and clamps. Fixed modes retain the separate original blend
factors. This host always selects operation-kind zero; inclusion of the shader's
ROP/axis-clip branches does not qualify or expose those independent policies.
The uniform-backdrop `GroupBlend.wgsl` fast path remains separate future work.

Private `SamplingUniforms` retains the complete original 96-byte record with
compile-time size and field-offset assertions. Source origins subtract the two
original integer-to-float conversions, and extents retain the actual physical
bounds. Unused operation/pattern fields remain typed zero. Every operation owns
its original 256-byte uniform slot; no per-frame uniform mutation or upload is
required. The explicit four-entry layout retains sampled destination/source,
unfilterable pattern texture (bound to the original source for operation zero)
and the exact uniform range. No sampler or float32-filterable feature is added to
the destination-aware pass.

The retained image command now carries its already-resolved physical bounds
through text expansion and compilation. Advanced operations are separate ordered
records, so they cannot merge across an ordinary draw or another operation.
Compilation does not recover bounds from a rounded image quad, mask alpha or ink
envelope. Each actual target prepares two full-size attachments (destination/root
and output) and one source attachment sized to the maximum bounded source among
its live operations. These private resources preserve the actual renderer format,
sample count, device and render/sample/copy usages. Source targets skipped by an
empty/disjoint actual scissor produce no advanced operation; canonical source
recording remains retained and later scope restoration stays ordered.

The complete layer-tree budget now includes conservative actual advanced scratch
allocations alongside capture/effect allocations, before the first private layer
capture. Per-texture 256 MiB and aggregate 512 MiB admission remains; operation
counts, uniform buffer limits and original binding alignment are also checked.
This does not claim the full native residency pool/eviction or full draw-pass
budget compiler has been ported. Pipeline state initializes lazily and is taken
as a private async candidate: failure/cancellation cannot publish half-prepared
resources. Already prepared scenes retain independent actual pipeline, buffer,
bind-group and texture handles.

### Ordered GPU execution

Each target with advanced operations paints into its owned destination attachment,
using the caller's original clear color. Ordinary vector/text/image ranges use the
same existing renderer, split only at advanced operations; immutable mask/scissor
state is carried into each subsequent span. For each operation:

1. Clear bounded source scratch, resolve the actual retained layer image through
   final group opacity and outer masks with the original Src pipeline, and apply
   the actual localized integer scissor. Source quads retain original zero-origin
   and width/DPI expressions, whole-texture UVs and premultiplied color metadata.
   Mask sampling keeps the actual global physical render origin. As in the native
   advanced route, source resolution uses the original linear layer binding;
   explicit nearest selection continues to affect the separate fixed route.
2. Run the unchanged full-target advanced shader, reading the original current
   destination and bounded resolved source and writing an independent output.
   No sampled texture aliases the current attachment. Original source-coordinate
   bounds, color conversions, alpha multiplication and clamp order remain intact.
3. Replace the parent from that output using the original full-size Src image
   quad. Subsequent ordinary draws and advanced operations observe this completed
   destination in painter order.

Finally copy the owned destination to the caller's actual target using the same
Src route. Browser and native swapchain attachments need no sampling usage or
new surface format. All passes are encoded into the caller's existing encoder;
there is no intermediate queue submission, CPU readback or GPU-completion wait.
The surrounding explicit capture/effect API still submits once for that source
capture. Re-encoding a prepared scene performs no new allocation or upload and
reuses its own scratch attachments in command order. Wgpu retains each encoded
resource; destructors never destroy a live consumer texture, submit or poll.
Exact source-format round trips are intentionally preserved, including sRGB
attachment conversions; no direct-write approximation removes the original
intermediate stored boundaries.

`AdvancedBlendMetrics` reports actual live operations, three passes per operation
plus the final root copy, scratch storage, padded sampling/resolve/copy uniforms
and quad/index payload. These are actual root resources and per-replay work, not
unique global residency or measured performance. `LayerMetrics` additionally
accumulates advanced work inside materialized child captures. Root draw counts
include the actual source/blend/replace/root-copy draws and exclude suppressed
operations; ordinary empty drawing spans add no draws. Images, masks and source
capture metrics retain their prior independent meanings.

### Gallery, static checks and remaining gates

The shared native `--layers` / browser `?scene=layers` gallery now includes all 29
native modes in source enum order, with both bounded child and actual root
advanced destinations, nonzero/fractional bounds, translucent overlap, group
opacity, outer masks/clips and following painter commands. It retains optional
explicitly loaded bounded text, shadows and empty-target ownership, and adds a
nonempty clip disjoint from an advanced layer to exercise suppression/restoration.
Both hosts expose root and captured-subtree advanced counters.

Implementation-first checks are strict native all-target/all-feature, browser
WASM and CPU-only Clippy; formatting, whitespace and browser JS syntax; and the
existing offline Naga test over all thirteen original shader modules. No shader
source, compiler default, driver, dependency version or package contract is
changed. Complete matched source parameter/pass/bounds/rejection/float controls,
all-format and all-mode live GPU/browser pixels, opacity/mask/clip/empty cases,
repeated-frame and multi-target ownership, cancellation/device loss, allocator/
process/frame benchmarks and full package/source/application qualification are
deferred to integrated validation. The earlier browser and performance failures
remain open. Uniform-backdrop optimization, full cache/revision/residency policy,
source-specific ROP/AA/background/Clear operations, geometry/brush/picture masks,
shader/image filters and complete native semantic transport remain unfinished.


## Ordered target-storage Clear implementation (2026-11-09)

The original storage operator is now connected to the retained Rust command
stream, ordinary/bounded captures and destination-aware layer passes. The public
contract research at the start of this ledger continues to govern independent
paint/clip state, immutable source records, device domains and retained resources.
This is a direct port of ProGPU's original operator, not a new clear algorithm or
an optimization claim. Original sources consulted, SHA-256:

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Backend/progpu_native_target_clear.cpp` | `ff98709c7fb03998e23edc0ce5126ef47435d2a71b48250b3360e20ed7229f31` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/tests/progpu_native_target_clear_fixture.hpp` | `7f94a584318a8cc3f3ac91fa42c8304fa3e518dc781904f39a7a2f8d9220c33e` |
| `src/ProGPU.Backend/Shaders/TargetClear.wgsl` | `17ed457c5840152c4578d5262c56d9bdf3b673fa518e9589687e813e938eafc8` |

`docs/native-target-storage-clear.md` retains the complete source AA/background,
ordinary-layer ownership, depth, provider, package and pixel gates. Its original
fifteen native fixtures have been inspected, not replayed or qualified by this
Rust checkpoint. Existing Windows/source and provider receipts remain independent.

### Original CPU records and admission

`ClearColor` validates finite straight RGBA and alpha 0..=1 before recording,
retaining original component bits, signed zero and finite negative/HDR RGB. Its
private storage prevents bypassing validation through a public POD constructor.
The original independent RGB multiplication uses four SIMD lanes; Preserve uses
the supplied alpha, while explicit actual-target Ignore uses one. The source
alpha slot is assigned directly, with no clamp, quantization or inherited opacity.
`Scene::clear_target` appends one typed command without geometry, brush, hit data or
child traversal. CPU and compiled iterators expose original colors in order.

Complete scope preflight rejects direct per-draw masks around Clear even when the
binary clip is empty, before layer captures or text atlas mutation. Ordinary
isolated child targets remain independent of outer final masks/opacity. Binary
clips retain the existing original physical `ceil(double(edge)-0.5)` rule and
bounded target localization; no AA coverage is applied to storage Clear itself.
An empty resulting scissor suppresses the operation and its upload. The current
scene API uses alpha-preserving targets. The explicit low-level encoder accepts an
actual caller-owned texture with `TargetAlpha`; it inspects its base-level extent,
format, dimensionality, layer/sample count and attachment usage. Same-device
ownership remains a wgpu contract, never inferred from numeric dimensions.

### Original storage pass and ordering

The unchanged canonical `TargetClear.wgsl` is module fourteen. The lazy pipeline
retains its original nonempty shader/pipeline identities, fragment uniform of
exactly 16 bytes, three-vertex fullscreen primitive, no blend, all-channel writes
and single-sample target. Every nonempty occurrence allocates its own original
Uniform/CopyDst buffer, uploads exactly 16 bytes and binds that actual buffer.
This preserves separate values for multiple encodes before submission, including
warm replay. It deliberately does not turn the original upload contract into a
cached mutable or preparation-only uniform.

Encoding loads/stores the actual current attachment, sets its actual viewport and
physical binary scissor, then issues one draw. The ordered span encoder splits at
Clear records and carries the original scope state into subsequent draws. A first
Clear still retains the caller's initial attachment load/clear outside its scissor.
Consecutive clears retain distinct ordered operations. With destination-aware
blending, Clear targets the actual owned root between source/blend/replace passes;
bounded captures use their own physical target. No CPU readback, extra submission,
completion wait, explicit resource destruction or destructor queue work is added.
Wgpu retains actual buffer/bind-group dependencies through command submission.

`ReplayMetrics` separates encoded Clear uploads/allocations from earlier geometry,
mask, image and uniform preparation. `render_with_metrics` returns the real queue
submission plus these counts; `encode_with_metrics` reports commands before caller
submission, and `PreparedScene::replay_metrics()` describes the frozen plan.
Captures retain their source replay counts and `LayerMetrics` accumulates captured
subtree work without recounting it on root replay. The browser reports its last
actual encoded frame independently of these preparation/capture totals. These are
command counters, not GPU completion, allocator profiling or parity evidence.

### Shared sample and remaining qualification

Native `--clears` and browser `?scene=clears` use the same twelve authored panels:
opaque/transparent/partial alpha, fractional/exact-half/adjacent float binary clips,
nested and empty clips, bounded source clears with final layer opacity/rounded
masks, clears before/between/after advanced modes, source shadows, optional local
font and subsequent draw restoration. No sample claims to reproduce the original
native fifteen-case source inventory or conditional source AA controls.

Implementation-first checks: strict all-target/all-feature native Clippy, browser
WASM and CPU-only Clippy; formatting/whitespace/JS syntax; the existing offline
Naga validation across fourteen canonical modules. Complete original records,
source admission and pixel corpus, live native/browser execution, repeated/unsubmitted
encodes, all target formats, explicit Ignore-alpha output, lifetime/cancellation,
performance/Instruments and package/application gates remain deferred. No original
shader, dependency, driver, compiler default or qualification gate is changed.

Source AA background initialization inward of the nearest ordinary layer and
coverage resolve at pop remain unfinished. They require actual parent storage at
painter position and must not be fabricated by the already-captured ordinary-layer
API. Independent per-axis DPI/viewport, scene Ignore-alpha owners, nested picture
transport, full source depth/semantic budgets and source opacity/Clear policies
also remain separate work. The surrounding full mask/filter/cache/archive and
platform port remains incomplete.
