# Owned DrawingImage shader samplers

An original MIL ImageBrush sampler can now name an initialized DrawingImage in
the same channel. The existing owned drawing/tile compiler produces the full
RGBA child picture. It retains the original drawing-bounds origin separately
from the zero-origin natural image extent, then applies the original viewbox,
viewport, brush transform, opacity and addressing. No synthetic bitmap, CPU
raster, new renderer or shader wire is introduced.

Sampler declaration may precede DrawingImage initialization. Capture cannot:
an uninitialized image is an invalid handle. An initialized image with a null
drawing, or an initialized empty DrawingGroup, is genuine transparent content.
Neither is confused with an uninitialized bitmap upload. The picture remains
owned and immutable after the source channel is updated or destroyed.

Before a DrawingImage sampler can take any empty/transparent tile shortcut, the
existing ordered dependency-revision walk checks its entire resource closure.
The sampler-only policy retains the existing depth bound and active-path cycle
check; missing resources fail. DrawingBrush, VisualBrush, BitmapCacheBrush,
external/double-buffered/D3D images and media remain unsupported even inside a
nonpainting branch. Ordinary DrawingImage consumers keep their existing policy.
Dependency deletion and updates retain the original channel graph rules; every
drawing, child, brush, geometry and animation generation contributes to capture
revision. Original sampler transform-animation gates remain unchanged.

## Implementation provenance and applicability

The source contract is Microsoft's public
[ImageBrush.ImageSource](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imagebrush.imagesource)
and [DrawingImage](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.drawingimage)
API. Rendering and ownership reuse ProGPU-owned `append_single_tile_brush`,
`append_drawing_image`, `capture_brush_rectangle` and
`append_cache_resource_revision` from `7a3623381f9af7f5c18d81ff0e367f6a88265016`.
This connects an existing source family to an existing capture capability; it
does not change scene, shader, pipeline, atlas or device-loss architecture.
Both native providers consume the same retained picture resource. Managed
rendering already consumes typed source image/drawing capture rather than this
native MIL admission function; no managed wire or rendering algorithm changes.
No foreign implementation is copied. Dependency traversal remains bounded by
the existing graph/depth budgets and shared capture owns all GPU work.

Controls are authored alongside the connection. All execution, original WPF
observation, native/provider pixels, packaging and application qualification
remain deferred to the final integrated tip.
