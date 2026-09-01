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
- Hidden style uses the existing depth-tested solid-plus-derivative-edge path,
  so back/occluded triangle edges are rejected by the depth attachment. X-ray
  retains depth-tested faces with view-angle opacity. Conceptual uses the
  existing bounded three-light Gooch model.
- Stable retained replay remains O(I) fragment/vertex work for I referenced
  indices with O(1) visual-style state per batch. Lighting is bounded to three
  fixed lights and derivative wire evaluation is bounded to one test per
  fragment.

## Adapted or rejected concepts

- Adopted Direct2D/Win2D-style separation of retained CPU content from
  device-domain resources and Vello/WebRender-style compact retained state.
- Adapted AutoCAD's public visual-style behavior to ProGPU's existing typed
  Mesh3D modes. Sketch jitter/extensions and configurable occluded-line
  linetypes are deferred because they require a distinct edge stream and
  quality/performance contract rather than fragment-only approximation.
- Rejected recompiling CAD snapshots, rebuilding selection acceleration, or
  uploading geometry on style changes. Text shaping/layout is unaffected and
  remains reusable, consistent with Skia, DirectWrite, Parley, and HarfBuzz.
- Material texture leases remain separate follow-up work because the stable
  native Mesh3D ABI currently has no texture-resource identity. Adding an
  unmanaged texture handle to a mesh record or performing per-batch crossings
  was rejected.

## Validation contract

- Exhaustive typed mapping tests cover every managed visual style.
- Native stream tests verify atomic render/shading mode parity and the light
  intensity contract.
- Shared-shell tests verify style switching preserves the retained CAD scene,
  geometry objects, and camera.
- Shader-resource audit and native shader compilation cover the target-specific
  native WGSL variant. Broader managed/native pixel goldens, device-loss runs,
  and matched p50/p95/p99 measurements are part of the later comprehensive
  validation phase.
