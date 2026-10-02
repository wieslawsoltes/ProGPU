# Original-owned CFF prepared glyphs

This implementation extends the explicit original-font prepared-outline route;
it does not select that route automatically or qualify DirectWrite raster parity.
The authored product and reference controls have not been executed.

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
No local build, CPU/native/GPU test, VM run or workflow dispatch accompanies this
implementation checkpoint. Ordinary source/UI admission and full exact-tip CI
remain pending.

## Authored controls (not executed)

The shared provider fixture selects seven independently authored font families:
default/affine CFF1, two-FD CID CFF1 with explicit/inherited matrices, static CFF2,
and varying CFF2 outlines with fixed or varying HVAR advances. Eleven font
instances exercise explicit and null advances: 22 source configurations, each
with cold, warm and separately compiled independent line/cubic geometry images.
Both native providers keep exact full-frame bytes, one source geometry draw,
actual scene command counts, one submission, nonempty ink and untouched channel/
alpha/background assertions. Source files and axis-query producers are mutated
after capture; retained replay must not call them again.

Raw controls retain literal contour/advance tables, source collection offsets,
top/FD noncommuting order, static SDK descriptors beside real fvar axes, old raw
decoder coordinates, transformed-output tails and unsupported dictionary forms.
Fourteen source-family/axis/table faults preserve the earlier prepared owner.
A later malformed Type2 glyph must preserve an earlier valid output and its one
cached glyph without publishing the intervening uncached empty glyph. Distinct
owners with identical bytes cannot consume one another's request.

Original Windows fixture execution is a separate mandatory gate. The controls
are authored assertions, not a successful runtime receipt or DirectWrite matrix,
outline, metric, pixel or application qualification.
