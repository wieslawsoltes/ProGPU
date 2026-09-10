# System.Drawing stock-icon contract

## Contract sources

This is a clean-room managed implementation based on the pinned .NET 10.0.11 reference assembly and Microsoft public documentation. No framework implementation source was copied.

- [`StockIconId`](https://learn.microsoft.com/dotnet/api/system.drawing.stockiconid?view=windowsdesktop-10.0) defines the 93 public stock identifiers and their numeric Windows shell identities.
- [`StockIconOptions`](https://learn.microsoft.com/dotnet/api/system.drawing.stockiconoptions?view=windowsdesktop-10.0) is a flags enum containing `Default`, `SmallIcon`, `ShellIconSize`, `LinkOverlay`, and `Selected`.
- [`SystemIcons.GetStockIcon`](https://learn.microsoft.com/dotnet/api/system.drawing.systemicons.getstockicon?view=windowsdesktop-10.0) returns an uncached icon owned by the caller and supports both option-based and explicit-size requests.
- [`SystemIcons`](https://learn.microsoft.com/dotnet/api/system.drawing.systemicons?view=windowsdesktop-10.0) exposes cached process-wide icons through its static properties.

## Portable managed behavior

ProGPU exposes the exact enum identities and both public `GetStockIcon` overloads. Every defined identifier produces a nonempty, owned `Icon`. Explicit positive sizes are preserved; `SmallIcon` selects 16×16 and the default or shell-size path selects the portable 32×32 logical size. Link and selected options modify the rendered pixels. Undefined identifiers, unsupported option bits, and nonpositive explicit sizes fail before allocation.

Requested icons are new disposable instances. Traditional static properties remain lazily cached, and `Shield` has its own security glyph rather than aliasing `Warning`. `Icon.CreateOwned` transfers a completed managed bitmap directly into the icon, avoiding the previous PNG encode/decode round trip and keeping ownership explicit.

The portable catalog groups the Windows identifiers into deterministic notification, folder, drive, media, document, printer, network, security, device, action, and application glyphs. This guarantees useful cross-platform semantics without `HICON`, Win32 shell calls, runtime reflection, private-state probes, or fake compatibility objects.

## Platform boundary

The managed glyph catalog is not a claim of Windows shell artwork, local desktop-theme, DPI-theme, or accessibility-theme parity. Exact native shell artwork belongs behind a typed local-OS stock-icon provider. A future Windows adapter can resolve the official identifier and flags through the shell, while Linux and macOS adapters can map the semantic identifier to their own icon themes. The managed catalog remains the deterministic fallback when no local provider is installed.

`ShellIconSize` currently resolves to the 32×32 portable logical default because ProGPU has no typed local shell-metrics provider yet. Adding one must not introduce a Win32 dependency into the canonical managed path.

## Quality and performance evidence

Nine focused tests verify the full enum/flag identities, option and explicit sizes, uncached caller ownership, distinct semantic categories, renderable pixels for all 93 identifiers, overlay and selection output, validation, static caching, and a warmed allocation ceiling. The complete drawing suite passes 234/234.

On the 2026-08-27 ARM64/.NET 10.0.11 ShortRun, `CreateAndDisposeFolderIcon32` measured a 1.490 microsecond median (1.512 microsecond mean, 0.0731 microsecond standard deviation) with 13.97 KB allocated. `CreateAndDisposeSelectedLinkIcon32` measured a 2.884 microsecond median (3.262 microsecond mean, 0.8907 microsecond standard deviation) with 14.65 KB allocated. Three measured iterations and denied process-priority elevation make these coarse local subsystem checkpoints. The focused suite independently guards the warmed ownership path with a 36 KB-per-operation in-process ceiling.

ApiCompat removes the `StockIconOptions` type suppression, all 88 previously missing `StockIconId` field suppressions, and the option-based `GetStockIcon` overload suppression. Measured debt falls from 42 missing types, 278 missing members, 43 other diagnostics, and 363 total to 41 missing types, 189 missing members, 43 other diagnostics, and 273 total, with no breaking changes or stale suppressions.

The complete LibreWinForms source-first shadow gate passes the default canonical build at 0 warnings/0 errors, the source-built ProGPU canonical build at the established 613 warnings/0 errors baseline, typed platform tests at 22/22, ProGPU adapter tests at 10/10, canonical lifecycle tests at 24/24, and the frozen portable comparison build at 30 warnings/0 errors. NuGet support remains the normal development mode; coordinated source development uses the pinned submodule.
