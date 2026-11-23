# Native render pipelines and draw execution port

Implementation checkpoint only: source formatted; no build, shader validation, test, runtime, browser, performance or package execution was run for this slice.

`native_scene::engine_gpu` ports the original demand-created vector, text, hinted-paint, image, effect and fixed layer-composite pipelines. Original shared WGSL is included unchanged. Explicit layouts retain their original visibility, uniform/storage minima, 56-byte vector vertices, 96-byte glyph instances and the hinted-paint Uint32 attribute at offset 92. Masked vector premultiplication, ordinary chain straight-alpha output, retained-image premultiplication, direct-image straight alpha, matrix/effect straight output, RGB/alpha-only writes, floating coverage and linear-byte layer opacity remain independent variants. Unsupported group combinations fail before pipeline publication. Runtime error scopes publish only complete candidates and leave compiler defaults intact.

Material pages retain actual admitted scene generations. Whole CPU/device inventory preflight precedes GPU resource creation; brush/gradient/style storage retains original geometric capacity growth and exact wire bytes. Immutable target frames hold the original 224-byte uniforms and actual material buffers so later targets cannot overwrite already encoded consumers. The root glyph coverage certificate preserves its complete exact arithmetic and physical viewport checks. It does not infer source glyph or target-opacity admission.

Actual vector buffers preserve original page-global indices and zero base vertex. Native path and glyph render pages bind their actual source atlases without copying coverage. Source image/effect mask precedence and color-matrix group positions remain exact. Both render-pass and render-bundle routes use the same prepared draw record, original full-buffer ranges, first indices, base vertices and instance ranges. Hinted paint retains its empty group-two slot, exact external texture owners, target-specific uniforms and 6/12-vertex branch. Preparation owns source pages, target frames and mask views; a submission callback can retain the complete draw through completion. Cold resource metrics and zero-upload immutable replay metrics are separate.

This is not a complete native scene replay adapter. The outer command executor must still supply exact ordered materialized-layer targets, presentation/scissor/depth, source mask production, RGB occurrence passes, effect/cache/capture integration, draw merging and family cache invalidation. Path coverage's required separate submission boundaries remain with the native path engine; image live blur must execute before consumers. No old C++/C#/Windows/provider/package/application/pixel gate is relaxed, and no source qualification is claimed.

## Owned source provenance

| Original owned source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Backend/progpu_native_pipeline.cpp` | `064c54ee475982af9dc6d6191ca5769582b6e82be7b0edb8f4dcbdacd48eecb4` |
| `src/ProGPU.Native/src/Backend/progpu_native_image_layer_resources.cpp` | `8d8fb8816f1db86563b5ab1551446f928bf4915021f095cfc1b8737d2c16d523` |
| `src/ProGPU.Native/src/Backend/progpu_native_image_execution.cpp` | `09135c8ead728a45d4e2a65ab9196b2a2a3fbff535b65d8f01816ef37c6b547e` |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_coverage_frame.hpp` | `16c53cadea98a2f058aaadc8c24ce6483ffb7703b6eefc0a8765db74df2c4710` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_masked_vector_pipeline.cpp` | `52a6ce35a451c08cd525be202c9dcb3dcc536db0005995e34f36f0e8cb62deed` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_masked_text_pipeline.cpp` | `28e0daa53f34cb3df3c5a607c96e4e25951772b83907e1b67a28c68b39c7936c` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_brush_mask_pipeline.cpp` | `048e11ada73b1f325e70633f5dafbd27b7f3c8076d43ee652654a0fe5cc21b69` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_mask_chain_pipeline.cpp` | `adb675667452729483eba70f80c4f1d1f5c5c3ddb306dc5aeec438c06b5712d1` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_image_effect_resources.cpp` | `91c50901679c0c0e4975ccf4ef947433a77141d13c62fdeb6fce20ee90905287` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp` | `759fa316f751c098485eb89aab0508ed059ee6c1ed64516862a9815ec9c8b6d5` |
| `src/ProGPU.Native/src/Scene/progpu_native_glyph_paint_alpha.hpp` | `5d308e1992b11632098bbe388cc658af13e3c84b5401656cdfbfcce4298bb315` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_draw_execution.cpp` | `4dd136cd49ec8dd28e21e7541ba74db83cc6a7b9fe0acd8a7aa8f81a45e7e8b5` |
