# Owned visual composition port

Implementation: `composition::visual`, with no renderer or Scene changes. This
increment ports retained hierarchy/property ownership and produces real immutable
local Scene pages. Automatic whole-visual rendering remains a separate integration.

Implemented:

- Thread-local `VisualCompositor` domains; Visual, ContainerVisual, DrawingVisual,
  SpriteVisual and ShapeVisual handles; weak parents, cycle/domain admission,
  ordinary/topmost partitions, relative sibling moves, and original independent
  state/content/tree invalidation policies. Immutable drawing pages replace the
  source's directly mutable DrawingContext ownership API.
- Raw scene properties and separate finite composition-wrapper properties,
  original center/anchor/relative-size/relative-offset/quaternion matrix order,
  full inherited Matrix4 frames, independent coordinate-frame metadata, clipping,
  mask/effect/cache metadata and template children-before-content ordering.
- Original scalar/vector animation bindings over `scene::animation`, shared actual
  animation owners, completion retirement and active-subtree accounting. Custom
  callbacks run only during explicit update/preparation. `WeakVisual` supports
  callbacks without strong callback/visual cycles. Typed ordinal property bags
  preserve distinct scalar/vector/matrix/quaternion/color kinds.
- Shared solid/linear/radial/material brushes, original mapping/interpolation/
  extension/coordinate-transform rules, shared mutable gradient stops with stable
  duplicate-offset ordering, weak counted subscriptions and immutable material
  generations. Original color-matrix/blend/blur/drop-shadow effect descriptors
  retain independent radii, colors and source-order metadata.
- All five concrete composition geometry families: ellipse, rectangle, rounded
  rectangle, line and typed owned path. Original rectangle/ellipse/line trims and
  CompositionPathData's 32-sample Bezier / at-most-128-sample arc length tables;
  trim output remains original analytic subsegments. Unsupported rational trim
  evidence follows the original skipped-measure branch; full paths remain owned.
- Nested composition shape collections, whole-candidate reparenting and cycle
  checks, view-box transforms, original fill/stroke materials, caps, joins, dash
  snapshots and transformed-centerline non-scaling strokes. The original WinUI
  adapter's `MiterOrBevel` label selects its default Miter branch; this port does
  not silently change that adapter to the independent generic pen policy.
- `Visual::snapshot()` stages all new local pages, detects reentrant preparation
  and changed generations, then publishes together. Read-only VisualSnapshot
  accessors retain source identity, versions, complete matrices, clips, frozen
  effect/mask values and ordered children. `mark_snapshot_clean` retires flags only
  for the actual consumed generation. Existing snapshots survive every edit.

Integration: create nodes/resources through one `VisualCompositor`, assign the
typed properties/resources, build the ordered tree, tick animations, then call
`snapshot()`. `snapshot.page().scene()` is the actual `Arc<Scene>` local recording.
The renderer must apply `global_transform()` before geometry/text planning and
consume the snapshot's outer/local clips, masks, opacity, cache/effect frames and
template order at the actual target; pages must not be scaled after rasterization.

Remaining original slices after the renderer increment below: source
hit-geometry/provider/layout callbacks;
mutable composition clip/shadow objects and disposal notifications; full visual
shadow-mask/owned-shader effect adapters; surface/mask/backdrop/effect brush scopes;
external host/platform wrappers and `TransformToVisual` target inversion. Exact
immutable `VisualCompositeClip` descriptors and independently owned Scene commands
are retained, but do not claim those missing provider contracts. Runtime trig,
quaternion and SIMD rounding still require the deferred source comparison gates.
The neutral Rust batch setters publish one coherent change rather than exposing
the original wrapper's intermediate per-field setter notifications.

Only formatting was run. No compiler, tests, GPU validation or benchmarks were
run, as required by the implementation-first instruction. No build artifacts were
generated.

Original source SHA-256:

```
546cd62ba3f2c0d4a85f1479d8c4122c0032b46b090341d088a2749fa1c264fe  src/ProGPU.Scene/Visual.cs
9e4669a01c1514768b3cd76c45c755332267dfea1114e788fc9286d7eed574d7  src/ProGPU.Scene/CompositionAnimations.cs
61873a389ada4622dc8dd360fcd8d04971372ae1d0d5d5ed48b3c8d3072c67d4  src/ProGPU.WinUI/Composition/CompositionVisuals.cs
a27704d1f649d530dce1cf3a5ff5e987908c38064ebf66e90df9739ffec9131b  src/ProGPU.WinUI/Composition/CompositionShapes.cs
525c9ec8b5b291bcf40968d8d11c6d7d3d8676046f1736d6cf0ea09d020f9f36  src/ProGPU.WinUI/Composition/CompositionPaths.cs
fc0b6799667041a7c1607436dc1a557f1be72fae9c9a75666c998c26c12d6cb1  src/ProGPU.WinUI/Composition/CompositionPathData.cs
6c38aef99bad252dd90814d7640d67b10979c335cba44ea7769375781424d1ce  src/ProGPU.WinUI/Composition/CompositionGradients.cs
0cec68bea008432376f884aaff223eeb655086d2d7fd544e0859b958d144e372  src/ProGPU.WinUI/Composition/CompositionObject.cs
```


Actual-target renderer increment: `Renderer::prepare_visual`,
`prepare_visual_with_options` and `prepare_visual_snapshot` now lower frozen
pages in original template/ordinary child order, compose source transforms before
path/stroke/text preparation, carry per-draw ancestor opacity and preserve device
Clear. Local/parent clips keep their original order and exact endpoints. Local
culling never erases descendants. Original optional layer projection uses ceiling
logical dimensions; required layer projection retains the exact visual size.
Cache snapping, independent semantic DPI and explicit cache ClearType policy are
retained. Effect captures exclude root transform/state and retain independent
X/Y padding; final clips/opacity apply outside the capture.

Managed Gaussian/Box blur and sharp/packed-alpha shadow use unchanged original
kernels, exact 16/64-byte records, half-radius ties-to-even snapping and original
shadow-then-source ordering. ColorMatrix uses the original ImageEffect extension;
Blend uses the retained final destination compositor. Every target has fresh owned
textures/parameter resources. Immutable cache keys retain source/page generations,
presentation, complete capture frame and renderer execution options. Candidates
publish after the complete final scene succeeds; older prepared generations keep
their independent resources. Live nested cache/shader recipes conservatively
recapture the containing visual until their explicit revisions join that cache key.

Canonical rectangles/rounded contours, uniform rounded rings and rectangular-hole
rings retain original analytic admission/composition and physical inverse rows.
Fallback geometry/brush masks paint through preceding coverage before the completed
mask is quantized; private replacement metadata prevents multiplying that coverage
again. Full-target mask textures retain actual projection/DPI/extent identity and
reject source retargeting. These currently allocate RGBA alpha storage instead of
the original bounded R8 pool; the actual larger allocation is counted and bounded.
Source recordings, mask captures, preparation metrics and replay scenes are exposed
through `PreparedVisual` without executing UI callbacks during encode.

Explicit remaining admission gaps: perspective/late-camera visual traversal;
typed visual WpfShaderEffect and DropShadow opacity-mask-visual adapters; deferred
Boolean clip programs beyond existing `compile_raster_path` admission; source
hit/input/provider contracts. Frozen final-device shaders/glyphs cannot be retargeted
as source pages. Hinted outlines require their exact prepared DPI and identity basis
at the actual capture; no atlas policy or outline scaling is substituted. Original
platform/application/pixel and performance qualification remains deferred. No build,
test, compiler, GPU/browser validation or benchmark was run; formatting only.

Renderer source provenance:

```
a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd  src/ProGPU.Scene/Compositor.cs
557f747e6772917fdbf34c06b838f430734a71e86839e50a03de90c248c5e017  src/ProGPU.Scene/EffectCaptureFrame.cs
7e3cd2be4a1f02cfe44f9b784759cf5ae5677f089c5f7c5c2db7e2a4b28c8ea1  src/ProGPU.Compute/ComputeAccelerator.cs
7ffc9636c103bddbbf13e7ba5fc253d4e9a02c26478edc5e5a38ef9a81de8834  src/ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl
fc700b1b057b369588960ce485d8bf07a0cb9a6f06e8611f794c1d990f32080c  src/ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl
475756cd86b206afa45725603817aed8f01a1dd09e52d3e621a3759d878b6e80  src/ProGPU.Compute/Shaders/DropShadow.wgsl
759a107e0428fc95f3241c219ca52d12d82c7630a85909f1bbb133d9f8e9f6c2  src/ProGPU.Compute/Shaders/ShadowBlurHorizontal.wgsl
0bfb3975168f9a98c4e6cd3edd0b518dc9ca30664ce947b2e5d45eb1ce205b4c  src/ProGPU.Compute/Shaders/ShadowBlurVertical.wgsl
```
