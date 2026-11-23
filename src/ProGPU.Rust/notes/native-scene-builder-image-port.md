# Native scene brush/image producer port

`native_scene/builder/brush.rs`, `builder/image.rs`, `builder/image/copy.rs`
port the original owned native recorder, with typed safe wire values and fallible
storage publication. `semantic/image_payload.rs` is the independent original image
execution/builder payload parser; it is intentionally not added to stream admission.

Implemented: bitwise solid reuse; all brush local stop inventories and canonical
rebasing; RGBA8/BGRA8/R8 copies without pixel conversion; external image identity;
owned picture bytes and distinct presentation; monotonic image updates; original
contiguous image-to-patch batching and hit-owner boundaries; full suffix/patch
admission; exact source vertex-color metadata; complete full-image-copy recognition;
source builder resource copy/move; SRC copy commands and clip-only suspension with
append-only rollback. Original transform/opacity/sampling bit identity is retained.

Public suffix inputs use `NativeSceneImageOptions`; full-copy outputs use
`NativeSceneFullImageCopy`, with a distinct presentation-aware query. Existing
source callbacks and GPU leases are not fabricated by external-image metadata.
Nested picture bytes retain original deferred ingestion admission. The standalone
scene byte serializer, scope methods and later native execution are separate owners.
No validation, builds, tests, browser runs, fixtures or benchmarks were run.

Original SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_brush.cpp` | `6b23ed8ad44c1cbc56f6f9fca967dc5b5ea6a26fcc94cd63138a4dbd8635cd03` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_image.cpp` | `c65b7f68453c9fedd93e0dd9be55211338f2b2e75d246b3a575ba40086ec95be` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_internal.hpp` | `ae9b617fe3298d9c6da5cdee913c232173526a0faa53a64365dc5bc742949ef0` |
| `src/ProGPU.Native/include/progpu_native_scene_builder.hpp` | `2c2c0395ad6e834c452932a0b56cb67f17d3407af4fe707d9bffc96cb24664f7` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image.cpp` | `0fba76f13ece2b5a6b726429951c014997303f0fd2a73c8cd90fffad12c056d4` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image.hpp` | `7db83407cb4c1afab2ccc1a30ee2ddcab99f972819c7607e48b0336a900039aa` |
