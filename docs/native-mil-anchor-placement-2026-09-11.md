# Measured native anchor placement

The acceptance consumer is LibreWPF's unchanged RealXamlCompilerHarness document,
executed by RealApplicationRunHarness. Its Figure and Floater retain child
Paragraphs and default dimensions. This checkpoint implements a shared native
placement prerequisite, not automatic sizing or source admission.

`try_place_text_anchor` places an already measured positive outer box in an
explicit finite reference frame. Left/center/right alignment fixes its X position.
Existing half-open exclusions permit exact edge contact. When delay is enabled,
the resolver retries at native exclusion-bottom events; otherwise a collision
fails. Neither sideways relocation, overlap, clipping nor an unbounded-height
substitution is implicit. The output rectangle is published only after fitting.

The implementation reuses `try_resolve_text_line_intervals`, its intrinsic
coordinate validation, disjoint borrowed scratch and native interval union.
There is no device, allocation, font lookup, callback or managed composition.
Time is O(A * E log E), with A bounded explicitly and O(E) caller scratch.
Downward event progression and interval containment are ordered dependencies;
independent rectangle coordinate checks retain the existing NEON/SSE2 paths.

Focused tests cover chained collisions, left/center/right reference alignment,
delay disabled, exact contact, exhaustion, unchanged failure output, invalid
coordinates/enums/budgets and oversized/zero-width rejection.

Local macOS ARM64 compilation of `progpu_native_text_tests` succeeded; the
corresponding CTest passed (1/1, 0.57 seconds). This is shared native text-library
evidence, not both GPU-provider packaging, C++ module or platform qualification.

Still required: source-owned automatic dimension measurement, reference-frame
and offset resolution, wrapping policy, empty anchor semantics, a batched native
transport, retained source subtree layout and original document-position mapping.
The positive-size primitive must not be used to silently discard empty content.
Default Figure/Floater application behavior, Windows comparison, package startup
and final cross-platform qualification are not established by these tests.
