# Original Microsoft WPF Display text reference

This Windows-only reference observes the original `TextFormatter` and source
`GlyphRun` API using the repository's exact Inter-Regular font. It does not load
LibreWPF or ProGPU rendering libraries. Original PresentationCore strong-name
identity, loaded DirectWrite/native module hashes, runtime/OS/architecture,
font and producer hashes accompany every CreateNew receipt.

The fixed 192-case inventory covers Ideal/Display, both paragraph directions,
13/17 DIP em sizes, four DPI scales, three original texts (including kerning,
ligature and combining opportunities), and wrapped/unwrapped widths. Each line
records original source ranges, glyph IDs, advances, offsets, cluster/caret
metadata, baseline, selection and ink bounds. Continuations use independently
cloned original TextLineBreak objects after the preceding line is disposed.
No expected metrics are inferred from glyph ink, a font's nominal advances, or
the portable implementation. Null source offset/caret collections remain null.

The path-filtered CI jobs keep Windows Server x64 and Windows 11 ARM64 receipts
separate. The reference has a 60-second observation budget and a 90-second outer
process bound. A successful receipt establishes Microsoft API observations only;
it does not qualify a FreeType interpreter choice, portable Display selection,
caret/pixel parity, or a product/package release. A paired portable comparison
and original application gates remain required.

Public contract references: [TextFormatter.FormatLine](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformatting.textformatter.formatline?view=windowsdesktop-10.0)
and [GlyphRun](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.glyphrun?view=windowsdesktop-10.0).
Only public APIs and their observed results are used; no foreign text-engine
implementation is incorporated.
