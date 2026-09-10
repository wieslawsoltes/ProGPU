# Measured native anchor placement

The acceptance consumer is LibreWPF's unchanged RealXamlCompilerHarness document,
executed by RealApplicationRunHarness. Its Figure and Floater retain child
Paragraphs and default dimensions. This checkpoint implements shared native
placement and width-policy prerequisites, not source admission.

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

## Two-pass width policy

`try_resolve_text_anchor_width` distinguishes fixed, fill and fit-content modes.
The source first resolves the available reference width and horizontal insets.
The initial native constraint precedes real subtree formatting. Fit-content then
uses the actual measured child extent to shrink that constraint and explicitly
requests remeasurement; it never rescales existing lines. Fill preserves its
available constraint regardless of a narrow child. Fixed width is constrained by
available space. Exhausted content width stays zero, with insets retained as
real outer overflow. Content overflow does not enlarge the fitting constraint.

This distinction was traced in LibreWPF's existing `FigureHelper`,
`FigureParagraph` and `FloaterParagraph`: auto Figures can require a second child
format, while Stretch Floaters do not shrink. The shared function contains no
PTS calls or WPF policy names. Source page/column units, margins, rounding policy,
actual subtree measurement and height remain consumer/transport work, not guessed
from text length or a substitute TextBlock. The caller must retain the first-pass
constraint and use the returned remeasurement width; repeatedly shrinking from
new measurements is not an implicit convergence algorithm.

Metric validation uses four independent NEON/SSE2 lanes and a fixed-size scalar
reference on other targets. Width selection is allocation-free O(1). Focused
tests cover initial and measured fit, fill, fixed width, overflowing child extent,
zero available width, inset overflow, invalid metrics/enums and atomic failure.
The updated macOS ARM64 native text target compiled and its CTest passed
(1/1, 0.51 seconds); no source application or package admission follows.

## Batched width transport

`progpu_native_document_resolve_anchor_widths` transports the shared policy in
24-byte requests and 16-byte results, generated into the existing managed
document contract. Mode and measurement flags are validated before enum/bool
conversion. Aligned disjoint spans are bounded by the existing document item
budget. Two linear passes avoid temporary allocation while retaining whole-batch
failure atomicity. Inputs must stay unchanged for the synchronous call.

`NativeDocumentFlow.ResolveAnchorWidths` pins the spans once and selects the
existing wgpu-native or Dawn library explicitly. It does not initialize a GPU or
measure source content. Both export allowlists include the new entry point.
The focused C fixture tests layouts, values, later-item rejection, invalid mode
256, short output capacity and empty input. The native-backed managed consumer
checks both providers and retained output after a later invalid request.

Local evidence: both native libraries compiled; document-flow CTest passed
(1/1, 0.29 seconds); managed backend and consumer builds completed with zero
warnings/errors; both export allowlists passed; the MIL-only consumer passed
including both-provider anchor checks. This uses local project-reference builds,
not exact-head published package provenance. Placement transport, source child
measurement and Figure/Floater interaction remain required.

## Source-ordered placement transport

`progpu_native_document_place_anchors` and `NativeDocumentFlow.PlaceAnchors`
now carry measured positive outer boxes and their resolved reference frames.
Each successful placement joins the original collision exclusions for subsequent
anchors; input order is source order, not a sort by geometry. Three bounded
temporary arrays are allocated once for the changed batch. The shared native
interval/placement algorithm owns all collision fitting; managed code only pins
generated 40-byte requests and 16-byte rectangles. Results are copied out only
after every anchor fits. Later invalid flags or unsupported fits leave every
output unchanged. Existing exclusions are validated even with no anchors.

Space is O(E+N); time follows the shared per-request placement bound over E+i
rectangles for request i, including its explicit attempt budget. This is not
per-frame work or a per-anchor native crossing. SIMD validation and ordered
progression remain in the shared core. Source wrap-side policy must separately
produce the text exclusions; collision rectangles do not establish that policy.

The C fixture and both-provider managed consumer cover chained placement,
unchanged output after a later invalid request, non-delaying rejection and ABI
sizes. Native libraries and CTest built/passed locally (1/1, 0.36 seconds).
The managed consumer built without warnings/errors and passed against both
local providers; both exported-symbol checks and generated contract verification
passed. These are project-reference checks, not published package qualification.
Source-owned child measurement, original TextPointer mapping, retained drawing,
neutral provider admission and full application qualification remain unfinished.

## Neutral provider contract

`IPortableAnchoredDocumentFlow` is an explicit optional extension of the existing
document-flow service. Sequential request/result layouts mirror native width and
placement batches; width mode and alignment use typed uint-backed enums. Flags
retain native 0/1 validation. The source must require this capability before
formatting anchored subtrees; ordinary block placement is not a fallback.
The interface retains no child objects and does not transfer source positions.
An implementation/ABI fixture and actual source consumer remain separate work.
