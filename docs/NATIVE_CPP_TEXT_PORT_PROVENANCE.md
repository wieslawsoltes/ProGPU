# Native C++ text-port provenance and execution plan

## Scope and policy

ProGPU's native text implementation is a full parallel backend port of the proven,
original ProGPU-owned clean-room C# implementation. Managed and native builds share
the same canonical production shader files rather than maintaining shader forks. The authoritative source
checkpoint for the first managed-picture glyph-lowering slice is
`d5a41e169f19f2da103a7cd8001f35f3b250198d`. The repository policy in
`agents.md` permits this cross-language/backend work when each slice records
its in-repository source provenance, preserves public and performance
contracts, and adds matched differential tests. No third-party implementation
source is copied or translated.

WinUI controls, XAML, and media are outside this native text-port phase. The
target is the reusable font, shaping, fallback, layout, retained-scene, and GPU
text core used below those frameworks.

## Authoritative ProGPU sources

| Native responsibility | ProGPU-owned source of truth | Preserved contract |
| --- | --- | --- |
| Recorded shaped runs | `src/ProGPU.Scene/RenderCommand.cs` | Immutable glyph-index/position arrays, range ownership, transform, hinting, rendering mode, and font presentation state. |
| DPI/subpixel placement and glyph presentation | `src/ProGPU.Scene/Compositor.cs` | Maximum-singular-value raster size, 4-way physical subpixel phase, affine bases, bold/italic/font transform, style opacity, and unchanged-run reuse. |
| Font data and glyph outlines | `src/ProGPU.Text/TtfFont.cs` and its partials | Bounded SFNT/table validation, metrics, cached immutable outlines, color/bitmap metadata, and explicit ownership boundaries. |
| OpenType shaping | `src/ProGPU.Text/OpenTypeTextShaper.cs`, `src/ProGPU.Text.Shaping/` | Reusable glyph IDs, advances, offsets, clusters, script/language/direction state, GSUB/GPOS/GDEF behavior, and malformed-font failure rules. |
| Fallback and font discovery | `src/ProGPU.Text/FontManager.cs` and platform catalogs | Deterministic fallback order, cached font identity, and platform-neutral/provider seams. |
| Paragraph/line layout | `src/ProGPU.Text/TextLayout.cs` | Wrapping, trimming, alignment, positioned runs, caret/selection geometry, and hit testing. |
| Outline compilation and compute coverage | `src/ProGPU.Vector/PathAtlas.cs`, `src/ProGPU.Backend/Shaders/GlyphRasterizer.wgsl` | Exact line/quadratic/cubic outline semantics, bounded compute rasterization, winding rules, and retained atlas generations. |
| GPU text composition | `src/ProGPU.Backend/Shaders/Text.wgsl` | Physical-pixel atlas sampling, affine placement, style modes, masks, premultiplied output, and bounded shader work. |

Every C++ source file added for later phases must name the exact source file and
source checkpoint in its adjacent design note or provenance table. Native data layout,
ownership, and file boundaries may be optimized for native performance, but the applicable
algorithms and observable, quality, complexity, and performance contracts are ported in
full. Shader algorithms use the same canonical ProGPU resource files in both managed and
native builds; only generated embedding or binding wrappers may differ.

Blittable font, shaping, layout, and scene transport records added by this port use the
same header-driven C# generation lane as existing native scene records. This removes
parallel handwritten field layouts while keeping managed ownership and ergonomic APIs
outside the wire contract. Every new eligible record must add its generator marker and
pass the stale-output plus native/C# size-and-offset gates in the same slice.

The initial native OpenType layout execution boundary ports the ProGPU-owned
coverage/class lookup and GDEF compatibility policy at checkpoint `3cc418aa`.
`open_type_layout_table_view` lazily validates GSUB/GPOS lookup records;
`open_type_gdef_view` borrows GDEF 1.0/1.2/1.3 glyph classes,
mark-attachment classes, and MarkGlyphSets coverage without copying font data.
Coverage and range-class queries are `O(log R)`, dense class queries are
`O(1)`, fixed blocklist evaluation is bounded `O(1)`, and all views retain
`O(1)` storage. This common validated layer is shared by the following native
GSUB and GPOS executors so lookup flags do not fork or allocate per glyph.
The first GSUB executor ports the managed raw bounded path for Single,
Multiple, Alternate, Ligature, Extension, and Reverse Chaining Single
substitutions. Reverse lookups traverse the glyph buffer from end to start and
validate ordered backtrack/lookahead coverages before mutation. The executor mutates a
caller-owned fixed-capacity `shaping_glyph` span in place, preserves clusters
and placement fields across expansion, applies GDEF base/ligature/mark and
mark-set filtering, clamps one-based alternate feature values, and preflights
each individual substitution before mutation. A lookup pass is commonly
`O(P * S * (log C + K))`; in-place expansion or compaction makes the bounded
worst case `O(P * S * (log C + K + N))` for `P` input positions, `S`
subtables, coverage size `C`, matched ligature length `K`, and glyph capacity
`N`. Storage is `O(1)` beyond caller capacity.
Coverage-based Context and Chaining Context format 3 tables share the same GDEF
eligibility walk, validate every ordered coverage before dispatch, apply
sequence lookup records against the current post-mutation buffer, propagate
unsafe-to-break flags across matched input, and cap nested lookup recursion at
64 without heap scratch.
Context formats 1 and 2 add explicit glyph-sequence and ClassDef rule-set
matching over the same caller buffer. Rule offsets, input counts, class sets,
and substitution records are fully bounded before dispatch; ignored marks stay
in place while visible input positions are resolved without temporary arrays.
Chaining Context formats 1 and 2 use the corresponding glyph and three-ClassDef
rule layouts for backtrack, input, and lookahead matching. They share the same
bounded nested-record executor and preserve reverse-order backtrack semantics.
The native shaping-plan boundary also walks ScriptList, Script, LangSys,
FeatureList, and Feature tables directly from the borrowed layout data. It
selects the required feature plus explicitly requested features, falls back to
DFLT/default LangSys, preserves feature and lookup order, and deduplicates
lookup indices into caller storage. The requirements pass reports a safe
upper-bound capacity in `O(S + F + L)` time; selection is `O(S + F + L^2)` in
the worst case because deduplication intentionally uses no heap scratch.
The native GPOS executor ports `OpenTypeTextShaper.cs` through checkpoint
`e4d836b2`. It applies SinglePos formats 1/2, PairPos glyph-set and ClassDef
formats, CursivePos, MarkBasePos, MarkLigPos, and MarkMarkPos, including
ExtensionPos wrappers and the shared GDEF lookup filter. Base placement and
advance values remain signed font units; every variable-length ValueRecord,
anchor, and referenced device table is bounded before mutation. The stable
32-byte interop glyph record does not acquire transient shaping state. Instead,
the caller provides one fixed eight-byte attachment record per glyph and one
byte of resolution scratch. Parent chains are resolved without allocation,
with cycle detection and a depth limit of 64. A lookup is
`O(P * S * (log C + log K))` for positions `P`, subtables `S`, coverage size
`C`, and pair/anchor search size `K`; resolving mark relationships adds
`O(P + A)` work for the advances `A` crossed by attached marks. Anchor format-3
Device and VariationIndex references are applied through the caller's ppem and
normalized variation coordinates using the shared item-variation executor.

The resolved feature-value lane ports the ProGPU-owned 16-byte
`ShapingFeature` contract and its half-open `[Start, End)` input ranges. One
borrowed span now controls GSUB, alternate selection, GPOS, Arabic forms,
Hangul Jamo stages, conditional fraction actions, and the Indic/USE/Myanmar/
Khmer staged executors. A lookup shared by multiple feature tags uses the same
required/global-feature precedence as the managed plan. Ranged lookups execute
at eligible cluster starts without allocating or materializing per-glyph
masks; ordinary runs with no ranged settings retain the existing bulk lookup
executor. The implementation is isolated in
`progpu_native_open_type_feature_values.cpp`, keeping the uniform-run
orchestrator focused on stage ordering and preserving C++20 module builds.

Unicode bidi analysis ports the ProGPU-owned `Bidi/Uax9Resolver.cs` at
checkpoint `d9e89879`. The generator now copies the already generated Unicode
17 bidi-class and paired-bracket packed records from
`Bidi/UnicodeBidiData.Generated.cs` into the native table header, so the two
backends cannot acquire independent property data. The native resolver applies
UAX #9 revision 51 explicit embeddings/overrides, isolates, weak types, paired
brackets, neutrals, implicit levels, L1 resets, and retained X9-control levels.
All unit, active-index, level-run, and bracket-pair storage belongs to the
caller; validation and short capacity fail before either output or caller
scratch is touched. Typical work is `O(N)`, with `O(N log N)` bounded bracket
pair ordering and `O(N)` caller storage. Explicit levels are capped at 125 and
the paired-bracket stack at 63 as required by the algorithm.

Extended grapheme segmentation is an original native implementation of
[Unicode Standard Annex #29 revision 47](https://www.unicode.org/reports/tr29/tr29-47.html),
rules GB3-GB13. Its generated Unicode 17 property inputs are the official
[GraphemeBreakProperty.txt](https://www.unicode.org/Public/17.0.0/ucd/auxiliary/GraphemeBreakProperty.txt),
[emoji-data.txt](https://www.unicode.org/Public/17.0.0/ucd/emoji/emoji-data.txt),
and [DerivedCoreProperties.txt](https://www.unicode.org/Public/17.0.0/ucd/DerivedCoreProperties.txt),
each pinned by SHA-256 in `eng/generate-unicode-grapheme-table.py`. The generated
managed property source is then the one input copied by the native Unicode
generator, keeping property identity automatic. A compact streaming state
implements Hangul, Extend/ZWJ/SpacingMark/Prepend, Indic-conjunct, emoji-ZWJ,
and regional-indicator rules in `O(N)` time and `O(1)` internal storage. The
count/write boundary is transactional and returns both source and decoded
scalar ranges for shaping and caret reuse.

Uniform-run orchestration ports the stage ownership from ProGPU-owned
`CpuOpenTypeShaper.cs` and `OpenTypeTextShaper.cs` at checkpoint `3b9ade5f`.
It assigns grapheme-preserving source clusters, maps cmap glyphs, applies base
and HVAR advances, executes caller-selected Script/LangSys GSUB and GPOS plans,
honors the shared GDEF blocklist/mark policy, and resolves attachment chains.
Glyph capacity remains caller-selected because legal MultipleSubst expansion is
font-dependent; all lookup, grapheme, attachment, and state buffers are explicit
typed spans. The boundary is one bulk call over a uniform run and never performs
per-glyph managed/native crossings. Script-specific joining/reordering,
fallback selection, and paragraph layout intentionally remain subsequent
reusable CPU stages rather than being hidden in rendering.

The granular `Text/Shaping/progpu_native_use_diacritics.cpp` unit directly
ports ProGPU-owned `GlyphSubstitutionBuffer.NormalizeUseDiacritics` from
`src/ProGPU.Text/OpenTypeTextShaper.cs` at checkpoint `e68517cc`. It borrows the
same validated `UnicodeNormalizationData.bin` view as the standalone native
normalizer and expands only canonical decompositions whose first scalar has a
Unicode mark general category, matching the managed USE boundary. An
option-aware requirements pass reports expanded complex-script metadata before
mutation; cmap mappings and glyph capacity are then preflighted before a
backward in-place write preserves each source cluster. This is
`O(G log R + D)` work with `O(1)` internal storage for `G` glyphs, `R`
decomposition records, and `D` written components. Missing normalization data,
invalid font mapping, and short glyph storage fail without exposing partial
run output. Header and named-module consumers exercise the same implementation.

Arabic run-boundary context directly ports the neighboring-text state seeding
and final-action update from ProGPU-owned
`GlyphSubstitutionBuffer.AssignArabicJoiningActions` at checkpoint `b5cf0ae9`.
The native options borrow decoded pre/post scalar spans in the same bulk call;
the joining pass examines at most five scalars on each side, skips transparent
marks, and never retains or copies the context. The same pre-context signal
suppresses `InsertBeginningDottedCircle`, matching the managed chunked-shaping
contract. Boundary work is fixed `O(1)` beyond the existing `O(G)` state
machine and requires no additional caller scratch.

Initial scalar-to-glyph expansion is isolated in the granular
`Text/Shaping/progpu_native_initial_mapping.cpp` unit and directly ports
ProGPU-owned `GlyphSubstitutionBuffer.Create`, `TryAppendIndicSplitMatra`, and
`AppendNormalizedRune` from `src/ProGPU.Text/OpenTypeTextShaper.cs` at
checkpoint `0a08efec`. The C++ path uses the existing borrowed
`UnicodeNormalizationData.bin` view: missing mapped scalars use canonical FormD,
missing U+2011 tries U+2010 first, Indic mark-led composites expand regardless
of source-glyph coverage, and Khmer split matras prepend U+17C1. Every emitted
component retains the managed grapheme/source cluster and passes the existing
space fallback and variation-aware cmap path. The option-aware requirements
pass counts expansion and sizes complex-script metadata before the write pass;
font mapping, advances, and capacity are preflighted before output mutation.
Work is `O(N log R + D log C)` for input scalars `N`, normalization records
`R`, output components `D`, and cmap records `C`, with `O(1)` internal storage,
no per-glyph boundary calls, and matched decomposition, split-matra,
non-breaking-hyphen, and short-buffer tests.

Native shaping-route selection is isolated in
`Text/Shaping/progpu_native_shaping_route.cpp` and directly ports ProGPU-owned
`CreateShapingPlan`, `ResolveDirection`, `ResolveLayoutScript`,
`IsIndicShaperScript`, and `UsesArabicJoiningScript` from
`src/ProGPU.Text/OpenTypeTextShaper.cs` at checkpoint `2a47eec0`. Given one
borrowed font and Unicode script tag, it applies the exact third-generation
then second-generation Indic GSUB ScriptList preference, falls back to the
managed USE script set, selects the matching Indic/USE/Khmer/Myanmar route,
retains Arabic-joining evidence, canonicalizes `hira`/`laoo`, and resolves the
same default LTR/RTL direction. Script discovery deliberately preserves the
managed tolerant bounded record scan; later strict layout construction still
owns malformed-table rejection. Work is `O(S)` for `S` ScriptList records with
`O(1)` storage and no font-table copy. Synthetic fonts cover generation
priority, fallback routes, direction overrides, canonical tags, and invalid
direction failure in the header-path suite; the public type and resolver are
also compiled and linked through the named-module consumer.

The same route unit ports `ToLanguageTag` and the Hebrew `HasMarkFeature`
decision from that checkpoint. BCP-47 matching is bounded, ASCII
case-insensitive, treats underscore as a hyphen, and returns the managed
`dflt` fallback without allocation. A tolerant borrowed GSUB/GPOS FeatureList
scan suppresses Hebrew presentation-form fallback exactly when `mark` is
advertised. Synthetic GPOS plus language-variant tests cover both decisions.

Native feature-plan construction is isolated in
`Text/Shaping/progpu_native_shaping_features.cpp` and directly ports the
ProGPU-owned `TextShapingOptions` default feature baseline,
`AddScriptFeatures`, and `AddDirectionalFeatures` from
`src/ProGPU.Text/OpenTypeTextShaper.cs` at checkpoint `25762bb8`. It preserves
the managed feature order and last-value behavior, Khmer/Indic `liga` policy,
Arabic `stch`/`mset`, Hangul/USE/Indic stage tags, direction features, and
vertical `kern` removal. The bounded API emits ordered tags separately from
non-default global value records so value-one defaults do not become explicit
ranged features. Work is `O(F^2)` for the deliberately small feature set `F`,
uses caller-owned scratch/output only, and is covered by default, LTR, RTL,
vertical, Khmer, Indic, Arabic, override, and short-buffer tests. Both the
header and named-module consumers compile and link the public contract.

`Text/Shaping/progpu_native_shaping_request_features.cpp` directly ports the
feature normalization at the `CpuOpenTypeShaper.Shape` boundary from
`src/ProGPU.Text/CpuOpenTypeShaper.cs` at checkpoint `2dad8df4`. Full-run
records override the same default baseline with `int.MaxValue` clamping,
explicit tags are deduplicated in request order, partial ranges remain intact,
and a ranged positive value can add an enablement entry above a zero baseline.
Sizing and writing use `O(F^2)` time, caller-owned output, and no allocation;
default override, duplicate/ranged enablement, clamping, invalid-range, and
transactional short-buffer cases are matched in the native suite and the API
is linked through the named-module consumer.

The native run contract now carries `unicode_script` independently from the
selected layout `script`, matching managed `CreateShapingPlan` ownership at
checkpoint `ec2cf08d`. A zero Unicode tag falls back to the legacy single-tag
contract. Generation-specific layouts such as `dev2`/`dev3` therefore select
their exact GSUB/GPOS ScriptList while Unicode preprocessing, vowel repair,
Indic reorder placement, joining, Hangul preparation, and fallback-mark rules
continue using `deva` or the corresponding base script. A synthetic `dev2`
uniform run verifies that the Devanagari vowel-constraint insertion remains
active through the complete shaper rather than only the isolated helper.

The raw GPOS executor now covers rule-, class-, and coverage-based Context and
Chaining Context formats 1-3. Nested position records reuse the same borrowed
lookup table and caller glyph/attachment buffers with a fixed 64-level cycle
bound, preserving GDEF filtering and unsafe-to-break ranges.

GPOS ValueRecord and anchor adjustments now share the native font variation
store port: VariationIndex records resolve through GDEF 1.3, and classic Device
tables decode signed 2/4/8-bit ppem deltas before converting them back to font
units. The shaper forwards the existing normalized-coordinate span in bulk;
there is no secondary variation parser or glyph-by-glyph interop path.

Arabic joining now ports ProGPU-owned `ArabicJoiningData.Generated.cs` and its
state machine directly. Managed and native fallbacks share checked-in Unicode
17 `Mn`/`Me`/`Cf` ranges generated from the official
[`UnicodeData.txt`](https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt)
(SHA-256 `2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c`).
The uniform-run shaper carries the action in reserved internal flag bits across
GSUB expansion/ligature replacement, targets `isol`/`fina`/`fin2`/`fin3`/
`medi`/`med2`/`init` lookups at exact eligible glyph positions, then clears the
internal bits before the public bulk glyph result crosses the ABI.

Font fallback now ports the grapheme-preserving ownership boundary from
ProGPU-owned `FontManager` and `OpenTypeTextShaper`: platform discovery resolves
and parses faces before shaping, then one borrowed candidate span is searched
without callbacks or file access. The preferred face stays first, fallback never
splits an extended grapheme, missing coverage remains explicit, and adjacent
graphemes with the same face/state coalesce into reusable runs. Selection is
`O(G * F * S)` worst case for `G` graphemes, `F` faces, and `S` scalars in a
grapheme, with `O(1)` internal storage and caller-owned output.

Platform font discovery now has a matching native provider/cache seam. A host
adapter exposes stable family/face identities and borrowed font bytes from
CoreText, DirectWrite, Android/iOS platform catalogs, application assets, or a
browser download cache. The resolver parses candidates only on cache misses,
matches family/style and scalar coverage, and stores positive and negative
results in a caller-owned generation-keyed ring. Provider generation changes
invalidate entries without walking or clearing the cache. A hit is `O(C)` for
`C` cache slots and performs one bounded provider refresh; a miss is `O(F*T)`
for `F` faces and SFNT table cost `T`. Discovery, downloading, mapping, and byte
lifetimes remain outside shaping, so no per-glyph callback, file access,
allocation, or retained managed pin is introduced.

## Delivered borrowed SFNT/TTC foundation

The first text-core slice ports the ProGPU-owned `SfntFontFace.cs` contracts at
checkpoint `2f2a92c4286da763d4e4be0908b0f6b706a86c3f` into the standalone
`progpu_native_text` C++20 library. `sfnt_font_view` borrows a caller-owned byte
span and retains no file, mapping, decoder, or heap ownership. It validates the
SFNT/TTC header and directory bounds once, preserves TTC absolute table offsets
and last-record-wins duplicate-tag behavior, and skips an individually invalid
table record as the managed implementation does. Table lookup and construction
are `O(T)` time with `O(1)` storage for `T` table records.

The same view reads `head`, `hhea`, `hmtx`, and `maxp` metrics without copying
and selects cmap format 4, 12, and 13 subtables using the managed Unicode and
Microsoft-symbol precedence. Format 12/13 lookup is `O(log G)` for `G` groups;
format 4 is `O(S)` for `S` segments. All paths are allocation-free and CPU-only.
Short and long `loca` tables resolve into borrowed `glyf` byte spans in `O(1)`
time and storage per glyph; empty glyphs preserve an empty successful result,
and non-empty records expose their contour count and exact font-unit bounds
without parsing or allocating an outline graph.
The simple decoder and contour lowerer port `src/ProGPU.Text/TtfFont.cs` at
checkpoint `ba6b5588afff85203b64d48c534c4780afb8d75c`. Simple TrueType
records continue through an allocation-free two-pass
decoder. Pass one validates strictly increasing contour endpoints, instruction
ranges, repeated-flag expansion, and the complete X/Y delta byte budget while
reporting exact caller-buffer requirements. Pass two writes raw flags and
signed accumulated coordinates directly into caller spans. Its complexity is
`O(C + P + B)` time and `O(1)` internal storage for `C` contours, `P` points,
and `B` encoded bytes. Empty, simple, and composite glyphs are distinct typed
results. A second allocation-free count/write pair ports
`TtfFont.DecodeContourToFigure` directly into the canonical
`progpu_native_path_segment` ABI. It preserves line closure, explicit
on-curve points, implied midpoints between consecutive off-curve points, and
quadratic controls in `O(C + P)` time with `O(1)` internal storage. `gvar`
application remains open rather than silently approximated.

The managed and native tests share the repository's exact `Inter-Medium.ttf`
asset as a differential checkpoint. Both assert 2,048 units per em, 2,937
glyphs, scalar U+0053 to glyph 397, advance/side-bearing `1323/106`, and glyph
bounds `(106,-25)-(1217,1510)`. The native test additionally verifies the
same glyph's one contour, 46 decoded points, 59 instruction bytes, mixed
on/off-curve flags, repeated-flag behavior, insufficient caller buffers,
truncated coordinates, excessive repeats, decreasing endpoints, and explicit
composite classification. Matched final path evidence covers all 34
line/quadratic records for Inter Medium glyph 397 with an exact shared 64-bit
hash of `13245664145576799719`, including the
start point `(665,-25)` and closed endpoint.
Composite record parsing ports the descriptor loop in
`TtfFont.ParseCompositeGlyphOutline` at checkpoint
`3abbec85d749466130538c8371dc772f1ef08671`. A first pass validates every
component, signed/unsigned byte/word arguments, F2Dot14 uniform, axis, or 2x2
transforms, continuation flags, and optional instruction range while reporting
the exact component count. A second pass writes fixed caller-owned component
records. Both passes are `O(K + B)` time and `O(1)` internal storage for `K`
components and `B` component/instruction bytes.

Recursive expansion ports `TtfFont.BuildCompositeGlyph` at checkpoint
`d477532a4bec274bdb47634dacab91d809c80fa2` and uses a fixed 33-glyph ancestor
stack matching ProGPU's
depth limit plus caller-owned simple-point/contour scratch and final point/path
spans. Its preflight reports exact scratch and output counts; the write pass
applies byte/word XY offsets, scaled offsets, deterministic midpoint-to-even
grid rounding, uniform/axis/2x2 transforms, and parent/component point
attachment without retaining any temporary graph. Ordinary work is linear in
visited glyph records, component records, decoded points, and path segments;
nested point attachments may require bounded child preflight, making the
worst case `O(D * (G + K + P + S))` for depth `D <= 33`, while internal storage
remains `O(1)`. Cycles and excessive depth are bounded, invalid point
attachments skip that component as in the managed implementation, and
insufficient output fails before writing. The Inter Medium U+00E9 checkpoint
resolves to composite glyph 618, exactly reproduces its two component records
(glyphs 614 and 1770), and expands to 35 points and 27 path records. Managed
and native streams share start `(630,-23)` and exact hash
`5543379682355176128` across all three contours.
Descriptor parsing and recursive outline assembly remain separate translation
units sharing only a private fixed-record reader, keeping the native text port
granular without duplicating component semantics.

The native hardware sample now connects those exact canonical records directly
to the retained glyph resource and the production glyph-atlas execution path.
When passed the repository's Inter Medium face, it decodes U+00E9 through the
standalone C++ font library, builds no intermediate geometry graph, and submits
the resulting 27-record composite outline through the shared
`GlyphRasterizer.wgsl` and `Text.wgsl` modules. The Apple M3 Pro/Metal
checkpoint renders glyph 618 at 48 logical pixels from a 64-physical-pixel
atlas raster, verifies a bounded yellow-coverage region during readback, and
retains the complete sample's eight GPU draws and 11,616 uploaded vertex bytes.
The fallback sample glyph remains available when no font path is supplied, so
packaged consumers do not acquire a source-tree asset dependency. Repository
Clang and Windows build scripts pass the font explicitly, making the real
font-to-GPU connection an integration gate on runnable targets.

Variable-font metadata starts at checkpoint
`3d1bae94c64259afbd64ed2bc630fb3430d2bc79` with a separate
`progpu_native_font_variations.cpp` translation unit that ports
`OpenTypeVariationData.ParseAxes`. The borrowed two-pass API validates the
`fvar` header, axis offset/count/stride, and complete fixed-size record range
before writing. It preserves tags, signed 16.16 minimum/default/maximum values,
flags, and name IDs without resolving provider-owned strings or allocating.
Counting is `O(1)` and decoding is `O(A)` time with `O(1)` internal storage for
`A` axes; an undersized caller span writes nothing. Synthetic hidden-axis,
truncation, and transactional-buffer cases pass under normal, LLVM named-module,
and ASan/UBSan builds. The real InterVariable checkpoint matches the managed
implementation's `opsz` 14/14/32 and `wght` 100/400/900 axes exactly. `avar`
normalization follows at checkpoint
`05ca2df1faee220bd7783611a3cf13ad72189130`: one signed 16.16 user
coordinate is clamped and normalized with the managed away-from-zero F2Dot14
rule, then passed through the selected piecewise `avar` map with managed
midpoint-to-even interpolation. The allocation-free scan validates every map
range in `O(A + M)` time and `O(1)` storage for `A` axes and `M` map pairs;
an absent/out-of-range optional table retains the base normalized coordinate,
and an axis-count mismatch is ignored exactly like the managed implementation.
Synthetic endpoints, clamping, interpolation, invalid-axis, and truncated-map
cases plus the real InterVariable checkpoints match `opsz=23 -> 8192`,
`wght=500 -> 2949`, and `wght=700 -> 8847`.

The first `gvar` slice follows at checkpoint
`9e0c63be839ad651c61f477da1f85111204eaa21`. Granular
`progpu_native_gvar.cpp` and `progpu_native_gvar_packed.cpp` translation units
port the ProGPU-owned table layout, glyph-offset, shared-tuple, packed-point,
and packed-delta contracts without retaining or allocating table data. Every
packed stream uses a validating count pass before its transactional caller-span
write pass, both `O(N)` time and `O(1)` internal storage for `N` encoded values.
Normal Clang, named C++20 module, and ASan/UBSan builds cover malformed runs,
undersized outputs, duplicate points, signed byte/word deltas, and all-point
encoding. The real InterVariable checkpoint matches 2 axes, 5 shared tuples,
2,937 glyphs, long offsets, exact shared tuple coordinates, and exact 594/60
byte glyph payloads for glyphs 397/618. Tuple regions, scalar evaluation, and
outline application were sequenced as the following granular slices.

Tuple regions and scalar evaluation follow at checkpoint
`1c31a45d271543a919b1397ba2a18f83f3f24cc1` in the granular
`progpu_native_gvar_tuples.cpp` unit. A validating count pass resolves shared
or embedded peaks, explicit or implicit start/end regions, private-point flags,
and total tuple payload bounds before the write pass touches caller-owned
header and F2Dot14 coordinate spans. Region scalar evaluation preserves the
managed invalid/zero-peak behavior and piecewise ramps in `O(A)` time and
`O(1)` storage for `A` axes. Synthetic embedded/intermediate/private-point
coverage plus real Inter shared tuples pass normal, named-module, and
ASan/UBSan gates. Packed tuple payload application and untouched-point
interpolation follow in the next checkpoints.

Allocation-free IUP interpolation follows at checkpoint
`83cc056eb2311a6ba23bdd2b003de91c15576002` in
`progpu_native_gvar_interpolate.cpp`. It validates complete contour ownership
before mutation, then scans each circular contour in `O(P)` time and `O(1)`
internal storage, preserving the managed one-touch propagation, two-touch
piecewise interpolation, equal-coordinate min/max rule, and wraparound pairs.

Simple-glyph tuple application follows at checkpoint
`ca9e9d22250b09035906c42814fafa6ef35ab898` in the granular
`progpu_native_gvar_apply.cpp` unit. It preflights all shared/private point and
X/Y delta streams before writing output, uses exact caller-owned scratch spans,
evaluates each tuple region, applies all-point or sparse deltas, and invokes the
IUP pass for untouched points. Work is `O(T * (A + P + D))` with bounded
caller storage and `O(1)` internal storage for tuples `T`, axes `A`, points
`P`, and packed deltas `D`. Fractional varied points lower directly to the
canonical line/quadratic path ABI without rounding. The managed and native
InterVariable `opsz=23` glyph-397 checkpoint matches start `(648.5,-25)`, 39
segments, and exact full-stream hash `12343280691057163238` under normal,
named-module, ASan/UBSan, and focused managed gates.

Composite-component tuple application follows at checkpoint
`6ca97ba7fc9def783fc511c8918e1743de701414`. The shared/private point and
packed-delta walker is isolated in `progpu_native_gvar_payload.cpp` and reused
by both simple and composite glyphs. `progpu_native_gvar_composite.cpp`
preflights every tuple, evaluates the same normalized regions, and writes
caller-owned component offsets transactionally with no internal allocation.
The native InterVariable `opsz=23` glyph-618 checkpoint exactly resolves its
two component offsets to `(0,0)` and `(15,0)`; the authoritative managed
outline independently matches start `(595,-24)`, 2 figures, 36 segments, and
hash `12064242707506207632`.

Recursive varied-composite expansion follows at checkpoint
`7c045a805a3f4d7028d2f503660c402ff3ead09d`. The dedicated
`progpu_native_true_type_varied_requirements.cpp` pass measures exact maximum
simple-tuple, composite-tuple, varied-point, and active recursion-path offset
storage. `progpu_native_true_type_varied_outline.cpp` then varies every simple
child, applies parent component deltas before scaled-offset transformation,
preserves point attachment and midpoint-to-even grid rounding, and emits the
canonical path ABI directly. Both passes use a bounded 33-glyph ancestor stack
and caller-owned spans with no internal heap allocation. Native InterVariable
glyph 618 now matches the full managed `opsz=23` checkpoint: start
`(595,-24)`, 36 segments, and exact hash `12064242707506207632`. Normal,
named C++20 module, ASan/UBSan, short-scratch transactionality, and focused
managed differential gates pass.

Phantom-point advance fallback follows at checkpoint
`868906b56aba0e79d918555de2482e351b57e125` in the granular
`progpu_native_gvar_phantom.cpp` unit. It reuses the shared tuple payload
walker, accumulates left/right phantom X deltas for all-point or sparse tuples,
and publishes their difference only after complete validation. Work is
`O(T * (A + D))` with caller-owned `O(T * A + I)` scratch for tuples `T`, axes
`A`, packed deltas `D`, and glyph item count `I`. A synthetic half-scaled tuple
produces exact left/right deltas `2/5` and advance delta `3`; short scratch and
item-count-under-four behavior pass normal, named-module, and ASan/UBSan
gates. The metric API now composes this raw evaluator with the HVAR precedence
and item-variation-store slice through its exact caller-scratch requirements.

Borrowed item-variation stores and HVAR advance precedence follow at checkpoint
`920df3b0a2971232f84720ab384a3c2a737dd15c`. The granular
`progpu_native_item_variation_store.cpp` unit validates format-1 region lists,
subtable offsets, word/long-word delta rows, region indices, and format-0/1
delta-set maps before exposing a borrowed view. Lookup scans only the selected
row and its referenced regions in `O(R * A)` time with `O(1)` storage for `R`
regions and `A` axes. `progpu_native_hvar.cpp` applies the optional advance map
and reports whether HVAR owns advance variation so callers use phantom points
only when the managed implementation would. InterVariable glyph 397 at
`opsz=23` resolves exact native delta `-28`, matching the managed 2048-em
advance `1314 -> 1286`. Synthetic map clamping, row selection, missing-HVAR,
truncation, normal, named-module, ASan/UBSan, and 29/29 managed Inter gates
pass.

MVAR and GDEF layout-store consumers follow at checkpoint
`aed98a99996447a5b2048d5b1f7df6c14c9d1eb4`. The granular MVAR unit validates
record stride/count/store bounds, preserves the managed last-duplicate-tag
rule, and evaluates each metric record through its own borrowed store. The
GDEF 1.3 unit resolves layout variation indices through the same parser and
returns an explicit absent-store state for earlier table versions. Native
InterVariable `opsz=23` resolves exact `xhgt=-31`, matching managed X-height
`1118 -> 1087`, and successfully evaluates its real GDEF layout store. Normal,
named-module, ASan/UBSan, and 29/29 managed Inter gates pass. Wiring these
layout deltas into the later native GPOS port remains sequenced with shaping.

CFF1 container and Type 2 outline parity follows at checkpoint
`fdb47fb7973c844cb3db27b8ab968318d22b4a7b`, porting the ProGPU-owned
`Cff1OutlineSource.cs` implementation. CFF INDEX offsets, top/private
dictionaries, FDSelect formats 0/3/4, global/local subroutines, and selected
charstrings remain borrowed table views. Format-0 FD lookup is `O(1)` and
range formats are `O(log R)` without the managed range arrays. The complete
Type 2 operator set uses fixed 513-value operand and 32-value transient stacks,
bounds subroutine depth at 10, and emits line/cubic records directly into the
shared renderer ABI. A count pass makes caller-buffer writes transactional;
execution is `O(B + S)` time and `O(1)` internal heap storage for executed
bytes `B` and emitted segments `S`. Managed and native Noto CJK checkpoints
match exactly: `A` is glyph 34 with 14 canonical segments and hash
`1714381338565491643`; `日` is glyph 20220 with 16 segments and hash
`5620540281806238275`. Normal Clang, LLVM named modules, ASan/UBSan, 20/20
managed Noto tests, and Emscripten/Emdawnwebgpu/Chromium pass.
The DICT real-number reader uses a bounded locale-independent decimal/exponent
parser rather than optional floating-point `std::from_chars`, preserving C++20
compatibility with Xcode 16.4 libc++ as well as current Clang, GCC, and MSVC.

Borrowed color-font data begins with `sbix` at checkpoint
`ad2d2c43b62d5814066b2b7316241d4204aba7dd`. Strike selection preserves the
managed closest-ppem rule and higher-ppem tie break; PNG/JPEG/TIFF payloads
remain borrowed, and `dupe` chains preserve the referencing glyph's origin
while bounding depth at 16. Lookup is `O(S + D)` time and `O(1)` storage for
strikes `S` and duplicate depth `D`. OpenType SVG table/range lookup follows at
`ca025130da3201972a9d11c07bcbf91f6d925e76`: a validated glyph record returns
its borrowed encoded document, covered glyph interval, and explicit gzip flag,
with a 16 MiB encoded-document bound. Gzip decoding and XML-to-color-layer
conversion remain separate bounded slices.

CBLC/CBDT lookup ports the ProGPU-owned `TtfFont.cs` implementation at source
checkpoint `873593a7c56ced46c2480ea72de017cd7f4e5cc3`. It follows the
[OpenType 1.9.1 CBLC location contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cblc),
the inherited [EBLC index-subtable formats](https://learn.microsoft.com/en-us/typography/opentype/spec/eblc),
and the [CBDT formats 17-19 contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cbdt).
All five dense/sparse index formats are implemented in a dedicated resolver;
small, big, and index-owned metrics resolve in a separate CBDT reader. Strike
selection preserves closest ppem and the managed higher-ppem tie break, and
the result borrows the exact PNG payload with horizontal bearings rather than
allocating or decoding it. Work is `O(S + R + N)` and `O(1)` storage for
strikes `S`, subtable records `R`, and sparse records `N`. Apple Clang, LLVM
named-module, ASan/UBSan, Emscripten/Emdawnwebgpu/Chromium, and six focused
managed CBLC/CBDT tests pass.

Layered COLR/CPAL lookup ports the ProGPU-owned `TtfFont.cs` implementation at
source checkpoint `30e9ebe57ef7e4a7e575dd57c9cf6540687b87ae`. It follows the
[OpenType 1.9.1 COLR contract](https://learn.microsoft.com/en-us/typography/opentype/spec/colr)
and the corresponding
[CPAL palette contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cpal).
The current slice decodes the version-0 base/layer record model, including the
compatible prefix in a version-1 table; full COLR version-1 paint graphs remain
future work. Base-glyph lookup is `O(log B)`, layer decoding is `O(L)`, palette
resolution is `O(1)` per layer, and all table storage stays borrowed. The caller
supplies the exact output span, malformed tables fail before any output is
written, invalid palette selections preserve the managed palette-0 fallback,
and palette entry `0xFFFF` is retained as an explicit foreground-color flag.
Normal Apple Clang, LLVM named modules, ASan/UBSan, Emscripten compilation plus
the shared Emdawnwebgpu/Chromium runtime contract, and five focused managed
color-glyph renderer/compiler tests pass.

The dependency-free compressed-glyph foundation starts with a standalone
zlib/DEFLATE C++20 module. ProGPU's managed implementation delegates this seam
to `System.IO.Compression`, so the native implementation is original work from
[RFC 1950](https://www.rfc-editor.org/info/rfc1950/) and
[RFC 1951](https://www.rfc-editor.org/info/rfc1951/), with the eventual PNG
consumer constrained by the [W3C PNG specification](https://www.w3.org/TR/png-3/).
Stored, fixed-Huffman, and dynamic-Huffman blocks decode into a caller-owned
output span, including overlapping history copies up to 32 KiB. Header,
dictionary, canonical-tree, length/distance, trailing-byte, capacity, and
Adler-32 failures are explicit. Decoding is `O(I + 15S + O)` worst-case time
for input bytes `I`, Huffman symbols `S`, and output bytes `O`; scratch remains
fixed `O(1)` stack storage with no heap allocation or native dependency. Normal
Apple Clang, LLVM named-module, ASan/UBSan, and Emscripten compilation plus the
shared Emdawnwebgpu/Chromium runtime contract cover all three block types,
history overlap, malformed headers, trailing data, short output, and checksum
failure. PNG chunk/filter conversion and SVG gzip framing remain separate
bounded consumers.

The compression module also implements the
[RFC 1952](https://www.rfc-editor.org/info/rfc1952/) single-member gzip framing
required by OpenType SVG glyph documents. It validates optional extra, name,
comment, and header-CRC fields, then checks payload CRC-32 and `ISIZE` around
the same caller-buffer DEFLATE engine. `sfnt_svg_glyph_document_view` stays
borrowed; its typed decoder either copies plain XML or inflates gzip into the
caller span without a heap allocation or platform codec dependency. Framing is
`O(I + O)` time and `O(1)` internal storage, with short output, malformed
headers, DEFLATE errors, and checksum failures reported explicitly.

The next bounded slice ports ProGPU's managed PNG ownership and pixel contract
into the standalone `progpu_native_image` C++20 library while deriving the wire
format exclusively from [W3C PNG Third Edition](https://www.w3.org/TR/png-3/).
It validates signature, critical-chunk order, CRC-32, palette/transparency
metadata, consecutive `IDAT` payloads, and the exact zlib result before writing
straight RGBA8. The completed profile covers every legal grayscale, RGB,
indexed, grayscale-alpha, and RGBA depth, including packed 1/2/4-bit samples,
16-bit full-range normalization, sequential scanlines, Adam7, and filters 0–4.
Parsing and decode are `O(C + B + W*H)` time with caller-owned compressed,
filtered, and RGBA spans and `O(1)` internal state. Adam7 uses a fixed seven-pass
layout and scatters directly into the destination without a temporary frame.
Unknown critical chunks, malformed palette indices, illegal depth/interlace,
CRC/Adler failures, and short buffers fail transactionally. Normal Apple Clang
warnings-as-errors, LLVM named
modules, ASan/UBSan, and a real Emscripten/Emdawnwebgpu/Chromium execution gate
exercise this same library.

WOFF1 normalization follows the ProGPU-owned managed
`SfntFontContainer.DecodeWoff1` contract and the
[W3C WOFF 1.0 recommendation](https://www.w3.org/TR/WOFF/). A first O(T)
requirements pass validates all directory/source/target bounds and reports the
exact SFNT output plus maximum-table scratch. Normalization preflights every
zlib-compressed table before touching the output, writes canonical SFNT search
parameters and directory records, and then decodes/copies each aligned table.
The result is `O(T + I + O)` time with `O(M)` caller-owned scratch and no heap,
for tables `T`, input/output bytes `I`/`O`, and maximum table result `M`.
Malformed compressed data is transactional. Raw SFNT/TTC remains a bounded
pass-through, while WOFF2 is rejected explicitly in both managed and native
implementations. Microsoft symbol cmap precedence and `F000` remapping are
already matched. COLR version-0/CPAL matches the managed color-layer contract;
COLR version-1 is outside the current managed parity surface.

The first OpenType-SVG vector checkpoint ports ProGPU-owned
`PathGeometry.Parse`, `PathAtlas.CompileFillPath`, and resolved-arc bounds to
`progpu_native_svg_path.cpp`. The public C++20 header/module surface performs a
full requirements pass before writing caller-owned canonical path segments,
supports `M/L/H/V/Q/T/C/S/A/Z`, implicit and relative commands, figure closure,
and the shared WPF fill prefix, and rejects malformed input transactionally.
Work is `O(B + S)` time and `O(1)` internal storage for path bytes `B` and
segments `S`.

The next checkpoint ports the ProGPU-owned `OpenTypeSvgGlyphParser` element,
state, reference, shape, color, and gradient contracts into granular native
XML/value/layer files. It supports namespace-neutral `svg/g/defs/use`, a
64-reference-depth bound with cycle suppression, `path/circle/ellipse/rect/
polygon`, inherited transforms/fill/opacity/fill-opacity, CSS hex and the same
named-color subset, and linear/radial gradients with ordered stops and
pad/reflect/repeat spread. The public two-pass API is transactional and emits
canonical caller-owned layer, path, brush, and gradient-stop records directly;
it does not introduce an SVG renderer or retained XML graph. The cold parse is
`O(B + E + A + S + G log G)` time and `O(E + A + S + G)` temporary storage for
document bytes `B`, elements `E`, attributes `A`, path segments `S`, and stops
`G`; retained replay uses only the emitted pointer-free records. The native
fixture and `OpenTypeSvgManagedOracleMatchesNativeLayerFixture` assert the same
four-layer solid/linear/radial/reference result, transforms, bounds, opacity,
spread, and stop coordinates.

CFF2 container and variable-outline parity follows the WOFF1 checkpoint. It
ports the proven ProGPU CFF path writer and Type 2 evaluator behind an explicit
CFF2 execution mode while deriving format differences from the
[OpenType 1.9.1 CFF2 table contract](https://learn.microsoft.com/en-us/typography/opentype/spec/cff2)
and
[CFF2 CharString contract](https://learn.microsoft.com/en-us/typography/opentype/otspec190/cff2charstr).
The borrowed container validates the five-byte header, uint32 INDEX objects,
required TopDICT/FontDICT/PrivateDICT graph, optional FDSelect, FontMatrix, and
the exact length-bounded VariationStore. CFF2 execution rejects widths,
`endchar`, `return`, and removed Type 2 logic/storage operators, uses implicit
program/subroutine termination, and evaluates `vsindex`/`blend` from normalized
F2Dot14 coordinates. Active region scalars are computed once per selected
ItemVariationData into fixed 512-value evaluator storage. Opening is `O(F + V)`
for FontDICTs `F` and variation subtables `V`; decode is `O(B + S + A*R)` for
executed bytes `B`, emitted segments `S`, axes `A`, and active regions `R`,
with borrowed table storage, fixed parser/evaluator stacks, and caller-owned
transactional path output. Static, default-instance, peak-instance, malformed
operator, complete SFNT-container, LLVM named-module, and ASan/UBSan tests pass.

The first Unicode/shaping checkpoint ports the value-only records from
`src/ProGPU.Text.Shaping/ShapingContracts.cs` and the strict scalar-input
boundary used by `src/ProGPU.Text/OpenTypeTextShaper.cs`, both at source
checkpoint `3623a26c41d34e514a948a4694233e6514cf14a4`. The C++20 records retain
the managed field order and 4-byte layout for bulk transfer and future direct
GPU-plan upload. UTF-8 and UTF-16 use a validating count pass followed by a
transactional caller-span write pass; both are `O(N)` time and `O(1)` internal
storage for `N` input units. Invalid, overlong, truncated, surrogate, and
out-of-range encodings are rejected without replacement scalars or partial
output. Each decoded record preserves code point, original input offset and
unit length, Unicode 17 script tag, and canonical combining class.

`eng/generate-native-unicode-tables.py` derives the native script and combining
class tables from ProGPU's existing managed generated tables and the CI native
contract gate rejects stale output. This makes the already-generated managed
data the shared semantic source rather than introducing parallel handwritten
arrays. Script lookup retains ProGPU's `hira -> kana` and `laoo -> lao `
OpenType mappings; Common, Inherited, Unknown, and invalid scalars resolve to
`DFLT`. The architecture follows the official
[Unicode 17 Script Property](https://www.unicode.org/reports/tr24/),
[Unicode Text Segmentation](https://www.unicode.org/reports/tr29/),
[Unicode Normalization](https://www.unicode.org/reports/tr15/), and
[Unicode Bidirectional Algorithm](https://www.unicode.org/reports/tr9/)
boundaries. This checkpoint deliberately does not claim grapheme,
normalization, or bidi completion; those algorithms build on these exact
scalar records in subsequent slices.

Canonical FormD/FormC execution follows in the granular
`progpu_native_unicode_normalization.cpp` unit and directly consumes the same
`src/ProGPU.Text/UnicodeNormalizationData.bin` plan loaded by
`UnicodeNormalizationPlan.cs`. The native view validates the complete
little-endian header, record counts, scalar ranges, sorted keys, decomposition
spans, and composition pairs once while retaining only the caller's borrowed
bytes. Requirements reports exact maximum FormD scalar capacity. The write
pass expands fully decomposed sequences, performs stable canonical-class
ordering, and optionally compacts unblocked canonical pairs while preserving
the original input range. Validation and short-buffer failures are
transactional. Decomposition and composition lookup are `O(log R)` for `R`
records; ordinary ordered text is `O(D log R)` for `D` decomposed scalars, while
the allocation-free stable reorder has an explicit adversarial `O(D^2)` worst
case for one reverse-ordered combining sequence. The implementation uses no
locale, ICU, platform normalizer, or heap-owned table copy. Synthetic Latin
multi-step decomposition, reordered combining marks, canonical composition,
source-range merging, malformed-resource, and short-buffer cases use the real
shared 450,920-byte Unicode plan.

The same scalar unit now exposes an initial two-pass script itemizer matching
`OpenTypeScriptResolver.Infer`: Common/Inherited `DFLT` scalars attach to the
active preceding script, while leading Common/Inherited text adopts the first
following resolved script. It reports exact run count before writing
caller-owned `unicode_script_run` records and preserves both scalar and source
input ranges. Counting and writing are `O(N)` with `O(1)` internal storage;
invalid scalars and short output are transactional. This is intentionally the
managed first-strong shaping boundary, not a claim that Unicode
Script_Extensions or locale tailoring is complete.

The reusable OpenType execution boundary begins in
`progpu_native_open_type_layout.cpp`, porting ProGPU-owned
`OpenTypeTextShaper.FindCoverage`, `GetGlyphClass`, and lazy raw lookup access at
checkpoint `89d610c2`. It follows the official
[OpenType 1.9.1 common layout table formats](https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2).
Borrowed Coverage formats 1/2 and ClassDef formats 1/2 validate their complete
sorted arrays and disjoint ranges once, then answer in `O(log R)` time and
`O(1)` storage for `R` records. A shared GSUB/GPOS header view validates version
1.0/1.1 top-level arrays, then lazily validates each requested Lookup record,
all subtable offsets, flags, and optional mark-filtering-set index. This keeps
startup proportional to requested work and establishes one malformed-font
boundary for both substitution and positioning. Synthetic dense/range
coverage, class, mark-filter, subtable, sorting, and transactional failure
cases pass the focused native gate.

The header-compatible library compiles under the normal Clang/MSVC/GCC matrix,
is part of the Emscripten all-target build, and adds a real
`import progpu.native.text;` consumer to the LLVM Clang/Ninja named-module gate.
Focused synthetic tests cover SFNT metrics, BMP and supplementary cmap lookup,
TTC face selection, borrowed identity, invalid face indices, truncated
directories, invalid collection counts, explicit direct-view WOFF rejection,
and bounded WOFF1 normalization with compressed/uncompressed tables.

## Cross-engine research gate

The architecture was checked against primary sources for
[OpenType file organization and table-directory contracts](https://learn.microsoft.com/typography/opentype/spec/otff),
[OpenType character mapping](https://learn.microsoft.com/typography/opentype/spec/cmap),
[Skia shaping/SkParagraph](https://skia.org/docs/dev/design/text_shaper/),
[DirectWrite text formatting](https://learn.microsoft.com/windows/win32/directwrite/text-formatting-and-layout),
[HarfBuzz shaping plans](https://harfbuzz.github.io/shaping-and-shape-plans.html),
[HarfBuzz's glyph/rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html),
[Vello](https://github.com/linebender/vello),
[Parley](https://github.com/linebender/parley), and Firefox's
[WebRender architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html).
The adopted boundary keeps shaping and line layout as reusable retained results
while GPU rasterization, upload, batching, masking, and composition remain
device work. ProGPU adapts that boundary to its pointer-free scene ABI and
typed generation ownership. It rejects per-glyph native calls, character
remapping in the renderer, runtime reflection, unbounded caches, CPU texture
readback, and foreign source organization or implementation text.

## Delivered managed-picture bridge

`GpuPictureNativeSceneCompiler.Glyphs.cs` lowers already-shaped monochrome
`DrawGlyphRun` records to native outline/segment resources, positioned glyph
instances, and a deduplicated solid text-style page. Compilation is target-DPI
sensitive and explicitly records that dependency. It preserves transforms,
four-way subpixel placement, bold, italic, font stretch/skew, brush opacity,
and grayscale/aliased/ClearType selection. Color layers, embedded bitmap
glyphs, and vector fallback now lower at the same one-time revision boundary:
COLR/OpenType-SVG vector layers and explicit/CFF glyphs reuse retained native
paths/materials, while sbix/CBDT payloads reuse the managed decoder and metric
resolver before transferring tightly packed decoded RGBA8 records into the
native color atlas. Mixed presentation families preserve source order, and
repeated bitmap instances share one decoded resource. Compressed font bytes,
decoder state, path objects, and per-glyph calls never cross the native ABI.
Decorations and text-specific masks remain fail closed until their dedicated
lowering paths land.

For `G` positioned instances, `U` unique phase/raster outline variants, and `S`
outline segments, compilation is `O(G + S)` time with `O(U + G + S)` bounded
snapshot storage. Stable retained replay performs `O(G)` GPU instance work,
zero managed allocation, and zero repeat upload. The Apple M3 Pro/Metal matched
384-primitive picture now includes seven distinct outlines, 141 segments, and
18 bold positioned instances. Native/managed output differs by at most 2/255,
has zero pixels over 3/255, and has mean absolute channel difference
`0.000063175/255`; both replay paths allocate 0 B/frame.

## Parallel native implementation phases

### SFNT subset parity continuation

The native C++20 text library now directly ports the ProGPU-owned
`SfntFontSubsetter` glyph-ID-preserving path from checkpoint `edf3ea85`.
The caller-owned two-pass API retains glyph zero and the transitive closure of
TrueType composite dependencies, preserves source glyph IDs by leaving
unselected `glyf` entries empty, removes `DSIG`, emits long `loca`, and rebuilds
the SFNT directory checksums and `checkSumAdjustment`. This is cold font
preparation rather than a frame path: parsing, dependency traversal, and output
construction are `O(T + G + B)` for tables `T`, glyphs `G`, and copied bytes
`B`; stable rendering remains unaffected. The matched managed/native fixture
produces the same 272-byte font and FNV-1a signature
`10017802304682166674`, including one requested composite and its otherwise
unrequested component. The compact path also matches the managed oracle: it
builds ascending source-to-subset mappings, rewrites composite component IDs,
compacts `maxp`, `hhea`, `hmtx`, `loca`, and `glyf`, and removes stale cmap,
layout, vertical, color, math, signature, and post tables that would reference
the old glyph domain. Its matched fixture is 264 bytes with FNV-1a signature
`5117190155084041207`.

### SFNT metadata and canonical names

The granular `src/Text/Metadata` units directly port the ProGPU-owned
`SfntFontFace` name selection/decoding and `SfntFontMetadataReader` face-style
contracts from checkpoint `654fbd97`. The borrowed `sfnt_font_view` now exposes
the managed SFNT name IDs, exact caller-owned UTF-8 requirements and decode
passes, OS/2/head weight-width-italic resolution, and embedding rights. Name
selection preserves the managed platform/language score, canonical Latin-name
preference, first-record tie behavior, UTF-16BE/Latin-1/UTF-8 decoding, NUL
removal, and Unicode whitespace trimming. The selected record is scanned in
`O(B)` time and `O(1)` internal storage for `B` encoded bytes; the face search
is `O(R + B)` for `R` name records. A short destination remains untouched.
Managed and native fixtures cover localized Windows Arabic versus canonical
Unicode Latin selection, embedded NUL removal, trimming, English score,
OS/2/head style flags, embedding rights, missing IDs, and transactional output.
File discovery and fallback catalog policy remain host/provider concerns rather
than being embedded into this allocation-free byte-view layer.

The same metadata folder ports the managed glyph-resident `sbix` construction
path. A requirements pass validates at most 4,096 strikes and tables, resolves
each selected PNG/JPEG/TIFF record through at most 16 `dupe` links, and reports
exact standalone `sbix` and SFNT sizes. The write pass retains every strike,
empties non-selected glyph entries, preserves the referencing glyph's origin,
rebuilds the selected `sbix` checksum, and copies all other source tables into
an aligned standalone face. Work is `O(T + S * D + B)` for tables `T`, strikes
`S`, duplicate depth `D <= 16`, and copied bytes `B`, with `O(1)` internal
storage and one caller-owned snapshot. Short outputs remain untouched. Native
tests reopen the resulting font, select both direct and duplicate glyph data,
verify that unrelated glyphs are absent, and run under the module, sanitizer,
and browser compiler gates; the established managed resident-font tests remain
the behavioral oracle.

The generic standalone-face path directly ports
`SfntFontFace.CreateStandaloneFontData` from checkpoint `f21d5cbf`. It extracts
one borrowed SFNT/TTC face into a caller-owned, four-byte-aligned SFNT snapshot,
preserves checksums and table bytes, sorts the directory by tag, selects `OTTO`
for CFF/CFF2 faces, and matches the managed parser's last-valid-record-wins
handling for duplicate or invalid directory entries. Requirements and write
passes are transactional; a short output or scratch span leaves the output
untouched. Directory discovery is `O(T^2)` worst case with `O(1)` internal
storage for at most 4,096 source records, sorting is `O(U log U)` in the
caller-owned scratch for `U` retained unique tables, and copying is `O(B)` for
`B` output bytes. This cold font-boundary operation does not affect steady
shaping or rendering paths.

The granular `src/Text/Font` policy layer directly ports the variable-style
mapping in `FontManager.ApplyStyleVariations` from checkpoint `885f58c0`.
Normalized requests map weight, width class, italic, and slant onto recognized
`wght`, `wdth`, `ital`, and `slnt` axes while unrelated axes retain their
defaults. The result carries exact signed 16.16 user coordinates plus the
existing `fvar`/`avar` normalized coordinate, with no float conversion at the
interop boundary. Requirements fully validate recognized axes before the
caller-owned output is touched. Work is `O(A^2 + A*M)` with `O(1)` internal
storage for the cold style-instance boundary, where `A` is the small font axis
count and `M` is the selected `avar` map; shaping and replay remain unchanged.
Synthetic four-axis fixtures cover all managed mappings, and production Inter
confirms the exact `wght=700` user coordinate and normalized value `8847`.

Vertical text initialization now directly ports the ProGPU-owned
`TtfFont.GetAdvanceHeight`, `GetVerticalOriginY`, `TryGetVorgOrigin`, and
`GetTrueTypeVerticalOrigin` policy from checkpoint `f56a73cd`. Borrowed
`vhea`/`vmtx` records provide per-glyph vertical advances and bearings, `VORG`
uses bounded binary search with its default origin, and TrueType bounds provide
the managed centered fallback. Native top-to-bottom and bottom-to-top shaping
now starts with the same negative vertical advance, horizontal centering, and
vertical-origin offset instead of silently emitting horizontal metrics. Table
and metric lookup is `O(T + log V)` for `T` directory records and `V` VORG
records with `O(1)` storage; the normal per-glyph shaping loop remains `O(G)`.
Synthetic vertical tables cover explicit/default VORG, long-metric fallback,
centered fallback, both vertical directions, and transactional borrowed views.
Right-to-left and bottom-to-top runs also apply the managed final visual-order
reversal, including monotone-character combining-run cluster restoration.

Horizontal advance initialization now directly ports the ProGPU-owned
`TtfFont.GetAdvanceWidth` policy from checkpoint `4e6ff74d`. A borrowed face
with no usable `hmtx`/long-metric count returns the same half-em fallback
instead of rejecting shaping; normal faces reuse their last long metric and an
explicit native variation instance applies the existing HVAR delta. Horizontal
shaping retains managed away-from-zero rounding, while vertical centering uses
the managed midpoint-to-even rule before integer halving. Each query is
`O(T + V)` time for `T` directory records and the bounded HVAR variation-store
work `V`, uses `O(1)` storage, and adds no interop crossing or allocation.
Synthetic missing-metric and vertical-shaping cases plus production variable
font coverage protect the shared metric path. The bounded scratch overload now
also directly ports `TtfFont.Variations.GetVariationAdvanceDelta` and
`ComputeGlyphVariationItemCount` from checkpoint `5abc583db`: HVAR remains the
preferred source, while fonts without a usable HVAR store evaluate the already
ported raw `gvar` left/right phantom points. The requirements pass reports the
exact tuple, region, point-number, and delta spans; execution is transactional,
allocation-free, and `O(T * (A + D))` for tuples `T`, axes `A`, and decoded
deltas `D`. Synthetic simple-glyph coverage resolves base advance `600` plus
phantom delta `3`, rejects short scratch without publishing a partial result,
and production Inter confirms that HVAR still wins without phantom scratch.

Fallback mark placement now has a direct allocation-free native port of the
ProGPU-owned `GlyphPositionBuffer.ApplyFallbackMarkPositioning` algorithm from
checkpoint `2b871936`. The caller-owned transient metadata span preserves
prior-positioned marks and ligature component/count state without expanding or
forking the stable 32-byte shaped-glyph wire record. The implementation keeps
the same modified-combining-class recategorization, top/bottom/left/right and
center alignment, one-sixteenth-em vertical gap, same-class stacking,
directional advance compensation, component subdivision, and unsafe-boundary
flags. Work is `O(G * T)` over `G` glyphs and `T` borrowed face tables with
`O(1)` internal storage and no allocation. Synthetic stacked, prior-positioned,
ligature-component, and failure-transaction cases cover the direct API. The
same stage is now integrated into ordinary full-run shaping: GSUB carries
bounded ligature count/component metadata in private flag bits, GPOS records
which glyphs it positioned in the caller-owned attachment scratch, and the
shaper strips the private bits before returning the stable public glyph span.
The integrated Latin mark fixture covers managed advance zeroing and fallback
offsets; low-level GSUB/GPOS fixtures cover the transient metadata boundaries.
The adjacent glyph-bounds slice now adds a general caller-scratch outline
query that selects static TrueType bounds or decodes the already ported
active-`gvar`, CFF1, and CFF2 path sources before applying the same conservative
control-point envelope and integer rounding as the managed geometry path. Its
requirements pass reports the exact point, path-segment, and varied-outline
scratch needed by execution. A second fallback-mark overload consumes that
scratch together with the full HVAR/phantom-point advance scratch, so direct
fallback placement is variation- and CFF-aware without native allocation.
Ordinary full-run shaping intentionally continues through its bounded legacy
scratch route by default. Its run scratch now accepts an optional synchronously
borrowed `fallback_mark_positioning_scratch` pointer; when supplied, both run
advance initialization and fallback mark positioning reuse the exact phantom
advance and outline-bound buffers, upgrading active `gvar`, CFF1, and CFF2
behavior without any per-glyph allocation. The pointer and every nested span
remain caller-owned and are never retained. Production Inter static/variable
and Noto CFF fixtures cover source selection and successful bounds reduction,
while the synthetic mark fixture covers equivalent direct and full-run output
through the extended scratch path.

Arabic `stch` parity now has its allocation-free native expansion kernel,
directly ported from the ProGPU-owned managed `ApplyArabicStretch` stage at
checkpoint `c483e175`. Multiple substitution can opt into private multiplied
and component-index metadata without changing the stable 32-byte glyph wire
record. The exact .NET 10 Unicode general categories used by managed word
context discovery are generated into deterministic C++20 ranges and verified
by the native contract gate. A requirements pass computes the exact output and
run capacities; execution uses caller-owned run scratch and expands backward
in place, preserving direction, fixed/repeating widths, overlap distribution,
the 256-glyph-per-run cap, the 1,048,576-glyph global cap, and unsafe break and
concatenation flags. Work is `O(G log C + G * T)` for glyphs `G`, generated
category ranges `C`, and borrowed font tables `T`; internal storage is `O(1)`.
The full-run shaper now executes `stch` as its own first Arabic substitution
stage, excludes it from generic lookup replay, converts MultipleSubst component
parity into fixed/repeating actions, retains those actions through positioning
and directional reversal, expands after reversal, and clears every private bit
before publishing output. Callers provide the optional run span on
`open_type_shape_run_scratch`; runs without `stch` require no extra storage,
while a stretching run fails explicitly with `insufficient_buffer` when either
its exact run scratch or final glyph capacity is absent. A synthetic Arabic
GSUB fixture covers the complete substitution-to-expansion path in addition to
the direct kernel and transactional capacity tests.

Legacy kerning fallback now directly ports the ProGPU-owned
`GlyphPositionBuffer.ApplyLegacyKern` policy from checkpoint `34b76eeb`.
The adjacent `sfnt_font_view::try_get_design_kerning` query separately mirrors
the narrower public `TtfFont.GetKerning` format-0 contract in design units.
Native shaping reads Microsoft and Apple `kern` table headers, format 0 sorted
pairs, format 2 class tables, horizontal and cross-stream adjustments, GDEF
mark skipping, signed odd-value splitting, clamped metrics, and unsafe-break
dependencies. A selected GPOS `kern` or `dist` feature suppresses the fallback
exactly once, while an explicitly disabled run-level `kern` baseline performs
no legacy work. Table traversal is `O(S * (G log P))` for `S` format-0
subtables, `G` glyphs, and `P` pairs; format-2 lookup is `O(S * G)`. Storage is
`O(1)`, all table/glyph spans remain borrowed, and no managed/native crossing
or heap allocation is added per pair. Synthetic fixtures cover Microsoft and
Apple headers, both formats, cross-stream positioning, GPOS suppression, GDEF
mark skipping, explicit disable, and negative odd adjustments.

OpenType feature discovery now directly ports the ProGPU-owned
`OpenTypeTextShaper.GetFeatureTags`/`AddRawFeatureTags` behavior from checkpoint
`064260fe`. The native two-pass API walks borrowed GSUB and GPOS feature
records, tolerates truncated trailing records like the managed reader,
deduplicates tags across both tables, and writes the same ordinally sorted
union into caller-owned storage. Exact sizing is `O(F^2)` time and `O(1)`
storage for `F` feature records; population is `O(F * U + U log U)` for `U`
unique tags with no heap allocation. Synthetic fixtures cover cross-table
duplicates, sorting, missing tables, malformed trailing declarations, and
transactional insufficient output.

Caret selection and visual movement now directly port the ProGPU-owned
`TextLayout.GetCaretStop` and `MoveCaretVisually` rules from checkpoint
`288e9f74`. The native APIs scan the already built physical-order caret span,
preserve the managed nearest-logical-position and affinity tie break, then move
by one clamped visual index across mixed LTR/RTL lines. Each query is `O(C)`
time and `O(1)` storage for `C` caret stops, performs no allocation, and adds no
managed/native crossing when used inside the native editor/layout path.
Synthetic mixed-direction wrapped-line fixtures cover leading/trailing
affinity, forward/backward motion, endpoint clamping, and empty input.

1. Freeze bounded native byte ownership and provenance for SFNT/container,
   table-directory, metrics, cmap, and outline access.
2. Port TrueType/CFF, variation, bitmap/color, and SVG glyph data paths with
   malformed-font property/fuzz coverage.
3. Port Unicode decoding, grapheme/script/language itemization, bidi, GSUB,
   GPOS, and GDEF while differentially comparing glyph IDs, clusters,
   advances, and offsets against the authoritative C# implementation.
4. Add native fallback/provider seams for desktop, mobile, and browser without
   external runtime dependencies.
5. Port wrapping, trimming, caret/selection geometry, hit testing, and reusable
   positioned-run caches.
6. Connect native shaped runs directly to the standalone C++ retained-scene
   compiler and existing compute glyph/text pipelines. Native shaping, run
   assembly, decoded outlines, retained atlas resources, and the shared
   glyph/text shaders are connected without a per-glyph interop call.
7. Gate cold start, first interaction, sustained layout/shaping throughput,
   allocations, cache residency, malformed input, DPI/subpixel quality,
   browser AOT, and matched C#/C++ screenshots before claiming parity.

At checkpoint `82da561e`, warning-clean Apple Clang and LLVM 22 named-module
builds pass the complete native text suite. The production Inter qualification
reuses one parsed face and one caller-owned shaping plan for 1,024 runs and
matches the managed glyph ID, cluster, advance, offset, and dependency-flag
signatures for kerning (`AVATAR`), contextual substitution (`office AV`),
automatic Unicode fractions (`1⁄2`), and independent ligature/kerning feature
ranges. The native test suite also covers transactional malformed SFNT, cmap,
GSUB, GPOS, WOFF, CFF, variation, bitmap/color/SVG, Unicode, fallback, wrapping,
and interaction paths. Direct retained text rendering remains covered by the
matched GPU screenshot result above; final cross-platform release CI and
manual sample inspection remain PR-level gates rather than text-port gaps.
