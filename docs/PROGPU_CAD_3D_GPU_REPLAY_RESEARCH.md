# ProGPU.CAD retained managed Mesh3D replay research and contract

Status: implemented and verified, 2026-08-31

## Scope and clean-room provenance

This checkpoint makes a camera-only managed `Viewport3D` frame reuse the
immutable CAD mesh generation and its device-local geometry buffers. It adds
typed frame metrics, explicit scene invalidation, and context-replacement
rehydration. Projection/view/camera state remains the only bounded per-frame
upload. It does not change mesh shaders, text, shaping, raster quality, native
ABI records, or CAD file semantics.

No third-party implementation source was copied, ported, translated, or used
as a source-text template. Approved original in-repository ProGPU provenance
is:

- `src/ProGPU.WinUI/Controls/Viewport3D.cs` for the existing recursive model
  compilation, offscreen target, camera, and input contracts;
- `src/ProGPU.Scene/Extensions/Mesh3DExtensionPipeline.cs` for the existing
  canonical managed Mesh3D records, resource cache, and one-submit-per-frame
  implementation;
- `src/ProGPU.CAD.Sample/CadSampleView.cs` for the shared desktop/browser CAD
  host and immutable mesh-generation replacement boundary;
- `src/ProGPU.CAD/CadMesh3DSceneCompiler.cs` and
  `src/ProGPU.CAD/CadMesh3DViewCoordinator.cs` for immutable generation-owned
  batches and camera-independent retained state;
- `src/ProGPU.Backend.Native/NativeRendererTypes.cs` and the native retained
  scene implementation for the approved ProGPU-owned update/frame metric and
  upload-accounting conventions.

The retained payload generation, managed metrics, invalidation API, context
ownership checks, and regressions are original ProGPU work. ACadSharp supplies
the DXF/DWG object model but is not traversed by camera-only replay.

## Primary-source architecture review

| Stack | Primary evidence | Finding and ProGPU decision |
|---|---|---|
| Skia / SkParagraph | [SkPicture API](https://api.skia.org/classSkPicture.html), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/) | An immutable picture replays recorded commands, while shaping produces reusable positioned glyph results. Adopted: retain the compiled mesh command payload and leave the existing shaped CAD text generations untouched. Rejected: treating command replay as zero draw work. |
| Direct2D / DirectWrite / Win2D | [Direct2D API and resources](https://learn.microsoft.com/en-us/windows/win32/direct2d/the-direct2d-api), [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains), [DirectWrite layout reuse](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite), [Win2D device-loss handling](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm) | Device-independent geometry/layout can survive redraw, but device-dependent resources belong to one device and must be recreated after loss. Adopted: retain CPU mesh entries by scene generation, cache GPU buffers inside one pipeline/context, and recreate viewport targets when the active context changes. |
| WebRender | [upstream profiler counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs) | WebRender reports display-list/scene/frame/renderer/GPU work separately and exposes draw calls, vertices, texture uploads, bytes, memory, and render reasons. Adapted: report scene reuse, geometry/record/index/uniform upload bytes, draw calls, command buffers, and submission independently. |
| Vello / Parley | [Vello retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [current Vello scene API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), [Parley text stack](https://github.com/linebender/parley) | Static fragments/resources and dynamic transforms are separate concerns; Parley keeps Unicode analysis, shaping, and layout on the CPU. Adopted: camera matrices are late state and immutable CAD geometry remains reusable. Text layout is not rebuilt or moved to the GPU. |
| HarfBuzz | [buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html), [shape-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html) | Shaping transforms Unicode buffers into positioned glyphs and permits cached shaping plans. It is not involved in a Mesh3D camera frame. Existing ProGPU glyph runs, fallback, variable-font state, and caches are deliberately unchanged. |
| WebGPU | [WebGPU specification](https://gpuweb.github.io/gpuweb/), [queue API](https://gpuweb.github.io/types/interfaces/GPUQueue.html) | A device exclusively owns its buffers, textures, bind groups, and command objects; loss makes all of them unusable. `writeBuffer` issues buffer writes and `submit` hands command buffers to the device timeline. Adopted: identify context replacement before using an offscreen texture, rehydrate GPU resources through the replacement pipeline, count every upload byte, and retain one shared queue submission for all Mesh3D viewports in a frame. |

The broader required audit found no change applicable to startup/lazy font
initialization, shaping or line-layout reuse, font fallback, variable-font
coordinates, glyph/path/texture atlas keys or eviction, worker preparation,
DPI/subpixel text behavior, or visibility culling. Those remain owned by the
unchanged 2D/text and CAD compilation pipelines. Mesh draw calls still scale
with visible draw batches; this checkpoint only removes redundant CPU scene
traversal and geometry/record/index uploads from stable camera replay.

## Retention, invalidation, and device contract

`Viewport3D.EnableRetainedSceneCache` is opt-in because the generic Media3D
object model currently exposes mutable lists and materials without a complete
typed change-notification graph. With the option enabled, the caller must use
`InvalidateScene` after changing children, model transforms, geometry arrays,
or materials. The method advances a nonzero scene/record generation and
invalidates the visual. Typed lighting, render-mode, and shading-mode setters
advance only the record generation because they do not change model traversal
or geometry. Camera mutation, target size, DPI, and offscreen texture
replacement advance neither generation.

One retained payload and its mesh-entry list are rebuilt only when the scene
generation changes. A stable camera frame updates target references and camera
matrices but does not recursively visit model nodes. Dynamic media materials
continue to invalidate the record generation because current texture
descriptors/effects can affect GPU records and bind groups. The CAD sample owns
immutable batches, enables retention, and calls `InvalidateScene` once after a
generation replacement.

The Mesh3D extension treats generation zero as the existing dynamic behavior:
records and indices are rebuilt and uploaded each compile. A nonzero retained
record generation skips record construction and record/index writes only when
the pooled viewport resource contains the same generation and record count.
Buffer creation, growth, a new compositor/context, or generation change forces
a complete reupload. The camera uniform is always written because projection,
view, target size, or camera position may change.

Each `Viewport3D` offscreen color/MSAA/depth texture records its owning
`WgpuContext`. A different active context disposes the old targets before any
use and creates fresh targets on the new context. The compositor owns a fresh
Mesh3D extension and therefore reconstructs records, bind groups, and geometry
buffers from the retained CPU payload. No device pointer or lease crosses the
context boundary.

## Complexity and measurement contract

For `N` model nodes, `B` mesh records, and `I` de-indexed triangle vertices, a
changed retained generation uses `O(N + B + I)` CPU work and `O(B + I)` upload
bytes. A stable camera frame uses `O(B)` draw encoding, one bounded
`GpuMesh3DUniforms` write per viewport, one command buffer per viewport, and
one shared queue submission for the extension. It performs zero model-tree
visits, zero geometry uploads, and zero record/index uploads. Draw encoding is
not claimed to be entity-independent.

Managed frame metrics report the immutable generation and whether it was
reused; viewport, mesh, and draw counts; scene compilations/model visits;
geometry cache hits/misses; geometry, record, index, and uniform upload bytes;
resident geometry/resource counts and buffer bytes; logical color/MSAA/depth
target bytes; command buffers; and the shared queue submission count. Logical
target bytes are format/sample accounting, not a claim about driver allocation
or physical GPU residency. The submission count is a pipeline-frame total and
must not be summed across viewport snapshots. Matched native applicability uses
the existing `NativeSceneUpdateMetrics` and `NativeSceneFrameMetrics`: stable
native replay likewise performs no scene update and reports its actual bounded
frame uploads/submission. No native code, C ABI, generated wire declaration,
or canonical shader changes in this checkpoint.

## Verification evidence

The focused real-WebGPU regression
`Mesh3DRetainedReplayTests.CameraReplayRetainsSceneUploadsAndRehydratesOnNewContext`
proves:

- first retained render uploads records, indices, and geometry;
- camera-only replay reuses the scene with zero geometry/record/index upload,
  one bounded uniform upload, and one shared submission;
- a lighting-only record generation reuses compiled model geometry while
  reuploading records and indices;
- explicit scene invalidation rebuilds and reuploads the correct generation;
- a replacement WebGPU context rehydrates targets, records, and geometry from
  retained CPU data without retaining old-device resources;
- persistent geometry, viewport buffers, and logical target accounting remain
  stable across camera replay and are rebuilt in the replacement context.

`CadMesh3DViewportTests.SharedSamplePreservesOrbitCameraUntilExplicitResetOrFit`
proves that the shared CAD sample enables retention, preserves camera state,
and advances one mesh generation exactly once. Native applicability is covered
by `progpu_native_webscene_provider_tests.cpp`: its semantic-scene lanes assert
zero vertex/index/texture upload on stable replay, rebuild after Dawn engine
replacement, and zero-upload replay after recovery. The native C++ renderer
already owned this retained semantic-scene contract, so duplicating the new
managed `Viewport3D` frontend there is not applicable.

The Release `--mesh3d-replay-batches` lane records SHA-256 identities for the
benchmark, backend, CAD, scene, headless host, and WinUI assemblies. Checked-in
one- and 512-batch reports use 24 warmups and 480 sequentially measured camera
frames. Both report zero managed allocation, zero model visits, zero
geometry/record/index
upload, one 144-byte uniform upload, one command buffer, and one submission per
stable frame. The one-batch p50/p95/p99 is
0.3733/1.3845/6.3927 milliseconds; the 512-batch result is
0.4060/1.5921/5.7145 milliseconds. The 512-batch lane retains one 96-byte
shared geometry buffer, 462,992 viewport-buffer bytes, and 33,177,600 logical
target bytes; draw encoding remains intentionally `O(B)`.

Matched macOS Allocations, Time Profiler, and Metal System Trace exports are
retained under `artifacts/benchmarks/cad-3d-gpu-replay-instruments`. Instruments
reports 88,533,920 bytes of persistent heap/anonymous VM during the extended
instrumented process, 1,229,502,160 aggregate allocated bytes, and
1,140,878,928 transient bytes; none of these values is managed-object residency.
The Time Profiler samples confirm repeated wgpu Metal render-pass validation,
resource tracking, the bounded queue write, and queue submission, with no
repeated CAD scene compilation in the profiled stack. Metal System Trace
observed 417 submissions, 548 completions, a 69,795,840-byte maximum
`currentAllocatedSize`, and 74 buffer allocations totaling 9,699,328 bytes.
Only two observed buffers totaling 262,144 bytes lacked a recorded deallocation
before the short capture ended. It observed no drawable waits, compiler spills,
potential hangs, hang risks, or command-buffer errors. The capture is a short
diagnostic window rather than a steady-state physical-residency ceiling;
uninstrumented p50/p95/p99 and typed upload/residency counters remain the
performance source of truth.
