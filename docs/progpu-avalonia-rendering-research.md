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
- The clean-room effect-scope replacement keeps the same typed design without
  retaining the imported recorder structure. Its scope stack is lazy, so an
  ordinary drawing context owns no effect collection; reset/dispose discard
  incomplete subtrees transactionally. Six new assertions cover blend
  identity, radius-to-sigma/padding conversion, retained blur recording, and
  unbalanced drop-shadow cleanup. The exact-source retained/flattened matrix
  passes all nine pages and the dedicated blur/drop-shadow fixtures with zero
  fallback nodes.
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
- the WebGPU native
  [surface capability negotiation contract](https://webgpu-native.github.io/webgpu-headers/Surfaces.html)
  and Dawn Vulkan's current
  [opaque-alpha swapchain requirement](https://dawn.googlesource.com/dawn/+/41e4d9a34c1d9dcb2eef3ff39ff9c1f987bfa02a/src/dawn/native/vulkan/SwapChainVk.cpp);
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

The Linux qualification run also caught an alpha-mode difference that the
portable descriptor tests could not expose: Avalonia's software-rendering
fallback selected its 32-bit transparent X11 visual, whose Vulkan surface
advertised premultiplied alpha, while Dawn's non-Android Vulkan swapchain
requires an opaque-capable visual. ProGPU now implements Avalonia's typed
`IPlatformRenderInterfaceNativeSurfaceFeature`; X11 observes that feature
before window creation and selects the system default 24-bit visual. Surface
alpha selection is also backend-aware: Vulkan/Xlib requires and selects
`Opaque`; D3D12 and Metal keep the premultiplied preference and fall back
through the advertised capabilities. Both decisions are bounded startup work
and add no frame-path work, device import, intermediate texture, or allocation.

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

## Context-owned Avalonia composition backend (2026-07-26)

The earlier retained-tree feature lived on a leased drawing context and kept
its target-scene registry in the offscreen cache. That proved the rendering
model but left scene lifetime at the wrong boundary. The next clean-room slice
uses a new internal `ICompositionServerBackend` resolved once from Avalonia's
platform render-interface context. `ServerCompositor` invokes it before the
old drawing-context feature, and `ProGpuCompositionServerBackend` owns one
scene per target until target destruction, render-target corruption, context
loss, or context disposal.

The design reused the production-engine comparison recorded earlier in this
document and checked these primary sources at the ownership boundary:

- Avalonia 12.0.5's pinned
  [`PlatformRenderInterfaceContextManager`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Avalonia.Base/Rendering/PlatformRenderInterfaceContextManager.cs),
  [`ServerCompositor`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.cs),
  and
  [`ServerCompositionTarget`](https://github.com/AvaloniaUI/Avalonia/blob/fee9c561ce036e8a3e8cee2397c75ca599b4790d/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionTarget.cs)
  establish graphics-context, batch, target, damage, readback, and disposal
  ordering;
- WebRender's
  [rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  separates the retained scene/backend from per-frame renderer work;
- Vello's
  [renderer API](https://docs.rs/vello/latest/vello/struct.Renderer.html) and
  [scene API](https://docs.rs/vello/latest/vello/struct.Scene.html) separate
  persistent scene construction from device-owned rendering resources;
- the already-recorded Skia/SkParagraph, DirectWrite/Direct2D, Parley, and
  HarfBuzz sources continue to support reusable CPU shaping/layout with
  device-owned raster/cache state. This slice does not move shaping,
  fallback-font selection, or line layout onto the GPU.

Adopted: one typed backend per graphics context, persistent per-target scene
state, explicit target/context teardown, generation-based incremental
synchronization, and a transient typed encoder for the current render pass.
Adapted: Avalonia remains the protocol host for atomic batches, animations,
custom messages, completion, readback, and damage while ProGPU owns the render
scene. Rejected: a process-global scene registry, drawing-context lifetime as
scene lifetime, reflection-based service discovery, runtime detours, and
silently accepting the old flattened traversal in strict lanes.

Fresh 30-warm-up/60-measurement Buttons processes validated both presentation
paths. Silk.NET produced 121.47 FPS and Avalonia Native/Dawn produced
120.23 FPS; both recorded 90 backend renders, one 789-node scene, zero
fallback nodes, and zero tracked intermediate-texture bytes. The run is
refresh-limited and establishes path/lifetime correctness rather than a speed
claim. A package-only multi-window test recorded 66 backend renders across two
scenes, then preserved rendering after disposing first the shared-device owner
and later a borrower.

The paired Xcode Allocations, Time Profiler, and Metal System Trace capture
used the reusable profiler's bounded final window and deleted 118.29 MiB of raw
trace bundles, 110.28 MiB of Xcode scratch, and 28.34 MiB of XML exports after
writing compact summaries. Allocations reported 199.32 MiB persistent native
heap plus anonymous VM: 36.08 MiB heap payload and 163.23 MiB anonymous VM.
The largest GPU/window allocations were two QuartzCore IOSurfaces totaling
25.00 MiB and 14.06 MiB of live IOAccelerator storage. The capture reported
zero compiler spills, potential hangs, hang risks, or command-buffer errors.
This attribution remains consistent with bounded startup/driver pools, not a
210 MiB live Metal leak. Because this short Metal export observed completions
but no allocation/submission rows in its final window, it is not used to infer
per-frame GPU throughput.

The package build exposed two contract regressions and both were fixed before
acceptance. The native surface-selection feature had been public, which added a
type to `Avalonia.Base`; it is now internal and strict ApiCompat passes against
the official 12.0.5 assembly identity. A diagnostic
`surface.GetType().Name` emitted a `System.Reflection.MemberInfo` type
reference; replacing it with a typed constant removed runtime reflection, and
the final packed renderer/windowing metadata audit passes.

### Compact retained-visual delta classification

The next compositor slice replaces the undifferentiated changed-visual signal
with an internal byte-sized flags contract. The stable 64-bit retained visual
identity remains the handle, while `Transform`, `Bounds`, `Appearance`,
`Content`, `InheritedDrawingOptions`, and `Topology` described which parts of
the ProGPU mirror need synchronization. Flags coalesce in-place until the
target revision is accepted, so publishing and merging one change are `O(1)`
and allocation-free. A transform-only update now changes only the ProGPU
transform; a bounds-only update changes only local bounds. At this
intermediate stage, appearance changes retained full typed state
synchronization, while content changes retain command
revision recording, and topology remains a transactional full-tree update.
Inherited render/text options currently force a full `O(V)` synchronization
for `V` target visuals so descendants cannot retain stale effective options;
the later direct protocol will replace that conservative step with an explicit
descendant generation.

This adapts the already-researched WebRender epoch/property-binding split,
Vello retained scene encoding, DirectComposition property animation model,
and Skia/Graphite recorder/resource separation to Avalonia's existing atomic
batch state machine. It rejects an untyped property bag, reflection-based
dispatch, and copying Avalonia's generated serializer layout into ProGPU.
Avalonia still evaluates its animations and computed properties; the mask is a
clean-room typed boundary that lets the ProGPU owner consume the minimum safe
state until direct animation channels are implemented.

Two new pinned-source tests prove exact transform-only classification and
inherited text-option classification. Together with the delayed-parent and
draw-list revision tests, the focused contract passes 4/4. A fresh Release
Buttons run completed 90 server-backend frames at 123.09 FPS, with one
789-node scene, 89 incremental synchronizations, zero fallbacks, and zero
tracked intermediate-texture bytes. The animated Composition page completed
90 backend frames at 121.14 FPS, with one 739-node scene, 88 incremental
synchronizations, zero fallbacks, one custom visual, and 92,928 tracked
intermediate-texture bytes. These short runs validate the path and delta
lifetime; their refresh-limited FPS and fresh-process footprint are not used
as a causal performance claim.

This is still not the final compact protocol runtime. Avalonia server objects
currently retain property storage, apply deserialized changes, recompute
properties, and provide the changed-node list consumed during mirror
synchronization. The next architecture slice is generation-checked paged
ProGPU handles updated directly during deserialization, followed by compact
animation channels, while preserving Avalonia's existing batch/readback
ordering and public assembly contract.

### Generation-checked retained-state handles

The first compact-runtime step replaces the ProGPU mirror's
`Dictionary<long, Visual>`, full-sync visited hash set, and stale-node scratch
list with a ProGPU-owned paged state store. Each Avalonia server visual carries
only a primitive backend-owner value and a 64-bit handle. The handle combines
a slot index with a generation; lookup also validates the stable retained
identity. Reusing a released slot therefore cannot make a stale handle refer
to a new visual. Allocation, incremental lookup, and release are `O(1)`.
Full synchronization and stale-state collection remain `O(V)` for `V`
allocated slots, but use a linear page scan and one synchronization generation
instead of hashing every visited identity or allocating stale-node storage.
Pages contain 256 inline slots and grow only when the live high-water mark
crosses a page boundary.

This design is informed by the following primary sources:

- [WebRender's current scene-building and cache source](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src)
  separates a serialized display list, retained scene construction, interned
  resources, and epoch-validated cache residency;
- [DirectComposition basic concepts](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts)
  define stable ordered visuals whose property/content changes become visible
  atomically at commit;
- [Skia Graphite `Recorder`](https://skia.googlesource.com/skia/+/d78564aad21d/include/gpu/graphite/Recorder.h)
  owns recorder-local task and resource-cache state and exposes explicit
  resource cleanup;
- [Vello](https://github.com/linebender/vello) encodes a retained scene for
  compute-oriented rendering while minimizing transient buffers;
- [Parley](https://docs.rs/parley/latest/parley/) reuses shared font/layout
  contexts and layout scratch, and
  [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  keep shaping plans reusable outside compositor-resource ownership.

Adopted: stable resource identity, generation validation, owner-local state,
linear packed traversal, explicit cleanup, and retained CPU shaping results.
Adapted: ProGPU uses an original C# struct-page/free-list design and two
primitive fields on the existing Avalonia ABI shell; it does not reproduce
another engine's containers, naming, serialization layout, or control flow.
Rejected: foreign source translation, object reflection, untyped property
bags, native pointers in Avalonia contracts, a process-global visual registry,
and moving Unicode/OpenType shaping into the GPU state store.

The corresponding implementation passes five dedicated handle-store
contracts, all 62 Avalonia renderer contracts, the pinned Avalonia
retained-revision tests, and 197 focused compositor/layer tests. Two matched
600-frame runs per backend kept ProGPU at 120.214 mean FPS versus Skia at
119.984 FPS across Buttons and Composition. The paged state is a managed
ownership improvement, not a native-memory claim: matched Xcode Allocations
still measured 307,192,096 bytes of ProGPU native heap plus anonymous VM
versus 223,512,608 bytes for Skia, dominated by ProGPU's
119,095,296-byte IOGPU remote-storage group.

The next compact-protocol increment removes the target's duplicate
changed-visual hash set. Each visual's existing byte-sized retained-change
state reserves one queue bit; the ordered target list adds the visual only
when that bit transitions from clear to set. Exact-revision acknowledgement
clears both the queued marker and accumulated typed change mask. Publishing,
coalescing, and acknowledgement remain allocation-free `O(1)` per change,
while consuming `C` changes remains ordered `O(C)`. This adapts
DirectComposition's atomic property visibility and WebRender's
epoch-validated retained updates to Avalonia's existing batch transaction; it
rejects an unordered property bag, reflection, and foreign serializer layout.

The focused pinned Avalonia gate passes 5/5 and the complete ProGPU core gate
passes 2,487/2,487. Three alternating fresh-process Composition runs completed
6/6 across ProGPU and Skia. ProGPU averaged 120.288 FPS, retained a median
20.96 MiB managed heap, and allocated 4.99--5.01 KiB/frame; Skia averaged
119.917 FPS, retained 15.17--15.18 MiB, and allocated 5.81--5.82 KiB/frame.
Xcode Allocations measured 188,714,864 bytes of persistent native heap plus
anonymous VM, 234,784 bytes below the prior 188,949,648-byte capture. The
0.12% process-level difference is treated as fresh-process noise; the causal
result is removal of the hash container and its growth/entry storage, not a
claimed whole-process reduction. IOAccelerator (10,158,080 bytes) and Metal
resource-list residency (835,584 bytes) were unchanged. Compact evidence is
retained in `artifacts/avalonia-inline-change-queue-profile-20260727` and
`artifacts/avalonia-inline-change-queue-instruments-20260727`; raw Xcode
traces, allocation exports, and task-specific scratch were removed after the
summary was generated.

The changed-visual list now exposes a typed
`RetainedCompositionVisualDelta` instead of a bare server-object reference.
At queue insertion it snapshots the backend owner, index-plus-generation
handle, and stable retained identity as primitives. ProGPU's normal
incremental path performs one direct page lookup with those captured values
and then checks that the page still belongs to the same transitional ABI
shell. If a slot was released/reused or ownership changed before
acknowledgement, the delta is rejected and the whole synchronization retries
transactionally. Coalesced change bits remain on the source shell for now so
repeated updates still require no queue search or allocation.

This is the next clean-room adaptation of the same primary-source principles:
DirectComposition's commit makes property changes atomically visible,
WebRender uses epochs to reject stale retained state, and Skia Graphite keeps
recorder-local resource ownership. Adopted are captured primitive identity,
generation validation, and an explicit stale-update failure path. Adapted is
the original ProGPU page-handle layout and Avalonia revision acknowledgement.
Rejected are native pointers, reflection, an object dictionary, copying a
foreign serializer, and accepting the source object's current handle after
the delta was queued.

The pinned Avalonia gate now passes 6/6, the source-contract gate passes 6/6,
the full core suite passes 2,488/2,488, and the retained/flattened pixel matrix
passes every page and specialized fixture with zero fallback nodes. Across
three alternating 120-warm-up/600-measured-frame Composition runs, ProGPU
averaged 120.257 FPS and 4.99--5.00 KiB/frame; Skia averaged 119.761 FPS and
5.68--5.82 KiB/frame. Xcode Allocations measured 189,713,984 bytes of
persistent native heap plus anonymous VM for ProGPU versus 223,791,088 bytes
for Skia: ProGPU was 34,077,104 bytes (15.2%) lower. Heap payload was
29,216,320 versus 46,843,888 bytes, anonymous VM was 160,497,664 versus
176,947,200 bytes, and IOAccelerator VM was 11,108,352 versus 16,203,776
bytes. This protocol slice is treated as a correctness prerequisite for
direct serialization rather than the cause of that process-level native
difference. Raw traces, XML exports, and Xcode scratch were deleted after the
compact summaries were produced under
`artifacts/avalonia-typed-handle-delta-*20260727`.

The paired forced-GC follow-up distinguishes heap occupancy from reachability.
The ordinary GC-dump type report counted 13,264,627 bytes for ProGPU and
9,528,901 bytes for Skia, including a ProGPU-only 1,048,576-byte array. A
root-aware heap walk proved that array unreachable and measured 11,552,234
versus 8,882,780 live bytes instead, an exact 2,669,454-byte managed gap. The
remaining delta is dominated by original ProGPU retained-scene storage:
336,984 bytes for 739 typed compositor mirror visuals, command/vector/text
arrays, and the managed font catalog. Adopted is root-filtered retention
measurement and automatic removal of raw dumps; rejected is optimizing an
unreachable buffer or relabeling it as a leak.

The next clean-room ownership slice applies the same retained-scene principle
inside ProGPU rather than changing Avalonia's public contract. Production
compositors keep common traversal state compact and allocate optional effects,
masks, animation, and cached-surface state only when used. ProGPU therefore
moved its own optional `Visual` fields into a typed lazy cold-state object and
changed `AvaloniaCompositionScene` child synchronization to edit the stable
child list in place. Adopted are hot/cold field separation and stable retained
identity. Adapted are ProGPU's existing typed owner/invalidation contracts:
the cold state is created by explicit setters and never discovered through
reflection. Rejected are a property bag, runtime field lookup, clearing cold
state between frames, rebuilding children, copying a foreign visual layout,
and weakening clip/effect/cache invalidation.

Across 739 Avalonia mirror visuals this reduced the object itself from 456 to
328 bytes and removed the duplicate synchronization list. Root-filtered live
retention was 11,641,229 bytes for ProGPU versus 9,135,365 for Skia; the
2,505,864-byte gap is 163,590 bytes smaller than the preceding root-aware
capture. Three alternating Composition runs measured 120.228 FPS and
5,114.28 bytes/frame for ProGPU versus 119.980 FPS and 5,951.69 bytes/frame
for Skia. Xcode Allocations measured 190,884,224 bytes of native heap plus
anonymous VM for ProGPU versus 223,217,424 bytes for Skia, preserving a
32,333,200-byte (14.48%) ProGPU advantage. This evidence keeps the remaining
work correctly scoped to command/vector/text arrays and the managed font
catalog rather than adding native allocation workarounds.

The complete 2,489-test core suite, the 40-test visual/layer/clip/effect gate,
the retained/flattened pixel matrix, replacement-package runtime smokes, and
NativeAOT execution pass. The root-aware profiler deletes its temporary full
heap dumps and raw GC dumps by default after compact type and live-root
reports are written.

The following compact-page slice applies retained display-list specialization
without modifying the public `CompositorDrawCall`. Incremental pages already
reject charts, extensions, static buffers, masks, and custom data; retaining
their fields nevertheless made every admitted draw 256 bytes. Adopted is a
typed 56-byte page-local vector/text/texture record. Adapted is expansion into
the existing general draw-call value at replay so pipelines and batching keep
one implementation. Rejected are changing the public draw-call ABI, an object
property bag, pointer packing, union aliasing, accepting unsupported state,
or bypassing pixel/invalidation gates.

For 141 Composition pages, live draw-call arrays fell from 39,480 to
11,280 bytes (-28,200 bytes, -71.4%). A 600-frame run remained at 120.226 FPS
and 5,107.27 allocated bytes/frame. Root-filtered ProGPU retention measured
11,609,007 bytes versus Skia's 9,135,381. Exact-binary Xcode Allocations
reported 188,709,520 native-heap-plus-anonymous-VM bytes for ProGPU, preserving
a 34,507,904-byte (15.46%) advantage over the recent matched Skia capture.
All 2,490 core tests, the complete retained/flattened pixel matrix, package
identity/reflection contracts, multi-window lifecycle, and NativeAOT runtime
gate pass.

The measured follow-up now lazily creates bounded-mask uniform bind groups by
the draw-call kinds actually present and keeps a sliding three-entry pool
after one-mask steady demand. Stable Composition ownership fell from 24 to 19
buffers, 12 to 7 textures/views, and 32 to 11 bind groups across both changes,
with the retained/flattened pixel matrix still byte-identical. Instruments
showed no causal IOAccelerator reduction because the first Composition frames
require up to 60 standalone mask surfaces before settling. This rejects
further tuning of the same per-mask texture pool as the next strategy. The
next mask architecture will avoid that startup high-water by representing
canonical rounded clips analytically in the existing fragment-mask contract,
and will evaluate atlas packing for independent non-analytic masks. Nested
mask correctness, antialiasing, DPI, transform, and retained invalidation stay
part of the pixel contract.

The full retained-pixel contract was rerun after the delta change. All nine
ControlCatalog pages and the geometry-clip, inherited text-option, conic-mask,
blur, drop-shadow, and BitmapCache variants matched the explicitly flattened
comparison byte-for-byte, with zero retained fallback nodes. The comparison
switch now suppresses the context-owned `ICompositionServerBackend` itself,
rather than merely suppressing the older drawing-context feature, so its
telemetry correctly reports zero retained scenes and backend renders. The
geometry fixture now animates its clipped root; its previous descendant was
outside the small ellipse and Avalonia correctly culled those invisible
updates before the short benchmark could capture a post-warmup frame. The
fixed retained fixture recorded 20 backend renders, one 789-node scene, 19
incremental synchronizations, and zero fallbacks; its flattened counterpart
recorded zero retained scenes while producing identical pixels.

The exact post-change binaries were then measured in alternating order for two
fresh 120-warm-up/600-measured-frame Composition runs per backend. ProGPU
averaged 120.258 FPS versus Skia's 120.011 FPS (+0.21%), 8.334 ms versus
8.349 ms mean frame time, and 5,733 versus 5,931 managed bytes allocated per
frame. Both are refresh-limited. ProGPU's maximum physical footprint was
346.00 MiB versus 299.70 MiB for Skia (+46.30 MiB), while managed retained
memory was 21.07 MiB versus 14.94 MiB. The non-Silk Avalonia Native/Dawn lane
separately completed 600 frames at 119.98 FPS with 725 typed backend renders,
one 739-node scene, zero fallback nodes, and only 1,792 bytes of tracked
intermediate textures.

Matched Xcode Allocations, Time Profiler, and Metal System Trace captures
attributed the footprint rather than treating it as a texture counter. On the
Composition workload, ProGPU retained 305,914,192 bytes of native heap plus
anonymous VM versus Skia's 223,428,496 bytes. Both retained the same
92,274,688-byte one-time dispatch-continuation reservation. ProGPU's
IOAccelerator VM was 124,534,784 bytes versus 16,007,168 bytes for Skia,
partly offset by 11,345,472 fewer native-heap bytes and 13,500,416 fewer
IOSurface bytes. A static ProGPU Buttons capture retained only 172,619,056
bytes in total and 10,977,280 bytes of IOAccelerator VM, proving that the
large Composition result is feature/workload-dependent rather than base
backend ownership.

Controlled clean-room A/B captures rejected three suspected causes. Replacing
the native mapped upload ring with the bounded queue-write fallback left
IOAccelerator VM effectively unchanged at 124,518,400 bytes. Stopping and then
removing Avalonia's custom visual also left it within fresh-process variance.
Finally, reusing oversized bounded-mask textures passed focused pixel tests
but retained 126,763,008 bytes of IOAccelerator VM and increased mask payload,
so that experiment was rejected and reverted. Allocation timestamps place the
IOAccelerator/resource-list burst inside an approximately 0.7-second startup
window; the rolling Metal capture completed 560 command buffers with zero
compiler spills, drawable waits, hangs, hang risks, or command-buffer errors.
This is bounded Metal driver/pipeline pool state, not monotonic per-frame
growth and not a ProGPU texture leak. Compact evidence is retained under
`artifacts/avalonia-compact-delta-profile-20260726`,
`artifacts/avalonia-compact-delta-dawn-profile-20260726`,
`artifacts/avalonia-compact-delta-instruments-20260726`,
`artifacts/avalonia-skia-baseline-instruments-20260726`, and
`artifacts/avalonia-progpu-buttons-instruments-20260726`; all raw traces,
Xcode scratch, and XML allocation exports were deleted after summary
generation.

### Analytic affine canonical-mask contract

The subsequent lifetime profile identified the apparent Composition memory
gap precisely: startup required 48 independent canonical sharp-rectangle clip
masks under affine transforms. Although the texture pool later retained only
three entries, constructing 48 standalone WebGPU textures and their
render-pass resources established an approximately 119 MiB IOGPU
remote-storage high-water. The workload used no rounded, general-path, or
opacity masks at that peak.

ProGPU now represents canonical rectangular and elliptical rounded clips with
an original typed affine mask record. Fragment shaders invert the physical
fragment position into clip-local coordinates, evaluate the rectangle or
appropriate per-corner ellipse, and use WGSL `fwidth` derivatives to preserve
transform-aware edge antialiasing. The operation is `O(1)` per fragment, uses
96 bytes of uniform state per simultaneously live mask, and avoids a texture
sample and mask render pass in the analytic branch. The outermost offscreen
transaction is a full render target and therefore supports the same analytic
contract; this is how Avalonia Native/Dawn presents directly into an imported
drawable. General geometry, composed/nested fallback masks, opacity masks, and
deeper layer/effect offscreen transactions keep the existing texture path.

Primary design sources are the
[WGSL derivative built-ins](https://www.w3.org/TR/WGSL/#derivative-builtin-functions),
the [WebGPU resource model](https://www.w3.org/TR/webgpu/), WebRender's
[rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
Skia Graphite's
[`Recorder`](https://skia.googlesource.com/skia/+/d78564aad21d/include/gpu/graphite/Recorder.h),
and Vello's
[scene](https://docs.rs/vello/latest/vello/struct.Scene.html) and
[renderer](https://docs.rs/vello/latest/vello/struct.Renderer.html) contracts.
Adopted are explicit resource ownership, retained scene data, typed cache
identity, and derivative-based analytic coverage. Adapted is a ProGPU-specific
uniform ABI that lets analytic and texture masks share one pipeline layout.
Rejected are foreign source translation, runtime reflection or shader
construction, CPU flattening, and treating non-canonical paths as analytic.
The CPU shaping/layout separation from the previously researched
Skia/SkParagraph, DirectWrite/Direct2D, Parley, and HarfBuzz designs is
unchanged.

Matched Xcode Allocations reduced native heap plus anonymous VM from
307,312,064 to 188,949,648 bytes (-118,362,416 bytes, -38.5%).
IOAccelerator VM fell from 122,519,552 to 10,158,080 bytes, while live IOGPU
remote-storage resources fell from 929 entries/119,177,216 bytes to 143
entries/9,633,792 bytes. The matched Skia capture retained 223,512,608 bytes
of native heap plus anonymous VM, making the final ProGPU allocation ledger
34,562,960 bytes (15.5%) lower. In ordinary fresh-process benchmark telemetry,
ProGPU remained 19.55 MiB higher at the maximum physical-footprint snapshot
and 5.79 MiB higher in managed retained memory; these distinct ledgers are
reported separately.

Two repeated 600-frame Composition runs averaged 120.230 FPS and approximately
5.00 KiB allocated per frame for ProGPU versus 119.916 FPS and 5.81 KiB per
frame for Skia. The compositor recorded zero mask textures and zero mask
render passes. Focused affine sharp and elliptical-rounded clip tests, 200
compositor/layer contracts, four pinned Avalonia retained-composition tests,
and the complete retained/flattened ControlCatalog matrix pass. Compact
evidence is retained in the `avalonia-affine-mask-*` artifacts dated
2026-07-27; raw Instruments traces and exports were cleaned after summarizing.

The first post-change Native/Dawn run revealed that the imported drawable uses
the compositor's outermost `RenderOffscreen` entry point. Its previous blanket
offscreen exclusion therefore retained the 48-texture startup path. Analytic
clips are now accepted at offscreen depth one, while nested layer/effect
transactions remain conservative. A new focused test renders the affine clip
twice into an explicit texture, asserts zero texture-mask passes, and checks
inside/outside pixels.

After that correction, the non-Silk Composition lane required
`DawnMetalIOSurface`, produced 119.63 FPS and 4.88 KiB allocated per frame,
and reported zero mask textures, zero mask passes, and zero tracked
intermediate texture bytes. Xcode Instruments attributed 197,850,976 bytes of
native heap plus anonymous VM, 13,615,104 bytes of IOAccelerator VM, and 12
Metal resource lists totaling 589,824 bytes. It recorded 330 submissions, 863
completions, and no Metal errors or compiler/hang signals. This closes the
backend portability gap without a cross-device copy or a Dawn-specific shader
variant.

### Shared PathAtlas transient-variant policy

The all-page desktop validation found that dense scrolling vector text could
retain an 8 MiB R8 PathAtlas even though its final frame needed only 86 paths
and about 96 KiB of coverage. The cause was temporal cache residency after the
maximum atlas dimension increased to 4096, not Avalonia scene ownership or a
Metal leak.

The clean-room policy now alternates growth axes without moving existing
texels and performs an allocation-free live-area test before retaining a
larger recovery texture. A sparse current frame takes the compositor's
existing single bounded retry, uses the deterministic multi-strategy packer,
and rerasterizes coverage before any changed UV is submitted. Stale
phase/transform variants are discarded transactionally. This adapts the
explicit cache/resource ownership and generation invalidation already
researched in Skia Graphite, WebRender, Vello, Direct2D, and WebGPU; it does
not copy their packing code or data structures. Runtime reflection, CPU
flattening, unbounded keys, delayed retry, and submitting moved UVs are
rejected.

On the matched WinUI `Text & Documents` workload the final texture fell from
8 MiB to 0.5 MiB, tracked texture/staging residency from 9.83 to 2.33 MiB,
allocation from 357,316 to 331,500 bytes/frame, and compositor time from
4.0873 to 2.7073 ms with unchanged 58.4 wall FPS. Xcode attributed
209,554,320 persistent native heap plus anonymous VM, a 51,838,976-byte peak
Metal allocation counter, and zero Metal errors, spills, or hang signals.
The final all-page run completed 54/54; its longer `Text & Documents` process
shrunk the atlas to 0.25 MiB, reduced tracked texture/staging residency from
17.82 to 2.09 MiB and physical footprint from 510.69 to 478.52 MiB, while
improving wall FPS from 62.96 to 64.49.
Detailed evidence and the adopted/adapted/rejected rationale are recorded in
`docs/progpu-gpu-texture-memory-audit.md`.

One final differential check qualified where the direct sharp-rectangle path
is valid. The ProGPU test scene was executed against native SkiaSharp 3.119.4;
Skia matched the pre-optimization picture-shader pixels byte-for-byte.
Analytic final-target coverage is therefore adopted for ordinary UI paths,
but applying that SDF while rasterizing an offscreen recorded picture is
rejected because the later texture filter performs a second coverage
convolution. ProGPU keeps the original retained path and PathAtlas coverage in
offscreen transactions, preserving Skia output while retaining the primary
target's memory and batching benefit.

### Compact Avalonia retained-command ownership

The next root-filtered heap investigation found that the Avalonia mirror
retained 281 ordinary commands in 274 `RenderCommand[]` arrays. The general
command is deliberately broad, but its array stride is 560 bytes; 125 of
those commands were glyph runs, 151 were ordinary vector primitives, and only
five used another representation. This made one rectangle or glyph run pay
for every texture, effect, chart, extension, mesh, static-buffer, and custom
field in the general command ABI.

The clean-room replacement keeps the public Avalonia and ProGPU contracts
unchanged. Stable ordinary visuals retain typed vector or glyph-run command
objects, borrow already-owned glyph index/position arrays, and expand a
command value on the stack at compilation. Unsupported commands, GPU
transform indirection, retained resources, and custom visuals remain on the
complete `DrawingContext` path. Recording uses one scene-owned scratch
context. An EventPipe allocation trace caught and prevented an intermediate
design from trimming a changing text visual's private command list every
frame; that version allocated a new 4.5 KiB command array on each update.

This adopts retained display-list specialization and lazy optional state from
the previously reviewed Skia Graphite, WebRender, Vello, Direct2D, and
HarfBuzz/SkParagraph separation contracts. It adapts them to ProGPU's typed
owner and invalidation interfaces: no foreign command encoding, source
layout, or implementation control flow is used. Rejected are changing the
general command ABI, a tagged object/property bag, reflection, unsafe union
aliasing, copying glyph arrays, discarding state needed for DPI/atlas/device
loss recompilation, and accepting an unsupported command approximately.

Root-filtered live `RenderCommand[]` storage fell from 163,936 to 5,696 bytes
(-158,240 bytes, -96.5%). The typed command objects and cache owners retain
61,968 bytes, and 100 no-longer-needed per-visual
`DrawingContext`/`List<RenderCommand>` pairs disappeared. The final whole
rooted ProGPU heap was 11,521,811 bytes versus 9,127,249 bytes for the same
Skia reference binary, a 2,394,562-byte gap. Relative to the pre-change
11,609,007-byte ProGPU capture, total rooted retention fell 87,196 bytes;
small differences beyond the deterministic owner ledger remain
fresh-process variation.

The corrected 600-frame Composition run measured 120.27 FPS and 5,383
managed bytes/frame. The matched Skia run measured 119.89 FPS and 5,816
bytes/frame. The complete 2,490-test core suite, 65 Avalonia contract tests,
and retained/flattened pixel matrix pass byte-for-byte. The refreshed
Avalonia 12/11 integration package stack passed exact Avalonia ABI and
runtime-reflection validation; its isolated package-only smoke rendered 30
frames through one retained scene with zero fallback nodes.

Fresh matched Xcode Allocations captures measured 176,094,176 bytes of
persistent native heap plus anonymous VM for ProGPU versus 213,472,032 bytes
for Skia: ProGPU was 37,377,856 bytes (17.5%) lower. Native allocator payload
was 19,037,152 versus 34,394,912 bytes (-44.7%), and anonymous VM was
157,057,024 versus 179,077,120 bytes (-12.3%). Both processes made the same
92,274,688-byte one-time dispatch-continuation reservation. ProGPU used two
IOSurfaces/26,214,400 bytes versus Skia's three/39,321,600 bytes, and
IOAccelerator VM was 11,173,888 versus 18,317,312 bytes. Neither Metal trace
reported a compiler spill, hang, hang risk, or command-buffer error.

Compact evidence is under
`artifacts/avalonia-compact-retained-command-*20260727`. The profiler deleted
the temporary 812 MiB attribution dump, both later heap dumps and GC dumps,
the EventPipe trace, every raw Instruments trace/export, and all task-owned or
new Xcode scratch after producing the compact reports.

### Ordered primitive retained-state snapshots

The next compact-protocol increment removes a mutable-source read from the
most common incremental visual channels. A queued delta now captures its typed
change mask, state/content revisions, affine transform, and local content
bounds together with visibility, opacity, the existing owner,
generation-bearing handle, retained identity, and transitional source shell.
If the same visual changes again before acknowledgement, its value is replaced
at the original queue index. This preserves first-change ordering, accumulates
all flags, and publishes the latest coherent primitive state in `O(1)` time
without a queue search, hash, or new managed object.

ProGPU validates the captured handle and retained identity, writes the
transform and bounds snapshots directly into the corresponding paged visual
state, applies captured visibility/opacity, and records the captured
state/content revisions. The ordinary incremental renderability check walks
the retained ProGPU parent hierarchy rather than rereading Avalonia ancestors.
Complex appearance, resource, topology, and inherited-property changes
deliberately retain the existing typed source synchronization until their
ownership and descendant generation contracts are explicit. An
invisible-to-visible deferred subtree still traverses the typed server tree;
removing that traversal requires direct descendant topology/content
generations. Full synchronization remains the transactional recovery for a
stale handle or unsupported delta.

The design continues the primary-source model already recorded above:
[DirectComposition](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts)
makes ordered visual-property updates visible at commit,
[WebRender](https://searchfox.org/firefox-main/source/gfx/wr/webrender/src)
separates epoch-validated retained state from scene construction,
[Skia Graphite `Recorder`](https://skia.googlesource.com/skia/+/d78564aad21d/include/gpu/graphite/Recorder.h)
keeps recorder-local state and explicit resource ownership, and
[Vello `Scene`](https://docs.rs/vello/latest/vello/struct.Scene.html) keeps a
typed encoded scene for later GPU work. Adopted are ordered atomic visibility,
immutable primitive snapshots, owner-local state, and stale-generation
rejection. Adapted is an original queue-index refresh protocol and
`Matrix3x2`/`Vector4` transport chosen to match ProGPU's float scene ABI.
Rejected are reflection, an untyped property bag, copying a foreign serializer
layout, queue scanning, per-change objects, silently accepting a reused
handle, and prematurely moving Unicode/OpenType shaping onto the GPU.

Seven pinned Avalonia retained-composition tests pass, including a focused
coalescing contract that proves one queue slot receives the latest transform,
visibility, opacity, unioned change mask, and revision before exact-revision
acknowledgement clears the slot index. The patched Avalonia build is
warning-free, the compositor patch reapplies cleanly to the pinned official
source, the source-integrated ProGPU renderer compiles, and the repository
patch-contract tests cover the direct primitive fields and mirror-hierarchy
renderability path. The complete retained/flattened ControlCatalog pixel
matrix also passes byte-for-byte: nine zero-fallback pages plus geometry
clips, inherited text options, conic/picture opacity masks, blur/drop-shadow
effects, and every BitmapCache scale/snap/ClearType fixture.

A fresh 600-frame runtime comparison measured ProGPU at 120.29 FPS and 5,384
managed bytes/frame versus Skia at 120.01 FPS and 5,953 bytes/frame. ProGPU
completed 718 incremental synchronizations with zero fallback nodes and zero
tracked intermediate-texture bytes. Matched Xcode Allocations captures
reported 187,442,112 bytes of persistent native heap plus anonymous VM for
ProGPU versus 222,262,048 bytes for Skia (-34,819,936 bytes, -15.7%).
Allocator payload was 28,812,224 versus 45,380,384 bytes, anonymous VM was
158,629,888 versus 176,881,664 bytes, IOSurface VM was 26,214,400 versus
39,321,600 bytes, and IOAccelerator VM was 9,240,576 versus 16,138,240 bytes.
Both processes retained the same 92,274,688-byte dispatch-continuation
reservation and 13,303,808 bytes of attributed JIT arena pages. These matched
totals qualify absence of a native regression; the managed delta change is
not presented as their cause. Compact evidence is under
`artifacts/avalonia-direct-delta-profile-20260727` and
`artifacts/avalonia-direct-delta-instruments-final-20260727`; raw Instruments
traces, XML exports, task scratch, and Xcode `ktrace` bundles were deleted by
the profiler.
The rebuilt exact-source replacement stack passed strict ABI validation for
every merged Avalonia assembly, the renderer/windowing runtime-reflection
metadata audit, Avalonia 12 and 11 integration packing, and an isolated
package-only smoke that rendered 30 frames through one retained scene with
zero fallback nodes.

### Primitive appearance channel

The next bounded protocol increment separates opacity and visibility from the
remaining complex appearance state. The final free low bit in the existing
byte-sized retained-change field is `PrimitiveAppearance`; the high bit
continues to represent queue membership. Deserialized and animated
opacity/visibility changes publish that flag and the already captured scalar
values. Clip, size, mask, effect, cache, adorner, and render-option mutations
continue to publish `Appearance` and use the full typed synchronization path.
Coalescing both classes in one ordered queue slot remains `O(1)` and
allocation-free.

This paragraph records the intermediate protocol. Layout, geometry, bitmap
cache, effect, opacity-mask, inherited-option, topology, and adorner state now
all have direct typed channels; the final catch-all bit is removed below.

The ProGPU consumer applies a primitive delta directly to its
generation-checked visual page. It does not reread Avalonia geometry, effects,
opacity masks, cache modes, brushes, render options, text options, transforms,
or bounds. A batch that also contains a complex appearance mutation still
uses the complex path transactionally. This preserves observable batch
ordering and avoids inventing an untyped property bag or duplicating
Avalonia's serializer.

This is the next concrete application of the primary-source designs recorded
above: DirectComposition exposes independently animatable visual properties,
WebRender separates dynamic property bindings from scene reconstruction,
Vello retains typed scene state, and Graphite keeps recorder/resource
ownership explicit. Adopted is the separation of hot scalar properties from
resource topology. Adapted is a clean-room byte flag and immutable scalar
snapshot fitting Avalonia's existing acknowledgement protocol. Rejected are
reflection, per-update objects, runtime service lookup, copying another
engine's serializer layout, and treating size/clip/effect dependencies as
independent before their bounds generations are explicit.

Eight pinned Avalonia protocol tests pass, including an opacity-only
classification and a coalesced transform-plus-primitive-appearance snapshot.
The compositor patch reapplies cleanly to the official pinned source, the
source-integrated renderer builds warning-free, and repository patch-contract
tests cover the primitive flag and direct ProGPU update.

The complete retained-versus-flattened pixel contract also passes after the
split: nine zero-fallback ControlCatalog pages plus native linear, conic, and
picture opacity masks, transformed adorner clip chains, blur and drop-shadow
effects, geometry clipping, inherited text options, and BitmapCache
scale/snap/ClearType behavior. This verifies that primitive updates did not
bypass the complex appearance path or reduce rendering quality.

The exact replacement stack was rebuilt after the split. Every merged
Avalonia 12 assembly passed ABI validation, both renderer/windowing assemblies
passed the runtime-reflection audit, and the Avalonia 12 and 11 integration
packages were produced. An isolated package-only smoke rendered 29 frames
through one retained scene with 29 server-backend renders and zero fallback
nodes.

On the animated `Composition` page, a fresh 600-frame run measured ProGPU at
120.29 FPS and 5,382 managed bytes/frame versus Skia at 120.01 FPS and 5,953
bytes/frame. ProGPU completed 719 incremental synchronizations with zero
fallback nodes and zero tracked intermediate-texture bytes. The two-byte/frame
change from the immediately preceding ProGPU run is within measurement noise;
the evidence establishes no hot-path regression rather than an FPS claim.

Matched Xcode Allocations captures reported 193,224,224 bytes of persistent
native heap plus anonymous VM for ProGPU versus 222,944,272 bytes for Skia
(-29,720,048 bytes, -13.3%). Allocator payload was 29,990,432 versus
46,308,368 bytes, anonymous VM was 163,233,792 versus 176,635,904 bytes,
IOSurface VM was 26,214,400 versus 39,321,600 bytes, and IOAccelerator VM was
13,828,096 versus 15,941,632 bytes. Both retained the same 92,274,688-byte
dispatch reservation and 13,303,808-byte JIT arena. The ProGPU IOAccelerator
high-water varied upward from the previous fresh launch, so these figures are
a native no-regression comparison and are not attributed causally to the
managed protocol split. Compact evidence is under
`artifacts/avalonia-primitive-appearance-*20260727`; raw traces, XML exports,
task scratch, and exact Xcode `ktrace` bundles were deleted automatically.

### libdispatch continuation reservation qualification

The repeated 92,274,688-byte `VM: Dispatch continuations` row is a private
per-process virtual-address reservation, not shared memory and not an
88 MiB resident working set. A paired `vmmap` sample measured only 540,672
resident and 524,288 dirty bytes in that region. Apple documents that
unallocated virtual pages are not equivalent to resident memory and exposes
resident/unallocated columns through `vmmap`.

The current Apple libdispatch source explains the exact size on this host:
the allocator owns one magazine per logical CPU, macOS magazines contain 512
pages, and this machine reports 11 logical CPUs with 16 KiB pages. Therefore
`11 * 512 * 16,384 = 92,274,688` bytes. The heap is mapped with the
`VM_MEMORY_LIBDISPATCH` tag and its unused physical pages remain
demand-paged. Consulted primary sources:
[allocator constants](https://github.com/apple-oss-distributions/libdispatch/blob/2361ffb78a76f7ee488cd052eb0bc5c767118bf9/src/allocator_internal.h),
[mapping and allocator selection](https://github.com/apple-oss-distributions/libdispatch/blob/2361ffb78a76f7ee488cd052eb0bc5c767118bf9/src/allocator.c), and
[Apple virtual-memory accounting](https://developer.apple.com/library/archive/documentation/Performance/Conceptual/ManagingMemory/Articles/VMPages.html).

libdispatch has no supported application API to resize that mapping. Its
internal process-launch variable `LIBDISPATCH_CONTINUATION_ALLOCATOR=0`
selects the malloc fallback on this OS build and removes the VM row, but it is
an unsupported implementation detail and must be set before allocator
initialization. It is not a valid package or compositor configuration
contract.

Matched Xcode Allocations with that diagnostic override reported 97,949,840
bytes of persistent heap plus anonymous VM instead of 193,224,224 bytes in
the preceding default capture. The approximately 95.3 MB reduction is
chiefly the missing virtual reservation. Across three fresh 600-frame runs,
ProGPU median resident memory changed from 226,508,800 to 225,624,064 bytes
(-0.39%) while Skia moved in the opposite direction by +0.05%; physical
footprint similarly changed -0.70% for ProGPU and +0.07% for Skia. Those
mixed directions are launch noise, not evidence of a meaningful physical
memory saving.

ProGPU FPS was unchanged (+0.01%). Its median frame moved +0.83%, p95 -1.12%,
and p99 +1.95%; Skia's median was unchanged while p95 moved +2.23%. An
alternating seven-pair native stress test of five million short dispatch work
items measured the malloc fallback 2.28% slower (0.761954 versus 0.744965
seconds median). Adopted is accurate virtual/resident/dirty reporting.
Rejected is changing a private OS allocator to improve a virtual-only total:
it offers no repeatable UI working-set benefit and weakens the allocator-heavy
case. Compact evidence is under `artifacts/dispatch-continuation-*20260727`.

### Native SwiftUI/Metal reference

The repository now includes an independent native control: SwiftUI hosts an
`MTKView` through `NSViewRepresentable`; the renderer retains one command
queue, keeps drawable textures display-only, acquires the drawable at the
onscreen render pass, presents through the command buffer, and then commits.
This follows Apple's primary
[MTKView](https://developer.apple.com/documentation/metalkit/mtkview/),
[framebuffer-only](https://developer.apple.com/documentation/metalkit/mtkview/framebufferonly),
[drawable-lifetime](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Drawables.html),
[command-queue](https://developer.apple.com/documentation/metal/MTLCommandQueue),
and [NSViewRepresentable](https://developer.apple.com/documentation/swiftui/nsviewrepresentable)
contracts.

At 1600 × 1200 physical pixels the native control retained 23,592,960 bytes
of IOSurface, 94,162,125 bytes of active `owned unmapped (graphics)`, and an
initial 142,082,048-byte physical footprint. All three regions were stable.
The 92,274,688-byte dispatch mapping was present but only 196,608 bytes were
resident/dirty. Removing it with the unsupported malloc diagnostic did not
reduce final physical footprint and increased native allocator payload by
329,584 bytes.

This control is a floor, not an equivalent UI workload: it excludes .NET,
Avalonia, text/path atlases, retained scene state, and WebGPU. The applicable
presentation principles are already present in ProGPU's direct paths:
render-attachment-only usage, late drawable acquisition and release,
one-frame Apple wgpu latency where available, zero-copy Dawn IOSurface import,
and two retained QuartzCore IOSurfaces versus the control's three. Therefore
no compositor or surface allocation was changed. The evidence instead added
reusable GUI attach and native-without-EventPipe modes to the profiler.
Both Metal lanes reported zero compiler spills, hangs, hang risks, and
command-buffer errors. Compact evidence is under
`artifacts/swiftui-metal-memory-20260727`; raw traces, exports, and Xcode
scratch were deleted.

### Appearance fallback-classification gate

The next retained-compositor optimization removes a redundant managed-tree
read from transform and primitive-opacity updates. `RequiresFallback` depends
on complex appearance state, so the ProGPU delta consumer now reevaluates it
only when `CompositionVisualChangedFields.Appearance` is present. The pinned
Avalonia publisher classifies `AdornedVisual` as complex appearance because it
participates in that decision. Primitive opacity/visibility and transform
deltas continue to update their typed retained fields directly. Classification
is therefore `O(1)` for every delta, but the common scalar/transform path no
longer traverses Avalonia-owned clip, mask, effect, cache, and adorner
properties.

This follows the dynamic-property separation recorded above without changing
Avalonia's observable compositor contract. Adopted is the distinction between
scalar animation state and resource/topology state. Adapted is a conservative
bit gate on the existing ordered change snapshot. Rejected are cached
unversioned property references, reflection, and assuming an adorner cannot
change fallback eligibility.

This was an intermediate safety gate. The final direct-channel pass removes
the catch-all and `RequiresFallback` call entirely; every resource-bearing
delta now validates its own captured typed payload.

Nine focused pinned-Avalonia protocol tests pass, including the new
`AdornedVisual` classification. Six repository patch-contract tests pass. The
complete retained-versus-flattened pixel matrix remains byte-identical across
nine ControlCatalog pages, geometry clips, inherited text options, linear,
conic, and picture opacity masks, blur/drop shadow, and BitmapCache
scale/snap/ClearType variants, all with zero fallback nodes.

Three fresh alternating 600-frame runs measured ProGPU at 120.182 FPS,
8.338 ms mean frame time, 10.492 ms p99, and about 5,385 managed bytes/frame.
Skia measured 120.002 FPS, 8.348 ms mean, 10.290 ms p99, and about 5,947
managed bytes/frame. Each ProGPU run retained one 739-node scene, completed
718 incremental synchronizations, and reported zero fallback nodes and zero
tracked intermediate textures. The small timing differences are treated as
noise; the repeatable result is lower managed allocation with no quality or
throughput regression.

Matched Xcode captures of the exact binaries attributed 179,059,920 bytes of
persistent heap plus anonymous VM to ProGPU versus 211,502,192 bytes to Skia
(-32,442,272 bytes, -15.3%). ProGPU used 21,855,440 bytes of heap,
157,204,480 bytes of anonymous VM, 26,214,400 bytes of IOSurface, and
11,173,888 bytes of IOAccelerator VM; Skia used 33,834,096, 177,668,096,
39,321,600, and 16,269,312 bytes respectively. Both retained the same
92,274,688-byte dispatch virtual reservation and reported zero Metal spills,
hangs, hang risks, or command-buffer errors.

The exact Avalonia 12/11 replacement stack was rebuilt after the change. ABI,
runtime-reflection, and package checks passed, and an isolated NuGet consumer
rendered 29 frames through one retained scene with 29 server-backend renders
and zero fallback nodes. Compact evidence is under
`artifacts/avalonia-appearance-fallback-gating-*20260727`; raw Instruments
traces, exports, and Xcode scratch were deleted.

### Font-catalog ownership and retained-delta low-water policy

Root-filtered heaps identified two bounded but unnecessary ownership costs.
System-font discovery retained equal family, display-name, and path strings as
separate objects, and each composition target retained its largest startup
delta-array capacity after the visual tree settled. The font scan now
canonicalizes equal strings in a local collectible dictionary and deduplicates
faces with a typed identity comparer instead of allocating concatenated keys.
The default Avalonia matcher consumes the process-wide `FontManager` family
view directly instead of owning a second 851-face snapshot. Global
`String.Intern`, reflection, runtime parsing, and mutable shared caller arrays
remain rejected.

The retained protocol now has explicit consumer ownership. A target starts
tracking deltas only when the render thread discovers the typed
`ICompositionServerBackend` or retained drawing-context feature, and releases
its queue if that consumer disappears. Consequently the normal Skia path does
not allocate or retain ProGPU protocol storage. Active ProGPU targets preserve
capacity during bursts, then reduce it to 16 entries only after 60 successful
low-delta synchronizations. A later burst resets the observation window, so a
many-visual animation cannot trigger per-frame grow/shrink allocation.

This applies the ownership and lazy-activation principles already established
from DirectComposition, WebRender, Vello, Graphite, and the Apple Metal control:
retained state belongs to the active backend context, startup work is released
after measured steady state, and hot scalar updates remain typed. It adapts
those principles to Avalonia's acknowledgement protocol without copying an
engine serializer or scene implementation.

The directly attributable rooted-memory reductions are 30,190 bytes from 615
duplicate strings, 6,832 bytes from duplicate `FontInfo[]` storage, and 96,768
bytes from the two active ProGPU delta arrays. The latter fell from 98,352 to
1,584 bytes (98.4%). The Skia baseline's unused arrays fell from 98,352 bytes
to the 24-byte empty-array singleton. Final matched rooted heaps were
11,458,433 bytes for ProGPU and 9,108,725 bytes for Skia; ProGPU's remaining
2,349,708-byte gap includes its 739-node typed retained scene. ProGPU allocated
5,386.31 bytes/frame versus Skia's 5,955.85 while completing 719 incremental
synchronizations with zero fallback.

Fresh final-binary Xcode captures measured 191,141,536 bytes of persistent
native heap plus anonymous VM for ProGPU versus 210,844,192 for Skia
(-19,702,656 bytes, -9.3%). The split was 30,709,408/160,432,128 bytes for
ProGPU and 31,636,000/179,208,192 for Skia. ProGPU retained two QuartzCore
IOSurfaces totaling 26,214,400 bytes and 13,828,096 bytes of IOAccelerator VM;
Skia retained three totaling 39,321,600 bytes and 18,366,464 bytes of
IOAccelerator VM. Both retained the same 92,274,688-byte virtual dispatch
reservation. ProGPU's explicit Metal counter was stable at 29,589,504 bytes;
Skia's path exposes no equivalent counter in this trace, so its zero row is
not treated as zero GPU usage. Both reported zero spills, hangs, hang risks,
and command-buffer errors.

Eleven focused protocol tests pass. The complete retained/flattened pixel
matrix is byte-identical with zero fallback nodes. The exact Avalonia 12/11
stack passes ABI and runtime-reflection validation, and its isolated NuGet
consumer rendered 30 frames through one retained scene with 30 typed
server-backend renders and zero fallback. Compact evidence is under
`artifacts/avalonia-retained-protocol-ownership-*20260727`; raw dumps, traces,
exports, and Xcode scratch were deleted.

### Compact layout-clip channel

The retained protocol now separates layout clipping from resource-bearing
appearance. A `Size` or `ClipToBounds` mutation publishes those primitive
values in a dedicated typed delta. ProGPU retains any axis-aligned geometry
clip independently and intersects its bounds with the new layout rectangle;
a non-rectangular geometry retains its typed identity. An opacity mask or
effect additionally publishes its dedicated direct channel, because its
realization and sampling bounds can depend on the layout clip. The ordinary
path is therefore
constant-work and allocation-free, without rereading managed Avalonia
properties or recompiling unchanged custom content.

This design follows the separation visible in
[DirectComposition clipping](https://learn.microsoft.com/en-us/windows/win32/directcomp/clipping):
static rectangles are direct properties while changing/animated clips have a
separate clip object. It also follows the explicit saved clip stack and
axis-aligned fast path in
[SkCanvas](https://api.skia.org/classSkCanvas.html) and
[Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1rendertarget-pushaxisalignedclip).
[Win2D layers](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_6.htm)
reinforce keeping mask/layer resources separate from a primitive clip.
[WebRender's typed dynamic properties](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs)
and [Vello's retained scene](https://docs.rs/vello/latest/vello/struct.Scene.html)
inform the compact value channel and scene ownership. Unicode/OpenType shaping
remains a reusable CPU result, consistent with
[HarfBuzz shaping plans and caching](https://harfbuzz.github.io/shaping-plans-and-caching.html);
the clipping change does not reshape text.

Adopted are a dedicated primitive channel, explicit clip composition order,
and conservative promotion when resource bounds depend on layout. Adapted is
the axis-aligned intersection inside ProGPU's typed visual page. Rejected are
runtime reflection, cached unversioned Avalonia-property references, rebuilding
geometry for a rectangular layout update, and weakening mask/effect
correctness to reduce synchronization counts.

A deterministic fixture alternated only container size and `ClipToBounds`.
Across 60 warm-up and 180 measured frames it completed 238 compact layout-clip
synchronizations, zero complex-appearance synchronizations, two full
synchronizations, and only two initial custom-visual compilations for a
791-node scene. It sustained 120.299 FPS with 8.379 ms mean, 9.554 ms p95,
10.260 ms p99, 4,920 managed bytes/frame, 23,408,392 managed bytes, and zero
fallback nodes. The explicit Metal allocation counter was 29,458,432 bytes.

Xcode Allocations attributed 187,822,448 bytes of persistent heap plus
anonymous VM to the exact workload: 30,388,592 bytes of allocator payload and
157,433,856 bytes of anonymous VM. That total includes the already-qualified
92,274,688-byte sparse libdispatch virtual reservation. The presentation path
retained two IOSurfaces totaling 26,214,400 bytes and 10,911,744 bytes of
IOAccelerator VM. Metal System Trace reported zero compiler spills,
command-buffer errors, potential hangs, and hang risks.

Fourteen focused pinned-Avalonia protocol tests, 67 Avalonia contract tests,
and seven repository patch-contract tests pass. The retained/flattened pixel
matrix is byte-identical across nine zero-fallback ControlCatalog pages plus
geometry clips, inherited text options, aliased text, conic masks, blur, drop
shadow, and BitmapCache scale, snap, and ClearType variants. Compact evidence
is under `artifacts/avalonia-layout-clip-channel-20260727`. The final Avalonia
12/11 package stack passes public ABI and runtime-reflection inspection; its
isolated, SHA-512-validated NuGet consumer rendered 30 frames through one
typed retained scene with 30 server-backend renders and zero fallback nodes.
Raw traces, exports, and Xcode scratch were deleted.

### Typed geometry-clip channel and allocation audit

The next retained-protocol slice separates geometry-clip identity from complex
appearance. The pinned Avalonia server publishes the already adapted immutable
ProGPU path in the same value delta as its revision and generation-bearing
backend handle. ProGPU validates the handle, applies the path to the existing
retained visual, and recomposes it with the independently retained layout
rectangle. The direct path does not reread the managed server property.
Unsupported geometry and clipped adorners retain the transactional fallback;
supported blur and drop-shadow effects now use their own typed scalar channel,
while unsupported effect kinds continue to fall back transactionally.

This keeps the primitive/object separation described by
[DirectComposition clipping](https://learn.microsoft.com/en-us/windows/win32/directcomp/clipping)
and the explicit clip-stack model in
[SkCanvas](https://api.skia.org/classSkCanvas.html). The geometry path is
retained as an object while the layout rectangle stays a primitive fast path,
matching the distinction in
[Direct2D axis-aligned clipping](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1rendertarget-pushaxisalignedclip)
and the resource-bearing layer contract in
[Win2D](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_6.htm).
The compact value update follows
[WebRender's typed dynamic properties](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs),
while immutable scene-owned path identity follows
[Vello's retained `Scene`](https://docs.rs/vello/latest/vello/struct.Scene.html).
Text remains an independently reusable CPU-shaped result as recommended by
[HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html);
clip mutations neither reshape text nor invalidate font state.

Adopted are typed immutable geometry identity, generation-checked direct
addressing, and separate primitive and resource-bearing invalidation. Adapted
for ProGPU are the paged visual-state owner and the intersection of retained
geometry bounds with a captured layout rectangle. Rejected are runtime
reflection, string/type-name dispatch, unversioned property rereads, and
rebuilding unchanged geometry or effects for an ordinary clip mutation.

The allocation profile also exposed two independent hot-path defects. First,
default `ValueType.Equals` reflected over six `Vector4` fields in
`MaskSamplingUniforms`; explicit `IEquatable<MaskSamplingUniforms>` equality
now compares the typed fields directly. Second, transformed arc bounds created
a temporary `ArcSegment` every frame. The clean-room replacement evaluates the
affine arc endpoints and the x/y parametric extrema from the existing shader
parameters, accepting extrema only when their angles lie in the directed
sweep. It performs fixed `O(1)` work and storage without allocation and is
covered differentially against transformed retained-arc bounds.

Across 120 warm-up and 600 measured frames, alternating two precreated ellipse
clips produced one full synchronization, 719 incremental synchronizations,
719 direct geometry-clip synchronizations, zero complex-appearance
synchronizations, and zero fallback nodes in a 789-node scene. The final
EventPipe run sustained 120.229 FPS with 8.339 ms mean, 10.304 ms p95,
11.293 ms p99, 8,502 managed bytes/frame, and 23,222,088 managed bytes.
EventPipe's estimated measurement allocation fell from 27,168,358 to
4,321,172 bytes (-84.1%); the workload counter fell from 43,593 to 8,502
bytes/frame (-80.5%). The explicit Metal counter was 29,999,104 bytes, with a
190,464-byte bounded mask pool and a 262,144-byte path atlas.

Xcode Allocations attributed 184,007,520 bytes of persistent heap plus
anonymous VM: 23,952,224 bytes of allocator payload and 160,055,296 bytes of
anonymous VM. This includes the qualified 92,274,688-byte sparse libdispatch
virtual reservation, two IOSurfaces totaling 26,214,400 bytes, and 14,123,008
bytes of IOAccelerator VM. Metal System Trace reported zero drawable waits,
compiler spills, hangs, hang risks, and command-buffer errors. The
Instruments-launched benchmark sustained 119.480 FPS and 8,504 bytes/frame
with the same 719 direct synchronizations and zero fallbacks.

Sixteen pinned-Avalonia protocol tests, 67 backend contracts, seven
patch-contract tests, and 231 focused compositor/layer/clip/arc tests pass.
The full retained-versus-flattened pixel matrix passes nine zero-fallback
ControlCatalog pages and every dedicated mask, adorner, effect, clip, text
option, and BitmapCache fixture. The exact package stack passes ABI and
runtime-reflection audits; its isolated NuGet consumer rendered 29 frames
through one retained scene with 29 backend renders and zero fallback nodes.
Compact evidence is under
`artifacts/avalonia-geometry-clip-channel-20260727`; raw traces, dumps,
exports, and Xcode scratch were deleted.

### Typed BitmapCache channel and embedded GPU-resource lifetime

The next compact-protocol increment separates Avalonia `BitmapCache` state
from broad complex appearance. The pinned server publishes presence, render
scale, device-pixel snapping, and ClearType as an immutable typed snapshot.
ProGPU applies scale and snapping directly to the existing retained layer.
ClearType remains an inherited text semantic, so changing it conservatively
promotes the affected subtree instead of allowing descendants to retain stale
text rendering state. There is no reflection, type-name dispatch, or runtime
property probing.

The design retains the same cross-engine decisions used by the preceding clip
channels. [DirectComposition](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts)
and [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
separate stable visual/resource identity from changing content.
[Skia's canvas/picture model](https://api.skia.org/classSkPicture.html) keeps
recorded content reusable while surfaces own realization.
[Win2D layers](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_6.htm)
make offscreen resource lifetime explicit.
[WebRender typed dynamic properties](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs)
and [Vello's retained scene](https://docs.rs/vello/latest/vello/struct.Scene.html)
inform the compact value channel and scene ownership. Shaped text remains a
reusable CPU result following
[HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html);
only a ClearType semantic transition invalidates inherited text presentation.

Adopted are typed immutable state, stable resource identity, explicit
generation validation, and bounded layer ownership. Adapted for ProGPU are a
generation-bearing paged handle and the separation of texture content
generation from native texture-view generation. Rejected are reflection,
unversioned Avalonia property rereads, treating texel writes as view
replacement, and weakening inherited-text correctness for a lower
synchronization count.

Profiling found two independent GPU-resource defects. Avalonia's retained
scene root is embedded through a typed `DrawVisual` reference and intentionally
has no parent link to the ProGPU host root. The old liveness walk therefore
misclassified its cached layer as detached and disposed the texture on every
dirty frame. Embedded roots recorded for the current frame now participate in
the same layer/effect liveness walk. EventPipe no longer samples
`GpuTexture.Allocate` from `ApplyAndDrawLayer`; exact managed allocation fell
from 4,449 to 4,232 bytes/frame and the randomized estimate fell from
3,278,188 to 2,048,868 bytes over 600 measured frames.

The retained texture then exposed a second issue: its bind-group cache key used
content `Generation`, which advances after every offscreen redraw. WebGPU bind
groups depend on texture-view identity, not texel contents. A separate
`ViewGeneration` now advances only when the default native view is created or
replaced. Across three fresh 120/600-frame processes, the cache held one
persistent texture bind group instead of 30 and eight native bind groups
instead of 38. The 37,752-byte layer stayed live, all 719 changes used the
direct BitmapCache channel, and fallback remained zero. Exact allocation was
4,204-4,258 bytes/frame; mean FPS was 117.87, with two runs at 120 FPS and one
host-scheduling outlier at 113.63 FPS.

The final Xcode capture retained 201,794,384 bytes of native heap plus
anonymous VM, split into 23,896,912 and 177,897,472 bytes. Its footprint was
higher than the preceding capture because QuartzCore reached a
three-IOSurface high-water mark (39,321,600 bytes) rather than two
(26,214,400 bytes), with corresponding IOAccelerator growth. Three fresh
non-Instruments repetitions confirmed this presentation variance: two exposed
42,647,552 bytes through the Metal counter and one 29,671,424 bytes while all
three retained identical ProGPU textures and bind groups. macOS desired frame
latency remains one. Metal System Trace reported zero drawable waits, compiler
spills, hangs, hang risks, and command-buffer errors, so the third drawable is
recorded as a driver/compositor high-water state rather than attributed to the
view-identity change.

The full retained/flattened pixel matrix passes every zero-fallback page,
linear/conic/picture masks, transformed adorner clips, blur/drop shadow,
geometry clips, inherited text options, and BitmapCache scale/snap/ClearType.
Forty-four focused ProGPU resource tests, 69 Avalonia contracts, 18 pinned
retained-protocol tests, and seven patch tests pass. The regenerated exact
Avalonia 12/11 package stack passes ABI, public-key, and runtime-reflection
inspection. Its isolated package consumer rendered 37 frames through one
retained scene with 37 typed backend renders and zero fallback. Compact
evidence is under `artifacts/avalonia-bitmap-cache-channel-20260727`; all raw
profiles and Xcode scratch were deleted.

### Typed effect channel and post-bounds snapshot refresh

The effect protocol now separates Avalonia blur and drop-shadow state from
broad complex appearance. The pinned server publishes an immutable value
snapshot containing effect kind, radius, offset, packed ARGB color, opacity,
and the final post-effect output bounds. ProGPU validates the retained handle,
normalizes those scalars, recovers the unaffected content bounds, and updates
or reuses the matching ProGPU effect object. Removing an effect clears the
same retained slot. Unknown effect kinds deliberately abort the incremental
transaction and retain the existing full/fallback behavior.

The design continues to follow
[DirectComposition's stable visual/property model](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts),
[Direct2D effect resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/effects-overview),
[Skia image filters](https://api.skia.org/classSkImageFilter.html),
[Win2D effects](https://microsoft.github.io/Win2D/WinUI2/html/N_Microsoft_Graphics_Canvas_Effects.htm),
[WebRender typed dynamic properties](https://searchfox.org/mozilla-central/source/gfx/webrender_bindings/src/bindings.rs),
and [Vello's retained scene](https://docs.rs/vello/latest/vello/struct.Scene.html).
Text shaping remains independent reusable CPU work following
[HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html);
changing a visual effect does not reshape glyphs.

Adopted are stable effect identity, typed immutable properties, explicit output
bounds, and transactional unsupported-state handling. Adapted for ProGPU are
radius-to-sigma conversion, content-bound recovery from Avalonia's
post-effect bounds, and reuse of the existing retained effect object. Rejected
are reflection, runtime type-name dispatch, property rereads after publication,
and treating a same-extent scalar/color update as a new texture identity.

One ordering defect was found during the clean-room protocol implementation:
Avalonia queued its initial delta before `UpdateRoot` finalized subtree and
effect bounds. The target now refreshes only the already queued snapshots
immediately after that recomputation. This is allocation-free `O(D)` work for
`D` dirty visuals and preserves queue order and coalesced change masks.

The first deterministic fixture alternated blur radius and therefore changed
the required offscreen extent. EventPipe sampled `GpuTexture.Allocate` under
effect drawing, with 4,755 exact managed bytes/frame and a 3,278,732-byte
randomized allocation estimate over 600 frames. That workload measured
legitimate texture resizing rather than the scalar channel. The corrected
fixture alternates two immutable drop shadows with identical radius and offset
but different colors. It exercises the effect channel every frame while
holding texture dimensions constant.

Across three fresh 120-warm-up/600-measured-frame Release processes, the fixed
fixture completed 719 direct effect synchronizations, one initial full
synchronization, zero complex-appearance synchronizations, and zero fallback
nodes per run. Median throughput was 119.73 FPS and median exact managed
allocation was 4,503 bytes/frame. The effect texture remained 12,240 bytes.
A follow-up EventPipe trace reported 4,384.36 bytes/frame and a 2,049,020-byte
randomized estimate, with no `GpuTexture.Allocate` sample in the measured
interval.

The final no-hold Xcode Time Profiler and Metal System Trace run sustained
117.945 FPS under instrumentation, reported 4,382.85 managed bytes/frame,
23,741,512 managed bytes, 42,614,784 Metal allocated bytes, and a
348,718,232-byte process footprint. It retained two persistent texture bind
groups and eleven native bind groups. Metal reported zero spills, potential
hangs, hang risks, or command-buffer errors; two drawable waits totaled
0.036 ms. The hold-enabled Allocations capture attributed 198,218,016 bytes
to persistent native heap plus anonymous VM, including the previously
qualified 92,274,688-byte sparse dispatch-continuation reservation, three
IOSurfaces totaling 39,321,600 bytes, and 12,943,360 bytes of IOAccelerator
VM.

Twenty pinned retained-protocol tests, 71 Avalonia contracts, seven patch
contracts, one headless pixel contract, and 214 focused layer/compositor/clip
tests pass. Static blur and drop-shadow ControlCatalog captures each use one
typed effect synchronization and zero fallbacks. Compact evidence is under
`artifacts/avalonia-effect-channel-20260727`; raw `.nettrace`, Xcode trace,
export, and scratch artifacts were deleted after the summaries were recorded.

### Typed opacity masks, content revisions, and texture-free solid coverage

The opacity-mask protocol now publishes an immutable typed brush handle and
the post-layout subtree bounds through its own change bit. Mutable brush
resources use explicit server observers, and detaching a brush removes its
observer before the replacement is attached. Unsupported mask types fail the
incremental transaction without partially changing the ProGPU scene.

The design follows
[DirectComposition's visual/property separation](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts),
[Direct2D opacity masks](https://learn.microsoft.com/en-us/windows/win32/direct2d/opacity-masks-overview),
[Skia save-layer masking](https://api.skia.org/classSkCanvas.html),
[WebRender's retained display-list model](https://github.com/servo/webrender),
and [Vello's retained scene](https://docs.rs/vello/latest/vello/struct.Scene.html).
[HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
continues to inform the independent reusable CPU text result; changing mask
alpha does not reshape or re-record glyph runs.

Adopted are stable resource identity, typed immutable state, bounded mask
storage, and explicit invalidation domains. Adapted for ProGPU is a solid-mask
fast path that represents the mask as an affine analytic rectangle and scalar
alpha in the existing sampling uniform. Rejected are reflection, runtime
property probing, treating mask state as drawing content, unbounded per-frame
textures, and capturing a pooled analytic bind group into a persistent
incremental page.

Profiling exposed that base Avalonia visuals previously derived content
revision directly from all visual state. A color-only mask animation therefore
recompiled its `CompositionCustomVisualHandler` on every frame. The server now
owns an independent monotonic content revision. Solid-color, surface,
draw-list, acrylic, size-dependent, full custom, and partial custom
invalidations advance it; opacity, transform, effect, and mask changes do not.
The deterministic fixture fell from 239 custom compilations in 240 server
frames to one initial compilation in the final runs.

The initial typed mask used one 69,888-byte R8 offscreen texture and one mask
render pass. The final solid path uses one pooled 96-byte sampling uniform and
bind group, zero mask textures, zero mask render passes, zero mask draw calls,
and zero intermediate texture bytes. Gradient, picture, image, nested, and
otherwise non-uniform masks keep the bounded texture implementation. Pixel
tests cover half-alpha output, offset render-target alignment, and the retained
gradient texture fallback.

Across three fresh 120-warm-up/600-measured-frame Release processes, the
fixture averaged 119.966 FPS. Exact managed allocation was 4,220, 4,175, and
4,174 bytes/frame; managed retained memory was 22.06-22.07 MiB and maximum
physical footprint was 331.55 MiB. Every run reported 718 direct mask
synchronizations, two initial full synchronizations, one custom compilation,
zero complex synchronizations, and zero fallback nodes.

The paired EventPipe/vmmap capture reported a 13,798,147-byte GC heap,
19.84 MiB of live native allocator payload, stable 37.50 MiB IOSurface
residency, and only +0.30 MiB VM_ALLOCATE residency over five samples. The
first 164.20 MiB graphics high-water region dropped to 4.23 MiB after `vmmap`
inspection while dirty bytes stayed at 4.23 MiB, confirming reclaimable driver
residency rather than application growth.

Xcode Instruments reported 42,696,704 bytes for
`MTLDevice.currentAllocatedSize`, no drawable waits, compiler spills, hangs,
hang risks, or command-buffer errors. It observed 259 transient 131,072-byte
wgpu buffers totaling 33,947,648 bytes, with only one 131,072-byte buffer live
at capture end. The persistent allocation ledger again includes the
92,274,688-byte Foundation dispatch-continuation virtual reservation; its
paired resident footprint was only 0.55 MiB. All raw traces, exports, gcdumps,
dumps, and Xcode scratch were removed after compact summaries were written.

The full pinned Avalonia.Base suite passes 2,865 tests with 12 intentional
skips. Twenty-five focused pinned revision/protocol tests, 73 Avalonia backend
contracts, seven patch contracts, one headless pixel test, and 214 focused
compositor/layer/clip tests pass. Compact evidence is under
`artifacts/avalonia-opacity-mask-channel-20260727`.

### Design gate: inherited drawing-option generations

The remaining inherited `RenderOptions`, `TextOptions`, and BitmapCache
ClearType path was reviewed before changing its synchronization contract.
Avalonia's pinned `RenderOptions.MergeWith`, `TextOptions.MergeWith`, visual
render traversal, and composition serializer are the observable API authority:
the nearest visual supplies every specified value, unspecified values flow
from its parent, and a disabled-ClearType bitmap-cache ancestor converts
subpixel text to grayscale presentation without changing shaping.

The cross-engine review reinforces separating retained content from inherited
presentation state:

- [DirectComposition](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts)
  keeps ordered visual identity and applies a committed property batch
  atomically, parent before child.
- [Direct2D and DirectWrite](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  keep reusable glyph/layout results separate from the render target's current
  transform and text-rendering settings.
- [Win2D layers](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_CreateLayer_6.htm)
  make the subtree/layer presentation boundary explicit.
- [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h)
  retains shaped and laid-out paragraph results independently of final canvas
  presentation.
- [WebRender scene building](https://searchfox.org/mozilla-central/source/gfx/wr/webrender/src/scene_building.rs)
  and [Vello `Scene`](https://docs.rs/vello/latest/vello/struct.Scene.html)
  retain typed scene state for later frame construction rather than rereading
  an application tree during drawing.
- [Parley](https://docs.rs/parley/latest/parley/) and
  [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  keep Unicode/OpenType shaping reusable; antialiasing, hinting, and cache-layer
  ClearType policy do not require reshaping.

Adopted are typed local-option snapshots, nearest-node inheritance, an explicit
descendant presentation generation, transactional acknowledgement, and
re-recording only nodes whose effective presentation changed. Adapted for
ProGPU is an original allocation-free depth-first propagation over the stable
mirror hierarchy, including a single atomic re-record for any flattened
fallback subtree. Rejected are reflection, runtime property lookup, a full
Avalonia-tree rescan, per-descendant transport messages, reshaping text for an
antialiasing change, and accepting stale hidden-node commands when a subtree
becomes visible.

### Exact late-bound presentation variants

The implemented command contract classifies inherited texture sampling, text
rendering, and text hinting as typed presentation dependencies. A compact
command re-record compares every content-bearing field exactly. Differences
limited to declared inherited fields update the compact value in place without
advancing content revision; geometry, glyph, resource, or dependency-mask
changes still invalidate content. Unsupported and general retained commands
remain conservative. This is collision-free `O(C)` comparison for `C` changed
compact commands, with `O(1)` additional storage per visual and no hash,
reflection, or copied glyph array.

Incremental page identity now contains the exact dependency mask and effective
presentation values. Two antialiasing states therefore occupy two bounded page
variants while a real content revision evicts obsolete variants for that
visual and can reuse their arrays. A focused layer test exercises both state
variants and subsequent content invalidation; an Avalonia contract test proves
that presentation-only compact re-recording preserves storage identity while a
glyph-position change is classified as content.

On the Buttons stress fixture, three fresh 120-warm-up/600-measured-frame
Release processes completed 719 typed inherited-option synchronizations each,
with zero fallback nodes. Page compilation fell from 599 per run before exact
classification to zero, while page hits were 67,088 per run. Exact managed
allocation improved from 4,002-4,003 to 3,780-3,782 bytes/frame. Throughput was
118.74, 119.87, and 119.82 FPS; p99 frame time was 10.131, 9.918, and 10.054
ms. An additional Xcode Time Profiler run measured 0.316 ms average compilation
and cleaned 102.8 MB of trace/scratch data. A paired EventPipe run measured
0.355 ms and attributed only 0.03% exclusive CPU to incremental-page replay,
0.02% to the inherited-option refresh, and 0.18% to upload encoding, showing
that an earlier uninstrumented 0.51-0.58 ms series was not a repeatable
comparison-path regression.

The initial exact-classification implementation still uploaded 28,138,752
bytes per 600 frames because mode and effective opacity were encoded in all
603 glyph instances. A first experiment placed text mode in the global
256-byte brush table; per-buffer telemetry proved that brush-index churn
increased transfer to 32,528,352 bytes and dirtied vector, glyph, and brush
buffers. That design was rejected rather than accepted from a microbenchmark.

The corrected design follows the separation already exposed by
[DirectWrite glyph runs and rendering parameters](https://learn.microsoft.com/en-us/windows/win32/directwrite/rendering-to-a-bitmap),
[Win2D `DrawGlyphRun`](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawGlyphRun_2.htm),
[Skia glyph drawing](https://api.skia.org/classSkCanvas.html),
[Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html), and
[HarfBuzz shaping-plan reuse](https://harfbuzz.github.io/shaping-plans-and-caching.html):
glyph identity and placement remain reusable, while current paint/rendering
parameters are supplied separately. ProGPU adopts that concept as an original
typed 32-byte `GpuTextStyle` ABI containing color, effective opacity, and
rendering mode. It adapts it to deterministic incremental-page-local style
indices and one storage binding already compatible with the vector layout.
It rejects per-glyph presentation duplication, global brush-table coupling,
runtime reflection, reshaping, direct retained-page GPU ownership, and a new
pipeline per mode.

Three fresh 120-warmup/600-measured-frame Release runs measured
119.45-119.96 FPS, 3,780 exact managed bytes/frame, 0.3008-0.4241 ms average
compilation, and 0.0140-0.0190 ms average upload time. Every run retained 48
active styles in 1,536 GPU bytes and uploaded exactly 920,064 bytes, all in
the text-style stream. Glyph, vector, brush, index, and texture upload bytes
were zero, a 96.7% reduction from the previous 28,138,752-byte path.
Retained/flattened output remained byte-identical.

Xcode Allocations, Time Profiler, and Metal System Trace then qualified fresh
processes with automatic trace/export/scratch cleanup. The instrumented
1,200-frame run sustained 119.97 FPS, 3,774 bytes/frame, 0.271 ms compilation,
0.011 ms upload, and 1,841,664 style-only upload bytes with zero glyph upload.
Instruments reported zero compiler spills, hangs, hang risks, command-buffer
errors, and drawable waits. The capture observed the known one-time
92,274,688-byte dispatch reservation; its raw traces and XML exports were
removed after compact summaries were generated. Evidence is under
`artifacts/avalonia-text-style-stream-20260727`.

All 77 Avalonia retained-command contracts, all 23 focused layer-render tests,
and all 27 pinned retained-protocol/context tests pass. The full pinned
Avalonia.Base suite passes 2,867 tests with the 12 intentional upstream skips.
The changing inherited-options screenshot is byte-identical between the
retained and comparison lanes, differs from the default screenshot, and
reports zero retained fallback nodes. Compact evidence is under
`artifacts/avalonia-inherited-drawing-options-channel-20260727`; raw EventPipe
and Instruments traces, exports, and Xcode scratch were removed after their
compact reports were generated.

### Typed incremental topology transactions

The next retained-scene gate replaces topology-triggered full-tree
synchronization with an explicit ordered child payload. The pinned Avalonia
server publishes the current typed `ServerCompositionVisual` child list only
when the topology bit is present. The payload is a read-only view consumed on
the same render-thread transaction; it does not copy the list, serialize a
foreign object layout, or expose mutable state to another thread.

The design was checked against the existing full cross-engine gate and these
topology-specific primary sources:

- [DirectComposition visual trees](https://learn.microsoft.com/en-us/windows/win32/directcomp/how-to--build-a-visual-tree)
  retain visual identity, explicit sibling order, and batch tree changes at
  commit;
- [Direct2D and DirectWrite](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  keep drawing and text resources independent from visual-tree ownership;
- [Win2D device/resource ownership](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm)
  keeps resource lifetime tied to an explicit device domain;
- [Skia pictures](https://skia.org/docs/user/api/skcanvas_creation/)
  retain drawing commands independently of the application tree;
- [Firefox rendering architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  serializes a WebRender display list into a retained scene;
- [Vello](https://github.com/linebender/vello) encodes a typed scene for later
  GPU rendering;
- [Parley](https://docs.rs/parley/latest/parley/) and
  [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  reinforce that reparenting visual output must not reshape reusable text.

Adopted are stable visual handles, explicit ordered children, atomic
transaction consumption, and deferred reclamation after the whole transaction.
Adapted for ProGPU is an original two-phase algorithm: topology deltas first
reorder, add, remove, or reparent existing mirror nodes; ordinary property and
content deltas then run against the resulting hierarchy. One final
reachability walk refreshes counts and releases detached handles only after
all parent deltas have been applied. Reparented nodes recompute inherited
drawing options without rebuilding their retained glyph or geometry content.
Rejected are reflection, runtime parent discovery by type name, recursive
full Avalonia-tree synchronization, copied child arrays, foreign serializer
layout, and destroying/recreating a visual merely because its parent changed.

For a topology transaction with `K` published child references and `V`
reachable mirror nodes, synchronization is `O(K + V)` time and `O(1)`
additional per-node storage. The reachability pass is intentionally once per
transaction rather than once per delta. Frames without topology changes keep
the existing unchanged or ordinary incremental path and perform no topology
walk.

The ControlCatalog fixture moves one retained custom visual between two
container parents every frame. Across three fresh
120-warm-up/600-measured-frame Release processes, every run reported 1,800
measured topology synchronizations, zero measured full synchronizations, zero
fallback nodes, a stable 793-node scene, and one custom-visual compilation.
Throughput was 120.27, 120.20, and 120.24 FPS. Exact managed allocation was
3,716 bytes/frame in every run; compilation was 0.2548, 0.3018, and 0.2545 ms.
The retained and comparison screenshots are byte-identical after the
deterministic reparent cycle.

Xcode Time Profiler identified only startup/JIT/window creation stalls; it
reported no hang risks. The paired managed EventPipe capture completed the
600-frame measurement with zero GCs. `MarkReachableMirror` appeared in only
three CPU samples, and `TrySynchronizeTopologyDelta` did not enter the top 30
sampled methods. The instrumented in-app result remained 120.03 FPS,
0.2387 ms compilation, and 3,715 bytes/frame. Raw Xcode traces, 178 MiB of
Xcode scratch, the detailed XML exports, and the raw `.nettrace` were removed;
compact evidence remains under
`artifacts/avalonia-topology-channel-20260727`.

All 27 focused pinned retained-protocol tests and all 77 Avalonia backend
contracts pass. The full retained/comparison pixel matrix now includes the
topology fixture and remains byte-identical with zero fallback nodes.

### Typed adorner dependency and mirror-owned clip reconstruction

The adorner relationship now has its own retained-delta bit and captures the
typed adorned server visual plus clipping flag at publication time. ProGPU
resolves that value through the existing generation-checked visual handle and
stores only the stable mirror relationship. It no longer rereads the Avalonia
visual tree while constructing an adorner clip or routes this change through
the generic appearance synchronizer.

The clip chain is reconstructed from mirror-owned transforms, layout bounds,
and typed geometry. A reusable per-adorner path and clip list provide bounded
storage proportional to ancestor depth. Topology and clip changes refresh all
live dependencies once per transaction; direct relationship and transform
changes refresh only adorners that own or traverse the changed mirror node.
An invalid cross-tree relationship or an unsupported fallback node fails the
incremental transaction and re-enters the existing conservative full
qualification path. There is no reflection, copied hierarchy, runtime patch,
or per-frame relationship allocation.

This follows the same retained identity and explicit clip-stack principles
reviewed for DirectComposition, Direct2D layers, WebRender clip chains, Skia
save/clip state, and Vello scenes, adapted as an original typed ProGPU mirror
algorithm. Unicode shaping and glyph identity remain independent of the
adorner presentation relationship.

The deterministic AdornerLayer gate alternated the adorned relationship for
600 measured frames in three fresh Release processes. Every run reported 600
measured typed adorner synchronizations, zero measured full synchronizations,
zero fallbacks, zero generic complex-appearance synchronizations, 579 stable
scene nodes, and exactly 7,904 managed bytes/frame. Throughput was 120.13,
120.11, and 120.18 FPS; average scene compilation was 0.2469, 0.2687, and
0.2610 ms.

The retained and flattened dynamic screenshots are byte-identical, and the
full pixel matrix remains zero-fallback. An Xcode Allocations, Time Profiler,
and Metal capture completed 1,200 measured relationship changes at 119.89 FPS
and 0.1760 ms average compilation. It attributed 20.92 MiB to live native heap
payload, 26.21 MiB to two IOSurfaces, and 8.80 MiB to live IOAccelerator
resources. The anonymous-VM total includes the already documented one-time
92,274,688-byte dispatch-continuation reservation. The in-app Metal counter
was 29,704,192 bytes; this Xcode version exported no explicit Metal allocation
rows, but reported 764 command-buffer completions and no drawable waits,
compiler spills, hang risks, hangs, or command-buffer errors. Raw traces,
exports, and private Xcode scratch totaling 500.0 MiB were removed after the
compact summaries were generated. Evidence is under
`artifacts/avalonia-adorner-channel-20260727`.

### Removal of the catch-all appearance serializer

The direct-channel inventory is now complete for transform, local bounds,
primitive visibility/opacity, layout clipping, geometry clipping, bitmap-cache
policy, effects, opacity masks, inherited drawing options, topology, and
adorner relationships. The former bit-two whole-appearance channel had no
remaining publisher. Its consumer branch still performed a full
`SynchronizeState`, reevaluated unrelated resources from the live server
visual, and retained a conservative fallback check that could no longer be
reached.

The dead bit and branch are removed while preserving every later bit
assignment. Incremental transactions now accept only immutable typed payloads
captured by the pinned Avalonia server. Unsupported effect, mask, geometry, or
adorner state is validated by its owning channel and fails closed before
partial commit. Full-scene creation keeps the complete typed state
synchronizer and fallback classifier. This reduces branch and source-read
surface without changing serializer layout, public API, rendering quality, or
GPU storage.

### Final preview-27 qualification and runtime identity

ControlCatalog now places the selected windowing platform, renderer,
compositor, and text shaper in the main-window title. Registration is typed and
reflection-free. The configured title is updated after the first rendered
ProGPU frame so the rendering field reports the observed presentation path:
Silk.NET WebGPU surface, Dawn Metal IOSurface, Dawn D3D12 HWND, Dawn Vulkan
Xlib, or the Avalonia framebuffer fallback. The Skia reference reports
Avalonia.Native, Skia, Avalonia retained composition, and HarfBuzz.

A final matched Buttons qualification used the same Release source tree,
2048x1600 physical target, 60 warm-up frames, 180 measured frames, a fixed
120 Hz timer, and three fresh processes per lane. Medians were:

| Window/render/text lane | FPS | Average frame | P99 frame | Allocation/frame | Managed retained | Physical footprint |
|---|---:|---:|---:|---:|---:|---:|
| Avalonia.Native / Dawn Metal / ProGPU | 119.53 | 8.426 ms | 10.052 ms | 3.36 KiB | 21.18 MiB | 359.11 MiB |
| Silk.NET / Dawn / ProGPU | 120.37 | 8.364 ms | 11.227 ms | 3.67 KiB | 22.36 MiB | 329.45 MiB |
| Silk.NET / Dawn / HarfBuzz | 120.46 | 8.354 ms | 12.846 ms | 3.73 KiB | 21.89 MiB | 327.33 MiB |
| Avalonia.Native / Skia / HarfBuzz | 119.57 | 8.402 ms | 10.613 ms | 5.57 KiB | 16.73 MiB | 265.27 MiB |

The fixed-rate workload is presentation-limited, so these values demonstrate
equivalent sustained throughput rather than a renderer speedup. ProGPU/Silk.NET
allocates 34.1% fewer managed bytes per measured frame than Skia, but still
retains 5.62 MiB more managed memory and 64.19 MiB more physical footprint.
The native Dawn lane allocates 39.6% fewer bytes per frame than Skia but retains
93.84 MiB more physical footprint. These residual footprint differences remain
qualified costs, not a claimed leak. One Skia process encountered an unrelated
one-second scheduling stall; medians and per-run tails are therefore reported
instead of its biased mean FPS.

The final retained/flattened pixel gate is byte-identical for nine zero-fallback
ControlCatalog pages and the geometry-clip, linear/conic/picture-mask,
topology, adorner, blur, drop-shadow, inherited drawing-option, and BitmapCache
scale/snap/ClearType fixtures. The full ProGPU suite passes 2,500 tests. The
exact Avalonia ABI gate passes, the packed renderer and Silk.NET assemblies
pass the no-reflection audit, and isolated package consumers pass single-window
and shared-device disposal-order smokes with zero fallback nodes. A trimmed
22,912,080-byte macOS arm64 NativeAOT consumer also renders successfully with
one retained scene and zero fallbacks.

Evidence is retained under
`artifacts/avalonia-final-buttons-matrix-20260727`,
`artifacts/avalonia-final-retained-pixels-20260727`, and
`artifacts/avalonia-final-package-stack-20260727`.

### Embedded same-device activation and retained-atlas recovery

The embedded sample exposed an initialization-order defect that aggregate
frame telemetry could not detect. The outer Avalonia renderer had already
created the first `WgpuContext`, but the embedded compositor's shared-device
lookup considered only contexts registered by Silk.NET windows. It therefore
created a second native WebGPU device. Avalonia successfully imported the
process-local texture lease, but the drawing context correctly rejected the
texture because its owning device did not match the render target. The shell,
navigation, and frame counters continued updating while the embedded content
area stayed at its clear color.

The correction follows WebGPU's
[`GPUObjectBase`](https://gpuweb.github.io/gpuweb/#gpuobjectbase) device
ownership rule and Avalonia's public
[`IExternalObjectsRenderInterfaceContextFeature`](https://github.com/AvaloniaUI/Avalonia/blob/12.0.5/src/Avalonia.Base/Platform/IExternalObjectsRenderInterfaceContextFeature.cs)
lifetime boundary. The first healthy ProGPU context is now adopted into the
typed shared-device registry before a new context is considered. Imported
textures still require exact native device identity; unrelated devices are
never copied or treated as compatible. The host also reports a device-domain
mismatch as a failed qualification instead of silently accepting an empty
content frame.

The sample benchmark now reads the final surface and requires pixels that
differ from the expected clear background in addition to frame, backend,
presentation-path, and retained-scene telemetry. All eight embedded sample
pages passed that pixel-bearing gate with both ProGPU OpenType shaping and the
optional HarfBuzz comparison lane. A real macOS window capture also shows the
vector-drawing sample in the previously empty content region.

The same qualification found a separate bounded-atlas recovery failure on the
ControlCatalog ColorPicker page. An incremental frame introduced two paths
after a previous complete scene had filled a rectangular atlas. The old reset
logic repacked only paths touched by that partial frame; the mandatory
same-frame full retry then needed six recently used paths that had just been
discarded and could exhaust its single permitted recovery transaction.

The clean-room fix retains a bounded previous complete live set, unions it
with the current partial set, and probes the existing multi-strategy rectangle
packer at the current and permitted larger atlas dimensions. If growth is
needed, it creates the final empty recovery texture directly rather than
copying coverage whose coordinates are about to change. The retry remains
single-shot and fail-closed, packing work is bounded by the live atlas entry
limit, and a conservative union that cannot fit falls back to the current
transaction rather than looping. A regression reproduces the 12-path prior
scene plus 8-path partial update and proves that all 14 paths survive the one
reset. ColorPicker then renders through both Silk.NET shaping lanes without a
second atlas abort.

### Final ControlCatalog census and allocation attribution

The final source-built ControlCatalog census completed all 70 pages in five
isolated lanes: Silk.NET/ProGPU shaping, Silk.NET/HarfBuzz, Avalonia
Native/Dawn/ProGPU shaping, Avalonia Native/Dawn/HarfBuzz, and the
Avalonia Native/Skia/HarfBuzz reference. All 350 processes completed with no
qualification failures. The OpenGL pages correctly display their unsupported
context message under the WebGPU compositor; this is visible application
content, not an implemented OpenGL-interop claim. A longer alternating
Buttons, ColorPicker, and Composition comparison completed another 45 of 45
runs.

The Composition allocation trace found a ProGPU-specific hot path that the
aggregate counters had hidden. `DrawingContextImpl.DrawEllipse` created a
complete retained ellipse path before it knew whether the brush required a
geometry clip, then separately recorded the typed analytic ellipse. Ordinary
solid and gradient brushes never consume that path. The resulting
`ArcSegment`, `PathSegment[]`, `PathFigure[]`, `PathFigure`, and
`ProGpuPathShape` samples accounted for the repeatable allocation gap.

The corrected path classifies brush content first and materializes the
ellipse clip only for image and scene brushes. Solid and gradient ellipses
stay on the allocation-free typed primitive path. The post-change 600-frame
trace estimated 3,585,604 allocated bytes, down from 9,628,724 before the
change and below the matched Skia trace's 3,791,184 bytes. All path and arc
allocation types disappeared. Three fresh Composition processes averaged
6,225 bytes/frame for ProGPU versus 7,192 for Skia, a 13.4% reduction, while
the representative p99 frame times remained 9.332 and 9.291 ms. ProGPU
retained one custom visual, compiled it on every active frame, used zero
fallback nodes and zero intermediate textures, and preserved the existing
pixel and contract gates.

The paired forced-GC and macOS live-memory capture separates memory domains
instead of treating process footprint as one allocator. During the active
custom-visual workload ProGPU reported 20,060,704 managed bytes,
9,797,632 explicit Metal bytes, and a 307,266,712-byte physical footprint;
Skia reported 15,415,816 managed bytes and a 279,758,024-byte physical
footprint. Root-filtered live heaps were 10,974,455 and 8,419,453 bytes, so
the current typed ProGPU scene costs 2,555,002 additional rooted managed
bytes. Native allocator payload moved in the opposite direction:
23,159,856 bytes for ProGPU versus 28,184,000 for Skia, 5,024,144 bytes
(17.8%) lower.

The apparent active AGX difference is a high-water residency effect, not a
growing resource set. Across the diagnostic hold, ProGPU physical footprint
fell from 308,071,629 to 135,476,019 bytes; `owned unmapped (graphics)` fell
from 141,557,760 to 1,802,240 and IOAccelerator residency from 5,292,032 to
2,506,752 bytes. Its IOSurface residency stayed at 6,553,600 bytes. Skia
ended with 107,793,613 bytes of `owned unmapped (graphics)` and
39,321,600 bytes of IOSurface residency. Both processes reserved the same
92,274,688-byte libdispatch continuation address range, but only about
0.5--0.6 MiB was resident.

Final attached Xcode Allocations, Time Profiler, and Metal System Trace
captures reported zero compiler spills, potential hangs, hang risks, and
command-buffer errors in both lanes. In the five-second Metal window,
ProGPU completed 919 command buffers with zero drawable waits. Skia recorded
92 drawable waits totaling 715.545 ms, with an 8.089 ms maximum, alongside
186 submissions and 773 completions. This is a capture-specific latency
observation, not a universal renderer throughput claim. The attached traces
began after resource creation and exported no Metal allocation rows, so their
zero resource count is not used as a memory value; the in-app descriptor
ledger, `vmmap`, native heap, and IOSurface/IOAccelerator regions provide the
resource evidence.

The reusable profiler retained only compact JSON, Markdown, process logs, and
manifests. It deleted 740,861,595 bytes of raw `.trace` bundles, exported XML,
and Xcode scratch from this final pair. Evidence is under
`artifacts/controlcatalog-final-census-fixed-20260727`,
`artifacts/controlcatalog-composition-ellipse-after-20260727`,
`artifacts/controlcatalog-final-allocation-stacks-20260727`,
`artifacts/controlcatalog-final-managed-memory-20260727`, and
`artifacts/controlcatalog-final-instruments-20260727`.
