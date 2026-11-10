# Retained image and surface production port

Original owned sources (SHA-256):

| Source | SHA-256 |
|---|---|
| `src/SkiaSharp/SKImage.cs` | `96827d04df213d63249cb3fb4005bcc4e811367ced4a16c27a0d9c21b66c2273` |
| `src/SkiaSharp/SKImage.Api.cs` | `42b67d84e9cb98679221ee4232f1c6d49405cc9627a42e8f0535a14498643cd1` |
| `src/SkiaSharp/SKImage.Compatibility.cs` | `3858463d563a7935eea9d73f3419c00c39c045ba9be84c25d543ccfcd7eb49fc` |
| `src/SkiaSharp/SKSurface.cs` | `c52ae704abdd8ffbd044386efcb439d4c67adb4b3b08a895d6ab4018f709a9b6` |
| `src/SkiaSharp/SKSurface.Compatibility.cs` | `1480c7a28c30dacc7756750a5859a2dbc6e7523741dd68ed2f63731c7dca71e3` |
| `src/SkiaSharp/SkiaSharp.cs` | `3b43aa657ad5584157b612fd16d2b56d96af352e310d69ff5a004d49e85d216f` |
| `src/ProGPU.Backend/GpuTexture.cs` | `0b5e89412acd16e86ae2f86c7e85e8bfa880b85dea196a79c2014cc1928bdb25` |
| `src/ProGPU.Backend/GpuTextureReadbackBuffer.cs` | `289fd4ef04e4518f92aa9133bb9c0fb92a3dd950b97c32df53049306715bb2ae` |
| `src/ProGPU.Backend/Shaders/MipGenerator.wgsl` | `872a81b4bb8c49834f291ea37d6b606f9ecdd27c0213dca5b8351d515d60203c` |

`image::{ImageContext,TextureImage,TextureReadbackWork,TextureRasterView,
RenderSurface}` ports counted primary storage, independent subset wrappers,
owned/adopted/borrowed identities, once-only source release, exact context gates,
portable context-local realizations, immutable/native-order readback caching,
reusable aligned staging and CPU storage, bounded native mapping and asynchronous
browser completion. Common pixel copies, selected CPU views, CPU scaling, original
JPEG-versus-PNG policy, explicit GPU transfers and canonical GPU mip chains are
connected. Mip pipelines cache by actual context and output format. Source picture
capture reuses actual retained scene preparation; no source callback executes in
encode. Raster hot component lanes use safe SIMD and explicit scalar execution.

Surfaces retain real CPU borrows or owned storage, original metadata/origin,
single-sample gates, mip-zero attachments, original initialized-target load/clear,
null targets, presentation-only flush, exact CPU region rounding, shared owned
snapshot generations, sole-generation reclaim and first-write copy-on-write.
Borrowed targets snapshot through a fresh owned complete copy. Pending recordings
are retained as batches and assembled once at flush. Explicit close flushes and
polls once without waiting; abandonment never flushes, polls, waits or unmaps.
External producer callbacks are retained by actual image leases, full immutable
compiled source graphs and actual queue completion callbacks. AA children, filter
picture captures, masks, blur, managed/native shader inputs and capture submission
paths share that ownership boundary. Public encode-only GPU programs expose
`retain_submitted` for the caller's actual queue submission; Renderer and surface
submission APIs perform the step automatically.

Safe Rust uses counted wgpu resource handles rather than explicitly destroying a
texture while another actual handle can remain live. Raw borrowed texture entry
points require the caller's real device/queue and prohibit external destruction
during retained use; wgpu independently validates the actual backend domain. GPU
objects never infer a current/global context. CPU pointer pinning, native framebuffer
presentation registries and foreign GL/Vulkan/Metal backend handles require a real
external provider and are not fabricated here. Renderer targets currently admit
RGBA/BGRA 8-bit linear/sRGB formats; other source backend formats remain explicit
capability boundaries. Picture projective transforms outside the existing complete
2D affine source-frame contract reject. Arbitrary original SKPaint picture/save-layer
compatibility is expressed through the existing explicit retained Scene layer APIs,
not a new invented paint facade. Dependency codec bitstreams remain their separately
unqualified public-provider boundary.

No compilation, tests, fixtures, shader/runtime/browser execution or benchmarking
was performed, per the mandatory code-first instruction. Only touched production
files were formatted. No build artifacts were produced.
