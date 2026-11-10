# Managed ImageEffect command and pipeline port

Implementation only; no build, shader checks, tests, browser runs or benchmarks were executed. Existing explicit/native uniforms keep their independent admission.

The owned `ManagedImageEffectCommand` and `ImageEffectCommandData` lower RGB/typed planar sources, color matrices, luminance-to-alpha, spherical quaternion metadata, source rectangles and opacity into the existing ordered Scene ImageEffect renderer. `ImageEffectGeometry` retains original rectangle/Matrix4 intent through source capture; matrix composition precedes corner transformation. Original QuadClipper arithmetic is shared with BackdropMaterial, including mirrored orientation, independent far edges, threshold, interpolation order and rotated-quad passthrough.

`ImageEffectDrawPolicy::ManagedExtension` selects actual source alpha and original DstOver/Multiply/Screen premultiplied forcing. Pipeline identity includes target format, chain, output alpha and blend. All original fixed factors/default SrcOver fallback remain distinct from semantic layer blending and vector/text output selection. Visual ColorMatrix rendering uses this production route. Explicit masks retain their original precedence; binary clips remain independent. Canonical ImageEffect already adds the actual render origin before mask sampling.

Managed samplers retain original base LOD maximum 1, atlas linear mip interpolation, independent mip filters with maximum 32, and Cubic/Fant atlas fallback. Original live blur requires sigma (0.01,32], admitted RGB storage or half-size planar chroma. Other filterable inputs use the original bounded fragment blur, including finite sigma beyond 32 and R8 RGB. Unfilterable inputs without an admitted live route reject before mutation. Live output retains actual RGBA16F/source format, complete source ownership and fresh encode-time pixels; memory budgets count eight bytes for F16. Metrics distinguish actual inline blur and original blend fallback draws.

Remaining boundaries: mutable C# LastError/string state is represented by Rust Result, not emulated; media-provider normalization/transport and native packet policies stay independent. The original mutable pools, view-generation binding cache and same-frame blur deduplication are now implemented with independent prepared/submission leases; see `image-effect-cache-port.md` for exact producer and frame-clock admission. Runtime quaternion SIMD/FMA behavior, original pixels, browser/device/source/package qualification and performance remain deferred.

Original SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/ImageEffectParams.cs` | `833656e3f5567bc77b18b656278c1a273bdd72a2d2b1182ec96afe7a64eaea8e` |
| `src/ProGPU.Scene/DrawingContextImageExtensions.cs` | `ec89b557bb1c55d650924eb691715add3fdc2adaa49a400bd2b138faf7c640a0` |
| `src/ProGPU.Scene/Extensions/ImageEffectExtensionPipeline.cs` | `333e632ed7b0ff7ec9cbe14fa77cba5e4aa8169be37dda076950ebe7e667ef0d` |
| `src/ProGPU.Scene/Extensions/QuadClipper.cs` | `da3612c8915b465229a6f9208a8187a6430b56bd9d856aee06209a9bf6c2f0d9` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/RenderPipelineCache.cs` | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| `src/ProGPU.Scene/Shaders/ImageEffect.wgsl` (unchanged) | `994eb88551a7c7db875061fb7b372e5c68b976591faa65c0a80762f0726b45f8` |
