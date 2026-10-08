# Original ShaderEffect UV derivative constants

The native MIL ShaderEffect producer retains the original
`DdxUvDdyUvRegisterIndex` rather than rejecting every declared register. The
bounded shader family admits float registers 0 through 31, matching its actual
constant register file. `-1` retains the original no-derivative path. Other
negative indices and larger registers fail before a resource update publishes.

Version 3 is explicit 576-byte metadata: size/version, optional owned picture
index, derivative register, zero flags/three reserved words, and the unchanged
544-byte version-1 program. Versions 1 and 2 keep their layouts and behavior.
The optional picture retains version 2's earlier same-scene IMAGE with
IMAGE_PICTURE ownership. Unknown flags/versions, bad indices and malformed
programs reject atomically. The original internal v1/v2 reader also rejects v3
without changing its output instead of silently ignoring derivative metadata.

The renderer already requires a positive-axis, complete integral physical source
capture and identity final composite mapping. Within that admitted frame, the
UV-to-device linear basis is `diag(physicalWidth, physicalHeight)`. Its two
unit-device-step vectors are `(1/physicalWidth, 0)` and
`(0, 1/physicalHeight)`. Native preflight obtains these dimensions from the actual
layer presentation and target extent, including DPI. Source or viewport
translation has no vector component. The binding uses this same retained capture
frame; it does not infer dimensions from managed source size or texture allocation
padding. The selected register is written **after** copying original user
constants. Original DEF instructions retain their normal shader precedence.

This is a bounded diagonal-basis contract, not a general matrix inverse.
Fractional/cropped captures, rotated/reflected input, backdrop/cache-content
layers and unsupported mapped presentation remain gated. No identity inverse,
epsilon singular check, rounded source repair or new renderer path is added.
No derivative is synthesized in the source MIL constants or scene snapshots.
Programs remain cached by exact original bytecode; per-capture derivative values
belong to owned binding uniforms and their existing submission lifetime.

## Provenance and qualification

Observable source behavior was inspected in LibreWPF
`6b77f3cdd29d551907acd5636d75684f3433f9da`,
`src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp`, in the
hardware effect's derivative-register assignment: user constants are sent first,
then the requested register receives device-step vectors through the inverse
texture-to-device basis. The implementation here is local and specialized to the
already admitted native capture contract; no foreign implementation text or
matrix algorithm was copied. Original ProGPU baseline is
`a610659f977d873d8465d60b3c5a8dfa951a4cb1`.

Authored source controls retain indices 0/31, reject 32/negative non-sentinel
indices transactionally, keep original user constants unchanged and carry the
same metadata with owned ImageBrush pictures and changed source DPI. The native
uniform fixture covers all unknown flag bits, versions/sizes/reserved words,
self/forward picture references, old-reader atomic rejection, selected-register
precedence, unchanged nonselected registers, and zero/unrepresentable dimensions.
It is registered in the existing internal target beside the 477 bytecode controls.

The integrated source control now distinguishes the preserved v3 capture at
DPI 2 from the later final-device v5 path at DPI 1.5. The latter needs the full
non-dyadic inverse contract; it retains separate input/sampler pictures and an
exact 48-by-36 capture. Both controls require the original constant at register
zero and untouched source values at the requested derivative register. This is
a wire-selection assertion, not a new derivative calculation or pixel waiver.

The paired provider fixture checks every RGBA pixel over cold/warm/independent
replays. Original authored D3D tokens expose ddx and ddy in separate color
channels, with DEF-owned opaque alpha. It varies physical width/height, target
DPI, selected register and equivalent full presentation. The same shader bytes
cross changed binding generations. Translated viewport, fractional origin,
fractional physical dimensions and clipped input reject with zero renderer
submissions; no prior assertion or deadline changes. Existing source sampler,
picture-ownership and original shader pixel controls remain intact.

No native renderer build, execution of these new fixtures, GPU, VM, runtime
staging, application or package qualification has been performed. No expanded
source mapping or full ShaderEffect parity is claimed.

At implementation/fixture snapshot `b24e578a9e832e6a8a880b28d62d3a5a21d0e3fe`,
the complete native contract verifier passed: 143 commands/141 packet layouts,
MIL ledger 109/25/7, all generated C#/Unicode contracts, three inline-array
controls and the 93-field/seven-excluded-identity ownership guard. Strict
AppleClang C++20 `-Wall -Wextra -Wpedantic -Werror -fsyntax-only` passed for the
MIL producer/tests, raw scene reader, typed layer builder, new uniform controls,
internal registry and instantiated paired pixel fixture. These are source and
ABI checks, not execution of the native renderer or the new behavioral fixtures.
