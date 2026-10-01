# Direct route-segment editing

Selected HMI connections expose capsule grips on their editable horizontal and vertical route portions. Drag a grip perpendicular to the line to reposition the complete portion rather than moving two unrelated bends. Numbered waypoint handles retain their original priority. The library still uses the existing semantic link layer, shared designer canvas, pointer capture and retained compositor.

## WYSIWYG authoring

Select a line and drag its capsule grip. Horizontal portions move in Y; vertical portions move in X. Grid snapping follows the existing canvas setting, and Alt temporarily bypasses it. The fixed source and destination nozzle leads are excluded from the movable portion. Even a two-point straight route can have a movable middle: moving it inserts an explicit pair of constraints while preserving both nozzle attachments.

The preview highlights the proposed segment and reports its perpendicular offset. It owns one original route/document generation. Pointer moves update only the selected link's routing snapshot and its adorner; they do not serialize the document, rebuild equipment or add history. An unchanged offset is ignored. Release creates one transaction. A click, or a drag returned to zero displacement, creates no document change. The original unsnapped pointer displacement owns the click threshold, so changing Alt while stationary cannot produce an accidental edit.

A segment is an **exact straight constraint**, not merely a request to pass through two points. If an obstacle covers the proposed line while its endpoints remain clear, the route reports `BlockedSegment`; the preview is dashed and marked BLOCKED. It does not silently become a detour. Blocked endpoint pins retain `BlockedWaypoint`, and nozzle/route/search-budget failures keep their existing diagnostics. Release may persist an unroutable constraint as an explicit authoring request, never as successful process geometry.

A saved failed straight span retains a recovery capsule, including when equipment covers it. Dragging that capsule moves the stored pair back to free space. Ordinary numbered pins remain independently selectable. **Diagram → Release straight segments (keep pins)** frees the selected link's straight constraints without removing its waypoints. **Auto route** removes both kinds of constraints. Deleting a waypoint explicitly releases its incident straight spans. Inserting inside a constrained pair is allowed only on that line and splits the constraint into two straight pieces.

Individual pin changes cannot make an adjacent constrained span diagonal. Either drag the segment or release the relevant straight constraints first. If several constrained collinear pieces share a pin, moving only one can be rejected by its neighbor; the editor does not silently move constraints outside the selected portion. This is not coordinated whole-polyline deformation.

Grips require at least 32 screen units of segment length; zoom in for short portions. Nozzle escape leads themselves are not editable. Selection and grips are authoring state, not operator controls. Direction marks remain topology presentation, not proof of measured flow.

## Cancellation, ownership and retention

Escape, typed pointer cancellation, capture loss, secondary-button chords, viewport changes, foreign document edits, changing tools, entering runtime and disposal retire previews without publishing partial document state. A different pointer cannot move or release the owner's gesture. Owned teardown and restoration occur before releasing capture; retirement epochs prevent a synchronous capture-loss observer from allowing an old commit to overwrite a replacement gesture. The same retirement gate now protects existing waypoint drags.

The prior waypoint, marquee, placement and caption tools remain enabled. Recovery capsules have input priority over area-selection gestures and foreground equipment; normal equipment picking still governs ordinary route ink. Geometry changes are still processed by the existing router and retained layer. Telemetry and palette changes do not rerun search. No protocol, authentication, quality or command-review behavior was changed.

## Document and reusable API

`HmiDiagramLink.StraightSegments` contains ascending, unique zero-based indexes into `Waypoints`. An index `i` means that the traversal from `Waypoints[i]` to `Waypoints[i + 1]` must be one axis-aligned line. It is not an index into a transient computed path. Lists are bounded by the existing 16-waypoint budget, and explicit null, duplicate, out-of-range or diagonal constraints are rejected before publication.

Generated JSON includes the list without reflection. Old version-1 documents receive independent empty lists. Copies, faceplates' instantiated screen links and duplicated screens own independent lists. Reverse remaps `i` to `waypointCount - 2 - i` in reverse order. Copy/paste translates pin positions with equipment; pair indexes remain stable under translation. The serializer/router NativeAOT probe includes an actual constrained edge and persists its indexes.

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi.Designer;

// Selected-link route indexes are ephemeral and valid only for this route generation.
designer.SelectDiagramLink(linkId);
var segment = designer.GetSelectedRouteSegments().First(s => s.IsHorizontal);
designer.BeginSegmentDrag(segment.Index);
designer.PreviewSegmentOffset(-40); // Y for horizontal; X for vertical.
designer.CommitSegmentDrag();      // One undo entry, no equipment command.
// designer.CancelRouteEdit();     // Retire a preview instead.

// UI-independent configuration:
link.Waypoints = [new HmiPoint(300, 120), new HmiPoint(560, 120)];
link.StraightSegments = [0];
HmiRouteSegments.Validate(link.Waypoints, link.StraightSegments);
```

`HmiRouteSegmentEdit` snapshots the selected route portion and maps its endpoints into ordered pins. `Preview` creates an owned immutable `HmiRouteConstraints` result. A separate constructor handles persisted straight spans when a computed route is unavailable. `HmiLinkLayer.PreviewConstraints` atomically previews pins and pair indexes without editing the project. `GetEditableSegments` returns source-ordered portions excluding nozzle escapes; failed spans carry a `StraightSpanIndex` and a reserved computed-index range beginning at `MaximumRoutePoints`. These descriptors must not be persisted as topology.

## Router contract and cost

The original staged A* search still retains grid node, arrival axis and next-waypoint index. A constrained stage has one successor: the next pin, admitted only after testing its complete straight span against every inflated obstacle. The successor includes its actual length and bend cost; competing arrivals and following continuations remain in the same search. The search never greedily chooses independent legs. Constraint-free calls retain the original overload and behavior.

The limits remain 128 obstacles, 256 links per screen, 16 pins, 1,024 simplified points, 3,000,000 search states and 262,144 frontier entries. Additional straight-span validation is bounded by 15 spans times 128 obstacles. Plan construction scans at most 1,024 route points and 16 pins, then remaps at most 15 indexes. Adorner output is bounded by route length; dashed failure previews have at most 128 drawn pieces. This is sequential authoring-time graph orchestration with dependent traversal, not an independent raster workload moved off the GPU. Rendering reuses existing retained paths/text, cached brushes and device ownership. No frame-rate or hard-real-time claim is made.

The routing cache includes straight-span indexes in its identity. A constraint-only change therefore cannot reuse an unconstrained success. Failed routes remain explicit, and obstacle/quality semantics are unchanged. This is layout authoring, not engineering pipe clearance, electrical circuit verification, junction semantics or a process-flow solver.

## Verification and source provenance

The focused tests cover straight-edge preservation, all 16 nozzle-direction combinations, blocked segments versus unconstrained detours, fixed terminal leads, immutable snapshots, pin insertion/removal and budgets, reflection-disabled persistence, recovery, copying/reversal, no-op gestures, retention, cache identity, locks and runtime gates. Typed pointer tests cover foreign-pointer release, native cancellation, and replacement placement/segment/waypoint gestures created reentrantly during capture release.

The native visual probe exercises actual grip picking, previews, exact undo, Escape, capture loss, blocked-span pixels and over-equipment recovery in Light, Dark and HighContrast. It runs in addition to every existing component, initial-state pixel parity, caption, nozzle, waypoint, placement and marquee probe. `--segments-only` is a diagnostic selector; the default invocation retains the whole suite. Source/SDK/adapter identity and actual test results must accompany any claim of qualification.

Interaction concepts were reviewed against the public [yEd orthogonal-edge manual](https://yed.yworks.com/support/manual/orthogonal_edges.html) and [yFiles orthogonal editing contract](https://docs.yworks.com/yfiles-html/dguide/customizing_interaction_orthogonal_edges/): constrained segment motion and endpoint preservation, not source-code reuse. The implementation extends original ProGPU `HmiOrthogonalRouter`, `HmiLinkLayer`, `HmiRouteAdorner` and `HmiDesignerHost.RouteEditing` at base `a16668a6d598321ca0eb362d207558b449879d97`. No foreign implementation, renderer, shader, text shaper, font resource or extra GPU device was introduced. Existing cross-engine rendering boundaries remain those documented in `hmi-graphic-conventions.md`.

Physical GPU/Retina, touch/pen hardware, accessibility and industrial commissioning require independent qualification. All 48 component types, graphic profiles and control-system integration safeguards remain intact. Junctions, crossover bridges, recursive templates, OPC UA subscriptions/history and distributed historian workflows are not provided by this segment-editing extension.
