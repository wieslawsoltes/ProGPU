# Full-target Direct2D brush domains

## Implementation-first checkpoint (2026-09-07)

Surface-backed Windows Direct2D command-list translation now supports full-target
opacity-brush layers with the existing solid, linear and radial brush mappings,
including a geometric mask combined with the brush. Rotation, reflection, shear,
translation and nonuniform DPI use the same local-domain calculation as portable
C++ Direct2D. Finite non-axis-preserving layer content bounds remain a separate
unsupported case; this change must not broaden an exact geometric clip.

At translation, the surface mutex protects a snapshot of physical surface size
and the current context DPI. The backing surface size, not a temporary command
list target or stale creation-time DPI field, defines the output viewport. Each
full-target brush push inverts its current draw transform, then inverse-maps all
four viewport corners. Their outward-rounded local envelope supplies finite
brush-mask domain metadata. The mask keeps the original draw transform and brush
coordinate mapping; its GPU material and optional analytic geometric mask still
perform the actual coverage calculation. The envelope is not a geometric clip.

The additive result flag `HasTargetDependentMasks` / native bit 10 reports this
dependency. Consumers must rebuild the stream under a new generation when physical
surface dimensions or either context DPI changes, and before replay on a differently
sized/DPI target. Size and DPI must remain stable between measurement and writing.
The flag does not itself invalidate a caller-owned cache. Fixed record layouts,
exports and the ABI version remain unchanged; managed/native flag values have a
matched contract fixture. Existing streams without this dependency are unaffected.

The legacy standalone recorder has no target descriptor and still returns explicit
unsupported state for a full-target opacity brush instead of guessing dimensions.
The subsequent [ABI v55 target-aware recorder](direct2d-target-aware-recorders.md)
supplies a typed immutable descriptor and supports that recorder case.
Missing/nonpositive target extent, invalid DPI, noninvertible transforms and
unrepresentable bounds fail closed. No partial stream is returned as success.

## Shared original implementation and SIMD

The original ProGPU authority is
`9d75d31354db150b2a07668f7a278638d3732cf7` at
`src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_render_target.cpp`,
`try_resolve_full_target_local_bounds`. Its four-corner/envelope and outward
origin/extent rounding algorithm is moved into shared behavior-core
`viewport_coverage_bounds`. Both portable and Windows Direct2D now call it.
The existing per-provider matrix inversions are retained; their narrowed float
inverse is supplied to the common helper, while viewport arithmetic stays double.

Independent x/y arithmetic uses double-lane NEON on ARM64, SSE2 on qualified x86
targets, and Wasm SIMD128 when enabled. Loads/stores are unaligned-safe and all
storage is fixed stack data. Targets without double-lane SIMD retain a documented
four-corner scalar metadata path, not a whole-buffer CPU image fallback. Finite
classification, min/max reduction and outward rounding remain bounded scalar
control work. This introduces no new pixel kernel or execution-policy override.

Time and workspace are O(1) per full-target push, with no heap allocation in bounds
calculation. Retained mask/gradient/path resources retain their existing costs.
No CPU readback, repacking, shader fork, per-corner submission or GPU initialization
is added. No speed or allocation measurement is claimed before final validation.

The feature is COM/native-scene ingress, not a new ordinary managed renderer
algorithm. Managed applicability is the typed dependency flag and its contract
test. C++ portable and Windows consumers share the product calculation. Managed
WPF drawing, `PathAtlas`, text quality defaults and the existing CPU/GPU fallback
selection remain unchanged.

## Research and retained-resource decisions

- [Direct2D layers](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ns-d2d1-d2d1_layer_parameters)
  and [DPI](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-getdpi):
  preserve brush alpha multiplication, mask/draw transforms and separate horizontal
  and vertical DIP conversion. Reject implicit dimensions on a targetless recorder.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) and
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm):
  retain owned reusable recording and scoped composition, while making this
  target-bound compilation dependency explicit. No foreign source is ported.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html): preserve
  scene/transform/resource separation, culling and demand-driven GPU preparation.
  Do not allocate target textures during command translation. Worker scheduling,
  GPU batching, mask cache keys/eviction, and device-loss generations stay owned by
  existing shared execution; target-dependent streams require caller invalidation.
- [Parley](https://docs.rs/parley/latest/parley/layout/struct.Layout.html) and
  [HarfBuzz](https://harfbuzz.github.io/shaping-plans-and-caching.html): keep shaping
  and layout reusable and outside mask-domain work. Startup/lazy font discovery,
  fallback/variation identity, glyph uploads, atlas eviction, hinting/subpixel
  policy and text DPI behavior are untouched.

## Fixtures and qualification limits

Core fixtures compare SIMD-dispatched bounds against a deliberately scalar oracle
for 36 affine/viewport combinations, plus pointer, nonfinite, size and overflow
failures. Existing portable fixtures exercise rotated/sheared/reflected full-target
brushes, nonuniform DPI, bitmap/composite masks and singular-transform rejection.
New Windows fixtures cover 36 combinations of DPI, affine transform, geometry and
solid/linear/radial brush, require the target-dependency flag, and independently
inverse-map every viewport corner. A targetless recorder fixture requires failure.
Two byte-buffer casts in the preceding Windows AA fixtures are corrected to match
the C API `uint8_t*` destination type; those Windows fixtures remain uncompiled here.

These fixtures are authored, not executed. Apple Clang C++20 core/portable builds
compile the shared product calculation. ProGPU.Tests Release compilation succeeds
with zero warnings/errors; main was refreshed with zero missing commits.
Windows provider/fixture compilation,
SSE2/Wasm compilation, full renderer packaging, runtime/image/VM/platform gates,
SIMD benchmarks, source verifiers and exact-head CI qualification remain pending
under the implementation-first sequencing. This is not full Direct2D/Win2D parity.
