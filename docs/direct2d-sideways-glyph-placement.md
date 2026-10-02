# Original sideways glyph placement

The private prepared-original-font route supports even-bidi sideways OUTLINE
runs with natural measurement and explicit static vertical metrics. This is an
implementation checkpoint, not original Windows or application qualification.
Ordinary source factory/raster selection and the public COM ABI are unchanged.

## Retained source contract

Any nonzero original BOOL selects sideways placement; its raw captured value
remains unchanged. Glyphs keep their original logical occurrence order. Each
outline rotates left around its original vertical origin before the run's
baseline, advance and offset translations. The outer source transform remains
on the retained draw; it is not baked into a second font or used to reshape.

The strict metric owner is created lazily for sideways requests from the same
immutable original bytes. Horizontal requests never inspect newly required
vertical tables. A complete vhea/vmtx pair supplies nominal advance heights,
including compact repeated-advance tails and empty glyphs. TrueType top origins
use original yMax plus top side bearing. CFF uses explicit VORG. The horizontal
coordinate of the vertical origin is the retained horizontal origin plus half
the actual horizontal advance. An empty glyph still moves the pen but does not
need an invented ink origin.

Explicit signed advances, including zero and negative values, override nominal
heights. Advance offsets move along the run, ascender offsets move screen-up,
and neither changes subsequent pen positions. Design-lane exchange precedes
the same subtract/multiply/add placement sequence used for horizontal contours.
Independent XY pairs use the existing platform SIMD policy; the fixed remaining
quadratic/cubic controls retain a scalar tail. The original contour orientation,
paint, clipping, two-axis DPI and target transform remain retained.

Missing metric pairs, variable vertical origin/rounding, CFF without a proven
origin, and sideways combined with odd bidi remain unfinished source contracts.
They fail explicitly rather than synthesizing hhea metrics, using a control-point
envelope, applying unvaried metrics to a variable outline, or borrowing horizontal
RTL arithmetic. All placement and finite checks precede new cache, metric-owner
and output publication; a failed tail retains the previous result.

## Design sources and applicability

The public DirectWrite [glyph run](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_run),
[metrics](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_metrics)
and [offset](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_offset)
contracts define the direction/origin split. Exact original arithmetic and pixel
behavior still require the final independent SDK controls. No external
implementation source was read or copied.

The architecture retains the distinction between glyph placement and a run's
outer transform, also explicit in [Vello's glyph builder](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html)
and [Win2D glyph orientation](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasGlyphOrientation.htm).
[HarfBuzz's direction-specific origins/advances](https://harfbuzz.github.io/harfbuzz-hb-font.html),
[Parley's positioned glyphs](https://docs.rs/parley/latest/parley/layout/struct.Glyph.html),
[WebRender's glyph instances](https://docs.rs/webrender_api/latest/webrender_api/struct.GlyphInstance.html)
and [Skia's separate path/metrics operations](https://api.skia.org/classSkFont.html)
support reusing captured layout rather than reshaping source text. These API
comparisons do not establish their cache, hinting or source-compatibility policy
as ProGPU's own, and none is an original DirectWrite numeric oracle.

This change is confined to the native original-COM source adapter. Both native
render providers consume its same retained geometry through their existing
scene, upload and raster paths. No managed shaping/layout API, C wire, shader,
atlas policy, font fallback, device-loss handling, culling or GPU submission is
changed. First sideways preparation adds one bounded source-table preflight;
warm placement is linear in retained occurrences/segments, with no source font
callback or additional native crossing. No performance result is claimed.

Builds, source verifiers, CPU tests, original SDK controls and GPU/UI execution
remain deferred to the final integrated tip unless separately authorized.
