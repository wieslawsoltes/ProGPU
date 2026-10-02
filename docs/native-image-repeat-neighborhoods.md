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
