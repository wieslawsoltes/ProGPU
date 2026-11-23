# Original F16 SaveLayer targets

Implementation only; no builds, tests, browser execution, fixtures or performance validation were run.

`SaveLayerFlags::F16_COLOR_TYPE` now selects an actual RGBA16Float layer texture and matching renderer/pipelines before source painting. The previous-layer capture uses the same selected format. Original active clips, full-canvas/transformed-far-edge extent arithmetic, bounds clipping, initialized backdrop, source order, final paint alpha and blend remain unchanged. Ordinary layers and ordinary `RenderSurface::new` still select the original RGBA8 storage.

`PictureCaptureSession` owns one actual image context and separate exclusive renderer/cache generations per RGBA8/RGBA16Float target. Canvas preparation shares this session across nested pictures, prior captures, source layers and filter captures. The active renderer is detached before asynchronous preparation: cancellation retires that private owner rather than publishing incomplete cache state. Completed textures are independent immutable generations, retaining actual context/texture and submission owners. No renderer is inserted into a shared cross-thread image context, and no UI callbacks execute during encode.

`TextureImage::from_picture_with_format` exposes explicit source capture format; the existing factory remains RGBA8. Actual F16 textures are admitted by `GpuImage`, `TextureImage` and borrowed `RenderSurface` targets. Same-context snapshots/subset copies/mipmap generation preserve the actual format. Captured metadata records the actual half-float storage. Original image-context mip caches already key the actual format; renderer-local pipelines and effect/source caches cannot cross the session's format identities.

Canonical geometry, text, image, mask-composited, backdrop, managed shader and destination-resolve pipelines target the actual renderer format. Source-AA floating targets use the original non-byte route; initialized targets preserve its independent background/destination branch. Capture, retained-layer, cached-picture, owned-shader, source-mask and advanced-target resource accounting now uses eight bytes per F16 texel; R8 coverage remains independently accounted.

Original filter boundaries are retained. `CreateOwnedFilterTexture`/`RentFilterScratchTexture` and canonical compute shaders allocate RGBA8 outputs; they read the actual F16 source directly. There is no RGBA8 substitution before float source capture or filtering. Identity graph results and original zero-radius morphology whole-texture copies retain their source format and mip inventory. The retained filter plan transports those result formats so the final wrapper capture and mip generation do not narrow a float no-op result. Source-copy budgets and actual copy metrics count eight-byte texels.

Independent original boundaries remain explicit: SKImage's source readback contract only describes four-byte RGBA/BGRA rows, so F16 `TextureImage::readback` rejects before copying/mapping instead of misinterpreting half-float bytes. Existing cross-context GPU-only image, native bytecode/frozen-target, source text and provider gates remain unchanged. No new CPU half-float producer or native/provider/application pixel qualification is inferred.

Original SHA-256 provenance:

| Source under `src/` | SHA-256 |
| --- | --- |
| `SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `SkiaSharp/SKSurface.cs` | `c52ae704abdd8ffbd044386efcb439d4c67adb4b3b08a895d6ab4018f709a9b6` |
| `SkiaSharp/SKImage.cs` | `96827d04df213d63249cb3fb4005bcc4e811367ced4a16c27a0d9c21b66c2273` |
| `ProGPU.Compute/ComputeAccelerator.cs` | `7e3cd2be4a1f02cfe44f9b784759cf5ae5677f089c5f7c5c2db7e2a4b28c8ea1` |

Exact original entry points: `RenderLayerToTexture`, `RenderPreviousLayerToTexture`, `GetSaveLayerTextureFormat`, `CreateOwnedFilterTexture`, `RentFilterScratchTexture`, `CreateNativeTextureReadbackInfo`, and existing canonical compute source-input/owned-output methods.
