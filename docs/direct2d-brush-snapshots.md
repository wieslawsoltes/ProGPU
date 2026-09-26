# Direct2D brush snapshots

Windows command sinks now capture mutable brush properties before reusing a
retained paint record. Previously the cache compared only COM identity and the
draw transform. A caller of the standalone scene recorder could draw with a
brush, change its color, opacity or gradient geometry, then receive the old
paint on subsequent draws.

The cache covers all currently admitted brush families. Solid snapshots include
RGBA and opacity. Linear snapshots include start/end points, opacity and brush
transform. Radial snapshots include center, origin offset, both radii, opacity
and brush transform. Every entry retains its draw transform and owned COM
identity. Gradient stop collections are immutable; their independently owned
COM identity completes the key without copying stop arrays on unchanged hits.
Solid brush transforms do not affect solid coverage and remain irrelevant.

Changed states pass through existing validation and brush translation. Earlier
paint records remain immutable. Identical repeated states and A-to-B-to-A
transitions reuse their earlier indices. No public ABI, shader, execution-policy
default or managed wire declaration changes. Concurrent source mutation during a
callback remains outside the recorder's serialized-call contract.

The cache retains its existing linear search: O(B) time for B cached brush
states, plus O(1) property capture. Each state adds a fixed-size snapshot and an
owned immutable-collection identity. Changed gradients retain the existing O(S)
translation/storage for S stops. Unchanged hits allocate no stop vectors and
append no paint records. This is bounded COM metadata work, not a pixel kernel;
there is no applicable SIMD workload or new CPU fallback. Stable scene replay,
GPU uploads, submissions and pipeline initialization are unchanged. No measured
speedup or allocation reduction is claimed.

## Original implementation and applicability

The implementation authority is original ProGPU commit
`078ddc5ec9cc7478a7274f3f0036f554c42ca92e`:

- `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_render_target.cpp`,
  `add_brush`, snapshots current solid and gradient properties for each draw.
- `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d.cpp`, `add_brush`,
  `add_solid_brush`, `add_linear_gradient_brush`, `add_radial_gradient_brush`
  and `translate_gradient_brush`, own Windows recording and validation.

Portable COM already captures current properties and needs no product change;
it receives the same regression oracle. Ordinary managed rendering does not use
this Windows ingress cache. Both renderer modes consume the same unchanged
pointer-free brush table; managed Direct2D wrappers reach the corrected native
recorder without another implementation or per-draw managed/native crossing.
Only original ProGPU implementation is adapted; no foreign implementation text
or structure is imported.

## Research and design decisions

- [Direct2D brush properties](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1brush)
  and [solid color mutation](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1solidcolorbrush-setcolor%28constd2d1_color_f%29)
  establish mutable resource state. Preserve callback-time values as well as
  resource ownership. [Command-list streaming](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1commandlist-stream)
  motivates exercising system command-list snapshots independently from direct
  calls to the exported sink.
- [Skia paint](https://api.skia.org/classSkPaint.html) distinguishes copied paint
  values from shared immutable effects. Adopt that ownership distinction for
  mutable brush fields and immutable gradient collections; retain ProGPU's
  existing cache and serialization.
- [Win2D solid brushes](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Brushes_CanvasSolidColorBrush.htm)
  expose mutable color/opacity/transform. Resource reuse must preserve changes,
  so identity-only reuse cannot establish paint equivalence.
- [WebRender's retained rendering](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html) inform
  the separation of recording from GPU work. Existing scene reuse, visibility
  culling, worker preparation, demand-driven uploads and GPU batching remain
  with the common renderer. This fix adds no eager target/pipeline setup or
  independent path/texture cache, eviction scheme or device-loss lifetime.
- [Parley layouts](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz shaping-plan caching](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  keep reusable text results separate from paint changes. Font discovery/startup,
  shaping/layout reuse, fallback and variable-font state, glyph keys/eviction,
  atlas-generation invalidation, DPI/subpixel positioning and hinting remain
  unchanged. Brush mutation does not cause font discovery or text reshaping.

## Validation

Ubuntu ARM64 with Clang 18.1.3, CMake 3.28.3 and C++20 module scanning also
passed the core and compatibility suites on 2026-09-26 (2/2, 0.88 seconds).
SHA-256 checks matched the copied native source and shared regression files to
the host working tree. The Windows VM was suspended before Linux validation.

The shared `progpu_native_direct2d_brush_fixture.hpp` oracle checks all four
retained draws and their full paint values and gradient stops. Twelve cases
change one property at a time through A, B, B, A. Both portable COM and Windows
standalone recorders run the same source cases; Windows additionally requires
exactly two paint records and reuse of both repeated indices. The Windows suite
also runs twelve actual system `ID2D1CommandList` recordings through the sink.
Post-record mutation and release precede serialization, and system-list cases
release the caller's brush references before streaming.

Run `progpu_native_direct2d_compat_tests` for the portable fixtures and
`progpu_native_direct2d_tests` for Windows recorder/system-list coverage. These
tests qualify retained paint capture, not the remaining bitmap/image/glyph,
mesh, blend-mode or full Win2D/application/package contracts.

On 2026-09-26, the Release Apple Clang 21 portable compatibility test passed
(1.40 seconds). A fresh Windows ARM64 Visual Studio 2026/MSVC 19.51 build
compiled the Windows provider and all three Direct2D test targets. Core and
portable compatibility tests passed, and the Windows provider suite passed
(11.62 seconds), including all direct-recorder and system-command-list cases.
These builds disable the WebGPU renderer and do not establish full renderer,
image, package, NativeAOT or application qualification.
