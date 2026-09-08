# Portable popup placement bounds

## Core application dependency

LibreWPF MVP and Toolkit/AvalonDock menus, ComboBoxes and tooltips must be able
to extend beyond their owner window when hosted by an independent native popup.
Source inspection found that WPF constrained every portable popup to owner-client
bounds, including popups that already had a native surface. This change connects
placement to actual surface ownership; it is not runtime qualification.

## Shared contract and ownership

`IPortablePopupServiceRegistrar.TryGetPopupPlacementBounds` queries an already
owned source, including before its first Show. The router consults only the
registrar that created that source. Missing ownership or capability returns false;
another registered window must not guess a placement rectangle. The default
interface implementation preserves existing implementers but reports unavailable.
Source-built WPF reports unavailable placement explicitly rather than inventing
owner bounds for a native window.

`PortablePopupPlacementBounds.Kind` distinguishes:

- `OwnerSurface`: use the source-built WPF owner's actual laid-out client bounds.
- `NativeScreen`: use the selected screen and work-area rectangles. Menus/tooltips
  prefer the work area when their anchor lies inside it, retaining WPF's existing
  policy; other cases use the screen.

Screen and work-area rectangles must be finite, positive and nonoverflowing, with
the work area contained in its screen. `PortablePopupMonitorSelection` streams
monitor entries, selecting greatest positive target overlap or, when no screen
overlaps, shortest rectangle distance. Exact ties prefer primary, then inventory
order. Empty or wholly invalid inventories fail closed. Host inventory exceptions
remain explicit; they do not turn into a successful owner-surface result.

## Coordinates, implementation ownership and cost

Inputs and results use one consistent host desktop placement coordinate space,
not framebuffer pixels. Native placement consumes raw platform screen rectangles;
content scale must not independently divide each monitor's desktop origin.
GLFW documents monitor positions and work areas in screen coordinates with content
scale separate: [monitor guide](https://www.glfw.org/docs/latest/monitor_guide.html).
The algorithm is independently implemented here; no third-party code is copied.
WPF's existing work-area preference stays in WPF, while reusable selection and
validation are ProGPU-owned typed interop code.

This host-side query serves both managed portable and native MIL renderers. It
does not alter their scene, text, geometry, shader or C++ render algorithms, so
there is no paired native renderer algorithm to implement. Selection is O(M)
time with O(1) allocation-free scratch; the platform service may allocate its
O(M) monitor inventory. The monitor-at-a-time winner reduction is control-plane
work, not a compute-heavy CPU fallback or pixel loop; no GPU submission, upload,
readback, new scalar rendering fallback or SIMD speed claim is introduced.

The query preserves the existing platform coordinate transport. It does **not**
establish that all source-WPF logical/global coordinates agree across mixed-DPI
monitors. End-to-end coordinate projection, DPI transitions, native popup input
and edge nudging still require application qualification. In particular, do not
admit Windows package mode or remove its guard based on these unit fixtures.

## Authored coverage and qualification boundary

ProGPU regressions cover overlapping/offscreen/zero-sized targets, negative
origins, deterministic ties, invalid rectangles/inventories and owner-only routing
including refusal without fall-through. LibreWPF regressions cover host kind
before Show, raw monitor coordinates, empty inventory, destroyed/unknown sources,
WPF work-area selection, explicit failures and owner-surface bounds.

These regressions are authored for the final qualification phase. Compilation
results are recorded in the task/PR checkpoint; compilation is not test execution.
Final application checks must open menus/ComboBoxes/tooltips at owner and monitor
edges, exercise work-area reservations, close/reopen, move between DPI scales and
compare both renderers against native Windows. Existing SDK, third-party, sample
comparison, lifetime, performance and CI gates remain mandatory and unchanged.
