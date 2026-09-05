# ProGPU.CAD XLINE authoring research record

## Scope and authoritative behavior

This record begins with the default bounded shared desktop/browser two-point
`XLINE` mode and follows its clean-room completion into every documented mode
over the already implemented unbounded construction renderer. The design is a
clean-room implementation from public contracts:

- Autodesk's [XLINE command reference](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-Core/files/GUID-40650DCE-E8CA-483C-8E25-7FA9AB6992C1.htm)
  defines an infinite line and the default Point mode as a line through two
  specified points. Its repeated prompt keeps the first point common to later
  construction lines.
- Autodesk's [XLINE DXF contract](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-DXF/files/GUID-55080553-34B6-40AA-9EE2-3F3A3A2A5C0A.htm)
  defines group 10/20/30 as the WCS first point and group 11/21/31 as a unit
  WCS direction.

The initial checkpoint intentionally covered only Point mode. Subsequent
checkpoints add Horizontal, Vertical, Angle, Bisect, and Offset through the
same bounded typed command contract.

No third-party implementation source was copied, translated, or structurally
adapted. Approved implementation provenance is the original ProGPU-owned RAY
unit-direction reduction, coordinate/direct-distance/object-snap/grid/Ortho/
polar acquisition, edit history, snapshot, construction clipping, retained
picture, and managed/native picture compiler code already in this repository.
ACadSharp supplies only ProGPU's approved persisted object model and DXF/DWG
I/O boundary.

## Adopted state and persistence contract

`CadXLineAuthoringSession` accepts one finite WCS first point followed by
finite, distinct through points. The first point never advances. Each through
point is reduced immediately to a unit WCS direction using the proven
overflow-resistant ProGPU RAY normalization. `U` removes only the latest
direction and retains the common first point. The default bound is 65,536
lines.

Accepted XLINEs remain transient until Enter, Escape, or Finish. Completion
with no line changes no document generation. Otherwise one
`CadAddXLineSequenceCommand` creates separate ACadSharp `XLine` objects and one
history entry. Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, and CELWEIGHT are
captured atomically on first Apply; a locked layer or invalid CELTSCALE fails
before insertion. Undo detaches the retained entity instances and Redo
reattaches the same identities.

Typed absolute and relative Cartesian/polar coordinates, positive direct
distance, pointer input, object snap, grid, Ortho, polar tracking, and PolarSnap
reuse the shared plan acquisition path. Relative input and direct distance are
always based on the fixed common point. The first point may establish an
arbitrary WCS Z; later plan acquisition keeps that plane.

## Rendering research, retained preview, and parity

The existing construction renderer was re-audited against the required engine
set because authoring adds a transient rendering consumer:

- [Skia `drawLine`](https://api.skia.org/classSkCanvas.html) retains the normal
  finite-segment plus active-transform/clip model.
- [Direct2D axis-aligned clips](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to-clip-with-axis-aligned-rects)
  and [Win2D drawing-session layers](https://microsoft.github.io/Win2D/WinUI2/html/Methods_T_Microsoft_Graphics_Canvas_CanvasDrawingSession.htm)
  reinforce explicitly clipping before finite retained drawing.
- [WebRender's retained pipeline](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  separates retained display data from viewport-specific frame work.
- [Vello's retained scene API](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  keeps path data, transforms, and clips explicit.

ProGPU adopts no new rendering algorithm. Accepted directions reuse the exact
public `CadConstructionSceneCompiler.TryClipPlan` parametric slab clipper with
`isRay: false`, batch visible two-sided segments into one multi-figure
`PathGeometry`, and record one transient screen-space picture. The picture is
rebuilt only when a direction is accepted, undone, or the viewport changes.
Pointer motion replays that picture and draws one live guide. It never
fabricates far endpoints, edits the model, compiles a snapshot, uploads CAD
data, or crosses the native boundary.

SkParagraph, DirectWrite/Direct2D text layout, Win2D text layout, Parley, and
HarfBuzz were rechecked. They are not applicable because XLINE changes no
Unicode/OpenType shaping, line layout, fallback or variable-font state, glyph
cache, DPI/subpixel placement, text upload, or text device-loss invalidation.

The managed/native applicability audit finds one semantic implementation: the
managed ACadSharp frontend creates persisted entities, then both renderers
consume the same canonical retained path command. A regression compiles the
authored construction picture with `GpuPictureNativeSceneCompiler`. No C++ CAD
frontend, C ABI, generated wire record, shader, cache key, resource lease,
callback, or crossing change is applicable.

## Follow-on mode-completion foundation

The official command prompt also defines Horizontal, Vertical, Angle,
Bisect, and Offset modes. The first follow-on slice generalizes the immutable
edit payload from one common point to one independently validated point and
unit WCS direction per XLINE while retaining the original common-point API.
`CadXLineConstruction` now provides allocation-free exact solvers for:

- Horizontal/Vertical through-point construction from caller-supplied current
  UCS unit axes;
- absolute Angle construction from an orthonormal current basis and explicit
  ANGDIR sense;
- counterclockwise Angle/Reference rotation around an explicit plane normal;
- the internal unit bisector of two vertex rays, rejecting coincident or
  straight-angle inputs;
- parallel Offset by a positive distance with side-point arbitration; and
- parallel Offset/Through in the selected source line's plane, rejecting a
  point that would reproduce the source line.

The solvers accept no document or UI state and perform O(1) time and storage.
Heterogeneous definitions persist atomically through the existing command and
preserve entity identity across Undo/Redo. The remaining work is the bounded
mode prompt state, exact selectable linear-source contract for Angle/Reference
and Offset, shared controls, transient previews, and matched interaction and
persistence tests; the modes are not yet advertised as complete.

The next slice adds that bounded host-neutral prompt state. A generation-tagged
`CadXLineLinearSourceResolver` validates the complete immutable selection
candidate identity and resolves visible LINE, RAY, or XLINE snapshot geometry;
stale, fabricated, non-linear, and degenerate candidates fail closed without
consulting or retaining the mutable ACadSharp graph. The snapshot now separately
captures raw active-UCS Horizontal/Vertical axes and the ANGBASE-adjusted angle
basis plus ANGDIR, avoiding SNAPANG or polar-tracking state as a substitute for
command geometry. `CadXLineModeAuthoringSession` models exact point, scalar, and
source prompts for default TwoPoint, Horizontal, Vertical, absolute or reference
Angle, Bisect, and distance or Through Offset modes. It bounds retained output,
keeps invalid final inputs recoverable, resets partial mode input on local Undo,
and emits heterogeneous immutable definitions accepted directly by the existing
atomic edit command. Source resolution and every prompt transition are O(1) and
allocation-free after bounded storage warmup; definition snapshot creation is
O(L) time/storage for L accepted lines.

The interactive completion slice consumes that state directly in the shared
desktop/browser view and canvas. One selector starts Point, Horizontal,
Vertical, Angle, Bisect, or Offset. The invariant input box accepts angle values
in degrees, positive offset distances, `Reference`/`R`, and `Through`/`T` at
their exact prompts while retaining the established Cartesian, polar, relative,
and direct-distance point input elsewhere.

Angle/Reference and Offset source hover/click run the existing exact crossing
query over preallocated entity, candidate, match, handle-hash, and handle
buffers. The result rejects any truncation, filters through the immutable
generation-exact resolver, measures the exact visible LINE or viewport-clipped
RAY/XLINE segment in device space, and chooses the nearest candidate with an
entity-index tie break. Hover is O(log F + K + U) typical and O(F + K + U)
worst-case for F finite primitives, K local candidates, and U unbounded
construction primitives, with no per-move managed allocation after snapshot
buffer sizing.

Accepted heterogeneous definitions remain one retained multi-figure picture.
Live Point/Horizontal/Vertical/Angle/Offset/Bisect output calls the same exact
two-sided clipper; Bisect additionally draws its accepted/current finite input
rays. Scalar prompts suppress irrelevant snap guides and source prompts draw
the exact hovered primitive. No model mutation or document generation occurs
until completion.

The solver foundation slice passed 13/13 XLINE core tests and the complete
1,271/1,271 CAD suite in Debug and Release. One first Release-suite run observed
a 4,784-byte process-noise allocation in the pre-existing warm bounds-query
test; that test passed immediately in isolation and the complete unchanged
Release binary then passed 1,271/1,271. The direct two-package preview.62 audit
also passed, and its isolated package-only consumer built with 0 warnings and
0 errors, rejected upstream `ACadSharp`, and created an AC1032 document.

The prompt/source foundation passed the combined 26/26 XLINE core and current
interaction suite in Debug and Release, including raw-UCS/ANGBASE/ANGDIR capture,
all mode transitions, stale selection rejection, opposite-maximum-coordinate
normalization, and a zero-managed-allocation warm linear-source resolver test.
The complete unchanged test binaries pass 1,279/1,279 in both configurations.
The direct preview.62 ACadSharp.ProGPU/ProGPU.CAD content audit and isolated
package-only consumer also pass; the consumer builds with 0 warnings and 0
errors, rejects upstream `ACadSharp`, and creates an AC1032 document.

The interactive mode slice passes 31/31 focused XLINE tests in Debug and
Release, covering host-neutral non-mutating preview metadata, selector-driven
independent Horizontal placement, invariant degree conversion with live
infinite preview, exact distance-offset source/side interaction and persistence,
and direct `Reference`/`R` plus `Through`/`T` prompt routing across snapshot
generations. The complete CAD suite passes 1,284/1,284 in Debug and Release.
One first Debug run observed process-noise allocation in the pre-existing warm
plan-grid creation test; that test passed immediately in isolation and the
complete unchanged Debug binary then passed.
Fresh preview.62 CAD packages pass the two-package content/dependency audit;
the isolated package-only consumer builds with 0 warnings and 0 errors, rejects
upstream `ACadSharp`, and creates an AC1032 document. The package build reports
only the existing ACadSharp warning baseline.

## Complexity, validation, and remaining gates

Acceptance and in-command Undo are amortized O(1). Snapshot creation,
Apply/Undo/Redo, and viewport-preview rebuild are O(L) time and storage for L
accepted XLINEs. Steady pointer replay is O(1) retained commands plus one live
guide, with no line-count-dependent allocation. Completed construction overlay
compilation remains O(U) for U visible RAY/XLINE records.

Focused Debug and Release tests cover bounded state, common-point semantics,
degenerate/non-finite rejection, overflow-resistant normalization, current
property inheritance, locked-layer preflight, identity-preserving global
Undo/Redo, transient retained two-sided preview and viewport refresh, typed
relative input, object-snap precedence, direct distance, shared controls, `U`,
Enter/Escape/Finish, zero-generation empty completion, DXF/DWG round trips,
snapshot lowering, and managed/native retained replay.

The publication gates passed on 2026-08-31:

- focused XLINE authoring tests: 14/14 in Debug and through the Release
  authoring gate;
- all CAD authoring tests: 205/205 in Debug and Release;
- complete .NET 10 CAD suite: 1,267/1,267 in Debug and Release;
- Release ProGPU build: no ProGPU warning or error (the independently built
  ACadSharp source retains its existing warning baseline);
- `ACadSharp.ProGPU` and `ProGPU.CAD` packages built at
  `0.1.0-preview.62`; the direct two-package content/dependency audit passed
  and an isolated package-only consumer restored and built with 0 warnings and
  0 errors, rejected upstream `ACadSharp`, and created an AC1032 document.

The grouped package-list scan still reports the separately user-deleted browser
sample project. The equivalent direct two-package build, audit, and isolated
consumer gate passed without restoring or staging those deletions.

Command chaining, temporary overrides, expressions and drawing units, 3D
UCS/arbitrary-camera acquisition, object-snap tracking, visual goldens, and
dense-sequence p50/p95/p99 measurements remain later gates.
