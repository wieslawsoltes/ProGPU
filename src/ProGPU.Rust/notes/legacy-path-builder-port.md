# Original legacy path builder and retained conics

Implementation only. Source formatting ran; compilation, lint, tests, fixtures,
browser/shader execution and performance validation remain deferred by the user's
implementation-first requirement. No build, benchmark or trace artifacts were
generated. The existing original provider/package/application/pixel gates remain.

`vector::{PathBuilder, RetainedPath}` connects the original packed common-verb
writer to the complete legacy figure lane. The first conic/arc/legacy operation
materializes the actual packed generation. Relative verbs, endpoint/oval/tangent
arcs, circle/oval/rectangle/indexed round-rectangle primitives, polygons, triangles,
append/first-contour extend, reverse, transform, reset, snapshot and detach retain
their separate source policies. Empty-open moves replace their actual figure;
post-close writers restart at the contour start. Extend always emits the original
connecting line, including equal endpoints, and closed sources preserve actual
destination closure. Reverse traverses contours backward, swaps endpoint caps,
reverses controls/sweeps and reconstructs source conic groups. Endpoint and oval
arcs remain analytic. No builder operation initializes WebGPU.

An immutable `RetainedConic` owner is attached only to the first of the original
sixteen midpoint-matched polynomial quadratics. It retains original start/control/
end and weight. Remaining spans remain ordinary quadratics. Rendering, tight
bounds, source fill/stroke and composition sampling continue to consume those
actual polynomial spans; raw iteration emits the one original conic and skips its
span inventory. Clone/offset/transform/font placement preserve the original marker
coordinates; analytic partial-span extraction clears the full-group marker.
`PathSegment` is now Clone rather than Copy; ordinary segments retain only an
optional shared pointer, avoiding an inline source-point payload on every segment.
Independent XY arithmetic uses safe SIMD; parameter/contour/root traversal remains
ordered. Invalid or approximately-unit source weights retain the original plain
quadratic fallback. No endpoint repair replaces original conic arithmetic.

`conic_to_quadratics` preserves the independent recursive homogeneous subdivision,
left-before-right visits, exact control formulas and 0..20 depth admission. It is
O(2^depth) work/storage with bounded recursion, not repeated reevaluation. Raw and
force-close iterators now recover original conic metadata, and measured/effect
output uses the actual legacy builder to regenerate the sixteen source render
spans rather than substituting native rational-segment rendering.

Packed snapshots retain O(1) sharing and used-prefix COW. Legacy snapshots use the
original zero-offset clone arithmetic in O(N); detach transfers actual storage in
O(1). First immutable geometry access replaces that snapshot's packed lane with
one materialized generation, subsequently shared by all queries and Canvas draws.
The mutable CPU facade deliberately uses RefCell for this original observation
side effect; render consumers receive immutable Arc<PathGeometry> owners. Packed
transform keeps original homogeneous mapping/lazy translation, while materialized
transform keeps original Vector2 mapping and full conic metadata.

Raw point/verb/mask inventory is one ordered traversal with retained conic weights.
Line, rectangle, two-arc oval, eight-segment round-rectangle and original sampled
convexity queries retain separate admission. Contains retains the original
zero-tolerance query, explicit control-envelope fallback and independent inverse
flag. SVG output preserves original command ordering, actual arcs, recursive
32-quadratic conic output, move-only filtering and explicit closing-line policy.
Rust round-trip decimal spelling is not yet qualified as identical to .NET's
invariant `R` spelling; semantic numeric round-trip and exact string behavior have
separate remaining gates. Deferred Boolean mutation/raw topology requires an
actual solved generation and rejects rather than manufacturing an envelope;
native rational families keep their independent source contracts. These are
explicit remaining transport/qualification boundaries, not parity claims.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPath.cs` | `98f474ac8801e7d7b5ceed338191b979753babe0bb5b0aa826a8d1553f7cd660` |
| `src/SkiaSharp/SKPath.Compatibility.cs` | `f350ef96db62afc27bfa5da100f597f3aaefa8a3faefa0c05463d4ad3b633fa8` |
| `src/SkiaSharp/SKPathBuilder.cs` | `2079c66d1e0760be3a63d013c362cbcea90a14314993d110cd42d75b9425bb91` |
| `src/SkiaSharp/SKPath.Packed.cs` | `7f38c37f3106afb668a1ee40db6216d3a5d3e50feee8e750ed1add8c36562218` |
| `src/ProGPU.Vector/ArcSegmentGeometry.cs` | `847d2cab48f10fe903eafff7ac56da9a36f4be77ad6932258c45b3ac6b31428e` |
| `src/SkiaSharp/SKMatrix.cs` | `a1a0fb70cb138510b7407bf41de5b3d88fb6e3f34bde01dd948d1128268b54e3` |
