# Owned nested shader and transformed filter-source increment

Implementation is authored; no compiler, tests, browser, shader validation or performance runs were executed. Touched files were formatted only. No build, benchmark or trace artifacts were produced.

## Original provenance

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/OwnedShaderEffectSource.cs` | `5ee016cdd04720947b76e5d11eea354978d4653c36bdf4d0fc0f54a63df1918b` |
| `src/ProGPU.Scene/OwnedShaderEffectTexture.cs` | `c4b0736a8ffcbec332bfa309ea30d0311ee0167440d18662383f3886d50871aa` |
| `src/ProGPU.Scene/Compositor.OwnedShaderEffects.cs` | `da0c767b6ed25be653888435acb0751499da6282153b25bf9fded30cba77cc71` |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/ProGPU.Backend/Shaders/ClipCompose.wgsl` | `19518807c90f8485796cae98bb2c4fd760af2af4ca35b3211b7019e413ed25c4` |
| `src/ProGPU.Backend/Shaders/SampledMaskCommon.wgsl` | `4799e7646ac1cebbef3237ec248edebf3359880159ecdd098b4c9215ad995c79` |

## Owned recipe boundary

`ShaderEffectPreparation::prepare` returns a local boxed future under an exclusive `ShaderEffectPreparationContext`, with the actual target/capture descriptor supplied by the renderer. `OwnedShaderEffectSource` retains the validated original capture doubles, independent source translation and boxed recipe. Its Arc identity is independent of any target. `OwnedShaderEffectRecording` independently retains the balanced original Arc<Scene> and source lease.

`OwnedShaderEffectTexture` consumes an actual `CapturedScene`, `CapturedSourceScene` or `CacheSamplerRaster`. It retains the complete completed producer, including original picture/frame/cache ownership when present; there is deliberately no constructor accepting bare `GpuImage`. Independent clones are leases on that producer. Existing borrowed image/sampler APIs retain their original contract. Retained aliases still must not mutate or explicitly destroy a published texture; no API can retroactively freeze aliases that a caller retained before transfer.

`OwnedShaderEffectDescriptor` accepts only typed owned sampler records and has no implicit-input field. `OwnedShaderEffectParameters` freezes the complete original constants and ordered null/duplicate register inventory, retaining every sampler owner. The result has no Clone implementation and moves once out of preparation. Device admission visits every producer, including entries hidden by original register precedence. Root renderer integration retains that move-only result through an owned target Arc, supplies the actual implicit capture, and handles actual-target cache matching, cycle/depth guards, cancellation/rollback and completed-frame retirement.

Rust Arc/wgpu resource ownership replaces managed explicit-disposal/finalizer queues. This layer never explicitly destroys live shared GPU handles and has no finalizer callback. It neither copies texture pixels nor invents owned provenance from an external sampled image.

## Ordered scene and filter adapters

| Area | Implemented behavior |
| --- | --- |
| Owned command | `OwnedShaderEffectDraw` retains recording, full Matrix4 output transform, independent opacity and original managed blend admission. Recording invokes no recipe. |
| Target replacement | `owned_shader_effects` reports exact command indices; `replace_owned_shader_effects` preflights the entire replacement inventory and substitutes managed draws at those exact positions, preserving scopes and adjacent painter order. |
| Source placement | Compose only the owned draw's final output transform. Its original inner recording and recipe remain unchanged until their actual nested capture target exists. |
| Source ownership preflight | Traverse nested recorded pictures and all typed image-filter sources before proven-empty cache shortcuts. Ordinary storage depth and independent owned-source nesting remain separately bounded. |
| Full image filters | Scene recording/compiled ranges retain `ImageFilteredImageDraw` and its complete graph request. Consumer output placement does not replace filter source/canvas/transform metadata. |
| Transformed filter clips | `push_image_filter_clip` retains independent axis edges, or the original four-edge rectangle Path and complete affine transform. Non-axis rectangles use grid-1 path coverage; no rectangular envelope replaces the clip. |
| GPU path masks | `Mask::Path(ClipPath)` always uses materialization. Existing MaskComposer prepares the exact full-root ClipPlan; ordered composition samples its actual R8 output with nearest filtering at the bounded parent's original physical origin. No path coefficients or completed phase are rebased. |
| Resource admission | Path-mask preflight includes actual full-root targets, original atlas bounds and pass/allocation costs. Preparation preserves the renderer's signed-winding policy and default compiler options. |

Original implicit-input/raw-cache SourceRenderFrame constructors keep their existing positive-axis/translation admission. The separate image-filter frame admits general finite 2D affine placement. Layer bounds use transformed corner bounds only for storage allocation; rotated/sheared source AA groups and explicit area masks remain unsupported rather than being converted to envelopes. Final-device shader/atlas records still reject retargeting. Target-storage Clear under general coverage remains subject to its existing explicit scope admission.

## Remaining qualification

These implementations have not passed original source pixels, native/browser packages, source-host applications, cancellation stress, target-cache reuse, masked transformed Picture/Crop or performance gates. Source UI adapters must still supply exact immutable original recordings, revisions and root-property policy. They cannot claim source-native provenance from an authored Rust gallery. No provider, compiler default or source qualification gate was relaxed.

## Actual renderer target transaction

`renderer/owned_shader.rs` connects the owned recording to actual target preparation.
The target key retains renderer/device owner, original recording identity, all
projection and viewport float bits, actual signed physical origin/extent, semantic
DPI, complete source-capture frame and cached text suppression. It excludes the
final output transform: source realization remains independent of consumer paint.
A cache hit retains the exact previous parameter and source-texture generation.

The source identity guard spans recipe preparation, nested sampler realization
and implicit capture, retaining the original 256-source recursion bound. The
move-only result is device-checked across every sampler, including hidden duplicate
register entries. Renderer/device/target are checked again after recipe execution
and after implicit capture. Candidates publish only after successful capture;
failed or cancelled candidates remove only new nested variants. Complete prepared
scenes independently retain target owners, so later eviction cannot destroy their
recordings, parameter leases or source textures.

Preparation alone does not evict unused variants. Successful `render`, `capture`
or `submit_encoded_scene` records the completed submission epoch; the next
exclusive outer preparation sweeps unused cache entries for that submitted frame.
No GPU completion wait, explicit shared-resource destruction or callback occurs
in retirement. Callers using `encode` and their own encoder can use
`submit_encoded_scene` for this completed-frame ownership step. Direct external
queue submission cannot be inferred by the renderer. The cache also has an
explicit bounded target inventory. Nested text preparation preserves the parent
atlas batch and restores its prior protection state; only its owner ends that
batch, while failed nested preparation invalidates the uncertain atlas generation.

`OwnedShaderMetrics` reports commands, cache hits, captures and source allocation
bytes; layer/AA counters retain nested totals. The shared authored native/browser
gallery has twelve panels for repeated recordings, nested implicit input, owned
sampler captures, transformed output, opacity and bounded child targets. Use
`--owned-shaders` or `?scene=owned-shaders` after the implementation phase. The
24-kind image-filter gallery uses `--image-filters` / `?scene=image-filters`.
Neither gallery has been compiled or executed. All source, pixel, cancellation,
allocation and performance evidence remains deferred.
