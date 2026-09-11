# Core source text measurement and whole-word wrapping

## Application dependency

The existing LibreWPF native host constructs styled FormattedText before creating
a window host, and the package smoke App measures text in its constructor.
Source inspection found two real gaps: FormattedText defaults to WrapWithOverflow,
which the portable adapter rejected, and MinWidth had no native intrinsic-width
contract. This batch connects both to the existing ProGPU paragraph pipeline.
These are source-backed implementation findings, not newly executed app results.

## Shared implementation

`progpu_native_text_context_layout_configured_flow_paragraph` adds explicit
emergency/whole-word wrapping and optional intrinsic widths to the existing
styled, bidi-aware, tab-aware context layout. Old entry points and record sizes
are unchanged. Requirements and scratch capacities are shared with the existing
flow API. The new generated width record is 16 bytes; its separate pointer is
optional, initialized by struct size and published only after successful layout.
Unknown wrapping values, nonfinite metrics and truncated measurement requests
fail rather than returning partial intrinsic measurements.

The native logical scan implements whole-word wrapping by continuing an oversized
unbreakable run to a legal, shaping-safe boundary. It does not split a ligature or
silently revert to emergency cluster breaks. Existing emergency wrapping remains
the default for older callers. An unbounded width stays unbounded in either mode.
WPF Wrap maps to emergency wrapping, WrapWithOverflow maps to whole-word wrapping,
and NoWrap remains unbounded. The source adapter no longer rejects the default
FormattedText wrapping policy.

`try_measure_text_intrinsic_widths` consumes the same logical glyphs, source
clusters, Unicode break opportunities, scales and tab grid used for positioning:

- Minimum width is the largest unbreakable fragment at legal, shaping-safe breaks.
- Maximum width is the largest mandatory-break-delimited unwrapped extent.
- Trailing Unicode whitespace and zero-width break controls do not extend either
  width; internal whitespace still contributes advance. Classification examines
  a whole source cluster, so a mixed whitespace/text ligature is not trimmed away.
- Per-style font sizes and resolved tab advances remain native calculations.
  Tabs use the candidate line's grid, not a guessed count of spaces.
- Bidi reordering does not change which logical boundaries are legal. Measurement
  is independent of the requested wrapping width and runs before visual ordering.

The scan takes O(S + G) time and O(1) workspace for S scalars and G glyphs. Prefix
widths, cluster traversal, legal boundaries and tab stops depend on preceding
state, so ordered accumulation is intentional rather than a rejected GPU kernel.
The existing NEON/SSE2 metric validation/scaling remains shared. There is no GPU
readback, per-word reshaping or per-item interop. Metrics are opt-in; ordinary
paragraph creation does not pay for intrinsic measurement. No speed claim is made.

## Typed source connection

NativeTextParagraphSnapshot retains optional NativeTextIntrinsicWidths. The neutral
PortableTextParagraphRequest carries measurement intent and a typed wrapping mode;
IPortableTextParagraph exposes optional PortableTextIntrinsicWidths. The host maps
these directly without reconstructing widths from positioned-line envelopes.

Source TextFormatter's min/max method captures one provider and reuses normal typed
text/font/style extraction. Hard lines are measured up to the paragraph terminator,
with the existing source modifier scope preserved across explicit line breaks;
the paragraph result takes the maximum per-line requirements and accounts for
source indentation. Source measurement does not construct drawing GlyphRuns,
TextLines or source-map output snapshots. The provider currently still constructs
its normal immutable native layout/interaction snapshot; a dedicated metadata-only
fast path is deferred unless measurements show it matters to the core applications.
Missing or invalid provider measurements fail explicitly. Optimal paragraph caches,
forced-break reconstruction, trimming, display hinting and other documented source
text limitations are not claimed complete by these changes.

## Architecture evidence

[DirectWrite DetermineMinWidth](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-determineminwidth)
defines a minimum without emergency word splitting. Adopt that distinction, not
a formatted line width as a substitute. [SkParagraph's public Paragraph contract](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h)
separates min/max intrinsic widths from layout width and longest line; retain
separate metrics. [Unicode UAX #14](https://www.unicode.org/reports/tr14/)
provides the break-opportunity model; reuse ProGPU's existing resolver rather
than introducing a whitespace-only word splitter.

The [cross-engine record](native-mil-text-source-integration.md) supplies the
remaining HarfBuzz, Direct2D/Win2D, Parley/Vello and WebRender comparison: keep
shaping/layout as reusable CPU results, separate from visibility, rasterization,
atlas/cache residency and GPU composition. This extension changes no font fallback,
variable-font keys, worker scheduling, subpixel/hinting policy, demand uploads,
retained scene invalidation or device-loss ownership. Both renderer consumers
share the typed service. No foreign engine implementation is copied into ProGPU.

## Authored qualification, not executed

Native fixtures cover legal versus unsafe boundaries, mixed scales, mandatory
breaks, trailing spaces, mixed-source ligatures, tab origin, nonfinite rejection,
whole-word versus emergency line counts and a real-font styled C ABI request.
Managed fixtures cover ABI sizes, leased/pinned outputs, explicit measurement
intent, source hard-line scopes, missing-provider metrics and wrapping dispatch.
The existing source-built host and SDK App now require finite positive MinWidth
no larger than unwrapped Width. The SDK conditional checks await rebuilt packages.

Only compilation is performed during this implementation-first phase. Native-WPF
and DirectWrite comparisons, full application fidelity, Windows Parallels, package
consumption, performance measurements and required CI must still be qualified at
the final delivery commits. Windows SDK admission remains guarded. This batch does
not resolve the previously reported renderer/compiler/Ubuntu CI failures.

Compile-only checkpoint: native text and C-ABI showcase targets link under
AppleClang C++20 with strict warnings; ProGPU fixtures have 0 warnings/0 errors,
source PresentationCore fixtures 8 warnings/0 errors, bridge fixtures 116 warnings/
0 errors and the source-built application harness 0 warnings/0 errors. The existing
WGPU-disabled compile tree does not provide the full-renderer interop test target;
the standalone shaping showcase compiles the real text C ABI instead. No binary
was executed. Latest fetched ProGPU main is included.
