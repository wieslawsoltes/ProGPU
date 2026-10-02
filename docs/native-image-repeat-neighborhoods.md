# Full-source repeated bitmap neighborhoods

Native MIL's exact full-source Linear ImageBrush family now records an ordinary
owned image with existing extended-source Repeat/Mirror addressing. It no longer
first enlarges a source-clamped bitmap into a viewport-sized page, then filters
that page's repeats. Those two operations have different texel neighborhoods at
seams, even for a two-pixel source.

Admission requires an owned bitmap, complete original viewbox, exact full-viewport
mapping and positive-axis brush mapping. Stretch.Fill establishes that mapping
semantically; other stretches require exact computed extent and origin equality.
Do not test Fill through cancellation of a source-DPI scale product: ARM fused
arithmetic can preserve a double residue although the full-source Fill contract
still maps exactly onto its viewport. No epsilon or altered general transform is
used to admit this identity.
The world/paint transform and original clips remain on the existing image path.
Tile, FlipX, FlipY and FlipXY select the existing independent U/V address flags;
negative source coordinates retain their original periodic neighborhood.
There is no new shader, CPU renderer, readback, public ABI or resource lifetime.
The bitmap bytes stay in the original scene-owned image; opacity belongs to the
one paint, and a ShaderEffect sampler's outer immutable picture remains retained
by its original owner and generation.

Nearest, Fant, cropped/padded, vector/external, per-point-guideline and non-axis
brush captures retain their original normalized-page path and cache keys. This
bounded admission does not claim general repeated-capture equivalence.
Source capture/cache retirement and same-scene revision rejection are unchanged.
Setup is fixed-work O(1) per paint; sampling uses the existing four-neighbor GPU
Linear implementation and engine-owned addressed sampler cache.

## Independent contract and implementation provenance

The successful original Microsoft WPF SoftwareOnly reference workflow
`37013264907`, exact `5e13e5863053f9e5836fda323fdd52be6fe8a70b`, retained
24 cases and 72 replays on x64. Its `image-samplers.json` SHA-256 is
`d90dbd21d70869d1b42da5145e89e976c365d0134d015d38335f7efc151821b0`.
Cases `sampler-native-1/2/3` preserve original Linear realization (their attached
Nearest option on a bare DrawingVisual does not set the serialized field),
repetition, translation, alpha and original red/green or blue/green pixels.
The later actual-visual-field Nearest controls remain distinct and unchanged.
This source receipt is not original hardware or native-provider qualification.

Read-only original WPF ImageBrush realization research distinguishes direct
full-content image addressing from cropped/padded intermediate tiles. That
observable distinction informed this bounded predicate, not copied implementation
text. Original ProGPU implementation is authoritative: the existing
`Direct2D/progpu_native_direct2d_render_target.cpp` addressed bitmap-brush route
and `Scene/progpu_native_semantic_image_resources.cpp` retain the exact image
flags and lazy Repeat/Mirror sampler acquisition. Both native providers consume
that common scene contract; no provider-specific implementation was added.

Managed Scene already carries original texture U/V addressing through its
ordinary texture path. WPF's source repeated-tile command adapter remains a
separate paired integration requirement: individual source-clamped tile draws
cannot establish this neighborhood contract. The clean WPF ShaderEffect source
checkpoint is not repinned or changed by this native work. Do not claim complete
managed/native source parity until that route and the actual application gates
are connected and qualified.

## Controls and qualification

All thirteen existing shared shader sampler cases remain. Seven additional cases
exercise original repeated Linear red/green, translated and updated blue/green
inputs, plus two-axis FlipX/FlipY/FlipXY and translated mirror neighborhoods.
Each retains all-pixel RGBA comparison, cold/warm/independent-engine replay,
original deadlines, two cold/one warm submissions and effect ownership metrics
in both provider suites. Original channel resources mutate before every immutable
snapshot; every snapshot outlives channel retirement. Integer four-neighbor
reference arithmetic is independent of the renderer. Flip controls prove the
existing image-address contract, not yet a separately captured Windows case.

Raw source controls inspect every original image address bit, source extent,
negative translation and absence of an enlarged page. Cropped and padded cases
explicitly retain that page; nearest source cases retain their old outputs.
The original 144/192-DPI full-source cases and an additional asymmetric fractional
123.456789012345/183.456789012345-DPI raw case must select addressed original
texels independently of the compiler's multiply/add contraction policy.
Major implementation and fixtures are committed before bounded source checks.
Post-commit strict Clang syntax checks passed for the actual MIL compiler, MIL
test translation unit and an instantiated twenty-case shared provider fixture.
The complete native contract verifier's checks passed using the existing cached
generator binaries, including all three inline-array controls; no generator or
native build was needed. The independent integer reference matched every one of
the 16,384 original BGRA bytes in each of `sampler-native-1/2/3` from the successful
x64 receipt above. This is an offline reference check, not execution of the new
native implementation or its GPU fixture.
Hosted Build `37016865292` at `ed61c2802` exposed a genuine ARM admission defect:
the first repeated Linear case retained the old clamped page, failing both its
exact scene flags and all-pixel checks. For the full-Fill 144-DPI source,
contracted multiply/subtract retains a `2^-50` extent residual and `2^-51`
centered origin; isolated AArch64 source-expression LLVM lowering confirmed the
fused operations without linking or execution. Semantic Fill admission corrects
that predicate, while the original GPU assertions remain unchanged. The same
Build exposed an MSVC signed/unsigned fixture comparison, now explicitly typed.
Post-fix strict compiler/test/instantiated-fixture syntax, complete cached native
contract checks and whitespace checks passed. Actual post-fix ARM/provider
execution is still a separate hosted gate.
Hosted native/provider execution, Windows hardware, package closure and actual
source application qualification remain pending. No local native/GPU build,
runtime staging or VM is part of this change.

## Capture-pass isolation after hosted sampling failures

Build `37019432709` at `fdd89c8de` passes all raw MIL controls on Linux ARM64,
confirming the semantic Fill route. Its Vulkan llvmpipe GPU job `110878404492`
fails variant 15 at `(16,12)`: RGBA `(0,143,111,255)` instead of
`(0,143,112,255)`. Windows ARM64 D3D12 WARP job `110878404415` passes that case,
then fails variant 16 at `(19,13)`: `(32,96,0,255)` instead of `(31,96,0,255)`.
These observations do not identify which filtering pass introduces the byte
difference and do not establish an address mapping defect.

A failure-only paired-provider diagnostic extracts the unchanged immutable
sampler picture from the actual failing scene. It replays that nested scene on a
fresh engine with the same provider options and exact 32x24 physical frame,
original DPI and transparent clear. Cold and warm diagnostic replays must match
and each submit exactly once. Existing readback completion and resource teardown
remain authoritative; readback pitch is padded without changing returned bytes,
and Dawn retains its BGRA-to-RGBA swizzle. No work is submitted on the original
engines, and their three replay counters and original fatal pixel assertion are
unchanged. Logs distinguish capture bytes from post-effect bytes, including the
first RGB difference within the original clip, and report actual sampler flags.
This is diagnosis, not a rendering fix or qualification: native-sampler defaults,
explicit/required-native sampling policy, pixel expectations and tolerances are
unchanged. Hosted capture-pass evidence remains required before a policy change.

An ideal rational interpolation followed by nearest UNORM conversion is not a
portable promise of the ordinary hardware sampler. The Vulkan specifications
separately define [fixed-point conversion](https://docs.vulkan.org/spec/latest/chapters/fundamentals.html#fundamentals-fpfixedconv)
and [texel-coordinate precision](https://docs.vulkan.org/spec/latest/chapters/textures.html#textures-unnormalized-to-integer).
The [D3D11.3 functional specification](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)
sections 7.18.16 and 3.2.3.6 distinguish filtering precision from final format
conversion. These allowances are not proof of the pass responsible for either
observed mismatch. No device-output lookup, tolerance, software-reference
quantization substitution or automatic explicit-sampling policy is introduced.

Post-commit strict Clang C++20 syntax passes for the actual MIL fixture and the
fully instantiated shared twenty-case provider fixture, including its diagnostic
callback. Provider test translation-unit compilation and actual GPU execution
remain hosted checks. The failure mapper validates the complete final image and
does not index the capture for leaked pixels outside its receiving frame; such
pixels still reach the unchanged original fatal assertion.

## Paired native-filter and strict four-load controls

Diagnostic Build `37023286983` at `bb7a24ef8` isolates both failures before
the effect. Linux ARM64 Vulkan job `110891509309` captures `(0,143,111,255)`
at variant 15 `(16,12)`, exactly matching the effect's RGB. Windows x64 job
`110891508715` and ARM64 job `110891508816`, both D3D12 Microsoft Basic Render
Driver, capture `(32,96,0,128)` at variant 16 `(19,13)`; the final opaque
image is `(32,96,0,255)`. In all three jobs every RGB byte in the original
clip agrees between direct capture and effect. This rules out the downstream
effect as the source of these differences; it does not distinguish hardware
filter precision, coordinate interpolation, blending and format conversion.

The paired fixture now separates two contracts without a tolerance:

- All original cases 0–12 keep their existing default engines, exact pixels,
  retained source updates and cold/warm/independent counters.
- Cases 13–19 under zero engine flags compare every RGBA byte of the 32x24
  actual retained capture with an independent raw WebGPU render on that device.
  A test-only shader takes original 2x1/2x2 opaque input texels, unwrapped
  fragment-center UVs and native Repeat/MirrorRepeat samplers. It imports no
  compiled scene, production shader/address helper or observed output.
  The original straight source becomes premultiplied fragment output and uses
  ONE / ONE_MINUS_SRC_ALPHA blending into transparent RGBA8, matching the real
  retained capture contract (not the separate straight-alpha direct-image API).
  Every final 64x64 effect byte is checked against that independent capture,
  including the original final clip and opaque black exterior.
- The same seven original scenes also execute with the explicit four-load
  engine flag. Every original strict integer-rational expected byte and all
  original cold-2/warm-1/independent-2 submission and effect-cache assertions
  remain executable. Native precision is not a reason to skip, relax or fit
  those separate controls.

There are 81 effect replays: the original 60 plus 21 explicit controls. One
lazy raw reference pipeline and bounded policy/capture engines are reused per
provider. Capture readback uses actual RGBA8 on both providers, not a BGRA
presentation conversion; ordinary Dawn final-image readback retains its original
BGRA-to-RGBA swizzle. Buffers are unmapped only after the actual callback, before
their next use or release. The original provider wait budgets are unchanged.
No product defaults, RequireNative behavior, shader or renderer is modified.

This source checkpoint is not qualification. Both providers, Windows package
paths and all exact four-load controls still require hosted execution. Even
explicit sampling is not presumed to guarantee final UNORM arithmetic merely
because its four loads are authored; any strict failure remains a failure.

The blend boundary is established by actual source selection:
`Scene/progpu_native_semantic_draw_execution.cpp` selects `image_pipeline` for
the ordinary unmasked retained draw; `Backend/progpu_native_image_layer_resources.cpp`
creates it with `fs_retained_image_unmasked` and ONE source blending.
`Texture.wgsl` applies straight-source alpha before that blend. The reference
implements this algebra independently, without including that production shader.

After the major fixture and blend-boundary commits, strict Clang C++20 syntax
checks passed for the actual MIL test translation unit, the fully instantiated
paired scene fixture, and the fully instantiated raw GPU reference against the
existing original Dawn API header. Protocol generation, MIL coverage and native
memory-inventory source checks passed, as did whitespace checks. The local
browser wrapper is not a replacement for the pinned wgpu-native header (its
external `webgpu/webgpu.h` dependency is absent), so no old-ABI or full provider
translation-unit compilation is claimed. Those and all GPU/Windows/package
execution remain hosted gates. No local native build or GPU execution occurred.
