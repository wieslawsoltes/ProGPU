# Native typed scene builder

`native_scene::builder::NativeSceneBuilder` ports the owned native producer over
the original safe little-endian records. It does not bridge to the C++ engine.
The raw `NativeScenePacket` remains available for independent byte transport.

The core retains scene/resource generations, exact resource IDs, mutable brush
and text-style tables, source owner boundaries and sparse input metadata. Reset
reuses retained collection capacity. Resource/command append preflights grow
geometrically before related publication; no size-plus-one reserve loop is added.
Rust methods return the original error category as a `Result`, replacing the
mutable last-error slot, and returned indexes replace caller output pointers.
An invalid constructor is a failed result, never a partially usable recorder.

State producers preserve independent clip/mask/guideline policies and original
inactive fields. Static guidelines accept sorted infinities but reject NaN;
explicit physical offsets require finite coordinates and values within [-1,1].
Nearest selection retains lower-coordinate ties, original half-integer rounding
and the original float narrowing points. Self-copy has an explicit safe method;
cross-builder copies retain the exact payload and assign destination identity.

Save scopes retain actual source rectangles only for selected owners. Balanced
input-only scopes remain in CPU metadata but are omitted from raster serialization;
render-only scopes retain raster commands and independent input exclusion. Original
command IDs survive that filtering. Ordinary target Clear retains straight color
and remains independent of source transform/opacity. Recorded-command hit capture
retains original source frames, clips, native joins/caps, dashed traversal and
native quadtree order. `add_hit_test_index` retains complete typed DAGs, exact
containment and once-only references atomically.

The deterministic writer uses O(C+R+P) time and O(1) additional write storage.
`build_into` measures the complete stream and caller capacity before mutation,
zeros only the written prefix, writes all original alignment gaps and leaves the
caller tail untouched. It writes typed brush/style arrays directly without a
private stream copy. `build` returns one independently owned byte vector and
actual command/resource/brush/style/depth/arena/stream counts; reserve hints affect
capacity only. Serialized bytes do not assert renderer/device admission. They can
be passed to the separate original `NativeScene::new` admission stage.

The original multistep 3D producer may retain earlier material appends if a later
material or final mesh operation fails. That behavior is preserved rather than
changing post-failure stream identity. Shaped-glyph conversion can likewise leave
the already written scratch prefix; final recorded publication remains separate.
See `native-scene-builder-families-port.md`, `native-scene-builder-image-port.md`
and the layer producer notes for each family's source-specific rules.

Implementation and source formatting only. No builds, checks, tests, browser,
shader, benchmark, package or runtime qualification were performed. The full
original source/error/byte/ownership/SIMD/performance gates remain required.

## Original source SHA-256

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder.cpp` | `c3d052bd2d3e9d979f8ac3f46dabe84225433697b2e24a509dcfbb19adf6ef34` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_internal.hpp` | `ae9b617fe3298d9c6da5cdee913c232173526a0faa53a64365dc5bc742949ef0` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_capacity.hpp` | `9273f7f014d2fb2ba010fb2dde6054ff7673d5f6cbb32cb82e7a829973a03463` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_serialization.cpp` | `90e6281a889db0348f3e1a0e3e4ee669de12cfb2983d799859f23a4b61a57662` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_hit_testing.cpp` | `b45f92e7293c6a2efb84e45784ba4bf97e84768173d32a25e7adafc2c3b7e4e6` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.hpp` | `4125fabc4bd3fe5845f138c2d3f9d36b1ee6f0d5bf525bb86be9bb0ff228af3a` |
