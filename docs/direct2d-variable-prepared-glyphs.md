# Prepared original variable glyphs

This implementation extends the explicit original-font `OUTLINE` / natural,
horizontal Direct2D path. Direction is retained by the shared
[original placement](direct2d-horizontal-glyph-direction.md). It is not a hint interpreter, a modern
DirectWrite raster implementation, automatic text admission or desktop evidence.
Integrated validation is in progress. Original grayscale pixel differences and
whole-producer/package qualification remain open; the observations below qualify
only the contracts they explicitly exercise.

## Owned instance and paired geometry

The original retained Face5 capture owns the complete font bytes, original face
identity and ordered raw DirectWrite axis tags / float coordinates. The prepared
consumer converts tag byte order explicitly and matches every `fvar` axis exactly
once. Missing source axes, missing genuine Face5 capability, contradictory variable
identity, unknown extra axes and malformed tables do not choose default instances.
The five standard static design attributes may appear outside `fvar`; they are not
invented font axes. Static legacy faces retain their existing path.

The source values are the actual Face5 result, not the preceding factory request.
The original Windows SDK maps the unchanged fine request `0x42c80003` to a face
reporting `0x42c80004`; all five integral pixel-instance weights remain exact.
Forty-eight original SDK controls retain every canonical tag/value bit through
fresh face creation. The integrated reference compares capture against those
original API results and checks the fine canonical round trip, while raw Face5
controls still exercise unnarrowed input floats. No producer normalization or
source coordinate is patched. This follows the separate
[GetFontAxisValues result contract](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nf-dwrite_3-idwritefontface5-getfontaxisvalues).

Normalization promotes the original float exactly to double and uses original
16.16 font endpoints, without first narrowing the source coordinate to 16.16.
The existing native F2Dot14 / `avar` arithmetic remains the variation decoder's
coordinate policy. The new source API strictly validates `avar` 1.0 maps;
`avar` 2.0 and malformed maps remain unsupported rather than silently ignored.
The original signed-16.16 API retains its old behavior.

One prepared owner retains normalized coordinates, HVAR region scalars and maps.
Its mutex guards reusable bounded tuple/outline scratch and the immutable glyph
cache. The shared native simple/composite `gvar` decoder produces the contours.
The same instance supplies both horizontal phantom deltas and the nominal advance;
HVAR retains its existing advance precedence. An explicit HVAR LSB map supplies
the varied bearing, paired with varied outline minimum X. Otherwise the left
phantom supplies the original outline origin. Empty glyphs consume varied advances
without manufacturing ink. Explicit source advances, including zero and negative
values, still take precedence. Compact `hmtx` must contain every original bearing.

Original DirectWrite `GetDesignGlyphAdvances` and `GetGlyphRunOutline` select
HVAR advances through the stored `hmtx` advance index. A compact tail reuses the
last long metric before applying either the implicit or explicit HVAR map.
Forty-five independent SDK controls cover one/two/three long metrics, distinct
base advances and variation rows, implicit/identity/reordered maps, and five
axis positions. Original outline displacement follows that source advance;
`GetDesignGlyphMetrics` reports separate glyph metrics. Prepared TrueType source
placement retains this behavior in its existing immutable glyph cache. The
shared OpenType parser, per-glyph contour/phantom/bearing identity and original
caller advances remain unchanged. No new font callback or per-frame allocation
is added; selection is constant time per cold unique glyph.

The existing one-million-point/segment limits bound each scratch domain and the
published cache/run; scratch is reused under the same owner, never borrowed across
source callbacks. A failed later occurrence may grow private scratch but publishes
neither partial glyph-cache entries nor a partial run. Source face/table/outline
methods are not called per glyph after capture.

## Research and boundaries

The contract was independently implemented using the published
[OpenType HVAR](https://learn.microsoft.com/en-us/typography/opentype/spec/hvar)
and [gvar](https://learn.microsoft.com/en-us/typography/opentype/spec/gvar)
records and the existing ProGPU-owned native variation walkers. No external font
engine implementation was copied. HVAR side-bearing maps are optional, while
advance maps can use implicit glyph indices; contour and phantom variation remain
separate from a hinted component's `USE_MY_METRICS` behavior.

Related primary architecture references were
[HarfBuzz's font-coordinate API](https://harfbuzz.github.io/harfbuzz-hb-font.html),
[SkTypeface variation identity](https://api.skia.org/classSkTypeface.html),
[Vello glyph runs](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html),
[Parley positioned runs](https://docs.rs/parley/latest/parley/layout/struct.GlyphRun.html),
[WebRender scene ownership](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
and [Win2D CanvasFontFace](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasFontFace.htm).
These inform ownership separation, not arithmetic or pixel oracles.

Original Windows instance/outline/nominal-metric comparisons and paired native
provider full-byte controls are required. No expected result is derived from the
decoder under test. CFF/CFF2, simulations, multiple files, sideways
placement, GDI measuring, hinting and unsupported raster policies stay closed.
Neither a prepared object nor an authored fixture proves those contracts or
qualifies arbitrary variable-font application rendering.

## Controls and current qualification

The independent tiny-font fixture retains its own constant expectation table:
five default/positive/negative axis cases across four font alternatives (gvar,
compact gvar, HVAR advance, compact HVAR with both bearing maps). Its glyphs
exercise nonzero left/right phantoms, changed rectangular contours and an empty
glyph with a real changed advance. The original fixture bytes and generic OpenType metrics remain unchanged. Source
RTL rectangles use the independently observed compact-HVAR advance rule.

Both native providers register eighty source configurations across LTR and RTL: each instance uses
explicit and null/nominal advances, with aliased, fractional grayscale, or
transformed clip/layer scope. Every configuration compares all RGBA bytes across
cold, warm and independent geometry, retains exact caller draw/command/submission
counts, and requires nonempty ink, untouched background and opaque target alpha.
Original mutable font callbacks are disabled and their input storage changed after
capture; retained replay must not query them. Existing static controls remain.

The CPU source harness adds sixteen failed source/table preparation controls,
standard non-fvar attributes, static Face5 descriptors, independently known
normalization coordinates, a narrow axis that distinguishes original float from
premature 16.16 conversion, paired phantom/HVAR outputs, untouched failure outputs,
late malformed-glyph whole-run cache rollback and exact-owner rejection. Source
gvar admission explicitly rejects unknown versions/reserved flags without changing
the shared legacy parser. The complete local stock-native suite passes 47/47, including 45 additional
compact-HVAR cases with positioned/null advances, both directions and retained
source retirement. The Windows SDK matches all 45 source advance arrays and 90
complete prepared/original outline bounds. The original variable corpus reaches
all eighty cases; grayscale pixel differences remain failures. These checks do
not qualify the whole producer Build or source applications.

The separate stacked Windows companion creates actual original SDK variable
instances from the same authored bytes and compares original metrics, outlines
and complete images. That companion is an independent required qualification
gate, not a claim implied by these provider fixtures.

## Variable horizontal RTL controls (authored only)

The existing forty LTR configurations, original font bytes, expected design
table, transforms, clips and layer inputs are unchanged. A second direction pass
adds forty RTL configurations over the same four alternatives and five instances,
using original logical IDs `1,0,2`, bidi level 3 and baseline `(56,28)`. Explicit
advances are `12,-3,20`; null advances use the independent per-instance design
table. The third explicit advance remains distinct from the LTR input; it moves
the following pen without defining the current outline width. The no-ink middle glyph still moves the pen.
The original third offset `(-0.75,2.5)` changes its horizontal direction only.

The separate RTL rectangle oracle subtracts the preceding positioned advances
and the current glyph's independently authored varied design width, then applies
its independently authored variable origin. It does not query the decoder or the prepared run. Both providers consume
the shared existing callback with unchanged draw/command/submission assertions,
comparing every cold/warm/independent RGBA byte. Absolute first/last ink and outside
samples supplement the original nonempty/background/channel controls. Mutable
source bytes and axis callbacks are retired before each provider replay, exactly
as in the LTR fixture.

The Windows companion requests genuine variable Face5 instances and adds actual
RTL `GetGlyphRunOutline` bounds plus original `DrawGlyphRun` images. It checks
each prepared contour's logical occurrence against its own independent rectangle,
retained source owner/direction/IDs/offsets/advance policy, and compares complete
images with both independently recorded rectangles and prepared contours through
the original rasterizer. Null advances must also match actual SDK design-advance
queries. These controls use the public [logical outline input](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline)
and [directional offset](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_offset)
contracts; there is no reordering, reshaping or source-output substitution.

Integrated local stock Metal execution passes all eighty configurations with
the corrected design-width origin, including both coverage routes and original
cold/warm/independent comparisons. Final validation still requires the second
provider and independent Windows controls without changing deadlines, counters
or tolerances.
