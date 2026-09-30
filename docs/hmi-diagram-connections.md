# Semantic HMI diagram connections

The HMI studio supports same-screen, nozzle-to-nozzle diagram topology. A connection is a document object with stable endpoint identities, not a decorative stretched pipe and not an industrial network connection. The existing retained compositor draws it, and the existing shared designer remains responsible for equipment manipulation and history.

The Northwater sample now connects the buffer tank, isolation valve, transfer pump and polishing filter using three semantic links. Moving, resizing, mirroring or quarter-turning equipment updates the affected routes. Opening the sample, creating a link, or selecting a line never connects to hardware or sends a process command.

## Authoring

Use **Diagram → Connect nozzles**, the connection icon, or **Ctrl+L**. Click a source nozzle and then a destination nozzle. The first click only establishes a pending gesture; the second creates one validated undo transaction. **Escape** cancels. A project edit, screen change or preview transition also cancels a pending gesture. Nozzles are visible while the tool is active, with a separate selected-source marker.

Click a routed segment or select its row in the **Diagram** data tab to edit it in the HMI inspector. The inspector exposes the name, process/signal/electrical style, Boolean feedback tag, width, clearance, direction marker, hidden state and lock. Routing diagnostics and path length are read-only. **Reverse** swaps the endpoints; **Delete** removes the selected unlocked link. Zoom-to-selection frames a selected line as well as equipment.

Equipment remains the foreground picking target over its own shape. The link layer is registered as a shared-designer decoration, so it is not a giant selectable component, does not appear in the logical outline, does not enter Select All, and does not become interactive in the shared canvas's interaction mode. It remains a genuine retained child with the existing pan/zoom transform—not a bitmap overlay or another canvas implementation.

Deleting equipment removes its incident unlocked links in the same transaction. An incident locked link rejects the deletion and restores the visual. Copy/paste includes only links whose two endpoints are selected; it assigns fresh link and element identities. Links to unselected equipment are deliberately not copied. Duplicating a screen remaps all internal endpoint identities. Renaming a tag updates diagram feedback references, and the engineering report counts them as actual consumers.

## Stable ports and presentation

`HmiSymbolPorts` defines immutable, original ProGPU nozzle identities for 20 built-in symbol types: tank, pump, the four valve variants, pipe, heat exchanger, filter, strainer, compressor, heater, boiler, cooling tower, reactor, agitator, separator, flow meter, pressure transmitter and level transmitter. No process nozzles are invented for unrelated indicators, motors or UI buttons.

For unframed process presentation, `HmiPortLayout` and `HmiSymbolRenderer` share the same glyph fitting and mirror/rotation functions. Thus the attachment location follows the rendered nozzle rather than an approximate fraction of the entire control rectangle. Text remains upright. The `ShowConnectionPorts` appearance property controls glyph decoration; it does not delete topology.

A **card presentation** projects the nozzle to the corresponding card border. The whole card is an obstacle, preserving captions and readouts rather than drawing a lead behind an opaque card or across its text. Process presentations retain the actual mechanical nozzle point. Very small controls that cannot expose a glyph remain valid editable objects, but incident routing reports that the endpoint must be enlarged.

Nozzle names are stable identifiers such as `inlet`, `outlet`, `shell-inlet`, `shell-outlet`, `vent`, `drain` and `process`. They are not claims of a vendor equipment model, pipe specification, electrical terminal assignment or ISA symbol certification.

## Document and reusable APIs

`HmiScreen.Links` owns a list of `HmiDiagramLink`. Each link owns detached source and target `HmiLinkEndpoint` objects. Generated JSON metadata includes this closed graph and a typed string-enum converter for `HmiLinkKind`. Older version-1 documents without `links` receive independent empty lists. Computed bend points are not persisted as if they were topology. `HmiDiagramLink.Waypoints` stores up to 16 explicit ordered document-space constraints; the router preserves their exact positions while avoiding equipment. See [route editing](hmi-route-editing.md) for drag handles, cancellation, history and bounded search.

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;

var project = HmiShowcaseProject.Create();
var screen = project.Screens[0];
var link = screen.Links[0];
link.Name = "Feed / isolation";
link.Kind = HmiLinkKind.Process;
link.ActivityTag = "Valve.Open"; // Existing Boolean state, not a writable link.
link.Clearance = 12;

var designer = new HmiDesignerHost(project);
// In an owning WinUI application: window.Content = designer;
designer.SelectDiagramLink(link.Id);
designer.ZoomToSelection();

// Runtime use requires no designer assembly.
var runtime = new HmiRuntime(project);
runtime.Start(allowLocalWrites: false);
var view = new HmiScreenView(project, runtime);
IReadOnlyDictionary<string, HmiRouteResult> routes = view.DiagramLayer.Routes;

// At permanent removal:
view.Dispose();
runtime.Stop();
designer.Dispose();
```

`HmiLinkLayer` is also independently reusable. `SetScreen` snapshots validated topology and geometry; `ReadSample` and `RefreshTags` update feedback without editing that snapshot. `Routes` exposes immutable computed results, `RoutingPasses` counts route computations rather than frames, and `HitPort`/`HitLink` operate in document coordinates. The owner must use the existing view transforms for pointer coordinates. Like the surrounding WinUI components, the layer is single-owner-thread UI state.

## Routing and bounded work

The UI-independent `HmiOrthogonalRouter` is an original deterministic rectilinear visibility-grid A* implementation. Its grid contains all admitted inflated obstacle boundaries and both nozzle escape points. Search state retains arrival axis to penalize bends and the next waypoint index to preserve global constraint order. It does not select independent greedy routes between pins. Nozzle leads preserve their required departure/arrival direction, including a collinear reversal; simplification cannot erase that constraint.

The limits are explicit: **256 links per screen, 128 visible routing obstacles, and 1024 simplified points per route**. Obstacles include equipment glyphs and instrument/cards, excluding labels, drawing panels and passive pipe artwork; a pipe used as an endpoint is added as an owned obstacle. A document may still contain more controls than this routing budget, but a link cannot silently route through omitted obstacles. Over-budget scenes return `CapacityExceeded`; blocked nozzle escapes return `BlockedTerminal`; unavailable paths return `NoRoute`. These results contain no invented successful path. The editor shows diagnostics and endpoint failure marks.

The graph uses pooled, bounded search scratch. With `n` admitted obstacles its visibility grid has `O(n²)` nodes; A* queue work depends on the visited grid. This is dependent, edit-time graph search, not a GPU rasterization workload or a simulation-step operation. It creates no rendering device, shader pipeline, worker pool or framebuffer readback. Routing is synchronous and not a hard-real-time scheduling guarantee.

Successful geometry is retained across telemetry, quality, palette, name and style-only changes. Endpoint changes and obstacles intersecting an existing route invalidate that route; an unrelated edit leaves it intact. Route reuse rechecks the full visible-obstacle admission budget even when the new obstacles do not touch the cached line. Removing an unrelated obstacle may leave a still-valid, nonminimal route rather than needlessly perturbing the drawing. Rendering emits bounded retained lines, elbow fills, dashes and direction marks; feedback updates never run the router.

Clearance is additional space outside a conservative stroke/direction-marker envelope. The routing calculation accounts for the stroke and arrow envelope before testing obstacles. These are visual layout distances, not engineering pipe separation or a safety clearance.

## Feedback is not control logic

A link can bind to one existing Boolean activity tag. Good/true feedback highlights the line; good/false uses the neutral style. Missing, bad, uncertain or stale feedback uses an explicit unknown line treatment and cross, with no direction arrow. The tag remains an independent observation: a pump-running signal alone does not prove fluid flow.

Process, signal and electrical kinds select presentation only. Direction arrows describe the stored source/target topology, not measured flow direction, causality, a controller permissive or a verified electrical circuit. There is no flow solver, PLC logic generation, implicit command, process interlock or inferred transport mapping. Existing Modbus/MQTT/OPC UA authorization, review, reconnect-generation and write-outcome safeguards are unchanged.

## Validation

The focused suite adds independent tests for topology validation and budgets, generated JSON with reflection disabled, legacy documents, reference renaming, all 16 direction pairs, all 16 mirror/quarter-turn combinations, card-border attachments, exact terminal ownership, maximum obstacle admission, blocked escapes, degenerate/backtracking paths, immutable snapshots, route-cache reuse, visibility, locked deletion, clipboard remapping and undo/redo.

The actual retained-renderer smoke test creates a connection through injected nozzle clicks, picks its routed line, and undoes the transaction. Its independent line atlas compares only connection pixels—not labels—to distinguish process/signal/electrical styling and active/stopped/unknown feedback, and checks blocked endpoints. Existing component, palette, runtime-command, overlapping-selection and viewport tests remain enabled. `SerializationSmoke` additionally executes the router and round-trips links under CoreCLR/NativeAOT with reflection serialization disabled.

```sh
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke -c Release
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.VisualSmoke -c Release -- \
  /path/to/installed-font.ttf artifacts/hmi-visual Dark
```

The renderer's exact software/physical adapter and test source revision must accompany results. A passing software-Vulkan run is not physical Apple Metal/Retina qualification. No hardware process connection is part of the diagram tests.

## Remaining diagram boundaries

This is automatic same-screen orthogonal routing, not a plant-design or electrical CAD system. Ordered manually pinned waypoints, on-canvas insertion/dragging and exact coordinate editing are implemented in [route editing](hmi-route-editing.md). Editable whole-segment handles, semantic branch/junction objects, crossover bridges, wire numbering, pipe specifications, cross-screen connectors and physics are not implemented here. Flat faceplate templates do not yet store their own internal link graph; links between instantiated equipment live on the screen, and template changes that would invalidate a referenced nozzle are rejected by document validation. Commissioning, industrial historian persistence, redundancy and vendor protocol qualification remain separate concerns.

## References

The strong-port/obstacle contracts were reviewed against the public [orthogonal routing concepts](https://docs.yworks.com/yfiles/doc/developers-guide/orthogonal_edge_router.html); no external router implementation was copied. The closed persistence graph follows [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation). All glyph and transform implementation reused here is existing ProGPU source.
