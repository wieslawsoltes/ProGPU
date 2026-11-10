# Original integer region stack

`vector::Region` implements all six original `SKRegion` rectangle and region
operations, bulk/source assignment, actual-coverage contains/intersection/quick
queries, wrapping translation, canonical rectangle/clip/span snapshots and
original analytic boundary contours. Deferred rectangle Union retains its original
containment fast path and cached bounds; queries and dependent operations normalize
before exposing the inventory. Clip iteration culls disjoint rectangles without
cropping retained rectangles. Span iteration merges touching exclusive intervals.

Normalization retains sorted unique Y boundaries, merged ordered X intervals,
exact adjacent-band extension and stable rectangle publication order. The source's
rectangle storage ceiling of 1,024 and normalization ceilings of 512 Y coordinates,
1,024 intervals/merged intervals/rectangles and 2,048 band entries remain bounded
per-thread reuse limits. Rust container capacities differ from managed capacities;
allocation/performance equivalence is not qualified. Query normalization uses a
private `RefCell`, while mutations require exclusive Rust borrows. Owned iterator
snapshots never retain a mutable region or its reusable scratch.

Clipped path assignment flattens the original raw inventory once, using 16
quadratic/conic and 24 cubic subdivisions, original rational weights, finite-point
and consecutive-distance admission, and the original pixel-center winding/even-odd
equation. Exact clip admission and finite source/clip bounds remain independent;
inverse fill does not expand the source bounds. Four independent pixel-X admissions
use SIMD and preserve ordered edge expressions; scalar-reference scan is an explicit
option. Curve component evaluation and integer translation use independent SIMD
lanes. There is no per-pixel allocation, `Path.Contains`, WebGPU initialization,
CPU pixel upload or replacement of the renderer's analytic raster policy.

The separate rectangular-path assignment retains the original one closed figure /
three lines / four corner witnesses and 0.0001 comparisons. Boundary output retains
one closed three-line contour per canonical rectangle and connects directly to
existing vector fill/stroke/clip lowering. Integer extents and translations retain
source wrapping semantics. Out-of-range/nonfinite float-to-int bounds reject with
`InvalidInput`; original unchecked runtime conversion behavior outside the admitted
integer domain is not claimed. Deferred raw-path reconstruction and legacy retained
conic span metadata have their existing independent bridge boundaries.

Fallible vectors/maps and complete candidate publication preserve source bytes on
allocation failure; this explicit Rust error boundary is separate from managed
allocation exceptions. Exact runtime float/FMA, integer edge, source corpus,
pool/allocator, browser and performance controls remain unqualified.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPath.cs` (`SKRegion`, canonical bands, scanner and iterators) | `98f474ac8801e7d7b5ceed338191b979753babe0bb5b0aa826a8d1553f7cd660` |
| `src/SkiaSharp/SKPath.Compatibility.cs` (shared original raw path iterator) | `f350ef96db62afc27bfa5da100f597f3aaefa8a3faefa0c05463d4ad3b633fa8` |

No tests, compiler/lint, shader/browser execution, benchmarks or new fixtures were
run/authored. Source formatting only; no build, benchmark or trace artifacts.
