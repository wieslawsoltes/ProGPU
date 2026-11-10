# Native image execution resource page

Implemented `native_scene/engine_resources` image/geometry/external/GPU modules.
`NativeImagePage::compile_frames` consumes the actual admitted owner and ordered
`NativeCommandFrame`, retaining original state/target metadata. The explicit-input
variant preserves separate caller ownership; `compile_bounded` admits the actual
remaining vertex budget before packed allocation. Source quadrilateral/patch order,
UV arithmetic, scalar ties/zero rounding, opacity/address/alpha metadata, image
suffix gates and all sideband roles are retained. Four independent corner lanes
use safe SIMD with separate original multiply/add operations.

`NativeImagePageEngine` performs complete page/device preflight, native channel
uploads, exact selected-view bindings, shared samplers/indices, native matrix and
effect uniforms, and both original Gaussian passes. Live blur re-evaluates external
pixels every replay; no source resolver runs in encoding. Prepared generations and
actual producer leases survive queue completion. The separate original identity
module owns cache hashing; no substitute hash or inferred source generation exists.

Nested picture execution is a required typed `NativePictureImageResolver` supplied
by the command engine. It preflights child work and returns real rendered RGBA8
storage with its owner, once per selected resource. The page counts the output
allocation; child preflight excludes that output. The final ordered renderer,
mask/clip dispatch, complete engine publication and original package/application
qualification remain independent integrations. Resource preparation is not a claim
that every native command family renders through this adapter.

Source formatting only; no checks, builds, tests, fixtures, browser/graphics runs or
benchmarks. No new artifacts were produced.

Original SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_update_execution.cpp` | `ef4cb61079f0367f98dc11940277fff810728a54a6058b13215bd3e016cfa4d5` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_replay.hpp` | `daa885dc4c4238e9bcd4ce8df811a61030319b07a0186bd099f167005dce876b` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image.cpp` | `0fba76f13ece2b5a6b726429951c014997303f0fd2a73c8cd90fffad12c056d4` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image_resources.cpp` | `3d4dcfd1105259210d9482160a0a7dbde5f9dca4d186efa84847eb4adb896859` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image_effect_resources.cpp` | `91c50901679c0c0e4975ccf4ef947433a77141d13c62fdeb6fce20ee90905287` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image_blur_resources.cpp` | `c0295bef019ced851fbdf3f1fba291d6f94a5328fc410745109f619ab95034c7` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp` | `2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5` |
| `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp` | `85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672` |
| `src/ProGPU.Native/src/Backend/progpu_native_image_layer_resources.cpp` | `8d8fb8816f1db86563b5ab1551446f928bf4915021f095cfc1b8737d2c16d523` |
| `src/ProGPU.Native/src/Backend/progpu_native_image_execution.cpp` | `09135c8ead728a45d4e2a65ab9196b2a2a3fbff535b65d8f01816ef37c6b547e` |
