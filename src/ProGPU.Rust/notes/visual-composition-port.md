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
mutable composition clip/shadow objects and disposal notifications; visual
shadow-mask source adapters; surface/mask/backdrop/effect brush scopes;
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
DropShadow opacity-mask-visual adapters; deferred
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


Typed Wpf visual adapter: `CompositionEffectValue::WpfShader(WpfVisualEffect)`
now participates in actual rendering. `WpfVisualEffect::new` preserves generic
borrowed parameter/sampler semantics and default output alpha ordering;
`from_owned_source` retains an independent immutable preparation source and its
four-edge descriptor. `with_source_visual_opacity` is explicit and immutable.
Original independent double padding/endpoints, signed zero, source host rebase,
physical source projection and legacy scalar/explicit-raster-override routes stay
separate. An owned source's rebase cannot be rebound independently of its owner.
Generic raster overrides use original finite max(0,value), without ceiling.

Opted-in root opacity and opacity masks paint into the implicit source; root
geometry/outer clips remain on final output. Root zero alpha can therefore still
execute a constant-output shader, while ordinary zero ancestor alpha excludes
the subtree. Cached source roots keep independent raw-cache sizing, source DPI,
text policy and original input-space placement. The historical generic cached-root
route remains distinct. Final shaders consume the complete original source texture
with independent output edges/UVs at the actual parent; evaluated effects are not
resampled to implement source alpha ordering.

Owned recipes run before implicit capture with exact projection/viewport/lattice,
semantic DPI and capture metadata. Their move-only parameter generation retains
actual completed sampler producers. Shared detached source guards cover mixed
picture/visual preparation cycles and retain the exact source identity through
cancellation. Device/renderer/target are checked after preparation and capture.
Visual and ordered-source frame transactions restore prior visual-cache generations
on outer failure/cancellation. Each successful target privately owns input pixels,
parameter owners, original snapshot/pages and final parameters. Earlier frames
retain those generations through cache replacement. Generic borrowed parameters
are not relabeled as owned producer proof.

`PreparedVisual` exposes actual shader command/cache/capture/recipe counters and
shader source captures/recordings. Provider/UI hit geometry, original platform
qualification, camera/perspective traversal, shadow-mask visuals and unsupported
deferred clip programs remain independent gaps. Existing source glyph DPI/basis,
full sampler/device admission, mask ownership and no-UI-during-encode gates remain.
Only touched-file formatting was run; no tests, compiler, fixtures, GPU/browser
validation or performance work was added or executed in this implementation batch.

Additional source provenance:

```
5ee016cdd04720947b76e5d11eea354978d4653c36bdf4d0fc0f54a63df1918b  src/ProGPU.Scene/OwnedShaderEffectSource.cs
da0c767b6ed25be653888435acb0751499da6282153b25bf9fded30cba77cc71  src/ProGPU.Scene/Compositor.OwnedShaderEffects.cs
cd1aea0b7ace472a46f68dee9126f3280ddade8a1eb88871412f3468167bd7dd  src/ProGPU.Scene/ShaderEffectSourceCapture.cs
e633f404baae8fd6e274029c795c01e05378708dbf8f093530fa57472c66f431  src/ProGPU.Scene/WpfShaderEffectParams.cs
64dd29ca99d205a3fe39e829373075ee22f9f2f18c9de6c7d7b446c21759ac02  docs/source-shader-opacity-order.md
9c74e66e75c5a576489574a5e8f9c3ad31398e570599cd0e9457d822123aa73b  docs/managed-source-effect-capture.md
```

### Surface, mask and independent shadow sources

Ported `CompositionSurfaceBrush` full texture leases, null sources, ten original
interpolation labels, stretch/alignment, finite ratio clamping, snapping and the
original ordered anchor/center/scale/rotation/matrix/offset products. Shared
surface generations invalidate counted weak brush bindings. Mask brushes retain
independent source/mask bindings, reject original unsupported nested input kinds,
and freeze their original Begin/End scopes. Sprite, fill, stroke and visual
opacity scopes consume the actual frozen sources. Full-texture producer ownership
is required; an unrelated/subset texture is not an ICompositionSurface proof.

`Mask::Source` records original paint and placement. GPU capture binds preceding
analytic/sample coverage before quantization, then retains each completed source
texture. `Mask::Geometry` resolves original binary rectangle/canonical analytic/
painted path selection at the actual target. Semantic DPI, source material frames,
non-scaling stroke geometry and source device preflight stay independent. Source
captures use whole-root RGBA storage, charged explicitly, rather than claiming the
original bounded R8 allocation/performance. Metrics report source captures/draws
separately from mask composition passes.

`DropShadowSource` retains an independent `ShadowOpacitySource` visual, picture or
frozen composition brush. Its alpha capture uses the owner's exact effect frame;
root transform/state are excluded for visual sources, while the owner's original
color capture remains DrawSource's input. WinUI Default/Inherit source policy and
null white rectangle are explicit; CompositionMaskBrush has no PrepareSceneBrush
result on this path and retains an empty paint result plus its original owners.
Actual offscreen depth is scoped with a detached RAII guard, carried through
layers/AA/source targets, and included in visual/cached-picture/owned-shader cache
identity. Canonical masks retain the original depth <= 1 gate. This increment
also connects the root agent's independent blend stacks and VertexMesh source
placement/semantic opacity; no mesh coordinate clamp is introduced.

Remaining: the original managed BackdropMaterial extension/host-backdrop provider
pipeline; a mutable standalone WinUI CompositionShadow class/property binding
adapter (the owned explicit effect/source route is implemented); external UI
surface invalidation dispatch/provider qualification; full original pixels and
performance. The repository has no original CompositionBackdropBrush or
CompositionEffectBrush class to relabel as implemented. Implementation only:
formatting was run, with no compiler/test/browser/benchmark validation or fixtures.

Original SHA-256 sources:

| Original path | SHA-256 |
|---|---|
| `ProGPU.WinUI/Composition/CompositionSurfaces.cs` | `6d9a91a1f7f99052db71d016acfc09db48c530a2617344bb20867ef13aca5744` |
| `ProGPU.WinUI/Composition/CompositionMasks.cs` | `81499b18948c58f7b4801a00fbd1a191c5a10d7c8f9049f7d4cbfd2a1f8702c0` |
| `ProGPU.WinUI/Composition/CompositionShapes.cs` | `a27704d1f649d530dce1cf3a5ff5e987908c38064ebf66e90df9739ffec9131b` |
| `ProGPU.WinUI/Composition/CompositionShadows.cs` | `703127198465f9418651321f09691246fab15586db57319698ff0b8481efc87e` |
| `ProGPU.Scene/Visual.cs` | `546cd62ba3f2c0d4a85f1479d8c4122c0032b46b090341d088a2749fa1c264fe` |
| `ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |

Arbitrary text-in-SourceMask captures now use a private exclusive text-owner
transaction (`renderer/text/source_capture.rs`). It preserves any outer atlas,
starts an independent source tree batch, captures only after actual residency and
batch completion, and restores the outer atlas/tree flag on success, failure or
cancellation. Ordinary outer glyph positions, font generations and atlas policies
are unchanged. Temporary GPU handles stay retained by the submitted commands;
source recording/producer owners use the existing submission-retention path. No
Drop path flushes, polls or destroys a live texture. Text-mask implementation is
present; original pixels, cancellation/lifetime qualification and performance
validation remain deferred with the rest of the port.
