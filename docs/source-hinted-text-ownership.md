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

`HintedGlyphGeometry.SelectOccurrences` shares the original physical arrays and
retains their producer through the existing recorded-use lease. Its indices address
the current view, but every original positioned/logical/run/descriptor identity
survives reordering, repetition and nested selection. Empty and no-ink selections
remain distinct. One opaque raster-generation identity follows all selections;
atlas keys retain original outline slots and exact DPI without dereferencing a
retired wrapper or retaining source metadata through a cache key.

`DrawingContext.TryGetHintedGlyphInkBounds` uses those original outlines and writer
positions, publishing no partial bounds on failure. Physical-to-logical division
matches the native binding and canonical Text vertex shader; reciprocal
multiplication differs at non-power-of-two DPI. Ink, advance/input and padded
raster bounds remain separate. Additive recording overloads preserve hit-owner
identity through the normal resource-owning recorder, leaving original signatures
and default-transform calls unchanged.

`ApplyWithHintedGlyphResourcesWithMetrics` applies the canonical batch once and
reads its real native counters. The channel retains them only after the complete
hinted transaction commits; a later invalid binding leaves graph, cache and the
previous snapshot unchanged. The C query returns the existing fixed-width
32-byte metrics record by value, so no output pointer can alias immutable resource
inputs. No hinted binding is counted as a manufactured canonical command. The
managed host resolves the additive query before its first graph mutation, then
reads the committed result while retaining the original import owners. Like all
channel use, callers must serialize the full operation with updates/destruction.
The original void API still performs only its original update call.

## Architecture and qualification

### Original nominal design advances

`NativeHintedParagraph.PrepareGlyphResourceWithNominalMetrics` explicitly retains
one horizontal design advance per original positioned occurrence, including
repeated and no-ink glyphs. The C preparation export and separate immutable borrow
view are additive: existing resource/import layouts and ordinary preparation are
unchanged. `NativeHintedGlyphResourceReadLease.NominalMetrics` shares the original
resource's destruction-excluding use; access copies nothing and makes no native
call. An ordinary resource rejects this property instead of inventing metrics.

Capture reuses the original ProGPU `sfnt_font_view::try_get_horizontal_glyph_metrics`
against retained immutable font bytes and the exact collection face. The original
positioned-to-logical font map selects the palette slot; records preserve both
glyph and positioned identity. This deliberately does not use
`try_get_design_advance_width`, whose legacy absent-table path supplies half an em.
Unavailable original advances and coordinate-bearing styles/shared shaping
contexts reject before any resource publication, even for explicitly zero axes.
These are raw default horizontal `hmtx` values, not HVAR-instance, hinted 26.6,
measured/GPOS, ink or caret metrics. Source variation admission remains closed.

The existing [OpenType hmtx contract](https://learn.microsoft.com/en-us/typography/opentype/spec/hmtx)
defines nominal advance in design units, including last-long-metric reuse, and
[DirectWrite design metrics](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getdesignglyphmetrics)
distinguish these resolution-independent values from hinted positioning. The
cross-engine retained-layout/instance research below was rechecked; this change
adopts original generation retention, not another layout engine. No foreign source
implementation was copied. Raster caching, startup, culling, uploads, worker
scheduling and device loss are unchanged for both managed and native renderers;
the additive metrics remain producer/source metadata, not new raster inputs.

Preparation is O(T + F + G) time and O(F + G) storage in retained faces F,
their table-directory entries T and positioned occurrences G, using cached parser
table views and dependency-bound original owner/font gathers.
Read-lease acquisition and span access remain O(1). The complete new allocation is
included in both old and new borrow alias guards. No hinting/font program is
executed to obtain or copy these advances, and no per-glyph boundary call is added.
Source baseline versus paragraph draw origin, exact offsets and owning-line/source
maps are separate from these raw metrics. The frame validation below connects the
existing nominal-offset convention; Display/source interaction remain unadmitted.

Validation for this unit: 20 focused managed lease/metadata tests passed without a
native module, font parser or GPU. The actual managed native backend built Release
with zero warnings/errors; actual native capture/interop and both native test sources
passed strict C++20 syntax checks. Authored native controls use independent hmtx
entries and TTC faces, last-long-metric reuse, exact repeats/zero-width/no-ink slots,
late missing-metric rollback, coordinate rejection, original producer retirement
and old/new alias failures. Native/font execution and complete package/application
qualification were not run. The prior source Display, caret and outline gates stay
closed; this metadata is not interaction or rendering parity.

## Original source line frames

`NativeHintedGlyphResourceReadLease.ValidateSourceFrame` makes one synchronous
producer call under its existing destruction-excluding lease. Explicit nominal
preparation is required. The producer validates original selected indices, font,
em, bidi, measured advances and source horizontal nominal offsets, requiring one
actual retained writer line. RTL nominal advances use original hmtx / UPM * em,
not hinted device advances or a reconstruction from GPOS placement. Source offset
comparisons are exact numeric equality (zero signs are equivalent); measured
advances retain exact bit identity. No epsilon, snapping or prefix reshaping occurs.

The typed result separates the source baseline from the paragraph draw translation
and baseline-relative ink translation. The native line's actual baseline owns Y
conversion. Cross-line selections, vertical layout, nonfinite or unrepresentable
frames and every later invalid offset fail without output publication. Alias guards
protect all original allocations and supplied input spans. Old preparation and
binding signatures retain their existing behavior; new neutral default methods
reject unsupported providers rather than ignoring nominal/frame requirements.

The companion real source GlyphRun now uses the full overload, snapshots all
source offsets before validation, and consumes returned translations through the
existing recorded/native MIL paths. Its explicit publication accepts only the
existing Ideal nominal-offset convention. This is not ordinary Display selection,
Display metric rounding, source caret/cluster-map ownership or public outline
admission. Preparation and validation never change renderer raster/cache policy.
Validation is O(lines + selected occurrences), bounded temporary source transport
is O(selected occurrences), and native validation allocates nothing. It performs
no font loading, shaping, reflow, device work or per-glyph boundary crossing.

26 focused managed metadata/lease/frame-marshalling controls passed with no native
module, font parser or GPU, and the real managed backend compiled without warnings
or errors. Actual source and reference PresentationCore compiled with zero warnings
or errors using an explicit external dependency remap; all 56 focused adapter and
neutral controls passed, zero skipped. Actual native validation/interop and authored
independent arithmetic/producer/alias fixtures passed strict C++20 syntax checks
only. Native arithmetic/font execution, package pixels and application behavior
remain unqualified. Both source Display and caret/outline gates stay closed.

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

Focused capability and lifetime controls pass. They do not qualify loaded
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
and errors. All 29 focused source contract/admission/lifetime tests and all 44
selected-geometry/recording/cache tests passed without skipped cases. The latter
includes four exact fractional-DPI division controls. Strict C++20 syntax checks
passed for the actual MIL implementation, C interop and hinted MIL test source;
the managed native backend compiled with zero warnings/errors. Loaded C and both
provider package controls compare all metrics to an independent canonical update,
test replacement/empty batches and preserve original input/tail/rollback controls;
these new native/font/package cases are authored but not yet executed.
