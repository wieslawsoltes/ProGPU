# ProGPU.CAD PolarSnap and Snap Mode research record

## Scope and primary sources

This slice adds exact plan-view PolarSnap distance acquisition, the shared
Grid/Polar snap-type choice, and F9 Snap Mode behavior to desktop and browser
MOVE/COPY point prompts. The later POLARADDANG continuation supplies bounded
absolute additional paths before this distance query, and LINE supplies an
actual-last-segment-relative incremental path. Object-snap tracking, POLYLINE
relative measurement, 3D tracking, and an application settings-store adapter
remain outside this slice.

The implementation was designed clean-room from public behavior contracts:

- Autodesk's [POLARDIST reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT/files/GUID-2CE7AC0B-D502-49F1-8C51-A67AA3E4BB15.htm)
  defines the PolarSnap distance as registry/profile state with initial value
  zero.
- Autodesk's [SNAPTYPE reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-2BC423FB-0BCD-4086-91F6-BE00F695FCB6.htm)
  defines registry values Grid 0 and PolarSnap 1.
- Autodesk's [SNAPMODE reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-D6B961E9-1D95-458E-BEA1-C9997789EDC2.htm)
  defines current-viewport on/off state as drawing-persisted.
- Autodesk's [Snap and Grid tab reference](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Core/files/GUID-66D637C9-6C47-420C-ADD1-83B64C73217A.htm)
  defines F9, requires Snap Mode plus Polar SNAPTYPE, makes zero POLARDIST
  inherit Snap X spacing, and distinguishes rectangular/isometric Grid snap
  from PolarSnap.
- Autodesk's [Polar Tracking and PolarSnap overview](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Core/files/GUID-7EC3C63D-EA4E-4E65-A676-C3A3627E3F19.htm)
  defines distance increments relative to the first point, requires an active
  polar alignment path, and states that Grid snap and PolarSnap are mutually
  exclusive.

No third-party implementation source was used. Exact approved provenance is
the ProGPU-owned immutable active-VPORT grid settings, polar-path query,
point-prompt state machine, object-snap precedence, direct-distance resolver,
history, and shared shell, plus the typed in-repository ACadSharp `VPort.SnapOn`
and `VPort.SnapSpacing` model at pinned feature commit `83300fd0`. No foreign
helper, naming, control flow, lookup encoding, or source text was adopted.

## Adopted state and algorithm

The canvas retains three independent concepts:

- active-VPORT SNAPMODE is drawing state; F9 and either snap control change it
  with one `CadSetPlanSnapModeCommand` generation and one snapshot rebuild;
- Grid versus Polar SNAPTYPE is profile/session state and never enters DXF/DWG;
- non-negative POLARDIST is profile/session state and never enters DXF/DWG.

Switching Grid to Polar while SNAPMODE is already on changes no document
generation. Turning Snap Mode off retains the selected type, so the next F9
restores that type. Invalid profile distance cannot enable PolarSnap. A
snapshot-producing SNAPMODE transition is refused while the drafting-grid
panel contains staged or invalid values, preventing unrelated input loss.
Undo/Redo synchronizes the current type-specific toggle from the restored
active-VPORT value. Direct canvas setters remain explicit session overrides for
embedders.

Object snap remains first. Ortho remains second and is mutually exclusive with
polar tracking. The existing polar query first acquires the nearest incremental
alignment path and the shell applies its fixed 10-device-pixel perpendicular
aperture. Only then, when SNAPMODE is on and SNAPTYPE is Polar, PolarSnap
quantizes the along-path distance.

For accepted base `B`, acquired unit polar direction `R`, raw projected distance
`d`, configured distance `p`, and positive active Snap X spacing `sx`:

```text
h = p > 0 ? p : sx
n = round-away-from-zero(d / h)
d' = n * h
P' = B + d'R
```

Finite and overflow checks fail closed. Query work and storage are O(1), with
no table, search, allocation, scene compilation, or document mutation. The
zero-distance fallback reads immutable active-VPORT SNAPUNIT X even though the
Grid query itself is disabled by Polar SNAPTYPE.

Typed absolute/relative coordinates bypass the pointer pipeline. A bare direct
distance uses only the actually acquired polar direction and preserves the
typed length; PolarSnap never rounds it. Object snap returns before PolarSnap,
so an exact entity point cannot be displaced to a distance increment.

## Rendering and managed/native applicability

PolarSnap changes only the transient point and the existing fixed-device marker
and polar guide endpoints. It adds no retained production primitive, shader,
texture, upload, cache, native wire record, C ABI crossing, or backend-specific
algorithm. The committed MOVE/COPY continues through the same typed command and
produces the same retained picture for managed and native scene compilation.
The paired renderer applicability audit therefore finds no C++ or shader change;
managed/native committed-scene behavior remains identical.

The mandatory cross-engine rendering/text research gate is not triggered by
this input-only slice. No rendering, scene compilation, shaping, font, glyph,
path, image, cache, startup, worker, DPI, device-loss, or GPU-pipeline contract
changed.

## Verification and remaining gates

Focused core tests cover explicit and zero/fallback distance, invalid and
overflow fail-closed behavior, and 1,024 zero-allocation warm queries. Shared
interaction tests cover live pending-pointer reevaluation, exact quantized MOVE,
object-snap precedence, direct-distance separation, Grid/Polar exclusion,
profile type retention across F9, invalid-distance rejection, staged-panel
protection, exact generation counts, and Undo/Redo synchronization. Command
tests cover exact active-VPORT identity/value validation plus DXF and DWG
SNAPMODE round trips. The browser reservation regression includes F9.
The complete macOS arm64 Release ProGPU.CAD suite passes 1,075/1,075.

Cross-session profile persistence, object-snap tracking and acquired points,
POLYLINE last-segment-relative polar angles, 3D UCS Z paths, arbitrary-camera rays,
temporary overrides, interaction image goldens, and dense-drawing p50/p95/p99
evidence remain.
