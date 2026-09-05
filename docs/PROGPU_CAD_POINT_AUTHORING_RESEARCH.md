# ProGPU.CAD POINT authoring research record

## Scope and authoritative behavior

This checkpoint adds one bounded shared desktop/browser `POINT` command over
ProGPU's existing exact retained POINT pipeline. The implementation is
clean-room and derives behavior only from public contracts:

- Autodesk's [POINT command reference](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-3F5861A1-9A63-42A6-8F12-3395771BAA6D.htm)
  specifies one point-object result, one `Specify a point` prompt, 2D or 3D
  input, and PDMODE/PDSIZE-controlled appearance.
- Autodesk's [POINT DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-9C6AD32D-769D-4213-85A4-CA9CCB5C5317.htm)
  defines group 10/20/30 as one WCS location, group 39 as thickness, group 210
  as extrusion direction, and group 50 as the UCS X-axis angle in effect when
  the point was drawn and PDMODE is nonzero.
- Autodesk's [MULTIPLE command reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-60331A1C-1CD0-4626-BC56-F33D0A3683E9.htm)
  establishes that repetition is a generic outer command wrapper stopped by
  Escape, not a multi-point state embedded in POINT.
- Autodesk's [PDMODE reference](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT-MAC/files/GUID-82F9BB52-D026-4D6A-ABA6-BF29641F459B.htm)
  confirms that point display mode is drawing-persisted global state.

No third-party source text, names, helpers, control flow, tables, or file
organization were copied or adapted. Approved implementation provenance is the
original ProGPU-owned coordinate parser, plan viewport, object snap, grid snap,
edit history, snapshot compiler, point-marker compiler, and native picture
compiler already in this repository. ACadSharp remains the approved persisted
object model and DXF/DWG I/O boundary.

## Adopted acquisition and persistence contract

`CadPointAuthoringSession` validates one finite double-WCS location in O(1)
time and O(1) storage. A click or absolute invariant Cartesian/polar coordinate
commits immediately and ends the command. Relative and direct-distance input
fail explicitly because ProGPU does not yet expose a global last-point or
command-local base before POINT accepts its sole location. Escape cancels
without publishing a generation. A later generic MULTIPLE facility may repeat
POINT, but this command does not fabricate that outer state.

`CadAddPointCommand` creates one ACadSharp `Point` and one history entry.
CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, and THICKNESS are captured
atomically on first Apply. Locked layers, invalid CELTSCALE/PDSIZE/PDMODE, and
nonzero THICKNESS fail before mutation. Undo detaches the retained point and
Redo reattaches the same entity identity.

When PDMODE is nonzero, the command validates the active UCS axes, persists
their normalized plane normal as group 210, and expresses the active UCS X
axis as group-50 rotation in that OCS basis. PDMODE zero uses the canonical WCS
normal and zero rotation because marker orientation is irrelevant. PDMODE and
PDSIZE themselves remain drawing-global header values and are not copied into
the entity.

## Rendering, performance, and parity applicability

POINT has no accepted intermediate state, so it does not allocate an accepted
geometry preview or rebuild one on pointer motion. The shared object-snap and
grid markers remain the live acquisition feedback. After commit, the ordinary
generation rebuild feeds the new entity to the existing exact POINT pipeline:
PDMODE zero point batches or viewport-dependent analytic PDMODE marker paths,
with the same PDSIZE, lineweight, selection, Node snap, print, and native replay
contracts already documented in `PROGPU_CAD_ARCHITECTURE.md`.

The existing POINT rendering research against Skia, DirectWrite/Direct2D and
Win2D, WebRender, Vello/Parley, SkParagraph, and HarfBuzz was re-audited for
applicability. This authoring slice changes no retained primitive, renderer,
scene compiler, cache key, shader, text shaping/layout, DPI/subpixel policy,
upload, resource lease, device-loss path, C ABI, generated wire record, or
managed/native crossing. Therefore no new engine concept or paired C++ CAD
frontend is applicable. Both managed and native renderers consume the same
existing immutable point/path commands; the focused native replay regression
validates that boundary.

## Complexity, validation, and remaining gates

Acquisition, Apply, Undo, and Redo are O(1) time and retained storage. The
normal document rebuild keeps its existing O(E) snapshot/scene cost for E
entities. Steady pointer motion uses the existing bounded snap query and adds no
POINT-specific allocation or native call.

Focused tests cover finite validation, atomic current-property capture, active
UCS group-50 orientation, mode-zero orientation irrelevance, locked-layer and
invalid-current-state preflight, entity-identity-preserving Undo/Redo, immediate
typed commit, relative/direct-distance rejection, Escape cancellation, Node
snap reuse, shared controls, DXF/DWG round trips, exact marker lowering, and
managed/native retained replay.

The publication gates passed on 2026-08-31:

- focused POINT authoring tests: 11/11 in Debug;
- all CAD authoring tests: 191/191 in Debug and Release;
- complete .NET 10 CAD suite: 1,253/1,253 in Debug and Release;
- direct `ACadSharp.ProGPU` and `ProGPU.CAD` packages built at
  `0.1.0-preview.62`; the two-package content/dependency audit passed, and the
  isolated package-only consumer restored, built with zero warnings and zero
  errors, rejected upstream `ACadSharp`, and created an AC1032 document.

The grouped package-list precheck still reports the separately user-deleted
browser sample project. The equivalent direct two-package build, audit, and
isolated consumer gate passed without restoring or staging those deletions.

Global last-point/current-elevation semantics, generic MULTIPLE command
repetition, command chaining, temporary overrides, expressions and drawing
units, arbitrary-camera acquisition, visual goldens, POINT thickness geometry,
and dense-authoring p50/p95/p99 evidence remain later gates.
