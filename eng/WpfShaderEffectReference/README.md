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

## Original ImageBrush sampler frames

An additive, separate `image-samplers.json` receipt keeps every original arithmetic
case unchanged and captures 14 original ImageBrush cases, each cold, warm and with
an independently recreated visual and bitmap. Four inputs match the original
ProGPU-owned sampler fixture at `b044a8cee25ea32be4842253e013381882c3d023`,
`src/ProGPU.Native/tests/progpu_native_shader_sampler_pixel_fixture.hpp`: source
bitmap DPI (144,192), brush opacity, repeated addressing, translation, independent
shader sampling and final source clip. Every output byte has an independent stripe
expectation; the source bitmap and complete original brush input are saved too.

The other ten inputs use a 400x200 bitmap with independent source DPI axes,
absolute/relative equivalent viewboxes, Stretch=None in a 100x100 effect input,
nearest/bilinear shader sampling, and translated FlipX tiles. The integral
(192,384)-DPI crop retains a 100x20-DIP source frame, so its four plain cases
must letterbox to exactly 20 centered rows with quarter-opacity red/green pixels.
Fractional DPI (123.456789012345,183.456789012345) and mirrored cases retain
every original pixel without inventing an idealized float-to-DIP oracle. Five
absolute/relative pairs must match completely, preserve final clipping/opaque
alpha, and have actual opacity-bearing output rather than blank or unchanged
white. Those pairs are not labeled independent color-oracle cases.

Inputs use public original `BitmapSource.Width/Height` for absolute viewboxes,
not a replacement double DPI formula. PixelShader sampler registration and actual
ImageBrush realization remain Microsoft WPF. This adds no production renderer,
native shader, CPU fallback or source admission. ARM64 executes all inputs as
the existing explicit unavailable-software control, reporting zero qualified
sampler shader cases. All previous deadlines, color assertions and native/package
gates remain; a reference mismatch fails rather than removing its case.

After the implementation commit, the small original-only Windows-targeted project
compiled against cached reference assemblies in 1.51 seconds with zero warnings
or errors. This host used installed SDK 11 preview with target `net10.0-windows`;
it installed no SDK and loaded no WPF renderer or ProGPU native runtime. Actual
Windows source capture remains the hosted workflow's responsibility. The new
sampler phase shares the original process-wide 60-second stopwatch rather than
resetting the capture deadline.

The first hosted original x64 run `37010052606` (job `110847365474`) passed the
25 arithmetic inputs but contradicted the existing native fixture's hard stripe
expectation. `sampler-native-0` returned a linear red/green ramp at x=16..31:
BGRA `(0,4,124,255)` through `(0,124,4,255)`, despite nearest options on the effect
visual and its ImageBrush. These failed original PNG/raw/input artifacts are
retained; no native parity claim or passing sampler receipt follows from them.

Two additive parent-Nearest controls isolate incoming render-state inheritance
from options on the effect visual itself, bringing the inventory to 16 inputs,
48 replays and 10 independent color controls. Pixel failures now accumulate so
all original inputs and equivalence pairs are observed in one bounded run. Any
failure writes only `image-samplers.failed.json`, reports zero qualified cases
and fails the workflow after capture. None of the pixel assertions, original
cases or capability checks is weakened. The producer behavior being investigated
is documented in the original WPF `ShaderEffect.cpp` secondary-input capture:
its fresh capture context inherits the incoming render state. This observation
does not establish the effective render-option ordering by itself.

The complete native ARM64 original capture `37010911430`, job `110850135785`,
established a distinct unavailable-software outcome for all 16 ImageBrush inputs:
their shader contributes no color, leaving every BGRA pixel `(0,0,0,255)` over
the original black background. The old 25 implicit-input controls still produce
their required clipped white input. Their checks stay unchanged; the new sampler
negative control checks every byte against the observed empty contribution and
continues to report zero qualified shader cases. All input/PNG/raw artifacts and
the failed initial white-input assumption remain recorded. This architecture's
negative outcome must never become a product pixel expectation or replace the
positive x64 or native ARM64 GPU gates.
