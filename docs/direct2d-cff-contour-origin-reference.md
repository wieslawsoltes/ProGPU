# Independent original CFF contour-origin controls

This child adds 12 source configurations: two original CFF fonts, explicit or
null advances, and the three existing sideways frames. Both fonts have genuine
vertical tables but no VORG. The contours are cubic arches whose exact maxima
are 300/301.5 and 280; their control points instead reach 400/402 and 380. Literal
rotated cubic points, not a product extrema solver or control envelope, form the
independent oracle. The actual source run includes an empty glyph, signed
advances, asymmetric offsets and raw sideways BOOL values 1/-1.

Both native providers compare whole 64x64 RGBA frames for cold, warm and independent
geometry paths. Existing caller assertions retain one source draw, the exact
recorded command count and one submission. Source files are mutated after capture
to prove retained ownership; no later source-outline or table callback is used.

The original Windows companion creates genuine DirectWrite faces from the complete
owned bytes, checks Analyze family/face identity, reads back CFF/vhea/vmtx bytes
and verifies the actual absence of VORG. It compares every original single-glyph
cubic control point against the literal oracle, then compares full BGRA frames
from actual DrawGlyphRun, GetGlyphRunOutline, independent geometry and prepared
geometry. Null advances are compared with the actual SDK vertical advances, and
same-owner original warm replay must remain byte-identical. Every frame must have
nonempty red ink, exact other channels/alpha and an untouched black corner.

The original SDK integer metric fields are printed as named observations for both
metric orientations. In particular, this source does not assume how a 301.5 design
maximum is rounded into INT32 metadata. The precise contour/pixel expectation is
still strict: if the original implementation disagrees at final execution, that
is a real pending contract to diagnose, not a tolerance waiver or a claim that
observing metrics qualified the renderer.

Integrated original Windows ARM64 execution now completes all 12 configurations.
The unchanged first arch produces source origin 480 (482 for the second font),
while the literal tight-curve contract requires 380 (381.5). Independent original
horizontal and sideways outline observations confirm that the difference is in
source placement, not the retained font bytes or a rounded metric display.
The source-origin policy remains unresolved; no literal, font or product
arithmetic has changed to conceal the difference.

After a successful original call and complete segment validation, exact control
point comparisons now contribute to the existing final failure total. This lets
the remaining original font families execute in the same run. Native/API and
structural failures still stop immediately, and any collected comparison failure
still rejects the process after normal resource retirement. All previous static,
variable, horizontal, sideways and full-byte controls remain required. The
completed inventory is diagnostic evidence, not source or package qualification.
