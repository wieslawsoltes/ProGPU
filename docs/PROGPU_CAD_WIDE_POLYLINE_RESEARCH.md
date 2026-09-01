# ProGPU.CAD Wide and Tapered Polyline Research and Contract

Date: 2026-08-31

## Scope

This clean-room work covers ACadSharp `LwPolyline.ConstantWidth`, legacy
`Polyline2D` entity defaults, constant per-segment overrides, exact
variable-width straight segments, and drawing-level FILLMODE rendering. Width
is absolute OCS/model-space geometry centered on each segment and follows the
complete entity, block, camera, and print transform. Fill-on constant profiles
retain the original analytic line-and-bulge stroke; tapered straight profiles
lower to one filled outline. Fill-off profiles retain one hollow entity command
whose straight/tapered segment cells, straight bevel cells, and constant-width
annular bulge sectors preserve their authored boundary seams.

Genuinely variable-width bulges, mixed skinny/wide segment streams,
constant-width bulges whose inner signed boundary crosses the arc center, and
patterned-wide cap exceptions remain typed unsupported geometry. None is
approximated as a constant, cosmetic centerline, flattened curve, or boolean
union.

## Primary sources consulted

- Autodesk [`LWPOLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm):
  group 43 is the optional constant width, groups 40/41 are per-vertex start
  and end widths, group 42 is bulge, coordinates are OCS, and group 43 is not
  used when variable-width codes are present.
- Autodesk [`POLYLINE` DXF entity](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-ABF6B778-BE20-4B49-9B58-A94E64CEFFF3.htm):
  groups 40/41 are the legacy default start/end widths, group 39 is thickness,
  and elevation and extrusion establish the entity OCS.
- Autodesk [`VERTEX` DXF entity](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-0741E831-599E-4CBF-91E1-8ADBCFD6556D.htm):
  groups 40/41 are the start/end widths of the segment beginning at that
  vertex and group 42 is its bulge.
- Autodesk [`AcDb2dPolyline::defaultStartWidth`](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDb2dPolyline__defaultStartWidth.html):
  a vertex without its own group-40 value uses the owning polyline's default;
  DXF output omits a vertex value equal to that default. The same contract
  applies to end width, which means an omitted value and an explicit zero must
  remain distinguishable.
- Autodesk [`SetWidth`](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-ActiveX-Reference/files/GUID-ED45F9D1-AE03-4DF0-9F2D-2019BD42CD52.htm):
  start and end width belong to the segment at the selected polyline index.
- Autodesk [`PLINE` command](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-11883C70-6435-4F80-8FB4-F6E933B8FD94-htm.html):
  width is centered on the segment; wide-segment junctions are normally
  beveled; nontangent arcs, very acute angles, and dot-dash linetypes have
  documented exceptions; Width/Halfwidth values persist into later segments.
- Autodesk [`FILLMODE`](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-FC385D70-45AA-4B9A-848A-CA3906C36124.htm):
  wide polylines participate in the drawing-level filled/unfilled policy.
- Autodesk [`FILL`](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-A09941AF-919C-4E72-96F4-3DF3C11922DE.htm)
  and the official [wide-polyline display troubleshooting](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Wide-polylines-do-not-appear-as-filled-in-AutoCAD.html):
  disabling fill displays wide polylines as outlines, while REGEN applies a
  changed setting. Autodesk's published fill-off sample and independently
  observed multi-segment output show segment and join boundaries rather than a
  boolean-unioned exterior perimeter.
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
  centered on the path; explicit widening produces an outline using a caller
  flattening tolerance. ProGPU adopts retained outline lowering for exact
  straight tapers and rejects tolerance-flattened curved taper boundaries.
- Direct2D [`ID2D1Geometry::Outline`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-outline)
  produces a fill-equivalent outline after removing transverse intersections,
  and Skia [`SkPathOps`](https://github.com/google/skia/blob/main/include/pathops/SkPathOps.h)
  union/simplify operations construct non-overlapping contours. Both were
  rejected for FILLMODE lowering because eliminating the original segment and
  join seams changes the observed CAD output.
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
  retained topology and a single encoded line width can be consumed by a GPU
  stroker without author-time tessellation; current Vello has no per-segment
  tapered-width scene contract. ProGPU keeps its constant fast path and uses a
  filled-path representation for exact straight tapers.
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
- ACadSharp feature commit
  [`817fd18d`](https://github.com/wieslawsoltes/ACadSharp/commit/817fd18d1708a25ba8c13db8db05d7a4f127ab3c)
  adds width-presence state to
  `Vertex` and `LwPolyline.Vertex`. DXF read/write now preserves omitted values
  separately from explicit zero; LWPOLYLINE DWG width arrays carry explicit
  zero pairs, while a legacy DWG zero pair retains its established entity-
  default fallback. This is original ProGPU-owned fork work and is covered by
  matched ACadSharp and ProGPU round-trip tests.

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

For a variable-width straight segment with endpoint half-widths `a` and `b`,
the two source endpoints offset by `+/- a` and `+/- b` form one exact convex
trapezoid (or triangle when one endpoint width is zero). At each non-collinear
interior vertex, the preceding end width and following start width form the
outer bevel triangle. Every trapezoid and bevel is a consistently wound figure
inside one `FillRule.Nonzero` `PathGeometry` and one `DrawPath` command. The GPU
therefore evaluates the union once, including translucent brushes, instead of
blending overlapping segment draws. Nonuniform and sheared block transforms
apply to this complete source-space fill. This is exact O(S) work and storage
for S straight segments with no tessellation tolerance.

The immutable snapshot also captures drawing-level `FILLMODE` on each planar
polyline. When fill is disabled, the plan and print compilers use the ordinary
resolved entity lineweight to stroke one retained path containing separate
closed figures: an exact quadrilateral for each straight or tapered segment,
an exact concentric annular sector for each constant-width bulge, and an exact
triangle for each applicable straight bevel cell. Figures are deliberately not
boolean-unioned, so segment endpoints, join edges, and self-crossing overlap
remain visible. The analytic arcs retain their original sweep and the complete
path retains the OCS/block/page transform. A half-width equal to the source arc
radius is exact because the inner boundary collapses to the center point.

A tapered circular bulge has radius `r +/- w(t)/2`, where width is linear in
the bulge parameter. Its boundaries are spiral-like rather than finite circular
or rational-quadratic arcs. Direct2D widening and CPU flatteners would introduce
a scale-dependent tolerance; substituting a constant stroke would change both
pixels and selection. ProGPU therefore rejects that profile until a shared
analytic curved-boundary primitive and matched managed/native solver exist.

## Exact selection contract

Point and Crossing selection consume the same source-space butt-cap/bevel-join
topology as rendering. A straight segment is one affine quadrilateral with
independent endpoint widths;
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

FILLMODE changes display and plot representation, not the authored polyline's
geometric width. ProGPU therefore keeps the existing exact filled-strip point,
Window, and Crossing selection contract when the display is hollow. This is an
explicit ProGPU inference from Autodesk's separate fill/display and selection
contracts; it avoids changing editability or spatial bounds when FILLMODE is
toggled.

Selection is O(S) time for S segments. Each constant-width bulge uses at most eight rational
quadratic boundary tests plus two endpoint sections; all temporary controls,
box corners, and roots use bounded stack storage. Warm point and box queries
allocate zero managed memory and do not initialize a GPU backend.

## Deliberate fail-closed behavior

- Variable-width straight `LWPOLYLINE` and legacy `POLYLINE` segments are
  exact. Uniform per-vertex profiles collapse back to the constant analytic
  path, including bulges. Open terminal-vertex widths do not describe a
  segment; the final vertex of a closed polyline does.
- A variable-width bulge remains unsupported because its offset boundaries are
  not circular arcs. A variable profile containing an entire zero-to-zero
  segment also remains unsupported: that segment is a skinny stroke, so exact
  output needs a mixed fill/stroke batch contract rather than silently dropping
  a degenerate fill.
- For FILLMODE off, a constant-width bulge whose half-width exceeds its source
  radius remains unsupported. Its inner signed boundary crosses the center and
  changes topology; a positive-radius annular sector would be incorrect. The
  equal-radius point collapse is supported.
- Simple or complex linetypes on a wide polyline emit `CADSCENE009` and replay
  one continuous wide path. Autodesk documents cap/join exceptions for
  dot-dash patterns; the existing zero-width linetype lowerer cannot preserve
  them.

## Complexity, retention, and parity audit

Snapshot width resolution, FILLMODE capture, normalization, and exact bounds are O(V) time for V
vertices and O(V) retained vertex storage. Widths live beside the existing
immutable vertex topology rather than in a pointer-bearing parallel object
graph. Scene recording is O(V): each entity produces one retained command;
constant profiles use O(U) cached pens for U distinct `(style,width)` pairs,
while a tapered fill retains at most S trapezoids and S bevel triangles and a
hollow profile retains at most S segment cells and S bevel cells in one path.
Constant bulges add two analytic arcs and two radial edges each. Stable replay,
pan, zoom, and print replay do not rebuild CAD geometry and introduce no
per-frame managed/native crossing, retained upload, or CAD allocation.

## Release measurements and Instruments correlation

The arm64 macOS Release candidate was measured serially with two matched
10,000-entity fixtures. Both use the same two straight segments and produce
exactly 10,000 retained scene commands; the control uses one constant-width
analytic stroke per entity and the candidate uses two tapered trapezoids plus
one bevel in one fill command. Five warmups and 40 measured iterations were
used. Times are milliseconds; allocations are managed bytes per complete
operation.

| Operation | Constant p50 / p95 / p99 | Tapered p50 / p95 / p99 | Constant / tapered allocation |
|---|---:|---:|---:|
| Snapshot compile | 10.204 / 18.121 / 21.618 | 10.274 / 14.566 / 18.259 | 15,354,689 / 15,354,789 B |
| Plan-scene compile | 5.281 / 20.668 / 25.866 | 6.919 / 27.461 / 35.630 | 10,561,366 / 15,361,217 B |
| Print-plan compile | 17.309 / 38.907 / 48.629 | 20.820 / 42.646 / 43.615 | 11,805,670 / 16,605,867 B |
| Spatial query (nanoseconds) | 4,700 / 7,000 / 22,000 | 4,700 / 6,900 / 14,700 | 0 / 0 B |

The additional scene/print storage is the intended bounded O(S) retained path
topology: three filled figures replace one stroked figure for this two-segment
fixture, while command count stays one per entity. Snapshot allocation is
effectively unchanged, selection stays allocation-free, and stable picture
replay performs no CAD compilation or upload.

Subsequent source changes were confined to legacy `POLYLINE` temporary-storage
removal and unused open-terminal validation; neither branch is reachable from
these `LWPOLYLINE` fixtures. A same-final-DLL matched recapture retained the
same 10,000-command result, zero query allocation, and bounded topology
allocation delta. Its absolute percentiles were slower and more variable while
two user-owned virtual machines and an unrelated long-lived Mono test consumed
multiple cores, so they are retained as a contention audit in the JSON artifact
instead of replacing the controlled table or being misclassified as a code
regression.

Xcode Instruments 16.0 then launched the same final DLL in matched Time
Profiler (20 iterations) and Allocations/VM Tracker (10 iterations) runs for
both fixtures. All four final processes exited normally. The exported Time
Profiler tables contain 3,873 constant and 3,845 tapered samples and zero Potential Hangs
rows. Allocations retained freed events and all heap/VM types; their benchmark
JSON reports the same bounded managed allocation relationship shown above.
Metal System Trace is not applicable to this CPU snapshot/scene-compilation
workload: it creates no device, GPU resource, command buffer, submission, or
readback, and the change adds no shader or native GPU contract. Managed/native
picture compilation is instead covered by the focused semantic regression.
Raw `.trace` bundles and XML exports remain task-local; this table is the
retained compact export. The machine-readable counterpart is generated locally
under the ignored `artifacts/benchmarks/cad-wide-polyline-comparison.json` path.

The FILLMODE checkpoint adds a second same-final-binary, interleaved Release
comparison with 10,000 entities, five warmups, and 40 measured iterations. Each
entity contains one straight and one constant-width bulge segment; both modes
record exactly 10,000 commands. The host remained heavily contended by two
user-owned virtual machines and an unrelated Mono process, so these values are
a reproducible contention audit rather than a clean absolute baseline.

| Operation | Filled p50 / p95 / p99 | Hollow p50 / p95 / p99 | Filled / hollow allocation |
|---|---:|---:|---:|
| Snapshot compile | 30.018 / 55.460 / 68.713 | 26.451 / 47.107 / 64.927 | 15,354,779 / 15,354,620 B |
| Plan-scene compile | 25.494 / 48.557 / 55.797 | 48.823 / 75.233 / 79.583 | 10,721,424 / 13,601,402 B |
| Print-plan compile | 31.935 / 65.331 / 100.914 | 36.713 / 90.528 / 113.264 | 11,966,703 / 14,847,031 B |
| Stable replay | 118.116 ms | 92.303 ms | 0 / 0 B |

The approximately 2.88 MB plan/print delta is the intended bounded analytic
outline topology. Snapshot allocation is effectively unchanged, command count
does not increase, and stable replay remains zero-allocation. Xcode Instruments
16.0 Time Profiler launched the same final benchmark successfully: the target
exited with status zero and the exported table contains 7,040 samples spanning
00:04.286 through 00:12.523. Allocations/VM Tracker launch injection stalled
before managed initialization, and attach mode required an administrator
credential unavailable to this run; incomplete traces are not counted as
evidence. The managed allocation counters above provide the available
correlation. Metal System Trace remains genuinely inapplicable because this
CPU-only workload creates no WebGPU device, resource, command buffer,
submission, or readback. The machine-readable evidence and exact limitation
are generated locally under the ignored
`artifacts/benchmarks/cad-fillmode-outline-comparison.json` path.

The managed and native renderers consume the same `GpuPicture` path and the
same existing ProGPU path/stroke compiler. No C ABI record, generated C# wire type,
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
containment, exact tapered trapezoids and discontinuous bevels, explicit-zero
DXF/DWG presence, zero warm-query allocation, bounded shared pen identity,
exact FILLMODE-off straight/tapered cells and concentric bulge sectors,
lineweight use, print replay, managed/native compilation, authoring and
Undo/Redo, DXF/DWG round trips, signed-inner-boundary rejection,
curved-variable rejection, and patterned fallback diagnostics. The final
focused wide-polyline/authoring suite passes 71/71 in Debug, and the full project suite
passes 1,362/1,362 in both Debug and Release. The ACadSharp width-writer focus
passes 5/5 on net48; after the
public omission-reset API was added, its current width-presence subset passes
2/2 on net48. A full upstream net48 run was attempted separately, then cancelled
after 46 minutes when the Rosetta/Mono host reached a 7 GB peak and prolonged
paging without another result; this environmental runner pathology is not
reported as a pass or a product failure. Fresh `ACadSharp.ProGPU` and
`ProGPU.CAD` packages at
`0.1.0-preview.62` pass the two-package content/dependency audit. The fork
package has one net10 asset/dependency group and records feature commit
`817fd18d`; `ProGPU.CAD` depends on the exact same-version fork identity. An
isolated package-only consumer uses package-source mapping that excludes
upstream ACadSharp, restores a byte-identical fork assembly, builds with 0
warnings and 0 errors, and creates an AC1032 document.
The grouped wrapper remains unavailable while the separately user-deleted
browser sample project is absent; validation did not restore or stage it.
