# Owned DrawingImage shader samplers

An original MIL ImageBrush sampler can now name an initialized DrawingImage in
the same channel. The existing owned drawing/tile compiler produces the full
RGBA child picture. Its ImageBrush mapping retains the original drawing-bounds
origin rather than substituting the zero-origin Width/Height extent. It applies
the original viewbox, viewport, brush transform, opacity and addressing. No synthetic bitmap, CPU
raster, new renderer or shader wire is introduced.

Sampler declaration may precede DrawingImage initialization. Capture cannot:
an uninitialized image is an invalid handle. An initialized image with a null
drawing, or an initialized empty DrawingGroup, is genuine transparent content.
Neither is confused with an uninitialized bitmap upload. The picture remains
owned and immutable after the source channel is updated or destroyed.

Before a DrawingImage sampler can take any empty/transparent tile shortcut, the
existing ordered dependency-revision walk checks its entire resource closure.
The sampler-only policy retains the existing depth bound and active-path cycle
check; missing resources fail. DrawingBrush, VisualBrush, BitmapCacheBrush,
external/double-buffered/D3D images and media remain unsupported even inside a
nonpainting branch. Ordinary DrawingImage consumers keep their existing policy.
Dependency deletion and updates retain the original channel graph rules; every
drawing, child, brush, geometry and animation generation contributes to capture
revision. Original sampler transform-animation gates remain unchanged.
This is ownership/revision preflight, not eager evaluation of every nested
semantic value. Registered scalar/glyph/guideline resources retain the ordinary
drawing compiler's lazy semantic validation; no claim is made that an empty
paint evaluates otherwise unused current-value dictionaries.

The shared ImageBrush DrawingImage path now preserves original bounds X/Y for
absolute viewboxes. Relative viewboxes still resolve against the same bounds.
Replaying the drawing at that original origin cancels its independent DrawImage
normalization exactly once; ordinary DrawImage destination mapping is unchanged.
The existing vector intermediate clips TileMode.None to the actual viewport.
An absolute viewbox at (14,20) and one at (4,0) are deliberately distinct for
content whose original bounds start at (10,20). No bitmap behavior changes.

## Implementation provenance and applicability

The source contract is Microsoft's public
[ImageBrush.ImageSource](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imagebrush.imagesource)
and [DrawingImage](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.drawingimage)
API. Rendering and ownership reuse ProGPU-owned `append_single_tile_brush`,
`append_drawing_image`, `capture_brush_rectangle` and
`append_cache_resource_revision` from `7a3623381f9af7f5c18d81ff0e367f6a88265016`.
This connects an existing source family to an existing capture capability; it
does not change scene, shader, pipeline, atlas or device-loss architecture.
Both native providers consume the same retained picture resource. The paired
managed source paths at LibreWPF `fab2bc2383c181660430ac4d27d1c400fbb553e7`
already retain this distinction:

- `WpfDrawingReplay.TryReplayPortableDrawingBrushFill` retains the original
  drawing bounds, resolves relative viewboxes against their X/Y, leaves absolute
  viewboxes unchanged and retains the source clip. Its separate direct DrawImage
  path owns destination normalization.
- `WpfNativeMilSceneCompiler` publishes the original X/Y/Width/Height through
  the typed `IPortableDrawingBoundsSource` DrawingImageBounds sideband. Null or
  known-empty drawings still retain graph invalidation while publishing a null
  drawing handle.
- The existing managed
  `ObjectRenderDataDrawingContextReplaysDrawingImageBackedImageBrushThroughDrawingTiles`
  control preserves a nonzero drawing origin and its image/drawing dependencies.

Consequently no managed source adapter, wire or renderer adjustment is required
for this correction. Both inferred native bounds and explicit nonzero source
bounds are retained in the authored controls; source-exported bounds are not
replaced with an inferred zero-origin extent.
No foreign implementation is copied. Dependency traversal remains bounded by
the existing graph/depth budgets and shared capture owns all GPU work.

## Authored controls and execution status

The shader fixture contains nine same-owner source states, with three complete
frame replays per state on each native provider. Its literal colors distinguish
original/shifted bounds, opacity after overlapping children, absolute/relative
viewboxes, viewport clipping, tile addressing, live brush updates, null drawing,
an empty group and refilling that same group. The original Microsoft WPF
[companion](original-shader-drawing-image-reference.md) authors the same nine
states and 27 replays without constructing a BitmapSource.

Ordinary ImageBrush controls separately paint the relative, positive absolute
and shifted-origin cases without a ShaderEffect. Each is captured with inferred
bounds and with the original nonzero DrawingImageBounds sideband: six
configurations and 18 full-frame replays per provider. They retain the source
graph through null/empty/refill transitions, retire it before replay and compare
every pixel against literal colors and against the paired bounds mode. Exact
source command order, two original child draws and vector-tile composition
remain asserted; no per-source-command GPU draw count is invented. A separate
ordinary original-WPF family retains those three states and nine replays without
creating a ShaderEffect, including positive ordinary drawing on native ARM64.
It does not alter the nine-state shader reference inventory or its distinct
unavailable SoftwareOnly control.

Raw controls retain declaration-before-initialization, untouched caller output
on failed capture, graph mutation/deletion rollback, leaf-only revision changes,
explicit bounds, immutable scenes after source destruction/detachment, hidden
unsupported dependencies, cycles and the unchanged recursive depth budget.
They distinguish ownership preflight from lazy semantic replay rather than
pretending every registered leaf already contains an initialized current value.

All controls are authored only. No build, syntax check, test, verifier, original
WPF observation, native/provider execution, packaging, application run or CI
dispatch was performed. Qualification remains deferred to the final integrated
tip; neither source inspection nor these authored assertions proves parity.
