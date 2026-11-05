# Binary font scan metadata — implementation batch

The prepared font-batch path retains binary scan metadata independently of
antialiased vector geometry. Both renderers consume one shared GPU implementation.
The original authored-font comparisons pass on local macOS ARM64; system-font,
source application and complete package qualification remain open. Existing
source consumers remain unchanged until that qualification.

`NativeHintedCoverage.Monochrome` applies only to captured, already-positioned
TrueType font batches. The original contour writer produces the same physical
lines and quadratics. Cubics, unsupported flags, malformed topology and unknown
policies reject before publication. Existing coverage modes and record layouts
remain unchanged. Old producers reject the new mode.

Every emitted segment carries its actual contour's descriptor:

- `Pad0`: marker bit 8, raw resolved SCANTYPE in bits 0–2, original contour
  override marker in bit 9, and the original admitted outline flags in bits 16–31.
- `Pad1`: contour start relative to this outline's segment range.
- `Pad2`: the complete contour segment count.

Raw SCANTYPE aliases are retained. A marked contour start replaces the current
mode; singleton contours still update that state without manufacturing a segment.
The original batch remains owned, including no-ink descriptors and raw tags.
Generic paths continue to reject nonzero reserved words. Managed import validates
the complete contiguous contour partition, closure, uniform descriptor, original
flags and source bounds. Copies preserve caller tails and atomic rejection.

Implementation provenance: the existing ProGPU contour writer in
`progpu_native_hinted_outline.cpp`; public
[OpenType scan-conversion rules](https://learn.microsoft.com/en-us/typography/opentype/spec/tt_instructions#scantype)
and [FreeType outline metadata](https://freetype.org/freetype2/docs/reference/ft2-outline_processing.html).
No foreign renderer implementation was used.

This uses the existing [cross-engine font architecture research](native-font-hinting.md)
and [retained presentation-policy research](source-grayscale-text-contrast.md).
Adopted: immutable prepared layout, explicit raster identity, lazy device pipelines,
batched uploads and owned submission retirement. Adapted: the original ProGPU atlas
and native raster page carry the additional scan buffer. Rejected: CPU bitmap
fallback, source reshaping, per-glyph interop calls and cross-API mode aliases.
The algorithm comes from public scan contracts and independently observed original
outputs; it does not copy another rasterizer's source or implementation structure.

`BinaryGlyphRasterizer.wgsl` uses one 256-thread workgroup per scanline and two
ordered dispatches. It sorts at most 512 crossings from 256 original line/quadratic
segments, accumulates winding, and writes packed binary R8 coverage. The second
pass reads a separate completed horizontal buffer, avoiding races between dropout
decisions and their output. Pixel boundaries, raw mode aliases and the bounded
bitmap extent are explicit. Vertical gap admission preserves the original bitmap
row direction in the contour's Y-up frame. Continuation counts distinct square
boundary contacts, retains actual orthogonal caps and exact cap contacts, and
keeps with-stubs contour-vertex contacts separate from positive winding intervals.
Quadratic endpoints use their first nonzero one-sided coefficient; an interior
curve extremum does not become an endpoint. Exact nondegenerate quadratic
boundaries retain closed coverage. Exact straight boundaries adjoining positive
winding retain their original closed coverage; negative-winding interiors remain
filled while their exact straight boundaries stay open. Flat quadratics keep
their independent rule. At a flat straight edge, exact endpoint transfers recover
the winding below the scan from the existing lower-inclusive winding; no shifted
scan, source-coordinate change or contour-area proxy participates.
No epsilon, source-coordinate adjustment or AA threshold substitutes for a binary
decision. The independent original system-font comparisons remain required.

Smart curved dropouts resolve only the two selected original roots, using at
most 32 de Casteljau subdivisions each in increasing scan-axis direction. The
one-26.6-unit control-hull bound selects the lower-parameter endpoint's unit;
exact endpoint and dyadic roots retain their coordinates. Their midpoint uses
the nearest 26.6 unit with lower-unit half ties. Line-only intervals retain their
original arithmetic. Analytic winding, gap detection and source coordinates do
not change. Public [Microsoft scan-conversion algorithm prose](https://patents.google.com/patent/US6529197B1/en)
describes selective subdivision for curved dropout transitions. The precise
arithmetic here is an original implementation checked against the retained
observations; that source does not by itself establish broad parity.

Both renderers use the same shader and preserve actual submission ownership.
Managed work uses the existing uniform/output rings, including oversized glyphs;
native work retains both buffers and bindings in its existing raster lease. The
extra buffer participates in native memory inventory, staging metrics and semantic
resource budgeting. Managed `GlyphAtlas.CoverageStagingBytes` reports both allocated
coverage rings after binary demand; ordinary demand keeps its original reservation
and warm binary replay reuses both rings. This allocated-reservation metric remains
separate from native per-operation staging bytes. Pipelines remain lazy; binary-only
demand does not compile the ordinary vector rasterizer. Immutable source bytes
participate in raster identity.
Shared command admission requires aliased integer-origin replay and the complete
bounded outline inventory; the inventory maximum is retained once per generation,
including selected views. Unsupported execution paths reject instead of selecting
AA or a CPU bitmap substitute.

The checked-in fixture contains all 880 original Windows observations of two
ProGPU-authored fonts: five scan modes, eight outline families, eleven phases and
two gasp tables. Both original GDI+ and GGO_BITMAP outputs agreed, and all 1,760
captured hinted/unhinted GDI contours matched the authored source. The fixture
digest is `6d13e632b5f260217bc17d9c86b61e77506f463644eeee923280400a52f3b092`.
The owned font inputs are 20,952 bytes each; the tests verify their original
SHA-256 values before execution. No system-font binary or foreign renderer code
is included.

The additional `BinaryGlyphCurveOriginals.json` fixture retains all 880 cases
from eight independently authored curved ribbons, including transposed pairs,
five modes, eleven phases and both gasp tables. Original run `39156744403`
captured 2,640 full frames in 3.899 seconds without input or desktop windows.
All 1,760 hinted/unhinted outlines matched the authored geometry and all 880
GDI+/GGO_BITMAP pairs agreed. The diagnostic PR was closed without merging.
The fixture digest is
`b30470feae7d095adad5e549595adeaed929058e3040d94640f0ca214313e2b8`;
its two original authored fonts are 23,812 bytes each. Both fixture sets run
through the same managed and native checks, preserving the earlier inventory,
full frames, padding, cold/warm reuse and original font-digest validation.

The normal Windows system-font reference lane also retains a small fitted versus
unfitted source control using the same owned straight-contour font. Glyphs 93–95
straddle its exact right-edge boundary. Each runs with GDI+ GridFit and unfitted
monochrome at pixel em sizes `32 - 1/256`, `32` and `32 + 1/256`, with the original
integer origin and complete 64-by-64 binary frames. The three size-32 GridFit
frames must still equal the original fixture exactly. The other 15 captures are
observations, not new expected pixels or permission to alias the two policies.
The source process admits the exact font/fixture hashes and checks a ten-second
capture deadline. This adds no workflow, desktop window or input operation.

This distinction matters for the separate system-font unhinted candidate in
[diagnostic PR #428](https://github.com/wieslawsoltes/ProGPU/pull/428). Both providers
produced identical output and exact ordered GGO unhinted geometry for all 64
descriptors, but seven descriptors differed from the original pixels. Matching
GGO's captured 26.6 outlines does not establish the internal precision or coverage
policy of GDI+'s unfitted renderer. The candidate remains unqualified. Preserve
the original failures and the independent GridFit fixtures while attributing the
difference; making all straight boundaries inclusive would contradict existing
original GridFit observations.

The additional source scan-mode observation keeps those 18 captures and the same
ten-second deadline. It pairs fitted and unfitted output for the original five
scan modes and eight outline families at phase zero and 32 pixels per em: 80
additional full frames, with all 40 fitted frames checked against the immutable
original fixture. Unfitted frames remain observations, without expected-pixel
replacement. Both matrices use the same hash-verified owned font, private family,
native drawing call and failure-preserving cleanup. The normal Windows x64 and
ARM64 system-font jobs execute this probe; no new workflow or desktop input is
needed. Both jobs passed at `f65f3c646` in
[Build 41932551078](https://github.com/wieslawsoltes/ProGPU/actions/runs/41932551078).
Independent review verifies all 196 complete RGBA frames, all 86 immutable fitted
comparisons and equality of all 98 x64/ARM64 pairs. The source receipts, including
retirement, take 0.575 seconds on x64 and 0.462 seconds on ARM64.

Every same-glyph fitted/unfitted pair agrees. In particular, diagonal dropout
retains five distinct frames for the five authored SCANTYPE modes, even when
unfitted. This is an observation about the original owned font, not permission
to equate all [GDI+ text rendering modes](https://learn.microsoft.com/en-us/windows/desktop/api/gdiplusenums/ne-gdiplusenums-textrenderinghint)
with a [FreeType outline-load flag](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html).

A separate local probe compiles the actual current native capture sources against
the pinned owned FreeType. Its 320 captures cover all 40 source glyphs, four load
targets and both interpreters. Points, contour ends and curve topology agree in
this owned font. Normal, Monochrome and HorizontalLcd retain every authored scan
mode; Unhinted yields flags zero and no contour overrides, losing the selected
mode in 32 of 40 glyphs for each interpreter. This establishes a metadata gap for
these inputs. It does not attribute the seven original system-font failures or
permit changing Unhinted's existing instruction-free contract.

The [source scan witness replay](../eng/probes/UnfittedScanWitness/README.md)
tests that explanation against the original 64 descriptors and full images. A
separate same-source/device Monochrome capture supplies only scan/dropout metadata
after complete topology checks. Original unhinted geometry, precision flags and
expected images remain unchanged. Both original provider processes keep their
45-second limits; source and witness arrays are retained independently. The
existing Windows reference lane used the exact previously qualified producer
package from the failed experiment, isolating this change from new product code.
The replay at `490367550` in
[Build 41939650876](https://github.com/wieslawsoltes/ProGPU/actions/runs/41939650876)
completed both providers and failed their original pixel checks. Each captured
all 128 full frames in about 6.2 seconds, retained exact ordered geometry for all
64 descriptors, preserved every non-scan segment bit and retired its private
fonts and owned processes. All 128 provider pairs and cold/warm images agree.

Compared with the unchanged original experiment, only six frames changed: one
pixel in each cold/warm image for Microsoft Sans Serif source slots 1, 5 and 11.
Those three descriptors now match their original images. No previously matching
pixel changed. Segoe UI slots 2, 24, 27 and 30 still differ: eight frames and
36 pixels per provider, versus the original fourteen frames and 42 pixels.
Slots 2 and 30 share glyph 80; the other remaining glyphs are 90 and 41.

This attributes those three Microsoft Sans Serif differences to scan metadata
in this isolated replay. It is not a complete raster fix or source/application
qualification. The remaining Segoe UI pixels require independent precision and
scan-boundary evidence; exact GGO 26.6 geometry does not establish GDI+'s internal
unfitted representation. Preserve both failures, all original expected images
and the independent fitted fixtures. No generic boundary, font-load policy,
shader or acceptance threshold changed. Artifact `13375063886` has SHA-256
`02204e87d090266c2190b7e553128e7571731141344f92daa38d7e5cae32cd8e`.
The completed replay is retired from routine CI, removing its expiring historical
artifact dependency. Its source, inputs and both failed results remain retained;
this does not turn the remaining original pixel failures into passing acceptance.

An additional owned-source direction observation pairs the original font with a
separate derivative. Only seven four-point right-edge contours reverse point
order; their original coordinates, metrics and scan instructions remain intact.
The derivative has a distinct family name and SHA-256
`872c5a82825c5e1aa3e51589d758680b36a7e005b8fa982efd21d19b239fb91d`.
Its deterministic generator follows the public
[OpenType glyph format](https://learn.microsoft.com/en-us/typography/opentype/spec/glyf).
Actual native capture independently verifies both complete 441-glyph inventories
under four load targets and both interpreters: 7,056 captures preserve all
metadata, all 3,472 unaffected captures per font and the exact reversed point
order in 56 affected comparisons. This is font-structure validation, not pixels.

The original 98-frame source control retains its unchanged expectations and
ten-second bound. A separate ten-second, 28-frame observation pairs both contour
directions with fitted/unfitted rendering over five scan modes and the three
original mode-one phases. All seven original fitted frames must still match the
immutable fixture. Reversed frames remain observations, with complete pixels,
owned private-family retirement and no input or desktop windows. Both architectures
passed in [Build 41943979905](https://github.com/wieslawsoltes/ProGPU/actions/runs/41943979905).
Independent review verifies all 252 full frames, all 126 cross-architecture pairs
and all 100 immutable original comparisons. Each of the five phase-zero reversed
contours adds the nine exact right-edge samples; the adjacent phases are unchanged.
Every fitted/unfitted pair agrees. Both direction observations complete in about
0.021 seconds, including owned-family retirement.

`BinaryGlyphDirectionOriginals.json` retains these seven original reversed-font
frames independently of the earlier 1,760-case fixtures. Its SHA-256 is
`aebb5e4955a0003808f639601188c9151315ecc98ead3929f366513cd2a643fd`.
The unchanged renderer fails five of these cases and 45 pixels while both earlier
880-case fixtures pass. The common shader now admits the exact downward vertical
line crossing. Its upward counterpart stays open; no slope, flat boundary,
quadratic, dropout arithmetic, sorted event or source coordinate changes.
All 37 focused managed controls pass after the correction, including all three
original pixel inventories and cold/warm reuse. The native package consumer also
retains the separate seven-case inventory through original segments and both
owned font interpreters, without reducing either earlier 880-case inventory.
Both rebuilt native providers pass all nine local executions: the three complete
fixtures through original segments and both font interpreters, with unchanged
cold/warm submissions, counters, full-frame padding and source lifetime checks.
The exact loaded library hashes are retained with the results; wgpu completes in
1.29 seconds and Dawn in 1.67 seconds. Windows and package qualification remain
required.

This proves and corrects the observed owned vertical-boundary defect. It does not
by itself resolve the four remaining Segoe UI descriptors or qualify a GDI+
unfitted source adapter. Their original full frames and metadata replay failure
remain separate gates; do not infer a sloped-edge or unfitted precision policy
from these rectangles.

A local replay of the exact retained 64-case scan-applied geometry then tests
the correction against every original full image. It retains the original render
loop, 45-second/16-MiB bound, descriptor order, cache counters and cold/warm frames;
it does not repeat source capture or replace Windows acceptance. Both rebuilt
providers produce identical 128-frame inventories. Only the two glyph-80
descriptors change: all 32 differing pixels across their four cold/warm frames
now match. No other pixel changes. Source slots 24 (`w`) and 27 (`F`) still fail,
each by one pixel per frame, leaving four differing pixels per provider.

The remaining observations need broader independent direction and stroke-end
coverage. A separate owned derivative reverses all 440 original contours across
the eight original families, five modes and eleven phases. It preserves every
coordinate, on/off-curve tag, instruction and metric; the original font and the
seven-contour derivative stay immutable. Its SHA-256 is
`6aec3fab6c6e9265541cb5bb9874b754bb2537e380187e4d35edafbb56db5bd4`.
Actual native captures verify all 3,520 reversed point/tag inventories across
four load targets and both interpreters, with unchanged metadata and eight
unchanged notdef captures. The Windows observer is compiled and retains a
separate ten-second bound for 1,760 full frames, including all 440 original
fitted comparisons. Both smaller source observations keep their original inputs,
counts and deadlines. Both Windows observations pass in
[Build 41946818514](https://github.com/wieslawsoltes/ProGPU/actions/runs/41946818514).
Independent review verifies every one of the 3,520 full frames, all 1,760
architecture pairs and all 880 immutable original comparisons. Every fitted and
unfitted pair agrees; both source processes retire their owned families in about
1.1 seconds. The smaller 98/28-frame controls also retain their original matches,
deadlines and cleanup. This adds no workflow, desktop window or input action.

The complete `BinaryGlyphAllDirectionsOriginals.json` fixture retains all 440
reversed originals, with SHA-256
`2b9ab3fabed7d7437cd181a9b93648725d9189214ce76f4eb79809e8068adbbb`.
The vertical-only correction still fails 43 of these cases and 90 pixels:
horizontal boundaries, diagonal crossings and triangular contours require the
same directed treatment. The common shader now uses the actual signed winding
on either side of an exact straight crossing. Flat line contacts use the original
winding above and exact start/end transfers for the limit below. Existing interior
roots, event order, original coordinates, flat-curve calculations and all dropout
arithmetic remain unchanged. This is not an epsilon, rounded scan or whole-contour
area classification.

All 38 focused managed controls pass, including every unchanged older case. Both
rebuilt native providers pass all 12 full-frame fixture executions with original
segments and both font interpreters, including complete blank padding, cold/warm
cache counters and source lifetime. A further complete retained 64-case replay
changes only the two cold/warm images for source slot 24 (`w`), one pixel each,
both to the original expectations. All provider pairs agree and no other pixel
changes. Only slot 27 (`F`) remains: one pixel per cold/warm frame per provider.
Its stroke-end/dropout behavior, fresh Windows source execution, final packages
and actual application qualification remain open.

A separate flat-cap observation varies eleven finite endpoint coordinates in two
authored strip frames, under all five scan modes and both contour directions.
One frame retains the original half-pixel strip thickness; the other uses an
independently declared fractional strip. Coordinates and sidebearings belong to
these new test fonts, not modified system-font inputs. Advance widths and original
scan instructions stay unchanged. The first preparation incorrectly retained an
old sidebearing after moving the new strip's left bound; native capture exposed
the resulting phantom-point translation before any source execution. That failed
preparation is retained. Corrected authored sidebearings match their declared
left bounds, and all 1,760 selected native point inventories now match exactly;
all 5,296 unselected inventories and all scan metadata are unchanged across both
derivatives and four load targets/two interpreters.

The corrected forward font SHA-256 is
`02ec6a8655adc7766325f8178a3433dd9b640b5ac61fa01bcaaa8ee3d5027ab7`;
the reversed font SHA-256 is
`d21da6a20855aecc2e329aebcf41554f71f14bd6311d4968143ea410cc006be6`.
The source observer retains 660 full frames under a separate ten-second bound,
including 110 mandatory unchanged original fitted comparisons. Earlier 98, 28
and 1,760-frame observations retain their original expectations, schemas and
limits. Build [41949972992](https://github.com/wieslawsoltes/ProGPU/actions/runs/41949972992)
completed both source observations in 0.232 and 0.377 seconds. All 660 x64/ARM64
frame pairs and all fitted/unfitted pairs agree. All 1,886 earlier frames per
architecture remain byte-identical. Artifact 13380622019 has SHA-256
`73dcdb927cf7b8f9e19ef33720d6ea1d6dc1ef732cd0428edcef9682fc602c2a`;
artifact 13380383561 has SHA-256
`45585dbb4c6b0b50933a182adc683fd5a919d992622ad6a6ee66d321943a4bd2`.

The two new immutable fixture files retain all 220 original forward/reversed
frames. Before the correction, 16 reversed cases lose one exact endpoint pixel
each; every previous fixture and all 110 forward cases pass. The missing pixels
are closed, positive-winding flat boundaries at upper scan endpoints. Ordinary
lower-inclusive crossings correctly have no interval there. The shared shader
now evaluates those exact straight endpoints separately, using the existing
signed endpoint transfer and original scan modes. An existing nonzero interval
stays on its original path. Axis transposition reverses only the winding test;
no perturbed level, root replacement, contour-area approximation or new bitmap
expectation is involved. Stub-excluding modes still require two distinct inward
continuation contacts. Original event slots, parameters, sorting, lane traversal,
vertex-dropout calls, buffers and barriers remain unchanged. The additional
work is bounded by O(C*S), with at most S upper flat caps across both directions.

All 40 focused managed controls pass, including all 2,427 original cases and
cold/warm cache checks. A separate trial shortened the existing finite-cap
allowance; it broke original forward and reversed source images and was discarded.
Its failure is retained. Both rebuilt native providers pass all 18 executions
(original segments and actual font captures under interpreters 35/40), including
full pixels, source retirement and cold/warm cache counters. All 128 retained
system-font frames per provider remain byte-identical to the previous run, so
the separate `F` mismatch is unchanged. These isolated strip observations do not explain the
system-font `F` mismatch: their complete glyph bounds differ from a connected
stem. Fresh Windows/provider/package qualification remains required.

A further independent observation adds a declared one-pixel connected stem below
the same eleven fractional upper strips, in all five modes and both directions.
The stem reaches design Y=64; it changes the actual whole-glyph extent without
moving the upper strip. These are newly authored six-point outlines, not altered
system-font images. The original font, isolated strips, advance widths and scan
instructions remain byte-identical. All 880 selected native captures match their
exact declared points across four load targets and both interpreters; all 6,176
unselected captures remain unchanged. The forward font SHA-256 is
`947028b7af7c585f3367fb9702b834965eba7b163fec8b3272e4fce96a5e339c`;
the reversed font SHA-256 is
`ef4f3997a72798d035f4d729f0757c1449746acd65a3a379aace2054eb93841e`.
The existing windowless observer is compiled for 330 additional full frames,
including 55 unchanged original fitted comparisons, under its separate ten-second
bound. All earlier observations retain their own captures and deadlines. Source
execution is pending; this preparation does not resolve the original `F` pixel.


The preceding 18-frame probe at `b5865332` completed on both Windows architectures
in [Build 41916411244](https://github.com/wieslawsoltes/ProGPU/actions/runs/41916411244).
All 18 paired full frames agree across architectures, all three original fitted
frames per architecture match, and each fitted/unfitted pair is identical. This
small right-edge observation does not select the source's unfitted dropout or
precision policy. The broader mode inventory separates those questions from
the already qualified fitted boundary rule; the seven system-font descriptor
failures remain unresolved.

Both captured JSON files are byte-preserving Git assets. The first Windows
package runs at `dcaac6c7` rejected their unchanged SHA-256 checks before rendering:
the default text checkout changed the final LF into CRLF. An isolated checkout
with `core.autocrlf=true` reproduces both altered digests; explicit `-text`
attributes retain each original blob byte and its original digest. No fixture,
font, expected pixel, digest assertion or renderer changes in this repair. The
failed runs remain retained; complete corrected-head CI is still required.

The subsequent Windows wgpu package runs exposed an FXC compiler failure: the
two-root loop wrote dynamically indexed vector components. The pinned Naga
translation reports that exact write at HLSL line 163, followed by forced loop
unrolling errors in the enclosing contour/dropout paths. Each quadratic root now
uses the same scalar helper, in the original order, before constructing the
two-lane result. Derivative arithmetic, endpoint admission, rejected-root
sentinels, stable root calculation and every scan/dropout operation are unchanged.
The helper changes no wire layout or dispatch ordering.

Local validation passes all 36 managed binary/shader controls and all six original
880-case executions through each rebuilt native provider, including unchanged
cold/warm pixels and ownership assertions. A first fixed-component-write
candidate did not return from FXC within its diagnostic's remaining 42 seconds;
that failed attempt remains retained and is not compiler qualification. The
scalar-helper candidate then exceeded independent 60-second compiler-only bounds
for both entry points, without a returned HRESULT or bytecode. Those failures
remain retained; compiler optimization behavior and complete package CI are
still unresolved. The local pixel passes do not qualify Windows compilation.
Original consumer progress now distinguishes render from submission wait, and
Windows failure receipts retain the full native exit code before shell narrowing;
these observations change no inputs, assertions, workload sequence or deadlines.

The owned Naga workgroup-initialization repair subsequently resolves that FXC
optimizer stall; see `native-windows-shader-compiler.md`. At `686a34bb`, all x64
package lanes pass, while ARM64 FXC, DXC and Dawn reach the first original frame
and fail during submission wait. A bounded original DXC capture retains a preceding
write access violation in WARP-generated code: `mov w28,#0x31a4; add x28,xzr,x28;
str w4,[x28]`. The shifted-register ADD uses the zero register rather than SP,
exactly constructing the captured invalid address. The later return-address
failure is in `PixelJITProcessor::ExecuteJIT`. The capture reached its eight-dump
cap; it remains incomplete diagnostic evidence, not a successful product gate.

Each entrypoint now visits its two original dropout indices through one bounded
loop: start at the invocation's lane, add 256, stop below 512. For every lane
0–255 this preserves the exact ordered pair, helper arithmetic, early returns,
shared-memory contents, atomics and subsequent vertex dropout. Both renderers and
both native providers consume this same shader. No compiler flag, adapter default,
native dependency, resource layout, dispatch count or pixel expectation changes.

Matched native ARM64 release compilations retain the loop. Horizontal/vertical
FXC bytecode falls from 81,296/64,088 to 50,484/37,524 bytes; DXC falls from
28,900/23,528 to 19,776/15,728 bytes and its local allocations fall from 16 to 8.
FXC temporary-register counts are mixed, so this is not a uniform register-pressure
or runtime-speed claim. All eight compiler cases pass. Local validation passes
36 managed binary/shader controls and six complete 880-case comparisons through
each rebuilt native provider with zero differing pixels. Actual ARM64 WARP
execution and whole package qualification remain required before calling this a
runtime fix or advancing consumers.

That loop revision, `b7c191f2`, passes 75 of 76 whole-head checks. Its default
FXC and Dawn ARM64 package lanes pass; explicit DXC still fails. The independent
bounded six-group runtime checks also pass on FXC in 44.12 seconds and Dawn in
59.07 seconds. DXC fails at its first frame in 2.46 seconds. Its complete native
exception capture records a smaller `0x2060` JIT frame but the same invalid
address construction: `mov w28,#0x2054; add x28,xzr,x28; str w4,[x28]`.
The actual write target is `0x2054`; the final fail-fast subtype is `0x49`.

The remaining eight DXC local arrays hold two-component root vectors addressed
by dynamic indices. The shader now selects the original scalar components
directly: the continuation loop visits only indices 0 and 1, and smart-curve
selection retains the original order modulo 2. Both roots are already fully
initialized immutable values. This changes neither arithmetic nor component
values, the three-edge visit order, early returns, sample positions, workgroup
storage or dispatches. It does not interpolate between components or change
compiler defaults.

The scalar-selection revision passes the same 36 managed controls and all six
880-case comparisons on each rebuilt local native provider, with zero differing
pixels. All four native ARM64 compiler cases pass with the exact retained compiler
identities and original flags: DXC's eight local arrays become zero in each entry
point. FXC's loop counts remain 7/5 and its temporary-register counts increase
from 26/24 to 27/25, so this is not a uniform register-pressure claim. Actual
ARM64 runtime execution must still establish whether it removes the remaining
generated-code failure; compilation and local pixel agreement do not qualify
the source or package.

The scalar-selection source at `1f81f86e6` subsequently passes all six original
ARM64 groups on default FXC in 40.49 seconds and Dawn in 52.90 seconds. Explicit
DXC still fails during the first frame wait after 3.10 seconds with exit code
`0xC0000409`, before completing a comparison group. Removing DXC's local arrays
therefore does not establish a runtime repair. The exact generated header, two
native DLLs, original package closure and failure receipt remain retained; a new
bounded capture independently records a first invalid store to `0x2074` in an
`0x2080` JIT frame. Five events complete in 6.53 seconds without exhausting the
eight-event cap. The observed shifted-register ADD uses XZR, not SP; removing
DXIL arrays did not repair the generated address.

The next source revision shares the two original smart-curve endpoint evaluations
through one bounded loop. Existing admission proves `index < end < 512`; stepping
by `end - index` visits exactly those two endpoints in order. Each result is assigned
to its own scalar, preserving the single original addition and subsequent midpoint
arithmetic. There is no zero-seeded accumulation, interpolation, reordered edge or
new raster policy. The helper's original 32-step bound, all early returns, barriers,
atomics, shared memory and dispatches are unchanged. Both managed and native
renderers consume this same canonical shader. All 36 managed controls and all six
original 880-case groups in each local native provider still pass with zero pixel
differences. All four original Windows release-flag compiler cases also pass.
Both DXIL entries now retain one 32-step subdivision body inside the endpoint
loop, with five square-root sites instead of six and no local arrays. FXC
temporary counts remain 27/25. These measured compiler changes do not establish
actual Windows runtime safety; the original three runtime routes remain required.

That shared-endpoint revision subsequently passes all six original groups through
DXC in 11.71 seconds and FXC in 50.49 seconds, but Dawn exceeds the unchanged
60-second limit during the sixth group. A fixed same-host A/B/B/A comparison
retains source-coherent native pairs and identical noncandidate modules: the
preceding scalar revision passes in 52.40 and 52.30 seconds, while the shared-loop
revision times out in 60.03 and 60.04 seconds at the same sixth-group frame. These
censored measurements demonstrate a revision-specific Dawn cost on that host;
they do not establish its exact completion time or erase the failed original run.

The next implementation retains the original analytic parameter already computed
for each scan event. The event ordinal is exactly `2 * lane + root`, so `order / 2`
recovers the original segment index, including inactive slots. Sorting only swaps
complete events. Replacing the redundant source word with the actual parameter
preserves the 16-byte event and 10 KiB workgroup inventory. It avoids repeating the
same quadratic solve inside smart subdivision and restores the original two
explicit endpoint calls and their single midpoint addition. Root admission,
subdivision arithmetic, barriers, source coordinates and sample policy remain
unchanged. The ordinal proof covers all 512 slots for every admitted segment
count; it is not a substitute for actual compiler, pixel and runtime validation.
All 36 managed controls and all six original 880-case groups on each rebuilt
local native provider pass with zero pixel differences. The actual generated
header equals the complete canonical shader plus NUL and occurs exactly once in
each library. Windows compiler/runtime and whole-producer qualification remain
pending; local controls do not establish Windows timing or source UI parity.

The rejected global midpoint quantizer regressed earlier diagonal controls and
was removed. Its failed captures remain retained. The bounded curved candidate
passes all original and new cases through native wgpu and Dawn, including actual
font captures under interpreters 35 and 40. A separate diagnostic also compares
all 128 retained system-font contours without pixel differences; this is not the
final original source-application, 72-frame text pair or package qualification.

Local validation on 18 October 2026:

- All 880 complete authored coverage cases match exactly in the managed atlas,
  native wgpu and native Dawn; every cold/warm pixel remains identical.
- Both native providers also pass all 880 cases from actual captured font batches
  under both TrueType interpreters, after disposing the public source owners.
- All 72 focused shader/atlas/integer-origin tests pass. The original native
  contour controls and package-consumer device/font/paragraph ownership controls
  pass with binary preparation included.
- The full native build succeeds for both providers. The first full renderer run
  passes 6,352 tests, retains 12 platform skips, and fails one source-text audit
  that still expects the former no-argument compute-pass signature. The audit now
  requires both the typed selector and its actual call; the failed run is retained.
- After correcting that source audit, the complete renderer passes 6,353 tests
  with the same 12 platform skips and no failures. Generated native contracts and
  the 101-field native memory inventory verification also pass.
- All 230 headless tests pass with zero skips.
- After the curved endpoint/boundary/dropout fix, the full renderer passes 6,354
  tests with the same 12 platform skips; all 230 headless tests still pass. Each
  native provider passes six complete 880-case executions (two original fixture
  sets through raw contours and both font interpreters), with zero differing
  pixels and unchanged cold/warm ownership checks. The managed suite executes
  both original fixture sets with the same atlas and ownership assertions.

The JIT/NativeAOT package scenario groups add both binary provider cases while
preserving every original MIL, text and EDIT scenario. The separate DX12 package
and NativeAOT lanes include the original binary check as well. Whole producer
CI, original system-font pixels, final source wiring, packages and application
acceptance are still required. Authored controls do not establish broad Microsoft
parity or release readiness.
