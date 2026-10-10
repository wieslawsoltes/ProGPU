# Original variable-font source controls

These fixtures accompany the retained OUTLINE consumer, not modern raster-mode,
hinting, RTL or vertical admission. They are authored for final integrated-stack
execution; no result is claimed here.

`progpu_native_direct2d_variable_font_fixture.hpp` builds a tiny original font
from the established owned three-glyph fixture without changing that fixture's
bytes. Glyph zero has no ink; glyphs one and two have four-point rectangles.
The default font obeys the TrueType variable-font requirement that default LSB
equals xMin and head flag bit 1 is set. Variation deltas nevertheless move both
horizontal phantom points: the flag is not permission to erase varied origins.

The new font owns one wght axis, range 100–900/default 400; avar maps normalized
0.5 to 0.75. Positive gvar tuples expand and translate both outlines and change
both horizontal phantom points. Negative tuples supply distinct opposite-side
observations. A fixed table of independent design bounds, origins and advances
is the oracle, not product-decoded output. The five source weights are 400, 650,
900, 250 and 100. An additional original-SDK coordinate
100.00002288818359375 retains binary32 precision finer than 16.16.

Four font variants share the same expected observations:

- gvar metrics with complete hmtx records;
- gvar metrics with a compact hmtx advance and bearing tail;
- HVAR advances with gvar left-phantom placement;
- compact hmtx plus HVAR advance, explicit left-bearing and right-bearing maps.

Both HVAR side-bearing maps agree with the same varied outline and phantom
positions. The empty glyph's varied advance still moves the following glyph.
The builder also authors name, STAT, OS/2 and Unicode cmap metadata for the
actual SDK loader. It uses only public OpenType wire contracts and existing
ProGPU-owned assembly utilities; no external font or foreign implementation is
copied, parsed to generate expectations, or downloaded.

The Windows reference creates actual instances using
`IDWriteFontFace5::GetFontResource` and `IDWriteFontResource::CreateFontFace`,
retaining registered-loader lifetime through every instance and prepared
context. It independently reads the original canonical axis inventory, checks
owned capture order/tag/value bits and complete file bytes, queries original
INT32 design advances, and compares original `GetGlyphRunOutline` bounds with
the authored design table. The fine-coordinate case checks original readback,
not a manufactured fractional outline or widened interpretation of INT32.

The forty paired pixel cases cover four fonts, five instances and both supplied
and absent advances. Original DrawGlyphRun, independent geometry and prepared
geometry must agree for every byte; absent advances are additionally compared
with actual original design advances. Identity/aliased, fractional/grayscale
and transformed clip/opacity scenarios use the same recorded source inputs.
The consumer's two native-provider fixtures retain cold/warm/independent
comparisons and no-source-callback ownership controls. Existing cases, deadlines
and assertion thresholds are unchanged.

Composite variations are not part of this initial three-glyph reference
inventory. Passing this inventory would not establish that separate family or
modern DirectWrite raster equivalence.

## Primary contracts

- [OpenType gvar](https://learn.microsoft.com/en-us/typography/opentype/spec/gvar)
  and [common variation formats](https://learn.microsoft.com/en-us/typography/opentype/spec/otvarcommonformats)
  define tuple/point/phantom data and item-store encoding.
- [HVAR](https://learn.microsoft.com/en-us/typography/opentype/spec/hvar),
  [fvar](https://learn.microsoft.com/en-us/typography/opentype/spec/fvar) and
  [avar](https://learn.microsoft.com/en-us/typography/opentype/spec/avar) define
  metric maps, user axes and normalized remapping.
- [CreateFontFace](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nf-dwrite_3-idwritefontresource-createfontface)
  selects an original SDK instance; its actual axis readback remains authoritative.
- [GetGlyphRunOutline](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline)
  supplies original outline/advance/offset observations independently of the
  prepared native decoder.

All build, syntax, source, SDK, native, GPU and platform checks remain deferred.
