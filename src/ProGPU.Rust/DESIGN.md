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
