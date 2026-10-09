# Original variable-sideways font reference

Integrated original Windows ARM64 execution now completes all 80 configurations.
The original metric and pixel comparisons still fail, so this is not source,
GPU, package or application qualification. The original 24 static sideways
controls are unchanged.

## Strict independent inventory

The new Windows companion consumes the source consumer's frozen
`variable_sideways_pixel_fonts` inventory. Its eight alternatives are:

1. TrueType gvar, complete vertical metrics.
2. TrueType gvar, compact vertical metric tail.
3. TrueType gvar plus implicit VVAR advance mapping.
4. TrueType gvar plus nonidentity VVAR advance/side-bearing mappings, compact metrics.
5. CFF2 fixed metrics without VVAR.
6. CFF2 fixed metrics without VVAR, compact metrics.
7. CFF2 with VVAR advances but no vertical-origin map.
8. CFF2 with VVAR advance/origin maps and compact metrics.

Every family uses all original authored wght values 400/650/900/250/100 and both
supplied/null advances: 80 configurations. These source values produce integral
authored design metrics, so no fractional design-metric rounding rule is inferred.
The retained frame variant is selected once per configuration from the existing
identity-aliased, fractional-translation-grayscale and outer-90-degree/clip/layer
families. It is not a new exhaustive 3-way multiplier.

The expected rectangle coordinates are independent literal tables, reviewed
against the authored source metrics and fixed em/baseline/offset contract. They
do not call the source consumer, font decoder, metric owner or variation reader.
The fixture retains the empty glyph's actual varied advance while making no
claim that its missing TrueType ink bounds establish an origin.

## Actual original source observations

The companion registers an original DirectWrite in-memory loader, identifies the
real outline family through file analysis, obtains the genuine Face5 font
resource, and creates the actual original axis instances. Complete SDK-returned
axis order, raw tags and binary32 values are compared with the owned source
capture; original bytes, face type/index and simulations are retained.

The original table accessor reads vhea, the entire vmtx compact tail, VORG, VVAR
and gvar when present. The returned bytes must match the original complete font,
not a product parser's reconstructed data. Actual ordinary/sideways design
metrics and horizontal/vertical design advances are queried separately. Only
the integral source inventory has fixed numeric assertions.

For each configuration the companion performs original `GetGlyphRunOutline`
extraction and checks its envelope against the literal independent rectangles.
It then compares all original DrawGlyphRun bytes with independently constructed
geometry and the native prepared geometry rasterized by the original target.
Null-advance cases include an additional original draw using advances returned
by the original SDK query. Raw BOOL values 1/-1, source logical order, offsets,
original face identity and the absence of an advance array remain observable.

Successful original calls and complete structural validation precede numeric
comparisons. Metric values, outline bounds and full frames contribute to the
existing final failure total, so the first source difference no longer hides
later fonts or instances. Every failed comparison still rejects the process;
API failures and invalid inventories remain immediate failures.

The compact CFF2 family with VVAR origin mapping exposes an additional source
difference. Across the four nondefault weights, original metrics keep origins
700/600 for the two ink glyphs while applying the varied vertical advances.
Both original metric orientations and original run outlines establish that fixed
source origin. The DirectWrite adapter and its independent literals now retain
VORG while continuing to apply the observed varied advances. The generic VVAR
reader and raw delta assertions are unchanged, as are the complete original font
bytes, axes, configurations and exact full-frame comparisons.

## Unresolved numeric observations are not fabricated oracles

The optional fixture flag `vvar_precedence_discriminator` intentionally preserves
gvar but changes the VVAR adjustments. It is allowed only on the explicit mapped
TrueType VVAR family. Its positive VVAR advance rows are 96/160/96, top-bearing
rows 40/44/-28 and bottom-bearing rows 56/68/60; the negative region is minus half.
That table disagreement is a raw reader/SDK observation input, not part of the
80 positive source configurations. `expected_vertical_glyph` rejects the flag,
preventing an accidental assertion that an unknown original precedence rule
matches the product's chosen branch.

The separate focused CPU observer also owns the exact binary32 coordinate
650.125 on compact mapped TrueType and compact VORG/origin-mapped CFF2. It records
original metric/outline results instead of rounding them into this literal
inventory. No whole-font
or per-pixel CPU fallback, metric clamp, observation filtering or tolerance is
introduced here.

Relevant primary boundaries remain the public
[VVAR format](https://learn.microsoft.com/en-us/typography/opentype/spec/vvar),
[gvar format](https://learn.microsoft.com/en-us/typography/opentype/spec/gvar),
[Face5 axis values](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nf-dwrite_3-idwritefontface5-getfontaxisvalues), and
[original glyph outline API](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline).
No foreign engine implementation supplies the bytes, literal geometry or metric
policy.

Validation on 2026-10-09: all49 local macOS ARM64 native CTests pass. The original
Windows ARM64/MSVC software-adapter run completes1121 strict comparisons with128
remaining full-byte failures, down from204. All CFF control-point, source-origin,
variable bearing/origin and run-envelope comparisons pass. Twelve additional
original-origin assertions retain both metric orientations. This is source
placement evidence; the remaining pixel failures still reject qualification.
