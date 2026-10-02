# Owned ImageBrush shader samplers

The original MIL ShaderEffect path can capture one static ImageBrush backed by
an owned bitmap upload. This is an additional supported source family, not full
ShaderEffect, external-sampler or application qualification.

The original version-1, 544-byte shader resource is unchanged. Version 2 is an
explicit 560-byte wrapper: size/version, an earlier same-scene IMAGE resource
index with the IMAGE_PICTURE flag, a zero reserved word and the unchanged version-1 program. The
reader validates the entire original bytecode before publication. Unknown
versions, malformed sizes, self/forward references and non-picture references
fail; no device pointer or managed texture enters the wire contract.

Source realization draws the actual ImageBrush over a zero-origin rectangle
whose dimensions are the integral physical implicit-input extent, with identity
world mapping. It uses the original ProGPU native tile renderer, retaining brush
opacity, viewport/viewbox, addressing, static transforms and source bitmap
sampling. The child scene owns its bitmap bytes. Final source clips and opacity
remain outside the shader; the picture contains full premultiplied RGBA, not a
mask's alpha. The shader sampler retains its independent nearest/linear mode and
clamps normalized coordinates to the complete capture.

The decoder checks the actual same-channel brush and bitmap resources again at
scene compilation, including deletion, replacement and external-source changes.
Dependency revisions include the brush, transforms and bitmap. Static transform
graphs are bounded; cycles, missing resources and animations fail explicitly.
Unbound bitmap uploads may be registered before their source pixels arrive, but
cannot produce a scene. VisualBrush, DrawingBrush, DrawingImage, double-buffered
or external bitmap sources, animated brushes and multiple samplers stay closed.

Both native providers acquire the picture through existing owned picture
rasterization and queue submission. A binding retains the exact engine-owned
picture alongside its program, constants and bind group; disposal follows the
existing span/submission retirement. Owner and physical-extent mismatches fail.
Capture textures are included in combined scene budgets and the deduplicated
GPU memory inventory. No new renderer, CPU sampling or device-handle borrowing
is introduced. Version 1 retains its prior allocation-aware UV normalization.

## Contract evidence and provenance

The observable source contract was inspected in LibreWPF
`6b77f3cdd29d551907acd5636d75684f3433f9da`,
`src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp`:
ImageBrush samplers realize into the physical implicit-input width/height with
zero-origin identity mapping, then use clamp addressing and the declared shader
sampling mode. Intrinsic image size is not the sampler capture extent. This
behavior informed a clean-room implementation; no foreign implementation text
or control flow was copied. Capture, tile rendering, picture ownership and
retirement reuse original ProGPU machinery from
`e4aec5ab9f79697020e1921eb675a86f6992a86c`.

## Qualification

Authored source controls exercise the real original MIL ImageBrush route,
registration before upload, physical capture dimensions despite different image
DPI, malformed versions/indices, wrong resource kinds, atomic transform-cycle
rejection, immutable bitmap revisions, external-source replacement and guarded
dependency deletion. Captured bytes remain valid after source disposal.

A shared fixture is wired into both native provider GPU suites. It constructs
the original source packets and uploaded red/green or blue/green bitmap, then
disposes the channel before rendering. Four cases cover brush opacity, repeated
addressing, an actual brush translation, original shader sampling, final source
clip and physical normalization. Every RGBA byte is checked independently over
cold/warm/independent-engine replay, with two cold submissions (sampler capture
plus parent) and one warm submission. BGRA surface readback is reordered to RGBA
only; no value conversion or tolerance is applied.

Post-commit bounded checks pass: the complete native contract verifier
(143 commands/141 packet layouts; MIL ledger 109 top-level/25 render-data/7
undispatched; all generated C# and Unicode contracts; three inline-array
controls), the 93-field ownership inventory with seven excluded non-owning
identities and its new retained-sampler lease check, and strict AppleClang C++20
`-Wall -Wextra -Wpedantic -Werror -fsyntax-only` for the actual MIL producer,
scene validator, layer builder, MIL test translation unit and instantiated shared
sampler fixture. The syntax check caught and corrected a kind/flag distinction:
pictures are IMAGE resources carrying IMAGE_PICTURE, not a separate resource kind.

No native renderer build, execution of the new MIL fixture, GPU execution, runtime
staging, VM, source application or package qualification has been performed for
this extension. Fractional or
cropped input captures, extra sampler registers and unsupported original shader
instructions retain their existing fail-closed gates.

## Integrated source ancestry

The sampler branch merges arithmetic snapshot
`0594dbcfebeb79bf2c13dd9c940d5949e986e3dc` and scene-owner picture-cache snapshot
`b1a0f229910625b19ae392a7f1a9cbcb6b8e5d7f`, retaining their ancestry together with
the published shader snapshot `e4aec5ab9f79697020e1921eb675a86f6992a86c`.
The picture owner field, private capture resource scope and both independent
pixel fixtures coexist. Dawn retains exact BGRA-to-RGBA readback ordering.
No assertion, submission expectation or application gate is removed by the merge.
