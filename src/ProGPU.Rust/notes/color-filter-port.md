# Original color-filter port

Code-first checkpoint, 2026-11-09. No compilation, tests, GPU/browser execution,
benchmarks, or parity qualification were performed, per the user's mandatory
port-before-validation instruction. Only the owned Rust sources were formatted.
No build products, result images, benchmark output, or traces were generated.

## Scope and public integration

`image::ColorFilter` owns immutable original factory generations through `Arc`.
Factories cover normalized RGBA and HSLA matrices, five compact channel-table
layouts, gamma tables, lighting, 29 semantic blend modes, luminance-to-alpha,
ordered Compose, premultiplied Lerp, high contrast, and six-color overdraw.
`ColorFilterKind` exposes immutable views; callers cannot mutate retained bytes.
Original no-op blend factories return `None`; lerp retains original NaN rejection,
endpoint selection (including infinities), and same-owner identity selection.
The protected original gamma objects become shared `OnceLock` generations.

`ColorFilter::apply_color` ports the original managed RGBA8 brush evaluator,
including alpha-zero behavior, HSL round trips, all separable/nonseparable blends,
per-node byte quantization, and midpoint-to-even conversion. `apply_brush_color`
retains the original float-to-byte-to-filter-to-float brush boundary. This is a
single brush-color API, not a CPU image-buffer fallback. Matrix output channels,
Porter-Duff and lerp arithmetic use `wide::f32x4` intrinsic lanes. HSL reductions,
branching hue selection, and the original scalar floating-point transcendental
calls preserve their original per-color dependencies; no scalar pixel loop is
introduced. Exact managed runtime `Vector4.Dot`/`Vector4.Lerp` rounding and
platform transcendental behavior remain differential-validation obligations.

`ColorFilterPlan::new(filter, maximum_steps)` creates bounded child-first
operations. Compose binds outer evaluation to the completed inner result;
Lerp evaluates children against the same input. Identical immutable node/input
pairs share an actual planned result; no matrix multiplication or algebraic
fusion crosses an intermediate clamp or RGBA8 quantization. The explicit root
owner keeps every dependency alive. Rust adds a nesting guard of 256 nodes and a
caller-selected operation budget to bound stack and prepared resource work;
these are documented Rust admission limits, not native producer qualifications.

`ImageColorMatrix` retains the original five rows/offset arrays and explicit luma
flag. `from_managed([f32; 20])` preserves finite-only managed admission and already
normalized offsets. `from_native` / `to_native` preserve native magnitude <=1024,
96-byte size, flags bit zero, and reserved-zero checks. `NativeImageColorMatrix`
is the 96-byte header/rows/footer packet. `ImageColorUniforms` is the independent
96-byte six-vector GPU record. Both have compile-time size assertions.
`NonlinearColorUniforms` and `OverdrawColorUniforms` also preserve exact 96-byte
records and source parameter construction. Native Texture drawing applies the
matrix before luma, then clamps. Existing ImageEffect drawing applies luma before
the matrix; combined flags must not silently select the wrong ordering.

With `gpu`, `ColorFilterComputeEngine::new(device, preference)` retains the
actual device and lazily creates the five original pipelines. Automatic and
NativeCompute resolve to the original compute route; other forced routes reject
rather than changing the algorithm. Async `prepare(operation, background,
foreground)` validates the complete single operation before GPU allocation,
retains actual same-device premultiplied captures, and owns immutable parameters
plus a private premultiplied RGBA8 output. Unsupported alpha/format/resource
requirements reject. Shader and allocation candidates use validation error scopes
before publication. No shader source is rewritten.

`prepare_blend` independently exposes original ImageBlend, including linear-RGB
policy; `prepare_arithmetic` exposes the original four coefficients and optional
premultiplied enforcement. Binary output dimensions are the per-axis maximum of
the two original input extents, with original transparent out-of-range reads.
Unlike the mutable managed destination, the Rust output is newly owned and has
no prior destination extent to include. Unary output matches its input.

`encode(encoder, prepared)` appends one original 16x16 compute pass without queue
submission, polling, CPU pixel work, or readback. Prepared handles retain inputs,
output, pipelines, and bindings through caller encoding/submission. Resources are
retired by wgpu ownership; no explicit destruction invalidates earlier users.
`output()` exposes the actual target, `inputs()` exposes actual source owners,
and `metrics()` reports actual parameter bytes, output storage, and workgroups.
Re-encoding rereads current source texture contents; immutable parameters do not
claim external pixels are immutable.

Matrix/luma operations deliberately route to the independent Texture renderer
and return `UnsupportedExecution` from this compute-only engine. The subsequent retained integration below connects those descriptors to draw
preparation while preserving the separate compute-only API boundary.
Blend operations require the caller's actual full-size premultiplied capture of
the constant foreground brush, matching original `SKCanvas.RenderColorFilter`;
the operation's byte color remains in the plan. The engine does not substitute
a one-texel texture or CPU image fill. Lerp takes its actual two child captures
and uses original arithmetic coefficients `[0, weight, 1-weight, 0]` with
premultiplied enforcement. The subsequent retained executor supplies those matrix/constant-capture draws
and executes the complete dependency plan.

## Package integration

The GPU module directly includes these unchanged ProGPU-owned sources. The crate
package allowlist must retain all five (root owns Cargo edits):

- `/src/ProGPU.Compute/Shaders/ColorTable.wgsl`
- `/src/ProGPU.Compute/Shaders/NonlinearColorFilter.wgsl`
- `/src/ProGPU.Compute/Shaders/OverdrawColorFilter.wgsl`
- `/src/ProGPU.Compute/Shaders/ImageBlend.wgsl`
- `/src/ProGPU.Compute/Shaders/ArithmeticComposite.wgsl`

No dependency changes are required. All CPU descriptors and evaluators build
independently of the GPU feature by construction; this statement describes the
module boundary and is not a claim of a compilation result.

## Remaining independent work

- Runtime-effect filter nodes belong to the separate programmable-effect stack;
  this module does not add a placeholder evaluator or silently pass them through.
- Compatible fragment/SIMD/scalar image execution routes remain explicit rejects
  until complete equivalent implementations are available and qualified.
- Differential factory/CPU/GPU/source pixels, immutable lifetime/resource-domain
  controls, shader validation, browser execution, packaging, and cold/warm
  performance comparisons remain deferred to the final validation phase.

The architecture/research boundary reuses `DESIGN.md`: preserve owned ProGPU
algorithms, separate immutable CPU generations from same-device GPU resources,
and retain original shaders. No foreign implementation was read or copied.

## Exact original source SHA-256

| Original source | SHA-256 |
|---|---|
| `src/SkiaSharp/SKColorFilter.Overloads.cs` | `89145ff53f47397645ec74c8c30686f5243c40716e8895876264c5748eeb6dec` |
| `src/SkiaSharp/SKPaint.cs` | `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff` |
| `src/SkiaSharp/SKHighContrastConfig.cs` | `e9f8179ea8df22e3029d80d92181e82aa4ffb66da92099ea78acaab782ceb178` |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/ProGPU.Scene/ImageEffectParams.cs` | `833656e3f5567bc77b18b656278c1a273bdd72a2d2b1182ec96afe7a64eaea8e` |
| `src/ProGPU.Backend/RenderPipelineCache.cs` | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| `src/ProGPU.Backend/Shaders/Texture.wgsl` | `5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51` |
| `src/ProGPU.Compute/ComputeAccelerator.cs` | `7e3cd2be4a1f02cfe44f9b784759cf5ae5677f089c5f7c5c2db7e2a4b28c8ea1` |
| `src/ProGPU.Compute/Shaders/ColorTable.wgsl` | `c71eae9f4d6a988510340fa805de9e36b984312a2a9bf12e9e88796341c5c15b` |
| `src/ProGPU.Compute/Shaders/NonlinearColorFilter.wgsl` | `16468fca216bc177f71b07ad3b277a8c289e6f9fd66f5419469adf5dec6bc47d` |
| `src/ProGPU.Compute/Shaders/OverdrawColorFilter.wgsl` | `95b2fe90d46cb606a97167c0fa7a1a4977ea6ce1af708c36de08a2d772923655` |
| `src/ProGPU.Compute/Shaders/ImageBlend.wgsl` | `9ad85488633177d5e5473fa3290f117c8e321c340acea5e9f40b8746daec7c55` |
| `src/ProGPU.Compute/Shaders/ArithmeticComposite.wgsl` | `776b9d9ab31222d1c3cb50f1d5a6528d48ab89fb7aeed0355439e6e9485178eb` |
| `src/ProGPU.Native/include/progpu_native.h` | `bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp` | `3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image_resources.cpp` | `3d4dcfd1105259210d9482160a0a7dbde5f9dca4d186efa84847eb4adb896859` |


## Retained renderer and graph execution increment

`ImageColorDraw { draw, matrix }` retains the direct native contract independently
of the image source/sampler key. `Scene::draw_image_color` compiles exact image
vertices and matrix indices; `PreparedImages::draw_color` selects original
`fs_main_color_matrix_unmasked` or `fs_main_color_matrix_chain`. The uniform is
bound in group two when unmasked, group three after the actual mask group when
masked. The unchanged shader returns straight RGB: fixed-function color blending
uses SrcAlpha/OneMinusSrcAlpha and alpha uses One/OneMinusSrcAlpha, preserving the
native producer's fixed-function multiplication and UNORM rounding boundary.

`ColorFilteredImageDraw { draw, filter, maximum_steps }` and
`Scene::draw_color_filtered_image` retain full-source evaluation before final
image geometry, source rectangle, opacity, sampler and independent masks/clips.
The whole plan is bounded to 16,384 operations and accounted against the existing
combined scene texture/pass budget before allocation. Actual device identity,
source dimensions, compute/render/binding capabilities and compiled final mesh
bytes/finiteness are admitted before candidate publication. Intermediate textures
are original RGBA8Unorm, at the full original source dimensions and DPI one.
Straight/R8 sources first enter a canonical GPU image capture; already
premultiplied RGB sources retain their actual sampled generation directly.

The executor preserves child-before-parent order with distinct owned targets:

- Matrix and luma use unchanged `ImageEffect.wgsl`, preserving its neutral-effect
  arithmetic, original 0.00001 alpha threshold and luma-before-matrix policy.
  Luma and matrix remain separate graph nodes; there is no cross-node fusion.
- Constant blend foregrounds are actual full-size analytic solid rectangles from
  canonical `Vector.wgsl`, painted over a transparent target. No Clear-as-color
  substitution, one-texel inference or CPU pixel fill is used.
- Table, HSLA, high contrast, overdraw, blend and lerp reuse the five original
  compute shaders and immutable prepared resources from the initial increment.
- Requested final mipmaps use the original GPU `MipGenerator.wgsl`, following a
  GPU level-zero copy. Each replay regenerates them after the completed graph.
  Final draw sampling/addressing, patches, transform and projective geometry are
  compiled against the actual premultiplied output through the ordinary image
  writer; final outer opacity and coverage are applied only by the consumer.

`Renderer::configure_color_filters` supplies typed compute-route configuration.
`prepare_color_filters` retains initialized device domains on candidate errors;
prepared source handles, immutable filter generations, parameters, pipelines and
outputs own their dependencies independently of renderer caches. Error scopes
surround pipeline/resource candidates. `encode_sources` appends the ordered
render/compute/mip passes to the caller encoder before final consumers, without
queue submission, readback, source callbacks or explicit resource destruction.
Root integration retains painter order, masks/scissors, ordinary layers, bounded
advanced blends, target Clear and source-AA group ownership.

`PreparedScene::color_filter_metrics` reports actual final draws, original
operations, capture/render/compute/mip passes, intermediate storage and immutable
geometry/parameter bytes. Nested layer/AA aggregates use checked `merge`.
The shared `examples/support/color_filter_scene.rs` records sixteen authored
panels spanning direct native matrix/luma, tables, HSLA, high contrast, gamma,
blend, compose, lerp and overdraw with mask chains, layers, fractional AA clips,
mips and later painter-order geometry. The gallery has not been executed.

This increment closes the earlier retained matrix/constant-capture/graph wiring
gap. Runtime-effect filter nodes and the deferred cross-implementation, browser,
packaging and performance qualification remain independent unfinished work.
No checks/tests or build/benchmark artifacts were generated.

| Additional original source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Backend/progpu_native_image_execution.cpp` | `09135c8ead728a45d4e2a65ab9196b2a2a3fbff535b65d8f01816ef37c6b547e` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_mask_chain_pipeline.cpp` | `adb675667452729483eba70f80c4f1d1f5c5c3ddb306dc5aeec438c06b5712d1` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_draw_execution.cpp` | `4dd136cd49ec8dd28e21e7541ba74db83cc6a7b9fe0acd8a7aa8f81a45e7e8b5` |
| `src/ProGPU.Scene/Shaders/ImageEffect.wgsl` | `994eb88551a7c7db875061fb7b372e5c68b976591faa65c0a80762f0726b45f8` |
| `src/ProGPU.Backend/Shaders/MipGenerator.wgsl` | `872a81b4bb8c49834f291ea37d6b606f9ecdd27c0213dca5b8351d515d60203c` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
