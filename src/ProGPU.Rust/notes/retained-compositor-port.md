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
