# Avalonia integration rendering and text research

This record is the design gate for moving the Avalonia ProGPU and Silk.NET
backends into the ProGPU repository. The implementation moved from the existing
ProGPU/Avalonia integration; no implementation source was taken from the other
engines listed below. Those engines were reviewed for public contracts,
architecture, quality constraints, and validation ideas.

The full composition-replacement boundary, package contract, implementation
slices, and performance gates are specified in
[`AVALONIA_COMPOSITOR_BACKEND_ARCHITECTURE.md`](AVALONIA_COMPOSITOR_BACKEND_ARCHITECTURE.md).

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
- [Win2D offscreen drawing](https://microsoft.github.io/Win2D/WinUI3/html/Offscreen.htm)
  makes one `CanvasRenderTarget` both drawable and directly reusable as an
  image/effect input; CPU pixels and file output are explicit consumers rather
  than part of every drawing-session commit.
- [Skia `SkSurface`](https://api.skia.org/classSkSurface.html) exposes a GPU
  render target directly as drawable surface content, with `readPixels` as a
  separate operation. The current
  [GPU surface implementation](https://skia.googlesource.com/skia.git/+/94450cd1df4e6e025a4b7d6e2122035a904f2102/src/image/SkSurface_Gpu.cpp)
  shares a texture-backed snapshot when its ownership constraints permit and
  copies only when required.
- The [WebGPU copy contract](https://gpuweb.github.io/gpuweb/#copies)
  requires texture readback to be expressed as `copyTextureToBuffer` into a
  `MAP_READ | COPY_DST` buffer. This makes readback an explicit synchronization
  and storage cost rather than an implicit property of a render pass.
- [WebRender](https://github.com/servo/webrender) uses a retained display-list
  boundary, visibility-aware scene processing, batching, and explicit texture
  and glyph caches.
- [Firefox Rendering Overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  documents the production WebRender sequence from a serialized display list,
  through a scene and culled frame, to GPU commands. The current
  [WebRender scene builder](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/scene_building.rs)
  also documents asynchronous linear scene construction, retained-state
  interning, picture creation, and stacking-context order.
- [Vello](https://github.com/linebender/vello) keeps an encoded scene and uses
  compute-oriented GPU rasterization; its documented prefix-sum approach
  informed the decision to retain ProGPU scene commands and GPU path work.
  Its integration example renders straight into a caller-provided sampleable
  `wgpu::Texture`, then blits or otherwise consumes that texture.
- [Vello `Scene`](https://docs.rs/vello/latest/vello/struct.Scene.html) stores
  ordered drawing commands, context, and resources and can append a child scene
  under a transform. Its
  [raw `Encoding`](https://docs.rs/vello_encoding/latest/vello_encoding/struct.Encoding.html)
  keeps separate typed streams for paths, draws, transforms, styles, and
  resources.
- [Vello glyph-rendering plan](https://github.com/linebender/vello/issues/204)
  describes the quality/performance tradeoff between transformed vector glyphs
  and an atlas, including hinting behavior during dynamic transforms.
- [DirectComposition basic concepts](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts)
  define an ordered parent/child visual tree, per-visual content and properties,
  composition targets, and transactional updates. The current
  [Windows visual layer](https://learn.microsoft.com/en-us/windows/apps/develop/composition/visual-layer)
  preserves the same retained composition model for Windows App SDK clients.
- [Skia Graphite `Recorder`](https://skia.googlesource.com/skia/+/d78564aad21d/include/gpu/graphite/Recorder.h)
  separates recording and replay, owns device-side caches, and exposes explicit
  resource cleanup and memory-budget diagnostics.
- [Skia Graphite `ResourceProvider`](https://skia.googlesource.com/skia/+/263308ea4386/src/gpu/graphite/ResourceProvider.cpp)
  centralizes immutable shader/pipeline resource creation at the shared GPU
  context boundary rather than at an individual render target.
- The [WebGPU specification](https://gpuweb.github.io/gpuweb/) defines shader
  modules, bind-group layouts, pipeline layouts, and pipelines as device-owned
  objects and defines explicit layout compatibility. The
  [canvas-output explainer](https://gpuweb.github.io/gpuweb/explainer/#canvas-output)
  permits one device to serve multiple independently configured canvases.
- [Apple's shader-optimization guidance](https://developer.apple.com/videos/play/tech-talks/10580/)
  identifies dynamically indexed private arrays as a common register-spill
  cause and recommends reducing private storage and register pressure.
- [Parley](https://github.com/linebender/parley) keeps text shaping and layout
  as reusable CPU-side results which a renderer consumes.
- [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  and [cluster semantics](https://harfbuzz.github.io/clusters.html) define the
  script, language, direction, feature, glyph-position, and cluster contracts
  which the Avalonia 11 text adapter must preserve.
- [HarfBuzz shape-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  keeps reusable shaping decisions attached to a face and segment properties;
  it does not make shaping own glyph raster textures.
- [DirectWrite color-font support](https://learn.microsoft.com/en-us/windows/win32/directwrite/color-fonts)
  keeps glyph IDs and positions independent of the render-time representation
  selected for COLR layers, SVG, `sbix`, or CBDT bitmap glyphs.
- [DirectWrite font-file fragments](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontfilestream-readfilefragment)
  expose bounded borrowed ranges whose lifetime is controlled by an explicit
  release context rather than copying every requested table or glyph payload.
- [HarfBuzz blobs](https://harfbuzz.github.io/harfbuzz-hb-blob.html) wrap
  immutable parent font storage and create read-only sub-blobs without requiring
  writable per-table copies.
- [Skia `SkData`](https://skia.googlesource.com/skia/+/9a3f5541542/src/core/SkData.cpp)
  memory-maps file-backed data and keeps zero-copy subsets alive through parent
  ownership.
- [Skia `SkStrikeCache`](https://skia.googlesource.com/skia/+/main/src/core/SkStrikeCache.h)
  has explicit byte and entry budgets. Its
  [GPU `GlyphVector`](https://skia.googlesource.com/skia/+/5934f0e64066/src/text/gpu/GlyphVector.h)
  records an atlas generation and can regenerate atlas residency.
- [WebRender's LRU cache](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src/lru_cache.rs)
  backs texture-cache lifetime tracking and uses epoch-checked weak handles so
  a reused cache slot cannot validate stale retained content.
- [WebGPU `GPUQueue.writeBuffer`](https://gpuweb.github.io/gpuweb/#dom-gpuqueue-writebuffer)
  permits aligned writes to an explicit byte range of an existing buffer.
  [Direct3D 12 resource uploading](https://learn.microsoft.com/en-us/windows/win32/direct3d12/uploading-resources)
  likewise separates aligned buffer suballocation and range copies from
  resource creation.
- [Avalonia `BitmapCache`](https://docs.avaloniaui.net/api/avalonia/media/bitmapcache)
  defines retained subtree rasterization together with resolution scaling,
  destination pixel snapping, and the ClearType-versus-grayscale text policy.
- [Avalonia effects](https://docs.avaloniaui.net/docs/graphics-animation/effects)
  define typed whole-visual blur and drop-shadow parameters, while
  [`DrawingContext.PushEffect`](https://docs.avaloniaui.net/api/avalonia/media/drawingcontext)
  defines content bounds as pre-inflation input and reserves the effect output
  padding before entering the platform renderer.
- [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  use content bounds to limit layer composition and intersect transformed
  geometric-mask bounds with those content bounds. WebRender likewise models
  masks as bounded
  [`CacheMaskTask`](https://doc.servo.org/webrender/render_task/struct.CacheMaskTask.html)
  render tasks rather than requiring one full-frame mask surface.
- [Skia `SkCanvas`](https://api.skia.org/SkCanvas_8h_source.html) keeps device
  clip bounds for quick rejection and carries optional bounds through its
  internal save-layer path. Vello's
  [renderer architecture](https://github.com/linebender/vello#motivation)
  explicitly treats intermediary textures as a cost to minimize through
  GPU-native clipping and bounded temporary work.
- [Skia image filters](https://api.skia.org/classSkImageFilters.html) express
  Gaussian blur strength as sigma and crop the input/output with an explicit
  rectangle. [Direct2D Gaussian blur](https://learn.microsoft.com/en-us/windows/win32/direct2d/gaussian-blur)
  likewise treats standard deviation as the filter parameter and documents
  transparent soft-edge output growth. ProGPU therefore keeps typed content
  bounds, filter sigma, and transparent raster padding as separate values.
- Avalonia's typed composition render contract applies an opacity mask to the
  visual subtree bounds. ProGPU maps solid, linear-gradient, radial-gradient,
  and conic-gradient masks to its existing GPU mask pass. Scene and image masks
  are recorded into retained picture masks.
- [Avalonia `ConicGradientBrush`](https://docs.avaloniaui.net/api/avalonia/media/conicgradientbrush)
  defines its sweep angle from above the center point. ProGPU preserves that
  public convention with a center-relative brush coordinate transform over its
  reusable sweep primitive instead of changing the primitive's shader-wide
  angle semantics.

## Comparison and decisions

| Concern | Cross-engine finding | ProGPU/Avalonia decision |
| --- | --- | --- |
| Startup and lazy initialization | GPU renderers separate inexpensive registration from device-owned initialization. | `UseProGpu` and `UseSilkNet` register typed services. A window creates its `WgpuContext` when Silk initializes that window; offscreen rendering creates a context only on first use. |
| Shaping and layout reuse | Skia/SkParagraph, DirectWrite, Parley, and HarfBuzz produce positioned glyph/cluster results before rasterization. | Avalonia's shaping contract is implemented by the managed ProGPU OpenType shaper by default. The optional `--harfbuzz` lane remains differential evidence. Both produce `ShapedBuffer`; compositor drawing consumes retained glyph indices and positions instead of reshaping. |
| Retained scene reuse | WebRender converts a serialized display list into an interned scene before culling it to a frame. DirectComposition keeps ordered visual identity and applies updates transactionally. Vello stores ordered commands/resources and can append child scenes. Graphite separates recording, replay, and resource ownership. | The pinned Avalonia target exposes a typed, revisioned visual-tree transaction. `AvaloniaCompositionScene` mirrors stable server visual identity and child order, updates only changed nodes after the initial synchronization, and records each node into a persistent ProGPU visual. `DrawVisual` embeds the retained subtree at the correct command position; unsupported composition semantics are isolated as atomic Avalonia-rendered subtrees while they are implemented natively. |
| Visibility and upload | Production renderers cull before expensive raster/upload work and upload demanded resources. DirectWrite defers the color-glyph representation choice until rendering. | Avalonia owns visual invalidation and culling. Shaped bitmap runs remain `DrawGlyphRun` commands; ProGPU decodes and uploads only demanded glyphs during scene compilation. |
| Cache identity and eviction | Texture/glyph caches must include device identity, font/glyph/style/scale state, and bounded eviction or generation invalidation. Skia associates GPU glyph consumers with an atlas generation; WebRender combines LRU residency with epoch-validated handles. | Typeface caches include the Avalonia request and font location. The duplicate unbounded Avalonia RGBA page atlas and decoded-pixel cache were removed. Embedded bitmaps use ProGPU's bounded color atlas, whose LRU reuse advances `Generation`; the adapter retains only a 2,048-entry metrics LRU with zero decoded pixel bytes. Exact WGSL, ABI-versioned layouts, and semantic pipeline descriptors remain reference-counted by the shared native-device lifetime. |
| Worker preparation | Text shaping and retained scene preparation are CPU work; GPU upload and presentation remain device work. | The integration does not move Unicode/OpenType shaping to a shader. Avalonia scheduling prepares commands; ProGPU records and submits on the render path. No reflection bridge is used in rendering hot paths. |
| GPU organization | Vello/WebRender batch encoded commands and parallel raster work rather than reading a surface back through the CPU. Apple recommends fixed vector state instead of dynamically indexed private arrays when shader compilation reports spills. | Surface presentation passes the `WGPU_SURFACE` handle and uses a GPU texture blit. Offscreen readback exists only for APIs that explicitly request CPU pixels. Glyph/path winding state uses fixed vector lanes while preserving scalar sample arithmetic and the exact half-open crossing contract; a matched Instruments run reduced first-use compiler spills from seven to zero. |
| Whole-visual effects | Avalonia supplies pre-inflated platform bounds, Skia consumes Gaussian sigma and an explicit crop, and Direct2D grows soft-edge output with transparent pixels. | `IBlurEffect` and `IDropShadowEffect` map to typed ProGPU effect nodes. Each node retains the uneffected subtree bounds, a bounded transparent border, converted sigma, offset, straight RGB, and combined color/effect alpha. The same implementation services retained visuals and nested flattened drawing scopes without reflection. |
| DPI, subpixel, and hinting | DirectWrite supports fractional glyph origins; Vello notes that hinting can shimmer under animated transforms; physical target size and logical layout must remain distinct. | Silk reports physical `FramebufferSize`. Avalonia coordinates remain logical and the renderer carries DPI into target creation and text sizing. Final placement is not rounded to whole logical pixels. |
| Fallback and variable fonts | HarfBuzz shapes a selected face and preserves clusters; font fallback and variation selection belong before rendering. | Avalonia font fallback remains authoritative. The ProGPU adapter preserves clusters, direction, language, and feature ranges. Unsupported variation behavior is not guessed in the renderer. |
| Device loss and target replacement | Direct2D/Win2D invalidate all resources owned by a lost device. | Caches check `WgpuContext` identity and disposed state. Window disposal releases the window context and dependent framebuffer resources. Recovery must create a new context rather than reuse textures from the disposed one. |

## Adopted, adapted, and rejected

Adopted:

- reusable shaped glyph results rather than GPU-side Unicode shaping;
- retained scene commands and GPU-side presentation without live-surface
  readback;
- explicit context ownership for textures and caches;
- device-lifetime sharing for compatible immutable shader/layout/pipeline
  resources;
- fixed vector-lane glyph/path winding state, validated by zero compiler spills
  and the complete retained/flattened pixel gate;
- generation-safe path-atlas shrink after a stable hysteresis interval, using a
  bounded deterministic packing probe and same-frame demand growth;
- retained and incremental path replay explicitly marks the last compiled live
  path set, preventing hysteretic shrink from discarding still-submitted UVs
  merely because CPU compilation was skipped;
- one bounded generation-tracked color glyph atlas instead of a second
  Avalonia-owned unbounded page list;
- file-backed bitmap-font payloads remain memory-mapped and glyph images borrow
  immutable `sbix` slices instead of retaining one encoded managed array per
  glyph;
- physical framebuffer dimensions with logical Avalonia layout coordinates;
- lazy, demand-driven context and glyph resource creation.

Adapted:

- WebRender/Vello retained-scene ideas are expressed through ProGPU's typed
  command recorder and Avalonia's existing invalidation contract;
- DirectComposition's visual identity and transactional update model is adapted
  to stable Avalonia server IDs, monotonically increasing revisions, and an
  explicit synchronization-completion acknowledgment;
- Vello child-scene composition is adapted as ProGPU `DrawVisual`, but retains
  child identity instead of performing Vello's documented `O(N)` append;
- DirectWrite/Parley shaping separation is expressed through Avalonia
  `IGlyphTypeface`, `GlyphRun`, and the v11 `ITextShaperImpl` seam;
- device-loss rules are enforced through `WgpuContext` identity/disposal rather
  than a DirectX-style device event.
- Graphite/Direct2D device-domain reuse is adapted as exact typed keys and
  leases; mutable targets and retained state are deliberately not shared.
- Graphite/WebRender budgeted-cache behavior is adapted as independent
  rectangular PathAtlas shrink. Stale UV entries are discarded only together
  with a generation advance, so retained scenes cannot submit moved coverage.
- DirectWrite's shape/render separation is adapted by retaining Avalonia's
  shaped arrays while selecting bitmap/color representation during ProGPU
  compilation. Mixed-run slices reference the original arrays rather than
  allocating one-element copies.

Rejected:

- runtime reflection to bridge Avalonia private rendering types;
- reshaping text during compositor submission;
- unconditional per-frame scene rebuilding or bitmap upload;
- CPU readback followed by presentation;
- whole-pixel logical text snapping, which would lose physical subpixel
  placement at non-integer DPI;
- copying cache, shaping, or rasterizer implementations from another engine.
- sharing one mutable compositor across multiple surfaces, which would make
  target-local texture validation and disposal incorrect.
- a separate Avalonia bitmap atlas, because it duplicated decoded pixels and
  added unbounded 4 MiB pages outside the compositor generation contract.

## Validation record

Validation is performed against the final source state:

- Avalonia 12 renderer and Silk.NET projects build on .NET 10.
- Avalonia 11 renderer and Silk.NET projects build from the same shared sources
  with `AVALONIA11` conditionals.
- 95 focused Avalonia renderer, shaping, cache, and packaging contract tests
  pass.
- 34 focused Silk.NET dispatcher, input, cursor, icon, timer, and framebuffer tests
  pass.
- 2 Avalonia Skia compatibility/source-integrity tests pass. The project links
  53 files from the pinned Avalonia fork submodule and keeps only the original
  `GlyphRunImpl.cs` override locally; the effective 54-file set matches
  Avalonia `12.0.5` commit
  `fee9c561ce036e8a3e8cee2397c75ca599b4790d` byte-for-byte.
- 274 expected render baselines and 10 input images decode successfully.
- The exact official Avalonia 12.0.5 upstream text corpus passes against the
  managed ProGPU shaper: 274 of 279 `Media.TextFormatting` tests succeed with
  the five original skips, and all 13 `GlyphRun` tests succeed. Fixture service
  selection is compile-time and reflection-free; test bodies and assertions
  remain upstream.
- The retained composition slice preserves 787 stable ProGPU visual nodes on
  the Buttons page and records six outer host commands instead of the flattened
  path's 293. Pixel captures for Buttons, Composition, Acrylic, and BitmapCache
  are byte-identical to the exact same binary's flattened path. All four pages
  now remain fully inside the retained scene with zero fallback nodes.
- A benchmark-only Apple Color Emoji fixture exercises 67 distinct `sbix`
  glyphs. Retained and flattened captures are byte-identical and visually match
  the official Skia/HarfBuzz lane; the adapter reports 67 metric entries, zero
  decoded-pixel bytes, and a single bounded 1 MiB compositor color atlas.
- ProGPU-backed Avalonia geometry clips now bind directly to the retained
  mirror node and apply through ProGPU's existing geometry-mask scope. A
  bounded 160×120 elliptical root clip on Buttons produced zero retained
  fallback nodes and a byte-identical 1024×800 capture against the exact same
  binary with `PROGPU_AVALONIA_RETAINED_SCENE=0`. The repeatable
  `tools/test-avalonia-progpu-retained-pixels.sh` gate checks that fixture plus
  the nine named ControlCatalog pages. Unknown platform geometry
  implementations still fail closed to the atomic subtree path.
- Avalonia render and text options are merged along the typed composition
  parent chain using the same `current.MergeWith(pushed)` precedence as the
  production Avalonia backends. A root-level aliased-text option applied after
  attachment exercises inherited descendant invalidation: the retained and
  flattened 1024×800 Buttons captures are byte-identical, differ from the
  ordinary antialiased fixture, and retain zero fallback nodes.
- Avalonia `BitmapCache` now maps to ProGPU's native retained layer cache.
  `RenderAtScale` multiplies the physical raster size, non-positive values
  suppress and release the layer, and `SnapsToDevicePixels` aligns the
  destination origin in physical coordinates. `EnableClearType=false`
  converts inherited subpixel text to grayscale within the cached subtree;
  enabling it preserves the subpixel mode. The default, scale-2, fractional
  snap, ClearType-on, and ClearType-off ControlCatalog fixtures are
  byte-identical to the exact-binary Avalonia flattened path and use zero
  fallback nodes. A focused layer test also verifies 2x, 0.5x, and zero-scale
  texture lifecycle behavior.
- ControlCatalog Canvas now keeps its linear-gradient opacity mask in the
  retained ProGPU scene. Its 1024x800 retained capture is byte-identical to the
  exact same binary's flattened path, reports zero fallback nodes, and is part
  of the repeatable pixel gate.
- A root conic-gradient opacity mask at 23 degrees maps to a native rotated
  ProGPU sweep brush. Its retained and flattened 1024x800 captures have the
  same SHA-256, differ from the ordinary Buttons baseline, and report zero
  fallback nodes. The mapping preserves Avalonia's above-center angle contract
  without changing ProGPU's general sweep-gradient shader semantics.
- An adorner whose `AdornerIsClipped` policy is disabled needs no extra
  composition operation after Avalonia computes its typed server transform.
  ControlCatalog AdornerLayer therefore remains native and its retained and
  flattened captures are byte-identical with zero fallback nodes.
- Clipped adorners now carry a compact ordered array of parent-relative
  rectangle or ProGPU geometry clips. Compilation pushes each clip under its
  retained transform and pops the chain in reverse; the same chain constrains
  GPU hit-test state and offscreen layer/effect descendants. Clipboard and
  Notifications retained captures are byte-identical to the flattened path
  with zero fallback nodes.
- Scene and image opacity masks are recorded once as a retained `GpuPicture`
  and use ProGPU's existing picture-mask pass. The Fluent GroupBox border-gap
  `VisualBrush` on HeaderedContentControl is byte-identical retained versus
  flattened and no longer requires a fallback subtree.
- Avalonia `IBlurEffect` and `IDropShadowEffect` now stay native in both the
  retained visual mirror and the flattened `IDrawingContextImplWithEffects`
  contract. The effect input is captured using explicit translated subtree
  bounds, raw Avalonia radius is separated from Gaussian sigma and raster
  padding, and drop-shadow offset, RGB, color alpha, and effect opacity remain
  typed parameters. Buttons blur and colored offset-shadow fixtures each
  produce byte-identical retained and flattened 1024×800 PNGs, visibly differ
  from the no-effect baseline, allocate the expected two ProGPU compute
  pipelines, and report zero retained fallback nodes.
- A current 3-warmup/3-measured-frame census launched all 70 exact-source
  ControlCatalog pages. Sixty-nine pages produced a main retained scene and
  the separate-surface Composition page completed normally. After validating
  the three formerly nonzero pages above, every observed main scene reports
  zero fallback nodes.
- A 30-frame warmup/60-frame Buttons measurement after separating visual-state
  and immutable draw-list revisions and recording visible content on demand
  produced 60.99 FPS, 16.949 ms/frame, 0.439 ms compilation, 0.793 ms total
  compositor time, and 5,408 allocated bytes/frame. Before this split, the same
  retained workload measured 0.762 ms compilation, 1.156 ms compositor time,
  and 5,515 bytes/frame. Retained picture residency fell from 815 to 648 and
  picture compilations from 1,181 to 920. The exact-binary flattened comparison
  measured 0.355 ms compilation, 0.783 ms compositor time, and 5,399
  bytes/frame. This is a substantial retained-path improvement and allocation
  parity, but not yet a general CPU speedup; incremental/paged GPU scene
  compilation remains an acceptance gate.
- The compiler now honors a typed `IOwnedRenderCommandCache.HasRenderCommands`
  signal. Empty Avalonia mirror nodes bypass command-cache lookup and playback
  without changing topology or skipping their visual state and descendants.
  A paired 60-frame warmup/180-frame Buttons run reduced compilation from
  0.715 ms to 0.591 ms; a separate pixel capture remained byte-identical to the
  validated retained baseline. All 18 `LayerRenderTests`, including
  incremental page and dirty-upload regressions plus a
  child-rendering regression whose empty parent cache must never be queried,
  native BitmapCache scale/lifecycle coverage, and retained picture-mask
  coverage pass. The timing is a local bounded result because short desktop
  GPU runs remain noisy.
- The next clean-room compiler slice adds typed incremental scene pages.
  Only an `IIncrementalRenderCommandCache` whose commands are immutable until
  explicit invalidation can opt in. Page keys include content revision,
  transform, effective opacity/clip/blend, logical and physical target state,
  DPI, atlas generations, and rounded-pipeline specialization. Unsupported
  commands and composition scopes fail closed to ordinary compilation.
  Replayed pages append normalized typed vertex/index/text/texture streams and
  merge compatible draw calls across page boundaries. Content revisions evict
  older revisions for that visual; useful transform/state variants remain
  bounded by the compositor-wide LRU.
- Dynamic scene buffers retain a CPU comparison shadow and compare fixed 4 KiB
  byte ranges. The first frame, buffer replacement, or growth performs one full
  write. Later frames issue aligned `GPUQueue.writeBuffer` operations only for
  changed ranges. This is `O(B)` comparison work for `B` assembled scene bytes,
  `O(D)` queue bandwidth for `D` dirty bytes, and bounded `O(B)` shadow
  storage. The feature can be disabled in the exact same binary with
  `PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES=0`.
- On Apple M3 Pro/macOS 26/.NET 10 Release, a paired
  60-warmup/180-measured-frame exact-source Buttons run transferred 733,184
  scene bytes with incremental pages versus 14,930,160 bytes disabled
  (95.1% less). Average compilation moved from 0.6141 to 0.5636 ms, upload from
  0.3014 to 0.1840 ms, and total compositor time from 1.4617 to 1.3188 ms.
  The live page store stabilized at 235 pages/243,944 bytes and the upload
  shadow at 106,496 bytes rather than retaining obsolete animated revisions.
  Wall FPS remained VSync-limited and is not used as a causal claim.
- The exact-source ControlCatalog completed 140/140 fresh processes: every one
  of 70 pages with both the ProGPU and HarfBuzz shapers. ProGPU shaping averaged
  60.694 FPS, 17.006 ms/frame, and 6,076.18 allocated bytes/frame; HarfBuzz
  averaged 60.622 FPS, 17.015 ms/frame, and 6,076.59 bytes/frame. The result
  establishes workload parity without claiming a causal speedup.
- Four NuGet package/version entries and their symbol packages build; archive
  inspection confirms exact Avalonia and ProGPU dependencies.
- The private exact-identity `Avalonia` 12.0.5 replacement is produced by
  Avalonia's official merge/reference-assembly pipeline. Every net8.0/net10.0
  `lib` and `ref` assembly passes strict ApiCompat against the official
  package, the packaged `Avalonia.Base` byte-matches the validated source
  build, and an isolated package consumer verifies the exact restored payloads.
- Avalonia's delayed parent-change state had one independently reproduced
  state-machine defect: the consumption assignment cleared
  `IsDirtyForRender` twice and left `NeedsBoundsUpdate` set. A no-op
  recomputation consequently advanced the retained revision from 1 to 2.
  The clean source patch clears each of the three distinct flags, and the new
  `ParentChangeDelayedFlagsAreConsumedByOneRecompute` regression passes along
  with all 2,853 `Avalonia.Base.UnitTests` cases (2,841 passed and 12 original
  skips). Strict ABI validation still reports `Avalonia.Base, Version=12.0.5.0`
  with public-key token `C8D484A7012F9A8B`.
- Alternating exact-source ControlCatalog Composition measurements used the
  same Release binary, 120 warmup frames, and 300 measured frames. The two
  fixed runs measured 120.46/120.36 FPS, 0.3935/0.3415 ms compilation,
  0.9124/0.8170 ms total compositor time, and 5,836/5,499 managed bytes/frame.
  The intervening original-bug run measured 120.28 FPS, 0.3660 ms
  compilation, 0.8515 ms compositor time, and 5,530 bytes/frame. All runs
  retained 738 nodes, performed one full and 419 incremental synchronizations,
  used zero fallback nodes, and reported 34,177,024 tracked Metal bytes.
  Therefore this is a correctness and no-regression result, not a performance
  claim: the animated Composition workload has legitimate per-frame changes
  and does not isolate the eliminated no-op path.
- An isolated package-only application restores and builds with no project
  references for both Avalonia 12.0.5 and Avalonia 11.3.18.
- The full `ProGPU.slnx` build succeeds, including both shared-source Avalonia
  lanes, the unchanged Skia backend, ControlCatalog, RenderDemo, and tests.
- The ProGPU runtime suite passes 2,475 tests after the retained `DrawVisual`,
  embedded-visual invalidation, typed empty-command-cache, incremental-page,
  partial-upload, BitmapCache, picture-mask, and transformed outer-clip
  and translated effect-content-bounds semantics changes, including the typed
  device-loss generation regressions.
- The final replacement refresh passes all 89 Avalonia renderer/package tests
  and all 42 Silk.NET integration tests. Its isolated SHA-512-validated
  package consumer rendered 21 frames, observed one retained scene, and
  reported zero fallback nodes; both packaged host assemblies pass the
  runtime-reflection metadata audit.
- The exact-source ControlCatalog Composition gate now attaches a bounded
  animated `CompositionCustomVisualHandler` through the public typed API and
  requires native execution telemetry. A 30/60 final run retained one custom
  visual, compiled 89 handler revisions, retained 739 total scene nodes, and
  reported zero fallback nodes. This is a behavioral coverage run, not a
  performance comparison, because the explicit fixture adds one animated draw.
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

## Cross-context images and Retina resize follow-up

The Skia compatibility resize failure exposed a resource-domain boundary that
the original startup smoke check did not exercise:

- [Skia `SkImage`](https://api.skia.org/classSkImage.html) defines an immutable
  image whose pixels may live in a raster bitmap, encoded data, or GPU memory,
  and permits lazy allocation of additional storage when needed.
- [Direct2D wrong-resource-domain diagnostics](https://learn.microsoft.com/en-us/windows/win32/direct2d/d1121)
  require a bitmap used by a device context to belong to the same device
  resource domain.
- [WebGPU image-copy validation](https://www.w3.org/TR/webgpu/#abstract-opdef-validating-texture-copy-range)
  validates copies between GPU textures owned by the active device rather than
  defining an implicit cross-device copy.

ProGPU adopts the same ownership split without a GPU readback. An immutable
`SKImage` created from an `SKBitmap` retains one tightly packed RGBA snapshot
and lazily uploads it once into each target `WgpuContext`; later draws reuse the
context-local texture. Images which wrap borrowed or GPU-only textures retain
strict resource-domain rejection because no portable CPU representation exists.
The Silk window now pushes its exact stored context while invoking Avalonia's
paint callback and disposes that same instance with the window. The portable
snapshot costs four bytes per pixel for the lifetime of a bitmap-backed image;
it avoids a synchronous readback and bounds context materialization to one
upload per image and target context.

Retina resizing also revealed that Avalonia can express a full-window rectangle
as three explicit line segments plus the implicit closing edge. When that
canonical sharp rectangle provably cannot fit the maximum atlas, the direct
path recognizer emits two analytic triangles in O(1) time and storage,
independent of framebuffer dimensions. Smaller sharp rectangles retain their
existing atlas rasterization and antialiasing; fully rounded and arbitrary
paths also continue through the PathAtlas. An arbitrary path that cannot fit
after the single deterministic reset still fails explicitly. This removes the
former 2,408x1,608 and 2,568x1,528 full-window atlas requests rather than
increasing atlas size or reducing raster quality.

### Resize and image performance validation

A native ControlCatalog `--skiashim` run exercised four automated logical
window sizes (1280x760, 1420x900, 1210x680, and 1360x820) on the Retina
framebuffer. It completed without a cross-context exception, an oversized-path
failure, or WebGPU validation output.

A Release image harness compared the parent revision with the final source on
the same Apple M3 Pro. Each process warmed 40 frames and measured 400 draws of
a 128x128 bitmap-backed image into a 192x128 surface; the values below are
medians from four processes.

| Workload | Median frame time | Managed allocation/frame |
| --- | ---: | ---: |
| Parent, unchanged same-context draw | 1.4852 ms | 8,760 B |
| Final, unchanged same-context draw | 1.4796 ms | 8,783 B |
| Final, cached cross-context draw | 1.5415 ms | 8,718 B |

The final same-context result differs by -0.4% in time and +23 bytes per frame,
both within run-to-run noise. The first cross-context draw took a 17.8708 ms
median to create and upload the target-context texture. Subsequent
cross-context frames allocated no portable pixel buffer and ran within 4% of
the parent same-context median, confirming that resize compatibility does not
introduce a per-frame CPU upload. Direct rectangle regressions additionally
assert zero PathAtlas entries when the canonical rectangle is oversized, normal
atlas use when it fits, and preservation of the general oversized-path failure
test.

## Exact Avalonia compositor comparison (2026-07-25)

The Avalonia replacement lane was measured in fresh Release processes on the
same Apple M3 Pro/macOS host. Each ControlCatalog process selected one of
Buttons, Composition, Custom Drawing, or TextBlock, warmed 60 frames, measured
180 frames, then performed two blocking compacting collections before retained
managed memory was sampled. The clean `main` control used detached commit
`1417e7d3c07b1e71d1960aa0326e87724e002f19`; the current retained and flattened
controls used the same current binaries and differed only by
`PROGPU_AVALONIA_RETAINED_SCENE`. The Skia control used the official Avalonia
12.0.5 packages and HarfBuzz.

| Lane | Mean FPS | Mean frame | Mean compile | Mean compositor | Allocation/frame | Managed retained | Mean physical footprint |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Clean `main` ProGPU | 61.325 | 16.440 ms | 0.053 ms | 0.279 ms | 3.06 KiB | 23.54 MiB | 241.98 MiB |
| Current flattened ProGPU | 59.256 | 17.044 ms | 0.224 ms | 0.845 ms | 20.12 KiB | 93.59 MiB | 436.93 MiB |
| Current retained ProGPU | 58.629 | 17.277 ms | 0.511 ms | 1.210 ms | 23.54 KiB | 96.34 MiB | 647.99 MiB |
| Official Skia/HarfBuzz | 120.080 | 8.380 ms | unavailable | unavailable | 6.26 KiB | 12.35 MiB | 321.11 MiB |

The Skia wall cadence is about 120 Hz while the Silk.NET ProGPU host presents
at about 60 Hz, so its FPS and wall-frame columns are not an isolated renderer
throughput comparison. Skia also does not publish the ProGPU internal compile,
upload, or render-pass counters. Allocation, retained managed memory, and
process footprint remain directly observable, although macOS physical
footprint includes driver allocations and is noisier than managed memory.

Against the exact clean `main` control, the repaired retained lane is 4.4% lower
in wall FPS, 5.1% higher in mean frame time, 7.7 times higher in
allocation/frame, 4.1 times higher in retained managed memory, and 2.7 times
higher in mean physical footprint. Against the same current binary's flattened
lane, retained is 1.1% lower in FPS, 1.4% higher in frame time, 2.3 times higher
in compile time, 43% higher in total compositor time, 17% higher in
allocation/frame, 2.9% higher in managed memory, and 49% higher in physical
footprint. These measurements do not yet support a retained-compositor
performance-win claim.

The first retained Composition measurement was additionally invalid: it
reported 47.96 FPS, 175.23 KiB/frame, and no backend metrics because repeated
incremental-page captures saturated the cache and an oversized transformed path
failed atlas packing. The repaired run reports 58.46 FPS, 7.83 KiB/frame,
0.532 ms compilation, 1.273 ms compositor time, 273 incremental pages, and zero
fallback nodes. The repair bounds the global page store to 512 pages, bounds
transform/state variants to two per visual, backs volatile visuals off for 600
frames, performs saturation preflight before allocating capture arrays, and
allows the path atlas to grow on demand to 4096 for legitimate desktop-scale
paths. Sharp transformed rectangles that exceed the atlas now use the existing
bounded analytic-triangle path without relaxing rotated-edge quality.

The remaining retained physical-memory gap is understood: 35 to 48 concurrently
live transformed `ClipToBounds` scopes are materialized as full-target R8 mask
textures, while the flattened lane records equivalent content without these
masks. Axis-aligned rectangle geometries are now recognized allocation-free and
use the scissor stack. Rotated rectangles deliberately retain exact mask
coverage; converting them to bounding scissors was rejected because it changes
pixels. The next memory work is therefore bounded/local mask allocation with an
explicit sampling origin, or an analytic transformed-rectangle clip contract,
followed by the same retained/flattened pixel and memory comparison.

The retained pixel gate passed nine zero-fallback pages plus native
linear/conic/picture opacity masks, transformed adorner clip chains,
blur/drop-shadow effects, geometry clipping, inherited text options, and all
BitmapCache scale/snap/ClearType fixtures. Focused tests also cover cache
saturation, per-visual volatility backoff, allocation-free rectangle
recognition, axis-aligned scissor selection, rotated sharp-rectangle direct
rendering, rotated clip edges, arbitrary combined geometry, and desktop-scale
path growth.

## Bounded retained-mask follow-up (2026-07-25)

The full-target mask diagnosis was resolved using these primary architecture
contracts:

- [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  define explicit content bounds and a geometric-mask transform; when bounds
  are omitted, the transformed mask bounds determine the effective layer
  extent. ProGPU adopts the bounded-resource principle while retaining its own
  WebGPU render-pass architecture.
- [WebGPU texture-copy validation](https://www.w3.org/TR/webgpu/#abstract-opdef-validating-texture-copy-range)
  requires every source and destination origin plus extent to fit its selected
  subresource. Each ProGPU mask copy therefore records a clamped integer
  physical-pixel rectangle.
- [Vello](https://github.com/linebender/vello) demonstrates a longer-term
  compute-oriented clip path that avoids intermediate clip textures. That
  design was not adopted in this focused change because replacing ProGPU's
  clip representation and all extension pipelines would be a materially
  broader quality and performance change.
- [WebRender](https://searchfox.org/firefox-main/source/gfx/wr/webrender)
  keeps clip and spatial information in the retained scene before frame
  construction. ProGPU likewise keeps logical clip identity in the retained
  scene, but materializes only the physical coverage needed by the current
  target.

ProGPU now constructs masks through one reusable full-target R8 scratch texture
so every existing vector, text, image, effect, and retained-glyph vertex
projection remains unchanged. After each construction pass, only the
conservative transformed AABB (including a two-physical-pixel antialias guard)
is copied into the live mask texture. The mask bind group carries typed
physical origin and inverse-extent uniforms; every built-in masked shader and
the ShaderToy, image-effect, backdrop, retained-glyph, and generated WPF-effect
pipelines use the same bounds-checked sampling contract. Nested masks multiply
against the parent's local origin and extent during construction.

Storage dimensions are rounded up to 16-pixel size classes while the origin
remains exact. The additional texels come from the cleared scratch surface and
are transparent, so this changes allocation reuse rather than clip coverage.
The inactive texture pool is configurable and defaults to 128 entries;
surplus textures and their bind groups/uniform buffers are released after
submission. Public metrics now report pooled mask bytes and scratch bytes.

Focused headless tests cover a rotated clip, nested rotated geometry-mask
intersection, offset explicit render-target viewports, vector/text/texture and
effect mask consumers, bounded residency, and pool eviction. The complete
nine-page retained/flattened pixel gate passed again before the matched
performance run.

The same four pages and 60-warmup/180-measured protocol from the exact
comparison above produced:

| Retained lane | Mean FPS | Mean frame | Mean compile | Mean compositor | Allocation/frame | Managed retained | Mean physical footprint |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Before bounded masks | 58.629 | 17.277 ms | 0.511 ms | 1.210 ms | 23.54 KiB | 96.34 MiB | 647.99 MiB |
| Bounded masks | 58.336 | 17.328 ms | 0.507 ms | 1.237 ms | 23.62 KiB | 96.39 MiB | 496.64 MiB |

Mean process physical footprint decreased by 23.4% (151.35 MiB), and the
maximum across the four fresh processes decreased from 650.22 MiB to
504.14 MiB. Mean mask texture residency decreased from an inferred
131.25 MiB (42 full-target R8 textures at 3.125 MiB each) to a directly
reported 4.85 MiB (1.72 MiB pooled local masks plus one 3.125 MiB scratch
surface), a 96.3% reduction. Mean wall FPS changed by -0.5%, compositor time
by +2.2%, and allocation/frame by +0.3%; these small differences include the
new bounded copy and are not claimed as a speed improvement.

Against the unchanged current flattened lane, retained physical footprint is
now 13.7% higher rather than 49% higher. It remains 2.05 times the clean
`main` control and 1.55 times the official Skia process footprint, while
retained managed memory remains essentially unchanged. The next work should
target retained-picture churn and incremental upload/compilation cost; a
future direct local-projection or compute clip path may remove the scratch copy
only after equivalent extension coverage and pixel evidence exist.

The repository-wide follow-up allocation inventory, effect-intermediate
changes, attributed texture metrics, and next optimization order are recorded
in [the GPU texture memory audit](progpu-gpu-texture-memory-audit.md).

## Bounded advanced-blend source follow-up (2026-07-26)

The texture-memory audit's Skia Graphite, Direct2D, WebRender, and Vello
resource-graph research was applied to destination-sampling image blends.
ProGPU keeps the required full-size ping-pong output because WebGPU render
attachments cannot also be sampled by the same pass, but no longer
rasterizes a small source quad into a second full-size color texture.

The compositor derives conservative physical bounds directly from the indexed
transformed texture vertices, intersects them with the target and clip, and
uses one demand-sized source texture for the frame. Typed per-pass uniform
slots preserve the global-to-local projection and global opacity-mask
coordinates. The fragment shader subtracts that same origin and treats
out-of-region source samples as transparent. Source capacity grows on demand,
shrinks after 240 substantially smaller frames, and the lazy blend textures
are released after 240 frames without advanced blending.

This adopts bounded render-task allocation and explicit resource lifetime,
while rejecting a target readback/copy, CPU quad reconstruction, framebuffer
fetch assumptions, and same-attachment read/write. GPU tests cover all
destination-sampling modes, transformed and clipped bounds, opacity masks,
chained blends, and mixed normal/advanced ordering. In the focused 128x96
case, a 20x12 affected region reduces blend-intermediate residency from
98,304 to 50,112 bytes (49.0%) with identical sampled pixels.

The matched Release ControlCatalog Composition check measured ProGPU at
119.778 FPS/8.383 ms and Skia at 119.665 FPS/8.390 ms. The page retained zero
advanced-blend bytes, confirming that this optimization does not perturb a
scene that does not use destination-sampling image blends. Its process
snapshot still showed a 90.52 MiB physical-footprint gap, while ProGPU
reported only 3.21 MiB of tracked intermediates and 52.09 MiB of Metal
allocation. A full-window Xcode trace reported a 69.0 MiB Metal maximum,
64.0 MiB final value, zero drawable waits, compiler spills, command-buffer
errors, or hang risks; this evidence keeps the remaining process-footprint
work separate from the completed bounded-source change.

Skia compatibility filtering now reuses separable blur/morphology temporaries,
successful filter-graph intermediates, consumed filter sources, and
previous-layer/backdrop inputs through one bounded per-canvas transient pool.
Reuse requires an exact WebGPU context, texture format, dimensions, and usage
match. It is safe only after the consuming GPU operation has been submitted;
later submissions on the same WebGPU queue remain ordered. Failed or
unconsumed passes dispose their resources instead of pooling them. Final
filtered textures remain outside the pool while owned by deferred drawing
commands, although a pooled texture may become a later final output after
ownership has first transferred out of the pool.

The pool retains at most four textures or 64 MiB and is disposed with the
canvas. Repeated blur and `InitializeWithPrevious` regressions prove both the
ownership rotation and disposal boundaries. The new executable
`ProGPU.GpuMemoryBaseline --mode filter` held its 320x240 native snapshot at
seven textures/7.062 MiB from frame 1 through frame 311. A direct Xcode
Instruments run of the 1024x800 workload recorded 206 Metal command-buffer
completions but zero resource-allocation rows in the final five-second window,
with no drawable waits, compiler spills, errors, or hang signals. Its two raw
trace bundles were deleted after compact export, reclaiming 108,917,309 bytes.

The retained Avalonia scene also now compacts each completed command list to
its exact count. This adapts Graphite's separation between recording-time
growth and retained resource residency to ProGPU's typed command arrays.
Mutable and pooled drawing contexts keep their reusable spare capacity;
stable composition visuals pay one compaction allocation only when their
content is recorded and then reuse the exact array on unchanged re-recordings.

In the source-built ControlCatalog `Buttons` workload, the forced-GC heap fell
from 16,366,140 to 15,129,427 bytes. `RenderCommand[]` residency fell from
731,496 to 45,136 bytes, reducing the managed gap to the original Skia capture
from 7,135,538 to 5,898,825 bytes. A temporary post-collection full heap also
proved that the apparent 1,048,600-byte `gcdump` array had no live GC root and
was capture overhead. The approximately 1.6 GiB of temporary full dumps was
deleted after the ownership and size evidence was extracted.

The corresponding matched Release Composition run remained presentation
limited: ProGPU measured 119.933 FPS/8.3655 ms and Skia measured
119.960 FPS/8.3665 ms. Managed retention was 24.20 versus 14.92 MiB and
allocation was 11.51 versus 5.50 KiB/frame. The 396.17 versus 306.70 MiB
physical footprints remain fresh-process samples rather than evidence that a
managed-array compaction changed driver or runtime reservation behavior.

## Retained command ownership follow-up (2026-07-26)

The next sampled Composition-page profile identified two redundant ownership
boundaries rather than a GPU leak. Incremental scene pages copied every
changed visual's packed arrays even when the obsolete revision had already
become reusable. ProGPU now detaches only an exact invalid page, or an
obsolete content revision for the same stable visual, and overwrites
exact-sized arrays. It deliberately preserves same-content transform variants
as independent cache entries.

This follows the production-engine pattern of separating retained scene
identity from transient compilation storage while adapting it to ProGPU's
typed arrays. It rejects a global scratch arena because pages are
independently cached and replayed, and rejects reusing a same-content
transform variant because that would invalidate a still-live cache key.
Across 600 frames the compositor reused 1,797 arrays over 675 page
compilations. Managed allocation fell from 11,465 to 9,132 bytes/frame
(-20.35%) at an unchanged refresh-limited 120.04/120.03 FPS.

The follow-up trace then identified `GpuPictureRecorder.CopyList`: Avalonia's
immutable render data was copied into a nested picture even while ProGPU was
already recording the stable visual that would own it. The retained-scene
scope now expands those typed nodes directly into its command list. The
revisioned picture cache remains active for standalone/fallback rendering,
where no owning retained visual exists. This is a compile-time typed path
with no reflection and preserves the outer scope's transforms, command
ordering, and retained resource leases.

The matched result removed all 991 nested picture compilations and reduced
allocation from 9,132 to 5,437 bytes/frame (-40.47%), or 52.58% from the
initial profile. FPS remained 119.89 and the managed heap fell by 1.79 MiB
against the immediately preceding run. A fresh original-Skia comparison
measured 119.969 FPS and 5,632.88 bytes/frame: ProGPU was within 0.064% FPS
and allocated 196.12 fewer bytes/frame. Its remaining end-of-run gap was
8.28 MiB managed, 33.94 MiB working set, and 84.75 MiB physical footprint.

Retained/flattened pixel comparisons passed across nine ControlCatalog pages
plus clip, text-option, conic-mask, blur, drop-shadow, and bitmap-cache
fixtures. Raw traces and the temporary screenshot tree were deleted after
compact JSON/Markdown evidence was preserved.

The exact post-change binary was also run through Xcode Allocations, Time
Profiler, and Metal System Trace. The final Metal window reported 903
submissions, 1,408 completions, and zero resource-allocation rows, drawable
waits, compiler spills, command-buffer errors, or hang signals. Its
`currentAllocatedSize` table and the Allocations object's table were not
available, so no replacement values are inferred from those missing exports.
The three raw traces were deleted after compact export, reclaiming
189,786,015 bytes.

## Embedded Avalonia font ownership (2026-07-26)

A post-collection ownership trace found six live 309-316 KiB managed arrays
owned by Avalonia's embedded Inter glyph typefaces. Avalonia resource loading
already exposed each face as a byte range within the assembly's single
`!AvaloniaResources` manifest resource, but the renderer copied every range
through `MemoryStream.ToArray()`. The source-built compositor now carries that
range through an internal typed `AssemblyResourceSliceStream`. `TtfFont`
borrows the immutable mapped assembly bytes and retains their
`UnmanagedMemoryStream` owner; package mode and compressed WOFF/WOFF2 inputs
keep the existing copy/normalize fallback. No public Avalonia contract,
runtime reflection, symbol probing, or native-memory relabelling is involved.
The final package seam keeps the owning `Assembly` private to Avalonia.Base:
`AssemblyResourceSliceStream.OpenResourceStream()` returns the manifest
resource as a neutral `Stream`, and the typed offset/length contract remains
unchanged. `EmbeddedFontData.TryOpen(Stream, ...)` transfers ownership only
for the zero-copy `UnmanagedMemoryStream` case; an unsupported stream remains
caller-owned and follows the bounded copy fallback. This removes even a
`System.Reflection.Assembly` type reference from `Avalonia.ProGpu.dll`, which
is enforced by the package metadata audit, without adding reflection or
changing the mapped-resource lifetime.

The clean-room design was informed by these primary contracts:

- DirectWrite's
  [`IDWriteFontFileStream::ReadFileFragment`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontfilestream-readfilefragment)
  returns a bounded fragment plus a release context, requires bounds checks,
  and permits concurrent calls. ProGPU adopts the explicit range and retained
  owner, while immutable assembly storage avoids mutable stream-position
  synchronization.
- [HarfBuzz blobs](https://harfbuzz.github.io/harfbuzz-hb-blob.html) separate
  immutable borrowed data, sub-blobs, duplication, and destroy callbacks.
  ProGPU adopts the immutable owner/slice lifetime and copies only when
  normalization requires writable replacement data.
- [`SkData::MakeWithProc` and `MakeWithoutCopy`](https://api.skia.org/classSkData.html)
  likewise distinguish a borrowed lifetime from release-callback ownership,
  while [SkParagraph's `FontCollection`](https://skia.googlesource.com/skia/%2B/2198b4ec8d81/modules/skparagraph/include/FontCollection.h)
  caches managers and typefaces. ProGPU keeps one reusable parsed `TtfFont`
  per Avalonia typeface instead of copying bytes into a second cache.
- WebRender keeps raw font templates out of instance state as
  [`Arc<Vec<u8>>`](https://searchfox.org/mozilla-central/source/gfx/wr/webrender_api/src/font.rs#232-253);
  size, variations, and platform options live in separate font instances.
  ProGPU preserves that raw-data/instance split.
- Parley/fontique
  [clones shared blobs and keeps weak shared-cache entries](https://github.com/linebender/parley/blob/9c41a4d0b9aa1aae7b8fdad8cf31728c9c3476bb/fontique/src/source_cache.rs#L95-L218)
  and [memory-maps path sources](https://github.com/linebender/parley/blob/9c41a4d0b9aa1aae7b8fdad8cf31728c9c3476bb/fontique/src/source_cache.rs#L249-L254).
  ProGPU adopts shared immutable storage but retains strong ownership for the
  lifetime required by Avalonia's table/stream contract.
- Win2D resolves packaged fonts by application URI and maps
  [`CanvasFontSet` to `IDWriteFontSet`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasFontSet.htm).
  Its URI-only public contract was examined but rejected as a ProGPU bridge:
  it would add a Windows-specific lookup layer rather than preserve the
  existing cross-platform Avalonia stream API.

The forced-GC Composition heap fell from 14,198,298 to 12,491,558 bytes
(-1,706,740 bytes, -12.02%). The six large arrays disappeared; the remaining
1,048,600-byte array still has no GC root and is capture overhead. A matched
600-frame run measured ProGPU at 119.989 FPS, 8.3474 ms/frame, and 5,283
allocated bytes/frame versus Skia at 119.982 FPS, 8.3470 ms/frame, and 5,634
bytes/frame. ProGPU therefore remained refresh-limited and allocated 6.23%
less per frame. Its end-of-run gaps were 6.34 MiB managed, 28.92 MiB working
set, and 80.08 MiB physical footprint; the native/driver gap remains separate
work and is not attributed to the removed managed font copies.

The full retained/flattened pixel contract passed. Xcode Allocations, Time
Profiler, and Metal System Trace then reported 558 submissions, 860
completions, and no exported Metal allocation, drawable-wait, spill, error, or
hang rows in the steady capture window. The rolling allocated-size table was
again unavailable, so no value is inferred. All three raw trace bundles were
deleted after export, reclaiming 192,414,221 bytes. Compact evidence remains
in `artifacts/avalonia-font-slice-controlcatalog-20260726`,
`artifacts/avalonia-font-slice-retained-pixels-20260726`, and
`artifacts/avalonia-font-slice-instruments-20260726`.

## Avalonia host external-memory presentation gate (2026-07-26)

The host-control path previously called `ZeroCopySharedTexture` is not
zero-copy. It renders into a normal `wgpu-native` texture, copies that texture
to a mapped buffer, copies the mapped rows into an IOSurface or D3D11 texture,
and only then asks Avalonia to import the platform image. The name and
diagnostics now report `SharedImageReadback`; the old enum/property names remain
obsolete compatibility aliases.

The following primary contracts determine the replacement architecture:

- The [WebGPU specification's external texture contract](https://gpuweb.github.io/gpuweb/#external-texture)
  is an immutable, sample-only video/image snapshot. It cannot serve as a
  render attachment and therefore cannot implement this producer path.
- WebGPU objects are rooted in one logical device, as described by the
  [WebGPU explainer](https://gpuweb.github.io/gpuweb/explainer/#adapters-and-devices).
  Sharing an opaque `WGPUTexture` pointer between unrelated devices is invalid;
  adapter equality alone is insufficient.
- Dawn's official
  [Metal shared-texture-memory implementation](https://dawn.googlesource.com/dawn/+/cebf22738909498596d4c7ede83d229addbd5e9e/src/dawn/native/metal/SharedTextureMemoryMTL.mm)
  imports an IOSurface as texture memory with render-attachment, texture,
  storage, and copy usages. This is the correct WebGPU-native contract for a
  renderable external image.
- Apple's
  [`newTextureWithDescriptor:iosurface:plane:`](https://developer.apple.com/documentation/metal/mtldevice/maketexture%28descriptor%3Aiosurface%3Aplane%3A%29?language=objc)
  creates a Metal texture whose storage is the IOSurface; CPU locking and row
  copies are not part of that path.
- Avalonia's typed composition interop distinguishes automatic, keyed-mutex,
  semaphore, and timeline-semaphore synchronization. Its Metal/Skia
  implementation uses timeline semaphores when snapshotting an imported Metal
  texture, so an IOSurface handle alone is not a complete cross-queue
  ownership protocol.

The currently shipped Silk.NET `wgpu-native` 2.23.0 binary exports neither
`wgpuDeviceImportSharedTextureMemory` nor any equivalent IOSurface/DXGI import
entry point. Its current upstream source has no shared-texture-memory C ABI
either. A pointer cast, Objective-C inspection of opaque `WGPUTexture`
internals, or CPU copy relabeled as zero-copy was rejected.

The eventual implementation must therefore be a typed backend capability:
import platform shared memory on the producing WebGPU device, begin exclusive
access with the consumer's prior timeline value, render directly into the
imported texture, end access, and pass the exported fence/value to Avalonia's
timeline-semaphore update. Device loss, import loss, resize, and incomplete
synchronization must select the existing readback fallback. The Dawn-native
runtime and ABI must be packaged as a reviewed dependency before that
capability can be enabled.

The current `WebGPUSharp` 0.5.5 NuGet package is a viable runtime input, not a
drop-in binary replacement. Its reviewed package contains native assets for
macOS arm64/x64, Windows arm64/x64, and Linux arm64/x64, and the macOS binary
exports `wgpuDeviceImportSharedTextureMemory`,
`wgpuSharedTextureMemoryBeginAccess`, `wgpuSharedTextureMemoryEndAccess`, and
the shared-fence entry points. However, its current Dawn C ABI uses
`WGPUStringView` descriptor fields while Silk.NET.WebGPU 2.23.0 uses the older
null-terminated `const char*` layout. Renaming or redirecting that binary under
Silk's `wgpu_native` import would shift descriptor fields and is rejected as
ABI-unsafe. WebGPUSharp's public generated FFI currently covers the core API
but not shared-texture-memory handles, so the acceptable integration is a
compile-time Dawn backend using its exact core ABI plus a small typed extension
surface generated from the same official Dawn headers. It must not use a
runtime resolver, native-symbol probing, or opaque handle introspection.

That package foundation is now implemented as `ProGPU.Backend.Dawn`. It has a
normal reviewed `WebGPUSharp` dependency, disables runtime marshalling, and
uses compile-time `LibraryImport` declarations for only Dawn's missing shared
memory/fence extension entry points. Its ownership seam exposes typed
WebGPUSharp FFI handles and deterministically releases imported texture
memory, textures, fences, render-pass resources, command encoders, and command
buffers. It performs no runtime reflection, dynamic library loading, symbol
lookup, or private Dawn-object inspection.

`DawnWebGpuApi` and `DawnGpuContext` now complete the core device boundary.
The former translates ProGPU's stable typed descriptor contract into current
Dawn descriptors, including enum values whose numeric layouts changed; the
latter creates and owns low-level Instance, Adapter, Device, and Queue handles,
polls through the required timed-wait feature, and releases them in dependency
order. `WgpuContext.InitializeExternalNativeDevice` lets the existing
compositor, caches, and mapped-upload ring use that exact device without
reflection or an alternate renderer architecture.

The standalone `ProGPU.DawnSharedMemoryProbe` is the executable conformance
gate. On Apple Metal it allocated a 64x64 BGRA IOSurface, imported it as Dawn
shared texture memory, queried its dimensions, format, and usages, initialized
the full ProGPU compositor through `DawnWebGpuApi`, and rendered a retained
colored rectangle over the compositor clear color directly into the imported
texture. CPU reads validated both the clear pixel and the rectangle's center
pixel. The first `EndAccess` exported an MTLSharedEvent at timeline value 4;
importing that event, waiting in the second `BeginAccess`, and issuing a second
translated render pass advanced the timeline to 5 and produced the second
expected clear color. The validated usage set was
`CopySrc | CopyDst | TextureBinding | RenderAttachment`.

This proves that the compositor and shared IOSurface texture use the same Dawn
device, as well as the Metal ownership protocol. `ProGpuHostControl` now uses
that exact path on macOS: it creates the typed Dawn context, wraps the imported
texture as an owned `GpuTexture` without allocating a second texture, renders
between `BeginAccess` and `EndAccess`, passes the exported MTLSharedEvent/value
to Avalonia's `UpdateWithTimelineSemaphoresAsync`, and waits on the consumer
value before reusing the texture. The path uses one timeline-serialized
IOSurface; `SharedImageReadback` retains its two-image diagnostic path.
Capability failure, import failure, and resize still select the readback/custom
visual fallback. A second device plus a hidden copy remains explicitly
rejected. Each image also owns one reusable end-access result; a stable
MTLSharedEvent is retained once and reused, so steady presentation does not
allocate a managed result object or retain/release the same Objective-C event
on every frame.

The first end-to-end launch also found an initialization race: a background
`ProcessEvents` tick could submit Dawn's pending signal command buffer while
the UI thread was still encoding compositor initialization work. Metal
correctly aborted with `encodeSignalEvent:value: with uncommitted encoder`.
Dawn event processing is now performed under `WgpuContext.RenderLock` at the
completed frame/access boundary; only the Silk backend retains the host's
background polling thread.

Matched Xcode Metal System Trace captures validate the result. The final
single-IOSurface capture recorded 261 submissions, 592 completions, zero
drawable waits, command-buffer errors, compiler spills, or hang signals.
Seventy observed allocations totalled 221,544,448 bytes, but only four
resources totalling 13,926,400 bytes were live at capture end. Reducing the
shared path from two IOSurfaces to one reduced observed resources from 76 to 70
and total captured allocation traffic by 12,664,832 bytes. The rolling
`MTLDevice.currentAllocatedSize` maximum/last values
(166,100,992/155,353,088 bytes) remained within normal fresh-process variation
of the two-image capture (161,824,768/151,027,712 bytes), so this is a resource
and bandwidth reduction rather than a claimed driver-working-set reduction.
All 51,072,310 bytes of raw trace data were deleted after compact export.

While that core-backend integration remains open, the transitional path owns one
serialized mapped transfer buffer for both swapchain images instead of one per
image. It removes exactly
`align_up(width * 4, 256) * height` bytes of mapped native allocation (7.91 MiB
at 1920x1080). IOSurface dictionary construction also frees each temporary
unmanaged UTF-8 key immediately, eliminating six native leaks per surface
creation. The path is enabled only when Avalonia advertises automatic
synchronization for the selected image handle. The macOS Skia importer
advertises timeline semaphores instead and rejects automatic snapshots; a
runtime capture found roughly 303 exceptions in one sample window before this
gate was added. macOS therefore uses the custom-visual fallback until the
typed Dawn fence/value exchange is implemented.

## Rectangular PathAtlas refinement (2026-07-26)

The earlier generation-safe shrink used independent power-of-two reductions,
matching the broad budget-and-eviction lessons from Skia and WebRender but
leaving excess residency for asymmetric live path sets. The implementation now
adapts those same bounded-cache principles to ProGPU's rectangular R8 atlas:
after the coarse probe, it evaluates deterministic 256-texel reductions in
both axis orders and accepts the lowest-area packing only when it removes at
least 25% of the current texture.

This is an original ProGPU packing policy rather than an implementation copied
from another engine. The existing retained-scene generation contract remains
the correctness boundary: a successful repack advances both atlas generation
and texture revision, discards stale UVs, and rerasterizes every live entry
before rendering. Exact search is deliberately rejected for this maintenance
path; the bounded heuristic runs only after 240 stable frames.

The source-built ControlCatalog Composition page reduced PathAtlas residency
from 8,388,608 to 4,587,520 bytes (-45.31%), with an identical 3,801,088-byte
decrease in ProGPU's Metal counter and no measurable FPS change (119.98 FPS).
The full retained/flattened pixel matrix and the focused asymmetric-atlas
regression passed. Compact evidence is retained under
`artifacts/avalonia-rectangular-path-atlas-controlcatalog-20260726` and
`artifacts/avalonia-rectangular-path-atlas-retained-pixels-20260726`.

## Bounded direct mask rendering (2026-07-26)

The previous geometry/opacity-mask implementation already stored coverage in
bounded R8 textures, but rendered every non-full-target mask into an additional
full-frame R8 scratch texture and copied the bounded rectangle afterward.
Direct2D's content-bounds contract and WebRender's bounded cache-mask tasks
support making the bounded destination the render target itself. Vello's
minimal-intermediate principle reinforces removing the redundant surface.
Skia's device-clip and layer-bounds behavior reinforces retaining explicit
device-space bounds and quick rejection.

ProGPU now remaps its existing full-target projection into the bounded mask
attachment with one fixed-work matrix adjustment per pass. A reusable typed
uniform resource is owned by each pooled mask texture; no runtime reflection,
per-frame resource creation, or shader-source generation is involved. Fragment
sampling adds the mask attachment's world-space origin, and clip scissors are
intersected in full device space before translation to local attachment
coordinates. Nested masks therefore preserve the same world-coordinate
sampling contract.

Adopted:

- render directly to the content-bounded R8 mask texture;
- retain mask resources by typed texture ownership and reuse them across
  frames;
- preserve full-scene geometry/brush coordinates and translate only projection,
  fragment origin, and scissors;
- keep the existing generation/lifetime contract for sampled mask textures.

Rejected:

- a full-frame scratch texture plus texture-to-texture copy;
- mutating one shared uniform buffer between passes in the same submission;
- changing shaped glyph data or text layout to accommodate mask coordinates.
  HarfBuzz and Parley remain reusable CPU shaping/layout results, and the
  optional HarfBuzz comparison lane is unchanged.

On the 600-frame source-built Composition workload, tracked intermediate
texture bytes fell from 3,369,728 to 92,928, the explicit Metal counter fell
from 50,823,168 to 47,415,296 bytes, and allocation fell from 5,453 to 5,322
bytes/frame. FPS remained refresh-rate equivalent at 119.96 versus 119.98.
The full retained/flattened mask, clip, effect, text-option, and BitmapCache
pixel matrix passed.

The first steady Instruments follow-up exposed a transport regression rather
than a retained texture: updating the new mask uniform with
`QueueWriteBuffer` created 51 transient 128 KiB wgpu staging buffers in five
seconds. The [wgpu queue upload contract](https://docs.rs/wgpu/latest/wgpu/struct.Queue.html)
documents that native queue writes use temporary staging storage, while the
[WebGPU mapping rules](https://gpuweb.github.io/gpuweb/#buffer-usage) permit
the existing `MAP_WRITE | COPY_SRC` ring used by ProGPU's encoded scene
uploads. Mask uniforms now join that same frame upload batch before copy
encoding. This retains one distinct destination uniform buffer per mask, so
multiple passes cannot alias values, while removing the native queue-write
allocation path. It does not alter scene compilation, shaping, atlas keys,
DPI/subpixel behavior, fallback fonts, or device-loss invalidation.

The matched after-change Metal trace contained zero resource allocations in
the steady window, zero spills, hangs, and command-buffer errors. The explicit
Metal counter dropped by one 128 KiB staging block, from 34,308,096 to
34,177,024 bytes. Compact evidence is retained under
`artifacts/avalonia-package-stream-instruments-20260726` and
`artifacts/avalonia-mask-uniform-upload-instruments-20260726`; all raw trace
bundles were deleted after export.

The next forced-GC ownership pass found that cold retained-scene state was
being allocated on every mirrored visual even when it was never used.
Following the retained display-list separation already studied in
[WebRender](https://searchfox.org/mozilla-central/source/gfx/wr/webrender)
and [Vello](https://github.com/linebender/vello), ProGPU now keeps topology,
transform, invalidation, and command ownership eager while allocating optional
animation dictionaries, leaf child lists, adorner-clip scratch lists,
specialized drawing buffers, and retained-resource lease lists only on first
use. This is an original typed ownership change; it does not adopt foreign
source structure and introduces no reflection or runtime probing.

Lookup, traversal, and synchronization remain `O(V)` for `V` mirrored visuals.
The first mutation of an optional collection is amortized `O(1)`, and
unchanged frames allocate none of these cold objects. Public collection
semantics, invalidation propagation, scene page/cache keys, shaping, DPI and
subpixel policy, atlas generations, and device-loss handling are unchanged.

On the same 120-warmup/600-frame Composition workload, forced-GC managed
retention fell from 22,324,280 to 21,970,776 bytes (-353,504 bytes,
-0.337 MiB). The remaining matched gap to the original Skia lane is
6,298,864 bytes (6.007 MiB), down from 6.344 MiB. Throughput remained
refresh-rate equivalent at 120.15 FPS, allocation was 5,469 bytes/frame,
the retained scene held 738 nodes with zero fallback nodes, and the explicit
Metal counter stayed at 34,177,024 bytes.

The exact-binary Xcode follow-up observed zero Metal resource allocations,
graphics-compiler spills, hangs, and command-buffer errors in its steady
window. The Allocations template did not expose an exportable allocation
table, so the managed improvement is claimed from the induced-GC benchmark,
not inferred from Instruments. All 2,463 core tests and the complete
retained/flattened pixel matrix passed. Compact evidence is retained under
`artifacts/avalonia-retained-lazy-state-controlcatalog-20260726`,
`artifacts/avalonia-retained-lazy-state-instruments-20260726`, and
`artifacts/avalonia-retained-lazy-state-pixels-20260726`. The three raw
Instruments traces were deleted after export, reclaiming 209,235,746 bytes.
After the resolved summary was written, 9,291,069 bytes of derivation-only
TOC/XML exports were also deleted, for 218,526,815 bytes reclaimed in total.

The package lifecycle gate now includes a real NativeAOT run. Initial
publication succeeded but the native executable demonstrated that Silk.NET's
ordinary assembly discovery had been trimmed first for windowing, then for
input. ProGPU now registers both concrete GLFW platforms through Silk.NET
2.23.0's public typed `GlfwWindowing.RegisterPlatform()` and
`GlfwInput.RegisterPlatform()` APIs before window creation. This preserves
the same Silk.NET backend and public Avalonia startup contract while removing
reflection discovery from the selected path.

The exact isolated restore verified the SHA-512 identity of Avalonia, both
integration packages, and all eight ProGPU runtime packages, published a fully
trimmed 22,696,432-byte macOS-arm64 executable, and executed it. The native app
rendered 15 frames with one retained scene and zero fallback nodes. Temporary
restore, compiler, and publish data were deleted on exit. The architecture
record and primary Silk.NET/.NET sources are linked from
`docs/progpu-avalonia-replacement-package.md`.

## Same-device Avalonia composition surface

Avalonia 12's official
[`IExternalObjectsRenderInterfaceContextFeature`](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/src/Avalonia.Base/Platform/IExternalObjectsRenderInterfaceContextFeature.cs)
accepts an advertised platform-handle type and image properties, while
[`CompositionInterop.ImportImage`](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/src/Avalonia.Base/Rendering/Composition/CompositionInterop.cs)
turns that platform image into a composition-owned imported image.
[`CompositionDrawingSurface.UpdateAsync`](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/src/Avalonia.Base/Rendering/Composition/CompositionDrawingSurface.cs)
publishes an imported image without requiring a keyed mutex or semaphore when
the producer and consumer already share one ordered device queue.

ProGPU adopts that public contract for the direct renderer. The handle value is
an opaque, process-local registry token, never a native `WGPUTexture` pointer.
Import validates the advertised handle type and dimensions, then acquires a
typed reference-counted texture lease. The embedded sample compositor renders
straight into that texture on the same `WgpuContext`; Avalonia's ProGPU drawing
context samples the same texture after queue ordering, with no CPU readback,
IOSurface copy, reflection, Objective-C inspection, or second Dawn device.
Owner disposal removes the token immediately while outstanding imported-image
leases keep the texture alive until composition releases them.

This adapts Avalonia's external-object lifetime boundary to an intra-ProGPU
same-device transfer. It rejects implementing Avalonia's intentionally
non-client-implementable shared-context marker, casting opaque WebGPU handles,
or treating a process registry token as an inter-process native handle.

The Avalonia-hosted sample benchmark now fails closed unless both the outer
Avalonia renderer and embedded compositor produce ProGPU draw calls, the
embedded backend is Silk.NET native WebGPU, presentation is
`SameDeviceTexture`, and the outer retained scene has zero fallback nodes.
All eight sample pages passed that gate in fresh processes. Across the ProGPU
text-shaping lane they averaged 118.159 FPS and 8.523 ms/frame; seven pages
allocated 4.05–10.77 KiB/frame, while the animated Charting page allocated
19.46 KiB/frame after its focused allocation cleanup.

The same eight-page matrix also passed with `--harfbuzz`. Its first aggregate
was 120.116 FPS versus 118.159 FPS for ProGPU, but the difference came from one
109.928 FPS ProGPU Glyphs launch. Three additional fresh-process pairs measured
ProGPU at 119.732–120.199 FPS and HarfBuzz at 119.410–120.456 FPS, with the same
49,741,824-byte Metal counter. The outlier is therefore launch/process noise,
not a repeatable shaping regression. ProGPU retained about 0.45 MiB more
managed memory on that glyph-heavy page; allocation/frame overlapped between
the two lanes and remains a separate ownership-optimization target.

## Reflection-free image-brush contract

Avalonia 12 intentionally keeps `IImageBrushSource.Bitmap` internal. The
exact-source lane now binds that member directly through the pinned,
strong-named friend-assembly contract and compiles only against the patched
Avalonia implementation assemblies. A dedicated MSBuild reference-selection
target prevents Avalonia's NuGet private-API target from adding a second,
unpatched assembly identity to that compilation.

The ordinary NuGet lane no longer probes `Bitmap` or `IRef<T>` with
`PropertyInfo`. It uses only public `Bitmap`/`WriteableBitmap` operations and
caches an immutable ProGPU snapshot; mutable sources refresh when their
retained drawing command is rebuilt. This compatibility path is `O(P)` for
the first snapshot of `P` pixels and `O(1)` for subsequent immutable-brush
lookups. The pinned exact-source lane remains `O(1)` and zero-copy.

The metadata contract inspector passes for `Avalonia.ProGpu`,
`Avalonia.SilkNet`, and `ProGPU.Avalonia`. Diagnostic exception labels in the
legacy host also use `HResult` instead of `GetType().Name`, leaving no runtime
reflection TypeRefs in any of the three integration assemblies.

## Typed device-loss recovery gate

The [WebGPU device-loss contract](https://www.w3.org/TR/webgpu/#dom-gpudevice-lost)
defines loss as a terminal state for the affected device. The current
[wgpu-native C API](https://github.com/gfx-rs/wgpu-native/blob/trunk/ffi/wgpu.h)
provides the device-loss callback at device creation. Microsoft's
[Win2D recovery guidance](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost)
recreates the device and all device-owned resources, and the
[Direct3D 11 recovery sequence](https://learn.microsoft.com/en-us/windows/uwp/gaming/handling-device-lost-scenarios)
releases the old swap chain/resources before returning to the render loop.

ProGPU adopts those observable contracts through its existing typed
boundaries:

- a monotonic device-loss generation invalidates every existing
  `WgpuContext`; a replacement context starts on the new generation;
- normal `Destroyed` callbacks are ownership completion, not device loss;
- lost contexts are excluded from active-context/surface lookup and
  multi-window device sharing;
- `SkiaContext.IsLost` is updated lock-free, allowing Avalonia's renderer
  manager to dispose and recreate the backend without platform graphics;
- the Silk.NET window creates a replacement device and surface before its
  next paint, then disposes the old device-bound caches; a lost device skips
  the unsafe idle wait;
- Dawn reports the same typed loss event, so the compositor contract is
  backend-independent.

The pinned Avalonia source manager previously checked only
`IPlatformGraphicsContext.IsLost`. That misses render backends, including
ProGPU/Silk.NET, which intentionally use no separate `IPlatformGraphics`.
The exact-source replacement now also checks
`IPlatformRenderInterfaceContext.IsLost`, matching that interface's explicit
thread-safe contract. A source regression verifies disposal and recreation
with `graphics: null`; ProGPU unit regressions verify generation behavior,
normal-destruction filtering, Avalonia backend reporting, and recovery-before-
paint ordering.

This is an original, reflection-free recovery path. It adapts the
device/resource invalidation model but does not copy foreign implementation
structure. The isolated Dawn IOSurface probe additionally uses Dawn's typed
native `wgpuDeviceForceLoss` diagnostic entry point, verifies that the real
native callback marks the ProGPU context lost, then creates a healthy
replacement device. This qualifies the native callback and replacement
boundary without destabilizing the machine through a system-wide driver
reset. wgpu-native has no equivalent exported force-loss entry point, so its
deterministic state-machine tests remain the safe qualification lane.

The macOS qualification passed: the shared IOSurface advanced its Metal
timeline from 4 to 5, Dawn delivered the forced native loss callback with the
diagnostic message, the existing `WgpuContext` became lost, and a newly
created Dawn context reported initialized and healthy.

## Typed multi-window disposal-order validation

Avalonia's top-level lifecycle requires each window to own its platform
surface while shared rendering resources remain valid until the last device
domain borrower releases them. ProGPU validates that contract through public,
typed package APIs: `WindowImpl.HasActiveWebGpuContext`,
`WindowImpl.SharesWebGpuDeviceWith`, and the existing disposal task. No native
handle inspection, reflection, event interception, or assembly probing is
used.

The package-only sequence disposes the original shared-device owner before a
surviving borrower, then creates and disposes another borrower while the
survivor remains open. The exact replacement stack rendered 24 frames before
owner disposal, 22 afterward, and 20 after borrower disposal; both device
identity checks passed, one retained scene remained active, and no compositor
node used the flattened fallback.

Matched Xcode Metal traces then showed 32.54 MB
`MTLDevice.currentAllocatedSize` and 34.49 MB of explicit live resources for
the final small survivor, versus 62.65 MB and 64.60 MB for a maximized
one-window package baseline. The stable 163.4–166.0 MiB macOS
`owned unmapped (graphics)` region is therefore AGX high-water/residency, not
accumulated disposed-window textures. This adopts Direct2D/Win2D-style
resource-domain lifetime semantics through ProGPU's original typed ownership
model while rejecting global-owner lifetime assumptions and runtime
compatibility hooks.

## Avalonia Native WebGPUSharp presentation lane (2026-07-26)

Avalonia windowing and ProGPU rendering are now independently selectable. On
macOS, `UseAvaloniaNative().UseProGpu()` obtains Avalonia's typed
`IMetalPlatformSurface`, imports each drawable IOSurface into the exact
WebGPUSharp/Dawn device, and renders the ProGPU compositor directly into that
texture. Avalonia continues to own NSWindow, CAMetalLayer, input, screens,
clipboard, accessibility, and presentation. Silk.NET windowing and its
`wgpu-native` surface are not involved.

The implementation follows these primary contracts:

- Apple exposes an IOSurface-backed Metal texture through
  [`MTLTexture.iosurface`](https://developer.apple.com/documentation/metal/mtltexture/iosurface).
- Dawn's
  [`SharedTextureMemoryMTL`](https://dawn.googlesource.com/dawn/+/5e7097e945bbe4b35361d83438c6fbbb62748e62/src/dawn/native/metal/SharedTextureMemoryMTL.mm)
  imports an IOSurface and exports an `MTLSharedEvent` when exclusive access
  ends.
- Metal texture usage and storage choices affect compression and residency;
  Apple's
  [texture optimization guidance](https://developer.apple.com/documentation/metal/optimizing-texture-data)
  treats those choices as an explicit performance contract.
- Apple identifies the lossless BGRA IOSurface FourCC as `&BGA` in
  [TN3121](https://developer.apple.com/documentation/technotes/tn3121-selecting-a-pixel-format-for-an-avcapturevideodataoutput).

The frame protocol is fully typed and contains no runtime reflection, symbol
lookup, or private Dawn object inspection:

1. Avalonia begins one native Metal rendering session and supplies its
   `MTLTexture`.
2. Compile-time Objective-C ABI calls read the public `iosurface`, `width`,
   `height`, and `pixelFormat` properties.
3. A bounded four-entry cache reuses one Dawn shared-memory object and texture
   wrapper per CAMetalLayer drawable.
4. Dawn begins exclusive access, ProGPU submits the compositor render pass
   directly to the imported texture, and Dawn ends access.
5. Avalonia's Metal queue waits for Dawn's exported shared-event value before
   presenting, signals the consumer value afterward, and the next Dawn access
   waits for that value.

There is no full-size texture copy, CPU readback, mapped staging image, or
second presentation texture. Empty retained command lists still submit the
required clear and access fence; this is necessary because a non-retained
drawable cannot reuse prior contents.

The first telemetry run exposed a separate Avalonia policy that coupled direct
rendering to static previous-frame retention. It allocated a full-window
offscreen layer for every non-retained CAMetalLayer drawable, rendered the
retained scene there, and submitted a final texture draw to Dawn. The exact
source contract now uses `IsSuitableForDirectRendering` to decide whether the
layer is needed; the existing per-session `PreviousFrameIsRetained=false`
still requests a correct full redraw. The strict profiler requires both one
native retained scene and `PresentationPath=DawnMetalIOSurface`, so the hidden
layer or a framebuffer fallback cannot pass as zero-copy.

The stock Avalonia.Native 12.0.5 CAMetalLayer produced a 2048x1600
`BGRA8Unorm` Metal texture backed by a lossless-compressed `&BGA` IOSurface on
the test system. WebGPUSharp 0.5.5/Dawn reported an undefined zero-sized shared
texture for that storage format. The exact-source lane therefore sets
`CAMetalLayer.framebufferOnly = false` at native build time, which produces an
ordinary BGRA IOSurface that the pinned Dawn binary imports successfully.
`tools/build-avalonia-native-dawn.sh` builds that exact-source native
allocation change in an isolated Xcode DerivedData directory, installs the current-
architecture dylib into the ControlCatalog output, signs it ad hoc, and
deletes all intermediate build data. The strict validation lane fails if the
source contract or import is absent. Package compatibility may instead pass
`--allow-dawn-presentation-fallback`; the failed Dawn target and its device are
released before the existing framebuffer target is created.

The longer-term queue contains two measured alternatives rather than assuming
that disabling framebuffer-only storage is always optimal:

- add and upstream Dawn Metal shared-texture support for Apple's lossless
  `&BGA` drawable format, then compare bandwidth, IOSurface/IOAccelerator
  residency, and frame-time percentiles against uncompressed BGRA;
- retain the exact-source uncompressed allocation as the deterministic
  shipping lane if compressed import is unavailable or regresses correctness.

Windows remains a typed D3D shared-handle/fence lane and Linux remains a typed
Vulkan dma-buf/synchronization-fd lane. Neither will cast handles between
unrelated WebGPU devices or add a readback path disguised as zero-copy.

The reusable ControlCatalog profiler now accepts
`source-progpu-native` and `source-progpu-native-harfbuzz`. It builds the exact
Avalonia source compositor, installs the exact native library, launches
`--native-windowing` in fail-closed Dawn mode, validates zero retained-scene
fallback nodes, and records target dimensions/DPI alongside FPS, allocations,
process memory, atlases, intermediate textures, and native resource counters.

A matched Buttons run used 120 warm-up and 300 measured frames:

| Lane | FPS | Allocated/frame | Resident | Physical footprint | First ProGPU frame |
| --- | ---: | ---: | ---: | ---: | ---: |
| Avalonia Native + Dawn + ProGPU | 118.570 | 3,536 B | 206.33 MiB | 379.72 MiB | 1,201.25 ms |
| Silk.NET + wgpu-native + ProGPU | 120.392 | 3,979 B | 215.66 MiB | 317.64 MiB | 1,001.82 ms |
| Avalonia Native + Skia/HarfBuzz | 119.968 | 5,950 B | 202.66 MiB | 262.99 MiB | n/a |

All lanes were refresh-limited. Native Dawn was 1.51% below the matched Silk
lane, allocated 443 fewer managed bytes/frame, and used 9.33 MiB less resident
memory, while its physical footprint remained 62.08 MiB higher and its first
ProGPU frame was 199 ms later. Versus Skia it allocated 2,414 fewer bytes/frame
and used 3.67 MiB more resident memory, while physical footprint remained
116.73 MiB higher. Native and Silk both reported a 2048x1600 physical target.
After removing the hidden layer both reported the same 1608-pixel peak path
raster height and zero tracked intermediate texture bytes; Native/Dawn
reported one 789-node retained scene with zero fallback nodes.

Matched Xcode Allocations, Time Profiler, and Metal System Trace captures
after direct rendering attributed 20.89 MiB of live IOAccelerator storage and
50.00 MiB of IOSurface storage to the Native/Dawn process, not the previously
suspected roughly 210 MiB active Metal leak. Persistent heap plus anonymous VM
was 217.28 MiB. Removing the hidden layer reduced that aggregate by 21.65 MiB,
heap payload by 15.44 MiB, anonymous VM by 6.20 MiB, and IOAccelerator
residency by 16.95 MiB versus the prior Native/Dawn capture. IOSurface storage
increased by 12.50 MiB because this capture observed four 2048x1600
QuartzCore surfaces instead of three; it is a visible target-pool cost, not a
ProGPU intermediate texture.

The Metal trace recorded 214 submissions, 431 completions, 54 drawable waits
totalling 213.606 ms, zero compiler spills, potential hangs, hang risks, or
command-buffer errors. Relative to the earlier Silk Instruments baseline,
direct Native/Dawn still added 36.09 MiB heap/anonymous VM, 6.89 MiB
IOAccelerator storage, and 25.00 MiB IOSurface storage. Compact evidence
remains under `artifacts/avalonia-dawn-direct-instruments-20260726`; the tool
deleted about 371 MiB of raw traces, Xcode scratch, and XML exports after
writing a 44 KiB summary. The remaining physical-footprint delta is an
optimization target, not evidence of a 210 MiB live Metal allocation.

## Native Dawn HWND/Xlib presentation follow-up (2026-07-26)

The non-Silk design no longer depends on a missing cross-device WebGPU import
API on Windows or Linux. When ProGPU owns the Dawn device, the shortest path is
to let Dawn own presentation too while Avalonia retains windowing and platform
services. Avalonia 12.0.5 already exposes the required typed surface objects:
Win32 includes an `INativePlatformHandleSurface` with `HWND`, and X11 includes
one with `XID`. Avalonia's own Vulkan factories consume those same contracts.

The design used these primary sources:

- Avalonia's official
  [`INativePlatformHandleSurface`](https://docs.avaloniaui.net/api/avalonia/platform/inativeplatformhandlesurface)
  API and the pinned 12.0.5
  [Win32 surface list](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Windows/Avalonia.Win32/WindowImpl.cs)
  and
  [X11 surface list](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Avalonia.X11/X11Window.cs);
- WebGPUSharp 0.5.5's exact
  [surface descriptors and sample](https://github.com/EmilSV/WebGPUSharp/blob/9a750346ff77a25eb671f630797b62100a9de926/README.md)
  plus its
  [`compatibleSurface` adapter contract](https://github.com/EmilSV/WebGPUSharp/blob/9a750346ff77a25eb671f630797b62100a9de926/gen/RequestAdapterOptionsFFI.cs);
- Dawn's
  [typed Windows, Xlib, and Wayland surface ownership](https://dawn.googlesource.com/dawn/+/55623705bef897b77888c3c9410c94cbaa3c1e4e/src/dawn/native/Surface.cpp);
- Microsoft's
  [DXGI HWND swapchain guidance](https://learn.microsoft.com/windows/win32/api/dxgi/nf-dxgi-idxgifactory-createswapchain)
  and Khronos'
  [Vulkan Xlib WSI contract](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html).

Adopted: select D3D12 or Vulkan against the target surface before requesting
the device; query formats and alpha modes; configure FIFO presentation at the
physical pixel size; acquire the swapchain texture, render the retained scene
directly, and present. X11 owns a bounded display connection because Avalonia's
public surface contract intentionally exposes only the XID. Rejected: importing
DXGI/dma-buf memory from another graphics device, CPU framebuffer readback,
full-window blits, reflecting into Avalonia platform internals, and treating a
Wayland descriptor as usable before Avalonia ships a corresponding typed
windowing surface.

The portable implementation cross-builds on macOS for all target APIs and has
focused descriptor tests. The strict ControlCatalog profiler now expects
`DawnD3D12HWND` on Windows and `DawnVulkanXlib` on Linux, and CI runs a short
X11/Mesa Vulkan telemetry smoke plus Windows compilation. Software Vulkan CI
is a correctness gate, not a hardware performance comparison. The already
qualified macOS non-Silk lane was re-run after the generalization: Buttons
completed with both shapers at 119.75-119.79 FPS, zero retained fallback nodes,
zero tracked intermediate texture bytes, and
`PresentationPath=DawnMetalIOSurface`.

## Same-device embedded-texture allocation correction (2026-07-26)

EventPipe allocation stacks exposed repeated `InvalidOperationException`
creation in `DrawingContextImpl.ResolveBitmapTexture`. A texture created by
the shared native WebGPU device was rejected when the render target used a
different `WgpuContext` wrapper for that same device. Avalonia caught and
logged the exception every frame, masking a correctness error as roughly
38-46 KiB of managed allocation per frame on Designer and Charting.

The WebGPU object validity rule is device identity, not wrapper identity:
[`GPUObjectBase`](https://gpuweb.github.io/gpuweb/#gpuobjectbase) records an
owning device, and an object is valid for a target when those devices match.
Dawn's
[`implicit_device_sync`](https://dawn.googlesource.com/dawn.git/+/cf54bb8c2aa971b67329b0d90923c23e0f8d6baa/docs/dawn/features/implicit_device_sync.md)
documents device/queue multithread access while command encoders remain
externally serialized.

The fix gives all wrappers in one typed device domain the same render lock and
accepts textures when `SharesDeviceWith` is true. Unrelated devices remain a
hard failure. After that correction, bounded retained label-measurement caches
removed the actual steady Chart legend and Designer diagnostic-pill shaping
work. Three repeated 600-frame runs reduced Charting to 5.68-5.87 KiB/frame
and Designer to 6.58-6.68 KiB/frame while both remained near 120 Hz.

The final alternating all-sample matrix contains 32 successful fresh
processes: eight samples, two shapers, and two runs, each with 120 warm-up and
300 measured frames. Aggregates were:

| Signal | HarfBuzz | ProGPU shaper | ProGPU - HarfBuzz |
| --- | ---: | ---: | ---: |
| Mean FPS | 119.974 | 120.014 | +0.040 (+0.033%) |
| Mean frame | 8.3905 ms | 8.3876 ms | -0.0029 ms |
| Mean page p95 | 10.4506 ms | 10.3088 ms | -0.1418 ms |
| Mean page p99 | 11.7945 ms | 11.2843 ms | -0.5102 ms |
| Allocated/frame | 4,807.9 B | 4,830.8 B | +22.8 B |
| Managed retained | 39.10 MiB | 39.63 MiB | +0.53 MiB |
| Resident | 329.70 MiB | 325.44 MiB | -4.26 MiB |
| Physical footprint | 434.93 MiB | 432.45 MiB | -2.48 MiB |
| Mean compile | 0.61097 ms | 0.59150 ms | -0.01947 ms |
| Mean render | 0.21440 ms | 0.20750 ms | -0.00690 ms |
| First frame | 1008.07 ms | 1047.97 ms | +39.90 ms |

These short refresh-limited runs establish equivalence and catch tail/allocation
regressions; the small deltas are not presented as universal speedups. Compact
evidence is retained under
`artifacts/avalonia-samples-final-repeated-20260726`,
`artifacts/avalonia-chart-legend-cache-20260726`,
`artifacts/avalonia-designer-pill-cache-20260726`, and
`artifacts/avalonia-sample-device-domain-after-20260726`.
