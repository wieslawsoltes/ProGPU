# Original-bytecode arithmetic contracts

The shared native ShaderEffect translator extends the original `ps_2_0` and
`ps_3_0` bytecode family with DP2ADD, LOG, version-specific SINCOS and an explicitly
bounded immutable-DEF NRM subset. It does not replace source shaders with named
effects or arbitrary WGSL. Original resource-19 bytes, ABI, constant bindings,
12 temporaries, 32 float constants, one implicit sampler and existing budgets
remain unchanged. Both native providers consume this one emitter; the separate
managed explicit-WGSL registry is not a bytecode translator.

## Operand and numeric contracts

DP2ADD reads both first operands' swizzled XY regardless of the destination mask.
Its third source must have a replicate swizzle; the scalar sum is replicated into
the selected destination components. All inputs are evaluated into an immutable
result before any aliased destination component is assigned.

LOG also requires a replicate swizzle. Its sign-independent scalar result is
replicated, with exactly the documented negative maximum finite float for zero.
The emitter uses an actual conditional before invoking `log2`, not `select` with
an invalid zero logarithm in one argument. Subnormal magnitude bits are converted
to a normal integer-valued float, whose logarithm is adjusted by the binary32
exponent scale; the builtin never receives a subnormal that it could flush to
zero. NEG, ABS and ABSNEG retain the existing source-modifier ordering. This is
not a new claim about globally preserving signed zero, NaNs, overflow or denormals
through other instructions, nor proof of numeric pixel parity for WGSL builtins.

NRM is admitted only when its source is a direct immutable DEF register, after
the original swizzle and admitted source modifier. It still executes its dot,
zero guard, inverse square root and multiplication on the GPU; compilation does
not evaluate output pixels or fold in application uniforms. The compiler checks:

- Every consumed XYZ magnitude is at most `2^62`.
- Nonzero XYZ has at least one magnitude at least `2^-62`.
- For nonzero XYZ, selected W has magnitude at most `2^62`.
- For zero XYZ, selected W has magnitude at most one.

These conservative admission bounds keep the positive squared length normal and
below `3 * 2^124`, and keep products finite. The zero case branches before
`inverseSqrt`, using the documented maximum finite float factor instead. The
destination must be temporary. Unselected W is neither range-checked nor
multiplied. Mutable constants, temporary/input operands (including aliasing),
and values outside this explicit compiler subset remain rejected; this is not
general runtime-valued NRM support. The proof state is owned by the current parse
and retains original DEF bits, not a cross-program analysis cache.

SINCOS requires a temporary destination with exactly X, Y or XY written and one
replicate-swizzled angle. The original program owns the documented `[-pi, pi]`
angle precondition; the emitter adds no angle reduction, clamp or substitute
output. Shader model 3 has one source and preserves every unselected component.
Shader model 2 has three sources: two distinct, unmodified, identity-swizzled
constant registers must carry the exact SDK coefficient vectors in prior DEFs.
Live-uniform coefficient forms remain rejected because immutable translation
cannot verify them. The macro invalidates every unselected XYZ component while
preserving W; a later read must follow a real rewrite of an invalidated lane.
Verified coefficient operands are the opcode's exception to the ordinary
single-constant-register read port. No shader-model-3 one-source encoding is
silently accepted as shader model 2.

Both versions now enforce one distinct ordinary float-constant source register
per instruction. Shader model 2 additionally permits at most two reads of that
register and one texture-coordinate-register read. This closes a validation gap
in the previous model-2 arithmetic path, not a new register capability. Source
lane availability, write masks, SAT, supported modifiers, exact lengths and
reserved bits remain fail-closed. Any error leaves original bytes and the
caller's previous generated program untouched.

## Deliberately unsupported numeric families

RCP and RSQ require infinity at zero. WGSL permits finite-math assumptions, so a
plain reciprocal, inverse square root or bitcast infinity does not establish
their required propagation. EXP and POW additionally need an owned overflow and
domain contract; POW's absolute-base behavior, zero-base cases and restriction
against aliasing its exponent cannot be assumed from WGSL `pow`. Those four
opcodes remain rejected, as does runtime-valued NRM. There is no runtime black or
clear output, arbitrary finite clamp, GPU status readback or CPU pixel fallback.
Full floating-point equivalence, hardware precision, invalid runtime input and
application qualification remain open beyond the syntactic/finite subset here.

## Specification and implementation provenance

The implementation extends ProGPU's original translator at
`53fe409a65ed4a164956353822183fcac84ea9bf`; no third-party translator was copied.
Microsoft's [DP2ADD](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dp2add---ps),
[LOG](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/log---ps),
[NRM](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/nrm---ps) and
[SINCOS](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/sincos---ps)
define the implemented operand, zero, write and version contracts. The coefficient
values and opcode numbers come from the public
[Microsoft SDK token header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/d3d9types.h),
not an imported approximation implementation. The
[model-2](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx9-graphics-reference-asm-ps-registers-ps-2-0)
and [model-3 register tables](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx9-graphics-reference-asm-ps-registers-ps-3-0)
define read-port/read-count differences.
[RCP](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/rcp---ps),
[RSQ](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/rsq---ps),
[EXP](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/exp---ps) and
[POW](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/pow---ps) were
reviewed against [WGSL floating-point evaluation](https://www.w3.org/TR/WGSL/#floating-point-evaluation)
and remain explicitly unsupported, not approximately implemented.
The existing [effect architecture research](native-mil-shader-effects.md#research-and-provenance)
still applies; no renderer, resource lifetime or pipeline-cache design changes.
Parsing remains O(tokens) with fixed register proof state and O(tokens) optional
generated output. This instruction-dependent pass is not a scalar rendering path.

## Controls and remaining qualification

`progpu_native_shader_effect_arithmetic_tests.cpp` exports
`run_shader_effect_arithmetic_tests()` for the owning native registry. It uses
original independently authored D3D tokens, exact emission/ordering checks and
atomic negative controls. Coverage includes both versions, all write masks,
scalar lanes/modifiers, partial source availability, alias snapshots, signed-zero
DEF bits, NRM boundary/just-outside values and mutable-source rejection, exact
SINCOS coefficients, clobbered-lane read/rewrite behavior, cross-version lengths,
read-port/count rules, reserved bits and unresolved opcode rejection. These are
not a second pixel evaluator or a substitute for original shader reference pixels.

The implementation is committed before focused CPU-only translator checks.
The integration owner wires the independent fixture into the existing native
target. Native renderer/dependency builds, GPU execution, both-provider/package
gates and original WPF application qualification have not been run by this batch.
