# ProGPU.CAD direct-distance research record

Date: 2026-08-30

## Scope and primary sources

This slice adds direct-distance entry to the shared desktop/browser MOVE and
COPY second-point prompt. After accepting a base point, the user moves the
cursor to establish direction and enters one positive invariant distance. It
does not change the explicit Cartesian/polar coordinate grammar, add a global
last point, parse expressions or units, implement temporary override keys, or
extend the current WCS-XY plan interaction to arbitrary cameras or 3D UCS
planes.

The implementation was designed clean-room from public behavior contracts:

- Autodesk's [Direct Distance Entry command modifier](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-MAC-Core/files/GUID-BF4A06D8-2D66-427D-9460-B62A479B22B4.htm)
  defines a numeric distance at a point prompt as a distance from the last
  point along the current cursor path, commonly composed with Ortho or snap,
  and excludes its use while temporary override keys are active.
- Autodesk's [coordinate-entry overview](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-0A0135DB-3216-482B-81DD-74E6DB8CA3E3.htm)
  distinguishes a bare numeric value followed by Enter as direct-distance
  entry from explicit coordinate forms.
- Autodesk's [precision workflow](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-OnBoarding/files/ACD_FOUNDATIONS_MAIN6.html)
  documents direct-distance composition with Ortho and polar tracking.
- Autodesk's [Ortho reference](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-C3B5D7B3-8057-4D8B-A3A2-0F5F0778BF37.htm)
  defines horizontal or vertical cursor restriction relative to the current
  UCS and the exclusion between Ortho and polar tracking.
- Autodesk's [distance and angle entry reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-LT/files/GUID-92BDE481-49D0-4AED-A6C5-0B78051EAE99.htm)
  separates cursor-directed distance from explicit relative polar input.

No third-party implementation source was consulted or used. The approved
implementation provenance is the original in-repository ProGPU
`CadCoordinateInput`, `CadPlanViewport`, `CadPlanOrthoConstraint`,
`CadPlanPolarTrackingSettings`, and `CadSampleCanvas` point-prompt state. This
slice requires no ACadSharp change.

## Adopted contract and algorithm

`CadDirectDistanceInput` deliberately remains separate from
`CadCoordinateInput`. A bare scalar is context-sensitive command input, while
`x,y[,z]`, `@dx,dy[,dz]`, `distance<angle`, and `@distance<angle` remain
explicit coordinates with unchanged meaning. The scalar parser accepts at most
128 UTF-16 code units, invariant floating-point notation, and finite values
strictly greater than zero. Zero, negative, non-finite, overlong, coordinate,
expression, and unit-suffixed values fail without changing prompt or document
state.

A direct distance is accepted only after a base and a post-base cursor position
exist. For base `B`, finite non-zero direction vector `D`, and requested
distance `d`:

```text
M = max(abs(D.x), abs(D.y), abs(D.z))
S = D / M
P = B + S * (d / sqrt(dot(S, S)))
```

Scaling before normalization avoids overflow while preserving direction.
Parsing is O(L) time and O(1) storage for bounded input length `L`; resolution
is O(1). Both are allocation-free after warm-up. A non-finite intermediate or
result fails closed.

The raw plan direction is obtained by inverse-projecting the last logical
screen cursor at the accepted base Z, then subtracting the base. This preserves
the exact base plane rather than leaking the viewport's default Z into the
displacement. When Ortho is enabled, the same active rectangular UCS/SNAPANG
basis selects the nearest signed axis, but grid spacing is deliberately
disabled for the length calculation. When polar tracking is enabled, its
ANGBASE/ANGDIR-adjusted direction is used only if the path is actually acquired
inside the existing 10-logical-device-pixel aperture. Otherwise the raw cursor
direction applies.

Running object snap and rectangular grid continue to own clicked-point
acquisition, but neither replaces the raw cursor ray for a bare distance.
Consequently a typed length stays exact instead of becoming the distance to an
object or grid point. Ortho and acquired polar tracking intentionally own only
the direction. Explicit Cartesian and polar coordinates continue to bypass
the pointer constraint pipeline entirely.

The shared action is disabled for a bare scalar until both base and direction
exist. Cursor availability publishes one transition event per prompt rather
than refreshing the shell on every pointer move. Rejection preserves prompt
stage, selection, immutable snapshot, document generation, and history.
Successful input uses the existing second-point transition and therefore
commits exactly one MOVE/COPY edit and one rebuilt immutable picture.

## Rendering, text, and managed/native applicability

The required architecture gate was rechecked against
[Skia's staged text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender's rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
[Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html).
The applicable principle remains separation of lightweight interaction state
from retained scene, text-layout, upload, and resource caches. Direct-distance
entry changes no shader, text shaping/layout, glyph/path/image cache, scene
record, upload, DPI/subpixel policy, worker initialization, or device-loss
contract.

This is shared host-side point-prompt policy. The native renderer consumes the
same rebuilt retained picture after a committed edit, so there is no parallel
C++ algorithm, wire record, C ABI call, GPU resource, or shader to add. Managed
and native committed-scene behavior remains identical.

## Verification and remaining gates

Focused tests cover invariant positive parsing, malformed/non-positive/
non-finite/bounded rejection, overflow-safe normalization, base-plane
preservation, degenerate direction, 1,024 zero-allocation warm parse/resolve
operations, first-point rejection, missing-direction rejection, raw direction
with an active coarse grid, exact Ortho and acquired-polar composition, one
generation/history edit, and shared action enablement after the first
post-base cursor move. The complete Release CAD suite and native-linked browser
build remain the integration gates.

Temporary overrides, expression/unit input, zero-distance no-op policy, global
last-point state, dynamic-input tooltips, 3D UCS planes, arbitrary-camera rays,
interaction image goldens, dense-drawing latency percentiles, and licensed
behavior differentials remain before broader dynamic point input is complete.
