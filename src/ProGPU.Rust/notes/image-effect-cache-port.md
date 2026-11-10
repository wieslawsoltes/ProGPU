# ImageEffect pools and retained retirement

Implementation only: source formatting, no compiler/tests/shader/browser/benchmark execution.

The production renderer now shares one original ImageEffect layout and shader module, caches format/alpha/blend pipelines and original samplers, and reuses the 288-byte effect buffers/bindings. Gaussian shaders/samplers are shared by route; admitted idle storage reuses both actual target textures and both original 912-byte buffers. All prepared programs keep independent strong leases. Failed or canceled preparation drops its detached private candidate cache; previously published scenes retain their exact pipelines, buffers, source owners and targets.

Same-preparation blur reuse keys actual luma/chroma handles, both original content/view generations, exact sigma bits, original YUV Vector4 equality (including equal zero signs), output format, alpha and arithmetic policy. CPU uploads use a separate proven immutable-owner namespace. `BlurInput::with_generations(ImageEffectTextureGeneration, Option<ImageEffectTextureGeneration>)` transports actual producer generations; borrowed untracked resources never invent revisions and retain fresh blur evaluation. Native explicit-uniform blur remains independent. Original binding reuse depends on texture/view generations, sampling, address, target format and source policy, not content generations.

`Renderer::begin_image_effect_frame(u64)` supplies the original monotonic host clock. Both existing winit/browser hosts call it from actual presentation counts before preparation/drawing. Nested targets on that renderer share the clock. Generic callers without an explicit clock use separately documented preparation epochs; switching clock domains retires old age-indexed cache leases. Actual drawing touches bindings, and actual prepass encoding touches blur storage, so retained replay stays warm. Unused bindings expire after 120 frames and blur storage after 240. Prepared scenes survive eviction. Caches retain at most 16,384 entries per pool and 512 MiB of idle/leased blur storage; uncached private ownership handles the existing admitted frame when retention capacity is full.

Mutable recycling requires exclusive cache ownership and completed GPU work. Sequential encode/submit pairs carry monotonic completion receipts. More than one outstanding raw encoding without per-command-buffer submission tickets disables mutation for that resource rather than inferring which recording submitted. Never-encoded abandoned candidates may recycle. All GPU retirement is by actual wgpu/producer owners, with no explicit destroy, wait, readback, finalizer lock or global device inference.

`ImageEffectMetrics` exposes deduplicated draw counts, actual blur texture allocations/storage reuse, parameter reuse, view-binding hits and pool counts at publication. Preflight budgets share the same immutable/tracked dedup admission while still visiting every source/device before shortcuts. Resource metrics describe implemented work; pixel/performance/browser/native-provider/package qualification remains deferred. Untracked external producers and ambiguous raw submission ordering retain the explicit gates above.

Original SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/Extensions/ImageEffectExtensionPipeline.cs` | `333e632ed7b0ff7ec9cbe14fa77cba5e4aa8169be37dda076950ebe7e667ef0d` |
| `src/ProGPU.Backend/GpuTexture.cs` | `0b5e89412acd16e86ae2f86c7e85e8bfa880b85dea196a79c2014cc1928bdb25` |
| `src/ProGPU.Backend/GpuTextureGaussianBlur.cs` | `5aed1ae6e8669c6d565218f570d850c53af839a4a81eb513bf9a21633517f008` |

Canonical shader hashes and the original shared pipeline-cache contract remain in `managed-image-effect-port.md`; shader source and compiler defaults are unchanged.
