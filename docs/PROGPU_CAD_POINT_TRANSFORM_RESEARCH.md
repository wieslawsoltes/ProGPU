# ProGPU.CAD two-point MOVE/COPY research

Date: 2026-08-30

## Scope and clean-room provenance

This slice adds an interactive base-point/second-point workflow over the
existing typed selection-set MOVE and COPY commands. It does not add a new
entity transform, renderer, scene format, shader, native ABI, or ACadSharp
change. The implementation was designed from the public behavior contracts
below and from these original ProGPU-owned sources in this repository:

- `src/ProGPU.CAD/CadPlanViewport.cs` for allocation-free double-WCS to
  float-screen mapping;
- `src/ProGPU.CAD/CadEditing.cs` for transactional selection-set translation,
  structurally complete duplication, and exact inverse Undo/Redo;
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs` for semantic selection ownership,
  pointer routing, retained overlays, and generation replacement;
- `src/ProGPU.CAD.Sample/CadSampleView.cs` for the shared desktop/browser
  command shell.

No third-party implementation source, helper structure, naming, or control
flow was consulted or used.

## Public behavior sources

- Autodesk's current [MOVE command](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-47CE7325-84C0-4414-80A3-29DC98392709-htm.html)
  defines selection followed by a base point and second point; their difference
  is the move vector.
- Autodesk's [COPY command](https://help.autodesk.com/cloudhelp/2020/ENG/AutoCAD-Core/files/GUID-1CF9287F-06E8-4D03-8377-2E130862FE02.htm)
  uses the same two-point vector while retaining the original selection. It
  separately defines Multiple and item-count-includes-source Array/Step/Fit
  modes.
- QCAD's [Move / Copy reference](https://www.qcad.org/doc/qcad/latest/reference/en/scripts/Modify/Translate/doc/Translate_en.html)
  likewise starts from an existing selection, accepts a reference point and a
  target point, then chooses whether to retain the original entities.

The adopted observable contract preserves the prepared selection, accepts two
points, computes `second - base`, and commits one atomic MOVE or COPY. ProGPU
chooses the operation before point acquisition because its shared shell already
exposes separate typed commands. The later explicit Multiple COPY mode retains
the same base and repeats independently reversible placements until Enter,
Escape, or the caller's placement bound. Autodesk's displacement shortcut, UCS,
and QCAD's post-point option dialog remain separate interaction contracts.
Bounded explicit absolute/relative Cartesian and polar coordinate entry was added in the next
checkpoint and is specified independently in
[`PROGPU_CAD_COORDINATE_INPUT_RESEARCH.md`](PROGPU_CAD_COORDINATE_INPUT_RESEARCH.md).

## State, ownership, and invalidation

`CadSampleCanvas` owns one bounded interaction state:

1. `AwaitingBasePoint` retains only the operation kind.
2. The first left click converts the current plan-view screen point to a
   double-precision WCS-XY point and advances to `AwaitingSecondPoint`.
3. Hover motion updates one screen-space preview point. It emits no state event,
   mutates no entity, publishes no generation, and performs no snapshot or
   picture compilation.
4. The second click converts through the current viewport, subtracts the
   retained base point, and dispatches the existing
   `CadTranslateEntitiesCommand` or `CadDuplicateModelSpaceEntitiesCommand`
   through the synchronized history. Single mode then clears the state.
   Multiple COPY keeps the base, reports the completed placement, and accepts
   another second point without changing the source selection.
5. Escape, selection clear, document replacement, and resource release discard
   uncommitted point state without an edit. Escape after one or more Multiple
   placements ends the prompt and retains those already committed edits. A
   failed semantic command reports a typed `Failed` transition after the
   existing command has preserved transaction atomicity.

The preview is intentionally one fixed-device guide plus the translated
selection bounds. It is O(1), does not clone entity graphs, does not mutate a
retained `GpuPicture`, and does not invalidate the compiled scene root. Middle
or right drag keeps the existing camera-only pan behavior; the base point stays
in WCS while its projected screen location follows the camera. Wheel zoom also
remains a uniform-only camera update.

MOVE with coincident base and second points completes without publishing a
history entry. COPY with coincident points remains an exact overlapping copy,
matching the existing duplicate command's valid zero-displacement contract.
The source semantic handles remain selected for repeated COPY and for
MOVE/Undo/Redo.

## Complexity and parity audit

Begin, point acceptance, cancellation, hover update, and preview recording are
O(1) time and storage. A MOVE commit retains the existing O(N) semantic
transform for N selected roots; COPY retains the existing bounded O(N) clone
and ownership work. The current generation replacement remains O(E + G) for E
snapshot entities and G retained commands. This slice makes no incremental-
scene performance claim.

The managed and native renderers consume the same recompiled retained picture
after commit. No rendering algorithm, canonical shader, resource identity,
device-loss rule, C ABI, or native code changes, so a paired native
implementation is not applicable. Existing managed/native transform and copy
regressions remain authoritative; the new tests cover only the shared input
state and its dispatch into those commands.

## Verification and remaining gates

Focused tests cover stage ordering, exact WCS displacement, O(1) hover preview,
zero snapshot publication before commit, one-generation MOVE/COPY, preserved
source selection, Undo/Redo, zero-displacement COPY, no-op MOVE, Escape and
direct cancellation, and shared desktop/browser button enablement. Existing
command tests cover nested/attributed entities, transaction rollback,
managed/native replay, and DXF/DWG round trips.

The later documented slices implement object/grid snaps, direct-distance cursor
entry, Ortho/polar tracking, and current-UCS/global-last-point typed resolution
on this state. Arbitrary-camera planes, 3D point acquisition, associative arrays, full
transformed-geometry ghosting, and grip editing remain explicit
follow-ups. This is a behavior and
workflow checkpoint, not a before/after performance improvement; macOS
Instruments evidence is therefore not claimed.
