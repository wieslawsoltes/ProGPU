# Native document block placement

## Core dependency and current boundary

LibreWPF's Showcase contains a FlowDocumentScrollViewer with a heading, Hyperlink and
list, plus FlowDocumentReader and FlowDocumentPageViewer. Its portable
`FlowDocumentView` now consumes this reusable placement layer and the retained
paragraph pipeline. Scroll-view and source paginated-viewer implementations are
connected, **not runtime-qualified**; symbol-marker font integration and unsupported
document policies remain explicit qualification/dependency items.

`NativeDocumentFlow.ResolveWidths` resolves constraints for a source-owned preorder
block tree. The caller formats actual paragraphs using the existing retained native
text pipeline. `Arrange` places their actual line metrics and returns block boxes,
line positions and document extent. It does not recreate shaping, clusters, source
positions or drawing annotations.

The neutral `IPortableDocumentFlow` contract has a zero-copy LibreWPF adapter.
Native SDK pre-host initialization, portable activation and direct-host startup
install a lazy default. Explicit overrides retain priority and disposal restores
the default. Registration performs no native loading, font work or GPU creation.
The source-owned block/paragraph formatter, invalidation lifecycle and actual
FlowDocumentView drawing/content/text-view/scroll consumer now exist (checkpoints
below). Paginated policy limitations and Windows SDK admission remain separate
open requirements; the consumer connection does not establish application parity.

## Source paginated-viewer connection

LibreWPF now selects `PortableFlowDocumentPaginator` from the frozen media choice
before constructing the Windows PTS paginator. Source page size, padding and column
width policy feed the existing native block/paragraph pipeline followed by
`IPortableDocumentFlow.Paginate`. The page provider returns actual DocumentPages,
consumed by the existing DocumentPageView rather than replacing the application
viewer with scrolling or a synthetic tree. Both portable renderer modes share this
source adapter and ProGPU C++ fitter; no second managed fitting algorithm was added.

Paragraph keep-together/keep-with-next and ancestor forced page/column boundaries
travel as source-admitted breaks. Native content boxes reserve border/padding
extents at block edges. Provider output is checked for ordered page/column indices,
fragment counts, complete inset fit and unchanged interior line spacing before
publication. A fragment translates the retained source drawing once: page-local
text positions use that same mapping. Actual paragraphs, list markers, source
brushes, column rules and original document indices remain source-owned.

The shared ITextView serves scrolling and paginated consumers, with page-affinity
boundaries, column-local point lookup, caret/selection rectangles, glyph ranges,
cross-column line navigation and content hit testing. Continuation pages enumerate
already-open original content ancestors as well as their local source edges.
Page disposal clears its visual without disposing borrowed TextLines. Source edits,
page-size changes and formatter replacement invalidate interaction and release the
old generation. Background pagination coalesces a full synchronous generation on
the owning dispatcher; it is not incremental or worker-thread document layout.

Nonzero fragment-relative widow/orphan constraints, decorated blocks spanning
page/column fragments and empty decorated blocks fail explicitly pending their
native contracts. Column balancing, variable-width fragments, first-line indent,
hyphenation, exhausted-width wrapping, RTL block layout and embedded objects/tables
remain unsupported. No clipping, constraint relaxation, ordinary-font symbol
substitution or fallback to PTS is authorized by those limitations.

Source policy/ownership traversal is ordered document-object work. Building input
and page indices costs O(B + N); each page visits its own lines, reachable ancestor
blocks and marker range, with preorder sorting for paint order. Page/line lookup is
logarithmic; nearest-column lookup is bounded by the native column limit. Native
fitting keeps the complexity and SIMD metric validation documented below. No new
pixel fallback, per-line P/Invoke or performance claim is introduced.

Authored source fixtures cover the real DocumentPageView wrapper, columns and
affinities, original Hyperlink ownership, continuation ancestors, invalid provider
geometry, insets, explicit missing constraints, edits and disposal/suspension.
They use prescribed provider output to exercise source contracts, not an independent
native fitting oracle. Compilation is recorded in the LibreWPF delivery checkpoint;
tests, applications, native typography/images, package consumption, Windows VM/GPU,
benchmarks and CI are deferred until feature freeze. Windows SDK admission remains
guarded. Next work follows the core application delivery queue, not optional
pagination API expansion.

## Source scroll-view connection

LibreWPF's actual FlowDocumentView selects portable ownership before PTS access on
every OS. A real source DrawingVisual/IContentHost draws the live TextLines,
numbered markers, block backgrounds and border rings. Native MIL receives normal
typed source render data; there is no fake TextBlock tree or second composer.
The source ITextView uses those same lines and original TextContainer pointers for
content hits, selection, caret bounds, line/page navigation and bring-into-view.
Page navigation here means scroll-viewport movement, not document pagination.

Native line positions drive logarithmic line lookup. Scrolling retains the layout
and drawing generation and applies a visual translation plus document-local clip;
viewport text queries translate once, whereas IContentHost rectangles remain in
document coordinates. Source edits reformat and replace the owned generation.
Layout invalidation, suspension and document replacement invalidate interaction;
no disposed lines remain an eligible text view. Published GlyphRuns are not
rewritten for hit testing. Equivalent trailing caret affinities now use native
logical boundaries rather than CharacterHit object equality, preserving source
hidden edges and rejecting positions inside shaped clusters.

Lazy paragraph defaults are registered by portable activation and direct managed
hosts as well as native SDK pre-host initialization. Both renderer modes therefore
share the source document path, without changing explicit-provider priority or
the Windows SDK admission guard. Per-generation source traversal/drawing is
ordered object work, not an additional pixel/compute fallback; this checkpoint
does not claim incremental document layout, viewport virtualization or speedups.

Authored source fixtures cover scrolling without reformat, real glyph drawing,
Hyperlink content ownership, selection coordinates, trailing caret affinities,
line/viewport navigation, edit replacement, suspension and detachment. Deterministic
fixture providers are source-contract checks, not native typography or image
oracles. Source fixture and host graphs compile; execution remains deferred to
feature freeze. Required symbol faces, page formatting, exhausted zero-width
wrapping, indentation/hyphenation, unsupported document objects and Windows SDK
admission remain explicit. No application/package/VM/GPU/CI pass is claimed.

## Sequential native pagination prerequisite

Historical prerequisite checkpoint, superseded by the source consumer above:
the Showcase's FlowDocumentPageViewer then reached the PTS paginator unconditionally.
`NativeDocumentFlow.Paginate` now provides the missing reusable sequential page/
column fitting operation over already-shaped lines. The installed document C
header owns its generated fixed records. Both wgpu-native and Dawn wrappers pin
caller spans in one synchronous LibraryImport; the optional neutral document
service has a zero-copy LibreWPF adapter and fails explicitly if not implemented.
Registration remains lazy. Both managed and native WPF renderer modes use this
same device-independent native service; there is no separate managed paginator.

Inputs carry actual positive line advances, interior spacing, replacement leading
spacing at a fragment start and source-admitted/forced break boundaries. Prefix
metrics plus predecessor and next-forced-boundary indices select the furthest
fitting admitted break. Forced page breaks skip unused columns; a force on the
first input does not manufacture a blank page. Outputs retain input-line order,
zero-based page/column identity and column-local Y. Empty input returns zero pages.
Non-fitting indivisible content reports Unsupported without clipping a line or
silently relaxing a constraint. Invalid flags, metrics, overflow, capacity and
aliasing fail before publication, including when failure follows an earlier fit.
All outputs and caller tails remain untouched on failure.

Cost is O(N + F log N) time and O(N) temporary storage for N lines and F fragments.
Binary search avoids quadratic rescans of long kept ranges. Prefixes, predecessor
indices and boundary choices have ordered dependencies; independent metric pairs
reuse the existing NEON/SSE2 validator. This is CPU-owned text metadata, not a
rejected compute workload or a new CPU pixel fallback. No speed claim is made.

This utility is **not paginated-viewer activation**. The next required consumer
must share source page/column-width policy, resolve keep/widow/orphan constraints
(including fragment-relative constraints, not just static paragraph-edge flags),
produce page visuals and fragmented block decorations, and expose original
document positions through page-local ITextView. Column balancing, changing-width
fragmentainers and impossible-fit relaxation are not implemented by this fitter.
Do not silently replace the Showcase page viewer with a scroll view or remove its list.

Source inspection also corrects the symbol-font blocker classification: ProGPU's
managed SfntFontFace and native sfnt_font_view already implement Microsoft symbol
cmaps, and this Mac has Wingdings installed. That is not execution evidence, nor
portable font availability on Linux. Preserve the actual-face check and qualify
the source marker through the retained paragraph pipeline; do not duplicate cmap
support or substitute arbitrary ordinary-font characters.

The design rechecked the primary [CSS fragmentation model](https://www.w3.org/TR/css-break-3/)
for admitted/forced boundaries and separation of box fragmentation, and WPF's
[ColumnWidth contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.documents.flowdocument.columnwidth)
for source-owned width policy. It adopts separation of shaping/layout/drawing from
[Skia](https://docs.skia.org/docs/dev/design/text_shaper/),
[Parley](https://docs.rs/parley/latest/parley/),
[HarfBuzz](https://harfbuzz.github.io/shaping-concepts.html) and
[Win2D](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
and retained-scene reuse from [WebRender](https://doc.servo.org/webrender/) and
[Vello](https://docs.rs/vello/latest/vello/). No implementation text was imported.
Startup/font discovery, fallback/variable fonts, atlas keys/eviction, demand upload,
workers, DPI/hinting and device-loss ownership are unchanged: fragmentation only
remaps already-formatted line placement. Balancing or a second shaper is rejected
as an implicit responsibility of this bounded fitter.

Authored fixtures cover sequential/forced page and column breaks, kept ranges,
replacement leading space, exact fits, empty input, ABI fields, capacity/aliasing,
failure atomicity and a scalar per-line placement oracle. Native fixture compilation
succeeds under strict AppleClang C++20; the managed fixture graph builds with
0 warnings/errors and the LibreWPF adapter fixture graph with 116 warnings/0
errors. Fixtures are not executed. Full native
libraries/packages, actual paginated viewer, platform/VM output and CI remain open.

## Source formatter checkpoint — not viewer activation

LibreWPF now has `PortableDocumentParagraphSource`, `PortableFlowDocumentLayout`
and `PortableFlowDocumentFormatter`. They consume the original TextContainer,
Paragraph, Section, List and ListItem tree. WPF's existing MbpInfo, page-width/
padding policy, TextProperties, LineProperties and MarkerProperties remain
source-owned; no WPF implementation was copied into ProGPU. Real TextLines feed
the native width/placement service and retain their original global source ranges.

Hidden element edges remain source symbols; the paragraph closing edge terminates
its range without adding an invented document position. Inline decoration and
direction scopes flow through TextSpanModifier/TextEndOfSegment, with actual
TextFormatter/native-provider rejection of unsupported semantics. Bounded copies
use one UTF-16 lookahead unit to avoid splitting a surrogate pair. Empty styled
inline metric contributions and embedded/anchored objects fail explicitly instead
of becoming zero-width-space glyphs or disappearing.

List markers use the existing generated marker TextSource as separate text,
preserving source numbering, actual marker font and offset without pretending
that marker characters exist in the document. Symbol markers require a real
symbol face; a fallback font rendering ordinary text is rejected. Availability
and fidelity of those symbol faces remain a required Showcase dependency, not qualified
by numbered-marker fixtures. First-line indentation, hyphenation and exhausted
zero-width wrapping remain explicit missing native contracts.

The portable formatter participates in FlowDocument's IFlowDocumentFormatter
lifecycle. A source-only formatting scope initializes existing change/highlight
notifications and detects reentrancy/caught illegal mutations without constructing
a PTS page or context. Width/DPI/mode plus source invalidation own retained layout
generations. Unchanged requests reuse output; invalidated requests currently do
full source reformatting. Replacement/suspension disposes owned TextLines and
continuations. Failed replacements leave the formatter invalid, never publish an
empty successful layout. This is not an incremental-layout performance claim.

Source fixtures cover hidden/scoped ranges, bounded surrogate copies, source
block-property transport, numbered markers, generation reuse, edit invalidation,
failure state and caught reentrancy. Their deterministic providers test source
consumption only, not native shaping or block-layout correctness. Source production
and fixture graphs compile; no tests, verifiers, apps, VM/GPU, benchmarks or CI ran.
That historical formatter-only checkpoint is superseded by the scroll-view
connection above; it does not itself qualify application rendering.

## Contract and implementation

`progpu_native_document_flow.h` is the fixed C ABI source. Generated double-
precision C# records are pinned through synchronous LibraryImport calls for both
wgpu-native and Dawn libraries. Authored fixtures cover every neutral/native field;
the adapter neither repacks per-line arrays nor probes WPF objects. No caller
pointers are retained.

- Preorder roots, containers and line-owning leaves partition the formatted line
  array. Containers have no own lines. Width resolution ignores line ranges.
- WPF resolves margin, border and padding policy. ProGPU consumes finite,
  nonnegative DIPs, without importing WPF property or PTS implementation.
- Positive sibling margins collapse by maximum. Zero-inset container edges
  collapse with children; empty transparent containers collapse through. Border
  and padding stop edge collapse. This is not complete CSS layout.
- Widths precede formatting. Zero means exhausted space, not unbounded wrapping.
  Overflowing line widths remain in the extent, including ancestor right insets
  and margins. Root outer spacing is retained.
- Inputs are bounded to 1,048,576 blocks/lines and 128 nested nodes. Alignment,
  capacities, overlapping spans, topology, ranges and finite arithmetic are checked
  before publication. Failures leave outputs untouched; allocation failure is
  distinct from invalid input.
- Negative margins, fixed-height boxes, floats, columns, pagination and RTL block
  ordering are outside this initial contract. Adapters must reject missing
  semantics explicitly, not flatten them or replace content with emptiness.

Width resolution is O(B); arrangement is O(B + L) time and temporary storage.
Native scratch is bounded per changed layout; consumers should retain output and
source lines across replay. Tree ancestry, adjoining margins and vertical prefixes
have ordered dependencies. Independent metric pairs use NEON on ARM64 and SSE2 on
x64, with a fixed two-value reference on other targets. Test-only scalar predicates
and flat-prefix oracles cover that boundary. This CPU-owned layout is not a rejected
GPU kernel: no pixels, readback, upload or per-item GPU submissions are introduced.
GPU execution policy is unchanged. No SIMD or latency speed claim is made.

## Research and original implementation

This is original ProGPU code, not a source port of WPF or another engine. The
[cross-engine text record](native-mil-text-source-integration.md#architecture-sources-and-decisions)
continues to govern font/cache/interaction ownership. The focused primary-source
review adopted concepts, not implementation text:

- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/),
  [Parley](https://docs.rs/parley/latest/parley/) and
  [Vello](https://docs.rs/vello/latest/vello/): separate reusable layout from drawing
  and preserve actual source annotations and formatted lines.
- [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html):
  leave positioning and cluster identity in the existing paragraph shaper.
- [Win2D text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm):
  keep interaction tied to formatted output, not a second text copy.
- [WebRender](https://doc.servo.org/webrender/index.html): separate preparation
  from retained renderer work. Atlas keys, upload policy, workers, visibility
  culling and device-loss ownership are unchanged here.
- [CSS 2.2 margin collapse](https://www.w3.org/TR/CSS22/box.html#collapsing-margins):
  positive adjoining-margin behavior informed the bounded contract and independent
  fixtures. Full CSS formatting rules are not adopted.

## Compilation and deferred qualification

The standalone C ABI fixture target compiles under strict AppleClang C++20.
ProGPU managed fixtures compile with 0 warnings/errors; LibreWPF adapter fixtures
compile with 116 warnings and 0 errors across their existing build graph.
These are authored fixtures, not executed tests. The public header is installed,
and generation/verification scripts include its records; only generation has run.
Full platform renderer/package linking, ABI execution, scalar/SIMD comparisons,
actual Showcase viewer interaction, image/lifetime/performance comparisons, Windows
Parallels and exact-head PR CI remain in final qualification. This checkpoint
does not qualify a package-mode app or close the core application queue.
