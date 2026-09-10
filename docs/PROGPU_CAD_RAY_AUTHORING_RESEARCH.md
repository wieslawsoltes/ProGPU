# ProGPU.CAD RAY authoring research record

## Scope and authoritative behavior

This checkpoint adds a bounded shared desktop/browser `RAY` command over the
already implemented unbounded construction-geometry renderer. The design is a
clean-room implementation from public contracts:

- Autodesk's [RAY command reference](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-A7A32623-24A4-453C-B3DD-877A6E4D6216.htm)
  specifies one start point, a redisplayed through-point prompt that creates
  multiple rays, and Enter completion.
- Autodesk's [construction-line workflow](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-LT/files/GUID-E5BB4333-DAA8-4F49-948E-0709297E3C88.htm)
  confirms that every subsequent ray passes through the first point.
- Autodesk's [RAY DXF contract](https://help.autodesk.com/cloudhelp/2018/ENU/AutoCAD-DXF/files/GUID-638B9F01-5D86-408E-A2DE-FA5D6ADBD415.htm)
  defines group 10/20/30 as the WCS start and group 11/21/31 as a unit WCS
  direction.
- Autodesk's [AddRay contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-LT-ActiveX-Reference/files/GUID-0B34B2D9-AC2E-4DAE-9599-DB2BD495760F.htm)
  requires two unique WCS points and defines the one-sided path from the first
  through the second point to infinity.

No third-party implementation source was copied, translated, or structurally
adapted. Approved implementation provenance is the original ProGPU-owned
coordinate, direct-distance, object-snap, grid, Ortho, polar, edit-history,
snapshot, construction clipping, retained-picture, and managed/native picture
compiler code already in this repository. ACadSharp supplies only ProGPU's
approved persisted object model and DXF/DWG I/O boundary.

## Adopted state and persistence contract

`CadRayAuthoringSession` accepts one finite WCS start followed by finite,
distinct through points. The start never advances. Each through point is
reduced immediately to a unit WCS direction, so the active state matches the
persisted entity rather than retaining arbitrary ray lengths. Normalization
uses component scaling, with a second scaled-endpoint path when subtraction of
opposite finite coordinates overflows. `U` removes only the latest direction
and retains the common start. The default bound is 65,536 rays.

Accepted rays remain transient until Enter, Escape, or Finish. Completion with
no ray changes no document generation. Otherwise one
`CadAddRaySequenceCommand` creates separate ACadSharp `Ray` objects and one
history entry. Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, and CELWEIGHT are
captured atomically on first Apply; a locked layer or invalid CELTSCALE fails
before insertion. Undo detaches the retained entity instances and Redo
reattaches the same identities.

Typed absolute and relative Cartesian/polar coordinates, positive direct
distance, pointer input, object snap, grid, Ortho, polar tracking, and PolarSnap
reuse the shared plan acquisition path. Relative input and direct distance are
always based on the fixed start, not the previously accepted through point.
The first point may establish an arbitrary WCS Z; later plan acquisition keeps
that plane.

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
public `CadConstructionSceneCompiler.TryClipPlan` parametric slab clipper, batch
visible segments into one multi-figure `PathGeometry`, and record one transient
screen-space picture. The picture is rebuilt only when a direction is accepted,
undone, or the viewport changes. Pointer motion replays that picture and draws
one live guide. It never fabricates a far endpoint, edits the model, compiles a
snapshot, uploads CAD data, or crosses the native boundary.

SkParagraph, DirectWrite/Direct2D text layout, Win2D text layout, Parley, and
HarfBuzz were rechecked. They are not applicable because RAY changes no
Unicode/OpenType shaping, line layout, fallback or variable-font state, glyph
cache, DPI/subpixel placement, text upload, or text device-loss invalidation.

The managed/native applicability audit finds one semantic implementation: the
managed ACadSharp frontend creates the persisted entities, then both renderers
consume the same canonical retained path command. An authored-ray regression
compiles the resulting construction picture with
`GpuPictureNativeSceneCompiler`. No C++ CAD frontend, C ABI, generated wire
record, shader, cache key, resource lease, callback, or crossing change is
applicable.

## Complexity, validation, and remaining gates

Acceptance and in-command Undo are amortized O(1). Snapshot creation,
Apply/Undo/Redo, and a viewport-preview rebuild are O(R) time and storage for R
accepted rays. Steady pointer replay is O(1) retained commands plus one live
guide, with no ray-count-dependent allocation. Completed construction overlay
compilation remains O(U) for U visible RAY/XLINE records.

Focused Debug and Release tests cover bounded state, fixed-start semantics,
degenerate/non-finite rejection, overflow-resistant normalization, current
property inheritance, locked-layer preflight, identity-preserving global
Undo/Redo, transient retained clipped preview and viewport refresh, typed
relative input, object-snap precedence, direct distance, shared controls,
`U`, Enter/Escape/Finish, zero-generation empty completion, DXF/DWG round trips,
snapshot lowering, and managed/native retained replay.

The publication gates passed on 2026-08-31:

- focused RAY authoring tests: 14/14 in Debug and through the Release authoring
  gate;
- all CAD authoring tests: 180/180 in Debug and Release;
- complete .NET 10 CAD suite: 1,242/1,242 in Debug and Release;
- Release ProGPU build: no ProGPU warning or error (the independently built
  ACadSharp source retains its existing warning baseline);
- `ACadSharp.ProGPU` and `ProGPU.CAD` packages built at
  `0.1.0-preview.62`; the two-package content/dependency audit passed and the
  isolated package-only consumer restored, built with 0 warnings and 0 errors,
  rejected upstream `ACadSharp`, and created an AC1032 document.

The grouped package-list scan still reports the separately user-deleted browser
sample project. The equivalent direct two-package build, audit, and isolated
consumer gate passed without restoring or staging those deletions.

Command chaining from the prior command endpoint, temporary overrides,
expressions and drawing units, 3D UCS/arbitrary-camera acquisition,
object-snap tracking, visual
goldens, and dense-sequence p50/p95/p99 measurements remain later gates.
