# Ordered route editing

Route pins are persistent, exact constraints in document coordinates. They guide the existing semantic nozzle router; they are not disconnected drawing points, control-system commands or guessed process behavior. The designer, standalone runtime layer, JSON graph and clipboard share the same model.

## Authoring workflow

Select a diagram connection, then choose **Diagram → Add route waypoint** (Ctrl+Shift+L) or **Add waypoint** in the Diagram panel. Click on the shared canvas. The click is grid-snapped using the existing designer grid; insertion order is selected by the closest source-to-target position on the current routed path. The point remains where it was clicked, rather than being projected back onto the old line. When a route is unavailable, a new pin is appended. Exact positions can also be entered in the HMI inspector's pin X/Y rows.

The selected connection shows numbered, screen-sized handles. Drag a handle to preview an obstacle-avoiding route. Release commits one document transaction, regardless of the number of move events. **Escape**, pointer cancellation, lost capture, a changed screen/document, or entering simulation/live preview cancels the gesture. Previewing never publishes a partial document or an intermediate undo entry. A failed or blocked route remains explicit; the router does not silently move the pin.

Arrow keys nudge the selected pin one document unit; Shift+arrows use ten. Zoom-to-selection frames blocked pins and their endpoint equipment for recovery. **Delete** removes the selected pin first; when no pin is selected it retains the existing selected-link/equipment behavior. **Remove waypoint** removes the selected pin and **Clear route waypoints** / **Auto route** restores unconstrained routing. Reverse swaps endpoints and reverses pin order in one transaction. Locked links reject edits. A pin inside an obstacle is drawn as a warning handle above the equipment so it can be dragged back to free space. Hiding the connection or its endpoints hides its handles.

Pins are absolute screen-document coordinates. Moving equipment does not translate them implicitly: they continue to describe the operator's routing constraints. Copy/paste translates copied internal links' pins by the same offset as their equipment and assigns fresh link/endpoint identities. Duplicating a screen preserves pin positions while owning independent pin lists. Existing flat faceplate templates still do not own internal link graphs.

## Reusable API

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi.Designer;

// The selected diagram link must exist, be visible and be unlocked.
designer.SelectDiagramLink(linkId);
designer.InsertRouteWaypoint(0, new HmiPoint(320, 120));
designer.InsertRouteWaypoint(1, new HmiPoint(570, 120));

// Optional host-driven gesture; real canvas gestures capture their native pointer.
designer.BeginWaypointDrag(0);
designer.PreviewWaypointPosition(new HmiPoint(340, 140));
designer.CommitWaypointDrag(); // one undo record
// designer.CancelRouteEdit(); // restores the committed geometry instead

// Exact non-gesture transactions:
designer.MoveRouteWaypoint(1, new HmiPoint(590, 120));
designer.SelectRouteWaypoint(1);
designer.RemoveSelectedWaypoint();
```

The UI-independent overload is `HmiOrthogonalRouter.Route(source, target, obstacles, waypoints, clearance)`. The original overload is unchanged and supplies no constraints. `HmiRouteWaypoints.FindInsertionIndex` uses closest-segment arclength and deterministic source-order ties. `Translate` creates an independent list and rejects overflow before returning any result.

`HmiLinkLayer.PreviewWaypoints` is a single-owner UI API: it updates one owned link snapshot and computed route, preserving unrelated route objects and tag bindings. Reapplying the committed screen cancels that preview. The workbench owns the document transaction and enforces the gesture's original screen/link/document revision; a view operation alone never edits `HmiProject`.

## Router and failure contract

The visibility grid includes both nozzle escapes, every admitted inflated obstacle boundary and each pin's coordinates. Search state contains the grid node, incoming axis and next required pin index. This retains competing continuations across pins, rather than committing to a shortest first leg that may force a worse next leg. The heuristic is Manhattan distance through the remaining ordered pins. The existing length-plus-axis-change cost and deterministic state-ID tie break are retained. Pins may lie on straight segments; simplification preserves them even when collinear. Order is a traversal constraint, not a guarantee that the path never crosses itself or passes a later point earlier in its traversal.

The router never overrides an obstacle or nozzle escape to satisfy a pin. A point inside an inflated obstacle returns **BlockedWaypoint**, with no successful path. Nozzle failures remain **BlockedTerminal**. **NoRoute** and **CapacityExceeded** continue to return explicit diagnostics and an empty path. Adding unrelated obstacles beyond the admission budget invalidates a previously successful cached result instead of routing through omitted geometry.

All constraints are finite and within ±65,536 document units; duplicate positions and null lists are rejected. The limits are 16 pins per link, 128 visible obstacles, 1,024 simplified route points, 3,000,000 search states and 262,144 frontier entries. The staged grid is `O((k + 1) n²)` search states for `k` pins and `n` admitted obstacle/grid coordinates. Scratch arrays are pooled and bounded; there is no unbounded background queue, hidden GPU readback or per-link device. This is synchronous authoring-time search, not a hard-real-time control scheduler.

During a pointer drag only the selected link reroutes, and repeated coordinates are ignored. The gesture snapshots the committed document string once; pointer moves neither serialize the project nor rebuild equipment visuals. Unrelated route identities, telemetry and quality remain intact. Palette and telemetry updates do not run routing. Viewport-only canvas notifications do not overwrite a pending route preview.

## Persistence and validation

`Waypoints` participates in the existing `HmiJsonContext` source-generated graph. JSON reflection remains disabled in the regression executables. Legacy version-1 links with no `waypoints` field get independent empty lists; explicit null, nonfinite, duplicate and over-budget values are rejected before publication. Copying, reversal, screen duplication, file save/load and undo/redo preserve the ordered values.

The tests use an independent unit-grid Dijkstra oracle for constrained length/bend cost; test exact pin retention, terminal directions, obstacle permutation, blocked points, maximum pin count, legacy JSON and independent snapshots; and exercise copy/reverse/undo and route-cache admission. The native visual probe adds actual pointer insertion, capture, drag/release, Escape, capture loss, blocked-pin recovery and exact gesture history. It produces `waypoints-<palette>.png` and `waypoint-blocked-<palette>.png` alongside the existing component/diagram renders. These are real retained-compositor frames; the executing adapter must still be identified separately from physical-device qualification.

```sh
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke -c Release
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.VisualSmoke -c Release -- \
  /path/to/installed-font.ttf artifacts/hmi-visual Dark
```

Manually moving entire routed segments, explicit junctions/branches, crossover bridges, wire numbering and engineering flow/electrical solving remain separate work. This extension does not alter protocol authorization, feedback quality, reviewed command submission or controller-side interlocks.
