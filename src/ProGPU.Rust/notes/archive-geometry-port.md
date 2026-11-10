# Original target-dependent archive geometry

Source SHA-256:
- `src/ProGPU.Scene/Compositor.cs`: `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd`
- `src/ProGPU.Scene/SplineGeometry.cs`: `5796cc29235c6be09cc8e8190d09f94511bc875e499d157183c1bdb02b85bd53`
- `src/ProGPU.Scene/Extensions/SplineExtensionPipeline.cs`: `b2c1aade24d8a3758fcd7eda27f433c8c409b69fd05620349f2ad81d8d7ea3c3`
- `src/ProGPU.Vector/TransformMetrics.cs`: `12271082b95c438ed47d041e961e757e5a0b94bc56334a978f5acc2f17a838de`

`ArchiveGeometryDraw` retains original commands, complete path/point/double owners,
parent transform and semantic opacity until actual Scene target compilation.
Source capture composes its matrix before density, triangle or path-atlas choices.
`Scene::compile_for_target` expands the source generation atomically, then invokes
existing material/stroke/raster writers in original order.

Direct rounded paths use the original distinct 3..12 line/arc contour recognizer,
canonical start/segment order, circular-radius/rotation/sweep checks, independent
containment support functions, partial-corner difference/ring admission and exact
float tolerance. The original 8..128 quarter-arc density, duplicate compaction,
balanced convex triangulation and outer/inner band preserve shape13 analytic edges
and the independently owned internal-edge mask. Empty/degenerate triangle output
falls through to the original general PathAtlas route. Sharp canonical rectangles
use direct rectangle coverage only on the actual final target. Existing exact
`RenderFrame.mask_depth` supplies offscreen identity; no format inference occurs.
`Renderer::prepare_capture_scene` explicitly prepares that original target policy.
A prepared final-target sharp specialization rejects subsequent `capture`, keeping
later filtered-image coverage from silently changing. Actual source-frame captures
already carry the correct offscreen depth.

Text-derived path gamma now retains the original font/skew/reflection/axis branch,
0.875 transformed coverage, 24-pixel boundary and 0.72/0.5 small/large values.
Actual semantic DPI and sanitized static zoom stay distinct from the independent
ordinary path64-phase, unquantized raster contract. This is the original DrawPath
route, not an invented positioned-glyph or source raster-policy witness.

DrawSpline retains original inline-before-provider selection of control points,
knots and weights. The complete original De Boor float homogeneous evaluator uses
original inclusive span selection, double knots/delta, float weights/alpha, reverse
updates and near-zero denominator/W policies. Actual transformed control envelope
selects 0/10/25/50/100 segments before the original polyline stroke writer. Invalid
domain uses the source polyline fallback; missing points/knots and invalid pen keep
the source early-outs. Public `SplineGeometry` also supplies original fixed100
source-path creation, domain selection, density and owned point evaluation.
Malformed safe indexing fails before publishing output; no foreign spline engine.

Canonical shaders and compiler defaults are unchanged. Hatch production support
is documented separately. Other external/native extension providers and source
qualification gates remain independent. No build, test, browser or benchmark was
run; only touched source formatting was performed.
