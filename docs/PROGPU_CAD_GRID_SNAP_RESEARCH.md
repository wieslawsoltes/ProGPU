# ProGPU.CAD rectangular and isometric grid-snap research record

## Scope and primary sources

The initial slice added exact rectangular drafting-grid acquisition to the
shared desktop/browser MOVE and COPY point prompts. The isometric continuation
adds exact Left/Top/Right SNAPISOPAIR lattices, active-pair Ortho directions,
visible affine dot-grid reuse, persisted SNAPSTYL/SNAPISOPAIR/SNAPUNIT editing,
and DXF/DWG fidelity. PolarSnap, tracking, and arbitrary-camera point
acquisition remain separate contracts.

The implementation was designed clean-room from public behavior and format
contracts:

- Autodesk's [Snap and Grid tab reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-66D637C9-6C47-420C-ADD1-83B64C73217A.htm)
  defines snap as an invisible rectangular lattice that restricts cursor input
  to independently positive X/Y intervals, and distinguishes it from the
  separately displayed grid. It also distinguishes rectangular, isometric, and
  polar snap types.
- Autodesk's [SNAPBASE reference](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-LT/files/GUID-484B0F6E-4EB0-4B83-9C95-16F5CCE8F3E2.htm)
  defines the snap/grid origin as current-viewport drawing state relative to the
  current UCS.
- Autodesk's [SNAPANG reference](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-Core/files/GUID-7C4EAEAE-3738-4E51-AC9B-B16B5A2CDB3B.htm)
  defines the current-viewport snap/grid rotation relative to the current UCS.
- Autodesk's [ObjectARX snap-angle contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbViewport__setSnapAngle_double.html)
  confirms radians, the UCS XY plane, and positive counterclockwise rotation.
- Autodesk's [revised VPORT header-variable reference](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-ED26E626-BC45-4256-9914-87E5FFE934B8.htm)
  establishes that `*ACTIVE` VPORT records override legacy header SNAPMODE,
  SNAPUNIT, SNAPBASE, SNAPANG, and SNAPSTYLE values and defines SNAPISOPAIR
  values 0 Left, 1 Top, and 2 Right.
- Autodesk's [VPORT DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-8CE7CC87-27BD-4490-89DA-C21F516415A9.htm)
  identifies the first `*ACTIVE` record as the current viewport and defines the
  persisted group codes. Its storage description calls group 13 DCS, while the
  typed ObjectARX setter defines that same group-13 value in UCS coordinates;
  ProGPU follows the typed API semantics used by the in-repository model.
- Autodesk's [SNAP command reference](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-F47F4AAF-4859-45D4-846C-3742268834A9.htm)
  defines the equal-aspect isometric lattice, its initial 30/150-degree axes,
  the three ISOPLANE pairs, and the fact that a lined grid does not follow the
  isometric lattice.
- Autodesk's [ISOPLANE command reference](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-9B1EEA63-BEC1-413E-B69F-541B5865F1A1.htm)
  defines Left as 90/150 degrees, Top as 30/150 degrees, Right as 90/30
  degrees, and requires Ortho to use the active pair when SNAPSTYL is
  isometric.
- Autodesk's [SNAPSTYL reference](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-E04B7A7B-8232-44C3-BD74-20BCFEC07C2E.htm)
  defines drawing-persisted rectangular value 0 and isometric value 1.

No third-party implementation source was used. The exact approved source
provenance is the ProGPU-owned snapshot and plan-point infrastructure plus the
in-repository ACadSharp `Tables/VPort.cs` contract and ProGPU-owned DXF writer
fix at feature commit `592e5f1c`. ProGPU consumes typed `SnapOn`,
`SnapSpacing`, `SnapBasePoint`, `SnapRotation`, `IsometricSnap`, `SnapIsoPair`,
and UCS origin/axis properties. The approved fork fix emits already-modeled
VPORT groups 77/78 and adds an independent three-version round-trip test; no
third-party implementation text or control flow was copied.

## Adopted contract

`CadSnapshotCompiler` captures the first case-insensitive `*ACTIVE` VPORT into
immutable `CadPlanGridSnapSettings`. The persisted base is transformed from UCS
coordinates to WCS. SNAPANG is composed with the normalized UCS X/Y axes, so a
rotated or tilted orthonormal UCS retains its actual grid plane rather than
being flattened to WCS XY. Invalid spacing, non-finite state, degenerate axes,
or non-orthogonal axes fail closed to a valid disabled default.

Rectangular and isometric snap are supported. `CadPlanIsoplane` preserves all
three valid SNAPISOPAIR values. Starting from the rotated orthonormal UCS basis
`U,V`, the exact unit directions are:

```text
A30  = (sqrt(3)/2) U + (1/2) V
A90  = V
A150 = -(sqrt(3)/2) U + (1/2) V
Left  = (A90, A150)
Top   = (A30, A150)
Right = (A30, A90)
```

Every pair has determinant magnitude `sqrt(3)/2`. With the required equal
SNAPUNIT aspect, all three pairs span the same triangular point lattice while
changing Ortho/crosshair directions exactly. Invalid pair values or unequal
isometric spacing fail closed instead of producing an invented skew lattice.
PolarSnap remains explicitly deferred. The shared checkbox starts from
persisted SNAPMODE and remains a bounded interaction-session override.

For a pointer point `P`, grid origin `O`, unit axes `X,Y`, axis dot product `g`,
and spacings `sx,sy`, the common dual-basis projection computes:

```text
D   = P - O
det = 1 - g*g
u   = (dot(D,X) - g*dot(D,Y)) / det
v   = (dot(D,Y) - g*dot(D,X)) / det
N   = D - uX - vY
```

Rectangular `g=0` retains independent midpoint-away-from-zero rounding. For an
isometric `g=+/-0.5`, independently rounded axial coordinates identify a base
cell but are not always the Euclidean-nearest triangular point. ProGPU therefore
evaluates that point and its fixed eight neighboring index pairs, retaining the
strictly nearest WCS point; the base wins exact ties. A 3x3 neighborhood is
complete because both fractional axial coordinates begin in `[-0.5,0.5]`.
The normal component `N` is restored unchanged. A four-ULP correction
recognizes only exact half-spacing ties perturbed by trigonometric basis
construction. Invalid or overflowing queries return `false`. Rectangular work
is O(1), isometric work is fixed O(9), storage is O(1), and warm replay
allocates no managed memory.

Point-prompt precedence is exact object snap, then grid snap, then raw pointer
position. Object snap wins because it identifies authored geometry within its
bounded aperture; grid is an unconditional lattice constraint and provides the
fallback when no object candidate exists. Both the base and second pointer
stages commit the double-WCS grid result directly instead of round-tripping
through float screen coordinates. Typed absolute/relative Cartesian and polar
coordinates remain authoritative and bypass both pointer constraints. Pan,
zoom, arrange, cancellation, prompt reset, scene replacement, and accepted
points clear transient grid state.

## Rendering and managed/native applicability

The shared shell records a two-line fixed-device plus marker only while a grid
candidate is active. It reuses dynamic theme pens and sits after the retained
CAD picture. Hover does not mutate the document, publish a snapshot, upload a
resource, or alter a cache key.

The rendering/text architecture gate was rechecked against
[Skia's staged text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender's rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
[Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html).
The applicable common principle is to retain semantic results and keep
lightweight transient interaction decoration outside scene/resource caches.
No shaping, layout, fallback, glyph/path/image cache, upload, DPI/subpixel,
batching, startup, or device-loss decision changed.

The native renderer consumes the same committed retained picture after a
MOVE/COPY edit. Grid acquisition is shared host-side input policy: it adds no
native scene compiler, shader, GPU resource, wire field, C ABI crossing, or
backend-specific algorithm. A second C++ snap implementation is therefore not
applicable; managed/native committed-scene parity remains unchanged.

Isometric Ortho chooses the active pair direction with the least perpendicular
distance, projects onto that exact unit axis, and optionally composes the
nearest triangular grid point while preserving an off-grid accepted base. The
existing direct-distance path consumes the same Ortho result, so there is no
second direction implementation or per-frame allocation.

## Verification and remaining gates

Focused tests cover independent rectangular spacing/base, half ties, rotation,
an arbitrary 3D plane with preserved normal component, exact three-isoplane
axes and shared lattice, Euclidean nearest-cell correction, malformed aspect
and pair rejection, active-VPORT capture, active-pair Ortho, and 1,024
zero-allocation warm queries for both styles. Shared-shell tests cover exact
rectangular and isometric two-stage COPY, marker recording, precedence,
typed-coordinate bypass, one edit generation, and desktop/browser-shared
controls. ACadSharp VPORT groups 77/78 pass three DXF versions; ProGPU
style/pair/spacing edits pass DXF and DWG round trips.

PolarSnap, status-key plane cycling, object-snap tracking, arbitrary-camera
screen rays, broader image goldens, and large-scene p50/p95/p99 interaction
evidence remain before the broader drafting-grid feature can be called
complete.
