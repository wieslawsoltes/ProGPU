# Original pixel-shader cross products

The shared native translator now accepts original `CRS` (opcode 33) in its
existing `ps_2_0` and `ps_3_0` programs. Both native providers use the same
translator; this adds no wire layout, register file, pipeline family or fallback.

Source framing follows Microsoft's [pixel-shader CRS contract](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/crs---ps)
and the [public SDK opcode enum](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/d3d9types.h).
Only a temporary destination with a nonempty XYZ-only mask is admitted. Neither
input may name that destination, and source swizzles must remain identity.
The original source modifiers, saturation, register limits and per-instruction
read-port checks still apply. Vertex-only `SLT`/`SGE` remain rejected rather than
being mistaken for the pixel-shader comparison/predication contract.

Emission computes the right-handed cross product only for selected destination
components. Each selected component requires its actual two source components;
an unneeded third component and source W are not read. Unselected destination
components, including a previously written W, remain unchanged. Rejection never
publishes partially translated text or changes the source byte stream.

The original 367 arithmetic controls remain independent. Added controls cover
every admitted mask in both shader models, exact component dependencies on each
operand, all existing source modifiers, saturation, handedness, preserved W,
source aliases, forbidden masks/swizzles/registers, constant read ports and
malformed instruction framing. The shared GPU fixture keeps its original four
programs and every byte/counter assertion, then adds six original bytecode cases:
the three cyclic unit-vector products in each model, with negative constants and
NEG source modifiers in the model-three cases. Exact red/green/blue pixels and
unchanged alpha are independent of translator output. Actual source texels feed
the vector multiplication, and cold/warm/independent-engine replay keeps the
existing submission, pass and uniform-upload assertions and deadlines.

Implementation commit `a462cbbef` preceded validation. The bounded standalone
C++20 warnings-as-errors check then passed the unchanged 110 translation and
367 arithmetic controls plus 406 additional cross-product controls. Strict
syntax checking of the instantiated shared pixel fixture and the diff check
also passed. No renderer/native library build, GPU, VM or runtime staging was
performed. Both provider GPU runs, Windows, package/NativeAOT and application
qualification remain required; passing a translator control is not rendering parity. General
matrix operations, predicate/control flow and the separately documented numeric
domain gaps are not admitted by this addition.

## Dawn Metal scalar-source emission

Exact PR263 head `be0c699587ba83967af432b1ff935e907acbaba6`, Build
`37007183954`, macOS ARM job `110838116571` rejected creation of the owned
bytecode pipeline with Tint's `swizzle view instruction still has usages after
lowering`. The new CRS expression used scalar loads through redundant nested
reference swizzles, such as `(r[0].xyzw).y`. The
[pinned Dawn lowering pass](https://dawn.googlesource.com/dawn/+/710c33013c53ab2700d332c25ff51430251a8cc4/src/tint/lang/core/ir/transform/lower_swizzle_view.cc)
collects view Load/Store instructions and fails on remaining view usages;
that compiler failure occurred before the new pixel comparisons.

CRS now emits direct scalar reads from the validated original registers. NEG,
ABS and ABSNEG are applied componentwise in their original order; required source
lanes, partial masks, right-handed multiplication/subtraction order, SAT and
preserved destination W are unchanged. No whole-register snapshot reads an
unwritten lane. Other opcodes retain their existing generated expressions, and
original bytecode, pipeline/cache identity, resource budgets and provider choices
are unchanged. No upstream compiler code or flags are patched.

The original 110 translation, 367 arithmetic and 406 CRS controls remain, with
an additional 232 scalar-source programs spanning both models, every XYZ mask and
all pairs of admitted source modifiers, plus exact original signed-zero DEF bits
and their modifier operations. This is emission/bit-retention coverage, not a new
claim about WGSL floating-point propagation. All ten original paired GPU programs,
pixels, cold/warm/independent counts and deadlines are unchanged. The emission
fix must still pass the exact hosted Dawn/Metal lane; no local renderer/GPU run
or pixel qualification is inferred from these source controls.
