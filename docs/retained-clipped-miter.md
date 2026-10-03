# Source-qualified clipped miters

`Pen.ClipMiterAtLimit` preserves an explicit centerline-relative clipped-miter
policy. The property defaults to false, and the existing `Pen` constructor and
enum-based join APIs are unchanged. It is effective only for `PenLineJoin.Miter`.
Bevel, Round and MiterOrBevel ignore it while retaining the raw property value
in snapshots, so a later join mutation does not lose caller intent.

Win2D `CanvasStrokeStyle` selects it for `CanvasLineJoin.Miter`. Direct2D's
owned clipped-miter geometry is distinct from [MiterOrBevel's over-limit bevel](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_line_join),
and Canvas defines its [limit relative to half the stroke width](https://microsoft.github.io/Win2D/WinUI3/html/P_Microsoft_Graphics_Canvas_Geometry_CanvasStrokeStyle_MiterLimit.htm).
The implementation reuses ProGPU's existing centerline-relative clipping
algorithm and source-reference receipts. It does not import another renderer.

## One retained policy through the existing paths

The additive typed `StrokeJoinGeometry.WriteLineJoin` overload takes a `Pen`
and resolves its clip policy separately from WPF reversal behavior. Existing
enum writers keep their old defaults; the WPF writer retains its old clipping
and reversal behavior. The selected normal-width corner uses the existing
three-triangle clipped fan; clipping does not imply a square extension at an
exact reversal.

`StrokePathGeometry` hit/outline geometry and the compositor use that same
typed writer. For the new true policy, paint and hit geometry now agree with
the existing clipped rectangle, linear and materialized stroke bounds. Those
existing bounds/outline algorithms remain unchanged, including their legacy
false-policy behavior; this is not a global generic-miter correction. Closed
seams retain the selected paint policy. Existing cap, dash, transform,
tolerance and width arithmetic are not replaced.

`WithBrush`, compositor-derived pens, undashed stroke caches and materialized
linear-dash keys retain the raw bool alongside the exact join and miter limit.
The native scene compiler emits the new flag only for an effective Miter0
policy: polyline/stroke flag bit 11 or PathJoin primitive flag bit 7. Other
primitive kinds and non-Miter joins do not acquire a wire flag. Contradictory
raw native descriptors are rejected rather than silently changing policy.
The separate WPF flag and its existing device-width restrictions are unchanged.

Fixed and hairline analytic joins carry the independent policy through their
existing device-coordinate descriptor. They clip the actual device-width
corner rather than a source-space approximation. The existing direct shader
and CPU/native triangle routes remain the renderer; there is no new fallback,
additional draw pipeline, readback or host loop.

## Archive and package boundary

Picture archives now write version 7, retaining the raw bool after the existing
pen fields. Versions 1–6 still read with false. An explicit old-version write
with true fails rather than discarding intent, even when the current join makes
that intent inactive. Existing archive depth, storage and type gates remain.

The native record layouts do not change, but the additional flag is a new wire
semantic requiring matching rebuilt native producers and managed consumers.
Older producers reject it. This does not update downstream dependency pins,
renderer defaults or WPF's original source enum.

## Remaining source and qualification boundaries

Generic `Miter=0` still bevels on overflow unless explicitly opted in; the old
generic paint versus clipped-bounds/materialized-outline mismatch remains
unfinished. `MiterOrBevel=3` always bevels on overflow. This source-qualified
correction does not redefine Drawing's `LineJoin.Miter`:
[GDI+ specifies a clipping reference measured from the inner corner](https://learn.microsoft.com/en-us/windows/win32/api/gdiplusenums/ne-gdiplusenums-linejoin),
which is not permission to substitute the Direct2D centerline plane. Exact
Drawing Miter policy remains separate original-contract work. Drawing's
MiterClipped-to-MiterOrBevel mapping remains unchanged.

Focused source, geometry, archive, native transport and actual-render controls
are authored with this change. They are not executed qualification: no builds,
tests, syntax checks, verifiers, original probes, GPU/UI/VM runs, package runs
or CI have been performed. Original pixel/tolerance/transform and final package
gates remain required; no full Direct2D/Win2D/Drawing or source UI parity is
claimed.
