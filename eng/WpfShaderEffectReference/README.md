# Original WPF shader reference

This Windows-only, source-only executable references original Microsoft WPF,
never LibreWPF or a ProGPU runtime. It captures the committed matrix/cross-product
family through real `PixelShader`, `ShaderEffect`, `Point4D` constant registers and
`RenderTargetBitmap`, using explicit `SoftwareOnly` **ps_2_0** execution. The
[original render-mode contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.effects.shaderrendermode)
is a reference choice, not a product renderer fallback. Microsoft WPF software
rendering does not establish ps_3_0 hardware qualification, so that model is not
silently substituted or marked passed here.

The 25 cases contain two multiply controls, three cyclic cross products and all
five matrix shapes with identity/swapped vector components and positive/negated
inputs. Contrasting W coefficients distinguish three/four-component dots;
swapped input cases require actual source swizzle behavior. Every case performs
cold, warm and independent-visual replay. All 4,096 premultiplied BGRA pixels
must match exact independent colors, opaque final alpha and an external source
clip, without tolerance, skipped pixels or count-only comparison.

Each input's exact original tokens/constants is saved before execution. PNGs and
raw first-replay pixel bytes accompany the complete receipt, along with source
commit, process architecture, producer and original managed/native WPF hashes.
The original invalid-shader event fails the capture; output mismatch cannot be
treated as an omitted unsupported case. The bounded workflow runs x64 and ARM64,
uses 60-second internal/90-second process/8-minute job limits and never loads or
stages an unqualified ProGPU producer. Native provider, package/NativeAOT and
actual application gates remain separate.
