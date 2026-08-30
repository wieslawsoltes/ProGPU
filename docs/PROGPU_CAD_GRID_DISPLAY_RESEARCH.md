# ProGPU.CAD adaptive drafting-grid display research record

Date: 2026-08-31

## Scope and primary sources

The initial slice adds a visible rectangular drafting grid to the shared
desktop/browser plan canvas. It captures the active VPORT display state
independently from point snap, adapts density during zoom, honors drawing-limit
clipping, and renders the lattice through one retained affine GPU primitive.
The follow-ups add generation-safe persisted GRIDMODE, SNAPUNIT, GRIDUNIT,
GRIDDISPLAY bits 1/2/4, GRIDMAJOR, SNAPSTYL, and SNAPISOPAIR editing; the
registry-backed host GRIDSTYLE Lines/Dots choice; and exact Left/Top/Right
isometric dot grids. Dynamic-UCS following/editing and arbitrary-camera
grid-plane projection remain separate contracts.

The implementation was designed clean-room from public behavior and format
contracts:

- Autodesk's [grid and snap behavior overview](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-FEA6BC6E-D81E-4AD2-BD4C-70078C57709A.htm)
  defines the displayed grid and input snap as independent settings, permits
  rectangular X/Y spacing and rotated UCS alignment, distinguishes dots from
  line/major-line styles, and describes adaptive zoom behavior.
- Autodesk's [GRID command reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-MAC-Core/files/GUID-7EC38AD6-FA34-4115-9E1C-6F13E1BA033D.htm)
  defines adaptive suppression while zooming out, optional subdivision while
  zooming in, independent rectangular aspect spacing, limits behavior, major
  cadence, and that Aspect is unavailable for isometric snap.
- Autodesk's [SNAP command reference](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-F47F4AAF-4859-45D4-846C-3742268834A9.htm)
  defines the equal-aspect isometric lattice and states that a lined grid does
  not follow the isometric snap grid.
- Autodesk's [ISOPLANE command reference](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-9B1EEA63-BEC1-413E-B69F-541B5865F1A1.htm)
  defines Left as 90/150 degrees, Top as 30/150 degrees, and Right as 90/30
  degrees, and documents F5/Ctrl+E cycling.
- Autodesk's [Function Key Reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-ACAA0279-047D-458E-889F-60BBFDD40489.htm)
  defines F5 as cycling the 2D isoplane setting, while the
  [SNAPISOPAIR reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-10E95216-5E3C-45F2-A6B9-79E7660A1F60.htm)
  confirms drawing persistence and numeric Left/Top/Right values.
- Autodesk's [GRIDDISPLAY reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-LT-MAC/files/GUID-4D6AC943-FC9C-4CB8-A4E6-AD7313BF9C3A.htm)
  defines bit 1 as beyond-limits display, bit 2 as adaptive density, and bit 4
  as below-base subdivision when adaptive display is active.
- Autodesk's [GRIDMAJOR reference](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-94C8162E-B852-469D-B434-5BB822B0215C.htm)
  defines a valid major cadence of 1 through 100 and an initial value of 5.
- Autodesk's [GRIDSTYLE reference](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-30FC52C7-A734-43EE-A08D-96717A4B4959.htm)
  defines model-space bit 1 as dots, zero as lines, and—critically—stores the
  setting in the application registry rather than the drawing.
- Autodesk's [GRIDUNIT reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-6E37252F-77E5-4266-8759-AAD5E5577F1C.htm)
  defines rectangular X/Y viewport spacing and the exact `0,0` inheritance from
  the current snap spacing.
- Autodesk's [VPORT DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-8CE7CC87-27BD-4490-89DA-C21F516415A9.htm)
  identifies the first `*ACTIVE` VPORT as current and defines its persisted grid,
  snap, UCS, and display-behavior records.

No third-party implementation source was copied, ported, translated, or used as
an implementation template. Exact approved source provenance is the existing
ProGPU-owned `DrawDotGrid`, vector shader, retained command compiler, semantic
native geometry stream, `CadPlanViewport`, and active-VPORT grid-snap capture.
The in-repository ACadSharp feature source at commit `592e5f1c` supplies typed
`VPort.ShowGrid`, `SnapSpacing`, `GridSpacing`, `GridFlags`,
`MinorGridLinesPerMajorGridLine`, `IsometricSnap`, `SnapIsoPair`, UCS, SNAPBASE,
SNAPANG, and model-limit values. The approved ProGPU fork changes add missing
R2007+ DXF group 60/61 writer emission and VPORT group 77/78 isometric writer
emission, each with independent round-trip regressions. ACadSharp `master`
remains untouched and synchronized with upstream.

## Adopted display and GPU contract

`CadSnapshotCompiler` captures immutable `CadPlanGridDisplaySettings` separately
from `CadPlanGridSnapSettings`. GRIDMODE therefore controls pixels and SNAPMODE
controls pointer acquisition without either silently enabling the other. The
rectangular display basis composes normalized active-VPORT UCS axes, SNAPBASE,
and SNAPANG exactly as the snap lattice. Isometric mode derives the exact active
30/90/150-degree pair from that basis and requires equal effective X/Y spacing.
Invalid spacing, cadence, limits, style/pair, non-finite state, unexpected axis
angles, or an edge-on WCS-XY projection fails closed.

For persisted spacings `sx,sy`, projected axis lengths `px,py`, camera zoom `z`,
minimum device separation `m = 8`, and major cadence `k = max(2, GRIDMAJOR)`, the
adaptive planner multiplies both spacings by `k` until
`min(sx*px*z, sy*py*z) >= m`. When GRIDDISPLAY bit 4 permits subdivision, it
then divides both by `k` while the next level still meets `m`. The shared factor
preserves the authored lattice aspect and major proportion. The loops are
hard-bounded to 32 levels and all overflow/non-finite results fail closed.

The planner inverse-projects the four visible clip corners into the grid basis,
adds one-cell guard space, and creates one local rectangle plus one affine
local-to-screen matrix. GRIDDISPLAY bit 1 selects either the entire plan viewport
or the intersection with WCS model limits. Planning is O(1), allocation-free,
and independent of the number of visible dots.

`DrawingContext.DrawDeviceDotGrid` records one typed command with affine lattice
spacing, physical-pixel radius, local bounds, brush, and transform. The
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

`DrawingContext.DrawDeviceLineGrid` reuses that same stable semantic command and
native primitive. A positive integral cadence distinguishes line mode from the
legacy zero-cadence dot mode. The managed compiler encodes negative
`cornerRadius` as the minor physical width; the native primitive carries
`p2={1,cadence}`, rectangular spacing in `p3`, and width in
`stroke_thickness`. No record size, enum value, generated wire declaration, or
managed/native crossing changes.

Canonical `Vector.wgsl` evaluates the nearest member of each of the two line
families. For local residual `r` and device gradient length `|g|`, the signed
device distance is `|r|/|g| - w/2`. A line whose integral index is divisible by
GRIDMAJOR uses width `2w`; the union is the minimum of the two family distances.
This is fixed O(1) work and O(1) private storage per covered fragment, retains
one quad and one draw regardless of grid density, and preserves one-physical-
pixel minor/two-physical-pixel major widths through rotation, anisotropic scale,
and ordinary shear. Singular Jacobians remain transparent.

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
vertices, six indices, shape type 25, affine spacing, physical radius,
line width/cadence when applicable, affine transform, brush, alias flag, clip,
and failure semantics. The stable C
ABI record layout and generated C# wire declarations are unchanged. The shared
desktop/browser canvas records the same command with a dynamic theme brush.
Because Autodesk documents that lined GRIDSTYLE does not follow isometric snap,
the canvas forces the existing dot primitive while isometric mode is active and
retains the host Lines preference for a later return to rectangular mode. This
adds no shader source, native record, crossing, primitive, or per-dot work.

## Persisted edit and shell contract

`CadPlanGridDisplayEditValues` is a detached typed value for the editable active
VPORT subset. `CadSetPlanGridDisplayCommand` captures the exact retained VPORT
identity and raw pre/post values, including unedited GRIDDISPLAY bit 8 and
unknown bits. Apply, Undo, and Redo are O(1), use one document generation each,
and reject identity replacement, unexpected mutation, invalid finite ranges,
invalid style/pair/aspect, or a no-op edit. The command owns SNAPUNIT, SNAPSTYL,
and SNAPISOPAIR as part of this drafting-grid edit, but mutates no SNAPMODE,
SNAPBASE, SNAPANG, UCS, limits, GRIDSTYLE, or transient host state.

`CadPlanGridPresentationStyle` owns the separate host-only Lines/Dots choice.
The shared desktop/browser view exposes one `Dots (GRIDSTYLE)` control and the
canvas defaults to Lines, matching Autodesk's initial registry value. Toggling
it invalidates only the canvas: it allocates no document command, advances no
content generation, changes no Undo/Redo entry, and cannot affect DXF/DWG save.
Persistence across application sessions remains a future host settings-store
adapter because desktop registry/preferences and browser local storage are
platform boundaries, not CAD document state.

Persisted GRIDUNIT components accept finite values greater than or equal to
zero. Snapshot capture resolves each zero component from the corresponding
positive SNAPUNIT component; malformed inherited spacing still fails closed.
The raw persisted values remain available to the shell and save pipeline, so a
zero is not rewritten to an effective spacing merely because the drawing was
displayed.

The shared `CadSampleView` supplies dynamically themed controls for visibility,
X/Y SNAPUNIT, X/Y GRIDUNIT, rectangular/isometric style, Left/Top/Right plane,
adaptive display, subdivision, beyond-limits display, and GRIDMAJOR. One Apply
action creates one history entry and one complete immutable
snapshot/picture replacement. Snapshot notifications transactionally refresh
the controls after Apply, Undo, Redo, or document load; a refresh guard prevents
control assignment from creating edits. Invalid and unchanged values disable
Apply. Desktop and browser hosts continue consuming the same shared view source.

F5 and Ctrl+E use a dedicated O(1) reversible SNAPISOPAIR command rather than
round-tripping every grid-panel field. The exact active VPORT and SNAPSTYL/pair
state are validated across Apply, Undo, and Redo. Rectangular SNAPSTYL retains
its rendered basis while remembering the newly cycled dormant pair. Staged or
invalid panel values block cycling, and the browser host suppresses the native
reload/navigation defaults for the two shortcuts before shared dispatch.

The persisted-edit follow-up does not itself change a shader, renderer, native
ABI, draw command, GPU resource, cache, or native scene compiler. Managed/native
rendering parity is therefore not separately applicable to the host-side
document mutation. The later line-presentation follow-up does change shared
coverage semantics and is paired in the managed compiler, native semantic
compiler/validator, canonical shader, C contract documentation, and regressions.
The ACadSharp save boundary is applicable and covered by matched DXF/DWG
round-trip tests. Normal retained replay remains unchanged and allocation-free;
editing intentionally pays one bounded command allocation and one snapshot
compilation outside the per-frame path.

## Verification and remaining gates

Focused managed tests cover command recording and invalid parameters, one-quad
compilation, active-VPORT capture independent of SNAPMODE, rotated origin/basis,
GRIDDISPLAY flags, GRIDMAJOR cadence, adaptive coarsening, below-base subdivision,
limits clipping, exact isometric axis pairs, equal-aspect and malformed-pair
rejection, 1,024 zero-allocation plans, shared
canvas draw ordering, exact persisted edits, raw flag preservation, zero GRIDUNIT
inheritance, one-generation Apply/Undo/Redo, shared-shell synchronization,
snap-state independence, DXF/DWG persistence, and native semantic wire lowering.
The Clang C++ regression covers fixed-device dot/line decoding, cadence
validation, vertex parameters, capacity, and the preserved legacy variant.
Headless coverage verifies command validation, both one-quad compilation modes,
dot transparency, affine dot circularity, and one-pixel minor versus two-pixel
major line output. Shader-resource coverage verifies the canonical module and
its required algorithm/complexity contract.

Final macOS arm64 Release validation passed 1,052/1,052 ProGPU.CAD tests. The
isometric continuation's focused grid/snap/Ortho/COPY set passed 36/36, the
complete headless dot-grid pixel class passed 24/24, and the isometric native
semantic-transform regression passed 1/1. The ACadSharp R2007/R2013/R2018 VPORT
group 77/78 DXF dependency regression passed 3/3 on its net9.0 target with
major-runtime roll-forward. The shared desktop host's net10.0 warning-as-error
build completed with zero warnings and zero errors. Release packing produced
paired `ACadSharp.ProGPU.0.1.0-preview.62.nupkg` and
`ProGPU.CAD.0.1.0-preview.62.nupkg` artifacts. Package-content validation found
the fork's net10.0 assembly and exact same-version package dependency, and the
isolated consumer created an AC1032 document without resolving upstream
ACadSharp. External publication remains a release action.

The keyboard-cycle continuation additionally passed all 15 focused grid/view
tests plus its browser-host reservation regression. Paired package verification
and the isolated consumer passed again with the same preview.62 package closure.

Host-profile persistence beyond the current shared view, transient dynamic-UCS
following/editing, broader screenshot goldens at
multiple DPI scales,
extreme-shear visual differentials, and representative large-drawing GPU
p50/p95/p99 measurements remain before the broader drafting-grid area is
complete. This functional slice makes no unmeasured FPS or latency claim.
