# System.Drawing typed native-image import contract

Date: 2026-08-27

## Scope and clean-room evidence

This checkpoint restores the official `Bitmap.FromHicon(IntPtr)` and
`Bitmap.FromResource(IntPtr, string)` surface and makes the already-present
`Icon.FromHandle(IntPtr)` image path functional. The contract is based on the
official reference assembly, Microsoft Learn API documentation, and observable
tests. No upstream implementation source is copied or mechanically ported.

The APIs are Windows-native entry points: the first consumes an `HICON`; the
second names a bitmap resource in a module identified by an instance handle.
They are not file/manifest-resource decoding aliases. Managed type-relative
resources continue to use `Bitmap(Type, string)`, and the existing managed
ICO/PE file parser remains separate from live-handle import.

## Typed capability and ownership

`ProGPU.SystemDrawing.INativeImageImportService` is the narrow local-OS seam.
It receives the original icon or module handle plus a guarded
`NativeImageImportDestination`. A provider must synchronously call `SetRgba`
exactly once with positive dimensions and an exact `width * height * 4`
row-major RGBA8 span. The destination immediately copies that span into
caller-owned storage, becomes inactive when the provider returns or throws,
and rejects missing, duplicate, late, or incorrectly sized writes.

The resulting `Bitmap` owns the copied pixels. ProGPU does not retain the
native handle, provider storage, module, resource pointer, or destination. The
caller may therefore release the source native object after import without
invalidating the managed pixels. `Icon.FromHandle` uses the same import and
owns a bitmap snapshot for `ToBitmap`; exporting a live `Icon.Handle` remains
a distinct typed Windows-adapter capability.

Registration is process-scoped, rejects ambiguous simultaneous providers, and
has one disposable owner. Zero icon handles and null resource names validate
before capability lookup. Module-handle and non-null resource-name semantics
remain provider/OS responsibilities, so a zero module handle is not silently
rewritten. A missing provider throws `PlatformNotSupportedException` at the
typed boundary. No reflection, private-field scan, duck typing, GDI+ pointer,
or renderer-native handle is used.

## Quality and performance gates

Five focused tests cover both restored bitmap methods and `Icon.FromHandle`,
exact handle/name transport, dimensions and representative pixels, provider
buffer mutation after the synchronous write, missing/duplicate/invalid writes,
single-owner registration, registration disposal, validation order, missing
capability, and warmed allocation. The 16-by-16 gate permits at most 32,768
bytes across sixteen imports, including one unavoidable owned 1 KiB pixel
snapshot per result.

`NativeImageImportBenchmarks.Import64x64IconSnapshot` measures the typed
provider call, guarded synchronous copy, bitmap construction, pixel read, and
disposal while deliberately excluding OS/GDI handle-decoding latency. The
2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 648.615 ns median
(645.822 ns mean, 15.825 ns standard deviation) and 16.43 KB per import. One
launch and three measured iterations make this a coarse local checkpoint; the
focused allocation ceiling is the stable gate.

Removing the two exact member suppressions reduces reviewed debt from 0
missing types, 7 missing members, 13 other diagnostics, and 20 total to 0
missing types, 5 missing members, 13 other diagnostics, and 18 total. The five
remaining member diagnostics are `Font.FromHdc`, three native graphics/palette
entries, and `Graphics.AddMetafileComment`; they remain separate typed-adapter
or portable-recording work.
