# Native MIL path-join policy

## Source connection

Native MIL ordinary polylines already select WPF join semantics. Curved or
smooth contours and TileBrush pen masks now select the same policy when they
enter the existing semantic path stroker. The selected policy includes both
clipped miter overflow and WPF reversal behavior; the independent clipped-miter
flag alone cannot represent it.

The source keeps its original pen join values 0 through 2. The semantic style's
`wpf_join_semantics` becomes `PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS`
(bit 8) on emitted PathJoin records. The flag is valid only for normal-width
PathJoin primitives with those source join values. Fixed-device, hairline,
MiterOrBevel, other primitive kinds and contradictory descriptors do not gain
admission. The C and managed flag declarations are paired. Record layouts do not
change, but the additional flag requires matching rebuilt native producers;
older binaries do not implement its semantics.

Both native renderers and retained native input consume the existing owned join
triangles under the selected policy. The source call sites retain their original
segment/tangent frames, brushes, uniform guidelines, caps, dash intervals and
phase, gap partitions and closed seams. Tile masks retain their own capture
frame and are not replaced by bounds geometry. The shared compiler's candidate
publication and rollback behavior remains in place.

When a source input index is requested, tiled strokes retain the original lowered
bodies, dash caps, joins and filled segments in a separate input scope. Their
original primitive transforms and source clips are unchanged. The existing mask
and tile paint commands occupy a render-only scope; mask bounds and tile quads
are not source hit geometry. No extra stroke solve or pixel readback is used, and
both native providers consume the same scene and input records.

Retained render-data packets also prevent deletion of their typed native
dependencies, even after detachment from a Visual. Deleting or replacing the
owning render data releases those references. Deletion and cache revision use
one framed handle visitor, retaining ordered cache dependencies and the existing
exclusion of managed-only legacy BitmapEffect indices. Rejection remains inside
the original atomic channel transaction. External video remains unadmitted for
cache rendering even though its native handles receive deletion protection.

Path-stroke material bounds retain existing curve/body/cap measurement and union
actual WPF PathJoin coverage emitted from the same prepared contours and original
double dash inputs. The shared renderer's triangle bounds use the same
affine/uniform branch and no AA fringe. Exact min/max endpoints are retained
until the complete bounds are published. The corrected source-space painted
extent is shared by relative brush mapping and tile allocation; absolute
mappings, captures and ownership are unchanged. No blanket inflation or
replacement bounds geometry is used. Original failure and degenerate-cap
handling remains in place.

## Smooth joins are round, not absent

This connection preserves the existing semantic `smooth_join` conversion to
Round, including the selected WPF policy on that resulting join. It does not
use the managed generic writer's no-adornment shortcut as an original WPF
contract.

Behavior research used the immutable original WPF revision
`381194e1ffe4d64fb747556fcaf76e1c34fe9df8`:

- [ByteStreamGeometryContext](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/ByteStreamGeometryContext.cs#L679-L718)
  retains the actual `IsSmoothJoin` flag in the source segment record.
- [Figure's flag reader](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/geometry/figure.h#L167-L170)
  exposes that source flag to widening.
- [The corner handoff](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/geometry/strokefigure.cpp#L937-L944)
  passes the smooth value to the round-selection argument, whose
  [corner contract](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/geometry/strokefigure.cpp#L2061-L2092)
  selects Round independently of the pen's requested join.

These sources establish behavior only. No foreign widening implementation was
copied or translated. The implementation reuses ProGPU's existing semantic path,
dash walker and WPF join geometry. Direct2D's separate forced-round segment
contract and independent clipped-miter source policy remain unchanged.

## Authored controls and remaining gates

The new `NativeWpfPathJoinTransportTests` file authors ten managed constructor
configurations: three admitted source join values, three rejected primitive
kinds, three rejected join/width combinations, and independent clip-only versus
WPF versus legacy policy. Rejected construction leaves no published candidate;
these controls do not load a native library or exercise a GPU.

Two native internal groups cover actual primitive triangle routing, affine
frames, edge ownership, clipping thresholds, reversal and semantic compilation
of closed solid/dashed paths. Unsupported wire descriptors and failed semantic
compilation retain seeded outputs. Bounds controls compare unpadded emitted
coverage and literal transformed reversal extents, distinguish independent
clipping, and retain both outputs on invalid input.

The MIL fixture retains eight configurations: solid/tiled brush, corner/reversal
and solid/dashed path. It authors 32 complete pixel renders per provider
(cold, warm, independently recreated source and brush mutation), source-owner
input queries, rejected source changes and smooth Round updates. Its independent
full-frame expectations remain strict. Direct submissions stay exactly one;
tiled submissions allow the existing one-to-two mask-preparation path without
claiming cache or performance qualification.

The existing original-WPF runner additionally authors those eight ordinary
stroke configurations, 24 cold/warm/independent-source BGRA captures and 36
point queries. It retains the existing runner deadline, shader inventory and
architecture controls; these ordinary strokes do not require software shader
availability. Captures and failure receipts precede assertion failure.

All controls in this slice are authored, not executed. In particular, the
original-WPF companion is not a new authorized probe or an observed comparison.

No build, test, syntax verifier, GPU/UI run, original probe, CI or package
qualification was performed for this slice. Exact-head provider rendering and
input, original Windows comparisons, complete package consumers and source
application gates remain required. This does not resolve the separate legacy
generic Miter paint/bounds mismatch or qualify original GDI+ clipping, device-width
WPF joins, or application performance. Pins, defaults and renderer selection
are unchanged.
