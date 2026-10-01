# Source-preserving native text continuation reflow

## Core application dependency

LibreWPF's **ProGPU.Wpf.ToolkitApp** presents its native MIL frame, then a
TextBlock arranged inside the real Toolkit/AvalonDock tree reformats a retained
continuation at 203.23666666666668 DIPs instead of its measured 217 DIPs. Source
index 38 matches the original continuation. Rejecting every width change prevents
the existing live input acceptance gate from progressing.

## Shared native contract

`progpu_native_text_context_layout_continued_flow_paragraph` reuses the original
full-paragraph shaping, physical font/style resolution, contextual runs, bidi,
cluster breaks, justification classification and measured-object pipeline. A
binary search then selects the requested **original shaped cluster boundary**.
Only placement consumes a suffix; no isolated suffix is reshaped and no WPF-local
composer guesses line breaks or glyph widths. Original glyph, font, UTF-16 cluster
and scalar identities are retained. Layout Y starts at zero for the new suffix.

The existing flow/inline capacity queries and scratch arena remain authoritative.
`shaped_glyph_count` describes the full original shaping operation; `glyph_count`
and lines describe the returned continuation. Input start must be a nonnegative
existing shaped boundary, not a surrogate/ligature interior or an inferred nearby
position. Invalid boundaries clear result counts without publishing glyph/line
output. All spans remain synchronously borrowed under the original context lease.

Optional measured styles and U+FFFC objects use the same native measured layout.
Objects before the continuation stay in shaping context but are not published as
remaining placements. Objects in the suffix retain original source positions,
glyph/line ownership and metrics. Standard collapse can place its sign on a
continued paragraph through the existing collapse contract; its logical lookup
accounts for nonzero original glyph-index origins without weakening glyph checks.

Work remains full-paragraph shaping plus suffix layout, with O(log G) boundary
lookup for G logical glyphs and the existing bounded scratch. This is not a new
CPU raster fallback. SIMD policy is unchanged; binary-search branches are a
dependent metadata lookup, not a new whole-buffer scalar compute kernel.

`NativeTextParagraphSnapshot.CreateContinued` preserves original cluster ends,
bidi levels and native caret/selection output. The optional neutral
`IPortableReflowTextParagraph` capability lets source adapters retain the actual
paragraph/provider instead of re-querying a registry or rereading mutable source.
The WPF provider retains inline input and a bounded one-entry reflow cache; its
source TextLine wraps the native suffix over the original full source map/styles.
Disposing an earlier line or provider override does not discard that paragraph.

Exclusion/float reflow still needs retained fragment-placement semantics and is
explicitly rejected. Measured collapse still needs its existing sign-metric
contract; justified collapse's unchanged-advance requirements are separate from
supported justified continuation layout. Neither is silently approximated.

## Validation and remaining application gate

The additive hinted-resource operation is separate from the context operation
above: `progpu_native_hinted_glyph_resource_reflow` reuses already-owned original
shaping and hinted runs, so it performs no new shaping or hinting at all. Its
managed `NativeHintedGlyphResource.Reflow` returns an independently owned suffix
with original global source/glyph identities and newly measured frames/interaction.
It inherits the original execution settings and optional nominal metrics. See
[source hinted ownership](source-hinted-text-ownership.md#retained-width-changing-continuation)
for lifetime, atomicity and remaining gates. Native continuation controls for this
new hinted operation are authored and syntax-checked, not executed; the historic
ordinary continuation results below do not qualify it.

- Both native providers compile; all 19 local native suites pass. The C interop
  fixture compares original full-paragraph glyph identities across widths and
  directions, tests measured object continuations and rejected boundaries.
- The managed consumer compiles without warnings and passes 28 continuation
  layouts: two widths, both paragraph directions, left/justified layout, styled
  Latin/mixed-bidi/tab/surrogate input and four measured-object starts. It checks
  original glyph/font/cluster/bidi identities, complete remaining input, native
  interaction, ordinary continued collapse and surrogate-interior rejection.
  This fixture runs from the existing NuGet consumer, hence the existing JIT and
  NativeAOT package gates also cover it. Local execution used project references
  and current native libraries, not final NuGet qualification.
- Native ABI/generated contracts and both export allowlists verify. No MIL wire
  layout, public numeric record or generated coverage ledger was changed.
- The diagnostic Toolkit live gate now passes its former continuation rejection
  and reaches filter focus. It then fails on a separate rectangle-width rejection
  in MIL translation. This is not full Toolkit or final package acceptance.

Finish source regression execution, that concrete rectangle failure, the same full
Toolkit/AvalonDock live path and final exact package/platform/CI qualification.
No dependency pin or merge follows from the diagnostic result alone.

## Provenance and implementation applicability

Original ProGPU code, extending the existing paragraph core and snapshot/collapse
transport at `a8afeab6`. No third-party implementation was copied. The wgpu-native
and Dawn libraries share the same C++ text implementation and additive export.
Both WPF portable renderer modes consume the same typed text provider; Windows
MIL retains its native text path. There is no separate C# layout approximation,
GPU shader change, quality waiver or new renderer fallback.
