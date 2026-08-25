# System.Drawing formatted text contract

## Scope

This checkpoint completes the pinned .NET 10 managed `Graphics.DrawString`, `Graphics.MeasureString`, `Graphics.MeasureStringInternal`, and `Graphics.MeasureCharacterRanges` span/point overload shapes. It also replaces prefix-width character-range approximation with cluster-aware regions obtained from the shared typed ProGPU text layout.

The next typed semantics slices implement explicit tab-stop expansion, culture-native digit substitution, vertical and RTL base directions, `NoFontFallback`, `MeasureTrailingSpaces`, visible `DisplayFormatControl` representatives, retained mnemonic underlines for `HotkeyPrefix.Show`, whole-line `LineLimit` layout, and slash-aware horizontal `EllipsisPath` trimming.

## Contract authority

The public surface is checked against `System.Drawing.Common.dll` from the pinned official `Microsoft.WindowsDesktop.App.Ref` 10.0.11 pack. The official [`Graphics` source contract](https://github.com/dotnet/winforms/blob/3a6805cb175abc6f22b08c01ebc09f77304de31c/src/System.Drawing.Common/src/System/Drawing/Graphics.cs) was inspected for overload, null/empty validation-order, and delegation behavior only. Microsoft documents [`LineLimit`](https://learn.microsoft.com/dotnet/api/system.drawing.stringformatflags) as laying out only entire lines while the default permits a partially obscured final line, and [`EllipsisPath`](https://learn.microsoft.com/dotnet/api/system.drawing.stringtrimming) as replacing the center while preserving as much of the final slash-delimited segment as possible. The implementation here is original managed ProGPU code.

The exact ApiCompat change removes sixteen `CP0002` suppressions and adds none. The reviewed debt moves from 437 to 421 diagnostics: 55 missing types, 319 missing members, and 47 other shape diagnostics.

## Typed architecture

String and `ReadOnlySpan<char>` entry points converge on the existing managed formatted-layout path. ProGPU currently materializes span input once because `ProGPU.Text.TextLayout` owns a string for shaping, wrapping, cluster indices, fallback, bidi, hit testing, and retained glyph recording. No caller array is retained, and no reflection, private-field probes, native GDI+ pointers, or fake text-layout objects are introduced.

`MeasureCharacterRanges` now asks `TextLayout.GetSelectionRectangles` for shaped UTF-16 cluster bounds. Multiple physical rectangles are unioned into the official `Region` result, preserving wrapped lines and bidi-capable cluster order. Rectangle origin and horizontal/vertical alignment offsets are applied consistently with drawing. The normal path intersects regions with the layout rectangle; `StringFormatFlags.NoClip` preserves overflow.

`TextLayoutFormattingOptions` is the backend-neutral seam for non-OpenType layout behavior. It carries fallback enablement, trailing-space measurement, the first tab offset, and relative tab intervals without coupling `ProGPU.Text` to `System.Drawing`. Tab positions are accumulated from the line origin and the final positive interval repeats after the declared stops. Invalid non-finite or non-positive stored intervals are ignored by layout without changing `StringFormat.GetTabStops` round-trip behavior.

`DirectionVertical` selects the existing top-to-bottom shaping path, while `DirectionRightToLeft` supplies the UAX #9 paragraph base direction. `NoFontFallback` prevents platform fallback resolution at the font-run boundary rather than replacing glyphs after shaping. Native-digit substitution occurs before shaping and retains one UTF-16 code unit per ASCII digit, preserving cluster/range indices. Culture data is used first, with explicit Arabic, Persian/Urdu, Thai, Bengali, and Devanagari digit maps when the runtime reports Latin native digits.

Without `MeasureTrailingSpaces`, line layout and selection positions still retain whitespace advances, but the reported line width stops at the last non-whitespace candidate. With the flag, the complete shaped advance contributes to measurement.

`DisplayFormatControl` preserves default ignorables through the typed shaping buffer. A control without a nominal cmap entry therefore records the font's outlined `.notdef` glyph instead of the invisible space substitution used by the normal path. This supplies a visible representative without inventing a second text renderer. `HotkeyPrefix.Show` removes the first unescaped ampersand, retains the shaped UTF-16 cluster index of the following scalar, and records one filled underline rectangle using the owning shaped face's underline metrics. `HotkeyPrefix.Hide` performs the same ampersand unescaping without decoration; doubled ampersands remain one literal ampersand. The decoration shares the glyph run's brush, origin, transform, and active layout clip.

For height-constrained clipped layout, the default visible budget rounds the rectangle height up to the next line height so a partially visible final line participates in shaping and `charactersFitted`. `LineLimit` keeps the exact rectangle height, so the trim-fit search can retain only complete lines. Layout beyond the last visible line is no longer shaped and then merely clipped, which also makes `linesFilled` and `charactersFitted` describe the visible result. Line height comes directly from the selected face's OpenType metrics without constructing a probe layout. `NoClip` retains the unbounded-layout behavior unless trimming or `LineLimit` explicitly requests a boundary.

`EllipsisPath` first retains the final forward- or backslash-delimited segment, shortening it from the left only when the ellipsis plus the complete tail cannot fit. It then spends the remaining shaped width budget on the leading path. Every candidate boundary is UTF-16 scalar-safe, final fit is decided by the same typed shaper used for drawing, and a mnemonic retained in either the leading or trailing segment is remapped to its displayed cluster before underline recording. Strings without a path separator continue through ordinary character-ellipsis behavior.

Empty span measurement returns `SizeF.Empty` before validating the font. Empty span drawing validates the brush first, then returns before validating the font, matching the official observable ordering.

## Quality and performance gates

`GraphicsStringFormatQualityTests` covers:

- equality of string and span measurement plus typed retained glyph commands;
- cluster-based measurable regions spanning wrapped lines;
- clipped versus `NoClip` range bounds;
- official empty-span validation order;
- explicit tab-origin geometry;
- trailing-space width control;
- typed vertical shaping;
- culture-native digit glyph substitution;
- deterministic fallback suppression using Inter plus Noto CJK;
- visible default-ignorable representative glyph recording;
- mnemonic show/hide unescaping and retained underline geometry;
- bounded warmed allocation for mnemonic recording;
- whole-line versus partially visible final-line geometry and fitted counts; and
- path-prefix/final-segment retention, ellipsis glyph recording, retained-tail mnemonic geometry, and bounded warmed path allocation; and
- bounded warmed allocation windows for baseline and advanced formatted span measurement.

`GraphicsStringFormatBenchmarks.MeasureSpan` measures the corresponding warmed shaped span layout. The original 2026-08-22 ARM64/.NET 10.0.11 in-process ShortRun measured a 10.709 µs median (11.316 µs mean, 1.490 µs standard deviation) with 6,712 B/op. In the paired advanced-format checkpoint run, the baseline measured 11.909 µs median, 11.823 µs mean, 0.447 µs standard deviation, and 6.64 KB/op. `MeasureAdvancedFormatSpan`, covering tab expansion, Arabic digit substitution, and trailing-space measurement, measured 7.235 µs median, 7.243 µs mean, 0.245 µs standard deviation, and 5.67 KB/op. The mnemonic checkpoint's `RecordMnemonicString` measured a 3.021 µs median, 3.134 µs mean, 0.309 µs standard deviation, and 2.02 KB/op. The slash-aware checkpoint's `MeasureEllipsisPathSpan` measured an 88.79 µs mean, 1.203 µs standard deviation, and 70.02 KB/op; the focused suite caps the same warmed operation at 96 KB/op. Each checkpoint used one launch, three warmups, and three measured iterations; process-priority elevation was denied. These are coarse managed text-layout/recording measurements, not GPU presentation or end-to-end control-rendering throughput. Hosted System.Drawing, full repository, native renderer, and LibreWinForms source-first lanes remain the integration authority.

## Remaining work

Digit-substitution coverage should expand from the current single-code-unit scripts to any official multi-code-unit or contextual behavior found by Windows differential probes. Wrapped/vertical path trimming, vertical wrapping/alignment, vertical mnemonic decoration, and RTL tab-stop geometry need Windows differential probes plus dedicated cross-platform pixel baselines. Each behavior needs pixel/geometry, allocation, and benchmark evidence before it can be considered complete.
