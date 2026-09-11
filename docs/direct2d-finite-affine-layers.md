# Finite affine Direct2D layers

## Implementation-first checkpoint (2026-09-07)

Portable C++ render targets and the Windows command-list/standalone-recorder
translator now accept finite layer content bounds under rotation, reflection and
shear. The previous blanket non-axis-preserving rejection is removed. Uniform
layer opacity, optional geometric masks, existing solid/gradient opacity brushes
and portable bitmap opacity brushes keep their normal retained representations.
No native ABI, shader, managed scene layout or execution-policy default changes.

## Coordinate and ownership contract

At PushLayer, map the finite content rectangle through the current world
transform to its target-aligned bounds. Those bounds constrain the existing
semantic layer. Geometric masks remain exact retained vector masks, transformed
independently by maskTransform followed by the captured world transform. Their
bounds may narrow the layer allocation but never replace mask coverage.

An opacity brush needs a finite domain covering the entire layer. Under rotation
or shear, the original local content rectangle alone maps only to a quadrilateral
inside the target-aligned extent. Reusing it would incorrectly remove alpha near
the extent corners. The new path inverse-maps the target rectangle and gives its
outward-rounded local envelope to the existing brush mask compiler. Its brush
coordinate transform stays unchanged. The layer bounds, not this conservative
material envelope, constrain target coverage.

Axis-preserving transforms retain their existing direct domain. Full-target
layers retain their existing target-size/DPI contract. A finite domain does not
require output dimensions, so legacy targetless standalone recorders support
these finite layers without the ABI v55 target descriptor. Finite content alone
does not set HasTargetDependentMasks. All state is captured at push; later world
transform changes affect subsequent draws, not the layer mask or bounds.

Zero-width/height target extents produce empty material domains without inversion.
An unrepresentable or noninvertible nonempty brush domain still fails closed;
background initialization, ClearType/ignore-alpha options and resource families
not yet implemented remain explicit gaps. This change does not fill them with
an approximation, create synthetic COM geometry, or read CPU pixels.

## Shared SIMD implementation and provenance

Original ProGPU source is `565e079f8c621e9aafd3b75b3f96bf9438b3d927`, specifically
`src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_core.cpp`
viewport_coverage_bounds and both existing PushLayer implementations in
`progpu_native_direct2d.cpp` and `progpu_native_direct2d_render_target.cpp`.
The original viewport algorithm is generalized to rectangular_coverage_bounds
with a double target origin. The zero-origin API delegates to it and retains a
zero-origin arithmetic fast path. No third-party implementation is copied.

NEON, SSE2 and enabled Wasm SIMD128 compute independent double x/y origin and
corner arithmetic. Preserve explicit multiply/add order and the existing outward
float origin/extent rounding. The four-corner scalar metadata fallback remains
only for targets without double-lane SIMD; scalar finite classification, min/max
reduction and rounding are bounded control work, not a whole-buffer pixel loop.
All workspace is fixed stack storage and unaligned loads remain safe.

Time/storage added per affected layer are O(1), with no heap allocation in domain
mapping. Existing mask/path/gradient resource and bitmap child-picture recording
costs are unchanged. No extra GPU layer, draw, submission, readback, upload,
shader pipeline or CPU pixel fallback is introduced by domain calculation.
Actual measurements, including any change in affected mask allocation dimensions,
are deferred; there is no speedup or memory-improvement claim.

This is native COM ingress behavior in two C++ producers, not a new managed
renderer algorithm. Both producers change together and share domain arithmetic
and fixture expectations. The managed renderer already consumes the unchanged
native scene layer/mask contract; managed wrappers reach this behavior through
their existing native calls. No WPF-specific adapter or reflection is needed.

## Primary research and decisions

- [Direct2D layer parameters](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ns-d2d1-d2d1_layer_parameters)
  distinguish content limits from geometric coverage and brush alpha.
  [Layers overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
  documents group composition, masks and rectangular-clip alternatives;
  [axis-aligned clipping](https://learn.microsoft.com/en-us/windows/win32/direct2d/how-to-clip-with-axis-aligned-rects)
  describes rectangular bounds. The target-aligned affine content extent is the
  implementation interpretation used here, consistent with existing ProGPU
  clip/bounds handling; its native-Windows pixel conformance is not yet proven.
  The final oracle must specifically distinguish an AABB from the transformed
  source quadrilateral and compare edge coverage and geometric-mask combinations.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) and
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm)
  inform scoped recording and independent bounds/clip state. Preserve one group
  composition; reject per-draw opacity and fake rectangle geometry adapters.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html) inform
  retained resource separation and demand-driven GPU work. Existing culling,
  worker preparation, GPU batching, mask cache keys/eviction and device-loss
  ownership remain in the shared renderer. No new cache or eager target allocation.
- [Parley](https://docs.rs/parley/latest/parley/layout/struct.Layout.html) and
  [HarfBuzz](https://harfbuzz.github.io/shaping-plans-and-caching.html) reinforce
  reusable shaping/layout results. Font startup, fallback/variation state, glyph
  atlas generation/eviction, demand uploads, DPI/hinting and subpixel policy are
  unchanged; layer-domain work must not trigger text reshaping or discovery.

## Authored fixtures and deferred qualification

The core scalar oracle covers 108 translated-domain combinations in addition to
the 36 original zero-origin cases and invalid origin/extent/pointer cases. A
shared bounds/brush/vector-mask fixture is consumed by 18 portable and 24 Windows
finite layer combinations. Both mutate the draw transform after the push to
check capture timing. Windows cases additionally compare legacy targetless
recorder bytes with surface-backed translation and reject false target-dependency
flags. Portable bitmap/composite opacity fixtures now use finite shear as well
as the full-target affine case, preserving snapshot lifetime and sampling checks.

The existing Windows native-vs-ProGPU D3D12 differential executable adds two
finite-shear image cases (without/with a solid opacity brush). The same original
ID2D1RenderTarget drawing calls execute through each implementation. Interior and
exterior RGBA samples differ by at most two channel units, including AABB corners
outside the source parallelogram. An explicit center-alpha range rejects two
blank outputs and requires group opacity .625. A one-pixel border around the
fractional content extent is excluded from this focused domain classifier;
fractional edge quality still requires the final dedicated image qualification.
Existing ordinary image thresholds and one-submission requirements remain intact.
These cases are part of the existing differential target, not a new optional gate.

Fixtures are authored, not executed. Apple Clang C++20 core/portable COM/header
targets compile/link. The fast build excludes the Windows provider and full
renderer, so Windows code/fixtures and full renderer packages remain uncompiled
for this batch. SSE2/Wasm compilation, managed/native runtime and cross-platform
image comparisons, VM/native-Direct2D oracle, performance/SIMD measurements,
package SDK/third-party gates, source verifiers and exact-head PR CI qualification
remain pending. Main was refreshed with zero missing commits. The full
MIL/DirectX/Direct2D/Win2D goal remains active and unqualified.
