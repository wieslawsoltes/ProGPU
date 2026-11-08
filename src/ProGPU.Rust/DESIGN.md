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
