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

This implementation-first checkpoint has not run a native renderer build, GPU
fixture, VM, runtime staging, application or package qualification. Focused
source/raw and paired provider controls follow separately. No expanded source
mapping or full ShaderEffect parity is claimed.
