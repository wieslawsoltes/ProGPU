# Measured inline paragraph interaction

## Acceptance dependency

ShowcaseApp's unchanged rich document requires selecting text around and
interacting with its real inline Button. The existing native measured paragraph
producer retains unequal line heights and actual baselines. The old interaction
API interprets `baseline_y` as a line top, so passing those new outputs directly
would place hits, carets and selection below the real line. This checkpoint
closes that native API/binding mismatch, not the remaining source-control path.

## Shared implementation

Added `progpu_native_text_interaction_get_measured_requirements` and
`progpu_native_text_interaction_build_measured`, with matching span-based
`GetMeasuredRequirements`/`BuildMeasured` managed bindings. Both instantiate
the existing interaction algorithm over the caller's positioned glyphs, lines,
cluster ends and bidi levels. Request/result wire layouts and ABI versions are
unchanged; generated C# remains authoritative for those records.

Measured line tops use a double-precision prefix of actual line heights from
zero, including empty lines, matching the paragraph writer. Baselines must lie
within those boxes; negative/nonfinite metrics and cumulative coordinate overflow
are rejected before array writes. Zero measured heights remain zero. Object
glyph/font sentinels remain non-ink metadata: interaction requires no font lookup,
device, glyph atlas or texture. Existing C and C++ entry points retain their
baseline-as-top and minimum-one-unit height behavior.

Requirements, capacity checks, cluster grouping, RTL affinity, selection and hit
queries remain shared rather than duplicated. Work stays allocation-free
O(G + L), for G positioned glyphs and L lines, with caller-owned output buffers.
The added line-prefix validation is dependency-bound, not a parallel CPU fallback;
existing native shaping/measurement SIMD paths are unchanged. No GPU readback,
managed Y correction, per-glyph crossing or new raster algorithm is introduced.

Provenance and public-contract research are retained in
[measured text lines](native-mil-measured-text-lines-2026-09-10.md) and its linked
inline/anchor design. The original managed StyledTextLayout already positions
inline boxes against measured baselines; it needs no rendering change here.
Both native providers compile the same added C API implementation and exports.

## Verification

On macOS arm64:

* Both native provider libraries rebuilt successfully.
* Native text and C interaction suites rebuilt and passed (2/2). Added coverage
  includes tall object identity, unequal heights, an intervening empty line,
  RTL hit/selection, zero heights, legacy behavior, undersized output, invalid
  baseline/height values and cumulative overflow with output preservation.
* The managed consumer built with zero warnings/errors and passed with project
  references and local native libraries. Its new check consumes actual inline
  paragraph output: line tops 0/20/62, object height 42 and width 30.25, and hit
  ownership at the second line's top. This is not a produced-package validation.
* Native contract/Unicode freshness verification passed without wire changes.

Logs are `artifacts/release-hour/measured-interaction-{build,tests-build,tests,
consumer-build,consumer,contract}.log`. The measured interaction binding is
exercised through the default native provider; rebuilding both libraries does
not itself qualify the other provider or Windows/Linux runtime behavior.

## Remaining work

Connect the measured API to the retained native paragraph snapshot, add neutral
positioned-object/source metrics, then adapt actual WPF TextEmbeddedObject
measurement, visual lifetime and original document positions. Figure/Floater
exclusion remains a distinct native document-flow requirement. Keep all source,
SDK/package, cross-platform and full application admission gates unchanged.
The separate SVG checksum merge blocker is still unresolved.
