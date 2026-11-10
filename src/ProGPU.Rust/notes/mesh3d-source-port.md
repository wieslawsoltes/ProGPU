# Original Mesh3D source and CPU producer

Implementation-first production port; source formatting only. No compilation,
tests, shader checks, browser/runtime execution, fixtures or benchmarks were run.
No generated build/results/trace artifacts were created.

`mesh3d` now owns the original complete typed source inventory: solid/wireframe/
solid-wireframe modes, seven shading modes, all edge classes/modifiers/topology,
front/back material inputs, static geometry identity and signed version, original
normal generation, exact deindexed triangle-list storage, up to sixteen lights,
linear/radial gradient material records, color/specular brush target, RGB/planar
texture leases, YUV/color-matrix/fused effect metadata, tiling and normalized crop/
rotation/mirror presentation, and immutable viewport generation inputs.

`MeshGeometry3D` keeps actual arrays and source identity across replacement;
prepared frames retain the selected generation. Invalid indices independently
select original Zero position, UnitY normal and Zero UV; incomplete triangle tails
are retained. Normal production preserves complete supplied normals, original
triangle order, duplicate-index accumulation, the 0.0001 threshold and UnitY zero
fallback. Independent XYZ arithmetic uses safe SIMD; explicit `prepare_normals`
selects Automatic/CpuSimd/ScalarReference and reports its path. Forced unavailable
GPU normal kernels reject. Scalar reference remains explicit, apart from targets
without intrinsic support. Structural source array gathering does not fabricate
arithmetic or GPU execution.

`GpuMesh3DRecord` is 560 bytes, vertices 32, lights 80, uniforms 224, edges 80,
and gradient stops 32; padding is explicit zero. CPU candidates preserve original
opacity applications, normal inverse-transpose/identity policy, source camera
inverse failure, ordered unsorted gradient stops, material-transform rows and
independent light admission. Aggregate gradient/edge/light capacities are admitted
before texture acquisition. General matrix inversion reuses the existing owned
adjugate/SIMD implementation; original default-runtime inversion/trig/normalization
bit equivalence remains a differential qualification gate.

`Mesh3DViewport::compile_source` ports the actual recursive ModelVisual producer:
content transforms propagate to children; front/default/back admission stays
separate; back entries do not duplicate edge overlays; solid versus gradient
source opacity, material RGBA multiplication and alpha extraction retain their
source order. Theme values require the explicit original immutable resource
snapshot. No host palette is fabricated. Caller-selected source YUV metadata
remains explicit; native media descriptor discovery and UI event subscriptions
are separate host adapters.

`MeshTextureSource` is the typed receiving-device RGB/planar acquisition seam.
Actual raw plane pairs and producer borrows survive encoding and queue completion;
owned and explicitly borrowed constructors remain distinct. Source acquisition
returns an optional complete lease, not an unowned handle; false capability and
wrong-device handling retain the original white-material fallback in the GPU
engine. Content/view revisions are explicit and absent metadata does not claim
stable pixels. Rust Arc/Drop releases the actual once-retired borrow, without
flushing, polling, destroying externally owned textures, or invoking callbacks
while encoding. Platform decoder/native-provider selection remains external.

The GPU half (parallel owner) implements the original canonical Solid/Wireframe/
Edges pipelines and actual retained upload/blur/depth/MSAA resources. Root owns
ordered Scene/Renderer viewport capture plus subsequent ordinary Texture draw.
Original extension 8 itself does no main-pass draw; typed payload owners remain
outside the v1-v10 archive wire, with explicit serialization rejection. Original
provider/package/source-application and native/browser pixels and performance
remain unqualified until the mandated later validation phase.

Original owned source SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/Extensions/Mesh3DExtensionPipeline.cs` | `d3911e0a662038d7d213867278d60621602cb861f9e353c5530b46036e29884c` |
| `src/ProGPU.WinUI/Controls/Viewport3D.cs` | `6a9e079f1e32dfed8294f760f1e5f177f0a814a2e3e8037205415933b49558b9` |
| `src/ProGPU.Scene/Shaders/Mesh3DSolid.wgsl` | `ee1348c02a4c916598432675c1f63af2765474d17f27b3a9975d8944fca08eaf` |
| `src/ProGPU.Scene/Shaders/Mesh3DWireframe.wgsl` | `a450ca485f293e0a227e1e83714bf5c5ea52211e5a6168895b0c7e34cb4fa1a1` |
| `src/ProGPU.Scene/Shaders/Mesh3DEdges.wgsl` | `5044170d295432dbb131f30e6e465554ab606c640b5221b340fa332f6c7be67d` |
| `src/ProGPU.Backend/IProGpuTextureSource.cs` | `b04a3527e977361674633b6214df5b5d2658bb54c12eca868496ebebbb5a7771` |
| `src/ProGPU.Scene/IDrawingExtension.cs` | `44b7e14697cac1f90fd4e57f52c63078ed64ecac1ddeca13d8ac29295d52cbfc` |
