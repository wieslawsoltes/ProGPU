# Direct canvas authoring

The HMI studio provides two direct authoring tools on the existing shared designer canvas: draw-to-size component placement and directional area selection. These tools complement native caption editing, nozzle connections and route-pin editing. They do not add a second canvas, renderer, GPU device, protocol connection or document format.

## Draw a component at its actual size

In **Components**, the pencil/draw button beside a symbol arms a one-shot placement tool. The adjacent **+** keeps the existing immediate insertion workflow. Dragging the symbol tile itself continues to use the shared toolbox drag/drop path. Both small buttons remain inside the palette's scroll gutter; names wrap in bounded text regions, and tooltips expose their full text.

Press the canvas and drag to define the new component's rectangle. All four drag directions are supported. The preview is the actual `HmiControl` instance rendered through the existing adorner transform, not a screenshot or a generic box. A screen-sized dimensions label shows the proposed size and location. Click without dragging to use the exact catalog size and default presentation, including schematic defaults for instrumentation/electrical primitives.

**Shift** preserves the catalog aspect ratio. **Alt** bypasses the current grid; the initial anchor is established when the gesture begins. The click threshold uses the original unsnapped pointer location, so changing Alt before releasing a stationary pointer cannot collapse a catalog-sized component into an 8×8 object. Release commits the exact preview rectangle in one undo transaction, assigns a fresh identity, and selects the new component. The tool then returns to selection. The quick toolbar's draw command reuses the selected component's **type**, or the last used type, not its bindings or commands.

**Escape**, capture loss, a canceled pointer, a secondary-button chord, a viewport change, a foreign document edit, switching tools, entering runtime preview, unloading or disposing the host retires the draft. No intermediate document/history entry exists. A different pointer cannot move or complete the owning pointer's gesture. Release-time focus/capture callbacks cannot let a retired gesture overwrite a replacement operation.

Placement uses the project contract: X/Y within ±32,768 and dimensions from 8 to 16,384 document units. Invalid preview coordinates do not alter the last valid model; native invalid gestures are canceled with a diagnostic. Controls may be drawn beyond the artboard, just as existing geometry editing permits. The new component has catalog defaults and no process binding or command target. The author must explicitly configure its tags and behavior afterward.

## Window and crossing selection

Choose the selection arrow or press **V**, then drag on empty canvas. Drag **left to right** for a solid window: a component's complete layout bounds must fit. Drag **right to left** for a dashed crossing region: intersecting layout bounds qualify. Candidate components receive a separate outline, and the label shows the operation and candidate count.

The operation is captured at pointer press:

| Modifier | Operation on release |
| --- | --- |
| None | Replace the current selection. |
| Shift | Add matching components. |
| Ctrl | Toggle each matching component once. |
| Ctrl+Shift | Remove matching components. |

The actual selection does not change while dragging; cancellation restores it because no provisional selection was published. Commit emits one selection-set notification, not one notification per item. Candidate order follows the actual painter order rather than R-tree packing order. Visible members of a flat authoring group expand together. Hidden components are excluded; locked components can be selected and inspected, but stay protected against edits.

Selection uses component layout rectangles, not only painted glyph pixels. It does not select connections or route pins as though they were equipment. Existing equipment, line picking, waypoint recovery handles, resize thumbs, rulers and connection gestures retain input priority. A waypoint inside equipment or outside a failed route remains recoverable instead of starting a marquee. Area selection changes neither project dirty state nor undo history.

## Reusable APIs

```csharp
using ProGPU.Hmi;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi.Designer;

// These model-coordinate APIs are also used by real pointer-captured gestures.
designer.BeginComponentPlacement(HmiSymbol.HeatExchanger);
designer.BeginPlacementDrag(new HmiPoint(400, 240));
designer.PreviewPlacement(new HmiPoint(780, 500), preserveAspect: true);
designer.CommitPlacement(); // Exactly one project transaction.

// Selection is view state, never a project transaction.
designer.BeginAreaSelection(new HmiPoint(80, 120), DesignerSelectionOperation.Add);
designer.PreviewAreaSelection(new HmiPoint(800, 600));
designer.CommitAreaSelection();

// Retire either a pending tool or a live draft.
designer.CancelCanvasAuthoring();
```

The HMI-independent `DesignerDragRectangle` validates finite geometry and constructs anchored rectangles in any quadrant with optional aspect constraints. `DesignerRegionQuery` snapshots top-level visible non-decoration bounds into the existing ProGPU R-tree. `DesignerRegionAdorner` paints the shared bounds/highlight/HUD presentation. `DesignerSelectionService.SelectRange` supports replace/add/remove/toggle with validated enumeration and a single notification. No HMI dependency was added to the shared designer.

The region index is valid for its captured document/layout generation. Recreate it after geometry changes; do not treat borrowed control references as immutable controls. The default query budget is 5,000 components. Adorner highlights are limited to 5,000 and its caption to 256 UTF-16 code units; validation occurs before publication. The workbench is single-owner UI state, following the surrounding WinUI API contract.

## Retention, bounds and execution policy

A placement gesture allocates one component preview and one detached default definition. Pointer moves update only that preview's geometry and the existing adorner. They do not serialize JSON, create per-move undo records, rebuild equipment, evaluate protocol mappings or reroute existing links. Repeated unchanged rectangles are ignored. An actual release runs the existing validated project transaction and reconciliation path.

A selection gesture snapshots the existing spatial index and group metadata once. Queries traverse the shared R-tree, filter containment when needed, and restore painter order. Worst-case query/selection work is bounded by the admitted scene size; group expansion and final ordering can inspect `O(N)` components, and candidate sorting is `O(K log K)`. Highlight storage is `O(K)`. There is no claim that a very large crossing query is constant-time. This is pointer-event authoring/index orchestration on the existing designer, not a new data-parallel rasterization workload or a telemetry/frame loop. No framebuffer readback, worker queue or per-object submission is added to production controls.

The GPU receives ordinary retained ProGPU control/line/text drawing. Existing scene ownership, shaping, font fallback, batching, caches, device recovery and alpha/DPI rules remain unchanged. Theme references resolve from the owning studio, including active-tool outlines and the preview. No fixed production shader, external renderer, font package or replacement text shaper is introduced. Local software-Vulkan probe duration includes test setup and image readback; it is not physical-GPU frame time.

## Verification and limits

The focused tests cover all 48 catalog types, quadrant/aspect geometry, click and Alt behavior, retained preview identity, exact one-step undo, foreign edits, viewport/runtime/disposal cancellation, multiple pointer identities, reentrant capture release, group/hidden/lock policy, selection ordering and all four operations, query budgets and atomic bounded adorner snapshots.

The native framebuffer probe exercises actual pointer draw/release, rendered preview, window/crossing selection, modifier keys, Escape, typed capture loss, secondary-button cancellation, and both palette buttons. It runs alongside the existing glyph, quality, formatting, caption, design/runtime pixel, semantic-link and waypoint recovery probes in all three palettes. Narrow palette text is bounded rather than painting underneath adjacent controls. Actual hardware touch/pen, accessibility, Retina/Metal and vendor interoperability remain separate qualification.

```sh
dotnet test tests/ProGPU.Hmi.Tests -c Release
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.VisualSmoke -c Release -- \
  /path/to/installed-font.ttf artifacts/hmi-visual Dark
```

The optional `--authoring-only` argument isolates the new input probe during diagnosis. Default invocation still runs the complete suite; no existing assertion or palette was removed. Native artifacts record the exact source revision and renderer environment.

This extension does not add standards certification or new industrial command authority. The existing 48 original symbols and graphic conventions remain unchanged. Native Modbus/MQTT/OPC UA acquisition, reviewed writes, authenticated host authorization, quality checks and controller-side interlocks are untouched. Nested faceplate editing, whole-segment routing, junctions/crossover bridges and durable industrial historian workflows remain distinct features.

## Primary references and source provenance

The interaction contracts were reviewed against [Ignition 8.3 drawing tools](https://docs.inductiveautomation.com/docs/8.3/ignition-modules/vision/working-with-vision-components/drawing-tools) and [WinCC Unified V21 object positioning](https://docs.tia.siemens.cloud/r/en-us/v21/configuring-screens-rt-unified/configuring-screen-objects-rt-unified/moving-objects-rt-unified/positioning-an-object-rt-unified). Adopted concepts are explicit draw tools, direct bounds authoring and temporary snap bypass; no vendor source code, artwork, project format or pixel-identical shell is copied. Directional window/crossing behavior is the explicit ProGPU contract above.

Implementation reuses original `DesignerCanvas`, `DesignerSelectionService`, `HmiControlCatalog`, `HmiControl` and pointer-capture infrastructure from ProGPU base `46c599f0508ffb809517051cedee53b3bc94e76b`. No new rendering/text architecture was selected. The existing cross-engine boundary review in [graphic conventions](hmi-graphic-conventions.md#shared-correctness-and-rendering-boundaries) remains applicable: text/layout and retained GPU drawing stay separate; upstream engines are references, not copied implementations.
