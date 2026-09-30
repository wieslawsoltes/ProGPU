# HMI designer and reusable components

ProGPU HMI adds an embeddable human-machine interface designer, reusable GPU-rendered process controls, a UI-independent project model and a deterministic local runtime. The workbench reuses the repository's existing visual-design infrastructure instead of introducing a second canvas interaction engine.

## Run the sample

```sh
dotnet run --project samples/HmiDesigner/HmiDesigner.csproj -c Release
```

The HMI workbench is also available in the shared sample gallery's **Visual Designer → HMI** tab. Both hosts use the same `HmiDesignerHost` component.

This sample targets **ProGPU's WinUI-compatible `Microsoft.UI.Xaml` implementation**, not the Microsoft Windows App SDK. Its native execution and GPU availability follow the existing ProGPU sample-host requirements. The new components do not create their own GPU device, use a WebView, or rasterize their UI through another graphics framework.

## Package boundaries

| Library | Responsibility | Designer dependency |
| --- | --- | --- |
| `ProGPU.Hmi` | Versioned documents, typed values, validation, bounded history, alarm state, recipes, file persistence and deterministic simulation | None; UI-independent |
| `ProGPU.WinUI.Hmi` | HMI controls, retained vector drawing, component catalog and runtime screen view | None |
| `ProGPU.WinUI.Designer` | Existing shared canvas, toolbox drag/drop, outline, layout inspector; new bounded journal, multi-selection commands/adorners and custom-state registration | Shared infrastructure |
| `ProGPU.WinUI.Hmi.Designer` | HMI project sessions, workbench, binding/action inspector, data editors and local preview lifecycle | Reuses the shared designer |

The core, controls, designer and three optional transport libraries are packable and inherit the repository's versioning and signing settings. Adding them to the repository does not publish packages to NuGet automatically.

## Authoring workflows

The **Screens** pane creates, duplicates, names and switches screens. The inspector with no selection edits project name, screen name, dimensions and start-screen ID. Screen deletion refuses to remove the final screen or a screen still referenced by navigation actions.

The searchable **Components** pane offers 40 symbol types: tank, pump, valve, motor, pipe, conveyor, gauge, bar graph, numeric display/input, indicator, trend, alarm banner/list, command button, toggle, navigation button, recipe button, label and rectangle. Drag an item using the shared `ToolboxItem`, or click **+** to insert it. The original two-screen water-treatment project demonstrates linked tags, trends, navigation, alarms and recipes.

The shared `DesignerCanvas` owns dragging, resize handles, grid/snapping, pointer-centered zoom and panning. The shared logical outline treats composite HMI controls as atomic components rather than exposing their private label/input visuals. Ctrl-click or **Select all** builds a selection set. Toolbar commands align all six edges/centers, distribute horizontally/vertically, move forward/back, group/ungroup and lock/unlock. Group selection and movement preserve individual component identities. Groups are flat authoring groups. The Faceplates tab provides typed reusable equipment compositions with explicit master synchronization; recursive nested templates remain outside this implementation.

`Ctrl+C/X/V/D` copies, cuts, pastes and duplicates. The clipboard is a detached model snapshot; pasted elements get fresh identities and independent group IDs. `Delete` removes unlocked elements. Arrows nudge one unit; Shift+arrows nudge ten. `Ctrl+Z/Y` undo and redo. Text-input focus retains ordinary text-editing keys. Locking protects design manipulation; it is not runtime authorization.

**HMI** properties edit labels, units, value/visibility/enabled tag bindings, ranges, precision, actions, hidden state, group and geometry. **Layout** reuses the existing property grid. Numeric parsing uses an invariant decimal point. Visibility and enable bindings require Boolean tags. Known actions are `None`, `ToggleTag`, `WriteTag`, `Navigate`, `AcknowledgeAlarms` and `ApplyRecipe`.

**Tags**, **Alarms** and **Recipes** are editable tables. Tag renaming updates all bindings, alarm references, action targets and recipe destinations in a single validated transaction. Deleting a referenced object is rejected. Alarm thresholds support deadband and on-delay. Recipe application validates every destination before publishing any local value.

**Project JSON** exports, validates and applies the entire versioned project, including fields not surfaced by an individual table. Import validation happens before journal publication. A failed edit/import leaves the previous document and undo history intact. Project edits, screen operations and data-model edits share one bounded undo journal; selection, zoom and preview telemetry are not document changes.

## Embed the designer

```csharp
using ProGPU.WinUI.Hmi.Designer;

var designer = new HmiDesignerHost();
window.Content = designer;
// Optional host-specific physical display scale:
// designer.GetDpiScale = () => currentDisplayScale;
```

The constructor can take an `HmiProject` and a ProGPU `TtfFont`. `Session.GetProject()` returns a detached validated project, `Session.ExportJson()` returns the current committed design source, and `Session.IsDirty` compares the current document with the last saved checkpoint. `SaveFileAsync` only marks the exact saved revision clean; later edits remain dirty. Loading a file refuses to overwrite edits made while the asynchronous read was in progress.

Call `Dispose()` when permanently removing the host. Unloading the host stops simulation without discarding the project.

## Embed runtime controls without the designer

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi;

var tank = new HmiTank { Label = "TK-101", Unit = "%", Value = 62 };
var project = HmiDemoProject.Create();
var runtime = new HmiRuntime(project);
runtime.Start(allowLocalWrites: false);
var screen = new HmiScreenView(project, runtime);

// Advance on the owning application/UI thread, or publish validated acquisition batches.
runtime.AdvanceSimulation(TimeSpan.FromMilliseconds(100));
// When the screen is permanently removed:
// screen.Dispose(); runtime.Stop();
```

`HmiControlCatalog.Items` describes all 40 insertable symbols. `HmiControlCatalog.Create` creates configured controls. Strongly typed classes are provided for tank, pump, valve, motor, gauge, trend, alarm list and numeric display; `HmiControl(HmiSymbol)` covers every symbol. Common display values use dependency properties (`Value`, `Label`, `Unit`, `Quality`, `IsActive`). `ApplyDefinition` and `CaptureDefinition` exchange detached design configuration, not event handlers or live telemetry.

`HmiDesignerRegistration.Register()` is an optional bridge. It registers factories, configuration copying and atomic logical-tree policy in the shared designer registry. Runtime controls never reference the designer assembly. Third-party controls can use the same registration overload without changing HMI internals.

## Runtime semantics and performance

The runtime has a **single owner thread**. Acquisition adapters must marshal completed batches onto that thread. `IHmiTagSource` is the read-adapter seam. Optional `ProGPU.Hmi.Modbus` and `ProGPU.Hmi.Mqtt` packages provide real transports; see [control integrations](hmi-control-integrations.md). Samples have typed values, quality and source timestamps. Publishing validates the whole batch first and rejects unknown tags, malformed quality, future timestamps and per-tag time reversal. Effective stale quality is computed from each tag's configured timeout.

Local writes require a running runtime, explicit local-write permission, a writable destination, good quality, matching value type and an in-range value. The sample's **Run** command explicitly enables local simulation writes. The reusable runtime defaults to read-only. Local simulation writes are never forwarded to a transport. Live acquisition uses a separate, explicitly authorized and reviewed external-command coordinator.

Alarm state distinguishes active, returned and acknowledged conditions. Acknowledgement does not clear an active condition. Bad/stale quality preserves an existing active alarm and reports unknown quality instead of silently returning it to normal. Delay timing is based on the runtime clock. The bounded audit journal records local commands and lifecycle operations; it is not a durable, tamper-evident compliance log.

History uses fixed-capacity chronological rings with an aggregate one-million-sample budget. Trend drawing emits min/max buckets to preserve spikes while bounding geometry to the plot width (at most 512 buckets). Bad-quality intervals break connecting lines. Runtime views index controls by tag and update the affected controls rather than rebuilding the designer. Value, quality and activity updates are batched per control. Static controls do not own timers or GPU devices.

The workbench uses one 100 ms simulation timer with at most one queued UI callback. Generation checks discard callbacks from stopped previews. Pausing stops logical time; stepping advances exactly 100 ms. Simulation time is deterministic tick time, not a claim of wall-clock real-time scheduling. Diagnostic/tag-table refresh is explicit; the runtime canvas updates live.

## Persistence and validation

The format is JSON schema version 1, with strict member names and string enums. It never deserializes arbitrary CLR type names or evaluates scripts. Limits include 8 MiB per document, 128 screens, 4096 tags, 4096 alarms, 256 recipes, 5000 elements per screen, bounded text and finite geometry/ranges. Unknown schemas, dangling references, invalid action destinations and incompatible value types are rejected.

Desktop saves write a same-directory temporary file, then replace the destination. The method validates before writing, cleans up abandoned temporary files and does not modify the destination on validation failure. It does not promise power-loss durability or a database-grade multi-user transaction. The undo journal is separately bounded by state count and estimated string memory.

## Validation

```sh
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet build samples/HmiDesigner/HmiDesigner.csproj -c Release
dotnet build src/ProGPU.Samples/ProGPU.Samples.csproj -c Release
dotnet pack src/ProGPU.Hmi/ProGPU.Hmi.csproj -c Release
dotnet pack src/ProGPU.WinUI.Hmi/ProGPU.WinUI.Hmi.csproj -c Release
dotnet pack src/ProGPU.WinUI.Hmi.Designer/ProGPU.WinUI.Hmi.Designer.csproj -c Release
```

The dedicated GitHub Actions workflow builds the standalone host and gallery, runs focused model/designer regression tests, and produces package/test artifacts. Build/test results must be distinguished from interactive native GPU qualification. Automated model tests do not establish Windows/macOS/Linux pointer behavior or production GPU pixel correctness.

## Deliberate boundaries

This implementation is an HMI authoring, rendering and local-runtime library, **not a commissioned SCADA or safety system**. Optional adapters support Modbus TCP and MQTT 5/TLS with the boundaries in [control integrations](hmi-control-integrations.md). The optional OPC UA package supplies typed scalar acquisition, bounded browsing and reviewed writes; see [OPC UA commissioning](hmi-opcua.md). It does not include OPC DA, PLC downloads, distributed redundancy, durable historian storage, alarm shelving/escalation, multi-user authorization, a credential vault, tamper-evident audit storage, recursive nested symbol templates, engineering pipe specifications or electrical circuit solving, or vendor project-format interoperability. Alarm-list controls show a bounded summary, not a server-side alarm historian. Browser file pickers, deployed browser sample qualification and device-specific touch qualification are separate work.

Real equipment integration must implement authenticated transport, least-privilege authorization, server-side interlocks and validation, write confirmation/timeout semantics, reconnect quality, commissioning and independent safety functions. Do not use visual state or client-side designer locks as a safety interlock. No certification or vendor feature parity is asserted.


## Equipment and commissioning extension

Connections edits endpoint profiles and typed I/O mappings, starts explicit read-only acquisition, and reviews single-use external write requests. Faceplates captures and instantiates equipment masters with typed slots. States edits priority-based equipment conditions. New symbols include heat exchangers, filters, compressors, fans, heaters, thermometers, boilers and cooling towers. See [the integration guide](hmi-control-integrations.md) for protocol details, security boundaries and tests.


## Process studio appearance

The standalone app and gallery open the Northwater process studio example. [The visual studio guide](hmi-visual-studio.md) covers the 40-symbol renderer, palette selection, orientation, dynamic captions, compact menu layout, rulers, runtime scaling, source-generated JSON and executable pixel/input probes.

## Semantic diagram editing

The [diagram connections guide](hmi-diagram-connections.md) covers stable nozzle topology, orthogonal routing, card/process attachment rules, line feedback and diagnostics, copy/undo behavior and bounded work. The Northwater sample uses actual routed connections between its process equipment. Diagram links are separate from network connection profiles and never submit control commands.
