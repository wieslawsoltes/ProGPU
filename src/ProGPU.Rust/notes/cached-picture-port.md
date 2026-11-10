# Original cached-picture source and renderer port

`composition::cached_picture` retains static/live sources, transactional refresh, exact invalidation generations, independent lookup/recording leases and bounded source dependencies. Sources run without a lookup/state borrow; late notifications cannot retain a retired owner. Shared Rust ownership replaces the original borrowed/owned source disposal flag. The exact subscription retires before the final source owner.

`Scene::draw_cached_picture` keeps painter order and original final placement/opacity/blend outside capture. Actual-target preparation resolves ordinary picture captures with exact logical extents, ceil physical allocation, original raster DPI and ClearType policy. Empty/zero-scale sources refresh and preflight before painting nothing. This frame stays distinct from raw-cache shader-sampler frames. Each result retains its exact immutable original recording/texture/device; cancelled preparation rolls back new cache entries, and completed-frame sweeping preserves independently retained generations.

The cross-family cycle/depth inventory traverses picture/layer/owned-shader, image-filter, color-filter and runtime-child dependencies before a new live recording is published. Opaque custom recipe captures remain caller-owned and must preserve the same acyclic ownership contract. Direct and captured-child metrics retain separate cache/capture/draw counts. The shared desktop/browser `--cached-pictures` / `?scene=cached-pictures` gallery authors twelve consumers.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/CachedPicture.cs` | `90a90bea020e87700f922a0bc7f2f438ffac46c9c6c1d8f1ca5feae7d1a087f0` |
| `src/ProGPU.Scene/CachedPictureSourceCache.cs` | `f4a8aac4a195dae82a8f0db622d081a4be64f936bb22ebb92509cce5a13c8f74` |
| `src/ProGPU.Scene/ICachedPictureSource.cs` | `cea054bedea30a72c3af7379ad2b3de5d10347967aa8a9e18f5a65b2ffdc5323` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |

Relevant compositor methods: EnsureLayerTexture, ApplyAndDrawLayerCore and RenderOffscreenCore. Full visual traversal/page replay/culling and original source-provider cache policies remain separate integration work. No qualification is claimed: source formatting only; compilation, tests, runtime/browser, allocation, performance and package gates remain deferred.
