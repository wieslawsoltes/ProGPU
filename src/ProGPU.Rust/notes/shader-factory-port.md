# Original immutable shader factories

Implementation only; source formatting performed, with no compiler, tests, shader/runtime/browser validation or generated artifacts.

`shader::{Shader, ShaderStops, ShaderGradientOptions}` connects all original factory categories to existing actual `FilterShaderSource` owners: empty/byte/float color, linear/radial/two-point conical/sweep gradients, fractal/turbulent Perlin, image/picture, fixed/arithmetic composition, local-matrix/color-filter wrappers and reflected runtime instances. Small gradients retain inline stops; overflow is immutable shared storage. Positions remain unsorted; byte division, float clamping, default offsets, selected linear-space interpolation, eight-entry inverse cache, singular gradient policy and exact degenerate sweep averaging follow the owned source. Independent color lanes use safe SIMD; stop accumulation stays ordered.

Public `to_brush` copies mutable output and applies original CPU solid/stop color filters. Canvas separately treats shader color-filter wrappers as GPU captures. Ordinary Paint shader color filters now modify source stops before interpolation, preserving paint alpha separately; Perlin is not falsely color-filtered by that CPU policy. Local-matrix wrappers preserve source identity and retain the separate original Matrix4 inverse policy.

Texture factories make the original actual owned base-level GPU copy and selected canonical mips. Bitmap/pixel producers retain actual immutable pixel owners; picture factories retain actual commands and explicit tile/cull/color inventory. `prepare_scene`/`render` use genuine initialized GPU capture and the existing complete image-filter shader executor, with source/device admission and original runtime/perspective gates. CPU image-shader uploads now receive the original mip sampling request. No source callback runs during encoding. Canvas analytic commands also retain the actual source center for the existing three-argument material API, including the independent original circle and ellipse center expressions.

Remaining independent work: full original Paint.GetFillPath, legacy text/drawable facade, existing perspective/F16/source-language execution gates and provider/pixel/performance qualification. No foreign compiler/renderer implementation was used.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPaint.cs` | `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff` |
| `src/SkiaSharp/SKShader.Overloads.cs` | `36b1c4795b6179732a1e720bb710fd2f1c3cecff3a5e38117cdb2e85c02296cb` |
| `src/SkiaSharp/SKImage.cs` | `96827d04df213d63249cb3fb4005bcc4e811367ced4a16c27a0d9c21b66c2273` |
| `src/SkiaSharp/SKPicture.cs` | `10f7fe2fd1682989e8baff48afe1c911c5b8f166b9e754772a210269e1d1c26c` |
