# ProGPU.CAD polar-tracking research record

## Scope and primary sources

This slice adds exact incremental plan polar tracking to the shared
desktop/browser MOVE and COPY second-point prompts. The continuations add exact
profile-distance PolarSnap, drawing-persisted F9 Snap Mode, and up to ten
profile-scoped absolute non-incremental additional angles. The LINE continuation
adds explicit actual-last-segment-relative incremental angles. Object-snap
tracking/acquired points, 3D Z paths, and arbitrary-camera acquisition remain.

The implementation was designed clean-room from public behavior contracts:

- Autodesk's [polar tracking and PolarSnap overview](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Core/files/GUID-7EC3C63D-EA4E-4E65-A676-C3A3627E3F19.htm)
  distinguishes temporary angular alignment paths from distance-quantized
  PolarSnap, defines UCS/drawing-angle orientation, documents near-path
  activation, and states the Ortho/polar and grid/PolarSnap exclusions.
- Autodesk's [Polar Tracking tab reference](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-D7CBB7B0-9140-4C53-88EF-08EAA09FA9D7.htm)
  defines incremental versus additional absolute angles, the standard
  increments, absolute-UCS versus last-segment measurement, and the separate
  object-snap-tracking policy.
- Autodesk's [POLARMODE reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-D91628CC-9975-4DBF-8D02-10B23A6F3ED5.htm)
  confirms that polar preferences and object-snap tracking settings are
  registry state rather than drawing state.
- Autodesk's [AUTOSNAP reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-BE7947EB-2A08-4406-A169-4C5E125B1F4D.htm)
  defines polar enablement as application/profile state.
- Autodesk's [POLARANG reference](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-LT-MAC/files/GUID-0CF67F9E-F953-43D6-9227-0D56E0E693ED.htm)
  specifies a 90-degree default and the standard 90, 45, 30, 22.5, 18, 15,
  10, and 5 degree choices.
- Autodesk's [ANGBASE reference](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-MAC-Core/files/GUID-B7CAB5F3-16BC-4E06-97BC-AAAEC052727E.htm)
  and [ANGDIR reference](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-Core/files/GUID-A432574D-35B6-4D9E-8D8F-4259F2066234.htm)
  define drawing-persisted zero orientation and positive angular direction
  relative to the current UCS.
- Autodesk's [precision workflow](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-GettingStarted/files/GUID-061A5ED6-E7F7-437E-978B-58146316EF40.htm)
  confirms nearest-preset-angle behavior and the separate coordinate-entry,
  object-snap, and direct-distance mechanisms.
- Autodesk's [Function Key Reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-ACAA0279-047D-458E-889F-60BBFDD40489.htm)
  defines F10 as Polar Tracking and states that F8 Ortho and F10 Polar are
  mutually exclusive.

No third-party implementation source was used. Exact approved source
provenance is the ProGPU-owned plan viewport, point prompt, object/grid/Ortho
queries, immutable snapshot, and retained overlay. The dependency contract is
the in-repository ACadSharp `CadHeader.AngleBase`/`AngularDirection` and active
`VPort` UCS model at submodule commit
`6353e17ebcf4c6c57479a9998a9e05738b180f9c`. That dependency commit normalizes
DXF ANGBASE degrees to the existing radians object-model/DWG convention and
corrects the documented counterclockwise default, with an independent DXF
round-trip regression. No foreign source text or control flow was copied.

## Adopted contract and algorithm

`CadSnapshotCompiler` captures the active VPORT UCS X/Y axes and composes the
drawing-persisted ANGBASE into an immutable WCS basis. Invalid, non-finite,
degenerate, non-orthogonal, or unknown angular-direction state fails closed.
AUTOSNAP, POLARANG, POLARMODE, and additional angles are profile/registry state,
not DXF/DWG document state. ProGPU therefore initializes a new view with polar
tracking off and the documented 90-degree default, then exposes a shared
session-only toggle and the eight standard increments. It does not invent
drawing persistence for application preferences.

F10 toggles that session/profile state in both shared hosts and the existing
control uses the same path. Because Autodesk also defines mutual exclusion,
enabling Polar while drawing-persisted ORTHOMODE is on executes one exact
reversible ORTHOMODE=0 edit before enabling Polar; otherwise F10 advances no
drawing generation. Enabling F8 disables Polar without inventing AUTOSNAP or
POLARMODE drawing persistence. Snapshot-producing toggles preserve staged grid
panel values by refusing the conflicting action, and browser F8/F10 defaults
are reserved before shared dispatch.
The separate PolarSnap continuation records its SNAPMODE/SNAPTYPE/POLARDIST
state and distance algorithm in `PROGPU_CAD_POLAR_SNAP_RESEARCH.md`.
The additional-angle continuation records its bounded POLARADDANG profile,
arbitration, and last-segment fidelity boundary in
`PROGPU_CAD_ADDITIONAL_POLAR_ANGLES_RESEARCH.md`.
The LINE continuation records its explicit previous-segment basis, command
state, and authoring transaction in `PROGPU_CAD_LINE_AUTHORING_RESEARCH.md`.

For accepted base `B`, pointer `P`, ANGBASE-adjusted orthonormal axes `X,Y`,
direction sign `s` (`+1` counterclockwise, `-1` clockwise), and increment `a`:

```text
D = P - B
x = dot(D, X)
y = s * dot(D, Y)
t = atan2(y, x)
k = round-away-from-zero(t / a)
q = k * a
R = cos(q)X + s*sin(q)Y
d = dot(D, R)
P' = B + dR
```

The increment must be finite, positive, no greater than 90 degrees, and divide
one complete turn. The nearest-path tie is deterministic. Query work and
storage are O(1), with no angle table, enumeration, or managed allocation.

The geometric query always returns the nearest alignment projection. The
shared shell activates it only when `P'` lies within 10 logical device pixels
of the pointer. Autodesk specifies near-path activation but not a public
tolerance; this bounded zoom-independent aperture is an explicit ProGPU policy
matching the existing object-snap acquisition scale. Outside that aperture,
the pointer proceeds to rectangular grid snap and then raw input.

Pointer precedence is exact object snap, active Ortho or incremental/additional
polar tracking with optional acquired-path PolarSnap distance, Grid SNAPTYPE,
then raw input.
Object snap returns first. Ortho and polar are
mutually exclusive in both shared controls and canvas state. Tracking applies
only after a base point exists; typed absolute/relative Cartesian and polar
coordinates bypass the pointer pipeline. The tracked point commits directly as
double WCS. The core polar query does not quantize distance; the later profile-
scoped PolarSnap query is deliberately separate and runs only after angular-
path acquisition.

## Rendering and managed/native applicability

The existing base-to-pointer rubber band uses the tracked endpoint. While a
path is active, one additional fixed-device full-view alignment line is drawn
from the base in the tracked direction, clipped by the existing canvas clip.
Pointer motion mutates no document, generation, immutable scene, upload, cache
key, or GPU resource.

The required rendering/text architecture gate was rechecked against
[Skia's staged text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender's rendering pipeline](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
[Parley's reusable layout model](https://github.com/linebender/parley/blob/main/doc/concept.md),
and [HarfBuzz shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html).
The applicable principle remains retention of semantic results with lightweight
interaction decoration outside scene/resource caches. No shaping, layout,
fallback, glyph/path/image cache, upload, batching, DPI/subpixel, startup,
worker, or device-loss behavior changed.

The native renderer consumes the same committed retained picture after the
MOVE/COPY edit. Polar tracking is shared host-side input policy and adds no
native scene compiler, shader, GPU resource, wire record, C ABI crossing, or
backend-specific algorithm. A parallel C++ implementation is not applicable;
managed/native committed-scene behavior remains identical.

## Verification and remaining gates

Focused tests cover nearest-angle projection, ANGBASE-adjusted and clockwise
bases, invalid/non-finite/disabled rejection, 1,024 zero-allocation warm
queries, immutable snapshot capture, device-aperture activation and release,
exact MOVE commit, full-view guide recording, object-snap and typed-coordinate
override, shared increments, and bidirectional Ortho mutual exclusion. The
ACadSharp dependency tests cover DXF ANGBASE radians, ANGDIR, and versioned
ORTHOMODE round trips. Shared-view tests cover F10, persisted-Ortho exclusion,
exact generation counts, Undo/Redo synchronization, staged-panel protection,
and browser key reservation. PolarSnap regressions cover explicit and Snap-X-
inherited distance, live prompt reevaluation, object-snap precedence, direct-
distance separation, F9/type retention, exact SNAPMODE history, staged input,
zero-allocation warm queries, browser reservation, and DXF/DWG round trips.
The complete macOS arm64 Release ProGPU.CAD suite passes 1,075/1,075.

Relative-to-last-segment measurement is covered for LINE and remains pending
for POLYLINE. Object-snap tracking and acquired points, 3D UCS Z paths,
temporary overrides, cross-session host profile persistence,
arbitrary-camera rays, interaction image goldens, dense-drawing p50/p95/p99
evidence, and independent DXF/DWG angle fixtures remain before the broader
tracking feature can be called complete.
