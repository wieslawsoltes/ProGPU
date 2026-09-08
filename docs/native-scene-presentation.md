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

Compilation checkpoints are recorded in the PR. Full renderer/provider builds,
all tests/verifiers, macOS/Linux and Windows Parallels runs, text/clip/image
comparisons, device/lifetime tests, cache/performance measurements and exact-head
CI remain outstanding until the requested core feature freeze. Keep this document
and the host guards honest about that distinction.
