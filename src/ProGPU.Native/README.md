# ProGPU.Native

`ProGPU.Native` is the parallel C++ rendering implementation of the proven,
ProGPU-owned managed compositor contracts. It owns native command encoding, pipeline and buffer
lifetime, batching, submission, and validation while consuming the exact same
[`Vector.wgsl`](../ProGPU.Backend/Shaders/Vector.wgsl) source as the managed
renderer.

The native ABI accepts an existing device, queue, and target view through an
explicit backend adapter. Desktop/mobile packages retain the pinned May-2024
wgpu-native ABI or the separately resolved WebScene Dawn ABI. Browser builds
use Emdawnwebgpu's stable WebGPU C surface and advertise a distinct browser
backend ABI. Every constructor rejects all other ABI identifiers, preventing
handles from one descriptor/procedure domain from being cross-cast into
another.

Production implementation is split behind the unchanged public C ABI. The
main translation unit owns exported entry points and device lifetime;
`progpu_native_scene_builder.cpp` owns the standalone C++20 retained recorder
and deterministic pointer-free compiler used directly by both the desktop and
browser samples. Its static C++ API never crosses the stable C ABI and its
compiled stream is retained across frames; one update is submitted only when
the caller changes the scene generation. Shared builder ownership and bounded
transactional state live in `progpu_native_scene_builder_internal.hpp`, while
`progpu_native_scene_builder_geometry.cpp` owns general geometry plus connected
polyline/NURBS stroke recording and canonical auxiliary arenas. New command
families extend through their own similarly scoped translation units;
`progpu_native_scene_builder_path.cpp` owns retained path/segment recording and
reuses the native path-rasterizer validation contract without a parallel
builder-only approximation. `progpu_native_scene_builder_image.cpp` owns
retained RGBA8 resources and nearest/linear/cubic image commands, including
optional color matrices; multiple draws may reference the same pixels and
stable replay performs no texture upload. Callers can advance the immutable
scene generation and transactionally replace a same-layout RGBA8 resource
while preserving its stable id; the replacement must advance that resource's
generation.
`progpu_native_scene_builder_glyph.cpp` owns retained vector-glyph outline
resources, positioned runs, and deduplicated text styles. It is the direct
native destination for the planned C++ shaper/layout output and does not add a
per-glyph managed/native call.

Native code is strict portable C++20. Clang is the primary toolchain; GCC and
Visual Studio MSVC compile/test the same header compatibility implementation in
CI. Windows enters the selected Visual Studio compiler environment but uses a
parallel single-configuration Ninja build by default; `-Generator VisualStudio`
is retained as a local compatibility fallback. The independent MSVC
qualification lane builds the directly linked renderer and all CPU/native unit
tests, while the ClangCL Windows runtime lanes own the duplicate Dawn-ABI build,
package staging, D3D12 sample, and a bounded representative managed
differential profile. The Windows entry point accepts `-BenchmarkProfile Full`
for the exhaustive 44-case cross-product and `-BenchmarkProfile Smoke` for the
10-case D3D12 CI profile; Linux/macOS CI retains the exhaustive scene-family
coverage. Both Windows profiles build each managed harness once and execute its
DLL directly. On Build run 31819205840, ClangCL compiled and linked 139 native
steps in about 53 seconds while repeated D3D12 process cold starts extended the
win-x64 job to about 31 minutes. The bounded profile removes 34 of those 44
benchmark process starts without removing native tests, ABI/export checks,
package staging, the D3D12 sample, managed/native image comparison, or any
semantic resource/effect family. Exact-head Build run 31830534644 confirms the
result: win-x64 fell from 36m40s on exhaustive run 31807899721 to 9m53s
(`-73.0%`), and win-arm64 fell from 42m08s to 12m54s (`-69.4%`). The broad
Windows managed job also fell from 19m08s to 18m10s after building each harness
once (`-5.1%`). This avoids
compiling the same renderer twice in the compatibility lane without removing a
compiler, ABI, runtime, or package gate. Module-capable LLVM Clang/Ninja builds additionally expose and test
`import progpu.native.scene_builder;` through a CMake `CXX_MODULES` file set.
Apple Clang, Emscripten, and other configurations without standard-module
dependency scanning use the thin installed header over the same library; no
implementation or semantic fork is maintained, and compiler-specific BMIs are
never shipped.
`progpu_native_scene.cpp` owns pointer-free stream validation,
`progpu_native_effect_plan.cpp` owns the bounded three-texture chain schedule,
`progpu_native_semantic_budget.hpp` owns checked scene/layer/effect budget
accounting, `progpu_native_semantic_state.cpp` owns the allocation-free state
and layer-target cursors, and `progpu_native_semantic_validation.cpp` owns
bounded family-payload preflight.
`progpu_native_semantic_effect_cache.cpp` owns the backend-neutral retained
effect-output identity and invalidation rules. GPU-visible record layouts,
atlas keys, alignment, and subpixel phase math live in
`progpu_native_gpu_records.hpp`. `progpu_native_draw_state.cpp` owns the
WebGPU-independent validation, compatibility-prefix resolution, clip rounding,
mask normalization, effect-chain copying, and retained-payload hashing used by
the legacy frame-family entry points. Geometry compilation is no longer one
large header: `progpu_native_geometry_base.hpp` owns shared math and ABI assertions,
while the stroke, dash/polyline, spline, and analytic headers own their
respective algorithms. The four-line `progpu_native_geometry.hpp` remains the
compatibility aggregator. `progpu_native_engine.hpp` now owns the opaque
engine's WebGPU handles, cache state, release ordering, and buffer-growth
invariants; `progpu_native_semantic_replay.hpp` owns the retained GPU page,
bundle-span, effect-dispatch, and layer-slot records; and
`progpu_native_webgpu_resources.hpp` owns the non-copyable temporary path-raster
handle group. Pipeline ownership is split by resource family:
`progpu_native_pipeline.cpp` owns vector/analytic construction and shared
uniform creation, `progpu_native_path_text_resources.cpp` owns path and glyph
compute/atlas/text resources, `progpu_native_image_layer_resources.cpp` owns
image, layer-mask, and fixed/advanced blend pipelines, and
`progpu_native_clip_resources.cpp` owns retained clip textures, buffers, and
bindings. Retained execution is split separately from resource construction:
`progpu_native_clip_execution.cpp` owns vector-clip replay,
`progpu_native_layer_resource_execution.cpp` owns pooled layer/mask resources,
`progpu_native_effect_execution.cpp` owns effect resources and dispatch,
`progpu_native_layer_composite_execution.cpp` owns group composition, while
`progpu_native_image_execution.cpp` owns image upload and image-mask updates.
`progpu_native_replay_execution.hpp` is the
small internal seam used by the remaining C ABI/frame-family entrypoints in
`progpu_native.cpp`. Path, positioned-glyph, and RGBA-image frame execution is
further separated into `progpu_native_path_execution.cpp`,
`progpu_native_glyph_execution.cpp`, and
`progpu_native_texture_execution.cpp`; each owns one frame-family algorithm
without a shared mutable implementation file. Both wgpu-native and Dawn
targets compile the same private module set. The CPU-only modules are also
compiled directly into focused internal tests so their behavior cannot depend
on WebGPU startup.
Semantic-scene execution follows the same boundary:
`progpu_native_semantic_update_execution.cpp` owns transactional immutable
snapshot updates, `progpu_native_semantic_draw_execution.cpp` owns packed-page
render-bundle encoding, and `progpu_native_semantic_render_execution.cpp` owns
scene compilation and replay orchestration.
`progpu_native_semantic_identity.cpp` computes allocation-free typed content
identities once per accepted update. Brush, text-style, analytic, path, glyph,
and image pages are therefore retained independently across scene generations;
the full scene hash still owns display-order render bundles and effect output.
Decoded color-glyph ownership is split again:
`progpu_native_semantic_color_glyph.cpp` performs CPU-only pointer-free
metadata/range validation, while `progpu_native_color_glyph_resources.cpp`
owns transactional RGBA atlas creation, shelf packing, upload, and text bind
group replacement. Neither module parses a font, SVG, PNG, JPEG, or other
compressed input.
The first standalone text-core slice now lives in `progpu_native_text`.
`progpu_native_sfnt.cpp` is a direct native port of ProGPU's owned
`SfntFontFace` contracts: it exposes a caller-owned, allocation-free borrowed
SFNT/TTC face view with bounded directory lookup, `head`/`hhea`/`hmtx`/`maxp`
metrics, and selected cmap format 4/12/13 lookup. It performs no file I/O,
decompression, WebGPU initialization, or managed/native call per character.
The same borrowed view resolves short/long `loca` offsets into zero-copy
`glyf` slices and exposes contour count plus exact font-unit bounds without
materializing an outline graph. Simple glyphs use a two-pass caller-buffer API:
the first pass validates instructions, contour endpoints, repeated flags, and
coordinate byte ranges while reporting exact point/contour counts; the second
expands signed deltas directly into caller-owned point records. Empty,
simple, and composite records are classified explicitly. The library performs
no heap allocation in either pass. Decoded simple contours then use another
allocation-free count/write pair to produce the renderer's canonical line and
quadratic `progpu_native_path_segment` records with exact implied-midpoint and
closed-contour behavior. Variation deltas remain a subsequent slice. Composite
descriptor decoding is allocation-free:
it validates and writes caller-owned component records with byte/word XY or
point arguments, F2Dot14 uniform/axis/2x2 transforms, continuation state, and
optional instruction lengths. A bounded recursive count/write API then expands
those records directly into caller-owned float points and canonical path
segments, including scaled offsets, midpoint-to-even rounding, point
attachment, and a fixed 33-glyph cycle/depth stack. No temporary outline graph
or per-component allocation is retained.
The hardware sample accepts an optional third argument naming an SFNT/TTC font.
With the repository's Inter Medium face it decodes composite U+00E9 in C++,
passes its canonical path records directly to the retained glyph resource, and
renders through the same shared compute-rasterizer/text shaders as managed
compiled pictures. This validates the native font-to-GPU seam without adding a
font parser to the renderer or a per-glyph ABI crossing. Omitting the argument
keeps the source-independent fallback fixture used by package consumers.
`progpu_native_font_variations.cpp` begins variable-font parity with an
allocation-free two-pass `fvar` axis reader. It retains each tag, signed 16.16
range/default, flags, and name ID in fixed records, validates the complete table
before writing caller storage, and is exported through both the header and LLVM
module surfaces. The same allocation-free surface now normalizes signed 16.16
user coordinates and applies optional piecewise `avar` mappings in a bounded
scan. Granular `progpu_native_gvar.cpp` and
`progpu_native_gvar_packed.cpp` units now expose borrowed glyph tuple slices,
shared tuples, and transactional packed point/delta decoding with no internal
allocation. A third granular unit validates and decodes shared/embedded tuple
regions and evaluates exact managed piecewise scalars directly from F2Dot14
caller spans. The next granular units implement allocation-free circular IUP
inference and transactional simple-glyph tuple application. Fractional results
lower without rounding to the canonical path ABI; native and managed
InterVariable `opsz=23` glyph 397 match all 39 segments and the exact stream
hash. A shared granular payload walker now also drives allocation-free
composite-component tuple application. Native glyph 618 resolves the exact
managed component offsets `(0,0)` and `(15,0)` while the managed reference
outline is pinned at 2 figures, 36 segments, and its full-stream hash.
Granular requirement-measurement and decode units now recursively vary every
simple child, retain parent offsets only for the active component stack, and
apply scaled offsets, point attachment, and grid rounding directly into the
canonical path ABI. The full native glyph-618 outline matches the same start,
36 segments, and managed hash with caller-owned bounded scratch. Phantom
advance fallback now reuses the same tuple walker and returns the exact
right-minus-left phantom X delta without internal allocation. HVAR precedence,
borrowed item-variation stores, long/short delta rows, and delta-set maps now
execute allocation-free; InterVariable glyph 397 matches the managed HVAR
delta `-28`. The native MVAR consumer now matches InterVariable
`xhgt=-31` (`1118 -> 1087`), and the GDEF 1.3 consumer evaluates its borrowed
layout store for the later GPOS port.
The dependency-free compressed-glyph foundation now includes a separate
caller-buffer zlib/DEFLATE library. It implements stored, fixed-Huffman, and
dynamic-Huffman RFC 1951 blocks, overlapping 32 KiB history copies, RFC 1950
header checks, and Adler-32 validation without heap allocation or a platform
compression dependency. The output span is the explicit memory bound; invalid
headers, truncated/oversubscribed streams, invalid distances, trailing bytes,
short output, and checksum mismatches fail explicitly. PNG scanline filtering
and SVG gzip framing remain separate consumers rather than being folded into
the zlib decoder. The same compression module now exposes a bounded RFC 1952
single-member gzip decoder with optional extra/name/comment/header-CRC parsing,
payload CRC-32 and output-size validation. OpenType SVG document views use that
path directly; uncompressed SVG remains a bounded caller-buffer copy.

The first consumer is the standalone `progpu_native_image` C++20 library and
`progpu.native.image` module. Its dependency-free PNG path validates the
signature, ordered critical chunks, per-chunk CRC-32, palette/transparency
metadata, consecutive `IDAT` payloads, zlib checksum, and exact caller buffer
requirements before producing pixels. The bounded profile accepts every legal
PNG grayscale, RGB, indexed, grayscale-alpha, and RGBA bit depth, including
packed 1/2/4-bit samples, 16-bit samples, and both sequential and Adam7 scan
orders. It reconstructs all five standard filters in place and emits straight
RGBA8 using exact full-range sample normalization.
Parsing and decode are `O(C + B + W*H)` for chunks `C`, encoded bytes `B`, and
pixels `W*H`; all compressed, filtered, and output storage is caller-owned.
Malformed input, illegal bit depth/interlace, short scratch, palette index,
and checksum failures are explicit, and the RGBA destination is unchanged on
failure. Adam7 uses seven fixed pass descriptors and scatters samples directly
from filtered caller scratch without a temporary full-frame image.

The text module now normalizes WOFF1 through an exact two-call caller-buffer
contract. The requirements pass validates the directory and reports the final
SFNT size plus maximum-table scratch. Normalization preflights every compressed
table before touching the destination, then writes the canonical SFNT directory
and aligned table payloads. Work is `O(T + I + O)` with `O(M)` caller scratch
for tables `T`, compressed bytes `I`, output bytes `O`, and largest compressed
table result `M`; the implementation has no heap or platform codec dependency.
WOFF2 remains explicitly unsupported pending the bounded Brotli/transform slice.

The same text library now opens borrowed CFF2 tables with uint32 INDEX counts,
required TopDICT/FontDICT/PrivateDICT ownership, optional FDSelect, and a
length-bounded VariationStore. The shared Type 2 evaluator has an explicit
CFF2 mode: it rejects widths, `endchar`, `return`, and removed logic/storage
operators; accepts implicit CharString/subroutine termination; and evaluates
`vsindex`/`blend` from caller-provided normalized F2Dot14 coordinates. Region
scalars are computed once per selected variation-data subtable into fixed
stack storage. Container validation is `O(F + V)`, and outline decode is
`O(B + S + A*R)` for FontDICTs `F`, variation subtables `V`, executed bytes
`B`, emitted segments `S`, axes `A`, and active regions `R`, with borrowed
tables and caller-owned output.

The Unicode/shaping port begins with the fixed native equivalents of ProGPU's
`OpenTypeTag`, feature, glyph, direction, cluster-level, and flag records plus
strict two-pass UTF-8 and UTF-16 decoding. Every successful scalar retains its
original input-unit offset and length, Unicode 17 script tag, and canonical
combining class. The decoder validates the complete input before touching a
caller span, rejects overlong UTF-8, surrogate scalars, truncated sequences,
and unpaired UTF-16 surrogates, and performs `O(N)` work with `O(1)` internal
storage. Script and combining-class tables are generated from the same managed
packed data and are verified by the native contract CI gate; the native text
library does not maintain a handwritten Unicode table fork.

Canonical normalization consumes the same
`UnicodeNormalizationData.bin` resource as managed ProGPU through a validated,
borrowed view. A requirements pass reports maximum FormD capacity; the write
pass expands fully decomposed scalars, performs stable canonical-class ordering,
and optionally compacts FormC pairs while preserving source ranges. Typical
work is `O(D log R)` for `D` decomposed scalars and binary-searched records `R`;
in-place stable ordering is `O(D)` for already ordered text and `O(D^2)` only
for an adversarial reverse-ordered combining sequence. Storage is entirely
caller-owned and validation or capacity failure leaves output untouched.

An initial allocation-free script itemizer groups these scalar records into
source-preserving OpenType runs. Common/Inherited (`DFLT`) scalars attach to the
preceding resolved script, or the first following script at the beginning of a
run, matching managed ProGPU's first-strong inference. Counting and writing are
separate `O(N)` passes with `O(1)` internal state and transactional short-buffer
failure. Script_Extensions and language tailoring remain explicit later stages
rather than being guessed inside the decoder.

The Unicode 17 bidi-class and paired-bracket ranges are generated from the
same ProGPU managed packed tables and searched directly in native code. The
native UAX #9 revision-51 resolver ports the managed explicit embedding,
isolate, weak, bracket, neutral, implicit-level, and line-reset rules. It keeps
source-unit ranges in the result and uses only exact caller-owned unit, index,
level-run, and bracket-pair spans. Resolution is `O(N log N)` in the bounded
worst case because bracket pairs are sorted by opening position; ordinary text
is linear, retained state is `O(N)`, isolate/embedding depth is capped by the
normative level 125, and the bracket stack uses the normative 63-entry bound.
Capacity failure is transactional.

Extended grapheme segmentation follows Unicode 17 UAX #29 revision 47 rules
GB3-GB13, including Hangul composition, Extend/ZWJ/SpacingMark/Prepend,
Indic-conjunct linking, emoji ZWJ sequences, and regional-indicator pairing.
The official grapheme, emoji, and Indic property files are pinned by SHA-256
into a managed generated source and copied into the native generated header by
the existing stale-data gate. Counting and writing are separate linear passes,
use `O(1)` internal state, preserve original source/scalar ranges, and leave the
caller output untouched on insufficient capacity.

The first shared OpenType execution primitive is a granular borrowed layout
unit for Coverage formats 1/2, ClassDef formats 1/2, GSUB/GPOS headers, and lazy
LookupList records. Construction validates complete sorted arrays, disjoint
ranges, coverage indices, offsets, subtable references, and mark-filtering-set
storage before exposing binary-search lookup. Views allocate nothing; creation
is `O(R)`, lookup is `O(log R)`, and malformed records fail explicitly. GSUB
and GPOS will consume this one parser rather than maintaining separate helpers.

Native GPOS keeps the 32-byte bulk shaped-glyph ABI unchanged. Single and Pair
positioning mutate that span directly; Cursive, Mark-to-Base,
Mark-to-Ligature, and Mark-to-Mark additionally write one fixed eight-byte
relationship into a caller-owned attachment span. A separate bounded pass
resolves parent chains with caller-owned byte states, rejects cycles or depth
beyond 64, and applies pen-advance compensation without a heap graph. Lookup
execution is `O(P * S * K)` in the bounded worst case and attachment resolution
is normally `O(P + A)`, where `P` is glyph count, `S` is subtable count, `K` is
coverage/anchor search work, and `A` is the advances crossed by attached marks.
Anchor formats 1-3 are validated; device/variation deltas remain a later GPOS
slice rather than being silently approximated.

C++ clients can use the header surfaces or, on the supported LLVM
configuration, `import progpu.native.text;`,
`import progpu.native.compression;`, or `import progpu.native.image;`.
Additional renderer domains will move behind similarly typed internal modules
as their ownership seams are stabilized; no module exports backend descriptor
layouts.

The public C header is the single source of truth for opted-in blittable C# ABI
records. `PROGPU_CSHARP_STRUCT` markers generate the matching internal
`NativeMethods` layouts while handwritten C# retains constructors, validation,
ownership, and convenience properties. Regenerate and verify those records with:

```sh
./eng/progpu-generate-native-contract.sh
./eng/progpu-verify-native-contract.sh
```

The verification command is also a pull-request and release gate; changing an
opted-in native record without committing its exact generated C# layout fails CI.

Build, test, and run the live offscreen sample from the repository root:

```sh
./eng/build-progpu-native.sh
```

The command writes the verified sample image to
`artifacts/progpu-native/sample/progpu-native-sample.ppm` and then runs the
typed .NET host to produce `progpu-native-managed-sample.ppm` through the same
C++ engine. The repository build passes Inter Medium to the native sample and
therefore gates composite C++ glyph decoding through the hardware glyph atlas;
the executable's optional third argument can select another local SFNT/TTC
face. Third-party headers remain under ignored `artifacts/`; no upstream
implementation is vendored into ProGPU.

Build the same C++ renderer as WebAssembly and execute it against a real
`navigator.gpu` device in Chromium:

```sh
PROGPU_NATIVE_BROWSER_INSTALL_CHROMIUM=1 \
  ./eng/progpu-test-native-browser.sh
```

The Emscripten/Emdawnwebgpu lane compiles the shared renderer modules and WGSL,
serves the generated page over HTTP, and runs a Playwright integration test.
The gate validates the browser-specific ABI/capability identity and replays a
six-command retained semantic backdrop scene with analytic and path resources,
one retained brush table, one retained positioned-text style table, one
bounded isolated layer/effect, six GPU draws, and
one renderer submission. Deliberately wrong magenta source colors prove that
red/blue solid remapping and the green-to-yellow path gradient come from the
native retained material page. The first frame uploads that page once and the
stable frame uploads zero brush/stop/text-style bytes. The text gate also
exercises the canonical uint64 glyph-outline scene ABI through wasm32's checked
`size_t` translation. Before that evidence scene, an independent retained
color-glyph fixture validates the decoded-RGBA resource on wasm32, executes the
production color-atlas branch of `Text.wgsl`, requires the exact 16-byte first
upload, and requires zero color-atlas/vertex/coverage upload on stable replay.
Independent mask fixtures then verify both isolated-layer and exact per-draw
semantics. The state-mask fixture binds one transformed analytic rounded mask
to two overlapping translucent rectangles in one vector batch: three semantic
commands, three typed resources, one GPU draw, exact premultiplied overlap
pixels, and zero-upload replay. A second state-mask fixture binds one retained
R8 coverage mask to an uploaded color-matrix image and a retained color glyph:
two semantic commands, four resources, two GPU draws, exact excluded-half
pixels, and zero image/glyph/mask/uniform upload on stable replay. The color
matrix and state mask remain independent shader bindings and execute in one
image draw without an intermediate texture. These fixtures use the same
explicit bind-group layouts and production `Vector.wgsl`, `Text.wgsl`, and
`Texture.wgsl` as direct wgpu-native and packaged Dawn/WebScene.
The test rejects console and WebGPU
validation errors, verifies clear, parent, gradient, and composited-layer
pixels, and saves the exact canvas plus a JSON contract under
`artifacts/progpu-native/browser-evidence/`. The hardware Dawn lane separately
keeps the stricter actual-parent advanced-multiply differential; complete
advanced-blend browser differentials remain a later parity checkpoint. The
surface texture is acquired and rendered inside `requestAnimationFrame`, as
required by Emdawnwebgpu, and the following animation frame marks the output
inspectable for deterministic browser capture. The Emscripten runtime remains
alive across those callbacks and page navigation owns final teardown. Because
Linux headless Chromium omits WebGPU canvases from compositor screenshots, the
test-only evidence module renders to a copyable WebGPU target, copies it to the
presentation texture and a mapped buffer in one diagnostic GPU submission, and
reconstructs an RGBA evidence canvas. This diagnostic submission/readback is
outside renderer metrics and is never compiled into production native libraries.
The
browser adapter deliberately
does not advertise the native synchronous submission-index timeline; browser
hosts use JavaScript `GPUQueue.onSubmittedWorkDone()` at their scheduling
boundary. Page-owned device/surface handles remain alive for the page resource
domain and are reclaimed with the WebAssembly instance.

Run the interactive desktop gallery directly on the exact wgpu-native backend:

```sh
./eng/run-progpu-native-desktop.sh
```

The launcher builds the independently reproducible CMake target, selects the
portable desktop TFM, and opens the **Native C++ Renderer** page. The ordinary
desktop launch remains on Dawn for native media interop. After the provider
runtime is available, `--native-renderer-dawn` opens the same page through the
typed provider-resolved adapter; `--native-renderer` remains the direct
wgpu-native comparison. The two handle domains are deliberately separate.

Verify that the same renderer source remains compatible with WebScene PR #10's
exact modern WebGPU header contract:

```sh
./eng/progpu-verify-native-dawn-header.sh
```

This builds the separately linked `progpu_native_dawn` shared library with
warnings as errors, runs its fail-closed provider contract test, and verifies
its exported-symbol allowlist. The library has no Dawn or wgpu-native link
dependency: its typed constructor loads every required WebGPU procedure through
a neutral callback backed by WebScene's provider resolver. The ordinary
wgpu-native constructor is disabled in this binary, so the two object domains
cannot be cross-cast accidentally.

Run the macOS-arm64 hardware integration against the exact WebScene provider
and Dawn revisions recorded in `eng/progpu-native-dawn.version.json`:

```sh
./eng/progpu-verify-native-webscene-provider.sh
```

The gate builds WebScene's provider through its own published build entry
point, creates one Metal provider/device/canvas resource domain, renders the
ProGPU C++ frame into the acquired canvas texture, waits for its native queue
submission, presents it, and verifies the external IOSurface retain/release
lifecycle. Production rendering and presentation remain GPU-only and
zero-copy. The gate maps the IOSurface only after presentation for deterministic
pixel verification and a CI evidence image; that readback is test-only. It also
creates the C++ renderer through the public typed .NET `NativeDawnAdapter`,
resolves Dawn procedures from the provider module, renders and reads back known
pixels, forces a real Dawn device loss, recreates the engine on a replacement
device, verifies the same pixels again, and publishes a second managed-host
capture.

Both `progpu_native` and `progpu_native_dawn` are staged in the
`ProGPU.Backend.Native` RID package for Linux, macOS, and Windows x64/arm64.
The source-independent package consumer loads both binaries and validates their
distinct backend identities. Every runnable RID build also executes the native
C++ backend sample, requires the host backend (`Vulkan`, `Metal`, or `D3D12`),
reads back and checks known pixels, and uploads both the PPM capture and a
`progpu-native-provider.txt` adapter/backend record. The exact WebScene
provider hardware gate runs separately on macOS arm64 because that provider
revision currently exposes Metal/IOSurface.

Build the provider-resolved mobile C++ adapter without linking a private Dawn
copy:

```sh
./eng/build-progpu-native-mobile.sh all
```

The Android output contains API-30 `arm64-v8a` and `x86_64` shared objects with
static libc++ and no Dawn/WebGPU DSO dependency. The iOS output is a static
XCFramework containing `ios-arm64` plus universal arm64/x64 simulator slices.
Both use immutable WebGPU headers at
`01addc4ba8a2915a061b7095a6768b512071ab96`, verify their exported ABI, and are
packaged by `ProGPU.Android` and `ProGPU.iOS` with the typed managed host. The
platform package continues to supply the actual Dawn runtime and same-device
WebGPU handles.

ABI v3 also publishes an opaque submission token for each native frame.
External-image owners can poll or wait for that token before recycling a
borrowed texture; stable rendering does not wait and creates no managed
per-frame synchronization object. Platform decoder handle import and producer
fences remain in the typed Dawn platform adapters: IOSurface/MTLSharedEvent,
DXGI/keyed mutex, AHardwareBuffer/SyncFD, and DMA-BUF/SyncFD are converted to a
same-device WebGPU view before entering C++. The renderer deliberately does not
duplicate OS handle descriptors in its stable semantic ABI.

Run the matched managed/native rectangle differential and CPU-submission
benchmark after the native build:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --rectangles 384 --warmup 60 --iterations 600
```

Use `LD_LIBRARY_PATH` with the same directories on Linux. The benchmark renders
the same retained scene into two textures on one device, rejects pixel drift,
alternates measurement order, and reports p50/p95/worst CPU submission plus
managed allocation. It does not by itself establish whole-engine parity.

Use `--semantic-scene` for the first whole-scene substitution benchmark rather
than a single-family call:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --semantic-scene --rectangles 384 --warmup 120 --iterations 600 --sync --write-images
```

Both sides retain the same four-quadrant analytic/path/glyph/image workload.
The native side installs one versioned pointer-free snapshot and renders it
through one C ABI call, one command buffer, and one queue submission. The
managed side uses the production retained `Visual`/`Compositor` path. The
report separates CPU submission from GPU-completion wait, publishes snapshot
and frame metrics, requires zero stable vertex/index/texture/coverage upload,
checks zero managed allocation after warm-up, and writes native, managed, and
amplified-difference images. Use multiple alternating Release runs and the
required platform profilers before making a performance claim from this mode.

Add `--semantic-layer-effects` to wrap that mixed scene in a retained
Gaussian-blur/drop-shadow chain and a post-effect rounded mask. Stable native
replay keys the completed effect output by immutable scene hash, unique layer
pop command, target extent, and effect-texture generation. It therefore emits
zero effect compute passes until any of those inputs changes. On the Apple M3
Pro reference machine, three alternating 300-frame synchronized Release runs
measured native p95 `1.766-1.776 ms` versus managed `1.866-2.133 ms`; before
retaining the output, native p95 was `3.158-3.421 ms`. The pixel differential
remained a maximum `7/255`, with 64 of 518,400 pixels above a difference of 3.
Matched Time Profiler and Metal System Trace captures live below
`artifacts/performance/native-semantic-layer-effects/`.

Semantic value preflight also enforces checked aggregate compilation budgets
before creating an encoder: 16,384 draw passes, 256 MiB of expanded vertices,
64 MiB of indices, 256 MiB each of textures and aligned coverage staging, and
512 MiB total across those domains. This bounds adversarial expansion while
the broader stream-format limits remain available for future non-draw records.
Identical repeated-family commands use a content-addressed retained revision,
so intervening families do not force a flush or extra submission. Distinct
repeated payloads remain the paged-buffer continuation.

Add `--group-vector-clip-chain --write-images` to apply the retained
intersection/difference path-mask gate to the selected family. The chain uses
independent affine transforms and cubic coverage, validates mutation rebuilds,
and requires unchanged replay to report a clip-cache hit with zero clip passes,
uploads, family-content rebuilds, or native managed allocation. The native
build scripts run this mode for solid, analytic, geometry, path, glyph, and
image families; `--group-texture-mask` and `--group-rounded-mask` select the
other common-mask representations.

Exercise the first indexed analytic batch with deterministic rectangles,
ellipses, circular rounded rectangles, strokes, and affine transforms:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --analytic --rectangles 512 --warmup 60 --iterations 600
```

Add `--analytic-kind 1` for the tight ellipse-only differential or
`--analytic-kind 2` for rounded rectangles. The mixed gate records the bounded
AA-edge difference from the managed compositor's separate solid-rectangle
stroke specialization; the general analytic paths remain within 3/255 per
channel, and the original rectangle fast path remains byte-exact.
Add `--dpi 2` to render a 480 by 270 logical scene into the 960 by 540 physical
target and exercise Retina projection and analytic derivative coverage.

Exercise the indexed geometry batch with flat-cap lines, transformed fills,
hairlines, fixed-device strokes, and exact non-conformal stroke outlines:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --geometry --rectangles 512 --warmup 60 --iterations 5000 --write-images
```

Use `--geometry-kind 0 --geometry-line-mode 0|1|2` to isolate hairline,
fixed-device, or ordinary transformed lines. Use `--geometry-kind 3|4` for an
isolated quadratic/cubic Bezier, or `--geometry-curves` for a deterministic
mixed curve scene covering hairline, fixed-device, and ordinary affine
strokes. Use `--geometry-start-cap 0|1|2|3` and
`--geometry-end-cap 0|1|2|3` to select flat, square, round, or triangle caps.
The differential compares a second submission after both pipelines are fully
warmed and reports the optional native compiled-payload hash alongside the
readback hashes. `--sync` includes an individual
device-completion wait inside each renderer's measured interval. Generated
native, managed, and absolute-difference images are written under
`artifacts/progpu-native/differential/`.

Use `--geometry-polylines`, `--geometry-splines`, or `--geometry-dashes` for
the connected-stroke lanes. Geometry benchmarks publish a stable native
content revision, so timed replay reuses compiled CPU vectors and the prior GPU
vertex/index/brush upload exactly as the managed retained scene does.

Use `--paths` for the first Tranche B lane. It transfers compact analytic path
segments, dispatches the shared path-coverage compute shader on a cache miss,
and composites retained atlas quads. Add `--write-images` for native, managed,
and amplified-difference captures. DPI-1 and Retina DPI-2 outputs are
byte-exact against the managed compositor.

Use `--glyphs` for the retained positioned-glyph lane. The managed side shapes
and positions glyph IDs once, while C++ owns outline validation, production
`GlyphRasterizer.wgsl` compute dispatch, the bounded R8 glyph atlas, and one
instanced `Text.wgsl` composite. Add `--dpi 2` for the Retina gate and
`--write-images` for exact native, managed, and amplified-difference captures.
Use `--drain-each-pair` to bound queue depth while measuring CPU submission
without charging the shared GPU completion wait to either renderer; use
`--sync` when deliberately measuring complete GPU work.
Use `--atlas-growth` with `--paths` or `--glyphs` and a sufficiently large
`--rectangles` count to exercise transactional 1024-to-4096 R8 atlas growth,
generation stability, and zero-upload retained replay.

Use `--images` for the retained straight-alpha RGBA8 lane. The first frame
uploads one typed pixel payload and compiles one transformed quad; later frames
reuse the texture, sampler bind group, vertices, indices, and uniforms. Add
`--dpi 2` and `--write-images` for the Retina exact-pixel gate, or `--sync` to
separate CPU submission from the shared WebGPU/Metal completion wait.

Use `--external-images` for the same-device zero-copy lane. It binds an
existing RGBA/BGRA WebGPU texture view directly and performs no native texture
upload. The native renderer retains the view until replacement or disposal;
the caller must keep the underlying texture alive for that interval.

Use `--group-gaussian-blur`, `--group-drop-shadow`, or
`--group-effect-chain` to apply retained GPU effects after any of the six frame
families. The chain benchmark evaluates Gaussian blur followed by source-alpha
drop shadow, compares it with independently nested managed visuals, requires a
five-pass changed graph and zero-dispatch stable replay, and retains three
full-target RGBA8 intermediates. Add `--recompute-group-effect --sync` for the
matched changed-graph GPU-complete distribution or `--write-images` for native,
managed, and amplified-difference screenshots.

Use `--group-blend-mode <GpuBlendMode>` to composite a retained root group
through any of ProGPU's 29 blend modes. Exact Porter-Duff/coefficient modes use
one fixed-function WebGPU composite pass. Multiply, Screen, Overlay, and the
other destination-aware modes retain a bounded source texture and execute one
static WGSL fullscreen pass over the target backdrop. Stable advanced replay
skips the source-family pass, reuses its pipeline and texture, and allocates
zero managed bytes after warm-up. The current ABI applies the mode to the root
group against the frame clear color. Semantic nested/backdrop layers now have
an exact pointer-free descriptor, typed analytic-mask/effect-chain resources,
canonical validation, and a checked preflight budget. Analytic rounded masks
execute through retained per-occurrence uniforms in nested bounded parents;
one-to-eight-node Gaussian/drop-shadow chains execute through depth-indexed
three-texture intermediates and one packed dynamic-offset uniform page before
mask/opacity composition. Advanced destination-sampling now resolves the
effected/masked source into bounded scratch, samples the actual rendered
parent, and replaces the parent through shared `AdvancedBlend.wgsl`. Explicit
bounded backdrop input and parent-dependent cache exclusion are implemented.

Current native parity:

- versioned C ABI and exact backend-ABI rejection;
- borrowed external render targets with retained device/queue ownership;
- one batched draw and one submission for all solid rectangles;
- physical framebuffer sizing and logical-to-physical DPI projection;
- exact `VectorVertex` layout and the shared solid-rectangle shader path;
- indexed mixed analytic rectangle/ellipse/circular-rounded-rectangle fill and
  stroke batches with per-primitive affine transforms;
- indexed line, triangle, and quadrilateral batches, including
  one-device-pixel hairlines, positive fixed-device strokes, conformal scalar
  expansion, and transformed local outlines under anisotropic scale/shear;
- indexed quadratic/cubic Bezier batches: conformal and device-space strokes
  are evaluated by the production 24-section GPU curve shader, while ordinary
  anisotropic/sheared strokes use bounded 24–1,024-section exact local-outline
  compilation before the same indexed GPU pass;
- flat, square, round, and triangle start/end caps for lines and Bezier curves;
  hairline/fixed caps expand after the full affine transform, while ordinary
  non-conformal caps transform their complete local outlines;
- connected open/closed polyline and adaptive rational-spline strokes with all
  transform modes, caps, joins, and reusable odd/even dash styles;
- one affine analytic WebGPU quad for every positive-width round cap, including
  anisotropic/sheared ordinary strokes;
- explicit retained geometry revisions that reuse compiled CPU payloads and
  skip unchanged GPU vertex/index/brush uploads while still encoding and
  submitting the current target pass;
- retained filled line/quadratic/cubic/resolved-arc paths with a native-owned
  geometrically growing bounded R8 coverage atlas, published generation,
  64-phase tile reuse, shared compute/vector WGSL, and no stable-frame raster
  or payload upload;
- retained positioned glyphs with deduplicated analytic outlines, a
  native-owned geometrically growing bounded R8 glyph atlas, published
  generation/growth counters, production glyph-compute/text-composite WGSL,
  one instanced draw, exact DPI-1/DPI-2 parity, and no stable-frame glyph
  raster or payload upload;
- retained straight-alpha RGBA8 images with checked row stride/source bounds,
  affine destination transform, opacity, persistent nearest/linear samplers,
  production unmasked `Texture.wgsl`, exact DPI-1/DPI-2 parity, and no stable
  texture/vertex/index/uniform upload;
- retained same-device straight-alpha RGBA/BGRA texture views with typed
  device/usage/format/sample validation, zero CPU transfer, and explicit
  borrowed-view lifetime ownership;
- retained anisotropic Gaussian blur, source-alpha drop shadow, and immutable
  one-to-eight-node linear effect chains with bounded texture pooling,
  independent content/effect revisions, and zero-dispatch stable replay;
- all 29 root-group blend/compositing modes, with fixed-function fast paths for
  exact coefficient equations and one destination-aware static WGSL pipeline
  for advanced modes, retained across all six frame families;
- pointer-free semantic layer descriptors plus checked 256 MiB peak/512 MiB
  combined budgets, and retained physical bounded/full-target
  opacity/forced-isolation plus fixed-function Porter-Duff execution through a
  depth-indexed maximum-extent texture pool, target-local analytic/path/glyph/
  image compilation, occurrence-packed composite quads, nested state
  restoration, and zero stable retained uploads;
- pointer-free semantic rounded-rectangle-mask and one-to-eight-node
  effect-chain resources with exact native/.NET layout, typed references,
  canonical validation, and zero managed allocation across 10,000 complete
  caller-buffer builds; rounded masks execute in retained nested composites with
  parent-local coordinates, and effect chains execute in declared order through
  a bounded depth-indexed GPU pool before mask/opacity composition. Stable
  replay retains every texture/binding and uploads zero effect or mask bytes;
- append-compatible per-draw semantic mask state with a typed preceding
  `LAYER_MASK` reference, canonical absence, transactional preflight, and exact
  analytic rounded coverage in analytic, geometry, point-batch, vertex-mesh,
  connected-stroke, and retained-path families. Batch spans split only when
  mask identity changes, keep the retained mask binding alive, and add no
  isolated texture or composite draw. Retained glyphs, plain images, and fused
  color-matrix images share the same analytic or sampled mask state;
- a pointer-free 432-byte analytic mask-chain payload with two to four inline
  transformed rounded masks, canonical zero trailing records, fixed three-step
  shader continuation, and one portable group-2 binding. Vector, text, and
  image chains add no draw or mask texture and retain zero-upload replay;
- clean-room managed lowering of one to four nested canonical affine rectangle
  or rounded-rectangle `PushGeometryClip` scopes to the exact per-draw mask
  state, including rotated/sheared finite invertible transforms. A fifth clip,
  general vector mask, sampled-mask construction, or isolated-layer chain is
  typed fail-closed rather than approximated;
- pointer-free retained semantic solid/linear/radial/two-point-conical/sweep
  brushes with exact production `GpuBrush`/gradient-stop layout, compact
  analytic/path maps, scene-wide referenced-range deduplication, GPU-only
  gradient evaluation, transactional material-buffer growth, and zero stable
  brush/stop upload on Metal and browser WebGPU;
- retained Perlin-noise brushes with independent affine brush coordinates,
  bounded 255-octave evaluation, a zero-table deterministic fallback, or one
  exact validated/remapped 512-record permutation/gradient table; stable replay
  reuses the production `Vector.wgsl` material page without a noise texture;
- pointer-free retained semantic solid text styles with exact production
  `GpuTextStyle` storage, grayscale/aliased/ClearType mode selection,
  scene-state opacity variants, one shared storage buffer, and zero stable
  style upload; glyph shaping, positions, outlines, and atlas ownership remain
  independent reusable resources, while wasm32 narrows canonical uint64 scene
  outline ranges once at the execution boundary;
- pointer-free decoded straight-alpha RGBA8 color-glyph resources with exact
  row-stride/range validation, one native-owned bounded atlas texture, the
  production `Text.wgsl` intrinsic-color path, state/style alpha, and zero
  stable texture/instance upload. The standalone native text library now owns
  bounded borrowed sbix and CBLC/CBDT strike selection, OpenType SVG document
  lookup, and allocation-free COLR version-0/CPAL layer decoding. COLR base
  lookup is logarithmic, palette lookup is constant work per layer, malformed
  tables fail transactionally, and `0xFFFF` foreground-color references remain
  explicit. The current managed-picture bridge still owns compressed PNG/SVG
  decoding and OpenType shaping until their native slices connect; COLR version-1
  paint graphs and SVG vector layers will lower to the existing retained
  path/brush/layer resources instead of creating a second native vector engine;
  the shared provider/browser fixture also lowers and validates ordered vector layers,
  strikethrough, and underline without a text-specific geometry path;
- destination-aware semantic nested blend restore and explicit bounded
  backdrop input using the actual rendered
  parent texture, shared `AdvancedBlend.wgsl`, and a checked three-texture
  scratch budget; empty bounded layers avoid invalid zero-size scissors;
- compact reusable per-frame solid-brush tables only for geometry whose shader
  payload occupies the vertex color fields;
- four vertices and six indices per analytic primitive, one draw/submission,
  lazily initialized reusable resources, and no per-primitive WebGPU resource
  allocation;
- reusable uniform/vertex resources with geometric buffer growth;
- headless hardware-WebGPU image verification;
- a typed zero-copy .NET host sharing device, queue, and render target;
- allocation-free typed .NET substitution over both wgpu-native and Dawn,
  including transactional Dawn device-loss recreation;
- provider-resolved Android arm64/x64 and iOS device/simulator packaging with
  no private Dawn linkage;
- an interactive desktop page cycling reusable 1–4,096 rectangle, analytic,
  geometry, GPU Bezier, connected polyline, dashed, rational-spline, and
  retained compute-path batches plus upload-backed and same-device zero-copy
  images;
- exact managed/native pixel differential and matched submission benchmark.

The complete migration sequence and .NET substitution gates are in
[`NATIVE_CPP_ENGINE_SPECIFICATION.md`](../../docs/NATIVE_CPP_ENGINE_SPECIFICATION.md).
The bounded macOS baseline, exact commands, retained trace inventory, and
scope limitations are recorded in
[`NATIVE_CPP_PERFORMANCE_BASELINE.md`](../../docs/NATIVE_CPP_PERFORMANCE_BASELINE.md).
