# MSVC excluded-origin output conversion

The [MSVC C++20 job](https://github.com/wieslawsoltes/ProGPU/actions/runs/34544210807/job/103093203686)
failed at text shaping interop line 2207 with C4244, promoted by /WX: empty
excluded input assigned a double origin directly to the existing float content
height field.

The origin is already validated as finite, nonnegative and within float range
before this branch. Make the existing conversion explicit with static_cast<float>;
do not suppress the warning, change the ABI, clamp the origin, or alter the double
fragment placement contract. Both native providers compile this shared source;
managed consumers use the same C result and need no behavioral fork. This is an
implementation-specific compiler fix, not a new layout or SIMD algorithm.

The C interop regression now tests empty input at a fractional double origin,
requires the float result with zero glyphs/lines, and rejects a double outside
float range. Empty source TextLine admission remains a separate contract.

Provenance: original repository code and the compiler diagnostic only; no external
implementation was imported. Strict C++20 and warnings-as-errors remain enabled.
Final MSVC CI on the new head is still required before calling this gate green.

Local validation: both native provider libraries and the text interop test target
rebuilt on macOS ARM64. Focused CTest passed 1/1 in 0.85 seconds. This local compiler
result does not substitute for MSVC execution.
