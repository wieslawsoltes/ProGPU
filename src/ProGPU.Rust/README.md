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
cargo test --locked --test digest -- --ignored --nocapture
cargo test --locked --test selection -- --ignored --nocapture
cargo test --locked --test managed_feature_selection -- --ignored --nocapture
cargo test --locked --test managed_context -- --ignored --nocapture
cargo test --locked --test gsub -- --ignored --nocapture
cargo test --locked --test managed_gpos -- --ignored --nocapture
cargo test --locked --test managed_positioning -- --ignored --nocapture
cargo test --locked --test managed_variations -- --ignored --nocapture
cargo test --locked --test managed_names -- --ignored --nocapture
python3 eng/progpu-test-rust-indic-syllables.py
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
python3 eng/progpu-benchmark-rust-digest.py
python3 eng/progpu-benchmark-rust-selection.py
python3 eng/progpu-benchmark-rust-managed-features.py
python3 eng/progpu-benchmark-rust-managed-context.py
python3 eng/progpu-benchmark-rust-managed-gpos.py
python3 eng/progpu-benchmark-rust-managed-positioning.py
python3 eng/progpu-benchmark-rust-managed-variations.py
python3 eng/progpu-benchmark-rust-managed-names.py
PROGPU_RUST_ACTIONS_INDIC_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py
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
| OpenType common tables and GDEF | Borrowed/owned coverage, classes, lazy lookup validation, GDEF classification/mark sets and exact independent blocklist; 6,415,296 native and 5,374,312 actual C# queries; 26 browser controls per DPR | Complete shaping-plan integration and original text pixels |
| Lookup accelerators | Original five-mask native and distinct three-mask managed filters; exact single coverage and owned format-3 context preparation; 39,859 C++ records / 57,233,864 bytes and 15,728,640 C# glyph queries / 201,347,072 bytes; 15 browser controls per DPR | Complete shaping sequence, native positioning integration and full text pixels |
| Native feature selection | Exact script/language fallback, required/requested/excluded features, ordered variation records and owned selections; 50,956 native controls / 7,157,396 bytes; 1,728 actual C# shared-policy controls; 16 browser checks per DPR | Complete staged shaping and native-selection performance gap |
| Current managed feature policy | Separate immutable UTF-16 request, full fallback/value/global precedence, variation substitutions and optional managed digest; 327,015 actual C# controls / 27,595,740 bytes; 20 browser checks per DPR | Complete script-stage orchestration and full text pixels |
| Managed context and ordered matching | Owned raw managed format-3 contexts, exact weak coverage admission and original glyph/indexer/skip rules; 56,996 actual C# source records, 22,558,608 coverage queries and 1,191,586 ordered matches; 70 browser checks per DPR | Complete shaping/layout/GPU text integration and large-query baseline gap |
| Native GSUB execution | All eight lookup types; contextual formats 1–3; caller-owned 32-byte glyph records, original errors/tails, nested state, GDEF and metadata; 117,973 C++ records / 60,322,260 bytes and 3,968 actual C# shared-policy records / 975,232 bytes | Native GPOS/device positioning, complete shaping, fallback/layout/GPU text and performance qualification |
| Current managed positioning | All nine raw GPOS types, complete attachment state, owned feature/digest plan and layout variation dispatch; 32,008 complete C# plan cases and 760,086 exact delta queries; 151 browser controls per DPR | Metric/default-ignorable preparation, complete varied outlines/vertical origins, native device GPOS, fallback and full shaping/GPU text |
| Current managed variation data | Complete original fvar/avar/gvar/HVAR/GDEF/MVAR admission, normalized immutable instances, lazy tuple cache, simple/component/phantom deltas and MVAR/HVAR/layout queries; 8,772 C# cases / 8,192,752 exact bytes; 96 browser controls per DPR | Full varied-outline/metrics integration and query/frame performance |
| Current managed font names | Original name-table decoding, UTF-16 character ranking, stable ordinal deduplication and real axis-name resolution; 236,128 C# cases / 12,151,088 exact bytes; 93 browser controls per DPR | Broader discovery/source policies, complete outline/metric preparation and end-to-end performance |
| Current managed font and preparation | Owned font/cache/metadata, nominal/symbol/UVS mapping, explicit runtime grapheme and normalization profiles, complete original buffer creation | Varied outlines, source raster policies, fallback and complete shaping orchestration |
| Common managed script actions | Original dotted circle, directional fallback, Hangul, modified combining order, Hebrew, USE diacritics, Thai/Lao, fractions, Arabic joining, presentation fallback, vowel constraints, Khmer preparation, Myanmar preparation/reordering, USE preparation/recording/reordering, Indic preparation and stage-state actions; 1,657,647 complete C# cases plus complete raw property queries | Indic initial/final reordering, full plan sequencing, positioning initialization and full text pixels |
| Indic/Khmer/Myanmar/USE properties | Typed original Indic category/position and USE classification; 2,228,228 actual C# queries plus all 65,536 raw words match native and browser output | Indic initial/final reordering and complete shaping-plan integration |
| Khmer preparation | Independent syllable parser; original unsafe flags, broken circles, full-record reordering, cluster merging and feature masks; 1,398,562 private parser observations plus full action controls | Complete shaping-plan sequencing, font GSUB/GPOS integration and GPU text |
| Myanmar preparation/reordering | Separate original stages; independent syllable parser, kinzi/base positions, stable whole-record sorting, matra ordering and bounded scratch reuse; 12,981,319 parser cases and full action controls | Full shaping-plan sequencing, font GSUB/GPOS integration and GPU text |
| USE stages | Original filtered-index preparation, repha/pre-base recording, topographical forms, dotted circles and full-record reordering; independent parser matches 5,870,757 cases | Full shaping-plan orchestration, Indic stages, font integration and GPU text |
| Indic preparation | Original category/position metadata, independent six-family syllables and signed unsafe-break flags; 4,768,385 parser cases and complete action controls | Font-dependent initial/final reordering, complete shaping-plan sequencing and GPU text |
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

On Linux the separate diagnostic can additionally set
`PROGPU_RUST_BROWSER_GPU_STACKS=1`. It samples CPU counters/wait channels three
times for the test's unique live descendant GPU process and captures one bounded
GDB thread backtrace during final DPR 2 completion. `gpu-process.json` records
actual process generations and any unavailable debugger/permissions. Attachment
briefly pauses the process and can perturb timing; this observation never
qualifies performance or replaces the original gate. No heap/core dump or browser
profile is retained. CI enables it only in the failure-only diagnostic run.

Selection validates the actual browser executable's device/inode, owned ancestry
and original PID generation. Both a separate exact GPU-role argument and a packed
title beginning with the exact executable path followed by the GPU-role token
are supported; an embedded marker alone is insufficient. Identity is rechecked
before sampling and attachment. `capture-gpu-test.py` covers misleading titles,
different executable images, reparenting and PID reuse. For an isolated Linux
diagnostic with an already installed browser, run
`python3 src/ProGPU.Rust/browser/capture-gpu-live-test.py --executable /path/to/chrome --output /tmp/progpu-gpu-probe.json`.
It owns and cleans its temporary browser/profile and records bounded samples and
debugger output. This probe does not run the rendering gate or qualify its pixels.

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
shaping data boundaries. `LayoutTable::apply_gsub` now executes the original native
substitution contracts; GPOS and complete shaping remain separate.

Hosted normalization checkpoint `672d632b9` passes Windows, browser and GPU;
Linux's actual managed comparison fails at U+105C9, while its native comparison
passes. The frozen resource decomposes that scalar to U+105D2 U+0307, whereas
that runtime returned its original two UTF-16 units. macOS was cancelled. This
historical frozen-plan mismatch remains recorded; the additive profile API below
addresses explicit source-data selection without changing that frozen API.

A reproducible diagnostic, `python3 eng/progpu-audit-rust-normalization-runtime.py
--output /tmp/progpu-normalization-runtime.json`, rebuilds the original managed
generator under the current runtime defaults. Local Ubuntu/ICU 74.2 lacks 20
of the frozen plan's decomposition records and 20 composition pairs; macOS
reproduces the frozen resource exactly. Compact `perf/normalization-runtime-*.json`
receipts retain the differences. CI runs this audit after an oracle failure while
preserving the failed result. The subsequent explicit profiles below receive full
canonical comparisons; no original test input is excluded.

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
`UNICODE-LICENSE.txt` notice. Microsoft USE specification facts retain their
MIT notice in `MICROSOFT-FONT-TOOLS-LICENSE.txt`. Cargo declares `MIT AND Unicode-3.0`.

Lookup preparation preserves the original native five masks and the independent
managed three masks; `GlyphSetDigest::managed()` projects the exact managed state.
Their positive results remain approximate and can differ. Raw lookup digests keep
the original offset interpretation and require independent executor eligibility.
Exact single-coverage preparation and complete format-3 context preparation retain
their own stricter coverage validation. Invalid lookup headers are errors; an
unsupported optional accelerator is `None`, which never approves lookup execution.

`LayoutTable::build_context_into` validates the entire inventory and both caller
capacities before writing. `PreparedContext` owns the selected layout/font generation,
ordered descriptors and coverage offsets/digests; its borrowed exact coverage queries
do not reparse or copy font bytes. Full ordered glyph traversal and GDEF filtering
remain with the future executor, rather than being inferred from these set filters.

`SelectionRequest` selects the original native script/language/feature policy.
`LayoutTable` exposes allocation-free capacity, bulk/excluded, single-feature and
membership queries. Bulk success sorts unique IDs; single-feature success retains
source order. Required features survive exclusions. Capacity preflight intentionally
does not validate lookup IDs: a later invalid ID can retain the original written
prefix on failure, while `LookupSelection::new` publishes an owned result only on
complete success. Owned selections freeze the actual table/font generation, exact
requested/excluded tags and normalized coordinates.

Feature variations preserve first matching records, signed inclusive conditions,
first duplicate substitutions and original malformed-record behavior. Invalid
alternate pointers skip that feature rather than selecting its original table.
These native APIs do not select shaping defaults or translate language names.
The distinct current managed selection API below supplies C#'s additional policies;
the 1,728 shared-policy comparisons qualify only the common native inventory.
Rust's larger native-selection baseline is slower than C++; the compact
receipt records that gap and the different work performed by the C# control.

`ManagedLookupSelection` retains original raw table/font bytes and a frozen
`ManagedSelectionRequest`. Four-unit UTF-16 `ManagedFeatureSetting` tags preserve
ordinal identity and arbitrary signed values. It follows original C# last-setting,
required/global precedence, exact/DFLT/dflt/latn fallback, language normalization and
last-duplicate alternate substitution. Null matched substitutions still report
active variation use. It preserves managed partial-table admission independently
of the stricter native parser. Selected IDs do not imply executable lookup validity.
Original source indexing exceptions become atomic `FontError::InvalidFace` errors.

The actual original method passes 327,015 records / 27,595,740 metadata/digest bytes,
including actual font-instance coordinates, every fixture prefix, 25,000 mutations
and an exhaustive UTF-16 language-folding witness. Browser controls cover both DPRs,
signed values, global/Jamo rules, distinct native/managed fallbacks, reversed digest
ranges and extreme 32-bit variation offsets. The eight-workload baseline verifies
all selected metadata hashes; 512 static features measure p50 1889.542 µs Rust and
2715.100 µs C#. Ownership and optional context work differ, and compilation activity
was present: these are diagnostic construction measurements, not full shaping or
stable speedup qualification. The later context stage below retains its own evidence;
full lookup execution and staged shaping remain pending.


`PreparedManagedContext` retains original raw table/font bytes and optional
format-3 context metadata. `ManagedLookupSelection::context(position)` borrows
prepared context for that selected entry; absent contexts require no separate
allocation. The returned view preserves original backtrack/input/lookahead order,
lookup flags, three-mask filters and managed linear/binary coverage membership.
Unsorted/overlapping source inventories retain their original query behavior.

`ManagedContextInput` borrows the original accelerator fields from an immutable
glyph generation, freezes active joiner/syllable state and builds the source buffer
digest once. Existing source digests can be reused with `with_digest`; they must
cover all current glyph IDs. Ordered queries allocate nothing. Input/context
joiner rules differ, and the original first-position indexer maps out-of-syllable
glyphs to 0xffff. Flagged GDEF contexts keep the original conservative fast path
after digest intersection. A positive result still requires exact lookup execution.
This API neither manufactures complete shaping records nor supplies a full shaper.

The actual C# context parser, matcher and GlyphSubstitutionBuffer pass 95,829,796
exact bytes, including every scalar through ordered matching, raw visibility and
syllable bytes, mutations and every fixture prefix. Eighteen preparation/query
baseline workloads match complete metadata/result bytes. At 4,096 repeated glyphs,
Rust ordered search is about 39 µs versus C# 29 µs; the gap remains open. Queries
allocate nothing in Rust by construction and zero measured bytes in C#. Broader
performance claims require matched Instruments and complete shaping workloads.

`LayoutTable::apply_gsub(index, position, storage, options, state)` allocates no
memory. `None` visits the whole live prefix; `Some(position)` preserves original
lookup-at policy. `GsubState::glyph_count` selects the live prefix and slice length
supplies capacity. On errors, original earlier mutations and unused storage tails
remain observable. The optional context boundary publishes only after successful
application. This low-level contract is deliberately not transactional. Source,
32-bit offsets, recursion bounds and explicit GDEF/filter generations remain checked.

Run `python3 eng/progpu-benchmark-rust-gsub.py` for 24 matched native/Rust execution
baselines. Buffer reset, allocation and serialization are outside timing; every
complete state/storage byte must match. Native and managed policies still differ
in default ignorables, already-ligated expansion, one-component ligature flags,
malformed-table admission and staged feature handling. The current managed source
comparison qualifies only their explicitly shared mutation contracts.


`ManagedGsubTable` and `ManagedGlyphBuffer` provide the separate current C# nested
executor and reverse-chaining subtable. `ManagedShapingGlyph` retains all 31
original fields, including independent grapheme/character clusters, signed
advances/offsets, script metadata and attachment state. Raw managed coverage,
class lookup, joiner filtering, syllable admission, ligature merging and
already-ligated expansion follow the original ordered operations. Errors retain
previous mutations; private nesting depth and mark coverage restore on return.

`ManagedMarkProfile` explicitly selects the source Rune mark repertoire. It is
independent of canonical normalization and defaults to Unicode 16, matching the
frozen native mark inventory. A complete actual-runtime probe selects the
source profile in differential tests; no operating-system inference is used.
GDEF classes narrow through the original byte enum before zero-class fallback.
Invalid Unicode scalars reject only when original Rune classification is reached.

The original 64-level nested lookup rule remains. Rust additionally bounds
extension chains at 64 hops and nested lookup attempts at 65,536 per operation.
`apply_lookup_at_with_limits` accepts an explicit `ManagedGsubLimits` budget.
Exhaustion returns `FontError::Capacity` with preceding mutations retained; this
is a separate safety policy, not original source equivalence. Two branching
mutation witnesses exceed the original C# control's three-second observation;
they are reported separately from the 74,981 exact-output source cases.

Forty-four browser controls per DPR exercise these current managed contracts.
Run `python3 eng/progpu-benchmark-rust-managed-gsub.py` for 24 full-record matched
C#/Rust nested/reverse execution baselines. Five warmups precede 40 measured
rounds; source font construction, buffer reset/preallocation and serialization
are untimed, while production allocations remain timed. The complete raw stage dispatcher described below builds on this core. Script
preparation/reordering, GPOS, fallback, paragraph layout and GPU text rendering
remain separate work.


`ManagedFeatureOptions` freezes the complete current managed baseline, ranged
feature values and explicit-tag policy. UTF-16 tags retain exact ordinal identity;
ranges retain byte tags, unsigned values and half-open input intervals. Default
list identity remains distinct from a custom list with null explicit tags. The
original signed-cluster and last-applicable-range rules are shared by per-record
script admission and feature-valued alternate execution.

`ManagedSubstitutionPlan` owns original selection, optional context filters,
feature options and exact raw GSUB/font bytes. `apply_stage` executes any of the
42 `ManagedSubstitutionStage` values using original lookup/filter order. It
retains required-feature bypass, already-inserted output skipping, original
forward deletion progress, context continuation, accumulated buffer digests,
reverse order and wrapping random-alternate state. Full records feed context
filters directly through the sealed `ManagedContextRecord` interface; matching
adds no intermediate glyph inventory or allocation.

The nested-call budget applies independently to each root invocation. The source
comparison includes a 131,073-glyph run, which must not fail merely because its
ordinary root calls exceed one recursive lookup's budget. Top-level source
errors preserve their actual partial writes and traversal state; reverse errors
can retain selected mark coverage, unlike nested calls' finally restoration.
The independent original fraction-preparation marker is explicit buffer state.

The source dispatcher passes 262,144 feature queries, 1,974 stage-policy queries
and 26,753 complete stages / 46,818,828 bytes. Browser validation adds 145 controls
per DPR. `python3 eng/progpu-benchmark-rust-managed-dispatch.py` compares 24 full
raw-stage workloads against the original C# executor, including all record and
retained state output. The initial diagnostic capture had high host contention;
its timings are not performance qualification. This API executes already-prepared
metadata; it does not supply full script preparation, reordering or complete
paragraph shaping.


`font::layout::ManagedGposTable` executes all nine current managed raw positioning
lookup types on an owned `ManagedPositionBuffer`. It retains all 31 record fields,
original per-add saturation, filtering, partial error state and actual source
ownership. `resolve_attachments()` and `zero_mark_advances()` preserve original
attachment cycles, direction and late-zero policy. Contextual and attachment
traversals use explicit stacks; the separate per-root work budget is configurable.
Inputs are already-prepared design-unit records. Feature/variation dispatch,
metric initialization, fallback, native device GPOS and complete shaping remain
separate work. The original C# oracle verifies 63,557 full cases; browser validation
replays 80 exact C# fixtures plus a cyclic-work control at both DPRs. See the design
ledger and `perf/managed-gpos-baseline.json` for scope and matched baseline evidence.


`ManagedPositioningPlan` retains an immutable selected font, original lookup
selection, feature options and already-normalized instance coordinates. `apply`
performs the original raw lookup and variation pass in order and returns the
plan's kerning presence, independently of whether any glyph changed. Attachment
resolution stays explicit. `ManagedLayoutVariations` preserves complete-font
reads, eager metadata admission, lazy selected-row failures, exact ordered float
arithmetic and original missing-store behavior. Oversized inventories fail safely.

The prepared-instance boundary is deliberate: the oracle calls the actual C#
`WithVariations`, captures its actual normalized coordinates, and compares the
resulting plan, deltas and complete records. **32,008 cases, 760,086 delta queries
and 44,353,296 output bytes** match, including 2/3-axis products, malformed leaf
and store data, unusual dispatch policies and a 131,073-glyph run. This does not
port or qualify C# instance creation, its fallback on invalid metadata, full
metric preparation or complete shaping. **151 captured C# controls** replay in
WASM at both DPRs; the mandatory source oracle verifies the fixture byte-for-byte.

The complete-plan benchmark compares 30 exact-output workloads with actual C#,
using cached plans and prepared buffers. Five warmups and 40 measured rounds
retain production allocation; preparation/reset/serialization stay outside timing.
`perf/managed-positioning-baseline.json` records hashes, load and all quantiles.
This is a diagnostic baseline, not an optimization or frame-performance claim.
The hosted `deaf86ae6` Linux source/package, Windows and Vulkan/winit jobs pass;
its original and diagnostic browser runs fail at final DPR 2 capture with GPU
process exit 512 and device loss. Original flags, deadlines and qualification
gates remain unchanged.


`font::ManagedVariationData::from_face` ports the complete original managed
process-neutral metadata constructor. Its name resolver is explicit, as in the
original constructor; callers supply the selected source's name policy. Invalid
metadata returns `None`; capacity failures stay errors. Axis ordering is validated
at the original instance boundary. `create_instance` retains last matching UTF-16
settings, original float/fixed conversions, ordered avar mappings, exact normalized
cache-key bits and HVAR scalar inventory. Instances and lazy glyph caches retain
the same immutable selected source across clones.

`ManagedVariationInstance` exposes original HVAR/MVAR/layout deltas and cached gvar
simple, component and phantom operations. Glyph parse failures retain the original
once-only error followed by cached empty state; the first query freezes item count.
Simple IUP uses coordinates changed by earlier tuples. Source scalar arithmetic,
partial writes and complete-font reads remain unchanged. The source comparison
covers **8,772 cases / 8,192,752 exact bytes** and **96 captured browser fixtures**.
`ManagedPositioningPlan::from_variation_instance` now accepts Rust-selected state;
all **32,008 original positioning cases** independently verify its computed
coordinates against actual C# selection before comparing complete outputs.

This ports the process-neutral variation component. Full `TtfFont` root/FIFO cache,
instance metadata, hinted/varied outline construction and
metric preparation remain separate required work. Its default instance retains a
complete zero-coordinate inventory; the full font wrapper still owns default-root
selection and skipped variation application. The 48-workload admission/instance
baseline retains allocation and original compiler/JIT behavior. Query throughput,
full shaping/frame performance and previously recorded native FP gaps remain open.


`SfntFace::managed_names` and `managed_name` preserve original `SfntFontFace`
name selection: declared-table bounds, whole-record inventory admission, individual
invalid-record skipping, UTF-16BE/Latin-1/UTF-8 replacement decoding, NUL removal,
whitespace trimming, stable source ranking and ordinal deduplication. Platform 1,
encoding 0 retains the original Latin-1 route. Ranking examines UTF-16 code units;
supplementary letters do not lose the original ASCII/Latin score bonus. Character
facts use the existing frozen Unicode 16 inventory, independently checked against
all 65,536 original managed Char inputs; this does not infer a future runtime policy.
Names own their bytes after the font is dropped.

`ManagedVariationData::from_named_face` resolves axis names from that same selected
face, retaining original decoded name selection and missing-name tag fallback. The
explicit callback constructor remains independent. Actual C# `TtfFont.VariationAxes`
controls cover the integration. **236,128 source cases / 12,151,088 exact bytes**
include exhaustive two-byte UTF-8/Latin-1 input, odd/truncated UTF-16, malformed
strings, duplicate inventories, header/record bounds and ranking. The **93-case /
61,979-byte** browser fixture is captured from original C# and revalidated in CI.
`eng/progpu-benchmark-rust-managed-names.py` measures complete `GetNames`/`TryGetName`
queries against the source at 1/8/64/512 records. It excludes SFNT construction and
serialization, retaining production allocations and original runtime behavior;
this initial baseline does not qualify full font/shaping/frame performance.

`font::ManagedFont` now owns original uncompressed SFNT/TTC constructor metadata,
selected variation instances and the original strong 32-entry FIFO cache. Separate
roots have separate identities; clones retain the same instance. Normalized-key
equality survives eviction, while `same_instance` distinguishes a recreated entry.
Cache values hold immutable snapshots without root back-references, so retained
instances outlive their caller's root without an Arc cycle. Failed metadata
selection publishes no cache entry. Every TTC face directory validates before
the selected constructor runs, including malformed unselected faces.

The wrapper applies original wght/ital/slnt and MVAR metadata, preserves owned
palette cloning and no-op identity, and exposes horizontal/vertical advances,
layout and simple/component variation queries. `ManagedPositioningPlan::from_font`
uses this exact source/coordinate generation; all 32,008 original positioning
controls now exercise it. Full varied outline construction, vertical origins,
hinting, color-layer rendering, compressed/mapped/resident factories, discovery
and complete shaping/layout/GPU text remain separate required ports.

The constructor/cache oracle compares **10,369 actual C# cases / 81,025,616 bytes**,
including complete metadata, reference/key identity matrices, palette overrides,
FIFO eviction, malformed metadata and TTCs, source errors and partial mutation.
Its **104-case / 1,805,194-byte** browser capture is checked against actual source
in CI and replayed at both DPRs. Run `cargo test --locked --no-default-features
--test managed_font --test managed_positioning -- --include-ignored --nocapture`
with .NET 10; `PROGPU_RUST_DOTNET` selects its executable. The initial diagnostic
benchmark is `python3 eng/progpu-benchmark-rust-managed-font.py`; it compares
construction, cache hits, FIFO rotation and owned palette clones with original C#.

`font::ManagedCharacterMap` retains original managed nominal mapping separately
from the native `SfntFace` policy. It preserves format 12/13/4 precedence, original
checked counts and wrapping arithmetic, legacy version-zero symbol pages, and
ordered whole-cmap variation-selector reads. `Some(0)` from `variation_glyph`
means an explicit nondefault glyph-zero mapping; `None` means unsupported. Default
sequences require nonzero nominal coverage. The source's first matching selector
and malformed-read behavior remain intact, including duplicate/unsorted records.

`ManagedFont::glyph_index`, `has_glyph` and `variation_glyph` use that same immutable
source. ASCII lookup lazily allocates the original per-instance 128-slot cache;
one atomic word per slot holds both initialized state and glyph, including zero.
Clones retain the instance cache; new variation instances have independent caches.
Failed queries never publish a value. Non-ASCII mapping stays on its original
direct path. These APIs perform nominal mapping, not shaping or fallback discovery.

The source oracle compares **8,436 cases / 5,143,568 queries / 61,756,560 exact
bytes**, including complete BMP inventories, both legacy pages, every PUA coverage
hole, malformed maps and selector ordering. **88 source-captured browser cases**
run at both DPRs. Run `cargo test --locked --no-default-features --test
managed_mapping -- --include-ignored --nocapture` with .NET 10. The legacy mapping
data are regenerated from actual API observations by
`eng/progpu-generate-rust-symbol-maps.py`; CI verifies them with `--check`.
`eng/progpu-benchmark-rust-managed-mapping.py` records matched nominal, cached-font
and selector batches, including first-call cache costs. Complete script
preparation, varied outlines, hinting, fallback/layout and GPU text remain pending.

`unicode::grapheme::ManagedGraphemeProfile::Unicode16_0` selects the observed
original managed grapheme repertoire independently of normalization and category
profiles. `managed_next_grapheme_length(&[u16], profile)` returns the first text
element's original UTF-16 length, treating each unpaired surrogate as a one-unit
replacement scalar. Empty input returns zero. It visits only that cluster and its
following scalar, uses O(1) scratch, and allocates nothing.

For strict complete capture, construct `DecodedText` and use
`GraphemePolicy::ManagedRuntime(profile)` with `GraphemeText` or `segment_into`.
Malformed text rejects before publication. This source policy omits GB9c and is
not full Unicode 16 conformance. Existing Unicode 17 and native
`ManagedCompatible` behavior remain separate. Select profiles from actual source
observations; unsupported numeric profile values reject.

Run `cargo test --locked --release --no-default-features --test managed_grapheme
-- --include-ignored --nocapture` with .NET 10 (`PROGPU_RUST_DOTNET` selects it).
The original source comparison covers 22,256,392 cases and 899,691,464 exact bytes.
The browser replays 49 frozen source captures plus unsupported-profile rejection
at both DPRs, alongside existing rendering gates. Regenerate/check pinned public
facts with `python3 eng/progpu-generate-rust-grapheme-profile.py --check`.
`python3 eng/progpu-benchmark-rust-managed-grapheme.py` compares complete boundary
walks with C#; `PROGPU_RUST_GRAPHEME_BEFORE` adds a retained before binary and three
rotating-order process trials. Source parity here does not establish complete
shaping, layout, fallback or GPU text parity.

`font::layout::ManagedGlyphBuffer::from_text` performs the complete original
managed text preparation before script-specific shaping stages. Pass an owned
`ManagedFont`, UTF-16 source, original script and
`text::preparation::ManagedPreparationOptions`. The options explicitly select
normalization, mark and grapheme profiles, cluster level, flags and retained
pre/post UTF-16 context. `buffer.preparation()` exposes the immutable original
source generation; `glyphs()` / `glyphs_mut()` use the same complete records as
the existing GSUB executor. Already-mapped buffer constructors remain separate.

Preparation includes conditional NFC, malformed-unit replacement, source cluster
rules, UVS including explicit glyph zero, Indic/Khmer splits, missing-glyph NFD,
space/hyphen fallback and original unsafe-interior flags. Input and output retain
the original 1,048,576-record bound. Owned prepared buffers recycle up to four
4,096-capacity record arrays per thread; font/text/context owners retire normally.
`into_glyphs` transfers records without pooling them. Full script stages, shaping
sequencing, positioning preparation and layout remain separate unfinished work.

Run `cargo test --locked --release --no-default-features --test managed_preparation
-- --include-ignored --nocapture` with .NET 10 (`PROGPU_RUST_DOTNET` selects it).
The complete source comparison covers 312,627 cases / 873,278,732 bytes; browser
controls replay 110 source captures under all three normalization profiles.
`python3 eng/progpu-capture-rust-preparation.py --check` rechecks every original
Khmer predicate observation. `python3 eng/progpu-benchmark-rust-preparation.py`
compares creation/retirement and complete output with C#; optional
`PROGPU_RUST_PREPARATION_BEFORE` includes a retained unpooled binary. The driver
selects a normalization profile from actual source behavior, never an OS name.


`ManagedGlyphBuffer::apply_script_action` applies one explicit
`text::script_actions::ManagedScriptAction` to an owned prepared buffer. The
operations retain the actual font/text/context generation, all unrelated glyph
fields, lookup state and original writes made before an error. An already-mapped
buffer without preparation metadata rejects before mutation. Plan-dependent
choices such as vertical-substitution presence, Hebrew mark-feature presence and
USE selection are explicit inputs; they must come from the eventual full planner.
Fractions select independently witnessed `ManagedDigitProfile::Unicode16_0`.

These actions cover dotted-circle insertion, direction fallback, Hangul
composition/decomposition and Jamo masks, modified combining-class ordering,
Hebrew presentation composition, USE diacritic decomposition, Thai/Lao expansion,
fraction masks, substitution/syllable clearing, Arabic stretch recording and
whole-record reversal. They do not implement a complete shaping pipeline.
Remaining Indic/USE syllables and reordering,
ordered stage orchestration, positioning initialization, paragraphs and GPU text
remain required work.

Run `cargo test --locked --release --no-default-features --test
managed_script_actions -- --include-ignored --nocapture` with .NET 10. The oracle
compares 658,040 complete action cases / 704,161,720 bytes on the observed Unicode
17 normalization runtime and 1,114,113 modified-class/digit queries. The browser
replays 267 frozen original cases under each explicit normalization profile, plus
invalid-profile, truncated-input and overflow-length controls. `python3
eng/progpu-benchmark-rust-script-actions.py` records three rotating process trials
of 40 matching C#/Rust action workloads, including complete outputs. Actual
preparation and raw-record restoration are outside action timers. The original
stable insertion sort and vector shifts retain worst-case quadratic work; no
end-to-end performance qualification follows from this baseline.


`ManagedScriptAction::ArabicJoining` adds original contextual joining for Arabic,
Syriac and the other admitted cursive scripts. It preserves Syriac Alaph and
Dalath/Rish forms, transparent glyphs, exact five-scalar pre/post context bounds,
malformed-unit replacement and source cluster safety/tatweel flags. Joining uses
frozen Unicode 17 facts independently of normalization and runtime Rune profiles.
`managed_joining_type` exposes the original raw classification; invalid raw values
retain NonJoining. Regenerate and compare all 1,114,114 original queries with
`python3 eng/progpu-generate-rust-joining.py --check` (.NET 10 required).

The expanded `managed_script_actions` source test retains all earlier controls and
now compares 736,501 cases / 896,557,796 exact bytes on the witnessed Unicode 17
normalization runtime. The browser fixture contains 368 complete original cases.
Set `PROGPU_RUST_ACTIONS_JOINING_ONLY=1` when running the action benchmark for the
16 joining-only C#/Rust workloads. This action assigns contextual feature forms;
complete shaping orchestration and rendered text remain separate work.


`ManagedScriptAction::ArabicFallback { enabled, features }` retains the original
presentation fallback action on an actual prepared generation. Capture its five
whole-buffer feature switches with `ManagedArabicFallbackFeatures::from_features`;
the last exact UTF-16 setting wins, any nonzero signed value enables it, and missing
settings remain disabled. The caller supplies the original plan's fallback decision.
Feature ranges and baseline defaults do not participate in this source operation.

The port retains all 76 presentation-form bases and all three ordered ligature
passes: three triples, 58 ordinary pairs and five mark pairs, including the original
private-use targets. Existing GSUB traversal preserves GDEF, ignored marks,
joiners, syllable bounds, component metadata and full cluster mutation. Invalid
Unicode keeps the original action error and any preceding mutations.
`python3 eng/progpu-capture-rust-arabic-fallback.py --check` verifies hash-pinned
Unicode facts and independently observes the original API; no foreign generated
source array is transcribed. This check needs .NET 10.

The complete source corpus now matches 914,017 cases / 1,018,793,660 bytes on the
observed Unicode 17 normalization runtime, retaining every prior case. Its 546
frozen browser cases run under all three explicit normalization profiles.
Set `PROGPU_RUST_ACTIONS_FALLBACK_ONLY=1` in the action benchmark for 24 matched
fallback workloads, including joined text, private-use targets and skipped marks.
These are action baselines; full source stage sequencing, layout and GPU text
remain unfinished.


`ManagedScriptAction::VowelConstraints` ports the original prohibited-sequence
insertion before complex-script preparation. It preserves exact script spelling,
source scan order and two-code-point precedence over a longer rule with the same
prefix. The inserted dotted circle uses the final source cluster and original
constructor defaults; it is retained even when its font glyph is zero. Buffer
flags, existing grapheme metadata and unrelated records are unchanged.

The 103 constraints across 16 scripts come from pinned public Microsoft USE
specification data and Unicode 17 script facts. Their complete inventory and
4,863,144 matcher queries are independently verified with the original APIs by
`python3 eng/progpu-generate-rust-vowels.py --check` (.NET 10 required).
The public data's MIT notice is retained in `MICROSOFT-FONT-TOOLS-LICENSE.txt`;
Unicode facts retain `UNICODE-LICENSE.txt`. No generated foreign engine table or
implementation is copied.

The complete source corpus now compares 1,064,105 cases / 1,150,599,296 bytes on
the witnessed Unicode 17 runtime. All 914,017 previous cases remain; 771 frozen
browser cases also run in ordinary native CI under three explicit profiles.
`PROGPU_RUST_ACTIONS_VOWEL_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 20 matched workloads. This is a new-action baseline, not complete shaping
or rendered-text qualification. Complex syllable analysis/reordering, full source
stage orchestration and positioning initialization remain required.

`unicode::ManagedIndicProperties` retains exact source category/position bits.
`managed_indic_properties` preserves the public Unicode query's zero result for
invalid scalars; `ManagedIndicProperties::for_raw_code_point` preserves the raw
script-stage default word `0x0e00`. `managed_use_category` preserves the original
numeric USE classification, including zero for invalid/unclassified values.
These are immutable point queries, independent of runtime normalization profiles.

The generator observes actual original public/raw APIs without reading their
foreign generated arrays. Its independent packed ranges occupy 11,176 bytes.
`python3 eng/progpu-capture-rust-script-properties.py --check --rust` compares all
13,500,440 original output bytes, including every Unicode-domain raw value, every
high-bit alias and all 16-bit extraction inputs. Ordinary native tests replay
5,672 captured boundary/interior records. Browser tests compare the complete
source-output SHA-256 at both DPRs without bundling the 13.5 MB response.

`python3 eng/progpu-benchmark-rust-script-properties.py` runs 36 matched C#/Rust
lookup workloads in three rotating trials. This capture has 33 lower Rust medians
and three higher ones; it is a new-feature baseline, not complete shaping or
allocation-event qualification. Remaining Indic stages and full plan sequencing are pending.

`ManagedScriptAction::Khmer` performs the original `PrepareKhmerShaping` operation
on the actual prepared `khmr` generation. Exact other script spellings are no-ops.
It classifies original code points, assigns syllable bytes and unsafe-break flags,
inserts admitted broken-cluster dotted circles, then reorders complete records
and assigns the original feature masks. Source clusters, unrelated fields,
font/text/context owners and original mutation-before-error behavior survive.

The independent parser uses public grammar and directly observed original API
results. No foreign generated transition tables or lexer structure are copied.
`python3 eng/progpu-test-rust-khmer-syllables.py` compares 1,398,562 sequences /
9,586,614 exact syllable bytes, including arbitrary categories, long joiner runs,
subscript chains and serial rollover. The complete source action corpus now
matches 1,163,438 cases / 1,280,069,720 bytes on the witnessed Unicode 17 runtime;
all 1,064,105 prior cases remain. The 1,041 frozen cases replay under three
explicit profiles on native and browser builds.

`PROGPU_RUST_ACTIONS_KHMER_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 28 matched workloads across full/missing glyph coverage and short/long
runs. All complete outputs match in three rotating process trials. The 24 active
workloads have lower Rust medians; four disabled-script cases are clock-limited.
This is a new-stage baseline, not an allocation-event or full shaping/frame
qualification. Remaining Indic stages and full source orchestration remain
unfinished.


`ManagedScriptAction::MyanmarPreparation { enabled }` classifies the original
code points, assigns syllable bytes and marks unsafe breaks. It preserves the
original position bytes. `MyanmarReordering` is a separate later operation over
the current records: it inserts admitted broken-cluster circles, assigns kinzi,
base, medial and vowel positions, merges clusters, stably sorts complete records
and restores original pre-base vowel/selector grouping. Neither operation infers
planner admission from script spelling. The planner must supply the original
stage order around substitutions.

`python3 eng/progpu-test-rust-myanmar-syllables.py` compares 12,981,319 sequences /
75,858,807 exact bytes with the actual original API. The independent parser uses
public grammar and observed source behavior, without foreign transition tables.
The full action corpus retains every earlier case and now matches 1,324,919 cases /
1,481,007,028 bytes on the witnessed Unicode 17 runtime. Its 1,398 frozen source
cases run under all three profiles, giving 4,197 checks per browser DPR including
invalid requests. Reordering controls include all raw category pairs, ligated
records, arbitrary syllable/position bytes, neighboring cluster extension, flags,
missing dotted glyphs, repeated stages and long selector-bearing vowel sequences.

`PROGPU_RUST_ACTIONS_MYANMAR_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 32 matched workloads in three rotating trials. All 28 active workloads
have lower Rust median times; four disabled-preparation no-ops remain clock-limited.
This new-stage baseline does not qualify allocator events or complete shaping.
The stable counting sort uses owned O(N) scratch and retains at most one empty
allocation of capacity 4,096 per thread, independently of prepared-buffer storage.
Nested leases, thread teardown and oversized storage retire without source owners.
Original repeated cluster merges and dotted insertions retain their worst-case
quadratic cost. Remaining script families, full orchestration, paragraph/layout,
GPU text and original provider/package/source/pixel gates remain unfinished.


The explicit USE actions preserve the original separation between
`UsePreparation { enabled }`, `UseRecordRepha`, `UseRecordPrebase` and
`UseReordering { enabled }`. Preparation classifies raw code points, filters CGJ
and conditional ZWNJ only from recognition, preserves original record positions,
assigns syllables/unsafe flags/repha eligibility and selects source topographical
forms for non-Arabic-joining scripts. Leading or wholly filtered records retain
their original syllable bytes. Failed mark queries leave earlier classification
writes intact. Recording actions consume actual substituted/eligible metadata;
reordering consumes the current records after basic substitutions.

`python3 eng/progpu-test-rust-use-syllables.py` compares 5,870,757 independent
parser cases / 35,631,822 exact bytes with the original API. The full source action
corpus now contains 1,532,882 cases / 1,715,629,716 bytes on the witnessed Unicode
17 runtime, retaining all 1,324,919 earlier cases. The 1,910 frozen source captures
are shared by native and browser replay under all three normalization profiles.
These are explicit stage operations; the full planner must still select the
original lookup groups and interleave substitutions in their original order.


`PROGPU_RUST_ACTIONS_USE_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 40 workloads across full/missing coverage, short/long runs, filtered
records, hieroglyphs, recording/reordering and disabled stages. A six-second
Instruments CPU profile identified unnecessary alternative-family scans for
unambiguous hieroglyph prefixes. The direct prefix route preserves all original
results. Three rotating uninstrumented trials compare the original Rust binary,
current Rust and actual C#: all 36 active workloads have lower current Rust
medians than C# in this capture; the four hieroglyph after/before ratios are
0.523–0.571. Four disabled no-ops remain clock-limited. Complete exported CPU
tables, before source, input and binary hashes are retained in compact form;
allocation, process-memory, GPU and full shaping/frame qualification remain open.

The hosted Myanmar checkpoint's browser failure remains recorded in
`perf/browser-41b320-hosted.json`: all action/offscreen checks passed before DPR 2
device loss during final presentation; the diagnostic rerun also lost its device.
The bounded process inventory found a packed GPU marker but no eligible separate
GPU argument, so there is no backtrace or established crash attribution. A later
local/hosted pass does not by itself resolve this reliability gate.
