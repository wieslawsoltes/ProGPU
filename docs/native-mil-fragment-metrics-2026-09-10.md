# Native fragment paragraph measurement

## Acceptance dependency

The unchanged LibreWPF Showcase Figure/Floater document needs paragraph extents
that retain clearance gaps and same-row fragments. The existing ordinary-line
measurement sums heights, so it cannot measure excluded paragraphs. The new
try_measure_fragment_text_lines consumes the retained placements directly;
excluded-paragraph layout uses it before publishing successful result metrics.

Content width is the maximum interval-left plus actual line width, including
overflow. A positive requested width remains measured_width; zero selects actual
content width. Content height includes explicit clearance before and between rows.
These are paragraph-local layout extents, not glyph ink or anchor subtree bounds.
Empty input has zero content extent and retains a positive measured width.

Validation checks finite nonnegative origins, positive interval widths, finite
line metrics, contiguous row indices, shared row metrics and consistently ordered
nonoverlapping intervals. Failure clears the result. Adjacent rows retain the
double layout prefix in text_fragment_placement.top rather than reconstructing
adjacency from rounded floats. The flow result retains its double height too;
text_layout_metrics exposes the final float extent. Baseline validation and caret navigation compare
against the corresponding published float frame, as interaction already does.
No epsilon or widened geometry tolerance is introduced.

## Provenance, applicability and cost

Original ProGPU implementation extends the fragment layout/interaction at
86afa6df and 66f6475a and ordinary metrics in progpu_native_text_metrics.cpp.
The [fragment interaction design](native-mil-fragment-interaction-2026-09-10.md)
and its research references remain applicable; no external implementation is
copied. The header and named module expose the same implementation.

This is an allocation-free O(fragments) scan with O(1) workspace. Row topology
checks depend on the preceding fragment; it is not a parallel CPU fallback
kernel. Existing intrinsic glyph translation and metric-envelope paths remain
unchanged. No GPU resource, upload, readback or per-fragment managed call is added.
Both native providers share this code. Managed/native WPF consumers must receive
these same retained placements through the forthcoming shared transport; neither
has new source anchor admission from this change alone.

## Regression coverage and remaining work

Tests cover two fragments per row after a vertical clearance, constrained versus
content width, actual overflow, inconsistent row tops with cleared failure output,
fractional-height row adjacency and a baseline exactly on the published top edge.
The latter first reproduced an invalid rejection in the new metric validator;
the fix aligns its coordinate frame with native interaction, without changing
the expected geometry. Interaction metadata is regenerated for the final LTR
output rather than reusing an earlier RTL fixture's cluster ends. The named-module
consumer also calls the empty-input API.

The Release native text suite and named-module consumer pass, and both wgpu and
Dawn libraries compile. Build/test logs are artifacts/fragment-metrics-build.log,
artifacts/fragment-metrics-providers-build.log and
artifacts/fragment-metrics-tests.log. These are focused native checks, not final
package, platform, application or performance qualification.

Remaining work includes C ABI/generated managed transport and snapshot ownership,
anchor sizing/positioning and source subtree integration, bounded nonconvergent
and zero-height cases, unchanged application acceptance, final platform packages,
rendering comparisons and green exact-head PR CI. The C++ placement layout change
does not alter a published C wire record; future wire declarations must be explicit
and generated rather than aliasing this C++ structure.
