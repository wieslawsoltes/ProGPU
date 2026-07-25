# ProGPU voxel engine architecture

Status: first playable vertical slice, 25 July 2026.

## Decision

ProGPU's existing `Viewport3D`, `MeshGeometry3D`, OBJ reader, camera matrices, and
`Mesh3DExtensionPipeline` remain the right API for arbitrary imported and modeled
objects. The voxel engine reuses the lower-level ProGPU infrastructure:

- compositor extension lifecycle and offscreen texture composition;
- physical-pixel render targets, depth/MSAA policy, `GpuBuffer`, `GpuTexture`,
  `RenderPipelineCache`, and `ShaderResource`;
- the same `RenderCommand`/compiled-scene integration and matrix conventions;
- the normal visual animation update phase and typed WinUI input events.

The generic mesh pipeline is deliberately not the voxel hot path. It de-indexes every
mesh, uploads generic per-model material records during compilation, uses an OBJ-shaped
object model, and submits orbit-camera interactions. A block world needs editable dense
chunk storage, neighbor-aware surface extraction, indexed packed vertices, versioned
chunk uploads, first-person collision, grid ray queries, and visibility-driven chunk
submission. Adding those concerns to `MeshGeometry3D` would make both APIs less clear.

The resulting package boundary is:

```text
ProGPU.Voxel
  block data -> sparse world -> dirty chunks -> greedy indexed meshes
       |              |               |              |
       +---------- player/collision and grid DDA -----+

ProGPU.Voxel.WinUI
  background generation -> frame simulation -> frustum-visible render payload

ProGPU.Scene
  VoxelTerrainExtensionPipeline -> versioned visible-geometry arena -> pure WGSL shading
```

`ProGPU.Voxel` has no UI or GPU dependency. A server, editor, test runner, or another
frontend can use the same world, mesh, collision, and ray APIs.

## Clean-room research record

No foreign implementation source was copied, ported, translated, or used as a file or
control-flow template. The implementation was written against public contracts,
algorithm descriptions, and independently designed ProGPU types.

Primary sources examined:

- The [WebGPU specification](https://gpuweb.github.io/gpuweb/) and
  [WGSL specification](https://www.w3.org/TR/WGSL/) define vertex/index/storage buffer
  usage, render passes, resource binding, shader layout, and direct indexed draws.
- The [W3C Pointer Lock 2.0 specification](https://www.w3.org/TR/pointerlock-2/)
  defines the user-activated browser capture lifecycle, `movementX`/`movementY`,
  and `pointerlockchange` used by the first-person input host. Desktop follows
  Silk.NET's typed `CursorMode.Raw` capability contract and falls back to
  `CursorMode.Disabled`; browser uses the broadly supported baseline lock request
  because unadjusted motion is an optional capability.
- Mozilla's [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  describes display-list to scene to culled-frame separation and demand preparation of
  GPU resources.
- Skia's [canvas creation documentation](https://skia.org/docs/user/api/skcanvas_creation/)
  describes backend-owned surfaces and GPU-context resource caches; the
  [SkCanvas overview](https://skia.org/docs/user/api/skcanvas_overview/) keeps draw
  state explicit at call sites.
- Microsoft's [retained versus immediate mode](https://learn.microsoft.com/en-us/windows/win32/learnwin32/retained-mode-versus-immediate-mode)
  guidance informed the retained world plus explicit frame-payload boundary.
  [Direct2D and DirectWrite](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  explicitly recommend reusing prepared glyph positions instead of recomputing layout;
  ProGPU applies the same prepare-on-change rule to chunk meshes.
- [Vello](https://github.com/linebender/vello) separates scene encoding from WebGPU
  context setup and moves bounded parallel work to GPU shaders.
- [Parley](https://github.com/linebender/parley) and the
  [HarfBuzz shaping model](https://harfbuzz.github.io/shaping-concepts.html) preserve
  reusable semantic CPU results. They informed the decision to keep HUD text in
  ProGPU's existing shaping stack rather than creating text in the voxel shader.
- Mikola Lysenko's algorithm analysis,
  [Meshing in a Minecraft Game](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/)
  and [part 2](https://0fps.net/2012/07/07/meshing-minecraft-part-2/), defines the
  observable greedy rectangle-merging problem and its linear voxel-volume cost.
- Amanatides and Woo's
  [A Fast Voxel Traversal Algorithm](https://physique.cmaisonneuve.qc.ca/svezina/projet/ray_tracer/download/A_Fast_Voxel_Traversal_Algorythm_For_Ray_Tracing.pdf)
  defines incremental uniform-grid traversal used by block targeting.
- Godot's [MultiMesh optimization guidance](https://docs.godotengine.org/en/stable/tutorials/performance/using_multimesh.html)
  documents the batching-versus-culling tradeoff. ProGPU batches geometry per chunk so
  a chunk remains the visibility and update unit.
- Unreal Engine's
  [Nanite virtualized geometry overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine)
  motivates visible-detail work, compressed internal geometry, fine-grained streaming,
  and avoiding object-count-scaled CPU work. Nanite's mesh-cluster format itself was
  rejected because cubic editable terrain and baseline WebGPU have different constraints.

## Cross-engine comparison

| Concern | Production/research pattern | ProGPU decision |
|---|---|---|
| Startup and lazy initialization | Skia/Vello separate GPU context setup from scene description; WebRender prepares a frame from a larger scene. | World generation and initial meshing run on a worker. Shaders, pipelines, bind groups, textures, and GPU chunk buffers are created on first visible use. |
| Reusable layout/scene results | DirectWrite caches glyph positions; HarfBuzz/Parley preserve shaped/layout results; WebRender retains a scene. | Block data is authoritative, each chunk owns a mesh generation, and unchanged mesh arrays/GPU buffers are reused. |
| Visibility culling | WebRender culls scene to frame; Nanite limits work to visible detail; Godot notes whole-batch culling limits. | CPU homogeneous-clip frustum and distance tests select chunks. Individual blocks are not scene objects. |
| Cache keys and eviction | Skia keys GPU resources; WebRender uses resource caches; Nanite streams fine-grained data. | Stable transfer-geometry identity, mesh generation, origin, and visible ordering key a packed vertex/index arena. Current demo residency is bounded by generated chunks; streaming worlds will add explicitly budgeted arena pages. |
| Demand-driven upload | WebRender prepares only resources needed by a frame. | Only visible chunks enter the payload; only new or changed generations upload. |
| Worker preparation | WebRender scene building and production engines move reusable preparation away from presentation. | Terrain generation and initial greedy meshing are worker-owned. Interactive single-chunk remesh is synchronous today because a 16³ chunk is bounded; a scheduler seam is planned for streaming. |
| GPU organization | Vello uses GPU compute where parallelism wins; WebGPU exposes explicit render resources; Godot batches instances. | WGSL handles transforms, procedural texels, lighting, selection, water motion, and fog. CPU meshing remains cheaper and simpler for small editable chunks. Chunk origins and indices are baked into a visible-geometry arena for one indexed terrain draw. |
| DPI/subpixel/hinting | Skia, DirectWrite, HarfBuzz, and Parley keep text-specific DPI and shaping behavior. | The 3D framebuffer uses physical pixels. HUD text remains in ProGPU.Text; voxel geometry does not invent a text or snapping path. |
| Fallback fonts and variable fonts | HarfBuzz/Parley/DirectWrite treat fallback and variation state as shaping inputs. | Unchanged and deliberately outside the voxel renderer. The sample uses the existing application font stack. |
| Device loss and invalidation | GPU engines recreate device-owned caches while preserving CPU scene data. | The world and chunk meshes are CPU-owned. The compositor extension owns and disposes GPU buffers; texture recreation is size/sample aware. Full device-loss replay can rebuild every GPU resource from retained mesh arrays. |

## Implemented API and cost model

- `VoxelWorld`: sparse `Dictionary<VoxelChunkPosition, VoxelChunk>` storage. Average
  block get/set is O(1). Boundary edits invalidate both participating chunks.
- `VoxelChunk`: fixed 16³ `ushort` storage, O(1) mutation, explicit content and mesh
  generations.
- `VoxelGreedyMesher`: three-axis, slice-mask rectangle merging. Work is
  O(3 × 17 × 16²), temporary storage is O(16²), and output is O(Q) for Q merged quads.
- `VoxelRaycaster`: uniform-grid DDA, O(K) for K crossed cells, O(1) storage.
- `VoxelPlayerController`: axis-separated AABB collision, fixed small overlap query per
  movement axis.
- `RelativePointerCapture`: ProGPU-specific first-person input extension outside the
  `Microsoft.UI.Xaml` namespace. Desktop uses Silk raw mouse mode with a disabled-cursor
  fallback; browser uses the Pointer Lock API and summed coalesced motion. Capture ends
  on Escape, host focus loss, unload, or a platform-initiated lock loss without adding
  non-WinUI members to `PointerRoutedEventArgs`.
- `VoxelFrustum`: conservative eight-corner homogeneous clip test, fixed O(1) work.
- `VoxelTerrainExtensionPipeline`: two power-of-two GPU arenas for visible indexed
  geometry, generation/origin/order-sensitive repacking, no per-chunk storage buffer,
  exact uniform change detection, and one indexed terrain draw. Stable frames perform
  no geometry or uniform upload. Repacking is O(V + I) when an edit or visibility
  change alters the arena, for V vertices and I indices.
- `VoxelGameView`: background deterministic terrain generation, first-person movement,
  click-to-capture continuous mouse look, immediate left-button mining/right-button
  placement, wheel and number-key hotbar selection, Escape release, seven block
  materials, selection highlighting, physical-pixel depth/MSAA targets, procedural WGSL
  surface detail, directional light, and fog.

The WGSL module is embedded and loaded through `ShaderResource`. Its header documents
algorithm, time, and space complexity. It performs no texture fetches: the first slice
uses deterministic procedural block texels so the sample has no copied or licensed game
assets.

## Validation and next engine tiers

The focused tests cover negative chunk coordinates, indexed greedy output, full-chunk
surface collapse, material merge boundaries, cross-chunk invalidation, DDA targeting,
player collision, frustum rejection, and deterministic generation.

Release measurements on 25 July 2026 used arm64 macOS binaries, seed 1337, and a
radius-three world. Desktop measurements used a fresh process, 180 warmup frames and
600 measured frames with VSync disabled unless noted:

| Measurement | Result |
|---|---|
| Generated world | 72 chunks, 191,696 solid blocks |
| CPU generation | 2.283 ms median over five iterations |
| Initial greedy meshing | 12.954 ms median over five iterations |
| Surface reduction | 30,776 visible block faces to 4,545 merged quads, an 85.23% reduction |
| Retained mesh payload | 18,180 vertices, 27,270 indices, 545,400 bytes |
| Final desktop Release | 2.073 ms average total frame, 12.167 ms maximum, zero frames over 16.67 ms |
| Final desktop steady allocation | 1,307 managed bytes per complete gallery frame; no measured GC collections or pause |
| Browser Release AOT publish | 83 assemblies processed; both voxel assemblies compiled to WebAssembly AOT |
| Real Chromium WebGPU smoke | 72 chunks rendered; selection, movement, relative pointer-lock look, targeting, and block clicks exercised; zero console errors or warnings |

The final input build was republished and rerun after correcting the right-handed
`CreateLookAt` horizontal convention. A 60-frame warmup plus 300-frame desktop
NativeAOT run measured 5.6202 ms average total frame time, 17.7037 ms maximum,
0.3841 ms average compilation, 1,117 managed bytes per frame, and zero GC
collections. The focused input suite passed 39 relative-pointer/touch tests and all
11 voxel tests, including explicit screen-right strafe and mouse-yaw sign
regressions. The browser AOT artifact was then loaded in Chromium with zero console
errors or warnings; a physical click acquired Pointer Lock and Escape released it.

### Xcode Instruments profile and optimization

The desktop profile used Xcode 16 `xctrace` Time Profiler and Game Memory against the
same Release executable and benchmark window. The retained trace artifacts are ignored
build outputs under `artifacts/voxel-instruments`; `tools/profile-voxel-instruments.sh`
reproduces the captures.

Game Memory exposed two costs that the managed allocation counter could not show:
28 separately rounded 128 KiB vertex buffers plus 28 separately rounded 128 KiB index
buffers for the visible chunks, and a per-frame WebGPU staging stream caused by
rewriting unchanged records/uniforms. The first optimization packed chunk geometry into
two arenas, changed the depth target from `Depth32Float_Stencil8` to `Depth24Plus`,
and stopped stable uniform updates when the world has no animated water.

| Instruments measurement | Before | Shared arenas | Change |
|---|---:|---:|---:|
| Voxel vertex buffers | 3,473,408 B / 28 resources | 278,528 B / 1 resource | -92.0% |
| Voxel index buffers | 3,670,016 B / 28 resources | 65,536 B / 1 resource | -98.2% |
| Depth target at 2028×1180 | 12,845,056 B | 10,092,544 B | -21.4% |
| Labelled voxel render resources | 30.34 MB | 20.79 MB | -31.5% |
| WebGPU staging activity over the trace | 7,093 writes / 865.44 MB | 5,516 / 658.98 MB | -22.2% writes, -23.8% bytes |
| `MTLDevice.currentAllocatedSize` peak | 89,751,552 B | 87,179,264 B | -2,572,288 B |

The second optimization removed the remaining 128 KiB chunk-record storage buffer,
baked chunk origins and base indices into the arena, removed the shader storage lookup,
and collapsed 28 visible-chunk calls to one indexed terrain draw. The final Time
Profiler run reported 0.246 ms compilation, 0.438 ms compositor time, 1.981 ms total
frame time, 11.0 ms maximum, 1,172 managed bytes/frame, and zero missed frame budgets.
An independent final run reported 0.212 ms compilation and 0.371 ms compositor time;
the spread is expected from macOS surface acquisition and profiler overhead, so both
runs are retained rather than selecting only the fastest result.

The browser run also exposed a deferred Fluent-theme module-constructor edge in the
trimmed AOT startup path. `FluentThemeResources` now explicitly runs its generated,
idempotent module registration before URI resolution; the republished artifact passed
the same real-browser smoke.

This slice is a playable engine foundation, not a claim that every large-world system is
finished. The public boundaries intentionally leave room for these measured follow-ups:

1. camera-centered chunk streaming with cancellable generation and a byte-budgeted CPU/GPU LRU;
2. worker remesh queues with generation-stamped publish and stale-result rejection;
3. texture-array material packs, mipmaps, alpha-tested foliage, and a separate transparent pass;
4. GPU Hi-Z occlusion and indirect submission where target WebGPU capabilities make it a win;
5. sunlight/block-light propagation, persistence, entities/ECS, networking, and deterministic simulation;
6. cold-start, first-interaction, sustained frame-time percentile, upload-byte, residency,
   browser AOT, and image-regression dashboards on release binaries.

GPU-driven occlusion, meshlets, or a Nanite-like hierarchy are not enabled blindly:
greedy chunks already make large flat voxel surfaces extremely compact, and every added
stage must beat the current final-binary frame-time and memory measurements without
reducing quality or edit latency.
