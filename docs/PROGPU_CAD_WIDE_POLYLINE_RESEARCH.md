# ProGPU.CAD Constant-Width LWPOLYLINE Research and Contract

Date: 2026-08-31

## Scope

This clean-room checkpoint adds exact retained rendering and authoring
publication for fill-on ACadSharp `LwPolyline.ConstantWidth`. The width is an
absolute OCS/model-space width centered on one retained line-and-bulge path. It
is independent of display/plot lineweight and follows the complete entity,
block, camera, and print transform.

Variable/tapered vertex widths, FILLMODE-off outline rendering, and patterned
wide-polyline cap exceptions remain explicit unsupported geometry. The work
does not approximate those cases as a cosmetic centerline.

## Primary sources consulted

- Autodesk [`LWPOLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm):
  group 43 is the optional constant width, groups 40/41 are per-vertex start
  and end widths, group 42 is bulge, coordinates are OCS, and constant width
  is not the variable-width representation.
- Autodesk [`PLINE` command](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-11883C70-6435-4F80-8FB4-F6E933B8FD94-htm.html):
  width is centered on the segment; wide-segment junctions are normally
  beveled; nontangent arcs, very acute angles, and dot-dash linetypes have
  documented exceptions; Width/Halfwidth values persist into later segments.
- Autodesk [`FILLMODE`](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-FC385D70-45AA-4B9A-848A-CA3906C36124.htm):
  wide polylines participate in the drawing-level filled/unfilled policy.
- Skia [`SkCanvas::drawPath`](https://api.skia.org/classSkCanvas.html) and
  [`SkPaint`](https://api.skia.org/classSkPaint.html): a retained contour is
  stroked once with explicit width, cap, and join rather than expanded into
  independently blended segment draws.
- Direct2D [`DrawGeometry`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawgeometry)
  and [`ID2D1Geometry::Widen`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-widen),
  plus the Win2D [vector feature contract](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/):
  geometry and stroke style are separate retained inputs; stroke width is
  centered on the path; explicit widening is available when an outline is
  required.
- Linebender [Vello scene stroking](https://github.com/linebender/vello/blob/main/vello/src/scene.rs),
  [stroke-style encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  and [GPU-friendly stroke expansion paper](https://arxiv.org/abs/2405.00127):
  retained topology and compact width/join/cap style can be consumed by a GPU
  stroker without author-time tessellation; dash fallback is a distinct path.
- Mozilla [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html):
  self-contained retained display-list data is compiled into a culled frame
  and GPU commands, reinforcing separation of immutable scene content from
  viewport replay.
- Skia [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
  Microsoft [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  Linebender [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://harfbuzz.github.io/harfbuzz-hb-shape.html) were examined as
  required. Their shaping, fallback, bidi, line-breaking, glyph positioning,
  and reusable text-layout results are unaffected by a vector-stroke change.

No foreign implementation text, structure, names, control flow, tables, or
helpers were copied or translated. The sources establish observable CAD
behavior and retained-rendering architecture only.

## Approved in-repository provenance

The implementation directly extends original ProGPU-owned code in:

- `src/ProGPU.CAD/CadDocumentSnapshot.cs` and `CadSnapshotCompiler.cs` for
  immutable OCS polyline topology, affine block expansion, analytic bulges,
  exact world bounds, typed diagnostics, and spatial-index publication;
- `src/ProGPU.CAD/CadPlanSceneCompiler.cs` for the shared retained
  `PathGeometry`, style cache, drawing-picture output, print filtering, and
  managed/native picture-compilation contract;
- `src/ProGPU.CAD/CadPolylineAuthoring.cs` for typed current-property capture,
  one-entity history publication, and identity-preserving Undo/Redo;
- ProGPU.Vector and ProGPU.Scene's existing analytic path stroker and
  `PenStrokeTransformMode.Normal`, which are already shared by managed and
  native scene compilation.

## Geometry and rendering contract

For constant width `w`, each line segment uses offsets `+/- w/2` along its OCS
normal. Each circular bulge arc uses concentric signed radii `r + w/2` and
`r - w/2`; source-space endpoint cross-sections and bevel junctions complete
the same single stroke. Straight and circular extrema are transformed through
the exact retained OCS/block basis when snapshot bounds are built. This keeps
visibility culling, fit-to-page, and window selection aware of visible width.

The plan scene records the original analytic path once with:

- thickness equal to DXF constant width;
- `PenStrokeTransformMode.Normal`, so nonuniform block, camera, and page
  transforms apply to the complete source-space stroke;
- bevel joins and flat/butt start, end, and dash caps;
- the resolved entity brush, with lineweight excluded from geometric width.

One `(style index, width)` cache entry is shared by all matching entities in a
compile. Recording one path instead of one draw per segment prevents repeated
alpha application at joins and keeps analytic bulges available to the existing
managed/native GPU stroke compiler.

## Deliberate fail-closed behavior

- Any nonzero per-vertex start/end width remains unsupported. Exact tapered
  strips require segment-specific outlines, corner classification, and matched
  CAD differential fixtures.
- FILLMODE off remains unsupported because AutoCAD displays/plots the boundary
  outline of the filled object. Replaying the centerline would be incorrect.
- Simple or complex linetypes on a wide polyline emit `CADSCENE009` and replay
  one continuous wide path. Autodesk documents cap/join exceptions for
  dot-dash patterns; the existing zero-width linetype lowerer cannot preserve
  them.
- Exact point and crossing selection return `UnsupportedGeometry` for wide
  polylines instead of testing the centerline. Exact window containment remains
  valid through the expanded world bounds. Filled-stroke point/crossing
  selection is a follow-up checkpoint.
- Legacy `Polyline2D` width remains unsupported. Its entity-level and
  per-vertex width rules need a separate persistence and differential audit.

## Complexity, retention, and parity audit

Snapshot normalization and exact bounds are O(V) time for V vertices and O(V)
retained vertex storage. Scene recording is O(V), produces one retained path
command, and uses O(U) cached pens for U distinct `(style,width)` pairs. Stable
replay, pan, zoom, and print replay do not rebuild CAD geometry and introduce
no per-frame managed/native crossing, upload, or CAD allocation.

The managed and native renderers consume the same `GpuPicture` path and the
same existing ProGPU stroke compiler. No C ABI record, generated C# wire type,
C++ CAD frontend, shader, atlas, device-loss contract, or GPU resource owner
changes. A matched native compilation regression verifies the normal-transform
stroke lowers to native geometry. Print retains the model-space pen inside one
page transform, and DXF/DWG tests verify width persistence.

Focused regressions cover straight and bulged bounds, analytic arc retention,
bevel/butt model-space style, nonuniform block transforms, bounded shared pen
identity, print replay, managed/native compilation, authoring and Undo/Redo,
DXF/DWG round trips, recoverable FILLMODE failure, variable-width rejection,
patterned fallback diagnostics, and explicit selection status. The focused
suite passes 24/24 in Debug and Release. The complete CAD suite passes
1,327/1,327 in Debug and Release. Fresh `ACadSharp.ProGPU` and `ProGPU.CAD`
packages at `0.1.0-preview.62` pass the two-package content/dependency audit;
an isolated package-only consumer resolves the fork identity, rejects upstream
ACadSharp, builds with 0 warnings and 0 errors, and creates an AC1032 document.
The grouped wrapper remains unavailable while the separately user-deleted
browser sample project is absent; validation did not restore or stage it.
