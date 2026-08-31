# ProGPU.CAD retained 3D camera research and contract

Status: implemented checkpoint, 2026-08-31

## Scope and clean-room provenance

This checkpoint separates the shared CAD shell's interactive perspective state
from immutable mesh generations. Orbit, pan, wheel/keyboard zoom, snapshot
replacement, explicit Fit, managed `Viewport3D`, and the optional native scene
adapter now use one typed camera contract. An edit may change the snapshot's
large-WCS rebase origin without resetting the user's view.

No third-party implementation source was copied, ported, translated, or used as
a source-text template. Approved original in-repository ProGPU provenance is:

- `17d41098:src/ProGPU.CAD.Sample/CadSampleView.cs`, whose former
  `RebuildMesh3DView` body is the exact source of the retained Z-up fit constants
  (`1.8` radius, `(r,-r,0.8r)` offset, `42` degree field of view, and bounded
  near/far planes);
- `src/ProGPU.CAD/CadPlanViewport.cs` for the existing original ProGPU rule that
  camera state remains independent of snapshot geometry and compensates a
  replacement rebase in constant work;
- `src/ProGPU.CAD/CadMesh3DSceneCompiler.cs` for camera-independent immutable
  triangle batches;
- `src/ProGPU.WinUI/Controls/Viewport3D.cs` and
  `src/ProGPU.Scene/Extensions/Mesh3DExtensionPipeline.cs` for the existing
  managed perspective/orbit and canonical GPU matrix contracts;
- `src/ProGPU.CAD.Native/CadNativeMesh3DSceneCompiler.cs`,
  `src/ProGPU.Backend.Native/NativeSceneStreamBuilder.cs`, and
  `src/ProGPU.Scene/Shaders/Native3D.wgsl` for the existing one-resource,
  one-command native replay contract.

The new two-part camera position, coordinator, validation, statistics, host
wiring, and differential tests are original ProGPU code. ACadSharp supplies the
mutable DXF/DWG document and is not consulted by camera-only operations.

## Primary-source architecture review

| Stack | Primary evidence | Finding and ProGPU decision |
|---|---|---|
| Skia / SkParagraph | [SkCanvas overview](https://skia.org/docs/user/api/skcanvas_overview/), [Skia coordinate spaces](https://skia.org/docs/user/coordinates/), [Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/) | Skia keeps transforms on the drawing context instead of rewriting local geometry, and shaped/formatted text is a reusable result independent of its renderer. Adopted: mesh coordinates and all existing CAD glyph runs remain immutable while the view changes. |
| Direct2D / DirectWrite / Win2D | [Direct2D transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-transforms-overview), [DirectWrite and Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite), [Win2D quick start](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/quick-start) | Direct2D applies a render-target transform to subsequent draws; DirectWrite explicitly gains performance by reusing positioned layout objects; Win2D recommends creating visual resources once and reusing them per frame. Adopted: a generation owns mesh resources, while camera matrices are late view state. |
| WebRender | [Servo display-list/spatial-tree design](https://github.com/servo/servo/wiki/Design/a88683ec289b53b9f50242d4c27fcc22ddb76039), [upstream scene builder](https://searchfox.org/mozilla-central/source/gfx/wr/webrender/src/scene_builder_thread.rs), [upstream profiler counters](https://searchfox.org/mozilla-central/source/gfx/wr/webrender/src/profiler.rs) | Spatial-node transforms and scroll offsets update without layout, while profiler counters distinguish display-list, scene-build, frame-build, upload, renderer, and GPU costs. Adapted: ProGPU exposes separate scene-compilation and camera-only counters instead of hiding an entity traversal inside “frame time.” |
| Vello / Parley | [Vello vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [current Vello scene API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), [Parley API](https://docs.rs/parley/latest/parley/) | Vello's retained-fragment design keeps global transforms out of path data; its current scene append operation is explicitly O(N), so it is not treated as an O(1) camera mechanism. Parley reuses font/layout contexts and permits re-line-breaking without recreating unchanged text. Adopted: retain immutable CAD mesh/text results; rejected: claiming scene append or rebuild is a camera update. |
| HarfBuzz | [HarfBuzz overview](https://harfbuzz.github.io/what-is-harfbuzz.html), [buffers and shaping](https://harfbuzz.github.io/harfbuzz-hb-buffer.html) | HarfBuzz maps Unicode buffers to positioned glyph information; it is not a camera or mesh system. Existing ProGPU shaping/fallback results remain generation-owned and are neither reshaped nor invalidated by this change. |
| WebGPU | [WebGPU resource binding](https://gpuweb.github.io/gpuweb/#resource-binding), [WGSL uniform address space](https://gpuweb.github.io/gpuweb/wgsl/#address-spaces) | Read-only uniform bindings are the appropriate late-bound location for projection/view state. The existing managed and native Native3D pipelines already use that separation, so no new shader or binding is introduced. |

The required broader audit found no reason to change startup/lazy font
initialization, shaping/layout reuse, fallback fonts, variable-font state,
glyph/texture/path cache keys, visibility culling, worker preparation, atlas
eviction, DPI/subpixel text behavior, or device-loss invalidation. Those are
owned by the unchanged retained 2D/text pipelines. Mesh scene compilation and
GPU upload still occur only when the immutable CAD generation changes. This
checkpoint does not claim incremental mesh chunks or shared block instances.

## Camera and generation contract

`CadMesh3DViewport` stores position as a two-part value: a double WCS anchor and
a double local offset. A fitted camera anchors at the snapshot rebase, so adding
an offset such as `14.4` to a coordinate near `10^12` does not first lose the
offset to cancellation and then subtract it again. `WorldPosition` is the
semantic WCS view, while `CreateProjectionCamera` evaluates

`(positionAnchor - currentRebase) + positionOffset`

and narrows only that local result to finite `Vector3`. Look and up directions,
near/far planes, and field of view are validated and retained. Parallel look/up
vectors, non-positive planes, invalid field of view, non-finite values, and a
rebased position outside float range fail explicitly.

`CadMesh3DViewCoordinator.ReplaceSnapshot` performs exactly one
`CadMesh3DSceneCompiler` call. A new/reset document or the first non-empty mesh
generation receives the established fitted view. An edit in the same session
retains the exact two-part WCS camera and changes only its current rebase.
Explicit Fit is available in both plan and 3D modes. Empty mesh generations
disable the depth view and own no camera.

`ProjectionCamera.SetView` publishes position plus look direction atomically.
The built-in orbit controller owns one target, radius, azimuth, and elevation;
pan changes only that target. One pointer/keyboard step therefore exposes one
complete camera change instead of two transient invalidations. The sample
captures that result through `CadMesh3DViewCoordinator.CaptureCamera` and does
not touch the current snapshot, ACadSharp session, mesh batches, or native
scene.

## Complexity, counters, and boundary behavior

For `E` retained entity headers, `V` triangle vertices, `I` indices, and `B`
style batches, generation replacement remains `O(E + V + I)` time and
`O(V + I + B)` storage. One camera capture, rebase replacement, fitted-matrix
creation, or projection/view-matrix creation is bounded `O(1)` work and storage.
After warmup it allocates zero managed bytes.

`CadMesh3DViewStatistics` separately reports scene compilations/replacements,
compiled entity visits, fitted/preserved views, and camera updates. The
camera-only scene-compilation, entity-visit, draw-batch-visit, and upload-byte
counters are contractual zeros. They prevent a future host from presenting a
hidden geometry rebuild as a uniform-only orbit/pan/zoom path. They do not claim
that actual GPU draw submission is independent of batch count; full GPU replay
metrics and broader batching remain an explicit rendering gate.

Normal native operation still performs at most one scene update per changed
generation and one render call per frame. `CadNativeMesh3DSceneCompiler` now has
an overload accepting the shared viewport, requires the scene and camera to
have the same rebase origin, and writes its exact projection, view, local camera
position, and viewport into the existing pointer-free ABI. There is no new
P/Invoke, record, enum, shader, upload, or C++ implementation. Managed/native
applicability is satisfied by one shared algorithm and a stream-level matrix
differential.

## Verification record

Focused regressions cover:

- exact legacy fit behavior at large WCS coordinates;
- exact semantic camera retention while the snapshot rebase changes;
- 65,536 allocation-free camera captures with zero entity/batch/upload work;
- shared desktop/browser host retention across a real one-generation edit and
  reset on a newly loaded session;
- byte-stream native camera matrices and local position matching the managed
  camera contract.

The final Release benchmark assembly (SHA-256
`f938077bf8bd84aaa87c04331dccbafb61794e71750bdf9cc91a0436818de64f`)
loaded both a one-entity and a 10,000-entity retained mesh generation once. It
then ran 48 measured batches of 65,536 camera captures per generation after six
warmups. One entity measured p50/p95/p99
`7.2793/9.7206/14.5672 ms`; 10,000 entities measured
`5.0183/10.5257/13.8607 ms`, a large/small p95 ratio of `1.082824`.
Both lanes allocated zero managed bytes and retained exactly one compilation,
with zero camera-only scene compilations, entity visits, draw-batch visits, or
upload bytes. This qualifies entity-count independence; it is not presented as
a before/after speedup.

Instruments 16.0 Time Profiler ran the same final assembly for 250 batches per
scene, exited zero after `5.418940 s`, and produced a retained 3,408-sample
export. Allocations/VM Tracker ran the same workload, exited zero after
`7.647928 s`, and retained both tracks configured for all heap/VM types and
freed events. `xctrace export` did not expose its allocation-event schema, so
no native-heap number is claimed; the explicit managed counter is the
allocation correlation. Metal System Trace is genuinely inapplicable because
this CPU benchmark never initializes WebGPU or encodes, submits, presents, or
reads back GPU work. The machine-readable distribution and exported
Instruments evidence are under
`artifacts/benchmarks/cad-3d-camera-*`. GPU submission, residency, and frame
percentiles remain open rather than being inferred from this CPU qualification.

Final Debug and Release `ProGPU.CAD.Tests` runs each passed 1,403/1,403. The
five focused camera tests passed in both configurations, the directly affected
WinUI/media/DPI set passed 149/149, and the complete Release `ProGPU.Tests` run
passed 3,846/3,846. Release packing produced
`ACadSharp.ProGPU.0.1.0-preview.62.nupkg` and
`ProGPU.CAD.0.1.0-preview.62.nupkg`; the latter retains the exact fork-package
dependency. An isolated warning-as-error net10.0 consumer restored that closure,
constructed an AC1032 face document, compiled/fitted the new retained camera,
and printed `AC1032:42` with zero warnings. The longstanding ACadSharp
multi-target `NU5128` pack advisory remains visible on the fork package and is
not caused or hidden by this checkpoint.
