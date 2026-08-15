# Native C++ command parity

`GpuPictureNativeSceneCompiler.GetCommandCapability` is the executable
command-family inventory. `NativePictureCompilerTests` enumerates every
`RenderCommandType` and fails when a new command has no explicit native route.
Payload validation remains stricter than this structural classification and
always returns a typed failure without partially committing a scene.

| Route | Commands | Current boundary |
| --- | --- | --- |
| Direct draw | Rect, path fill, text, image, analytic geometry, polyline/spline, point/mesh/chart, glyph, and 3D line/mesh families | Path strokes and CPU-only combined-path materialization remain typed payload exclusions. |
| State scope | Clip, opacity, geometry-mask, opacity-mask, and blend push/pop | Canonical affine rectangles, bounded rounded-mask chains, and solid opacity folding are implemented; resource-backed arbitrary masks remain pending. |
| Nested picture | `DrawPicture` | Immutable retained children are recursively flattened with state-boundary validation. |
| Built-in extension | `DrawExtension` | Line/spline/chart/3D built-ins are selected by stable extension ID; unknown or object-backed extensions fail closed. |
| Explicitly unsupported | `DrawHatch`, `DrawStaticDxf`, `DrawVisual` | Hatch requires its dedicated shared shader material; static DXF and embedded visual commands retain live managed/GPU ownership and cannot enter the pointer-free immutable scene contract. |

Image effects currently include external same-device RGB textures,
nearest/linear/cubic sampling, affine color operations, luminance-to-alpha, and
spherical projection. Auxiliary chroma/YUV, explicit texture masks, and the
managed-authoritative live Gaussian prepass remain the image-resource slice.

This inventory distinguishes parity work from intentional ownership
boundaries; it must not be used to convert an unsupported command into a silent
no-op or an approximate fallback.
