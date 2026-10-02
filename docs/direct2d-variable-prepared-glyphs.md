# Prepared original variable glyphs

This implementation extends the explicit original-font `OUTLINE` / natural,
horizontal Direct2D path. Direction is retained by the shared
[original placement](direct2d-horizontal-glyph-direction.md). It is not a hint interpreter, a modern
DirectWrite raster implementation, automatic text admission or desktop evidence.
All implementation and authored controls in this stack are **unexecuted** pending
the final integrated validation requested by the user.

## Owned instance and paired geometry

The original retained Face5 capture owns the complete font bytes, original face
identity and ordered raw DirectWrite axis tags / float coordinates. The prepared
consumer converts tag byte order explicitly and matches every `fvar` axis exactly
once. Missing source axes, missing genuine Face5 capability, contradictory variable
identity, unknown extra axes and malformed tables do not choose default instances.
The five standard static design attributes may appear outside `fvar`; they are not
invented font axes. Static legacy faces retain their existing path.

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

## Authored controls (not executed)

The independent tiny-font fixture retains its own constant expectation table:
five default/positive/negative axis cases across four font alternatives (gvar,
compact gvar, HVAR advance, compact HVAR with both bearing maps). Its glyphs
exercise nonzero left/right phantoms, changed rectangular contours and an empty
glyph with a real changed advance. No old fixture bytes or expectations changed.

Both native providers register forty source configurations: each instance uses
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
the shared legacy parser. All are authored only; no build or test was executed.

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
table. The third explicit advance differs from the LTR input deliberately so the
RTL ink regions have a visible gap. The no-ink middle glyph still moves the pen.
The original third offset `(-0.75,2.5)` changes its horizontal direction only.

The separate RTL rectangle oracle subtracts each occurrence's cumulative advance
including its own advance, then applies its independently authored variable
origin. It does not query the decoder or the prepared run. Both providers consume
the shared existing callback with unchanged draw/command/submission assertions,
comparing every cold/warm/independent RGBA byte. Absolute first/last ink and gap
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

All eighty configurations are authored, not executed. No build, syntax check,
source verifier, original SDK probe, GPU run or CI was performed for this child.
The final integrated validation must execute both providers and the independent
Windows controls without changing existing deadlines, counters or tolerances.
