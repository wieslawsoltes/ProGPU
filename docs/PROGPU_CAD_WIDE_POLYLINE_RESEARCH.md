# ProGPU.CAD Constant-Width Polyline Research and Contract

Date: 2026-08-31

## Scope

This clean-room work adds exact retained rendering and authoring publication
for fill-on ACadSharp `LwPolyline.ConstantWidth`, then extends the same retained
contract to legacy `Polyline2D` entity defaults and constant per-segment
overrides. The resolved width is an absolute OCS/model-space width centered on
one retained line-and-bulge path. It is independent of display/plot lineweight
and follows the complete entity, block, camera, and print transform.

Variable/tapered vertex widths, FILLMODE-off outline rendering, and patterned
wide-polyline cap exceptions remain explicit unsupported geometry. The work
does not approximate those cases as a cosmetic centerline.

## Primary sources consulted

- Autodesk [`LWPOLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm):
  group 43 is the optional constant width, groups 40/41 are per-vertex start
  and end widths, group 42 is bulge, coordinates are OCS, and constant width
  is not the variable-width representation.
- Autodesk [`POLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-ABF6B778-BE20-4B49-9B58-A94E64CEFFF3.htm):
  groups 40/41 are the legacy default start/end widths, group 39 is thickness,
  and elevation and extrusion establish the entity OCS.
- Autodesk [`VERTEX` DXF entity](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-0741E831-599E-4CBF-91E1-8ADBCFD6556D.htm):
  groups 40/41 are the start/end widths of the segment beginning at that
  vertex and group 42 is its bulge.
- Autodesk [`AcDb2dPolyline` methods](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-__MEMBERTYPE_Methods_AcDb2dPolyline.html):
  a vertex without its own group-40/group-41 values uses the owning polyline's
  corresponding default width. ProGPU resolves ACadSharp's omitted optional
  zero state before classifying the retained stroke.
- Autodesk [`PLINE` command](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-11883C70-6435-4F80-8FB4-F6E933B8FD94-htm.html):
  width is centered on the segment; wide-segment junctions are normally
  beveled; nontangent arcs, very acute angles, and dot-dash linetypes have
  documented exceptions; Width/Halfwidth values persist into later segments.
- Autodesk [`FILLMODE`](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-FC385D70-45AA-4B9A-848A-CA3906C36124.htm):
  wide polylines participate in the drawing-level filled/unfilled policy.
- Autodesk [object-selection guidance](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-OnBoarding/files/ACD_FOUNDATIONS_MAIN7.html):
  crossing selection includes objects within or touching the region, while
  window selection requires complete containment. The visible filled strip,
  rather than its zero-width centerline, is therefore the selection geometry.
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
- Direct2D
  [`StrokeContainsPoint`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-strokecontainspoint)
  and Win2D
  [`CanvasGeometry`](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm):
  point containment is a property of the complete transformed stroked area and
  its width/style. ProGPU adopts that semantic contract but rejects their
  caller-selected flattening tolerance because CAD selection must not depend on
  zoom or tessellation density.
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
- ACadSharp feature commit
  [`90a423e0`](https://github.com/wieslawsoltes/ACadSharp/commit/90a423e0ef673fb6ca1f8e00bbc3c5b473249d35)
  for original DXF writer publication of legacy `POLYLINE` groups 39/40/41;
  the existing DWG writer and generic DXF reader already persisted them.

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

## Exact selection contract

Point and Crossing selection consume the same source-space butt-cap/bevel-join
stroke topology as rendering. A straight segment is one affine quadrilateral;
its two triangles and every bevel join are tested directly in WCS. A bulge
segment is the ruled strip between signed radii `r - w/2` and `r + w/2`, closed
by its two radial endpoint sections. Each circular boundary is split into at
most four exact positive-weight rational quadratic spans and transformed into
WCS before using ProGPU's existing rational stationary-point and box-plane root
solvers. No arc is flattened.

For point queries, a Gram-system inverse maps the orthogonal WCS plane
projection back through an arbitrary affine OCS basis. Membership in the
signed-radius interval covers widths equal to or greater than the bulge
diameter; the reported distance remains the exact 3D distance to the filled
strip. For Crossing queries, rational boundary/box intersections and endpoint
sections handle boundary contact. A bounded 12-edge plane/AABB slice test
covers the complementary case where the selection box lies wholly inside the
strip without touching its boundary. Window selection remains the inclusive
containment test against the snapshot's exact expanded WCS bounds.

Selection is O(S) time for S segments. Each bulge uses at most eight rational
quadratic boundary tests plus two endpoint sections; all temporary controls,
box corners, and roots use bounded stack storage. Warm point and box queries
allocate zero managed memory and do not initialize a GPU backend.

## Deliberate fail-closed behavior

- Any `LWPOLYLINE` per-vertex width remains unsupported. A legacy `POLYLINE`
  is accepted only when every actual segment resolves equal start/end widths
  and the same constant value. Open terminal-vertex widths do not describe a
  segment; the final vertex of a closed polyline does. Any resolved taper or
  segment-to-segment change remains unsupported because it requires exact
  filled-outline lowering and matched CAD differential fixtures.
- FILLMODE off remains unsupported because AutoCAD displays/plots the boundary
  outline of the filled object. Replaying the centerline would be incorrect.
- Simple or complex linetypes on a wide polyline emit `CADSCENE009` and replay
  one continuous wide path. Autodesk documents cap/join exceptions for
  dot-dash patterns; the existing zero-width linetype lowerer cannot preserve
  them.

## Complexity, retention, and parity audit

Snapshot width resolution, normalization, and exact bounds are O(V) time for V
vertices and O(V) retained vertex storage. Legacy width resolution is one
bounded pass and stores no parallel width stream after proving constancy. Scene
recording is O(V), produces one retained path command, and uses O(U) cached pens
for U distinct `(style,width)` pairs. Stable replay, pan, zoom, and print replay
do not rebuild CAD geometry and introduce no per-frame managed/native crossing,
upload, or CAD allocation.

The managed and native renderers consume the same `GpuPicture` path and the
same existing ProGPU stroke compiler. No C ABI record, generated C# wire type,
C++ CAD frontend, shader, atlas, device-loss contract, or GPU resource owner
changes. Exact CAD selection is a host-side immutable-snapshot query; there is
no native CAD selection frontend or managed/native crossing to update. A
matched native compilation regression continues to verify that the same
normal-transform stroke lowers to native geometry. Print retains the
model-space pen inside one page transform, and DXF/DWG tests verify width
persistence.

Focused regressions cover straight and bulged bounds, analytic arc retention,
bevel/butt model-space style, nonuniform and sheared nested block transforms,
signed-radius bulge selection, bevel and flat-cap selection, whole-box strip
containment, zero warm-query allocation, bounded shared pen identity, print
replay, managed/native compilation, authoring and Undo/Redo, DXF/DWG round
trips, recoverable FILLMODE failure, variable-width rejection, and patterned
fallback diagnostics. The focused lightweight suite passes 15/15, the legacy
suite passes 13/13, and the complete CAD suite passes 1,349/1,349 in both Debug
and Release. The ACadSharp DXF writer regression passes 3/3 across AC1015,
AC1021, and AC1032. Fresh `ACadSharp.ProGPU` and `ProGPU.CAD`
packages at `0.1.0-preview.62` pass the two-package content/dependency audit;
an isolated package-only consumer resolves the fork identity, rejects upstream
ACadSharp, builds with 0 warnings and 0 errors, and creates an AC1032 document.
The grouped wrapper remains unavailable while the separately user-deleted
browser sample project is absent; validation did not restore or stage it.
