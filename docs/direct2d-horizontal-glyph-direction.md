# Original horizontal glyph direction

The private prepared-original-font path now retains all horizontal bidi levels.
Parity selects the advance direction; it does not reorder source glyphs, reverse
their contours, re-resolve bidi or shape replacement text. Sideways/vertical
metrics, GDI measurement and unsupported raster modes remain separate contracts.
The explicit static-metric sideways family is implemented separately in
[sideways glyph placement](direct2d-sideways-glyph-placement.md); it does not
borrow the horizontal RTL arithmetic or admit combined sideways/odd bidi.

The original request owns its logical IDs, explicit-or-null advances, offsets
and baseline. A left-to-right outline uses the accumulated advance. A
right-to-left outline starts one original design width to the left of its pen.
The caller's positioned advance moves the following pen; it does not replace
the current glyph's design width. Advance offsets
change sign with the run direction; ascender offsets do not. The existing
per-glyph design-origin subtraction and em scale retain their order, including
the paired variable-font origin/advance. Explicit zero and negative advances
retain their meaning, and an empty glyph still consumes its advance.

This uses the public [glyph-run direction](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_run),
[directional offsets](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_offset)
and [logical outline input](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline)
contracts with the existing ProGPU-owned decoder and placement code. The exact
source placement and arithmetic remain subject to the original Windows controls
below; their full pixel gate remains unresolved. No foreign renderer implementation was read
or copied.

Integrated original-SDK execution disproved the initial use of the current
positioned advance as the RTL outline width. At em 62.5, an authored hmtx width
of 500/1000 gives a design width of 31.25 even when the caller supplies 24. The
original run's two rectangles begin at 29.75 and 9.5, rather than 37 and 16.75.
Fifty-four SDK outline controls confirm the design-width rule across three
bearing inventories, three em sizes, LTR/RTL and explicit, zero/negative or null
advances. Source IDs, font bytes, advances, offsets and baselines are unchanged.

The shared prepared path now separates these two roles. Independent static and
variable rectangle expectations use their authored design metrics, and the
Windows fixture checks each original SDK outline occurrence before comparing
pixels. It completes all 48 original pixel cases before failing on any remaining
mismatch, preserving the complete byte comparisons. The Windows ARM64 SDK/WARP
execution now passes all 48 outline observations and all 24 aliased pixel cases;
the remaining 18 grayscale mismatches are a separate unresolved gate. All 47
local stock Metal native tests also pass, including the full GPU corpus.

Complete finite-pen/origin/segment preparation still precedes both cache and run
publication. A later overflow retains the caller's prior result and existing
cache. Direction adds constant work per occurrence, no font callback, additional
native crossing, source shaping, GPU submission or raster policy change. Ordinary
`DrawGlyphRun` and its COM ABI remain unchanged.

## Authored controls, not executed

The original 24 static-font left-to-right pixel cases are preserved. An additional
24 right-to-left cases retain the same three original byte-owned fonts, positive
and negative bearings, compact metrics, explicit/nominal advances and four
coverage/transform/clip/layer configurations. Independent literal rectangles and
absolute ink/gap/background samples do not query the product decoder. Both native
providers compare complete cold/warm/independent images. The actual Windows
reference compares original `DrawGlyphRun`, those rectangles and prepared
contours through the original rasterizer; nominal cases also compare genuine
null advances against original SDK design-advance queries.

Raw controls retain levels 1, 3 and UINT32_MAX, both advance policies, original
logical occurrence order, empty-glyph movement, signed offsets, contour winding,
cache reuse and atomic overflowing RTL origins. Existing unsupported raster,
sideways, measurement and reentrant-source controls remain.

The stacked [variable-font direction controls](direct2d-variable-prepared-glyphs.md#variable-horizontal-rtl-controls-authored-only)
add forty independent original/provider RTL configurations while preserving all
forty variable LTR configurations. These changes do not establish original Windows pixels,
package support, source editor behavior or application parity. All builds,
tests, source verifiers and GPU/UI runs remain deferred to the final integrated
tip; no validation was executed for this checkpoint.
