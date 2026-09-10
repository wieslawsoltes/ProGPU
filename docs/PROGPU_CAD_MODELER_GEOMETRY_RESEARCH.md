# ProGPU.CAD modeler geometry research and design record

Date: 2026-08-30

Scope: ACadSharp-backed `BODY`, `REGION`, and `3DSOLID` payload ownership,
immutable ProGPU.CAD snapshots, optional DWG display-wire retention, managed/native
wireframe replay, exact selection, editing safety, and the future ACIS face-
tessellation boundary. This is a clean-room design record; no third-party
implementation text, organization, helper structure, or lookup data was copied.

## Authoritative contracts consulted

- Autodesk's [BODY DXF contract](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-7FB91514-56FF-4487-850E-CF1047999E77.htm),
  [REGION DXF contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-644BF0F0-FD79-4C5E-AD5A-0053FCC5A5A4.htm), and
  [3DSOLID DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-19AB1C40-0BE0-4F32-BCAB-04B37044A0D3.htm)
  define `AcDbModelerGeometry`, modeler format version, and proprietary data as
  the persisted authority. The specification does not publish a surface topology
  decoder or license an approximate replacement.
- Autodesk's [Fusion supported-format contract](https://help.autodesk.com/cloudhelp/PTB/Fusion-Designs/files/TPD-SUPPORTED-FILE-FORMATS-HYBRID.htm)
  identifies SAT/SAB as ACIS geometry and documents a version boundary. ProGPU
  therefore preserves the original payload and its binary/text identity instead
  of silently normalizing it to a guessed record set.
- Open CASCADE's [meshing guide](https://dev.opencascade.org/doc/occt-7.8.0/overview/html/occt_user_guides__mesh.html)
  keeps B-rep topology authoritative and adds triangulation, controlled by linear
  and angular deflection, for shaded visualization. Adopted the topology-versus-
  visualization separation; deferred meshing until ProGPU owns a complete bounded
  surface contract.
- Skia's [mesh contract](https://api.skia.org/classSkMesh.html) and current
  [Ganesh tessellation source domain](https://github.com/google/skia/blob/main/src/gpu/ganesh/tessellate/GrPathTessellationShader.cpp)
  confirm that explicit mesh data and path tessellation are renderer inputs, not
  substitutes for a CAD B-rep decoder.
- Direct2D's [geometry tessellation contract](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-tessellate)
  produces clockwise triangles from device-independent geometry with an explicit
  flattening tolerance. Adopted explicit tessellation output and rejected hidden,
  fixed-detail surface approximation.
- Win2D's [cached geometry contract](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm)
  reinforces pay-once/draw-many retention and device ownership. No Win2D API
  defines ACIS payload semantics.
- WebRender's [retained primitive store](https://github.com/servo/webrender/blob/main/webrender/src/prim_store/mod.rs)
  and [rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  informed immutable display data, visibility processing, and resource resolution
  outside frame replay.
- Vello's [retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  and [scene contract](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  informed transform-independent retained fragments and batched GPU submission.
- SkParagraph's [shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  DirectWrite's [layout/render separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
  Parley's [text stack](https://github.com/linebender/parley), and HarfBuzz's
  [shape-plan implementation](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape.cc)
  were rechecked for the mandatory text gate. They are non-applicable: this slice
  changes no shaping, fallback, glyph cache, variable-font state, subpixel/DPI
  behavior, or text device-loss handling.

## Adopted ProGPU architecture

`CadModelerGeometryPrimitive` retains typed BODY/REGION/3DSOLID identity, the
modeler version, binary/text payload flag, and fixed-width offsets/counts into
snapshot-wide payload and display-wire streams. Payload bytes are copied exactly
under per-entity and per-document limits so the immutable snapshot cannot be
changed through ACadSharp's mutable `byte[]`. Optional ACadSharp public `Wire`
records retain type, selection marker, ACIS index, point boundaries, and their
exposed affine display transform. Parent INSERT transforms are applied once in
double precision before WCS bounds and rebasing.

Snapshot work and storage are `O(W + P + B)` for wires `W`, points `P`, and payload
bytes `B`, with explicit document limits. A failure rolls back all four parallel
streams. Payload-only modeler entities remain in the snapshot with empty finite
bounds and do not enter the BVH; no origin point, straight edge, face, or box is
invented. The pinned AC1021 DWG fixture exercises this real payload-only case.

Display wires lower once per entity to one existing ProGPU-owned
`DrawingContext.DrawAcisSolid` command. Exact implementation provenance is
`src/ProGPU.Scene/Extensions/AcisSolidExtensionPipeline.cs`,
`src/ProGPU.Scene/Shaders/AcisSolid.wgsl`, and the existing native-picture
`DrawLine3DBatch` lowering in `GpuPictureNativeSceneCompiler.cs`. This checkpoint
directly reuses those original in-repository algorithms and canonical shader; it
does not copy or port a third-party implementation. Stable picture replay retains
the same command and edge buffers with no ACadSharp traversal or payload parsing.

Point selection measures exact retained point/segment distance. Window selection
requires every display-wire point inside the WCS box; Crossing accepts a retained
point or segment intersection. Payload-only entities return typed unsupported
geometry if directly tested. Generic move/rotate/scale commands reject modeler
geometry before mutation because changing display wires without synchronously
editing the authoritative ACIS payload would make Undo/Redo and saved output lie.

The legacy ProGPU-owned `AcisSatParser`/`AcisSabParser` were inspected as approved
in-repository provenance. They support only heuristic endpoint extraction and are
not used by this checkpoint because treating their straight endpoint chords as a
complete REGION/solid representation would violate the quality contract. They may
inform matched migration tests once a complete independently specified modeler
topology layer exists.

## Managed/native and sample applicability audit

The managed plan compiler emits the existing ACIS extension command. The existing
native picture compiler already recognizes the same command and lowers all edges
to one pointer-free Line3D resource/draw path. The focused parity regression checks
equal edge count. No public C record, generated C# wire binding, C++ module, shader,
per-edge P/Invoke, or retained-resource lifetime change is required.

The shared desktop/browser sample already consumes `CadPlanSceneCompiler`, so
display-wire modeler entities appear in both plan views without a sample-specific
renderer branch. The desktop depth view continues to show only true triangle
batches; presenting wires as shaded faces was explicitly rejected. The four
unrelated browser-sample deletions in the working tree were not modified.

## Verification and measured evidence

- Release `ProGPU.CAD.Tests`: 703/703 in 5 seconds on the development host.
- `CadModelerGeometryTests`: focused coverage includes immutable payload copying,
  typed metadata, batched managed ACIS replay, matched native Line3D replay,
  print-plan reuse, exact point/Window/Crossing selection, payload-only deferral,
  parallel-stream rollback, edit rejection, and a pinned real DWG.

These are correctness observations, not latency or throughput claims. The
structural steady-state contract is one retained command per modeler entity with
display wires, one immutable copy of each payload, no replay-time payload parsing,
and no per-edge managed/native crossing. Cold-start, B-rep decode/tessellation
p50/p95/p99, memory-residency, browser AOT, shaded image-quality, device-loss, and
matched Instruments/Metal evidence remain required for filled-surface support.

## Rejected or deferred alternatives

- Endpoint chords from heuristic SAT/SAB scans were rejected as solid/surface
  rendering. Curve and face semantics cannot be inferred from two vertices.
- Filled bounding boxes, convex hulls, control cages, and fixed-resolution surface
  grids were rejected because they change topology, selection, occlusion, and print
  output.
- Payload parsing during render replay and per-wire/per-edge native calls were
  rejected; immutable scene generation is the batching boundary.
- Mutating optional display wires while leaving ACIS bytes unchanged was rejected.
- Complete SAT/SAB topology, analytic curves/surfaces, trimming loops, adaptive
  tessellation, shaded/depth batches, hidden-line removal, materials, edit-kernel
  operations, and exact save-time payload transformation remain deferred rather
  than approximated.
