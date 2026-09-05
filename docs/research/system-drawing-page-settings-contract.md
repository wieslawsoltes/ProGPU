# System.Drawing page device-selection contract

## Contract sources

The public surface is pinned to the .NET 10.0.11 `System.Drawing.Common` reference assembly. Managed behavior follows the Microsoft-documented printing model and the upstream MIT-licensed source contract; the portable fallback does not copy native DEVMODE or printer-driver code.

- [`PageSettings`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.pagesettings?view=windowsdesktop-10.0) associates one page with `PrinterSettings` and exposes page-level paper source and printer resolution selection.
- [`PageSettings.PaperSource`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.pagesettings.papersource?view=windowsdesktop-10.0) selects the printer tray for a page.
- [`PageSettings.PrinterResolution`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.pagesettings.printerresolution?view=windowsdesktop-10.0) selects the page resolution.
- [`PaperSource`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.papersource?view=windowsdesktop-10.0) retains the raw driver bin while mapping user-defined values to the public `Custom` kind.
- [`PrinterResolution`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.printerresolution?view=windowsdesktop-10.0) exposes a validated mutable kind plus custom X/Y DPI values.

## Managed behavior

`PageSettings(PrinterSettings)` is public and retains the supplied managed printer model. The portable default owns explicit custom `PaperSource` and `PrinterResolution` objects instead of querying a nonexistent driver. Setters retain the caller's typed objects, getters return them without allocation, and cloning follows the managed framework contract: margins are cloned while printer settings, source, and resolution references remain associated with the page clone.

`PaperSource` now defaults to `Custom`. `RawKind` retains the exact driver/bin integer; values at or above the user-bin boundary report `PaperSourceKind.Custom` while preserving that raw value. `PrinterResolution.Kind` is mutable, defaults to `Custom`, and rejects values outside the official contiguous `High` through `Custom` range with `InvalidEnumArgumentException`. Named kinds use the named `ToString` form; custom resolutions retain invariant X/Y output.

Assigning null to `PageSettings.PrinterSettings` creates a fresh managed settings model, matching the framework's reset behavior. Null paper-source and resolution assignments fail immediately rather than leaving latent null state in the portable page model.

## Platform boundary

This slice models selection; it does not claim that the host printer supports the selected tray or resolution. Installed device capabilities, default DEVMODE values, printable/hard margins, native job handles, and driver validation remain responsibilities of the typed local-OS printing adapter already identified by the printing plan. `CopyToHdevmode` and `SetHdevmode` remain explicit unsupported native boundaries.

A future adapter should translate `RawKind`, named/custom resolution, and page settings into its native job model only when a real printer is selected. Windows can preserve DEVMODE behavior; Linux and macOS adapters should map through their printing APIs without leaking handles into `PageSettings`.

## Quality and performance evidence

Three focused tests cover constructor association, official defaults, raw/custom source behavior, resolution validation, setter/getter identity, clone ownership, string state, null reset/validation, and zero allocation across 100,000 warmed source/resolution read groups. The complete drawing suite passes 249/249.

The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured `ReadPageDeviceSelectionBatch` at a 0.615 ns median (0.615 ns mean, 0.004 ns standard deviation) per alternating page source/resolution read group with 0 B allocated. One launch and three measured iterations make this a coarse local scalar-access checkpoint.

ApiCompat removes six missing-member suppressions, reducing debt from 40 missing types, 144 missing members, 23 other diagnostics, and 207 total to 40 missing types, 138 missing members, 23 other diagnostics, and 201 total, with no breaking changes or stale suppressions.
