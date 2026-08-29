# ProGPU.CAD Architecture and Delivery Specification

Status: foundation, 2026-08-29

## Scope

`ProGPU.CAD` is the CAD application and engine layer for opening, processing,
editing, rendering, printing, and saving DXF/DWG documents on desktop and in the
browser. ACadSharp owns the file-format object model. ProGPU owns presentation,
interaction, acceleration, text, printing, and platform integration.

The existing `ProGPU.Dxf` package remains available during migration. It is an
approved in-repository source for direct ports into `ProGPU.CAD`; it is not the
new document model. New features target ACadSharp `CadDocument` and must not add
new netDxf dependencies.

## Clean-room provenance

No third-party renderer implementation is copied, ported, translated, or
transcribed. External engines below were consulted only for public contracts,
documented algorithms, architecture, observable behavior, and test ideas.
ACadSharp is consumed as the reviewed MIT-licensed submodule at
`external/ACadSharp`. Its public object model and readers/writers are a normal
dependency, not copied implementation.

Approved ProGPU-owned sources that may be ported directly are:

- `src/ProGPU.Dxf`: the current DXF renderer, retained static-buffer adapter,
  hatch handling, and SAT/SAB readers.
- `src/ProGPU.Scene/Extensions`: spline, hatch, ACIS, 3D line, and retained-DXF
  compositor extensions.
- `src/ProGPU.Scene/Shaders/AcisSolid.wgsl`, `Hatch.wgsl`, `Line3D.wgsl`,
  `RetainedGlyph.wgsl`, and the managed/native canonical `Native3D.wgsl`.
- `src/ProGPU.Backend/Shaders/GlyphRasterizer.wgsl`, `PathRasterizer.wgsl`, and
  `Text.wgsl`.
- The managed/native scene compilers and shared native C ABI already in this
  repository.

Every direct port must name its exact source file in the change and add matched
old/new differential tests. Foreign file organization, helper names, control
flow, lookup data, and comments are prohibited implementation sources.

## Architectural boundaries

```text
DXF / DWG bytes
      |
      v
ACadSharp readers <--> mutable CadDocument <--> ACadSharp writers
                           |
                  edit transaction + generation
                           |
                           v
              immutable ProGPU.CAD snapshot
                /          |           \
       spatial index   resource table   semantic handles
                \          |           /
                    viewport compiler
                           |
             retained ProGPU scene/resources
                           |
             managed or native compositor
                           |
         screen / browser / bitmap / print target
```

## Implemented immutable-scene slice

The first phase-2 slice is implemented in `src/ProGPU.CAD`:

- `CadSnapshotCompiler` captures the mutable document and matching content
  generation under one lock, resolves visible layers and indexed stroke styles,
  normalizes supported planar entities from OCS to double-precision WCS, and
  emits fixed typed line, circle, arc, ellipse, spline, polyline, solid, and
  3D-face streams.
- `CadSpatialIndex` is an immutable median-split AABB hierarchy. Construction is
  `O(N log N)`; caller-buffer queries allocate no managed memory and are
  `O(log N + K)` on typical spatial data, `O(N + K)` worst case.
- `CadPlanSceneCompiler` rebases WCS coordinates and records an exact top-view
  projection through retained analytic ProGPU commands. Circle projection uses
  the existing affine analytic ellipse primitive, arcs use one `ArcSegment`,
  and NURBS use the retained spline extension. It performs no fixed-detail
  curve tessellation and carries no viewport or camera state. Its print path can
  exclude non-plottable layers before recording without changing ordinary screen
  visibility.
- `CadPrintPlanCompiler` maps one immutable snapshot into bounded physical paper,
  printable-margin, extents/window, fit/custom-scale, centered/offset, DPI, and
  fixed-lineweight state. It replays one filtered analytic `GpuPicture` under a
  printable-area clip; preview and later output adapters therefore share the
  same generation, camera, vector paths, shaped glyph runs, and physical stroke
  widths. Page dimensions remain integer output pixels with an explicit total-
  pixel budget, while model/page mapping remains allocation-free double-to-float
  matrix math after compilation.
- `CadPageSetupCatalogCompiler` captures layouts and standalone named
  `PLOTSETTINGS` overrides under the same document-generation lock into bounded,
  deterministically ordered, ProGPU-owned snapshots. It copies all strings and
  retains physical media, margins, plot origin, target space, rotation, plot
  area/window, custom and standard scale state, lineweight/style flags, and
  shade policy without retaining an ACadSharp object. The paired typed lowerer
  accepts only the currently exact model-space extents/wireframe subset and
  reports every unsupported coordinate, paper, style, or shade policy before a
  print plan is created.
- Lightweight polylines retain their whole path as one command. Straight and
  positive/negative bulge segments remain analytic in the entity OCS, with a
  checked affine OCS-to-WCS projection. Wide polylines are deliberately reported
  as unsupported until filled-outline lowering lands; they are not confused with
  cosmetic lineweight.
- Legacy 2D POLYLINE uses the owning polyline elevation and OCS normal, ignores
  the historically unused vertex Z value, and shares the same one-path analytic
  bulge representation. Legacy 3D POLYLINE retains an independent packed WCS
  point stream with exact XYZ bounds and records one top-projection path. Width,
  extrusion, and unresolved curve/spline-fit semantics are reported rather than
  flattened or silently treated as centerlines.
- Ellipses and elliptical arcs preserve their WCS major/minor basis, parameter
  sweep, and exact double-precision extrema. The plan compiler records one unit
  analytic ellipse or arc under an affine transform, never a sampled polygon.
- SOLID entities retain exact OCS-normalized corners, normalize Autodesk's
  persisted zig-zag `first,second,third,fourth` order to perimeter
  `first,second,fourth,third`, and retain signed thickness as one WCS extrusion
  vector. Flat SOLIDs render as even-odd filled plan paths; extruded SOLIDs
  record the exact projected base/top/connector shell as wire geometry. 3DFACE
  entities retain perimeter-ordered WCS corners and invisible-edge flags. The plan
  wireframe records only visible 3DFACE edges, while both records also compile
  to exact Flat triangle surfaces for the shared managed/native depth path.
  Nonzero ellipse thickness remains reported until complete 3D side-surface
  lowering is available.
- Normal/Outer/Ignore solid and exact DXF/PAT patterned HATCH entities retain
  bounded packed loops and analytic line/circular-arc/elliptic-arc plus exact
  degree-one-through-three polynomial and positive-weight rational-quadratic/
  rational-cubic
  spline segments in one OCS-relative double-precision coordinate system. The
  plan compiler records
  one even-odd filled path per hatch with one affine OCS-to-plan transform;
  patterns add one retained procedural brush without generating hatch-line
  geometry. Multiple families, tangent row shifts, and the specified six-item
  positive-dash/negative-gap/zero-dot grammar remain compact and procedural.
  Normal keeps every loop, Outer keeps nesting levels zero and one, and Ignore
  keeps only level zero in the rendered path while all source loops remain in
  the immutable snapshot. It never samples a curve or emits a command per
  edge/line. Above-cubic spline edges, gradient, annotative, paper-oriented,
  coincident, and malformed hatches remain explicit
  diagnostics rather than approximated fills.
- Single-line TrueType TEXT resolves through a typed host font service, shapes
  during immutable snapshot construction with the existing ProGPU Unicode/
  OpenType pipeline, and stores packed glyph indices, positions, font runs, and
  conservative metric/outline-aware bounds. OCS normal/rotation, effective
  entity width, oblique shear, generation mirrors, justification, and nested
  block transforms compose into one affine glyph-run transform. Recording stays
  one `DrawGlyphRun` command per
  contiguous fallback-font run and requests retained vector coverage for CAD
  zoom; it never expands ordinary text into per-glyph path commands. Font
  substitution is an explicit diagnostic. Overline, underline, and strike-
  through toggles retain bounded filled decoration rectangles in the same
  affine basis. Extrusion remains a diagnosed fidelity gate. Documented
  decimal-character, degree, plus/minus, diameter, percent, and DXF Unicode
  escapes are decoded before shaping.
- Model-space lineweights are recorded as fixed device-space strokes; explicit
  zero-width lineweights use the ProGPU hairline sentinel. Referenced LTYPE
  definitions are captured once into bounded packed pattern/element tables;
  resolved styles point to those tables and carry the checked product of
  drawing `LTSCALE` and entity scale. Simple A-aligned dash/gap/dot patterns are
  lowered in model space for lines, circles/arcs, ellipses, lightweight and
  legacy 2D polylines, 3D polylines, and visible 3DFACE edges while retaining
  one `DrawPath` command per entity. Circular, bulge, and elliptical pieces
  remain analytic arcs, and the resulting path still uses fixed-device CAD
  lineweight. Entity PLINEGEN state selects uninterrupted 2D-polyline traversal
  or endpoint alignment at every segment. Open, closed, and periodic rational
  SPLINEs through degree ten use one uninterrupted WCS arc-length traversal and
  exact retained rational subcurves; compact periodic records and already
  cyclically extended knot vectors normalize to one standard evaluator form,
  while a nonperiodic closure is an exact degree-elevated line span. Persisted relative/absolute
  complex text and SHX-shape descriptors share definition-owned payloads and
  retain tangent-aware placements. LIN-only upright rotation, nonzero complex advances,
  decorated linetype text, independently observed AutoCAD dot-first/closed-
  pattern residual distribution, and non-A alignment remain explicit diagnostics rather than unbounded or
  silently approximate expansion.
- `CadShxFont` provides the first bounded SHX source layer. It parses the
  standard compiled `AutoCAD-86 shapes 1.0` container into one immutable owned
  byte store, retains each shape program as a packed slice, validates the
  directory/range/record boundaries and exact EOF marker, and exposes the
  standard font header metrics. `CadShxInterpreter` executes standard commands
  0 through 14 into caller-owned retained analytic line/arc paths with bounded
  recursion, commands, output, scale, coordinates, and the specified four-entry
  position stack. `CadShxGlyphCache` and `CadShxTextLayout` retain interpreted
  glyphs per font/shape/orientation and produce bounded standard-font character
  placements. A typed host resolver supplies those caches to horizontal SHX
  TEXT, standard horizontal SHX MTEXT, and default-insertion dual-orientation
  vertical TEXT lowering. The immutable snapshot packs placements, paint and
  transform runs, MTEXT masks/decorations/separators, coalesced single-line
  decoration strokes, and affine text bases; the plan compiler records each
  drawable placement with its shared analytic glyph path. Compiled Unicode and
  Big Font containers, vertical/RTL SHX MTEXT, mixed TrueType/SHX MTEXT runs,
  and non-default/decorated vertical TEXT placement remain explicit gates. Ordered,
  bounded desktop discovery is host initialization work rather than a render-
  path filesystem dependency.

### Retained HATCH island-style contract

Autodesk defines HATCH boundary coordinates in OCS and defines Normal island
detection as odd parity, Outer as the first inward level without resuming, and
Ignore as filling through internal loops. `CadSnapshotCompiler` accepts finite,
closed, non-gradient solid and patterned hatches with all three styles. It
normalizes each
polyline bulge or ordered line, circular-arc, elliptic-arc, and supported
polynomial spline edge into a
`CadHatchSegment`; loops refer to contiguous segment ranges and the entity
refers to a contiguous loop range. One world origin plus an orthonormal OCS is
stored per hatch, so large WCS translations remain outside the local float
geometry used for retained drawing. Signed polyline bulges and clockwise edge
arcs remain analytic. Full turns are represented in the snapshot as one
parametric segment and split into two half arcs only when recorded, matching the
existing ProGPU path contract. Every source loop remains retained. Each loop
also carries an immutable `ContributesToFill` decision: Normal marks all loops,
Outer marks topology depths zero and one, and Ignore marks only depth zero.
Rendering then selects those contours into the same one even-odd path. The
result is independent of source-loop order and supports multiple disconnected
outer regions without trusting the unrelated DXF `External` bit as topology.

Normal compilation is transactional and `O(L + S)` time/storage for `L` loops
and `S` segments. Outer/Ignore add exact analytic containment visits after
double-precision loop-bound rejection. Their worst case is `O(L^2 * S)`, so a
shared document-wide 10,000,000-visit budget counts both pair probes and every
examined segment; successful and failed entity attempts consume the same
budget. Defaults also bound one snapshot to 1,000,000 hatch loops and 5,000,000
hatch segments. Failure cannot publish a partial entity or orphan its local
loop/segment data. Explicit self-intersecting/duplicate flags and coincident or
touch-only topology without an unambiguous analytic sample are diagnosed rather
than guessed. Exact double-precision arc and polynomial-derivative extrema
supply world bounds.
Recording is `O(L + S)`, emits one `DrawPath` command and one transform per
hatch, and delegates winding coverage and antialiasing to the existing canonical
ProGPU analytic path pipeline. There is no hatch-specific shader, upload, cache,
or managed/native crossing.

Horizontal-WCS point selection uses the same retained contribution set. Line,
ellipse, quadratic, and cubic ray crossings use direction-aware half-open
endpoint ownership so a shared vertex is counted once, including clockwise full
turns and Bezier tangencies. Zero-tolerance queries and line/Bezier proximity
are exact and allocation-free. Elliptic-edge proximity outside the fill with
positive tolerance, and point/Crossing queries for a non-horizontal hatch plane,
return typed unsupported results instead of a tessellation guess. Window
selection is exact from the entity bounds; horizontal Crossing selection combines
exact analytic boundary/box intersection with rectangle-corner fill containment.
Query cost is `O(S)` time and `O(1)` bounded stack storage.

The pinned ACadSharp fork now reads DXF group 75 into `Hatch.Style` at commit
[`94597d6c`](https://github.com/wieslawsoltes/ACadSharp/commit/94597d6c3b7f6a296d7048ca3e8cff6f0add976c), with an
independent writer/reader round-trip regression; previously its writer emitted
the field but its reader discarded it. This is an original ProGPU.CAD
implementation. The exact approved in-repository
provenance inspected was `src/ProGPU.Dxf/DxfHatchRenderer.cs`, the generic path
records in `src/ProGPU.Vector`, and the managed/native scene compilers. The old
renderer's fixed 16-step arc flattening, per-pattern-line CPU construction, and
mutable entity cache were specifically rejected; no implementation text,
helper structure, or control flow was ported. ACadSharp's pinned public HATCH
types were used only as the persisted object-model contract. The native C++ tree
has no CAD document frontend, so a paired native snapshot compiler is not
applicable. Both renderers consume the same existing retained filled path and
analytic arc commands; polygonal and curved native-picture regressions cover
that shared boundary. No canonical shader, C ABI, compositor, atlas, DPI,
invalidation, or device-loss behavior changes.

### Exact polynomial and positive-weight rational HATCH spline-edge contract

Autodesk's HATCH boundary record carries degree, rational and periodic flags,
knots, weighted control points, and optional fit/tangent data. Snapshot
construction validates all of that persisted input and retains the exact subset
that the shared filled-path contract can represent: degree-one rational spans
 (whose locus is a line), polynomial quadratics/cubics, and positive-weight
rational quadratics/cubics. A rational quadratic Bezier span with endpoint weights
`w0,w2` is reparameterized monotonically to unit endpoint weights and canonical
middle weight `w1/sqrt(w0*w2)`; Cartesian control points and the oriented curve
locus are unchanged. A rational cubic span with source weights `w0..w3` uses
the exact positive projective reparameterization
`v1 = exp(log(w1) - 2/3 log(w0) - 1/3 log(w3))` and
`v2 = exp(log(w2) - 1/3 log(w0) - 2/3 log(w3))`, yielding canonical weights
`[1,v1,v2,1]` without changing its Cartesian controls, oriented locus, or
endpoints. Degrees four through ten remain typed unsupported until the shared
path ABI gains genuine matching segments. They are never tessellated or made
zoom-dependent.

Ordinary, compact-periodic, and validated cyclically expanded knot records share
`ICadCanonicalSpline` and the original ProGPU `CadRationalBezier` homogeneous
knot-insertion implementation. Each non-empty knot span becomes one exact line,
`QuadraticBezierSegment`, `RationalQuadraticBezierSegment`, or
`CubicBezierSegment`/`RationalCubicBezierSegment`. The shared extractor now keeps
the controls on both sides of an interior span; this fixes the previously latent
periodic/interior-span discontinuity instead of hiding it in HATCH-specific code.
The final periodic span owns the same homogeneous seam point as the first span.
Matched standalone-spline selection and linetype regressions cover this shared
change. Fit points and tangents are validated as source metadata; the persisted
control/knot representation remains the rendering authority.

For `C` controls, `K` knots, `B` non-empty spans, and degree `P <= 3`, validation
is `O(C + K)`, exact extraction is `O(B*P^2)`, and retained storage is `O(B)`.
The document-wide default 10,000,000-scalar source budget counts three values per
control, knots, fit-point coordinates, and authored tangents on successful and
failed attempts. It complements the existing loop, segment, and topology budgets;
failure publishes no entity or orphan stream. Tight rational bounds isolate the
homogeneous derivative numerator roots in Bernstein form. Point containment
isolates `Y(t)-queryY*W(t)` roots and
applies the same derivative-directed half-open ownership used by the canonical
path rasterizers. Positive-tolerance Bezier proximity and Crossing-box tests
reuse `CadSplineSelection`'s exact bounded Bernstein algorithms. Construction,
selection, and retained replay allocate no geometry samples.

This is an original ProGPU clean-room extension. Exact approved in-repository
provenance is `CadCanonicalSpline.cs`, `CadRationalBezier.cs`,
`CadSplineSelection.cs`, `CadSnapshotCompiler.Hatch.cs`, and
`ProGPU.Vector/PathGeometry.cs`; ACadSharp supplies only its public persisted
HATCH object model. The native C++ tree has no CAD document compiler, while the
managed and native renderers consume the same canonical `PathRasterizer.wgsl`.
The stable 48-byte path record uses kind 4 for the canonical rational quadratic:
`p0..p2` hold its controls, `p3` is zero, `pad0` holds the positive finite float
weight bits, and `pad1/pad2` are zero. Kind 5 retains a canonical rational cubic
in `p0..p3`, with positive finite `v1/v2` bits in `pad0/pad1` and zero `pad2`.
Polynomial and rational cubics deliberately share one homogeneous shader branch:
kind 2 selects unit weights while kind 5 selects the retained positive weights.
This is the same analytic equation, keeps output and complexity unchanged, and
bounds canonical shader size and D3D12 shader-compilation pressure without a
duplicated polynomial implementation.
Every managed/native scene, clip,
layer-mask, and execution validator enforces that form; glyph validation remains
limited to its existing line/quadratic/cubic domain. The managed retained-HATCH
fragment shader applies the same analytic winding equation because its direct
procedural-brush pipeline has a different stage/interface from the canonical
compute atlas shader; it does not define a different segment representation,
root equation, winding rule, or complexity contract. Raster work remains `O(A*S)`
for `A` samples and `S` segments with constant storage; CAD extraction remains
`O(B*P^2)` and `O(B)` storage.

Rational quadratic and cubic fills, clips, CPU/GPU hit queries, transforms, hashing,
native-picture replay, printing, periodic topology, exact CAD selection, and
device rebuild from retained records are covered. Rational strokes and geometric
path-boolean reconstruction return explicit unsupported failures because those
pipelines do not yet retain a weighted output verb; they are not flattened.
No ABI-size/version, crossing-count, per-frame allocation, DPI, atlas-generation,
upload-retention, or device-loss ownership contract changes.

### Retained DXF/PAT patterned-HATCH contract

The patterned contract accepts finite, non-gradient Normal/Outer/Ignore HATCHes with
one or more persisted line families. `CadHatchPattern` addresses shared packed
family and dash streams. Each family retains its local base point, unit tangent,
positive perpendicular spacing, signed tangent row shift, dash range, and dash
period. Positive dash values draw, negative values skip, and zero values draw a
dot; the six-value bound is Autodesk's PAT definition maximum rather than a
ProGPU approximation.
ACadSharp's line records are authoritative because its DXF/DWG readers assign
overall angle/scale metadata before appending the already evaluated line
records; snapshot construction does not rotate or scale them a second time.
Signed normal offsets are normalized by reversing both row direction and
tangent shift, preserving the same integer row lattice. Continuous families
retain zero dash records.
DXF group 77 adds the perpendicular family only for user-defined type 0; the
flag is ignored for predefined/custom patterns as Autodesk specifies.

Construction retains the exact solid-HATCH boundary pipeline and adds
`O(F + D)` time/storage for `F` families and `D` dash items. Separate pattern,
family, and dash budgets are checked before publication, so an over-limit or
invalid entity cannot leak a pattern, family, dash, loop, or segment. The plan
compiler emits the same one analytic `DrawPath` command. One-family continuous
and equivalent perpendicular two-family patterns retain the zero-auxiliary
`HatchPatternBrush`/`CrossHatchBrush` fast paths. Other definitions use one
`HatchPatternSetBrush`; the fragment shader evaluates the periodic field in
`O(F * 6)` time and `O(1)` private storage. Zero brush thickness means a
derivative-filtered one-device-pixel hairline; positive thickness remains in
pattern coordinates. Camera, DPI, and INSERT transforms therefore change
placement and coverage in the GPU without CPU line generation, curve
flattening, or per-frame allocation.

Managed/native applicability was audited and implemented together. Four
existing 32-byte auxiliary records encode each family: base/tangent/spacing,
row shift/period/count, and six dash values. The 256-byte brush ABI, bindings,
and crossing count are unchanged; the scene-wide 65,536-record budget is
preflighted transactionally. Both semantic compilers validate normalization,
periods, record counts, unused values, and reserved zeros. The managed renderer
and native C++ renderer consume the same canonical `Vector.wgsl`; the legacy
retained hatch extension's canonical `Hatch.wgsl` uses the same packed grammar
and coverage algorithm. Picture archive version 5 preserves families, dashes,
color, thickness, opacity, and transform while versions 1–4 remain readable.
Live GPU pixels cover both shader entry paths;
native-picture and print regressions cover the CAD path. CAD deliberately uses
ordinary `DrawPath`, not the hatch extension, because only the former retains
elliptic `ArcSegment` boundaries across both renderers.

Horizontal point selection first evaluates exact odd-parity clipping and then
each affine family. It projects onto the world-space tangent, tests periodic
dash segments/dots, and visits rows only while their exact perpendicular lower
bound can improve the result, capped at 4,096 visits before returning typed
unsupported. Nonuniform, rotated, and sheared INSERT transforms remain exact
and allocation-free. A nearest family point that cannot be proven inside the
clipped fill, an
outside positive-tolerance query, and patterned Crossing selection return typed
unsupported results. Window selection remains exact from analytic bounds.
Patterned Crossing selection remains an explicit transactional gate
until bounded exact boundary/pattern intersection is implemented; the old
renderer’s CPU hatch-line explosion and fixed-detail clipping remain rejected.

### Retained simple-linetype contract

The immutable snapshot owns `CadLineTypePattern` records and one packed
`CadLineTypeElement` stream. Only referenced definitions are captured, names are
interned case-insensitively, and publication is transactional: an invalid or
over-limit definition cannot leave an orphan partial pattern. Defaults bound a
generation to 65,536 referenced definitions and 1,000,000 descriptors. Each
stroke style contains a fixed pattern index and an effective model scale equal
to `LTSCALE * entity.LineTypeScale`; zero, negative, non-finite, overflowed, or
structurally invalid simple patterns are rejected before scene publication.

`CadLineTypeLowerer` is the CAD semantic stage. For an open A-aligned primitive
whose first descriptor is a positive dash, it places half that first dash at
each endpoint, fits as many complete pattern periods as possible, and divides
the remainder equally between the endpoint dashes. A primitive shorter than one
period is drawn continuously, matching Autodesk's documented short-entity rule.
Zero first descriptors retain start/end dots; because Autodesk specifies those
endpoints but does not publish residual-distribution math for dot-first patterns,
the current deterministic rule chooses the nearest integral repetition count
and scales that period uniformly. Closed entities use the same integral-period
fit because Autodesk documents only a “reasonable display” for objects without
endpoints. Both choices remain named differential-conformance seams rather than
claims about unpublished implementation details.

Polyline traversal uses the stored entity flag, not the current document
`PLINEGEN` default: disabled lightweight/2D polylines restart A alignment at
each vertex, while enabled ones carry one scalar pattern through line and bulge
segments. Three-dimensional polyline edges and 3DFACE edges restart at each
vertex because Autodesk exposes uninterrupted generation only for lightweight/
2D polylines. The lowerer first counts output and aborts transactionally when the
document-wide default limit of 1,000,000 figures, 4,000,000 visited pattern
descriptors, or 1,000,000 source segments would be exceeded. Successful and
failed attempts consume those shared budgets. The separate descriptor-step
budget prevents a gap-heavy definition from hiding unbounded work behind a
small visible-figure count, while the source budget prevents short-pattern
special cases from repeatedly scanning large polylines. A per-entity limit of
16,384 bulge-arc or non-empty NURBS-span maps additionally bounds the 128-bin
scratch amplification.
The successful preflight is repeated deterministically during emission, so
descriptor traversal is at most twice the configured preflight budget. It then
emits one retained path with one figure per visible dash/dot; source arcs are
split into analytic `ArcSegment` values. A fixed 128-bin, eight-point
Gauss-Legendre arc-length map supplies deterministic bounded distance inversion
for affine ellipses and non-uniform block images without flattening their
geometry. For S source segments, Q visited pattern descriptors, and F output
figures, compilation is `O(S + Q + F log S)` time and `O(F)` retained storage,
with `Q` explicitly bounded by the descriptor-step option in each pass.
Camera replay, lineweight, and upload contracts do not depend on entity count or
zoom after that retained picture is published.

### Exact rational-spline and cyclic-seam linetype contract

An open degree-one B-spline or NURBS with a canonical nondecreasing knot vector
and strictly positive active knot spans is geometrically the ordered chain of
its control points. Positive rational weights change parameter speed but not
that locus. The linetype lowerer therefore measures the original WCS 3D control
edges, applies one uninterrupted A-aligned simple or complex pattern across the
whole spline, and emits exact retained line segments plus tangent-aware complex
placements. It neither samples the spline nor uses the renderer's viewport-
dependent spline tessellation as CAD pattern geometry. Source-segment,
figure, placement, and descriptor budgets are preflighted through the same
transactional path as other supported entities. For N control points this adds
`O(N)` time and scratch storage before the existing bounded pattern traversal.

Higher-degree curves use bounded arc-length inversion plus exact rational
subcurve extraction. `CadNurbsLineTypeLowerer` converts every non-empty active
knot span into a rational Bezier span by local homogeneous knot insertion in
`O(P^2)` bounded work for degree `P <= 10`. It measures the resulting WCS 3D
curve with the same 128-bin, eight-point Gauss-Legendre policy used for affine
arcs and refines each distance inversion inside its bin with eight safeguarded
Newton/bisection steps. Measurement determines only dash endpoints and complex
placements. Homogeneous de Casteljau subdivision extracts the actual retained
subcurves, so output geometry is not a line approximation. A dash crossing `K`
source spans is recorded as one rational spline with `K*P+1` control points and
internal multiplicity `P`, preserving one connected stroke and its caps/joins.
Constant geometric spans consume no pattern distance or output piece while
remaining charged to the source/map preflight.

Periodic SPLINE records use one typed cyclic topology rather than a sampled or
synthetic closing edge. Autodesk's managed constructor contract stores `N`
control points and `N+1` fundamental knots for degree `P`; `CadCanonicalSpline`
appends the first `P` controls and extends the first/last `P` knot intervals by
one period, exposing the standard `N+P` control and `N+2P+1` knot relation to
the shared retained evaluator. Dependency snapshots that already carry the
extended knot vector are accepted only when both outer interval sets agree
with that same fundamental period. The resulting domain has exactly `N`
cyclic source spans, matching endpoints without setting the retained command's
synthetic-close bit. A closed but nonperiodic record retains its ordinary NURBS
and appends one straight closing span degree-elevated to `P`, so a dash can
cross the curve/line seam inside one exact rational command. Both loop forms
use the existing deterministic integral-period closed-path planner; Autodesk's
unpublished residual distribution remains a separately documented conformance
gate.

For `B` non-empty knot spans, `Q` visited descriptors, `F` figures, and `E`
emitted Bezier pieces, conversion and measurement are `O(B*P^2 + 128*B*P)`,
pattern traversal is `O(Q)`, extraction is `O(E*P^2)`, and retained/scratch
storage is `O(B*P + 128*B + E*P)`. Figure, placement, descriptor, source-span,
and map limits are checked before proportional output publication. Periodicity
remains a distinct snapshot bit rather than being inferred from closure.
Malformed cyclic extensions and internal knot multiplicity greater than the
degree remain unresolved instead of inventing a connecting stroke across a
discontinuity.

### Retained complex-linetype contract

Complex A-aligned descriptors extend the same scalar endpoint planner instead
of creating a second path walker. The snapshot retains DXF codes 74/46/50/44/45
as typed element kind, relative-or-absolute rotation, scale, and linetype-axis
offsets. Referenced TrueType strings are shaped once into the existing packed
glyph/run/font streams; standard SHX text is laid out once into the existing
glyph-instance stream; SHX shape numbers resolve once to cached analytic paths.
Every occurrence retains only an element index, rebased path point, and tangent.
The scene recorder applies X/Y offsets in effective linetype-scaled axes, then
applies relative tangent rotation or absolute WCS-XY rotation. Text-style fixed
height, width, oblique, and generation flags are retained in the shared text
resource; `S=` supplies the height when style height is zero and otherwise
multiplies the fixed height. Shape `S=` multiplies the interpreted shape path.
Neither X nor Y offset is multiplied by `S=`. Embedded content is emitted only
at complete pattern positions and is never clipped to an endpoint.

Complex descriptors consume the same definition, source-segment, arc-map, and
descriptor-step limits as simple patterns plus a separate default 1,000,000
placement limit. Preflight counts figures and placements before allocating or
publishing either; failed attempts consume the shared document budgets and fall
back transactionally to the ordinary continuous entity command with a typed
diagnostic. For S source segments, Q descriptor visits, F stroke figures, and P
placements, compilation is `O(S + Q + (F + P) log S)` time and `O(F + P)`
retained storage. Definition text shaping is `O(C + G)` once for C code units
and G glyphs, not once per P occurrence. The existing per-payload code-unit
limit and document-wide glyph limit include complex-linetype payloads.

The persisted DXF LTYPE record distinguishes only relative and absolute rotation
in group 74. Autodesk's newer `U=` upright mode is a LIN authoring contract but
has no distinct state in the documented DXF bitfield or the pinned dependency's
public model, so ProGPU does not guess it after serialization. Nonzero complex
descriptor advances and decorated complex text also remain named conformance
gates pending an authoritative persisted-format contract. Missing fonts/shapes,
Unicode or Big Font SHX containers, invalid shape numbers, and unsupported style
contracts remain unresolved resources. A host resolver may explicitly substitute
a font or shape file; the retained resource records that decision and scene
diagnostics report it once per referenced linetype.

This implementation is clean-room. It consumes only ACadSharp's public LTYPE and
STYLE properties as the dependency boundary and does not call, reproduce, or use
its convenience geometry helper as a design source. Exact ProGPU-owned source
provenance is `CadLineTypeLowerer`, `CadSnapshotCompiler`'s existing TEXT/SHX
normalization, `CadShxInterpreter`, `CadShxGlyphCache`, `TextLayout`,
`DrawingContext.DrawPath`, and `DrawingContext.DrawGlyphRunRange`. Managed and
native applicability is shared at the retained-picture boundary: the paired
regression compiles the complex path plus vector glyph runs through
`GpuPictureNativeSceneCompiler`; no shader, C ABI, atlas, or backend-specific
renderer change is required.

### Bounded external LIN library import and reload

`CadLinFile` is a clean-room, browser-neutral ASCII parser for Autodesk's public
LIN authoring grammar. It accepts comment/blank lines, two-line `*name,description`
plus `A,...` definitions, positive dashes, negative gaps, zero dots, quoted text
descriptors, shape descriptors, `S=/R=/A=/U=/X=/Y=` transforms, and `d/r/g`
rotation units. Tokenization is one pass over top-level commas while respecting
quoted strings, doubled quotes, and brackets. The default contract bounds input
to 4 MiB, a physical line to 4,096 bytes, definitions to 4,096, descriptors per
definition to 256, total descriptors to 65,536, and retained text to 1,048,576
characters. Parsing is `O(B + E)` time and `O(B + E)` detached immutable storage
for B bytes and E descriptors; invalid ASCII, duplicate names, incomplete pairs,
unbalanced syntax, invalid numbers/names, and over-limit input fail before a
document lock or table mutation.

`CadImportLineTypesCommand` materializes those detached definitions against one
document as a single generation. `Reject` refuses every name collision;
`ReplaceExisting` swaps description and complete segment sequence in place, so
the registered `LineType` object, handle, layer references, and entity references
remain exact. Protected `ByBlock`, `ByLayer`, and `Continuous` entries and
xref-dependent records cannot be replaced. Text descriptors resolve an existing
STYLE by exact case-insensitive name. Shape descriptors resolve the requested
shape name through `ICadShxShapeResolver` and create at most one deterministic,
document-owned shape STYLE per normalized SHX basename. Missing text styles,
unregistered SHX content, and unresolved shape names fail preflight without a
partial generation. Apply/Undo/Redo are `O(D + E)` and retained command storage
is `O(D + E)` for D definitions and E descriptors.

Autodesk's LIN grammar distinguishes upright `U=` from relative `R=` and
absolute `A=` rotation, but the persisted LTYPE group-74 flags exposed by the
pinned document model distinguish only relative and absolute. The parser retains
upright as a typed value; direct command construction rejects it, while the
shared picker imports all other definitions and reports the exact upright count
left unchanged. It never silently downgrades `U=` to `R=`. Shape imports require
the host's existing registered SHX catalog; browser hosts use bundled bytes and
desktop hosts use the existing drawing/support-directory discovery policy.

The shared desktop/browser shell exposes `Load LIN` (`Reject`) and `Reload LIN`
(`ReplaceExisting`) through one `.lin` picker and typed `ImportLineTypes` seam.
One successful action publishes one history item, generation, immutable snapshot,
selector refresh, and retained picture replacement. Simple and complex text
definitions survive both DXF and DWG round trips. The persistence gate also
found that ACadSharp's DXF writer emitted complex-segment STYLE handle group 340
while its reader discarded that value; the ProGPU-owned feature branch now
captures and resolves it with a direct round-trip regression.

This work uses only Autodesk's public
[simple LIN grammar](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-LT-Customization/files/GUID-EF1DF0A9-2088-487C-8085-16FEE6425405.htm),
[text descriptor grammar](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-LT-Customization/files/GUID-FEDCE7EB-4919-43AE-A54E-F3A293DD60CA.htm),
[shape descriptor grammar](https://help.autodesk.com/view/ACDLT/2026/ENU/?caas=caas%2Fdocumentation%2FACD%2F2014%2FENU%2Ffiles%2FGUID-AF0613E6-5C8B-47F0-800C-8B2524BF2015-htm.html),
[library/load contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Customization/files/GUID-5A6E6759-8A9A-4A8A-9AEE-EE9DB72F792D.htm),
[load/reload selection behavior](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-8B84AA03-B157-4C5D-8B30-AB94C8890B2B-htm.html),
and [linetype manager behavior](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-19A63E71-A551-4767-88FA-A6F54034C9DE.htm).
Adopted the observable grammar, explicit library selection, load/reload conflict,
and persisted STYLE-reference contracts; adapted them into bounded immutable
records, typed failures, and one reversible ProGPU command. Rejected command-line
search paths, runtime text-style invention, partial success after malformed input,
and foreign parser/helper structure. Exact in-repository dependency provenance is
ACadSharp feature commits
[`ff65795e`](https://github.com/wieslawsoltes/ACadSharp/commit/ff65795e624a47b21e420344030c767215ec5af5)
and [`3d074ec4`](https://github.com/wieslawsoltes/ACadSharp/commit/3d074ec4dd94ff7e1e89aaf540c45015ff27a9ff),
which add atomic definition replacement and DXF complex-style restoration;
ACadSharp `master` remains synchronized with upstream.

The mandatory renderer/text applicability audit found no rendering algorithm or
resource-policy change: imported entries become the same immutable
`CadLineTypePattern`/element streams already consumed by managed recording and
native picture compilation. The previously researched Skia/SkParagraph,
DirectWrite/Direct2D and Win2D, WebRender, Vello/Parley, and HarfBuzz startup,
layout reuse, retained-scene, cache, upload, batching, DPI/subpixel, fallback,
variable-font, and device-loss contracts are unchanged. A paired regression
compiles a reloaded referenced definition through both managed and native picture
paths. No C ABI, shader, native document frontend, cache key, atlas generation,
or managed/native boundary crossing was added, so a native C++ source change or
performance claim would be inapplicable.

Higher-degree spline fragments use the existing `DrawingContext.DrawSpline`
contract and are copied into the same owned picture point/knot/weight streams.
Paired weighted single- and multi-span regressions compile those fragments into
native spline stroke batches through `GpuPictureNativeSceneCompiler`. No shader,
C ABI, native spline evaluator, cache, atlas, or device-loss contract changed;
the managed/native applicability finding is therefore shared retained input and
matched compilation coverage, not a one-sided backend algorithm.

Closed/periodic support extends the same original ProGPU-owned
`CadNurbsLineTypeLowerer` and existing `DrawingContext.DrawSpline` path through
the new indexed `CadCanonicalSpline` view. It consumes only ACadSharp's public
degree, flag, control, knot, and weight collections. Compact-period expansion,
cyclic-interval validation, and the degree-elevated closing span are derived
from Autodesk's published constructor/entity contracts and standard rational
B-spline mathematics; no dependency geometry helper or third-party renderer
implementation informed their code structure. Compact/extended ordinary-scene
and linetype regressions both compile through the same native picture boundary.

This slice changes no shader, native C ABI, or backend descriptor. Managed and
native picture compilers consume the identical existing `DrawPath`, analytic
arc, affine-transform, and fixed-stroke contracts. The paired regression creates
an owned CAD picture containing dashed circular and elliptical arcs and compiles
it through `GpuPictureNativeSceneCompiler`; the remaining native applicability
finding is therefore no native implementation change, not an unexplained
one-sided renderer feature.

The exact approved ProGPU-owned implementation provenance for this slice is
`src/ProGPU.Scene/RenderCommand.cs` (`DrawingContext.DrawLine`, `DrawEllipse`,
`DrawPath`, `DrawSpline`, and `DrawGlyphRunRange`),
`src/ProGPU.Text/TextLayout.cs`, and `src/ProGPU.Vector/PathGeometry.cs`
(`PathGeometry`, `PathFigure`, and `ArcSegment`). The new adapter and indexing
algorithms are original ProGPU code based on the public contracts and Autodesk
coordinate specification; no third-party renderer source was used. No shader or
managed/native compositor implementation changed, so the parity audit finds the
native side not applicable to these typed CPU snapshot/recording/physical-page
adapters. Both compositors continue consuming the same pre-existing retained
command, clip, fixed-stroke, and picture-transform contracts. No shader, GPU ABI,
native renderer, atlas, or device-loss behavior changed.
The exact box-selection implementation is likewise original ProGPU code derived
from these retained parametric records, inclusive AABB inequalities, and the
standard convex separating-axis theorem. No third-party selection implementation,
helper layout, lookup data, or source structure was consulted or reproduced. The
new spline-selection implementation shares only the ProGPU-owned homogeneous
knot-insertion and de Casteljau representation already used by exact spline
linetype lowering. Its Bernstein products, stationary-distance polynomial,
six-plane parameter partition, bounded Descartes subdivision, duplicate-root
policy, and typed numerical-failure path are original code derived from the
published mathematical contracts listed in the research record. No Autodesk,
ACadSharp, geometry-kernel, or research implementation text, helper structure,
coefficient table, or control flow was copied. This is a managed immutable-
snapshot query and changes no retained draw command, shader, C ABI, native
renderer, resource identity, upload, or device-loss behavior; the native parity
finding is therefore not applicable, while both renderers continue consuming
the same unchanged spline records. The semantic-handle collector is an original
bounded open-addressed table over the
existing ProGPU candidate records; its folding, probing, ordering, and capacity
contract were designed here rather than taken from a foreign container. The
TEXT selection implementation is also original ProGPU code. Its approved
in-repository provenance is the retained payload and renderer convention in
`CadDocumentSnapshot.cs`, `CadSnapshotCompiler.cs`, `CadPlanSceneCompiler.cs`,
`TtfFont.GetGlyphOutline`, `PathGeometry`,
`ArcSegmentGeometry.TryGetArcCenter`, and the ProGPU-owned Bernstein solver
introduced for exact spline selection. The new code derives fill winding,
affine plane projection, Bezier stationary-distance equations, box-plane
intersection, and SVG endpoint-arc conics from public mathematical/API
contracts; it does not reproduce third-party source, helper organization,
tables, names, or control flow. TrueType outlines remain scaled by
`1 / UnitsPerEm` with the renderer's explicit y inversion, while SHX paths keep
their authored analytic coordinates. Whitespace/no-outline glyphs remain empty
rendered geometry rather than guessed boxes. This CPU immutable-snapshot query
changes no command, shader, C ABI, native renderer, resource generation,
upload, or device-loss contract. The managed/native applicability audit is
therefore not applicable to implementation changes; both renderers still
consume the same unchanged retained glyph runs and SHX paths, and native picture
coverage remains in the existing paired CAD picture tests. The
shared sample interaction is an original composition of the existing ProGPU
`GpuPicture`, `DrawingContext`, pointer-capture, and dynamic-theme contracts. Its
`CadPlanViewport` factors the sample's existing rebase/camera mapping into one
typed double-world/float-screen authority; it does not reproduce a foreign CAD
viewer or selection implementation.

### Document authority

- One `CadDocument` is the semantic authority. Entity handles remain the stable
  identity used by selection, properties, undo, collaboration, and diagnostics.
- Mutations occur through document edit transactions. A successful outermost
  transaction advances one monotonic content generation; a failed or cancelled
  transaction publishes no generation.
- File reads and writes are coarse asynchronous operations. ACadSharp progress
  and non-fatal notifications are translated into typed ProGPU.CAD diagnostics.
- Save never mutates the active document generation. Dirty state compares the
  saved generation with the current content generation.
- A document snapshot is immutable and tagged with its source generation. It
  stores compact, pointer-free typed data needed by rendering and processing;
  it never exposes mutable ACadSharp collections to worker or GPU work.

### Retained rendering

- Document generation, layer/style generation, viewport generation, and device
  generation are independent. Camera-only changes must not traverse or rebuild
  the ACadSharp entity tree.
- The first compiler records analytic paths, splines, hatches, 3D edges, images,
  and shaped glyph runs through existing public ProGPU drawing contracts. The
  second compiler retains GPU resources and submits one scene update per changed
  immutable generation and one render submission per frame.
- Pan, zoom, orbit, and view projection update bounded camera/view uniforms.
  Geometry is not CPU-transformed for each frame. Large world coordinates are
  rebased to a stable local origin in the compiled snapshot while exact double
  coordinates remain in the semantic document and spatial index.
- Visibility is hierarchical: layout/space, frozen or disabled layer, block or
  XRef bounds, entity bounds, then primitive-level work only where needed.
  Selection and snapping share semantic spatial indexes; they do not require a
  duplicate per-frame rendered hit-test scene.
- Device loss invalidates device resources but not semantic snapshots, shaped
  text, or CPU spatial indexes. Atlas movement/repack advances the relevant
  generation and recompiles before moved UVs can be submitted.

### 2D, 3D, and quality rules

- ACadSharp OCS/elevation/extrusion data is normalized with the Autodesk
  arbitrary-axis contract before WCS/view projection. Planar entities are not
  assumed to lie on world Z.
- Curves remain analytic through the existing ProGPU path/spline contracts.
  Tessellation, when a backend requires it, is view-error bounded, cached by
  immutable geometry/style generation, and never substitutes a fixed low-detail
  approximation for deep zoom.
- Model-space lineweight display is cosmetic in device pixels and does not grow
  under zoom. Paper-space and print lineweights use physical units and plot
  scale. Wide polylines remain geometry, not cosmetic lineweight.
- Existing ProGPU winding, DPI, four-phase subpixel snapping, 8x8 high-precision
  coverage, vector glyph cache, color-font, fallback-font, and shaped glyph-index
  contracts remain unchanged.
- TTF/OTF CAD text uses the complete ProGPU Unicode, bidi, fallback, shaping,
  layout, and vector/atlas text stack. SHX is an additional typed font source,
  not a fallback that bypasses shaping or text identity. Its design must cover
  regular, Unicode, Big Font, stacked text, shape references, search paths,
  substitution, missing glyphs, and bounded parsing as those capabilities land.
- 3D content shares the renderer's depth, camera, resource, device-loss, and
  retained submission contracts. ACIS/SAT/SAB acceleration must preserve the
  full boundary representation and may not collapse solids to a wireframe-only
  approximation.

### Managed/native parity and boundary

Every scene or shader feature is audited for both compositors. Shared algorithms
use canonical shader files and matched output tests. Managed/native crossings
are batched by immutable snapshot generation: no per-entity, per-primitive,
per-glyph, or per-table-entry P/Invoke is allowed. Any new wire record begins in
the public C header, is blittable and fixed width, is generated into C#, and is
validated for version, size, alignment, offsets, ranges, and device domain.

## IO and browser policy

- Desktop paths use ACadSharp stream readers/writers through a typed
  `ICadDocumentStore` service. `ICadDocumentPathStore` is the explicitly
  non-browser filesystem adapter over that stream contract. Browser hosts
  supply streams or random-access adapters; engine APIs do not depend on native
  file dialogs.
- Format selection uses both the requested format and validated content. A file
  extension alone is not a security boundary.
- Reads have explicit limits for file size, object count, recursion, decoded
  string size, decompression, proxy graphics, XRefs, image data, and ACIS data.
  Limits and cancellation are part of the public load contract.
- Saves write a new stream/file and replace the destination only after success
  where the platform permits. Warnings are returned to the caller; unsupported
  or lossy output must never be silently reported as a clean save.
- DWG writer support is capability/version gated because upstream stability is
  not uniform across versions. Round-trip conformance is required before a
  version is advertised as production save support.

`CadDocumentPathStore` normalizes one caller-authorized file path but never
creates its parent directory. Reads open one asynchronous sequential file
stream, deny concurrent writers/deletion through the share mode, retain the
absolute source name, and delegate all content detection, byte/entity limits,
diagnostics, and cancellation to `CadDocumentStore`. Auto save format accepts
only a `.dxf` or `.dwg` destination extension; explicit format remains
authoritative, while load Auto mode still validates the content signature and
does not treat an extension as a security boundary.

A path save creates one bounded-name `CreateNew` staging file in the exact
destination directory, forces the stream store's saved-generation commit to
remain deferred, serializes the locked document generation, flushes .NET and OS
file buffers, closes the handle, checks cancellation again, and performs one
same-directory `File.Move(..., overwrite: true)`. Only after that namespace
operation succeeds does it commit the serialized generation and publish the
single deferred `Completed` progress notification. Serialization, flush,
cancellation, invalid destination, and replacement failures therefore leave an
existing destination unchanged, keep the session dirty, and best-effort delete
only the adapter-owned staging path. A newer in-memory edit made after the
serialized capture remains dirty even when that older generation is
successfully committed. Same-directory staging prevents the cross-volume copy
fallback; atomic namespace replacement still depends on the host filesystem,
and this contract does not claim a portable directory-metadata fsync.

The operation is `O(N)` serialization time and `O(N)` temporary filesystem
storage for `N` output bytes with bounded `O(1)` managed adapter state. It adds
no document copy beyond the writer, renderer work, shader, native ABI crossing,
upload, or managed/native replay difference. Focused tests cover DXF and DWG
extension inference/round trips, replacement of an existing destination,
serialization failure, cancellation after writing starts, post-serialization
commit failure, exact saved-generation state, progress ordering, missing-parent
policy, and staging cleanup.

Primary platform contracts consulted on 2026-08-29:

- [.NET `File.Move(String, String, Boolean)`](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move)
  provides the overwrite move used for the final same-directory namespace
  operation; cross-volume move is explicitly rejected architecturally by
  staging in the destination directory.
- [.NET `FileStream.Flush(Boolean)`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush)
  defines the `flushToDisk: true` intermediate-buffer flush performed before
  the staging handle is closed and renamed.
- [.NET `FileStream`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream)
  defines the asynchronous file-handle behavior used by the path adapter.

## Persisted draw order

ACadSharp remains the authority for DXF/DWG `SORTENTSTABLE` persistence. During
snapshot capture, the original ProGPU `CadDrawOrderResolver` materializes each
distinct model-space or referenced block collection once, validates that every
sparse sorter belongs to that exact block and appears only once, and resolves
ascending effective sort handles. Entities without an override retain their
authored handle as the effective key; malformed equal keys retain collection
order as a deterministic recovery tie-break. A foreign, duplicate, or
wrong-owner entry rejects the complete snapshot before publication.

`CadDrawOrderPurpose.Regeneration` is the default and follows modern AutoCAD's
always-sorted REGEN behavior. `Plotting` applies the table only when the drawing's
persisted `SORTENTS` Plotting bit is enabled. The snapshot records its purpose,
whether active overrides were present, and whether its order is plot-compatible;
`CadPrintPlanCompiler` rejects an incompatible display snapshot rather than
silently producing the wrong plot. Callers compile a plotting snapshot when the
document deliberately disables plot sorting.

`CadSetModelSpaceDrawOrderCommand` is the reversible authoring seam for Bring to
Front, Send to Back, Bring Above, and Send Under. It resolves the complete
current back-to-front order before mutation, preserves the relative order of
both the selected and remaining subsequences, and inserts Above after the
frontmost reference or Under before the backmost reference. Selected and
reference handles must be disjoint members of the same model space. The default
bounds retain at most 65,536 selected/reference handles and visit at most
5,000,000 model-space entities.

Each successful edit replaces the persistence object with a canonical complete
permutation using unique compact ascending keys, avoiding sparse-key overflow,
ties, and selection-order reversal. Undo restores the exact prior sparse
entity/key pairs or removes a table created by the command; replacement carries
an internal rollback snapshot, and failed resolution, validation, or limit
checks publish no generation or history entry. The command deliberately
preserves the drawing's existing `SORTENTS` Plotting policy. The shared sample
exposes selection-preserving To front/To back actions plus the complete
Above/Under reference prompt. Starting the latter freezes the edited semantic
handle set, and each exact click, Window, or Crossing gesture accumulates
disjoint reference roots in snapshot-sized caller buffers. Enter commits all
references through one command/history entry; Escape, Clear Selection, or a new
document cancels without changing content generation. The reference overlay and
unsupported/truncated counters remain transient UI state.

One reference gesture costs the existing broad-phase and exact-selection work,
typically O(log F + K + X) and O(F + K + X) worst case for F finite retained
primitives, K candidates, and their bounded exact-geometry cost X. Semantic
deduplication is O(K) average, and refreshing the complete reference overlay is
O(E) for E retained entity headers. Storage is O(E), allocated with the other
snapshot selection buffers and reused across gestures. Accumulation performs no
ACadSharp traversal, scene compilation, persistence mutation, or GPU upload.
Only Enter performs the documented O(E + S + E log E) persisted-order command
and the sample's current full immutable scene rebuild.

Resolution costs O(E + S + E log E) time and O(E) bounded snapshot storage for
E entities and S sparse entries in each distinct visited block. Cached child
orders are reused across every MINSERT cell and repeated INSERT, avoiding both
per-instance sorting and ACadSharp's convenience API's per-entity sparse-table
scan. One INSERT or persisted DIMENSION picture stays a contiguous parent draw
unit while its children follow the definition's own table. Visibility and plot
eligibility filter after ordering, so removing hidden commands cannot reorder
the remaining commands. Steady camera replay still performs no ordering work or
managed allocation.

The managed plan compiler emits commands in resolved snapshot order. Its shared
`GpuPicture` is the sole planar input to both managed replay and the native scene
packer; the native path may combine adjacent strokes into one resource/draw but
retains record order. No new C ABI, native CAD compiler fork, shader, glyph,
atlas, DPI/subpixel, cache-generation, upload, or device-loss contract applies.
DXF/DWG round trips, managed command order, native picture compilation, nested
block contiguity, plot policy, and malformed-table regressions cover the shared
behavior.

## Standalone sample hosts

The shared interactive viewer lives in `ProGPU.CAD.Sample` and is hosted
unchanged by `ProGPU.CAD.Sample.Desktop` and `ProGPU.CAD.Sample.Browser`.
It freezes one generation-tagged scene into an owned `GpuPicture`; wheel zoom
and pointer pan then update only the replay camera matrix. Left click performs a
five-logical-pixel Crossing query, left-to-right drag performs Window selection,
and right-to-left drag performs Crossing selection. The shared shell reuses
snapshot-sized caller buffers, resolves expanded primitives to unique semantic
root handles, reports unsupported/truncated results, and records only transient
fixed-device-space selection rectangles after the immutable picture. Camera and
selection interaction never revisit ACadSharp or recompile geometry. The
draw-order toolbar uses those same exact queries for a plural Above/Under
reference prompt: gestures add disjoint semantic roots without replacing the
edited selection, a separate fixed-device-space overlay shows the accumulated
reference bounds, Enter publishes one reversible edit, and Escape cancels.
Conflicting transform, order, delete, history, and 3D-view actions remain
disabled while the prompt owns pointer selection. The
second shared toolbar row applies a finite positive invariant WCS step along
`-X`, `+X`, `-Y`, or `+Y` to every selected semantic root handle through one
`CadTranslateEntitiesCommand`. A third row rotates the same selection in either
direction by an invariant degree step around WCS +Z and uniformly enlarges or
shrinks it by an invariant factor. Both use the center of the complete retained
bounds for every selected semantic root as their base point, including all
expanded primitives of a selected INSERT. The current plan shell has no UCS
state, so its explicit base-axis contract is WCS +Z; arbitrary base-point, UCS,
reference-angle, and reference-length input remain later editor tools. One
`CadDocumentHistory` belongs to the loaded session, so each Move, Rotate, Scale,
Undo, or Redo publishes exactly one generation and then prepares one complete
replacement snapshot and picture. The prior picture stays drawable until
replacement preparation succeeds and is disposed immediately after the atomic
state swap. Selection buffers are reused when the entity count still fits,
selected handles and complete semantic-root overlays survive transform history, and
`CadPlanViewport.WithRebaseOrigin` compensates pan so a changed snapshot rebase
does not move unchanged WCS content on screen. This first synchronous editor
integration is O(E + G) per committed action for E retained entities and G text
glyphs because the complete immutable snapshot/scene is rebuilt; it makes no
incremental-compilation or edit-latency claim. Generation-keyed reusable chunks,
worker preparation, stale-publication rejection, and equivalent managed/native
measurements remain required before large-drawing edit performance is accepted.

The representative scene exercises
lines, OCS circles/arcs, analytic ellipses, bulge polylines, NURBS, filled
SOLID, 3DFACE visible-edge semantics, a rotated non-uniform MINSERT array, and
retained shaped TrueType TEXT while framework chrome uses dynamic theme
resources.

The desktop host uses the existing ProGPU WinUI/GLFW presentation path. The
browser host uses `BrowserGpuRuntime`, canonical ProGPU browser assets, SIMD,
native Wasm linking, and the same shared viewer assembly. One shared shell now
uses ProGPU's typed `FileOpenPicker`, `FileSavePicker`, and `StorageFile`
contracts to open DXF/DWG bytes through `CadDocumentStore`, rebuild one retained
picture, and save to native paths or browser downloads. Save remains explicitly
labelled uncertified in the UI and opts into development output; an unknown
destination extension is rejected rather than receiving mismatched content.
Staged saves defer their saved-generation commit until the platform storage
write succeeds, so a failed native write or browser download cannot mark the
session clean.

The hosts remain an executable engine-integration baseline, not yet the complete
CAD editor shell. They now share plan, Flat 3D, and retained print-preview modes
with generation-safe layout/named-page-setup selection plus an explicit A4
model-extents fallback and reversible creation/application of named page setups
from Model; dedicated dockable property/layer panels, arbitrary-camera projected
selection, broader editing tools, field-level page-setup editing,
printer/export adapters, and round-trip-certified output remain tracked
application phases.

The Release browser AOT publish succeeds. Its linker audit currently reports
annotation warnings in ACadSharp's initialization/reflection utilities and in
existing UI binding paths. Those warnings remain visible as an AOT-hardening
gate; the typed CAD snapshot, scene compilation, and retained replay paths do
not use reflection.

Exact in-repository host provenance is `src/ProGPU.Samples.Desktop/Program.cs`,
`src/ProGPU.Samples.Browser/Program.cs`, and
`src/ProGPU.Browser/BrowserAssets/`. File workflow provenance is the existing
ProGPU-owned `src/ProGPU.WinUI/Core/Storage.cs`,
`src/ProGPU.WinRT/Windows/Storage/StorageFile.cs`, and
`src/ProGPU.Browser/BrowserStorageServices.cs`. The new programs reuse those
public host/storage contracts and link the canonical browser JavaScript assets;
no third-party host implementation was copied.

The 2026-08-27 desktop Release smoke opened a real `floorplan.dxf` through the
native picker and rendered its retained plan (`967` visible entities, `194`
unsupported entities, and `709` diagnostics) with the shared toolbar/status
shell. The browser Debug build completed its native Wasm link with zero warnings;
an interactive browser picker/download smoke remains open.

## Editing model

- `CadDocumentHistory` is a bounded, generation-synchronized undo/redo owner.
  It executes only repository-defined `CadEditCommand` implementations; the
  abstract mutation methods are internal so consumers cannot inject an
  unvalidated arbitrary command behind the history contract.
- The first commands translate, rotate, uniformly scale, or copy a deduplicated
  stable handle set, set entity visibility or SOLID thickness, assign an existing layer, and add or
  remove one model-space entity, or atomically remove a bounded semantic handle set.
  They resolve every model-space handle before mutation,
  preserve the original visibility vector, apply the inverse transform for
  undo, and advance one document generation for execute, undo, or redo. The
  rotate command accepts a finite non-zero axis/radians plus a finite optional
  pivot and normalizes the axis without overflow. Pivot rotation uses the
  documented translate-to-pivot, axis-angle rotate, and translate-back entity
  operations sequentially, with staged rollback, so it does not depend on an
  undocumented matrix composition order. Uniform scaling uses the public
  origin overload and accepts only a positive, finite, non-unit factor with a
  finite inverse. Non-uniform scaling is not exposed because entity families
  such as circles cannot preserve their authored type under anisotropic scale.
  SOLID transforms explicitly convert its OCS corners through WCS: translation
  projects the displacement back into OCS, rotation updates both corners and
  normal while retaining signed thickness, and uniform scale multiplies signed
  thickness with the geometry. This avoids relying on ACadSharp's corner-only
  generic SOLID transform and keeps execute/undo/redo surface-equivalent.
  Transform commands roll back an already-applied entity prefix if a later
  entity fails. Add requires a detached zero-handle object; remove retains the
  same object for undo and treats a collection cancellation as a failed command
  with no published generation. Multi-remove deduplicates at most 65,536 handles,
  resolves the complete set, and uses ACadSharp's ProGPU-owned preflighted
  `TryRemoveRange` contract. Every cancellable callback runs before structural
  mutation, all items detach before removal notifications publish, and one
  retained object array survives handle reassignment across Undo/Redo. The
  dependency implementation is original ProGPU work at
  `external/ACadSharp/src/ACadSharp/CadObjectCollection.cs` (ACadSharp commit
  `01ca5534`); no third-party implementation text or structure was used.
- Layer assignment resolves the complete entity set and target table entry
  before mutation, retains each prior layer, and validates every retained table
  identity before undo/redo. A missing or externally replaced layer therefore
  fails before partial property changes; setter failure rolls back the already
  applied prefix.
- Layer visibility is a separate multi-layer command over the authored `IsOn`
  state. It resolves every case-insensitively deduplicated table name before
  mutation, retains each prior state, and restores the snapshot's existing
  visibility filtering on undo. A distinct multi-layer command owns plot
  eligibility (`PlotFlag`) and retains the snapshot state consumed by later
  print planning without changing screen visibility.
- The shared desktop/browser shell now exposes those two persisted states for
  one explicit layer-table selection. Its detached, generation-refreshed layer
  catalog remains bounded by the same 65,536-entry/1,048,576-character contract
  as entity layer assignment and preserves the selected name across edits.
  `On`/`Off` rebuilds the screen snapshot and therefore both managed and native
  replay; `Plot`/`No plot` leaves the screen snapshot visible while the existing
  plotting-purpose snapshot excludes the layer. Each Set action is one existing
  transactional command, generation, retained-scene rebuild, and Undo/Redo item.
  Missing layer names fail during detached capture or complete command preflight.
  DXF and DWG regressions retain visibility, plot eligibility, freeze, and lock
  states.
- Autodesk's public
  [LAYER DXF record](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-D94802B0-8BE8-4AC9-8054-17197688AFDB.htm)
  defines a negative group-62 color as Off and group 290 zero as Do Not Plot;
  the public
  [Layer Properties Manager contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-B297EBD9-D68C-47E1-87CE-1B3798496599.htm)
  further specifies that an Off layer is invisible and never plotted even when
  its Plot flag is on. ProGPU adopts that distinction and rejects conflating
  `PlotFlag` with screen visibility.
- The same detached shared row now exposes persisted `Freeze`/`Thaw` and
  `Lock`/`Unlock` using ACadSharp's existing `LayerFlags.Frozen` and
  `LayerFlags.Locked` bits. The exact approved in-repository source provenance
  is `external/ACadSharp/src/ACadSharp/Tables/LayerFlags.cs`, with its existing
  DXF/DWG readers and writers; ProGPU does not duplicate the wire values.
  Commands change only the requested bit and preserve `FrozenNewViewports`,
  XRef, and referenced-state bits. Complete layer lookup precedes mutation,
  and Undo/Redo retains exact prior flags.
- Freeze is a semantic snapshot-compilation filter, not an opacity or late GPU
  discard. Top-level entities, inherited layer-zero block content, explicit
  nested layers, and per-face polyface layers are rejected before bounds,
  style, geometry, text shaping, spatial-index, plan/3D/native-picture, upload,
  or print streams are built. A frozen primary INSERT therefore hides its
  complete expansion, and frozen geometry does not affect fit/zoom extents.
  Snapshot traversal remains `O(E)` for E visited entities and can only reduce
  downstream work and storage.
- Lock leaves screen and plot replay unchanged. Initial resolution of every
  handle-targeted entity mutation rejects a locked source layer after the
  complete target set is resolved and before mutation, so mixed locked/unlocked
  batches publish neither partial state nor history. Ownership-only retained
  validation remains separate so Undo/Redo can reverse an edit that placed an
  entity onto a locked layer. Adding a new entity to a locked layer and using a
  locked entity as a read-only Above/Under reference remain allowed; layer-table
  property commands also remain available. The shared shell retains locked
  selections for inspection but disables entity transforms, style edits, draw
  order, copy, and delete until the layer is unlocked.
- Autodesk's public
  [Freeze/Thaw contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-09F5021E-1348-424E-866D-A0B07681286B.htm)
  states that frozen objects are excluded from display, regeneration, and Zoom
  Extents. Its
  [layer-state definition](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Architecture/files/GUID-40795C0B-53A1-4696-88E0-5D3A078D6280.htm)
  requires a frozen primary object's subcomponents to be invisible. The public
  [locked-layer contract](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-Core/files/GUID-415798D2-818A-42CE-A052-B1C80B650575.htm)
  forbids object modification while retaining inquiry, object-snap/reference,
  addition, display, and normal plot behavior. ProGPU adopts those semantics.
  Configurable `LAYLOCKFADECTL` screen fading is intentionally deferred rather
  than hard-coding an approximation that could leak into print output.
- The cross-engine applicability gate compared retained boundaries in
  [SkPicture](https://api.skia.org/classSkPicture.html),
  [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist),
  [Win2D CanvasCommandList](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm),
  [WebRender's Scene-to-Frame culling](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
  and [Vello Scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs).
  ProGPU adopts early omission before its reusable retained recording rather
  than recording transparent commands or creating per-layer GPU resources.
  [DirectWrite text layout](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextlayout),
  [Skia shaped text](https://skia.org/docs/dev/design/text_shaper/),
  [Parley](https://docs.rs/parley/latest/parley/), and
  [HarfBuzz shaping](https://github.com/harfbuzz/harfbuzz/blob/main/src/hb-shape.cc)
  confirm that reusable shaping/layout results are independent of the drawing
  model; this slice changes none of their inputs, caches, fallback, bidi,
  variable-font, DPI, or subpixel contracts because frozen entities are rejected
  before ProGPU invokes text lowering. There is no startup/lazy initialization,
  worker scheduling, atlas eviction, demand-upload, viewport culling, cache-key,
  device-loss, shader, C++ path, or native ABI change. Both renderers consume
  the same filtered immutable streams.
- A reproducible Release feature-cost lane compares the same 10,000-entity
  binary with all entities thawed and with alternating entities assigned to one
  frozen layer:

  ```text
  dotnet run --project src/ProGPU.CAD.Benchmarks -c Release --no-build -- --entities 10000 --warmup 3 --iterations 12 --queries 1000 --output-json cad-layer-thawed.json
  dotnet run --project src/ProGPU.CAD.Benchmarks -c Release --no-build -- --entities 10000 --alternating-frozen-layers --warmup 3 --iterations 12 --queries 1000 --output-json cad-layer-frozen.json
  ```

  On the 2026-08-29 Apple Silicon/.NET 10 run, the thawed/frozen fixtures
  produced 10,000/5,000 visible and expanded entities, 10,000/5,000 retained
  commands, and 4,095/2,047 spatial nodes. Snapshot p50/p95/p99 was
  30.683/45.219/45.219 ms with 10,895,475 managed bytes per operation versus
  22.227/60.006/60.006 ms with 5,040,061 bytes. Plan-scene recording was
  12.303/26.993/26.993 ms with 9,280,690 bytes versus
  2.333/12.740/12.740 ms with 5,160,688 bytes. This short run validates exact
  early omission and establishes a reproducible feature-cost baseline; its
  process-level latency tails are noisy, so it makes no performance-improvement
  claim and does not substitute for Instruments evidence when optimization is
  claimed.
- Layer color assignment accepts indexed and true explicit colors, rejecting
  `ByLayer`, `ByBlock`, and the header-only `ByEntity` sentinel. It restores each
  prior table value and updates inherited entity RGB through the same immutable
  snapshot style resolution used by both picture compilers.
- Layer lineweight assignment accepts declared explicit widths, hairline, and
  `Default`, while rejecting entity-only `ByLayer`/`ByBlock` and undefined wire
  values, including ACadSharp's internal `ByDIPs` sentinel. Inherited entities
  resolve the new physical/cosmetic thickness in the immutable snapshot; undo
  restores each authored layer enum.
- Layer linetype assignment resolves an existing explicit table entry before
  mutation and rejects entity-only `ByLayer`/`ByBlock` targets. Inherited entity
  styles retain the resolved name in the snapshot, while non-continuous pattern
  rendering remains behind the documented A-alignment fidelity gate.
- The shared desktop/browser shell exposes those existing commands through one
  additional detached layer-style row tied to the same generation-refreshed
  layer selection. Color accepts ACI 1–255 or `#RRGGBB`; layer inheritance and
  header sentinels are never offered as layer values. Lineweight offers only
  `Default`, hairline, and the declared fixed CAD widths. Linetype lists only
  drawing-resident entries and removes `ByLayer`/`ByBlock`. The separate bounded
  `Load LIN`/`Reload LIN` workflow now adds or identity-preservingly replaces
  those table entries. Each Set action publishes one history item,
  generation, snapshot, and shared managed/native picture replacement while
  preserving the selected layer. Locked layers remain editable through the
  layer table because lock constrains member-entity mutation, not layer
  administration. DXF and DWG round trips retain all three authored values.
- Autodesk's public
  [Layer Properties Manager](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-B297EBD9-D68C-47E1-87CE-1B3798496599.htm)
  exposes Color, Linetype, and Lineweight as layer properties, and its
  [LAYER contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT/files/GUID-9123091A-2DCB-4DE8-983C-F7CA38FA67BE.htm)
  specifies that `ByLayer` objects inherit those authored layer values while an
  explicit object value overrides them. The
  [layer lineweight dialog](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-E30B3681-544D-4DA5-856D-B33497D005C0.htm)
  supplies fixed widths plus `Default`. ProGPU adopts those persisted semantics;
  Color Book selection, viewport overrides, and plot-style
  editing remain separately typed future contracts.
- The rendering applicability gate rechecked
  [SkPaint](https://api.skia.org/classSkPaint.html),
  [Direct2D DrawGeometry](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawgeometry),
  [Win2D DrawGeometry](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawGeometry_5.htm),
  [WebRender's Scene-to-Frame boundary](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
  and [Vello scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs).
  Their geometry APIs consume resolved brush/stroke state at retained recording,
  matching ProGPU's existing immutable `CadStrokeStyle` table; no per-layer GPU
  object or late property lookup is introduced. Skia/SkParagraph, DirectWrite,
  Parley, and HarfBuzz were also rechecked for the required text applicability
  gate: layer style changes neither shaped text, fallback, bidi, variable-font,
  DPI/subpixel, glyph-cache, nor atlas identity. This host reachability slice
  changes no startup, worker, upload, eviction, device-loss, shader, C++ path,
  native ABI, or renderer algorithm; both renderers consume the same rebuilt
  immutable picture.
- Layer-table lifecycle is a typed reversible contract. Creation copies the
  selected layer's currently supported editable state (On/Off, Plot, Freeze,
  Freeze-in-new-viewports, Lock, explicit Color, fixed/default Lineweight, and
  drawing-resident Linetype) into a detached zero-handle `Layer`, then transfers
  that entry to the document table. Rename mutates the retained entry name, so
  entity, current-layer, and viewport references keep the same object identity.
  Removal first rejects layer 0, Defpoints, the current layer, xref-dependent
  layers, every registered entity reference across model space, paper space,
  block definitions and owned child entities, and every viewport frozen-layer
  reference. Only then does it detach the table entry; Undo restores the same
  object with a newly assigned handle. Invalid or duplicate names and failed
  removal preflights leave both the table and content generation unchanged.
- Populated-layer merge is a separate typed command rather than an unsafe delete
  option. It resolves a bounded set of at most 65,536 retained sources and one
  target before mutation, rejects layer 0, Defpoints, current, and xref-dependent
  sources plus xref-dependent targets, then captures every registered
  `Entity.Layer` reference across model space, paper space, block definitions,
  and owned children together with its exact original source identity. It also
  snapshots every affected viewport's complete frozen-layer sequence. Apply
  reassigns all captured entities to the target, removes every occurrence of any
  source from those viewport sequences, and only then performs one atomic source
  table range removal. The target identity and all target properties remain
  unchanged. Undo re-adds the same source objects with new handles, restores each
  entity to its exact original source, and restores every viewport sequence,
  including ordering and duplicate occurrences; Redo validates and repeats the
  retained transition. Capture is capped at 1,000,000 entity plus affected-
  viewport snapshot references. Complete work is O(S + R + F + A + Fv) and
  retained command storage is O(S + A + Fv) for S sources, R registered entities,
  F inspected frozen references, A affected entities, and Fv frozen entries in
  affected viewports. ACadSharp validates the table set before mutation and emits
  one immutable typed range notification; document unregistration plus exact-
  identity entity repair is O(S + R), not O(SR). The existing whole-generation
  O(E + G) snapshot/picture replacement follows one successful edit. Failed
  preflight, Apply, Undo, or Redo validation publishes no partial document
  generation.
- The shared desktop/browser shell exposes `New layer`, `Rename layer`,
  `Delete unused`, and a dedicated merge row with `Queue selected`, a source
  count, `Clear sources`, one explicit target, and `Merge queued`. The source
  queue is stamped with both document-session identity and content generation;
  load, another edit, Undo, or Redo clears it before stale names can be committed.
  Duplicate queue entries are suppressed case-insensitively, protected/current/
  xref-dependent sources cannot be queued, and a queued source cannot also be the
  target. A
  created layer uses the selected layer as its template and becomes the selected
  table row; a renamed layer remains
  selected under its new name; a removed selection falls back to layer 0. Name
  validation uses the loaded document version, the 31/255-character persistence
  limits, and the shared forbidden-character set. The Delete button performs no
  registry scan while typing or selecting; delete and merge commands perform the
  definitive O(S + R + F) reference scan only on click. A successful merge selects
  the retained target, preserves semantic entity handles and current selection,
  and replaces the shared managed/native picture once. Create and rename table
  work is average O(1), followed by the existing whole-generation O(E + G)
  immutable snapshot/picture replacement.
- Autodesk's
  [Layer Properties Manager](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-B297EBD9-D68C-47E1-87CE-1B3798496599.htm)
  defines new-layer inheritance and create/rename/delete reachability. Its
  [layer workflow](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-Core/files/GUID-28E52FA3-248E-4F65-94DD-7C47BE761D58.htm)
  defines the persisted name limits and forbids deleting layer 0, Defpoints,
  current, object/block-used, and xref layers. Autodesk's
  [unreferenced-definition guidance](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-8411645E-618C-4081-8FE4-5E09B52BAEDF.htm)
  additionally identifies paper-space, nested-block, current, permanent, and
  xref references. Autodesk's
  [LAYMRG contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-7D66940F-EC67-4DEC-89BC-82B887EABD6E.htm)
  specifies Ctrl+click selection of multiple source layers, movement of their
  objects to one target, and purge of the originals. The matching
  [dialog workflow](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-D7F24669-483F-4589-96AC-557F08784E7E.htm)
  likewise separates multiple selected sources from one target, while the
  [Merge to Layer contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-B7125CCB-A0DA-439F-BC43-A2F50CFFFAB7.htm)
  specifies that the target's properties are retained. ProGPU adopts those
  observable multi-source semantics and explicitly removes source viewport-
  freeze references instead of changing the target's independent viewport state.
  The shared host adapts Ctrl+click to an explicit queue because its portable
  ComboBox contract is single-select; a modal picker and repeated single-source
  transactions were rejected because they either diverge across hosts or expose
  partial generations. No Autodesk or other engine implementation source was
  copied. Complete typed registry enumeration is ProGPU-owned ACadSharp commit
  `b83de724`; atomic range removal and its single O(S + R) immutable notification
  are ProGPU-owned commits `5797dee5`, `48678eb8`, and `03c2d648`. The dependency
  master branch remains untouched.
- This lifecycle and merge slice changes the object table and shared host, then
  invokes the existing generation rebuild. It changes no scene compiler, shader, cache,
  GPU resource, native ABI, or managed/native renderer algorithm. Both renderers
  consume the same post-edit immutable picture, and native picture regressions
  cover both used-layer rename and populated-layer merge. Text shaping, fallback,
  bidi, variable-font,
  DPI/subpixel, glyph-cache, atlas, startup, worker, upload, eviction, and
  device-loss contracts are unchanged, so no paired renderer/text source change
  applies.
- Entity duplication uses ACadSharp's public detached-clone contract. The
  single-entity command applies an optional finite translation before ownership
  transfer and retains its cloned object across Undo/Redo handle reassignment.
  The selection-set command deduplicates and bounds at most 65,536 source
  handles, resolves the complete source set before cloning, applies one optional
  finite WCS displacement to every detached clone, and publishes one structurally
  complete `AddRange` batch. Invalid input therefore leaves model space unchanged,
  while Undo uses the preflighted batch-removal contract and Redo restores the
  same independent clone graphs instead of recloning potentially changed sources.
  ACadSharp's matching ProGPU-owned batch-add implementation and regressions are
  recorded at submodule commit `d17db6f8`; the ProGPU command, snapshot, UI,
  Undo/Redo, DXF, and DWG tests are matched. Work and retained storage are O(N)
  for N unique sources, and a successful Apply/Undo/Redo publishes one generation.
- Linetype assignment uses the same table-identity and rollback contract for
  explicit, `ByLayer`, and `ByBlock` entries. The immutable snapshot retains the
  newly resolved linetype name. Entity linetype-scale assignment accepts only
  positive finite values and restores each prior authored scale. These edits do
  not imply dash rendering: non-continuous patterns continue through the
  explicit unsupported diagnostic until the documented A-aligned endpoint
  planner is implemented.
- Lineweight assignment accepts only declared `LineWeightType` values, retaining
  explicit widths plus `ByLayer`, `ByBlock`, `Default`, and hairline semantics
  without resolving them prematurely. It rejects ACadSharp's internal `ByDIPs`
  sentinel because that value is absent from Autodesk's standard object
  lineweight contract. Undo restores each authored enum value; the next
  immutable snapshot resolves it through the existing layer/block style
  contract, so both picture compilers receive the same physical or cosmetic
  stroke policy.
- Color assignment retains indexed, true-color, `ByLayer`, and `ByBlock` values
  as authored and rejects the header-only `ByEntity` sentinel before mutation.
  Undo restores each semantic value rather than baking resolved RGB, while the
  snapshot regression verifies that an explicit true color reaches the shared
  retained stroke style unchanged.
- Transparency assignment likewise retains explicit 0–90, `ByLayer`, and
  `ByBlock` values instead of baking alpha into the mutable document. The shared
  bounded property transaction captures a rollback vector, validates retained
  entity/table identity, and reverses an already-applied setter prefix on
  failure. Snapshot coverage verifies the existing integer-to-alpha rendering
  contract after the edit.
- Persisted entity visibility uses the common ACadSharp `IsInvisible` flag and
  retains the complete prior Boolean vector. An invisible edit is consumed by
  the existing immutable-snapshot filter before both managed and native replay.
  The shared canvas now preserves selected semantic handles when every selected
  object becomes invisible, even though the rebuilt snapshot has no selectable
  records, so Show and Undo remain available without inventing a hidden-object
  render proxy.
- Signed thickness editing is deliberately typed to `SOLID`. The bounded
  `CadSetSolidThicknessCommand` accepts at most 65,536 distinct model-space
  handles, resolves and verifies the complete set as `Solid` before mutation,
  rejects non-finite targets, and retains each exact prior signed value. Apply,
  Undo, and Redo are O(S) time and O(S) retained storage for S selected SOLIDs,
  publish one generation, and feed the already-shared exact closed-shell
  snapshot, managed Mesh3D, and native Mesh3D paths. A mixed entity-family set
  fails before any thickness changes.
- The shared desktop/browser shell exposes common Color, Lineweight, Layer,
  Linetype, Linetype Scale, Transparency, persisted Visibility, and all-SOLID
  signed Thickness values for the complete
  semantic-root selection. A synchronous session read resolves each selected
  handle to an exact model-space entity, returns `null` for a mixed common
  value, and allocates no per-call collection. Capture is O(S) time and O(1)
  auxiliary storage for S selected handles. Layer and linetype choices are
  detached, generation-tagged, case-insensitively sorted snapshots of the
  definitions already loaded in the document. Each catalog family is bounded
  to 65,536 entries and 1,048,576 UTF-16 characters; capture is
  O(L log L + T log T) time and O(L + T + C) owned storage for L layers, T
  linetypes, and C characters, and the shell rebuilds it only when the document
  identity or generation changes.
- The UI displays `*VARIES*`, disables the controls for an empty selection,
  retains unsupported `ByDIPs` as a diagnostic-only display value, and uses
  only dynamic theme brushes. Color input accepts `ByLayer`, `ByBlock`, ACI
  1–255, or exact `#RRGGBB`; lineweight offers `ByLayer`, `ByBlock`, `Default`,
  and every Autodesk standard physical width. Layer and linetype choices are
  limited to loaded document definitions. Linetype scale accepts only a finite
  positive invariant value. Transparency accepts `ByLayer`, `ByBlock`, or an
  integer from 0 through 90. Visibility offers persisted Visible/Hidden states.
  SOLID thickness accepts any finite invariant signed value, including zero;
  the field displays `N/A` and remains disabled unless every selected semantic
  root is a SOLID. One Set action applies one typed command to all
  selected roots, publishes one generation, preserves selection, recompiles
  the retained picture transactionally, and enters the existing Undo/Redo
  history. Mixed/common UI state, catalog replacement, rendered color,
  lineweight, linetype scale, alpha, visibility filtering, signed shell
  extrusion, selection retention across a fully hidden rebuild, managed/native
  scene acceptance, Undo/Redo, and DXF/DWG persistence have matched regressions.
- The clean-room behavior sources for these two additions are Autodesk's public
  [common DXF entity group codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-3610039E-27D1-4E23-B6D3-7E60B22BB5BD.htm),
  where group 60 is the persisted entity visibility flag, and the
  [managed SOLID thickness contract](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid_Thickness.html),
  where group 39 is a read/write double along the entity normal. ProGPU adopts
  those authored properties and the existing ACadSharp persistence model. It
  rejects treating temporary `HIDEOBJECTS` isolation as persisted visibility,
  exposing thickness for entity families whose snapshot lowering still reports
  nonzero thickness as unsupported, or presenting Material/Plot Style controls
  before a ProGPU renderer/plot-policy consumer exists. This slice changes no
  shaders, native ABI, C++ topology, cache key, upload policy, text system, or
  managed/native rendering algorithm: both implementations consume the same
  rebuilt immutable snapshot and existing SOLID shell stream.
- The shared shell also exposes `Copy −X`, `Copy +X`, `Copy −Y`, and `Copy +Y`
  against the same finite positive invariant WCS step used by Move. One action
  copies the complete semantic-root selection through one bounded command and
  one retained-scene rebuild. Source handles remain selected after Apply,
  Undo, and Redo so repeated displacement copies remain explicit and the shell
  never retains handles that ACadSharp clears while detached duplicates are in
  history. This is a typed desktop/browser editing surface only; it does not
  add a second renderer, shader, native ABI, or document-object implementation.
- The clean-room behavior source for this workflow is Autodesk's public
  [Properties palette](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DidYouKnow/files/GUID-94C065AB-FF9E-4752-B778-23D2FBB87E18.htm),
  [object-property tools](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-81585857-F1B1-44F4-B7D0-B707386CA721.htm),
  [color contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-14BC039D-238D-4D9E-921B-F4015F96CB54.htm),
  [CHPROP contract](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-05074362-FC4B-4582-A7A4-B3F6170BB4A7-htm.html),
  [general-property contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-99AFB412-CEE2-4C12-B7AD-E11E9389BBF9.htm),
  [linetype and scale contract](https://help.autodesk.com/view/ACD/2026/ENU/?guid=GUID-20B4D4B3-1220-426A-847B-5BBE36EC6FDF),
  and [standard lineweight list](https://help.autodesk.com/cloudhelp/2024/CHS/AutoCAD-ActiveX-Reference/files/GUID-DE1AC635-BCEA-428F-A47D-80E35EFE55D3.htm).
  ProGPU adopts common-property application to every selected object and the
  visible mixed-value state, and lists only drawing-resident layer and linetype
  definitions. It adapts the palette to bounded explicit Set actions and
  disables empty-selection defaults. Automatic `.lin` loading is rejected at
  this seam because loading external definitions requires a separate typed,
  bounded file workflow; color books are rejected because the pinned document
  contract does not expose them. No foreign source text or implementation
  structure was used. This is an application surface over the existing
  property commands and immutable style compiler, not a renderer, shader,
  native ABI, or resource-lifetime change; the existing shared retained scene
  remains the managed/native parity boundary.
- A direct session edit from another owner invalidates both history branches.
  The expected generation is checked again under the document lock so a race
  cannot apply an undo to the wrong document state. Failed resolution or command
  execution does not publish a generation or enter history.
- Commands are typed and begin from handles or an explicitly transferred
  detached entity. ACadSharp assigns a new handle when a removed object is added
  back, so applied commands retain and revalidate object identity after their
  first handle resolution. This lets an earlier translate/visibility command
  undo correctly across delete/restore without guessing or mutating ACadSharp's
  handle allocator. Each command records only enough prior semantic state for
  deterministic undo/redo, never a duplicate full document.
- Nested edits publish one atomic generation. Selection, hover, camera, and
  temporary tool overlays are view state and do not dirty the document.
- `CadSelectionQuery` is the first selection seam over immutable snapshots. It
  maps BVH intersections into caller-owned, generation-tagged primitive/handle
  records with no managed allocation. AABB overlap is explicitly broad phase:
  expanded block primitives remain separate even when they share one root
  handle. `CadSelectionHitTester` then performs exact allocation-free world-space
  proximity tests for lines, conformal circles/arcs, lightweight and legacy 2D
  polylines (including signed circular bulges under conformal transforms), 3D
  polylines, positive-weight rational splines, filled TrueType TEXT, and stroked
  SHX TEXT. TrueType selection consumes the retained shaped glyph IDs,
  positions, per-run fonts, cached line/quadratic/cubic outlines, and filled
  decoration rectangles. It preserves holes and the outline fill rule rather
  than selecting advance/ascent boxes. SHX selection consumes the same cached
  analytic glyph paths and decoration segments as recording; elliptical arc
  segments are split into at most four exact positive-weight rational conics,
  never flattened. Affine text bases are tested directly in WCS, including
  non-uniform block transforms and text-plane distance. Spline picking isolates the
  exact rational Bezier spans of the canonical open, closed, or periodic NURBS,
  forms the degree-`3P-1` squared-distance stationary polynomial in homogeneous
  Bernstein form, and evaluates every isolated real root plus both endpoints.
  Filled SOLID and 3DFACE proximity uses the retained triangle union;
  3DFACE invisible-edge flags affect only wireframe presentation, not the
  selectable or shaded surface. It validates candidate generation and
  immutable header identity before indexing primitive buffers. Inclusive
  world-space box tests add distinct Window (whole selectable geometry) and
  Crossing (any intersection) semantics. Window containment consumes the exact
  retained extrema; crossing uses segment slabs, bounded analytic parameter
  partitioning at box-plane roots for affine circles/arcs/ellipses and bulges,
  exact rational spline partitioning at every real root against all six box
  planes, and the complete convex triangle/box separating-axis set for filled
  SOLIDs and 3DFACEs. These tests remain O(S) for S polyline segments,
  O(B * P^2 * R) for B spline spans and
  degree P, and O(G * T * R) for G retained glyphs with T analytic outline
  segments and bounded root-isolation work R; analytic non-spline primitives
  remain O(1). Filled Crossing additionally tests the exact intersection polygon
  of the text plane and selection box, covering a box slice wholly inside a
  glyph without accepting a hole. All paths use bounded stack storage and allocate no managed
  memory after snapshot construction. The root solver caps degree at 29,
  recursion at 52 bisections, and work at 16,384 nodes per polynomial; clustered
  roots that double precision cannot resolve return `UnsupportedGeometry`
  instead of an AABB or viewport-tessellation answer. Anisotropically
  transformed circular/bulge geometry and ellipses still return typed
  `UnsupportedGeometry` point results; affine curve, spline, and text
  box tests remain world-space exact within the shared relative coordinate
  tolerance. A
  caller-buffered open-addressed collector then collapses primitive hits to one
  semantic root handle while preserving first-candidate order. It rejects mixed
  snapshot generations before touching scratch/output, keeps its table at no
  more than 50% load, reports destination truncation without changing the total,
  and is O(K) average/O(K^2) collision worst case with O(K) caller-owned storage
  for K candidates. `QueryExactBounds` composes broad phase, exact testing, and
  semantic collection without allocating after the caller buffers are prepared;
  candidate and destination truncation remain distinct. `CadPlanViewport`
  applies the same WCS rebase, Y inversion, zoom, pan, and viewport center to
  rendering and inverse pointer mapping, and creates an inclusive WCS-XY
  selection column spanning the snapshot's complete Z range. Text selection,
  arbitrary-camera projected selection volumes, and
  AutoCAD's viewport-visible dashed-linetype Window exception remain explicit
  work; the current Window contract intentionally evaluates complete retained
  source geometry.
- The shared sample's actionable transforms consume selected semantic root
  handles in the existing multi-entity translate, pivoted axis-angle rotate, or
  pivoted uniform-scale commands. Selection-bound refresh visits all retained
  headers after exact semantic deduplication, so an INSERT selected through one
  expanded primitive still rotates/scales about the center of its complete root
  bounds. The plan shell uses WCS +Z and the complete selection center until a
  typed UCS/base-point input contract lands. It never mutates the frozen picture
  or command stream in place. Successful Move/Rotate/Scale/Undo/Redo first
  advances the session/history generation, then compiles and installs one
  matching immutable snapshot/picture; if preparation fails, the prior owned
  picture remains installed and the error is surfaced. Rebase compensation is
  O(1); selection-bound refresh is O(E) average through retained handle identity;
  complete snapshot/scene replacement remains O(E + G) and is an explicit
  foundation limitation rather than an incremental-rendering claim.
  Multi-selection Delete uses the same selected semantic root handles and the
  bounded atomic range command. The desktop/browser shared shell exposes both a
  Delete button and the Delete key, clears the deleted selection because restored
  ACadSharp objects receive new handles, and rebuilds exactly once for Delete,
  Undo, or Redo. A missing handle or any cancelled item leaves every selected
  object attached with its original handle and publishes no generation. Work is
  O(N + E + G) for N deleted roots and the current whole-snapshot O(E + G)
  replacement; no per-entity retained-scene mutation occurs.
- Background compilation captures a generation and publishes only if it still
  matches; obsolete work is discarded. The UI may continue drawing the previous
  immutable snapshot while the next generation compiles.
- Collaboration and scripting consume the same command contracts. Neither is
  allowed to mutate ACadSharp collections behind the transaction boundary.

## Static BLOCK/INSERT lowering

Static INSERT entities expand into the immutable primitive streams once per
document generation. The original top-level INSERT handle is retained on every
expanded header so hit testing and editing continue to address the block
reference as one CAD object. Nested transforms use an original double-precision
affine value with allocation-free point/vector application and composition. For
a block-space point `p`, block base point `b`, insertion position `P`, OCS basis
`O`, rotation `R`, and nonzero scale `S`, the mapping is
`P + O * R * S * (p - b)`. ACadSharp's public object model supplies `P` as the
WCS-equivalent position; the raw DXF group is OCS data.

Child geometry remains analytic under affine transforms: transformed circles
and arcs carry affine axes into the existing ellipse/path command transforms,
ellipses retain transformed major/minor axes, splines transform control points,
and bulge polylines transform their planar basis without fixed sampling. Exact
axis-extrema bounds are recomputed after transformation. Layer `0` inherits the
effective INSERT layer, while ByBlock color, lineweight, linetype, and
transparency inherit the resolved parent INSERT style. All descendants retain
their own nonzero layers and explicit properties.

MINSERT uses the same lowering. For zero-based column `c` and row `r`, column
spacing `dc`, and row spacing `dr`, its cell origin is
`P + O * R * (c * dc, r * dr, 0)`. Thus rotation applies to both each block and
the whole array, while the independently specified spacing is not multiplied by
the per-block scale. Cells are emitted row-major and every primitive retains the
single top-level INSERT handle. The affine axes are composed once per array;
each cell changes only the translation, so lowering remains allocation-free per
instance apart from the immutable primitive output itself.

Expansion is bounded by configurable depth, array-instance, and entity-count
limits and detects cycles along the active block path. The array limit is
checked before any cell is emitted, including for an empty block whose instances
would not consume the entity budget. Exceeding the global entity budget fails
the snapshot explicitly at the bound instead of returning a partially rendered
drawing; depth and cycle failures diagnose only the affected INSERT. Expansion
is `O(I + E)` before the `O(E log E)` BVH build for `I` block instances and `E`
expanded visible entities; camera replay remains independent of both source and
expanded entity counts. Dynamic evaluation graphs and XRefs are explicitly
diagnosed rather than rendered approximately. Shared block-
fragment/GPU instance reuse remains a later optimization after inherited-style
variants and instance hit identity are fully specified.

### Block attribute lowering

ATTDEF and ATTRIB use the existing retained TEXT/MTEXT pipelines rather than a
second attribute renderer. During INSERT expansion, a nonconstant ATTDEF is a
template and emits no pixels; its matching ATTRIB owns the per-reference value.
A constant ATTDEF remains definition-owned and is emitted once for every INSERT
or MINSERT cell. A malformed constant ATTRIB is ignored so it cannot duplicate
the definition-owned value. Standalone model-space ATTDEF entities remain
ordinary visible default text. Invisible attribute flags suppress both display
and print while leaving the mutable document data intact; Verify and Preset do
not change rendering.

AutoCAD attribute-reference geometry is already transformed from block space
when the reference is created. ProGPU therefore applies only the containing
ancestor transform to ATTRIB coordinates. An MINSERT cell adds its rotated,
unscaled row/column offset, but never reapplies the referenced INSERT's scale,
rotation, base-point shift, or normal. Nested INSERTs consequently transform
the baked inner reference exactly once. Layer `0`, ByBlock style, root-handle
identity, plottable-layer filtering, expansion limits, diagnostics, spatial
selection, print replay, and native-picture compilation remain the same as for
other expanded text descendants.

Single-line values lower through `CompileText`. Multiline and constant-
multiline values require the typed embedded `MText` payload and lower through
the complete TrueType or standard-SHX `CompileMText` path, retaining columns,
stacks, masks, decorations, glyph runs, paths, bounds, and exact selection.
`CadSetAttributeValueCommand` resolves one model-space INSERT plus a
case-insensitive tag and explicit duplicate-tag occurrence, updates the
single-line and embedded-MTEXT values together, and retains exact prior values
for generation-synchronized Undo/Redo. Both tag and value are bounded owned
strings, with the value capped by the same 65,536-code-unit per-entity snapshot
contract. Constant values remain definition-owned and are rejected by that
reference edit command.

`CadAttributeValueCatalogCompiler` copies the values reachable from one
selected model-space INSERT into a bounded generation-tagged catalog. It emits
reference-owned variable ATTRIB values, definition-owned constant ATTDEF
values, and variable ATTDEF defaults as distinct typed entries, preserving
case-insensitive tag plus explicit duplicate-tag occurrence addressing,
multiline and hidden metadata, and the selected block identity. Malformed
multiline payloads are counted as unsupported rather than exposed as editable
values. Catalog construction is
`O(D + A + S)` time/storage for `D` definitions, `A` references, and `S` copied
UTF-16 code units, capped at 4,096 entries, 65,536 code units per string, and
1,048,576 total code units. It performs no font lookup, shaping, scene
compilation, or GPU work under the document lock.

`CadSetConstantAttributeDefinitionValueCommand` uses the selected INSERT only
as the authorization and addressing root, then retains and edits the exact
constant ATTDEF in its block. Single-line and embedded-MTEXT values change
transactionally together. Undo/Redo validates the retained INSERT, block,
definition, tag, and occurrence identities; it does not rewrite any INSERT
reference because every instance reads the same definition-owned constant.

`CadSetVariableAttributeDefinitionDefaultCommand` uses the same selected-INSERT,
tag, and occurrence address but rejects constant definitions and edits the exact
variable ATTDEF default. It updates single-line and embedded-MTEXT payloads
transactionally and retains exact identity and prior values for Undo/Redo.
Following AutoCAD's definition contract, assigned ATTRIB values on existing
INSERTs remain unchanged; INSERTs created later inherit the current default.
Undo/Redo therefore changes the default inherited by still-later INSERTs without
rewriting any existing reference.

`CadSynchronizeBlockAttributePropertiesCommand` implements the bounded,
property-only portion of block attribute synchronization. One selected,
unlocked model-space INSERT authorizes the edit; the command finds every
registered INSERT that retains the same block identity, copies each ATTDEF's
entity, text, mode, tag, and starting geometry properties into its matching
ATTRIB, and applies that INSERT's block transform. Existing assigned single-line
and embedded-MTEXT values are preserved. Case-insensitive tag matches consume
references in retained order, including duplicate tags; unmatched definitions
and references are then paired in retained order so definition tag renames do
not move the corresponding assigned value.

The complete batch is preflighted before mutation and retains the exact
ATTRIB/MText object states needed for transactional Undo/Redo. It rejects
locked target references, XRef/unloaded or dynamic blocks, malformed multiline
payloads, and more than 4,096 definitions, 65,536 INSERTs, or 1,048,576
attributes. Definition/reference count differences are rejected explicitly:
the pinned ACadSharp collection removal contract clears object handles, so an
exact reversible structural add/remove reconciliation needs a future
handle-preserving sequence-replacement dependency contract. For `D`
definitions, `I` references, and `A = D * I` retained attributes, first apply is
`O(D + I + A)` time with `O(I + A)` owned history state; Undo and Redo are
`O(I + A)`.

The shared desktop/browser shell exposes all three value-ownership paths
through one selector and value editor, plus the selected block's property-sync
action. A commit advances one document generation, rebuilds one retained
snapshot, preserves the selected INSERT and attribute key, and refreshes the
displayed value. Locked INSERTs remain inspectable but cannot authorize
reference, definition, or synchronization mutation. The same regenerated
TEXT/MTEXT commands feed managed replay, the native picture compiler, printing,
and exact selection; no shader, native ABI, cache key, glyph upload, or
device-loss contract changes.

For `I` array cells, `B` non-attribute block children, `C` visible constant
definitions, and `A` visible variable references, lowering is
`O(I * (B + C + A))` time and immutable output storage, bounded by the existing
array, nesting, expanded-entity, text-code-unit, and glyph limits. The traversal
adds no per-frame work or native crossings; stable scene/print replay retains
the same command and device-resource behavior. ATTDISP override state,
annotative contexts, fields, dynamic-block evaluation, prompt/definition
editing, structural reference add/remove reconciliation, and per-reference
XData cleanup remain explicit future contracts.

The 2026-08-28 matched macOS Release checkpoint used .NET 10.0.5, three
warmups, 24 samples, 100 variable attributed INSERTs with 21-character Inter
values, and 10,000 exact-selection queries. It produced exactly 100 source,
200 expanded, and 100 recorded entities/commands with zero unsupported or
invalid entities. Snapshot p50/p95/p99 was
`9.9083/13.8461/16.5490 ms` at `484,144 B/op`; plan-scene compilation was
`0.0254/0.0282/0.2904 ms` at `58,280 B/op`; exact point and bounds selection
were `458.0/862.7/996.4 us` and `310.5/441.2/644.6 us`, both at zero managed
bytes per operation. The same final binaries and equal-length ordinary TEXT
control produced 100 source/expanded/recorded entities, snapshot
`10.3279/14.5186/17.1422 ms` at `476,032 B/op`, plan scene
`0.0247/0.0289/0.3796 ms` at `58,280 B/op`, point selection
`439.8/757.9/1163.5 us`, and bounds selection
`317.2/562.7/665.7 us`, again with zero selection allocation. The extra
attribute traversal therefore stays in the same measured complexity and
latency class; its `8,112 B` snapshot difference is the retained INSERT/object
graph overhead for the 100-reference workload, not per-frame state.

## Persisted DIMENSION-picture lowering

Every ACadSharp `Dimension` subtype lowers the anonymous block already persisted
with the drawing. Snapshot compilation never calls `UpdateBlock` or otherwise
regenerates associative geometry: doing so would mutate document authority,
change save behavior, and make display depend on an incomplete evaluator. A
missing or empty picture is diagnosed explicitly; XRef, unloaded, dynamic,
cyclic, over-depth, and over-budget pictures follow the same bounded failure
policy as static INSERT expansion. Definition-point `POINT` children on
`Defpoints` are construction metadata and are not emitted as visible PDMODE
glyphs.

Picture entities are authored in block MCS. DXF group 12 is decoded in the
dimension's OCS and applied as a relative WCS displacement before any ancestor
INSERT transform; group 10 is not an implicit picture origin. Child layer `0`,
ByBlock properties, plottable filtering, and the top semantic handle reuse the
existing block rules. Each child then enters its ordinary analytic geometry or
complete TEXT/MTEXT/SHX compiler. Exact point/Window/Crossing selection tests
the individual expanded primitives and deduplicates successful hits to the one
DIMENSION/ancestor handle. The retained scene, native-picture compiler, and
print plan consume those same immutable primitives without a DIMENSION-specific
wire record, shader, P/Invoke, or replay path.

Translate updates semantic descriptors plus group 12 without rewriting the
picture. Rotation and scaling transform both the semantic descriptors and
persisted picture contents, preserve the group-12 WCS displacement across the
new OCS, and use transactional rollback with active-picture cycle and depth
guards. Duplicate relies on an independent cloned anonymous picture. Undo/Redo
therefore rebuilds exactly one matching immutable generation and never invokes
dimension layout generation. For `D` dimension roots and `E` visited picture
entities, expansion and picture transforms are `O(D + E)` time with `O(E)`
immutable output for compilation and at most the configured nesting depth of
temporary traversal state; stable replay adds no dimension-specific CPU work or
allocation.

This slice supports persisted display geometry for linear, aligned, ordinate,
radius, diameter, arc-length, two-line angular, and three-point angular
dimensions. Associative constraint evaluation, DIMSTYLE-driven regeneration,
annotative contexts, grip/measurement editing, and creating new dimension
pictures remain separate required contracts.

## Exact level-0 MESH wireframe lowering

An ACadSharp `Mesh` at subdivision level zero lowers its persisted vertex and
face lists into one deterministic set of undirected topology edges. Face order
and winding do not affect deduplication: the first occurrence of canonical
`(min(vertexA, vertexB), max(vertexA, vertexB))` establishes output order, so a
shared face boundary is recorded once. Vertices remain double-precision WCS
points through snapshot normalization and any ancestor INSERT affine transform.
Every retained edge carries the MESH/ancestor semantic root handle and existing
layer, ByBlock, lineweight, linetype, transparency, BVH, exact selection,
printing, and managed/native line-command behavior.

Compilation validates all vertices, face lengths, distinct vertex counts, and
zero-based index ranges before publishing any edge. It bounds total visited face
indices with `MaxMeshFaceIndices`, charges every derived edge to the global
expanded-entity limit, and emits neither partial topology nor guessed repairs.
For `V` vertices and `K` total face-index visits, expected construction is
`O(V + K)` time and `O(V + E)` temporary/output storage for `E <= K` unique
edges; hash-table worst case is `O(K^2)`. Stable replay uses the ordinary
retained line stream and adds no MESH-specific allocation, upload, crossing, or
shader.

This level-zero wire contract is preserved alongside the Flat and authored-
subdivision surface contracts below. The source MESH object continues to own
editing and DXF/DWG saving.

## Exact legacy polygon/polyface MESH wireframes

Legacy `PolygonMesh` and `PolyfaceMesh` entities use their persisted WCS vertex
topology rather than the modern `Mesh` face array. A polygon mesh validates the
declared M and N counts against the complete vertex array, walks the rectangular
grid in deterministic M-major order, and closes the M and N axes independently
from their DXF flags. Canonical undirected vertex-pair keys remove only a
topologically duplicated edge, including the two-vertex-axis closure case.
Collapsed edges, vertex width/bulge, thickness, and fitted quadratic, cubic, or
Bezier surfaces are rejected instead of being silently shown as a control cage.

A polyface mesh first validates every signed, one-based face index in a complete
transactional pass. The first zero terminates a one-, two-, three-, or four-index
record and all following values must also be zero. A negative index suppresses
the edge beginning at that index; it does not erase another face's visible use
of the same edge. Two-index records retain their documented line semantics;
one-index records retain a styled POINT using the document PDMODE contract. A
second pass resolves face layer/visibility/style and emits visible points and
edges. Deduplication includes the coordinate vertex plus resolved layer/style
for points, or the canonical edge plus resolved layer/style for edges,
preserving coincident geometry whose face properties differ.

For `V = M*N` polygon vertices, construction visits exactly `2V` candidate grid
directions and uses `O(V + E)` temporary/output storage for `E` unique edges.
For a polyface with `V` coordinate vertices and `F` face records, both validation
and emission are `O(V + F)` because each record contains at most four indices,
with `O(V + E)` temporary/output storage. Hash-table worst case is quadratic in
the number of candidate edges. Both forms charge topology visits to
`MaxMeshFaceIndices`, preflight the global expanded-entity budget, keep vertices
in double-precision WCS through editing and ancestor INSERT transforms, and
reuse the existing retained line/point, BVH, exact selection, native-picture,
and print-plan paths. No C ABI, shader, upload, or renderer-specific contract
was added; managed and native renderers consume the same canonical commands.

## Exact level-0 and bounded subdivided MESH surfaces

Modern subdivision-level-zero `Mesh`, unfitted legacy `PolygonMesh`, and
`PolyfaceMesh` records now retain one semantic `CadMesh3DPrimitive` in addition
to their exact visible edge/point streams. Face positions and flat unit normals
remain double-precision WCS in the immutable snapshot; modern per-vertex UVs
are retained when the persisted count exactly matches the vertex count. One
draw range preserves each source face's resolved layer/style. Negative polyface
indices still suppress only their directed wire edge and do not suppress the
face fill. Two- and one-index polyface records remain line/point topology and do
not become degenerate triangles.

Triangles are direct. A simple planar N-gon is projected along its dominant
Newell-normal axis relative to its first point, checked for collapsed or
self-intersecting boundaries, and deterministically ear-clipped. Work is
`O(N^2)` worst case with `O(N)` scratch and exactly `N-2` output triangles.
Autodesk documents a quadrilateral mesh face as two adjacent triangles, so a
non-planar quad uses the stable persisted `0-2` diagonal. A non-planar face with
more than four vertices has no persisted diagonal and remains explicitly
unsupported. Each triangle intentionally owns three vertices: interpolation
therefore cannot smooth across a CAD face or the fixed diagonal, and the exact
Flat visual style needs no invented crease threshold.

A modern `Mesh` with a positive persisted subdivision level now evaluates a
bounded uniform Catmull-Clark cage at that authored level. Each refinement
computes one face average, uses the four-point interior-edge mask, treats
boundaries and sharp edges as midpoints, and applies the smooth, two-edge
crease, and three-edge corner vertex masks. Boundary edges remain infinitely
sharp. Persisted crease `-1` remains sharp at every level, zero remains smooth,
and a positive level loses one unit of sharpness per refinement. With Blend
Crease enabled, the final fractional unit linearly blends the corresponding
smooth and sharp masks; a fractional or negative non-`-1` value without that
public meaning is rejected. Final face-corner normals are area weighted across
smooth adjacency and split at surviving hard creases. Per-control-vertex UVs
use linear topological refinement because the MESH record has no independent
face-varying seam data. The refined quads then use the existing deterministic
triangle and retained wire paths, so selection, printing, managed depth/orbit,
and optional native replay observe one identical generation.

Compilation rejects repeated/collapsed face vertices, same-direction shared
edges, edges incident to more than two faces, disconnected vertex fans,
non-manifold boundaries, crease records outside the face topology, and
degenerate refined faces before publishing the entity. The default maximum is
six subdivision levels and one million aggregate source/refined corner visits;
both are caller-configurable, including a zero level ceiling. If the source has
`C` face corners and requests `L` levels, the final cage has `C*4^L` corners and
the exact visit count is `C*(4^(L+1)-1)/3`. Construction is
`O(sum(V_l + E_l + C_l))` expected time and storage over the evaluated levels,
with hash-table worst cases called out as quadratic; the explicit corner budget
bounds exponential refinement before a new level is allocated. Adaptive limit
patches, legacy fitted polygon meshes, material/texture leases, and a complete
hidden-line policy remain separate fidelity gates.

`CadMesh3DSceneCompiler` copies one immutable generation into consecutive
same-style, float-rebased triangle-list batches in `O(M + R + V + I)` time and
`O(V + I + B)` storage for mesh instances `M`, face ranges `R`, flat vertices
`V`, indices `I`, and batches `B`. It is camera-independent. The shared sample
maps those batches to the existing depth-buffered `Viewport3D`/`Mesh3D`
pipeline, uses a Z-up perspective orbit camera, and renders both face sides in
Flat mode. Plan rendering and printing continue to consume only the exact wire
streams, so no filled face is accidentally projected into a 2D plot policy.
Point and Window/Crossing selection use the semantic triangle surfaces and
deduplicate them with the existing edge candidates to one source handle.

The optional `ProGPU.CAD.Native` adapter writes all style batches into one
existing native Mesh3D resource and one draw command. The stream is fixed-width,
little-endian, pointer-free, and uses the existing native C ABI and canonical
`src/ProGPU.Scene/Shaders/Native3D.wgsl`; there is no per-face, per-triangle, or
per-frame P/Invoke. Normal retained operation performs one scene update when
the CAD generation changes and one render submission per frame. The C++ engine
already implemented and validated this Mesh3D ABI, so the parity audit required
no C++ semantic fork or shader copy; matched tests cover the shared CAD batch
data and native stream boundary. Texture leases and a native CAD document
frontend remain explicit follow-ups.

Exact approved in-repository provenance is
`src/ProGPU.Scene/Extensions/Mesh3DExtensionPipeline.cs`,
`src/ProGPU.WinUI/Controls/Viewport3D.cs`,
`src/ProGPU.Backend.Native/NativeSceneStreamBuilder.cs`,
`src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_3d.cpp`, and
`src/ProGPU.Scene/Shaders/Native3D.wgsl`. The new code consumes those original
ProGPU contracts directly; the CAD topology/triangulation and adapters are new
clean-room implementations with matched managed/native boundary tests.

## Exact SOLID and 3DFACE Flat surfaces

The same immutable `CadFacePrimitive` drives plan, selection, and depth
rendering without a second entity adapter. Snapshot capture converts SOLID's
OCS corners to WCS and swaps its persisted third/fourth corners once so the
record always follows the geometric perimeter. Signed thickness becomes one
WCS extrusion vector along the normalized OCS normal and is transformed as a
vector by every ancestor INSERT/MINSERT affine transform. Bounds include both
base and top corners. 3DFACE already persists WCS corners around its perimeter.

One allocation-free bounded topology helper is authoritative for scene
counting, scene emission, point selection, and box selection. A repeated
fourth/third SOLID corner emits one triangle when flat or an eight-triangle
closed triangular prism when thick. A simple convex or concave quadrilateral
selects the concavity-preserving `0-2` or `1-3` diagonal and emits two cap
triangles when flat or a twelve-triangle closed prism when thick. A properly
crossed SOLID is split at the exact dominant-plane segment intersection into
two even-odd triangular lobes: two triangles when flat and two independent
eight-triangle prisms when thick. Thus a face requires at most sixteen output
triangles and constant stack scratch. Each emitted triangle owns three vertices
and one exact double-precision cross-product Flat normal before float rebasing;
signed negative thickness reverses base/top outward orientation. A zero-area
triangle has no surface and is skipped without consuming a draw-batch budget.

Invisible 3DFACE edge bits continue to suppress only the corresponding plan,
linetype, native-picture, and print wire. They never suppress the retained Flat
surface, including when all four edges are invisible. Point selection measures
distance to the triangle union, and Window/Crossing selection uses the same
triangle/box separating-axis test as SOLID and MESH. This keeps shaded output
and semantic surface selection aligned while leaving existing 2D wire/print
policy explicit. Flat SOLID plan/print remains even-odd filled. Thick SOLID
plan/print emits its projected base, top, and unique connector boundary as one
wire command; an extrusion parallel to the plan view collapses to one base wire
without duplicate overdraw. Depth-aware shaded output remains the 3D scene's
responsibility.

`CadMesh3DSceneCompiler` runs in `O(M + F + R + V + I)` for mesh instances
`M`, retained SOLID/3DFACE records `F`, mesh/style ranges `R`, output vertices
`V`, and indices `I`. Consecutive faces sharing a semantic root handle, layer,
and style coalesce into one exactly sized allocation set and one draw batch;
the existing global batch budget remains authoritative.
Managed `Viewport3D` and optional native replay consume the same expanded batch
arrays, existing pointer-free Mesh3D stream, stable C ABI, and canonical
`Native3D.wgsl`. Native C++ has no ACadSharp frontend and requires no parallel
semantic implementation, C ABI, generated binding, shader, or crossing change.
Matched tests cover planar and crossed SOLID order, positive/negative and
ancestor-transformed thickness, triangular/quad/crossed shell counts,
non-planar 3DFACE normals, triangle sentinels, all-invisible edges, large-WCS
rebasing, degenerate surfaces, exact side/lobe/gap/concave-notch selection,
plan/print policy, DXF/DWG round trips, and managed/native stream counts.

## Exact retained POINT dots and regenerated markers

`POINT` group 10/20/30 coordinates are already WCS. Snapshot capture validates
finite coordinates and applies only ancestor INSERT/MINSERT affine transforms;
it does not apply the entity normal as an OCS conversion. The primitive also
retains the finite group-50 rotation and the affine image of the rotated OCS
marker axes without moving the WCS location. `PDMODE=0` records the point as the existing ProGPU
round hairline `DrawPointBatch`, whose radius is fixed to half a physical device
pixel after the camera transform. `PDMODE=1` intentionally publishes no visible
primitive and no unsupported diagnostic. Modes 2, 3, and 4 retain the documented
plus, X, and vertical tick; enclosure bits 32 and 64 add the documented circle
and square, including 96 as their combination. Base value 1 remains valid with
an enclosure, so 33/65/97 render only their enclosing figures. Other bit
patterns fail transactionally. Nonzero thickness remains explicit unsupported
until retained extrusion geometry lands.

`CadPointMarkerSceneCompiler` resolves PDSIZE only after the caller supplies a
finite view. Positive values are drawing-unit diameters, zero is five percent
of drawing-area height, and negative values use their absolute percentage of
viewport height. The retained viewer rebuilds this small overlay on zoom or
viewport-height changes; printing resolves the same contract against its
finite placement area and model-units-per-output-pixel scale. Ordinary plan
geometry remains camera independent. Rotated plus/X/tick/square figures are
exact lines; orthogonal projected circle axes become two exact analytic arc
segments, while the rare affine-sheared projection retains the existing exact
transformed ellipse command rather than approximating it.

Direct POINT capture is `O(1)` time and storage per visible entity. A one-index
polyface record shares the same primitive and style contract; the mesh performs
its complete topology/PDMODE validation pass before emitting any point or edge.
Plan recording copies caller stack spans into one reusable point stream, so it
uses `O(P)` retained storage for `P` points without one managed array per point.
Compound regeneration is also `O(P)` and coalesces each contiguous style range
into one retained path command; it uses at most five figures and seven segments
per marker, with bounded exact-ellipse fallback commands only when projection
introduces shear. Stable replay adds no marker regeneration or managed/native
crossing. Managed drawing, picture nesting, native scene compilation, selection,
and print replay resolve the same immutable primitive and ordinary retained
commands. The existing native point/path/ellipse ABI and canonical shaders did
not change, and the C++ renderer already consumes those commands; no one-sided
native implementation or generated-wire update applies.

## Exact viewport-clipped RAY and XLINE construction geometry

`RAY` and `XLINE` group 10/20/30 base coordinates and group 11/21/31 direction
vectors are WCS values. Snapshot capture preserves the base point in double
precision, applies an ancestor INSERT/MINSERT affine transform to the point and
vector, and normalizes the transformed direction. A zero or non-finite
direction invalidates only that entity transactionally. Rays retain the
parameter domain `[0,+infinity)`; construction lines retain
`(-infinity,+infinity)`. Their entity headers deliberately carry empty finite
bounds: construction geometry neither changes Zoom Extents nor enters the
finite median-split BVH. A construction-only document therefore has empty
automatic extents and requires an explicit view or plot window.

`CadConstructionSceneCompiler` clips each visible primitive against an explicit
finite WCS-XY plan window using the two-axis parametric slab interval. It emits
the resulting exact segment relative to the snapshot rebase origin; a vertical
WCS line whose XY projection is a point emits the existing retained point-batch
primitive. Consecutive source-order segments with the same resolved continuous
style share one retained multi-figure `PathGeometry`, while a style transition
flushes the batch and preserves draw order. No arbitrary far endpoint,
viewport-dependent tessellation, new shader, upload format, or managed/native
crossing is introduced. The desktop/browser sample rebuilds only this bounded
overlay when pan, zoom, size, or content changes and keeps `OnRender`
mutation-free. Print compilation uses the exact plot window and printable-area
clip. A non-continuous construction linetype is omitted with `CADCON001` until
its unbounded phase-origin contract is independently established; it is never
silently replaced with a continuous stroke.

Every finite selection broad phase appends the `U` unbounded construction
records after the ordinary BVH result. Point picking computes distance to the
infinite line or the endpoint-clamped ray. Crossing selection intersects the
full 3D parametric domain with the finite query box; Window selection always
misses because a finite box cannot contain an unbounded primitive. Thus a
query is `O(log F + K + U)` typical and `O(F + K + U)` worst case for `F`
finite records and `K` BVH hits, with zero warm-query managed allocation.
Snapshot and viewport-overlay construction are both `O(U)` time/storage.
The top-view shell creates an all-finite-Z selection column so projected
construction geometry at any elevation is selectable without assigning it a
finite document extent; the general public box API retains its exact 3D
semantics.

The native applicability audit found no native CAD document frontend to pair
with the managed ACadSharp snapshot compiler. Both managed and native
renderers consume the same already-supported retained analytic `DrawPath` and
point-batch commands; the matched regression compiles the clipped overlay
through `GpuPictureNativeSceneCompiler`. The stable C ABI, generated wire
records, canonical shaders, path caches, device-loss behavior, and submission
crossing count are unchanged.

## Retained TrueType TEXT lowering

The snapshot compiler accepts an `ICadTextFontResolver`; hosts may use the
process `FontManager` plus an explicit embedded fallback, so browser builds do
not depend on system font files. Font resolution is outside render and replay
hot paths. A missing face rejects only the affected entity, while a deliberate
fallback emits `CADSNAP005` rather than silently changing document typography.
SHX and Big Font styles are never sent through a TrueType substitute.

Supported single-line TEXT is shaped at a normalized one-unit em size. The
immutable snapshot interns typefaces by identity and stores one packed glyph
index/position stream plus contiguous fallback-font ranges. Entity height,
effective entity width, oblique shear, generation mirrors, OCS rotation/normal,
and ancestor block transforms stay in a double-precision affine basis; glyph data
is not regenerated for camera changes. Left/center/right and top/middle/bottom/
baseline anchors use the Autodesk second-point rule. Conservative bounds union
the shaped advance/vertical metrics with available glyph outline bounds and transform all
four affine extrema into WCS before BVH construction.

Aligned TEXT derives baseline orientation and uniform character scale from the
two OCS endpoints while preserving the effective width-factor aspect ratio. Fit
TEXT preserves entity height and derives only horizontal scale. Both finish
exactly on the transformed second point even after font substitution, nested
non-uniform block transforms, or backward generation; backward text anchors at
the second point and traverses the same segment in reverse. Non-baseline or
non-coplanar two-point combinations fail explicitly.

The bounded content decoder maps Autodesk's three-digit decimal `%%nnn`,
`%%d`, `%%p`, `%%c`, `%%%`, and four-hex-digit `\U+XXXX` sequences before
shaping. Numeric sequences must contain exactly three decimal digits. It also records the visual
ranges toggled by `%%o`, `%%u`, and `%%k`; toggles still active at end of TEXT
close automatically. Underline geometry uses the resolved OpenType `post`
position/thickness, strike-through uses the `OS/2` position/thickness, and
overline uses the horizontal ascender plus the font's underline thickness.
These become filled local rectangles, so width, oblique shear, mirrors, Align/
Fit, OCS, and ancestor transforms apply exactly once. Visual cluster runs merge
in one pass; a toggle boundary that splits one shaped cluster fails explicitly
instead of changing shaping or partially painting a ligature/combining cluster.
Decoration rectangles participate in conservative WCS bounds. Invalid UTF-16,
malformed numeric controls, unknown font-specific controls, and missing required metrics
remain explicit gates; control syntax is never painted literally.

The decoration-specific engine audit preserves the same layout/render split:
SkParagraph exposes simultaneous underline/overline/line-through state outside
the glyph stream; DirectWrite and Win2D attach underline/strike-through to text
ranges and surface separate renderer calls; WebRender tracks interned line-
decoration primitives separately from interned text runs; Parley resolves
optional decoration offsets and sizes from containing-run metrics; and Vello's
glyph encoder remains a glyph-run operation while ordinary filled geometry is a
separate scene primitive. HarfBuzz defines shaped clusters as indivisible and
explicitly identifies cluster mapping as the seam for ranged attributes. ProGPU
therefore adopts retained metric-driven rectangles and cluster-safe range
mapping, while rejecting per-glyph decoration expansion, re-shaping around a
toggle, or putting decoration state into cached glyph identities.

Single-line snapshot work is `O(C + G)` for `C` input/decoded UTF-16 code units and `G`
shaped glyphs, with bounded output storage `O(C + G + R + F + D)` while
decorations are present, for fallback runs `R`, interned faces `F`, and merged
visual decoration runs `D <= G`. Configurable per-entity UTF-16 and document-
wide glyph limits reject oversized input atomically before it can enter retained
streams. Plan recording is `O(R + D)` commands. Stable replay uses the existing
ProGPU retained glyph cache,
DPI/subpixel policy, fallback, color-font, variable-
font, and vector-text coverage contracts. MTEXT uses the styled layout and
immutable stream contract below instead of inheriting the older `ProGPU.Dxf`
renderer's estimated bounds, per-character width loop, or formatting-stripping
approximation.

This is a managed ACadSharp snapshot/resource adapter. It changes no shader,
stable C ABI, native renderer, or compositor algorithm. Both compositors already
consume the same retained glyph-run contract, so no paired native implementation
change applies; matched native picture/pixel coverage remains the integration
gate when the CAD differential suite lands.

## Typed MTEXT content boundary

`CadMTextParser` is the clean-room source-language boundary for retained MTEXT.
It scans persisted content once and emits immutable text, paragraph-break,
column-break, and stacked-text inlines. Every inline carries its fully resolved
group state: font metadata, relative or absolute height, width, tracking,
oblique angle, baseline alignment, indexed/true/inherited color, decoration
flags, and paragraph alignment. Braces save and restore the complete state;
the Autodesk eight-level default is caller configurable but remains strictly
bounded. Semantic breaks are not flattened into spaces and stacked fractions
retain both operands plus horizontal, diagonal, or tolerance separator kind.

Escaped delimiters, nonbreaking space, four-hex Unicode, percent symbols,
decoration toggles, font options, numeric formatting, paragraph alignment, and
the three stack forms have focused conformance tests. The parser keeps the raw
paragraph payload so indentation, tabs, and reset controls cannot disappear
while their typed layout lands. Fields and unknown controls fail explicitly;
malformed groups, numbers, Unicode, stacks, and missing semicolons report the
source offset. Output code units, inline count, and nesting are independently
bounded before retained snapshot streams change.

Parsing is `O(C + R)` time and `O(D + R)` storage for `C` source code units,
`D` decoded code units, and `R` semantic inlines. It performs no font lookup,
shaping, GPU work, reflection, or ACadSharp mutation.

## Retained MTEXT layout, replay, and selection

`CadSnapshotCompiler` consumes the typed content once. `StyledTextLayout` is an
original generalization of the authoritative in-repository
`TextLayout.GenerateShapedLayout` pipeline: paragraph-wide UAX #9 resolution,
fallback-aware OpenType shaping with pre/post context, cluster-safe wrapping,
visual reordering, variable metrics, width/tracking, baseline shifts,
justification, and inline boxes. A single-style differential test matches the
existing layout's glyph identities, clusters, and positions. Layout is
`O(T + G + B)` average time and storage for UTF-16 units `T`, glyphs `G`, and
inline boxes `B`; adversarial platform fallback discovery retains the existing
font-manager cost.

The MTEXT compiler maps semantic paragraph and column breaks without stripping
them. It applies the documented 3-on-5 line-spacing basis, distinguishes Exact
from AtLeast spacing, keeps static/dynamic persisted column heights bounded,
honors explicit column breaks and reverse flow, excludes gutters from each
background mask, and resolves all nine attachment points before composing the
entity's WCS normal/direction basis with any nested block affine transform.
Horizontal, diagonal, and tolerance stacks remain inline objects; their upper
and lower operands are independently shaped at a bounded relative size and
their separators are retained filled geometry. Unsupported fields, paragraph
indentation/tab payloads, vertical flow for TrueType, compiled Unicode/Big Font
SHX content, invalid numeric state, and content exceeding persisted column
capacity remain explicit typed diagnostics rather than degraded output.

The immutable snapshot owns global glyph indices/positions and font identities,
plus MTEXT-specific contiguous runs carrying font size, stretch, shear, and
RGBA paint. Separate packed streams retain masks/frames, decorations, and stack
separators. Snapshot publication is transactional: parsing, shaping, column
placement, bounds, and budgets complete in temporary storage before any shared
stream changes. Plan recording is `O(R + M + D + S)` commands for runs `R`, mask
rectangles `M`, decoration rectangles `D`, and separators `S`; replay never
reparses or reshapes content. `DrawTransformedGlyphRunRange` combines the
existing ProGPU-owned range and local-font-transform contracts while preserving
shared arrays and unsheared positions.

Point and Window/Crossing selection traverse the same cached analytic glyph
outlines with the retained stretch/shear, plus the exact filled mask,
decoration, and separator geometry. Warm selection allocates zero managed
memory. The print compiler reuses the identical retained commands. The managed
compositor and native picture compiler already consume this shared glyph/shape
contract; a native-picture regression covers formatted MTEXT, so no separate
native CAD scene compiler applies to this slice. Matched pixel and Release
latency/throughput evidence remains required before making a performance claim.

Standard horizontal SHX MTEXT reuses the same typed parser and column/background
contracts but has an original analytic-path layout specialization in
`CadSnapshotCompiler.ShxMText.cs`. Each decoded standard-SHX character resolves
to the immutable `CadShxGlyphCache` once, retains the font-authored horizontal
advance, and is positioned once before snapshot publication. Ordinary spaces,
DXF U+0020 escapes, and decimal shape 032 are break opportunities; U+00A0 maps
to the same space glyph while retaining its nonbreaking semantic bit. Wrapping
breaks only at those opportunities, and a long unbroken word overflows its
column instead of being split by character. Paragraph/column breaks, all nine
attachments, Exact/AtLeast 3-on-5 spacing, left/center/right/justify/distributed
alignment, static or dynamic auto-height/reverse columns, height/width/tracking/
oblique/baseline/color formatting, three stack forms, masks/frames, decorations,
and nested affine transforms lower into temporary device-independent geometry.

The snapshot then appends one packed shared SHX glyph stream and contiguous
SHX-MTEXT transform/paint runs plus the existing MTEXT rectangle/stroke streams
transactionally. Compilation is `O(C + G + L)` time and temporary storage for
decoded units `C`, retained glyph placements `G`, and lines `L`; the bounded
32-iteration automatic-column search adds `O(32L)`. Recording and exact
selection are `O(G + M + D + S)` for drawable glyphs, masks/frames,
decorations, and stack separators. Replay performs no parsing, font lookup,
interpretation, layout, or outline cloning, and warm point/Window/Crossing
selection allocates zero managed memory. Standard SHX does not define Unicode
shaping, fallback runs, variation axes, or synthetic bold/italic: compiled
Unicode/Big Font, vertical/RTL layout, inline TrueType switching, and SHX bold/
italic therefore remain diagnosed capability gates rather than approximations.

The exact in-repository provenance is the existing ProGPU-owned
`CadMTextParser`, `CadSnapshotCompiler.MText.cs`, `StyledTextLayout`,
`CadShxText.cs`, single-line SHX lowering in `CadSnapshotCompiler.cs`, and the
retained path/selection contracts in `CadPlanSceneCompiler` and
`CadTextSelection`. No third-party parser, layout, renderer source, naming, or
control flow was used. The managed/native applicability audit found no native
CAD parser or scene compiler to duplicate: both picture compilers consume the
same existing `DrawPath`/`DrawRectangle` commands. A formatted native-picture
regression and print-plan regression cover that shared contract; no C ABI,
shader, upload, atlas, cache-generation, or device-loss rule changes.

A 2026-08-28 macOS arm64 Release smoke run of the checked-in benchmark fixture
(`100` formatted MTEXT entities, one warmup, five measured iterations, `10,000`
exact-selection queries) recorded snapshot p50/p95/p99 of
`37.2231/39.8837/39.8837 ms`, plan-scene
`0.2485/0.5205/0.5205 ms`, point selection
`505.2/600.5/843.2 us`, and bounds selection
`192.1/262.4/331.7 us`. Warm exact selection reported zero managed allocation;
snapshot construction allocated `3,454,976` bytes and plan recording `864,920`
bytes per 100-entity operation. This is a bounded feature smoke measurement,
not a before/after performance or quality claim. The reproducible command uses
`--entities 0 --mtext-entities 100 --text-selection --warmup 1 --iterations 5
--queries 10000`; integration performance claims still require longer matched
runs and the repository's platform/GPU profiling gates.

The corresponding 2026-08-28 macOS arm64 Release SHX-MTEXT smoke fixture used
`100` formatted, stacked, two-column entities, one warmup, five measured
iterations, and `10,000` exact-selection queries. It retained `1,400` commands
and recorded snapshot p50/p95/p99 of `1.6948/2.2920/2.2920 ms`, plan-scene
`0.5510/3.3621/3.3621 ms`, point selection
`87.0/164.3/474.5 us`, and bounds selection
`41.2/53.7/100.1 us`. Snapshot and plan recording allocated `1,330,744` and
`1,846,520` managed bytes per 100-entity operation; both warm exact-selection
lanes allocated zero. The JSON output is reproducible with the checked-in
`--shx-mtext-entities` lane and the command below. This is feature smoke
evidence from one final binary, not a before/after performance or quality claim.

## Bounded standard SHX source

`CadShxFont.Parse` is the initial clean-room SHX ingestion boundary. The input
is caller-owned only for the synchronous parse. A successful parse copies it
once into immutable owned storage and retains every program as a
`ReadOnlyMemory<byte>` slice, so no per-shape program copy or runtime text
parsing is required. Default limits cap a source at 16 MiB, 65,535 directory
entries, and 2,000 program bytes per shape. The parser rejects malformed
directory ranges, duplicates, unterminated names/programs, truncated records,
invalid standard-font metrics, trailing data, and unsupported container
signatures before publishing a font.

Parsing takes `O(B + S)` time and `O(B + S)` owned storage for `B` input bytes
and `S` shape-directory entries. Shape lookup is expected `O(1)` and program
storage remains packed.

### Standalone SHAPE identity and retained-path contract

A standalone DXF `SHAPE` carries its shape name in group 2; that value is not a
text-style name. DWG instead retains the numeric shape identifier and the
shape-file STYLE handle. The pinned ACadSharp model therefore preserves both
representations: DXF reads and writes `ShapeName` without inventing a STYLE,
while DWG reads and writes `ShapeNumber` plus the shape-file STYLE reference.
Its transform implementation derives the entity's complete local affine frame,
transforms the origin and axes, and decomposes the result back into size,
relative X scale, rotation, and oblique values. Collapsed or reflected frames
fail instead of publishing a corrupt entity.

`ICadShxShapeResolver` is a distinct typed capability on the reusable catalog
and its immutable resolver snapshots. A DWG identity resolves only within its
referenced primary or explicitly mapped shape file, by number first and then by
name. A DXF name with no file identity searches registered non-text shape files
in stable registration order, matching AutoCAD's loaded-shape namespace. Shape
names are indexed case-insensitively in expected `O(1)` time, but only valid
uppercase definition labels participate; lowercase font-description labels are
not promoted into drawable identities. Exact primary lookup is expected `O(1)`;
DXF name-only lookup is `O(F)` across `F` registered shape files, with expected
`O(1)` work per file. Resolver freezing copies that bounded order once per
catalog generation, so one document compile cannot observe a mixed registration
state.

Snapshot lowering interprets the resolved immutable glyph at most once through
`CadShxGlyphCache`, then retains one `CadShxShapePrimitive` containing the shared
analytic path and a WCS origin/X/Y basis. The basis composes size, X-only width
factor, vertical oblique shear, OCS rotation and normal, and every enclosing
block transform exactly once. The documented insertion point remains WCS even
when the OCS controls orientation. Nonzero thickness is an explicit unsupported
3D extrusion contract. Missing identities, empty programs, non-finite or
degenerate transforms, and unavailable shape-capable resolvers also fail with a
typed diagnostic rather than substituting arbitrary geometry.

Compilation is `O(C + S)` on the first use of a definition and expected `O(1)`
on later cache hits for `C` executed SHX commands and `S` retained segments.
Bounds are the exact affine image of the cached analytic bounds. Recording emits
one existing stroked `DrawPath`; exact point and Window/Crossing selection walk
the same analytic path. Viewport culling, retained scene replay, print-plan
recording, lineweight policy, and device loss therefore continue through the
existing shared path pipeline with no new shader, upload, texture, or managed/
native ABI. The native renderer needs no CAD parser fork and consumes the same
retained command as the managed renderer.

`CadShxInterpreter` implements the standard command stream directly from the
Autodesk contract: 16 encoded vector directions; draw/move modes; cumulative
divide/multiply scale; balanced push/pop with the specified four-location
stack; one-byte standard subshape calls; single and repeated signed XY
displacements; octant, fractional, bulge, and polyarc commands; and command 14
dual-orientation gating. Arcs stay analytic `ArcSegment` values. A full octant
circle becomes exactly two retained semicircles because the endpoint-based path
record cannot encode a coincident-start/end full circle as one segment; no
curve is sampled. Pen-up commands advance the retained endpoint without
emitting geometry, and discontinuous moves start a new figure when drawing
resumes. The font header's above/below units and final pen-up endpoint remain
available for the later height, baseline, character-advance, and vertical-
advance lowering.

Interpretation is `O(C + S)` time and `O(S + D)` storage for recursively
executed commands `C`, emitted retained segments `S`, and active subshape depth
`D`. Defaults cap commands and segments at 100,000 each, recursion at 32,
absolute coordinates and cumulative scale at one billion, and position depth
at the format-defined four entries. Cycles, missing/reserved subshapes,
unbalanced stack operations, zero/overflowing scale, malformed operands,
unreachable terminators, unsupported vertical commands, and limit overruns fail
the whole interpretation. Each direct call returns a fresh caller-owned path.
`CadShxGlyphCache` instead interprets once per immutable font identity, shape
number, and orientation, keeps the mutable path private, and exposes immutable
advance/bounds/segment metadata. Its locked cache is safe for concurrent
snapshot workers; lookup is expected `O(1)` after the first bounded execution.
Unicode two-byte subshape references and Big Font ranges use different
contracts and are rejected instead of being guessed as standard records.

`CadShxTextLayout` scans one standard-font TEXT value in `O(C + G)` time and
retains `O(G)` placements for `C` UTF-16/control-code units and `G` characters.
It accumulates each font-authored pen-up endpoint as the next origin rather than
estimating character widths. Autodesk decimal controls address their exact
three-digit shape number; degree, plus/minus, and diameter controls and literal
Unicode equivalents map to the standard format's reserved shapes 256, 257, and
258. Percent, DXF four-hex-digit escapes, and decoration toggles are decoded
without changing glyph identity. Each placement also preserves whether its
space is a line-break opportunity so U+00A0 can share shape 32 without becoming
breakable. Missing shapes, malformed controls, surrogate
pairs, unsupported nonstandard Unicode, empty control-only strings, coordinate
growth, and code-unit/glyph limits fail explicitly. Decoration flags are
retained per placement so snapshot lowering can coalesce exact authored spans
without rescanning or rewriting the source string.

`CadSnapshotCompiler` accepts an `ICadShxFontResolver`, keeping desktop font
search, browser-bundled assets, and application substitution policy outside the
document and render hot paths. Standard horizontal SHX TEXT scales the font's
above metric to entity height, preserves its below-baseline metric and actual
path bounds, and composes effective width, oblique shear, generation mirrors,
OCS rotation/normal, justification, and ancestor block transforms into one
double-precision basis. Align and Fit use the two authored endpoints: Align
changes both axes while preserving the width-factor aspect ratio, and Fit keeps
the authored height while changing horizontal scale. Horizontal overline,
underline, and strike-through flags are coalesced into retained stroke segments
at `above`, `-below`, and the midpoint of that font-header box respectively.
The spans include decorated spaces, share the entity's fixed-device-space pen,
and add at most three segments per placed glyph.

A dual-orientation standard font can also lower a default-justified vertical
STYLE. The font's command-14 program remains authoritative: the entity insert
point is the documented top-center start, each character must produce a
downward Y-only authored advance, and the normal height, width-factor, oblique,
mirror, OCS rotation/normal, and ancestor-block basis is composed once. This
does not rotate horizontal glyph geometry or synthesize a vertical advance.
Non-default vertical justification and decorated vertical text still reject the
affected entity because their placement has not yet been independently
verified. A substituted SHX font emits `CADSNAP006`. Missing glyphs,
orientation-inconsistent per-character advances, Big Font, and unsupported
vertical placement reject the affected entity rather than guessing layout.

`CadShxFontCatalog` is the default reusable resolver for hosts and benchmark
fixtures. Initialization parses or registers immutable standard caches under a
portable filename plus explicit aliases; lookup strips either Windows or Unix
directory separators and compares names case-insensitively without touching the
filesystem. Hosts may install explicit SHX-to-SHX filename mappings and one
alternate font. A present mapping wins even when the requested font is also
registered; if its target is absent, lookup falls back to the originally
requested filename. Style-name aliases are considered only after filename
lookup, and alternate/style/mapped substitutions remain diagnostic. Registration
is transactional on alias collision, repeated resolution is locked and expected
`O(1)`, parsed bytes are owned once, and Big Font requests never enter the
standard catalog. The catalog caches an immutable resolver generation until its
configuration changes; each document compile captures that generation once so
concurrent host registration cannot mix font policy inside one snapshot. The
shared sample exposes this catalog so desktop code can
register discovered support files and browser code can register bundled byte
assets through the same API. Ordered filesystem search remains host
initialization work rather than synchronous snapshot behavior.

`CadShxFontDiscovery.DiscoverAsync` is the opt-in desktop host adapter for that
filesystem work. It captures a document's distinct standard-SHX style filenames
under the session lock, then releases the document before doing any IO. It probes
the drawing directory first and explicit support directories in caller order,
using exact filenames without enumerating directories. An FMP replacement is
probed before its original name; if the mapped target is unavailable, discovery
probes the original filename so the catalog can apply its documented fallback.
Defaults bound the operation to 256 directories, 4,096 distinct style requests, 16 MiB per parser
input, and 256 MiB total. Candidate sizes and reads are preflighted before any
registration, first-existing corrupt files do not silently fall through to a
lower-priority directory, and missing/rejected resources produce bounded typed
diagnostics. Parsing is `O(B + S)` for total bytes `B` and SHX directory entries
`S`; exact path probing is `O(F * D)` for unresolved filenames `F` and search
directories `D`. Browser hosts continue to register bundled bytes directly, and
snapshot compilation/render replay performs no discovery or file IO.
The shared sample now invokes this adapter before snapshot compilation when its
picker returns an existing fully-qualified desktop file. Its public ordered
support-directory list is copied for the operation; the drawing directory still
wins. Virtual browser files never enter that path and continue through the same
byte-only catalog seam. The status bar reports loaded, missing, and rejected SHX
resource counts rather than hiding substitution state.

`CadFontMappingTable.Parse` provides the bounded configuration seam for the
documented FMP format. It accepts ordinary ASCII input containing exactly one
requested/replacement pair per non-empty line, separated by one semicolon;
requested names contain no path and may omit their extension, while replacement
filenames require an extension. It trims only surrounding ASCII space/tab and
rejects comments or other undocumented syntax, duplicate requested names,
paths, control/non-ASCII bytes, ambiguous separators, missing extensions, and
empty tables. Defaults cap the source at 1 MiB, 16,384 mappings, and 1,024 bytes
per line. Parsing is `O(B)` time and `O(M + T)` retained storage for source bytes
`B`, mappings `M`, and filename characters `T`. Applying an SHX-to-SHX table to
`CadShxFontCatalog` validates the complete table before changing one resolver
generation; cross-kind mappings remain retained configuration data for the later
unified TrueType/SHX resolver and cannot silently enter the standard SHX path.

The snapshot owns packed `CadShxGlyphInstance` placements and
`CadShxDecorationSegment` values but references the resolver-owned immutable
`CadShxGlyph` metadata. Plan recording is `O(G + D)` and emits one existing
`DrawPath` command for each drawable stroked glyph plus one `DrawLine` per
coalesced decoration segment, applying only retained affine placement; spaces
and other pen-up-only shapes emit no glyph command. `D` is bounded by `3G`.
All repeated character instances share the same cached `PathGeometry`, so
snapshot compilation neither reinterprets nor clones glyph outlines. TrueType
and SHX TEXT/MTEXT placements share one document-wide glyph budget. Freezing the
recorded scene uses the normal retained picture ownership contract.

The binary container layout was independently observed from the compiled
`external/ACadSharp/samples/test_shape.shx` artifact pinned by this repository;
no ACadSharp or other third-party parser implementation was consulted or
copied. The regression reads that compiled artifact as an observable fixture.
Program semantics and the 2,000-byte definition limit come only from the
official Autodesk shape/font documentation linked below. Autodesk's Unicode
documentation specifies the source `*UNIFONT` header, 16-bit shape numbers,
and two-byte command-7 references, but not the compiled container layout; no
compiled Unicode or Big Font parser is inferred from a foreign implementation
or signature alone. The pinned standard fixture
also executes through the new interpreter, while independent synthetic tests
cover every command family, direction geometry, analytic endpoints/radii,
horizontal/vertical behavior, default top-center vertical snapshot placement,
decoration run coalescing, state composition, cycles, malformed programs, and
configured bounds. The managed snapshot/recording adapter changes no
shader, C ABI, compositor algorithm, or GPU resource contract. It feeds the
pre-existing retained analytic `DrawPath` and `DrawLine` commands consumed by
both managed and native picture compilers, so a distinct native SHX parser or
per-glyph boundary call is neither required nor permitted. Existing native
line/analytic-path lowering tests remain the paired integration gate;
CAD-specific managed/native image differentials remain required before full
fidelity acceptance.

The Release benchmark's optional `--shx-interpretations` lane measures fresh
uncached path construction outside the document pipeline. Two 24-iteration,
1,000-shape runs on the same Apple Silicon/.NET 10 host reported batch
p50/p95/p99 of 3.043/6.942/7.783 ms and 1.490/4.280/5.696 ms, with 1,256,065
managed bytes per batch in both runs. The synthetic shape exercises direction
vectors plus octant, bulge, and polyarc output. These numbers establish the
interpreter construction baseline only; process-to-process tails vary, no
improvement claim is made, and they are not a steady TEXT replay target. The
allocation result motivated the implemented per-font glyph cache: normal
retained replay must not construct a `PathGeometry` for every placed character.
Snapshot/scene integration now proves cache reuse under a representative
document workload.

The same two runs measured 1,000 warm-cache layouts of eight standard glyphs at
p50/p95/p99 2.475/3.695/4.089 ms and 1.565/2.212/2.507 ms, with 584,024 managed
bytes per batch in both runs. This is a device-independent placement baseline,
not retained replay and not an improvement claim. Snapshot integration packs
those placements into generation-owned arrays and reuses the cached glyph paths;
it does not reinterpret or clone eight path graphs per TEXT entity.

The 2026-08-28 Release decoration baseline used the same final binary, 1,000
eight-glyph SHX entities, five warmups, and 50 measured iterations. Plain and
three-run decorated inputs recorded 8,000 and 11,000 commands respectively.
Snapshot p50/p95/p99 was 7.052/22.980/40.892 ms with 1,900,393 managed bytes for
plain input and 5.403/17.117/21.360 ms with 3,035,375 bytes for decorated input.
Plan recording was 7.614/39.206/44.884 ms and 10,816,762 bytes versus
11.158/58.786/129.463 ms and 15,616,681 bytes. Process-order and GC effects make
the latency tails unsuitable for a comparative claim; these numbers establish
the explicit retained-command and allocation cost of three decoration segments
per entity. The snapshot path uses one generation-owned packed decoration list
and no per-entity decoration staging list or array.

The 2026-08-27 Release applicability/performance audit compared commit
`778dc69f` with this lowering on the same Apple Silicon/.NET 10 machine and the
same 10,000-entity workload. A 100-iteration alternating-order control measured
snapshot p50/p95/p99 at 6.433/12.413/21.210 ms before and
7.008/12.521/14.140 ms after, means of 7.712/7.781 ms, and
9,078,865/9,053,174 managed bytes per generation. Repeated 24- and 100-iteration
runs showed GC/system noise in individual tails, so these results establish no
repeatable regression and make no improvement claim. Matched Xcode Instruments
Time Profiler, Allocations/VM Tracker, and eight-second Metal System Trace
captures plus exported tables are retained locally under
`artifacts/progpu-cad/instruments/block-insert-20260827/`. The host reported that
its selected Metal counter profile was unavailable, but command-buffer and
`currentAllocatedSize` events were captured; their differing active-frame counts
preclude a GPU delta claim. The native renderer, stable C ABI, canonical shaders,
and GPU algorithms are not changed: both managed snapshots emit the existing
retained commands, so no paired native implementation change applies to this
ACadSharp graph adapter.

## Printing and export

Printing is a separate physical-output compiler over a layout snapshot. It
resolves page setup, viewports, CTB/STB plot styles, physical lineweights,
transparency, raster quality, font substitution, and paper-space ordering. It
must reuse analytic vector and shaped-text resources and produce deterministic
preview/output from the same compiled print plan.

The first model-space print-plan foundation is implemented by
`CadPageSetupCatalogCompiler`, `CadPageSetupPrintOptionsCompiler`, and
`CadPrintPlanCompiler`:

- every layout and standalone named page setup is copied synchronously with its
  matching document generation. Setup count, individual/total UTF-16 ownership,
  diagnostics, and cancellation are bounded; ordering is deterministic with
  model layout first, then paper layouts, then named overrides. The immutable
  catalog exposes no mutable ACadSharp object;
- the snapshot retains paper/media/printer identifiers, dimensions and margins
  (which the DXF contract defines in millimeters), paper unit, rotation, plot
  area, DCS plot-window values, layout limits/extents, plot origin, centering,
  current custom scale, standard-scale selection/factor, lineweight/style flags,
  and shade mode/resolution/DPI. Retention is not a claim that every policy is
  already renderable;
- exact lowering presently requires model space, a defined 0/90/180/270-degree
  page rotation, inch or millimeter paper units, drawing extents, explicit wireframe, enabled and
  unscaled object lineweights, and no applied nonempty CTB/STB style sheet.
  Standard scale code zero selects Fit; otherwise the source's current custom
  numerator/denominator is authoritative and converts to drawing units per
  millimeter. A centered flag selects centered placement; otherwise plot origin
  becomes an offset from the printable lower-left;
- Window is deliberately rejected even though its raw rectangle is retained:
  Autodesk defines the stored coordinates in display coordinate system (DCS),
  while the current print plan accepts an explicit WCS window. Display, named
  view, Limits, paper-space/Layout viewports, pixel paper units,
  hidden/shaded/as-displayed output, disabled/scaled lineweights, and applied
  plot styles likewise return specific diagnostics rather than an approximation;
- the pinned ACadSharp page-setup surface does not expose Autodesk's separate
  Plot Transparency option. Generation-matched lowering therefore rejects a
  snapshot containing any non-opaque retained style (`CADPAGE118`) instead of
  assuming that transparency should or should not print. The direct programmatic
  print-plan API remains an explicit caller policy and preserves retained alpha;
- paper size and unprintable margins are finite millimeters converted once to
  deterministic integer output pixels with round-half-away-from-zero behavior;
  page coordinates are limited to exact float integers and the default total
  target budget is 268,435,456 pixels;
- page rotation treats stored paper dimensions and margins as physical,
  unrotated media coordinates. A 90/270-degree setting exchanges the oriented
  output axes and permutes the four device margins; 180/270 additionally
  compose one exact half-turn after placement. The printable clip undergoes the
  same rotation. Positive offset axes therefore originate at AutoCAD's rotated
  lower-left corner, including asymmetric-margin and upside-down output;
- the default plot area is the exact union of visible retained entities on
  plottable layers, while a caller can supply an explicit finite WCS window;
  non-plottable layers remain visible in the screen snapshot but are skipped by
  the print scene;
- fit chooses the smaller positive X/Y printable scale. Exact scale expresses
  drawing units represented by one paper millimeter. Degenerate one-axis extents
  fit by the other axis; a point-like extent uses the explicit units/mm fallback;
- centered placement keeps the plot-bounds center at the printable center.
  Offset placement maps the plot lower-left to a finite millimeter offset from
  the printable lower-left, preserving CAD's Y-up model and page Y-down output;
- CAD lineweight millimeters convert through the output DPI and remain fixed
  device strokes, independent of model plot scale by default. The explicit
  lineweight multiplier is the future page-setup `ScaleLineweights` seam;
- one owned content picture is retained by the plan. `CreatePagePicture` adds
  only one printable clip and one transformed picture replay, and returns an
  independently owned page picture suitable for preview or a later platform
  printer/vector/raster adapter. Compilation is O(E + C) time and O(C) retained
  storage for E entity headers and C scene commands; no raster surface is
  allocated.

The shared desktop/browser shell now consumes that contract through a retained
page-setup-driven preview. Its selector is rebuilt from the detached catalog on
each content generation and lists layout-owned settings, named overrides, and
one explicit A4 model-extents fallback. It prefers the first supported
model-space layout, preserves a selection by source kind and name across a
generation replacement, and never silently substitutes the fallback for an
unsupported setup. Unsupported entries remain visible with their first typed
`CADPAGE` diagnostic; selecting one releases any old preview and reports the
unsupported contract. This is preview selection only: it does not mutate which
page setup is current in the ACadSharp document. A separate `Apply to Model`
command is enabled only for model-compatible named setups, so preview and edit
intent cannot be confused.

`CadApplyNamedPageSetupCommand` implements that edit as an original fixed-field
copy of the ACadSharp `PlotSettings` contract. It resolves the target layout and
named dictionary entry under the session edit lock, rejects model/paper target
mismatches before mutation, preserves layout identity, block ownership, tab
order, geometry, UCS, and viewport state, and changes only inherited plot
settings. The old and applied states are retained as bounded value records;
Apply, Undo, and Redo are transactional O(1) operations with O(1) history
storage. The shared shell applies to the displayed Model layout, publishes one
new content generation, rebuilds the catalog while preserving the named
selection, and exposes the edit through the existing synchronized Undo/Redo
history. DXF and DWG save/reload regressions cover the applied settings.

`CadCreateNamedPageSetupCommand` implements the inverse authoring workflow. It
resolves one source layout and the case-insensitive `ACAD_PLOTSETTINGS`
dictionary before mutation, rejects duplicate or over-budget names, constructs
one detached `PlotSettings`, copies only the shared fixed plot contract, and
sets both its dictionary identity and page-setup name to the requested value.
The source layout and its block, geometry, tab, UCS, and viewport state are
never changed or retained by the new object. Undo removes the exact retained
dictionary object and detaches its document ownership; Redo restores that same
object identity through ACadSharp's collection ownership events. Construction
is O(N) time/storage for bounded source/new names, while Apply, Undo, and Redo
are O(1) over the fixed plot record and one dictionary entry. The shared
desktop/browser row saves the current Model settings, selects the new catalog
entry after the synchronous generation replacement, disables case-insensitive
duplicates, and uses the existing history. Matched ownership, duplicate,
selection, Undo/Redo, DXF, and DWG regressions cover the contract.

`CadUpdateNamedPageSetupFromLayoutCommand` adds the first bounded Modify action.
It resolves a source layout and existing named setup under the session lock,
rejects model/paper target mismatches before mutation, and replaces only the
named object's fixed plot contract. The target object, document handle,
dictionary ownership, dictionary key, `Name`, and page-setup name stay stable;
the source layout remains unchanged. Previous and applied plot states are fixed
value records, so Apply, Undo, and Redo are transactional O(1) work/storage
after O(N) bounded name ownership. The shared `Update selected` action is
enabled only for model-compatible named entries, preserves catalog selection
across each generation replacement, and does not implicitly apply the modified
record to any layout. Matched identity, target-space, UI, Undo/Redo, DXF, and
DWG regressions cover this behavior.

`CadDeleteNamedPageSetupCommand` removes only a standalone named setup. It
resolves the exact `ACAD_PLOTSETTINGS` entry, scans the bounded layout catalog,
and rejects deletion before mutation when any layout's page-setup name refers
to that entry. Apply and Redo are O(L) time for L layouts plus O(1) dictionary
work; Undo is O(1), and retained command storage is one bounded name and the
detached object reference. Undo restores the same `PlotSettings` instance and
dictionary ownership; consistent with ACadSharp collection removal, detach
clears the document handle and reattachment assigns a fresh handle. No layout
plot state is modified. The shared `Delete setup` action is available only for
an unassigned named selection, falls back to the Model layout selection after
removal, and participates in the existing generation-safe Undo/Redo history.
Matched assignment preflight, identity, UI, Undo/Redo, DXF, and DWG regressions
cover the contract.

`CadRenameNamedPageSetupCommand` changes a named setup without replacing its
object. The command preflights a case-insensitive target-name collision, retains
at most 4,096 exact layout references whose current page-setup marker matches
the setup dictionary name or page name, and then changes the dictionary key,
named `PageName`, and referring layout markers transactionally. This preserves
the setup object, owner, handle, fixed plot values, layout geometry, and the
observable association shown by Page Setup Manager. Initial Apply is O(L) for
L layouts; Undo/Redo are O(R) for R retained referring layouts, and command
storage is O(R + N) for bounded names and prior immutable marker strings.
Case-only/no-op renames are rejected because the drawing dictionary is
case-insensitive. The shared `Rename selected` action consumes the bounded name
input, selects the renamed catalog entry, and uses the same history generation.
Matched collision, identity, association, UI, Undo/Redo, DXF, and DWG
regressions cover the contract.

`CadImportNamedPageSetupsCommand` implements bounded cross-document import.
The command synchronously captures either all named setups or an explicit
case-insensitive subset while holding the source session read lock, owns every
fixed plot value and copied string, and then releases the source completely.
The import is capped at 4,096 setups, 4,096 UTF-16 code units per string, and
1,048,576 total copied string code units. The target edit preflights the whole
batch and offers two explicit policies: `Reject` leaves the drawing and history
generation untouched on any name collision; `ReplaceExisting` preserves the
target object's dictionary identity, casing, owner, and handle while replacing
only its fixed plot state. New entries retain exact object identity through
Undo/Redo and receive ACadSharp's normal fresh handle on reattachment. One
command is one content generation, all mutations have inverse rollback, and
layouts—including their page-setup markers and copied plot state—are never
changed implicitly. Capture costs O(I + S) time/storage for I setups and S
owned string units; Apply, Undo, and Redo cost O(I).

The shared desktop/browser shell uses the existing DXF/DWG picker and exposes
separate `Import setups` and `Import / replace` actions, making collision intent
explicit before file selection. It loads the source into an independent
session, imports every standalone named setup through the existing synchronized
history, rebuilds the detached catalog, reports created/replaced counts, and
keeps the current drawing identity unchanged. DWT is not advertised because
the current document store supports DXF/DWG streams only. Matched reject,
replacement identity, detached-source, subset, Undo/Redo, shared-UI, DXF, and
DWG regressions cover the contract.

This design adopts Autodesk's documented selection and redefine-on-conflict
semantics while making rejection/replacement an explicit typed policy. It
adapts the dialog-owned workflow to ProGPU's session snapshot and reversible
command model. It rejects silent replacement, implicit propagation to layouts,
retained source-document references, and unbounded source strings. Primary
references are Autodesk's
[named page-setup workflow](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-62B163D7-92F5-4793-85C2-246BDDA81470.htm),
[direct import procedure](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-4FADF4BA-7FE6-4F57-84BC-0A5A56F8930F.htm),
[Import Page Setups dialog](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-C3DE7352-1DE5-4F82-A5FE-5884C27B0473.htm),
and the official
[plot-settings dictionary contract](https://help.autodesk.com/cloudhelp/2017/ENU/AutoCAD-NET/files/GUID-56BD3247-471C-4471-A238-FFDFDC3BD2E4.htm).

This slice changes document metadata and the shared host only. It changes no
scene compiler, shader, render command, cache, GPU resource, native ABI, or
managed/native renderer behavior, so a paired native rendering implementation
is not applicable; both renderers continue consuming the same immutable scene
and print-plan contracts.

That persistence gate exposed an ACadSharp DXF reader defect: overlapping group
codes from `AcDbPlotSettings` and `AcDbLayout` were always offered to the layout
map first. The pinned ProGPU-owned ACadSharp feature commit `024ef7f5` routes
layout parsing by the active subclass and adds ASCII/binary differential
round-trip coverage. The dependency master branch remains untouched.

Entering preview compiles a separate generation-tagged snapshot with
`CadDrawOrderPurpose.Plotting`, so a drawing whose SORTENTS Plotting bit differs
from interactive regeneration is never previewed in the wrong order. The print
snapshot resolves against a white physical-page background. In particular,
indexed ACI 7 is deterministically white on the dark model canvas and black on
the light page, while explicitly authored true white remains white. The switch
uses integer Rec. 709 luminance weights and a midpoint light/dark threshold; no
other indexed or true color is changed.

`CadPrintPreviewCanvas` chooses an output DPI from the available logical
viewport and the selected setup's oriented physical media dimensions, capped at
300 DPI, then displays the resulting page at one page pixel per logical pixel.
Quarter-turn page rotations exchange the fit axes. That 1:1 contract is
intentional: the page compiler's fixed-device strokes already express physical
lineweights at its output DPI, and a second fit transform would incorrectly
leave those widths unscaled. The control owns only the independently retained
page picture and page metadata, centers it with a translation, draws a transient
dynamic-theme paper/printable-area frame, performs no CAD traversal or print
compilation during `OnRender`, and disposes the retained page on exit. Catalog
refresh and compatibility labeling are O(L log L + S) capture plus O(L)
constant-size lowering for L setups and S owned string code units. Entering
preview costs the existing plotting snapshot O(E log E) plus print-plan O(E + C)
work and O(E + C) generation storage; stable preview replay is O(C) retained
commands with no semantic-document access or retained upload after the compiled
scene cache is warm. A content-generation replacement exits and releases a stale
preview instead of presenting the old page.

Catalog extraction, model-space page rotation, and applying a compatible named
setup to a layout are now implemented, but this foundation does not claim
layout/paper-space viewport lowering, DCS camera/view lowering, page-setup
creation/editing/import, CTB/STB overrides, shaded-viewport policies,
transparency flattening, PDF/SVG, raster encoding, printer
enumeration/spooling, or multi-page collation. Those remain explicit typed
compilers/adapters and conformance gates; unsupported features are not silently
rasterized or dropped. The preview adds no shader, stable C ABI, native renderer,
compositor, atlas, or device-loss behavior. The ACI-7 decision occurs once in the
shared immutable style stream; managed and native picture compilation consume
the same resolved color, retained picture, clip, affine transform, shaped text,
analytic path, and fixed-stroke commands, with a matched native-compilation
regression. No paired native algorithm change applies.

## Performance and conformance gates

Representative Release workloads report cold open, first visible frame, pan,
zoom, orbit, edit-to-frame, save, and print-plan p50/p95/p99. Counters include
entities visited, visible entities, scene updates, draw calls, boundary
crossings, bytes copied/uploaded, retained uploads, CPU/GPU allocations, cache
residency/evictions, and device-resource generations.

Stable replay targets one render submission, zero scene update, zero retained
upload, and zero managed allocation. Camera replay is `O(V + D)` GPU work for
visible primitives `V` and draw batches `D`, with CPU work bounded independently
of total document entity count after snapshot/index construction. A content
edit recompiles only affected immutable chunks and dependent block instances.

Required fixtures include all supported entity families, large coordinates,
non-world OCS, nested/cyclic blocks and XRefs, layouts/viewports, lineweights,
linetypes, hatches, splines/NURBS, images, dimensions, rich text, TTF/OTF/SHX,
ACIS, corrupt/truncated inputs, and every advertised DXF/DWG version. Tests cover
read/write/read semantic round trips, image comparisons at multiple DPI/zoom,
managed/native differential rendering, selection/snapping, invalidation,
device loss, browser AOT, and bounded-resource fuzzing.

macOS performance claims additionally require matched Release Instruments
Allocations/VM Tracker, Time Profiler, and Metal System Trace captures.

### Reproducible phase-2 CPU baseline

`ProGPU.CAD.Benchmarks` provides JSON p50/p95/p99 and allocation output for
snapshot construction, page-setup catalog capture, retained plan-scene
recording, retained physical print-plan construction in both zero- and
270-degree rotation lanes, and spatial queries. Run:

```bash
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 20000 --draw-order --warmup 3 --iterations 12 --queries 100
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 20000 --draw-order-edit-entities 100 --warmup 3 --iterations 12 --queries 100
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --complex-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --linear-spline-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --nurbs-spline-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --periodic-spline-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 1000 --spline-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --text-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --text-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-mtext-entities 100 --text-selection --warmup 1 --iterations 5 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --block-array-columns 10000 --warmup 5 --iterations 100 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --warmup 5 --iterations 50 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --text-decorations --warmup 3 --iterations 50 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --warmup 3 --iterations 24 --queries 1000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --shx-decorations --warmup 3 --iterations 24 --queries 1000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --solid-hatches 100 --hatch-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --pattern-hatches 100 --hatch-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --pattern-hatches 100 --complex-pattern-grammar --hatch-island-styles --hatch-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --dimension-entities 1000 --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --thick-solid-entities 1000 --warmup 3 --iterations 24 --queries 1000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --mesh-entities 1000 --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --mesh-entities 100 --mesh-subdivision-level 2 --warmup 3 --iterations 24 --queries 1000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --polygon-mesh-entities 1000 --polyface-mesh-entities 1000 --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --point-entities 10000 --warmup 3 --iterations 24 --queries 10000
```

The initial 2026-08-27 Apple Silicon/.NET 10 baseline for 10,000 mixed analytic
entities records snapshot p50/p95/p99 of 17.699/27.464/40.229 ms, plan-scene
recording of 10.555/38.890/43.964 ms, and spatial-query p50/p95/p99 of
2.8/14.9/18.3 microseconds with zero managed allocation per warm query. Snapshot
and scene construction allocate 9,223,164 and 9,280,779 bytes per generation,
respectively. These are transparent starting measurements, not an improvement or
release-acceptance claim. Full representative viewer workloads, GPU counters,
matched managed/native results, and required macOS Instruments traces remain
open gates before performance acceptance.

The 2026-08-29 draw-order feature-cost pair used the same final Apple
Silicon/.NET 10.0.5 Release binary, 20,000 mixed analytic entities, three
warmups, 12 construction iterations, and 100 queries. Both fixtures retained
exactly 20,000 source/visible/expanded entities and 20,000 plan commands with no
unsupported or invalid content. The no-override regeneration lane measured
snapshot p50/p95/p99 `17.2352/60.5667/60.5667 ms` at `18,986,344 B/op`; a
complete reverse-order table measured `23.9463/70.6382/70.6382 ms` at
`21,326,312 B/op`. Plan-scene allocation stayed effectively identical at
`18,560,692` versus `18,560,690 B/op`, confirming that the feature cost is
bounded to snapshot resolution and stable replay does not inherit ordering
work. The noisy construction tails and absent Instruments/GPU trace make this a
transparent cost baseline, not a release-performance claim.

The 2026-08-29 draw-order authoring baseline used the same Apple
Silicon/macOS 26.6/.NET 10.0.5 Release binary with 20,000 model-space entities,
a stable 100-entity selection, three warmups, and 12 iterations. One measured
operation includes canonical Bring to Front plus exact sparse/absent-state Undo,
therefore two locked generations and two table replacements. It measured
p50/p95/p99 `6.8600/56.2667/56.2667 ms` at `2,781,896 B/op`. Every iteration
restored the original no-table state; the fixture still compiled exactly 20,000
source/visible/expanded entities with zero unsupported or invalid content. The
short run has a noisy tail and no Instruments/GPU trace, so this is a transparent
feature-cost baseline rather than a performance-acceptance claim.

The 2026-08-29 persisted-DIMENSION feature-cost run used one final Apple
Silicon/.NET 10.0.5 Release process, 1,000 DIMENSION roots, three warmups, 24
construction iterations, and 10,000 spatial queries. Each picture retained two
lines, two SOLIDs, and one MTEXT entity. The snapshot reported exactly 1,000
source roots, 6,000 expanded records including those roots, 5,000 recorded
entities/commands, and zero unsupported or invalid entities. Snapshot
p50/p95/p99 was `16.2112/109.7964/115.1985 ms` at `12,353,946 B/op`; plan-scene
recording was `5.4038/26.7515/28.4307 ms` at `8,192,984 B/op`; print planning
was `8.9577/26.7981/32.4683 ms` at `9,625,156 B/op`; and spatial-query
p50/p95/p99 was `5.5/9.0/11.1 us` with zero managed allocation. This is a
transparent feature/cost baseline with visibly noisy construction tails, not
an improvement or release-acceptance claim; matched viewer/GPU/native-image and
required Instruments evidence remain open acceptance gates.

Two consecutive 2026-08-29 Release feature-cost runs used the same final
Apple Silicon/.NET 10.0.5 binary, 1,000 standalone thick SOLIDs, three warmups,
24 construction iterations, and 1,000 spatial queries. The fixture alternated
simple and crossed quadrilaterals plus positive and negative thickness, yielding
exactly 1,000 source/expanded/recorded records, 1,000 face ranges and batches,
14,000 shell triangles, and zero unsupported or invalid entities. Snapshot
p50/p95/p99 was `10.840/14.690/23.925 ms` and
`8.901/15.656/18.555 ms`, allocating `789,573` and `789,577 B/op`.
Camera-independent 3D-scene construction was `12.483/20.761/24.729 ms` and
`14.672/24.815/33.531 ms`, allocating `1,760,784` and `1,761,115 B/op`.
Plan-scene construction was `2.239/11.709/17.191 ms` and
`2.296/10.888/28.407 ms`; spatial queries remained zero-allocation at
`3.7/12.4/38.4 us` and `4.2/13.7/57.3 us`. The ignored JSON artifacts are
`artifacts/progpu-cad/cad-thick-solid-run1.json` and
`cad-thick-solid-run2.json`. These are transparent feature-cost baselines, not
improvement or release-acceptance claims; matched viewer pixels, GPU residency
and submission counters, native differential images, and Instruments traces
remain open gates for a future performance claim.

The 2026-08-29 level-0 MESH feature-cost run used one final Apple
Silicon/.NET 10.0.5 Release process, 1,000 tetrahedral MESH roots, three
warmups, 24 construction iterations, and 10,000 spatial queries. Four persisted
triangular faces per source deduplicated to six unique topology edges. The
snapshot reported exactly 1,000 source roots, 7,000 expanded records including
those roots, 6,000 retained commands, and zero unsupported or invalid entities.
Snapshot p50/p95/p99 was `9.7672/74.9136/83.5604 ms` at `5,039,586 B/op`;
plan-scene recording was `2.9977/24.9205/38.4965 ms` at `6,144,691 B/op`;
print planning was `10.5460/38.3424/54.3644 ms` at `9,628,565 B/op`; and warm
spatial-query p50/p95/p99 was `16.0/33.8/74.1 us` with zero managed
allocation. This is a transparent feature/cost baseline with noisy tails and a
deliberately overlapping 3D-to-plan projection, not an improvement or release-
acceptance claim. Indexed edge batches, representative orbit/depth workloads,
GPU counters, managed/native images, and required Instruments traces remain
open gates.

The 2026-08-29 legacy-MESH feature-cost run used one final Apple
Silicon/.NET 10.0.5 Release process, 1,000 open 3-by-3 polygon meshes, 1,000
tetrahedral polyface meshes, three warmups, 24 construction iterations, and
10,000 spatial queries. The snapshot reported exactly 2,000 source roots,
20,000 expanded records including those roots, 18,000 retained commands, and
zero unsupported or invalid entities. Snapshot p50/p95/p99 was
`38.4807/84.0939/106.8618 ms` at `17,573,783 B/op`; plan-scene recording was
`45.6892/86.0196/98.5248 ms` at `18,432,861 B/op`; print planning was
`70.8539/120.4183/134.3322 ms` at `28,877,458 B/op`; and warm spatial-query
p50/p95/p99 was `7.8/78.6/210.8 us` with zero managed allocation. This is a
transparent feature/cost baseline with noisy tails and deliberately overlapping
3D-to-plan projections, not an improvement or release-acceptance claim. Indexed
edge batches, representative orbit/depth workloads, GPU counters,
managed/native images, and required Instruments traces remain open gates.

Two consecutive 2026-08-29 subdivision feature-cost runs used the same final
Apple Silicon/.NET 10.0.5 Release binary, 100 closed tetrahedral modern MESH
roots at authored level two, three warmups, 24 construction iterations, and
1,000 spatial queries. Each source refined to 48 quads, 96 triangles, and 96
unique displayed edges; both runs reported exactly 100 source roots, 9,700
expanded records including the roots, 4,800 face ranges, 9,600 triangles, 100
3D draw batches, and zero unsupported or invalid entities. Snapshot
p50/p95/p99 was `26.669/55.135/60.927 ms` and
`32.198/45.421/62.398 ms`, allocating `32,082,980` and `32,125,010 B/op`.
Camera-independent 3D-scene construction was `0.507/9.124/13.513 ms` and
`0.851/7.161/10.541 ms`, allocating `1,062,376 B/op` in both runs.
Plan-wire construction was `14.410/34.380/34.821 ms` and
`12.154/35.525/43.172 ms`; warm spatial queries remained zero-allocation at
`39.6/136.8/155.0 us` and `50.7/155.2/212.2 us`. The ignored JSON artifacts
are `artifacts/progpu-cad/mesh-subdivision-level2-run1.json` and
`mesh-subdivision-level2-run2.json`. These measurements expose current
feature cost and noisy construction tails; they make no latency improvement or
regression claim and do not replace matched viewer pixels, GPU residency and
submission counters, native differential images, or required Instruments
captures for a future optimization claim.

The 2026-08-29 retained-POINT feature-cost run used one final Apple
Silicon/.NET 10.0.5 Release process, 10,000 PDMODE-zero POINT entities, three
warmups, 24 construction iterations, and 10,000 spatial queries. The snapshot
reported exactly 10,000 source/expanded records, 10,000 retained point-batch
commands, and zero unsupported or invalid entities. Snapshot p50/p95/p99 was
`15.4765/96.0577/98.3715 ms` at `5,162,431 B/op`; plan-scene recording was
`2.5843/5.8061/6.6926 ms` at `6,023,216 B/op`; print planning was
`6.4834/21.7627/25.2050 ms` at `11,987,292 B/op`; and warm spatial-query
p50/p95/p99 was `3.8/7.1/17.2 us` with zero managed allocation. This is a
transparent feature-cost baseline with noisy construction tails, not an
improvement or release-acceptance claim. Point-command coalescing,
representative viewport interaction, GPU counters, managed/native images, and
required Instruments traces remain open gates.

The 2026-08-29 compound-POINT feature-cost checkpoint used two separate final
Apple Silicon/.NET 10.0.5 Release processes, 10,000 `PDMODE=98`, `PDSIZE=-5`
POINT entities with rotating group-50 angles, a 1,080-pixel marker viewport at
0.25 model unit per pixel, three warmups, 24 construction iterations, and
10,000 spatial queries. Both runs reported exactly 10,000 source/expanded
records, zero unsupported/invalid entities, and one contiguous-style retained
marker command containing 50,000 exact figures. Snapshot p50/p95/p99 was
`12.9299/37.0579/37.3837 ms` and `15.2497/44.5245/47.3621 ms`, allocating
`7,899,914` and `7,900,452 B/op`. Marker regeneration was
`21.7753/39.7722/44.5901 ms` and `20.6250/40.8564/43.1821 ms`, allocating
`9,372,445 B/op` in both runs. Print planning, which includes the same overlay,
was `25.6968/43.2771/53.4240 ms` and `15.4630/33.3415/35.3602 ms`; warm spatial
queries remained zero-allocation. The ignored artifacts are
`artifacts/progpu-cad/cad-point-marker-run2.json` and
`cad-point-marker-run3.json`. This exposes feature cost and noisy process-level
tails; it makes no latency improvement or regression claim and does not replace
interactive multi-DPI pixels, GPU residency/submission counters, native
differential images, or Instruments captures for a future optimization claim.

The 2026-08-29 construction-geometry feature-cost run used one final Apple
Silicon/.NET 10.0.5 Release process, 5,000 RAY plus 5,000 XLINE entities, one
explicit `[-100,-100]` through `[12100,500]` plan/plot window, three warmups,
24 construction iterations, and 10,000 caller-buffered selection queries. The
snapshot reported exactly 10,000 source/expanded records, zero finite BVH
nodes, and zero unsupported or invalid entities. Source-order style batching
retained all 10,000 clipped primitives as one analytic path command. Snapshot
p50/p95/p99 was `10.6186/17.5711/18.0285 ms` at `4,217,905 B/op`; the ordinary
finite plan compiler was `0.0355/0.0425/0.2000 ms` at `832 B/op`; construction-
overlay compilation was `3.1109/8.9830/9.1115 ms` at `2,026,104 B/op`; print
planning was `3.0830/15.0855/17.0833 ms` at `2,030,620 B/op`; and the explicit
`O(U)` unbounded broad-phase query was `31.9/90.1/167.8 us` with zero managed
allocation. This is a reproducible feature/cost baseline, not an improvement
or release-acceptance claim. Representative interactive traces, managed/native
images, GPU counters, non-continuous construction-linetype phase work, and the
required matched Instruments captures remain open acceptance gates.

The 2026-08-28 simple-linetype feature-cost baseline used one final Release
binary in two sequential processes, 1,000 mixed analytic entities, five
warmups, and 30 measured iterations. Continuous input recorded 1,000 commands;
the `[3, -1.5, 0, -1.5]` patterned input retained the same 1,000 commands and
lowered 6,250 dash/dot figures from 1,250 source segments through 10,250 bounded
descriptor steps. Snapshot p50/p95/p99 was 4.453/13.939/19.988 ms with 805,411
managed bytes for continuous input and 4.519/9.071/9.734 ms with 805,594 bytes
for patterned input. Plan-scene recording was 0.656/4.819/6.507 ms and 928,552
bytes versus 10.354/19.437/23.926 ms and 2,054,747 bytes. These numbers expose
the bounded retained-geometry construction cost; sequential-process, JIT, GC,
and system noise preclude a comparative regression or improvement claim. The
result does not replace full viewer, GPU, image-quality, managed/native differential,
or matched Instruments gates.

The first 2026-08-28 complex-linetype feature-cost run used one Release binary,
1,000 mixed analytic entities, two warmups, and eight iterations. One shared
`"GAS"` TrueType definition produced 3,500 stroke figures and 2,750 placements
from 1,250 source segments through 10,250 bounded descriptor visits, recording
3,750 commands with no unsupported entities or linetypes. Snapshot p50/p95/p99
was 6.281/16.293/16.293 ms with 807,640 managed bytes; plan-scene recording was
15.650/30.777/30.777 ms with 3,747,295 managed bytes. This short run validates
the benchmark lane and exposes feature cost only. It is not a comparative
performance claim and does not replace matched viewer/GPU, image-quality,
managed/native, or Instruments evidence.

The first 2026-08-28 open linear-SPLINE linetype feature-cost run used one
Release binary, 1,000 rational degree-one splines with two control edges each,
two warmups, and eight iterations. It retained 1,000 commands and 7,000 figures
through 11,000 descriptor visits and 2,000 source segments with no unsupported
entities or linetypes. Snapshot p50/p95/p99 was 4.550/10.419/10.419 ms with
1,121,304 managed bytes; plan-scene recording was 3.407/14.998/14.998 ms with
2,168,602 managed bytes. This short run validates the exact-subset benchmark
lane and exposes feature cost only. It is not a comparison or performance claim
and does not replace viewer/GPU, image-quality, managed/native, or Instruments
evidence.

The first 2026-08-28 open weighted quadratic-NURBS linetype feature-cost run
used one final Release binary, 1,000 three-span splines, two warmups, and eight
iterations. It retained 5,000 exact rational spline commands through 7,000
bounded descriptor visits and 3,000 source spans with no unsupported entities
or linetypes. Snapshot p50/p95/p99 was 5.042/11.926/11.926 ms with 1,467,776
managed bytes; plan-scene recording was 86.645/99.421/99.421 ms with 10,205,156
managed bytes. This short run validates the dedicated weighted multi-span lane
and exposes feature cost only. It is not a comparison or performance claim and
does not replace matched viewer/GPU, image-quality, managed/native, or
Instruments evidence.

The first 2026-08-28 Normal solid-HATCH feature-cost run used one final Release
binary, 100 hatches with one square outer loop and one square island, three
warmups, 24 construction iterations, and 10,000 direct immutable-candidate
queries. It retained 100 commands for 100 entities with no unsupported or
invalid input. Snapshot p50/p95/p99 was 0.746/2.290/3.836 ms with 611,608
managed bytes; plan recording was 0.091/0.097/0.151 ms with 139,880 bytes.
Point-selection p50/p95/p99 was 1.8/3.9/4.1 microseconds and alternating
Crossing/Window selection was 2.1/4.9/5.2 microseconds, both with zero managed
allocation per warm query. This is a standalone feature-cost baseline, not a
before/after performance claim, and does not replace viewer/GPU, image-quality,
managed/native differential, or required Instruments evidence.

The first 2026-08-28 continuous patterned-HATCH feature-cost run used one final
Release binary, 100 single-family hatches with one square outer loop and one
square island, three warmups, 24 construction iterations, and 10,000 direct
immutable-candidate queries. It retained 100 commands and 100 pattern records
with no unsupported or invalid input. Snapshot p50/p95/p99 was
1.764/4.546/5.894 ms with 628,712 managed bytes; plan recording was
0.218/0.426/0.869 ms with 151,080 bytes. Point-selection p50/p95/p99 was
3.5/7.9/17.6 microseconds and alternating typed-unsupported Crossing/exact
Window selection was 0.2/0.5/0.6 microseconds, both with zero managed
allocation per warm query. This is a standalone feature-cost baseline, not a
before/after performance claim, and does not replace matched GPU/image-quality,
managed/native, or required Instruments evidence.

The first 2026-08-28 complete bounded pattern-grammar feature-cost run used one
final Release binary, 100 two-family HATCHes with positive dashes, negative
gaps, zero dots, tangent row shifts, one square outer loop, and one square
island. Three warmups, 24 construction iterations, and 10,000 direct immutable-
candidate queries retained 100 commands, 200 families, and 600 dash items with
no unsupported or invalid input. Snapshot p50/p95/p99 was
0.823/2.615/2.641 ms with 715,072 managed bytes; plan recording was
0.137/0.166/0.868 ms with 180,688 bytes. Point-selection p50/p95/p99 was
4.0/5.1/5.3 microseconds and alternating typed-unsupported Crossing/exact
Window selection was 0.2/0.3/0.3 microseconds, both with zero managed allocation
per warm query. This is a standalone feature-cost baseline, not a before/after
performance claim, and does not replace matched GPU/image-quality,
managed/native, or required Instruments evidence.

Two sequential 2026-08-28 island-style feature-cost runs used the same final
Release binary with 100 alternating Outer/Ignore patterned HATCHes, three nested
square loops per entity, 200 procedural families, 600 dash items, three warmups,
24 construction iterations, and 10,000 direct immutable-candidate queries.
Both retained 100 commands with no unsupported or invalid input. Snapshot
p50/p95/p99 was 2.7805/4.0557/4.6995 and 1.4276/2.3745/3.3648 ms with 1,106,576
managed bytes per snapshot in both runs; plan recording was
0.3466/0.7514/5.9948 and 0.1288/0.2929/0.8586 ms with 167,088 bytes. Point
selection was 3.6/4.6/27.2 and 1.7/4.3/8.5 microseconds; alternating typed-
unsupported Crossing/exact Window selection was 0.4/0.5/0.7 and
0.2/0.5/0.6 microseconds. Both warm query lanes allocated zero managed bytes.
The process-order and tail spread preclude a latency comparison claim; these
runs establish bounded feature cost and do not replace matched GPU/image-
quality, managed/native, or required Instruments evidence.

Two sequential 2026-08-28 polynomial spline-edge feature-cost runs used the
same final Release binary with 100 two-family patterned HATCHes, one exact cubic
spline cap plus closing line and one square island per entity, three warmups, 24
construction iterations, and 10,000 direct immutable-candidate queries. The
command used `--entities 0 --pattern-hatches 100 --complex-pattern-grammar
--hatch-spline-edges --hatch-selection`. Both runs retained 100 commands with no
unsupported or invalid input. Snapshot p50/p95/p99 was
1.4743/2.7308/3.2409 and 1.0174/2.0711/3.1591 ms with 640,112 managed bytes per
snapshot; plan recording was 0.1333/0.8222/1.0487 and
0.1311/0.2090/0.5217 ms with 175,888 bytes. Pattern-clipped point selection was
9.1/95.2/147.0 and 8.8/64.3/130.8 microseconds; alternating typed-unsupported
Crossing/exact Window selection was 0.1/0.1/0.1 and 0.1/0.2/0.2 microseconds.
Both warm query lanes allocated zero managed bytes. The tail spread precludes a
latency comparison claim; these runs establish bounded exact-subset feature cost
and do not replace matched GPU/image-quality, managed/native, or required
Instruments evidence.

Two paired 2026-08-28 rational-quadratic feature-cost runs used the same final
Release binaries and command as the polynomial spline-edge lane, adding
`--rational-hatch-spline-edges` only for the feature runs. Each corpus contained
100 two-family patterned HATCHes, three warmups, 24 construction iterations,
and 10,000 immutable-candidate queries. Polynomial snapshot p50/p95/p99 was
1.5458/3.3926/6.3392 and 1.1540/3.7527/5.1504 ms; rational-quadratic snapshot
cost was 1.7892/7.7386/10.3148 and 1.3755/4.2481/6.3912 ms. Both allocated
666,752 bytes per snapshot. Polynomial plan recording was
0.1279/0.1912/1.7774 and 0.2325/0.5750/1.3784 ms with 175,888 bytes; rational
recording was 0.1600/0.5305/1.0783 and 0.2110/0.5546/1.1884 ms with 175,088
bytes. Polynomial point selection was 10.9/96.6/152.6 and
9.7/97.6/145.1 microseconds; rational selection was 12.9/86.0/153.8 and
13.0/91.5/164.4 microseconds. Crossing/Window query medians remained 0.1
microseconds and all warm query lanes allocated zero bytes. The run-to-run tail
spread precludes a latency improvement or regression claim; this is bounded
feature-cost evidence and does not replace GPU image-quality differentials or
Instruments profiling for a future optimization claim.

Two paired 2026-08-28 rational-cubic feature-cost runs used one final Release
binary and the same 100 two-family patterned-HATCH corpus as the polynomial
cubic lane, adding `--rational-cubic-hatch-spline-edges` only for the feature
runs. Each run used three warmups, 24 construction iterations, and 10,000
immutable-candidate queries. Polynomial snapshot p50/p95/p99 was
1.7210/3.3844/7.1344 and 2.7752/6.9500/10.3589 ms; rational-cubic snapshot
cost was 7.2749/13.7047/15.2495 and 9.0320/11.8265/11.9227 ms. Both allocated
693,392 managed bytes per snapshot. Polynomial plan recording was
0.2524/1.3638/1.3694 and 0.1532/0.5551/1.0765 ms with 175,888 bytes;
rational-cubic recording was 0.2197/0.5055/1.4236 and
0.1749/0.6341/2.1191 ms with 176,688 bytes. Polynomial point selection was
16.7/109.2/246.0 and 16.6/102.0/204.6 microseconds; rational-cubic selection
was 16.3/95.6/142.0 and 16.5/114.7/261.5 microseconds. Crossing/Window p50 was
0.1 microseconds for both, and every warm query lane allocated zero managed
bytes. Exact quartic Bernstein extrema isolation raises immutable snapshot
construction cost without changing retained command count or query allocation;
the run-to-run tails preclude a broader latency claim. These measurements do
not replace managed/native GPU image-quality differentials or Instruments
profiling for a future optimization claim.

The first 2026-08-28 weighted periodic quadratic-NURBS linetype feature-cost
run used one final Release binary, 1,000 four-span loops, three warmups, and 24
iterations. It retained 4,083 exact rational spline commands through 8,083
bounded descriptor visits and 4,000 source spans with no unsupported entities
or linetypes. Snapshot p50/p95/p99 was 0.548/9.266/11.819 ms with 1,226,744
managed bytes; plan-scene recording was 133.575/142.936/174.639 ms with
11,005,545 managed bytes. This run validates compact/expanded cyclic lowering,
closed-pattern traversal, and the dedicated resource-accounting lane; it is a
feature-cost result only, not a comparison or performance claim, and does not
replace matched viewer/GPU, image-quality, managed/native, or Instruments
evidence.

The first 2026-08-28 exact spline-selection feature-cost run used one final
Release binary, 1,000 open weighted quadratic NURBS with three non-empty spans,
three warmups, 24 construction iterations, and 10,000 direct immutable-candidate
queries. Point-selection p50/p95/p99 was 12.8/79.5/97.0 microseconds; alternating
Crossing/Window box selection was 14.0/18.2/27.9 microseconds. Both paths
reported zero managed allocation per warm query. Snapshot p50/p95/p99 was
4.735/6.252/12.203 ms with 1,467,941 managed bytes, and the unchanged retained
scene recorded 1,000 commands. These measurements expose feature cost and the
long-tail root-isolation workload only; they are not a before/after improvement
claim and do not replace representative viewer interaction, quality comparison,
managed/native rendering, GPU counters, or required Instruments evidence.

The first 2026-08-28 retained TEXT-selection feature-cost runs used the final
Release binary, 1,000 entities, three warmups, 24 construction iterations, and
10,000 direct immutable-candidate queries. The TrueType fixture retained 20
shaped glyphs per entity; point-selection p50/p95/p99 was 435/496/750
microseconds and alternating Crossing/Window selection was 191/325/338
microseconds. The SHX fixture retained eight analytic glyph instances per
entity; point-selection p50/p95/p99 was 63.1/72.0/76.5 microseconds and box
selection was 23.3/29.5/32.6 microseconds. All four warm query lanes reported
zero managed allocation. TrueType snapshot p50/p95/p99 was
11.655/86.502/102.837 ms with 4,371,169 managed bytes; SHX snapshot cost was
7.147/11.971/12.220 ms with 1,905,049 bytes. These figures expose exact-outline
feature cost—including the TrueType per-segment stationary-root workload—and
are not a before/after improvement claim. They do not replace representative
viewer interaction, image-quality comparison, managed/native rendering, GPU
counters, or required Instruments evidence.

The first two fresh 24-iteration Release runs of the 10,000-entity physical
print-plan path measured p50/p95/p99 at 16.320/49.488/58.868 ms and
18.680/46.709/63.583 ms, with 12,804,279 and 12,804,501 managed bytes per plan.
Each result includes plottable-bounds traversal, filtered retained-scene
recording at 300 DPI, physical pen creation, and the owned content picture, but
does not allocate the eventual raster target. The noisy snapshot/scene tails in
the same processes and the absence of a prior print implementation make these
feature baselines only; they are not an improvement or release-acceptance claim.

Two sequential 24-iteration Release runs of the new 270-degree page-rotation
lane measured p50/p95/p99 at 21.852/66.560/71.728 ms and
22.781/68.440/99.557 ms, with 12,804,577 and 12,804,706 managed bytes per plan.
The matching zero-degree lanes in those same processes measured
15.004/42.328/50.887 ms and 20.200/84.581/140.668 ms. Each plan still rebuilds
the complete 10,000-command retained print scene, so these noisy measurements
are a feature-cost baseline only; they neither isolate the fixed-work rotation
math nor support a regression or improvement claim. Page rotation itself adds
only bounded integer edge permutation and one affine matrix composition.

The same two fresh 24-iteration Release processes measured the default
two-layout page-setup catalog at p50/p95/p99 of 0.0012/0.0104/0.0550 ms and
0.0014/0.0160/0.0752 ms, allocating 2,120 managed bytes per immutable catalog in
both runs. Catalog cost depends on layout/setup count and copied text, not entity
count; the 10,000-entity fixture is retained only to keep the surrounding harness
comparable. These are feature/cost baselines with no improvement claim and do
not replace large-layout, corrupt-input, desktop/browser, or full output tests.

The MINSERT mode creates one block reference whose single line expands across
the requested number of columns. Two consecutive 100-iteration Release runs at
10,000 cells measured snapshot p50/p95/p99 of
4.856/13.501/15.956 ms and 5.258/9.713/11.030 ms, with
8,410,515 and 8,410,267 managed bytes per generation. Retained plan recording
measured 11.462/38.207/41.159 ms and 10.727/37.593/45.143 ms, with
10,240,521 and 10,240,498 bytes per generation. The snapshot reported one
source INSERT, 10,001 expanded entities including the root, and 10,000 recorded
commands. Alternating 10,000 ordinary-entity controls were intentionally kept
alongside these runs; graph shape and spatial distribution differ, so the data
is a feature baseline and makes no relative speed or regression claim.

The TEXT mode creates 1,000 twenty-one-character TrueType entities backed by one
embedded Inter face. Two consecutive 50-iteration Release runs measured
snapshot p50/p95/p99 of 15.412/34.109/48.727 ms and
12.850/36.243/40.096 ms, with 4,288,949 and 4,297,244 managed bytes per
generation. Retained plan recording emitted 1,000 glyph-run commands and
measured 0.199/1.997/12.281 ms and 0.338/1.955/11.911 ms, with 576,810 and
576,966 managed bytes per generation. Warm spatial queries remained zero-
allocation. These measurements establish the first feature baseline only; they
make no speedup claim and do not replace the matched viewer, GPU, native, or
Instruments acceptance gates.

The optional decoration mode keeps the same decoded twenty-one characters but
wraps its three logical fields in underline, overline, and strike-through
toggles. Two consecutive 50-iteration Release runs produced 3,000 merged
decoration rectangles and 4,000 retained commands. Snapshot p50/p95/p99 was
11.345/82.567/99.794 ms and 11.598/82.011/98.052 ms, with 5,428,388 and
5,431,835 managed bytes per generation. Plan recording measured
0.770/4.363/10.838 ms and 0.854/4.446/11.553 ms, with 2,305,072 and 2,304,948
managed bytes per generation. Matched undecorated runs after the new fixed
primitive fields retained 1,000 commands and measured snapshot p50 of
12.831/11.355 ms with 4,304,227/4,311,072 bytes, and plan p50 of
0.406/0.335 ms with 576,892/576,968 bytes. Tail timings were visibly noisy;
these are feature/cost baselines with no latency improvement or regression
claim. Warm queries remained zero-allocation.

The standard SHX TEXT mode creates 1,000 eight-character entities backed by one
cached analytic glyph. Its preflight rejects the run if any requested fixture
entity is unsupported or invalid. Two consecutive 24-iteration Release runs
retained all 1,000 entities and emitted 8,000 shared-path commands. Snapshot
p50/p95/p99 was 7.805/13.825/14.920 ms and 7.170/9.546/12.164 ms, with
1,839,947 and 1,840,014 managed bytes per generation. Plan recording measured
4.807/16.765/17.074 ms and 4.746/18.753/27.931 ms, with 10,816,601 and
10,816,564 managed bytes per generation. Warm spatial queries allocated zero
managed bytes. These are feature/cost baselines with visibly variable tail
latency, no improvement or regression claim, and no substitute for matched
viewer, GPU, native-image, or Instruments acceptance evidence.

Two consecutive 2026-08-29 Release feature-cost runs used one final binary,
1,000 tetrahedral level-zero MESH entities, three warmups, 24 construction
iterations, and 1,000 spatial queries. Both retained 1,000 source meshes, 4,000
face ranges/triangles, and 1,000 consecutive same-style 3D batches with no
unsupported or invalid entities. Snapshot p50/p95/p99 was
16.985/34.609/35.916 ms and 12.623/22.795/26.282 ms, allocating
11,779,869 and 11,736,814 managed bytes per generation. Camera-independent 3D
scene compilation measured 0.377/8.783/15.470 ms and
0.208/9.726/13.277 ms, allocating 680,776 and 680,996 bytes. Plan wire-scene
p50/p95/p99 was 8.474/30.133/35.024 ms and 6.965/28.466/34.755 ms. The JSON
artifacts are `artifacts/progpu-cad/mesh3d-feature-run1.json` and
`mesh3d-feature-run2.json`. Variable tails preclude a latency or regression
claim; this is the first feature-cost baseline and does not replace image
quality, GPU residency/submission counters, native differential pixels, or the
required Instruments capture for a future optimization claim.

Two consecutive 2026-08-29 Release feature-cost runs used one final binary,
1,000 persisted DIMENSION roots, three warmups, 24 construction iterations,
and 1,000 spatial queries. The persisted pictures contributed exactly 2,000
triangular SOLID arrowheads, which compiled to 2,000 source faces, face ranges,
triangles, and 1,000 same-root/style draw batches with no unsupported or invalid
entities.
Camera-independent 3D scene p50/p95/p99 was
`0.363/4.909/8.999 ms` and `0.364/7.136/9.192 ms`, allocating
`464,785` bytes per generation in both runs. Snapshot p50/p95/p99 was
`14.590/102.415/161.786 ms` and `19.146/191.191/247.491 ms`; plan replay was
`7.379/18.078/20.540 ms` and `4.694/31.247/31.648 ms`. The JSON artifacts are
`artifacts/progpu-cad/cad-face-surface-run1.json` and
`cad-face-surface-run2.json`. Variable construction tails and the mixed
DIMENSION/text workload preclude a latency or regression claim; this is a
feature-cost baseline and does not replace matched viewer pixels, GPU
residency/submission counters, native differential images, or the required
Instruments capture for a future optimization claim.

Run the standalone samples with:

```bash
dotnet run --project src/ProGPU.CAD.Sample.Desktop -c Release -f net10.0
dotnet run --project src/ProGPU.CAD.Sample.Browser -c Debug
```

## Delivery phases

1. Foundation: pinned ACadSharp submodule, project/package boundary, typed IO,
   document sessions, transactions/generations, diagnostics, architecture, and
   unit tests.
2. Immutable scene: ACadSharp entity adapters, coordinate normalization,
   spatial index, layer/style resolution, retained ProGPU scene compilation,
   and managed/native differential baselines.
3. Viewer: model/layout views, selection/snapping, overlays, fast pan/zoom/orbit,
   desktop and browser sample hosts, device recovery, and performance harnesses.
4. Fidelity: every 2D/3D entity, blocks/XRefs, hatches, images/underlays,
   dimensions, linetypes, plot styles, ACIS, and full TTF/OTF/SHX text.
5. Editing: typed tools/commands, properties, undo/redo, clipboard, constraints,
   background incremental compilation, scripting, and collaboration seams.
6. Output: version-gated DXF/DWG save, round-trip certification, print preview,
   physical printing, vector/raster export, and browser download flows.

## Primary research record

Sources consulted on 2026-08-27 through 2026-08-29:

- For persisted compositing order, Autodesk's
  [SORTENTSTABLE DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-462F4378-F850-4E89-90F2-3C1880F55779.htm),
  [DRAWORDER contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT/files/GUID-3DC76D6E-8F81-4803-8D0A-AA7541D6357E.htm), and
  [SORTENTS contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-56B7D915-515B-4A9C-BCB5-EF2D43C05FE5.htm)
  establish sparse entity/sort-handle pairs, ascending regeneration order,
  complete-object-set front/back/above/under operations, modern always-sorted
  REGEN, and the remaining Plotting bit. ProGPU adopts persisted ascending order,
  nested block-local tables, and explicit plot policy; it adapts them into one
  bounded immutable CPU snapshot step and rejects malformed ownership rather
  than copying an implementation. Skia's
  [ordered canvas/picture model](https://skia.org/docs/user/api/), Direct2D's
  [retained command-list model](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist), Win2D's
  [CanvasCommandList drawing-session model](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/quick-start), WebRender's
  [display-list/stacking-context architecture](https://github.com/servo/servo/wiki/Webrender-Overview), and Vello's
  [append-only scene encoding](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  confirm that semantic paint order is resolved before replay and survives
  backend batching. ProGPU adopts that separation and rejects GPU sorting because
  transparent CAD compositing is order-sensitive. Skia/SkParagraph's
  [shape-then-render split](https://docs.skia.org/docs/dev/design/text_shaper/),
  DirectWrite's
  [layout/render separation](https://learn.microsoft.com/en-us/windows/win32/directwrite/programming-guide),
  Parley's [itemization and shaping pipeline](https://github.com/linebender/parley/blob/main/doc/concept.md), and
  HarfBuzz's [positioned-glyph contract](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
  were examined and rejected as draw-order owners: retained glyph runs stay
  reusable and are ordered only as already compiled entity commands. The change
  does not alter startup/font discovery, shaping/layout reuse, visibility policy,
  glyph/path/texture keys or eviction, demand upload, worker preparation, DPI or
  subpixel behavior, fallback/variable-font state, or device-loss invalidation.
  Autodesk's older but still explicit
  [DRAWORDER multi-selection contract](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-MAC-Core/files/GUID-3DC76D6E-8F81-4803-8D0A-AA7541D6357E.htm)
  requires the relative draw order of multiple selected objects to remain
  unchanged, while the current
  [overlap-order workflow](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-D3009A24-3357-49E4-96BB-066D5A2CC25F.htm)
  places a selection immediately above or under its plural reference objects,
  using a second reference-selection stage before confirmation, and limits
  ordering to one model/paper space. ProGPU adopts those observable semantics as
  an original stable partition/insertion algorithm, canonical transactional
  persistence rewrite, and shared exact semantic-root prompt; it does not copy
  ACadSharp convenience-move or foreign UI control flow.

- For atomic selected-object deletion, Autodesk's
  [ERASE command contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-MAC-Core/files/GUID-6FC4AA9A-A3DC-49D0-A22B-CC0ED48C1310.htm),
  [selection-set erase contract](https://help.autodesk.com/cloudhelp/2026/DEU/AutoCAD-LT-ActiveX-Reference/files/GUID-54A5C5A5-5156-4470-A057-12C222CE3DAE.htm), and
  [SELECT contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT-MAC/files/GUID-0DD5DA73-9DC5-4424-8FED-7BBE3BE52A4D.htm)
  establish that ERASE/DELETE acts on the complete selected object set and that
  deleting objects clears the prior selection set. ProGPU adopts selection-set
  erase, Delete-key access, cleared stale selection handles, and one reversible
  history action. It adapts those observable contracts to its locked generation
  transaction and an original preflighted ACadSharp collection primitive rather
  than copying an implementation. Subobject erase, ALL/Last/Previous command-line
  modes, locked-layer policy, draw-order restoration, and paper-space selection
  remain separate future tool contracts. The slice changes only mutable
  ACadSharp ownership and whole-generation publication: both managed and native
  renderers consume the same next immutable snapshot, so no paired native edit
  frontend, C ABI, shader, cache, upload, text, DPI/subpixel, or device-loss
  change applies. Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender,
  Vello/Parley, and HarfBuzz were reviewed for applicability and rejected because
  no rendering, scene-command, shaping, layout, font, or GPU algorithm changes.

- For POINT display, Autodesk's
  [POINT DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-9C6AD32D-769D-4213-85A4-CA9CCB5C5317.htm)
  establishes WCS location, thickness, normal, and rotation fields;
  [PDMODE](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-Core/files/GUID-82F9BB52-D026-4D6A-ABA6-BF29641F459B.htm),
  [PDSIZE](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-826CA91D-704B-400B-B784-7FCC9619AFB9.htm), and Autodesk's
  [point-style guidance](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DidYouKnow/files/GUID-1823FF63-6952-47DD-89F8-29E4AA5B2582.htm)
  define the marker bit combinations, hidden mode, absolute/viewport-relative
  sizing, and the mode-zero single-pixel dot. Adopted exact WCS placement,
  rotated OCS marker axes, hidden mode, single-device-pixel mode-zero coverage,
  every documented plus/X/tick/circle/square combination, five-percent zero
  size, absolute positive size, and viewport-relative negative size. Rejected
  treating the WCS location as OCS, inventing a model-space radius before a
  view exists, flattening circles, or accepting undocumented bit patterns. The
  same contract applies to Autodesk's documented one-index PFACE point records.
  [Skia `drawPoints`](https://api.skia.org/classSkCanvas.html),
  [Direct2D `DrawEllipse`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1rendertarget-drawellipse),
  [Win2D `DrawCircle`](https://microsoft.github.io/Win2D/WinUI3/html/Overload_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawCircle.htm),
  [WebRender's retained pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst), and
  [Vello's retained scene](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  informed keeping point coverage analytic, explicitly transformed, retained,
  and shared across consumers. ProGPU adapts those public contracts to its
  existing original point, path, line, and ellipse commands; contiguous styles
  coalesce while exact affine-sheared ellipses remain unflattened. No foreign
  source or implementation structure was copied.
  SkParagraph, DirectWrite text layout, Parley, and HarfBuzz were rechecked and
  are not applicable because POINT changes no shaping, line layout, font
  fallback, glyph cache, variable-font, DPI/subpixel text, or text device-loss
  behavior. Managed/native applicability was audited explicitly: both renderers
  already consume the same canonical point, stroke, path, and ellipse ABI; the
  managed viewer/print compilers and native picture compiler now resolve the
  same retained overlay, with matched regressions and no C ABI, shader,
  generated binding, per-marker P/Invoke, or per-frame crossing change.

- For unbounded construction geometry, Autodesk's
  [RAY DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-638B9F01-5D86-408E-A2DE-FA5D6ADBD415.htm)
  and
  [XLINE DXF contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-55080553-34B6-40AA-9EE2-3F3A3A2A5C0A.htm)
  establish the WCS base point and unit WCS direction vector. Autodesk's
  [XLINE query guidance](https://help.autodesk.com/cloudhelp/2018/CHT/AutoCAD-ActiveX/files/GUID-D861D37D-15F5-4926-A171-F238D7AC3681.htm)
  confirms that the persisted object is a base point plus direction rather
  than two finite endpoints. Adopted those exact parametric records and their
  one-sided/two-sided domains; rejected finite surrogate bounds, arbitrary far
  endpoints, and inclusion in drawing extents.
  [Skia `drawLine`](https://api.skia.org/classSkCanvas.html) applies the active
  transform and clip to a finite segment;
  [Direct2D axis-aligned clips](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to-clip-with-axis-aligned-rects)
  and [Win2D drawing-session layers](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_CanvasDrawingSession.htm)
  establish explicit bounded clipping and batched drawing;
  [WebRender's retained pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  separates retained display data from viewport/frame work; and
  [Vello's retained scene clip API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  keeps clips and transformed path data explicit. ProGPU adapted those public
  contracts into a viewport-specific retained overlay, source-order contiguous
  style batching, and its existing path/point commands. It did not copy foreign
  implementation text, organization, helpers, or control flow. The old
  `ProGPU.Dxf` ACIS readers and ACIS shader were inspected only while selecting
  the checkpoint and were rejected as unrelated, incomplete sources; no code
  was ported from them.
  [SkParagraph's shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite architecture](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
  and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  were rechecked and are not applicable: the slice changes no shaping/layout
  reuse, glyph/font cache key or eviction, fallback/variation state,
  DPI/subpixel text placement, upload, or text device-loss invalidation.
  Startup remains lazy; finite scenes remain reusable across camera changes;
  only the `O(U)` construction overlay is rebuilt on viewport change; ordinary
  visibility, resource ownership, worker eligibility, GPU submission, cache
  generation, and device-loss contracts are unchanged. Managed/native parity
  was audited explicitly: the native tree has no ACadSharp frontend, while its
  existing picture compiler consumes the exact same retained path and point
  streams under a matched native-compilation regression. No shader, stable C
  ABI, generated binding, or renderer-specific semantic fork applies.

- For legacy polygon/polyface meshes, Autodesk's
  [POLYLINE DXF contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-ABF6B778-BE20-4B49-9B58-A94E64CEFFF3.htm)
  defines independent M/N closure flags, M/N counts, polygon/polyface type bits,
  and fitted-surface kinds. The
  [VERTEX DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-0741E831-599E-4CBF-91E1-8ADBCFD6556D.htm)
  establishes WCS storage for 3D vertices plus signed one-based face indices,
  zero termination, and negative-index hidden edges; Autodesk's
  [polyface-mesh guidance](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-96B6288E-F413-46C0-968A-A314171C0AAE.htm),
  [PFACE command contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-4B0667EF-D8E3-4BD2-A3EE-06CFF164A0A6.htm), and
  [M-vertex count contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-ActiveX-Reference/files/GUID-BA12991B-6400-46A0-B761-A2D449E518A6.htm)
  confirm face-specific properties, two-index line records, and grid dimensions.
  Adopted exact persisted grid/face topology, WCS transforms, hidden-edge
  direction, and face style identity; rejected fitted-control-cage display,
  index repair, synthetic fill, and style-blind edge collapse.
  [Skia triangle meshes](https://api.skia.org/classSkCanvas.html),
  [Direct2D `ID2D1Mesh`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1mesh),
  [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm),
  [WebRender's retained pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  [Vello's retained scene](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), and
  [WebGPU primitive topology](https://gpuweb.github.io/gpuweb/#enumdef-gpuprimitivetopology)
  informed immutable topology retention, culling, device ownership, and the
  future indexed-edge batching seam. Text-engine sources below—SkParagraph,
  DirectWrite/Win2D text, Parley, and HarfBuzz—were rechecked and are not
  applicable because this slice changes no shaping, layout, glyph/font cache,
  DPI/subpixel, fallback, or text device-loss behavior. No foreign source text,
  structure, or tables were used.

- For exact level-0 MESH wireframes, Autodesk's
  [MESH DXF contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-4B9ADA67-87C8-4673-A579-6E4C76FF7025.htm)
  establishes the subdivision level, level-0 vertex/face lists, explicit edge
  and crease records, and property overrides. Adopted the authoritative
  persisted level-0 topology and explicit subdivision boundary; rejected
  synthesizing a smooth surface, rendering a nonzero-subdivision control cage,
  and silently repairing invalid indices. Autodesk's
  [polyface-mesh guidance](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-96B6288E-F413-46C0-968A-A314171C0AAE.htm)
  was consulted for the distinct legacy face-record/invisible-edge model but
  not applied to modern MESH; legacy polyface and polygon meshes remain separate
  contracts.
  [Skia triangle meshes](https://api.skia.org/classSkCanvas.html),
  [Direct2D `ID2D1Mesh`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1mesh),
  [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm),
  [WebRender's display-list/scene pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  [Vello's retained scene](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), and the
  [WebGPU primitive-topology contract](https://gpuweb.github.io/gpuweb/#enumdef-gpuprimitivetopology)
  informed immutable topology retention, culling, device ownership, and future
  indexed line-list batching. Backend-specific triangle resources were rejected
  for this checkpoint because they are 2D/device-owned or would require the
  not-yet-matched shaded/depth pipeline; lowering exact edges to existing
  ProGPU-owned lines preserves lineweight, linetype, selection, print, and both
  renderer implementations today. SkParagraph, DirectWrite, Win2D text,
  Parley, and HarfBuzz were rechecked through the sources below and are not
  applicable: MESH changes no shaping, layout, font/fallback, glyph cache,
  subpixel/DPI, or text device-loss contract. Native C++ has no ACadSharp
  document compiler; both renderers consume the same canonical line commands,
  so no C++ source, C ABI/wire, P/Invoke, shader, or generated binding change
  applies.

- For flat-shaded level-zero surfaces, Autodesk's
  [MESH DXF contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-4B9ADA67-87C8-4673-A579-6E4C76FF7025.htm),
  [mesh display contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-E854E312-9BF4-476F-AF9D-AA2A276600C8.htm),
  [SHADEMODE contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-A39F669D-DD11-46DD-BD20-7C41F2FF5326.htm), and
  [polyface contract](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-DXF/files/GUID-96B6288E-F413-46C0-968A-A314171C0AAE.htm)
  establish persisted face topology, the triangle-pair interpretation of a
  quad, Flat versus smooth display, and signed hidden-edge indices. Adopted
  exact level-zero faces, face style identity, deterministic triangle lists,
  and the explicit Flat style; rejected inferred subdivision, smoothing/crease
  thresholds, and ambiguous non-planar N-gon diagonals.
  [WebGPU's primitive topology and depth contracts](https://gpuweb.github.io/gpuweb/),
  [Direct3D triangle lists](https://learn.microsoft.com/en-us/windows/win32/direct3d9/triangle-lists), and
  [Direct2D/Direct3D interoperation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-direct3d-interoperation-overview)
  informed indexed triangle batching, keeping CAD/UI 2D work separate from the
  depth pass, and retaining one same-device resource generation rather than
  crossing the managed/native boundary per primitive.
  [Skia Canvas](https://api.skia.org/classSkCanvas.html),
  [WebRender's retained pipeline](https://firefox-source-docs.mozilla.org/gfx/webrender/overview.html), and
  [Vello's retained scene](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  were compared for retained scene reuse, viewport culling, lazy resource
  preparation, batching, and device rebuild. They remain primarily 2D
  references; ProGPU therefore keeps its original camera-independent CAD scene
  and existing depth-aware managed/native Mesh3D backend instead of forcing a
  depth mesh through a foreign 2D display-list abstraction. Startup stays lazy,
  snapshot/batch keys are the immutable content generation plus style ranges,
  uploads are demand-driven by the existing geometry version/resource caches,
  worker preparation remains possible before GPU ownership, and device loss
  rebuilds from retained CPU arrays.
  [SkParagraph's shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite architecture](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Win2D text drawing](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawText.htm),
  [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md), and
  [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  were rechecked for the mandatory text audit. They are not applicable to this
  slice because shaping/layout results, visibility, glyph/texture cache keys and
  eviction, fallback and variable-font state, DPI/subpixel snapping, and text
  device-loss invalidation do not change. No foreign implementation text,
  structure, tables, names, or control flow was used.

- For authored modern-MESH subdivision, Autodesk's
  [MESH DXF contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-4B9ADA67-87C8-4673-A579-6E4C76FF7025.htm),
  [MESHCREASE behavior](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-F176266D-C615-4A0B-95ED-E8FBE1D4E392.htm), and
  [`AcDbSubDMesh::setCrease`](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbSubDMesh__setCrease_AcDbFullSubentPathArray__double.html)
  establish the persisted smoothness level, Blend Crease flag, explicit edge
  records, and `-1`/zero/positive crease meanings. Pixar's
  [OpenSubdiv Sdc overview](https://opensubdiv.org/docs/sdc_overview.html) and
  [documented compatibility rules](https://github.com/PixarAnimationStudios/OpenSubdiv/blob/dev/documentation/compatibility.rst)
  describe Catmull-Clark face/edge/vertex masks, smooth/dart/crease/corner
  classification, boundary interpolation, and uniform semi-sharp subtraction.
  Adopted those public mathematical contracts and adapted them into an original
  bounded, double-precision, generation-owned CPU evaluator with linear UV
  refinement and explicit topology diagnostics. Rejected foreign source code,
  helper/data layouts, adaptive patch tables, runtime dependency integration,
  guessed repair, control-cage substitution, and unbounded exact-level cache
  keys. Limit-patch evaluation remains a separately measured quality gate.
  [Skia triangle meshes](https://api.skia.org/classSkCanvas.html),
  [Direct2D meshes](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1mesh),
  [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm),
  [WebRender's retained pipeline](https://firefox-source-docs.mozilla.org/gfx/webrender/overview.html), and
  [Vello's retained scene](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  were rechecked for startup, retained reuse, culling, worker preparation,
  batching, cache identity/eviction, demand-driven upload, and device loss.
  They do not define CAD subdivision, so ProGPU evaluates immutable topology
  before device ownership and reuses its existing generation-keyed Mesh3D
  resources and shared managed/native depth path. Startup remains lazy, camera
  changes reuse the compiled scene, and visibility/upload/device-loss contracts
  are unchanged. The native C++ frontend has no ACadSharp document graph; it
  consumes the exact same final positions, normals, UVs, and indices through
  the existing single-update/single-draw stable ABI. Therefore no paired C++
  topology evaluator, shader fork, C record, generated C# wire, or extra
  crossing applies; matched native-batch tests cover the applicability seam.
  [SkParagraph shaping](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite architecture](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Win2D text drawing](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawText.htm),
  [Parley layout](https://github.com/linebender/parley/blob/main/doc/concept.md), and
  [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  are not applicable because this slice changes no shaping/layout reuse,
  fallback or variable-font state, glyph/texture cache key or eviction,
  DPI/subpixel placement, text upload, or text device-loss invalidation.

- For exact SOLID/3DFACE shaded surfaces, Autodesk's
  [SOLID DXF record](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-E0C5F04E-D0C5-48F5-AC09-32733E8848F2.htm),
  [managed SOLID thickness contract](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid_Thickness.html),
  [ActiveX thickness contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-ActiveX-Reference/files/GUID-0DFEAA18-C0FE-4B60-B103-39D52E939D63.htm),
  [signed ELEV behavior](https://help.autodesk.com/view/ACD/2027/ENU/?caas=caas%2Fdocumentation%2FACD%2F2014%2FENU%2Ffiles%2FGUID-EE3D7937-2CD7-4F0F-BC8F-0AA0DC306EAE-htm.html),
  [SOLID command geometry](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-0998E0EE-7829-4AA4-9282-4FC703F9B1F4.htm),
  [3DFACE DXF record](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-747865D5-51F0-45F2-BEFE-9572DBC5B151.htm), and
  [3DFACE display contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-5E88BB23-9110-45FB-B54A-3FF2E2002585.htm)
  establish OCS versus WCS storage, repeated-fourth-corner triangles, SOLID's
  diagonal third/fourth point convention, signed thickness along the normalized
  extrusion direction, consecutive 3DFACE edge flags, and
  the rule that an all-invisible-edge 3DFACE remains present when shaded.
  Adopted one perimeter-normalized immutable face record, one transformed WCS
  extrusion vector, bounded exact lobe/prism triangle lists, exact Flat normals,
  and surface selection independent of wire visibility;
  rejected treating SOLID's persisted order as a perimeter, hiding a shaded
  face with its wire edges, filling the bow-tie gap, unsigned thickness,
  depth-agnostic filled plan projection of a thick shell, or inventing area for
  degenerate triangles.
  [WebGPU's triangle/depth contracts](https://gpuweb.github.io/gpuweb/),
  [Direct3D triangle lists](https://learn.microsoft.com/en-us/windows/win32/direct3d9/triangle-lists), and
  [Direct2D/Direct3D interoperation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-direct3d-interoperation-overview)
  reinforce reuse of the existing depth-aware batch and same-device resource
  boundary. [Skia Canvas](https://api.skia.org/classSkCanvas.html),
  [WebRender's retained pipeline](https://firefox-source-docs.mozilla.org/gfx/webrender/overview.html), and
  [Vello's retained scene](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  were rechecked for startup/lazy initialization, retained-scene reuse,
  visibility, demand upload, worker preparation, batching, cache/device
  generations, and loss recovery. The existing ProGPU camera-independent CPU
  batch plus managed/native Mesh3D resource remains the applicable design;
  no per-face crossing, runtime shader, copied native topology cache, or
  backend-specific shell algorithm was introduced.
  [SkParagraph shaping](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite architecture](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Win2D text drawing](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawText.htm),
  [Parley layout](https://github.com/linebender/parley/blob/main/doc/concept.md), and
  [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  are not applicable because this slice changes no shaping/layout reuse,
  fallback, variable-font state, glyph/texture cache, DPI/subpixel policy, or
  text invalidation. Native applicability was audited: native C++ already
  consumes the same canonical Mesh3D ABI/shader and has no ACadSharp frontend,
  so a second implementation or shader fork would be incorrect. No foreign
  source text, structure, tables, names, or control flow was used.

- For persisted DIMENSION pictures, Autodesk's
  [common DIMENSION DXF contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-EDD54EAC-A339-4EBA-AEA6-EC8066505E2B.htm),
  [linear DIMENSION record](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-F0004556-493C-48D5-8619-61D6ADF05C04.htm),
  [OCS contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-D99F1509-E4E4-47A3-8691-92EA07DC88F5.htm),
  [`dimBlockPosition` behavior](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbDimension__dimBlockPosition.html),
  [dimension block-transform/layout methods](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-__MEMBERTYPE_Methods_AcDbDimension.html),
  [BLOCK record](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-66D32572-005A-4E23-8B8B-8726E8C14302.htm), and
  [BLOCKS section](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-9DDFC343-6C87-4AF8-B3B9-77E0AB5A3031.htm)
  establish the persisted anonymous picture, coordinate spaces, relative group-
  12 displacement, and explicit regeneration boundary. Adopted persisted
  picture replay and OCS-to-WCS displacement; rejected snapshot-time layout
  generation, using group 10 as picture translation, or treating Defpoints
  construction points as visible geometry.
  [Skia `SkPicture`](https://api.skia.org/classSkPicture.html),
  [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist),
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm),
  [WebRender's display-list/scene pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  [Vello scene append](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), and
  [Vello's retained-fragment model](https://github.com/linebender/vello/blob/main/doc/vision.md)
  informed bounded nested recorded-content replay, unbaked occurrence
  transforms, culling, and reuse. ProGPU adapts those concepts to its own typed
  immutable streams and copies no foreign source or structure. The existing
  [SkParagraph stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite architecture](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Parley layout model](https://github.com/linebender/parley/blob/main/doc/concept.md), and
  [HarfBuzz shape-plan model](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  remain authoritative for MTEXT children: persisted pictures reuse ProGPU's
  existing full text stack and introduce no second shaping, fallback, glyph-
  cache, DPI, subpixel, or device-loss path. Managed/native applicability was
  audited explicitly: both renderers already consume the same lowered canonical
  picture commands, while native C++ has no ACadSharp document compiler, so no
  C++ implementation, C ABI, generated wire, shader, or crossing change applies.

- For exact polynomial and positive-weight rational HATCH spline edges, Autodesk's
  [boundary-path spline record](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-DC5215D6-E73F-4DFF-8BE9-01CA9610FAEE.htm),
  [HATCH entity contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-C6C71CED-CE0F-4184-82A5-07AD6241F15B.htm),
  and [ObjectARX NURBS-loop API](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-__OVERLOADED_appendLoop_AcDbHatch.html)
  establish the degree, rational, periodic, knot, weighted-control, fit, and
  tangent fields and confirm that a HATCH loop may own a NURBS edge. Adopted
  strict persisted-record validation and exact homogeneous span extraction;
  rejected fixed-detail polygon conversion and silently discarding weights.
  Autodesk's [NURBS-data contract](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSpline__setNurbsData_int_Adesk__Boolean_Adesk__Boolean_Adesk__Boolean_AcGePoint3dArray__AcGeDoubleArray__AcGeDoubleArray__double_double.html)
  additionally requires positive rational weights and defines periodic knot
  counts. ProGPU adopts positive homogeneous cubic spans, canonical endpoint
  normalization, and bounded weighted-coordinate validation.
  [Skia's line/quad/conic/cubic path verbs](https://skia.googlesource.com/skia/%2B/refs/heads/main/include/core/SkPath.h),
  [Skia's conic contract](https://skia.googlesource.com/skia/%2B/2a8c48be4ff65d873d9d5ba65ecef989d82dd0be/site/user/api/SkPath_Reference.md),
  [Direct2D path geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/path-geometries-overview),
  [Win2D path-builder verbs](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm),
  [Vello's retained line/quad/cubic encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  [Vello's rational-Bezier roadmap](https://github.com/linebender/vello/blob/main/doc/roadmap_2023.md),
  [WebRender's path-builder surface](https://searchfox.org/firefox-main/source/gfx/2d/2D.h),
  and the homogeneous normalization result in
  [Rational Bezier Guarding](https://onlinelibrary.wiley.com/doi/full/10.1111/cgf.14605)
  show that those production path surfaces expose no weighted cubic verb.
  ProGPU therefore adds an original shared rational-cubic retained segment
  instead of translating to a backend-specific approximation. Skia's
  conic-to-quad approximation was rejected for CAD boundary fidelity;
  Direct2D/Win2D, Vello, and WebRender expose no equivalent weighted verb.
  [Bernstein-form Bezier root isolation](https://doi.org/10.1016/j.cagd.2016.02.017),
  the [rational Bezier derivative contract](https://www.sciencedirect.com/science/article/pii/016783969290014G),
  and IBM's [Bernstein subdivision conditioning analysis](https://research.ibm.com/publications/on-the-numerical-condition-of-bernstein-bezier-subdivision-processes)
  informed the bounded derivative-numerator extrema and ray-root evaluation;
  no sampled bounds or iterative tessellation fallback was adopted. ProGPU adapts
  those public behavioral/architecture concepts to its own immutable packed
  streams and shares only original in-repository spline/path algorithms; no
  foreign implementation text or structure was used. The required text audit
  rechecked
  [Skia's staged shaped-text model](https://skia.org/docs/dev/design/text_shaper/),
  [DirectWrite text layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
  [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md), and
  [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html);
  they are not applicable because this boundary change affects no shaping,
  layout, font fallback, glyph cache, DPI, or subpixel contract.

- For complete HATCH island-style lowering, Autodesk's
  [DXF HATCH group-75 contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-C6C71CED-CE0F-4184-82A5-07AD6241F15B.htm),
  [island-detection behavior](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-981679AC-7097-4724-A30D-33F1CAFDD81D.htm),
  [ObjectARX HatchStyle contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbHatch__HatchStyle1.html),
  [boundary-path flags](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-DC5215D6-E73F-4DFF-8BE9-01CA9610FAEE.htm),
  and [ObjectARX loop-type definitions](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbHatch__HatchLoopType.html)
  establish odd-parity Normal, first-level-only Outer, fill-through Ignore, and
  the distinction between persisted loop provenance flags and geometric
  nesting. Adopted exact bounded containment-derived contribution metadata;
  rejected source-order assumptions, treating `External` as island depth,
  deleting ignored source loops, and CPU/raster mask composition.
  [Skia path fill types](https://skia.googlesource.com/skia/+/f0b2393fdedc6d77b98a8c6ce8103c20cd11e400/docs/SkPath_Reference.bmh),
  [Direct2D alternate fill mode](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_fill_mode),
  [Win2D geometry path construction](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm),
  [WebRender's retained renderer](https://github.com/servo/webrender), and
  [Vello's retained fill-style encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs)
  reinforce keeping contour selection separate from analytic GPU coverage and
  retaining one explicit even-odd fill. ProGPU reuses its own canonical path
  rasterizers and managed/native picture contract; no renderer ABI, shader,
  cache, upload, DPI, invalidation, or device-loss rule changed. The required
  text audit rechecked
  [SkParagraph](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz shape plans](https://harfbuzz.github.io/harfbuzz-hb-shape-plan.html);
  they are not applicable because island classification changes no text,
  shaping, font, glyph-cache, fallback, subpixel, or DPI contract.

- For complete bounded DXF/PAT patterned-HATCH lowering, Autodesk's
  [pattern-definition contract](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-A6F2E6FF-1717-44B6-A476-0CA817ADD77E-htm.html),
  [DXF pattern-line data](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-7C05C0EC-B0FB-4A86-A164-B9E5C6C03990.htm),
  [dash-definition contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-LT-Customization/files/GUID-B67FADB9-0F10-4536-ABC4-BB7D8BDDF5B1.htm),
  [simple-pattern guidance](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Customization/files/GUID-7E1BFC62-C5F2-48C6-84D7-71FAD4DF0BEF.htm),
  [multi-line/dash grammar](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Customization/files/GUID-F3333A6A-DE51-4864-BEA6-1C6C5BF9BEF8.htm),
  [pattern-type contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbHatch__HatchPatternType.html),
  and [double-pattern contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbHatch__setPatternDouble_bool.html)
  establish line direction/base point, tangent/perpendicular offsets, dash
  arrays, absolute origin, and the user-defined-only perpendicular double.
  Adopted the full specified multi-family, tangent-row-shift, positive-dash,
  negative-gap, zero-dot grammar including its six-item definition limit;
  rejected CPU line explosion, silent dash removal, and treating full
  offset-vector length as spacing.
  [Skia local shader matrices](https://api.skia.org/classSkShader.html),
  [Direct2D brush transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-transforms-overview),
  [Win2D image-brush transforms/extend modes](https://microsoft.github.io/Win2D/WinUI3/html/Properties_T_Microsoft_Graphics_Canvas_Brushes_CanvasImageBrush.htm),
  [WebRender's repeating segmented brush shader](https://searchfox.org/firefox-main/source/gfx/wr/webrender/res/brush.glsl),
  [Vello's retained path transforms](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
  [Vello's dash-encoding discussion](https://github.com/linebender/vello/issues/303),
  [Direct2D stroke styles](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1strokestyle),
  and [Win2D custom dash syntax](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/archive/windows/win2d-path-mini-language)
  informed the independent pattern-space transform and retained procedural
  evaluation. Encoding-time stroke expansion was rejected because HATCH is a
  two-dimensional periodic field and CPU expansion would break retained zoom
  behavior. ProGPU keeps its own typed brush/wire/shader design and copies no
  foreign implementation. SkParagraph, DirectWrite text layout, Parley, and
  HarfBuzz were rechecked through the sources listed in the solid-HATCH audit;
  they are not applicable because this slice changes no shaping, glyph, font,
  fallback, DPI text-snapping, or text-cache contract.

- For solid HATCH lowering, Autodesk's
  [HATCH DXF contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-C6C71CED-CE0F-4184-82A5-07AD6241F15B.htm),
  [boundary-path data contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-DC5215D6-E73F-4DFF-8BE9-01CA9610FAEE.htm),
  [HatchStyle contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbHatch__HatchStyle1.html),
  [AcDbHatch architecture](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDbHatch.html),
  and [boundary overview](https://help.autodesk.com/cloudhelp/2018/CHT/AutoCAD-ActiveX/files/GUID-CC1986F6-9208-4A59-81EE-6E28A5BB2117.htm)
  establish OCS boundary data, typed edges, and Normal odd-parity island
  semantics. Adopted those public contracts; adapted them to bounded immutable
  packed records, exact extrema, one retained multi-figure path, and typed
  unsupported outcomes. Rejected serializing computed fill, unbounded malformed
  traversal, and treating every loop as an independent filled command.
  [Skia path fill types](https://api.skia.org/SkPathTypes_8h_source.html),
  [Direct2D geometry groups](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1geometrygroup),
  [Direct2D path geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/path-geometries-overview),
  and [Win2D CanvasPathBuilder](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm)
  support retained multi-figure analytic geometry with an explicit alternate/
  even-odd fill rule. Adopted that separation and reuse; rejected per-loop
  raster surfaces and fixed-detail polygon conversion.
  [WebRender's retained renderer](https://github.com/servo/webrender),
  [WebRender overview](https://github.com/servo/servo/wiki/Webrender-Overview),
  [Vello's encoded paths](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  and [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  informed compact retained path data, early transforms, visibility work, and
  explicit cache/upload counters; ProGPU retains its own scene, path, batching,
  and culling architecture and copies no foreign implementation. Vello's dynamic
  GPU memory and antialiasing tradeoffs were not introduced because this slice
  reuses ProGPU's established bounded path pipeline.
  The required text-engine audit rechecked
  [SkParagraph shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz shape plans](https://harfbuzz.github.io/harfbuzz-hb-shape-plan.html).
  They are not applicable to boundary filling: no Unicode analysis, shaping,
  line layout, fallback font, variable-font, glyph cache, or subpixel text state
  is touched. Startup remains lazy; immutable scene reuse, BVH visibility,
  demand-driven path upload, worker-eligible snapshot preparation, GPU batching,
  DPI transforms, cache eviction/generations, and device-loss invalidation all
  remain with their existing typed ProGPU owners. No new resource initialization
  or replay-time CPU preparation was added.

- [ACadSharp repository and format support](https://github.com/DomCR/ACadSharp)
  [reader API](https://github.com/DomCR/ACadSharp/blob/master/docs/articles/samples/reading.md),
  and the pinned fork's public
  [`CadObjectCollection<T>` contract](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/ACadSharp/CadObjectCollection.cs),
  [`CadObject.Clone` contract](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/ACadSharp/CadObject.cs),
  [`Entity` transform contract](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/ACadSharp/Entities/Entity.cs),
  and [`Transform` construction surface](https://github.com/wieslawsoltes/ACadSharp/blob/b469bd1ec7db6d7d364425f6165609e5ccf09b04/src/CSUtilities/CSMath/Transform.cs):
  adopted `CadDocument` plus format-specific reader/writer ownership; adapted
  behind typed store/diagnostic/capability services. Add/remove command design
  uses only the public collection ownership, cancellation, and observable handle
  reassignment contracts; it retains ProGPU command state rather than copying
  collection implementation text or structure. Rotation likewise uses only the
  public axis-angle/radians entity operation, normalizes the caller's axis in
  ProGPU, and applies the public inverse operation for undo. Pivot rotation
  composes only public translation/rotation calls and rejects private matrix
  ordering as an implementation dependency. Uniform scale uses
  the documented origin overload and a reciprocal factor. Duplication consumes
  only the documented detached-copy result and adds ProGPU-owned bounded
  selection-set state, optional WCS displacement, transactional batch ownership,
  and exact retained-clone Undo/Redo. The batch-add extension is original
  ProGPU-owned work in ACadSharp commit `d17db6f8`, paired with structural
  publication and invalid-input regressions. Rejected
  extension-only validation, unconditional DWG-save claims, private handle
  manipulation, pivot matrices based on undocumented composition order, and exposing
  anisotropic scaling without type-preserving entity conformance.
- [Autodesk DXF object coordinate systems](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-D99F1509-E4E4-47A3-8691-92EA07DC88F5.htm):
  adopted OCS/elevation/extrusion normalization and arbitrary-axis conformance.
- [Autodesk ELLIPSE entity contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-107CB04F-AD4D-4D2F-8EC9-AC90888063AB.htm),
  [SOLID contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-E0C5F04E-D0C5-48F5-AC09-32733E8848F2.htm),
  and [3DFACE contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-747865D5-51F0-45F2-BEFE-9572DBC5B151.htm):
  adopted WCS ellipse axes/parameters, OCS SOLID corners, WCS 3DFACE corners,
  and invisible-edge flags; adapted them into fixed immutable records and
  existing ProGPU analytic/fill paths; rejected polygonal ellipse sampling and
  incomplete extrusion rendering.
- [Autodesk POLYLINE contract](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-ABF6B778-BE20-4B49-9B58-A94E64CEFFF3.htm),
  [VERTEX contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-0741E831-599E-4CBF-91E1-8ADBCFD6556D.htm),
  and [AcDb2dPolyline vertex-position contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-AcDb2dPolyline__vertexPosition_AcDb2dVertex__const.html):
  adopted owning elevation plus vertex XY for 2D OCS conversion, full vertex XYZ
  for 3D WCS, bulge direction, closure, and fit/width flags; adapted both forms
  into packed immutable streams and rejected fixed sampling of fitted curves.
- [Autodesk INSERT contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-28FA4CFB-9D5E-4880-9F11-36C97578252F.htm),
  [BLOCK contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-66D32572-005A-4E23-8B8B-8726E8C14302.htm), and
  [AcDbBlockReference model](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbBlockReference.html):
  adopted the block MCS-to-WCS mapping, base point, insertion position,
  OCS-relative rotation/normal, scale factors, and nested memory-reuse model;
  adapted them into bounded immutable analytic expansion while retaining root
  handle identity; rejected approximate transformed extents and unbounded
  recursion.
- [Autodesk MINSERT command contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-A780A2FA-4A2E-4574-950F-E788AB71F527.htm),
  [AddMInsertBlock API](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-ActiveX-Reference/files/GUID-AAEFDED2-34A3-4466-A7AA-71CAD8DCB35C.htm),
  and [AcDbMInsertBlock methods](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-__MEMBERTYPE_Methods_AcDbMInsertBlock.html):
  adopted independent row/column counts and spacings plus rotation of both each
  block and the complete array; adapted cells into row-major bounded affine
  lowering with one semantic root handle. The scale-independent spacing order
  is an inference from Autodesk exposing scale factors and spacing distances as
  independent inputs and describing rotation, but not scale, as applying to the
  entire array. Rejected unbounded expansion and undocumented scale-dependent
  spacing.
- [Autodesk TEXT contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-62E5383D-8A14-47B4-BFC4-35824CAE8363.htm),
  [text symbol/control-code contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-968CBC1D-BA99-4519-ABDD-88419EB2BF92.htm),
  [DXF string storage contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-2553CF98-44F6-4828-82DD-FE3BC7448113.htm),
  [AcDbText width-factor contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-AcDbText__widthFactor.html),
  [text-style inheritance behavior](https://help.autodesk.com/cloudhelp/2018/CHT/AutoCAD-ActiveX/files/GUID-B6880624-B89C-4C7E-8276-6E21070CFBF6.htm),
  [TEXT command Align/Fit behavior](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-D1C664DD-63D9-467E-8EC1-2F5A1777A924.htm),
  [AcDbText alignment-point contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbText__alignmentPoint.html),
  [OpenType `post` underline metrics](https://learn.microsoft.com/en-us/typography/opentype/spec/post),
  [OpenType `OS/2` strikeout metrics](https://learn.microsoft.com/en-us/typography/opentype/spec/os2),
  [OpenType `hhea` ascender](https://learn.microsoft.com/en-us/typography/opentype/spec/hhea),
  [MTEXT contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-5E5DB93B-F8D3-4433-ADF7-E92E250D2BAB.htm),
  and [STYLE contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-EF68AF7C-13EF-45A1-8175-ED6CE66C8FC9.htm):
  adopted OCS/WCS point distinctions, the second-point justification rule,
  effective entity transform values and style creation defaults, two-point
  Align/Fit scaling, generation flags, decimal/symbol/decoration controls,
  OpenType decoration metrics, font metadata, and MTEXT attachment/flow/column scope; adapted
  supported TrueType TEXT into normalized retained font and decoration runs with
  conservative affine bounds and MTEXT into bounded typed inlines, styled
  paragraph layout, packed colored glyph/decorative streams, exact affine
  selection, and retained replay; rejected guessed text rectangles, stripped
  MTEXT formatting, silent SHX substitution, and approximating unsupported
  vertical/SHX/field/tab contracts.
- [Autodesk MTEXT object contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-ActiveX-Reference/files/GUID-2532B20E-413D-4F59-9E88-B40E8AABB9FF.htm),
  [formatted MTEXT ranges](https://help.autodesk.com/view/OARX/2024/ITA/?caas=caas%2Fdocumentation%2FCIV3D%2F2014%2FITA%2FfilesACD%2FGUID-ECEEF65E-C327-44B8-AFB9-C0ACA2CAEF55-htm.html),
  [column behavior](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-MAC-Core/files/GUID-6DF5368A-5F2F-44BE-8B80-F35FFEF80204.htm),
  [background-mask contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-3448A24E-E18B-4C8C-B8AB-84F4CD4EBC81.htm),
  and [line-spacing contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-ActiveX-Reference/files/GUID-429D5E20-4522-4699-BEC8-0D27CA17EDDF.htm):
  adopted one semantic object with word wrapping, scoped range formatting,
  eight-level grouping, semantic stack forms, explicit attachment/flow,
  text-driven or fixed columns, gutter-excluding masks, and AtLeast/Exact
  spacing; adapted the persisted language to original typed immutable ProGPU
  runs with caller-owned bounds; rejected ACadSharp's formatting-stripping
  convenience projection, runtime reflection, and treating unknown codes as
  visible characters.
- [Autodesk common entity property codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-3610039E-27D1-4E23-B6D3-7E60B22BB5BD.htm)
  and [ByBlock color behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-14BC039D-238D-4D9E-921B-F4015F96CB54.htm):
  adopted layer `0`, ByLayer, and ByBlock inheritance without mutating block
  definitions or cloning third-party entities.
- [Autodesk lineweights](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-4B33ACD3-F6DD-4CB5-8C55-D6D0D7130905.htm):
  adopted distinct cosmetic model-space and physical paper/plot policies.
- [Autodesk LTYPE records](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-F57A316C-94A2-416C-8280-191E34B182AC.htm),
  [simple-linetype semantics](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-LT-Customization/files/GUID-EF1DF0A9-2088-487C-8085-16FEE6425405.htm),
  [linetype scaling](https://help.autodesk.com/view/ACD/2026/ENU/?guid=GUID-20B4D4B3-1220-426A-847B-5BBE36EC6FDF),
  [global/object scale multiplication](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-45EABA0C-6558-4CEF-940F-023170207587.htm),
  and [polyline generation](https://help.autodesk.com/cloudhelp/2024/DEU/AutoCAD-LT-ActiveX-Reference/files/GUID-40F4B7B9-CB82-4D62-AD82-1BCFDBBC9F81.htm):
  adopted positive dash, negative gap, zero dot, entity/global scaling, and
  A-aligned endpoint requirements plus per-entity 2D-polyline generation. A
  fixed repeating phase was rejected because
  AutoCAD adjusts endpoint dashes per line/arc and draws a too-short primitive
  continuously. Adapted those public rules into packed referenced definitions,
  transactional limits, scalar endpoint planning, and analytic path splitting.
  Autodesk does not publish the exact residual distribution for dot-first or
  closed patterns, so their deterministic integral-period fit is documented as
  provisional and covered separately; the exact spline seam topology no longer
  depends on that residual-distribution choice.
- [Autodesk SPLINE DXF records](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-E1F884F8-AA90-4864-A215-3182D47A9C74.htm),
  [managed NURBS constructor contract](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-ManagedRefGuide/files/OREFNET-Autodesk_AutoCAD_DatabaseServices_Spline_Spline_int__MarshalAsUnmanagedType_U1__bool__MarshalAsUnmanagedType_U1__bool__MarshalAsUnmanagedType_U1__bool_Point3dCollection_DoubleCollecti.html),
  and [SPLPERIODIC seam policy](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-60D7953C-E22D-4CF3-B779-F776592A5F23-htm.html):
  adopted typed degree/control/knot/weight and closed/periodic distinctions.
  Adapted curves through the documented maximum degree ten to one uninterrupted
  WCS path. Degree one uses exact linear spans; higher degrees use bounded
  numerical arc-length inversion and exact rational subcurve extraction. The
  published periodic `N` control/`N+1` knot form is normalized to a standard
  cyclic evaluator form, while already extended dependency records must prove
  the same period. Legacy nonperiodic closure is a separate exact line span.
  Rejected viewport sampling for CAD pattern placement, duplicated seam edges,
  malformed cyclic intervals, and internal multiplicity greater than the
  degree.
- [Michigan Tech's NURBS knot-insertion notes](https://pages.mtu.edu/~shene/COURSES/cs3621/NOTES/spline/NURBS/NURBS-knot-insert.html)
  document the standard homogeneous knot-insertion result: inserting a knot
  changes the representation without changing the curve. ProGPU adopts that
  mathematical contract, not the page's implementation text or structure, and
  applies original bounded local insertion to isolate each Bezier span.
- [Autodesk's Window/Crossing selection contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-531FB60D-833B-4813-927A-42275CF6777D.htm),
  [`SELECT` command reference](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-0DD5DA73-9DC5-4424-8FED-7BBE3BE52A4D.htm), and
  [`AcDbCurve::getClosestPointTo`](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-__OVERLOADED_getClosestPointTo_AcDbCurve.html):
  adopted complete-object containment for Window, enclosed-or-touching geometry
  for Crossing, and the nearest point on the unextended curve for point picks.
  The documented dashed-linetype visible-vector exception is recorded but
  rejected for this source-geometry slice because selection does not yet retain
  viewport-visible dash fragments as semantic geometry.
- [HarfBuzz glyph rendering](https://harfbuzz.github.io/glyphs-and-rendering.html)
  and [`hb_font_draw_glyph_or_fail`](https://harfbuzz.github.io/harfbuzz-hb-font.html#hb-font-draw-glyph-or-fail)
  separate shaping's positioned glyph list from optional quadratic/cubic
  outline extraction and color/bitmap painting. ProGPU adopts that separation:
  it reuses the snapshot's shaped IDs and positions, queries cached monochrome
  outlines once per font/glyph, and treats a missing outline as no monochrome
  geometry rather than inventing an advance rectangle. Color/bitmap TEXT is not
  claimed by this monochrome CAD selection slice.
- [SkParagraph's public paragraph contract](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h)
  exposes coordinate-to-glyph and range-box APIs separately from line-path
  extraction, while [`SkPath::contains` and tight bounds](https://api.skia.org/classSkPath.html)
  operate on filled path geometry and its fill rule. ProGPU adopts the
  layout-versus-outline distinction and fill-rule containment. It rejects
  caret/cluster rectangles for CAD entity selection because they intentionally
  include layout space and do not preserve glyph holes.
- [DirectWrite `HitTestPoint`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittestpoint)
  and [`HitTestTextRange`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextrange)
  likewise return text-layout positions/regions, whereas Direct2D
  [`FillContainsPoint`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-fillcontainspoint)
  and Win2D
  [`CanvasGeometry` containment methods](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm)
  test rendered fill or stroke geometry under a transform. ProGPU adapts the
  latter geometry contract for CAD picks and keeps DirectWrite-style layout hit
  testing available as a distinct future text-editing concern. Platform
  flattening tolerances are rejected because retained CAD selection must not
  change with backend or viewport scale.
- [Parley `Cluster::from_point_exact`](https://docs.rs/parley/latest/parley/layout/struct.Cluster.html#method.from_point_exact)
  and [`Cursor::from_point`](https://docs.rs/parley/latest/parley/editing/struct.Cursor.html#method.from_point)
  confirm that editor hit testing targets clusters/caret affinity, not painted
  contours. [Vello's glyph rendering plan](https://github.com/linebender/vello/issues/204)
  and [retained glyph-run roadmap](https://github.com/linebender/vello/blob/main/doc/roadmap_2023.md)
  separate glyph-run identity from dynamic vector outlines and later caches.
  ProGPU adopts retained runs plus reusable outline resources and rejects both
  per-query reshaping and permanent per-glyph draw-command expansion.
- The [W3C SVG 2 elliptical-arc implementation notes](https://www.w3.org/TR/SVG/implnote.html#ArcConversionEndpointToCenter)
  provide the endpoint-to-center conversion and out-of-range-radii correction
  for retained `ArcSegment` values. ProGPU adapts that public mathematical
  parameterization, then splits a sweep into at most four 90-degree rational
  conics so SHX distance, winding, and box roots remain exact in Bernstein form.
  Fixed-angle or tolerance-dependent polyline flattening is rejected.
- [Firefox's WebRender hit-testing architecture](https://github.com/mozilla/gecko-dev/blob/master/gfx/docs/AsyncPanZoom.rst)
  uses dedicated transformed/clipped hit-test display items rather than reading
  glyph coverage, and the [rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  keeps layout-produced display lists separate from GPU painting. ProGPU adopts
  the separation of semantic hit data from GPU submission but rejects rectangle
  display items as the final CAD answer: immutable snapshot AABBs stay broad
  phase and analytic TEXT/SHX paths provide the exact phase.
- [Mourrain, Rouillier, and Roy's Bernstein-basis real-root isolation paper](https://library.slmath.org/books/Book52/files/24roy.pdf),
  [Mehlhorn and Sagraloff's deterministic bitstream Descartes analysis](https://www.mpi-inf.mpg.de/~mehlhorn/ftp/DeterministicBitstreamDescartes.pdf), and
  [Chen et al.'s global point-to-NURBS distance paper](https://www-sop.inria.fr/members/Gang.Xu/English/paper/CAD08_miniDistance.pdf):
  adopted the public mathematical facts that de Casteljau subdivision preserves
  Bernstein form, sign variation bounds interval roots, and interactive NURBS
  selection requires a global rather than one-seed local closest point. ProGPU
  adapts these concepts into a fixed degree-29 double-precision solver with
  explicit recursion/work caps. It evaluates all isolated stationary roots and
  all six box-plane roots; it rejects Newton-only local projection, viewport
  flattening, unbounded recursion, dependency geometry helpers, and silent
  answers when clustered roots cannot be resolved.
- [Skia `SkPathMeasure`](https://api.skia.org/classSkPathMeasure.html),
  [Direct2D `ComputeLength`/`ComputePointAtLength`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1geometry),
  [Win2D path measurement](https://microsoft.github.io/Win2D/WinUI3/html/Overload_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry_ComputePathLength.htm),
  and [Vello's inverse-arc-length dash design](https://github.com/linebender/vello/issues/303)
  confirm that general curved-pattern placement requires an explicit path-
  length/point/tangent contract. ProGPU adapts that separation with deterministic
  fixed WCS maps plus safeguarded local inversion, while rejecting viewport
  flattening and retaining exact rational output geometry. This is a numerical
  distance policy, not a claim about unpublished Autodesk conformance details.
  WebRender retains renderer-owned display lists but exposes no CAD NURBS
  linetype contract. SkParagraph, DirectWrite text layout, Parley, and HarfBuzz
  remain applicable only to the already shared complex-text payload, not spline
  measurement.
- [Autodesk text in custom linetypes](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-FEDCE7EB-4919-43AE-A54E-F3A293DD60CA-htm.html),
  [shapes in custom linetypes](https://help.autodesk.com/view/ACDLT/2026/ENU/?caas=caas%2Fdocumentation%2FACD%2F2014%2FENU%2Ffiles%2FGUID-AF0613E6-5C8B-47F0-800C-8B2524BF2015-htm.html),
  and the [DXF LTYPE record](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-F57A316C-94A2-416C-8280-191E34B182AC.htm):
  adopted definition-relative text/shape payloads, complete rather than trimmed
  embedded content, `S=` height/shape scaling, linetype-scaled but S-independent
  X/Y offsets, and persisted relative/absolute rotation. Adapted those rules
  into definition-shared shaped glyphs/cached SHX paths plus per-occurrence
  point/tangent records. Rejected per-occurrence shaping, viewport flattening,
  guessing LIN-only upright state after DXF serialization, and interpreting an
  undocumented nonzero complex advance.
- [Skia dash effects](https://api.skia.org/classSkDashPathEffect.html),
  [Direct2D retained stroke styles](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1strokestyle),
  [Win2D custom dash styles](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasStrokeStyle.htm),
  [Vello stroke encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs),
  [Vello's encoding-time dash decision](https://github.com/linebender/vello/issues/303),
  and [WebRender's retained CSS-border dash handling](https://searchfox.org/mozilla-central/source/layout/painting/nsCSSRendering.cpp):
  adopted reusable interval/phase/cap concepts and retained device-independent
  style ownership, but none is treated as an oracle for CAD A-alignment. The
  generic APIs couple dash units to stroke width or CSS border policy, while CAD
  requires model-unit intervals and fixed-device lineweight. Adapted Vello's
  pay-once encoding separation into a CAD-owned endpoint planner and analytic
  centerline splitter; rejected per-frame/backend dashing and rejected changing
  the shared ProGPU dash-width semantics for a CAD-only rule.
- [SkParagraph's shaped-text stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite/Direct2D integration](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
  [Win2D retained text layouts](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
  [HarfBuzz positioned buffers](https://harfbuzz.github.io/harfbuzz-hb-buffer.html),
  [Parley's itemized layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
  [WebRender's retained display-list overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  and [Vello's packed path encoding](https://github.com/linebender/vello/blob/main/vello_encoding/src/path.rs)
  were rechecked for this rendering change. Adopted their separation of reusable
  CPU shaping/layout results from compact retained vector commands and renderer-
  owned resources. Adapted it by shaping one linetype resource once and replaying
  existing glyph/path commands at bounded transforms. Rejected per-placement
  shaping, a second text renderer, backend-specific CAD expansion, and moving
  Unicode/OpenType shaping onto the GPU.
- [Skia canvas/picture API](https://skia.org/docs/user/api/),
  [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist),
  [Win2D `CanvasCommandList`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm),
  [WebRender's retained display-list architecture](https://github.com/servo/servo/wiki/Webrender-Overview),
  and [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md):
  adopted the separation between retained static drawing commands, camera/spatial
  transforms, and a small transient interaction overlay. Adapted that separation
  to one ProGPU-owned `GpuPicture` plus bounded selection state shared by desktop
  and browser hosts. Rejected rebuilding the CAD picture or mutating retained
  commands during pointer motion. For committed edits, adopted immutable
  command-list replacement and explicit resource ownership; adapted that into
  one generation-tagged ProGPU snapshot/picture swap while retaining the prior
  picture until the replacement is complete. [WebRender's current transaction
  and frame counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs)
  reinforce measuring display-list construction, scene building, uploads,
  batching, and memory separately. Rejected mutation behind a cached picture and
  rejected claiming the current full-generation rebuild as incremental. This
  interaction/edit slice changes no shader, compositor, upload, device-loss,
  atlas, or managed/native renderer contract; both backends continue receiving
  the same existing picture/overlay commands.
- [Autodesk page setup](https://help.autodesk.com/cloudhelp/2025/ENU/DWGTrueView/files/GUID-0D72CF75-DA37-4937-9D9A-D93AA9BDF8D3.htm),
  [Page Setup Manager](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-B06031A0-EF7E-4287-8E34-ABDCC40FF8C4.htm),
  [macOS Page Setup Manager deletion constraints](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-MAC-Core/files/GUID-5E728224-8273-4416-8BBA-CDBC87160F6F.htm),
  [named page-setup modification](https://help.autodesk.com/cloudhelp/2023/ENU/DWGTrueView/files/GUID-F8893121-2855-441D-B8EC-B388AAA96645.htm),
  [named page-setup workflow](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-62B163D7-92F5-4793-85C2-246BDDA81470.htm),
  [Plot Settings and Page Setups (.NET)](https://help.autodesk.com/cloudhelp/2017/ENU/AutoCAD-NET/files/GUID-56BD3247-471C-4471-A238-FFDFDC3BD2E4.htm),
  [plot-area choices](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-MAC-Core/files/GUID-7F356502-16EC-4371-86A9-2A58968762DD.htm),
  [plot-rotation enum](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbPlotSettings__plotRotation.html),
  [drawing-orientation behavior](https://help.autodesk.com/cloudhelp/2025/ENU/DWGTrueView/files/GUID-E05BF1C8-3C44-4E0C-917C-5A95C860A98E.htm),
  [`PLOTROTMODE` rotated-origin behavior](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-B376D968-4346-4D7E-9AE5-3888317B5730.htm),
  [physical paper margins](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbPlotSettings__getPlotPaperMargins_double__double__double__double__const.html),
  [PLOTSETTINGS DXF fields](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-1113675E-AB07-4567-801A-310CDE0D56E9.htm),
  [LAYOUT DXF fields](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-433D25BF-655D-4697-834E-C666EDFD956D.htm),
  [plot-settings/page-setup ownership](https://help.autodesk.com/cloudhelp/2017/ENU/AutoCAD-NET/files/GUID-56BD3247-471C-4471-A238-FFDFDC3BD2E4.htm),
  [plot-window DCS contract](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_PlotSettingsValidator_SetPlotWindowArea_PlotSettings_Extents2d.html),
  [plot-origin contract](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-__OVERLOADED_getPlotOrigin_AcDbPlotSettings.html),
  [custom plot scale](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-__MEMBERTYPE_Methods_AcDbPlotSettings.html),
  [plot scale behavior](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT/files/GUID-FCC2782E-7876-4EE0-86C1-AA222B4DD2E1.htm),
  [plot-transparency policy](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbPlotSettings__plotTransparency_const.html),
  and [plot styles](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-929FE8EC-EFE3-43BB-A79F-4FF509A91D5A.htm):
  adopted detached layout and named-override ownership, separate physical
  paper/margins, printable-relative offset or centering, explicit plot-area
  identity, fit or paper-unit/drawing-unit scale, fixed physical lineweight,
  plot eligibility, and explicit CTB/STB override phases. Adapted model-space
  Extents, center/offset, fit/current-custom scale, and object lineweight into
  the first bounded lowering. Rotation is adapted as an oriented physical-page
  contract: portrait/landscape exchanges the page axes and physical margin
  edges, while the upside-down states rotate placement and clip together around
  the output page. This follows the documented default rotated-origin offset
  behavior and keeps asymmetric margins observable. Retained Window as raw DCS data and rejected
  treating it as WCS without the saved view transform; also rejected guessing
  layout viewports/UCS limits, device pixel scale, style-table,
  disabled/scaled lineweight, shaded-output, or transparency policy. Paper image
  origin remains retained device metadata; the documented plot origin inside
  the paper margins is the current geometric offset authority. The shared host
  now adopts AutoCAD's observable list contract by presenting both layout-owned
  and named setups with selected-setup details encoded in the label. It adapts
  selection to a non-mutating preview choice because applying a named setup to a
  layout is an editing command with separate undo/save semantics. Unsupported
  source policies remain selectable for their precise diagnostic, and the
  sample-only A4 option is visibly labeled as a fallback rather than pretending
  to be a drawing-owned page setup. Autodesk's public managed contract applies
  a named `PlotSettings` object to a derived `Layout` with `CopyFrom`; ProGPU
  adopts the observable fixed plot-field copy while replacing database/runtime
  transaction assumptions with its own typed session generation, bounded value
  snapshot, target-space validation, and Undo/Redo command. For creation, the
  same Autodesk contract constructs a `PlotSettings` for the source layout's
  model/paper target, copies the layout's plot fields, assigns a distinct setup
  name, and inserts it into the drawing's plot-settings dictionary. ProGPU
  adopts those observable semantics with an original fixed-field copy,
  case-insensitive duplicate preflight, bounded ownership, exact retained-object
  Undo/Redo, and no foreign transaction or helper structure. Autodesk exposes
  Modify as an operation on the selected named record and keeps applying it to
  a layout as a separate action. ProGPU adapts that separation: `Update
  selected` replaces the named record from Model without changing object/name
  identity or propagating to layouts; the existing explicit Apply command owns
  layout mutation and its independent history entry. Autodesk exposes Delete
  only for named entries and specifies that a setup assigned to a layout cannot
  be removed. ProGPU adopts that observable guard with an original bounded
  layout preflight and retained-object history action; it does not copy foreign
  transaction, dictionary, or UI implementation structure. Autodesk also
  exposes Rename only for named entries while Page Setup Manager reports the
  current named association on layouts. ProGPU preserves that association by
  changing the named dictionary identity and referring layout markers in one
  original bounded reversible action.
- [Skia PDF pages](https://skia.org/docs/user/sample/pdf/),
  [Skia canvas backends](https://skia.org/docs/user/api/skcanvas_creation/),
  [Skia canvas transforms and clips](https://skia.org/docs/user/api/skcanvas_overview/),
  [Direct2D print control](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1device-createprintcontrol),
  [Direct2D transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-transforms-overview),
  [Win2D print events](https://microsoft.github.io/Win2D/WinUI3/html/E_Microsoft_Graphics_Canvas_Printing_CanvasPrintDocument_Print.htm),
  and [Win2D drawing-session transforms](https://microsoft.github.io/Win2D/WinUI3/html/P_Microsoft_Graphics_Canvas_CanvasDrawingSession_Transform.htm):
  adopted replaying one retained/vector page description into backend-specific
  page targets at the target DPI, with preview/output sharing the same page
  transform and clip. Skia's documented PDF fallbacks reinforce treating
  unsupported effects explicitly rather than silently losing text or vectors.
  [WebRender's reviewed display-list architecture](https://github.com/servo/servo/wiki/Design/a88683ec289b53b9f50242d4c27fcc22ddb76039)
  has no physical-page contract, so only its retained spatial-transform,
  clip-tree, and preview/resource separation applies.
  [Vello's unbaked affine retained fragments](https://github.com/linebender/vello/blob/main/doc/vision.md)
  support the same transform-reuse decision. Existing
  [HarfBuzz positioned glyph output](https://harfbuzz.github.io/shaping-and-shape-plans.html),
  [SkParagraph shaped-text stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite/Direct2D glyph-run transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
  and [Parley layouts](https://github.com/linebender/parley/blob/main/README.md)
  remain retained CPU results from the immutable snapshot; the page rotation
  neither reshapes text nor introduces a foreign document renderer. The shared
  preview adopts that exact retained page rather than creating a UI-only drawing
  path: it chooses a viewport-fit page DPI and replays at 1:1 so physical fixed
  strokes preserve their proportion. Autodesk's
  [background-adaptive ACI 7 contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-2B089E0A-BDC0-4916-885E-543A85FC8CFD.htm)
  confirms that ACI 7 appears white on dark backgrounds and black on light
  backgrounds. ProGPU adapts that observable rule through one deterministic
  luminance decision in snapshot style resolution; explicit true colors remain
  unchanged and the same packed result feeds managed and native replay.
- [Autodesk ROTATE](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-1C265537-FBAC-48D5-B448-B72E777071E5.htm),
  [rotation behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-9DB2CB8C-7FB7-45A4-83A7-82FFC53FC7E1.htm),
  and [SCALE](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-Core/files/GUID-D4E17E51-5000-4AB6-8D6A-6D2AB4863C75.htm):
  adopted selected-object transforms around a stationary caller-visible base
  point, a rotation axis parallel to the coordinate system's Z axis, and uniform
  factors above/below one for enlargement/reduction. Adapted the initial shared
  plan shell to use the complete semantic selection-bounds center and WCS +Z,
  because no typed UCS or arbitrary base-point interaction exists yet. Rejected
  silently presenting that fixed plan contract as full UCS, reference-angle, or
  reference-length behavior. The implementation calls only existing original
  ProGPU transform commands and does not reproduce Autodesk implementation code.
- [Autodesk COPY](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-1CF9287F-06E8-4D03-8377-2E130862FE02.htm):
  adopted a selection set, one relative X/Y/Z displacement vector, independent
  copied objects, and Single-mode completion as the public behavior contract.
  Adapted the current shared plan shell to four finite WCS-axis actions using
  the existing Move step and to preserve the source selection for deterministic
  repeated copies and generation-safe Undo/Redo. Multiple-mode prompting,
  base/second-point acquisition, linear Array/Fit, clipboard transfer, UCS input,
  and arbitrary 3D picking are rejected at this bounded toolbar seam until their
  own typed interaction contracts exist. The implementation is original ProGPU
  command/history/UI code over the pinned ACadSharp detached-clone and collection
  contracts; no Autodesk implementation text or structure was used.
- [HarfBuzz shaping](https://harfbuzz.github.io/what-is-harfbuzz.html),
  [Parley rich-text architecture](https://github.com/linebender/parley/blob/main/doc/concept.md),
  [SkParagraph](https://docs.skia.org/docs/dev/design/text_shaper/), and
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite):
  confirmed that shaping/layout stays separate from stroke patterns, transient
  selection state, and editor command state. No second text stack or foreign
  layout structure was adopted. The current complete snapshot rebuild may shape
  unchanged text again after a transform; generation-keyed shaped-run/chunk reuse is
  therefore retained as required work rather than hidden behind a UI shortcut.
- [Autodesk shape/font descriptions](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-DE941DB5-7044-433C-AA68-2A9AE98A5713.htm),
  [special codes](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-06832147-16BE-4A66-A6D0-3ADF98DC8228.htm),
  [vector directions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-0A8E12A1-F4AB-44AD-8A9B-2140E0D5FD23.htm),
  [text-font descriptions](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Customization/files/GUID-9BBE5B28-DF02-4EC5-863A-BA04AB6F5EF1.htm),
  [Unicode font descriptions](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-MAC-Customization/files/GUID-D38A5A7B-1877-46B3-8120-32DA5F7430D1.htm),
  [Big Font descriptions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-CDAF6EAF-85D1-48FC-9A78-43514E0132D5.htm),
  and [shape/font compilation](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-BC8EFEAC-D640-410A-8EC8-2EBB38DE6563.htm):
  adopted standard header metrics, bounded program size, command semantics,
  direction encoding, and the distinct regular/Unicode/Big Font contracts;
  adapted only the standard compiled container into an immutable source layer;
  rejected signature guessing, eager opcode expansion, and treating Unicode or
  Big Font records as standard shapes.
- For standalone SHAPE entities, Autodesk's
  [DXF group-code contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-0988D755-9AAB-4D6C-8E26-EC636F507F2C.htm),
  [AcDbShape API contract](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-RefGuide/files/OREF-__MEMBERTYPE_Methods_AcDbShape.html),
  [shape-file STYLE contract](https://help.autodesk.com/cloudhelp/2017/ENU/AutoCAD-DXF/files/GUID-EF68AF7C-13EF-45A1-8175-ED6CE66C8FC9.htm),
  [OCS contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-D99F1509-E4E4-47A3-8691-92EA07DC88F5.htm),
  [shape-name/file/scale behavior](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-MAC-Customization/files/GUID-AF0613E6-5C8B-47F0-800C-8B2524BF2015.htm),
  and [first-loaded duplicate-name behavior](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-829B0626-36AB-443A-B53C-D7227297C6CE.htm)
  establish name versus style identity, numeric DWG identity, WCS insertion,
  OCS-relative rotation, height, X-only width, vertical oblique shear, loaded
  shape-file lookup, and internal definition scale. Adopted separate name and
  number fields, exact file-scoped DWG resolution, ordered DXF name resolution,
  and one retained affine path; rejected interpreting DXF group 2 as a STYLE,
  using a Unicode character map, arbitrary cross-file numeric fallback, or
  silently flattening nonzero thickness.
  [Skia's retained path contract](https://skia.org/docs/user/api/skpath_overview/),
  [Direct2D immutable and transformed geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview),
  [Win2D `CanvasPathBuilder`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm),
  [WebRender's retained rendering pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  and [Vello's retained-scene/path-transform model](https://github.com/linebender/vello/blob/main/doc/vision.md)
  were rechecked for the required rendering gate. They support definition-owned
  reusable analytic geometry with occurrence transforms and renderer-owned
  device state; ProGPU adapts that architecture to its existing cached
  `PathGeometry` and shared `DrawPath` command. HarfBuzz's
  [Unicode shaping scope](https://harfbuzz.github.io/what-is-harfbuzz.html) and
  [Parley's layout architecture](https://github.com/linebender/parley/blob/main/doc/concept.md)
  were explicitly rejected for identity and layout here: a standalone SHAPE is
  one named/numbered special-SHX program, not a Unicode string, cluster,
  fallback-font run, or paragraph. No startup font enumeration, shaping,
  line-layout, DPI/subpixel text snapping, glyph atlas, variable-font state, or
  text-device-loss rule applies; catalog registration remains bounded host
  initialization and the ordinary vector-path cache owns raster residency.
- [Autodesk support-file search-path contract](https://help.autodesk.com/cloudhelp/2027/ENG/AutoCAD-Core/files/GUID-F95EE827-7567-44EA-9D69-E9D0D37EE13F.htm),
  [FONTMAP contract](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Core/files/GUID-FC45A5DC-31F5-4725-A482-C95769273C1C.htm),
  [font-substitution and FMP editing contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-LT/files/GUID-928DF015-1E04-4CC2-AF1B-0037548DFBAE.htm),
  and [missing-SHX resolution guidance](https://help.autodesk.com/view/ACADWEB/ENU/?caas=caas%2Fsfdcarticles%2Fsfdcarticles%2FAutoCAD-cannot-find-SHX-font.html):
  adopted ordered exact-filename lookup, mapping-before-original resolution,
  fallback to the original when a mapped target is missing, same-drawing/support
  path host discovery, ordinary ASCII one-pair-per-line mapping files,
  extensionless/pathless requested names, extension-bearing substitutes, and
  explicit replacement diagnostics. Adapted these into a browser-neutral
  pre-registered immutable catalog, strict bounded parser, and an asynchronous
  style-driven desktop discovery adapter whose ordering is caller-owned; rejected
  synchronous file IO during snapshot compilation, undocumented comment syntax,
  platform-global registry state, unreported alternate-font use, and treating a
  missing mapped font as missing original content. The current catalog-application
  seam is deliberately SHX-to-SHX; cross-kind SHX-to-TrueType FMP policy requires
  a later unified resolver contract.
- [Autodesk vertical text-style behavior](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-32786109-F454-47DD-AA4C-FB8C37F4430D.htm)
  and [Text Style dialog contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-1ED81E98-6463-4574-875F-183C8280C4AC.htm):
  adopted vertical SHX/Big Font mode as a distinct font capability, the
  dual-orientation availability gate, and the standard SHX default top-center
  insertion/downward-advance contract; retained the explicit ordinary-TrueType
  vertical-style gate. Rejected synthesizing vertical Latin TrueType layout for
  a contract Autodesk reserves for vertical SHX/Big Fonts and supported Asian
  vertical faces, and retained non-default/decorated vertical placement as an
  explicit gate pending observable conformance evidence.
- For standard horizontal SHX MTEXT, Autodesk's current
  [MTEXT command contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-E6BCE05D-B9E3-4875-BBBC-29134EA6FD51.htm),
  [multiline formatting/columns/stacking guide](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-E4DC3A14-3F0A-46AE-9503-6BBEE8DAF916.htm),
  [editing behavior](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-3740EC59-FB75-47FD-B042-03D6AC095D4F.htm),
  [alternate-editor control codes](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-7D8BB40F-5C4E-4AE5-BD75-9ED7112E5967.htm),
  and [text-style ownership](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-7B194BD5-421E-4BC5-94A4-A805134003F4.htm)
  were treated as the observable CAD contract. Adopted word-boundary wrapping,
  long-word overflow, explicit columns/stacks, SHX file overrides, and the
  documented absence of SHX bold/italic; rejected character-splitting overflow,
  synthetic emphasis, and inference of compiled Unicode/Big Font layouts.
  [Skia's text overview](https://docs.skia.org/docs/dev/design/text_overview/)
  and [shaper stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite rendering](https://learn.microsoft.com/en-us/windows/win32/directwrite/rendering-directwrite)
  with [Direct2D integration](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
  [Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  and [cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm),
  [WebRender's rendering pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  and [retained text-run contract](https://searchfox.org/mozilla-central/source/gfx/thebes/gfxTextRun.h),
  [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
  [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
  and HarfBuzz's [cluster](https://harfbuzz.github.io/clusters.html) and
  [shape-plan](https://harfbuzz.github.io/harfbuzz-hb-shape.html) contracts were
  rechecked as the rendering/text architecture gate. Adopted their separation
  of reusable CPU font/layout results, immutable positioned content, retained
  display data, and renderer-owned device resources. Adapted that separation to
  lazy standard-SHX interpretation/cache lookup during snapshot preparation,
  one immutable analytic path per font/shape, one positioned generation reused
  by spatial culling, plan replay, printing, and exact selection, and no texture
  upload. Host discovery/substitution stays initialization work; ordinary
  visibility uses the existing spatial index; preparation remains eligible for
  worker execution; draw batching, DPI/subpixel transform, and device-loss
  recovery remain the existing path-renderer contracts. Standard SHX has no
  Unicode clusters, fallback shaping, variable-font state, hinting atlas, or
  glyph texture residency to emulate. Rejected GPU text layout, per-frame
  parsing/interpretation, backend-specific CAD glyph caches, retained uploads,
  and a native-only layout fork.
- For block attributes, Autodesk's current
  [attribute-definition/reference guide](https://help.autodesk.com/cloudhelp/2025/ENU/OARX-DevGuide-Managed/files/GUID-2107599E-9405-4D8B-A6DD-83D603B41568.htm),
  [ATTDEF DXF contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-F0EA099B-6F88-4BCC-BEC7-247BA64838A4.htm),
  [ATTRIB DXF contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-7DD8B495-C3F8-48CD-A766-14F9D7D0DD9B.htm),
  [`SetAttributeFromBlock`](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_AttributeReference_SetAttributeFromBlock_Matrix3d.html),
  [`SetAttributeFromBlock` definition-and-transform overload](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-ManagedRefGuide/files/OREFNET-Autodesk_AutoCAD_DatabaseServices_AttributeReference_SetAttributeFromBlock_AttributeDefinition_Matrix3d.html),
  [constant-reference ownership](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_AttributeReference_IsConstant.html),
  [attribute modes](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-67A2DDAD-2217-412F-8AEF-D4495192F45B.htm),
  [definition-property editing](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-F351FDBB-2731-40C1-A186-1B1F47779E32.htm),
  [ATTSYNC value preservation](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-56B14079-250B-4C99-AB3D-F95BA1C32AB7.htm),
  [ATTREDEF structural behavior](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-MAC-Core/files/GUID-CB8237C2-EB63-4839-9B84-5B890C979CBB.htm),
  [definition/default editing](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-889213DA-A3AF-4020-89F0-1E5049AD26EC.htm),
  and [MINSERT behavior](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-A780A2FA-4A2E-4574-950F-E788AB71F527.htm)
  were treated as the public CAD contract. Adopted definition-owned constants,
  reference-owned variable values, invisible display/plot suppression, embedded
  multiline content, transform-baked reference geometry, array-cell replication,
  default-only variable-definition edits, and ATTSYNC's all-reference property
  propagation with assigned-value preservation. Existing assigned references
  stay unchanged while future INSERTs inherit an edited default; property sync
  instead rebuilds reference properties from each definition and INSERT
  transform without replacing the assigned value. Adapted structural behavior
  to an explicit rejection until a handle-preserving reversible collection
  contract exists. Rejected rendering replaceable defaults, rewriting existing
  reference values during either edit, nontransactional structural repair,
  duplicating malformed constant references, and applying INSERT geometry twice.
  [Skia's shaped-text stages](https://docs.skia.org/docs/dev/design/text_shaper/),
  [DirectWrite/Direct2D separation and reusable layouts](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
  [Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
  [WebRender's retained rendering pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst),
  [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
  [Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
  and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html)
  were rechecked for the required rendering/text gate. Startup remains lazy and
  uses the existing font resolvers; shaping/layout results, scene culling,
  glyph/path cache keys and eviction, visibility-driven upload, worker-eligible
  snapshot preparation, GPU batching, physical DPI/subpixel transforms,
  fallback/variable-font identity, and device-loss/atlas invalidation all remain
  in their existing typed owners. The slice adds no new device resource or
  backend cache and rejects a per-attribute renderer or replay-time shaping.
  The exact approved in-repository implementation provenance is
  `CadSnapshotCompiler.cs` (`CompileText` and INSERT traversal),
  `CadSnapshotCompiler.MText.cs`, `CadSnapshotCompiler.ShxMText.cs`,
  `CadSelection.cs`, `CadPlanSceneCompiler.cs`, `CadAttributeEditing.cs`,
  `CadAttributeSynchronization.cs`, and `CadPrintPlan.cs`; these original
  ProGPU algorithms are directly reused.
  ACadSharp's public object model and pinned fixtures were inspected only to
  identify persisted contracts and independently observable placement; no
  dependency implementation text or structure was copied. The native C++ tree
  has no CAD document compiler, so a paired native ATTRIB frontend is not
  applicable. The unchanged generic native-picture compiler consumes the same
  retained TEXT/MTEXT commands and is covered by the matched regression.
- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/),
  [SkParagraph decoration declarations](https://skia.googlesource.com/skia/+/7a1bf999357aa755768f7b72265b91eea5c2758c/modules/skparagraph/include/TextStyle.h),
  and [Skia text guidance](https://skia.org/docs/user/tips/): adopted separation
  and reuse of shaping, formatting, and positioned-glyph drawing; retained the
  existing ProGPU/HarfBuzz implementation instead of adding another text stack.
- [Skia CanvasKit paragraph shaping](https://skia.org/docs/user/modules/quickstart/)
  and [SkParagraph implementation domains](https://skia.googlesource.com/skia/+/ae8d412b9a5947483bde5e695fc8e27c5eda7b09/modules/skparagraph/src/):
  adopted build/layout/draw separation, nested style ranges, cached positioning,
  and width-driven wrapping for MTEXT; rejected reparsing or shaping during CAD
  scene replay.
- [DirectWrite resource/layout model](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite),
  [DirectWrite strikethrough renderer contract](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextrenderer-drawstrikethrough),
  and [Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview):
  adopted device-independent semantic/layout results, device-dependent retained
  resources, and explicit flattening-quality tests; rejected fixed realizations
  as the only representation for unbounded CAD zoom.
- [DirectWrite formatted layout](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextlayout)
  and [Direct2D retained text-layout rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite):
  adopted range attributes, separate glyph/decoration callbacks, cluster-aware
  hit regions, and layout reuse for MTEXT; adapted callbacks into immutable CAD
  glyph and geometry streams.
- [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm)
  and [Win2D text-layout range methods](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm): adopted pay-
  once/draw-many retention, device identity, and range formatting; rejected per-
  frame creation and world-coordinate clipping limits.
- [Win2D retained rich text](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  and [custom text renderer](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Text_ICanvasTextRenderer.htm):
  adopted cached formatted layouts, final-font glyph callbacks, explicit range
  color, and separate draw/layout bounds; rejected measuring fallback from the
  requested font alone.
- [WebRender overview](https://github.com/servo/servo/wiki/Webrender-Overview)
  and [current profiler counters](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs):
  adopted serializable retained display data, off-thread scrolling/scene work,
  visibility stages, and explicit upload/cache/memory counters.
- [Firefox rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [WebRender transformed hit items](https://firefox-source-docs.mozilla.org/gfx/AsyncPanZoom.html):
  adopted self-contained retained text/display streams, frame-time culling, and
  identical transform/clip treatment for render and hit data.
- [Vello retained scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
  [encoding roadmap](https://github.com/linebender/vello/blob/main/doc/roadmap_2023.md),
  and [glyph-run encoder contract](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html):
  adopted transform-independent analytic encodings, retained fragments, GPU
  transforms, typed resources, and glyph runs; adapted to ProGPU generations.
- [Parley text stack](https://github.com/linebender/parley) and
  [layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
  and [decoration metrics contract](https://docs.rs/parley/latest/parley/layout/struct.Decoration.html):
  adopted reuse of font context, Unicode analysis, shaping, line layout, and
  positioned results; kept CAD text styling outside Unicode shaping identity.
- [HarfBuzz shaping plans/caching](https://github.com/harfbuzz/harfbuzz/blob/main/docs/usermanual-opentype-features.xml)
  and [cluster contract](https://harfbuzz.github.io/clusters.html):
  adopted reusable shaping inputs/results keyed by font, direction, script,
  language, features, variations, and content; no CAD-specific glyph remapping.
