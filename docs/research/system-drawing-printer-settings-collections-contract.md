# System.Drawing printer-settings collection contract

## Contract sources

This is a clean-room managed implementation based on the pinned .NET 10.0.11 reference assembly and Microsoft public documentation. No framework implementation source was copied.

- [`PrinterSettings.StringCollection`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.printersettings.stringcollection?view=windowsdesktop-10.0) is a mutable `ICollection` and `IEnumerable<string>` with public array construction, indexed access, addition, copying, and enumeration.
- [`PrinterSettings.PaperSizeCollection`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.printersettings.papersizecollection?view=windowsdesktop-10.0), [`PaperSourceCollection`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.printersettings.papersourcecollection?view=windowsdesktop-10.0), and [`PrinterResolutionCollection`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.printersettings.printerresolutioncollection?view=windowsdesktop-10.0) are mutable non-generic `ICollection` implementations with public array construction, indexed access, addition, copying, and enumeration.
- [`PrinterSettings.InstalledPrinters`](https://learn.microsoft.com/dotnet/api/system.drawing.printing.printersettings.installedprinters?view=windowsdesktop-10.0) represents a snapshot of printer names available to the process.

## Managed collection behavior

All four nested collection types now derive directly from `object`, implement the official collection interfaces, remain inheritable, and expose virtual indexers. Construction snapshots the caller-owned array so replacing source entries cannot change the collection. Elements are retained by reference, additions return their inserted index, typed and non-generic copying share normal list validation, and enumeration preserves insertion order.

`ICollection.IsSynchronized` is false and `SyncRoot` is the collection instance. Warming and repeatedly reading an indexed entry allocates no managed memory. `InstalledPrinters` returns a new snapshot so adding to one caller's collection cannot mutate process-global state.

## Platform boundary

The portable fallback currently returns an empty installed-printer snapshot. This is explicit absence of a configured printer-enumeration provider, not a claim that the host has no printers. Real enumeration, capabilities, defaults, printer-change notification, and native print handles belong behind a typed local-OS printing service. Windows can preserve its spooler path, while Linux and macOS adapters can use their native printing systems without placing CUPS, AppKit, or Win32 handles into the managed collection model.

The slice does not change `GetHdevmode`, `GetHdevnames`, printing controllers, or device-context creation. Those remain separately reviewed backend work.

## Quality and performance evidence

Six focused tests verify exact base/interface/sealed/virtual shape through ApiCompat and reflection, input-array snapshots, addition/index/copy/enumeration behavior, non-generic collection state, null validation, isolated installed-printer snapshots, and zero allocation across 100,000 warmed indexed reads. The complete drawing suite passes 240/240.

The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured `ReadPaperSizeWidthBatch` at 0.965 ns median (0.947 ns mean, 0.041 ns standard deviation) per indexed width read with 0 B allocated. One launch and three measured iterations, with process-priority elevation denied, make this a coarse local regression checkpoint.

ApiCompat removes 16 missing-member and 20 base/interface/sealed/virtual-shape suppressions. Measured debt falls from 41 missing types, 189 missing members, 43 other diagnostics, and 273 total to 41 missing types, 173 missing members, 23 other diagnostics, and 237 total, with no breaking changes or stale suppressions.

The complete LibreWinForms source-first shadow gate passes the default canonical build at 0 warnings/0 errors, the source-built ProGPU canonical build at the established 613 warnings/0 errors baseline, typed platform tests at 22/22, ProGPU adapter tests at 10/10, canonical lifecycle tests at 24/24, and the frozen portable comparison build at 30 warnings/0 errors. NuGet support remains the normal development mode; coordinated source development uses the pinned submodule.
