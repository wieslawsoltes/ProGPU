# Exact path-atlas pixel mapping

The Windows drawing-clip reference exposed six Union pixels whose atlas coverage
was exactly 255 but whose intermediate R8 mask was 254. The final image preserved
that error. Both Windows x64 and ARM64 receipts locate the loss in the atlas-to-mask
quad draw, not in Boolean rasterization or final mask sampling. This does not
separately prove which interpolation or sampler arithmetic introduced the loss.

## Contract and paired implementation

`ProGPU.Scene/PathAtlasPixelMapping.cs` is the original implementation. The native
`Backend/progpu_native_path_pixel_mapping.hpp` ports that ProGPU-owned contract;
no third-party implementation is included. All four actual quad corners must be
finite integer points in the exact float integer range, with the same atlas-minus-
device offset. Bounding boxes, matrix labels and near-integer tolerances do not
establish this proof. Fractional phase, residual scaling, mirrors and rotations
retain filtering. Rasterizing a scaled source can still produce an actual 1:1
quad; the proof applies to the resulting corners, not source transform labels.

Managed admission additionally requires unit DPI, the full zero-origin physical
canvas and its unit logical projection, and no late GPU transforms. Native path
and clip execution have full-target projections. Their additional physical-frame
lane admits integral power-of-two DPI only after multiplying every actual corner
by that exact scale and proving the same integer atlas/device offset. Other DPI
values, fractional corners and residual scale keep filtering. The
general vector vertex shader rejects static/late-transformed encodings even if
the original vertex carried the marker. Shape 4's otherwise unused stroke field
carries -1 for the original unit-DPI lane and -2 for the proven native physical
lane. The latter computes the shader offset from `position * dpiScale`, never
from logical coordinates. Neither marker denotes a hairline stroke.

Both native providers and the managed renderer embed the same
`PathAtlasSampling.wgsl`. A flat integer offset and fragment position select the
texel without interpolating UVs. Bounded managed mask passes restore their actual
render origin before addressing; native clip passes already use full-target
coordinates. Non-admitted paths retain their original filtered gradients. Gamma,
edge aliasing, sample grids, Boolean topology and alpha composition are unchanged.
Each native clip node binds its own existing 256-byte uniform offset during both
the path pass and composition; the first node's mapping is never reused for later
tiles. The existing multi-node MIL ellipse/rounded-clip test covers this distinction.

Native direct fills, like clip nodes, rasterize using the original transform's
maximum scale multiplied by target DPI, with translation phase in physical
pixels. Local capture bounds divide by that same scale before final projection.
Managed ordinary path fills now also use the active target DPI for atlas scale
and translation phase. Explicit glyph raster policies remain authoritative, and
the existing retained-command and scene caches already include target DPI.
The 96-path DPI-2 differential matches all 518,400 pixels byte for byte after
this change; its opacity companion remains byte-identical. The managed exact-load
admission remains unchanged, including its separate unit-DPI gate.
The integrated 96/192-DPI hairline companion checks this with independent filled
rectangles: a one-physical-pixel fill must not be rasterized as a half-pixel DIP
tile and then enlarged. The Windows integrated run exposed additional 254/1
coverage values on its 192-DPI atlas transfer. The physical-frame proof addresses
that transfer; no pixel tolerance, coverage clamp or blanket nearest filter is
introduced. Cold/warm union fixtures cover both paths and clip masks at DPI1/2,
including unchanged warm upload counters. Windows rerun remains required.

The proof is fixed four-corner work with bounded stack state and no heap allocation.
There is no extra crossing, readback, upload, GPU submission or pipeline. The
vector varying gains three flat integers; native clip uniforms grow from 16 to
32 bytes within their existing 256-byte stride. Retained generation/viewport/DPI
invalidation and resource ownership remain unchanged. This is a pixel-correctness
fix, not a measured performance improvement or complete application qualification.

## Design references and applicability

Primary references consulted before implementation:

- [Skia sampling options](https://api.skia.org/structSkSamplingOptions.html),
  [Direct2D transforms](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-transforms-overview),
  and [Win2D image interpolation](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasImageInterpolation.htm):
  preserve transform and sampling semantics; reject a blanket nearest-filter
  substitution. Exact copying is admitted only where the pixel lattices coincide.
- [WebRender architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello scene painting](https://docs.rs/vello_api/latest/vello_api/trait.PaintScene.html):
  retain scene-owned preparation and GPU compositing. No new CPU raster path,
  worker job, cache identity, visibility rule or per-frame scene reconstruction.
- [SkParagraph](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h),
  [Parley](https://docs.rs/parley/latest/parley/), and
  [HarfBuzz shaping](https://harfbuzz.github.io/shaping-and-shape-plans.html):
  shaping/layout reuse, fallback fonts, variable-font identity and hinting remain
  outside this path-coverage transfer. Glyph placement and subpixel raster policy
  are not modified to repair a mask sample.
- [WGSL textureLoad](https://www.w3.org/TR/WGSL/#textureload): integer coordinates
  address actual retained texels. The output is their existing R8 coverage, not a
  reconstructed or clamped ideal shape.

Startup/lazy pipeline creation, atlas eviction/generation and device-loss rules,
worker preparation, culling and demand-driven upload are unchanged. Shader source
composition occurs once at managed type initialization or native build time.

## Qualification

### Exact line-edge classification

The shared path shader also admits an exact integer half-plane comparison when
both original line endpoints and every row sample are exact multiples of 1/16.
All scaled coordinates must lie in [-16383,16383]. Coordinate differences are
therefore at most 32766 and each signed product fits `i32`. The shader compares
the two products directly, retaining strict X crossings and half-open Y ranges.
It does not round coordinates or change the sample grid. Other coordinates use
the existing floating-point intersection. Curves, Boolean operations and signed
winding retain the same shared traversal.

This avoids dividing at a sample exactly on an edge, where a rounded reciprocal
can move the intersection across that sample. Four-lane integer arithmetic adds
constant work and no storage, dispatch, submission or managed/native crossing.
This is a precision fix; it makes no performance claim.

Both native provider fixtures include 14 original/reversed line cases on cold
and warm frames. Their independent rational scanline oracle sorts intersections
and fills sample spans. It covers negative endpoints, the signed-product limit,
and coordinates outside the integer bound or off the admitted lattice. Separate
Windows captures and both Metal providers expose the original on-edge failures.
This arithmetic admission does not itself implement Direct2D curve subdivision
or its source-coordinate conversion policy; those require separate transport and
qualification. Windows product/package and application checks remain required.

Matched managed/native admission tests reject every bad corner and axis, nonfinite
coordinates, fractional phase, unequal offsets, scale, reflection and rotation.
Managed and native GPU fixtures compare all channels against independent scalar
rectangle-union membership for both direct fills and clip masks on cold/warm frames.
The existing Windows Microsoft probe retains all 44 transform and 13 bitmap cases,
exact pixel hashes, original process bounds and intermediate diagnostic artifacts.

Passing a local Metal fixture does not qualify Windows, package consumers, popup
applications or releases. Full Build, Svg.Skia parity, Drawing/SVG reference and
application gates remain required before integration.

Local implementation check on macOS ARM64: 316 managed shader/admission/clip/
compositor/layer tests passed with no skips; both native libraries built, and the
geometry utility plus full Direct2D WebGPU/MIL test passed. Native cold/warm pixel
fixtures each retained one submission, and warm frames uploaded no coverage,
vertices or indices. The Dawn provider fixture is included in its existing
provider gate; that provider runtime and Windows results are not yet qualified.
