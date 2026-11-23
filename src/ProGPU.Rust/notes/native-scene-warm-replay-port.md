# Native submitted replay reuse

Implementation-only port of the original retained native render-bundle branch.
No build, test, browser, benchmark, pixel or package validation was performed.

## Original sources

- `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp`
  SHA-256 `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae`:
  bundle admission at 2188–2223, immutable bundle/mask/effect compilation at
  4380–5976, per-replay image blur at 3944, and the unchanged ordered effect,
  target-clear and fresh RGB execution loop.
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_update_execution.cpp`
  SHA-256 `ef4cb61079f0367f98dc11940277fff810728a54a6058b13215bd3e016cfa4d5`:
  original immutable source/content invalidation policy; the existing Rust
  cache owns its exact external-view and family-key implementation.

## Connected implementation

`engine_gpu/renderer/reuse.rs` retains the actual successfully submitted
`NativePreparedReplayResources`. A hit requires the same plan `Arc`, same
submitted page `Arc`, complete external metadata and actual view identities,
same native layout construction, matching device/format, and the exact live
layer pool owner/epoch. Equal bounds or a matching source hash alone never
establish a hit.

Warm rendering retains original ordinary render bundles, frames, layer quads,
mask captures, shader dependency textures/parameters, builtin effect bindings,
backdrop programs and advanced-composite resources. The first submitted use
completes immutable brush/composite masks; subsequent reuse does not rerun those
programs or invoke nested source callbacks. Texture storage remains owned by the
same captured generation.

Every occurrence recomputes output-cache branches from the actual committed
journal. Eligible RGB operations prepare fresh coverage/destination snapshots;
skipped cached bodies prepare none. Live ordinary image blur still encodes each
frame. Clear and actual builtin/shader/composite operations retain their source
ordering and output-cache keys. A final root-copy wrapper binds the newly
supplied attachment, retaining its real owner instead of reusing an old surface
view. Reuse publication follows successful queue submission only.

Warm metrics report zero immutable preparation uploads/allocations, while fresh
RGB uploads, live image passes, Clear uploads and actually encoded effect passes
remain observable. The source frame counters and independent subtree metrics
retain the existing original distinctions.

This is exact-owner replay reuse, not cross-generation family rebinding. The
latter remains independent despite the existing original family identities.
CPU ownership-vector copies still occur when constructing a new frame wrapper;
no claim of allocation-free CPU replay or measured performance is made.
