# Native retained WPF bytecode effects

The native MIL decoder connects `pixel_shader`, `implicit_input_brush` and
`shader_effect` to a real retained fragment pass. This is a bounded first family,
not complete WPF ShaderEffect or application qualification. The existing managed
`WpfShaderEffectExtensionPipeline` consumes explicitly supplied WGSL and remains
separate; its registry is not a translator for original WPF bytecode.

## Original source and scene contract

The decoder owns original pixel-shader bytes and constant-register values.
Resource changes participate in the visual cache dependency walk. Original MIL
resource numbers and packed array layouts are unchanged. The scene adds required
resource kind 19 with a version-1, 544-byte descriptor and auxiliary original
bytecode. No existing descriptor grows. Old readers reject the unknown required
resource; the new reader validates the whole program before accepting it.
Generated managed bindings describe that wire shape, not a rendering fallback.

The original family is `ps_2_0`, at most 64 KiB and 512 instructions, with one
declared 2D implicit-input sampler, `t0`, 12 temporary registers, 32 float constant
registers and fully written `oC0`. Executable instructions are MOV, ADD, SUB, MUL,
MAD, DP3, DP4, MIN, MAX, LRP, FRC, CRS, ABS, CMP and TEXLD. NOP, bounded comments, DCL
and finite DEF are parsed. Swizzles, write masks, NEG/ABS/ABSNEG source modifiers
and SAT are explicit. Reading an unwritten component fails. Unknown opcodes,
relative registers, predicates, unsupported modifiers, malformed lengths,
trailing tokens and incomplete output fail. Constant updates remain uniforms.

An additive [bounded `ps_3_0` family](native-shader-model-three.md) uses that same
resource and instruction contract, with one actual declared `TEXCOORD0 v#.xy`
input instead of the shader-model-2 texture register. It does not admit the full
shader-model-3 register file, instruction set or additional source semantics.

The [bounded arithmetic family](native-shader-arithmetic.md) adds DP2ADD, LOG,
model-specific SINCOS and explicitly admitted immutable-DEF NRM operands. Its
367 independent controls are registered with the original 110 translation
controls. Runtime-valued NRM, RCP, RSQ, EXP and POW remain rejected; this does not
claim complete arithmetic or floating-point equivalence.

The [original cross-product family](native-shader-cross-product.md) adds `CRS`
with selected-lane dependencies and the original destination, alias and swizzle
restrictions. Its 406 controls supplement, rather than replace, those original
translation/arithmetic controls; paired GPU qualification is still required.

Version 1 admits one untransformed implicit-input brush at opacity one. An
explicit [version-2 owned ImageBrush sampler](native-shader-image-samplers.md)
adds static, same-channel owned-bitmap capture without changing version 1.
[Version-3 UV derivatives](native-shader-uv-derivatives.md) retain the original
selected float register and populate it from the actual native capture basis.
Integer and Boolean registers, additional/external samplers,
nonzero padding, software-only mode, brush animation and transformed input remain
unsupported. The effect requires explicit positive source bounds, a positive
axis-aligned source basis and a complete integral physical capture. Clipped,
fractional, backdrop and cache-content captures fail preflight. These gates need
actual source sampling contracts, not rounded UVs or CPU resampling.

## Owned execution

Both native WebGPU providers share the clean-room decoder, canonical
`WpfBytecodeEffect.wgsl` template, fragment pipeline and isolated-layer pass.
Input/output retain premultiplied values. Texture coordinates clamp to the actual
capture, excluding spare allocation pixels. Nearest and linear sampling are
explicit. There is no CPU shader evaluator or renderer fallback.

The engine caches at most 64 exact original-byte/sampler programs. Bindings own
their constants buffer and bind group and retain their program independently.
Only cache-only programs can be evicted. Compilation/layer budgets remain
bounded; new buffers participate in native GPU memory inventory. Final source
clip/opacity composition stays outside the effect. Custom effects are not
declared identity-input effects for hit testing. Diagnostics distinguish custom
shader effects from Gaussian blur.

## Research and provenance

Implementation is local; no foreign implementation was copied. Original ProGPU
managed parameters and native isolated-layer ownership at
`48a49afeb993214c0c40c6908e9896ef0b5dec97` provide the lifecycle baseline.
Microsoft's [instruction-token](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/instruction-token),
[destination-token](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/destination-parameter-token)
and [source-token](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/source-parameter-token)
specifications define bytecode, not an imported compiler. The actual LibreWPF
`ShaderEffect` producer supplies register-array and sampler-mode contracts.

[SkSL](https://docs.skia.org/docs/user/sksl/) and
[Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/custom-effects)
inform immutable programs versus live parameters; neither implementations nor
color conversions were adopted.
[WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) and
[Vello](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md) inform retained
scene/task separation, not a replacement scene format.
[Win2D](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/custom-effects)
does not justify replacing bytecode with a named effect.
[Parley](https://github.com/linebender/parley) and
[HarfBuzz](https://harfbuzz.github.io/harfbuzz-hb-shape.html) remain text concerns;
this change does not reshape text. Host-owned cache lifetime is explicit, not
assumed from an automatic [wgpu cache](https://github.com/gfx-rs/wgpu/issues/7716).

## Remaining qualification

The implementation checkpoint has not run a renderer build or GPU test. The
bounded [CPU translator controls](native-shader-model-three.md#authored-controls-and-qualification)
pass independently; the complete contract generator, MIL ledger and 93-field
GPU ownership guard pass. Those checks do not qualify pixels.

Authored MIL controls exercise original odd-sized Int16 register arrays, exact
bytecode retention, sampler/constant updates, atomic invalid-resource rejection,
raw descriptor rejection and the custom-effect input gate. The shared GPU
fixture is wired into the existing wgpu-native Direct2D and Dawn provider tests:
four original ps_2_0/ps_3_0 programs/constant generations, cold/warm/independent
replays, all 4,096 RGBA pixels, exact source capture/final clip, one submission
and explicit effect pass/uniform-upload counts. Its oracle is exact white times
an original constant over opaque black; it does not reuse translated output as
expected pixels. The existing picture-axis pixel/submission checks are preserved.
These native MIL and GPU fixtures have not been executed locally.

Required follow-up includes the authored native/GPU checks, additional original
Microsoft-bytecode pixel references, nested clips, retirement/budgets, both
providers, Windows package/NativeAOT and source applications. Broader ps_3_0
semantics/instructions, dynamic flow, additional samplers,
nonintegral/expanded captures and animated inputs remain open. No parity,
performance, desktop rendering or complete ShaderEffect claim is made.
