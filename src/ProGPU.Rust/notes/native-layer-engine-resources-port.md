# Native materialized layer resources

Implementation only; no builds, tests, shader/runtime execution, benchmarks or fixtures run.

`engine_resources::layers` consumes the original preflight `NativeLayerBudget`/`NativeCacheBudget`. It preserves stable identity-to-slot mapping, reservations for identities appearing later in the incoming inventory, clearing retired cache identities, actual pooled allocation dimensions and separate logical source extents. Whole texture inventory is reserved before GPU allocation. Actual Arc allocation owners retain each residency charge through retired prepared generations and queue completion.

`NativeLayerTargetPool` realizes original target-format single-sample color textures with RenderAttachment/TextureBinding/CopySrc/CopyDst, two clamp level-zero samplers/bindings, independent Depth24Plus slots, and three RGBA8 single-sample effect textures with TextureBinding/StorageBinding/RenderAttachment and linear output bindings. Exact `NativeGpuUniforms` use allocation width/height and scalar raster DPI; shared `NativeFrameBindings` retain actual material buffers. Uniform bytes, material owners, color/depth/effect allocation sizes, texture generations and output-cache invalidation remain independent. Old owners are never explicitly destroyed while retained.

`NativeLayerPassState` selects original root clear/preserve, transparent versus ignored-alpha layer clears, actual background-initialized loads, depth clear-one versus load, cold content depth resets, root viewport certificate and target-Clear identity checks. It is working encoder state, discarded on failure. It does not infer an opaque materialized target from root clear metadata. Parent owns actual ordered capture/content/effect/composite execution and whole-scene admission.

Original output-cache keys and hit/record/invalidate rules are retained. `pool.output_updates(prepared)` snapshots current committed keys; actual encoded outputs update that candidate; `commit_submitted` publishes only after successful submission and rejects stale pool generations. No cache key creates pixel output. Texture owners can become `NativeImageTexture` sources while retaining their exact residency/source lease.

Ordinary and transformed composite quads preserve original unsigned source-target subtraction, independent source DPI, exact float expression order, UV extents and premultiplied consumer metadata. Independent transformed corners use typed SIMD/scalar-reference production. `NativeLayerCompositeEngine` preserves original saturating geometric buffer growth and the shared exact raw-FNV upload key; changed immutable generations use fresh buffers, stable replay uploads nothing. Actual allocations/uploads are reported. The parent budget owns frame/material/composite buffers separately from texture residency; four-byte texture/depth charges are source accounting, not a claim of backend physical allocation.

Remaining integration: ordered layer/background/effect kernels and final target submission are parent engine work. This resource producer does not qualify native packages, browsers, source applications or original pixels.

## Original source SHA-256

- `src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp` — `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3`
- `src/ProGPU.Native/src/Backend/progpu_native_layer_composite_execution.cpp` — `c3605dd11b5551e827f8689c6579bfc584d7219e61fe9c1fcb71f96b82a09f57`
- `src/ProGPU.Native/src/Backend/progpu_native_effect_execution.cpp` — `a2e42e6680497a0477c4bd8a94615b08766f91a3f6fdf57ff6c5de709489ee9f`
- `src/ProGPU.Native/src/Backend/progpu_native_pipeline.cpp` — `064c54ee475982af9dc6d6191ca5769582b6e82be7b0edb8f4dcbdacd48eecb4`
- `src/ProGPU.Native/src/Backend/progpu_native_image_layer_resources.cpp` — `8d8fb8816f1db86563b5ab1551446f928bf4915021f095cfc1b8737d2c16d523`
- `src/ProGPU.Native/src/Backend/progpu_native_buffer_capacity.hpp` — `9edcc2151b7c2fff7fd54f7c517aade131cec79d6021a3d0aaaaa3ebaadc9562`
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` — `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae`
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_effect_cache.hpp` — `4334b792e2b3562b76182599ae70e9c32477d21cd67668efb8eafd3e1e537667`
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_effect_cache.cpp` — `a0d006807459e2887e1f2944722b052834a1a0935d2dd269c12165ac9b6a2a4d`
