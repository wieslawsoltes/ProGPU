# Original archive analytic vector replay

`picture_archive/vector_draw.rs` and `scene/archive_vector.rs` connect the actual
owned archive command to original direct vector compilation, without converting
these command kinds into paths, raster images or vertex-color mesh substitutes.
This follows the existing [architecture research](../DESIGN.md#architecture-research-2026-11-08)
immutable source/material/target separation. Only original ProGPU source was used.

Authoritative source:

- `src/ProGPU.Scene/Compositor.cs`, SHA-256
  `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd`:
  `CompileFillTriangleCommand` (12183), `CompilePointBatchCommand` (12306),
  `CompileFillQuadCommand` (12427), `CompileDotGridCommand` (12475),
  `CompileDeviceDotGridCommand` (12523), ellipse fill (12587), circle radius
  selection (12694), exact affine admission (6181), shape encoding (550), and
  the actual identity `ClampToClip` body (16548).
- `src/ProGPU.Vector/TransformMetrics.cs`, SHA-256
  `12271082b95c438ed47d041e961e757e5a0b94bc56334a978f5acc2f17a838de`:
  existing owned `Matrix4::stroke_scales` is reused for original local padding.
- Archive source record/constructor provenance is retained in
  [picture-archive-material-geometry-port.md](picture-archive-material-geometry-port.md).

`ArchiveVectorDraw` retains the exact `Arc<ArchiveCommand>`, complete source point
buffer, original selected inline/provider interval, full matrix, semantic opacity,
and actual resolved vector brush. Inline nonempty points win; otherwise positive
provider counts select the exact offset/count. Zero or negative counts keep the
original empty behavior. Optional explicit `with_brush` supplies an actual source
resolution; unresolved theme/backdrop resources never receive a fabricated color.
Placement composes the complete actual target matrix and opacity without changing
raw source records or positions. Accessors permit the explicit archive capture
bridge to retain the actual owners.

Triangle/quad fills use shape 7 and original local brush coordinates. Point batches
retain local unscaled padding, original round/square selection and shape 19/20
hairline vertices at the same transformed center. Ordinary points keep shapes 0/1.
Dot grids retain shape 21, spacing/radius and phase fields; device dot/line grids
retain shape 25, negative radius encoding for lines, source finite rectangle and
invertible-affine admission, epsilon residual tests, and original integral 1..100
line frequency. Aliased mode adds exactly 1000. Quad winding and triangle ordinals
remain unchanged. The full point batch appends using one capacity reservation and
safe SIMD source XY projection, with no per-point allocation.

Ellipse and circle **fills** retain the separate original center-minus/plus-radius
then padding expressions and independent local texture coordinates. No XYWH
reconstruction changes far-edge float bits. Singular-scale padding keeps the
original 1.5 fallback; circles use RadiusX for both axes. Pens remain the separate
original retained stroke path owned by the peer replay layer. The source
`ClampToClip` calls are identity operations: this port does not infer a clamp from
the call sites. Actual Scene masks/scissors retain independent clipping.

Scene integrates the new typed command directly with material registration,
original vector blend selection, ordered mesh emission, premultiplied mask-chain
materialization, source capture placement/opacity and source preflight. No new GPU
pipeline or shader substitute was introduced; the existing original vector shader
consumes these exact shape records. The source command remains retained even when
its source early-out produces no draw. Device-grid invalid frames preserve the
original skip policy. Existing final finite-vertex admission remains unchanged.

Only O(1) work is done for fixed analytic commands and O(N) for point batches.
No performance or pixel claim follows. Archive acceptance remains independent
from rendering admission and from provider/package/application qualification.
Per the mandatory port-first instruction, no compilation, tests, fixtures, lint,
browser, benchmark or validation ran. Only new source formatting was performed;
no build/benchmark/trace artifacts were generated.
