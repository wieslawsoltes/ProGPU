# Suntrail and the reusable game engine

The immediate engine scope is the rendering cost Suntrail actually has. `ProGPU.GameEngine`
is a separate library depending on `ProGPU.Backend`, not WinUI or the sample. Its first
consumer is Suntrail's public drawing extension. This is a foundation in development,
not a completed general-purpose engine or a claim of AAA quality.

## Evidence and design decision

The September 5 iPhone workload spends roughly 30–34 ms in fragment work while managed
simulation takes about 0.05 ms. World shader specialization helped nominal-temperature
runs but did not preserve the frame-rate gain in the warm run. Generating rock cells,
foliage, mountain shading and noise for every covered pixel on every frame is the
architectural cost to remove. Another hash optimization or a large ECS rewrite does
not address that evidence.

The new flow is:

1. Game data produces visible instances, retaining painter order.
2. A material compiler requests only visible regions of immutable procedural artwork.
3. Engine residency pins existing requests, reserves bounded misses, and evicts only
   pages not used by the current frame. Generation checks reject stale handles.
4. The GPU compiles misses into material pages using the original canonical Suntrail
   artwork functions. A page becomes usable only after its bake submission.
5. Frame rendering samples resident artwork and applies current light emitters, tint,
   clipping and vignette. Water, characters, articulated foliage and effects remain live.
   Tree wind is the same affine sway, applied to cached quad vertices.

## Implemented boundary

`MaterialPageCache<TKey>` is a typed, fixed-capacity CPU residency table. The key belongs
to the material author. Lookups are expected O(1); a miss scans at most the fixed slot
count. Two-phase pinning prevents an early miss from evicting a later visible request.
Reserved pages cannot be evicted. Commit/cancel make bake readiness explicit.

`MaterialPageAtlas` owns device-local GPU storage and gutter/UV geometry. Suntrail selects a 4992-square
RGBA16Float allocation costing 190.125 MiB (under a 192 MiB budget), with 1444 independent 128-square interiors and
one-texel gutters. It never grows, performs no readback, and cannot cross device domains.
`MaterialPageInstance` is a contiguous, immutable 96-byte instanced transport.

Suntrail owns material classification, immutable recipe keys, its canonical shader,
world-space light behavior, and the procedural compiler adapter. Keys include original
size, material parameters/seed, world, dungeon, physical texel density and page coordinate.
Camera movement, local lights and tint do not invalidate static appearance. Changed
recipes/DPI request different pages. Device recreation creates a new atlas and table.
The Suntrail adapter currently owns one active game view per compositor; multi-view scene rendering is a later engine boundary. The WinUI compositor continues owning UI text, layout, glyph caches and presentation.

The initial 128 MiB candidate left 117 visible pages uncached on the device workload.
The final bounded setting fits that live set with zero visible fallbacks. Measured median iPhone fragment time is 16.51 ms versus 30.60 ms for the direct reference; full-frame pacing still misses the 60 FPS goal. See [validation](suntrail-validation.md).
The library accepts an explicit atlas extent; the game owns its platform memory choice.

Compilation is capped at 32 pages per preparation. Missing pages render through the
original live material equations; no geometry is silently dropped. All resident pages
share one texture binding. Contiguous cached runs combine different materials in one draw;
dynamic runs retain the existing bounded shader variants. Normal replay allocates no
managed objects and does not upload unchanged page instances or rebake resident pages.
First-use pipeline creation, cache convergence and scrolling misses must be measured
separately from settled replay. This first implementation does not yet provide level-load
prewarming or predictive page requests.

## Quality and limits

This replaces analytic material evaluation with at-least-native-density, filtered material assets;
it is intentionally not a bit-identical screenshot cache. The original material generator,
world differences and directional shading remain. Cliff normals now use a fixed half-world-unit
height stencil instead of screen derivatives: texture assets must not bake camera-phase-dependent
lighting. This refactors the original height field without replacing its rock/soil materials. Storage retains HDR values in float16;
there is no resolution scale reduction. At 1x display DPI, materials compile at 2x density to protect fine grain; Retina uses native density. Linear sampling and material-space AA can change
subpixel edges compared with evaluating analytic AA at each translated screen pixel.
Focused all-world images record mean/RMS error, outlier coverage and visible seams before
enabling this path. The direct analytic path remains available for comparison.

There are no mipmaps or perspective minification in this first orthographic consumer.
Full 3D must add a material representation with normals/roughness, mip generation,
perspective sampling, mesh geometry, depth, camera controls and volumetric collision.
It must not reinterpret this 2D appearance cache as a complete physically based 3D material.

## Primary research and clean-room provenance

Only public architecture/behavior is adopted. No third-party implementation was copied.
All artwork equations originate in this repository's
`src/ProGPU.Samples.Suntrail/Shaders/Suntrail.wgsl` at `977aa449`; the material/compiler
split directly refactors that original ProGPU source.

- [Unreal runtime virtual texturing](https://dev.epicgames.com/documentation/en-us/unreal-engine/runtime-virtual-texturing-in-unreal-engine): adopt cached, camera-independent material appearance and bounded updates. Adapt to a small deterministic CPU-visible page set; reject GPU feedback tables, compressed virtual-texture streaming and enormous world infrastructure for this workload. Animated appearance stays live.
- [Unity 6 SRP Batcher](https://docs.unity3d.com/6000.0/Documentation/Manual/SRPBatcher.html): adopt persistent GPU material data and contiguous per-instance buffers. Avoid exploding the shader-variant count for each asset. This is not a direct port of Unity batching.
- [Godot GPU optimization](https://docs.godotengine.org/en/stable/tutorials/performance/gpu_optimization.html): texture/material reuse and batching inform a common page binding; visibility granularity remains small instead of combining the whole level into one uncullable mesh.
- [Direct2D performance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance) and [Win2D offscreen drawing](https://microsoft.github.io/Win2D/WinUI3/html/Offscreen.htm): reusable GPU resources have an explicit owner; cached content needs explicit initialization, alpha and invalidation rules. No D2D/Win2D implementation or platform bridge is introduced.
- [WebRender](https://github.com/servo/webrender): retain the visibility/preparation/render separation already recorded in the Suntrail design research. The earlier decision to avoid artwork texture caches is superseded by measured iPhone fragment cost.
- [Vello](https://github.com/linebender/vello): retain a separate CPU scene and GPU work stage. A general compute vector rasterizer is not the needed replacement for these fixed artwork functions.
- [Skia text architecture](https://skia.org/docs/dev/design/text_overview/), [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/programming-guide), [Parley](https://github.com/linebender/parley), and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html): preserve CPU shaping/layout reuse, font fallback and existing text caches. This change does not introduce a second font engine, alter DPI/subpixel rules for UI text, or move shaping to the GPU.

Startup, lazy pipelines, scene reuse, culling, texture residency, demand-driven writes,
batching, DPI and device ownership are directly applicable. Worker preparation is deferred
until profiling shows CPU recipe work is significant. Glyph eviction, fallback fonts,
variable-font state and shaping are unchanged and remain in ProGPU's existing text stack.

## Managed/native applicability

This library consumes the existing public managed WebGPU backend and is shared unchanged
by Desktop, iOS and Browser. Suntrail currently has no native C++ scene/game host, so there
is no second game material implementation to port. The core managed/native compositor,
wire ABI, atlas algorithms and text shaders are unchanged. Any future C++ game host must
consume the same canonical artwork shader and matched page/quality contract; a generic
core rendering change would require paired managed/native implementation and regressions.

## Remaining engine work in task order

First finish material-page image/performance gates, residency tuning and level-load
preparation, then install the optimized iPhone build. Next introduce only the shared
scene/chunk and asset identities needed by connected levels and editor invalidation;
extract stable simulation timing/input actions when the actual second consumer needs
those boundaries. Complete connected rooms, Mario-compatible import/export and authoring.
Full 3D is last: perspective camera, actual meshes/depth, material channels/mips, lighting,
3D movement/collision and editor controls. Keep Suntrail rules/content outside the engine.
