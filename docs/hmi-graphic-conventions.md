# Graphic conventions and direct HMI authoring

The library contains **48 original retained-vector component types**. Process, HighPerformance and Schematic are explicit application presentation conventions, not certifications or complete reproductions of a standards organization's symbol database. The same controls, typography, geometry and source-generated document graph serve the runtime, component library and designer.

## Standards scope

Public publisher scope/catalog information was reviewed on 30 September 2026. Normative symbol databases, proprietary artwork and licensed standards tables were not imported into the repository. Matching an equipment name or choosing a palette is not a conformity assessment.

| Reference family | Relevant scope | Implemented support and limits |
| --- | --- | --- |
| [ISA-101](https://www.isa.org/standards-and-publications/isa-standards/isa-standards-committees/isa101) | HMI navigation, graphic/color conventions, dynamics, alarms and related interface behavior | Neutral normal-state presentation, explicit abnormal/unknown state, separate engineering context, direct authoring and navigation. This is not a complete lifecycle/philosophy or clause-by-clause conformance implementation. |
| [ANSI/ISA-5.1-2024](https://www.isa.org/standards-and-publications/isa-standards/isa-standards-committees/isa5-1) | Instrument/control identification and symbols | Original instrument bubble and shared-control-function vocabulary, explicit function/loop annotations and field/front-panel/rear-panel location markers. No exhaustive code table, certified tag parser or exact normative symbol-number mapping is supplied. |
| [ISO 10628-2:2012](https://www.iso.org/standard/51841.html), confirmed 2024 | Chemical/petrochemical diagram symbols; excludes electrotechnical diagram symbols | Existing process-equipment vocabulary, distinct schematic presentation and semantic nozzle topology. No claim that the complete symbol inventory, symbol proportions, line conventions or engineering specification requirements have been implemented. |
| [ISO 14617-1:2025](https://www.iso.org/standard/85641.html) and [ISO 14617-2:2025](https://www.iso.org/standard/83364.html) | General diagram-symbol rules and symbols | Scope reference for separating diagram vocabulary from equipment operation. No imported database or complete conformity claim. Fluid-power specifics are not inferred from generic process symbols. |
| [IEC 60617 database](https://webstore.iec.ch/en/publication/2723) | Electrotechnical diagram vocabulary, including conductors, devices and protection | Original normally-open/closed contact, coil, breaker, transformer and protective-earth primitives with stable terminals. These are named reference states, not a circuit solver, verified live device position or a reproduction of the full IEC database. |

[ISA's public operator-experience discussion](https://www.isa.org/intech-home/2023/april-2023/features/how-dcs-migration-improves-operator-experience) informed the separation between neutral normal graphics and abnormal emphasis. The palette alone is not an ISA-101 implementation. IEC/ISO/ISA symbol families must not be conflated into one universal certified graphics mode. ANSI/NEMA-specific alternatives, ISO 1219 fluid power, complete signal-line conventions, safety-sign databases and vendor symbol packs remain separate coverage work.

## Persisted graphic profiles

`HmiAppearance.GraphicStyle` selects:

- **Process:** the existing mechanical detail, process readouts and separate operating-state color.
- **HighPerformance:** neutral normal equipment, level/readout marks and running signals. Fault, warning, maintenance and unknown-quality semantics remain distinct. Normal/abnormal state does not depend solely on color.
- **Schematic:** reduced material fills and neutral diagram geometry, while retaining explicit quality and fault indication. It does not convert the project into a process/electrical solver.

Profiles are per component and remain independent of Light/Dark/HighContrast palettes. Applying a profile does not alter tag values, topology, protocol mapping, permissives or alarm configuration. All geometry/text uses the existing ProGPU retained path and text stack. Normal state changes never allocate a new rendering device or timer.

`CaptionFontSize` is 0 for the original automatic sizing or 8–72 document units. `CaptionAlignment` is Start, Center or End. Current alignment is physical left/center/right, not a new bidi-aware paragraph-direction policy. Long captions retain the existing single-line ellipsis and clipping behavior. Width-changing labels do not reshape unrelated equipment or change its geometry. Explicit font-size changes do change the reserved caption/glyph regions and therefore reroute any affected nozzles.

Instrument annotation uses `InstrumentCode`, `InstrumentLoop` and `InstrumentLocation`. Identifiers use bounded ASCII letters/digits and `-`, `.` or `/`; they are annotations, not reflection names or scripts. Location choices draw no divider (Field), a solid divider (PanelFront) or a dashed divider (PanelRear). The divider and annotation remain upright under symbol orientation/mirroring. Signal terminals continue to follow the oriented geometry. No function-code meaning, controller access or transport mapping is inferred from the text.

`NormalMinimum`/`NormalMaximum` form an optional finite, increasing pair within the component's engineering range. They render a neutral reference band and current-value mark in an available range row. The range must be enabled and have enough space. Bad/unknown quality or a state caption suppresses the band instead of overpainting the warning. These are **display references, not alarm setpoints**; changing them never creates or clears an alarm. The inspector edits both values atomically with `low;high`; an empty value clears both.

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi;

var gauge = new HmiGauge();
var definition = HmiControlCatalog.CreateDefinition(HmiSymbol.Gauge);
definition.Label = "PT-201 / Feed pressure";
definition.Tag = "Feed.Pressure";
definition.Unit = "bar";
definition.Maximum = 10;
definition.Appearance.GraphicStyle = HmiGraphicStyle.HighPerformance;
definition.Appearance.NormalMinimum = 3;
definition.Appearance.NormalMaximum = 7;
definition.Appearance.CaptionFontSize = 14;
gauge.ApplyDefinition(definition);
gauge.ColorScheme = HmiColorScheme.Dark;
```

`HmiControlCatalog.CreateDefinition` provides fresh model defaults without allocating temporary UI controls. The catalog factory and shared-designer registration reuse it. Eight new standalone classes cover `HmiInstrumentBubble`, `HmiControlFunction`, `HmiNormallyOpenContact`, `HmiNormallyClosedContact`, `HmiRelayCoil`, `HmiCircuitBreaker`, `HmiTransformer` and `HmiProtectiveEarth`. Their default appearance is schematic with ordinary value/range readouts disabled; quality reporting is not disabled. Stable terminals are available to the existing semantic link layer, but line kind and feedback still require explicit authoring.

## In-place editing and contextual inspector

Select a component and press **F2**, or double-click its actual caption. The shared `DesignerInlineTextEditor` overlays the control's own caption bounds under the existing adorner transform. Native text input, selection, clipboard, caret and IME processing remain owned by ProGPU's TextBox. There is no substitute web input or per-keystroke JSON editor.

Typing updates one retained caption; it does not serialize the project, move nozzles, recompute routes or create history. **Enter** commits one transaction, **Tab/Shift+Tab** applies and edits the next/previous visible unlocked caption in document order, and **Escape** cancels. Focus loss also cancels instead of silently accepting an edit while a toolbar/navigation operation changes context. Enter during IME composition does not commit the document; complete the composition first.

The text budget is 4096 UTF-16 code units, matching the document contract. An oversized native paste cancels the edit rather than modifying TextBox text reentrantly inside its insertion operation and corrupting its caret. Foreign document edits, preview, canvas manipulation and disposal retire drafts. Focus callbacks are reentrant: a replacement edit cannot be overwritten by the old commit. A commit/cancel initiated inside the editor restores focus to the authoring canvas so subsequent keyboard undo remains functional.

The **Format** menu and contextual HMI inspector provide profile buttons, in-place text editing, rotation, mirroring and copy/paste style. Formatting preserves retained controls and uses one validated transaction across the selected unlocked components. Format copy deliberately excludes label contents, tag bindings, commands, instrument identities/location and normal engineering bands. Paste cannot silently transfer a setpoint/reference band from another instrument.

The full advanced property grid remains available below the contextual controls. View → **Preview initial state (read only)** uses the existing runtime view without automatic simulation ticks or local writes. Conditional visibility is intentionally still ignored in the editable design canvas so hidden-by-condition equipment remains discoverable; the read-only initial preview shows runtime visibility. This distinction is not advertised as universal pixel equivalence for every dynamic screen.

The direct-entry interaction was reviewed against [WinCC Unified V21's in-object text entry](https://docs.tia.siemens.cloud/r/en-us/v21/configuring-screens-rt-unified/basics-rt-unified/using-the-unifed-screen-editor-next-gen.-rt-unified/managing-objects-rt-unified/enter-text-or-value-directly-into-the-object-rt-unified). Workspace/library/property separation was reviewed against [Ignition's designer interface](https://www.docs.inductiveautomation.com/docs/8.3/platform/designer/designer-user-interface). This is an original ProGPU UI, not copied vendor artwork, a vendor project-format implementation or a pixel-identical vendor clone.

## Shared correctness and rendering boundaries

Design reconciliation evaluates initial state rules with the same `HmiStateEvaluator` used by runtime. A fault visible in the initial runtime no longer appears healthy in design mode. The shared canvas's `ShowGridLines` setter invalidates retained drawing, fixing a stale-grid problem exposed by pixel comparisons. Unchanged controls, routes and object identities are preserved by the original reconciliation/cache contracts.

Palette materials remain cached; profile-specific `ThemeResourceBrush` references retain owning resource provenance. Instrument text visuals are created only for instrument symbols and reused. The renderer emits bounded original primitives; new glyphs have a fixed primitive count. Label typing is one native text-control update plus its owned caption, not a project-wide renderer rebuild. No physical-GPU frame-rate improvement is asserted.

The architecture review retained separation of text layout and GPU scene drawing: [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h), [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout), [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm), [WebRender](https://github.com/servo/webrender), [Vello](https://github.com/linebender/vello), [Parley](https://github.com/linebender/parley) and [HarfBuzz concepts](https://harfbuzz.github.io/shaping-concepts.html). No external renderer code, fonts, shader rasterizer, new glyph cache or text shaper was introduced. Existing font fallback/bidi/device ownership remains authoritative.

## Validation

The focused tests explicitly disable reflection-based JSON. They cover new profiles/identifiers/bands, legacy defaults, typed factories and terminal rotations; contextual format isolation, lock protection and retained identity; same initial state rules; Unicode/IME, one-step undo, Tab, focus loss, foreign edits, cancellation, over-budget paste and focus reentrancy.

The actual native framebuffer/input probe renders all 48 components, compares profile and fault pixels without relying on captions, exercises F2, native Unicode insertion, Enter, keyboard undo, actual caption double-click and Escape, and compares an identical fault-state component's design/runtime crop **byte for byte** with the same font, scale and adapter. Other diagrams, waypoint capture/cancellation and three-palette regression checks remain enabled. Pixel equivalence of this controlled fixture is not a cross-font/driver/OS golden-image guarantee or a complete accessibility audit.

```sh
dotnet test tests/ProGPU.Hmi.Tests -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke -c Release
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.VisualSmoke -c Release -- \
  /path/to/installed-font.ttf artifacts/hmi-visual Dark
```

The serialization probe includes the new graph under CoreCLR and NativeAOT; neither probe enables reflection globally. Tests use explicit software adapters and loopback transports. Physical Metal/Retina, touch/assistive technologies, vendor symbol qualification and deployed PLC/certificate/identity behavior remain separate checks. No production endpoint is connected by these examples or tests. Existing command review, authenticated authorization, connection-generation checks and uncertain write outcomes remain unchanged.
