# System.Drawing Font and Font-Collection Contract

## Contract and scope

The public contract is pinned to the .NET 10.0.11 `System.Drawing.Common` reference assembly used by the repository ApiCompat gate. Observable constructor, lookup, metric, cloning, ownership, validation, and disposal cases were compared with the canonical WinForms tests vendored by LibreWinForms. The implementation is original portable code over `ProGPU.Text.FontManager`, `FontFace`, and `TtfFont`; it does not copy the GDI+/HFONT implementation.

This slice completes the normal managed surface of `Font`, `FontFamily`, `System.Drawing.Text.FontCollection`, `GenericFontFamilies`, `InstalledFontCollection`, and `PrivateFontCollection`. HFONT, HDC, LOGFONT, GDI+, and CsWin32 pointer interfaces remain explicit Windows-adapter boundaries and stay in the reviewed compatibility debt rather than being represented by fake handles.

## Resolution and identity

`FontFamily(string)` performs case-insensitive exact lookup in the typed ProGPU catalog and throws when the family is absent. It never combines an unrecognized requested name with an unrelated fallback file. The string-based `Font` constructors preserve the framework's distinct behavior: they retain `OriginalFontName`, strip the vertical `@` prefix for lookup, and select the resolved generic sans-serif family when the requested family is unavailable.

Generic serif, sans-serif, and monospace resolution uses ordered cross-platform preferences followed by the first real catalog family or an explicitly registered ProGPU platform fallback. The resolved immutable source is cached, while every public generic-family property returns an independently disposable `FontFamily`. Installed collection enumeration snapshots the current typed catalog and remains usable after disposal, matching the public installed-collection lifetime contract.

## Private ownership and style selection

`PrivateFontCollection.AddFontFile` canonicalizes and deduplicates paths, reads the file into collection-owned bytes, and parses OpenType faces with `TtfFont`. `AddMemoryFont` copies the unmanaged caller buffer before parsing, so freeing or mutating caller memory cannot affect the collection. Existing non-font files add no invented family. Collection faces are grouped case-insensitively by their parsed family names and style coordinates; repeated style faces are deterministic first-wins entries.

Family instances and fonts hold immutable face snapshots, not back-pointers into mutable or disposed collections. A `Font` also snapshots its supplied `FontFamily`, so caller disposal cannot invalidate the font. Regular/bold and upright/italic matching uses typed OpenType weight and slant metadata. Underline and strikeout remain drawing decorations and do not select a different face. Unsupported requested styles use the closest face for font creation but `IsStyleAvailable` reports only a matching weight/slant face.

## Metrics, validation, and performance

`GetEmHeight`, `GetCellAscent`, `GetCellDescent`, and `GetLineSpacing` expose the selected `TtfFont` units-per-em, ascender, negated descender, and ascender-minus-descender-plus-line-gap values. `Font.GetHeight` scales the same typed metrics through the requested `GraphicsUnit` and DPI. Font sizes must be finite and greater than zero; `Display` and out-of-range units are rejected. Clone and serialization retain the public font identity and drawing properties, while platform handle conversion fails explicitly at the Windows boundary.

The focused quality suite covers exact lookup, fallback identity, public overload validation, real metrics, style availability, file and memory ownership, independent collection/family/font lifetimes, disposal behavior, invalid existing files, clone behavior, and zero allocation across 4,000 warmed metric reads. The complete drawing suite passes 128/128 tests.

ApiCompat debt moves from 59 missing types, 409 missing members, 46 other diagnostics, and 514 total to 55 missing types, 391 missing members, 47 other diagnostics, and 493 total. The apparent one-count increase in other diagnostics is reviewed native-pointer debt surfaced after formerly missing managed collection types became visible; the total debt falls by 21 and the gate has no stale suppressions.

`FontBenchmarks.ReadTypefaceMetrics` performs the same 4,000 warmed metric reads per invocation over one privately loaded Inter face. On the 2026-08-22 ARM64 Ubuntu/.NET 10.0.11 BenchmarkDotNet 0.15.8 ShortRun checkpoint, it measured an 8.368 ns median per metric read, 8.383 ns mean, 0.026 ns standard deviation, and zero managed allocation. The run used one launch with three warmup and three measured iterations; process priority elevation was denied. This is a local regression checkpoint, not an end-to-end text-rendering claim. Raw CSV, Markdown, HTML, and compressed JSON results remain under `BenchmarkDotNet.Artifacts/results` for the worktree run and hosted CI uploads benchmark artifacts.
