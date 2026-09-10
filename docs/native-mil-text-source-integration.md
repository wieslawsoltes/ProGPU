# Native text source integration

## Core consumer and existing implementation

LibreWPF's core applications include editable TextBox/RichTextBox content and
Toolkit/AvalonDock text. Source inspection found that unsupported portable WPF
formatting can reach `SimpleTextLine.CreatePortableFallback`, which constructs an
empty paragraph rather than a shaped line. This is an unfinished source-formatting
connection, not acceptable evidence of rendering parity. Do not switch Windows
text services to that path merely to remove an OS guard.

ProGPU already implements retained `NativeTextShapingContext` operations for
font fallback, OpenType shaping and bidi-aware positioned paragraph layout.
`GetParagraphRequirements`/`LayoutParagraph` and their C++ context implementation
are the existing reusable pipeline. Do not add a second paragraph composer in
the WPF bridge. Native output retains glyph/font indices, source clusters,
positions and line ranges; the WPF TextLine adapter must preserve them
for rendering, selection, caret navigation, wrapping and trimming, including
source-run styling and actual end-of-paragraph semantics.

## Styled source TextLine connection — current implementation

Standard run underlines now consume existing native range geometry and typed
rectangle/guideline replay; see [underline connection and limits](native-mil-standard-underlines.md).
Historical decoration gaps below remain applicable to the unsupported variants,
not to this newly connected standard TextBlock/Hyperlink path.

Intrinsic min/max widths and WrapWithOverflow now use the shared native configured
flow API; see [core measurement and wrapping](native-mil-intrinsic-text.md) for
the algorithm, application connection and unexecuted qualification coverage.

### Document source positions and property scopes

Core consumer: the existing native host now includes an actual source-built
TextBlock with nested Run/Span content. Source `ComplexLine` represents its element
edges as `TextHidden`; those positions must count in document navigation without
becoming text, glyphs or width. `PortableTextSourceMap` provides a reusable neutral
mapping between ordered visible source ranges and contiguous shaping text. It owns
one immutable range snapshot, with an allocation-free identity query path. Hidden
ranges map to a single text boundary; reverse mapping explicitly selects the source
position before or after hidden content. Empty/hidden-only sources remain valid.

WPF now consumes hidden runs and non-directional TextModifier/TextEndOfSegment
scopes, reusing its existing source-owned `TextModifierScope.ModifyProperties`
inside-out evaluation. No WPF implementation is imported into ProGPU. Original
run spans, line lengths, glyph source indices, logical movement, hit affinity,
selection ranges and dependent/trailing lengths use the source map. Hidden-only
selection has no ink extent, and no synthetic whitespace, control or missing-glyph
box is inserted. Wrapped lines share the immutable map/paragraph; open property
scopes survive explicit line breaks and clone/disposal through source TextLineBreak.
EndOfParagraph clears the scope. Malformed scope ends and excessive nesting fail.

This closes ordinary hidden inline edges and property-only scope transport, not
the whole document editor. Directional modifiers still need an explicit native
embedding contract; decorations, embedded objects, display-mode hinting and the
previously listed language/trimming/continuation-width gaps remain explicit.
Do not enable Windows SDK admission or report RichTextBox parity from this change.

Construction is O(R) time/storage for R visible source ranges; each boundary query
is O(log R), O(1) workspace, and allocation-free. Ordered partition validation and
binary-search branches depend on prior bounds; they are source topology work, not
independent arithmetic lanes or a rejected GPU kernel. Existing intrinsic UTF
decoding/metric scaling and native shaping, layout, bidi and interaction algorithms
are unchanged. The source adapter is shared regardless of managed/native glyph
renderer selection when the typed provider is registered; no backend-specific
raster/scene/ABI change is applicable. No performance improvement is claimed.

The primary-engine references in the research record below were refreshed for this
batch. Adopt Skia/HarfBuzz's distinction between source clusters and shaped glyphs,
DirectWrite/Win2D's range-based interaction, Parley's reusable layout and Vello/
WebRender's separate rendering/cache ownership. This source map adds no cache key,
atlas reset, worker/GPU submission, hinting or device-loss behavior. Reject treating
document edges as drawable placeholders; real embedded objects need their own
metrics. Existing source TextHidden/TextModifier contracts are the WPF authority.

Authored regressions cover mapping affinities, snapshot ownership, hidden-only and
identity inputs, invalid/overflow partitions, nested property order, source spans,
wrapped continuations and explicit-break scope cloning. The existing native host
requires positive inline document width and native font bindings in its scene.
Fixture/application execution and cross-platform/Windows evidence remain deferred.
Compile-only checkpoint: ProGPU fixtures finish with zero warnings/errors, source
PresentationCore fixtures with eight warnings/zero errors, and the native host
harness with one warning/zero errors. No tests, verifiers, applications, VM/GPU
workloads, benchmarks or CI checks were executed. Native C++/C ABI files were not
changed by this source-metadata connection. Latest fetched ProGPU main is included.

### Incremental tab connection

Core action: editing tab-separated text in the existing Showcase/source-host path.
`NativeTextFlowOptions` and two generated, borrowed/leased C/.NET paragraph
entry points now pass the incremental tab interval and text-start grid origin
into the existing native composer. A positive interval enables the flow behavior;
zero preserves the prior API behavior. Existing uniform/styled C and C++ entry
points keep their signatures. The install and contract-generation manifests
include the new header/record.

The native paragraph intercepts U+0009 as a non-ink layout item before shaping,
preserving its scalar/input cluster and selected face domain. Its positioned
`glyph_id` is the reserved `UINT32_MAX` tab sentinel, **not a drawable font glyph**.
The original Unicode/bidi/line-break stages still run; the control is not replaced
with spaces, a missing-glyph box or a count of guessed character cells. Logical
line scanning computes distance to the next leading-edge tab-grid stop, including
the supplied origin, before deciding wrap boundaries. An exact stop advances a
full interval. Per-line resolved advances are carried through bidi visual ordering
in caller-owned scratch, so RTL does not recompute tabs from the visual left edge.
Nonfinite or nonprogressing extents fail before output publication.

The source WPF adapter supplies `DefaultIncrementalTab` and its existing text indent
when a paragraph contains tabs. The neutral positioned glyph adds a typed `IsTab`
flag. Source GlyphRun creation excludes these non-ink items, while native caret/
selection buffers, source character indices, trailing-whitespace widths and cached
background rectangles retain their actual extent. The source selection-range list
includes tab-only ranges independently of drawable glyph runs. The existing host
case now includes a tab in its styled mixed-direction composite-family text.

Custom left/center/right/character-aligned stop collections, leaders and tab trimming
remain explicit unsupported contracts; a disabled incremental grid is not silently
replaced with a guessed width. This connects the default editor tab path, not all
tab APIs, document objects or complete WPF line semantics. Changed-width continuation,
first-versus-following-line paragraph indentation and the other existing editor
qualification gaps remain outside this connection's completion claim.

Tab widths and wrap/cursor scans depend on preceding advances, so that work is a
scalar prefix calculation rather than independent SIMD lanes. Independent native
metric conversion/scaling retains its shared NEON/SSE2 implementation; no new
CPU rasterizer, GPU fallback, readback or per-character submission is introduced.
Paragraph scratch grows by one float per glyph capacity. No latency, allocation
or throughput improvement is claimed before the deferred measurements.

Authored fixtures cover exact stops, indentation, LTR/RTL placement, wrapping,
overflow/invalid descriptors, styled face indices and borrowed output/lease
contracts. Source fixtures cover tab width, logical caret traversal, tab-only
selection and exclusion from ink glyph runs. None is executed evidence.

Compile-only checkpoint: the native text targets build with the existing strict
AppleClang configuration (WebGPU disabled); ProGPU managed fixtures finish with
zero warnings/errors, source PresentationCore fixtures with one warning/zero
errors, bridge fixtures with 116 warnings/zero errors, and the source-host harness
with zero warnings/errors. These are compilation results, not passing tests or
renderer qualification. No fixtures, verifiers, apps, VM/GPU workloads, benchmarks
or CI checks were executed for this batch.

Historical next blocker at the tab checkpoint: source `ComplexLine` emits
`TextHidden` for document element edges and `TextSpanModifier` for inline state,
while `PortableTextLine.Create` rejects both. Normal document-backed text must
preserve these source positions and scopes before its application path is closed.
Do not treat this tab checkpoint as complete RichTextBox support, or expand custom
tab APIs ahead of that required connection and package-startup closure.

The focused primary-source references are
[DirectWrite's incremental tab interval](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextformat-setincrementaltabstop)
and [Unicode bidi L1/L2](https://www.unicode.org/reports/tr9/#L1). Adopt a real
layout interval and keep logical measurement distinct from visual ordering.
The existing Skia/Win2D/Parley/Vello/HarfBuzz/WebRender ownership comparisons below
remain applicable: reuse CPU layout results, retain exact font identity, and leave
glyph atlas/cache/device lifetime and GPU scene execution unchanged. No foreign
implementation was copied. Full renderer/package/native-versus-Windows evidence
and CI still belong to final qualification.

### Source composite and fallback connection

The core source-host text case now selects WPF's `#GLOBAL USER INTERFACE` composite
family. The source adapter no longer requires the requested Typeface itself to
be a physical GlyphTypeface. `GlyphingCache.GetPortableFontRuns` exposes existing
source `TypefaceMap` family linking without the DirectWrite text itemizer or a
LineServices object. It reuses the formatter-owned bounded typeface cache and its
physical/scaled run identities, preserving composite family ranges, culture-based
family selection, configured fallback families, coverage and combining/joiner rules.
The existing source mapping algorithm is reused, not rewritten or copied into ProGPU.

Mapped ranges split source styles before the existing native paragraph call.
Their exact face bytes/index and `requested em size * mapped scale` reach ProGPU;
the actual WPF GlyphRun uses that same em size. Source Typeface baseline/line
spacing remains based on the requested em size, matching the existing source
TextShapeableCharacters distinction between line metrics and scaled glyph size.
An empty paragraph resolves a default face through a source space probe without
adding that space to its input or glyph output. No new glyph parser, shaper,
renderer or GPU fallback is involved.

Digit substitution is still checked before this mapping-only seam. Unresolved
null-shape contracts, device fonts and synthetic style simulations fail explicitly;
they are not silently treated as an ordinary face. Normal missing glyphs for a
resolved physical face retain native shaping behavior. This is a source fallback
connection, not a claim that arbitrary installed/composite fonts, emoji/variation
sequences or text-service behavior are runtime-qualified. Mixed languages, tabs,
document objects and the remaining editor gaps below remain open.

Authored source fixtures cover an actual bundled Latin-to-symbol fallback chain,
repeated cached face identity, culture-selected composite scale, surrogate-boundary
coverage and scaled wrapped GlyphRuns through the typed paragraph provider. The
source-host composite-family case retains its styled bidi text, image/geometry
and device-recovery assertions. Test/application execution remains deferred.
Compilation checkpoint: source PresentationCore fixtures finish with four warnings
and zero errors; the source-host harness with one warning and zero errors; bridge
fixtures with 115 warnings and zero errors. These are compile-only results.
No fixture, source verifier, app/VM/GPU workload, benchmark or CI qualification
was executed. The ProGPU native algorithms and generated ABI are unchanged in
this source-connection batch; latest fetched ProGPU main is already included.

The focused primary-source refresh is
[DirectWrite font mapping](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_2/nf-dwrite_2-idwritefontfallback-mapcharacters),
which reports a physical face, mapped length and em-scale, and
[Parley's font context](https://docs.rs/parley/latest/parley/struct.FontContext.html),
which separates reusable font discovery/cache state from layout. Adopt those
ownership distinctions through the existing WPF font cache, not Windows activation
or a new per-line font-discovery pass. The wider Skia/Win2D/WebRender/Vello/HarfBuzz
comparisons below remain unchanged. No foreign implementation code was imported.
The added traversal is dependency-bound range/cache metadata; existing native
SIMD shaping/layout and GPU rendering remain authoritative. Allocation, fallback
fidelity, startup and edit/scroll throughput still require final measurement.

### Styled physical-face connection

Core action: changing inline font size/face/foreground/background in the existing
Showcase editor or source-host FormattedText. The previous source adapter rejected
these paragraphs as mixed typography. Explicit physical-font styles now reach
the existing C++ paragraph through generated `NativeTextStyleRun` records and
two borrowed, leased styled-context APIs. Existing uniform C and C++ entry points
retain their signatures and route through the same implementation.

Styles partition logical scalar input and select a context-owned face, floating
DIP/design-unit scale, feature slice and OpenType language tag. The shared script,
bidi and shaping stages intersect those boundaries. Per-glyph scales follow the
logical glyph through line wrapping and visual reordering; metrics are not rounded
back into another font's integer units. Scratch requirements include that scale
stream. No second composer or third-party implementation was introduced.

`NativeTextParagraphSnapshot` maps UTF-16 style ranges to its existing decoded
scalar stream, rejecting gaps, overlaps and surrogate splits. The neutral WPF
contract carries explicit style font/size/features and per-output font indices.
The host retains exact render-font annotations for every selected context face.
Single-face style/size/feature changes reuse the leased native plans; multi-face
paragraphs use an isolated context disposed after owned output is produced, so
they cannot change the cached uniform fallback policy. A bounded multi-face plan
cache remains a performance qualification item, not a measured improvement.

Source WPF retains run-specific GlyphTypeface, em size, brushes and source ranges.
Actual GlyphRuns split at style/font/bidi boundaries and reject clusters crossing
their style domain. Native annotations are selected by output face index. Each
WPF line uses the maximum participating ascent and descent for natural height,
or the explicit paragraph line height; a large run on a later wrapped line does
not enlarge every preceding line. The native paragraph still has a uniform
temporary vertical stride; WPF consumes line-relative glyph positions and native
horizontal hit/selection extents, applying its own line height to selection and
cached brush backgrounds. No full native variable-line-height contract is claimed.

Still open for the core editor: mixed languages/localization, tabs,
document objects/modifiers/hidden runs, decorations,
baseline changes, trimming and the remaining gaps listed in the initial checkpoint
below. Boolean typography is transported; variations and synthetic font styles
are not newly implemented. This removes the explicit mixed physical-face/size/
brush rejection, not all RichTextBox blockers or Windows SDK admission.

Four independent metric lanes use alignment-safe NEON on ARM64 and SSE2 on x64
for conversion/scaling and finite-product checks. Other architectures have a fixed
four-lane reference. Wrapping/cluster boundary scans and cursor accumulation are
dependency-bound. This is CPU-owned typography, not a rejected GPU kernel; existing
GPU raster/upload/composition and execution policy are unchanged. No SIMD throughput
or whole-pipeline speed claim is made without the deferred benchmarks.

Authored native fixtures cover scaled bidi order, wrapping, actual face indices,
invalid style coverage and finite-product failure before publication. Managed
fixtures cover layout/lease/pinning and UTF-16 style boundaries; typed source
fixtures cover wrapped run metrics, exact render-font annotations and brushes.
The existing native host text case now changes size and foreground within mixed-
direction text. None has been executed. Compilation is not native or app parity.

Compilation checkpoint: strict AppleClang C++20 builds of the native text and
shaping-showcase fixture targets complete; the ProGPU managed fixture project
builds in Release with zero warnings/errors. Public interaction/style headers are
included in the native install manifest. No fixture, verifier, installed-package
consumer, app/VM/GPU run, benchmark or CI gate was executed. The latest fetched
ProGPU `main` is contained by the feature branch.

The [native document block-placement service](native-mil-document-flow.md) now
accepts real formatted lines without reshaping. Its source FlowDocument consumer
and pagination remain open; the utility is not document-viewer parity.

### Architecture sources and decisions

This integration retains the wider cache/GPU/device comparison in
[the rendering research record](progpu-avalonia-rendering-research.md), with this
focused primary-source refresh. These are conceptual comparisons, not imported
implementation text or evidence of matching output:

- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/):
  adopt separate shaped results and exact face identity with original text ranges;
  keep arbitrary drawing annotations in the source adapter.
- [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout)
  and [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm):
  adopt ranged formatting plus reusable interaction output, not platform COM
  activation as a prerequisite for portable paragraph layout.
- [Parley](https://docs.rs/parley/latest/parley/) and
  [Vello](https://docs.rs/vello/latest/vello/): adopt source ranges and reusable
  CPU layout feeding renderer glyph runs. Preserve lazy retained font contexts;
  document the multi-face context-reuse gap instead of claiming equivalent reuse.
- [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html):
  retain actual selected-face glyph positioning and script processing in ProGPU's
  existing native shaper, not source per-character nominal advances.
- [WebRender](https://doc.servo.org/webrender/index.html): preserve the separation
  between source/layout preparation and retained renderer resource/frame work.
  This slice changes no display-list invalidation, visibility culling, demand upload,
  worker scheduling, GPU batching, atlas eviction/keying or device-loss generation.

Startup remains lazy for native contexts; output is immutable and reused through
wrapped continuation. DPI/display hinting remains the existing Ideal-only source
profile. Fallback and variable-face identity are explicit remaining dependencies,
not atlas-cache guesses. Cold-start, editing/scroll percentiles, allocation/cache
residency, image quality and exact-binary scalar/SIMD comparisons remain required
at final qualification. The implementation-first instruction defers these runs;
the source research does not substitute for them.

## Source TextLine connection — initial single-domain checkpoint (historical)

The source adapter now exists: `PortableTextLine` consumes the neutral
`IPortableTextFormatting`/`IPortableTextParagraph` contract. A native-MIL host
registers the provider before source formatting. On platforms without Windows
LineServices, a rejected SimpleTextLine now tries that provider before the old
transitional empty fallback. When the registered provider rejects content, the
exception propagates; it cannot become an empty successful line. The established
simple formatter and Windows LineServices selection are not replaced in this
checkpoint, and Windows SDK admission remains guarded.

`NativeTextParagraphSnapshot` in ProGPU owns the reusable editor integration:
UTF-16 input, existing retained C++ paragraph shaping, native bidi resolution,
logical successor-cluster metadata, exact UTF input line ends and native
interaction buffers. The original C++ shaping/layout algorithms are unchanged.
The snapshot rejects truncated/ellipsis layout because synthetic glyphs require
an explicit source collapsing contract. No retained native pointer is exposed.
Requirements allocate owned output capacity; only successful result counts are
retained. Temporary scratch is pooled and always returned.

Source WPF owns physical GlyphTypeface byte/face-index/em-unit snapshots cached by
face, run/paragraph metrics and source character indices. The host weakly caches
the leased native contexts by those immutable font snapshots. Wrapped lines share
their exact rendering TtfFont annotation (same bytes and collection face index),
so replay does not choose another face by URI/family-name fallback. Source WPF
transports that existing opaque glyph-font annotation without inspecting it.
Wrapped lines share immutable paragraph output through cloned typed TextLineBreak continuation state;
disposing a break drops its reference without invalidating an independent clone.
Changing continuation width/source position is currently explicitly rejected.

The adapter creates actual GlyphRuns with source cluster maps, caret stops, bidi
levels and WPF offsets. Native Y-down positions transfer once to the initialized
source GlyphRun and are used by both native-vector and neutral compatibility
exports; they are not reconstructed as unshaped per-character advances. WPF
logical caret navigation uses ordered native cluster boundaries, not the native
API's distinct visual navigation operation. Native hit and selection queries
consume retained buffers. Boolean OpenType typography features cross a typed tag
contract; source DigitState decides whether unsupported digit substitution is
required rather than guessing from a culture name.

This is an incremental **single typography domain** connection, not complete
TextBox/RichTextBox support. Mixed fonts/sizes/cultures/brushes, composite-font
resolution and fallback-face mapping, tabs, document objects/modifiers/hidden
runs, decorations/effects/baseline changes, enum-valued typography alternates,
digit substitution, markers, justification and collapsing
symbols remain explicit missing connections. Display-mode hinting is rejected;
localization/variations, changed-width continuation, min/max paragraph measurement
and full editing behavior remain unqualified. Close these against the existing
Showcase and Toolkit/AvalonDock editor cases; do not replace them with nominal glyphs
or redefine this single-domain checkpoint as the application's finish line.

CPU input expansion has intrinsic BMP and four-surrogate-pair blocks; mixed or
malformed variable-length sequences and the bounded tail use the Rune decoder.
Cluster sorting/search, variable cluster grouping and logical navigation have
data-dependent topology. Owned DTO/font/glyph arrays are formatting lifetimes,
not allocations on each retained replay. These are CPU-owned source/editor
operations, not a GPU fallback, and no full SIMD or speed claim is made. Allocation
and throughput qualification remain required at feature freeze.

Authored fixtures compare UTF-16 expansion to the Rune oracle, and use a typed
source paragraph fixture for cluster maps, RTL glyph metadata, selection,
logical navigation and cloned continuation after line disposal. That fixture
does not execute native shaping. The existing native host harness now creates
its host before drawing, draws a mixed-direction/combining-mark FormattedText,
requires positive text width and native font bindings, and retains its bitmap,
geometry and device-recovery gates. Its existing dual-assembly diagnostic public
API reflection is not product reflection; remove it when the harness binds the
source WPF assembly directly. All fixture/application execution is deferred.

## C/.NET interaction stage

The existing native cluster/caret/hit/selection algorithms are now exposed through
six C exports and `NativeTextInteractionInterop`: requirements, build, hit test,
caret lookup, visual caret movement and selection rectangles. Inputs and outputs
are borrowed caller-owned spans, with no device, retained handle, per-glyph call,
buffer allocation or glyph repacking in this adapter. Managed output references
are pinned for the complete native call, including references into managed objects.

Cluster ends and resolved bidi levels are required **per positioned glyph**, not
per input scalar. Consumers must preserve original UTF input offsets and real
ligature/cluster boundaries after shaping and visual reordering; sequential glyph
indices or guessed direction are not substitutes. Requirements are capacities;
publish only the returned counts after success because caret deduplication can
reduce output. Buffers must be aligned and nonoverlapping. Invalid input or
insufficient output capacity fails closed, with zero published counts.

The original ProGPU interaction implementation at `8a51aa05` was moved into a
private record-templated implementation shared by the existing C++ API and new
C ABI. C records and C++ records have different layouts and are consumed directly,
not reinterpreted as one another. Valid-input algorithms are retained; finite
extent, bidi-level and boolean-flag validation is strengthened. These synchronous
CPU-owned editor queries are not a rejected-GPU fallback. This connection does
not claim complete SIMD qualification or measured performance improvement.

The new public C header generates its C# records through the existing contract
generator; generation and verification scripts include it. A standalone C ABI
fixture target builds without a WebGPU provider and compares C/native cluster and
caret output, RTL affinity, selection capacity failure and malformed metadata.
Managed fixtures cover ABI sizes and borrowed-buffer source contracts. These are
authored fixtures, not executed evidence. Native text targets and the managed
backend/fixture project compile; full renderer, runtime and application validation
remain deferred until feature freeze.

This closes the missing interaction export. The source connection above consumes
it; styled font runs and the remaining editor contracts are still core blockers.

## Retained context ownership connection

The managed wrapper previously read a raw pointer and then entered native code;
Dispose/finalization could destroy it during the call. Concurrent native calls
could also modify retained plan clocks, plans and fallback-font vectors without
synchronization. Every context operation now acquires the same stack-only use
scope, serializing the entire native call, not just the pointer read.

Disposal from another thread waits for an active operation. Reentrant disposal
marks the owner closed and defers destruction until the final nested use exits.
New acquisitions after closing throw; destruction occurs once. Finalization uses
the same owner, and failure to allocate the managed owner releases a successfully
created native context. Independent contexts remain independently executable.
The scope must stay on its acquiring thread and is internal; callers of the public
synchronous context APIs do not manage it or receive raw handles.

Requirements and execution remain separate operations, not an atomic sequence.
Fallback-font changes between them can change output requirements; consumers must
check execution status/counts and must not publish partially filled glyph data.
C/C++ users still own external context synchronization and borrowed input lifetime.
This change does not add native locks, change the C ABI or affect independent
stateless shaping/layout APIs.

No glyph arithmetic, CPU fallback, shader or algorithm is replaced. Existing
native implementations remain authoritative; no scalar substitute is introduced.
Synchronization overhead and contention are not benchmarked, so no speed claim
is made. Scope acquisition does not create a heap lease or per-call delegate.

## Authored evidence and remaining core work

Owner fixtures cover nested/reentrant disposal, exactly-once release, exclusive
concurrent use and disposal with an operation in flight. They isolate managed
ownership with a typed release callback; they do not execute a native shaper or
prove paragraph/glyph parity. Source guards require all public context operations
to hold the scope. Compilation is the current checkpoint; execution, native stress,
application images/caret/selection checks, performance and CI await feature freeze.

Still required: finish the source connection's explicitly listed application
dependencies above and qualify them against native Windows. The ownership and
styled/source-font adapter changes do not close the application feature or Windows SDK
admission. Managed portable rendering retains its independently selected mode.
