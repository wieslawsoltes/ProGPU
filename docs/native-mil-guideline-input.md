# Source input and raster guidelines

## Application dependency

The LibreWPF MVP's text underlines/control chrome must remain hittable at source
coordinates when guidelines change raster placement. Source WPF's point and
geometry drawing-context walkers treat guidelines as input-neutral. This batch
closes that source-coordinate connection, not complete native host query routing.

## Paired implementation

Native MIL already retains original primitive coordinates and transforms in
`Mil/progpu_native_mil.cpp`: `apply_static_guidelines`, `apply_dynamic_guidelines`
and `save_state` attach a guideline resource. The semantic renderer resolves it
later. The builder hit producer now admits `GUIDELINE_SET` only under explicit
`scene_hit_test_opacity_mode::source_geometry`, including logical image scopes.
It does not clear or rewrite the raster resource. Generic rendered-visibility
index capture continues rejecting this state because it cannot infer snapped
coverage from the original coordinates alone.

The managed WPF recorder has two existing routes: typed native primitive ingress
retains original coordinates; ordinary `DrawLine/Rectangle/RoundedRectangle/Ellipse`
snaps before recording. The latter now publishes ProGPU `SourceHitTestGeometry`
alongside the unchanged raster fields. A line publishes its original spine and
retains its typed pen/caps; auxiliary raster cap draws are explicitly excluded
from input because the primary line already owns their source coverage. This
also avoids indexing square-cap endpoint extension twice. Brush adaptation,
raster cap emission, transforms, aliasing and draw ordering are unchanged.

`GpuRenderCommandHitTestCacheBuilder` applies the annotation to a value copy and
reuses its normal geometry lowering. Invalid shape/metadata combinations throw
and prevent partial index publication until Clear; an exclusion cannot swallow
a state push/pop. Logical image scopes retain their destination input independently
of annotated contents. Raster-derived geometry caches are not reused for source
paths; applicable dashed/device-width queries prepare the original geometry.
This batch does not claim improved dash-query allocation or performance.

Retained picture command storage uses an optional indexed value-array sidecar;
clones share immutable storage. Existing compact raster formats are unchanged.
Construction remains O(C) for C commands, with O(C) extra metadata storage only
when an override exists; lookup/override are O(1), allocation-free. Ordinary
pictures allocate no sidecar. Raw RenderCommand carries one additional value.
`GpuPictureBounds` explicitly ignores this input-only metadata so raster bounds
still include snapped placement and auxiliary caps. Portable SKPicture archives
reject annotated commands until their versioned format can preserve the contract.
Native picture compilation receives any supplied canonical hit index separately;
it must not infer source input by re-reading raster fields.

This is typed metadata/dispatch, not a new compute kernel or scalar fallback.
Independent transforms, line metrics and query math continue through existing
intrinsic/vector implementations. No per-item native calls, GPU submissions,
pixel readback, new shader or source-local geometry algorithm was added.

## Provenance and authored coverage

Original ProGPU sources reused: `ProGPU.Scene/GpuRenderCommandHitTestCache.cs`,
`ProGPU.Scene/RenderCommand.cs`, `ProGPU.Vector/GpuHitTesting.cs`, and native
`Scene/Builder/progpu_native_scene_builder_hit_test_capture.cpp`. Source WPF
`HitTestWithPointDrawingContextWalker` and `HitTestWithGeometryDrawingContextWalker`
were consulted for observable guideline behavior, not copied into ProGPU.
The [existing cross-engine research and decisions](native-mil-hit-test-ownership.md#design-references-and-decisions)
remain applicable: separate source geometry/layout from raster placement and
retain scene-qualified owners. No new shaping, cache, worker or GPU architecture.

- Native scene 9818: static/multiple and explicit-offset guidelines, original
  transformed rectangle input, logical image input, preserved raster state and
  rejection of undeclared rendered-visibility behavior.
- Native scene 9819: canonical MIL Y1/Y2 scopes preserve original rectangle
  coordinates while emitting actual guideline resources.
- Managed `SourceHitTestGeometryTests`: compact snapshots/clones, rectangle,
  rounded rectangle and ellipse input versus raster bounds, invalid scope
  annotations and logical image precedence.
- LibreWPF `WpfReplayToProGpuCommandTests`: actual recorder Y1/Y2 snapping,
  original shape input and each line cap kind through retained snapshots.

Fixtures are authored for final execution. Builds are not runtime qualification.
Final renderer/headless, Svg.Skia exact-difference, native provider/module,
Windows comparison, package/application and performance gates remain required.

## Still open

Spatial masks, geometry clips, cached-brush paths, cache-specific snapping and
ScrollableAreaClip's source coordinate frame remain separate input contracts.
Native host owner queries remain disabled pending complete core coverage and
query routing. No blanket cache/mask/scroll admission, full guideline parity,
feature freeze or merge readiness follows from this checkpoint.
