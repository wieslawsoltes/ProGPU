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

Remaining original slices: automatic Compositor visual traversal/culling and
layer/effect/cache lowering; source hit-geometry/provider/layout callbacks;
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
