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

This implementation is committed before validation. Focused source/unit checks,
both provider GPU runs, Windows, package/NativeAOT and application qualification
remain required; passing a translator control is not rendering parity. General
matrix operations, predicate/control flow and the separately documented numeric
domain gaps are not admitted by this addition.
