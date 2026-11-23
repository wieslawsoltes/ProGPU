# Original Mesh3D GPU execution

`Mesh3DEngine` prepares an owned `Mesh3DRequest` into `PreparedMesh3D`. Preparation
acquires actual source leases and creates immutable GPU resources without a queue
submission. `encode` writes original material Gaussian passes followed by the
viewport pass; `image` is its premultiplied RGBA8 output. The returned image retains
the complete prepared resource/lease generation, and `retain_submitted` transfers
source ownership through the caller's real queue completion. Automatic/GpuShader
select the original fragment implementation; other forced execution paths reject.

Implemented source inventory:

- Exact RGBA8, Depth24PlusStencil8 and optional 4x MSAA attachments, original dark
  clear, depth/stencil initialization and resolve/store policy.
- Demand-only front/back solid, front/back unfilterable material, two-sided
  wireframe, visible-edge and occluded-edge pipelines for sample counts 1/4.
  Shader modules, fragment entry points, vertex layouts, straight blending,
  0xff stencil masks, depth comparison/write policy and compiler defaults remain
  original. SolidWireframe uses the original wireframe shader, not two mesh passes.
- Full 32-byte geometry vertices, 560-byte records, 224-byte uniforms, 80-byte
  lights/edges and 32-byte gradient stops. Mesh draws retain source front-before-
  back ordering and exact record ordinals; edge jitter uses original 6/18 vertices.
- Actual context/planar lease selection, source false/wrong-device white fallback,
  ten original sampler policies, filterable and R16/RG16 integer-load bindings,
  original YUV/material record inputs and premultiplied source metadata.
- Original live Gaussian eligibility and managed float kernels, including planar
  decode into RGBA16F. Dedup within a prepared viewport requires actual texture,
  content/view generation, conversion and sigma identity. Missing generations
  never invent a stable source revision.
- Geometry cache identity/version, per-viewport record/edge generation and exact
  opacity keys, cached white bindings and sample/family shader/pipeline caches.
  Later preparations cannot overwrite a retained earlier generation. Gaussian
  storage is reusable only when exclusively owned and confirmed complete; unused
  pool entries follow the original >240-frame retirement bound. Canceled or
  ambiguous encodes never claim queue completion.

The original warm static-record path skips ApplyMaterialBrush and writes one
zero gradient stop into the previous storage. Rust retains the old tail in an
immutable CPU snapshot and reproduces that selected data exactly. Upload metrics
report the actual snapshot upload, including the extra bytes needed for immutable
prepared generations; they do not claim the original mutable-buffer allocation
profile. Per-request metrics report actual geometry/cache/upload/storage/pipeline
and blur work. An explicit `begin_frame` supplies the real host frame; automatic
preparation epochs remain distinct from display frames and submissions.

`Mesh3DRequest::resource_cost` performs read-only whole-request admission before
texture acquisition, including every potentially eligible Gaussian pair. The
optional `resource_plan` freezes those per-source bounds for whole-scene budget
admission and `prepare_with_plan`; actual acquired blur work cannot exceed them.
`MeshTextureSource::metadata` defaults to unavailable, and owned image/plane
leases provide exact dimensions/formats. Unavailable metadata is rejected only
when an eligible live blur needs allocation bounds; ordinary sampling retains
its deferred acquisition route. Request opacity remains finite-only as in the
record path; the final Texture consumer has its independent opacity admission.

Original Viewport3D owns its independent camera/projection and appends a separate
Texture consumer; the extension's main-pass Render is a no-op. The root adapter
connects that ordered pair. Empty source viewport production emits neither member;
engine preparation rejects an empty request instead of inventing initialized
pixels for an absent external target. Nonempty null-geometry inventories still
clear their real target. Archive DataParam remains an unsupported external wire
slot; no viewport provider is synthesized by decoding.

Remaining boundaries: shared encoder-wide blur execution dedup and the original
mutable allocation profile require an explicit encoding/ownership batch and are
not inferred from frame numbers. Platform source providers still must supply
actual leases/context generations. General late-camera text/path is separate.
Compilation, GPU execution, browser pixels and performance qualification remain
deferred. No build, check, test, fixture, browser or benchmark was run.

| Original source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/Extensions/Mesh3DExtensionPipeline.cs` | `d3911e0a662038d7d213867278d60621602cb861f9e353c5530b46036e29884c` |
| `src/ProGPU.Scene/Shaders/Mesh3DSolid.wgsl` | `ee1348c02a4c916598432675c1f63af2765474d17f27b3a9975d8944fca08eaf` |
| `src/ProGPU.Scene/Shaders/Mesh3DWireframe.wgsl` | `a450ca485f293e0a227e1e83714bf5c5ea52211e5a6168895b0c7e34cb4fa1a1` |
| `src/ProGPU.Scene/Shaders/Mesh3DEdges.wgsl` | `5044170d295432dbb131f30e6e465554ab606c640b5221b340fa332f6c7be67d` |
| `src/ProGPU.Backend/RenderPipelineCache.cs` | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| `src/ProGPU.Backend/GpuTextureGaussianBlur.cs` | `5aed1ae6e8669c6d565218f570d850c53af839a4a81eb513bf9a21633517f008` |
| `src/ProGPU.WinUI/Controls/Viewport3D.cs` | `6a9e079f1e32dfed8294f760f1e5f177f0a814a2e3e8037205415933b49558b9` |
