# Mixed static DXF production

`StaticDxfBuffer` connects original static analytic, triangle/quad, point/grid,
vertex mesh, line/polyline, quadratic/cubic, path, spline and Hatch producers to
one immutable geometry/material/coverage generation. It reuses the already
ported exact command, stroke, direct-rounded and PathAtlas producers. Source
records and all side arrays remain independently owned; private execution
lowering cannot change the source archive.

The provider overload retains original selected arrays. The command-list
overload has no provider and ignores side-buffer counts while retaining inline
points/knots/weights, as the original static compiler does. Null-brush meshes
retain the original no-draw branch. Original opacity/mask/blend scope commands
have no static compiler branches and do not change semantic alpha1. Supported
binary inner clips do not become draw-time scissors. Their original float
intersection stack drives the actual prepared path-quad disjoint test before
stroke publication. A rejected fill retains its original material registration
and atlas request, suppresses its following stroke, and publishes no draw quad.
Private raster-only temporaries are removed from the final buffer; index and
embedded curve/cap/join base ordinals are rebased to the actual retained vertex
order without moving geometry or altering curve coordinates.

All static vertices receive `+200` only after complete geometry/raster planning.
Ordinary source DPI determines path coverage; static zoom remains independent
text-derived path gamma input and original retained bookkeeping normalization.
Direct path specialization uses the supplied actual source offscreen policy.
Static active-raster flags use the source's identity camera, not the command's
ordinary camera, and bypass clip/exact-load admission as in the original static
compiler. Original source brushes remain unchanged. Known private occurrence
ranges remove only the common helper's100 marker before the source200 pass;
shape values are never used to infer source history. Static extensions preserve
flush boundaries, including zero-output commands.
Both raw Hatch and explicit extension3 become dedicated Hatch ranges. Original
static Spline extension4 draws its compiled Vector interval; ordinary archive
extension4 remains its separate compile-only/no-op-render route. Private
source-over barriers preserve these static flushes without pretending they are
original archive commands.

`StaticDxfEngine::upload` owns one common vertex/index/material/Hatch-buffer
generation and uses the existing canonical GPU path/Boolean evaluator to populate
one retained R8 atlas. Coverage submission occurs during upload; target drawing
is never submitted implicitly. `prepare` freezes original viewport camera,
placement, projection and224-byte uniform metadata per actual target. Pipelines
use unchanged Vector/Hatch shaders, default compiler options, exact format and
original1/4 sample count. Vector consumes the original outer fixed-factor blend
policy and an optional typed resolved target-local mask. Hatch consumes neither
generic mask nor blend and retains original straight SrcOver. Both preserve the
actual outer binary scissor and ordered ranges.

Upload reuse is keyed by the exact immutable generation with weak cache entries.
Every prepared target owns its resources and source metadata independently of
the engine, cache eviction, later preparations and canceled candidates.
Queue-completion leases retain source image producers. Metrics separately report
static uploads, coverage textures, per-target uniform/mask work and cache hits;
empty vertex/index bindings use explicit4-byte Rust placeholders rather than
claiming identical managed allocation counts.

Original static text, retained glyph records and actual frozen atlas generations
now connect through the same engine; see `static-dxf-text-port.md` for source
ordering, ownership, original rejection gates and explicit atlas compilation.
Remaining boundaries are explicit: nested source pictures, external providers, unrelated static3D/extensions,
non-axis inner geometry-clip producers, rare stroke-to-filled-outline fallbacks
inside an active nonlate clip, and nonzero-origin outer mask records. This
production API now has the ordered Scene extension0 adapter described in
`static-dxf-scene-port.md`; it does not claim a complete original DxfStaticBuffer
replacement. No builds, tests,
browser runs, benchmarks or pixel qualification were performed.

Original source SHA-256 receipts:

| Source | SHA-256 |
|---|---|
| Scene/Compositor.cs | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| Scene/DxfStaticBuffer.cs | `1f150883f9dfa947debc9d1ac1c615eac663c8c08bbf5fc81d770148754da4bf` |
| Scene/Extensions/HatchExtensionPipeline.cs | `c8ac3e1dff8d8329d25514fee31d2c27d76f80ad833872778199811eb6bea68f` |
| Scene/Extensions/SplineExtensionPipeline.cs | `b2c1aade24d8a3758fcd7eda27f433c8c409b69fd05620349f2ad81d8d7ea3c3` |
| Backend/RenderPipelineCache.cs | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| Vector/PathAtlas.cs | `8b0c65142b0156f505374f1d20a6feff3eea1ed7bec23a1b8ce6f405e7414ccd` |
| Backend/Shaders/Vector.wgsl | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
| Scene/Shaders/Hatch.wgsl | `9895f6f7f4b5bb1f213f9a95625d8de8603ff0f7e32e2881a4b930d456714ab5` |
