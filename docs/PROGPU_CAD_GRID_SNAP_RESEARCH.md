# ProGPU.CAD rectangular grid-snap research record

## Scope and primary sources

This slice adds exact rectangular drafting-grid acquisition to the shared
desktop/browser MOVE and COPY point prompts. It does not add a visible drawing
grid, PolarSnap, isometric snap, tracking, direct-distance input, or
arbitrary-camera point acquisition.

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
  SNAPUNIT, SNAPBASE, SNAPANG, and SNAPSTYLE values.
- Autodesk's [VPORT DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-8CE7CC87-27BD-4490-89DA-C21F516415A9.htm)
  identifies the first `*ACTIVE` record as the current viewport and defines the
  persisted group codes. Its storage description calls group 13 DCS, while the
  typed ObjectARX setter defines that same group-13 value in UCS coordinates;
  ProGPU follows the typed API semantics used by the in-repository model.

No third-party implementation source was used. The exact approved source
provenance is the ProGPU-owned snapshot and plan-point infrastructure plus the
in-repository ACadSharp `Tables/VPort.cs` contract at submodule commit
`c5e7b3236ec1bf545c15f0db25d228d9f79ed598`. ProGPU consumes its typed
`SnapOn`, `SnapSpacing`, `SnapBasePoint`, `SnapRotation`, `IsometricSnap`, and
UCS origin/axis properties; no source text or control flow was copied.

## Adopted contract

`CadSnapshotCompiler` captures the first case-insensitive `*ACTIVE` VPORT into
immutable `CadPlanGridSnapSettings`. The persisted base is transformed from UCS
coordinates to WCS. SNAPANG is composed with the normalized UCS X/Y axes, so a
rotated or tilted orthonormal UCS retains its actual grid plane rather than
being flattened to WCS XY. Invalid spacing, non-finite state, degenerate axes,
or non-orthogonal axes fail closed to a valid disabled default.

Rectangular snap is supported. The immutable style still records isometric
state, but `TrySnap` returns false rather than pretending it is rectangular.
PolarSnap has no equivalent in the consumed VPORT contract and remains
explicitly deferred. The shared checkbox starts from persisted SNAPMODE and is
a bounded interaction-session override; changing it does not edit or republish
the document. Persisted drafting-settings editing remains a later command/UI
contract.

For a pointer point `P`, grid origin `O`, orthonormal axes `X,Y`, and spacings
`sx,sy`, the query computes:

```text
D  = P - O
u  = dot(D, X)
v  = dot(D, Y)
u' = round-away-from-zero(u / sx) * sx
v' = round-away-from-zero(v / sy) * sy
N  = D - uX - vY
P' = O + u'X + v'Y + N
```

The normal component `N` is preserved exactly apart from floating-point
roundoff, so grid acquisition does not silently project a point onto a
different plane. A four-ULP correction recognizes only mathematically exact
half-spacing ties perturbed by trigonometric basis construction; all ordinary
values use normal nearest rounding. Invalid or overflowing queries return
`false`. Work and storage are O(1), and a warm query allocates no managed
memory.

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

## Verification and remaining gates

Focused tests cover independent spacing and base, positive/negative half ties,
90-degree rotation, an arbitrary 3D grid plane with preserved normal component,
disabled/isometric/non-finite rejection, invalid settings, active-VPORT UCS/base/
rotation/spacing capture, and 1,024 zero-allocation warm queries. Shared-shell
tests cover two exact grid-snapped COPY stages, fixed-device marker recording,
object-over-grid precedence, typed-coordinate bypass, one edit generation, and
desktop/browser-shared checkbox propagation.

Isometric and polar lattices, visible adaptive grid rendering, persisted
drafting-settings editing, status-key accelerators, direct-distance/tracking
composition, arbitrary-camera screen rays, image goldens, large-scene
p50/p95/p99 interaction evidence, and DXF/DWG grid-setting round-trip fixtures
remain before the broader drafting-grid feature can be called complete.
