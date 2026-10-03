# Retained source WPF join policy

`Pen.UseWpfJoinSemantics` retains the explicit WPF source contract across managed
painting, geometry preparation and native scene compilation. Generic pens default
to false. This is distinct from `ClipMiterAtLimit`: source WPF also supplies its
reversal behavior and changes a smooth join to Round. Normal-width source join
values 0 through 2 are admitted; fixed-device, hairline and MiterOrBevel policy
combinations reject rather than silently using another law.

The existing source-built Showcase Layout action reaches typed stroked geometry
and the WPF pen adapters. Source inspection establishes the pen-policy and smooth
metadata mismatch; it is not an observed Showcase pixel failure. The corner and
reversal comparisons extend the existing geometry contract, without replacing the
acceptance application or claiming its UI qualification.

## Painting and retention

The typed join writer selects the existing owned WPF clipping/reversal algorithm.
Its directional overload consumes retained tangents directly, without constructing
temporary endpoints that can erase small directions at large coordinates. Managed
source paint and input share those source directions. Original generic paint keeps
its previous endpoint arithmetic. Smooth-source triangle edge ownership uses the
effective Round join, not the pen's overridden join. Ordinary explicit WPF writer
and allocating helpers likewise retain smooth-to-Round behavior; generic helpers
retain their existing smooth suppression.

Source primitive strokes use existing retained path compilation so the analytic
rectangle/ellipse shortcuts cannot ignore their join policy. Fill specialization
is unchanged. The source shim and real LibreWPF typed adapters select the policy;
cached-brush pens retain it before material replacement. Both solid and dashed
coverage, caps, gaps, transforms, source owners and closed seams remain part of
the original command, not a replacement bounds geometry.

Raw policy survives `WithBrush`, thickness/cap/material copies, command snapshots,
dash geometry keys, CAD material copies, overdraw forwarding and native benchmark
snapshots. Archive version 8 appends the policy after version 7's independent
clipping flag. Versions 1 through 7 read false; writing true to those old formats
rejects instead of discarding source intent.

Native scene compilation reuses the existing polyline WPF bit and the PathJoin
flag introduced by [native MIL path joins](native-mil-path-join-policy.md).
Smooth joins select Round only under the explicit source policy, and clipping
flags follow the emitted join kind. No native record layout or shader default is
changed. Matching producer binaries remain required for the PathJoin flag.

## Input and material bounds

The ordinary PathStroke hit predicate treats internal boundaries as round. It
cannot represent WPF joins merely by adding a few extra triangles: that would
retain incorrect rounded coverage behind Bevel or clipped Miter. The source-policy
route therefore uses exact source segment bodies with flat internal ends, plus
the shared retained joins and real source caps. Generic query behavior remains
separate. Bounds and materialized outlines retain the same reversal and smooth
metadata, including dashed runs and closed seams. Hit joins select the painter's
conformal world-space or affine source-space arithmetic, and retain gap caps and
the implicit closing segment after a trailing gap. Failed candidates restore
their original primitive/segment storage and accumulated bounds.

Authored controls are not passing evidence. The independent public source
`Geometry.StrokeContains` / contributing-pen bounds path currently constructs
`NativeGeometryQueryPen` for the Direct2D utility and carries no WPF policy. That
separate producer/source transport gap is **not** fixed by a retained `Vector.Pen`
flag and must remain open; no source-local stroker or false equivalence is added.

## Coordinated source dependency and qualification

The paired LibreWPF draft directly consumes this producer API. Its qualified
ProGPU gitlink stays unchanged until the coordinated final graph is rebuilt and
qualified. Those direct API references cannot compile or activate the new behavior
against the old producer. No reflection shim, guessed capability, automatic SDK
admission or unqualified pin update disguises that dependency.

The authored producer inventory includes 14 managed writer/paint configurations,
20 snapshot/cache/archive configurations, 22 widening/smooth/seam configurations
and 12 shim conversion configurations, plus 14 input/bounds configurations
(six actual GPU-query cases authored, not run) and seven native-compiler transport
configurations. The dependent LibreWPF draft authors 13 source adapter
and recording configurations. These comparisons have not been executed.
No builds, tests, syntax/source verifiers, new original probes, GPU/UI/VM work or
manual CI were run. Exact-head final rendering/input, original-Windows comparisons,
package consumers and application/platform qualification remain required before
merge or release. Device-width WPF joins and the independent public source query
gap are not admitted or reported complete.
