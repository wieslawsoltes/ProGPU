# ProGPU Rust

Native Rust port in progress. This is not yet a parity-qualified release.
The Cargo manifest is at the repository root so packaged crates can embed the
same canonical shader files as C# and C++ without copied shader forks.

Run `cargo test --locked` and `cargo clippy --all-targets -- -D warnings`.
The CPU geometry API also builds with `--no-default-features`.

```sh
# Desktop window (add -- --frames 4 for a bounded presentation smoke test)
cargo run --locked --features window --example winit
# Actual adapter pixels and lifetime checks; missing adapters fail this gate
cargo test --locked --test gpu_rendering -- --ignored --nocapture
# Original C++ byte-level differential oracle (requires clang++ and checkout)
cargo test --locked --test native_geometry -- --ignored --nocapture
cargo test --locked --test font -- --ignored --nocapture
cargo test --locked --test outline -- --ignored --nocapture
cargo test --locked --test composite -- --ignored --nocapture
cargo test --locked --test cff -- --ignored --nocapture
cargo test --locked --test variations -- --ignored --nocapture
cargo test --locked --test item_variation -- --ignored --nocapture
cargo test --locked --test gvar -- --ignored --nocapture
cargo test --locked --test horizontal -- --ignored --nocapture
cargo test --locked --test vertical -- --ignored --nocapture
# Simple text also requires .NET 10 SDK for the original managed-source control
cargo test --locked --test simple_text -- --ignored --nocapture
# Strict Unicode + original C++/C# properties; .NET 10 SDK and checkout required
python3 eng/progpu-generate-rust-unicode.py --check
cargo test --locked --test unicode -- --ignored --nocapture
cargo test --locked --test bidi -- --ignored --nocapture
# Independent official Unicode 17 levels (hash-pinned temporary downloads)
python3 eng/progpu-test-rust-bidi-conformance.py
cargo test --locked --test grapheme -- --ignored --nocapture
python3 eng/progpu-test-rust-grapheme-conformance.py
cargo test --locked --test line_break -- --ignored --nocapture
python3 eng/progpu-test-rust-line-break-conformance.py
cargo test --locked --test normalization -- --ignored --nocapture
python3 eng/progpu-test-rust-normalization-conformance.py
python3 eng/progpu-test-rust-normalization-profiles.py
python3 eng/progpu-generate-rust-normalization-profiles.py --check
cargo test --locked --test layout -- --ignored --nocapture
cargo test --locked --lib -- --ignored --nocapture
# Release baseline; reported CPU submission latency excludes the GPU wait
cargo run --release --locked --example retained_benchmark
cargo run --release --locked --example retained_benchmark -- --paths
# CPU-only original cmap + metrics workload (supply a real font file)
cargo run --release --locked --no-default-features --example font_benchmark -- FONT_PATH
# Matched strict decode + script itemization baseline with reusable buffers
cargo run --release --locked --no-default-features --example unicode_benchmark -- mixed 16 4096
cargo run --release --locked --no-default-features --example bidi_benchmark -- mixed 2048
cargo run --release --locked --no-default-features --example grapheme_benchmark -- emoji clusters 2048
python3 eng/progpu-benchmark-rust-line-break.py
python3 eng/progpu-benchmark-rust-normalization.py
python3 eng/progpu-benchmark-rust-normalization-profiles.py
python3 eng/progpu-benchmark-rust-layout.py
```

The renderer accepts a device/queue pair and produces immutable prepared scenes.
Prepare again when geometry, DPI or physical dimensions change; reuse a prepared
scene for stable frames. `Scene` records analytic and filled-path commands;
`Scene::compile` performs CPU-only placement and atlas planning at the actual DPI.
`Renderer::prepare_compiled` uploads that frozen generation and rasterizes its
unique paths into one owned atlas. Replay uses one indexed draw with original
painter order and no further path rasterization or geometry/frame uploads.
The optional path pipeline is created only when a path scene is prepared.
The example handles resize, scale changes, zero extents, suspend/resume and lost
surfaces. Full device-loss recovery is still pending. GPU/reference tests are
explicit opt-in tests so CPU-only consumers do not need a graphics adapter or C++
compiler; both must run in the port qualification lanes.

## Browser WebGPU

From the source checkout:

```sh
rustup target add wasm32-unknown-unknown
cargo install wasm-bindgen-cli --version 0.2.130 --locked
eng/progpu-test-rust-browser.sh
```

The script builds the browser example, generates matching bindings, installs the
pinned Playwright browser, serves an ephemeral loopback URL and runs real WebGPU
checks. Set `PROGPU_RUST_WASM_BINDGEN` to an isolated matching CLI executable if
another version is already installed. Linux hosts also need Playwright's Chromium
system libraries (`npx --yes playwright@1.62.1 install-deps chromium`). No missing
adapter or shader failure is skipped. On Linux the harness explicitly enables
Chromium Vulkan compositing with its SwiftShader driver. Linux CI runs a visible
browser under Xvfb: `PROGPU_RUST_BROWSER_HEADED=1 xvfb-run -a eng/progpu-test-rust-browser.sh`.
That configuration passes presentation on Ubuntu ARM64 and hosted x64; headless Linux still
produces a black canvas and is not qualified. The receipt records the actual
mode and launch flags. Device loss fails the run, with no automatic fallback.

The reusable site is in `artifacts/progpu-rust/browser`; serve that directory over
localhost HTTP or HTTPS to explore it interactively. The desktop sample uses
winit; the browser sample uses an HTML canvas and requestAnimationFrame with the
same renderer and scene. It serializes asynchronous preparation, physical resize
and tests so mutable WASM borrows cannot overlap. Readback is confined to the
validation bridge; normal canvas replay performs one retained draw.

The browser gate compiles all six canonical shader compositions and checks
204,800 authored pixels across DPR 1 and 2, including three physical scene DPIs,
all thirteen grayscale contrast values, cold/warm equality, later-generation
retirement, resize, and zero-extent suspension/resume. It also requires actual
compositor screenshot pixels for the background, analytic shapes and GPU paths;
offscreen success alone cannot pass presentation. PNGs and a compact actual
adapter/result receipt are written under `artifacts/progpu-rust/browser-results`.
Chromium software WebGPU is a reproducible additional gate; it does not establish
hardware-browser, original source-font, or complete engine parity. A WASM CPU control also verifies source-retained simple glyph points, instructions,
exact quadratic/composite path bytes, CFF1 Type 2 closure and truncated-record
rejection, CFF2 blended path bytes and variation metadata. CFF1 and variable
CFF2 paths also pass through retained GPU rendering at three
DPIs. Text/image shader
compilation alone does not implement their pending host stacks.

Builds and browser tools stay outside the distributable crate. Delete verified
`target/package` extractions and obsolete test outputs when no longer needed;
keep compact qualification receipts and reusable active dependencies. Use a fresh
disposable `CARGO_TARGET_DIR` for extracted-package checks: normalized archive
mtimes can otherwise reuse a stale compiled library in a shared local target.

## Port ledger

| Domain | Implementation | Remaining qualification |
| --- | --- | --- |
| Shader composition | Compile-time canonical vector, path, glyph, binary glyph, text, image modules | Device pipeline and pixel tests for each host path |
| Analytic geometry | Native layout and all three analytic shape expansions; 288 native differential cases | Broader managed/native corpus |
| wgpu resources and rendering | Immutable mixed analytic/path scenes and per-generation atlas bindings; GPU pixels, owner-domain and replay lifetime controls | Cross-provider pixel corpus, device-loss recovery |
| Browser sample and tests | WebAssembly canvas host sharing the winit scene; Chromium WebGPU shader, pixel, DPI/resize and retained replay gates | Additional browser/hardware matrix, device-loss recovery, complete stack |
| winit sample | Native window, DPI/resize, suspend/resume, lost surface handling; mixed Metal and Linux Vulkan/Xvfb presentation pass | Windows runtime and interactive lifecycle controls |
| Paths, strokes and Boolean geometry | All six path segment records; canonical batched masks and retained shared atlases; native extent grouping and physical path placement | Strokes, Boolean/signed programs and original corpus |
| Fonts and outlines | Owned SFNT/TTC, native metrics and cmap 4/12/13/14, standalone reconstruction, simple/composite TrueType records and recursive unhinted paths; native source-arithmetic differential controls | Compressed containers, interpreter, source policies and compiler-default native pixel qualification |
| CFF outlines | Owned CFF1/CFF2 containers, encoded INDEX/FDSelect, private/global subroutines and bounded Type 2 execution; original CFF2 variation blends and source-owned closed paths | Full text integration, original provider/source pixels and broader variable-font corpus |
| Font variation metadata | Allocation-free fvar/avar, item variation stores, encoded deltas and delta-index maps; original fixed/design and CFF2 selection contracts; native differential tests | Full text/metrics integration and compiler-default arithmetic qualification |
| TrueType variation decoding | Owned gvar tables/regions, packed iterators, circular IUP, complete payload preflight, simple-point accumulation, component offsets, horizontal phantom deltas and recursive varied outlines; 462,131 native source-arithmetic cases, twelve browser controls per DPR and three-DPI GPU varied paths | Vertical metrics integration and compiler-default arithmetic qualification |
| Horizontal variable metrics | Legacy HVAR advances, independent strict LSB preparation, cached source-owned scalar/map generations, original base fallback and HVAR-over-gvar precedence; 205,966 native cases and eight browser controls per DPR | Full source placement and compiler-default arithmetic qualification |
| Static vertical metrics and VVAR | Original vhea/vmtx/VORG and static glyf origin fallbacks; independent strict owned VVAR with complete preflight and atomic batches; native differential and ten browser controls per DPR | Source CFF/sideways integration, default arithmetic and full provider/package qualification |
| Simple UTF-16 glyph runs | Owned source/text/cluster/glyph generations, allocation-free mapping and exact scaled advances; 30,086 C++ cases and 10,683 original C# controls; ten browser controls per DPR | Full Unicode/OpenType shaping, bidi, fallback, paragraph layout and text GPU replay |
| Unicode decoding and properties | Strict UTF-8/UTF-16, exact scalar/source records, owned text, initial script runs and nine frozen property inventories; 552,778 C++ cases, 1,114,132 C# property values and fourteen browser controls per DPR | Full shaping/layout and GPU text |
| Bidi level resolution | Complete original embedding/isolate/bracket/weak/neutral/implicit/L1 resolver; reusable caller scratch and owned source generations; 168,077 C++ cases, 102,541 C# controls, Unicode 17 level conformance and fifteen browser controls per DPR | Line/cluster L2 visual ordering, inline direction policies, shaping/layout integration and actual text pixels |
| Grapheme segmentation | Original Unicode 17 state and separate managed compatibility; immutable source clusters and allocation-free UTF-16 starts; 1,888,801 native controls, 89,921 managed controls and 766 official fixtures | Word boundaries, fallback/shaping/layout integration and retained interaction |
| Line breaking | Original Unicode 17 and separate native EDIT selection profile; owned source, caller buffers and exact failure/tail behavior; 2,262,599 C++ cases, 1,688,365 C# controls, 19,338 official cases and fourteen browser checks per DPR | Word selection, width fitting, hyphenation, shaping/layout integration and text pixels |
| Canonical normalization | Original frozen plan plus explicit Unicode 15.1/16.0/17.0 source profiles; native source/tail semantics and original output metadata preserved; 3,668,863 native controls, 4,476,001 managed controls on both macOS and Ubuntu, 1,181,460 profile fixture checks, 36 browser controls per DPR | Source runtime selection and full shaping integration; performance and hosted/platform qualification |
| OpenType common tables and GDEF | Borrowed/owned coverage, classes, lazy lookup validation, GDEF classification/mark sets and exact independent blocklist; 6,415,296 native and 5,374,312 actual C# queries; 26 browser controls per DPR | Feature selection, lookup accelerators, GSUB/GPOS execution, shaping plans and full text integration |
| Images, brushes, clips, layers and effects | Pending | Complete retained resource and compositing contracts |
| Scene compiler, interaction and archives | Ordered CPU compilation, immutable source/atlas generations and within-frame raster sharing | Interaction, archives, culling, cross-generation cache eviction and transport |
| Performance and distribution | Reproducible retained Release baseline | Matched C#/C++ measurements, platform gates and package consumer |

No C/C++ FFI renderer, third-party text/raster engine, or CPU raster substitute is
used. Existing managed/native provider, source application and package gates
remain independent and mandatory.

The new Rust CI runs CPU/shader validation and package verification on Linux,
macOS and Windows. Linux additionally requires the native C++ oracle, Vulkan
software-adapter pixels and Xvfb winit presentation; macOS also runs the C++
oracle. A separate Linux Chromium job requires the WebAssembly pixel and
presentation harness. These are additive gates. They do not qualify the existing pinned
wgpu-native/Dawn packages, Windows compiler patches, or original source adapters.

## Provenance

Initial source baseline: `cbfd65c6a69c49e10a5dbe2fe35967311efd80cc`.
Geometry ports `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp`
and `progpu_native_geometry_analytic.hpp`; GPU records and projection follow
`progpu_native_gpu_records.hpp` and `progpu_native_pipeline.cpp`.
Shader compositions follow `src/ProGPU.Backend/Shaders.cs` and native CMake.
Path records and validation follow `progpu_native.h` and
`src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp`;
batch grouping follows `progpu_native_path_dispatch.hpp`. Scene path placement,
phase, atlas shelves and exact texel-load admission follow
`progpu_native_path_execution.cpp`, `progpu_native_gpu_records.hpp` and
`progpu_native_path_pixel_mapping.hpp`. Analytic scene padding retains the
original `progpu_native_vector_execution.cpp` formula. Explicit grayscale contrast
retains `progpu_native_glyph_coverage_transfer.hpp` metadata and the canonical
shader transfer; this is not source font or Microsoft pixel qualification.
The compute kernel is
the unchanged canonical `cs_main_single_path` with operation zero.
Font access ports `src/ProGPU.Native/src/Text/progpu_native_sfnt.cpp` and
`Text/Metadata/progpu_native_sfnt_standalone.cpp`. `SfntFace` owns shared immutable
bytes and supports explicit collection selection; missing tables, unmapped glyphs,
and unsupported variation sequences retain distinct results. A cmap lookup is
not shaping, and raw glyph bytes are not a hinted or rendered outline.
Only original ProGPU implementation is ported; external crates remain normal
Cargo dependencies with a committed lockfile.

Simple TrueType records decode through `SfntFace::simple_glyph`. The returned
immutable geometry owns the selected face, preserves full flags/instructions and
lowers to original line/quadratic segments. `glyph_requirements` preflights without
allocation. Single-point contours retain their points and emit no path segments;
composite and empty records retain distinct classifications. This is unhinted
design geometry, not shaped text or a source raster-policy implementation.

Static vertical APIs port `Text/Font/progpu_native_font_vertical_metrics.cpp`.
Neutral VVAR instances port `Text/progpu_native_vvar.cpp` and reuse the original
item-store arithmetic. They retain exact source/coordinate ownership, validate
all raw VVAR directory records and mappings, and expose optional deltas separately
from static origin selection. `VerticalMetricsInstance::variations_into` preserves
the caller's tail and rejects the whole batch before writing. These APIs do not
select CFF source control bounds or admit sideways rendering. A matched baseline
is available with `cargo run --release --locked --no-default-features --example
vertical_metrics_benchmark -- FONT_PATH FACE_INDEX NORMALIZED_COORDINATE`.

`text::SimpleGlyphRun` owns its original font and UTF-16 source; the free
`build_simple_glyph_run_into` / `fill_simple_glyph_advances` functions reuse caller
buffers without allocation. Unpaired surrogates, control mapping, soft hyphens,
first-cluster ownership, original scale order and ties-to-even rounding match
`progpu_native_sfnt_simple_glyph_shaper.cpp`. Advance metrics align with glyph
occurrences; sideways selects supplied heights and does not rotate outlines.
This is the original simple-run API, not full shaping. The managed differential
control links `src/ProGPU.Text/SfntSimpleGlyphShaper.cs` directly; set
`PROGPU_RUST_DOTNET` when using an isolated .NET 10 SDK. No C# or C++ runtime bridge
is part of the Rust crate. A reusable-buffer baseline is available with
`cargo run --release --locked --no-default-features --example simple_text_benchmark
-- FONT_PATH FACE_INDEX REPEATS` (repeats 1..4096).

Browser failures retain the active phase and console errors in `results.json`.
For bounded submission/completion diagnostics, set
`PROGPU_RUST_BROWSER_DIAGNOSTICS=1` when running the browser script. CI runs the
original gate first and only adds a separate diagnostic observation after failure;
that observation cannot replace the failed result. `DEBUG=pw:browser` retains
Chromium process-exit evidence in the job log. Compiler settings, launch flags,
shaders, pixel checks and deadlines are unchanged.

The line-break module exposes logical scalar boundaries and resolved classes,
with the original context-sensitive rule order and separate EDIT projection.
It preserves scratch-prefix writes on a later invalid scalar while never
publishing break output on failure. Owned text keeps the original encoding and
positions. The default profile matches the original managed resolver; this does
not supply line fitting, dictionary segmentation, shaping or text rendering.

Browser checkpoint `8e5702a52` passes hosted Linux/Windows and Vulkan/winit,
but both original and diagnostic browser runs time out at final DPR 2 completion.
The observed run has four pending submissions and no reported device loss;
prior completions take 22–29 seconds. Compact evidence is retained in
`perf/browser-8e5702-hosted.json`. This narrows investigation without closing the
original gate or changing deadlines, shaders, compiler flags or pixel assertions.

Canonical normalization ships the exact original `UnicodeNormalizationData.bin`.
`NormalizationData` borrows a validated plan, `NormalizationPlan` owns one, and
`NormalizedText` retains original encoded source alongside the normalized records.
The native API admits U+FFFE; explicit `new_managed` / `normalize_managed_into`
preserve the original managed canonical call's rejection. Both use the frozen
ProGPU plan, with no promise to track arbitrary host/runtime Unicode versions.
The managed shaper's grapheme and script exceptions remain separate integration.

`font::layout` exposes validated borrowed views and immutable owned GSUB/GPOS
and GDEF generations. `OwnedLayoutTable::from_face` and `OwnedGdef::from_face`
retain the selected `SfntFace` without copying its table bytes. Lookups validate
all declared subtable offsets on demand; their raw types/flags remain unchanged.
GDEF blocklist policy is explicit and independent of parsing. These APIs provide
shaping data boundaries; they do not yet apply substitutions or positioning.

Hosted normalization checkpoint `672d632b9` passes Windows, browser and GPU;
Linux's actual managed comparison fails at U+105C9, while its native comparison
passes. The frozen resource decomposes that scalar to U+105D2 U+0307, whereas
that runtime returned its original two UTF-16 units. macOS was cancelled. This
runtime-repertoire gap remains open; no oracle, runtime default or gate is changed.

A reproducible diagnostic, `python3 eng/progpu-audit-rust-normalization-runtime.py
--output /tmp/progpu-normalization-runtime.json`, rebuilds the original managed
generator under the current runtime defaults. Local Ubuntu/ICU 74.2 lacks 20
of the frozen plan's decomposition records and 20 composition pairs; macOS
reproduces the frozen resource exactly. Compact `perf/normalization-runtime-*.json`
receipts retain the differences. CI runs this audit after an oracle failure while
preserving the failed result. Runtime profiles and full canonical ordering remain
unqualified; no test input is excluded.

Explicit source normalization uses `ManagedNormalizationProfile` with
`NormalizedText::with_managed_profile`, `managed_profile_capacity`, or
`normalize_managed_profile_into`. Choose Unicode15_1, Unicode16_0 or Unicode17_0
from evidence about the source runtime. The library does not guess from an OS
or SDK version. Source classes govern ordering/composition; published records
retain ProGPU's original class/script metadata and exact source-range contract.
The owned result retains its chosen profile identity. Existing native, custom-plan
and `new_managed` frozen-plan entry points keep their original behavior.

The managed differential gate observes candidate source facts, then compares the
entire original corpus plus every scalar between combining marks and every
original composition pair. No original input is excluded. Local macOS selected
Unicode17_0 and Ubuntu/ICU 74.2 selected Unicode15_1; both pass 4,476,001 controls.
A new source/runtime still requires full comparison. Script exceptions, font
fallback and complete managed shaping are independent unfinished work.

Profile facts come from hash-pinned UnicodeData files, with no imported engine
implementation. The generator verifies every original canonical decomposition,
the exact older runtime plan inventory, and the original Unicode 17 class table.
The crate's code is MIT; the added Unicode facts carry the bundled
`UNICODE-LICENSE.txt` notice. Cargo declares `MIT AND Unicode-3.0`.
