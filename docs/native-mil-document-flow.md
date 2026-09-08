# Native document block placement

## Core dependency and current boundary

LibreWPF's MVP contains a FlowDocumentScrollViewer with a heading, Hyperlink and
list, plus FlowDocumentReader and FlowDocumentPageViewer. Its portable
`FlowDocumentView` currently returns empty layout. This checkpoint implements
the reusable placement layer, **not the finished viewer**.

`NativeDocumentFlow.ResolveWidths` resolves constraints for a source-owned preorder
block tree. The caller formats actual paragraphs using the existing retained native
text pipeline. `Arrange` places their actual line metrics and returns block boxes,
line positions and document extent. It does not recreate shaping, clusters, source
positions or drawing annotations.

The neutral `IPortableDocumentFlow` contract has a zero-copy LibreWPF adapter.
Native SDK pre-host initialization, portable activation and direct-host startup
install a lazy default. Explicit overrides retain priority and disposal restores
the default. Registration performs no native loading, font work or GPU creation.
The source consumer still needs original block extraction, paragraph formatting,
list markers, visual/content ownership, text-view interaction and invalidation.
Pagination and Windows SDK admission remain separate open blockers.

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
actual MVP viewer interaction, image/lifetime/performance comparisons, Windows
Parallels and exact-head PR CI remain in final qualification. This checkpoint
does not qualify a package-mode app or close the core application queue.
