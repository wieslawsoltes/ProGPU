# ProGPU.CAD XLINE authoring research record

## Scope and authoritative behavior

This checkpoint adds the default bounded shared desktop/browser two-point
`XLINE` mode over the already implemented unbounded construction renderer. The
design is a clean-room implementation from public contracts:

- Autodesk's [XLINE command reference](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-Core/files/GUID-40650DCE-E8CA-483C-8E25-7FA9AB6992C1.htm)
  defines an infinite line and the default Point mode as a line through two
  specified points. Its repeated prompt keeps the first point common to later
  construction lines.
- Autodesk's [XLINE DXF contract](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-DXF/files/GUID-55080553-34B6-40AA-9EE2-3F3A3A2A5C0A.htm)
  defines group 10/20/30 as the WCS first point and group 11/21/31 as a unit
  WCS direction.

This checkpoint intentionally covers the default Point mode. Horizontal,
Vertical, Angle, Bisect, and Offset prompt modes remain separate design and
conformance gates.

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

Horizontal/Vertical/Angle/Bisect/Offset modes, command chaining, temporary
overrides, expressions and drawing units, 3D UCS/arbitrary-camera acquisition,
object-snap tracking, non-continuous unbounded linetype phase origins, visual
goldens, and dense-sequence p50/p95/p99 measurements remain later gates.
