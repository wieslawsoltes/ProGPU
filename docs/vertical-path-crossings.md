# Exact vertical path crossings

The original Windows Gallery scenes spend about 10–13 seconds after submission
preparing cold coverage. An unchanged warm scene completes that interval in
77 milliseconds with no coverage dispatch or upload. Forced glyph routes retain
most of the cold delay. These observations identify raster preparation as the
remaining investigation area; they do not attribute every interval to paths.

Large original panel paths contain long exactly vertical sides and small curved
corners. Every admitted vertical side previously repeated the complete bounded
integer half-plane admission and arithmetic for each sample row. With equal
endpoint X, the admitted integer comparison reduces to the same strict X
comparison for either winding direction. The shared shader now selects that
comparison while retaining the original floating intersection expression. This
preserves the original behavior when finite Y subtraction overflows. There is no
epsilon, rectangle recognition, curve removal or source geometry substitution.

The nonvertical helper remains byte-identical apart from its private name.
Endpoint/Y admission, crossing order, winding signs, sample positions, sample
count, Boolean/signed programs and four-byte output packing remain unchanged.
The same shader resource supplies managed PathAtlas and both native providers,
including retained clips. Complexity remains O(A*S) for A samples and S segments,
with O(1) helper storage and no additional upload, dispatch or lifetime owner.

## Design sources and scope

The implementation is original ProGPU code, derived only from
`PathRasterizerCommon.wgsl` at `1f01d674a245717751c49b9c8d3dcf3b69eba664`.
Primary sources rechecked on 14 October 2026 inform architectural boundaries:

| Reference | Applied boundary |
| --- | --- |
| [Skia/SkParagraph](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h), [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout) | Keep reusable layout, painting and interaction separate. |
| [Direct2D performance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance), [Win2D resource lifetime](https://microsoft.github.io/Win2D/WinUI3/html/LoadingResourcesOutsideCreateResources.htm) | Preserve device-owned batches and resource reuse; add no flush or per-edge submission. |
| [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html), [Vello](https://github.com/linebender/vello) | Preserve retained scene/visibility boundaries and GPU raster work. |
| [Parley](https://github.com/linebender/parley), [HarfBuzz](https://harfbuzz.github.io/what-does-harfbuzz-do.html) | Shaping, font selection and layout remain independent CPU results. |

Startup/lazy initialization, retained scene reuse, visibility, cache keys and
eviction, demand uploads, worker preparation, device loss and atlas generations
are unchanged. DPI, subpixel sampling, hinting, fallback and variable-font policy
are unchanged. No external algorithm or implementation was transplanted.

## Validation and remaining qualification

The raw differential invokes the unchanged independent original path shader for
252 cases, comparing all eight signed lanes and untouched tails. Cases include
both directions, exact and non-grid coordinates, near-vertical rejection, bounded
integer limits, large finite coordinates, intermediate overflow and subnormals.
Independent strict geometric assertions cover normal/zero sample coordinates;
the full GPU differential also retains subnormals, which
[WGSL permits implementations to flush](https://www.w3.org/TR/WGSL/#floating-point-evaluation).
Existing original shader fixtures and expected pixels are unchanged.

Local Metal passes all 43 focused tests and all 48 configured native CTests.
An ABBA sequence preserves every byte of both independently retained original
Metal scene images, with original draws/submissions and actual library identity.
Those short local timings overlap and do not establish a Windows improvement.
Windows core CI now includes the raw crossing and complete single/linear/signed
path differentials. Whole producer CI, matched final profiling, original Windows
scene pixels and ordinary Gallery acceptance within its original 60 seconds
remain required before release.
