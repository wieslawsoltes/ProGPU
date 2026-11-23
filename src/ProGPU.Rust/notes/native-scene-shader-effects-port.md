# Native programmable effect resource bridge

Implemented `NativeShaderEffectPlan` and `NativeShaderEffectEngine` over the existing original bytecode/WGSL program implementation. All six raw resource versions retain exact descriptors, source generations, derivative registers and capture/sample/affine frames. The source input and independent sampler are captured in original order, with same-resource sharing and absent-sampler fallback. Actual captured views remain retained; no default-view reconstruction or receiving-frame resize is used.

Legacy programs render directly into the actual native layer's RGBA8 `effect[0]` allocation, preserving valid extent independently of spare storage. Input and picture captures use the enclosing native engine's actual target format, matching `create_semantic_picture_binding`; only legacy effect output is fixed RGBA8. Final-device programs render in the actual parent lattice. The typed vector-mask handoff requires the same scene, exact source resource index, original vector kind/unit opacity, actual target/presentation/DPI and retained R8 producer. Ordinary legacy layer masks remain the later layer composite's responsibility.

`preflight_sources` provides device/source budgets before layer allocation or source acquisition; `preflight` also checks actual layer/mask leases. Source callbacks run only during preparation. Suppressed final-device wrappers retain source identity without acquiring unused pictures or inventing a target. Prepared resources survive pipeline-cache replacement, failed candidates and queue submission; encode/draw perform no source callbacks. The native aggregate executor and common nested-picture runtime consume these APIs in their separate implementation slices.

Actual effect uniform uploads remain 544/608/624 bytes. The original replay planner's 528/592/608 accounting estimate is retained separately and never sizes buffers. Preflight includes actual-format capture storage; prepared picture metrics come from the real child producer, allowing cache hits to report zero uploads/allocations. Replaying a prepared effect adds draws/passes without claiming another immutable upload.

No build, compiler check, tests, fixtures, browser run or benchmark was performed; qualification remains deferred until the implementation-first port is complete. Canonical shaders and compiler defaults are unchanged.

Original source SHA-256:

| Original path (`src/ProGPU.Native/src/`) | SHA-256 |
|---|---|
| `Scene/progpu_native_shader_effect_execution.cpp` | `ea694e6902505dda480c45e94539793934a5630b41130b010c7e54c9c764f080` |
| `Scene/progpu_native_shader_effect_uniforms.hpp` | `53dbcd9e8d265e09792006e3527d28ab1bb8c37e3b4d56b299d101345069729e` |
| `Scene/progpu_native_shader_effect_resource.hpp` | `fda73197f0b2e6d1d0f1ea14d19824a30760fd1dc4a458f479da4645ac6e8467` |
| `Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `Scene/progpu_native_semantic_picture_mask_resources.cpp` | `ad9f661c3dd6cac4d612a28ff8b24124833b1b15af2f81d32c6e82c96106432a` |
| `Backend/progpu_native_layer_resource_execution.cpp` | `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3` |
