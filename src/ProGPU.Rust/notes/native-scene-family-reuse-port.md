# Native independent GPU family reuse

Implementation only. No builds, tests, fixtures, browser runs, benchmarks or
runtime validation were performed for this port.

Original source provenance:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp` | `759fa316f751c098485eb89aab0508ed059ee6c1ed64516862a9815ec9c8b6d5` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_update_execution.cpp` | `ef4cb61079f0367f98dc11940277fff810728a54a6058b13215bd3e016cfa4d5` |

The original render source independently owns brush/gradient and text-style
buffers (2115–2185), presentation-keyed analytic pages (2227 onward), path pages
with Boolean inventory (2628 onward), glyph pages with all seven source-indexed
inventories (2896 onward), image pages (3255 onward), and the separate retained
3D implementation. Original glyph paint texture binding includes actual selected
views and samplers, independently of the record count.

`engine_gpu/resources/reuse.rs` now retains independent last-submitted
material/vector/path/glyph/image generations. Absent families do not evict their
original retained entries. Native explicit empty-table backing has a separate
physical fallback owner. The renderer publishes through
`NativeScenePageEngine::commit_submitted` only after successful replay submission;
an abandoned prepared coverage page cannot become a cache hit.

Original family hashes, presentation/DPI/target values and count inventories
remain the primary keys. Actual layout construction, immutable engine flags,
antialias policy, resource scope, atlas sizes, path phase selection and external
view/generation tables retain their independent ownership checks. Brush/gradient
and style buffers reuse separately. Every returned typed wrapper owns the NEW
CPU page and scene; actual immutable GPU handles survive unchanged. This includes
current command-index descriptors for image draws and current source owners for
glyph paints and target frame bindings.

Submitted path/glyph atlases carry an explicit completed-coverage state when
rebound. Their output is retained, and original compute/fragment/explicit-CPU
coverage does not run again. Existing scalar/SIMD/binary flags and all source
pixel gates remain unchanged. Image textures, source captures, samplers and
parameters retain their actual owners; the original two-pass live image blur
still executes per replay. The already implemented native 3D key and immutable
buffer cache continues to rebind its own new source owner without a substitute
pipeline or shader.

Metrics report zero actual upload/allocation work for retained families, including
glyph paint records and atlas textures. Buffer-identical frame/glyph bindings
retain current CPU ownership while cloning their actual GPU bindings. Whole-plan
and render-bundle reuse remain separate: a new plan still rebuilds its ordered
operation inventory and performs complete current-source/device preflight.
CPU geometry lowering and ownership-wrapper allocation are not elided by this
GPU family cache. No measured performance or provider/package parity is claimed.
