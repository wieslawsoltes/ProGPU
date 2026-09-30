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
No C entrypoint, generated managed consumer or source Display admission is added.

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
do not yet have exported entrypoints, generated managed bindings or source
capability admission; they are not a completed public font ABI.

The adapter is now explicitly linkable into the product text context and both
renderers through the bounded cache. Next steps must retain original shaping
identities, expose leased C/managed batch transport with generated wire bindings,
and share its output across actual consumers. Linking alone does not prove that
either renderer or a source application has consumed a hinted generation.
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
and device-loss ownership are unchanged until actual product wiring. The future
bounded context cache must avoid repeated font copies/captures without confusing
font identity, positioned scene revisions or live atlas generations. Cold/warm
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
