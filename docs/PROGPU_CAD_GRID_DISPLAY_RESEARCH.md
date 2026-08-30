# ProGPU.CAD adaptive drafting-grid display research record

Date: 2026-08-30

## Scope and primary sources

This slice adds a visible rectangular drafting grid to the shared desktop/browser
plan canvas. It captures the active VPORT display state independently from point
snap, adapts density during zoom, honors drawing-limit clipping, and renders all
visible dots through one retained affine GPU primitive. It does not approximate
isometric mode, infer an unavailable GRIDSTYLE value, render line-style major
lines, follow a transient dynamic UCS, edit persisted drafting settings, or add
arbitrary-camera grid-plane projection.

The implementation was designed clean-room from public behavior and format
contracts:

- Autodesk's [grid and snap behavior overview](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-FEA6BC6E-D81E-4AD2-BD4C-70078C57709A.htm)
  defines the displayed grid and input snap as independent settings, permits
  rectangular X/Y spacing and rotated UCS alignment, distinguishes dots from
  line/major-line styles, and describes adaptive zoom behavior.
- Autodesk's [GRID command reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-MAC-Core/files/GUID-7EC38AD6-FA34-4115-9E1C-6F13E1BA033D.htm)
  defines adaptive suppression while zooming out, optional subdivision while
  zooming in, independent aspect spacing, limits behavior, and major cadence.
- Autodesk's [GRIDDISPLAY reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-LT-MAC/files/GUID-4D6AC943-FC9C-4CB8-A4E6-AD7313BF9C3A.htm)
  defines bit 1 as beyond-limits display, bit 2 as adaptive density, and bit 4
  as below-base subdivision when adaptive display is active.
- Autodesk's [GRIDMAJOR reference](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-94C8162E-B852-469D-B434-5BB822B0215C.htm)
  defines a valid major cadence of 1 through 100 and an initial value of 5.
- Autodesk's [VPORT DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-8CE7CC87-27BD-4490-89DA-C21F516415A9.htm)
  identifies the first `*ACTIVE` VPORT as current and defines its persisted grid,
  snap, UCS, and display-behavior records.

No third-party implementation source was copied, ported, translated, or used as
an implementation template. Exact approved source provenance is the existing
ProGPU-owned `DrawDotGrid`, vector shader, retained command compiler, semantic
native geometry stream, `CadPlanViewport`, and active-VPORT grid-snap capture.
The in-repository ACadSharp contract at pinned commit
`6353e17ebcf4c6c57479a9998a9e05738b180f9c` supplies typed `VPort.ShowGrid`,
`GridSpacing`, `GridFlags`, `MinorGridLinesPerMajorGridLine`, UCS, SNAPBASE,
SNAPANG, and model-limit values; no ACadSharp source change was required.

## Adopted display and GPU contract

`CadSnapshotCompiler` captures immutable `CadPlanGridDisplaySettings` separately
from `CadPlanGridSnapSettings`. GRIDMODE therefore controls pixels and SNAPMODE
controls pointer acquisition without either silently enabling the other. The
rectangular display basis composes normalized active-VPORT UCS axes, SNAPBASE,
and SNAPANG exactly as the existing snap lattice. Invalid spacing, cadence,
limits, non-finite state, non-orthogonal axes, isometric style, or an edge-on
WCS-XY projection fails closed.

For persisted spacings `sx,sy`, projected axis lengths `px,py`, camera zoom `z`,
minimum device separation `m = 8`, and major cadence `k = max(2, GRIDMAJOR)`, the
adaptive planner multiplies both spacings by `k` until
`min(sx*px*z, sy*py*z) >= m`. When GRIDDISPLAY bit 4 permits subdivision, it
then divides both by `k` while the next level still meets `m`. The shared factor
preserves the authored rectangular aspect and major proportion. The loops are
hard-bounded to 32 levels and all overflow/non-finite results fail closed.

The planner inverse-projects the four visible clip corners into the grid basis,
adds one-cell guard space, and creates one local rectangle plus one affine
local-to-screen matrix. GRIDDISPLAY bit 1 selects either the entire plan viewport
or the intersection with WCS model limits. Planning is O(1), allocation-free,
and independent of the number of visible dots.

`DrawingContext.DrawDeviceDotGrid` records one typed command with rectangular
spacing, physical-pixel radius, local bounds, brush, and affine transform. The
managed compositor and native semantic compiler both lower it to exactly four
vertices and six indices. The existing stable native geometry record is retained:
positive `stroke_thickness` selects the fixed-device variant, `p3` carries X/Y
spacing, and zero `p2` distinguishes it from the original scalar local-radius
grid. There is no ABI layout change and no extra managed/native crossing.

Canonical `Vector.wgsl` evaluates a fixed nine-neighbor lattice around the local
cell. Fragment derivatives form the local-per-physical-pixel Jacobian; its inverse
maps candidate centers to framebuffer space, where centers snap to one quarter of
a physical pixel and Euclidean radius stays fixed under rotation, anisotropic
scale, and ordinary shear. A singular Jacobian produces zero coverage. Work is
fixed O(9) per covered fragment, private storage is O(1), texture samples are
unchanged, and grid density creates no CPU vertices, uploads, cache entries, or
draw calls.

## Rendering/text architecture gate and applicability

The required architecture comparison was rechecked against primary sources:
[Skia's staged text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender's rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
[Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html).
The adopted common principle is to retain compact semantic state and defer
view-dependent parallel coverage to rendering, rather than rebuilding a CPU
display list or resource per visible item. Startup/lazy font initialization,
shaping/layout reuse, visibility of CAD entities, glyph/path/image cache keys and
eviction, demand-driven upload, worker preparation, DPI text hinting, fallback
fonts, variable-font state, and device-loss invalidation do not change.

This is a shared rendering primitive, so managed/native parity is applicable and
implemented. Both consume the same canonical shader and emit the same four
vertices, six indices, shape type 25, rectangular spacing, physical radius,
affine transform, brush, alias flag, clip, and failure semantics. The stable C
ABI record layout and generated C# wire declarations are unchanged. The shared
desktop/browser canvas records the same command with a dynamic theme brush.

## Verification and remaining gates

Focused managed tests cover command recording and invalid parameters, one-quad
compilation, active-VPORT capture independent of SNAPMODE, rotated origin/basis,
GRIDDISPLAY flags, GRIDMAJOR cadence, adaptive coarsening, below-base subdivision,
limits clipping, isometric/edge-on rejection, 1,024 zero-allocation plans, shared
canvas draw ordering, and native semantic wire lowering. The Clang C++ regression
covers fixed-device decoding, vertex parameters, capacity, and the preserved
legacy variant. Existing headless shader coverage verifies that the modified
canonical module compiles and retains periodic-dot transparency.

GRIDSTYLE line grids, emphasized major lines, exact isometric lattices, transient
dynamic-UCS following, persisted settings UI, screenshot goldens at multiple DPI
scales, extreme-shear visual differentials, representative large-drawing GPU
p50/p95/p99 measurements, and DXF/DWG round-trip fixtures remain before the
broader drafting-grid area is complete. This functional slice makes no unmeasured
FPS or latency claim.
