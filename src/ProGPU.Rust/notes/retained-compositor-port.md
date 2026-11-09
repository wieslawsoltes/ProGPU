# Retained compositor integration (2026-11-09; unvalidated)

The user mandates implementation before validation. No compiler, tests, lint,
shader validator, browser, benchmark or profiling ran. Source formatting is an
edit. Original evidence remains scoped to its earlier head. Existing DESIGN.md
architecture references apply; these are direct original ProGPU ports, with no
foreign implementation, shader changes or changed compiler/driver defaults.

## Original source provenance

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/RenderCommand.cs` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/Shaders/Texture.wgsl` | `5f9c663cdc9eac166506c93a1add5ec058c3d68f9a766253aee895afe4adda51` |
| `src/ProGPU.Backend/Shaders/AdvancedBlend.wgsl` | `f4143798b7172e117c7e838efe6e5e87bbed7010f6cb619b1a64693d6adcdb8e` |
| `src/ProGPU.Native/src/Backend/progpu_native_layer_composite_execution.cpp` | `c3605dd11b5551e827f8689c6579bfc584d7219e61fe9c1fcb71f96b82a09f57` |
| `src/ProGPU.Backend/Shaders/GroupBlend.wgsl` | `02984dbf021973adf4261ee27c39cf9b5182eb4adf9cfb7ba65c53341f265c31` |

## ROP3 retained operations

`RasterOperation` separates presence from all 256 truth-table bytes, including
zero. `RasterPattern` owns solid colors, original 8x8 bits/two colors, or an actual
image with original origin. `Scene::draw_raster_operation` records O(1) source and
pattern owners without uploads. Patches reject pending their separate layout and
blend integration. Compilation retains actual image vertices, index range,
reconstruction and pattern binding. Preparation uses original projected bounds,
binary scissors and actual analytic/materialized coverage.

Canonical Texture.wgsl retains its Pad0=2 source-pass opt-in, original projection,
physical fragment origin, SrcOver state and RGB/source-alpha-zero behavior.
Canonical AdvancedBlend.wgsl retains integer truth tables, negative pattern phase,
transparent tile background and actual texture extent. Pattern origin localizes
to the real parent after DPI multiplication. Every operation owns immutable
96-byte uniforms in aligned 256-byte slots, bounded source resolve, actual parent
blend and ordered replace. All textures/bindings/pipelines remain owned through
submission; no CPU pixels, readback, explicit destroy or per-operation submit.
Structural range traversal is bounded scalar; independent physical projection
uses SIMD. Full source commands, patches/caches and presentation remain open.

## Explicit uniform backdrop

`UniformBackdropEngine` ports the original final GroupBlend pass for a proven
premultiplied clear and actual source. Its 32-byte record retains clamp, native
round-away-from-zero and UNORM8 division. Independent channel arithmetic is SIMD;
four scalar rounds preserve the original rule. Newly owned targets clear using
the supplied raw color and optionally scissor. It never guesses uniformity of an
arbitrary destination. All fifteen advanced modes use the unchanged shader.
Pipelines are lazy and immutable prepared resources append one caller-owned pass.
Same-device/format/extent/budget admission precedes publication. Automatic retained
uniform-target selection, source caches and complete semantic transport remain open.

## Final-device shaders

`Scene::draw_shader_effect` retains the effect, original v5/v6 frame, actual target
lattice, implicit input, independent sampler, derivative and typed vector mask.
Whole-tree target preflight precedes renderer work. Ordinary coverage scopes reject
these commands; vector coverage requires the original typed ClipPlan producer.
Binary scissors remain independent. Renderer prepares each target's constants and
bindings separately, retains engine identity on failed candidates, and draws in
the actual parent pass in painter order. Final-device effects are never evaluated
into a smaller image and resampled. Prepared resources survive cache eviction;
layer/AA metrics include actual source work. Full nested capture/revision/source
ownership and archives remain open; see shader-effect-port.md.

## Hosts and deferred work

Winit/browser share 40 compositor panels (25 representative ROP codes plus all
15 advanced uniform-backdrop modes) and 12 shader-effect panels. Select
`--compositor` / `?scene=compositor` or `--shader-effects` /
`?scene=shader-effects`. These scenes are authored, uncompiled and unexecuted.
Original source floats/records/pixels, alpha/color spaces, scopes/targets,
cancellation/retirement/device loss, allocator/performance, Windows/ARM64,
browser DPR-2 and package/provider/application gates remain deferred and mandatory.


## Managed WGSL and color-filter integration continuation

The subsequent parallel implementation connects `ManagedShaderEffectDraw`,
`ImageColorDraw` and `ColorFilteredImageDraw` through immutable Scene commands,
CPU compilation and ordered Renderer draws. Whole-tree original target/device/
sampler-limit admission precedes renderer mutation. Color-filter graph bytes and
passes participate in the existing 512-MiB/16,384-pass aggregate layer budget.
Prepared graphs encode their original full-source passes before consumers on each
replay, including loaded AA targets. Final placement, opacity and masks remain at
the consumer. Layer/AA metrics separately retain filter and managed shader work.

Managed shader drawing retains original actual physical viewport, root/parent
projection, immutable parameters and actual sampled generations. It uses the
independent original managed nineteen-mode blend table, including fixed Multiply,
Screen, Darken, Lighten and Exclusion; native destination-blend classification
cannot stand in for this contract. Unscoped draws restore the complete parent
scissor so draw-local shader clipping cannot leak into a following command.

Ordinary root masks use exact original managed single-mask records. Chains and
axis-area coverage use the existing original materialization route. A produced
full-child R8 mask has a proven local one-to-one lattice; its origin is explicitly
zero at managed shader sampling while reciprocals and all remaining words stay
unchanged. Direct root-coordinate analytic/affine sampled scopes on bounded
parents reject before GPU mutation: the original managed nested path does not
rebase completed inverse rows, and this port does not manufacture admission by
quantizing analytic coverage. Explicit ManagedShaderMask records are already
local to their actual target. Combining an explicit draw mask and an ordinary
coverage scope remains rejected pending complete source ownership composition.

Original managed source evidence: Compositor.TryPushAnalyticCanonicalGeometryMask
and TryPushAnalyticSolidOpacityMask reject nested offscreen depth above one.
GetDrawCallMaskBindGroup binds actual source records unchanged; source transforms
and canvas origins enter before Matrix3x2.Invert. These remain the required routes
for later nested capture, rather than an arithmetic rewrite of completed rows.

The shared authored galleries add 16 color-filter panels and 12 managed WGSL
panels, selected by --color-filters / ?scene=color-filters and --managed-shaders /
?scene=managed-shaders. Neither gallery has been compiled or executed.

## Independent raw-cache sampler frame

`composition::CacheSamplerRasterFrame::new` ports the original complete metadata
algorithm. It retains exact double XYWH/cache scale, actual primary per-axis DPI,
live per-axis texture limits, independent far-endpoint narrowing, binary32 relative
near-integer rule, original cap-adjusted content scale and separate float offsets.
Original empty/zero-scale realizations use one texel and identity transform.
Independent endpoint, scale and relative arithmetic use intrinsic SIMD; bounded
per-axis admission and cap branches remain structural scalar policy. Original
signed-zero and all metadata bits remain available through `same_generation`.

Construction proves neither source/device ownership nor completed capture. Full
owned source recipes, nested target realization/rollback, raw-cache GPU capture,
source-root property exclusion and completed-frame eviction remain separate work.

| Additional original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/CacheSamplerRasterFrame.cs` | `f0dc3f68aab17f17e5bd01f3cd0bbabb4a70901d8e5e4495f8357de5f42d7362` |
| `src/ProGPU.Scene/CacheSamplerRaster.cs` | `588545145814b609ad92781a097f38f7250b84732f69313d8e66695aed53e29c` |
| `src/ProGPU.Scene/Compositor.CacheSamplerRaster.cs` | `17b6c4f4a0627ef8761822c03742d0e47f2910fb4401481cbc6afde1bf1990ae` |
| `src/ProGPU.Scene/OwnedShaderEffectSource.cs` | `5ee016cdd04720947b76e5d11eea354978d4653c36bdf4d0fc0f54a63df1918b` |
| `src/ProGPU.Scene/Compositor.OwnedShaderEffects.cs` | `da0c767b6ed25be653888435acb0751499da6282153b25bf9fded30cba77cc71` |
