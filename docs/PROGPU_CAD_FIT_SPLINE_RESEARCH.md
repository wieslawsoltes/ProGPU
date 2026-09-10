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
and derived control geometry. The original DXF writer did not serialize the
Uniform fit parameterization: a direct writer/readback characterization observes
Chord instead, with controls still absent. `CadDocumentStore` initially rejected
fit-only DXF writes with `CADSAVE002` before touching the destination or saved
generation. The follow-up below closes exact control/knot serialization for
the admitted representation only; it is not general writer certification.

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

## Exact DXF export and DWG control precedence (2026-09-08)

The dependency's allocation-free `Spline.TryGetFitPointCubicBezier` query now
recognizes the same open two-point Uniform cubic contract. The DXF writer emits
four exact controls and eight clamped knots for that case, retaining the authored
fit points, derivatives, and fit tolerance. It does not modify the spline's
collections, flags, or parameterization. ASCII and binary DXF use the same path.
DXF has no corresponding Uniform parameterization field in its
[published SPLINE group-code contract](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-E1F884F8-AA90-4864-A215-3182D47A9C74.htm);
the explicit control representation therefore preserves the curve on reopen
and subsequent saves. This is geometric preservation, not preservation of every
authoring-mode distinction.

`CadDocumentStore` admits precisely that export and retains `CADSAVE002` for
other fit-only systems, before touching the destination or saved generation.
The conversion uses the endpoint identities cited above and the original
ProGPU-owned `CadSnapshotCompiler.CompileSpline` contract at commit
`bd442c450b6d0b9c5dda3e9e1247f580cbdb7f65`. No third-party solver was copied or
translated. The query is fixed `O(1)` work/storage; serialization remains
`O(C + K + F)` for controls, knots, and fit points, with no temporary curve clone.

Testing DXF-to-DWG exposed a second defect: the DWG writer chose fit records even
when explicit controls were present, discarding the authoritative curve. It now
selects the control record when controls exist and encodes scenario flags in
local variables instead of mutating the live entity. That record cannot retain
the fit-point authoring payload, so saving emits a warning naming the spline.
The original in-memory fit data remains unchanged. This limitation is visible
to `CadSaveResult.Diagnostics`; it is not described as a lossless metadata round
trip. Fit-only Uniform DWG continues using its fit record.

The real fixture's `434` curve has identical control/knot data, managed retained
geometry, and native semantic scene bytes before and after ASCII/binary DXF
export. Shared rendering, shaders, ABI, caches, resource lifetimes, and replay
costs are unchanged; no separate native CAD file writer exists to update. These
are serialization and normalized-scene regressions, not a new native pixel or
performance measurement.

The follow-up passes 1,625 Release CAD tests, 3,862 core renderer tests, and
280 headless tests. CAD regressions include exact DXF export,
DXF resave and DWG conversion, representative-fixture scene equivalence, and
transactional rejection of unsupported fits. All 39 dependency spline cases
pass, including 19 new cases for conversion rejection, source immutability,
and control precedence in DWG AC1024/AC1027/AC1032. Dependency net9.0 tests run with Major roll-forward on
.NET 10; they do not certify a native .NET 9 runtime. Release browser AOT publish
also completes for this source revision. The published macOS hardware browser
smoke passes with visible drawing/column text, pan/zoom, save/reopen/resave,
16 retained entity types, 660 frames, 688 dispatches, and a resized 2880x1800
framebuffer. The initial image was inspected. The sample smoke establishes host
workflow health, while the focused fixture tests establish this fit export.

Linux CAD browser CI run `34205114167` passes on the preceding browser-deadline
commit `bd442c45`; the new serialization commit still requires its own CI. The
earlier blank-frame failures above are historical, not a current failing result.
No performance improvement or exhaustive writer compatibility is claimed.

Precommit source/history marker scans reviewed all implementation additions on
the parent feature branch relative to `origin/main` and the dependency branch
relative to `master`. Matches referred only to original ProGPU-owned algorithms
or ordinary geometric derivation; no foreign implementation attribution or new
vendored implementation was identified. New tests use explicit mathematical
expectations and the existing independently authored fixture.
