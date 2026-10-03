# Solid rectangle stroke geometry

The rectangle compatibility layer connects its existing public geometry queries
to ProGPU's owned path stroker for explicit same-factory solid styles, normal
stroke transformation, positive width and nondegenerate centerlines. It adds
join- and miter-aware `GetWidenedBounds`, `StrokeContainsPoint`, and `Widen`; it
does not add a renderer, public ABI, resource family or target-DPI policy.

## Source and transform contract

The existing `create_rectangle_path_geometry` TL → TR → BR → BL closed contour
is reused. A transformed rectangle retains those ordered corners after its
intrinsic source transform, not the enclosing axis-aligned rectangle. Reflection
therefore preserves orientation; shear and nonuniform scale alter the centerline
without scaling the pen. The existing path stroker then expands the centerline,
and only afterward applies the caller world matrix.

This ordering follows Microsoft's
[transformed geometry contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1transformedgeometry),
[widened-bounds contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-getwidenedbounds%28float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_d2d1_rect_f%29)
and [Widen contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-widen%28float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_id2d1simplifiedgeometrysink%29).
No Microsoft implementation was copied. Solid strokes avoid the unresolved
original rectangle dash-start/direction contract; the path's dash support is not
evidence that a rectangle can select that phase.

The internal shared helper owns its temporary path and source/factory references.
Numeric and factory checks precede publication. Optional `stroke_style1` discovery
uses normal policy only for `E_NOINTERFACE` with a null output; other failures are
preserved, successful null outputs fail, and returned references are released.
No fixed/hairline mode is silently reinterpreted as normal. Bounds and hit outputs
are initialized and receive a candidate only after success. The shared path
`Widen` prepares its outline before calling the destination sink.

## Preserved and remaining contracts

Null/default rectangle code is not routed through the new path. Its existing
base alternate-fill two-contour representation, including zero width, and the
portable transformed winding/force-unstroked representation remain intact.
Explicit solid zero-width bounds retain their old result; explicit zero-width
hit/widening stay unsupported. Degenerate source rectangles and exact collapsed
intrinsic centerlines are not new admission; no epsilon test is used.

Existing fixed/hairline bounds behavior is preserved, not newly qualified or
expanded. Dashed rectangles remain unsupported in these operations. The Windows
transformed null/default wrapper still has an older intrinsic/world composition
path that can scale stroke width; that is a remaining implementation gap, not a
claim that this explicit-style slice completes transformed strokes. Generic
non-rectangle fallbacks are unchanged.

## Authored controls, not execution

The shared fixture covers five joins/limits across five frames: plain,
caller-world shear, and nonuniform/reflected/sheared intrinsic transforms followed
by world shear. Each of the 25 cases compares a rectangle to an independently
authored ordered path, records bounds and 20 hit/widened-region probes, and retains
literal corner and shear-extremum assertions. Windows runs the same inventory
through the real Microsoft factory, the Windows wrapper, and the portable factory,
then compares their complete observations. No expected output comes from a product
decoder or rendered-image substitution.

Rejection controls cover foreign factories (including zero width), nonfinite or
invalid inputs, dash and degenerate gates, exact singular intrinsic transforms,
initialized failed outputs and untouched destination sinks. Two actual style1
controls retain the fixed/hairline base bounds and reject new hit/widen admission.
The existing default
rectangle/null-width fixtures remain unchanged.

These controls are authored only. No build, test, syntax check, verifier, native
provider execution, original Windows probe, GPU/UI run or CI dispatch was performed.
The shared CPU geometry implementation is used by both native providers; that
applicability does not qualify provider rendering, packages or applications.
