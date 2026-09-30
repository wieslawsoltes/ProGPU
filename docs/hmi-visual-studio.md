# HMI process studio: appearance, authoring and validation

The HMI workbench uses the same ProGPU WinUI canvas, geometry manipulation, logical outline, history and selection services as the existing designer. This visual extension does not introduce a browser/WebView, bitmap-based equipment, another graphics engine, or an independent GPU device per control.

## Run and explore

```sh
dotnet run --project samples/HmiDesigner/HmiDesigner.csproj -c Release
```

The standalone app and **Visual Designer → HMI** gallery page open the original Northwater treatment-train example. It has real linked tags, runtime navigation, alarms, a time-based trend and a local pump command. **File → Water treatment sample** retains the earlier example. Neither opening a sample nor switching palettes connects to hardware.

The fixed-height header separates project identity and operating mode from commands. **File**, **Edit**, **Arrange** and **View** retain the previous authoring operations without allowing the toolbar to grow vertically. The quick toolbar has original vector icons, tooltips and enabled-state feedback. At narrow widths it scrolls horizontally. The searchable component library displays the actual equipment glyphs used by runtime controls, not an emoji or stock thumbnail.

**View** selects Light, Dark or High-contrast appearance; toggles grid, snapping and adaptive rulers; fits the screen or selected objects; chooses compact/expanded data panels; and switches runtime fit/1:1. Inspector and library panes retain the shared responsive collapse/overlay behaviour. Closing data panels does not lose their state. **Arrange** adds primary-selection width/height/size matching while preserving locks and one undo transaction.

## Component vocabulary

There are 40 insertable component types. The 12 additions are control valve, check valve, butterfly valve, agitator, silo, hopper, separator, reactor, flow meter, strainer, pressure transmitter and level transmitter. Every new type has a standalone `HmiControl` subclass and an atomic shared-designer registration.

Equipment has a neutral mechanical silhouette, readable ports and a separate operating-state signal. Instruments retain framed readouts and engineering ranges. Pumps and motors no longer share the same generic circular picture; valves have distinct bodies/actuators; vessels, separators, filters and heat exchangers have purpose-specific geometry. The catalog, toolbox and runtime consume one bounded retained-vector implementation.

Unknown quality is not equivalent to stopped equipment. Bad/stale data produces an explicit text status, a cross and a warning underline; it does not render a last-known liquid level as a fresh measurement. Fault and maintenance tones remain separate from ordinary operating state. The high-contrast palette is an application palette, not a claim of accessibility certification or complete operating-system accessibility integration.

## Persisted appearance versus view state

`HmiElement.Appearance` owns a detached `HmiAppearance` value object. Copy, undo/redo, faceplate synchronization and source-generated JSON round-trip retain:

| Property | Meaning |
| --- | --- |
| `Presentation` | Automatic equipment/instrument presentation, unframed process or card. |
| `ShowTagName`, `ShowEngineeringRange`, `ShowValue` | Independent caption, range and readout visibility. Quality warnings are not suppressed by hiding an ordinary readout. |
| `ShowConnectionPorts` | Hide or show glyph nozzle decoration. Same-screen semantic topology is stored separately in `HmiScreen.Links`; see [diagram connections](hmi-diagram-connections.md). |
| `QuarterTurns` | 0–3 clockwise quarter-turns of the equipment geometry. Text stays upright. |
| `MirrorHorizontal`, `MirrorVertical` | Mirror local geometry before rotating it. |
| `AnimateFlow` | Explicit opt-in to decorative flow/rotation animation. Default is false. |

The **HMI** inspector exposes these properties for selected components. Existing version-1 documents without `appearance` receive independent default instances. Invalid appearance, nonfinite layout and invalid orientation are rejected before a transaction enters history. Orientation currently applies to glyphs, not chart data axes or label text.

`HmiDesignerHost.ColorScheme` and `HmiScreenView.ColorScheme` are view state, not document edits. They are instance-scoped; changing one designer does not mutate `ThemeManager.CurrentTheme`, recreate the model, dirty the project, or rebuild its equipment controls. WinUI properties retain `ThemeResourceBrush` provenance through `HmiThemeResources.GetReference`; retained geometry uses cached palette drawing materials. Shared tables now resolve brushes through their owning element's theme/resources, and TextBox honours scoped text/placeholder colours.

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;

var designer = new HmiDesignerHost(HmiShowcaseProject.Create())
{
    ColorScheme = HmiColorScheme.Dark
};
designer.SetDataPanelHeight(190);

var valve = new HmiControlValve();
valve.ApplyDefinition(new HmiElement
{
    Symbol = HmiSymbol.ControlValve,
    Label = "CV-201",
    Tag = "Feed.ValvePosition",
    Unit = "%",
    Width = 240,
    Height = 200,
    Appearance = new HmiAppearance { QuarterTurns = 1, ShowTagName = true }
});
valve.ColorScheme = HmiColorScheme.Dark;
```

## Shared-canvas correctness

`DesignerCanvas.DocumentSize` separates the logical artboard extent from the viewport. It is optional: existing responsive visual-design consumers retain the old behaviour when null. Selecting an element and changing zoom no longer shrinks a fixed HMI document to the viewport. Its `DocumentBackground` draws beneath the shared grid and snapping guides, never as a selectable child.

The R-tree's return order is not a z-order contract. Shared canvas picking now keeps existing logical-depth priority and breaks ties by the actual sibling traversal/painter order. A pump drawn after a crossing pipe is therefore selected before the pipe. The same rule applies to spacing-guide hover. This does not introduce a separate `Canvas.ZIndex` implementation or redefine the substrate's rendering order.

`HmiRuntimeViewport` fits the existing live screen with the native Viewbox transform, or scrolls it at 1:1. Mode changes retain screen identity, navigation, input state and transport subscriptions. The rendered test routes actual pointer events through the fitted transform and verifies that operating the switch updates runtime feedback without editing the design document.

## Reflection-disabled JSON crash

The project serializer uses `HmiJsonContext` metadata and the `JsonTypeInfo<HmiProject>` overloads for serialization/deserialization. The new appearance model is in that generated graph and uses a typed string-enum converter. It does not turn reflection back on globally or install a reflection resolver as a workaround.

The focused test project, gallery smoke executable and serialization probe run with `JsonSerializerIsReflectionEnabledByDefault=false`. The separate serialization workflow publishes and executes NativeAOT probes on Linux and macOS. NativeAOT qualification of that UI-independent serializer must not be confused with NativeAOT qualification of the entire desktop UI or third-party transport stack.

## Rendering architecture and primary-source review

This is a retained-control/presentation change, not a replacement glyph rasterizer, shaping engine or compositor. The following primary references informed the boundary choices:

| Reference | Applied decision |
| --- | --- |
| [SkParagraph API](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h) | Keep layout/caption measurement separate from scene drawing; do not parse or shape replacement text in a shader. |
| [DirectWrite formatting/layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout) and [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm) | Retain text visual identity and bound captions independently of equipment geometry, including rotated symbols. |
| [WebRender](https://github.com/servo/webrender) | Retain scene descriptions and update affected controls rather than rasterizing whole screens into uploaded bitmaps. |
| [Vello](https://github.com/linebender/vello) and [Parley](https://github.com/linebender/parley) | Keep GPU scene drawing and CPU text layout as distinct concerns. Reuse ProGPU instead of adding an interop engine. |
| [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html) | Preserve the existing text stack; no assumptions that one character equals one glyph or that reversing strings implements bidirectional layout. |
| [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) | Use generated graph metadata, including deserialization, rather than enabling reflection in a trimmed host. |

Device-loss recovery, glyph/texture cache keys, shaping/fallback and GPU batching remain owned by the existing substrate; this change adds no alternate cache, font enumerator, shader source, worker pool or device. Geometry emission is `O(G)` in the fixed number of primitives for the chosen glyph, with bounded arc subdivisions (at most 72) and stack-based polyline inputs. Trend reduction retains its source-time/min-max/gap semantics and bounded output buckets. Static equipment has no per-control timer; decorative animation is opt-in. The only framebuffer readbacks added here are in the test executable, never in the production controls.

These are implementation/complexity properties, not measured physical-GPU speed claims. Local software-Vulkan probe wall time includes CPU shader execution, PNG encoding and readback; it is not application frame time or a PLC scheduling guarantee. No real-device frame-rate improvement is asserted.

## Executable visual validation

```sh
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet run --project tests/ProGPU.Hmi.SerializationSmoke/ProGPU.Hmi.SerializationSmoke.csproj -c Release
PROGPU_WGPU_BACKEND=vulkan dotnet run \
  --project tests/ProGPU.Hmi.VisualSmoke/ProGPU.Hmi.VisualSmoke.csproj -c Release -- \
  /path/to/an-installed-font.ttf artifacts/hmi-visual Dark
```

The visual probe uses the real ProGPU retained compositor, native WebGPU and an offscreen framebuffer. It renders 40 full controls, a caption-free glyph atlas, equipment quality states/orientation, the designer, fitted runtime and a selected-equipment view. It checks that every glyph has actual ink, all 22 equipment silhouettes are distinct, ink stays within its owned thumbnail region, and operating/unknown/fault states change glyph pixels. It also exercises real runtime pointer activation, overlapping designer selection and zoom-to-selection. PNG hashes are diagnostic, not portable pixel-golden assertions across unrelated fonts/drivers.

Run Light, Dark and HighContrast as separate invocations to isolate native scene/device lifetime. The `HMI visual and pointer regression` workflow uses Mesa's software Vulkan adapter on Linux and uploads PNGs/logs; the ordinary HMI matrix checks Windows/Linux/macOS builds, protocol tests and gallery construction. All stages fail on errors; a skipped or queued workflow is not a passing result.

Physical Apple Metal/Retina, Windows DirectX, touch hardware, real PLC interoperability, complete accessibility conformance and production commissioning still require their own qualification. Same-screen semantic nozzle routing is documented in [diagram connections](hmi-diagram-connections.md); it is not an engineering pipe solver. Flat faceplates are not recursive vendor templates, and appearance does not weaken external command review/authorization.

## Pinned routing

[Ordered route editing](hmi-route-editing.md) extends semantic diagram links with exact persistent pins, numbered canvas handles, drag previews, atomic undo and explicit blocked-pin recovery. It uses the same retained adorner layer, palettes and native input ownership as the existing designer.
