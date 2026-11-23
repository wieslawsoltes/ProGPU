# Original per-draw destination blending

Implementation-only increment. No builds, tests, browser execution or pixel/performance qualification were run. Canonical shaders and compiler options remain unchanged.

`Scene::push_blend` transports the entire original typed mode inventory. Coverage and blending have independent stacks; source geometry, opacity, atlas phase and source owners remain unchanged. Canvas picture/image/path commands retain these scopes, and arithmetic paint blenders still reject as in SKCanvas.

The original families deliberately differ:

| Original route | Rust execution |
| --- | --- |
| Texture draw, including arbitrary image/atlas/tile quads | Retain the original index range; resolve the actual geometry into bounded premultiplied source storage with original SrcOver, scissor and mask; canonical AdvancedBlend samples the destination at its painter position. |
| HintedGlyphPaint with an actual bindable image texture | Original bearing/extent bounds, separate offset-plus-extent arithmetic, clipped outward source bounds and original bounded projection. The final text transaction shares immutable brush/stop/paint buffers with each retained bounded frame binding. Its actual atlas, source texture and source alpha interpretation survive through replay/submission. |
| Ordinary Vector/Text and registered-material HintedGlyphPaint | Original Multiply/Screen/Darken/Lighten/Exclusion fixed factors and alpha equations, with original premultiplied fragment selection. Other advanced modes retain the original SrcOver fallback. `LayerBlend::managed_vector_effective_mode` exposes that selection explicitly. This is not exact isolated-layer blending. |
| Native semantic isolated layer | Existing independent full destination-aware layer resolve; no synthetic per-vector layer is introduced. |

Hinted texture-backed resolve admission keeps the original `IsTextureBindable(drawCall.Texture)` gate. Null-texture registered materials do not acquire invented texture proof. Premultiplied hinted image paints use the original premultiplied fragment entry and factors directly rather than unpremultiplying and multiplying their sampled color again. Ordinary hinted shaders retain all original mask/fragment-position arithmetic, including their bounded-source behavior; no shader-local origin repair is inferred from another shader's contract.

Each prepared target owns root/output/maximum-bounded-source textures and immutable ordered operations. Source bounds come from original image indices or hinted instance metadata; clipping occurs before allocation for direct source ranges. Existing prebounded native group images retain their own extent contract. Shader/mask/text residency checks remain unchanged. Pipeline selection consumes the actual resolved mask state, including geometry masks lowered to binary scissors. Normalized resource owners are retained through the existing queue-submission path. The current executor retains its additional output-to-root copy per destination operation; metrics count actual passes and resources.

The authored `examples/support/backdrop_blend_scene.rs` is shared by winit `--backdrop-blends` and browser `select_backdrop_blend_scene`. It demonstrates six backdrop variants, fifteen advanced image modes beside the original vector policy, and Canvas picture transport. It is a production demo, not a pixel oracle or hinted/provider qualification.

Remaining boundaries: no fabricated general vector destination source route; no arithmetic Canvas blender admission; independent extension/filter blend scopes still reject unsupported combinations; original native/provider/application pixel gates remain outstanding. Source geometry, mask, resource and text policy limitations of their independent APIs remain in force.

Original SHA-256 provenance:

| Source under `src/` | SHA-256 |
| --- | --- |
| `ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `ProGPU.Scene/Compositor.HintedGlyphPaint.cs` | `6e0bdc5b62a950f9ac1ead890dee2a554191005f31e28c113a30cf75cb8a41b3` |
| `ProGPU.Backend/RenderPipelineCache.cs` | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| `ProGPU.Backend/Shaders/AdvancedBlend.wgsl` | `f4143798b7172e117c7e838efe6e5e87bbed7010f6cb619b1a64693d6adcdb8e` |
| `ProGPU.Backend/Shaders/HintedGlyphPaint.wgsl` | `1afaa272899c9f498602a9e21e9101540497afa37a8ac0cd44343b1f050014d2` |
| `SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
