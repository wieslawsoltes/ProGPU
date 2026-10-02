# Original WPF shader reference

## Additive original padding companion

`ShaderPadding.cs` adds a separate `shader-padding.json` receipt without changing
the original 25 arithmetic cases, 28 ImageBrush cases / 84 replays or their 19
software arithmetic controls. Thirteen padding inputs execute 39 actual original
Microsoft WPF captures under the same shared 60-second stopwatch, unchanged
90-second process and 8-minute job limits. They use public/protected original
`ShaderEffect` padding, constant and sampler properties, actual protected visual
bitmap-scaling state, `DrawingVisual` and `RenderTargetBitmap`, not ProGPU or
LibreWPF renderer assemblies. Every original input and all three PNG/raw replays
use fresh `CreateNew` files. A pixel mismatch preserves the full new inventory in
`shader-padding.failed.json`, qualifies zero new cases and still fails execution.

Ten inputs correspond to the native fixture's admitted variants 0–8 and 11 at
`8a14271e42aa2ed6f884f98b6b556fa60f1725dd`,
`src/ProGPU.Native/tests/progpu_native_shader_padding_fixture.hpp`. The source
reference is independently authored: source content is a white 16×8 physical
rectangle at (16,16), final clipping is (14,12,28,20), and asymmetric padding
produces the complete physical frame (12,14,32,16), or (14,12,32,16) after shifting
its origin at equal extent. Constant output must paint the transparent border;
implicit input must remain at its original source position. UV and selected c0
derivative output normalize against that complete frame, including source DPI1/2,
not the original content, final clip or spare allocation. Original user c0 values
are deliberately non-derivative values, preserving selected-register precedence.
The owned two-texel ImageBrush retains independent 144/192 source DPI and fills
the complete expanded frame with strict red/green stripes, through actual nearest
visual state rather than inert attached properties on bare visuals.

Three further consecutive inputs mutate only padding on the same actual constant
effect and receiving visual: zero → asymmetric → zero. Complete original reset
pixels must match, and successful software expansion must visibly change the
border. A source mutation ordinal is diagnostic input metadata, not an invented
native resource-generation counter. Cold/retained, warm and newly constructed
independent visual/bitmap captures compare every one of 64×64 premultiplied BGRA
pixels, including alpha and final clipping, without tolerance.

The source contract was read at immutable LibreWPF `381194e1`:
`WpfGfx/core/resources/ShaderEffect.cpp:172–179` projects the four public doubles
to local float edges; `WpfGfx/core/uce/drawingcontext.cpp:4912–4923` expands the
original inner effect frame before isolation. The test retains each original
double's exact bits, including `2 + 2^-25`, without rewriting it to its float
projection. Its device-free expected-frame/color file has 38 independently
authored controls, not copied original/native renderer implementation.
This reference-only addition changes no rendering architecture; both native
providers retain their separately authored strict fixture and admission gates.

Native fractional-physical and cropped-frame rejection variants 9/10 are
deliberately not labeled supported source inputs. The original ARM64 software
unavailability gate remains explicit: all new inputs still execute, implicit-input
effects must preserve white input, ImageBrush effects must contribute no color,
and the receipt qualifies zero shader cases. New original Windows execution is
pending hosted CI. No original software result qualifies native hardware,
fractional/cropped source integration, package or application behavior.

Post-commit checks at `4928f3a6536dc25f629a43311d58f65de7616f29`:
the three changed C# files passed SDK Roslyn syntax parsing; a bounded CPU-only
executable compiled the two actual pure oracle files with warnings as errors and
passed all 19 unchanged sampler plus 31 padding controls (50 total). Workflow YAML
parsing, preservation of every original workflow line/deadline, unchanged original
sampler files and `git diff --check` also passed. These checks did not compile the
WPF project, load WPF/native providers or execute shader pixels. The temporary
driver is `/private/tmp/progpu-padding-oracle-check.z33L0NDS/Program.cs`; compilation
had a 30-second process bound and its pure arithmetic execution a 10-second bound.
Actual original x64/ARM64 capture and eventual exact-head whole-Build checks remain
required; no native producer is staged by this reference companion.

### Original software UV phase evidence

Reference run `37036089882` at `30d13f64554dcf38445b90a5338f7e69c48a3493`
captured all 13 inputs / 39 replays on both architectures. The ARM64 negative
control passed with zero qualified shaders. X64 failed only the three UV replays:
the original software shader emitted red 16 / green 0 at (14,14), whereas the
initial oracle incorrectly expected red 20 / green 8 from a half-pixel phase.
The immutable failed receipt is SHA-256
`e478aa6a6094c6be4a4c10bce3fd1ae8f1cf00beba53348797008b1a10a711fa`;
all three UV raw images are independently identical at SHA-256
`4253e64e4503710269fd0ae1017d12d5ffb5ad1b7d9cfa36cf57969df0585073`.
Downloaded evidence is preserved under
`/private/tmp/progpu-padding-original-evidence.xQ32Rpy0`.

This distinction follows the original software contract, not fitted capture
bounds: immutable `381194e1` `ShaderEffect.cpp:1136–1174` starts the inverse
normalized-frame evaluation at device (0,0), and
`fxjit/PixelShader/pshader.cpp:286–306` advances integer scan X/Y with integer lane
positions. Independently applying `(x - frame.Left) / frame.Width` and
`(y - frame.Top) / frame.Height` matches every BGRA byte in each saved 64×64 UV
image; a half-pixel offset mismatches 896 channels in each. Frame, source bounds,
clip, original doubles and shader input bytes are unchanged. Seven added pure
controls require integer-origin first/adjacent/middle/last values, retain clip
rejection and explicitly reject the half-pixel hypothesis.

Only the explicitly `SoftwareOnly` reference oracle uses this phase. The native
GPU pixel-center fixture is unchanged, and a successful software UV receipt does
not establish hardware/native UV equivalence or complete padding parity. A fresh
hosted original reference is still required after this correction; the prior
failed receipt remains failed.

Post-commit checks at `9c808c157d5b70bbe06e61b5059182e47ceea779` passed
all 19 unchanged sampler and 38 padding arithmetic controls (57 total). The same
bounded CPU-only driver verified both immutable receipt hashes and every raw
image hash, then compared all 1,277,952 saved BGRA bytes (13 cases × 3 replays ×
2 architectures) against the corrected software/explicit-negative expectations,
with zero differences. This re-reads saved evidence; it is not a new original WPF
execution and does not relabel the failed producer as passed. Both changed C#
files passed syntax parsing, workflow YAML parsed, and its only guard change
requires seven additional controls; original main/sampler source is unchanged.
Whitespace checks passed. No product renderer, native fixture or runtime changed.

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
case unchanged and captures 24 original ImageBrush cases, each cold, warm and with
an independently recreated visual and bitmap. Eight inputs set actual protected
visual options, including two same-visual resets to inherited options after the
first capture. Four of those inputs match the original ProGPU-owned sampler
fixture at `b044a8cee25ea32be4842253e013381882c3d023`,
`src/ProGPU.Native/tests/progpu_native_shader_sampler_pixel_fixture.hpp`.
Six earlier inputs deliberately retain attached properties on bare visuals;
those do not set the actual visual options and keep their linear expectations.
Source bitmap DPI (144,192), brush opacity, addressing, translation, independent
shader sampling and final source clip all remain observable. Every output byte
in these fourteen small inputs has an independent nearest or linear expectation;
the source bitmap and complete original brush input are saved too.

The other ten inputs use a 400x200 bitmap with independent source DPI axes,
absolute/relative equivalent viewboxes, Stretch=None in a 100x100 effect input,
nearest/bilinear shader sampling, and translated FlipX tiles. The integral
(192,384)-DPI viewbox retains a 100x20-DIP mapping within a full 200x50-DIP
source image. Its four plain cases must preserve all 50 source rows after mapping,
with quarter-opacity red/green pixels rather than clipping to the viewbox.
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

The completed x64 diagnostic receipt from the same run has SHA256
`17f013af6e4936e2f36bbc753635712a5bbc088e41d9cfcf8ed43bdc98d6a356`.
Both parent-Nearest cases are byte-identical to their effect-visual counterparts;
the evidence therefore does **not** support an incoming-options-only product fix.
The original software pixel expectation now uses an independent two-texel linear
interpolation formula, pixel centers, clamp/repeat addressing and exact UNORM
rounding. It does not replace original inputs, embed captured pixel arrays or
introduce a tolerance. Whether hardware capture has the same realization policy
remains a distinct requirement before changing native/GPU defaults.

The integral source viewbox also selects a mapping, not an image crop for
TileMode.None. Its Stretch=None mapping places the 100x20 viewbox at (0,40) in
the 100x100 input, subtracting source origin (50,10). The complete 200x50-DIP
image thus occupies input y=30..80, or final y=40..90 after the visual origin;
every observed colored row is 40..89, not the initially predicted50..69.
The independent expected frame now preserves that full source overflow and
checks every pixel. All original input cases, failed receipts and exact
absolute/relative comparisons are retained. These observations expose missing
source/native qualification rather than establish product parity.

## Actual visual options versus attached properties

Original `RenderOptions.cs` registers the BitmapScalingMode attached property
without a changed callback for bare visuals. Original `UIElement.cs` overrides
its metadata and propagates into `Visual.VisualBitmapScalingMode`; `Visual.cs`
serializes that separate protected state into MIL. DrawingGroup has its own
property serialization. Consequently, the earlier bare DrawingVisual/ContainerVisual
inputs never emitted nearest options. Their linear output does not imply that
the native renderer must ignore valid nearest options or change its default.

The additional eight controls use small derived DrawingVisual/ContainerVisual
types to set and read the original protected property directly, without private
reflection or foreign implementation. Four set nearest on the receiving effect
visual; two inherit from a nearest parent; two capture that same nearest parent
and receiving visual, then reset the already-captured receiving visual to
Unspecified before its next capture. Independent visual replay uses the final
inherited state too. The nearest oracle remains the original strict stripe
formula. The first sixteen inputs, their actual unspecified visual fields,
independent linear/viewbox formulas, and historical failures remain intact.
This gives 24 sampler cases, 72 replays, 18 independent color cases and five
complete-pixel equivalence pairs, still within the shared 60-second deadline.

The exact source `5e13e5863053f9e5836fda323fdd52be6fe8a70b` completed hosted
workflow `37013264907` successfully on both Windows architectures. The x64 job
`110857864291` qualified all 24 sampler inputs and 72 replays, with all eighteen
independent color controls and five complete-pixel pairs passing. Its sampler
receipt SHA256 is
`d90dbd21d70869d1b42da5145e89e976c365d0134d015d38335f7efc151821b0`.
The ARM64 job `110857864140` passed the unavailable-software controls with zero
qualified sampler shaders; its receipt SHA256 is
`14bf8daed4e49f07f8734976287a22c2184d4a05026d1424265ba297c559517c`.
Both receipts retain the original PresentationCore identity, actual visual-field
state, all pixels and independent replays. The unchanged 25 arithmetic inputs
also passed their architecture-specific controls. This establishes the original
reference, not a native renderer, source-host or package qualification.

## Two-axis mirrored source neighborhoods

Four additive inputs use an original two-by-two red/green checkerboard at
144/192 DPI, the unchanged 32x24 receiving bounds and 16x16 external clip,
half-width viewport, half opacity and Bilinear shader sampler. FlipX, FlipY,
FlipXY and translated FlipX address each original source neighbor before
bilinear interpolation; the reference does not enlarge a clamped tile first.
The independent expected colors retain original software pixel-center mapping,
fixed-point coefficients and periodic/mirrored texel indices on both axes.
Every output byte, opaque alpha,
cold/warm/independent replay and the original shared deadline remains checked.

The first twenty-four sampler inputs and all twenty-five arithmetic inputs are
unchanged. The new complete inventory is 28 sampler inputs, 84 replays,
22 independent color controls and five complete-pixel equivalence pairs.
ARM64 still executes every input only as an unavailable-software control with
zero qualified shaders. These new mirror observations are pending actual Windows
execution; native-provider, source-host and package qualification remain separate.

The first mirror capture at `ce17d4669a00bf66165eb91d7ed3c261cdc5466d`,
workflow `37016244093`, x64 job `110867693904`, rejected the ideal continuous
interpolation oracle. All first twenty-four pixel arrays were byte-identical to
the earlier successful reference. The complete failed sampler receipt has SHA256
`cecfa04c5df9952310b728036ff3b32d1cd24970667535e72cdc9217d9b0e24e`;
all four new cases/replays and their raw/PNG inputs remain retained. No tolerance,
removed input or passing receipt followed from that failure.

Read-only original source at LibreWPF `381194e1` explains the software boundary:
`WpfGfx/core/common/BaseMatrix.cpp` applies device/texel center mapping;
`core/sw/swlib/bilinearspan.cpp` quantizes the inverse affine coefficients to
16.16, retains the top eight fractional bits, independently addresses four source
neighbors and rounds their single weighted byte sum. Its later
`CConstantAlphaSpan` uses independently rounded 16.16 alpha on those bytes.
`scanpipelinerender.cpp` emits brush bytes before `renderingbuilder.cpp` appends
that effect. `processorfeatures.cpp` and `swrast.cpp` select this scalar image
path on x64; the separate SSE2-for-effects capability does not select x86 image
interpolation. These observable arithmetic contracts informed the independent
`SoftwareSamplerOracle`; no original implementation was copied.

For the recorded 2x2 image mapped to 16x24, the inverse coefficients are
8192/65536 and 5461/65536; center offsets are -28672/65536 and -30037/65536.
TranslateX=8 shifts the original X frame exactly. Fractions truncate to 1/256,
the four-color weighted sum rounds once to a byte, then half-opacity rounds
that byte separately. The resulting independent calculation matches all 65,536
captured bytes. Nineteen arithmetic controls retain negative-coordinate floor,
mirror periods, fractional truncation, positive/negative coefficient half ties,
byte and alpha half-up rounding, and translated-coordinate identity.

This corrects only the explicit original SoftwareOnly reference. It does not
replace native hardware floating-point interpolation, change ProGPU defaults or
establish that original hardware has the same rounding. The original four native
mirror controls retain their independent floating-point image-address contract;
Windows hardware, source-host and package parity still need their own evidence.

After the software-oracle implementation commit, a dependency-free net10.0 host
linked the actual committed C# oracle and passed all nineteen arithmetic controls
plus every byte of all four immutable captured images. It loaded no WPF or native
renderer. The original-only Windows-targeted project then compiled in 0.96 seconds
with zero warnings/errors. A fresh hosted original capture remains required.
