# Native C++ command parity

`GpuPictureNativeSceneCompiler.GetCommandCapability` is the executable
command-family inventory. `NativePictureCompilerTests` enumerates every
`RenderCommandType` and fails when a new command has no explicit native route.
Payload validation remains stricter than this structural classification and
always returns a typed failure without partially committing a scene.

| Route | Commands | Current boundary |
| --- | --- | --- |
| Direct draw | Rect, path fill, line-only retained path stroke, text, image, analytic geometry, polyline/spline, point/mesh/chart, glyph, and 3D line/mesh families | Curved retained path strokes and CPU-only combined-path materialization remain typed payload exclusions. |
| State scope | Clip, opacity, geometry-mask, opacity-mask, and blend push/pop | Canonical affine rectangles, bounded rounded-mask chains, and solid opacity folding are implemented; resource-backed arbitrary masks remain pending. |
| Nested picture | `DrawPicture` | Immutable retained children are recursively flattened with state-boundary validation. |
| Built-in extension | `DrawExtension` | Line/spline/chart/3D/hatch built-ins are selected by stable extension ID; hatch boundaries reuse retained path batches and shared hatch material kinds, while unknown or object-backed extensions fail closed. |
| Explicitly unsupported | `DrawStaticDxf`, `DrawVisual` | Static DXF and embedded visual commands retain live managed/GPU ownership and cannot enter the pointer-free immutable scene contract. |

Image effects include external same-device RGB textures, zero-copy paired
R8/RG8 or Tier-1 R16/RG16 luma/chroma views, nearest/linear/cubic sampling,
affine color operations, luminance-to-alpha, spherical projection, explicit R8
texture masks, and the shared shader's bounded blur footprint. External image
bindings are ordered by immutable resource id plus primary/chroma/mask role, so
changing a view invalidates the retained image page without copying plane or
mask pixels across the C# / C++ boundary. The managed-authoritative separable
Gaussian implementation is now shared directly with C++: filterable RGB and
R8/RG8 planar sources run two retained horizontal/vertical GPU passes inside
the native command encoder, with no steady-replay interop call or CPU upload.
Tier-1 unfilterable R16/RG16 blur and effect-plus-state-mask-chain packing remain
the final image-effect conformance slice.

This inventory distinguishes parity work from intentional ownership
boundaries; it must not be used to convert an unsupported command into a silent
no-op or an approximate fallback.
