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

## Client-to-desktop source contract prerequisite

`PortableDesktopTransform` separates client-DIP-to-desktop geometry from
framebuffer DPI. It maps `desktop = origin + client * scale`, and maps back with
`client = (desktop - origin) / scale`. Desktop origins stay unchanged, including
negative monitor origins. The immutable snapshot rejects nonfinite origins and
nonpositive/nonfinite scales; a default/zero snapshot is invalid rather than an
implicit platform assumption. Point arithmetic preserves ordinary IEEE results.

`IPortableDesktopGeometryHost` is an optional package-neutral source capability.
Source-built WPF implements it and its `PointToScreen`/`PointFromScreen` paths,
including portable HwndSource ownership, consume this shared transform. The
source starts with explicit identity desktop scale for compatibility. Legacy
origin-only changes preserve the desktop scale, and framebuffer-DPI changes do
not replace it. Invalid snapshots fail before changing source state.

The contract follows GLFW's public distinction between desktop screen units and
framebuffer pixels. Windows/X11 screen coordinates map to pixels, whereas
macOS/Wayland can resize the framebuffer independently. Client-to-desktop scale
must follow the host's actual client-size policy, not an OS-name heuristic or an
assumed framebuffer/window-size ratio. See the official
[window coordinate and content-scale guide](https://www.glfw.org/docs/latest/window_guide.html#window_scale).
This is original ProGPU contract/arithmetic code; no external implementation is
copied. Each point conversion uses two intrinsic double lanes, O(1) time/storage,
no allocations, no native boundary and no GPU work. Both renderers use the same
source/platform contract; no C++ renderer algorithm changes are applicable.

At this prerequisite checkpoint automatic host mapping was not enabled. The
source extent/limit and host connections below supersede that implementation
status; they do not establish complete popup parity. Keep Windows SDK admission
closed pending the remaining independent native-surface ownership work.

Authored fixtures compare intrinsic mapping against scalar forward/inverse
oracles, retain negative/zero origins and unequal/fractional scales, reject
invalid/default geometry, and check source round trips, portable HwndSource,
origin updates, independent framebuffer changes and disposal. They are not run
until final qualification; neither compilation nor these CPU fixtures establish
monitor-transition, native-window, image, input or performance parity.
The ProGPU.Tests Release compile checkpoint succeeds with 0 warnings/0 errors.
The initial paired source-WPF fixture build stopped on MSB3491 / no space left
on device. Space was subsequently restored. A fresh restore exposed a source-test
NRBF downgrade; keeping its upstream test dependency separate from the portable
product pin resolved it. PresentationCore.Tests now restores/builds with 9
warnings/0 errors. NU1701 remains visible and runtime qualification is still open.

## Popup extent and limit connection

Source inspection of the same MVP/Toolkit menu/ComboBox path found a second
mismatch: `Popup.UpdatePosition` used framebuffer DPI to size the root rectangle
for screen-edge nudging even when portable placement used logical desktop units.
For a Retina-style desktop scale of one and framebuffer scale of two, this
treated a 100-unit popup as 200 desktop units while deciding whether to nudge it.
This is a source-backed finding, not a reproduced graphical run.

ProGPU now provides `ClientVectorToDesktop` and `DesktopVectorToClient` on the
existing transform. These scale offsets and extents without translating origins;
inverse mapping divides directly rather than multiplying by a potentially
overflowed reciprocal. Work remains O(1), allocation-free, with intrinsic double
x/y lanes and no GPU/native crossing. These are original ProGPU arithmetic
extensions to its own transform, with matched scalar-oracle fixtures.

WPF `PopupSecurityHelper.ClientOffsetToScreen`, `ClientSizeToScreen` and
`ScreenSizeToClient` consume those operations through source-owned geometry.
Portable child-interest points, root-size edge nudging, absolute-placement
offsets and desired-size restrictions now share desktop units with placement
anchors and monitor/work-area bounds. Restrictions return client-DIP sizes.
Legacy HWND sources retain their device-transform path; portable child points
are scaled only after their visual-to-client transform, never twice.

The shared source helper follows portable HwndSource ownership and does not
infer desktop scale from framebuffer DPI. Both renderer modes use this same
source code. No C++ scene, shader or renderer algorithm is changed, and no new
fallback or reduced rendering behavior is introduced. Authored source helper
fixtures cover independent desktop/framebuffer scales, negative offsets,
origin independence and size round trips. Existing source-contract fixtures
require the actual child, nudge, offset and restriction consumers to use it.

At the extent/limit checkpoint, host publication and bridge-local overlay/input
conversion remained open; the connection below implements them. The
custom-placement callback path retains its existing contract. Full application
placement, capture, mixed-monitor transitions and both renderer comparisons
still require final qualification; these authored fixtures are not runtime proof.
Compile-only checkpoints: ProGPU.Tests 0 warnings/0 errors; source-built WPF
application harness 4/0; PresentationCore.Tests 9/0 and PresentationFramework.Tests
3/0 after normal dependency restore. Test-utility NU1701 warnings remain visible.
No runtime fixtures, GPU/VM applications, source verifiers or CI qualification ran.

## Host, overlay and pointer connection

The acceptance action is opening, moving and clicking a menu/ComboBox in the
existing MVP/Toolkit applications. Source inspection found that the popup bridge
still treated legacy `(popupDevice - ownerDevice) / framebufferDpi` as owner DIPs,
although it is a desktop vector. Native pointer normalization also used framebuffer
geometry for native window coordinates. These are source-backed findings, not
reproduced application failures.

`PortableDesktopTransform.FromWindowCoordinates` now expresses the actual host
client-size policy: scaled native client dimensions use content scale, otherwise
desktop vectors use identity scale. Raw origins remain unchanged. The factory is
allocation-free O(1) metadata selection; mappings use the existing intrinsic
double-lane operations. Provenance is ProGPU's original transform and public GLFW
window-coordinate contract linked above, not external implementation source.

LibreWPF uses this shared policy for source geometry publication and native pointer
normalization. The popup bridge preserves the public legacy device transport:
decode framebuffer transport scale first, then use the owner's desktop inverse
for local offsets. Overlay replay and input use those same local DIPs. Moving or
rescaling an owner maps offsets forward before re-encoding the legacy transport.
Publication remains parent before child, and combined desktop/framebuffer changes
publish the new coordinate frame before moving native surfaces. Source capability
failure releases the newly created source and propagates explicitly.

Owner-surface popups inherit desktop scale. An independently surfaced popup keeps
its own source desktop scale after initialization; native diagnostic point/bounds
queries use that source transform rather than assuming owner scale. Native pointer
events use client-local desktop vectors, with no desktop-origin subtraction and
no framebuffer-ratio inference; existing Cocoa owner-relative input remains on its
explicit path. Compatibility/diagnostic input keeps its separate legacy adapter.

Both managed and C++ renderer modes share this host/source integration. No scene,
shader, C wire layout or C++ renderer algorithm changes apply. Fixtures cover the
explicit policy, negative origins, fractional/unequal desktop scales, framebuffer
changes, popup movement and local pointer routing. They are authored, not executed.

At this checkpoint the remaining core dependency was native-popup framebuffer
ownership; the following connection removes those owner-driven writes. Neither
checkpoint claims mixed-monitor runtime, graphics or input parity.

Compile-only checkpoint: ProGPU.Tests Release 0 warnings/0 errors; LibreWPF bridge
fixtures 21/0; source-built application harness 4/0. No tests, source verifiers,
GPU/VM workloads, benchmarks or CI qualification executed. ProGPU's branch
contains the latest fetched `origin/main` (zero upstream commits missing).

## Independent native-popup framebuffer ownership

The same MVP/Toolkit menu action exposed two source-backed overwrite paths:
`WpfPortablePopupBridge.TrySetOwnerGeometry` wrote the popup source DPI directly,
then the native adapter also forwarded owner DPI to its own host/source. This
could replace independently resolved popup framebuffer geometry with owner scale.

The adapter now accepts `SetOwnerTransportScale`, a position-decoding update only.
Its fields explicitly name owner transport scale; `SetPosition` continues decoding
legacy device coordinates before passing raw desktop positions to the native host.
The popup bridge calls that setter for native popups and updates source DPI only
for owner-surface popups. Native popup creation seeds source DPI once; the popup's
own native host callbacks own subsequent framebuffer geometry. Parent-first
scale/position publication and the single settled native move remain intact.

This is LibreWPF host ownership integration over the existing ProGPU desktop
contract, not a new renderer algorithm. Both managed and C++ renderers use the
same host code; no C++ scene, shader, wire record or fallback change applies.
The operation remains O(1) state assignment, with no new allocation, crossing,
resource recreation, readback or per-frame work. Original in-repository host code
is the implementation provenance; no third-party implementation was used.

Authored fixtures extend the existing nested single-move cases with different
native parent/child framebuffer scales, and cover the actual hidden adapter's
transport setter while preserving its source DPI and desktop scale. These tests
are compiled only, not executed. End-to-end mixed-monitor checks remain mandatory.

Next Windows blocker: the portable native-popup factory still rejects Windows,
and the LibreWPF decoration adapter has no Win32 popup-owner branch. Reuse the
existing ProGPU Win32 platform primitives to connect owned nonactivating windows;
do not remove the Windows SDK guard based on source/host compilation alone.

Compile-only checkpoint: LibreWPF bridge fixtures 116 warnings/0 errors initially,
20/0 on the final rebuild; source-built application harness 4/0. No tests, source
verifiers, applications, VM/GPU workloads, benchmarks or CI qualification ran.
The latest fetched ProGPU `origin/main` remains contained (zero missing commits).

## Placement selection coverage

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
