# Direct2D command-stream antialiasing

## Implementation-first checkpoint (2026-09-07)

The Windows `ID2D1CommandSink1` scene translator now accepts both declared
antialiasing modes for `PushAxisAlignedClip` and geometric `PushLayer` masks.
It uses the same pointer-free ProGPU mask and scope representations already
implemented by portable C++ `ID2D1RenderTarget`. No native ABI, exports, generated
wire records, shader, WPF bridge or execution-policy default changes.

For rectangular clips, capture the world transform at push time, compute the
transformed axis-aligned envelope, and intersect the parent clip. Aliased clips
remain state save/restore scopes. Per-primitive clips become zero-radius analytic
rectangle masks on an opacity-one source-over group. Pop closes that group,
applying fractional edge coverage once to all its contents, not independently
to overlapping draws. Later transform changes do not move an existing clip.
Clip/layer scope kinds remain distinct, including the new internal AA-clip kind;
crossed pops, nonfinite values, invalid enum values and capacity exhaustion fail
closed without publishing a partial stream. The existing clip diagnostics flag
continues to identify the source operation; this does not add a public flag.

Geometric layer masks independently select a 1x1 grid for aliased coverage or
the existing 8x8 grid for per-primitive coverage. Both vector-only and combined
geometry-plus-opacity-brush masks carry that grid on the retained analytic path.
Fill rule, line/cubic topology and `maskTransform * drawTransform` are unchanged.
The draw target's primitive antialiasing state does not override mask coverage.

The paired managed native-scene writer previously accepted only 4x4/8x8 grids,
although native mask/path validation and execution already admit 1x1. Both its
clip-path and path-fill validators now accept exactly 1, 4 and 8. Unknown grids
remain rejected; this is not a change to managed `PathAtlas` normalization or
ordinary managed text/path quality defaults.

## Ownership, original provenance and applicability

The original in-repository implementation authority is ProGPU
`c00ff1943992f11ce8435c7edc99d538ef07c94a`:

- `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_render_target.cpp`:
  `PushAxisAlignedClip`, `PopAxisAlignedClip`, `add_geometric_layer_mask`.
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask.cpp` and
  `progpu_native_semantic_validation.cpp`: declared 1/4/8-grid contract.
- `src/ProGPU.Native/src/Backend/progpu_native_clip_execution.cpp` and
  `progpu_native_path_execution.cpp`: existing native grid execution.

This is an original ProGPU-to-ProGPU port into the Windows translator, not a
foreign implementation port. Portable C++ rendering already has the product
behavior; its matched fixture is extended. Managed `NativeSceneStreamBuilder`
is updated for the same serialized contract. Ordinary managed scene compilation,
shaping, text and atlases are not changed because this feature translates COM
command lists into an existing native scene representation, not managed draws.

Rectangle translation is bounded O(1) per push/pop with the existing fixed-depth
stack and O(C) retained payload for C clips. Geometry capture remains O(S) work
and storage for S analytic segments; an opacity brush retains its existing stop
payload. Grid selection is O(1). No new CPU pixel loops, readback, repacking,
per-segment submissions, per-frame COM retention or fallback path is introduced.
GPU allocation/rasterization stays demand-driven in the shared mask/layer engine.
Mask grid remains part of retained resource data and therefore of resource
generation changes. Existing device-loss and atlas invalidation remain in force.
No new performance claim is made without measurements.

## Primary research and design decisions

- [Direct2D rectangular clips](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-pushaxisalignedclip%28constd2d1_rect_f_d2d1_antialias_mode%29):
  adopt transformed AABB semantics, once-at-pop blending and strict scope nesting.
- [Direct2D layer parameters](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ns-d2d1-d2d1_layer_parameters):
  adopt independent geometric-mask antialiasing and mask/draw transform composition.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) and
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm):
  retain scoped recording and reusable command resources; do not import their
  implementation or substitute per-draw clipping for the Direct2D group contract.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html): keep
  clip/group descriptions in retained scenes; leave culling, demand-driven GPU
  preparation, batching, worker ownership and cache lifetime in the common engine.
- [Parley layout](https://docs.rs/parley/latest/parley/layout/struct.Layout.html)
  and [HarfBuzz caching](https://harfbuzz.github.io/shaping-plans-and-caching.html):
  no shaping/layout work belongs in clip translation. Layout reuse, font fallback,
  variation state, DPI/subpixel/hinting, startup font discovery and glyph-cache
  eviction remain untouched. Do not initialize fonts or pipelines to record clips.

## Fixtures and pending qualification

The shared `progpu_native_direct2d_clip_fixture.hpp` oracle is called by portable
COM and Windows command-list fixtures. It requires eight correctly nested commands,
two overlapping draws, transformed fractional bounds `[2.25,3.625,10.5,17.25]`,
an identity-transform zero-radius group mask, and the intersected aliased child.
Windows aliased layer fixtures cover geometry alone and geometry plus an opacity
brush, requiring the same analytic path as the antialiased fixture except its grid.
Existing portable fixtures cover both independent mask modes and both mask kinds.
Managed boolean-vector-mask and mixed-path allocation fixtures are parameterized
over 1/4/8; undeclared-grid rejection fixtures are also authored.

Fixtures are authored, not executed. The fast macOS C++20 build compiles/links
the portable COM fixture and shared oracle only. It has no Windows provider target;
Windows provider/fixture compilation and full renderer builds remain outstanding.
Final `ProGPU.Tests` Release compilation succeeds with zero warnings and errors;
the authored allocation assertions have not been executed.
All runtime, image/differential, GPU/VM/platform/package, SIMD/performance,
source-verifier and exact-head CI qualification remains deferred until the final
validation phase. Backdrop/ignore-alpha layers, full-target command-list opacity
brushes and non-axis-preserving finite layer bounds remain explicit unsupported
translator cases; this checkpoint does not establish full Direct2D or Win2D parity.

The later [target-brush-domain checkpoint](direct2d-target-brush-domains.md)
supersedes the full-target surface-command-list opacity-brush limitation and
records the corresponding targetless-recorder and compilation limits.
