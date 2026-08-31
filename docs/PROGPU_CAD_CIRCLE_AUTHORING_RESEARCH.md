# ProGPU.CAD CIRCLE authoring research and conformance record

Status: implemented checkpoint, 2026-08-31

## Scope and clean-room provenance

This checkpoint adds four exact plan-view constructions to the shared desktop
and browser shell: center plus radius point, center plus diameter point, two
diameter endpoints, and three circumference points.

It does not implement Tangent-Tangent-Radius or Tan-Tan-Tan. Those modes need a
separate bounded entity-selection contract, tangent-family applicability,
multiple-solution arbitration, and selection-point proximity behavior. ProGPU
does not replace those requirements with an approximate circle.

No third-party implementation source was copied, ported, translated, or used as
a structural template. The implementation was designed from the public command
and DXF contracts below. Approved in-repository ProGPU provenance is:

- `src/ProGPU.CAD/CadLineAuthoring.cs` and
  `src/ProGPU.CAD/CadPolylineAuthoring.cs` for original ProGPU transaction,
  property-capture, and prompt-recovery patterns;
- `src/ProGPU.CAD/CadSnapshotCompiler.cs` and
  `src/ProGPU.CAD/CadPlanSceneCompiler.cs` for the existing managed analytic
  circle pipeline;
- `src/ProGPU.CAD.Native` and the existing native-picture regressions for the
  already-established equivalent native replay;
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs` and `CadSampleView.cs` for the
  existing shared typed point-acquisition and dynamically themed shell.

ACadSharp is consumed only through its reviewed in-repository public model:
`Entities/Circle.cs` and `Header/CadHeader.cs`. No ACadSharp source text is
included in ProGPU implementation files.

## Authoritative behavior sources

- [Autodesk CIRCLE command](https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-Core/files/GUID-C60B6D5D-AAEB-420F-917F-6E6B47E92F48.htm)
  defines center/radius-or-diameter, 2P diameter endpoints, 3P circumference
  points, TTR, and Tan-Tan-Tan. It also documents closest-selected-tangent-point
  arbitration when TTR has multiple solutions.
- [Autodesk CIRCLE DXF records](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-DXF/files/GUID-8663262B-222C-414D-B133-4A8506A27C18.htm)
  define OCS center groups 10/20/30, radius group 40, optional thickness group
  39, and extrusion groups 210/220/230.

For Axis-Z plan circles, OCS and WCS XY coincide. The authored entity therefore
stores the accepted WCS center with `Normal = AxisZ`. The command reads current
CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, and THICKNESS atomically on its
first apply. Nonzero THICKNESS fails before document mutation because the
current circle compiler retains only the planar analytic outline and must not
silently discard extruded side geometry.

## Cross-engine architecture review

- [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html) exposes a direct
  center/radius circle primitive and applies the current clip, matrix, and
  paint. Adopted: retain analytic center/radius state instead of building a
  point polygon for transient feedback.
- [Direct2D ellipse drawing](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to-draw-an-ellipse)
  represents the shape by center and radii and sends it to `DrawEllipse` or
  `FillEllipse`. [Win2D](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
  exposes GPU-accelerated primitive circles and reusable command lists over
  Direct2D. Adopted: one exact shared drawing command for the live circle and
  existing retained scene commands for the committed entity.
- [Vello](https://github.com/linebender/vello) is a GPU compute-centric 2D
  renderer. [WebRender](https://github.com/servo/webrender) is a retained
  GPU-based 2D renderer. Adapted: pointer motion changes only bounded prompt
  state and transient drawing; it does not mutate the CAD model, rebuild the
  immutable snapshot, upload resources, or cross a new native boundary.
- [SkParagraph](https://github.com/google/skia/tree/main/modules/skparagraph),
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://github.com/harfbuzz/harfbuzz) were examined as required.
  They shape or lay out text and have no role in circle construction, cache
  identity, fallback fonts, variable-font state, glyph upload, or this
  geometry-only preview. No text architecture changes are applicable.

The existing managed and native CAD renderers already consume the same analytic
circle snapshot and preserve selection, snapping, linetype, printing, and
persistence semantics. This checkpoint changes only host-neutral construction,
the shared shell, and document mutation. It adds no shader, packed record, C
ABI, GPU resource, cache-generation, upload, device-loss, or native algorithm
change, so paired native implementation work is not applicable. Existing
managed/native circle regressions remain authoritative.

## Geometry and state contract

`CadCircleAuthoringSession` owns a fixed two-point buffer. Two-point modes keep
one accepted point; 3P keeps two. The final point is solved without changing
the session, then `CadAddCircleCommand` is executed. If validation or document
preflight fails, the accepted points and final-point prompt remain available.

Center/radius uses the planar distance directly. Center/diameter divides that
distance by two. 2P uses the overflow-resistant half-sum center and half the
diameter distance. 3P translates the first point to the origin, scales both
remaining vectors by their largest absolute component, and solves the
normalized perpendicular-bisector determinant. This avoids squaring the large
absolute WCS origin and preserves bounded O(1) work. Duplicate, off-plane,
nonfinite, collinear, nonpositive-radius, and non-renderable-radius results fail
closed. Every accepted point remains on the exact first-point WCS-Z plane.

The shared point pipeline preserves its precedence: exact object snap, Ortho or
acquired polar path, active grid/PolarSnap, then raw pointer. Explicit typed
absolute/relative Cartesian or polar coordinates bypass pointer constraints;
after a center, a bare positive scalar is the documented radius or diameter
value without requiring cursor motion. In 2P or 3P it remains shared direct
distance along the post-base cursor direction. CIRCLE supplies no last-segment
direction, so relative polar tracking is not inferred from its construction
points.

After the required final point, one ACadSharp `Circle` is added as one history
operation and one content generation. Undo removes that same retained entity;
Redo restores the same identity and the original captured properties even when
header defaults have changed.

## Complexity and performance contract

- retained prompt storage: O(1), at most two `CadPoint3D` values;
- point validation and every construction solve: O(1);
- pointer preview: O(1), one analytic `DrawEllipse` plus at most two guide
  lines, with no tessellation, model mutation, snapshot compilation, upload, or
  managed/native call;
- apply, undo, and redo: O(1), one entity and one history record;
- completed-frame rendering: the existing analytic managed/native circle path,
  with no changed crossings, bytes copied, resources, or allocations.

This is a capability addition, not a rendering-speed claim. Dense-authoring
p50/p95/p99 measurements, visual goldens, arbitrary UCS/camera construction,
expressions/units, command chaining, temporary overrides, TTR, and Tan-Tan-Tan
remain future gates.

## Verification

Focused tests cover all four constructions, large-WCS normalized 3P solving,
duplicate/off-plane/nonfinite/collinear rejection, property capture, entity
identity through Undo/Redo, locked-layer/invalid-CELTSCALE/nonzero-THICKNESS
preflight, recoverable final-point failures, shared controls and Escape,
nonzero-Z direct distance, and DXF/DWG round trips. The complete .NET 10 CAD
suite passes 1,109/1,109 tests. The Release desktop build succeeds; its 65
warnings are the existing ACadSharp source baseline and no new ProGPU warning
is introduced. An isolated consumer restores, builds with zero warnings, and
runs against `ProGPU.CAD` plus `ACadSharp.ProGPU` version
`0.1.0-preview.62`, creating AC1032. The grouped package wrapper remains
blocked by the separately user-deleted browser sample project; the equivalent
direct pack and package-consumer gate were therefore used without restoring or
staging those deletions.
