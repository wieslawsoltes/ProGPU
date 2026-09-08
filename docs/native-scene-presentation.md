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

Compilation checkpoints are recorded in the PR. Full renderer/provider builds,
all tests/verifiers, macOS/Linux and Windows Parallels runs, text/clip/image
comparisons, device/lifetime tests, cache/performance measurements and exact-head
CI remain outstanding until the requested core feature freeze. Keep this document
and the host guards honest about that distinction.
