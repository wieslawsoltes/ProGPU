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

Focused controls cover actual MIL curved and tiled source routes, solid and
dashed joins, smooth Round selection, reversal, complete source ownership and
atomic rejected input. Native primitive controls distinguish the WPF flag from
independent clipping and reject unsupported wire combinations. These controls
are authored, not executed.

No build, test, syntax verifier, GPU/UI run, original probe, CI or package
qualification was performed for this slice. Exact-head provider rendering and
input, original Windows comparisons, complete package consumers and source
application gates remain required. This does not resolve the separate legacy
generic Miter paint/bounds mismatch or qualify original GDI+ clipping, device-width
WPF joins, or application performance. Pins, defaults and renderer selection
are unchanged.
