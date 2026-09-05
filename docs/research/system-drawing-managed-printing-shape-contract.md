# System.Drawing managed printing shape contract

## Contract sources

The public surface is pinned to the .NET 10.0.11 `System.Drawing.Common` reference assembly. Managed inheritance, virtual dispatch, event ownership, and legacy serialization shape follow the public Microsoft contract. Native spooler and device-context implementation is deliberately outside this slice.

- `PrintDocument` derives from `System.ComponentModel.Component`, so component containers and the inherited disposal event work without a replacement object.
- `QueryPageSettingsEventArgs` derives from `PrintEventArgs`, remains inheritable, exposes the print action, and resets a null page-settings assignment to a fresh managed page model.
- `PreviewPrintController.UseAntiAlias` remains virtual so derived preview controllers can customize policy while reusing the managed preview pipeline.
- `InvalidPrinterException` retains its protected `SerializationInfo`/`StreamingContext` constructor for exact legacy API shape.

## Managed behavior

`PrintDocument` now uses the canonical component base instead of independently recreating `IDisposable`. Its existing printing events and managed controller pipeline are unchanged, while `Component.Dispose()` now raises the normal `Disposed` event and participates in component-container ownership.

`QueryPageSettingsEventArgs` uses the existing `PrintEventArgs` base and therefore reports `PrintToPrinter` through the managed default constructor. Access and assignment mark its internal page-settings state as observed; assigning null creates a new `PageSettings` instance rather than retaining invalid state. The type is not sealed, matching designer and custom-print-pipeline extensibility.

`PreviewPrintController.UseAntiAlias` uses ordinary virtual getter/setter dispatch. The protected exception constructor delegates to the framework serialization base and exists for binary/source compatibility; no new serialization format or printer-settings payload is invented.

## Platform boundary

These contracts require no native printer access. `StandardPrintController` still fails at the explicit missing platform print adapter, while `PreviewPrintController` continues to render through managed ProGPU bitmaps. Installed-printer enumeration, device capabilities, printable margins, job submission, cancellation, and native handles remain typed local-OS printing-service work.

No spooler, CUPS, AppKit, HDC, runtime reflection in product code, private-field scan, or fake printer object is introduced. Reflection is used only by a focused test to verify the protected constructor and virtual metadata that ApiCompat also gates.

## Quality and performance evidence

Two focused tests cover canonical base types, sealed and virtual modifiers, protected constructor shape, inherited component disposal notification, print-action inheritance, null reset behavior, and derived preview-controller dispatch. Together with the preceding page/collection cases, the focused printing group passes 11/11.

ApiCompat removes one missing-member suppression and six other-shape suppressions, reducing debt from 40 missing types, 138 missing members, 23 other diagnostics, and 201 total to 40 missing types, 137 missing members, 17 other diagnostics, and 194 total, with no breaking changes or stale suppressions.

This slice adds no rendering command, printer enumeration, per-page work, or hot-path allocation. Existing preview pixel behavior and page device-selection allocation/benchmark gates remain authoritative.
