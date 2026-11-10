# Original runtime-effect port (2026-11-10; unvalidated)

Ported the complete original declaration/reflection and binding owner stack into
`runtime_effect`: all 18 numeric spellings, tightly packed arrays/matrices,
three child kinds, first-match duplicate names, exact source retention, lexical
main admission, top-level statement/comment traversal, original diagnostics,
zero/null defaults, native-endian uniform values, copy-on-write snapshots,
typed builders, independent instance generations and nine-component local matrix.
Child setters preserve the original acceptance of actual kinds independently of
declared kinds. Rust Drop retires only actual references; snapshots outlive builders.

The original `SKRuntimeEffectParser` does **not** compile SkSL function bodies or
produce executable IR. No foreign compiler or invented runtime is included.
`RuntimeEffectProgram::supports_language_execution()` explicitly returns false.
Actual instances now enter `FilterShaderSource`, `ColorFilterKind` and
`FilterBlender`; shader/blender execution retains the original unsupported gate.
Shader composition rejects runtime blenders at its factory boundary. The original
runtime color filter has distinct existing behavior: GPU `RenderColorFilter`
preserves the input (then the independent crop), while single-color `Apply`
reaches default Clear. Both are retained without claiming source-language execution.

Bounded iterative device ownership traverses every actual image, picture,
shader/filter/blender child, including ignored identity operations. Shared owners
are visited once. Renderer image/color-filter admission checks these owners before
GPU mutation. The owner walk is capped at 256 depth / 16,384 pending or visited
objects; excess is rejected atomically. Parsing is linear in source length plus
declarations, uniform lookup retains original linear first-match behavior, byte
copies use safe slice primitives and RGBA8 uniform conversion uses four SIMD
division lanes. Stateful lexical traversal is scalar; there is no CPU pixel loop.

Only original ProGPU-owned sources were consulted; existing DESIGN.md ownership
and device-domain research remains applicable. UTF-8 Rust source excludes lone
UTF-16 surrogates; BMP identifier categories reuse the owned Unicode tables and
supplementary scalar identifiers retain the original char-overload rejection.
Unicode runtime-version and culture-specific integer-sign behavior remain
differential gates. No build, compiler, tests, shader execution, browser, native
comparison or benchmark ran, per the user's implementation-first instruction.
Only touched-file formatting ran; no build or trace artifacts were produced.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKRuntimeEffect.cs` | `06bcaca91ec4fcfe744eca574d2aa9c412d5d646635e9bb3ff33745bcc25d23a` |
| `src/SkiaSharp/SKBlender.cs` | `ee11d400a60e2e583cb0fb59c606018f0fc9c7452f5baf0c8050d9a74e35d55c` |
| `src/SkiaSharp/SKPaint.cs` | `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff` |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/SkiaSharp/SKMatrix.cs` | `a1a0fb70cb138510b7407bf41de5b3d88fb6e3f34bde01dd948d1128268b54e3` |
| `src/SkiaSharp/SKShader.Overloads.cs` | `36b1c4795b6179732a1e720bb710fd2f1c3cecff3a5e38117cdb2e85c02296cb` |
| `src/SkiaSharp/SKImageFilter.Overloads.cs` | `ca8cb11c6e95cbb11b8dcd808130497972490a63981bb25719357fd59e642b94` |
| `src/SkiaSharp/SKColorFilter.Overloads.cs` | `89145ff53f47397645ec74c8c30686f5243c40716e8895876264c5748eeb6dec` |
