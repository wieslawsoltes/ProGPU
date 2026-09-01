# ProGPU.CAD current-UCS and global-last-point research

Date: 2026-09-01

## Scope and clean-room provenance

This checkpoint resolves the existing bounded Cartesian/polar grammar through
the immutable active-VPORT UCS for shared MOVE/COPY and COPYBASE/PASTECLIP
prompts. It also retains one application-owned global last accepted point. The
implementation is original ProGPU code over `CadPlanAuthoringContext`,
`CadCoordinateInput`, and the existing prompt state machines. No third-party
implementation source, helper structure, naming, or control flow was used.

Primary public behavior sources:

- Autodesk's [LASTPOINT system-variable contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-LT/files/GUID-4EE5F6EA-B8D1-4DD0-8D5D-B7FA2AD3A3D0.htm)
  defines the last specified point in current-UCS coordinates, its non-saved
  lifetime, and bare `@` as `@0,0,0`.
- Autodesk's [coordinate-entry contract](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-0A0135DB-3216-482B-81DD-74E6DB8CA3E3.htm)
  establishes current-UCS interpretation, absolute coordinates from the UCS
  origin, and relative coordinates from the previous point.
- Autodesk's [polar-coordinate contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-58C1D97C-A9B3-4C3C-AC1B-95BE3DF2EDB9.htm)
  establishes distance-and-angle entry relative to the current coordinate
  system.
- Autodesk's [ANGDIR contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-LT/files/GUID-A432574D-35B6-4D9E-8D8F-4259F2066234.htm)
  defines clockwise versus counterclockwise positive angular direction.

## Adopted contract

`CadCoordinateInput` remains a bounded, invariant, allocation-free parser.
Cartesian values are neutral UCS components. Absolute Cartesian input evaluates
`origin + xAxis*x + yAxis*y + normal*z`; relative Cartesian input uses the same
offset from a caller-owned WCS reference point. Polar input first reduces the
angle to neutral Cartesian components, then uses the ANGBASE-adjusted angular
axes and reverses the angular Y axis when ANGDIR is clockwise. Unsupported or
non-finite bases fail before state mutation.

The canvas initializes its last point to the supported current-UCS origin when
a document is installed, otherwise WCS zero. An accepted click or typed point
in MOVE/COPY and COPYBASE/PASTECLIP updates the value. It is intentionally not
written to the drawing, history, snapshot, or clipboard envelope. The first
relative prompt uses this global point; the second MOVE/COPY relative prompt
uses the retained command base. Bare `@` therefore recalls the applicable
reference exactly. Absolute input never uses the prior point.

Clicks and snap results remain exact WCS values and are not transformed a
second time. Direct distance remains a distinct post-base cursor operation.
Broader authoring commands, current-elevation/default-Z policy, explicit WCS
coordinate overrides, arbitrary-camera construction planes, and persisted host
profile restoration remain separate work.

## Complexity, rendering parity, and validation

Parsing is `O(L)` for bounded input length. Resolution and last-point updates
are `O(1)` time and storage with no document or GPU allocation. This checkpoint
changes no retained command, shader, cache key, upload, native ABI, or resource
lifetime. Managed and native renderers consume the same rebuilt picture after
an edit, so a paired native implementation is not applicable.

Focused tests cover bare `@`, raw-UCS Cartesian axes, ANGBASE/ANGDIR polar axes,
absolute and relative resolution, unsupported contexts, MOVE and COPY commits,
Undo, clipboard-relative paste, exact global-last-point advancement, malformed
input atomicity, and existing managed/native clipboard replay.
