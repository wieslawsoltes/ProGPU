# Original-owned CFF prepared glyphs

This implementation extends the explicit original-font prepared-outline route;
it does not select that route automatically or qualify DirectWrite raster parity.
The integrated controls now execute against the original Windows APIs and both
native providers. Full original grayscale pixels and whole producer/package
qualification remain separate gates.

The owned DICT real parser also corrects a bounded decimal conversion defect:
negative scales through22 divide by an exactly constructed integer power of ten,
instead of multiplying an already-rounded reciprocal. Positive powers through22
use the same exact integer sequence. This retains the existing digit grammar,
significand accumulation, signed zero, overflow rejection and longer-exponent
policy; it does not claim fully correctly rounded arbitrary-decimal conversion.
Literal controls require exact binary results for1/2048,1/1000 and the other
authored matrix values, with no matrix tolerance.

## Source and outline ownership

One retained original CFF face (source type CFF or OpenType collection) selects
exactly one CFF1/CFF2 table. Conflicting TrueType/CFF families, malformed competing
table ranges, simulations and multiple original files fail before publication.
The selected file bytes, original identity, glyph IDs and complete captured user
axis values remain owned by the existing source capture. Preparation does not
call source outline callbacks or infer default variable coordinates.

CFF1 Type2 decoding uses the existing ProGPU interpreter. A source-only preflight
retains the top and every FD matrix once. Selected FD coordinates map through the
top matrix to em units, then into head design units. An omitted top matrix uses
the CFF default; an omitted FD matrix retains the top frame. Unsupported stroked,
synthetic, PostScript and non-Type2 forms fail explicitly rather than dropping
their semantics. Nonfinite matrix results fail; no matrix inverse, epsilon or
geometry flattening is used. The additive decoder overload applies the retained
double matrix before publishing float line/cubic points, including closing edges.
Old raw CFF decoder signatures remain untransformed.

Original DirectWrite observations require an additional source translation rule.
For each original Top/FD translation component, the matched conversion is
`float(abs(float(value)) * 65536 + 0.5)`. An integer result other than zero emits
no source contours. The intermediate addition rounds in binary32: the immediate
float predecessor of `2^-17` still becomes one, while the next predecessor becomes
zero. Both signs and both axes are checked against original output; this is not
an epsilon chosen from prepared geometry. This arithmetic describes the observed
boundary, without asserting access to DirectWrite's implementation.

Top translation suppresses the complete face. The first FD also gates the whole
face, even when a glyph from a later FD is queried first. A later FD translation
suppresses its own glyphs. Omitted FD matrices inherit the Top frame; opposite
Top/FD translations do not cancel these source decisions. Converted-zero
translations do not shift emitted outlines. The complete composed matrix and
original file bytes remain retained, while generic CFF matrix decoding and its
independent mathematical contour controls remain unchanged.

Suppressed contours still undergo complete Type2 decoding, segment-budget and
metric validation before publication. Temporary segment storage is released,
and real advances, empty occurrences, source identity and cache ownership remain
intact. Malformed glyphs never become successful empty output. This cold O(S)
validation uses the existing decoder; warm ownership and replay remain unchanged.

The private prepared-original-font adapter is shared by both native providers.
Managed generic CFF decoding does not consume this DirectWrite source adapter,
so its mathematical matrix contract is unchanged. No wire or public COM slot
changes. This correction does not qualify other DirectWrite matrix numeric
decisions, implicit matrices at another UPM, arbitrary affine precision or source
application selection.

CFF2 keeps the established restricted reciprocal-UPM FontMatrix and existing
Type2 blend/vsindex/FD-local-subroutine machinery. Source admission requires the
parsed matrix to equal the reciprocal of its original head UPM, without inheriting
the raw reader's approximate check. Complete original float coordinates are
normalized once through the existing fvar/avar1 path. Unsupported avar versions
remain rejected. CFF2 never reads glyf phantom points or gvar.

Both families use hmtx advances in original design units. CFF2 uses HVAR when
present; absent HVAR means fixed hmtx advances, not fabricated variation. Its
CharString origin remains zero: an hmtx bearing does not translate CFF contours.
Original supplied advances (including zero/negative values), null-advance identity,
em size, offsets, target transform and source paint remain unchanged. Cubic control
points now follow the same final source placement arithmetic as line/quadratic
points. Whole-run placement must succeed before either outline-cache additions or
the output owner is published; the existing segment/scratch budgets remain.

## Primary contracts and qualification

The implementation reuses only ProGPU's own CFF/Type2/variation readers. Format
contracts come from [OpenType CFF](https://learn.microsoft.com/en-us/typography/opentype/spec/cff),
[Adobe CFF 5176](https://adobe-type-tools.github.io/font-tech-notes/pdfs/5176.CFF.pdf),
[Adobe PostScript Language Reference](https://www.adobe.com/jp/print/postscript/pdfs/PLRM.pdf)
(FD-to-top matrix composition, pp. 374–375),
[OpenType CFF2](https://learn.microsoft.com/en-us/typography/opentype/spec/cff2), and
[HVAR](https://learn.microsoft.com/en-us/typography/opentype/spec/hvar).
General PostScript matrix algebra is not evidence that original DirectWrite
accepts or renders each matrix family identically. Independent authored OpenType
fonts, actual Windows original outlines/metrics and full-byte provider comparisons
are required final gates. Those controls must distinguish top/FD order, inherited
FD frames, cubic handles, fixed versus varied CFF2 advances and original axes.
The original implementation checkpoint had no runtime qualification. The
integration now exercises those controls, while ordinary source/UI admission
and full exact-tip CI remain pending.

## Integrated controls

The shared provider fixture selects seven independently authored font families:
default/affine CFF1, two-FD CID CFF1 with explicit/inherited matrices, static CFF2,
and varying CFF2 outlines with fixed or varying HVAR advances. Eleven font
instances exercise explicit and null advances: 22 source configurations, each
with cold, warm and separately compiled independent line/cubic geometry images.
Both native providers keep exact full-frame bytes, the actual source geometry
draw count (one for emitted ink, zero for the original translated no-ink cases),
actual scene command counts, one submission, original ink admission and untouched
channel/alpha/background assertions. Source files and axis-query producers are mutated
after capture; retained replay must not call them again.

Raw controls retain literal contour/advance tables, source collection offsets,
top/FD noncommuting order, static SDK descriptors beside real fvar axes, old raw
decoder coordinates, transformed-output tails and unsupported dictionary forms.
Fourteen source-family/axis/table faults preserve the earlier prepared owner.
A later malformed Type2 glyph must preserve an earlier valid output and its one
cached glyph without publishing the intervening uncached empty glyph. Distinct
owners with identical bytes cannot consume one another's request.

Original Windows fixture execution is a separate mandatory gate. The controls
do not by themselves qualify DirectWrite grayscale pixels or applications.
The seven original font byte arrays are unchanged by the translation correction.
An additional 235 original matrix controls check signed adjacent-float boundaries,
Top/first/later/inherited FD scope and cancellation. Original outlines and aliased
pixels are observed in glyph order 2,0,1,2, retaining one face throughout. Native
controls independently check exact contours and pen movement with supplied signed
and absent advances, including empty glyphs between visible occurrences.

For the translation correction, all 235 original Windows ARM64 controls (940
outline/pixel observations) pass, and all 22 original CFF configurations execute.
Six original CFF grayscale comparisons still differ. The next original CFF
vertical-origin literal contour check also remains failed. All 49 local native
tests, including stock Metal and the exact Dawn/WebScene provider corpus, pass
in 24.06 seconds. The seven original input font arrays compare byte for byte with
the parent. This is evidence for the translation correction, not whole CFF,
Windows x64, package or application qualification.
