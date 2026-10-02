# Original pixel-shader matrix products

The shared original-bytecode translator accepts `M4x4`, `M4x3`, `M3x4`,
`M3x3` and `M3x2` (opcodes 20 through 24) in the existing `ps_2_0` and
`ps_3_0` families. The compiler validates the original two encoded operands,
including every implied consecutive matrix register, before publishing output.
The original program bytes remain the exact cache identity; no source mutation,
CPU pixel evaluator, new native crossing, wire version or pipeline family exists.

The destination must have the exact instruction-defined XY, XYZ or XYZW mask.
Neither the vector nor any implied matrix row may alias it. The vector admits
the original swizzle and optional negate; additional modifier families remain
explicitly rejected. Matrix rows are unmodified, identity-swizzled temporaries
or constants within the existing register limits. Relative addressing and other
register files remain unsupported. Each implied temporary row must contain all
three or four consumed components. One ordinary constant read port is enforced
for each expanded dot, not accidentally once for the entire matrix: a constant
vector with temporary rows is valid, but two different constants in one dot are
not. The prior ordinary-instruction read checks are unchanged.

Execution stays on the GPU, with two to four independent dot products in source
row order. Three-component products do not consume W. The complete result is
captured before destination writes, saturation applies to the requested result,
and unselected destination components remain unchanged. Parsing and generated
output remain O(original tokens), with fixed register proof storage and at most
four dots per matrix instruction. Both native providers use this same emitter.
The managed explicit-WGSL extension registry is not an original-bytecode compiler
and does not acquire a second translator or rendering fallback here.

## Contracts and provenance

The implementation extends original ProGPU translator and fixtures at
`be0c699587ba83967af432b1ff935e907acbaba6`; no foreign implementation was copied.
Microsoft's [M3x2](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/m3x2---ps),
[M3x3](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/m3x3---ps),
[M3x4](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/m3x4---ps),
[M4x3](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/m4x3---ps) and
[M4x4](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/m4x4---ps)
specify the row expansion and destination masks. The M4x4 page has a contradictory
second modifier sentence; this implementation follows its explicit first-sentence
vector-negate/swizzle permission and the common matrix operand contract, while
requiring unmodified matrix rows. Original Windows bytecode qualification remains
required, not inferred from translator checks. Opcode identities come from the
[public SDK header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/d3d9types.h).
The existing [cross-engine architecture research](native-mil-shader-effects.md#research-and-provenance)
still applies: immutable original programs and live constants retain the existing
lazy program/binding ownership, budgets and submission lifetime. This is an
instruction-dependent emitter extension, not an architectural or font change.

## Authored controls and remaining qualification

The original translation, arithmetic and cross-product controls remain separate
and unchanged. Additional controls cover every matrix shape in both models,
vector swizzles/negation, saturation, exact destination masks, preserved lanes,
every implied row/component, register boundaries, per-dot read ports, aliases,
source modifiers and atomic malformed-token rejection.

The existing paired-provider GPU fixture retains its original ten cases and
every byte/counter assertion, then adds ten original matrix programs. Actual
white input texels feed the vector; independently selected matrix constants
produce exact magenta with full alpha. Contrasting W coefficients distinguish
three- from four-component products. The shorter products must retain original
Z/W; model-three cases additionally exercise vector negation and saturation.
Cold, warm and independent-engine replay still checks every RGBA pixel, the
final clip, exact pass/cache/uniform-upload counts and one queue submission.
No deadline or assertion is relaxed. GPU, Windows original references, complete
package/NativeAOT and application qualification remain required.
