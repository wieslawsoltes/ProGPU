# Native scene presentation: viewport and independent DPI

## Contract checkpoint — 2026-09-08

This is implementation groundwork for the core native MIL application milestone,
not completed viewport/DPI rendering. The public C frame now carries an opt-in
presentation suffix, with generated managed wire declarations and a typed
`NativeScenePresentation` submission overload. The renderer validates the complete
mapping before GPU work. Full-target uniform mappings execute through the existing
renderer; partial viewports and unequal axes explicitly return `Unsupported`.
LibreWPF's corresponding guards remain until all consumers below are implemented.

## Coordinate and compatibility contract

Physical device position is `(viewportX + logicalX * scaleX,
viewportY + logicalY * scaleY)`. The scene remains in logical DIPs. The viewport
is a physical presentation boundary inside the target, not an application visual
transform or a replacement for source geometry clips. Keep independent positive
finite scales; never average them or stretch a uniformly rasterized intermediate.

The 32-byte pointer-free descriptor contains its size, four unsigned viewport
coordinates/extents, two scales and a zero reserved word. Validate target bounds
using subtraction to avoid unsigned addition overflow. Reject empty/out-of-range
extents, nonfinite/nonpositive scales and overflowed logical extents. Native
resolution publishes its output only on success; managed validation occurs before
the synchronous native submission. No allocation, device creation, pixel work or
resource lease is needed to construct/validate this fixed-size value.

`PROGPU_NATIVE_SCENE_FRAME_PRESENTATION` opts into the complete suffix. Unflagged
frames retain full-target uniform semantics. On 64-bit targets the frame grows
from 80 to 112 bytes, with the suffix at offset 76; original fields do not move.
The pre-damage prefix and previous damage-capable layout remain accepted. Damage
field availability uses the field endpoint, not the enlarged structure size.
An opted-in truncated suffix is invalid, and older renderers reject its unknown
flag rather than ignoring the requested mapping. This does not bump the unrelated
Direct2D ABI or expose application-facing pointers/reflection.

## Remaining execution work, in dependency order

State/domain checkpoint: native state cursors now accept independent scales for
uniform guideline translation, explicit physical offsets, nearest-guideline
per-point deformation and composite snapping. Output stays logical; integer
viewport placement contributes no fractional snapping phase. Source inspection
while authoring mixed-axis coverage identified incorrect first-Y indexing after
multiple X coordinates; that indexing is corrected with an authored regression.

Native rectangle/layer scissor helpers now project four edges with independent
scales and viewport origin, then intersect the physical viewport. Projection uses
SSE2, NEON or Wasm SIMD128 lanes where available, with the fixed four-value scalar
path only on targets without those intrinsics. It preserves separate multiply/add
operations, existing near-integer tolerance and outward integer clipping. A scalar
binary-fraction corpus is authored as the output oracle; no speed claim is made.

Layer target cursors retain the presentation domain alongside each materialized
extent. A local bitmap-cache page resets its origin to zero, sizes each dimension
with its own scale, and may exceed the root window; nested isolation clips to that
page, not the root viewport. Popping the scope restores the previous mapping in
O(1). Storage is fixed O(maximum materialized depth); there is no per-layer heap
allocation or backwards scan. All seven render/preflight/budget layer cursors and
six state cursors consume the normalized frame presentation record.

This still does **not** enable advanced frame rendering. Remaining projection,
mask/effect, raster scale/phase and cache consumers must be completed together
before the existing unsupported guard is removed.

Geometry/cache checkpoint: preflight and analytic/vector/path/glyph/image
preparation now map their logical transforms through the current layer's
presentation domain. The mapping retains independent ratios to the family's
existing raster-DPI basis; its final projection applies that basis once. It is
not an average DPI or a stretched intermediate. Per-point path guidelines resolve
in logical coordinates before points enter that same mapping. Transform
coefficients and paired point mapping reuse the intrinsic lane helper. Physical
origin differences are computed before float narrowing, retaining small local
offsets between large nearby unsigned origins. Equal-axis, zero-origin calls
retain the original localization arithmetic.

Draw and composite rectangle-scissor calls now use the current layer domain and
localize their physical intersection to the target. Logical clip/guideline
metadata is deliberately not rewritten along with the draw transform. This
separation is necessary for nested cache pages and exact mask consumers.

Analytic, path, glyph and image compiled-page identities, path/glyph upload
identities and the render-bundle identity now include all six viewport/axis
fields when presentation differs from the legacy full/uniform mapping. Legacy
hashes are preserved. The fixed 24-byte identity chain is O(1), allocation-free
and sequentially dependent; it is not a SIMD-eligible whole-buffer workload.
Brushes, text styles, logical resource snapshots and decoded color-glyph bitmap
atlas identities remain independent of device placement. Picture and 3D identity
consumers still need their own integration audit.

Layer/effect checkpoint: local-cache quads reconstruct logical source distances
using each cache axis, then apply the captured composite transform, logical
guideline snapping and parent presentation exactly once. Tile-cache composites
use that same parent localization. Already-physical isolation/backdrop quads
retain the base raster projection; they must not apply the DPI a second time.
The existing zero-origin uniform composite path retains its operation ordering.

Gaussian/box blur distances and shadow offsets now share a typed physical-effect
resolver between semantic preflight and dispatch. Its four independent lanes
reuse the SSE2/NEON/Wasm SIMD128 mapping; viewport translation never changes
effect distances. It rejects nonfinite/negative or over-limit physical kernels
before resource preparation, preserving the existing 128-pixel radius contract.
The same existing separable GPU shaders, intermediates, uniforms and quality
constants execute the effect; no CPU pixels, shader fork or new fallback is added.
Preparation remains fixed O(1) per node, with at most eight nodes per chain.

Retained-layer content revisions and ordinary effect-output scene revisions now
include presentation metadata. Owner/operation identities stay stable so a DPI
change invalidates content without inventing new cache owners. Legacy content
hashes are unchanged. This intentionally uses the complete root mapping even for
local pages; it is conservative across viewport moves, not a maximal-reuse claim.
Mask/picture mapping, glyph/path raster details, 3D, damage/clear behavior and
their remaining cache consumers still block enabling advanced presentation.

Mask checkpoint: per-draw and layer-composite mask creation now receives the
current target's presentation explicitly. Rounded/analytic chains and vector
clip paths share the same intrinsic affine localization as draw geometry.
Path/boolean topology, fill rules and sample grids remain unchanged; no path is
replaced by its rectangle bounds. Vector-mask retained revisions include the
presentation fields beyond the legacy DPI/dimensions/origin key.

Coverage bitmap masks map target-local physical positions back to source UVs
through a fixed double-precision affine inverse. Legacy mapping preserves the
previous arithmetic; advanced mapping subtracts physical origins before
narrowing. Invalid or nonrepresentable UV coefficients fail before GPU allocation,
without partially publishing an output. This is O(1) matrix/uniform setup, not a
CPU pixel loop. Existing source pixels and nearest/linear sampling remain intact.

Brush and geometry masks localize their geometry while retaining source material
coordinates. Composite guideline deformation resolves in logical coordinates
before the device transform. Geometry-mask scissor culling uses the independent
physical axes and viewport origin; its padded envelope only limits raster work,
not the exact mask. Composite vector/brush/geometry children receive the same
presentation and continue through their existing bounded GPU composition.
Picture-backed masks (standalone or composite children) still fail explicitly for
advanced mappings until nested-picture raster ownership and sampling are wired.
The whole-renderer guard remains; this checkpoint is not enabled host support.

Nested-picture checkpoint: the child frame is now resolved before creating its
texture/engine. Explicit source extents derive independent raster axes from their
own dimensions and logical bounds; they do not borrow or average the parent's
DPI. Target-space pictures inherit the parent viewport/axes, intersected with
their allocated source extent. The fixed resolver rejects invalid dimensions,
scale and unsigned-bound overflows transactionally. Child submissions carry the
same native presentation suffix; a still-unsupported child mapping propagates a
failure rather than producing a stretched uniform-DPI approximation.

Standalone picture-mask sampling now uses the shared inverse-UV mapper, including
target-local physical crop offsets. Target-space pictures publish an affine UV
map with the source crop, instead of relying on offsets previously consumed only
by composite masks. Cache-guideline inverse deformation is conjugated from logical
coordinates into the parent's target-local physical coordinates using each axis
and viewport origin. The nested RGBA texture, alpha sampling, linear sampler,
source leases and existing GPU submission model are unchanged. Ordinary picture
images still rasterize from their own immutable source descriptor: their cache
must not acquire a parent-viewport dependency simply because mask submission did.

This supersedes the standalone picture-mask rejection in the preceding checkpoint,
but **not** the global renderer/host guard. Composite picture masks remain gated:
their current `ClipCompose.wgsl` consumer loads the source texture directly and
does not evaluate a child's affine sampling uniforms or mask opacity. It needs
shared sampled-mask composition before transformed/source-extent pictures and
guideline deformation can be claimed correct in a composite. This is an existing
composition contract gap, including legacy cropped/source-extent cases, not a
reason to bypass the guard or claim parity from the child-frame compilation.

Sampled-composition checkpoint: composite masks now bind each child's existing
sampler, texture and 96-byte mask-sampling uniform record. A dedicated lazy
fragment entry point in `ClipCompose.wgsl` evaluates the child's affine UV,
outside-coverage behavior, alpha/red channel and opacity before multiplying the
previous R8 accumulation. This replaces the composite-only direct-texel shortcut;
ordinary vector clip-node composition keeps its existing raw-texel entry point.
The picture-specific advanced-presentation gate is removed and the parent mapping
is forwarded to picture children. The global renderer/host guard remains until
the other raster, 3D and damage/clear consumers are complete.

`SampledMaskCommon.wgsl` is the original ProGPU sampled-texture-mask function
extracted from `Texture.wgsl`, with the same arithmetic, filtering and quality
contract. Managed texture rendering and native texture/composite rendering consume
this exact source. Managed loading concatenates it once in static initialization;
native CMake embedding prefixes it for both modules and tracks it as a dependency.
No shader text is generated per frame and no backend-specific WGSL fork is added.

For K mask children and P target pixels, composition remains O(K * P) GPU work,
one existing draw/pass per child, two reads per pixel and two ping-pong R8 targets.
The new pipeline is created only when a composite mask first needs it, and released
with native clip resources. The change adds no intermediate resolve, CPU pixel
readback/repacking or per-child queue submission. Child mask uniforms are released
without destroying their GPU backing after encoding/submission, matching texture
ownership; otherwise their new sampled use could outlive a destroyed buffer.
Actual lifetime and output correctness still require final runtime qualification.

| Consumer | Required implementation before enabling host support |
| --- | --- |
| Semantic state and guidelines | Apply physical mapping at the device boundary, keep per-axis pixel snapping and explicit physical guideline offsets correct, and avoid double application in nested scopes. |
| Layer target cursor and scissors | Track root viewport origin/extent separately from offscreen origins, clip target-local replay correctly, and preserve load/clear plus logical damage semantics. |
| Analytic/vector/path/image families | Map geometry, material domains, source sampling and coverage into the same device basis. Retain exact path clips; do not broaden to bounds. |
| Glyphs and text styles | Derive actual per-axis raster scale/phase and cache identity while preserving shaped logical positions, hinting and coverage quality. Do not reuse uniform-resolution glyphs as a stretched substitute. |
| Masks, effects and nested pictures | Map mask bounds, blur axes and shadow offsets consistently. Distinguish a picture's own raster contract from the parent's presentation; localize layer composites exactly once. |
| 3D and retained caches | Use the physical viewport for projection and depth bounds. Include both scales and all viewport fields in affected path/glyph/image/layer/render-bundle keys; changes must invalidate stale output without discarding logical source data. |
| LibreWPF host | Pass actual viewport and axes through this shared API; remove guards only after the consumers above are implemented. Preserve typed input coordinates and independently selectable managed mode. |

The relevant native implementation is `progpu_native_semantic_render_execution.cpp`
and its state/target cursors, family execution and mask/effect helpers. Its current
uniform DPI is used throughout preparation, caches and execution; changing only
the final projection or host root would leave those consumers inconsistent.

## Original provenance and paired applicability

Original ProGPU source is `3bfed8d6`: the C scene frame, NativeCompositor.RenderSceneCore,
semantic render execution/state, generated wire contract and managed
Compositor.RenderTargetViewport/host-frame API. The managed renderer already keeps
explicit viewport metadata. Its existing behavior is the pairing reference, not
proof that every independent-axis case is qualified. Both implementations require
matched output/cache/damage/text tests at final qualification. This checkpoint
changes native submission metadata and validation, not either renderer's mapping
algorithm; no managed raster algorithm is replaced.

The subsequent native state/domain implementation ports the original ProGPU
algorithms at `99772128`: semantic_state_cursor guideline resolution,
resolve_semantic_scissor and semantic_layer_target_cursor. Uniform entry points
delegate to equal-axis mappings. These native helpers interpret compiled semantic
resources; managed WPF adapts typed source guideline data in
ProGpuCompositionCommandSink.TrySnapGuidelineX/Y and already keeps the axes
separate. Managed Compositor also retains explicit viewport metadata. Neither
managed algorithm is replaced by this native decoding fix. Source representations
and rounding paths are not identical, so this applicability finding does not
claim managed/native output parity; common-scene differentials remain mandatory.

Geometry/localization and presentation identity work derives from original
ProGPU `9080525c`: localize_semantic_state, semantic draw-family preparation,
per-point path deformation, SIMD edge mapping and content-hash/cache code.
Managed Compositor's viewport/DPI cache fields are the applicability reference;
its representation is not replaced by native semantic wire interpretation.
No new CPU rasterizer, shader, shaping pass, image copy or per-item native call is
introduced. Glyph-basis/raster quality and mask/composite coordinates remain
explicit pending output-qualification requirements.

Layer/effect integration derives from original ProGPU `011a465d`:
`append_semantic_transformed_layer_quad`, semantic effect preflight/dispatch,
`presentation_content_hash` and the native effect-output cache key. Managed
`Compositor.cs` effect rendering (`RenderOffscreen` and the subsequent
`ApplyBoxBlur`/`ApplyGaussianBlur`/`ApplyDropShadow` calls) already converts logical
distances by its scalar offscreen DPI and applies shadow placement during
composition. That path has no native scene-presentation suffix and is unchanged;
equal-axis behavior remains the common reference. The new unequal-axis native
submission is not evidence of unequal-axis managed effect parity. Matched
application images, cache invalidation and layer-composition differentials are
still required at qualification; no shared shader algorithm was changed here.

Mask integration derives from original ProGPU `9f4ce9c6`: semantic layer-mask
resource/binding creation, the coverage UV inverse, vector-mask revision and
rebuild logic, brush/geometry mask compilation and composite-mask children.
Managed `Compositor.cs` owns a separate retained mask/clip pipeline and does not
decode this native presentation suffix. Its existing scalar frame plus logical
transform path remains unchanged, as do the shared mask/vector shaders and source
materials. This native metadata integration must still be compared against that
managed output for common scenes at final qualification; it does not establish
independent-axis parity by itself. The existing primary-research decisions below
continue to apply: logical clips/materials are separate from device/layer mapping,
and device-dependent masks are invalidated without rebuilding logical content.

Nested-picture work derives from original ProGPU `27c888f9`:
`create_semantic_picture_binding`, `create_semantic_picture_image`, the shared
mask-UV mapper and the generated native frame contract. Managed
`Compositor.CreateMaskSamplingUniforms(MaskPixelBounds)` retains physical mask
placement, and its affine-mask preparation explicitly composes logical-to-physical
canvas placement before inversion. The native fix restores that coordinate
separation for its nested semantic stream; it does not replace managed mask
algorithms or shared shaders. Common cropped/affine picture scenes and mask
opacity/guideline cases require matched managed/native output qualification.
No foreign implementation is copied and no new CPU pixel fallback is introduced.

Sampled composition derives from original ProGPU `117c4d5d`:
`Texture.wgsl:sample_mask_alpha`, `ClipCompose.wgsl:fs_compose`, native clip resource
creation, layer-mask bind-group layout and composite child ownership. The managed
and native texture paths are both updated to consume the extracted common function;
the new composite entry point uses it directly. Managed compositor mask placement
remains authoritative for common-scene comparisons; it does not use the native
semantic composite stream or its former direct-texel shortcut. Shader initialization
is static/lazy as before, with no per-frame resource loading. No performance claim
is made until representative managed/native measurements and images are available.

## Primary research and design decisions

- [Direct2D DPI contracts](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-high-dpi)
  and [Win2D drawing sessions](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasDrawingSession.htm)
  inform the separation of logical content, device scale and session state.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) informs the distinction
  between drawing transforms, clips and layer/device ownership. Do not emulate
  presentation by mutating WPF's retained root or copying a foreign implementation.
- [WebRender overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  supports retaining scene data separately from frame work; viewport/DPI changes
  should update device-dependent preparation rather than recreate logical layout.
- [Vello render parameters](https://docs.rs/vello/latest/vello/struct.RenderParams.html)
  provide a comparison for target-size/render policy being explicit submission
  data. Retain ProGPU's existing GPU batches, caches and lazy pipeline ownership.
- [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  inform retaining CPU shaping/layout independently of device rasterization.

No foreign source is copied. Startup, font fallback/variation, shaping reuse,
worker scheduling, upload policy and device-loss ownership are unchanged here.
Future execution must preserve lazy setup, visibility culling, demand uploads,
bounded cache residency and generation invalidation. The descriptor/validation
cost is fixed O(1); it adds no shader, whole-buffer CPU loop, readback or per-item
submission. No performance claim is made before matched measurements.

## Authored coverage and qualification status

Managed/native fixtures cover exact layout, viewport origins, unequal DPI axes,
legacy defaults, bounds overflow, invalid scales and native transactional failure.
Provider fixtures cover explicit legacy-equivalent retained replay, unsupported
axis mappings without submission, truncated suffix rejection and old damage flags.
Fixtures are authored, not executed. The generated managed contract is produced
by the repository generator, not handwritten.

Native internal fixtures additionally cover per-axis static/explicit/per-point
snapping, multiple X/Y guideline indexing, SIMD edge projection versus a scalar
oracle, viewport clipping, independent cache dimensions, nested local-page
isolation beyond root dimensions and mapping restoration. Both the production
semantic-state translation unit and the internal fixture translation unit compile
with Apple Clang C++20 and warnings-as-errors. This is compilation, not execution.

Additional authored fixtures compare affine and point localization against an
independent scalar device-coordinate formula, preserve logical clip metadata,
check bit-identical legacy localization, cover large nearby physical origins,
and vary each presentation identity field independently. The main semantic
render-execution translation unit also compiles with Apple Clang C++20 `-O2`
and warnings-as-errors using the exact header pins from
`eng/build-progpu-native.sh`: wgpu-native `33133da4ec5a0174cb21539ef2d3346f75200411`
and webgpu-headers `aef5e428a1fdab2ea770581ae7c95d8779984e0a`. Headers are external
build inputs in temporary storage, not copied into product implementation.
This does not qualify a fully linked renderer/provider, another ISA or runtime.

The layer-resource execution source also compiles with these pinned headers and
the repository's normal CMake shader embeddings. The state and internal fixture
sources compile after adding effect-distance scalar-oracle cases for all three
kinds, viewport independence, physical kernel limits, overflow, transactional
failure and equal-axis behavior. The portable MIL fixture target builds as well.
No fixture, shader, image, VM workload, verifier or benchmark was executed for
this checkpoint. Full cached/tile-layer composite images remain unqualified.

Mask additions include authored inverse-mapping/forward-mapping differentials
for affine rotation/shear, unequal axes, cropped targets and large nearby physical
origins, plus legacy UV values and singular transactional failure. They compile
along with the state, main render execution, layer resources, bitmap/vector mask
resources, brush/geometry mask resources and composite-mask resources under the
same Apple Clang C++20 warnings-as-errors setup. Compilation is not execution;
mask sampling/AA images, mixed cache guidelines and retained invalidation still
require final runtime differentials.

Authored child-frame fixtures additionally cover inherited cropped viewport axes,
source-owned unequal axes, unchanged scalar image descriptors, oversized and zero
dimensions and transactional failure. State, picture-mask resources, layer
resources, composite-mask resources and internal fixture translation units compile
with the same strict C++20 setup. Child GPU rendering, composite mask sampling and
cross-platform output remain unexecuted and unqualified at this checkpoint.

Sampled-composition source fixtures assert that managed texture and native
composition call the same embedded function and that both native embeddings track
the common prefix. The managed ProGPU tests project builds with zero warnings and
errors. Native clip, composite-mask and main render-execution sources compile with
Apple Clang C++20 `-O2` and warnings-as-errors against regenerated embeddings.
The native image/layer resource source also compiles against the shared texture
embedding. LibreWPF's tests project builds with 116 warnings and zero errors;
these are build results, not executed tests or a green-CI assertion.
These checks are compilation/source-contract evidence only: WGSL pipeline creation,
rendered affine/cropped picture masks, child opacity products, outside-UV coverage,
stable replay and in-flight lifetime tests have not been executed. Final GPU image
differentials must compare composite masks to the same children applied through
ordinary managed/native sampled-mask rendering, including fractional transforms
and nested cache guidelines.

Compilation checkpoints are recorded in the PR. Full renderer/provider builds,
all tests/verifiers, macOS/Linux and Windows Parallels runs, text/clip/image
comparisons, device/lifetime tests, cache/performance measurements and exact-head
CI remain outstanding until the requested core feature freeze. Keep this document
and the host guards honest about that distinction.
