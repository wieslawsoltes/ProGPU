# ProGPU.CAD Architecture and Delivery Specification

Status: foundation, 2026-08-28

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
- `src/ProGPU.Scene/Shaders/AcisSolid.wgsl`, `Hatch.wgsl`, `Line3D.wgsl`, and
  `RetainedGlyph.wgsl`.
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
- SOLID entities retain exact OCS-normalized corners and render as filled paths.
  3DFACE entities retain WCS corners and invisible-edge flags; the plan
  wireframe records only visible edges while preserving the same face record
  for the later shaded/depth compiler. Nonzero ellipse/SOLID thickness is
  reported until complete 3D side-surface lowering is available.
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
  TEXT and default-insertion dual-orientation vertical TEXT lowering. The
  immutable snapshot packs placements, coalesced horizontal decoration strokes,
  and affine text bases; the plan compiler records each drawable placement with
  its shared analytic glyph path. Compiled Unicode and Big Font containers plus
  non-default/decorated vertical placement remain explicit gates. Ordered,
  bounded desktop discovery is host initialization work rather than a render-
  path filesystem dependency.

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
  `ICadDocumentStore` service. Browser hosts supply streams or random-access
  adapters; engine APIs do not depend on native file dialogs.
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
CAD editor shell: properties/layer panels, arbitrary-camera projected selection,
editing tools, printing, and round-trip-certified output remain tracked
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
- The first commands translate, rotate, or uniformly scale a deduplicated stable
  handle set, set entity visibility, assign an existing layer, and add or remove
  one model-space entity. They resolve every model-space handle before mutation,
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
  Transform commands roll back an already-applied entity prefix if a later
  entity fails. Add requires a detached zero-handle object; remove retains the
  same object for undo and treats a collection cancellation as a failed command
  with no published generation.
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
- Layer color assignment accepts indexed and true explicit colors, rejecting
  `ByLayer`, `ByBlock`, and the header-only `ByEntity` sentinel. It restores each
  prior table value and updates inherited entity RGB through the same immutable
  snapshot style resolution used by both picture compilers.
- Layer lineweight assignment accepts declared explicit widths, hairline, and
  `Default`, while rejecting entity-only `ByLayer`/`ByBlock` and undefined wire
  values. Inherited entities resolve the new physical/cosmetic thickness in the
  immutable snapshot; undo restores each authored layer enum.
- Layer linetype assignment resolves an existing explicit table entry before
  mutation and rejects entity-only `ByLayer`/`ByBlock` targets. Inherited entity
  styles retain the resolved name in the snapshot, while non-continuous pattern
  rendering remains behind the documented A-alignment fidelity gate.
- Layer creation transfers one detached zero-handle `Layer` into the document
  table and reverses that ownership on undo. LIFO history guarantees dependent
  in-history edits are undone first; arbitrary populated-layer deletion remains
  unsupported until entity/block reference reassignment has a complete semantic
  transaction.
- Entity duplication uses ACadSharp's public detached-clone contract, applies an
  optional finite translation before ownership transfer, and retains the one
  cloned object across undo/redo handle reassignment. The source is never
  mutated, and failed source resolution publishes no generation.
- Linetype assignment uses the same table-identity and rollback contract for
  explicit, `ByLayer`, and `ByBlock` entries. The immutable snapshot retains the
  newly resolved linetype name. Entity linetype-scale assignment accepts only
  positive finite values and restores each prior authored scale. These edits do
  not imply dash rendering: non-continuous patterns continue through the
  explicit unsupported diagnostic until the documented A-aligned endpoint
  planner is implemented.
- Lineweight assignment accepts only declared `LineWeightType` values, retaining
  explicit widths plus `ByLayer`, `ByBlock`, `Default`, and hairline semantics
  without resolving them prematurely. Undo restores each authored enum value;
  the next immutable snapshot resolves it through the existing layer/block
  style contract, so both picture compilers receive the same physical or
  cosmetic stroke policy.
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
  Filled SOLID proximity uses the retained triangle union, while
  stroke-only 3DFACE proximity tests only non-degenerate edges not masked by
  its invisible-edge flags. It validates candidate generation and
  immutable header identity before indexing primitive buffers. Inclusive
  world-space box tests add distinct Window (whole selectable geometry) and
  Crossing (any intersection) semantics. Window containment consumes the exact
  retained extrema; crossing uses segment slabs, bounded analytic parameter
  partitioning at box-plane roots for affine circles/arcs/ellipses and bulges,
  exact rational spline partitioning at every real root against all six box
  planes, and the complete convex triangle/box separating-axis set for filled
  SOLIDs.
  3DFACE box tests again ignore masked and degenerate edges. These tests remain
  O(S) for S polyline/face segments, O(B * P^2 * R) for B spline spans and
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
  arbitrary-camera projected selection volumes, draw-order resolution, and
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
  Multi-selection Delete is not exposed by this shell slice: the pinned public
  ACadSharp collection contract provides cancellable single-item removal but no
  atomic range removal. Sequential removal followed by public re-add rollback
  can assign new handles after a mid-batch cancellation, so presenting that as
  one atomic Delete/Undo action was rejected. A reviewed dependency transaction
  contract or an independently safe ProGPU command boundary must land before the
  UI enables multi-delete.
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
expanded entity counts. Dynamic evaluation graphs, XRefs, and attribute text
are explicitly diagnosed rather than rendered approximately. Shared block-
fragment/GPU instance reuse remains a later optimization after inherited-style
variants and instance hit identity are fully specified.

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
indentation/tab payloads, vertical flow, SHX/Big Font content, invalid numeric
state, and content exceeding persisted column capacity remain explicit typed
diagnostics rather than degraded output.

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
without changing glyph identity. Missing shapes, malformed controls, surrogate
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
and SHX placements share one document-wide glyph budget. Freezing the recorded
scene uses the normal retained picture ownership contract.

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

Catalog extraction and model-space page rotation are now implemented, but this
foundation does not claim layout/paper-space viewport lowering, DCS camera/view lowering,
CTB/STB overrides, shaded-viewport policies, transparency flattening, PDF/SVG,
raster encoding, printer enumeration/spooling, or multi-page collation. Those
remain explicit typed compilers/adapters and conformance gates; unsupported
features are not silently rasterized or dropped. This CPU metadata/lowering
slice changes no shader, stable C ABI, native renderer, compositor, atlas, or
device-loss behavior. Both managed and native renderers continue to consume the
same existing retained picture, clip, affine transform, shaped text, analytic
path, and fixed-stroke commands, so no paired native implementation applies.

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
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --complex-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --linear-spline-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --nurbs-spline-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 10000 --periodic-spline-linetypes --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 1000 --spline-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --text-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --text-selection --warmup 3 --iterations 24 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --block-array-columns 10000 --warmup 5 --iterations 100 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --warmup 5 --iterations 50 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --text-entities 1000 --text-decorations --warmup 3 --iterations 50 --queries 10000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --warmup 3 --iterations 24 --queries 1000
dotnet run --project src/ProGPU.CAD.Benchmarks -c Release -- --entities 0 --shx-text-entities 1000 --shx-decorations --warmup 3 --iterations 24 --queries 1000
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

Sources consulted on 2026-08-27 and 2026-08-28:

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
  only the documented detached-copy result and adds ProGPU-owned command state
  plus optional translation. Rejected
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
  the paper margins is the current geometric offset authority.
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
  neither reshapes text nor introduces a foreign document renderer.
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
