# CFF vertical origins and DirectWrite compatibility

The prepared-original-font DirectWrite adapter derives an absent CFF/CFF2 VORG
origin from the maximum selected control-point Y and the original vmtx top side
bearing. Original Windows `GetGlyphRunOutline` observations establish this source
placement policy independently of its integer metric query. The adapter reuses
its exact retained, matrix-transformed/varied contours without another decode,
source callback or font substitution. Explicit VORG still takes precedence.
TrueType bounds and phantom handling are unchanged.

## Separate arithmetic contracts

`read_directwrite_outline` selects the source control maximum. `read_outline`
retains the exact natural-curve maximum for callers that need geometric bounds.
Their result identifies which policy produced the origin. Both validate selected
points and segment kinds, combine the original bearing in double precision, and
publish only complete finite results. Neither invents integer rounding or an
int16 clamp. Empty contours keep their actual advance without fabricated ink.

The exact curve reduction still includes endpoints and interior derivative roots.
Derivative degree reduction uses exact zero. The stable quadratic formula and
double Bernstein evaluation retain the small-root, repeated-root, endpoint and
subnormal controls. Selecting the DirectWrite policy does not alter those bounds.

The completed source origin joins the existing per-font/per-instance vertical
cache. Metric and contour entries publish only after the complete run succeeds.
The operation remains O(S) time and O(1) scratch for S retained segments, with no
repeat reduction on warm glyphs. GPU rasterization, placement SIMD, submission
ownership and public interfaces are unchanged.

For the admitted CFF2 VORG/VVAR family, original Windows metrics and outlines
retain VORG across axis instances while varying the vertical advance. The source
adapter therefore validates the VVAR origin map but does not add its delta.
The generic VVAR reader and its numeric tests remain unchanged. An origin map
without the required original VORG base remains rejected; missing vertical-table
synthesis is a separate contract.

## Independent source evidence

The unchanged two-arch fonts distinguish curve maxima 300/301.5 and 280 from
control maxima 400/402 and 380. Original horizontal and sideways SDK outlines
at em1000 and em15.625 establish source origins 480/482 and340. The second font's
curve maximum is fractional, but its controls and observed origin are integral;
this does not establish a general fractional INT32 rounding rule.

Literal prepared coordinates now follow those original observations, while the
separate algebraic tests still require exact curve origins 380/381.5 and240.
The original font writer, full source bytes, explicit/null advances, empty middle
glyph, raw BOOL values and three reference frames are unchanged. Both providers
retain cold/warm full-byte comparisons and ownership controls. See the
[original reference inventory](direct2d-cff-contour-origin-reference.md).

The policy is based on original SDK observations, not another renderer's code.
The [vmtx](https://learn.microsoft.com/en-us/typography/opentype/spec/vmtx),
[VORG](https://learn.microsoft.com/en-us/typography/opentype/spec/vorg), and
[VVAR](https://learn.microsoft.com/en-us/typography/opentype/spec/vvar)
specifications describe the underlying tables; source compatibility decisions
remain explicit at the DirectWrite adapter boundary. Full Windows pixel,
provider/package and application qualification are separate gates.

Validation on 2026-10-09: all49 local macOS ARM64 native CTests pass. The original
Windows ARM64/MSVC software-adapter run completes1121 strict comparisons with128
remaining full-byte failures, down from204. All CFF control-point, source-origin,
variable bearing/origin and run-envelope comparisons pass. Twelve additional
original-origin assertions retain both metric orientations. This is source
placement evidence; the remaining pixel failures still reject qualification.
