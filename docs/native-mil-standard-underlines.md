# Source standard underlines over native paragraph ranges

## Core application dependency

LibreWPF's MVP document viewer includes a Hyperlink. Source ComplexLine already
publishes its underline through TextSpanModifier and real run properties, but
PortableTextLine rejected all decorations. The native host fixture now contains
a real Hyperlink and requires positive rectangle coverage plus its baseline
guidelines in the typed source render stream. This prerequisite is connected;
the FlowDocument viewer's block/page layout remains unfinished.

## Reuse and ownership

Standard run-level underlines use the existing retained ProGPU paragraph's
GetSelection range geometry. That service is already backed by native interaction
algorithms; the WPF adapter does not measure characters, reshape text, reconstruct
glyph clusters or infer bidi positions. Each source style contributes its actual
physical face's underline position/thickness and mapped em size, with the source
line baseline and foreground brush. Wrapped lines retain the same paragraph and
source styles. Interior tabs contribute their native range width without an ink
glyph; trailing source whitespace does not gain underline coverage.

The source emits retained filled rectangles with paired baseline/top-edge
guidelines, matching its existing SimpleTextLine standard-underline convention.
Both ProGPU renderer implementations already consume these typed commands; no
new renderer, C ABI, shader, font cache or alternate composer is needed. The native
MIL scene compiler retains compact Y2 guidelines and translates the rectangles.
Underline bounds participate in source TextLine ink/overhang metrics.

Only standard, nonanimated run underlines with the recommended font units, zero
offset and no explicit pen are admitted in this connection. Paragraph-level
decorations, custom/animated pens or offsets, other decoration locations, and
continuous mixed-font metric averaging remain explicit unsupported contracts.
The RichTextBox linear adapter still rejects decorating structural scopes until
its source scope transport is connected; do not remove that guard just because
TextBlock's ComplexLine transport can now render a hyperlink.

## Cost and applicability

Formatting retains O(S) source styles and O(Q) decoration rectangles for Q returned
visual range pieces. Selection geometry is queried once per decorated style, not
once per glyph or during steady draw replay. Worst-case construction remains
O(S*G) through the existing range query for G paragraph glyphs; this is not a
performance improvement claim. Small queries use 64 stack rectangles and larger
queries rent one reusable buffer. The no-decoration path retains no decoration
list. Replay records O(Q) ordinary rectangle commands; ProGPU owns batching and
rasterization. It adds no pixel readback, CPU rasterization, GPU initialization,
atlas invalidation or per-glyph native crossing. Native layout/interaction SIMD
and existing renderer execution policies are unchanged. The small source metric
mapping is per-style metadata, not a compute-shader fallback.

## Contract evidence

The [WPF TextDecoration contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.textdecoration)
distinguishes location, pen, offset and units. Keep unsupported variants explicit
instead of silently reducing them to the default. The source
MS/internal/TextFormatting/SimpleTextLine.cs standard underline and
LineServicesCallbacks.cs decoration callbacks remain the WPF semantic authority;
no WPF implementation is copied into ProGPU. Adopt the source physical font
metrics and baseline/top-edge guideline pairing, not a font-size-derived guess.

The [existing cross-engine record](native-mil-text-source-integration.md) remains
applicable: HarfBuzz/SkParagraph retain shaped clusters, DirectWrite/Win2D expose
range-based interaction, and Parley/Vello/WebRender separate layout from retained
rendering. This change consumes those already established boundaries and changes
no startup, glyph cache, worker, GPU-stage, DPI policy or device-loss architecture.

Authored source fixtures cover native range width, real font metrics/brush/ink,
wrapped continuation ownership, interior tabs, trailing whitespace and explicit
unsupported variants. They are not native shaping evidence. The existing native
host fixture exercises actual source shaping and MIL submission when executed.
All execution, Windows/VM comparisons, image/lifetime/performance gates and CI
qualification remain deferred under the user's code-first instruction.

Compile-only checkpoint: source PresentationCore fixtures finish with 4 warnings
and 0 errors; the native-host harness with 1 warning and 0 errors. No native C++
implementation or ABI changes were necessary. Latest fetched ProGPU main is
included; no CI status or runtime output is inferred from these builds.
