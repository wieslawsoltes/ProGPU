# Batched native font device advances

`NativeTextShapingContext.GetDeviceAdvances` now exposes the existing validated
OpenType `hdmx` records through one synchronous C call. The primary face is index
zero; other indices are the context's actual registered fallback faces. Results
are exact integer device-pixel widths represented as floats, in caller glyph
order. They are not design-unit advances, bearings, ink bounds or a shaping result.

## Ownership and validation

The native context retains a fixed 16-entry cache keyed by font index and exact
integer ppem. Both successful records and absent sizes are cached. Entries borrow
only the context's immutable font bytes, and survive fallback-vector growth;
eviction discards metadata, never font ownership. The existing managed use lease
serializes the entire query against context mutation and disposal.

Every glyph index is validated before any advance is written, including when the
font has no requested record. Alignment, count/capacity, address-range overflow,
font index, ppem narrowing and disjoint input/output/status storage are checked.
Missing records return successful unavailability without touching advances.
Invalid requests preserve all advances and clear availability; invalid status
alignment or overlapping spans preserve all caller storage instead. Successful
queries write exactly the requested glyph count and retain every unused tail.

NEON/SSE2 validate four IDs and convert/publish four widths at a time, with a
bounded scalar tail. Sparse byte gathers use scalar loads because these baseline
intrinsics lack the required arbitrary byte-gather operation. Targets without
either instruction set use the scalar reference loop. No GPU device, upload,
readback, thread-pool task or per-glyph interop is involved.

For G requested glyphs, T SFNT directory entries, R device records and S bytes per
record, a cold lookup costs O(T + R*S + G). A warm lookup costs O(T + G): the
glyph-count lookup still reads the font directory, while the 16-entry cache avoids
rescanning the device table. Additional context storage and working space are
bounded O(1). The one crossing borrows 4*G input bytes and writes 4*G output bytes;
it copies no font data. This is not an application performance qualification.

## Provenance and architecture review

The implementation is original ProGPU code over the previously implemented
[`hdmx` reader](native-font-device-metrics.md), using the published
[OpenType field contract](https://learn.microsoft.com/en-us/typography/opentype/spec/hdmx).
No third-party implementation or new dependency is included.

- [DirectWrite's GDI-compatible metric API](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getgdicompatibleglyphmetrics)
  makes device scale and measuring policy explicit. This API retains exact ppem
  identity, but deliberately does not claim that a cached width is the complete
  DirectWrite metric result. [Win2D glyph metrics](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Text_CanvasGlyphMetrics.htm)
  likewise distinguish advances and bearings; the new output contains only advances.
- [HarfBuzz's face/font lifetime and sizing model](https://harfbuzz.github.io/fonts-and-faces.html)
  separates reusable font data from size-dependent behavior. Adopt that separation
  through the existing owned context and a bounded size cache, without introducing
  another font owner or shaping implementation.
- [Skia's font contract](https://api.skia.org/classSkFont.html) separates linear
  metrics, hinting and baseline snapping. Keep those distinctions: neither an exact
  device record nor a successful span copy enables raster hinting or source snapping.
- [WebRender's rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  separates retained scene construction and glyph-atlas preparation. This CPU
  metadata query does not change display-list reuse, visibility, atlas eviction,
  worker scheduling, demand-driven uploads, GPU batching or device-loss behavior.
- [Parley's text-stack description](https://github.com/linebender/parley/blob/main/README.md)
  separates font discovery, shaping, metrics/outlines and layout;
  [Vello's glyph-run API](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html)
  exposes size, hinting and normalized variation coordinates independently.
  Keep fallback identity explicit and do not use these default-face cached widths
  as proof of hinted variable-instance support.

## Tests and remaining work

The standalone C ABI test covers cold/warm reads, sparse sizes, zero/255 widths,
duplicates/order, all four-lane/tail lengths, cache eviction, primary/fallback
identity, caller-font mutation, malformed later records, all invalid-index lanes,
missing records, short/unaligned/overlapping/overflowed spans and untouched tails.
Its 1,118 checks pass on macOS ARM64. The new CTest and existing hdmx reader CTest
pass, and the new test also passes with AddressSanitizer and UndefinedBehaviorSanitizer.

The managed consumer builds without warnings or errors and passes matching span,
cache, fallback, malformed-font and disposal checks. Local binding execution used
an explicitly isolated text-only native library compiled from these sources;
it is not a renderer package qualification. The same consumer is included in the
normal native package program, preserving all existing JIT/NativeAOT selectors.
Both native providers' required export lists include the new C entrypoint.
Native contract generation/ownership checks pass. Hosted cross-platform package
and module/header gates remain mandatory before merge.

Both renderer modes can consume this shared context API; no managed-only metric
algorithm, drawing fallback or renderer-specific behavior is added. Normal shaping,
layout, retained interaction and drawing are unchanged. Integration still requires
hint execution for uncached sizes, variable-instance eligibility, the device-grid
policy before fitting/positioning, and consistent source, continuation, caret and
raster consumers. LibreWPF #184 remains open and its Display guard is unchanged.
