# Default transformed rectangle strokes

The Windows compatibility wrapper now uses the same owned materialized-rectangle
helpers as portable geometry for a direct nondegenerate rectangle, null stroke
style and positive diagonal intrinsic transform. This includes identity,
translation and independent positive X/Y scales. It corrects the previous Windows
ordering, which combined intrinsic and caller world transforms and consequently
scaled the pen with the source geometry.

The source rectangle is first mapped by its intrinsic transform. Bounds and hit
queries expand that rectangle by the unchanged requested stroke width, then apply
the caller world transform. `Widen` uses the existing specialized implementation,
not the generic path stroker. Its positive-width output remains exactly one
winding/open filled figure, one 26-point `AddLines` call after the initial point,
and no `SetSegmentFlags` callback. Zero width calls only `SetFillMode(winding)`.
The caller still owns and closes the sink. Sink flags initially set by a fixture
are not flags emitted by this algorithm.

## Existing evidence and ownership

Microsoft's [transformed geometry contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1transformedgeometry)
separates geometry transformation from stroke transformation; its
[Widen contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1geometry-widen%28float_id2d1strokestyle_constd2d1_matrix_3x2_f_float_id2d1simplifiedgeometrysink%29)
applies the caller world transform after widening. The implementation reuses
ProGPU-owned code, not Microsoft implementation source.

The established independent Windows comparisons in
`progpu_native_direct2d_compat_tests.cpp` originate in commits `afd3cf9a9`,
`553275728`, and `305bcf051`. They retain rectangle `(1,2,5,8)`, intrinsic
`(2,0,0,.5,5,7)` and caller world `(2,0,0,3,10,-4)`. At width 2 their literal bounds
are `(22,17,42,32)`, point `(23,24)` hits and `(30,24)` does not. They also compare
every positive/zero widening callback, figure kind, flag callback count and point
against actual system Direct2D. This correction connects that existing contract
to the Windows compatibility object rather than substituting a new contour.

The shared internal selector retains the exact source/factory interface while
reading it. Only `E_NOINTERFACE` with no returned rectangle selects an unrelated
source fallback; genuine failures remain failures, successful null interfaces
fail, and failure-owned nonnull interfaces are released. Rectangle bounds publish
only after complete successful materialization. Query outputs initialize before
work; the specialized widening algorithm prepares all points before sink calls.
No public ABI or managed source contract changes.

## Boundaries and qualification

Only the established positive-diagonal direct-rectangle lane changes. It does not
add nested transformed sources, reflected, rotated, swapped-axis or sheared
null/default widening. Other Windows source/transform fallbacks retain their
previous behavior and remain separate work; this is not full transformed-geometry
parity. Existing explicit solid strokes, dashed gates, fixed/hairline policy,
degenerate and collapsed-inner-contour gates remain independent. No epsilon,
target DPI inference, provider fallback or sink-layout normalization was added.

Controls are authored for the existing portable CPU entry point and genuine
Windows factory/Windows wrapper/portable comparisons. Six configurations retain
identity, translation and the original nonuniform intrinsic, each at width 2
and zero. Every bound, hit and ordered sink event is compared, with literal
point arrays independent of the product helper. Only `AddLines` batching is
normalized into ordered point events; no primitive, state callback, figure
boundary or coordinate is discarded. Thirteen product-only rejection
configurations preserve initialized outputs and untouched sinks; portable
geometry additionally retains its singular-intrinsic rejection. None requires
the original implementation to reject an intentionally unadmitted product family.
The prior base/default and explicit-solid controls remain unchanged.

Both native providers use
the same CPU compatibility implementation; this does not establish GPU/package
or application qualification. No build, test, verifier, syntax check, probe,
CI dispatch, GPU/UI or VM execution was performed for this change.
