# Source capture placement increment

Implementation only; compiler, tests, pixel comparisons, browser runs and performance validation have not been run, following the required port-first order. This increment creates no build output, traces or benchmark artifacts.

## Original source provenance

| Original file | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Scene/Compositor.OwnedShaderEffects.cs` | `da0c767b6ed25be653888435acb0751499da6282153b25bf9fded30cba77cc71` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
| `src/ProGPU.Backend/Shaders/Texture.wgsl` | `5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51` |
| `src/ProGPU.Native/src/Scene/progpu_native_shader_effect.cpp` | `015fea0221d637621d241505d4d1cfe4a9362f2a0af320da64fb9fe2994abd85` |

The source routes are `CompileVisualTreeCore`, `CompilePicture`, `UsesLocalBrushCoordinates`, `TransformCommandBrush/TransformCommandPenBrush`, and `RenderOffscreenCore`. Canonical shaders are unchanged. The existing Rust Scene is already a retained source-space command generation; a capture applies its additional original parent placement to that generation instead of reconstructing producer visual history.

## Implemented API and integration

`Scene::place_source_capture(SourceRenderFrame) -> Result<Scene, Error>` is crate-private and constructs a complete independent candidate. `Scene::preflight_source_capture_devices(&Device)` inspects the original tree before placement and before a proven-empty cache shortcut; the renderer must call it even for empty input. It checks RGB, planar, image-effect mask, coverage/texture mask and ROP texture owners, including nested ordinary/AA children. It creates no resources and executes no source callbacks.

| Recorded family | Placement behavior |
| --- | --- |
| Analytic, filled path, material path | Compose complete original Matrix4 order into source transforms before padding/raster requests; source-local material coordinates stay unchanged. |
| Retained strokes | Preserve source geometry, all pen/dash/cap/join/width metadata and original recorded transform; compose the complete new frame before preparation; create independent preparation storage. World-coordinate gradient/noise/hatch material frames receive the added parent inverse; Solid and Tile retain original handling. The private placed-stroke record separately retains the original local brush if directed-cap preparation later selects fill coverage. One parent inverse is reused throughout the candidate tree. |
| Text | Compose source context before glyph sizing, origin policy, outline/atlas selection and fallback; preserve source arrays/font owners; set independent semantic DPI and ClearType suppression; reset active late-camera state. |
| Explicit text paths | Preserve independently selected raster scale, phase grid, coverage transfer and quantization policy; compose placement before path planning. |
| Images and image color/filter draws | Retain original source/destination, patches, quads and UV arithmetic; compose the outer command transform before actual-target snapping. |
| Tile images | Retain original output-to-tile UV arithmetic and original output quad; private placed command transforms only the original quad positions afterward. |
| Fused image effects | Place the four original texture quad positions; retain UVs, parameters, independent mask, planar source and storage policy. |
| Binary/AA clips, layers | Retain independent transformed near/far edges; internal edge getters feed original renderer allocation/clip arithmetic without edge-width-edge round trips. |
| Analytic and sampled masks | Compose original source descriptors before established inverse/coverage generation. Explicit area masks retain private independent edges with canonical kind 5. |
| Clear / ROP | Clear stays a target-storage command. Source image geometry moves; raw ROP pattern identity and anchoring stay unchanged. |

`LayerDraw::bounds_edges()` and `AxisClipDraw::bounds_edges()` feed actual target planning. Public constructors retain their original XYWH contract. A private bounds witness is invalidated if callers later edit a cloned layer's public bounds. `Mask::AxisClipEdges` carries an opaque private-construction `AxisClipMaskEdges` generation; direct chains and GPU materialization retain the same kind-5 semantics.

## Explicit restrictions and remaining work

Existing final-device `ShaderEffectDraw`, `ManagedShaderEffectDraw`, resolved AA indices, glyph atlas instances, inherited physical clips and resolved layer image bounds reject source placement. Their original target/atlas identity cannot be reconstructed from the completed record. The next owned nested-source increment must retain recipes and prepare consumer parameters at the actual capture target.

Perspective/nonfinite composed frames reject; source frames themselves admit only original positive-axis raw-cache placement or implicit-input root translation. Text contexts with unsupported explicit clips or nonunit static zoom still reject via the existing source text admission. Active GPU/MVP/camera flags reset for offscreen compilation; frozen glyph commands are never relabeled as fresh text. Explicit text-path raster policy is preserved, without inventing a new source atlas policy.

A proven-empty raw cache traverses complete source descriptors and actual resource-device ownership before returning an empty recording. Root cache opacity/transform/effect/clip exclusions remain the source recorder's contract; this flat Scene pass does not infer missing visual history. Layer effects, current source projection and actual parent allocation stay with the renderer's source-frame implementation.

Full float matrix inversion for world-coordinate material placement and all source fractional/padding/cache behavior still require the later original-source pixel gates. Authored code is not managed/native/browser/provider/package or source-application qualification.
