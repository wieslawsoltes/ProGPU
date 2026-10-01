# Original shader-model-3 bytecode translation

The retained native WPF ShaderEffect translator now admits a bounded `ps_3_0`
straight-line family. The acceptance action is a source ShaderEffect over its
implicit input in ShowcaseApp; this connects its bytecode input, not source UI,
pixel parity or a complete ShaderEffect implementation. The existing version-1
scene resource, original byte identity and shared native fragment execution are
unchanged. No caller-supplied WGSL, named replacement shader or CPU pixel path is
introduced.

## Declared source input and retained limits

Only the exact pixel version `0xffff0300` selects the new parser policy. Before
executable instructions, it accepts one `dcl_texcoord0 vN.xy`, for N from zero
through nine. The actual declared index owns subsequent input reads. A generated
local alias maps that register to the existing wrapper's UV parameter; the name
`t0` inside the wrapper is not admission of a D3D shader-model-2 texture register.
Only declared X/Y components may be consumed, including after swizzling. Input
Z/W never acquire the wrapper's default values. Missing, duplicate, late, packed,
centroid or other semantic declarations fail explicitly. Shader-model-2 retains
its original `t0` policy and rejects the new `v#` declarations.

The same MOV, ADD, SUB, MUL, MAD, DP3, DP4, MIN, MAX, LRP, FRC, ABS, CMP and
ordinary TEXLD family is reused. Arithmetic preserves admitted swizzles,
NEG/ABS/ABSNEG, destination masks and SAT. DEF retains finite immediate bits.
Shader-model-3 instructions may read one distinct float-constant register,
matching its read-port contract. TEXLD accepts unmodified temporary or declared
input coordinates, a declared identity-swizzled 2D sampler, and a temporary
destination. Coordinate swizzles are retained; projected/biased/explicit-LOD,
gradient, modified-coordinate and sampler-swizzle forms remain rejected.

The version-1 limits remain 32 float constants, 12 temporaries, one implicit
sampler, 64 KiB bytecode and 512 decoded instructions. This deliberately does
not expose shader-model-3's larger resource files. Integer/Boolean/predicate,
relative addressing, control flow, derivatives, extra inputs/samplers and depth
or multiple color outputs remain unsupported. At least one input sample and a
fully written `oC0` are still required. Failed validation/emission leaves the
caller's previous shader text and original bytes unchanged, including a failure
after a long valid prefix. No wire field, program cache or capture policy changes.

Translation is a bounded sequential token pass, O(T) time and O(1) register
validation state for T tokens; optional generated output is O(T). Parsing has
instruction dependencies and is not a data-parallel rendering fallback. Both
native providers consume this same translator. The managed explicit-WGSL effect
registry has no original-bytecode translator to update and is not used here.

## Specification and local provenance

This extends the original ProGPU decoder at `9cb3f0a2a`; no implementation from
another project was copied. Microsoft's
[shader-model-3 registers](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx9-graphics-reference-asm-ps-registers-ps-3-0)
define the `v#` input and float-constant read-port distinction.
[Semantic declarations](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dcl-usage---ps)
define register/semantic binding and placement before executable instructions;
[source tokens](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/source-parameter-token)
define addressing, swizzle and modifier bits. The exact `TEXCOORD0` declaration
uses the public D3DDECLUSAGE token field, not a shader name or bytecode hash lookup.
[TEXLD](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/texld---ps-2-0)
and the [instruction inventory](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx9-graphics-reference-asm-ps-instructions-ps-3-0)
bound the implemented subset. Broader texture behavior is rejected rather than
assumed. The existing [effect architecture research](native-mil-shader-effects.md#research-and-provenance)
and immutable-program/live-binding design remain applicable; no new renderer,
pipeline/cache architecture or text behavior is designed by this parser change.

## Authored controls and qualification

`progpu_native_shader_effect_translation_tests.cpp` exports the standalone
`run_shader_effect_translation_tests()` control function, without a private main
or test registry. The integration owner wires it into the existing native test
target. Independently authored byte tokens cover all ten input-register indices,
exact generated statements, the shared arithmetic family, swizzles/modifiers,
partial output writes, resource endpoints, DEF bit identity and unchanged ps_2_0.
Negative controls exercise cross-version inputs, unknown/packed/centroid semantics,
undeclared lanes, register/resource limits, addressing/flow/texture forms, corrupt
lengths, comments, END and atomic output. Exact instruction and byte budgets have
both positive and rejected-overflow controls.

These controls are translator contracts, not a shader interpreter, compiler or
pixel oracle. No renderer/native build, GPU, VM, runtime staging or original
application execution has occurred for this implementation checkpoint. Required
follow-up remains genuine compiled-source bytecode and independent Windows pixels,
source/MIL updates, both providers, full package/NativeAOT and original UI gates.
