# WinUI Composition retained WebGPU research

This record is the clean-room design gate for the retained visual foundation,
geometry/shape, clip, gradient, drop-shadow, and opacity-mask vertical slices of
`Microsoft.UI.Composition` in ProGPU.
The authoritative API shape is the public ECMA-335/WinRT metadata and XML
documentation from the repository's SHA-512-pinned
`Microsoft.WindowsAppSDK.WinUI` `2.3.0` package.
No Microsoft method body, generated projection body, or foreign renderer
implementation was inspected, copied, translated, or adapted.

## Primary sources

- [Windows visual layer overview](https://learn.microsoft.com/en-us/windows/apps/develop/composition/visual-layer)
  defines the retained, hardware-accelerated visual layer and the roles of
  `Visual`, `ContainerVisual`, `SpriteVisual`, brushes, effects, and
  animations.
- [Composition visual tree overview](https://learn.microsoft.com/en-us/windows/apps/develop/composition/composition-visual-tree)
  defines stable visual identity, ordered children, inherited transforms, and
  content-bearing primitive visuals.
- [Composition brushes](https://learn.microsoft.com/en-us/windows/apps/develop/composition/composition-brushes)
  defines a `CompositionBrush` as `SpriteVisual` content, documents supported
  mask/source brush combinations, and specifies alpha-driven opacity masking.
- [CompositionMaskBrush](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionmaskbrush)
  defines the retained source and mask inputs and explicitly disallows using a
  mask brush as an effect-brush source.
- [CompositionGradientBrush](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositiongradientbrush),
  [CompositionLinearGradientBrush](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionlineargradientbrush),
  and [CompositionRadialGradientBrush](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionradialgradientbrush)
  define retained color stops, absolute/relative coordinates, clamp/wrap/
  mirror extension, RGB interpolation, brush transforms, and the documented
  radial defaults.
- [DropShadow](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.dropshadow),
  [DropShadow.Mask](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.dropshadow.mask),
  and [CompositionDropShadowSourcePolicy](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositiondropshadowsourcepolicy)
  define retained color, opacity, offset, blur, explicit-mask, rectangular
  sprite-default, and visual-content-alpha shadow state.
- [LayerVisual](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.layervisual)
  defines a retained layer whose shadow derives from the combined alpha of its
  children by default.
- [ElementCompositionPreview](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.hosting.elementcompositionpreview)
  exposes the backing XAML visual and a custom composition visual.
- [SetElementChildVisual](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.hosting.elementcompositionpreview.setelementchildvisual)
  requires the custom visual to remain the last child and therefore above the
  element's ordinary visual content.
- [VisualCollection.InsertAtTop](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.visualcollection.insertattop)
  documents bottom-to-top collection order and bottom-to-top enumeration.
- [Visual.RelativeSizeAdjustment](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.visual.relativesizeadjustment)
  gives the exact effective-size equation: local size plus the component-wise
  relative adjustment multiplied by parent effective size.
- [CompositionSpriteShape](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionspriteshape)
  defines a retained geometry with independently mutable fill, stroke, dash,
  cap, join, thickness, and non-scaling-stroke state.
- [CompositionGeometry.TrimStart](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositiongeometry.trimstart)
  and the corresponding trim-end/offset contracts define normalized retained
  geometry trimming without changing geometry identity.
- [CompositionViewBox](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionviewbox)
  and [CompositionViewBox.Stretch](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionviewbox.stretch)
  define source bounds, stretch policy, and alignment for a `ShapeVisual`.
- [Windows.Graphics](https://learn.microsoft.com/en-us/uwp/api/windows.graphics)
  defines `IGeometrySource2D` as the marker that allows a typed geometry
  provider to become Composition path data.
- [CompositionPath](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionpath)
  defines an immutable path-data wrapper over `IGeometrySource2D`, while
  [CompositionPathGeometry.Path](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionpathgeometry.path)
  supplies those connected lines and curves to a mutable retained geometry.
- [CompositionRoundedRectangleGeometry](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionroundedrectanglegeometry)
  defines retained size, offset, corner radius, and inherited trim state for a
  rounded rectangle.
- [Visual.Clip](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.visual.clip)
  defines a retained clip in the visual's coordinate space.
  [CompositionClip.AnchorPoint](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionclip.anchorpoint)
  defines the normalized clip-size anchor, while the remaining clip transform
  properties define an independently animatable local transform.
- [InsetClip](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.insetclip),
  [RectangleClip](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.rectangleclip),
  and [CompositionGeometricClip](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositiongeometricclip)
  define edge-relative rectangles, independent per-corner radii, and reusable
  geometry/view-box clips respectively. Negative edge insets are valid.
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  separates reusable CPU descriptions from device-owned GPU resources.
- [Direct2D brushes](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-brushes-overview)
  define linear/radial gradient stops, brush-space transforms, color
  interpolation, and clamp/wrap/mirror extension as retained GPU draw state.
- [Direct2D performance guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance)
  recommends batching, resource reuse, atlases, and avoiding unnecessary
  flushes or interop transitions.
- [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  treats intermediate layers as bounded compositing resources rather than a
  default for every visual.
- [Direct2D opacity masks](https://learn.microsoft.com/en-us/windows/win32/direct2d/opacity-masks-overview)
  defines mask alpha as the visibility multiplier for source pixels and
  distinguishes rectangular, geometry, and grouped-content mask scopes.
- [Direct2D axis-aligned clips](https://learn.microsoft.com/en-us/windows/win32/direct2d/d1111-using-layer-when-clip-is-sufficient)
  recommends a clip rectangle instead of allocating a layer when its simpler
  semantics are sufficient.
- [Direct2D path geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/path-geometries-overview)
  separate reusable geometry descriptions from the draw operation that
  supplies fill and stroke state.
- [Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview)
  describe caching reusable fill/stroke geometry work when repeated draws
  justify the retained GPU resource.
- [Win2D device-loss handling](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm)
  recreates the device and all resources owned by it as one recovery event.
- [Win2D `CanvasLinearGradientBrush`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Brushes_CanvasLinearGradientBrush.htm)
  exposes retained stops, endpoints, transform, edge behavior, color spaces,
  and device ownership over Direct2D.
- [Direct2D Gaussian blur](https://learn.microsoft.com/en-us/windows/win32/direct2d/gaussian-blur)
  documents a GPU effect with bounded blur growth and quality/performance
  tradeoffs, while [Win2D `ShadowEffect`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Effects_ShadowEffect.htm)
  exposes retained blur and color inputs over that effect model.
- [Skia API overview](https://skia.org/docs/user/api/) and
  [`SkPicture`](https://api.skia.org/classSkPicture.html) preserve a reusable
  ordered command stream separately from its GPU replay.
- [`SkGradientShader`](https://api.skia.org/classSkGradientShader.html)
  exposes linear/radial gradient points, ordered stops, tile modes, color-space
  interpolation, and a local matrix as retained shader inputs.
- [`SkImageFilters::DropShadow`](https://api.skia.org/classSkImageFilters.html)
  models offset, blur sigma, color, and shadow-only/source-composite variants
  as an image-filter operation rather than CPU raster output.
- [`SkCanvas`](https://skia.googlesource.com/skia/+/refs/heads/main/include/core/SkCanvas.h)
  exposes transformed rectangle, rounded-rectangle, and path clip primitives
  whose result intersects the current clip stack, together with bounded
  save-layer scopes for compositing source content through an alpha mask.
- [SkParagraph sources](https://skia.googlesource.com/skia/+/main/modules/skparagraph/)
  retain shaping and line-layout results on the CPU side of the renderer.
- [Firefox rendering overview](https://searchfox.org/firefox-main/source/gfx/docs/RenderingOverview.rst)
  documents WebRender's display-list to retained-scene, culling, resource
  preparation, batching, render-task, and GPU-command stages.
- [WebRender gradient shaders](https://searchfox.org/firefox-main/source/gfx/wr/webrender/res/)
  keep linear/radial gradient evaluation and stop sampling in batched GPU
  paint stages rather than materializing one image per gradient.
- [WebRender render tasks](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/render_task.rs)
  represent blur and picture intermediates as bounded GPU render-graph work
  whose resources are reused through retained scene state.
- [Vello](https://github.com/linebender/vello) retains an encoded scene and
  performs compute-oriented GPU rasterization into a caller-provided texture.
- [Peniko](https://github.com/linebender/peniko), Vello's paint vocabulary,
  retains linear/radial gradient geometry, stops, and extend policy as brush
  data independent from scene encoding and GPU replay.
- [Parley](https://github.com/linebender/parley) keeps text shaping and layout
  reusable and separate from scene rasterization.
- [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  caches Unicode/OpenType shaping decisions rather than moving shaping into a
  compositor shader.
- [WebGPU specification](https://gpuweb.github.io/gpuweb/) defines device-owned
  pipelines, render passes, buffer/texture usages, submission, and device
  loss. A lost device invalidates its buffers, textures, and pipelines and
  requires their recreation.

## Cross-engine comparison and decisions

| Concern | Production/research finding | ProGPU decision |
| --- | --- | --- |
| Startup and lazy initialization | WinUI composition creates lightweight retained objects; Direct2D and WebGPU defer device resources to their owning device. | Constructing a public `Compositor`, visual, brush, or property set creates no `WgpuContext`, shader, pipeline, texture, or buffer. A host initializes WebGPU only when it renders. |
| Retained scene reuse | WinUI/DirectComposition retain visual identity; WebRender, Vello, and `SkPicture` retain scene or command identity. | Each public composition `Visual` owns one stable `ProGPU.Scene.ContainerVisual`. A color sprite records one immutable-until-invalidated rectangle command and uses the existing compiled-scene and incremental-page caches. No second renderer or display list is introduced. |
| Geometry and shape reuse | WinUI separates mutable shape, geometry, brush, path-data, and view-box identity; Direct2D separates reusable geometry descriptions from draw state and optional realizations. | A `ShapeVisual` flattens its ordered shape hierarchy into one retained ProGPU command cache. Full ellipses, rectangles, and rounded rectangles remain analytic commands. A `CompositionPath` snapshots typed ProGPU lines, quadratics, cubics, and arcs once; path and rounded-rectangle trims retain exact sub-segments selected from bounded length tables. Geometry-cache identity is retained with every source/trimmed path. No per-shape surface, pass, texture, or bitmap is created. |
| Gradient representation | WinUI, Direct2D/Win2D, Skia, WebRender, and Vello retain gradient geometry, ordered stops, extend mode, interpolation, and local transforms as paint data evaluated by the renderer. | Each gradient owner caches one typed ProGPU vector brush. Observable collection order remains unchanged while a stable retained snapshot is sorted only after stop mutation. The existing WebGPU gradient-stop storage buffer and vector shader perform interpolation; no gradient creates a texture, render pass, CPU bitmap, or readback. |
| Shadow representation | WinUI retains one shareable shadow object and chooses between a sprite rectangle, inherited visual alpha, or explicit brush alpha. Direct2D/Win2D, Skia, and WebRender execute blur/tint/composite work on GPU intermediates. | Each shadow owner retains one scene `DropShadowEffect`. The source subtree is rendered once into the existing bounded effect texture; an explicit or rectangular mask uses one additional retained texture, while inherited content reuses source alpha. The existing WebGPU compute path performs a sharp pass or separable blur and the original source is composited once above it. |
| Opacity-mask representation | WinUI composes two retained brushes; Direct2D multiplies source visibility by mask alpha; Skia brackets compositing in a saved layer; WebRender retains clip/mask render tasks; Vello encodes push/pop layer scopes. | `CompositionMaskBrush` retains typed source and mask objects and records one push/source-draw/pop scope. Solid masks use ProGPU's analytic WebGPU mask uniforms; gradients use the existing bounded GPU mask texture and gradient shader. Shapes bracket fill and stroke independently only when a scoped brush is present, preserving the ordinary one-record fast path. |
| Clip representation | WinUI and Skia retain transformed rectangle, rounded-rectangle, and path clips; Direct2D recommends an axis-aligned clip instead of a layer; WebGPU exposes a render-pass scissor while Vello/WebRender retain geometry masks and render tasks for general paths. | Each composition visual stores at most one typed local `VisualCompositeClip`. Axis-aligned rectangles intersect the existing WebGPU scissor stack. Rotated rectangles and canonical per-corner rounded rectangles use the existing analytic GPU mask. General paths use one bounds-sized R8 GPU mask. No clip creates a CPU bitmap, readback, visual-sized intermediate surface, or per-frame geometry. |
| Visibility and culling | WebRender culls the retained scene before expensive work; WinUI visual visibility and opacity are composited properties. | `IsVisible`, opacity, effective size, and the full local matrix update the existing scene node. Existing ProGPU clip culling and zero-opacity subtree rejection remain authoritative. |
| Cache keys and eviction | WebRender uses bounded generation-safe caches; Direct2D/Win2D bind GPU resources to a device. | The slice adds no device resource cache. It reuses ProGPU's target/DPI/atlas-generation-sensitive compiled-scene keys, bounded incremental pages, bounded atlases, and context identity checks. |
| Demand-driven upload | Direct2D recommends resource reuse; WebRender uploads demanded resources after visibility analysis. | A solid sprite contributes one ordinary retained rectangle. Existing dirty-range scene uploads write only changed GPU buffer ranges. Color changes invalidate owners but do not allocate or upload an intermediate CPU bitmap. |
| Worker preparation | SkParagraph, DirectWrite, Parley, and HarfBuzz retain CPU shaping/layout results. | Composition does not add Unicode or OpenType work. Existing positioned glyph results remain reusable CPU data; the new visual layer only composes their retained scene nodes. |
| GPU batching and compute organization | Direct2D batches compatible draws; WebRender and Vello encode/batch GPU work; WebGPU pipelines are complete reusable device objects. | Sprite rectangles flow into the existing WebGPU vector batch and pipeline cache. There is no per-sprite render pass, surface, readback, flush, shader source, or pipeline creation. Future effects must use bounded existing/new WebGPU passes with embedded shader resources. |
| DPI, subpixel, and hinting | DirectWrite/Skia preserve positioned glyph metrics; physical targets remain distinct from logical coordinates. | Composition properties remain logical floats and are transformed by the existing physical-framebuffer compositor. This slice adds no whole-pixel snapping and does not alter the quarter-physical-pixel text policy. |
| Fallback and variable fonts | Font fallback and variation selection happen before rendering in DirectWrite, SkParagraph, Parley, and HarfBuzz. | Unchanged. Composition reuses the selected face, glyph IDs, variation state, and retained text commands. |
| Device loss and atlas generation | Win2D recreates every device-owned resource; WebGPU device loss invalidates all child resources. | Public composition objects stay CPU-side. The existing host replaces `WgpuContext` and its caches after loss; the retained composition tree then recompiles against the new context. Atlas movement remains protected by generation-sensitive scene keys. |

## Adopted, adapted, and rejected

Adopted:

- exact pinned public type/member/enum/attribute identities for the selected
  Composition and XAML-hosting slice;
- stable compositor ownership and same-compositor tree validation;
- bottom-to-top visual ordering and enumeration;
- the documented relative-size equation and parent-size invalidation;
- shared mutable color brushes with change propagation to every live owner;
- a typed property set whose result distinguishes success, type mismatch, and
  missing values.
- retained shape/container collections, same-compositor reparenting and cycle
  rejection, independent shape transforms, geometry trim state, sprite fill
  and stroke state, and view-box stretch/alignment.
- `IGeometrySource2D`, immutable `CompositionPath` snapshots,
  `CompositionPathGeometry`, analytic `CompositionRoundedRectangleGeometry`,
  and the exact associated compositor factories and properties.
- `CompositionClip`, `InsetClip`, `RectangleClip`,
  `CompositionGeometricClip`, `Visual.Clip`, all pinned factories and
  transform properties, including shared weak-owner invalidation and
  geometry/view-box reuse.
- `CompositionGradientBrush`, linear/radial derived brushes, color stops and
  their observable list contract, exact enums/factories/defaults, relative and
  absolute mapping, clamp/wrap/mirror extension, RGB/RGB-linear interpolation,
  and independently mutable brush transforms.
- `CompositionShadow`, `DropShadow`, `CompositionDropShadowSourcePolicy`,
  `SpriteVisual.Shadow`, `LayerVisual.Shadow`, and their factories, including
  exact defaults, shared ownership, rectangular/inherited/explicit mask
  selection, retained WebGPU blur, and original-source compositing.
- `CompositionMaskBrush`, its source/mask ownership and factory, documented
  brush-combination validation, alpha multiplication, nested invalidation,
  and retained sprite/shape mask scopes.

Adapted:

- WinUI's retained compositor session maps to ProGPU's existing retained
  `Scene.Compositor` at render time rather than introducing a platform process
  or a second graphics device;
- `ElementCompositionPreview` stores one weakly keyed bridge per `UIElement`
  and marks the custom scene node as a retained topmost child, so later
  ordinary child mutations stay below it without reordering churn;
- all general 2D/3D visual values compose into one `Matrix4x4`, which the
  current WebGPU scene compiler already transports without reflection;
- relative parent sizing is delivered by a typed `IParentSizeDependentVisual`
  notification only when the parent's retained size changes.
- nested shape transforms and the view-box mapping are composed into the
  existing retained command transforms, while mutable brushes and geometries
  invalidate every live typed owner.
- clip transforms compose in visual-local space. A null geometric-clip
  geometry is treated as a no-op; this is an explicit clean-room inference
  from the documented nullable default and the requirement that a default
  clip object be safe before geometry is assigned.
- a default `SpriteVisual` shadow uses a retained opaque rectangle mask while
  a default `LayerVisual` shadow reuses its combined child alpha; an explicit
  brush mask takes precedence for both. This maps the documented public
  policy onto one provider-neutral scene effect without exposing WinUI types
  to the WebGPU backend.
- WinUI mask inputs map to ProGPU's typed scene brushes. A missing source or
  mask produces no pixels; this is a clean-room inference from the nullable
  defaults and the requirement that a newly created mask brush be safe before
  both inputs are assigned. Available color and gradient inputs are accepted
  according to the documented combination table; unsupported nested mask
  inputs fail transactionally.

Rejected:

- metadata-only public stubs for clips, shadows, effects, surfaces, or
  animations whose pixels or timing are not implemented yet;
- one native surface, render pass, texture, or CPU bitmap per sprite;
- sampled polyline approximations for ellipse and rectangle trims when an
  exact retained arc or bounded corner path is available;
- flattening trimmed quadratic, cubic, or elliptical-arc output into a sampled
  polyline: bounded samples estimate distance only, while emitted geometry
  remains an exact De Casteljau or analytic arc sub-segment;
- polling parent sizes each frame, runtime reflection, boxed drawing-context
  adapters, and unconditional root invalidation;
- copying Microsoft projection code or any Skia, WebRender, Vello, Parley, or
  HarfBuzz implementation structure.

## Complexity and ownership

- A composition scalar/vector/matrix property mutation is fixed `O(1)` and
  allocation-free after construction. It invalidates the affected retained
  node only when the value changes.
- A warmed update of an existing property-set key is expected `O(1)` with no
  boxing because values use a tagged inline struct. The dictionary retains
  `O(P)` entries for `P` distinct keys.
- Child insertion/removal is `O(C)` worst-case for `C` siblings because order
  is observable and stored in contiguous retained lists; traversal is `O(C)`
  and bottom-to-top.
- A color-brush update is `O(S)` for `S` live sprite owners, prunes dead weak
  owners, and performs no per-pixel CPU work.
- A parent size change performs `O(C)` typed child checks and recomputes each
  participating child's fixed-work effective size/matrix. Unchanged parent
  sizes perform no traversal.
- A color sprite owns one reusable drawing context, one command slot, and one
  shared scene brush. Stable frames reuse the compiled scene and issue no
  composition-specific managed allocation or CPU-to-GPU bitmap upload.
- A gradient with `G` stops rebuilds a stable retained stop snapshot in
  `O(G log G)` time and `O(G)` retained scratch only after collection, offset,
  or color mutation. Scalar/vector/transform changes are fixed `O(1)` apart
  from typed owner notification. A warmed owner reuses its vector brush,
  command capacity, stop arrays, and WebGPU storage path with no managed
  allocation. Stable frames reuse the compiled scene and perform fixed
  per-fragment linear/radial evaluation plus bounded stop lookup.
- Recording a changed shape hierarchy is `O(S + C + D)` for `S` shapes, `C`
  retained path segments, and `D` changed dash values. Stable replay reuses
  the compiled scene. Full ellipses/rectangles and trimmed ellipses/lines use
  fixed work; a trimmed rectangle emits at most five line segments.
- Shape and brush property changes are fixed `O(1)` apart from notifying their
  live owners. Dash snapshots allocate `O(D)` only after dash-list mutation;
  warmed transform and color mutations reuse retained command-list capacity
  and allocate no managed memory.
- Creating a `CompositionPath` snapshots `S` retained segments and builds at
  most `K=128` cumulative-length samples per curved segment: `O(S*K)` bounded
  time/storage once. A trim rebuild is `O(S log K)` and emits exact line,
  quadratic, cubic, or arc sub-segments. Stable replay retains both path and
  `RenderCommandGeometryCache`; path replacement, shape transforms, and
  rounded-rectangle property updates are allocation-free after warmup.
- An inset or square rectangle clip update is fixed `O(1)` per live visual
  owner and allocation-free after owner registration. Stable rectangle clips
  use the WebGPU scissor with `O(1)` CPU/GPU state. A rounded rectangle owns
  one retained path of at most eight segments and uses fixed-work radius
  normalization. General geometric-clip preparation is `O(S)` only when its
  retained path changes; stable frames reuse the path and compiled mask state.
  A general clip mask stores `O(B)` bytes for its bounded device-pixel area
  `B`, while scissor clips allocate no texture storage.
- A shadow property change is fixed `O(1)` apart from notifying `S` weak live
  owners. For `P` padded device pixels and snapped radius `R`, a sharp shadow
  is `O(P)` work and one destination texture; a blurred shadow is `O(P*R)`
  work across two separable WebGPU compute passes with one quarter-width
  packed temporary and one destination. Explicit/default rectangle masks add
  one retained `O(P)` RGBA texture; inherited content alpha adds none. Stable
  owners reuse all textures, pipelines, bind-group cache entries, mask command
  capacity, and effect identity.
- A mask-brush input replacement is fixed `O(1)` apart from notifying `S` weak
  live owners. Recording adds exactly two retained scope commands around the
  ordinary source draw. A solid mask uses fixed `O(1)` analytic state and no
  mask texture. A non-solid mask rasterizes `P` bounded device pixels on the
  GPU with `O(P)` storage/work, then multiplies source fragments by its alpha.
  Stable brushes reuse scene-brush identity, command capacity, mask resources,
  and pipelines; warmed color changes allocate no managed memory.

## Current validation and explicit boundary

Focused Release tests cover every property-set value kind and result status,
same-compositor ownership, bottom-to-top ordering, reparenting, cycle rejection,
zero managed allocations across 10,000 warmed property/visual updates,
WebGPU pixel output and color invalidation through `ElementCompositionPreview`,
persistent top-child ordering after a later ordinary insertion, compiled-scene
reuse, removal, and relative-size propagation after a XAML host resize.

The shape slice additionally covers collection ownership, same-compositor
reparenting, transactional cycle rejection, analytic ellipse/rectangle WebGPU
pixels, nested transforms, shared brush invalidation, compiled-scene reuse,
view-box stretch/alignment, trimmed-line output, and exactly zero managed
allocations across 10,000 warmed shape-transform/color updates.

The path/rounded-rectangle slice additionally covers exact pinned factories,
defaults and marker interfaces; immutable source snapshots; explicit failure
for an unregistered external marker implementation; full analytic rounded
rectangle pixels; trimmed rounded-corner arcs and trim-offset invalidation;
line, quadratic, cubic, and elliptical-arc path trimming; compiled-scene
reuse; and exactly zero managed allocations across 10,000 warmed mixed path,
rounded-rectangle, trimmed line, trimmed ellipse, and trimmed rectangle
updates. Distance sampling is bounded and never replaces the exact emitted
curve. The current built-in typed provider is `ProGPU.Vector.PathGeometry`;
future external geometry providers require an explicit reviewed typed adapter
instead of reflection or native-handle probing. ProGPU's internal
boolean-combined path representation is rejected explicitly until a typed
adapter defines its Composition trim semantics; it never degrades to empty or
flattened output.

The clip slice additionally covers exact pinned defaults and factories,
same-compositor validation, clip disposal, inset scissor pixels, canonical
per-corner rounded-mask pixels, geometric ellipse/view-box pixels, geometry
and radius invalidation, compiled-scene reuse, and exactly zero managed
allocations across 10,000 warmed inset/transform mutations. Clip hit testing
uses the same retained clip stack and transforms as color rendering. The
rounded and general paths remain device-independent retained geometry until
the existing WebGPU compositor selects its analytic or bounded R8 mask.

The gradient slice additionally covers exact pinned enums, inheritance,
collection/indexer, properties, defaults, and factories; same-compositor and
disposed-stop ownership; stable offset ordering without changing observable
list order; relative and absolute coordinates; brush transforms; clamp/wrap/
mirror state; RGB and linear-RGB interpolation; linear/radial sprite pixels;
shape-fill pixels; shared-stop invalidation; compiled-scene reuse; and exactly
zero managed allocations across 10,000 warmed stop-color/endpoint updates.
It reuses the existing WebGPU gradient buffer and vector shader on desktop,
mobile, and browser backends and adds no platform-specific rendering fork.

The shadow slice additionally covers exact pinned inheritance, enum values,
properties, defaults, factories, `SpriteVisual` and `LayerVisual` ownership,
same-compositor mask validation, disposal, rectangular sprite defaults,
inherited layer/content alpha, explicit brush-mask precedence, sharp and soft
WebGPU pixels, mask/child-position invalidation, and exactly zero managed
allocations across 10,000 warmed offset/color updates. The implementation
reuses ProGPU's existing embedded, complexity-documented shadow compute
shaders and bounded effect-texture cache; it adds no shader literal, CPU blur,
readback, platform renderer fork, reflection, or external dependency.

The opacity-mask slice additionally covers the exact sealed type, nullable
source/mask properties, factory, same-compositor ownership, supported input
categories, transactional rejection of nested mask brushes, retained
push/draw/pop command structure, alpha-gradient sprite pixels, independent
shape fill/stroke scopes, nested source/mask invalidation, and exactly zero
managed allocations across 10,000 warmed source/mask color updates. Solid
masks use the existing analytic WebGPU mask path; gradients use the existing
bounded GPU mask texture and shader path. No CPU bitmap, readback, reflection,
shader literal, platform renderer fork, or external dependency is introduced.

This is not full Composition parity. General composition effects,
surfaces/external textures, animations, interaction
tracking, projected shadows, and lighting remain missing until each has a real
typed WebGPU implementation and its own correctness/performance gate.
