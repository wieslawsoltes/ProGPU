# System.Drawing custom-cap and compound-pen contract

## Source contract

This slice restores the exact .NET 10 `CustomLineCap` and `AdjustableArrowCap`
types and the six `Pen.CompoundArray`, `Pen.CustomStartCap`, and
`Pen.CustomEndCap` accessors. The pinned `System.Drawing.Common` 10.0.11
reference assembly defines public shape. Observable defaults, validation,
ownership, disposal, cloning, and nonfinite scalar behavior were checked against
the source-reused upstream WinForms
[`CustomLineCapTests`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Drawing2D/CustomLineCapTests.cs),
[`AdjustableArrowCapTests`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Drawing2D/AdjustableArrowCapTests.cs),
and [`PenTests`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/PenTests.cs).
The portable geometry and rendering implementation is original ProGPU code.

`CustomLineCap` snapshots its fill and stroke paths, stores the four ordinary
base-cap values, preserves arbitrary `LineJoin`, inset, and width-scale state,
and deep-clones its geometry. Fill contours must be closed and attach across the
local baseline, matching upstream failure behavior. Stroke-cap selection is
transactional. Disposed caps reject further use with the same managed exception
family as the upstream contract. `AdjustableArrowCap` preserves nonsensical and
nonfinite public scalar state but emits no fabricated geometry when the values
cannot describe a finite arrow.

A `Pen` owns cloned custom caps and a cloned compound array. Getters return
independent snapshots, cloning a pen deep-copies all new state, and disposal
releases the owned caps. A compound pattern requires an even count of at least
two fractions in `[0, 1]` and nondecreasing order. Native-compatible NaN values
remain storable because ordered/range comparisons do not classify NaN as an
invalid fraction; nonfinite bands are skipped explicitly by rendering.

## Typed geometry model

For full stroke width `W`, each compound pair `[a, b]` becomes an independently
widened band with width `(b - a)W` and centerline offset
`((a + b) / 2 - 1/2)W`. Line and curve centerlines first use the existing
adaptive flattener. Offset vertices use intersections for ordinary joins, clamp
excessive miters, and treat each stroked run independently so an unstroked
connector cannot alter the next painted band. Segment stroked/smooth metadata
and explicit closure are retained. Dash intervals and offset are rescaled by
`W / bandWidth`, preserving physical dash lengths when the existing widener
normalizes them to the active band width.

Custom-cap paths use a typed local coordinate system: local X maps across the
endpoint normal, local Y maps along the outward tangent, and both axes scale by
`abs(W) * WidthScale`. `BaseInset * abs(W)` moves the cap attachment back along
the centerline. The cap's base cap remains the underlying line termination.
Fill geometry is appended directly; stroke geometry uses the cap's own join and
endpoint-cap state and is flattened with a scale-adjusted error tolerance before
using the shared stroke widener. Filled and outline adjustable arrows are built
from the same retained local contour.

The resulting nonzero-fill geometry is the one source of truth for production
bitmap/retained rendering, `GraphicsPath.Widen`, pen-aware bounds, and outline
hit testing. It composes with pen transforms through the existing inverse-tip
model. No HDC, GDI+ handle, runtime reflection, private-field scan, renderer-side
object probe, or fake drawing object is introduced.

## Quality and performance gates

Twelve focused tests cover upstream-compatible state and validation, path and
cap snapshot ownership, derived cloning, compound-array transactionality,
production center gaps, filled and outline arrows, generic fill and stroke caps,
endpoint orientation and inset, widened bounds, outline hit testing, nonfinite
state, zero-allocation warmed arrow mutation, and bounded geometry allocation.
The complete drawing suite passes 319/319 in Debug and Release.

`GraphicsPathBenchmarks.WidenCompoundArrowPenClone` measures an open four-point
centerline with two compound bands, a round join, and a filled adjustable end
arrow. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 3.757 microsecond
median (3.488 microsecond mean, 0.594 microsecond standard deviation) and 9.27 KB
allocated. One launch, three warmups, three measured iterations, and denied
process-priority elevation make this coarse local subsystem evidence. The
focused test independently enforces an 8-12 KB warmed allocation window and
exactly zero bytes across 10,000 cap-state mutation groups. Geometry work is
linear in the number of compound bands times flattened segments plus emitted
stroke triangles.

ApiCompat removes two missing-type and six missing-member suppressions. Measured
debt falls from 11 missing types, 104 missing members, 15 other diagnostics, and
130 total to 9 missing types, 98 missing members, 15 other diagnostics, and 122
total, with no new incompatibility or stale suppression.

## Remaining differential work

This slice establishes useful portable behavior but does not claim pixel-exact
GDI+ equivalence. Alternate-fill custom caps with nested same-direction contours
currently enter the shared nonzero widened result; a later typed contour
normalization or fill tessellation step must preserve even-odd holes without a
GPU readback. Acute/self-intersecting compound offsets also need Windows GDI+
image differentials before their miter pixels can be called exact. Those are
rendering-quality follow-ups, not reasons to reintroduce API suppressions or an
opaque native-handle path.
