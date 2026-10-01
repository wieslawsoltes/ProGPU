# Native font hinting

Display-mode text requires one selected hinted generation for fitting, placement,
interaction and raster outlines. Exact `hdmx` widths alone do not supply that
generation. The implementation uses a pinned external FreeType library through
its public API, without importing or adapting its implementation into ProGPU.

## Dependency ownership

`eng/native-freetype.json` pins FreeType 2.14.3's official repository, annotated
release object, resolved commit, original author and signing fingerprint.
`eng/progpu-prepare-freetype.py` requires a fresh explicit workspace outside the
repository. It never resets an existing checkout, edits a system library/package
cache or overwrites an existing payload. Its GnuPG keyring is task-owned; no user
keyring is changed. A bad, revoked, expired, missing or unexpected signature fails
before checkout/build. No weak-signature override is supplied.

Only a static Release/PIC library is produced, with optional compression, PNG,
HarfBuzz and Brotli dependencies disabled. Every archive object must independently
match the requested platform and architecture; directory names and compiler
success are insufficient. Fat, bitcode, hybrid ARM64EC and empty archives reject.
Unix/macOS/Windows targets retain the same source and public API.

The installation receipt includes source identity, actual library SHA-256,
verified object count, configure command and original-notice hashes. Original
FTL/dual-license documentation, contributed-driver notices and leading copyright/
license comments are preserved separately from implementation. This software is
based in part on the work of the FreeType Project. Runtime/package integration must
carry these notices and the credit, rather than assuming build-time provenance is
redistribution completeness.

The preparation controls run inside the existing Linux Build job. The six existing
native renderer jobs also execute the real signed-source producer, independently
verify every static object, and link a small original public-API probe to that exact
archive (no ambient `find_package` or system-library substitution). The probe
requires matching 2.14.3 headers/runtime, interpreter 35 and 40 availability, two
independent library policies and nonmutating rejection of an invalid policy. It
does not load a font, execute glyph instructions or render pixels. Artifacts retain
the static library, headers, original notices and complete receipt; they are not
native product/runtime packages. Actual producer evidence still requires a whole
successful exact-head Build, not one green architecture or a canceled producer.
Library version alone is not actual dependency identity; native integration must
also keep FreeType symbols private to the owning renderer and reject interposition.

## Remaining integration

Dependency preparation is not hinted-font execution or application qualification.
The native font adapter now owns immutable original font bytes, exact face index,
26.6 device-em request and fractional phase, interpreter policy and original-order
16.16 variation coordinates. Its native face and library end before their memory
font. Variable TrueType fonts require all axes explicitly within their ranges;
named-instance encodings, CFF/CFF2, color and bitmap-only fonts remain unadmitted,
rather than silently substituting a base instance or bitmap. That is an explicit
remaining compatibility requirement, not full source Display support.

One serialized native batch captures every original glyph's advances, all metrics,
linear advances, bearing deltas, outline flags, points, tags and contour ends from
the same glyph slot before another load overwrites it. Retained immutable batches
keep their exact original font/size/policy identity after adapter disposal. SIMD
validates original unsigned IDs on admitted x64/ARM64 targets; geometry uses exact
native signed-long bulk copies, without float rounding. The private C++ types are
not a wire ABI. No managed per-glyph crossing is added. Any later invalid ID,
native hint fault or allocation failure leaves the candidate unpublished and the
previous batch unchanged; descriptors and input order, including repeated glyphs,
remain intact.

Each identity also retains the actual selected native size frame: units per em,
integer x/y ppem, exact x/y 16.16 scales, driver-wide 26.6 metrics and separate
design-unit face metrics. These values come from the selected face after variation
and size setup, not an em/UPM approximation. The public
[FreeType size contract](https://freetype.org/freetype2/docs/reference/ft2-sizing_and_scaling.html)
distinguishes rounded ppem, device scales and historically rounded global metrics;
the latter are not exact glyph bounds or an application line-height policy.
Independent face controls compare every field for both interpreters, nonuniform
fractional sizes, phase changes and retained snapshots after adapter disposal.
The producer receipt requires these controls on every RID. Capturing this frame
alone does not qualify its subsequent shaping, fitting, interaction or GPU use.

The private design-vector projection now consumes those retained scales in one
allocation-free span operation. Automatic selection uses baseline SSE2/ARM64 NEON;
forced SIMD and explicit scalar-reference policies report their selected path.
Unsupported policies and forced GPU paths fail closed: these CPU-visible values
are inputs to native positioning, so a GPU pass would add prohibited readback.
This is not a renderer compute fallback. The scalar oracle calls the actual
public `FT_MulFix`; the original SIMD implementation uses widened magnitude
products, integer rounding and sign restoration, with at most one vector tail.
Its public arithmetic/domain reference is
[FreeType computations](https://freetype.org/freetype2/docs/reference/ft2-computations.html).
No foreign implementation text or structure was consulted or imported.

Both scale operands and rounded outputs must fit signed 32-bit arithmetic even
where native `long` is 64-bit. Exact integer bounds validate every SIMD lane
before the first output write. Complete output capacity may not alias input or
any retained source/configuration/glyph storage, including the device frame.
Output tails and all outputs on later invalid values remain unchanged. Authored
controls compare automatic/forced SIMD against the public scalar oracle for all
lengths 0–17, both signs and half ties, extreme values, nonuniform selected scales,
post-disposal generations, every invalid lane/tail and both native-long widths.
Every producer receipt requires these controls; current-head execution remains
subject to whole hosted CI. Work is O(N + G), N vectors and G retained glyphs
for the dependent ownership-range walk; auxiliary storage is O(1). No performance
claim is made. This displacement primitive adds neither origin/phase nor a late
advance substitution and does not yet connect source Display shaping/layout.

GPOS contour anchors now have a private retained-point accessor. It selects the
original positioned descriptor and contour index, not the first matching glyph
ID, and subtracts only that generation's explicit native phase. The
[OpenType GPOS AnchorFormat2 contract](https://learn.microsoft.com/en-us/typography/opentype/spec/gpos)
uses the hinted contour point for final device positioning. Cursive and mark
calculations need origin-relative points; copying phase into an advance would
be incorrect. Signed-32 positioning bounds are proven before subtraction even
when captured native `long` coordinates are 64-bit. Missing points, invalid
phases and unsupported coordinates fail explicitly, without clamping or a design
coordinate fallback. Status and coordinates return by value; no caller/source
buffer is modified. This is one dependent indexed metadata access, O(1) with no
allocation, font execution or GPU work, not a whole-buffer scalar pass.

Independent face controls compare every actual contour point for both policies,
phase changes, fractional sizes and snapshots after adapter disposal. Separate
unpacked descriptors with repeated IDs but different points reject ID-based
substitution; out-of-range/empty/extreme/invalid-phase controls remain separate.
The producer receipt requires retained-anchor evidence on every RID. The private
owned shaping path below connects GPOS device values, attachment arithmetic,
initial hinted advances and fallback geometry to this same batch. This accessor
alone does not enable Display or qualify source layout/interaction/raster.

The producer builds this original adapter and executes focused controls against
the host's actual Arial (Windows/macOS) or DejaVu Sans (Linux), without packaging
those fonts. Controls compare every retained metric/point/tag/contour/flag to a
separate public-API FreeType face, require an actual hinted-versus-unhinted
difference, retain caller mutation/disposal independence, exercise every SIMD lane
and bounded tail, invalid configuration publication, phase translation and empty/
repeated batches. The receipt records the actual test-font hash. These controls
also cover concurrent captures from one live owner and a nonuniform fractional
device-em request, without disposing the owner during a borrowed operation.
These controls are adapter/dependency evidence, not an independent Windows Display oracle or
GPU/application qualification. An original three-glyph SFNT fixture adds a valid
explicit grid-fit program and a later empty-stack `POP` program on the same valid
contour. A separate FreeType face proves that the faulty glyph parses/scales with
hinting disabled and fails only under pedantic native execution. Both interpreters
must preserve the earlier batch through repeated later-glyph faults and recover a
following valid capture. No production fault hook or error-ignoring flag is added.
Variable-instance controls remain required alongside subsequent integration.

## Immutable sources and bounded cache

The actual native primary/fallback text contexts now retain one shared immutable
`owned_font_source` containing original bytes and the original collection face
index. The fallback-palette index is not the collection index, and a parsed table
offset is not a replacement for either. Selection borrows the existing context
use lease, then returns a source owner that survives fallback-vector growth and
context disposal. Context creation holds its candidate under RAII through font/
normalization allocation and validation, so a failed construction releases it.
The native device-advance fixture now contains two independently authored TTC
faces with different device widths and reversed primary/fallback selection;
caller mutation, vector growth, exact source index and post-context lifetime are
checked without enabling hinting or altering the public context ABI.

The hinted adapter accepts that immutable source directly. Multiple device/
phase/policy configurations share the same original bytes, instead of cloning a
memory font for each native face; existing span creation still owns its input copy.
Source members are const, and every retained generation owns the source until all
faces and snapshots have released it. Transport preflight protects both the shared
source metadata and its bytes, in addition to configuration/batch storage.

`hinted_font_cache` retains at most 16 exact source/configuration entries with
bounded FIFO replacement. Each entry keeps one native face and its latest exact
ordered glyph-ID batch. Configuration matching includes both device-em values,
both phases, interpreter and every original-order variation coordinate. Source
identity is shared immutable ownership, not a borrowed mutable pointer or hash.
A warm identical sequence returns the same generation without cloning buffers or
running glyph instructions; source repetitions/order remain part of the key.
Changing glyphs captures before swapping either the retained key or generation.
Cold configuration creation/capture must complete before evicting anything.
Native faults preserve both the caller's prior output and the existing cached
batch. Evicted snapshots retain their source and remain transportable.

Cache mutation is serialized, including actual concurrent capture controls; it
does not authorize unleased context access or concurrent context disposal. The
existing creating-thread context lease remains required. Diagnostics separately
count actual face creations, capture attempts, batch hits and live entries, with
saturating counters; they do not claim application performance or GPU residency.
Lookup is O(K + A + G), K <= 16 entries, A variation coordinates and G ordered IDs;
warm auxiliary allocation is zero by construction, not measured application
qualification. Cold capture retains the dependent instruction/geometry cost;
cache-owned storage is O(K * (A + G + P + C)) plus shared original font sources.
Actual source layouts may independently retain older generations beyond the cache.

The isolated signed producer executes new original cache controls for both hint
policies: warm reuse/concurrency, different immutable owners, all size/phase/policy
key components, reordered/empty IDs, failed native glyph/configuration publication,
bounded eviction and generations surviving cache/source-owner disposal. Its receipt
requires `cacheProbe` evidence. The prior source/cache head passed all six native
jobs, but its whole exact-head Build remains pending. Product linking described
below is additional work and requires its own exact-head evidence.

## Explicit product linking and redistribution

`PROGPU_NATIVE_FREETYPE_MANIFEST` and its exact target RID explicitly select the
prepared archive; an omitted manifest keeps the original dependency-free path.
The read-only admission helper revalidates the signed source identity, actual
Release/static/PIC build configuration, every installed header, every archive
object, all producer controls and original notices. Paths are canonical and
cannot redirect through symlinks or build-tool argument delimiters. No ambient
FreeType lookup or foreign source patch is added. All six native CI jobs now pass
their own producer receipt explicitly into ordinary product builds; original
CTest, sanitizer, GPU, sample and package gates remain unchanged.

Both renderer contexts retain a lazy private `hinted_font_cache`. The internal
capture helper selects the exact immutable primary/fallback source under the
existing context use lease; no ordinary shaping call allocates a hinted face.
An absent dependency fails explicitly without replacing the caller's prior batch.
New ordinary CTest controls exercise both interpreters, actual context/fallback
growth, native-fault atomicity, warm reuse, eviction and transport after context
retirement. They test the static text core, not a loaded shared product font ABI.
The additive retained C entrypoints described below do not change source Display
admission or ordinary shaping.

Dependency symbols remain hidden at linkage: ELF uses the exact archive names
with `--exclude-libs`; Apple uses the absolute archive with `-load_hidden`.
Windows does not export the static dependency. The native adapter verifies that
all selected public FreeType function addresses belong to its own executing
image before any library/font creation; a version match cannot substitute for
module ownership. This uses borrowed Windows module handles or PIC/dladdr
identity, with no unload or global loader mutation. Export controls also reject
FreeType-prefixed symbols independently of their symbol type. Actual shared
product execution and negative/interposition controls still remain required.

Runtime staging requires the product CMake cache to select that exact receipt
and RID. Fresh SDK targets receive the verified archive, relative metadata,
original legal documents and FreeType credit; existing dependency targets are
never overwritten. Regular CMake installs carry the same archive and notices.
SDK imported targets verify the reviewed pin, target RID, archive and notice
hashes and retain transitive private linkage. Pre-NuGet admission independently
rechecks every staged archive's architecture and hash and every original notice;
one partial dependency requires all six RIDs. Native package CI explicitly
requires the dependency, so removing all markers cannot silently bypass it.
These implementation controls are authored; their current-head CI is not yet
evidence of successful redistribution or application qualification.

Applicability: primary/fallback ownership lives in the existing shaping interop
source shared by both native renderer libraries and called by the managed native
text providers. The public context signatures, shaping results, device-width
semantics and managed context use scope are unchanged; their existing independent
consumer gates remain. The private hinted cache does not yet alter either raster
provider or the managed Ideal path. Display-generation consumers still require
paired integration rather than declaring those implementations inapplicable.

The fixed-width transport helper is now compiled into the real native text core
as well as the isolated producer. It copies an already retained generation into
caller-owned glyph, signed 64-bit point, tag and signed 32-bit contour buffers.
All 14 metrics, descriptor order/repeats, flags, high tag bits and glyph-local
contour ends remain exact. Requirements validate complete contour topology and uint32
aggregate bounds before publication; buffer preflight rejects misalignment,
overflow, output overlap including unused capacity, and aliases with any retained
batch/font storage. Status and selected execution path return by value so they
cannot overwrite caller tails or source bytes through an aliased output reference.
On failure all caller buffers and requirement counts remain unchanged.

Automatic transport bulk-copies native 64-bit coordinate pairs, or uses exact
signed 32-to-64 SIMD widening on Windows. Contour widening uses independent
eight-lane NEON/SSE2 blocks with a bounded tail. Forced SIMD and scalar reference
paths are explicit; unsupported forced SIMD fails rather than silently falling
back. The CPU-owned boundary transfer performs no font execution, GPU work or
allocation. Its prefix offsets and contour ordering are genuinely dependent;
coverage rasterization stays on the existing GPU paths. Time is O(G + P + C),
with O(1) auxiliary space, excluding the caller-owned outputs.

Focused hosted controls exercise every length zero through 17 on all paths,
signed native-long extremes, exact metrics/tags/flags, repeats/non-ink descriptors,
short buffers, later invalid topology, aliases and all successful/failure tails.
The same transfer controls also consume actual host-font batches for both hint
policies, including after adapter disposal. They run in ordinary native CTest
and in the isolated six-RID producer, which requires explicit transport evidence
in its receipt. These controls are authored, not locally executed. The C records
now have additive retained entrypoints. Generated managed consumers and source
capability admission still require the same generation contract.

## Retained C generation ownership

`progpu_native_text_context_capture_hinted_batch` borrows one exclusive live
context use lease, one explicit request and the complete original-order glyph-ID
batch. It publishes an independently owned immutable handle only after native
capture succeeds. Failure leaves the prior handle unchanged; the caller remains
responsible for releasing any earlier handle after a successful replacement.
Device-em, fractional phases, interpreter 35/40 and every original-order 16.16
variation coordinate remain exact. Unknown ABI/size/reserved/policy values,
misalignment, overflow and publication aliases with input/context/font storage
reject before native execution. An unavailable build returns Unsupported, not
Ideal or a bitmap. Original `fvar` axis count is bounded by its uint16 wire field.

The handle retains its generation through later cache mutations, eviction and
context destruction. Counts and atomic copy operate only on that immutable
generation; callers own a use lease that excludes concurrent handle destruction.
Copy reuses the fixed-width bulk/SIMD transport, including full-capacity overlap,
source/handle alias rejection and untouched success/failure tails. It performs no
allocation, font execution or GPU work. Null destruction is permitted. Capture
adds one owned handle allocation, including a warm generation hit; no per-glyph
managed crossing or performance claim follows from cache reuse.

Ordinary context CTest controls now exercise the actual C capture/count/copy/
destroy boundary for both interpreters, later native instruction faults, invalid
request/output aliases, insufficient/overlapping output buffers, raw scalar
reference equality and snapshots after context destruction. Both renderer export
allowlists retain the four additive symbols. Browser/default builds expose the
same boundary but explicitly reject unavailable capture. These controls are
authored; actual shared-library and package consumers remain required.

The existing managed contract generator adds exact `int64_t` -> `long` support
and owns marked hinting records. A read-only hosted generation workflow emits
the original generator outputs with exact producer commit and input-header hash;
no local build or handwritten generated output is used. Hosted run36744475000
succeeded at730822ff04555f0c9670a0873fe9ac54bbdb9987. That run's header SHA-256
`d7b0be1416a02e18fa9a228ed1a80048f668b141aae8c0607f795b1867864fbe`
records the original capture-only header; every previous generated contract was
byte-identical. The later opaque shaped-run entrypoints change that header but
introduce no marked record or wire-layout change. `NativeTextHintingContract.g.cs`
is imported byte-for-byte from that successful generator artifact, and the
ordinary verifier continues checking it against the current public records.
Generator provenance is not native product/runtime qualification.

`NativeTextShapingContext.CaptureHintedBatch` retains the existing context use
through native capture and managed ownership construction, with paired raw-handle
release on construction failure. `NativeHintedFontBatch` retains immutable counts
and a separate monitor-backed handle owner for each whole atomic copy. Finalizer/
explicit disposal cannot release a borrowed pointer; snapshots survive context
disposal/cache eviction without keeping the mutable context live. Generated
records preserve all 14 signed 64-bit metrics and exact geometry, not floating
approximations. Public values ending in266/1616 denote the corresponding fixed-
point units under the existing generator naming convention. A captured generation
still does not attach itself to ordinary source layout or rendering.

Authored managed controls independently check fixed layouts, every metric offset/
signed type, integer extremes and purpose-correct disposal leases without loading
fonts. The existing source-independent package consumer also retains all prior
device-advance checks and adds actual loaded-library hinted generation controls
for both interpreters: exact original order/phase, complete raw warm equality,
short/overlapping buffers and untouched tails, cache eviction, concurrent copies
and post-context/batch disposal. The existing JIT/NativeAOT package processes and
deadlines remain intact; no new selector replaces an original independent case.
These product consumers are authored, not yet qualified by current exact-head CI.

The adapter is explicitly linkable into the product text context and both
renderers through the bounded cache. Leased managed transport below uses those
generated wire records. Subsequent consumers must retain original shaping
identities and share one generation across actual layout, interaction and drawing.
Linking alone does not prove that either renderer or a source application has
consumed a hinted generation.
No sampled width, isolated suffix reshape, per-glyph managed crossing, bitmap
substitution or Ideal coercion admits source Display mode.

Managed/native shaping, continuation, caret geometry, both raster providers and
the WPF source must consume the same generation before its existing guard changes.
Native package/NativeAOT and independent Windows source comparisons remain required.
The preparation controls and library-policy probe are authored, not locally
executed. Hosted CI runs them; no local dependency build, glyph/font execution,
GPU run or VM was performed during implementation.

Public references: [official downloads](https://freetype.org/download.html),
[glyph slot lifetime and hinted metrics](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html),
[driver/interpreter properties](https://freetype.org/freetype2/docs/reference/ft2-properties.html),
and [upstream licensing](https://freetype.org/license.html).
Module/linkage contracts follow the
[Apple linker manual](https://github.com/apple-oss-distributions/ld64/blob/main/doc/man/man1/ld-classic.1),
[GNU linker options](https://sourceware.org/binutils/docs/ld/Options.html),
[dladdr PIC caveat](https://man7.org/linux/man-pages/man3/dladdr.3.html), and
[borrowed Windows module query](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw).
The library-policy probe follows the public
[module property contract](https://freetype.org/freetype2/docs/reference/ft2-module_management.html)
and does not copy upstream implementation or its example code. All existing
shaping/layout, glyph/atlas, upload, GPU execution and source defaults remain
unchanged by the private source/cache implementation; application performance and independent
Windows Display comparisons are still unqualified.

## Architecture research and decisions

### Native device-frame positioning

The private GPOS executor can now borrow one retained hinted batch and its exact
original SFNT view. `bind_hinted_gpos_frame` checks immutable byte-owner identity,
collection face and units per em, selects the existing SIMD or explicit scalar
projection, and retains actual captured ppem rather than caller estimates. The
caller must keep both borrowed objects alive throughout the synchronous pass.
The frame also borrows the same immutable normalized-coordinate span for the
whole pass. Binding requires every original fvar axis in both raw signed16.16
and normalized F2Dot14 form, checks the existing native fvar/avar normalization,
and rejects missing/different axes. Device GPOS admission rejects a different
normalized span's values or count before glyph publication. This source-instance
check does not yet qualify actual variable-font hint/layout parity.
Public GPOS options, entrypoints and their design-unit behavior remain unchanged;
the device frame is an explicit private capability, not automatic Display admission.

Value records combine design coordinates and VariationIndex deltas before one
projection. Packed Device corrections remain exact integer pixels converted to
26.6 after projection, never a rounded design-unit round trip. Anchor format 1
projects original coordinates; format 2 reads the original source descriptor's
hinted contour point with phase removed and verifies its glyph ID; format 3 keeps
design/variation and pixel corrections separate. Nested/contextual and extension
lookups carry the same frame through the existing native lookup walker. These
contracts follow the public [OpenType GPOS specification](https://learn.microsoft.com/en-us/typography/opentype/spec/gpos);
no external positioning implementation was imported.

Independent four-lane additions use SSE2/ARM64 NEON with checked signed32
overflow, paired with an explicit scalar-reference policy. Pair and cursive
operations stage both affected glyphs before publishing them. Attachment graph
recurrences remain dependent native work, but the device resolver rejects
overflow instead of invoking the legacy resolver's signed32 clamp. Callers still
need whole-run scratch before immutable generation publication: an unsuccessful
later lookup or graph node does not roll back earlier successful work.

Authored controls retain the original design-unit tests and add both arithmetic
policies, all anchor formats, packed pixel corrections, pre-projection variation
sums, nested lookup propagation, pair failure atomicity, all four cursive
directions and both attachment flags, cycles, overflow and untouched tails.
Owned-context controls use the real hinted generation under both interpreters,
including after font/context retirement, and reject copied unrelated owners and
unsupported execution policies. Hosted native CI, not local builds or execution,
must qualify these controls on every target.

The private device-run executor shares the original shaping pipeline through
normalization, initial mapping, GSUB, default ignorables and complex preprocessing.
Its preparation seam captures the final substituted descriptors before initial
metrics, mark zeroing, fallback geometry or GPOS. Original public design-unit
entrypoints and ordinary source-layout defaults remain unchanged:

- `initialize_hinted_run_metrics` checks all original ordered glyph IDs and
  coordinate domains before any write; publishes captured horizontal advances,
  or negative vertical advances and original horizontal/vertical bearing-derived
  origin shifts, with SIMD/reference copies. It preserves source indices, styles,
  flags and complete caller tails, rejecting immutable-generation aliases and a
  later invalid descriptor. The explicit source-prefix overload leaves trailing
  auxiliary capture descriptors and complete output tails untouched; the original
  overload still initializes the entire captured batch. It performs no font
  execution, shaping or GPU work.
  These are initial metrics before mark zeroing, space fallback or GPOS, never a
  replacement for already-positioned advances. Vertical bearings follow the
  [public FreeType slot contract](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html);
  private synthesized metrics are not source vertical-writing admission.
- Private device kerning shares the original legacy subtable/pair walker. It
  projects the complete raw design delta before the original ordered pair split,
  uses checked SIMD/reference device additions, retains dependency flags and
  rejects a failed pair before publication. Original design-unit clamping,
  subtable coverage behavior and public calls stay unchanged; earlier successful
  pairs are not rolled back on a later failure.

Feature-value GPOS dispatch now propagates the private frame through both whole-
lookup and original half-open per-cluster ranges, without changing GSUB dispatch
or ordinary shaping defaults. Authored controls cover selected/unselected cluster
ranges, retained owned metrics on both interpreters/after retirement, every
direction, late failure/aliases/tails, and separate raw kerning-format/policy/
overflow cases. The existing full CTest runs retain all prior tests and include
the new independent kerning executable; no local tests were run.

### Owned native shaping generation

`try_shape_context_hinted` is an explicit private native factory. It borrows the
selected immutable primary/fallback source under the existing context use lease
and stages the complete run before publishing an immutable `hinted_shaped_run`.
The previous result remains owned and unchanged on capture, projection or any
later shaping failure. The retained result owns final positioned glyphs, normalized
coordinates, the captured batch and a descriptor index for every final draw.
It also owns the exact admitted scalar input and shapes from that owned copy,
preserving source indices/lengths, script and scalar metadata after caller mutation
or context retirement. Admitted substituted scalars are not the original
pre-substitution source or a replacement for its selected source/glyph bidi policy.
Every transfer, fitting and outline alias walk includes the full input capacity.
It does not retain the mutable context or enable a source Display capability.

Preparation captures every original post-GSUB ID in order, including repeats,
then only the auxiliary descriptors required by missing figure/punctuation spaces
actually present in that run. Both roles retain their own slot even if their IDs
match. Figure space selects the first present digit; punctuation selects period,
querying comma only when period is absent. Unused glyphs are never loaded merely
to fill a metric candidate list. Initial metrics use only the source prefix;
GPOS contour anchors and mark bounds read the same captured original slots.

Em-fraction space fallback projects the original integer design policy once.
Narrow space halves its existing device advance. Figure/punctuation advances and
fallback mark bearings, extents and original horizontal advance come from the
capture, not a second projected design width. The shared mark walker preserves
original recategorization, ligature components, stacking and dependency flags;
only its original UPM gap is projected. Device arithmetic rejects signed32
overflow rather than applying the legacy design-unit clamp.

RTL/BTT reversal carries descriptor indices with source glyphs. Arabic stretch
caches captured unpositioned advances in caller-owned run scratch, preflights
generated offsets and copies/reverses original descriptors with every expanded
glyph. Original run ordering, action/copy policy and 256-per-run/1,048,576-glyph
bounds remain authoritative. No final advance replacement, ID-based owner lookup
or second capture of already positioned output is permitted.

The existing requested unsafe-boundary verifier uses independent fragment capture
owners, preserving the parent generation and exact original comparison algorithm.
This opt-in verification is not source prefix shaping for caret placement.
The new standalone space, mark and stretch controls remain independent CTest
cases alongside all original tests. Actual context controls retain both interpreter
policies, source/descriptor identity, failure publication and post-context lifetime.
An original authored font adds [GSUB](https://learn.microsoft.com/en-us/typography/opentype/spec/gsub)
and [common layout tables](https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2)
to the existing instruction-fault fixture: substitution avoids a pre-GSUB faulty
glyph, and GPOS adjusts the captured valid descriptor. Hosted exact-head CI must
qualify these controls; no local font/build/test/VM execution was performed.

This single-font run now has an additive owned C/managed transport and the private
fitting/outline adapters below. It does not complete styled paragraph composition,
continuation, source interaction, either renderer's actual submission path or WPF
Display admission. Those consumers must retain one fully formatted generation
with original UTF-16, bidi, font/style and draw identities. Windows source/UI and
full native package gates remain open.

### Owned shaped-run C and managed boundary

`progpu_native_text_context_shape_hinted_run` selects the exact context-owned
primary/fallback font and calls the same private factory, not a second shaper.
Source scalars, context scalars, features and normalized coordinates are borrowed
under one context use and copied into call-owned preparation storage. Font bytes,
collection face and normalization resources cannot silently replace that context:
nonempty alternatives reject. The complete shaped generation and its original
captured batch survive context retirement and cache eviction.

The opaque handle exposes counts, atomic glyph-plus-descriptor copying, atomic
outline copying and destruction. Final shaping records use the existing signed
32-bit, Y-down convention in exact 26.6 device units; captured outline points and
metrics retain their original Y-up convention. Y negation is proven representable
before handle publication. Source/codepoint/cluster/flag fields stay exact, and
descriptor indices preserve the original slot even when glyph IDs repeat or
positions expand/reverse. Copies validate full capacities, pairwise overlap and
every retained allocation before writing, preserving all unused caller tails.
Four positioning lanes use baseline SIMD on the admitted x64/ARM64 targets.
Outlines reuse the original exact bulk transport; neither copy executes a font,
reshapes, allocates or submits GPU work. Unsupported dependency builds fail
explicitly. The two renderer export allowlists carry the same five symbols.

`NativeTextShapingContext.ShapeHintedRun` retains its existing exclusive context
lease through capture and managed construction. `NativeHintedTextRun` owns a
separate handle lease for both whole-run transfers; disposal cannot release a
borrowed native generation. The existing generated wire records remain unchanged.
Authored native controls compare complete output against independently captured
raw slots, original source identities, both interpreters and all four directions;
short/overlapping buffers, late faults, auxiliaries and retirement remain separate.
Loaded JIT/NativeAOT package controls add LTR/RTL device metrics, phase, exact warm
copies, caller tails and post-context ownership without replacing any prior case.
These controls require successful exact-head hosted CI, not local execution.

### Retained hinted fitting and interaction frames

`try_layout_hinted_shaped_run` feeds the owned generation into the original
`try_layout_measured_logical_shaped_text` writer. It does not reshape source text
or replace already fitted advances. One explicit logical-units-per-physical-pixel
value converts all four signed 26.6 positioning fields with the writer's existing
SIMD lane conversion. Source ascent/descent metrics and paragraph metadata are
required separately; rounded global native size metrics are not line-height policy.
Caller widths/heights remain in logical units, with no design-unit em scaling.

The snapshot owns run-order metadata, original logical cluster restoration,
positioned glyphs, lines, exact descriptor maps and advance-based line origins.
RTL cluster groups preserve their original within-cluster ordering. The original
writer and adapter share the same alignment/pen-origin arithmetic, including RTL
justification and trailing widths; glyph ink offsets do not own caret geometry.
Original output glyph indices map back to the retained run, not to an ID lookup.
Caller metadata mutation and context disposal cannot invalidate the snapshot.
Complete borrow/publication and retained-capacity alias checks precede any write.

This private adapter currently admits horizontal LTR/RTL with the original
wrapping, alignment, justification and line limits. Vertical writing and trimming
reject explicitly; source styles, multiline bidi composition, continuations and
hard-row source editing still require their contracts. Authored controls compare
the actual writer's raw output, extents and original advance-interaction API with
independent unpacked slots, large ink offsets, both directions, fractional logical
units, aliases and actual owned hinted runs. This is native fitting evidence when
executed, not source Display/UI or loaded public-layout ABI qualification.

### Original styled paragraph and measured interaction ownership

The private `try_layout_context_hinted_paragraph` opt-in now connects the original
paragraph producer to owned hinted runs. The existing producer still selects
source styles/fonts, digit substitution and source/glyph bidi policy, scripts,
graphemes, run boundaries, features, hard breaks and justification classes. Its
original logical restoration records each exact run/glyph/descriptor owner;
neither a second shaping pipeline nor a glyph-ID lookup substitutes for it.
Existing public paragraph entrypoints retain their original behavior.

Each style requires explicit device configuration and source ascent/descent.
Original source scale remains identity metadata; the measured writer converts
device 26.6 metrics only by logical-units-per-physical-pixel divided by 64.
The snapshot owns original request scalars/context/features/axes, admitted scalars,
font sources, styles, full shaped runs and formatting metadata. Its request
pointers refer only to its own storage, and copying/moving the snapshot is disabled.
Line limits may truncate positioned output, not the retained original paragraph.
Safe complete input/old-generation capacity alias preflight precedes diagnostic
writes, and a failed operation cannot replace the published snapshot.

An internal metadata sink captures actual line pen origins and L1-adjusted bidi
levels during the original measured writer's visual emission. Logical shaping
levels remain separate. Interaction uses those actual retained levels and origins,
not independently resolved bidi, reconstructed ink positions or reshaped prefixes.
Original scalar ranges define explicit cluster ends, capped at the producer's
actual BK/NL/CR/LF boundaries, including admitted CRLF pairs; source code points
and UTF-16 identity are not rewritten. The private interaction factory owns this
same paragraph and uses the original measured-advance box/caret builder, including
its measured row frames. No font object, context or mutable caller array is borrowed.

Authored CPU controls retain both interpreters, explicit mixed-font/style metrics,
source/glyph bidi policies, contextual digits, fractional device sizes/phases,
wrapping/alignment/justification, hard boundaries and maximum-line truncation.
Separate original writer/visual-group references check every output field, actual
trailing-space L1 levels and RTL pen origins. Ink-offset, caller mutation,
retirement, invalid policy, alias and untouched-tail controls remain independent.
These controls have not been executed locally and require exact-head hosted CI.

This seam admits horizontal, untrimmed styled paragraphs only. Tabs, objects,
vertical writing, synthetic collapse, exclusions, floats and continuations remain
explicitly outside it. It does not manufacture empty hard-row carets or qualify
source Display/editor admission. The explicit owned ABI below exposes that same
formatted generation without changing any original paragraph entrypoint.
The owned mixed-style frame below connects private renderer consumption; successful
GPU/package evidence and independent Windows text/UI comparisons remain required.

### Shared native renderer outline records

`write_hinted_run_outlines` converts the same retained batch into the actual
`progpu_native_glyph_outline` and `progpu_native_path_segment` record formats
consumed by the shared native glyph executor in both renderer providers. It keeps
one source-indexed outline slice per ink descriptor and explicit source/positioned
maps. Repeated IDs stay separate; auxiliary space descriptors are not source
draws, and no-ink slots retain an explicit sentinel. The private owned-frame
consumer below connects actual renderer submission; records alone are not rendered
output or complete source-host wiring.

Original quadratic contour/implicit-point handling reuses the native TrueType
writer. Explicit mixed cubic contours preserve the existing native cubic record
policy and source contour order. Signed coordinates and implied midpoints require
exact float physical projection before publication; captured phase is included
once, with raster scale one and no second subpixel offset. Baseline SIMD converts
independent point lanes with an explicit scalar reference and bounded tail. Forced
GPU conversion rejects because this pre-raster CPU-visible transport would need
a new readback contract; existing GPU coverage/composition stays unchanged.

Topology, complete output/scratch capacities, all retained allocation ranges and
pairwise overlap validate before publication. Original raw tags and flags remain
in the batch. Nonzero coverage admits ownership/reverse-winding flags only;
even-odd, scan/dropout and unknown policies reject. Curve tags admit only original
curve kinds and documented internal bits, not per-contour SCANTYPE overrides.
The public [FreeType outline contract](https://freetype.org/freetype2/docs/reference/ft2-outline_processing.html)
defines those tag/flag distinctions; a vector record adapter is not grayscale/BW
raster parity. Authored quadratic/cubic/winding and original-writer differential
controls retain scalar/SIMD comparisons, duplicate slots, all tails, later invalid
topology/policy/precision and spare-capacity aliases. Both real raster consumers,
atlas retention, full package/image and independent Windows gates remain open.

The default `hinted_outline_coverage::strict` keeps all of those original
rejections. Hosted actual-font GPU controls exposed original metadata flags
`0x108` and tag `0x15`, rejected before frame publication. An additive explicit
`nonzero_vector` contract now admits only the documented ignore-dropouts and
high-precision hints plus a mode-zero scan marker at a contour start. It selects
ProGPU's existing nonzero vector coverage, not FreeType scan conversion. Raw tags
and flags remain immutable. Even-odd, other raster switches, unknown bits,
nonzero scan modes and misplaced markers still reject. Separate opt-in controls
compare complete geometry/maps, scalar/SIMD, raw metadata, tails, aliases and late
faults; every existing strict negative assertion remains unchanged.
The actual owned-frame GPU fixture selects this contract explicitly, without
changing font size, capture, GPU work, counters, completion or deadlines.
Neither policy claims FreeType grayscale/B/W pixel parity or source Display.

### Owned glyph frame and original renderer consumer

`try_create_hinted_glyph_frame` now owns the exact retained layout/run and all
shared-renderer outline, segment and positioned-draw arrays. It verifies the
original logical restoration and source/descriptor maps, preserves visual draw
order and repeated source slots, and omits only explicit no-ink descriptors.
Auxiliary capture slots never become draws. Captured outline points remain Y-up;
fitted positions are already Y-down, with one explicit logical origin and no
second phase, snap or glyph-ID-based owner substitution.

The explicit target uses actual physical dimensions, view and DPI. Its stored
logical-units-per-pixel value must equal the literal float reciprocal of DPI,
and the product must be exactly one. Product equality alone can admit a wrong
adjacent float, so both checks are required. Finite target/projection checks and
complete input/previous-frame capacity alias checks precede atomic publication.
This is an explicit identity-basis/solid-color frame, not source style, arbitrary
transform, Display admission or independent physical-pixel parity.

The private `render_hinted_glyph_frame` adapter is compiled into both real native
providers and calls their existing `progpu_native_engine_render_glyphs` exactly
once. It keeps the CPU frame alive through that call; the original executor owns
GPU uploads and submission-retained resources. Status and staged metrics return
by value, with no successful metrics published after failure. No extra poll,
wait, readback or submission is added; submission metrics do not mean completion.

Authored CPU controls compare independent unpacked phase/position/map records,
scalar/SIMD paths, RTL/repeated draws, no-ink/auxiliary roles, fractional and
adversarial adjacent-float DPI, lifetime retirement, later invalid inputs and
used/spare-capacity aliases. Actual GPU execution through both providers, package
consumers, source formatting/interaction and independent Windows gates remain
required. These new controls have not been executed locally.

The existing wgpu-native Direct2D, Dawn macOS Webscene and Dawn Windows Direct2D
differential harnesses now add explicit-dependency hinted controls. An actual
font/context produces the retained run/layout/frame before context retirement;
the real renderer consumes it and the harness retains its original actual
completion/readback path. A separate unpacked reference directly emits the
authored four-point contours and calls the original measured writer, bypassing
the outline/frame adapters. Both policies retain phase, RTL/DPI, repeated source
draws, no-ink output, exact full pixels and every renderer metrics field.

Matched dedicated engines keep subject/reference histories comparable. Each call
advances the engine-owned cumulative submission count once and advances the
provider's actual queue token; those identities are not equated and neither means
completion. Flags/revision zero remain deliberate, so repeated frames still
upload instances and do not qualify atlas retention or warm zero allocation.
These authored controls are additive, keep all original gates/deadlines and
require their own whole successful exact-head CI. They exercise CPU-core font
ownership plus the actual loaded renderer, not loaded public font-ABI/package or
source Windows text/UI qualification.

### Owned original-paragraph glyph frames

`create_hinted_paragraph_glyph_frame` retains that exact full paragraph and copies
explicit solid colors for every original style. It converts each original run
through the shared outline adapter, rebases checked aggregate slices and retains
source/run/descriptor/font/style owners independently of repeated glyph IDs.
Positioned draws keep original visual order; only explicit no-ink draws are
omitted and auxiliaries never become source outlines. Every positioned run,
including no-ink items, must satisfy both literal reciprocal/product DPI checks.
Unpositioned line-limited source remains owned, not reformatted or discarded.
Target/origin and policy are explicit, source scale is not a second projection,
and captured phase/Y mapping is applied once. By-value publication exposes no
partial frame. The shared private consumer makes exactly one original renderer
call with unchanged flags, resources, submission and completion semantics.

Authored CPU controls compare independent raw contours, complete maps/colors and
both source fonts, phases, DPI rejection, no-ink/line limits and retirement.
All three existing GPU harnesses add dedicated fresh subject/reference engines
for the styled paragraph. Separate raw run restoration and the original measured
writer produce reference placements; literal four-point edges bypass both frame
packing and outline conversion. Full pixels/every metrics field, both visible
style colors, repeated/no-ink draws and post-context shared drawing/interaction
ownership are checked. Original actual completion/readback paths and deadlines
remain unchanged. These are authored controls requiring whole successful
exact-head CI, not atlas retention, Microsoft text parity or source UI admission.

The original paragraph cluster-break projection is also shared privately between
wire and native scalars without changing its body or the original paragraph input.
It retains source preflight, last-scalar boundary selection, same-cluster grouping
and caller tails. Its outputs are private scratch: a late invalid glyph group
can leave an earlier prefix written, not a public atomic-generation guarantee.
Independent header-only controls retain that exact legacy behavior. This seam
does not infer bidi, cluster ends or source metrics for a hinted paragraph.

### Explicit owned paragraph and frame transport

`progpu_native_text_context_layout_hinted_paragraph` publishes one immutable
original paragraph together with its measured interaction. Device configuration
belongs to each original style; variation ranges address one synchronous flat
16.16 array. The new counts and bulk-format records expose original/admitted
scalars, logical and positioned glyphs, exact run/descriptor owners, styles,
source metrics, cluster ends, both level generations, lines and actual pen
origins. Interaction copies use the same retained generation, not fresh shaping.

All declared output capacities, including unused tails, must be mutually
disjoint and outside every reachable owned allocation. Input record headers are
not read before rejecting overlap with owned storage. Complete preflight happens
before any copy or publication; even diagnostics remain unchanged on failure.
Fresh handle slots are never read. The caller destroys any previous handle and
excludes concurrent destruction while using a handle. Copies/counts/borrow do
not allocate, execute fonts or submit GPU work. Context retirement does not
invalidate a published paragraph.

The CPU transport controls compare all fields with independently produced
original generations and preserve all 17 output tails. They exercise every
short capacity, record/output/owned-used-and-spare alias, strict and unknown wire
policy, a later font instruction fault, hard-break/line-limit metadata, and
actual context/paragraph/frame retirement. Mock target views are only stored and
inspected, never passed to a GPU engine. A real context without the optional
font dependency must still reject device formatting without changing outputs.
These authored controls require exact-head hosted execution; they do not supply
GPU, loaded-package, source UI or Microsoft reference qualification.

`progpu_native_hinted_paragraph_prepare_frame` owns the original frame and its
paragraph/interaction independently of the paragraph handle. Projection and
coverage choices remain explicit, with strict coverage unchanged by default.
`progpu_native_hinted_paragraph_frame_borrow` returns only the existing flat
glyph-frame ABI under the frame's originating-module lease. An opaque paragraph
or frame handle must never cross into another native provider/module.

The managed `NativeHintedParagraph` keeps read-only owned format/interaction
snapshots and prepares independently disposable frames. The compositor retains
the stock text-library frame lease and the exact target's render lock, validates
target object/view generation/dimensions/DPI, then calls the selected provider's
existing `RenderGlyphs` once. In particular, a Dawn engine is never passed to a
stock text-library function. Target views remain caller-owned; this transport
adds no renderer default, extra poll, wait, readback or submission. Full loaded
JIT/NativeAOT package controls and independent Windows source text/UI evidence
remain required before source Display or editor admission.

The original normalized instance check uses public
[fvar](https://learn.microsoft.com/en-us/typography/opentype/spec/fvar) and
[avar](https://learn.microsoft.com/en-us/typography/opentype/spec/avar) contracts,
not external implementation code. Complete retained
formatting, fitting, interaction and both raster providers must consume that same
generation; independent Windows Display/UI and full package gates remain open.

Only public contracts/design notes were used; foreign implementation structure is
not copied. [Skia's shaped-text model](https://docs.skia.org/docs/dev/design/text_shaper/)
and [Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
support retaining original font/glyph identity and sharing formatted results across
drawing and interaction. Adopted: immutable batch snapshots; rejected: reshaping
substrings or deriving caret positions from ink geometry.
[Direct2D/DirectWrite integration](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
keeps layout and rendering distinct. A FreeType interpreter choice is not inferred
to reproduce Microsoft's measuring/rendering modes; an independent Windows oracle
must qualify the complete source path.
[WebRender font-instance options](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html)
make rendering policy part of font-instance identity; this adapter retains its
own exact policy/phase/variation identity rather than adopting foreign cache keys.
[Vello](https://github.com/linebender/vello) and
[Parley's layout concepts](https://github.com/linebender/parley/blob/main/doc/concept.md)
keep reusable CPU layout separate from GPU vector work. Adopted: CPU-dependent
native TrueType instruction execution and retained outlines; existing ProGPU GPU
coverage/composition stays unchanged. [HarfBuzz's responsibilities](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
do not replace font hint execution; shaping remains the original ProGPU pipeline,
not another engine's text implementation.

Creation is lazy/explicit and owns a font copy; capture is O(H + G + P + C), where
H is dependent instruction execution, G glyphs, P points and C contours, with
O(F + G + P + C) owned storage including F original font bytes. Startup, worker
preparation, scene visibility, demand uploads, cache eviction, atlas generations
and device-loss ownership are unchanged until actual product wiring. Subsequent
consumers of the existing bounded context cache must avoid repeated font copies/
captures without confusing font identity, positioned scene revisions or live
atlas generations. Cold/warm
application timings, allocation/residency evidence, native package/NativeAOT and
independent image gates remain outstanding; no performance benefit is claimed.

The authored fault fixture follows the public
[OpenType glyf](https://learn.microsoft.com/en-us/typography/opentype/spec/glyf),
[maxp](https://learn.microsoft.com/en-us/typography/opentype/spec/maxp),
[head](https://learn.microsoft.com/en-us/typography/opentype/spec/head),
[loca](https://learn.microsoft.com/en-us/typography/opentype/spec/loca) tables and
[TrueType instruction contract](https://developer.apple.com/fonts/TrueType-Reference-Manual/RM05/Chap5.html).
It assembles independent valid table/file checksums and uses original test-only
bytes, rather than modifying an external font or reproducing an interpreter.
