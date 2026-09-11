# Direct2D styled primitive ingress

## Implementation-first checkpoint (2026-09-08)

The Windows command-list/standalone-recorder translator now routes styled
DrawLine and DrawRectangle calls through the existing semantic geometry stroke
compiler. DrawGeometry and FillGeometry also accept aliased primitive state.
Portable C++ render targets already have these styled and aliased paths; both
producers now accept normal zero-width line/rectangle calls as nonpainting draws.
This does not imply full Direct2D or Win2D parity.

## Representation and ownership

An explicitly styled line becomes one open, hollow ID2D1PathGeometry from the
brush's owning factory. A styled rectangle becomes one closed
ID2D1RectangleGeometry from that same factory. These are real COM resources,
not private-layout casts or WPF-shaped adapters. Local COM owners release the
factory, sink and temporary geometry after synchronous capture; the resulting
scene owns its pointer-free geometry and style snapshot. Factory, Open and Close
failures are latched, with allocation failure distinguished from unavailable
resource support. No caller resource pointer is retained by this lowering.

The common stroke compiler preserves caps, joins, dashes, transform type,
primitive alias flags and captured world transform. Straight geometry retains
stroke batches; curves retain their existing semantic representation. Existing
Widen fallback consumes the same alias policy when a geometry cannot publish
the semantic Simplify representation. No rectangle is split into independently
capped lines and no stroke is replaced by its bounding box.

Path-fill descriptors use sample_grid 1 for aliased state and 8 for the existing
per-primitive AA path. Stroke and geometry descriptors retain their existing
edge-alias flags. This is primitive state, independent of layer-mask/clip AA.
It does not add support for the native DXGI multisample exception or prove
native-Direct2D edge equivalence.

Ordinary positive-width, null-style primitives retain their direct fast paths.
Normal zero-width null-style primitives validate their inputs, count one logical
draw, and emit no resource or coverage command. Negative/nonfinite widths remain
errors. An explicit hairline style still uses device-width stroke preparation,
even with a requested width of zero; zero width must not erase that geometry.

## Original provenance and paired applicability

Authoritative original ProGPU source is commit
`c85262bd32736cfe5d72caf31ca69b05e6941b05`, specifically:

- `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_render_target.cpp`:
  draw_styled_line, draw_geometry_shape, DrawGeometry and draw_captured_path.
- `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d.cpp`:
  draw_stroked_geometry, draw_semantic_stroked_geometry, translate_stroke_style
  and existing stroke/geometry alias flags.

The Windows producer reuses original portable lowering and its existing stroke
compiler instead of introducing a second stroker. Portable and Windows ingress
both receive the zero-width correction. The managed renderer and wrappers consume
the unchanged scene representation; no managed algorithm, public wire layout,
generated contract, ABI version or canonical shader changes. The same alias
fixtures cover both C++ producers. No third-party implementation is copied.

## Costs and shared rendering architecture

Primitive-to-geometry lowering adds O(1) COM calls and bounded geometry storage
only when a style is supplied. Existing style capture is O(D) for D dash entries;
existing semantic preparation remains proportional to captured segments and
generated coverage. It is not claimed allocation-free: temporary geometry/sink
objects and existing retained stroke arrays allocate at changed-scene recording.
Unchanged scene replay adds no calls, geometry rebuilding or retained uploads.

No new CPU pixel loop, GPU readback, upload stage, pipeline, per-item P/Invoke or
queue submission is introduced. Independent compute-heavy work remains in the
shared intrinsic/GPU implementations; COM calls and bounded control validation
are not SIMD workloads. Execution policy and fastest-qualified defaults do not
change. Performance and allocation measurements remain deferred.

## Primary research and decisions

- [Direct2D DrawLine](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawline)
  defines optional style, nonnegative DIP width and delayed render-target errors.
  Preserve those inputs and the existing ProGPU normal/hairline distinction.
  [Primitive antialiasing](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_antialias_mode)
  informs explicit alias propagation; do not substitute per-primitive AA when
  the caller selected aliased state.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) and
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm)
  inform common stateful recording and geometry/paint separation. Adopt shared
  lowering, reject a primitive-specific renderer or per-cap blended draws.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html) inform
  retained geometry/resource separation and GPU batching. Startup remains lazy;
  culling, worker preparation, path/texture cache keys and eviction, demand upload
  and device-loss generation remain shared renderer responsibilities. No new
  ingress cache or eager GPU initialization is added.
- [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz plans](https://harfbuzz.github.io/shaping-plans-and-caching.html)
  preserve reusable CPU shaping/layout as separate work. Font discovery,
  fallback/variation state, glyph cache ownership/eviction, hinting and subpixel
  positioning are unchanged. Geometry conversion does not trigger text work.
  Existing fixed/hairline DPI handling stays in the common stroke compiler.

## Authored coverage and remaining qualification

Windows fixtures compare twelve styled primitive/DrawGeometry byte-equality
pairs across line/rectangle, three stroke transform modes and both AA modes.
Additional curve/fill and zero-width cases check every relevant path, stroke
and geometry resource descriptor through a shared bounds-checked fixture.
Portable fixtures cover twelve line/ellipse mode/AA combinations, styled
rectangle and fill AA cases, zero-width empty scenes and negative-width errors.
The shared helper requires emitted coverage for nonempty draws; logical draw
counts alone cannot prove rendering.

Apple Clang C++20 portable COM fixtures compile and link. Fixture compilation
errors were corrected; none of these fixtures have been executed. The fast
build excludes the Windows provider and full renderer, so these Windows edits
and fixtures have no compilation evidence yet. Windows/MSVC/Clang and Linux/Wasm
builds, native pixel differentials, runtime/SIMD/performance, VM/package/SDK,
source verifiers and exact-head PR CI qualification remain deferred. No parity
or speedup claim is made. ProGPU main was refreshed with zero missing commits.

Remaining Windows command-stream gaps include bitmap/image/glyph recording,
meshes, opacity masks and FillGeometry opacity brushes, non-source-over blend
modes and broader context/Win2D integration. Those are implementation work,
not covered or waived by these primitive fixtures.
