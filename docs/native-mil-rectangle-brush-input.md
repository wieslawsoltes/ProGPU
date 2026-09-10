# Rectangular source fills with image/drawing/visual brushes

## Acceptance connection — 2026-09-09

The full LibreWPF Showcase's `ShowcaseDrawingImageBrushBorder` is a 36x36 Border filled by
an ImageBrush over `ShowcaseDrawingImage`. Its background owns source fill selection;
the brush's nested drawing is raster content, not the border's input geometry.
Source WPF `HitTestWithPointDrawingContextWalker.DrawGeometry` tests a non-null
brush against the geometry independently of its pixels. The geometry walker has
the corresponding fill relation contract. This is source-backed, not a live run.

Native image-brush replay isolates vector sources in a layer, including this
DrawingImage. The complete input producer rejected the unannotated layer or could
otherwise select only a brush's mapped image rectangle. Managed replay similarly
exposed flattened brush content instead of the original rectangular fill.

## Implementation

For actual rectangular tile-brush fills, native MIL now surrounds brush replay
with the builder's existing logical rectangle scope. Its state comes from source
geometry placement and inherited clips **before** viewport/brush transforms,
brush opacity, sampling and isolation are applied. The input producer emits one
rectangle and skips the enclosed raster commands. The scope closes before any
separate pen. Native raster brush preparation and owned resources are unchanged;
SAVE/RESTORE preserves the outer raster state too. An unavailable/invalid rendering
contract still fails; the annotation does not authorize renderer fallback.

This reuses canonical source rectangle metadata rather than a second geometry
snapshot, CPU pixel sampling, image-source owner map, or fake WPF drawing. Empty
brush content still leaves input for a non-null brush and a nonempty source fill.
The annotation is not used for nonrectangular fills or BitmapCacheBrush sources.
Unsupported inherited clips still reject complete input instead of broadening it.

The managed LibreWPF replay adapter adds a typed source-rectangle scope capability
to its direct and retained sinks. It wraps actual rectangle DTO fills of portable
image/drawing/visual brushes, closes through finally/ordinary Pop and leaves pen
replay outside. The implementation shares ProGPU's retained logical-rectangle
command/index machinery. Its existing wire-neutral flag retains the historical
name `IsImageHitTestScope`; its semantics are one source rectangle replacing its
internal raster draws. No command format or snapshot-field migration is needed.
Nonrectangular geometry bounds are never admitted as rectangular source input.
Managed rectangle DTOs and paths accepted by the existing exact rectangle clip
readers share this entry. Other source geometry representations and geometry-local
transform combinations remain separate input contracts, not bounds fallbacks.

Cost is O(1) additional metadata/state per eligible source fill, independent of
brush pixels/tiles. Stable retained snapshots preserve those scopes. The existing
intrinsic rectangle placement and canonical GPU query remain unchanged. There is
no new numeric CPU fallback, pixel readback/repacking, per-tile input submission,
quality constant or execution-policy default. No performance gain is claimed.

## Paired applicability and design references

Native MIL needed the logical fill owner around brush isolation. Managed ProGPU's
index already supports logical rectangles; LibreWPF's typed replay adapter needed
to publish the source fill rather than brush content. Both paths are changed and
have authored fixtures. Source WPF's brush/geometry transport is unchanged.

Use the [input ownership research](native-mil-hit-test-ownership.md#design-references-and-decisions)
and [drawing-mask distinction](native-mil-drawing-mask-input.md#research-and-applicability):
WebRender's retained spatial metadata, Skia/SkParagraph's geometry/layout boundary,
Direct2D/Win2D's fill-versus-raster distinction, DirectWrite's layout-owned input,
Vello/Parley's scene/layout separation and HarfBuzz's reusable shaping state.
Adopt retained input ownership; reject deriving it from texture coverage or another
engine's compositor implementation. No third-party implementation is ported.

Startup/lazy initialization, shaping/layout reuse, visibility culling, glyph/path/
texture cache keys and eviction, demand-driven uploads, workers, GPU batching,
DPI/subpixel/hinting, fallback/variable-font state and device/atlas invalidation
are unchanged. Required exact-head measurements remain in final qualification.

## Authored qualification and limits

Native fixture scene 9839 uses the existing image-brush harness with complete
input enabled: bitmap, DrawingImage, DrawingBrush and VisualBrush sources;
zero opacity, remapped/rotated viewport, empty vector content, repeated tiles and
a separate solid pen. It requires exactly one source rectangle plus the pen when
present, without brush-source owner leakage. Default fixture flags stay unchanged.

LibreWPF's product drawing-context fixture checks sparse/empty/missing drawing
content, an outer source clip, a separately captured pen and a following unmasked
draw. Its retained-sink fixture checks typed source traversal excludes internal
brush drawings and restores the following draw and owner.

These fixtures are authored, not executed. Full application/package/provider
qualification, native/managed/Windows query and image comparisons, lifetime,
performance and exact-head CI remain required. This does not finish application
input, nonrectangular brush-fill input, cached-picture source contracts or general
DirectX/Direct2D coverage. Only concrete acceptance dependencies reopen those paths.
