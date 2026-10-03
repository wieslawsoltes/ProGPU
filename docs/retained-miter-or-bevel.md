# Retained miter-or-bevel strokes

`PenLineJoin.MiterOrBevel` and `NativeStrokeJoin.MiterOrBevel` use explicit
value 3. The selected join retains its miter below the limit and becomes a bevel
above it. It does not inherit WPF's clipped-miter or reversal-extension policy.
Existing values 0/1/2 retain their existing behavior; the separate native MIL WPF
pen contract still admits only its original three values.

## Source contract and retained implementation

Win2D documents the [MiterOrBevel join](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasLineJoin.htm)
and its [miter-length to half-width ratio](https://microsoft.github.io/Win2D/WinUI3/html/P_Microsoft_Graphics_Canvas_Geometry_CanvasStrokeStyle_MiterLimit.htm).
`CanvasStrokeStyle.GetOrCreatePen` now retains that distinct value instead of
throwing. The original style version, brush identity, width, cap/dash data and
normal/fixed/hairline policy still select the retained pen.

GDI+ explicitly defines [LineJoinMiterClipped](https://learn.microsoft.com/en-us/windows/win32/api/gdiplusenums/ne-gdiplusenums-linejoin)
as miter-or-bevel, despite the name. Its inside-to-outside miter length divided
by full width gives the same threshold ratio for centered straight segments.
Both Drawing pen paint conversion and `GraphicsPath` stroke-query/widen
conversion now preserve this as value 3. This does not infer GDI+'s distinct
clipped-`Miter` plane from Direct2D, nor change `LineJoin.Miter`.

The implementation reuses ProGPU's existing join writers. Both allocating and
span writers retain the under-limit miter topology and over-limit bevel; the
explicit value bypasses WPF-only clipped/reversal branches. Undefined values
are rejected before destination writes. The public `Pen.LineJoin` storage
property remains unchanged.

Scene preparation retains the same value through rectangle, linear-path,
emitted-outline and smooth-path admission. Bounds and materialized outlines
omit clipped-miter points for value 3. Existing retained dash and undashed
stroke keys already include the exact join, so style mutation cannot reuse a
different join's coverage. CPU outline hit tests use the same join writer.
Device-width descriptors, canonical vector shaders and triangle edge masks
retain the explicit miter/bevel topology; source caps, dashes, closed seams,
transforms and device-width decisions are not replaced.

The native scene compiler maps 3 explicitly and rejects undefined joins rather
than serializing them as miter. Native scene, polyline, dashed/path and geometry
query admission share the new value. The native WPF policy flag cannot turn 3
into a clipped join. Packed fields already have two join bits, so no record
sizes or offsets change, but this **is an additive public API/wire semantic**:
older native producers do not support it. Matching rebuilt producer/packages
and final consumer qualification remain required; downstream pins are unchanged.

## Remaining boundaries and qualification

The explicit value did not itself resolve legacy `Miter=0` policy differences.
The subsequent [source-qualified clipped-miter policy](retained-clipped-miter.md)
preserves generic bevel-overflow defaults and lets Win2D request clipped
paint/hit geometry matching its existing clipped bounds. The legacy generic
paint/bounds mismatch and original Drawing Miter clipping remain separate
contract work. Existing Direct2D rectangle dash,
default transformed-source and fixed/hairline admission limits remain separate.

`MiterOrBevelStrokeTests` adds 17 authored configurations: four threshold,
one reversal, four closed-seam, one cache/snapshot, one invalid-value and six
actual headless normal/fixed/hairline configurations. The latter compare full
cold/warm pixel buffers against the separately selected existing miter or bevel
route; they are not an original Microsoft raster oracle.

The existing separate-process Drawing runner has an opt-in `-StrokeJoins`
extension, forwarded as `--stroke-joins` to both applications. Its 12 cases
cross rectangle/acute contours, Miter/MiterClipped and limits 1/2/10. Each
retains 256 outline queries, exact bounds bits, and full paired DrawPath/FillPath
RGBA comparisons; Widen point/type transcripts are diagnostic only. The
original 13 clip/44 transform cases, provenance and deadlines are unchanged.

`MiterOrBevelCanvasTests` and `MiterClippedPenTests` add six configurations
each for the real source adapters. Three native helper groups are registered
in the existing internal and geometry-utility executables:

- eight threshold/WPF-policy triangle-and-mask combinations, plus reversal and
  untouched invalid-tail controls;
- four retained stroke-mode layouts and six solid/dashed semantic-path
  compilations, with failed-resource rollback;
- sixteen closed-seam/dash/limit public C query combinations, plus undefined
  join output-reset controls.

These native controls are source/resource/query checks, not additional native
GPU pixel qualification. Existing native provider and package gates remain
required. No builds,
tests, syntax checks, verifiers, original probes, GPU/UI runs, package checks
or CI were executed for this slice. This is not full Direct2D/Win2D/Drawing,
WPF application or cross-provider image qualification.
