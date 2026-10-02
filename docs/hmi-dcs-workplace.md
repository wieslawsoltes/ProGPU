# Standalone DCS operator and engineering workplaces

The **ProGPU DCS** sample is a standalone, original operator/engineering application inspired by the public workplace and object-aspect workflows of **ABB System 800xA**. It is built entirely from ProGPU's existing WinUI-compatible controls, HMI components, text stack and retained renderer. It is not an ABB product, an 800xA client, or a reproduction of ABB proprietary graphics, project formats or controller software.

```sh
dotnet run --project samples/HmiDcs/HmiDcs.csproj -c Release
```

The initial screen is an **offline** Northwater plant overview. No driver, endpoint, simulation timer state, or write authorizer is activated merely by opening the sample. The shared gallery's **Visual Designer → HMI / DCS** entry now points to this standalone application rather than embedding a second complete workbench. The existing `samples/HmiDesigner` remains a focused engineering-only host for library consumers.

The standalone DCS host establishes its font before creating the studio. It
preserves an explicitly installed host default, otherwise using the existing
embedded Inter Regular face. Workplace controls snapshot that exact font at
construction and pass it to later screen/engineering views. Native `Window.Load`
occurs too late to supply those captured references and its optional Arial path
does not exist on ordinary Windows installations. The startup regression covers
this fontless-process path directly without creating a native window or GPU;
the original rendered DCS smoke cases, which explicitly supply a font, remain
required and unchanged. This fixes the missing-label startup path reported in
[issue #260](https://github.com/wieslawsoltes/ProGPU/issues/260), not a renderer or
industrial process-policy change.

The three new startup regressions link the exact sample startup source into the
existing serialized HMI test assembly; they preserve/restore the process-wide
default font and do not activate a window. They cover empty initial font state,
an explicit host face, and a later default-font change followed by navigation
and lazy engineering. Sample edits now trigger the unchanged full HMI visual and
pointer workflow as well as the existing three-platform build/test/package gate.
Post-commit local checks validated both changed project files, workflow syntax
and trigger coverage, and whitespace. Current cached HMI assemblies predate the
DCS workplace, so these new tests were not compiled or executed locally; no full
source closure, native renderer, GPU, VM or runtime staging was run. Hosted source
tests and final Windows visual confirmation remain pending.

## Workplace structure

The top-level **Operator Workplace / Engineering Workplace** switch changes working context without discarding the engineering document. Operator navigation has a persistent priority/quality band, Back/Forward/Home controls, display tabs, a searchable plant explorer, a central aspect area, a contextual object faceplate and a status strip. The alarm band remains visible while visiting graphics, alarms, events, trends and diagnostics.

Three scoped palettes—Light, Dark and HighContrast—apply to all workplaces. The normal process graphics use the existing HighPerformance convention: neutral ordinary equipment and readouts, explicit abnormal/unknown states, engineering context and upright captions. Connection ink follows the same neutral convention in operator and engineering workplaces. This is not a standards-conformance switch.

The example contains five screens: plant overview, treatment train, trends/alarms/recipes, pump station and energy utilities. It is an authoring/training example, not a commissioned plant process or physics model. Every interactive process object opens a contextual aspect; selecting a pump or valve **does not operate it**. Explicit navigation buttons navigate to their configured screen. The original equipment and bindings remain independently editable.

### Object context

Selecting equipment through the actual graphic or explorer opens its object faceplate. The faceplate presents the same retained symbol, value, source timestamp, quality, engineering range and tag identity. Operate, Trend, Alarms and Details link related information without discarding the current object. A bounded pin strip keeps up to eight object addresses. Navigation retains 64 screen/object/aspect entries and drops forward history when branching.

The process view is retained across aspect changes; opening the alarm list does not reconstruct all equipment or reset telemetry. A different screen creates a new view of the same runtime, not a new acquisition session. Workplaces can be unloaded and reattached: the local simulation stops and subscriptions are detached; navigation is retained and the screen projection is rebuilt when needed.

Plant tree and Faceplate toolbar actions release screen space. Below 1200 logical pixels the tree is automatically hidden unless explicitly overridden. The graphic uses the existing fitted Viewbox with an optional scrollable 1:1 mode. Horizontal command strips preserve overflow commands. The tree intentionally displays at most 256 matching objects; refine the search to find others. The model search API accepts a bounded result limit up to 5000.

### Alarms and events

The persistent P1/P2/P3 indicators map to the existing Critical/Warning/Information severity values, not to an imported ABB alarm model. Current alarms expose active, returned, acknowledged and unknown-quality states. Filters cover priority, text/tag, unacknowledged state, all/current conditions and selected-object scope. Locate source navigates to a graphic associated with the alarm tag. Selection-based acknowledgement requires a running local simulation, actor and comment; it never clears a still-active process condition.

The Events aspect reuses the runtime's bounded alarm-transition journal, including acknowledgement actor information and visible eviction counts. It does not claim to retrieve OPC UA Alarms & Conditions, controller sequence-of-events data or a durable audit database. CSV exports include the filtered alarm list, retained events or selected tag history. A read-only preview precedes desktop UTF-8 file saving. Formula-leading textual fields are escaped. File saves use a same-directory temporary file and replacement, not a database transaction or power-loss durability guarantee.

### Trends and diagnostics

The Trends aspect displays up to four numeric series, prioritizing the selected object's tag. Available windows are 30 seconds, one minute, five minutes and fifteen minutes, subject to the runtime's existing retention. Freeze creates detached sample snapshots for the displayed series; it does not pause acquisition. Live view returns to the current rings. Bad-quality gaps and source time are preserved by the existing `HmiTrend` renderer; the display does not fill missing history with synthetic samples.

System diagnostics show actual runtime tag values, quality and source timestamps. Refresh is explicit for the diagnostic table. The engineering link opens the existing connection commissioning tools. There are no invented controller-health, redundancy, network-security or availability indicators.

## Local simulation and command review

**Simulate** explicitly starts a local runtime. Hold pauses logical simulation time, Step advances a held runtime by 100 ms, and Stop cancels outstanding reviews and returns to offline/retained-value context. The automatic host tick is bounded to one pending UI callback. Tick time is deterministic demonstration time, not a hard-real-time schedule.

Object values that are writable in the document expose a local review action. Review ON/OFF or a numeric setpoint creates a detached, one-use review; feedback is not modified until **Confirm local**. Confirmation consumes the review before publication and rechecks object visibility/enabled bindings, type/range, feedback quality, unchanged expected feedback and the review generation. Reviews expire after 30 seconds of monotonic time, even while simulation is held or wall time changes. Cancel, navigation, stopping/holding, workplace switching and disposal invalidate them.

These reviews are **local-simulation-only**. They do not bypass or replace the transport-level authenticated authorization and reviewed-command services. A host-supplied runtime is read-only through `HmiWorkplaceSession`, even if its owning application can otherwise write; this workplace never starts or stops a borrowed runtime. Client-side UI/model checks are not an industrial security boundary. Real controller permissions, interlocks and independent safety functions remain authoritative.

The standalone sample registers the existing Modbus TCP, MQTT 5 and OPC UA factories only with the **engineering commissioning UI**. They are not invoked on startup, object selection, aspect navigation or switching workplaces. No permissive `WriteAuthorizer` is installed. Existing certificate checks, source quality, single-use external reviews, transport-generation gates and uncertain write outcomes remain unchanged. This extension adds no ABB-specific driver.

## Engineering and reusable libraries

Engineering is created lazily on its first use. It embeds the existing `HmiDesignerHost`, including all 48 original components, typed faceplates, tags/alarms/recipes, source-generated project persistence, native caption editing, draw-to-size placement, directional marquee selection, semantic nozzle connections, ordered waypoints and exact straight-segment editing. The shared designer is not forked into an independent interaction implementation.

Returning to Operator after a document edit creates a new offline runtime snapshot from the latest validated design. The editor instance, undo history and unsaved state remain intact; switching workplaces does not mark a project saved. Without document edits, the existing operator session and navigation are retained. Engineering previews/live acquisition are stopped when leaving Engineering; local operator simulation is stopped when entering it.

| Assembly | Responsibility |
| --- | --- |
| `ProGPU.Hmi` | UI-independent `HmiWorkplaceSession`, stable object/aspect addresses, local review lifecycle and `HmiDcsProject` alongside the existing runtime/model. |
| `ProGPU.WinUI.Hmi.Workplace` | Independently packable `HmiOperatorWorkplace`, scoped resources, linked aspects, faceplate, alarm/event/trend presentation. **No designer or protocol dependency.** |
| `ProGPU.WinUI.Hmi.Designer` | `HmiDcsStudio` composition and lazy engineering bridge, using existing shared design infrastructure. |
| `samples/HmiDcs` | Standalone WinUI-compatible desktop application with explicit adapter factories. |

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi.Workplace;
using ProGPU.WinUI.Hmi.Designer;

// Full standalone-capable operator + engineering composition:
var studio = new HmiDcsStudio(HmiDcsProject.Create());
window.Content = studio;
// Dispose studio when permanently closing/removing it.

// Operator-only use, without referencing any designer assembly:
var session = new HmiWorkplaceSession(project, suppliedRuntime);
var workplace = new HmiOperatorWorkplace(session);
session.Navigate(screenId, elementId, HmiWorkplaceView.Trends);
// The host owns suppliedRuntime's acquisition, dispatch, lifecycle and authorization.
// Dispose workplace, then session; suppliedRuntime is not stopped by either.
```

All runtime/session operations follow the existing **single-owner UI/application-thread contract**. Acquisition adapters must marshal batches onto that owner. Public project/object snapshots are detached. No session navigation or pending command review is serialized into the project. The sample and tests keep JSON reflection disabled.

## Retained rendering and correctness

The extension composes existing `HmiControl`, `HmiScreenView`, `HmiLinkLayer`, `HmiTrend`, `DataGrid`, `TextBox` and `RichEditBox`. It creates no extra GPU device, per-control timer, bitmap equipment renderer, WebView, font resource, shader rasterizer or replacement text shaper. Plot downsampling, glyph drawing, scene batching, shaping and GPU lifetime stay in the existing libraries. CPU work is bounded UI orchestration and snapshot/filtering, not a replacement rendering loop.

An opt-in `HmiScreenView.ObjectSelectionEnabled` switches embedded command behavior to aspect picking. Existing runtime views retain their original default behavior. HMI controls implement the existing background-hit-test interface for aspect picking without painting an opaque rectangle over the graphic.

Native testing exposed a shared Viewbox invalidation defect: its measure pass bypassed the internal presenter, allowing later child text changes to stop invalidation at an unmeasured parent. Measuring through the real presenter restores the existing layout chain. This prevents a trend's newly rendered curve from appearing beside stale value/time text. A regression verifies child remeasurement, and the original full graphics/pointer suite remains enabled. No visual comparison tolerance was relaxed.

## Validation and reference scope

```sh
dotnet test tests/ProGPU.Hmi.Tests -c Release
dotnet test tests/ProGPU.Hmi.OpcUa.Tests -c Release
dotnet build samples/HmiDcs -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke -c Release
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.DcsSmoke -c Release -- \
  /path/to/installed-font.ttf artifacts/hmi-dcs Dark
```

The DCS native probe exercises real display-tab/object clicks, local review/cancel/confirm, selected acknowledgement, export preview, linked aspects, engineering switching, unsaved design synchronization and reattachment. It renders all three palettes through actual ProGPU native WebGPU. It is additional to the existing complete HMI renderer/pointer probes, not a substitute. Test artifacts must identify source revision, SDK and software/physical adapter. Software-Vulkan results are not Apple Metal/Retina, hardware-touch, accessibility or production-PLC qualification.

The setup extension adds injected-pointer routes for Tags, Connections,
Components, Bind selected and Setup guide at both 1600×1000 and 640×1000. It
captures every revealed page/pane in every existing palette. Hidden/small data
panels must expand; larger panels stay unchanged. Wide inline neighbors remain
open, while compact side overlays dismiss the opposite pane. The exact selected
control, unsaved document, undo/redo availability and offline runtime/transport
state must survive every click. These routes use the visible toolbar arrows to
reveal overflow, never direct scroll-offset mutation or direct button callbacks.
The existing operator-compact, source tests, captures and 180-second per-palette
deadline remain unchanged. Compact widths other than 640, shorter engineering
heights, OS-delivered touch/keyboard and actual desktop DPI remain separate
qualification; an authored route or syntax check is not a passing native capture.

The setup toolbar shares the existing 40-pixel command row, preserving the
original 84-pixel header and button sizes. A prior extra auto-height setup row
failed the unchanged header-height test with 122 pixels. Removing that extra
row and making real overflow navigation explicit corrects the layout, rather
than relaxing the original assertion or hiding commands outside the viewport.

Public primary sources reviewed for workflow and presentation intent:

- [ABB System 800xA Operator Workplace](https://new.abb.com/control-systems/system-800xa/800xa-dcs/operator-interfaces-hmi/workplace-process-graphics): configurable operator workplaces, graphics and embedded trends.
- [ABB System 800xA Operations Client](https://www.abb.com/global/en/areas/automation/control-systems/800-xa/operations/operations-client): integrated graphics, alarms, events and trends, preserving object/workplace context.
- [ABB operator effectiveness](https://new.abb.com/control-systems/system-800xa/800xa-operator-effectiveness): object-centric information and operational context.

No ABB source, trademarks/logos as application identity, licensed symbol database or proprietary screen assets were incorporated. The previous [graphic-convention and cross-engine rendering boundary review](hmi-graphic-conventions.md) remains applicable; this extension changes composition and a measured-parent invalidation path, not the graphics/text architecture.

The implemented coverage is a standalone operator/engineering workflow using existing ProGPU HMI features. It is **not complete System 800xA feature parity**. ABB Aspect Object/PG2 import, AC800M/Control Builder compatibility, controller downloads, batch/SFC execution, redundant servers, 800xA authentication/permissions, historian integration, server-side alarm shelving/suppression, OPC UA subscriptions/A&C and coordinated multi-monitor operations remain separate features and integration work. The existing flat faceplate/template and same-screen diagram limits also remain. No standards or vendor certification is asserted.
