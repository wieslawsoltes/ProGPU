# System.Drawing formatted text contract

## Scope

This checkpoint completes the pinned .NET 10 managed `Graphics.DrawString`, `Graphics.MeasureString`, `Graphics.MeasureStringInternal`, and `Graphics.MeasureCharacterRanges` span/point overload shapes. It also replaces prefix-width character-range approximation with cluster-aware regions obtained from the shared typed ProGPU text layout.

Tab-stop expansion, digit substitution, vertical text, display-format controls, `NoFontFallback`, underline placement for hotkeys, and exact ellipsis-path behavior remain explicit follow-up semantics. This slice does not claim those flags merely because `StringFormat` stores them.

## Contract authority

The public surface is checked against `System.Drawing.Common.dll` from the pinned official `Microsoft.WindowsDesktop.App.Ref` 10.0.11 pack. The official [`Graphics` source contract](https://github.com/dotnet/winforms/blob/3a6805cb175abc6f22b08c01ebc09f77304de31c/src/System.Drawing.Common/src/System/Drawing/Graphics.cs) was inspected for overload, null/empty validation-order, and delegation behavior only; the implementation here is original managed ProGPU code.

The exact ApiCompat change removes sixteen `CP0002` suppressions and adds none. The reviewed debt moves from 437 to 421 diagnostics: 55 missing types, 319 missing members, and 47 other shape diagnostics.

## Typed architecture

String and `ReadOnlySpan<char>` entry points converge on the existing managed formatted-layout path. ProGPU currently materializes span input once because `ProGPU.Text.TextLayout` owns a string for shaping, wrapping, cluster indices, fallback, bidi, hit testing, and retained glyph recording. No caller array is retained, and no reflection, private-field probes, native GDI+ pointers, or fake text-layout objects are introduced.

`MeasureCharacterRanges` now asks `TextLayout.GetSelectionRectangles` for shaped UTF-16 cluster bounds. Multiple physical rectangles are unioned into the official `Region` result, preserving wrapped lines and bidi-capable cluster order. Rectangle origin and horizontal/vertical alignment offsets are applied consistently with drawing. The normal path intersects regions with the layout rectangle; `StringFormatFlags.NoClip` preserves overflow.

Empty span measurement returns `SizeF.Empty` before validating the font. Empty span drawing validates the brush first, then returns before validating the font, matching the official observable ordering.

## Quality and performance gates

`GraphicsStringFormatQualityTests` covers:

- equality of string and span measurement plus typed retained glyph commands;
- cluster-based measurable regions spanning wrapped lines;
- clipped versus `NoClip` range bounds;
- official empty-span validation order; and
- a bounded warmed allocation window for span measurement.

`GraphicsStringFormatBenchmarks.MeasureSpan` measures the corresponding warmed shaped span layout. The 2026-08-22 ARM64/.NET 10.0.11 in-process ShortRun measured a 10.709 µs median (11.316 µs mean, 1.490 µs standard deviation) with 6,712 B/op. It used one launch, three warmups, and three measured iterations; process-priority elevation was denied. This is coarse managed text-layout evidence, not GPU presentation or end-to-end control-rendering throughput. Hosted System.Drawing, full repository, native renderer, and LibreWinForms source-first lanes remain the integration authority.

## Remaining work

The next formatted-text slices should add typed layout options for tab stops, fallback suppression, format controls, digit shaping, vertical flow, mnemonic decoration, trailing-space measurement, exact line-limit behavior, and ellipsis-path layout. Each flag needs behavior, pixel/geometry, allocation, and benchmark evidence before it can be considered implemented.
