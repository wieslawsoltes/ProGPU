# Native retained mask resources

Implementation only; no builds, tests, shader compilation, browser runs or benchmarks were executed. Qualification remains deferred until the complete port is authored.

`engine_resources::masks` now owns all eight admitted native mask payloads: rounded/axis-area, coverage bytes, analytic chain, raw vector clip chain, brush, geometry, nested picture and composite. Target extent, per-axis presentation, semantic DPI and original state-cursor DPI remain independent. Source masks retain the actual admitted stream/resource generation and complete child owners. Analytic radii normalization, physical axis edges, affine UVs, red versus alpha channels, nearest/linear sampling, original four-edge bounds and post-cache guideline deformation retain original arithmetic.

Brush/geometry production uses the actual native primitive encoder and canonical Vector `fs_mask_target_space` R8 pipeline, including original target-space material coordinates and geometry-only padded binary scissor. Composite masks retain original vector/picture/geometry/brush ordering, child opacity once, two R8 accumulation targets, 256-byte dynamic records and canonical ClipCompose sampling.

Raw vector clips have their own genuine native plan: the complete shared segment inventory controls kernel classification; original root/Boolean offsets control deduplication; 1<<24 segment admission, 63-node programs, 512-byte copy offsets, actual-size grouped operation-zero dispatch and split phase boundaries remain intact. No managed Path capacity, reconstructed draw frame or concatenated leaf geometry substitutes for this source. The native path GPU backend now shares a read-only `NativeRasterPlan`; the existing `PreparedNativePathPage` API remains the concrete path alias. Clip atlas growth remains 1024/2048/4096, and the original folded resource/presentation revision plus target size/DPI selects the single retained vector cache. Completed immutable mask textures replace shared mutable overwrite/copy ownership: prior prepared bindings remain valid across later cache replacements.

Picture masks retain their independent source extent/frame and the actual engine target format. The original generation-independent byte identity, recursive external-image exclusion, 64-entry/64MiB optional FIFO cache and source ownership remain explicit. The engine requires `NativePictureMaskResolver`; it must perform complete nested admission before acquisition, then render the supplied actual child frame, with real same-device texture/producer ownership. The ordered native executor still needs to supply this resolver and select original receiving/source-extent target overrides; this module does not fabricate a host provider or qualify that integration.

API sequence: construct `NativeMaskRequest(scene, resource_index, NativeMaskTarget, optional original composite State)`; call `NativeMaskEngine::preflight(request, resolver)` for live-device, retained-atlas and nested-source costs before whole-scene GPU mutation; call async `prepare`; encode pending brush/composite passes before consuming `binding()`; call `retain_submitted` after queue submission. Vector compute/transfer and actual nested captures complete during preparation, preserving native phase order. Encoding invokes no providers. CPU-only request preflight reports base source costs; it does not replace engine/device preflight. Metrics distinguish real uploads, coverage staging, compute/render work, cache hits and retained texture bytes; failed/abandoned candidates retain already-submitted GPU owners until completion and cannot overwrite a published capture.

`vector::preflight_command` exposes the unchanged original count-only command route for the ordered draw planner, and `compile_mask_geometry` reuses the native geometry producer without a managed conversion.

Source SHA-256:

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask.cpp` | `6c3b0b22d2e7aac8a1e9d17b4d774d1dc7c8ae090f90766653581bc1003a370a` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_layer_mask_resources.cpp` | `66fcc7bc64d8b908788571c31f1bd0b6b671fdea2d25484be2ca22a4091a1158` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_brush_mask_resources.cpp` | `2bdfe30bc3da4d3ae5b8b86a39c931a718d75e28dca5f89be386e78e151c0bd1` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_picture_mask_resources.cpp` | `ad9f661c3dd6cac4d612a28ff8b24124833b1b15af2f81d32c6e82c96106432a` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_composite_mask_resources.cpp` | `3f4fee8d6ac8591cc9d2bf353ffcdb9e77a8504dd9a7e4f95db50837f049e664` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_brush_mask_pipeline.cpp` | `048e11ada73b1f325e70633f5dafbd27b7f3c8076d43ee652654a0fe5cc21b69` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_mask_chain_pipeline.cpp` | `adb675667452729483eba70f80c4f1d1f5c5c3ddb306dc5aeec438c06b5712d1` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_resource_execution.cpp` | `7ba451a122e78d4c60b02bd5f984091485067683ddcf8883522fc558d729a6d3` |
| `src/ProGPU.Native/src/Backend/progpu_native_clip_execution.cpp` | `19eac821712a3c166eae34ae27508027ae00e2a6511be728b353711fd9263f63` |
| `src/ProGPU.Native/src/Backend/progpu_native_clip_resources.cpp` | `dd36e3a5141f9cfd2f201c4e6afa699d537d67642cd26c02585e9fb902b2827f` |
| `src/ProGPU.Native/src/Backend/progpu_native_draw_state.cpp` | `2c59e9107eb66374a7d939f95a7d03c78f7e78ad25e3a84ecf06281227021816` |
| `src/ProGPU.Native/src/Backend/progpu_native_gpu_records.hpp` | `5f1eda15324be086c0b8880ee432e1244f2797f32f0137dd1d8a636149586311` |
| `src/ProGPU.Backend/Shaders/ClipCompose.wgsl` | `19518807c90f8485796cae98bb2c4fd760af2af4ca35b3211b7019e413ed25c4` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
