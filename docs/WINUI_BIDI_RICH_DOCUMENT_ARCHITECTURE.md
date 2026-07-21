# WinUI bidi and shared rich-document architecture

## Scope and conformance baseline

This record covers the ProGPU WinUI direction, bidi, shaping, rich display,
Markdown, flow layout, and editing work. The semantic source always stays in UTF-16
logical order. UAX #9 levels determine visual order; shaping is performed on
font/style/script/direction-uniform logical runs; line layout and hit testing retain
the mapping back to logical text positions.

The WinUI-facing baseline is:

- `FlowDirection` is inherited by ordinary framework elements and mirrors panel
  geometry; glyph outlines are shaped and reordered, not reflected.
- An RTL element's public local origin is its top-right corner. `TransformToVisual`,
  pointer/current/intermediate points, gestures, drag/drop, and automation point APIs
  use that logical coordinate frame; text hit testing converts back to the unreflected
  physical frame used by retained glyph positions.
- `Image` does not inherit `FlowDirection`, but an explicit RTL value mirrors it.
- `Run.FlowDirection` is retained as an explicit inline override and resolved as a
  UAX #9 isolate without inserting synthetic characters into shaping or edit text.
- `TextReadingOrder` defaults to `DetectFromContent` for text controls and chooses
  the paragraph base direction independently of text alignment.
- `TextAlignment` and `HorizontalTextAlignment` are aliases where WinUI exposes
  both; the last value set wins. An otherwise-default RTL control starts at the
  right edge.
- Caret, selection, pointer hit testing, deletion, and formatting operate on shaped
  clusters/graphemes and logical offsets, including mixed-direction lines.

## Primary sources

Microsoft and Unicode:

- [Windows RTL layout guidance](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/adjust-layout-and-fonts--and-support-rtl)
  specifies inherited root `FlowDirection`, automatic panel flipping, custom-control
  responsibility, and explicit image mirroring.
- [WinUI `FrameworkElement.FlowDirection`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.frameworkelement.flowdirection?view=windows-app-sdk-1.8)
  defines the top-right RTL coordinate origin, direction-sensitive hit testing and
  transforms, panel/template mirroring, unreflected text glyphs, right-default text
  alignment, explicit shape mirroring, and the `Image`/media inheritance exceptions.
- [WinUI `TransformToVisual`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.transformtovisual?view=windows-app-sdk-2.0)
  and [`GetCurrentPoint`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.input.pointerroutedeventargs.getcurrentpoint?view=windows-app-sdk-1.8)
  define direction-aware element-relative coordinate conversion for visual and pointer APIs.
- [WinUI `Run.FlowDirection`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.documents.run.flowdirection?view=windows-app-sdk-2.0)
  supplies explicit direction at the inline-run boundary.
- [WinUI `TextReadingOrder`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.textreadingorder?view=windows-app-sdk-2.0)
  distinguishes control-flow direction from first-strong content detection.
- [WinUI slider guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/slider)
  requires a horizontal RTL slider's low value to appear on the physical right.
- [WinUI scroll controls](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/scroll-controls)
  defines extent/viewport/offset behavior and programmatic horizontal/vertical
  offset semantics retained by the mirrored ScrollViewer chrome.
- [WinUI NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)
  defines hierarchical navigation layout and physical arrow-key movement used by
  mirrored Pivot, tab, tree, radio-group, and navigation-item interactions.
- [WinUI `AutomationFlowDirections`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.automationflowdirections?view=windows-app-sdk-1.8)
  defines the automation text-flow values exposed by rich-edit ranges.
- [Microsoft text directionality guidance](https://learn.microsoft.com/en-my/globalization/fonts-layout/text-directionality)
  recommends directional isolation for embedded runs whose direction differs from
  their surrounding paragraph.
- [WinUI/UWP `TextReadingOrder`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.textblock.textreadingorder)
  documents `DetectFromContent` as the Windows 10+ default.
- [WinUI `PasswordBox`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.passwordbox?view=windows-app-sdk-1.8)
  confirms that the control exposes `TextReadingOrder`, but no text-alignment API.
- [WinUI `RichEditBox`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.richeditbox?view=windows-app-sdk-2.0)
  defines the editing-control surface, formatted content role, input policies, and
  `Microsoft.UI.Text` document access.
- [Rich edit keyboard shortcuts](https://learn.microsoft.com/en-us/windows/win32/controls/about-rich-edit-controls)
  define direction-sensitive Ctrl+Left/Right word movement and the Ctrl+left/right
  Shift shortcuts that jointly set paragraph reading order and alignment; the same
  reference defines Ctrl+Tab as literal Tab and documents tab-backed simple tables.
- [TOM `ITextRow`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nn-tom-itextrow)
  defines row/cell/table navigation, insertion, per-row properties, delimiter-backed
  storage, and row-local RTL orientation.
- [TOM `ITextRange2`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nn-tom-itextrange2)
  and [`GetSubrange`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nf-tom-itextrange2-getsubrange)
  define multiple source-ordered subranges, including an active subrange, used by
  rich-edit table selections without converting the document to one contiguous span.
- [Rich Edit `TABLECELLPARMS`](https://learn.microsoft.com/en-us/windows/win32/api/richedit/ns-richedit-tablecellparms)
  maps cell widths, padding/shading, background colors, and four border widths/colors
  to their standard RTF control words.
- [WinUI `ITextParagraphFormat.AddTab`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.itextparagraphformat.addtab?view=windows-app-sdk-1.8)
  defines direction-relative tab origins and the 63-tab paragraph limit.
- [TOM `ITextPara`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nn-tom-itextpara)
  confirms that rich edit preserves pagination-oriented paragraph effects such
  as keep-with-next, page-break-before, and widow control through TOM/RTF even
  though those effects do not change the onscreen rich-edit display.
- [WinUI `RichEditTextDocument`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.richedittextdocument?view=windows-app-sdk-1.6)
  defines retained ranges/selections, undo, formatting, stream conversion, and
  document-wide text operations.
- [WinUI `AlignmentIncludesTrailingWhitespace`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.text.richedittextdocument.alignmentincludestrailingwhitespace?view=winrt-26100),
  [`IgnoreTrailingCharacterSpacing`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.richedittextdocument.ignoretrailingcharacterspacing?view=windows-app-sdk-1.8),
  and [`CaretType`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.text.richedittextdocument.carettype?view=winrt-26100)
  define document-wide alignment metrics, terminal tracking behavior, and caret visibility.
- [WinUI `ITextRange`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.itextrange?view=windows-app-sdk-2.0)
  defines logical range endpoints, formatted text, source-safe movement, and editing.
- [WinUI `TextRangeUnit`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.textrangeunit?view=windows-app-sdk-1.8)
  and the classic TOM [`ITextRange::Move`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nf-tom-itextrange-move),
  [`SetRange`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nf-tom-itextrange-setrange),
  and [`SetIndex`](https://learn.microsoft.com/en-us/windows/win32/api/tom/nf-tom-itextrange-setindex)
  define logical word/sentence/paragraph units, collapse accounting, ordered endpoints,
  active ends, and positive/negative unit indexing.
- [WinUI `RangeGravity`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.text.rangegravity)
  defines previous/following run formatting at insertion boundaries and inward/outward
  endpoint gravity.
- [WinUI `TextGetOptions`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.text.textgetoptions)
  and [`TextSetOptions`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.text.textsetoptions)
  define CR/LF and construct-boundary retrieval, object/hidden/numbered text, text
  limits, unlink/unhide insertion, and opt-in application of RTF document defaults.
- [WinUI `ITextSelection.MoveLeft`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.itextselection.moveleft?view=windows-app-sdk-1.8)
  and [`HomeKey`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.itextselection.homekey?view=windows-app-sdk-1.8)
  require physical arrow-key behavior for character/word movement and
  paragraph-direction-aware visual line edges.
- [WinUI `SelectionOptions`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.selectionoptions?view=windows-app-sdk-1.8)
  defines the active selection end, ambiguous line-edge affinity, insert/overtype
  mode, focus state, and replace-selection policy; [`TypeText`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.itextselection.typetext?view=windows-app-sdk-1.8)
  must consume the active Insert/Overtype state.
- [WinUI `TextConstants`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.text.textconstants)
  defines the mixed/undefined sentinel values returned by TOM format queries.
- [WinUI `TextChanging`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.richeditbox.textchanging?view=windows-app-sdk-2.0-preview),
  [`SelectionChanging`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.richeditbox.selectionchanging?view=windows-app-sdk-1.8),
  and [`Paste`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.richeditbox.paste?view=windows-app-sdk-1.8)
  define the cancellable pre-change and clipboard interception contracts used by the editor.
- [DirectWrite `AnalyzeBidi`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextanalyzer-analyzebidi)
  requires bidi analysis across whole paragraphs because splitting a paragraph can
  produce incorrect levels.
- [DirectWrite `IDWriteTextLayout`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextlayout)
  retains a fully analyzed/formatted block and exposes cluster, line, inline-object,
  point, text-position, and text-range metrics.
- [DirectWrite `DWRITE_TEXT_METRICS`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_text_metrics)
  distinguishes the alignment width that excludes trailing whitespace from the full
  typographic width that includes it.
- [DirectWrite `HitTestPoint`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittestpoint)
  returns logical position, leading/trailing affinity, inside state, and enclosing
  geometry.
- [DirectWrite `HitTestTextRange`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextrange)
  returns one or more visual geometries for a logical range and applies an explicit
  layout origin, rather than collapsing bidi fragments into one hit rectangle.
- [Direct2D and DirectWrite text rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  separates reusable layout from `DrawTextLayout`/glyph-run rendering.
- [Unicode Bidirectional Algorithm, UAX #9 rev. 51](https://www.unicode.org/reports/tr9/)
  is the normative source for paragraph levels, explicit embeddings/overrides,
  isolates, weak/neutral resolution, paired brackets, and L1/L2 visual reordering.
- [WHATWG HTML global direction requirements](https://html.spec.whatwg.org/multipage/dom.html#the-dir-attribute),
  [CSS Writing Modes `direction`](https://www.w3.org/TR/css-writing-modes-4/#direction),
  and [CSS Text Decoration](https://www.w3.org/TR/css-text-decor-4/) define the
  direction, language/style inheritance, and decoration semantics retained by the
  bounded HTML interchange adapter.

Shaping and production engines:

- [HarfBuzz responsibilities](https://harfbuzz.github.io/what-harfbuzz-doesnt-do.html)
  requires clients to perform bidi, line breaking, and rich-run segmentation; a
  shaping buffer must be uniform in font, size, script, language, and direction.
- [HarfBuzz buffer properties](https://harfbuzz.github.io/setting-buffer-properties.html)
  defines direction/script/language state and paragraph-boundary flags.
- [HarfBuzz clusters](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html)
  defines the source-to-glyph cluster mapping, monotonic direction guarantees, and
  cluster atomicity required by editing.
- [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/)
  separates rich input, reusable shaped output, width-dependent formatting, and
  rendering; shaped runs retain glyph positions and source indices.
- [Skia text API overview](https://docs.skia.org/docs/dev/design/text_overview/)
  keeps shaping usable independently from a canvas.
- [Firefox `gfxTextRun`](https://searchfox.org/mozilla-central/source/gfx/thebes/gfxTextRun.h)
  stores mostly-immutable positioned glyph runs with source offsets, cluster-safe
  break points, and reusable line-break state.
- [Firefox/WebRender rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst)
  separates retained display-list construction from renderer resource work.
- [Parley API](https://docs.rs/parley/latest/parley/)
  shares font/layout contexts and scratch allocation; its retained layout can be
  re-line-broken and re-aligned without rebuilding unchanged styled text.
- [Parley retained layout data](https://docs.rs/parley/latest/src/parley/layout/data.rs.html)
  stores font/style runs, bidi levels, source ranges, clusters, glyphs, lines, and
  line items as distinct stages.
- [Parley's upstream architecture](https://github.com/linebender/parley) keeps font
  fallback, shaping, font parsing, Unicode analysis, layout, selection, and editing
  as distinct but reusable layers.
- [Vello](https://github.com/linebender/vello) keeps retained scene construction
  separate from GPU compute rendering and glyph caching.
- [WebRender's spatial tree](https://github.com/servo/webrender/blob/main/webrender/src/spatial_tree.rs)
  retains scroll-frame transforms separately from scene content, reinforcing the
  single viewport-translation boundary used by the rich editor.
- [Open XML SDK overview](https://learn.microsoft.com/en-us/office/open-xml/word/overview),
  [stream opening](https://learn.microsoft.com/en-us/office/open-xml/word/how-to-open-a-word-processing-document-from-a-stream),
  and [WordprocessingML paragraph structure](https://learn.microsoft.com/en-us/office/open-xml/word/working-with-paragraphs)
  define the strongly typed package/part/body/paragraph/run boundary used by the DOCX
  adapter. The official [`DocumentFormat.OpenXml` package](https://www.nuget.org/packages/DocumentFormat.OpenXml)
  supplies package creation, relationship handling, and schema validation.

## Cross-engine decisions

| Concern | Adopted/adapted in ProGPU |
| --- | --- |
| Startup and lazy initialization | Font fallback discovery stays process-shared and asynchronously warmable. A presenter creates only its small layout session; Markdown parsing remains demand-driven. |
| Shaping/layout reuse | `TextLayout` retains shaped glyphs, safety flags, and cluster mappings. Rich documents retain per-block results in a presenter-local `RichDocumentLayoutSession`; content and style changes invalidate by version. Rich text and Markdown pass paragraph-local context across style/font items, refuse splits inside shaping clusters, and reshape both fragments when a line break crosses an `UnsafeToBreak` dependency. Width/alignment changes can rebuild formatting without sharing unsafe coordinates across controls. |
| Display-list reuse | Rich and Markdown presenters record commands once and replay them until layout, theme, selection, or hyperlink-hover state changes. Selection/hover are repaint-only. |
| Visibility culling | Single-column documents estimate offscreen block heights, realize a buffered viewport, recycle embedded controls, retain list capacity, and anchor the scroll position when estimates become exact. Rich editing projects run storage into paragraph blocks, so a 2,000-line editor realizes only the buffered viewport. Cache state is no longer stored authoritatively on semantic nodes. |
| Scroll and selection coordinates | Following WebRender's separation of scroll transforms and retained scene data, layout and caret positions stay in document coordinates and the viewport offset is applied once at input/output boundaries. `RichTextBlock` refreshes its realized block window only after a viewport-sized movement, while `ScrollViewer` translates retained commands for intermediate motion. Following DirectWrite, logical ranges produce separate line/direction rectangles; following HarfBuzz and Skia, hit testing never splits a shaped cluster. |
| Cache identity/eviction | Block keys include width, padding, font identity/size, foreground, alignment, theme, wrapping, reading order, and flow direction. Glyph/path cache keys and atlas-generation recovery remain compositor-owned. Offscreen block payloads are cleared without `TrimExcess` churn. |
| Demand-driven upload | CPU shaping and layout produce reusable glyph IDs/positions. Visible retained commands drive atlas demand; semantic import never initializes WebGPU. |
| Worker preparation | The design keeps semantic import and future shaping preparation separable from UI-thread child measure/arrange. Present implementation does not move framework-element measurement off thread. |
| GPU batching | Ordinary text remains retained glyph-run commands and compositor batching. Unicode analysis, shaping, line breaking, caret mapping, and document mutation remain CPU work because correctness depends on serial paragraph/run state and no measured complete GPU replacement exists. |
| DPI/subpixel/hinting | Existing physical-pixel atlas scale, quarter-pixel snapping, vector fallback phases, and unsnapped final quad placement are unchanged. Bidi only changes run order and logical mapping. |
| Font fallback and variations | The selected fallback face stays attached to each shaped result. Shaping options retain variation/feature identity; presenter block keys include the active face, while compositor glyph keys remain responsible for raster state. |
| Device loss | Semantic documents and shaped CPU results are device-independent. Atlas generations and scene compilation invalidate GPU residency without reparsing Markdown or changing logical text. |

## Shared document pipeline

```text
Markdown / HTML / RTF / DOCX / plain text / custom typed codec
                    │
                    ▼
       versioned RichDocument blocks
                    │
          notifying nested collections
                    │
                    ▼
       RichDocumentLayoutSession
  paragraph bidi → style/font runs → shaping
  → cluster metrics → line breaking → alignment
  → viewport realization / inline-object layout
                    │
                    ▼
       retained commands + hit-test geometry
                    │
        ┌───────────┼────────────┐
        ▼           ▼            ▼
 RichTextBlock  MarkdownTextBlock  RichEditBox
```

`RichEditBox` owns `RichTextBuffer`, an immutable-span/run-oriented edit store.
Inserts, deletes, formatting, and undo snapshots are proportional to affected styled
runs rather than allocating one managed object per character. Ordinary edits rebuild
only the paragraph interval touched by the change, keep unaffected block identity,
shift suffix offsets by the text delta, and invalidate only the corresponding
presenter caches. Its
`RichEditTextDocument`/range/selection facade edits that same store. A paragraph
projection supplies the shared block layout engine with global UTF-16 offsets,
including CR/LF separator lengths; only visible/overscan paragraphs retain shaped
characters. Browser/mobile IME composition, grapheme deletion, selection replacement,
read-only mode, casing, return policy, and text limits all converge on the same store.
Document-wide trailing-whitespace alignment and terminal-character-spacing policies
are inputs to the same retained layout cache and invalidate only the required scope.
Alignment normally excludes logical trailing whitespace using bidi-aware physical
bounds; callers can opt into the full line advance. Terminal character spacing can
likewise be excluded from both wrapping and line advance, and `CaretType.Null`
suppresses caret painting without rebuilding shaped content.

The facade implements the WinUI character- and paragraph-format surfaces, including
range-local direction/alignment/indents/spacing, custom left/right/center/decimal tab
stops, list markers and numbered-text extraction, font family/weight/stretch/style,
language and OpenType controls, background,
underline variants, baseline/subscript/superscript, hidden/protected text, stream
load/save, math state, and visual up/down movement. Inline images are retained as
immutable object payloads at U+FFFC positions, projected as sized inline elements,
decoded once and demand-uploaded on first render for supported encoded formats, and
expose alternate text through `TextGetOptions.UseObjectText`. The automation text
provider uses the same logical ranges and shaped line geometry for visible ranges,
format attributes, movement, hit testing, per-line screen rectangles, and embedded
child/range mapping.

The DOCX codec uses the Microsoft Open XML SDK only at the interchange boundary.
It opens and creates packages from byte streams, resolves document defaults and
based-on style chains, maps paragraph/run formatting, language and bidi state,
hyperlinks, numbering, table grids/merges/shading/borders, and embedded bitmap parts
to the shared semantic model, and writes the same features back as schema-valid
WordprocessingML. Binary storage is shared by desktop and browser hosts; browser save
copies bytes directly from WebAssembly memory into a `Blob`, avoiding base64/string
expansion.

Rich clipboard transfer retains styled spans and paragraph formatting when
`ClipboardCopyFormat` permits all formats. A typed host adapter publishes and consumes
plain text, RTF, and semantic HTML together; the process-local fallback uses the same
semantic data, and hosts that only implement text continue to receive interoperable
plain text. Browser copy uses native `ClipboardItem` HTML/RTF flavors where supported
and DOM paste captures external HTML/RTF synchronously before routing one paste action
through the focused WinUI control. macOS publishes string/RTF/HTML pasteboard types
together, Windows uses `CF_UNICODETEXT`, registered RTF, and `CF_HTML`, and Linux uses
an explicit Wayland/X11 plain-text provider with in-process rich-data preservation.

Keyboard navigation follows physical caret order while retaining logical UTF-16
selection endpoints. Ctrl+Left/Right visits Unicode word starts in visual order for
TextBox, PasswordBox, and RichEditBox, including mixed fallback-font bidi runs;
Ctrl+left/right Shift changes the selected RichEdit paragraphs to LTR/left-aligned or
RTL/right-aligned as one undoable formatting transaction. Caret lines are grouped by
overlapping vertical centers rather than glyph-top equality, so fallback ascenders,
mixed font sizes, superscripts, and embedded elements do not split one visual line.
Paragraph-format undo snapshots are captured for formatting transactions and only for
text edits that insert or remove paragraph separators, keeping ordinary within-line
typing independent of total paragraph count. TOM tab collections
enforce WinUI's 63-stop limit; RTL custom/default tab positions use the right content
edge as their origin in both virtualized single-column and shared multi-column layout.
The `ITextSelection` facade delegates character/word left/right and line Home/End to
the same physical caret engine as keyboard input, including selection-collapse and
active-end extension semantics.
Its `SelectionOptions` are behavioral: `StartActive` switches the real active end,
`AtEndOfLine` selects trailing caret affinity at an ambiguous soft-line boundary,
`Replace` chooses replacement versus insertion before a nondegenerate selection, and
bare Insert toggles `Overtype`. Overtype consumes the same number of whole Unicode
grapheme clusters as the typed input and never consumes a hard paragraph separator.
Inline embedded-object selections report `SelectionType.InlineShape`.
TOM point geometry follows the same logical endpoint contract: `GetPoint` resolves
the range end by default and `PointOptions.Start` resolves the start, while
`GetRect` continues to return the bounding rectangle for the complete range.
Logical `ITextRange` movement follows TOM's distinct model: Unicode-aware alphanumeric,
punctuation, whitespace, end-of-paragraph, sentence, and paragraph units are scanned
without substring allocation; CR/LF and legacy paragraph marks stay atomic; indexed
ranges use one-based/negative-from-end addressing; nondegenerate moves and deletes
count their collapse as one unit; endpoint setters collapse instead of creating
reversed ranges; and containment/equality are story-local. `Character` reads or
overwrites only the character at the range start, including for a nondegenerate range.
Soft-wrapped `Line` units use the same shaped caret lines as rendering and selection;
`HardParagraph` stops only at CR-family hard breaks. Character-format and individual
bold/italic/underline/strike/protected/link/caps/hidden/outline/subscript/superscript
units follow retained style-run boundaries. Detached ranges are weakly registered and
consume exact typed buffer replacement deltas, so their endpoints remain live through
typing, paste, IME, undo, and whole-document replacement without adding work when no
external range exists. Inward/outward/forward/backward gravity controls insertion-edge
tracking and run-boundary format queries. Text retrieval rejects conflicting LF/CRLF
normalization, honors grapheme-safe `AdjustCrlf`, and RTF/plain insertion applies
`Unlink`, `Unhide`, and grapheme-safe `CheckTextLimit` consistently.
RTF import retains `deff`, `deflang`/`lang`, and `deftab`; document defaults change
only when `ApplyRtfDocumentDefaults` is requested and participate in the same undo
transaction, while ordinary RTF insertion leaves host defaults intact.
The TOM story also exposes the rich-control-only implicit final CR as an undeletable
virtual EOP. It contributes to `StoryLength`, unit indexing, `Character`, UTF-32
queries, and ranges that explicitly include it, while ordinary `Text`/`GetText`
retrieval and the shared render buffer omit it. `AllowFinalEop` opts it into plain,
LF/CRLF-normalized, streamed, and RTF retrieval. A range can include the EOP, but a
collapsed insertion point is clamped before it, matching native rich-edit behavior.

Editable tables retain the rich-edit tab-backed row representation while projecting
through the same table-cell shaper, wrapper, and retained decoration primitives used
by semantic `RichTextBlock`/Markdown tables. Cell widths, padding, borders,
backgrounds, and table direction survive `RichDocument` snapshots, undo, HTML, and
RTF. Each cell wraps independently; the tallest cell determines row height; logical
UTF-16 positions, including tab delimiters, are remapped into the shared caret/hit-test
geometry. Tab and Shift+Tab move between cells through the real input dispatcher,
cross row boundaries, and Tab in the final cell appends an identically shaped row as
one undo transaction. Ctrl+Tab inserts a literal tab. RTL tables put logical column
zero on the physical right while retaining logical navigation and source order.
Merged cells are semantic `TableCell.ColumnSpan` and `TableCell.RowSpan` values. One
occupancy-aware grid assigns logical columns for both axes, measures every cell once,
and distributes a spanning cell's minimum-height deficit across its covered rows.
Nested semantic/HTML tables recursively reuse that grid and retain their decorations,
glyphs, source order, and RTL placement inside the owning cell. HTML uses `colspan`
and `rowspan`; RTF maps horizontal starts/continuations through `clmgf`/`clmrg` and
vertical starts/continuations through `clvmgf`/`clvmrg`. The editor projection keeps
standard vertical continuation placeholders in its delimiter-backed store but skips
them during Tab/Shift+Tab cell navigation and collapses them back to one semantic cell
when taking a snapshot. Each cached editor row paints its own background slice while
merge-aware border sides suppress internal horizontal seams, preserving block-level
virtualization without visually splitting the spanning cell.
`RichEditBox.SelectTableCells` adds a typed rectangular-selection extension without
changing WinUI's public `TextRangeUnit` values. `SelectedTableCells` exposes sorted,
content-only source ranges; tab and newline delimiters remain outside those ranges.
Copy/cut serialize a tab/newline matrix, paste distributes a matrix by logical row and
column, Delete/Backspace clear content without deleting the grid, and typing replaces
the selected matrix beginning at its upper-left cell. Bold/italic/underline and TOM
character formatting apply only to selected cells. All multi-cell mutations run as
one undo transaction and preserve row metadata. Vertical-merge continuations resolve
to their semantic merge origin and are never edited twice. The retained selection
overlay binary-searches sorted logical ranges for each visually bidi-reordered glyph,
so ordinary selections retain their existing `O(G)` paint path while a rectangular
selection paints in `O(G log C)` for `G` glyphs and `C` selected cells.
`RichEditTextDocument.GetRange2` exposes the concrete retained range for TOM2
extensions. `RichEditTextRange.InsertTable` validates dimensions, replaces a
nondegenerate range, inserts paragraph boundaries when surrounding text must be
isolated, initializes equal auto-fit or fixed-width columns plus the documented
0.5-point solid border, selects the first cell, and restores the original text and
paragraph kinds in one Undo. Table construction is `O(R * C)` output work and storage
for `R` rows and `C` columns; dimension and text-limit failures are checked before
allocating or mutating the document.

Rich layout resolves UAX #9 levels once for each complete semantic paragraph before
line breaking. Wrapped single-column, table-cell, and multi-column lines reuse slices
of those levels, apply the line-local L1 trailing-whitespace reset, and then perform L2
visual ordering. This preserves the first-strong paragraph base, isolates, and explicit
directional state across soft wraps while removing redundant per-line bidi analysis.
Explicit inline directions are projected through synthetic LRI/RLI/PDI controls and
mapped back to the original UTF-16 indices before shaping. Reusable `BidiData` resets
its brackets/embeddings/isolates capability state to unknown rather than false, so an
ordinary paragraph resolved first cannot suppress explicit controls in a later one.

The format layer is intentionally semantic and reflection-free. Generic
`IRichDocumentImporter<T>` / `IRichDocumentExporter<T>` adapters cover typed host
formats. `IRichDocumentFormatCodec` and `RichDocumentFormatRegistry` cover byte/file
formats by stable ID and extension. Built-in UTF-8 Markdown, HTML, RTF, and plain-text
codecs exercise both directions. RTF preserves Unicode, hyperlinks, font and color
tables, retained character styles, paragraph direction/alignment/indents/spacing,
custom tabs, numbering metadata, and inline picture bytes/dimensions/alternate text;
standard Word list tables/overrides and RTF table rows reconstruct semantic lists and
tables while preserving styled items, cells, column widths, direction, padding,
border color/width, and cell shading. HTML preserves the
semantic paragraph/list/table and inline model, nested tables, table `colspan`/`rowspan`, inherited `dir`/`lang`, heading and
paragraph metrics, common inline CSS, colors, table widths/backgrounds, and bounded
data-URI images. Unknown elements retain their textual descendants and scripts/styles
are never executed. `RichEditBox` can load a shared `RichDocument`, snapshot its
current contents, and import/export any registered codec as one undoable operation;
paragraph, list, table-column, and embedded-image metadata survives that bridge.
DOCX or application formats can register without introducing layout-specific branches.

## Adopted, adapted, and rejected

Adopted:

- whole-paragraph UAX #9 resolution, including isolates and paired brackets;
- logical source order with visual L2 run/cluster ordering;
- HarfBuzz-style direction-uniform shaping and monotonic cluster mappings;
- DirectWrite-style point/position/range hit-test geometry and caret affinity;
- Skia/Parley-style separation of semantic input, shaped data, formatting, and draw;
- Firefox-style retained source offsets and cluster-safe line/edit boundaries;
- presenter-local retained layout state and buffered block virtualization.

Adapted:

- WinUI inheritance and mirroring are implemented in ProGPU's typed visual/layout
  tree, with one centralized child-arrangement mirror, a direction-aware public
  coordinate transform, an explicit `Image` inheritance exception, and shape geometry
  mirroring. Custom-drawn controls map logical rectangles to physical draw positions
  while leaving their text glyphs unreflected;
- DirectWrite's monolithic layout object becomes block caches in one session so very
  large documents need not retain every positioned character;
- Parley's reusable scratch/context idea becomes a session that reuses child lists,
  block buffers, and viewport sets in managed code;
- external formats translate to one semantic model, so Markdown display and rich
  editing do not grow independent shapers or renderers.

Rejected:

- reversing UTF-16 input before shaping RTL text;
- reflecting over arbitrary external document models in render/edit hot paths;
- storing viewport coordinates or realized glyphs on shareable semantic nodes;
- shaping, parsing, or scheduling animation from `OnRender`;
- GPU bidi/shaping without a complete correctness and performance proof;
- clearing atlas generations or reducing raster quality to improve scroll numbers;
- per-character substring measurement for caret and pointer hit testing.

## Validation and remaining gates

Current focused coverage exercises mixed LTR/RTL runs, explicit bases, supplementary
characters, isolates/brackets, visual glyph order, hit testing, caret affinity,
selection rectangles, inherited/overridden flow, RTL child mirroring, `Image` and
shape behavior, top-right pointer and `TransformToVisual` coordinates, nested LTR
islands, centralized Canvas/Grid/Stack/Dock/Wrap arrangement, inline `Run` direction
and HTML/RTF round trips, call-order-independent bidi explicit controls, direction-aware
Slider/ToggleSwitch/ProgressBar/RatingControl/PasswordBox/ComboBox/DatePicker/
CalendarView/TreeView/ScrollViewer/DataGrid/Pivot/TabView chrome and keyboard behavior,
ToolTip/PathIcon/ColorPicker/virtualized-code-editor directional rendering,
WinUI property aliases/defaults, grapheme deletion, visual arrow
movement, shared Markdown/rich cluster metrics, nested document invalidation,
presenter cache isolation, RTF/HTML registry round trips, RTF font/color/hyperlink,
paragraph, standard list override, structural table, and picture fidelity, RTL columns,
RTL table columns, independently wrapped editable cells, horizontal/vertical merged cells,
nested semantic/HTML tables, retained table styling,
native Tab/Shift+Tab/Ctrl+Tab dispatch, transactional row insertion/deletion, typed text-document range edits and full
format interface surfaces and mixed-range sentinels, paragraph-local formatting and
split inheritance, undo/redo of paragraph direction and alignment, mixed-bidi visual
Ctrl+word/TOM selection navigation, direction-aware TOM Home/End, whole-paragraph
levels across soft wraps, right-relative RTL tabs in single/multi-column layout,
TOM word/sentence/CRLF/hard-paragraph indexing and collapse/delete/endpoint contracts,
live weak range tracking and gravity, shaped soft-line and retained format-run units,
semantic HTML direction/language/CSS/list/table/data-image import and export, and
undoable editor/shared-document snapshot and codec bridges,
custom/default tab layout and the 63-stop limit, semantic list-marker rendering and
numbered extraction, RTF and HTML native rich clipboard transfer, inline-image decoding/alternate text,
complete automation provider methods, embedded-object child ranges and per-line bounds,
notifying column metadata, incremental 2,000-paragraph editor
virtualization, viewport refresh after deep scrolling, document-coordinate hit-test
round trips, direction-run selection rectangles, software-keyboard/IME composition,
read-only input, casing, return
policy, and maximum length. The text-shaping sample has a passing 1280 x 800 headless
render with non-background-pixel and PNG assertions.

DOCX-focused validation covers Open XML schema validation, styled paragraph,
hyperlink, bullet-list, table/grid/column-span, bidi, and embedded-PNG round trips;
the sample saves binary DOCX through `StorageFile` and reopens it for editing. A
2,000-paragraph regression jumps from the initial realized window to paragraph 1900,
asserts that the viewport range advanced, round-trips a client point to the target
logical range, and keeps realized blocks bounded below 300.

Validation completed on the same source tree:

- `ProGPU.Samples` Release build: zero warnings and zero errors.
- `ProGPU.Tests`: 2,177 passed, zero failed/skipped.
- `ProGPU.Tests.Headless`: 192 passed, zero failed/skipped.
- focused bidi/editor/document coverage: 126 passed.
- `Text Shaping Lab` headless capture: 1280 x 800, non-empty pixel assertion and
  PNG creation passed; the initial viewport was inspected for clipping and contrast.
- browser Release AOT: all 72 eligible assemblies AOT-compiled and the native
  WebAssembly link completed with the typed browser clipboard and binary-save imports.
  Existing
  dependency-property/third-party trimmer warnings remain unchanged.
- real Chromium AOT smoke: the gallery initialized WebGPU, reported `HR: ready`,
  rendered continuously without console errors, navigated to `Text Shaping Lab`,
  and visibly rendered the live shaping controls and glyph preview.
- native `Text & Documents` sustained controlled scroll pass after shaping-safety
  integration: 20,000 paragraphs, 60 warmup plus 600 measured uncapped frames,
  181.54 wall FPS, 0.2650 ms average compositor time, 0.0297 ms average layout time,
  6.7643 ms worst compile, zero compile frames over 16.667 ms, 19,865 allocated
  bytes/frame, 594/600 scene-cache hits, and 41 realized paragraphs in the final
  sample. This is a current-machine sample, not a cross-machine comparison baseline.

Remaining format-depth gates include lossless preservation of Word features without
a counterpart in `RichDocument` (headers/footers, comments, footnotes, tracked
revisions, fields, and section/page setup), standard nested RTF/TOM editor tables,
full browser-grade HTML error recovery/CSS cascade,
remote-resource image loading, and format-specific features that have no semantic
counterpart in the current `RichDocument` model. RichEdit table metadata and visual
formatting round-trip through the semantic snapshot/codec bridge and direct cell
navigation/row creation plus horizontal and vertical merged-cell metadata are
implemented, including seamless cross-row merged border/background painting in the
live editor. Programmatic rectangular cell selection and its
copy/cut/paste/delete/format/undo semantics are implemented; a direct pointer gesture
remains a separate interaction gate until a documented WinUI/Rich Edit gesture can be
matched rather than invented. Linux native clipboard interoperability
remains plain-text because common Wayland/X11 command providers do not expose one
portable synchronous multi-flavor ownership contract; rich copy/paste remains intact
inside the process. The controlled scroll pass establishes a reproducible workload and one current-machine sample;
cold-start/first-layout distributions and longer target-hardware percentile runs still
need baseline/comparison captures before making broader performance claims. Any
repeatable quality or performance regression remains a blocking issue rather than an
accepted tradeoff.
