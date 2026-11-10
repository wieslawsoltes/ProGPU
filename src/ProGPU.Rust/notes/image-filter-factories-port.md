# Original image filter/picture/convenience producers (implementation only)

`TextureImage::apply_image_filter_in` preserves the original source intersection,
standardized integer output bounds, wrapping signed extent/translation arithmetic,
optional output-context validity, transparent translated SaveLayer, nearest source
draw, source alpha/color-space metadata, actual snapshot and explicit cross-context
readback transfer. `AppliedImageFilter` owns the completed image, exact subset and
integer offset; the floating overload converts that offset. The capture context is
explicit rather than an invented process-global Current. Null results publish no
output object. `from_canvas_picture` implements the optional full paint layer,
including independent color/image filters, instead of reducing paint to picture alpha.

Canvas bitmap/image/surface APIs freeze actual source generations, preserve original
LTRB-to-XYWH and point/far-endpoint arithmetic, exact legacy-quality rejection,
cubic coefficients, mip/aniso selection, null-surface bypass and one snapshot per
surface call. `SampledImage` has its own immutable recording and complete source/device
admission; CPU lowering and GPU realization reuse the ordinary image AA clip and
half-pixel far-edge extension. Source conversion precedes independent paint filtering.
`ImagePatchPolicy::ManagedImage` retains the original Compositor's unit q/patch-opacity,
signed destination, out-of-range UVs and nonpositive source-extent fallback. Native
input gates remain separate. The prior atlas legacy overload now shares the actual
paint-quality rejection (CPU bitmap resize's unknown-quality policy stays independent).

GPU filtering remains in the existing canonical graph executor. Dependent command
ordering is scalar; independent vertex/byte conversion keeps existing SIMD producers.
No new dependencies, raw pointers, callbacks during encode, fixtures or artifacts.
Only owned-file formatting ran; builds, tests, pixel/performance/browser qualification
remain deferred under the user's complete-code-first instruction. Existing affine,
source-language, device/texture and package qualification boundaries remain explicit.

Runtime-effect availability was re-read before this slice: original
`SKRuntimeEffectProgram` only owns source/reflection, and original shader/blender
execution explicitly rejects. The existing Rust reflection/bindings/owner graph and
distinct runtime color-filter GPU-pass/CPU-Clear policies already port that source.
An executable language compiler is not an unported original implementation; this
source limitation is not feature or source/application qualification.

| Original ProGPU-owned source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKImage.Api.cs` | `42b67d84e9cb98679221ee4232f1c6d49405cc9627a42e8f0535a14498643cd1` |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/SkiaSharp/SKSurface.cs` | `c52ae704abdd8ffbd044386efcb439d4c67adb4b3b08a895d6ab4018f709a9b6` |
| `src/SkiaSharp/SKRectI.cs` | `4dfcfa0018ddb1ef6a46b09e26af2467f547335ebd4d89ba44aa52f0b24bd3b2` |
| `src/SkiaSharp/SKPaint.Text.cs` | `6b3c9c6d6434a4e75f1cdaaad1909846218bab6224e1a8036d321fb62d200f36` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/SkiaSharp/SKRuntimeEffect.cs` | `06bcaca91ec4fcfe744eca574d2aa9c412d5d646635e9bb3ff33745bcc25d23a` |
| `src/SkiaSharp/SKPaint.cs` | `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff` |
| `src/SkiaSharp/SKShader.Overloads.cs` | `36b1c4795b6179732a1e720bb710fd2f1c3cecff3a5e38117cdb2e85c02296cb` |
