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
