# Native MIL host popup composition

## Implementation-first checkpoint — 2026-09-08

LibreWPF now adapts owner-surface popup roots into canonical MIL visual resources,
alongside its main root. ProGPU's existing C++ visual traversal owns the actual
composition, clipping and rendering. Separately surfaced popup windows inherit
the owner's selected renderer. The managed portable renderer remains independently
available; selecting native MIL no longer blanket-rejects portable popup roots.

This is core application integration for menus, ComboBoxes and tooltips, not a
new Direct2D compatibility expansion or proof of complete popup platform parity.

## Canonical graph and capture lifetime

The existing main visual becomes the first child of a protocol-owned container.
Each visible owner-surface popup follows it, in bridge order, through a separate
placement visual. Placement has a DIP offset and an exact local rectangle geometry
clip; its child is the actual source-built popup visual root. Popup state does not
inherit the main root's transform, clip, opacity or effects. Root-local popup
transforms and effects remain native visual state below the placement boundary.

This container is canonical MIL state, not a fake managed Visual or reflected
adapter. The ordinary source identity/cycle guards remain authoritative: repeated
visual parenting and missing typed visual state fail closed. A popup can reference
shared brush/image resources without manufacturing a second renderer.

Do not use ScrollableAreaClip for placement. That command captures world-space
pixel-aligned scroll state before a visual's local offset and applies snapping.
Popup bounds require ordinary local geometry clipping after placement. Width and
height must be finite and positive; offsets must be finite.

The host reuses list-owned value-record scratch and passes its span synchronously
to the producer. Scratch references are cleared in a finally block. The batch owns
its pointer-free serialized resources, and existing sideband leases own external
images. No per-popup P/Invoke or CPU pixel readback is added. No-popup production
keeps the original single-root packet shape.

Movement, resize, visibility, source render requests and removal advance a host
popup revision. A successfully installed batch records the revision captured
before preparation; changes raised during layout/capture remain pending. Layout
can close a popup or replace its root, so the bridge rereads visibility/root after
layout. Failed updates do not consume the revision. Stable-topology movement uses
the existing mutable-packet delta; structural additions/removals can still build a
replacement channel transactionally. General stable handle allocation remains
separate implementation work.

Separate native-window popups are excluded from owner-surface replay. They keep
their own render target/session, transient ownership and existing input routing,
but use the owner's renderer selection. No global default changes. Typed
presentation-source hit testing remains the fallback when a native scene has no
managed GPU-owner cache; this batch does not implement a new hit-test engine.

## Original implementation provenance and applicability

Original LibreWPF source is `b8f2e93fe`, particularly
WpfPortablePopupBridge.Replay/PositionPopupRoot/RequestRender,
WpfPortableNativePopupHost, WpfNativeMilSceneCompiler.BuildContext and
WpfNativeMilCompilationSession.Update/CreateDelta. Original ProGPU source is
`af36ba00`, specifically NativeMilBatchBuilder visual/rectangle command writers
and `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp` visual traversal, offset and
clip handling. The new native fixture is in progpu_native_mil_tests.cpp.

The missing behavior was host root selection, placement and invalidation, not a
missing native rendering algorithm. The typed WPF host/producer therefore changes;
ProGPU C++ consumes its unchanged canonical commands. The existing managed
portable popup path already composes separate roots and is preserved. Paired
producer/host and native semantic fixtures cover the adaptation contract. No C
ABI, shader, generated wire record or MIL product source changes are needed.
No foreign implementation is copied or adapted.

## Cost and research decisions

Popup preparation is O(P) for P open bridges plus the existing source traversal
cost for changed visible roots. Additional retained metadata is O(V) for V visible
owner-surface popups. A stable frame does not rebuild this metadata. Changed-scene
serialization retains its existing allocations; this is not claimed allocation-
free. Render requests from separately surfaced popups do not advance the owner's
popup snapshot revision. Fixed metadata checks and COM/window operations are not SIMD workloads;
compute-heavy rendering stays in the shared GPU/intrinsic implementation. No new
pipeline, glyph discovery, per-item submission or upload strategy is introduced.

- [WPF Popup](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/popup)
  documents separate visual roots, content bounds and independent effects. Adopt
  separate native placement and preserve existing source-built sizing/input.
  Owner-surface fallback remains surface-bounded and is not equivalent to an
  unrestricted separate desktop window.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) and
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm)
  inform explicit transform/clip state and shared command recording. Reject
  managed drawing callbacks mixed into native output or rebuilding fake shapes.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html) inform
  retained scene separation. Startup, visibility culling, worker preparation,
  demand uploads, GPU batching and path/texture cache keys/eviction remain shared;
  no popup-specific raster cache is introduced. Device-loss generations remain
  owned by the existing session/context and still require final qualification.
- [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  reinforce keeping reusable shaping/layout apart from presentation. Font fallback,
  variable-font state, hinting/subpixel policy and glyph atlas generations are
  unchanged. Popup placement must not reshape text or rediscover fonts. Independent
  presentation DPI support remains an explicit next core requirement.

## Authored fixtures and qualification still required

Managed fixtures inspect canonical clipping, parent structure, position-only
deltas, unchanged batches, removal rebuilds, invalid bounds and duplicate-parent
failure. Host fixtures cover visible/hidden/removal/root-change capture, revision
changes and exclusion of separate native windows. A source guard checks renderer
inheritance. The native fixture compiles two ordered roots at two popup offsets
and checks exact offset/local clip state rather than only counting commands.

Native MIL C++ fixtures compile/link with Apple Clang C++20. The managed test
graph compiles in Release; fixtures are not executed. Full renderer/provider
builds, native/managed image and input comparisons, VM/platform/SDK/package,
source verifiers, SIMD/performance and exact-head PR CI qualification remain
deferred. No pixel, performance, interactive or full-parity claim is made.

Native popup platform selection is unchanged: Cocoa/X11 use transient native
windows when enabled; other cases use their existing Windows/owner-surface paths.
Owner-surface content cannot escape the parent surface. Wayland popup protocols,
Windows native-MIL popup presentation outside owner bounds, nested mixed-surface
placement, lifecycle/input capture and renderer/device-loss behavior still need
end-to-end qualification and any resulting implementation fixes. Window-region,
partial-viewport and nonuniform-DPI host guards remain in place. Do not report the
core desktop milestone or full original goal complete from this checkpoint.
