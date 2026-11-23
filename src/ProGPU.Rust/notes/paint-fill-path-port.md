# Original paint fill-path producer

Ported original `SKPaint.GetFillPath` over `RetainedPath`/`PathBuilder`: source
path-effect application and paint adjustment, fill/stroke/hairline behavior,
resolution/matrix/cull overload policies, immutable replacement and original
builder append/failure-copy contracts. Output candidates publish atomically.

The actual producer preserves eight-level ordered quadratic/cubic subdivision,
original flatness thresholds and arc utility, bounded one-slot point cache,
duplicate/degenerate treatment, two-arc oval rings before dash dispatch, continuous
per-contour dash phase, original ordered stroke bodies and shared bounded cap/join
triangles. Original Skia paint cap/join mappings remain separate from vector pen
extensions. `Paint::normalize_stroke_winding` uses ordered signed-area selection
and actual segment reversal, keeping source geometry immutable. Independent XY
arithmetic uses SIMD; subdivision, cumulative dash traversal and area reduction
retain their required dependent order. A bounded work budget rejects excessive
production without changing a published destination.

`Paint::fill_path`, `fill_path_for_matrix`, `fill_path_with_cull`,
`replace_fill_path` and `append_fill_path` are explicit CPU geometry queries;
ordinary retained drawing keeps its existing GPU path. Native rational descriptors
and unresolved deferred Boolean generation require their actual source producers
and reject explicitly. Source conic polynomial spans remain the actual flattening
input; reversed derived contours do not invent conic source witnesses.

Authoritative original sources (SHA-256):

- `src/SkiaSharp/SKPaint.cs`: `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff`
- `src/ProGPU.Vector/ArcSegmentGeometry.cs`: `847d2cab48f10fe903eafff7ac56da9a36f4be77ad6932258c45b3ac6b31428e`
- `src/ProGPU.Vector/StrokeJoinGeometry.cs`: `6fb7218ec1d12df2b9b1fb9c0777633f1ce74eec90260fd23c6e8ff85cad86ec`
- `src/ProGPU.Vector/StrokeCapGeometry.cs`: `c47564c76c3eedaf4d759c532f273f1d66b92ef5f70591dbab558d38ca79df81`
- `src/ProGPU.Vector/TransformMetrics.cs`: `12271082b95c438ed47d041e961e757e5a0b94bc56334a978f5acc2f17a838de`

No foreign implementation. No build, tests, fixtures, runtime, shader or performance
validation was run: implementation first remains the explicit task policy.
