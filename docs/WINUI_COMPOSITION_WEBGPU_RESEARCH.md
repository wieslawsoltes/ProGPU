# WinUI Composition retained WebGPU research

This record is the clean-room design gate for the first
`Microsoft.UI.Composition` vertical slice in ProGPU. The authoritative API
shape is the public ECMA-335/WinRT metadata and XML documentation from the
repository's SHA-512-pinned `Microsoft.WindowsAppSDK.WinUI` `2.3.0` package.
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
  defines a `CompositionBrush` as `SpriteVisual` content.
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
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  separates reusable CPU descriptions from device-owned GPU resources.
- [Direct2D performance guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance)
  recommends batching, resource reuse, atlases, and avoiding unnecessary
  flushes or interop transitions.
- [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  treats intermediate layers as bounded compositing resources rather than a
  default for every visual.
- [Win2D device-loss handling](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm)
  recreates the device and all resources owned by it as one recovery event.
- [Skia API overview](https://skia.org/docs/user/api/) and
  [`SkPicture`](https://api.skia.org/classSkPicture.html) preserve a reusable
  ordered command stream separately from its GPU replay.
- [SkParagraph sources](https://skia.googlesource.com/skia/+/main/modules/skparagraph/)
  retain shaping and line-layout results on the CPU side of the renderer.
- [Firefox rendering overview](https://searchfox.org/firefox-main/source/gfx/docs/RenderingOverview.rst)
  documents WebRender's display-list to retained-scene, culling, resource
  preparation, batching, render-task, and GPU-command stages.
- [Vello](https://github.com/linebender/vello) retains an encoded scene and
  performs compute-oriented GPU rasterization into a caller-provided texture.
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

Rejected:

- metadata-only public stubs for clips, shadows, effects, surfaces, shapes, or
  animations whose pixels or timing are not implemented yet;
- one native surface, render pass, texture, or CPU bitmap per sprite;
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

## Current validation and explicit boundary

Focused Release tests cover every property-set value kind and result status,
same-compositor ownership, bottom-to-top ordering, reparenting, cycle rejection,
zero managed allocations across 10,000 warmed property/visual updates,
WebGPU pixel output and color invalidation through `ElementCompositionPreview`,
persistent top-child ordering after a later ordinary insertion, compiled-scene
reuse, removal, and relative-size propagation after a XAML host resize.

This is a foundation slice, not full Composition parity. Clips, shadows,
effects, surfaces/external textures, shapes/geometries, animations, interaction
tracking, projected shadows, and lighting remain missing until each has a real
typed WebGPU implementation and its own correctness/performance gate.
