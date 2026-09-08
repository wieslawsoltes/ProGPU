# Fit-only cubic rendering and basic-edit preservation

## Reproduced defect and bounded contract

The paired ACadSharp `samples/sample_AC1032_ascii.dxf` and
`samples/sample_AC1032.dwg` contain the same SPLINE handle `434`. DXF stores four
cubic control points and the clamped unit knot vector. DWG stores two fit points,
Uniform parameterization, and two non-unit endpoint derivatives, with no control
points or knots. Rejecting this DWG representation as malformed omitted a valid
visible curve.

For this open, nonperiodic, two-point uniform cubic, the controls are exactly
`P0 = F0`, `P1 = F0 + T0/3`, `P2 = F1 - T1/3`, `P3 = F1`, and the knots are
`[0,0,0,0,1,1,1,1]`. The independently stored DXF controls agree within `1e-10`
world units. Tangent magnitudes are essential; normalization changes the curve.

`CadSnapshotCompiler.CompileSpline` now lowers this representation once during
immutable snapshot capture. It does not modify ACadSharp fit data, call a mutable
dependency tessellator, flatten the curve, or add a second GPU spline engine.
Existing explicit controls/knots remain authoritative. Missing derivatives,
other parameterizations, multiple fit spans, and closed/periodic fit systems are
explicitly unsupported, not guessed. Nonfinite data, arithmetic overflow,
invalid degree/tolerance, and inconsistent weights remain invalid diagnostics
without publishing partial geometry.

This closes a representative rendering defect within the focused delivery
scope. It is not general fit interpolation or exhaustive spline certification.

## Research and original implementation

- [Autodesk: About Splines](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-58316136-30EB-499C-ACAD-31D0C653B2B2.htm)
  distinguishes fit and control representations, cubic fit curves, parameter
  spacing, tolerance, and optional endpoint tangents. Adopt the distinction;
  reject silently treating every fit system as the same curve.
- [Michigan Tech: Bézier derivatives](https://pages.mtu.edu/~shene/COURSES/cs3621/NOTES/spline/Bezier/bezier-der.html)
  gives the endpoint derivative equations used above. The conversion and tests
  are original implementations of those mathematical identities, not ports of
  another engine's spline solver.
- [SVG cubic path contract](https://www.w3.org/TR/SVG/paths.html#PathDataCubicBezierCommands)
  supports retaining cubic control geometry rather than substituting a polyline.

The cross-engine applicability review reuses the primary-source comparisons in
[the wide-polyline research](PROGPU_CAD_WIDE_POLYLINE_RESEARCH.md#2026-09-08-mixed-straight-widths):
[Skia](https://api.skia.org/classSkCanvas.html),
[Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview),
[Win2D](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm),
[WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html), and
[Vello](https://github.com/linebender/vello) inform the retained geometry boundary.
[SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
[DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
[Parley](https://github.com/linebender/parley), and
[HarfBuzz](https://harfbuzz.github.io/what-is-harfbuzz.html) remain the text contract
references. No shaping, layout, font fallback, variable-font state, hinting,
startup, or worker scheduling behavior changes here. Existing visibility,
scene/cache identity and eviction, demand upload, batching, DPI/subpixel policy,
atlas-generation invalidation, and device-loss behavior remain unchanged.

## Editing and saving

A focused MOVE regression exposed an independent dependency bug:
`Spline.ApplyTransform` transformed endpoint derivatives as positions. Moving
the curve by `(100,-30,4)` incorrectly moved an expected control from
`(103,-24,4)` to approximately `(136.333,-34,5.333)`.

The ACadSharp feature branch now applies the transform's linear part to tangents
through the public homogeneous-vector API with `w=0`. This preserves scale,
rotation, absent zero tangents, and translation independence even at large
origins. Normal/control/fit-point transformation retains the existing object
model path. The fix is in the dependency, not a ProGPU-specific edit workaround.

DWG save/reopen preserves the two fit points, parameterization, derivatives,
and derived control geometry. The current DXF writer does not serialize the
Uniform fit parameterization: a direct writer/readback characterization observes
Chord instead, with controls still absent. `CadDocumentStore` therefore rejects
fit-only DXF writes with `CADSAVE002` before touching the destination or saved
generation. Saving as DWG remains available. Lossless DXF conversion requires
exact control/knot serialization; that work is still open. Do not claim that a
renderable fit-only DWG has a certified DXF round trip.

## Complexity, parity, and validation

The admitted conversion is fixed `O(1)` arithmetic with four stack-resident
controls and eight constant knots. Existing snapshot control transformation and
publication stay `O(C + K + W)` for controls, knots, and weights. Unsupported
fit-data finite validation is `O(F)` for fit points. No new work occurs during
stable scene replay, and no performance improvement is claimed.

Managed and native rendering consume the same normalized CAD snapshot and
retained spline command. `CadPlanSceneCompiler`, `ProGPU.CAD.Native`, and
`GpuPictureNativeSceneCompiler` retain their existing spline lowering and
canonical shaders; there is no separate C++ CAD fit parser to update. No ABI,
shader, native wire layout, or per-primitive crossing changes.

Regressions cover exact controls/knots, preserved source data, point selection,
an affine INSERT, explicit-control precedence, unsupported/invalid inputs,
DWG round trip, transactional DXF rejection, the paired real fixture, and
MOVE/pivoted SCALE/undo/redo. Managed command buffers and native semantic streams
match the equivalent authored-control spline, including print output.
Headless comparisons require identical fit/control pixels and visible coverage
at 3x and 7x zoom, including an independently evaluated curve midpoint. Native
stream equality is not a claim of independently measured native pixel parity.

Final local Release checks: 1,586 CAD tests (24 new fit-spline cases), 3,862
core renderer tests, 274 headless tests (two new fit/control pixel comparisons),
and four focused ACadSharp regressions pass. The dependency tests target net9.0
with `DOTNET_ROLL_FORWARD=Major` on the installed .NET 10 runtime; this is not a
native .NET 9 runtime certification. Existing dependency warnings remain visible.

The preceding browser AOT build at ProGPU `8df6e7f2` passed the local smoke with
78 frames, 100 dispatches, a 2880x1800 resized framebuffer, and 16 model entity
types through DXF save/reopen. It predates this fit conversion. Linux CI run
`34194741029` passed CAD tests and AOT publishing but failed the blank-drawing
assertion; the preserved original error confirms that adapter-selection flags
did not solve presentation. Linux browser presentation and browser validation
of this final fit change remain open release gates.

Generated logs and images remain ignored under `artifacts/progpu-cad/` or test
build output; no screenshots, traces, or benchmark outputs are source commits.
