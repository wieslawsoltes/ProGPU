# Avalonia integration rendering and text research

This record is the design gate for moving the Avalonia ProGPU and Silk.NET
backends into the ProGPU repository. The implementation moved from the existing
ProGPU/Avalonia integration; no implementation source was taken from the other
engines listed below. Those engines were reviewed for public contracts,
architecture, quality constraints, and validation ideas.

## Primary sources

- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/)
  separates Unicode text, style blocks, shaping, line layout, and the shaped
  result consumed by a renderer.
- [Skia text overview](https://docs.skia.org/docs/user/tips/)
  and [Skia source](https://github.com/google/skia) provide the reference
  behavior for the SkiaSharp compatibility boundary.
- [Direct2D and DirectWrite text rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  separates reusable text layout and glyph runs from the renderer which
  consumes them.
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  distinguishes reusable CPU geometry from device-dependent GPU resources and
  requires recreation after device loss.
- [Win2D device-loss handling](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm)
  recreates the device and every device-owned resource as one recovery event.
- [WebRender](https://github.com/servo/webrender) uses a retained display-list
  boundary, visibility-aware scene processing, batching, and explicit texture
  and glyph caches.
- [Vello](https://github.com/linebender/vello) keeps an encoded scene and uses
  compute-oriented GPU rasterization; its documented prefix-sum approach
  informed the decision to retain ProGPU scene commands and GPU path work.
- [Vello glyph-rendering plan](https://github.com/linebender/vello/issues/204)
  describes the quality/performance tradeoff between transformed vector glyphs
  and an atlas, including hinting behavior during dynamic transforms.
- [Parley](https://github.com/linebender/parley) keeps text shaping and layout
  as reusable CPU-side results which a renderer consumes.
- [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  and [cluster semantics](https://harfbuzz.github.io/clusters.html) define the
  script, language, direction, feature, glyph-position, and cluster contracts
  which the Avalonia 11 text adapter must preserve.

## Comparison and decisions

| Concern | Cross-engine finding | ProGPU/Avalonia decision |
| --- | --- | --- |
| Startup and lazy initialization | GPU renderers separate inexpensive registration from device-owned initialization. | `UseProGpu` and `UseSilkNet` register typed services. A window creates its `WgpuContext` when Silk initializes that window; offscreen rendering creates a context only on first use. |
| Shaping and layout reuse | Skia/SkParagraph, DirectWrite, Parley, and HarfBuzz produce positioned glyph/cluster results before rasterization. | Avalonia 12 continues to use Avalonia's shaping contract. The v11-only shared source adapter maps HarfBuzz output once into `ShapedBuffer`; compositor drawing consumes glyph indices and positions instead of reshaping. |
| Retained scene reuse | WebRender and Vello retain an encoded display list/scene and perform GPU-oriented batching. | Avalonia drawing operations record typed ProGPU scene commands. `SurfaceRenderTarget` preserves recorded commands between compatible uses; no readback is used for a live Silk/WebGPU surface. |
| Visibility and upload | Production renderers cull before expensive raster/upload work and upload demanded resources. | Avalonia owns visual invalidation and culling. ProGPU receives only recorded draw work; bitmap glyph textures are created on demand and cached per live GPU context. |
| Cache identity and eviction | Texture/glyph caches must include device identity, font/glyph/style/scale state, and bounded eviction or generation invalidation. | Typeface caches include the Avalonia request and font location. Bitmap glyph cache entries are context-owned; offscreen textures are keyed by context, dimensions, and format. Context disposal invalidates device resources. Existing bounded `TwoLevelCache` behavior is covered by focused tests. |
| Worker preparation | Text shaping and retained scene preparation are CPU work; GPU upload and presentation remain device work. | The integration does not move Unicode/OpenType shaping to a shader. Avalonia scheduling prepares commands; ProGPU records and submits on the render path. No reflection bridge is used in rendering hot paths. |
| GPU organization | Vello/WebRender batch encoded commands and parallel raster work rather than reading a surface back through the CPU. | Surface presentation passes the `WGPU_SURFACE` handle and uses a GPU texture blit. Offscreen readback exists only for APIs that explicitly request CPU pixels. |
| DPI, subpixel, and hinting | DirectWrite supports fractional glyph origins; Vello notes that hinting can shimmer under animated transforms; physical target size and logical layout must remain distinct. | Silk reports physical `FramebufferSize`. Avalonia coordinates remain logical and the renderer carries DPI into target creation and text sizing. Final placement is not rounded to whole logical pixels. |
| Fallback and variable fonts | HarfBuzz shapes a selected face and preserves clusters; font fallback and variation selection belong before rendering. | Avalonia font fallback remains authoritative. The v11 adapter preserves clusters, direction, language, and feature ranges. Unsupported variation behavior is not guessed in the renderer. |
| Device loss and target replacement | Direct2D/Win2D invalidate all resources owned by a lost device. | Caches check `WgpuContext` identity and disposed state. Window disposal releases the window context and dependent framebuffer resources. Recovery must create a new context rather than reuse textures from the disposed one. |

## Adopted, adapted, and rejected

Adopted:

- reusable shaped glyph results rather than GPU-side Unicode shaping;
- retained scene commands and GPU-side presentation without live-surface
  readback;
- explicit context ownership for textures and caches;
- physical framebuffer dimensions with logical Avalonia layout coordinates;
- lazy, demand-driven context and glyph resource creation.

Adapted:

- WebRender/Vello retained-scene ideas are expressed through ProGPU's typed
  command recorder and Avalonia's existing invalidation contract;
- DirectWrite/Parley shaping separation is expressed through Avalonia
  `IGlyphTypeface`, `GlyphRun`, and the v11 `ITextShaperImpl` seam;
- device-loss rules are enforced through `WgpuContext` identity/disposal rather
  than a DirectX-style device event.

Rejected:

- runtime reflection to bridge Avalonia private rendering types;
- reshaping text during compositor submission;
- unconditional per-frame scene rebuilding or bitmap upload;
- CPU readback followed by presentation;
- whole-pixel logical text snapping, which would lose physical subpixel
  placement at non-integer DPI;
- copying cache, shaping, or rasterizer implementations from another engine.

## Validation record

Validation is performed against the final source state:

- Avalonia 12 renderer and Silk.NET projects build on .NET 10.
- Avalonia 11 renderer and Silk.NET projects build from the same shared sources
  with `AVALONIA11` conditionals.
- 56 focused renderer unit/contract tests pass.
- 34 focused Silk.NET dispatcher, input, cursor, icon, timer, and framebuffer tests
  pass.
- 2 Avalonia Skia compatibility/source-integrity tests pass. The 54 upstream
  `.cs` files match Avalonia `12.0.5` commit
  `fee9c561ce036e8a3e8cee2397c75ca599b4790d` byte-for-byte.
- 274 expected render baselines and 10 input images decode successfully.
- Four NuGet package/version entries and their symbol packages build; archive
  inspection confirms exact Avalonia and ProGPU dependencies.
- An isolated package-only application restores and builds with no project
  references for both Avalonia 12.0.5 and Avalonia 11.3.18.
- The full `ProGPU.slnx` build succeeds, including both shared-source Avalonia
  lanes, the unchanged Skia backend, ControlCatalog, RenderDemo, and tests.
- The existing ProGPU runtime suite passes 2,367 tests after the SkiaSharp
  surface-presentation change.
- Native startup smoke checks kept ControlCatalog alive for more than five
  seconds with both direct ProGPU rendering and `--skiashim`; RenderDemo and
  ProGpuSandbox also remained active until intentionally interrupted.

The smoke checks establish startup and continuous frame-loop viability. They do
not claim a comparative FPS or frame-time improvement; this migration preserves
the existing rendering algorithms rather than introducing a performance
optimization.

## Atlas-copy and rounded-border follow-up

The ControlCatalog validation follow-up used these additional primary contracts:

- [WebGPU image-copy validation](https://www.w3.org/TR/webgpu/#abstract-opdef-validating-texture-copy-range)
  requires the copy origin plus extent to remain within the selected texture
  subresource. ProGPU now rejects an atlas allocation that cannot fit before it
  becomes pending GPU work, and `GpuCoverageUpload` independently validates the
  source and destination ranges before recording the command.
- [Skia `SkPath`](https://api.skia.org/classSkPath.html) documents that open and
  closed contours have the same fill result while retaining different stroke
  behavior. The [SVG 2 fill contract](https://www.w3.org/TR/SVG2/painting.html)
  likewise treats an open filled subpath as implicitly closed.
- [Direct2D path geometry](https://learn.microsoft.com/en-us/windows/win32/direct2d/path-geometries-overview)
  keeps typed figures, segments, fill mode, and explicit open/closed state.

The adopted behavior is to preserve the original rounded-rectangle contours for
a contained difference, express the hole with even-odd fill, and send that
recognized topology through the bounded direct triangle path. This avoids both
an unnecessary boolean-path readback and a page-width atlas allocation.
Arbitrary intersecting path operations still use the general solver. The
ControlCatalog transition mark was separately compared with Avalonia `main`
at `fb09d611062b199f70551e9c26b2316f0173ff09` using the native Skia backend.
Both backends render the same three-contour transition-masked silhouette; the
two open contours are implicitly closed for fill and the original path data
remains unchanged.

Focused validation covers a single raster wider than the configured atlas,
source/destination copy invariants, implicit open-figure fill closure, exact
contained rounded-rectangle differences, the original transition-masked
multi-contour path, and a partial rounded border with a zero-width bottom edge.
The native ControlCatalog then remained active on the Window Customizations
page without WebGPU validation output; the title border rendered without the
former diagonal fill.

### Performance validation

Commit `b682d0cdc92d6716d9a1c9be80b33334fca41043` was compared directly with
its parent `1f52aa13` in detached worktrees using the same
`microsoft-ui-xaml` submodule revision. Both revisions were built in Release
with .NET SDK 10.0.201 and measured on an Apple M3 Pro running macOS 26.

The headless harness used the same 160x112 WebGPU target and ran four
alternating processes for each revision. Each process warmed 40 frames, then
measured 600 unchanged frames, 600 invalidated ordinary-path frames, and 80
unique partial-rounded-border differences. Values below are the median of the
four per-process medians; allocations are total managed allocations divided by
frames.

| Workload | Parent frame median | Commit frame median | Parent allocation/frame | Commit allocation/frame |
| --- | ---: | ---: | ---: | ---: |
| Unchanged compiled-scene replay | 0.0287 ms | 0.0254 ms | 390 B | 389 B |
| Invalidated ordinary path | 1.4235 ms | 1.4051 ms | 2,646 B | 2,656 B |
| Changing rounded-border difference | 15.3218 ms | 1.4791 ms | 26,476 B | 7,240 B |

Every unchanged frame was a compiled-scene cache hit on both revisions. The
ordinary-path difference is within run-to-run noise: the added combined-path
test is a branch and performs no allocation for ordinary paths. For the fixed
rounded-border workload, median frame time decreased by 90.3%, average
visual-tree compilation decreased from a four-run median of 6.8087 ms to
0.2068 ms (97.0%), and managed allocation decreased by 72.7%. The direct
recognizer is bounded to canonical four-to-eight-segment contours and avoids
the former GPU boolean-operation readback.

The repository's Release `Vector Shapes` sample benchmark was also run for
four 300-frame processes per revision with VSync disabled. GPU surface-acquire
time varied materially between processes, so wall FPS is not used as a
causality claim. Its median compositor time nevertheless moved from 0.2512 ms
to 0.1682 ms and median compilation from 0.0325 ms to 0.0190 ms, with no
compile frame over budget after the change. Release IL increased by 7,168
bytes in `ProGPU.Vector` and 512 bytes in `ProGPU.Scene`; `ProGPU.Backend` was
unchanged in size.
