# Programmable shader-effect port (2026-11-09; unvalidated)

The user mandates porting all implementation before validation. No compiler,
Clippy, test, Naga, browser, source-differential, benchmark or profiling command
was run for this increment. Rustfmt is used only as source formatting. The sample
is authored and has not been executed. Original qualification gates remain open.

## Original source provenance

All algorithms are direct ports of original ProGPU-owned sources. No external
renderer, shader translator or implementation source was incorporated.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect.cpp` | `015fea0221d637621d241505d4d1cfe4a9362f2a0af320da64fb9fe2994abd85` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect.hpp` | `e0b9552ea85d9b8db7165e77e6fb671cc00d17d8dd27f96cc104c412d9db6337` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect_resource.hpp` | `fda73197f0b2e6d1d0f1ea14d19824a30760fd1dc4a458f479da4645ac6e8467` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect_uniforms.hpp` | `53dbcd9e8d265e09792006e3527d28ab1bb8c37e3b4d56b299d101345069729e` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect_execution.cpp` | `ea694e6902505dda480c45e94539793934a5630b41130b010c7e54c9c764f080` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_capture_frame.hpp` | `c7ad3ceb49fa6c5a86c31c8a956094aa88ab47878def8a59a3f58497199bb206` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_sample_frame.hpp` | `6e80a73d4056ccb26ca626156490a3139fca94e4ec9eefe993b627d7a00e3b29` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_affine_frame.hpp` | `dbc3047321b3c73922e5f4f71c40315099479502de35843fca6544740fd556ed` |
| `src/ProGPU.Native/include/progpu_native.h` | `bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441` |
| `src/ProGPU.Scene/Shaders/WpfBytecodeEffect.wgsl` | `0b69f7629fd29563be7273f4b118f30e13b50e19701e5c9d83f58b175a93c529` |
| `src/ProGPU.Scene/Shaders/WpfBytecodeSampleEffect.wgsl` | `f409977760d9a63fd609e41811e05585fe6d4ad92c8969c73fa9dfc348536364` |
| `src/ProGPU.Scene/Shaders/WpfBytecodeAffineEffect.wgsl` | `baab72fd827e00263fd80a57873e99a1fb5eb71ddae20dd129d9bccb72d449af` |
| `src/ProGPU.Scene/ShaderEffectSourceCapture.cs` | `cd1aea0b7ace472a46f68dee9126f3280ddade8a1eb88871412f3468167bd7dd` |
| `src/ProGPU.Scene/EffectCaptureFrame.cs` | `557f747e6772917fdbf34c06b838f430734a71e86839e50a03de90c248c5e017` |
| `src/ProGPU.Scene/WpfShaderEffectParams.cs` | `e633f404baae8fd6e274029c795c01e05378708dbf8f093530fa57472c66f431` |
| `src/ProGPU.Native/tests/progpu_native_shader_effect_pixel_fixture.hpp` | `2442a69ce9dc77cd25d4d36b63ee0955144998ca4ca6287958f2b49bd049f3d7` |
| `src/ProGPU.Native/tests/progpu_native_shader_derivative_pixel_fixture.hpp` | `13c290afc55bb2e59a0f6776d6a9c94eece519cbf353caca47e22c6043504a24` |

## Connected production implementation

- `ShaderProgram` owns exact bytecode and a fully admitted translated body. The
  original 64-KiB/512-instruction ps_2_0/ps_3_0 decoder retains parameter flags,
  declarations, register read ports, defined-lane tracking, immediate bits,
  source modifiers, destination masks and complete END/output/sample admission.
  Original MOV/ADD/SUB/MAD/MUL, dot/min/max, finite LOG, LRP/FRC, matrix rows, CRS,
  ABS, compiler-proven NRM, exact DEF-coefficient SINCOS, TEXLD, CMP and DP2ADD
  branches are emitted in original order. Unsupported original numeric/resource
  forms reject; no pass-through registry replacement or CPU shader evaluator.
- The exact 544-byte descriptor and v1–v6 resource reader retain original source
  indices, revisions, constants, derivative register, reserved words, padding and
  distinct frame layouts. Import validates the complete candidate before output.
  An owning scene must still resolve earlier same-scene pictures and dependency
  generations; accepting wire bytes does not qualify arbitrary external textures.
- `CaptureFrame`, `SampleFrameRecord` and `AffineFrameRecord` retain the original
  power-of-two v4 admission and independent v5/v6 cofactor/inverse/residual math.
  Source floats publish each original multiply/add/divide separately. Signed
  origins, outward scale-space capture, final-device output, homogeneous W,
  mirrored cross terms, target projection and exact redundant wire bits remain
  separate. No aggregate-double reconstruction, epsilon admission or replacement
  of final sampling with a resized evaluated effect image.
- `SourceCapture` and `ManagedCaptureFrame` separately port the managed double
  source bounds and four independent padding values. Invalid/signed-zero metadata
  is preserved until admission. Original endpoint narrowing, scalar minimum-one
  sizing, explicit raster overrides, independent-axis physical origin, rebasing,
  projection extent, output edges/UVs and complete bitwise capture identity remain
  independent of native frame policy. `source_pixels_per_unit` consumes the actual
  supplied projection and normalized viewport, never inferred monitor DPI.
- `ShaderEffectEngine` lazily owns original bytecode pipelines with exact-byte,
  sampler-register, legacy/final/affine/masked and format keys. The original
  64-program cache evicts only cache-owned entries. Prepared/in-flight generations
  retain independent Arc ownership. Constants are copied per actual target into
  original 544/608/624-byte layouts; derivative overrides follow original
  constants and do not mutate the source effect.
- GPU sources retain actual device-bound premultiplied RGBA captures. Complete
  implicit input and independently captured sampler remain separate owners.
  Legacy preparation allocates an exact RGBA8 output; final-device preparation
  draws the original six-corner quad in the actual parent pass. Both use unchanged
  canonical wrappers with a validated bytecode body inserted at their original
  marker. Original default compiler options and premultiplied blending remain.
- `ShaderVectorMask` can be produced only through the original path-only
  `ClipPlan` factory. It retains the actual full-target R8 result, engine owner,
  target origin and target size. Arbitrary sampled alpha cannot be relabeled
  vector-source clipping. A dedicated private bind group exposes only the
  original consumed binding 1; the canonical shader performs the same integer
  coverage load once after shader evaluation.
- `PreparedShaderEffect::draw_owned` retains pipeline/resources independently of
  subsequent engine cache replacement. The renderer supplies its actual binary
  scissor; public engine drawing additionally checks engine identity. Encoding
  submits no legacy work itself, so callers place source evaluation before its
  consumer in original painter order. Source-vector mask production retains the
  existing clip producer's submission boundaries.

The shared `shader_effect_scene` authors 12 panels: four legacy captures and eight
actual-parent v5/v6 commands, with ps_2_0/ps_3_0 source fixture words, independent
sampler capture, constant/derivative output, fractional placement, shear,
reflection and typed curved vector coverage. It receives the actual target size;
it does not infer a target from panel bounds. Native/browser host selectors and
retained Scene/Renderer integration are owned by the root agent.

## Complexity and ownership

Bytecode decoding and emission are O(T) with bounded sequential register/defined-
lane state; declarations, read ports and writes depend on earlier instructions.
These are scalar structural operations, not per-pixel CPU processing. Frame
metadata has fixed arithmetic and no allocation. Shader pixel operations remain
GPU-parallel in the original production wrappers. Program-cache search is bounded
by 64 entries and exact bytes; no hash aliases a source generation. Stable
prepared replay allocates or uploads no parameters and keeps current source
handles live. Runtime mutation of borrowed GpuImage pixels remains producer-owned;
this API retains metadata and lifetime, not a secretly copied pixel generation.

The existing DESIGN.md architecture research continues to inform the same
boundaries: [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
for actual device/CPU ownership, [Skia canvas](https://skia.org/docs/user/api/skcanvas_overview/)
for independent matrix/clip/paint metadata, [WebRender](https://github.com/servo/webrender/wiki)
for immutable retained generations, [Vello](https://github.com/linebender/vello)
and [Parley](https://github.com/linebender/parley) for GPU work versus reusable CPU
text, and [HarfBuzz concepts](https://harfbuzz.github.io/shaping-concepts.html)/
[DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal)
for independent reusable text ownership. No foreign implementation is ported.

## Deferred scope and required qualification

This is the full admitted original native bytecode/frame engine, not a claim that
all managed ShaderEffect APIs or source-host semantics are finished. Complete
source visual/nested picture capture recording, dependency/revision transport,
managed arbitrary WGSL/multi-sampler parameter pipeline, cache-raster provider
factories, source input opacity/padding capture integration and automatic
source-host frame provenance still require connected ports. Existing unknown
bytecodes and source policies remain rejected exactly as in the original native
producer; no new source or Microsoft qualification is inferred.

All compilation/lint, parser/admission/body/wire/frame/constant byte comparisons,
original host float controls, exact full-format/sample/mask pixels, lifetime/
mutation/cancellation/device-loss checks, native/browser execution and complete
performance/allocator/provider/package/application qualification are deferred.
The three wrapper templates are not standalone static shader modules because
meaningful programs are created only after validated bytecode insertion. The
root-requested unchanged GroupBlend module was separately added to the static
module inventory; neither addition claims new successful shader validation.
