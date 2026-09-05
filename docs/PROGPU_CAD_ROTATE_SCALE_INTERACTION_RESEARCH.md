# ProGPU.CAD ROTATE/SCALE interaction research

Date: 2026-09-01

## Scope and clean-room provenance

This checkpoint adds complete bounded plan-shell base-point, direct-value, and
Reference workflows over ProGPU's existing exact pivoted rotate and uniform
scale commands. It is original ProGPU state-machine and UI code. No third-party
implementation source, helper structure, or control flow was used.

Primary behavior sources:

- Autodesk's [ROTATE command contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-1C265537-FBAC-48D5-B448-B72E777071E5.htm)
  defines a stationary base point, an axis parallel to current-UCS Z, direct
  angle/point input, and Reference mapping to a new absolute angle.
- Autodesk's [absolute-angle procedure](https://help.autodesk.com/view/ACD/2026/ENU/?caas=caas%2Fdocumentation%2FACDLT%2F2014%2FENU%2Ffiles%2FGUID-968C016A-FDC5-4ACD-845C-18AE5AB58664-htm.html)
  defines numeric or two-point reference angles followed by a numeric or point
  new angle.
- Autodesk's [SCALE command contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT/files/GUID-D4E17E51-5000-4AB6-8D6A-6D2AB4863C75.htm)
  defines a stationary base point, positive factor, and Reference scaling by
  reference and new lengths.
- Autodesk's [reference workflow note](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-DidYouKnow/files/GUID-D988658E-0A92-4353-B614-6C30CF6281A2.htm)
  confirms `@` base recall, two-point reference measurement, absolute new
  rotation angle, and `new/reference` scale behavior.

## Adopted interaction and mathematics

The shared `CadPointTransformOperation` state machine now covers Move, Copy,
Rotate, and Scale. ROTATE/SCALE first accept the same snapped, clicked, or
current-UCS/global-last-relative base point. Direct ROTATE accepts invariant
degrees or a point; positive direction follows ANGDIR and the command axis is
the immutable current-UCS normal. Direct SCALE accepts a positive factor or the
current-UCS planar distance from base to point.

`R` enters Reference. A reference angle/length may be numeric or measured by
two points. ROTATE then accepts a new absolute numeric angle or a point direction
from the transform base. SCALE accepts a numeric/point new length, or `P` and
two new-length points. Final scale is `newLength / referenceLength`. Distances
use overflow-resistant scaled hypot; angles use dot products against the
ANGBASE-adjusted axes and explicit ANGDIR sign. Degenerate, non-finite, zero,
overflowing, and non-invertible scale results fail without mutation.

Every accepted point advances the non-persisted global last point. Reference
stages remain cancellable and mutate neither document nor history. Completion
resets interaction state before dispatching one existing
`CadRotateEntitiesCommand` or `CadScaleEntitiesCommand`, preserving exact
transactional preflight, retained identity, one generation, and Undo/Redo.
The selection-centered degree/factor shortcuts remain available.

## Complexity, parity, and validation

Each prompt transition and measurement is `O(1)` time/storage. A commit retains
the existing `O(N)` transform and generation rebuild for `N` selected semantic
roots. Hover records only bounded guides; no entity graph is cloned or mutated
before commit.

This changes no shader, scene wire format, C ABI, resource identity, cache,
upload, or native renderer. Managed and native replay consume the same rebuilt
retained picture, so a separate native implementation is not applicable.
Focused tests cover current-UCS normal and ANGDIR rotation, two-point angle
Reference to an absolute angle, two-point reference and new SCALE lengths,
direct factor, global-last-point progression, one-generation commits, and exact
Undo. Existing entity-family transform and managed/native replay tests remain
the differential authority.
