# HMI authoring tools, alarm console and time-domain trends

These features extend the shared ProGPU WinUI HMI designer and the independently reusable HMI component library. They do not change the external-write authorization policy or create equipment connections automatically.

## Reflection-disabled applications

HMI project persistence, cloning and the designer journal use `HmiJsonContext`, a compile-time `System.Text.Json` serialization context. Every call selects `JsonTypeInfo<HmiProject>` explicitly. Closed generic enum converters preserve string-only enums without runtime type construction. Unknown members, numeric enum tokens, invalid references and the existing document budgets remain rejected.

This fixes the desktop gallery crash when a host disables reflection serialization, including trimmed or AOT-oriented hosts. Do not enable reflection globally as a workaround. The complete graph includes connection profiles, OPC UA settings, faceplates, state rules, recipes and trend options. Older schema-version-1 projects without trend options acquire their documented defaults.

MQTT command encoding writes its fixed `id` / `timestamp` / typed `value` envelope directly with `Utf8JsonWriter`. It has no anonymous-object or boxed-object serialization path. The encoded UTF-8 payload is bounded, including JSON escape expansion. This is the same absolute-command contract; it does not grant write authorization.

`tests/ProGPU.Hmi.Tests` disables reflection serialization in the actual test process. The separate `ProGPU.Hmi.SerializationSmoke` application checks the core graph, Unicode, runtime recipes and file persistence under both CoreCLR and a real published NativeAOT executable. A core smoke pass is not proof that all third-party protocol stacks or the complete desktop application are NativeAOT-compatible.

## Engineering diagnostics and tag cross-references

Open **Engineering → Analyze project**. The analyzer checks document validity, offscreen elements, repeated names, unbound or mistyped instruments, read-only input targets, missing button actions, navigation reachability and live-source/write-mapping coverage. Diagnostics are advisory except for existing project validation errors; they are not commissioning approval.

The **Tag cross-references** view lists value, visibility, enabled-state, state-rule, command, alarm, recipe, acquisition and permissive consumers. Concrete references in faceplate masters are included; template-local `$slot` names remain separate from project tag identities.

Filter either table by tag, code, owner or screen. **Locate diagnostic** and **Locate reference** navigate to exact screen/component identities, including locked or hidden components, without modifying the project. Results are snapshots: after editing, the pane explicitly reports that analysis is stale. Reanalysis is operator-driven rather than performed on every pointer move. Each result collection has a budget and reports truncation.

The analyzer is reusable without the UI:

```csharp
HmiEngineeringReport report = HmiProjectAnalyzer.Analyze(project);
foreach (HmiDiagnostic diagnostic in report.Diagnostics)
    Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
```

## Reusable alarm console

`ProGPU.WinUI.Hmi.HmiAlarmConsole` has no designer dependency. Attach a single-owner `HmiRuntime`, then use severity/search filters and the Attention, All, Active, Unacknowledged and UnknownQuality views. The designer's **Alarm console** tab attaches to the currently running simulation or explicit live-acquisition runtime.

The transition journal records activation, return, local acknowledgement, quality loss and quality restoration separately. Entries are immutable and sequence-numbered. Acknowledgements can include an actor and comment. Active conditions are not cleared by acknowledgement. Unknown quality is visible instead of being mistaken for a normal return.

The table realizes at most 500 matching rows per view. The journal retains 2048 entries and exposes the evicted count. CSV export quotes delimiters/newlines and neutralizes formula-leading operator text; designer exports appear in **Runtime / audit** for copying. This in-memory journal is not a durable historian, tamper-evident audit store or event-complete PLC feed.

```csharp
var console = new HmiAlarmConsole();
console.AttachRuntime(runtime);
console.AllowAcknowledgement = true; // Enables LOCAL HMI acknowledgement only.
console.Actor = authenticatedOperatorDisplayName;
console.AcknowledgementComment = "Reviewed locally";
// Detach/Dispose when the owner no longer uses the console.
```

The standalone console defaults to acknowledgement disabled. The designer allows local acknowledgement, not external controller acknowledgement. A display name is audit context, not authentication. Host authorization and controller safety logic remain independent.

## Timestamp-accurate trends

Trend X coordinates are derived from source timestamps, not sample indexes. Uneven polling intervals therefore remain uneven on screen. The inspector exposes **Trend window (s)** and **Maximum gap (s)**; defaults are 60 and 5 seconds. A zero maximum gap disables time-gap splitting, never quality-gap splitting.

`HmiTrendReducer` reduces chronological input into caller-owned min/max buckets without allocating its own storage. The control retains a 512-bucket scratch buffer and emits bounded vector geometry. Spikes remain represented by bucket extrema. Invalid, stale or missing-time intervals break connections. A bucket mixing good and invalid telemetry is conservatively omitted rather than interpolated across. Runtime stale transitions are retained as quality markers without advancing the original sample timestamp.

Time labels show the current view window. The source-age freshness policy is unchanged: target-server timestamp behavior and stale limits must be checked during commissioning. A display trend does not promise every process transition, interpolate unobserved equipment states or replace a historian.

## Designer and component polish

The canvas reconciles components by stable identity and symbol type, preserving retained controls across property edits, undo and redo. Unchanged controls do not recreate their internal labels/editors; initial tag lookup uses a dictionary. The canvas, logical outline, inspector, selection and history remain shared with the existing designer. The host avoids a second full refresh after an edit already published through `Session.Changed`.

Shared selection commands expose a scoped execution flag. HMI group-drag reconciliation does not reapply movement to an already aligned member during Align/Distribute, fixing double translation of edge-aligned components.

Static equipment controls retain only their three display labels. Command buttons and numeric editors are created only when the symbol/action requires them. Fan and compressor status uses RUNNING/STOPPED/UNKNOWN rather than displaying a Boolean as a numeric measurement. Unknown quality also halts running animation. Dependency-property font updates reach internal labels.

Numeric input preserves pending operator text when asynchronous feedback changes, including after focus moves to Apply. Feedback values are never optimistically overwritten. `HasPendingInput` and `ResetPendingInput()` expose the editing state to embedding hosts. Value and mapping validation plus the reviewed external-command coordinator remain authoritative.

## Validation commands

```sh
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet test tests/ProGPU.Hmi.OpcUa.Tests/ProGPU.Hmi.OpcUa.Tests.csproj -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke/ProGPU.Hmi.SerializationSmoke.csproj -c Release
dotnet publish tests/ProGPU.Hmi.SerializationSmoke/ProGPU.Hmi.SerializationSmoke.csproj -c Release -r osx-arm64 -p:PublishAot=true
```

The final command publishes the core regression probe, not the complete designer. Native window layout, physical GPU pixel rendering, pointer behavior and physical controller compatibility require separate qualification. See [HMI designer](hmi-designer.md), [control integrations](hmi-control-integrations.md) and [OPC UA commissioning](hmi-opcua.md).
