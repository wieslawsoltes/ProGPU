# Source hinted text ownership

The optional `IPortableHintedTextFormatting` capability carries the original
hinted paragraph into a source adapter without selecting WPF Display mode. Its
independent paragraph and glyph-run references own one native resource generation.
The ordinary `IPortableTextFormatting` and `IPortableTextParagraph` contracts are
unchanged; neither a `TtfFont` annotation nor an untyped replacement object stands
in for hinted geometry.

The native UTF-16 overload reuses `NativeTextParagraphSnapshot.DecodeUtf16` and
`MapStyles`, including the original scalar indices and digit/bidi policy. It calls
the existing complete-paragraph producer once. It neither shapes suffixes nor
introduces a second layout algorithm. Source adapters must pass real source
metrics and explicit per-style physical em, phase, interpreter and variation
ranges plus matching normalized 2.14 shaping coordinates. The source does not
normalize axes; the native producer verifies their original instance. A policy
not represented by the native producer must reject before
source admission.

`PortableHintedTextLine` copies the actual native baseline, height and horizontal
writer pen origin. It does not invent a line top: retained boxes and carets keep
their own original measured frames, and empty-row navigation remains unsupported.
Every glyph occurrence retains its original positioned/logical/run/descriptor,
font/style and UTF-16 identities. Glyph-run acquisition copies and validates exact
positioned indices in caller order, including reordered RTL and no-ink slots.

The LibreWPF adapter owns the prepared `NativeHintedGlyphResource`, leaving it
live while any source reference can acquire another use. Original contexts and
paragraph wrappers can retire after resource creation. Source handle disposal
rejects new uses of that handle; siblings continue to own the same generation.
Final teardown faults retain the exact owner for retry without decrementing a
use twice. Native import and rendering still need explicit connections to these
references; a copied metadata snapshot alone is not a render lease.

Preparation copies source text and neutral metadata once in O(T + G + L + I)
time/storage for UTF-16 length T, positioned glyphs G, lines L and interaction
records I. Run acquisition copies O(K) original indices; retaining an existing
reference is O(1). No GPU target, pipeline, submission or font decoder is created
by acquiring a reference. These are complexity statements, not measured speedups.

## Architecture and qualification

This adapter follows the separation already researched in
[retained hinted glyph replay](retained-hinted-glyph-replay.md). The public
[Skia shaped-text model](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite/Direct2D layout/render separation](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite),
and [Win2D retained layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
inform the choice to retain one formatted generation and its interaction.
[WebRender font-instance identity](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html)
informs explicit device settings;
[Parley layout concepts](https://github.com/linebender/parley/blob/main/doc/concept.md),
[Vello architecture](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md),
and [HarfBuzz shaping responsibilities](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
support keeping original shaping/layout separate from GPU consumption. No foreign
implementation is copied. Startup/lazy pipelines, atlas eviction, visibility,
upload/batching, worker scheduling and device-loss handling stay in the existing
renderers. This seam changes none of those policies and claims no performance win.

Focused capability and lifetime controls are authored. They do not qualify loaded
native/font execution, pixels, source editing or application startup. Source
GlyphRun publication, authoritative hinted ink bounds, managed/recorded and native
MIL replay bindings, continuation/collapse, tabs/objects, empty rows and transformed
rendering remain separate work. Both renderers and stock/Dawn packages require
their original complete gates. Keep WPF's Display rejection until that work and
independent Windows source comparisons are complete.

The native package paragraph fixture also compares every retained snapshot from
the UTF-16 overload with the existing scalar entrypoint across its original
mixed-style, supplementary UTF-16, bidi/digit and hard-break inputs. These loaded
producer checks are authored but unexecuted here. A narrow source-only Release
compile of the companion adapter and new test sources passed with zero warnings
and errors; no native/font/GPU calls or test cases were executed.
