# Original WPF shader reference

This Windows-only, source-only executable references original Microsoft WPF,
never LibreWPF or a ProGPU runtime. It captures the committed matrix/cross-product
family through real `PixelShader`, `ShaderEffect`, `Point4D` constant registers and
`RenderTargetBitmap`, using explicit `SoftwareOnly` **ps_2_0** execution. The
[original render-mode contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.effects.shaderrendermode)
is a reference choice, not a product renderer fallback. Microsoft WPF software
rendering does not establish ps_3_0 hardware qualification, so that model is not
silently substituted or marked passed here.

The x64 shader-pixel reference's 25 cases contain two multiply controls, three cyclic cross products and all
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

## Native ARM64 original-source limitation

The first strict source run `37002069190` passed all 25 cases on x64 but failed
the native ARM64 half-intensity control: original WPF returned white, not gray.
Diagnostic run `37002687162` at `4092a1058` preserved that failure and confirmed
original .NET 10.0.12's public
[`IsPixelShaderVersionSupportedInSoftware(2, 0)`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.rendercapability.ispixelshaderversionsupportedinsoftware)
returns **false** in the actual ARM64 process (SSE2 false), despite hardware
shader support and rendering tier 2. `RenderTargetBitmap` does not become a
hardware capture merely because that hardware capability exists.

The ARM64 job is therefore explicitly named `unsupported-software-control`.
It must prove the actual native architecture and missing software capability,
then execute all the same 25 original inputs and 75 replays. Every pixel must
retain the unmodified clipped white input, and 24 cases must fail the unchanged
shader-color oracle. Its receipt reports **zero qualified shader cases**. It
cannot replace the positive x64 reference or admit an ARM64 product rendering
failure; if original software support becomes available, the negative control
fails until its contract is intentionally updated. No existing ProGPU native
ARM64, provider, package or application assertion is removed or relaxed.

The paired native GPU fixture retains every original case and now exercises all
40 matrix model/swizzle/sign combinations against the real shader colors and
alpha on every admitted provider/platform. ARM64 must produce those colors, not
copy Microsoft's unsupported software-source behavior. x64 original pixels are
the independent reference; native ARM64 shader execution remains a distinct gate.

Implementation `f34c29c43` preceded the bounded postcommit check. The source-only
Windows-targeted project compiled from the existing cached reference pack in
1.42 seconds, with zero warnings/errors and no ProGPU renderer dependency.
Windows execution is delegated to the hosted reference workflow; this local
compile does not constitute a reference capture or pixel qualification.
