# Original font raster recommendations

The original Forms grid still requires source GDI and GDI+ raster behavior.
`TextRenderingHint` state alone does not select the required font execution,
coverage, contrast or system policy. The current generic compositor's
`Auto`/`Fixed`/`Animated` hinting values are placement policies, not those six
source rendering hints.

The independent Microsoft [reference run 39096839921](https://github.com/wieslawsoltes/LibreWinForms/actions/runs/39096839921)
retains original selected-face `gasp` bytes alongside font hashes. Its 72 complete
images and case receipts exactly match the earlier reference. Microsoft Sans
Serif at 13 ppem selects version-zero maximum18/flags1; Segoe UI at 12 ppem
selects version-one maximum19/flags7. Their different grayscale recommendations
correlate with their different `AntiAliasGridFit` pixels; that correlation does
not prove the complete raster algorithm.

`SfntRasterizationRecommendations.TryRead` validates the complete original table
before returning its first inclusive matching range. It retains version, queried
size, original maximum and all four independent flags. Unknown versions, reserved
flags, duplicate/descending ranges, truncated inventories and trailing bytes
reject atomically. A valid empty inventory or a size beyond a finite final range
has no recommendation; an explicit zero flag record is still available. The
[OpenType gasp specification](https://learn.microsoft.com/en-us/typography/opentype/spec/gasp)
recommends a final 65535 sentinel but does not require inventing one for a finite
inventory. No nearest-size selection or threshold rounding occurs.

`TtfFont.TryGetStaticRasterizationRecommendations` queries the actual selected
face's owned bytes and validates raw directory records, including malformed
entries omitted by legacy metadata. Duplicate/out-of-range/malformed gasp tables
throw before publishing output. Missing coverage returns false. Non-TrueType
faces and any raw `fvar` presence remain outside this static query; MVAR-adjusted
instance thresholds require a separate implementation. Ordinary font metrics,
glyph placement and rendering are unchanged. Work is O(T+R), for T table entries
and R ranges, with O(1) auxiliary storage and no allocations or GPU initialization.

The controls use the two original 16-byte tables, inclusive boundaries, every
truncated prefix, corrupt unselected later records, version-specific flags,
zero recommendations, missing coverage, raw duplicate/invalid records, existing
source storage identity and two independent TTC faces. They establish metadata semantics;
they do not qualify glyph rasterization or an application.

The paired native implementation is
`Text/Font/progpu_native_font_raster_recommendations.cpp`, sharing the same
value semantics through the C++ header and `progpu.native.text` module. Its
`sfnt_font_view` borrows the caller's existing immutable source lifetime and
returns values without retaining another owner. Both providers use the common
text library. This adds no C ABI, per-font P/Invoke, rendering flag or font-load
policy. Native failure uses the existing status/error convention; the managed
selected-face wrapper throws for malformed data. Both clear unavailable outputs.
Native and managed controls cover every integral size from 1 through 65535 for
each original table, including unselected corrupt records, raw `fvar` rejection,
missing TrueType outlines and independently selected collection faces.

The range walk keeps a dependent previous maximum and first matching record,
with no parallel raster workload or per-glyph work. Scalar validation is bounded
by the original 16-bit range inventory; no SIMD/GPU dispatch, font cache,
device ownership or frame replay changes are involved. The implementation uses
the original ProGPU reader and specification field contract, with no imported
third-party implementation text. Include-based and module-based native controls
exercise the same test source, and malformed-input controls also run with address
and undefined-behavior sanitizers. Whole hosted producer/package and original
application gates remain independent of these local metadata checks.

## Rendering integration still required

The source renderer must distinguish GDI quality from ordinary GDI+ hints and
retain the actual font/size, system settings, target and contrast. The canonical
Forms source already maps GDI hints to LOGFONT quality; portable drawing currently
does not carry that policy into glyph replay. [GDI font quality](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logfontw)
and [GDI+ antialiasing](https://learn.microsoft.com/en-us/windows/win32/gdiplus/-gdiplus-antialiasing-with-text-use)
are separate contracts. Never infer that Draft/Proof means non-antialiased output.

The native font adapter defaults to `FT_LOAD_TARGET_NORMAL`. Its explicit
private outline-load policy can also select monochrome, horizontal LCD or
unhinted scaled outlines, independently of interpreter35/40. Only the explicit
unhinted policy disables native glyph instructions; ordinary native faults retain
their failure and atomic rollback behavior. Every selection keeps outline-only,
no-auto-hinter loading, original phase/size and full slot capture. No CPU bitmap
rasterization is introduced. The public
[FreeType glyph retrieval contract](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html)
distinguishes load targets from raster formats and requires cache identity to
reflect policy changes. Interpreter35/40 availability is not Microsoft parity.
The exact outline policy belongs to each retained font identity and bounded
cache key. A warm request may reuse only its own generation; switching targets
creates a distinct face while retaining the same immutable source owner. Existing
hinted requests and paragraphs still select normal; admitting a source mode
requires the source policy and pixel gates below.
Independent public-API face controls compare all metrics, points, tags, contours
and flags for both interpreters, all four policies, fractional/nonuniform sizes
and phases. Controls retain post-disposal transport, warm target switching and
invalid-target rollback. The signed-source producer and admission verifier
require `outlineLoadPolicies` and `outlinePolicyIdentity` evidence on every RID;
older receipts cannot qualify this updated producer contract.

Both native providers and the managed native-font bridge use this common native
font execution service. There is no second managed TrueType instruction executor
to modify. The managed glyph geometry importer retains the captured points;
it must not re-hint them. No public wire field, source rendering selection,
generic placement mode or GPU coverage policy changes in this private execution
step. Existing whole-provider/package and Microsoft source comparisons remain
required before claiming the Forms rendering defect fixed.

`NativeTextShapingContext.CaptureOutlineBatch` and the additive native
`progpu_native_text_context_capture_outline_batch` now carry an explicit outline
target through the same whole-batch ownership path. The original
`CaptureHintedBatch` entry point selects normal and keeps its existing signature,
request size, ABI version and reserved-zero rule. The four public numeric values
come from the C header through generated constants; they are not duplicated in
managed policy code. The new entry point requires the matching native producer,
with no missing-export fallback to another target.

Policy validation precedes context/font work; every existing request, input-span
and publication-alias check remains shared. Failure preserves the previous batch
handle, while success transfers one independently owned generation. Managed
wrappers retain the exclusive context lease, one capture call per batch and
constructor-failure cleanup. Native context controls compare each public target
with its exact private generation and retain all four batches after the source
contexts retire. Invalid targets, nonzero reserved fields and output aliasing
the request reject without mutation. Dependency-absent controls return Unsupported
for valid targets. This boundary does not select GDI quality, GDI+ hint mapping,
contrast, source shaping or GPU coverage; those and final whole-product acceptance
remain required.

Already-positioned source glyphs can use the private batch outline writer without
constructing another shaped run. It emits one source-slot mapping per original
descriptor, including duplicate IDs and no-ink glyphs, and leaves placement and
advances to the source. Unlike shaped runs with explicit auxiliary descriptors,
the batch path validates every glyph. Both paths share original contour walking,
exact physical point/phase conversion, SIMD/scalar selection and coverage policy.
Their complete preflight retains output-tail and owned-capacity alias rejection,
atomic failure and unchanged source metadata. Work is O(G+P), for G glyphs and P
outline points, with caller-owned scratch bounded by the largest glyph.

This private writer is shared by both native providers and is the geometry source
for the managed native-font bridge; a second managed contour algorithm is not
introduced. Drawing replay integration remains required. Its antialiased vector
policy does not implement monochrome dropout,
Microsoft contrast or ClearType coverage. Synthetic original-contour controls and
actual captured batches after context disposal verify this geometry step; they do
not qualify the source rendering defect.

`NativeHintedFontBatch.CreateGeometry` exposes that same writer through an owned
immutable native geometry handle. The handle retains the exact captured batch
after its public batch and context retire. Creation allocates one complete
candidate and bounded largest-glyph scratch before publication; failures leave
the previous handle unchanged. Counts and descriptor records are generated from
the public C header, while outlines and segments reuse existing ABI records.
No shaped-run metadata, clusters, advances or caller positions are fabricated.

One synchronous `CopyTo` transfers the complete outlines, segments and original
descriptor mapping to pinned caller spans. Counts and copy operations make no
font, allocation or GPU calls. Complete capacities must be mutually disjoint and
outside the retained owner, geometry storage and original batch/font storage;
validation finishes before any write. Unused tails remain unchanged. No-ink
descriptors retain their original IDs with an absent-outline sentinel. Empty
batches remain valid with zero counts. Managed handles use the existing exclusive
native resource lease and constructor-failure cleanup, including finalization.
Both native providers share this implementation; the managed bridge imports its
geometry without a second contour converter. Ordinary rendering is unchanged.

Controls cover all four outline targets with both interpreters, exact geometry
and descriptor copies after context/batch disposal, retained lifetime and final
release, insufficient and overlapping outputs, original font-storage rejection,
invalid/unsupported policy rollback, empty batches and managed generated layouts.
These are ownership/transport controls, not GDI/GDI+ pixel qualification or a
claim that vector antialiasing implements the remaining source raster policies.

`NativeHintedGlyphGeometryFactory.CreateFontBatch` connects captured geometry to
the existing retained managed/native replay with the caller's original logical
baseline positions. It freezes every position before native preparation and
owns an independent geometry handle. Existing physical contour validation and
conversion are shared with the paragraph importer, without reshaping or another
contour algorithm. Preparation is O(G+P) and bounded by the existing geometry
limits. Complete copies occur only at preparation; replay retains its existing
atlas, batching, uploads, culling, paint and device-lifetime paths.

`FontGlyphPlacement` explicitly contains only the original descriptor index,
glyph ID, outline slot and position. It has no advance, cluster, bidi or paragraph
fields. `HasShapingMetadata` distinguishes this resource from original shaped
occurrences; requesting the absent representation throws. Selection retains
duplicate/no-ink slots and the original source/raster identity without copying
outlines. Both renderers consume the same placement fields and retain exact DPI
and existing identity-frame admission. They do not infer missing source metadata.

Paired synthetic controls compare the complete native command stream with the
existing shaped-resource route and retain every original managed cold/warm pixel,
upload and raster-submission check at two DPI scales. Independent lifetime and
selection controls verify explicit missing metadata and one final source release.
These controls establish the shared replay route; actual Drawing mode selection,
contrast/coverage and original Microsoft application images remain unfinished.

Keep font execution lazy and out of frame replay, retain bounded exact-generation
caches, and preserve original layout/interaction and GPU resource ownership.

The architecture follows the existing [font-hinting research](native-font-hinting.md):
[Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite rendering parameters](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriterenderingparams),
[Win2D layouts](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender font instances](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html),
[Vello](https://github.com/linebender/vello),
[Parley](https://github.com/linebender/parley/blob/main/doc/concept.md) and
[HarfBuzz](https://harfbuzz.github.io/what-does-harfbuzz-do.html).
Adopt the separation of reusable layout, font-instance policy and target-dependent
GPU coverage; reject reshaping for paint, captured-image substitution and global
gamma changes. Existing culling, worker preparation, bounded eviction, demand
uploads, batching, device loss and atlas-generation ownership remain unchanged.
Final source integration needs complete original pixels, cold startup/first
interaction, scroll percentiles, allocation/residency evidence and native/package
gates. This reader does not admit source raster policy or close Forms issue174.
