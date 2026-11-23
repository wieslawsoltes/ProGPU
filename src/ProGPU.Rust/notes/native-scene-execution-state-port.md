# Native ordered execution state and targets

`NativeExecutionCursor` retains one admitted `Arc<NativeScene>` and advances the
original target and saved-state cursors in command order. Each immutable command
frame retains the source state, localized state, actual target/presentation, raster
DPI and the current materialized attachment's ignore-alpha policy. No root-clear
or ancestor opacity is promoted into target opacity proof.

The complete semantic-state source is ported: original per-axis guideline
selection and physical offsets, uniform versus per-point/composite policies,
state application to source families, nearest-even image placement, actual
presentation and scalar localization, binary/storage/damage scissors, exact axis
clip edges, mapped effect distances, mask UV inversion, picture-source frames,
mapped-layer admission and sampled-shader/local-cache target domains. Four
independent projection lanes use the crate's safe SIMD vector implementation,
preserving separate multiply/add and original narrowing order.

`NativeSceneFrame` uses explicit descriptor-prefix variants so legacy fields stay
unread and a partial extended presentation cannot become a complete descriptor.
It does not contain a fabricated foreign target-view pointer. Presentation
admission remains separate from frame identity, device/provider and family
execution gates. Source coverage placement is owned by the path/glyph resource
producer rather than being reconstructed from these target bounds.

Compilation, live/pooled-layer and shared-cache budgets retain original limits,
slot histories and pooled-failure state. Residency reservations are move-only
owners; actual live allocations keep their charge through cache eviction and
submission retention. Rust bounds checks guard invalid direct helper use without
foreign pointer reads.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp` | `2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.hpp` | `4125fabc4bd3fe5845f138c2d3f9d36b1ee6f0d5bf525bb86be9bb0ff228af3a` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_budget.hpp` | `58c1bf76b9ef70522dd549624c7e8fe30eaf0523aef8965e21691c0977433167` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect_resource.hpp` | `fda73197f0b2e6d1d0f1ea14d19824a30760fd1dc4a458f479da4645ac6e8467` |
| `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp` | `85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672` |

Source implementation and formatting only. No build, tests, browser, benchmarks
or shader validation were run. Final native execution integration and original
provider/source qualification remain separate.
