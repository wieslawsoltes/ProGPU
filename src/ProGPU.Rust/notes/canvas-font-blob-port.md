# Original canvas font and text-blob factories

`text::canvas_font` retains an actual `ManagedFont` generation and original
Size/ScaleX/SkewX, hinting/edging and independent font flags. Explicit empty faces
stay distinct from system-default font selection; no platform font is invented.
Metrics preserve original integer-unit scaling, optional underline/strikeout
records, source x advance and x/cap-height fallback arithmetic. Nominal glyph
advances, positions, widths, measured bounds and source-byte breaking preserve
ordered float sums. Bounds keep the source SKRect zero-record empty semantics,
not a replacement positive-area rule.

The encoded reader preserves strict UTF-8, native-endian UTF-16/32 and original
glyph-ID transport (including ignored odd trailing byte). Complete malformed
Unicode returns the original empty/no-write result; encoded containment preserves
its original true result for empty/malformed input. Integer codepoint arrays keep
their independent valid-scalar compaction/zero-tail behavior. Safe slices replace
raw pointer overloads. Font parsing failures remain typed errors, distinct from
the original malformed-text policy. The reader is O(N) with bounded per-element
decoding, without repeated decoding of whole suffixes or an external text engine.

Glyph paths consume actual flipped managed outlines and retain independent source
scaling/skew expressions. Synthetic bold uses the original Paint fill-path and
winding normalization producer, keeping nominal advance unchanged. Canonical
64-unit callbacks and text/path-positioned outline factories retain analytic
curves and actual glyph owners. Warped text uses the original first-contour
measurement, per-glyph path cache, midpoint quadratics for lines, independent
control-point mapping and zero-point failure result. No shaping or paragraph
records are fabricated by these nominal APIs.

`text::text_blob::{TextBlob, TextBlobRun}` retains complete immutable source runs,
selected fonts, glyph slots, positions and optional shared RotationScaleMatrix
records. Ordinary/horizontal/positioned/rotation/path-positioned factories retain
source zero-fill/count rules and original midpoint selection (including its
contiguous glyph-slice policy). Bounds are lazily computed from actual glyph
paths and cached only after success. Cloning retains the same generation/unique
ID; original source arrays are already in the renderer's XY representation, so
`positioned_run` shares them without per-replay conversion. Combined path output
preserves the original placement/addition order and bold font geometry.

This is the nominal font and immutable blob production checkpoint. Mutable blob
builder storage leases, intercept queries, complete Canvas text/path raster policy,
platform typeface discovery and original source/package/pixel/performance
qualification remain separate implementation/validation work. No allocation-free
claim is made for immutable Arc-slice construction or the encoded convenience
methods. The actual font/outline/variation cache remains the existing owned Rust
implementation. No foreign rendering or shaping code is used.

The source SKRect midpoint is `left + (right - left) / 2`, independently from
source expressions that use endpoint sums. The legacy oval constructor and
rounded-rectangle recognizer now retain that exact expression; Canvas oval
recording is corrected in the accompanying image-batch checkpoint.

Only source formatting ran. No compiler/lint/tests, browser/shader execution,
fixtures, benchmarks or new build/trace artifacts. Earlier validation evidence
does not qualify these new paths.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKFont.cs` | `0c85f493bba6bbee861083e9ceecf61e23e4d187d00bec08f9f1bd35352c88ed` |
| `src/SkiaSharp/SKFont.Text.cs` | `79e9f10b2fa0987d0c5616b39f05845012248578271ed29c1df9db2b65167f7e` |
| `src/SkiaSharp/SKTextBlob.cs` | `884ec41c6931dfa829762cee59adfb1559b23c39e205fc499adb9243bfeb0155` |
| `src/SkiaSharp/SKRect.cs` | `ec297b0e18f3a1519106292f715bd65429ae495a22aee329e1e2a843ec6ba3a6` |
