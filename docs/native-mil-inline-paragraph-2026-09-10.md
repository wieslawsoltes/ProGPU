# Native measured inline paragraphs

The shared native paragraph now accepts measured inline objects through additive
requirements/layout C APIs and span-based NativeTextShapingContext bindings.
NativeTextStyleMetrics and NativeTextInlineObject wire records are generated
from progpu_native_text_flow.h. Both native providers compile the same code.

Explicit source-resolved physical style runs are required for nonempty input,
with one source-owned DIP ascent/descent pair per style. This bypasses native
fallback discovery for objects while preserving actual source font policy.
Ordered object descriptors cover every actual U+FFFC input scalar exactly once.
The paragraph splits shaping runs at those positions but retains its complete
Unicode bidi/break analysis. No surrogate text, placeholder font glyph or UI
pointer crosses the native boundary.

Objects remain positioned non-ink items with glyph_id UINT32_MAX-1 and
font_index UINT32_MAX. Their original source cluster and fractional advance
survive wrapping and visual order. Glyph y is the measured line baseline;
object top is baseline minus its declared ascent. Consumers must explicitly
handle this identity before any font lookup or glyph atlas operation.

The measured writer introduced in 64e3d57b now feeds paragraph output. Measured
extent uses the sum of line heights, not the legacy baseline-plus-height rule.
The original object-free APIs preserve their baseline and scratch contracts;
the extra item-metric scratch is allocated only by the new entry points.
Trimming remains rejected until sign metrics and object truncation ownership
are implemented. Source control hookup, native snapshot interaction and
Figure/Floater placement/exclusion are still required.

Validation is O(S + O + R) for source scalars, objects and styles, with intrinsic
NEON/SSE2 independent metric lanes and dependency-ordered source scans. Layout
reuses the existing native pipeline and caller scratch. Managed bindings retain
the context lease, synchronously pin spans, validate style-metric capacity and
use generated LibraryImport declarations. No per-object crossing, retained
caller pointer, GPU readback or new render fallback is introduced.

The original managed provenance and primary-source research are recorded in
[measured lines](native-mil-measured-text-lines-2026-09-10.md) and its linked
LibreWPF inline/anchor design. Managed StyledTextLayout already supports
baseline-aligned boxes; this native producer extends that common behavior,
not a replacement managed composer or full rich-text parity claim.

## Checkpoint evidence

- Native C API tests pass text/object/text with fractional width, three-line
  wrapping, tall line metrics, correct extent, RTL object identity, zero width,
  intrinsic widths and invalid source/metric/trimming rejection.
- Both native provider libraries rebuild successfully. CTest reports 20/20
  passing suites. Logs: artifacts/release-hour/inline-text-build.log,
  inline-text-tests.log and inline-text-ctest.log.
- Generated native contracts verify. Managed binding and local project-reference
  consumer build with zero warnings/errors. The full default consumer passes
  its new inline geometry/span validation plus existing renderer checks.
  Logs: inline-text-contract-verify.log, inline-text-consumer-build.log and
  inline-text-consumer-tests.log in the same artifact directory.

These are local component/development-consumer results, not source-independent
all-RID package or Windows application qualification. Producer 38b6a7a4 CI
separately exposed a missing exported MIL static Direct2D dependency in the
native SDK package. Fix that packaging graph before advancing downstream pins;
do not waive the consumer or treat successful dynamic-library rendering as
proof of a linkable static C++ SDK.
