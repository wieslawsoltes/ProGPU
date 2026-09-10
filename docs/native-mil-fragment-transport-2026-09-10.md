# Native fragment interaction transport

## Acceptance and contract

Showcase Figure/Floater wrapping requires drawing and interaction to retain the
same fragment geometry. The C entry progpu_native_text_interaction_build_fragments
and NativeTextInteractionInterop.BuildFragments now borrow positioned glyphs,
lines, source cluster ends, bidi levels and one explicit placement per line in
one synchronous call. Output retains fragment indices, actual row tops and gaps.

The public C header owns the generated NativeTextFragmentPlacement wire layout:
double top, uint32 row index, float left/width and a zero reserved uint32. Its
24-byte layout has no pointer or implicit gap; native tests assert size and field
offsets. It is not aliased to the differently ordered C++ placement structure.
The shared interaction templates consume either record type directly, with no
repacking, duplicated geometry algorithm or temporary allocation.

The existing request's exact structure size and ABI version remain mandatory.
Fragment count equals line count even for empty input. Pointer alignment, source
metadata counts, reserved zeros, nonnegative paragraph origins, finite geometry,
row continuity, shared metrics and ordered nonoverlapping intervals are checked
before output writes. Output counts are cleared on rejected input. Caller buffers
must not overlap and remain borrowed only during the call. Capacity of one box and
two carets per positioned glyph suffices; no new probing round trip is required.
Existing ordinary and measured entry points retain their coordinate conventions.

## Ownership, provenance and cost

This extends original ProGPU interaction templates from 66f6475a and the precise
fragment metadata at e06023d6. See [fragment metrics](native-mil-fragment-metrics-2026-09-10.md)
and [interaction](native-mil-fragment-interaction-2026-09-10.md) for algorithm and
research provenance. No third-party source is copied. Both native providers use
the same implementation and export the new symbol. The C# wrapper pins caller
spans and the result for the complete LibraryImport call, with no runtime marshalling.

Cost remains O(glyphs + fragments), O(1) workspace and one managed/native crossing
per interaction generation. Source topology comparisons are sequential; existing
native glyph/metric SIMD work is unchanged. This introduces no GPU fallback,
readback, per-fragment crossing, retained upload or performance claim. Both WPF
renderer modes must use the shared retained consumer when it is connected.

## Coverage and limits

Native ABI fixtures cover two fragments sharing a row, vertical clearance,
excluded-gap hit rejection, insufficient capacity, mismatched fragment count,
reserved fields, NaN/top mismatch, wrong ABI and empty input. Rejected calls keep
output canaries intact. The package consumer adds generated-layout and real
managed-to-native fragment calls using measured inline paragraph output, plus a
reserved-field rejection check.

The native text and interaction ABI suites pass. Both providers and the managed
binding compile; the project-reference consumer passes its MIL-only/guideline
smoke against the local libraries, including the new fragment calls.

Evidence logs are artifacts/fragment-transport-build.log,
artifacts/fragment-transport-tests.log, artifacts/fragment-transport-managed-build.log,
artifacts/fragment-transport-consumer-build.log and
artifacts/fragment-transport-consumer-tests.log. Project-reference consumer execution
is not exact-version package qualification.

Still required: excluded-paragraph shaping/layout C transport, retained snapshot
placement ownership and navigation transport, native anchor positioning and WPF
source subtree integration, unchanged application acceptance, exact-head packages,
cross-platform/Windows VM comparisons and green final PR CI. This change does not
enable Figure/Floater or relax an application admission gate.
