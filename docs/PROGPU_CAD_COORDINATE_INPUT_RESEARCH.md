# ProGPU.CAD typed coordinate-input research

Date: 2026-08-30

## Scope and clean-room provenance

This slice adds bounded typed coordinate entry to the existing shared
desktop/browser two-point MOVE and COPY workflow. A later checkpoint resolves
that grammar through the current UCS and supplies a non-persisted global last
point to MOVE/COPY and COPYBASE/PASTECLIP. It does not add arbitrary-camera
picking, a renderer, a scene format, a shader, a native ABI, or an ACadSharp
change.

The implementation was designed from the public behavior contracts below and
from these original ProGPU-owned sources in this repository:

- `src/ProGPU.CAD/CadGeometry.cs` for finite double-precision WCS values;
- `src/ProGPU.CAD/CadPlanViewport.cs` for the WCS-XY interaction plane;
- `src/ProGPU.CAD/CadEditing.cs` for transactional translation and duplication;
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs` for the bounded two-point state;
- `src/ProGPU.CAD.Sample/CadSampleView.cs` for the shared host-neutral shell.

No third-party implementation source, helper structure, naming, file layout,
or control flow was consulted or used.

## Public behavior sources

- Autodesk's current [MOVE command](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-47CE7325-84C0-4414-80A3-29DC98392709.htm)
  documents absolute `X,Y` input, relative `@dX,dY` input, and the base/second
  point difference as the displacement vector.
- Autodesk's [3D Cartesian coordinate entry](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-AABA8FE7-0E86-4046-96D5-CF5464D5FAC6.htm)
  documents `X,Y,Z`, optional two-coordinate input, and the `@` prefix for a
  point relative to the preceding point.
- Autodesk's [coordinate-entry overview](https://help.autodesk.com/view/ACD/2026/ENU/?guid=GUID-683349C0-E5C2-4E16-8846-5523E71172A9)
  distinguishes absolute Cartesian, relative Cartesian, and polar point input.
- QCAD's [command-line reference](https://www.qcad.org/doc/qcad/latest/reference/en/qcad_reference_manual_en.html#CommandLine)
  documents the common invariant forms `x,y`, `@x,y`, `distance<angle`, and
  `@distance<angle`; its Move/Copy reference resolves a relative target from
  the previously supplied reference point.

ProGPU adopts the common explicit syntax rather than AutoCAD's dynamic-input
mode-dependent defaults. Absolute Cartesian tuples resolve from the current
UCS origin through its raw X/Y/normal basis. Absolute polar input additionally
honors ANGBASE and ANGDIR. At a first MOVE/COPY or clipboard prompt, relative
input resolves from the global last accepted point; a bare `@` recalls that
point. At the second MOVE/COPY prompt it resolves from the retained base point
and therefore represents an exact typed displacement. Cartesian input may
carry two or three components; omitted Z is zero.

The parser intentionally rejects locale-dependent decimal separators,
expressions, unit suffixes, incomplete components, negative polar distance,
non-finite results, more than three Cartesian components, and input longer
than 128 UTF-16 code units. Dynamic-input `#`, direct-distance cursor input,
coordinate filters, drawing-persisted LASTPOINT, and current-elevation/default-Z
policy remain separate contracts.

## State, ownership, and failure semantics

`CadCoordinateInput` is an immutable, allocation-free parsed value. Parsing is
O(L) time and O(1) storage for at most 128 code units. UCS resolution is a
bounded affine basis evaluation and one checked double-precision addition.
Large angles are reduced to one turn before trigonometric evaluation.

`CadSampleCanvas` accepts the parsed point through the same state transition
and command dispatch used by pointer input. The first typed point must also be
representable by the current float-screen viewport so subsequent pointer hover
cannot fault while projecting the retained base. The second point must produce
a finite displacement. A rejected input leaves the prompt stage, selection,
history, document generation, snapshot, and retained picture unchanged.

Accepted first-point input advances to `AwaitingSecondPoint`. Accepted second-
point input clears the bounded prompt and executes exactly one existing MOVE or
COPY command. Coincident MOVE remains a completed no-op and coincident COPY
remains an exact overlapping copy. Pointer and typed input can be mixed in
either valid order, and Escape retains its existing cancellation behavior.

## Rendering and native parity audit

This slice changes point acquisition and adds no render, scene-compilation,
resource, cache, shader, C ABI, or native algorithm. Hover continues to record
one O(1) guide and translated bounds without snapshot publication. After a
commit, managed and native renderers consume the same rebuilt retained picture,
so a separate native implementation is not applicable.

## Verification and remaining gates

Focused parser tests cover every accepted grammar, whitespace, invariant
fractions/exponents, angle reduction, relative resolution, length bounds,
malformed input, non-finite values, negative polar distance, and overflow.
Shared-shell tests cover absolute base plus relative Cartesian displacement,
mixed pointer/relative-polar input, one-generation MOVE/COPY, Undo/Redo,
selection preservation, input rejection without publication, stage-specific
enablement, and Enter submission.

Object/grid/intersection snaps and Ortho/polar tracking are implemented by
later documented slices. Direct-distance cursor entry is a separate
context-sensitive modifier without changing this grammar; see
`PROGPU_CAD_DIRECT_DISTANCE_RESEARCH.md`. Current-UCS/global-last-point behavior
for MOVE/COPY and COPYBASE/PASTECLIP is specified in
`PROGPU_CAD_UCS_LAST_POINT_RESEARCH.md`. Broader authoring adoption, coordinate
filters, arbitrary-camera planes, 3D pointer acquisition, grips, and typed
ROTATE/SCALE base/reference prompts remain follow-ups. CAD-object
COPYBASE/PASTECLIP is specified in `PROGPU_CAD_CLIPBOARD_RESEARCH.md`.
