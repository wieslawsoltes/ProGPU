# Native explicit CPU glyph coverage

Implementation only; no compiler, test, fixture, browser, font execution,
benchmark or GPU validation was run. No build or capture artifacts were created.

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_execution.cpp` (CPU roots/crossings/lanes/arena, lines 1–851; explicit uploads, 1439–1485) | `9b131d1891faa278bb217a4ffb5558a426185dbc9ce2c48e15c5195d8dedd51b` |
| `src/ProGPU.Native/src/Backend/progpu_native.cpp` (mutually exclusive glyph engine flags) | `e124d2f9c53fe67c9fc8ab3b17094514dda00dcb28888678a8eae3fcd331c989` |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_raster_identity.hpp` (existing shared source raster identity, unchanged) | `e5da3db8815e7eb7cd73791a1f4419e4d73af11f2f53dcf6672c64f74f03d1cb` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_binary_glyph_contract.hpp` (existing independent binary admission, unchanged) | `7f6d3c2a1c003ef60c9282371db2b262ff813d1cc4fa1ec5cfc123a4e6b14de4` |

`NativeGlyphCpuCoverage::compile` consumes an actual immutable
`NativeGlyphRasterPage` and requires an explicit original scalar or intrinsic
CPU engine flag. It retains that page and publishes the complete zeroed coverage
arena, original unique-work upload ranges, selected policy, actual implementation
kind and metrics. It preserves the existing source-indexed aliases, original
segment flags, outline bounds, source placement and no-ink inventory.

The scalar route retains its own per-sample winding traversal and division-based
sample coordinates. The intrinsic route retains whole-inventory curve metadata,
the original 24-crossings-per-segment reserve, eight scanline boundaries,
reciprocal-multiplied coordinates, paired 16-sample pixels and separate 8-sample
odd tail. Both retain original root order, cubic power/trigonometric expressions,
endpoint tolerances, derivative evaluation, strict crossing comparison, nonzero
winding, and `(samples * 255 + 32) / 64` coverage. No FMA or rewritten roots are
introduced. Signed-zero-sensitive min/max retains the first source operand.

Safe `wide` lanes represent the original NEON/SSE2 masks and integer windings;
the rendering crate adds no unsafe code. Source targets without either original
ISA retain the original cached-crossing scalar branch (including wasm), rather
than being relabeled SIMD. `NativeGlyphCpuImplementation` exposes the actual
branch; portable lanes are named separately when the dependency cannot provide
native instructions for an otherwise selected source ISA. These facts do not
qualify architecture-specific floating transcendental results or font pixels.

`NativeGlyphPageEngine` now selects this producer only for the explicit CPU
flags. It completes CPU coverage and all source ranges before preparing private
GPU owners, then writes each original trimmed-last-row range into its owned R8
atlas. Default compute/fragment execution stays separate; binary and RGB reject
CPU selection as before. CPU preparation emits no synthetic compute or raster
pass. Original accounting reports the complete arena in `outline_upload_bytes`;
`NativeGlyphCpuMetrics::texture_write_bytes` separately reports actual queue-write
lengths. Prepared pages retain source/atlas/paint/instance owners through the
existing submission path and expose the CPU implementation and metrics. The
temporary arena retires after the queue has copied its upload bytes; warm output
reuse remains the outer engine's actual generation/cache contract.

An adjacent RGB correction adds `NativeRgbGlyphPage::compile_for_target` and
`actual_target_size`: actual pooled attachment size controls composite projection
and GPU admission, while source translation, logical extent, scissor and mask
frame remain unchanged. The original 4096 target bound still applies. This closes
an incorrect equality requirement between allocation and occurrence extent;
it does not infer opacity or manufacture a new source frame.

Original provider/package/source-application/browser/pixel/performance gates
remain independent and unqualified by this implementation increment.
