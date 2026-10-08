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
portable transformed winding/open representation remain intact. The latter emits
no segment-flags callback and leaves caller flag state unchanged.
Explicit solid zero-width bounds retain their old result; explicit zero-width
hit/widening stay unsupported. Degenerate source rectangles and exact collapsed
intrinsic centerlines are not new admission; no epsilon test is used.

Existing fixed/hairline bounds behavior is preserved, not newly qualified or
expanded. Dashed rectangles remain unsupported in these operations. The Windows
transformed null/default wrapper's positive-diagonal direct-rectangle lane is
corrected by the [paired default helper](direct2d-default-transformed-strokes.md).
Other default source/transform families retain the older intrinsic/world
composition fallback and remain a separate gap. This explicit-style slice does
not complete transformed strokes or generic non-rectangle behavior.

## Controls and integrated observations

The shared fixture covers five joins/limits across five frames: plain,
caller-world shear, and nonuniform/reflected/sheared intrinsic transforms followed
by world shear. Each of the 25 cases compares a rectangle to an independently
authored ordered path, records bounds and 20 hit/widened-region probes, and retains
literal corner and shear-extremum assertions. Windows runs the same inventory
through the real Microsoft factory, the Windows wrapper, and the portable factory,
then compares their complete observations. No expected output comes from a product
decoder or rendered-image substitution.

The integrated public-SDK probe on Windows ARM64 and x64 captured all 25 cases
and 2,000 containment/widened-containment results per architecture. Those Boolean
results match the portable macOS ARM64 observations exactly. Original rectangle
and original path bounds themselves differ by one float ULP for a world-sheared
round join. The combined intrinsic/world-shear round case also differs from the
portable analytic envelope by about 0.00006, inside the original requested
0.001 flattening tolerance. These are raw retained observations, not repaired
reference values.

The round-join check therefore validates curved X extrema against independent
disk support of the four authored vertices, using that same requested tolerance.
Straight Y extrema, the untransformed frame, all other joins, and every Boolean
query retain exact comparisons. This follows the public
[GetWidenedBounds geometric-error contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-getwidenedbounds%28float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_d2d1_rect_f%29);
it introduces no product geometry tolerance or pixel allowance.

Rejection controls cover foreign factories (including zero width), nonfinite or
invalid inputs, dash and degenerate gates, exact singular intrinsic transforms,
initialized failed outputs and untouched destination sinks. Two actual style1
controls retain the fixed/hairline base bounds and reject new hit/widen admission.
The existing default
rectangle/null-width fixtures remain unchanged.

The portable CPU suite passes locally. Full Windows product execution and both
provider/package gates remain pending. The shared CPU implementation and SDK
observations do not qualify provider rendering or applications.
