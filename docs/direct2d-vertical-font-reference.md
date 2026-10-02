# Original vertical-metric fonts and sideways reference

These fixtures and controls are authored, **not executed**. No font probe,
build, test, syntax check, renderer run or CI dispatch is part of this checkpoint.
Original Windows, both native providers and complete final-stack gates remain
required. The completed Rotate/Skew numeric-probe permission does not extend to
fonts.

## Independent original-owned font bytes

`progpu_native_direct2d_vertical_font_fixture.hpp` owns all new outline and
vertical-metric bytes. It reuses only original ProGPU sfnt/CFF construction
utilities and metadata. Existing font fixtures remain unchanged. Reassembly
sorts the directory, writes table checksums with zeroed head adjustment, then
writes the whole-file checkSumAdjustment. There are no imported font/program
bytes or product-decoder-generated expectations.

The static TrueType font has a genuine empty glyph and two asymmetric rectangles:
`[20,-40,300,360]` and `[-30,20,170,520]`. Horizontal advances are 480/600/700;
the two ink glyphs have horizontal origins 8/14. Full vertical metrics have
advance heights 900/1000/1100 and top bearings 700/140/80. The ink origins are
500/600. A separate compact vmtx form has one long metric and the entire two-item
bearing tail, making all base advance heights 900. The empty TrueType glyph has
an actual advance but no fabricated vertical origin from nonexistent glyf bounds.

The static CFF font encodes those same contours as independent Type2 programs,
with widths agreeing with hmtx. Its VORG default is 700, with an explicit glyph2
override 600; vmtx bearings 700/340/80 agree with the contour maxima. Both full
and compact metrics are available. CFF horizontal origins remain zero; no
TrueType phantom-origin rule is imported into CFF.

The independent variable TrueType extension uses actual wght values
400/650/900/250/100 and identity axis normalization (no avar). Its positive top
and bottom phantom delta pairs are `(24,-40)`, `(80,-16)`, `(32,-96)` for the
empty/first/second glyph. The negative tuple is minus half the positive tuple.
Separate horizontal phantoms and asymmetric contour changes prevent the old
horizontal metric reader from masquerading as vertical support. Literal tables
record every original coordinate/metric and every top/bottom delta pair; no
product normalization, outline or variation function generates those expectations.

Paired VVAR alternatives have implicit advance rows or nonidentity explicit
advance maps plus optional top/bottom-bearing maps. Advance deltas are 64/96/128;
the top-bearing and bottom-bearing deltas agree with the same contour/phantom
changes. Explicit mapping permutes the first rows so incorrectly using glyph
indices is observable. Absence of an optional map remains distinct from a
present mapping to a zero adjustment.

The CFF2 extension has genuine fvar identity, fixed contours and an explicit
VORG default/override. VVAR-absent instances are a positive fixed-metric family.
Optional VVAR advance and vertical-origin maps are independent of TrueType
phantoms; origin rows adjust 24/80/32 at the positive endpoint. These are raw
metric fixtures, **not variable sideways source admission**. The current source
consumer still has its original explicit variable-origin gates.

## Genuine original Windows controls

`progpu_native_direct2d_vertical_glyph_reference.hpp` is registered after the
existing prepared/CFF controls. It creates the original static TT/CFF fonts from
complete owned files through the registered DirectWrite loader, asks the SDK to
identify the actual outline family, and compares retained source bytes/face
identity. The SDK table accessor reads the complete vhea/vmtx and CFF VORG bytes,
including the compact tail, without a product parser supplying expected data.

Both ordinary and sideways `GetDesignGlyphMetrics` calls retain actual design
metrics. Original `GetDesignGlyphAdvances` calls provide separate horizontal and
vertical advances. The no-ink glyph only asserts supported advances; it is not
used to infer an absent TrueType ink origin.

The full pixel inventory has 24 configurations: two outline families, full versus
compact metrics, supplied versus null advances, and three original source frames.
It uses the source consumer's independently authored literal rectangle oracle,
not native output, to compare original `GetGlyphRunOutline` bounds and complete
original DrawGlyphRun images. Prepared geometry is separately rasterized by the
same original target. The null-advance path also compares an original draw using
actual original vertical design advances. Raw BOOL values 1 and -1 are retained.

The fixed input is em15.625/UPM1000, baseline `(4,20)`, source glyphs `1,0,2`,
supplied advances `16,-3,9`, and offsets `(.25,.5),(0,0),(-.75,2.5)`. Frames retain
identity aliased rendering, fractional translated grayscale rendering, and an
outer 90-degree transform with captured clip/layer opacity. No tolerance, inferred
offset correction, rounding policy or expected-pixel adjustment is introduced.
Authorship is not evidence that the original SDK has passed these controls.

The raw strict-metadata/VVAR adversarial cases and paired native provider replay
are owned by the corresponding producer/consumer fixtures. Variable source
placement, CFF without VORG, combined sideways/odd bidi and modern raster modes
remain separate contracts rather than being admitted by these tests.

## Primary contract sources

The wire construction uses [vhea](https://learn.microsoft.com/en-us/typography/opentype/spec/vhea),
[vmtx](https://learn.microsoft.com/en-us/typography/opentype/spec/vmtx),
[VORG](https://learn.microsoft.com/en-us/typography/opentype/spec/vorg),
[gvar](https://learn.microsoft.com/en-us/typography/opentype/spec/gvar), and
[VVAR](https://learn.microsoft.com/en-us/typography/opentype/spec/vvar).
The observation boundary is the original
[GetDesignGlyphMetrics](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getdesignglyphmetrics),
[DWRITE_GLYPH_METRICS](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_metrics), and
[GetGlyphRunOutline](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline)
contracts. No foreign engine implementation is an oracle or implementation input.
