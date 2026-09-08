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

## Windows owned nonactivating native popup connection

The existing MVP/Toolkit menu/ComboBox action could not create a native portable
popup on Windows: the factory rejected Windows and the decoration adapter only
configured Cocoa/X11 ownership. This is source-backed, not a reproduced VM run.

ProGPU now provides `NativePopupWindow.TryConfigureOwner` over neutral native
handles. `Win32NativeWindowPlatform.Popup.cs` reuses original ProGPU Win32 window
attribute/frame accessors from `Win32NativeWindowPlatform.cs`. Its original
configuration planner validates same-thread/process top-level windows and a hidden
popup, sets its owner and popup/nonactivating/tool-window styles, removes ordinary
overlapped chrome and taskbar-forcing style, and preserves unrelated bits. The
frame refresh has no show, move, resize, activation or topmost promotion. A failed
apply rolls back the captured attributes and removes the activation hook; the
caller must destroy the hidden popup even if rollback itself cannot succeed.

The implementation follows public contracts, not third-party implementation code:

- [SetWindowLongPtrW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw): top-level ownership is GWLP_HWNDPARENT, not child reparenting; zero previous values are valid and cached styles need frame refresh.
- [Extended styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles): nonactivation and tool-window state are separate from app-window and topmost state.
- [CreateWindowExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowexw) and [WM_MOUSEACTIVATE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate): click queue activation needs explicit handling; MA_NOACTIVATE preserves delivery of the click.
- [SetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass) and [DefSubclassProc](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-defsubclassproc): callback identity/lifetime is per window, subclassing stays on the owning thread, and unhandled messages continue through the chain.
- [.NET SetLastError interop behavior](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.dllimportattribute.setlasterror?view=net-10.0): the supported .NET runtime clears native last-error before flagged calls and caches the result for distinguishing valid zero returns from failures.

The native subclass uses one static `UnmanagedCallersOnly` entry, no managed
delegate/GCHandle or per-message allocation. It returns MA_NOACTIVATE for click
activation, otherwise forwards to DefSubclassProc, and removes its own hook on
WM_NCDESTROY. The backend assembly must outlive every configured native window.
Installation and each callback use O(1) state/work; the native subclass facility
owns one bounded registration per popup. Flag manipulation and ordered OS calls
have no independent data-parallel workload for SIMD or GPU execution. No renderer
submission, device initialization or readback is introduced.

LibreWPF's Win32 decoration branch consumes this API. Its portable factory now
admits Windows; the actual hidden native popup must configure ownership before
Show. Rejection or exception disposes it and fails explicitly rather than showing
an unowned window. Existing nonactivating GLFW show, owner transport decoding,
independent framebuffer ownership and renderer inheritance remain in place.
This is shared platform/host behavior for both managed and C++ renderers, with
no C++ scene, canonical shader, native C wire or rendering algorithm changes.

CPU planner fixtures exercise style preservation, each mutation/hook/frame failure
and rollback, plus invalid/foreign/child/visible rejection. A Windows-only fixture
creates real hidden system-class HWNDs, checks resulting ownership/styles and the
click-activation response, and destroys both windows through the native subclass
chain. It explicitly skips non-Windows hosts and does not prove real menu focus,
GPU output, monitor transitions or package activation. These fixtures are authored
for final qualification and have not run. The Windows SDK guard remains until
source media startup and the complete package application path are connected.

Compile-only checkpoint: ProGPU.Tests 0 warnings/0 errors, LibreWPF bridge fixtures
21/0 on the final rebuild and source-built application harness 5/0. The initial
parallel bridge build succeeded with one shared-output copy retry; overlapping
build graphs were serialized thereafter. Tests (including the Windows fixture),
source verifiers, GPU/VM applications, benchmarks and CI qualification were not
executed. The latest fetched `origin/main` is contained in this branch.

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
