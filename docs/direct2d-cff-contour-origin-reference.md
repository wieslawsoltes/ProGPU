# Independent original CFF contour-origin controls

This child adds 12 source configurations: two original CFF fonts, explicit or
null advances, and the three existing sideways frames. Both fonts have genuine
vertical tables but no VORG. The contours are cubic arches whose exact maxima
are 300/301.5 and 280; their control points instead reach 400/402 and 380. Literal
rotated cubic points observed independently through original DirectWrite calls
form the source-placement oracle; exact curve bounds have separate controls. The actual source run includes an empty glyph, signed
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

The original SDK integer metric fields remain named observations for both metric
orientations, with strict source-origin assertions. The second font has a301.5
curve maximum but integral402 control values; its observed origin482 is not a
rounding rule inferred from301.5.

The retained original Windows observations establish origins480/482 for the first
arch and340 for the second. Independent horizontal and sideways outlines at
em1000 and em15.625 confirm that these are actual placement values. The
DirectWrite adapter and literal expected coordinates now follow that source
policy. The font bytes, full-frame inventory and comparison strictness are
unchanged; exact natural-curve origins380/381.5 and240 remain separately tested.

After a successful original call and complete segment validation, exact control
point comparisons now contribute to the existing final failure total. This lets
the remaining original font families execute in the same run. Native/API and
structural failures still stop immediately, and any collected comparison failure
still rejects the process after normal resource retirement. All previous static,
variable, horizontal, sideways and full-byte controls remain required. The
completed inventory is diagnostic evidence, not source or package qualification.

Validation on 2026-10-09: all49 local macOS ARM64 native CTests pass. The original
Windows ARM64/MSVC software-adapter run completes1121 strict comparisons with128
remaining full-byte failures, down from204. All CFF control-point, source-origin,
variable bearing/origin and run-envelope comparisons pass. Twelve additional
original-origin assertions retain both metric orientations. This is source
placement evidence; the remaining pixel failures still reject qualification.
