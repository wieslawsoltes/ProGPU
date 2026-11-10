# Native retained 3D resource pages

Implementation only; no compiler, tests, shaders, GPU execution, fixtures or benchmarks run.

`NativeThreeDPage::compile_frames` retains the admitted scene and every actual ordered 3D frame. Whole inventory and storage bounds precede record allocation. It uses the original **unlocalized source state**, while camera viewport mapping retains full unclamped command bounds and subtracts actual target origin. Mapped presentation, per-point guidelines and per-draw masks keep their original execution rejection. The separate coarse native admission estimate is retained alongside actual packed storage/upload sizes.

The CPU page owns exact 176-byte cameras, 128-byte lines, 272-byte meshes, 80-byte adjacency edges, original 48-byte vertices, indices, 80-byte lights, 256-byte material brushes and 32-byte stops. Triangle strips preserve alternating winding and skip repeated-index triangles; edge topology, 6/18 vertices and independent occluded passes survive. Normal transforms are copied, not recomputed. Mesh materials retain original brush offsets/stops, default white material and exact primary external image ID/generation/view. `compile_frames_with_preference` exposes SIMD/scalar-reference CPU record production; four independent matrix columns keep original ordered multiply/add accumulators. Dependent stream, descriptor and topology traversal stays scalar. GPU rendering remains separate.

`NativeThreeDPageEngine::preflight/prepare` realizes the nine original storage bindings, default sentinel and only demanded canonical `Native3D.wgsl` pipelines. Lines require group zero only. Mesh culling, straight alpha, Depth24Plus, sample count one and edge depth-write/compare policy remain original; managed Viewport3D shaders, depth/MSAA and material records are not substituted. All actual device/storage/selected-view gates precede mutation. Candidate publication is atomic. Cache keys use the shared original family hash plus resource scope, frame DPI/size/draw count and exact retained sideband bindings. Warm preparation uploads nothing and reports zero new allocations. Every published generation owns storage, bindings, pipelines, selected texture producers and source stream through explicit queue-completion retention.

`PreparedNativeThreeDPage::encode_draw/encode_bundle_draw` replays a source command batch without source callbacks or uploads. `NativeThreeDDepthTarget::new` allocates the original one-mip RenderAttachment-only depth slot; target pool identity, depth clear/load/store, ordered scissor, root/layer/cache budget and whole-scene transactional publication belong to the parent native execution engine. Depth budget uses the original four bytes per pixel; backend physical Depth24Plus allocation is not claimed. This page alone is not an integrated native renderer or original platform/package/pixel qualification.

Additive integration: engine_resources exports these page/record/metrics/engine/depth types; peer glyph/path exports and unchanged native image-sampler visibility are connected. Root supplies the canonical shader/package include. Existing released image behavior is unchanged.

## Original source SHA-256

- `src/ProGPU.Native/src/Backend/progpu_native_3d_execution.cpp` — `1818f7668dad9ca54debc36503b4574e9d0036392363d7daeda8f100b106cf64`
- `src/ProGPU.Native/src/Backend/progpu_native_3d_execution.hpp` — `f02c0d8e23fe31d785f8d332abdefafa61678715605ae071ae200782fb07f10e`
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_replay.hpp` — `daa885dc4c4238e9bcd4ce8df811a61030319b07a0186bd099f167005dce876b`
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` — `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae`
- `src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp` — `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3`
- `src/ProGPU.Scene/Shaders/Native3D.wgsl` — `96648352f022fd294f1cf8892e5839185a33a147a64e660fb4a795bb73eac970`
