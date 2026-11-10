# Native retained effects and advanced layer blend

Implementation only; no compiler, test, shader, runtime, browser or performance validation ran for this increment.

`engine_resources::effects::NativeEffectPlan::from_resource` retains the admitted native scene, raw chain header/revisions and each raw GroupEffect. Physical X/Y DPI mapping and the original three-texture schedule reuse the already ported original `effects::EffectPlan`; the native logical scalar gates remain independent. Gaussian/box blur retain their exact 16-byte records, drop shadow its 32-byte record, and both blur passes remain present even for zero shadow radii. Uniform slots are zero-filled at 256-byte spacing.

`NativeEffectEngine::prepare_for_plan` consumes the original whole-scene layer plan plus actual `Arc<NativeLayerTarget>` owners. Its aligned slab retains the complete preflight pass capacity, including unused zero slots after actual cache omissions. It creates no effect textures, uses the actual color input and three RGBA8 layer intermediates, and encodes canonical horizontal/vertical/shadow passes with original 16-by-16 dispatch. Exact repeated binding pairs within a program share groups. Public preparation of an independent request inventory is also available. Prepared generations own source streams, buffers, groups, pipelines and target leases; encoding and per-step encoding do not submit or call providers.

`engine_resources::advanced_blend` retains the exact 96-byte native sampling record. Modes preserve ordinary destination blend, byte/nonbyte axis background replacement and independent axis coverage composition. Sampling origins subtract separately converted unsigned coordinates; empty semantic sampling remains zero while physical resolve scissors use the source one-pixel minimum. Consecutive axis ancestry comes from the actual ordered producer; first-operand C++ min/max ties are retained when intersecting child with its qualified parent.

`NativeAdvancedBlendEngine` preflights all actual source/parent owners, masks, selected effect outputs and original quad ranges. It shares native layer texture/frame/residency owners for its two explicit physical scratch slots; these slots do not describe source scope history. Candidate pools retain old allocation owners and publish only after successful preparation. Coverage is an independent R32Float render attachment, textureLoad-only, without a float32-filterable requirement. The original ordered passes are source/coverage resolve, canonical AdvancedBlend into full-target scratch, then Src replacement of the parent (RGB-only when the actual target ignores alpha). `PreparedNativeRootCopy` retains the real final attachment and performs the original transparent-clear isolated-root replacement. All prepared forms retain their complete owners through queue completion.

Actual frame/material/upload/allocation counters are exposed separately from replay pass costs. Stable prepared generations replay without uploads; a new immutable preparation owns its own uniform buffer rather than mutating an earlier prepared generation. Scratch textures and frame buffers reuse existing exact-size/byte identities. Native output cache identity/hit/publication is the existing `layers::NativeLayerOutputCache` and `NativeLayerTargetPool` submission transaction; this module never invents rendered cache output.

Remaining integration is the outer native ordered executor's actual cache-hit selection, scope/mask ancestry, effect requests and final attachment submission. Native WPF shader effects remain the independent shader adapter, not a built-in blur substitute. Advanced source resolve accepts the original single sampled-mask binding; an analytic chain must first be materialized by the native mask producer. Public CPU descriptors do not certify platform source pixels, providers or packages. Native/browser qualification and performance remain deferred.

## Original SHA-256

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Backend/progpu_native_effect_plan.cpp` | `fb737c514ac7e4979f2b07bc223d80dc1141ee97869b1d0fe5d1780f2e27b23b` |
| `src/ProGPU.Native/src/Backend/progpu_native_effect_plan.hpp` | `2de624832f22378fe73d3fdea67530059dc0d01a14da8ed69226d312c2b80e18` |
| `src/ProGPU.Native/src/Backend/progpu_native_effect_execution.cpp` | `a2e42e6680497a0477c4bd8a94615b08766f91a3f6fdf57ff6c5de709489ee9f` |
| `src/ProGPU.Native/src/Backend/progpu_native_advanced_blend_execution.cpp` | `44578984717579eb89a2c38ac62f097b5219211b963e8790dcc88092753e7446` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp` | `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_composite_execution.cpp` | `c3605dd11b5551e827f8689c6579bfc584d7219e61fe9c1fcb71f96b82a09f57` |
| `src/ProGPU.Native/src/Backend/progpu_native_gpu_records.hpp` | `5f1eda15324be086c0b8880ee432e1244f2797f32f0137dd1d8a636149586311` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_effect_cache.cpp` | `a0d006807459e2887e1f2944722b052834a1a0935d2dd269c12165ac9b6a2a4d` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_effect_cache.hpp` | `4334b792e2b3562b76182599ae70e9c32477d21cd67668efb8eafd3e1e537667` |
| `src/ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl` | `7ffc9636c103bddbbf13e7ba5fc253d4e9a02c26478edc5e5a38ef9a81de8834` |
| `src/ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl` | `fc700b1b057b369588960ce485d8bf07a0cb9a6f06e8611f794c1d990f32080c` |
| `src/ProGPU.Compute/Shaders/DropShadow.wgsl` | `475756cd86b206afa45725603817aed8f01a1dd09e52d3e621a3759d878b6e80` |
| `src/ProGPU.Backend/Shaders/AdvancedBlend.wgsl` | `f4143798b7172e117c7e838efe6e5e87bbed7010f6cb619b1a64693d6adcdb8e` |
