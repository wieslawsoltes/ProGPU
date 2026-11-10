# Original retained picture interaction and bounds

Source SHA-256:

- `src/ProGPU.Scene/GpuRenderCommandHitTestCache.cs`: `011ac58ddc898022d61fb0b983feba244a1863cd5c00de00586d08a64d5f0366`
- `src/ProGPU.Scene/GpuRenderCommandHitTestCache.SourceJoins.cs`: `e6de9555d46ef8caa989c058a778e4a61db660037a77ba3674256e82a4877221`
- `src/ProGPU.Scene/GpuRenderCommandHitTestCache.SourceVisual.cs`: `8ea05126353ff7fb83ca752989115152a6778e95739ec7fe7636d5f456704cf3`
- `src/ProGPU.Scene/GpuPictureBounds.cs`: `887424049406f942032bfe265f33d857f5a8515bff123706778183ef0af4862d`
- `src/ProGPU.Scene/SourceHitTestGeometry.cs`: `2410ff45d75c896485358889764fe13f2245f4d496df41d175dcb8c77bc109e9`
- `src/ProGPU.Vector/GpuHitTesting.cs`: `8435e814c26761b3a3454fdf773792cdfbf31b4490f65543fd1800641b9d7411`

`ArchiveHitTestCache` ports the ordinary retained command builder, source IDs,
quarter/fractional painter depths, opacity threshold, permissive ordinary clip
stack and shared path compilation. Primitive payloads remain distinct from their
clipped envelopes: 128-byte `HitPrimitive`, 32-byte `HitNode`, and the existing
48-byte segment wire. `ArchiveHitIndex` owns all arrays and actual source leases.
Its original quadtree preserves retained-versus-child assignment, child quadrant
order, primitive order, unsplit-child termination and the empty root node.
`from_primitives` retains caller-owned raw arrays, including unknown raw kinds and
flags, independently of command-builder admission; no source owners are invented.

Command lowering includes analytic fills/strokes, actual image-patch destination
quads, point batches, triangle/quad/mesh fills, all owned polynomial/rational/arc
paths, explicit source joins, original dash preparation, fixed/hairline device
geometry, fixed 100-segment spline input and 128-segment line-series chunks.
Device arcs preserve original 32..4096 subdivisions and quarter-pixel error
arithmetic; the original device switch's absent rational branches remain absent.
Explicit joins retain flat individual curve bodies and the actual join triangles,
including gap/closed-seam/cap/degenerate rollback policy. Existing original vector
compilers, dash preparers and join writers supply those exact owned primitives.
Unresolved Boolean reconstruction keeps the original failed-compile behavior;
ordinary geometry clips retain their original inherited-bound fallback, which is
not the stricter independent source-Visual clip contract.

`ArchivePicture` and `PictureArchive` expose `interaction_index` and
`raster_bounds`. Bounds retain original strict two-dimensional family admission,
64-level active-owner traversal, per-picture clip/opacity/mask/blend balancing and
the DrawPath-specific transform composition. Empty success returns a zero extent;
missing hinted geometry/DPI remains unsupported. Original DrawText estimated bounds
and raw glyph-position padding are preserved as compatibility behavior, not
relabelled as authoritative source glyph ink or raster pixels.

`CapturedHitCommand` carries actual source-only metadata independently of archive
v1-v10: unsnapped source primitive overrides, excluded auxiliary caps, source
opacity, image-input scope ownership, and point-only/region-only scopes. These
retain source owners and original finite/extents/type admission before input
publication. Failed captures require `clear`; image and point scopes must close
before index publication. Raw archive transport does not infer or serialize these
facts. The following `source-visual-input-port.md` increment adds the full explicit
source Visual walker, callback/snapshot contracts, strict clips, authoritative
source glyph ownership and actual paragraph-backed hinted interaction binding.
Platform producer implementation and source qualification remain independent; no
visual-size rectangle or alpha-mask envelope substitutes for those contracts.

Full matrix inverse rows preserve residual source components and the original
singular-to-identity rule; they do not reuse the stricter image-filter inverse
admission. Runtime-specific floating arithmetic, original input/pixel/provider
qualification, and GPU query execution evidence remain pending.

No compiler, tests, fixtures, browser, benchmark or query execution was run.
Only touched source formatting was performed; no build/result artifacts were made.
