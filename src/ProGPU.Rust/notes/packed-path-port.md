# Original packed common-path storage

`vector::PackedPathBuilder` retains the original Move/Line/Quadratic/Cubic/Close
inventory in one contiguous generation. `snapshot` shares its exact used prefix
and writer metadata in O(1); the first subsequent mutation copies only the used
commands when another owner still retains them. Reset retains reusable storage
without modifying snapshots. Detach transfers the actual generation and resets
fill/writer state. Relative verbs and post-close continuation use the original
live current point; consecutive empty Moves replace the previous Move.

Finite pure translations retain the original lazy command offset and independently
update bounds, tight bounds, current point and contour start. Command observation
materializes that accumulated translation once; subsequent builder copies retain
the observed state. General transforms use original projective point mapping and
recalculate control and derivative bounds. Queries never initialize WebGPU or
flatten curves. Analytic `PathGeometry`, `MeasuredPath` and `PathEffectSource`
bridges are explicit publication boundaries; inverse fill remains separate.

Tight bounds retain original per-axis control-point admission, ordered double
derivative roots, the 1e-12 degeneracy threshold, both cubic root visits and a
single float narrowing of each evaluated component. Independent XY polynomial
evaluation and translation use SIMD. Ordered verbs, dependent writer state and
root admission remain sequential. Bulk triangles reserve all four original verbs
per triangle before publication and preserve point/bounds order.

Rust owns buffers through `Arc`, with a bounded per-thread reusable command vector
and the original 64-command initial/4,096-command retained capacity limits. The
actual container allocation, allocator rounding and cross-thread retirement differ
from managed ArrayPool; allocation/performance equivalence is unqualified. Private
read materialization uses `RefCell`; immutable paths are not implicitly `Sync`.
There is no unsafe code, address exposure or mutable shared command alias.

This is the common packed path and builder lane. The original legacy mutable
SKPathBuilder adapter, retained 16-span conic metadata, complete arc/conic/primitive
builder operations and legacy topology policies remain separate port work. The
existing geometry/effect APIs do not silently stand in for those contracts.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPath.Packed.cs` | `7f38c37f3106afb668a1ee40db6216d3a5d3e50feee8e750ed1add8c36562218` |
| `src/SkiaSharp/SKPathBuilder.cs` (common verbs, snapshot/detach/reset and fill state) | `2079c66d1e0760be3a63d013c362cbcea90a14314993d110cd42d75b9425bb91` |

No tests, compiler/lint, shader/browser execution, benchmarks or fixtures were run
or authored. Source formatting only. Exact original runtime floating-point/FMA,
signed-zero, ownership, allocation, error and performance qualification remain open.
