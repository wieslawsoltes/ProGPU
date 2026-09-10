# Native paragraph flow around resolved exclusions

## Concrete acceptance dependency

The unchanged LibreWPF Showcase Figure/Floater content needs surrounding text
to span successive rows around native anchor boxes. This batch connects the
existing measured band placer into try_layout_excluded_logical_shaped_text.
It operates on original shaped paragraph input and already resolved exclusion
rectangles. Anchor sizing/placement and source subtree admission remain separate.

Each row starts from the source first-item/minimum height. A height mismatch
refits without consuming text; a blocked band advances to its real next-Y hint.
Accepted rows emit positioned glyphs through the shared writer and retain
text_fragment_placement records with row identity, explicit top, left and width.
The ordinary positioned line record remains paired with each fragment.
MaximumLines counts rows; next_glyph records a limited paragraph's unconsumed
source and the final fragment is marked clipped when that limit is reached.

Explicit tops are authoritative. Prefix-summing fragment heights would stack
same-row fragments and lose vertical clearance gaps. Existing prefix-based
interaction must therefore not consume this output without a fragment-aware
connection.

## Failure and performance contracts

An explicit attempt budget (1..1,048,576) bounds placement/refit/clear work.
Exhaustion or a non-progressing blocked band returns verification_failed with
no valid output prefix. A documented regression alternates between height 10
and height 25 because an exclusion changes which glyphs fit: this remains a
rejected compatibility case, not a successful conservative-height layout.
Zero-height seeds are also rejected rather than replaced with an invented
epsilon. These cases still require semantic completion before full parity.

The pass has no allocations, retained pointers or per-row C ABI calls. It hoists
full-paragraph scale/bidi/metric validation out of repeated rows and refits.
Fragment-local validation and output use the existing native writer and SIMD
metric/coordinate paths. Row prefixes use double precision; emitted fragment
coordinates retain their actual native float frame. Exclusion processing remains
O(E log E) per attempt, with the fitting prefix rescans described in the
[band fitting record](native-mil-anchor-band-fitting-2026-09-10.md).
No benchmark, cache/upload change or fastest-path qualification is claimed.

## Reuse and evidence

Original ProGPU provenance is the band placement implementation at 14c95fb2.
The [placement design](native-mil-anchor-band-placement-2026-09-10.md) and its
linked cross-engine research remain applicable; no foreign code is introduced.
Both native providers share the same text library. Both WPF rendering modes
use the native text provider; neither has source anchored layout admitted yet.
No reduced managed implementation is substituted.

Release native text compilation and the complete include-based test suite pass.
New cases cover blocked-area clearance, two fragments per row, original glyph
indices and output offsets, row limits, taller-item refits, finite attempt
failure, an actual refit cycle, and matching ordinary measured text output when
the exclusion set is empty. The named-module consumer builds and runs against
the same text/compression archives, including an empty paragraph call.

Logs: artifacts/anchor-paragraph-build.log, artifacts/anchor-paragraph-tests.log.
Module artifacts remain in artifacts/anchor-text-module.FxeqPf.

Remaining: fragment-aware caret/selection/hit geometry and retained transport;
native anchor sizing/positioning; source subtree/position ownership; completion
of nonconvergent/zero-height cases; unchanged application acceptance; final
both-provider package, Windows/Linux/macOS and PR CI qualification. Existing
source rejection, runtime defaults and release gates remain unchanged.
