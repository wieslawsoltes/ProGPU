# ProGPU CAD 3D visual-style research

Date: 2026-09-01

This record covers the clean-room design gate for the retained CAD 3D
visual-style implementation. No third-party source text or structure was
copied. The implementation is original ProGPU code. The exact implementation
provenance for its lighting math is the existing ProGPU-owned
`Mesh3DSolid.wgsl` and `Mesh3DWireframe.wgsl`; `Native3D.wgsl` applies that
same contract to its distinct pointer-free native storage ABI.

## Primary sources consulted

- [Skia documentation](https://skia.org/docs/) and
  [SkCanvas creation](https://skia.org/docs/user/api/skcanvas_creation/):
  device-independent recorded content is kept separate from GPU surfaces and
  their context-owned caches.
- [Skia text architecture](https://docs.skia.org/docs/dev/design/text_overview/):
  shaping/layout is separated from drawing so presentation changes do not
  invalidate reusable text results.
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains):
  CPU/device-independent resources survive independently of device-dependent
  GPU resources, which must be rebuilt after device loss.
- [DirectWrite introduction](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite)
  and [DirectWrite rendering](https://learn.microsoft.com/en-us/windows/win32/directwrite/rendering-directwrite):
  layout/glyph processing and the chosen render target remain factored layers.
- [Win2D device-loss handling](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost):
  device replacement recreates GPU resources while application-owned content
  remains authoritative.
- [WebRender profiler counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs)
  and [WebRender repository](https://github.com/servo/webrender): retained
  scene building, visibility, batching, uploads, GPU time, and residency are
  measured independently.
- [Vello](https://github.com/linebender/vello) and its
  [2023 roadmap](https://github.com/linebender/vello/blob/main/doc/roadmap_2023.md):
  compact reusable scene encodings and separately bound resources support
  presentation changes without rebuilding source geometry.
- [Parley](https://github.com/linebender/parley) and
  [Parley layout](https://docs.rs/parley/latest/parley/struct.Layout.html):
  font/layout resources and computed layout remain reusable across rendering.
- [HarfBuzz shaping and shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html):
  shaped glyph output is reusable input to a renderer and is not recomputed for
  a viewport surface-style change.
- [AutoCAD visual styles](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-F9113233-6798-4F5C-9A9F-7BA41CFA2533.htm),
  [Visual Styles Manager](https://help.autodesk.com/cloudhelp/2022/ENG/AutoCAD-Core/files/GUID-1966BEB9-6975-412B-834E-FD2E85A85330.htm),
  and [edge display](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-7CFAD837-E8D5-496E-B0CF-EAC773709392.htm):
  viewport visual style is a combination of face shading and edge policy;
  Conceptual uses cool/warm Gooch shading, Hidden suppresses occluded edges,
  Realistic uses smooth material shading, Shades of Gray is monochromatic,
  and X-ray uses partial transparency.

## Adopted design

- Visual style is an O(1) typed policy that atomically selects face shading and
  edge rendering. Camera-independent positions, normals, UVs, indices,
  selection topology, and BVHs are not rebuilt.
- Managed and native adapters consume the same nine CAD style choices and the
  same seven ProGPU shading algorithms. The native stream still performs one
  scene update per changed content generation and one render submission per
  frame; a style is encoded per retained mesh record, never crossed per face.
- Triangle-list adjacency is compiled once in deterministic source order in
  O(I) time and O(E) storage for I indices and E exact unique edges. Signed
  zero is canonicalized, one/two/more incident faces become
  boundary/manifold/non-manifold topology, and adjacent face normals remain
  camera independent. Material draw ranges of one modern mesh share one
  accumulator, so a material boundary does not invent an outline.
- Managed and native GPU pages carry equivalent 80-byte edge records. The
  camera, model transform, normal transform, crease threshold, physical-pixel
  width, and display flags classify boundary, crease, and silhouette edges at
  replay time. Camera motion therefore changes silhouettes without rebuilding
  or uploading the retained edge page.
- Visible edges use depth `LessEqual` with depth writes disabled. Optional
  occluded edges use a second `Greater` pass with bounded physical-pixel dash
  and gap lengths. Non-manifold edges are conservatively eligible as creases
  and silhouettes. Hidden, Conceptual, and Shaded-with-Edges enable visible
  streams; X-ray additionally enables the occluded stream.
- Native parity uses the existing fixed 256-byte Mesh3D ABI record with an
  `EdgeList` topology rather than adding pointers or crossings. Paired
  auxiliary vertices encode endpoints and adjacent normals; native scene
  compilation materializes the same 80-byte GPU records once per immutable
  generation. Normal face records and their public layout remain unchanged.
- Stable retained replay remains O(I + E) work with O(1) style state per batch.
  Lighting is bounded to three fixed lights, edge classification is O(1) per
  edge, and managed camera-only replay performs zero edge/record upload.

## Adapted or rejected concepts

- Adopted Direct2D/Win2D-style separation of retained CPU content from
  device-domain resources and Vello/WebRender-style compact retained state.
- Adapted AutoCAD's public face/edge separation to ProGPU's typed retained
  scene. Explicit boundary/crease/silhouette selection, physical width,
  visible and occluded colors, crease angle, and occluded dash/gap are now
  configurable without geometry rebuild. Sketch jitter and edge extensions
  remain deferred because they require a separate bounded quality contract.
- Rejected recompiling CAD snapshots, rebuilding selection acceleration, or
  uploading geometry on style changes. Text shaping/layout is unaffected and
  remains reusable, consistent with Skia, DirectWrite, Parley, and HarfBuzz.
- Rejected CPU camera-time edge classification, material-range-local
  adjacency, and triangle-derivative wireframe as the CAD shaded-edge
  implementation because each would respectively rebuild on orbit, invent
  false material seams, or expose triangulation diagonals. The legacy generic
  Wireframe mode retains derivative coverage for callers that request it.

## Validation contract

- Exhaustive typed mapping tests cover every managed visual style.
- Native stream tests verify atomic face/edge policy parity, fixed-layout
  `EdgeList` encoding, topology/counts, occluded policy, and light intensity.
- Shared-shell tests verify style switching preserves the retained CAD scene,
  geometry objects, and camera.
- Managed headless WebGPU tests compile and execute the visible/occluded edge
  pipelines and verify first-upload plus zero-upload camera replay. Native
  Clang Release compilation and native scene-builder validation cover the same
  resource contract. Matched managed/native shaded pixel goldens, device-loss
  runs, and p50/p95/p99 measurements remain part of comprehensive validation.

## 2026-09-01 implementation evidence

- Managed CAD Debug and Release suites each pass 1,505 tests. The focused
  retained-3D, shader-resource, and media Release lane passes 112 tests, and
  all nine Clang Release native CTest targets pass.
- The native Apple M3 Pro smoke workload executes retained 2D, face, visible
  edge, and occluded-edge pipelines in source order, reads back the final
  image, and reports 10 retained commands with one submission. Mixed 2D/3D
  bundle families use separate compatible render passes inside that command
  encoder; 3D spans retain their per-target depth contents across later spans.
- The managed Release edge replay workload uses 256 mesh batches, three exact
  boundary edges per batch, 12 warmups, and 120 measured camera frames. The
  first frame uploads 61,440 edge bytes. Stable replay uploads zero edge,
  record, index, and geometry bytes, allocates zero managed bytes, records 257
  draws in one command buffer/submission, and measures p50 0.3093 ms, p95
  1.2430 ms, and p99 12.9453 ms on the same Apple M3 Pro host. The ignored
  machine-readable report is generated under `artifacts/benchmarks/`.
- `eng/progpu-verify-native-contract.sh` confirms that checked-in generated C#
  declarations remain deterministic and synchronized with `progpu_native.h`.
