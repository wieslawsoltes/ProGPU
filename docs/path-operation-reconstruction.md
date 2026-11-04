# Closed rectangles and reconstructed contours

A closed rectangle can contain three explicit line segments: its fourth edge
returns implicitly to the start. `PrimitivePathGeometry` accepts that inventory
and still validates the closing edge and the complete rectangle area. This lets
the existing contained-primitive path preserve an intersection of identical WPF
layout clips, including the reported 21.798828 by 172 AvalonDock tab bounds.
A three-edge path with a diagonal closing edge remains rejected.

Reconstructed GPU contours cannot be discarded using their signed area. The two
filled lobes of a bow tie cancel in that sum, and a small positive source area
can become visible under a later target transform. The proposed `1e-4` area
filter in PR #424 failed all four new reconstruction controls: both traversal
directions of a bow tie and an exact dyadic triangle of area `2^-15`. Removing
that filter preserves every original endpoint and ordered edge. The existing
segment chaining policy and shader arithmetic are unchanged.

The rectangle fix does not establish a general fix for coincident edges in the
GPU Boolean solver. Linux System.Drawing corpus improvements contributed with
#424 require verification against the final combined source; the corpus gate
continues to compare the complete failure inventory and summary.

## Managed/native applicability

The changed admission is in the managed `PrimitivePathGeometry` recognizer,
used by `PathOpGeometrySolver` and compositor clip handling. The native geometry
entry point `progpu_native_geometry_combine` instead calls
`combine_native_fill_contours` in
`src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_path.cpp`.
Its `create_native_fill_geometry` supplies closed figures to the existing
Direct2D geometry core; it has no four-explicit-segment rectangle prerequisite.
Its normalization handles self-intersections before area normalization: one
crossing splits into the two lobes, and multiple crossings use the existing
self-intersection normalizer. Its final zero-area comparison is exact, not the
removed managed small-area threshold. Neither native code nor shared shaders
receive that proposed filter. No native implementation change follows from
restoring the original managed reconstruction behavior.

Validation of the corrected managed source: 11 focused controls pass;
the full renderer suite passes 6,449 cases with 13 platform-specific skips on
macOS; all 230 headless cases pass. Final hosted provider, SVG, package and
source-application gates remain required before release.
