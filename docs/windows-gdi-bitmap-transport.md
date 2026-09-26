# Windows GDI bitmap transport

`WindowsGdiBitmap` in the existing lightweight `ProGPU.Wpf.Interop` assembly
provides an explicit local Windows bitmap boundary for LibreWPF clipboard images.
It does not activate a renderer, WIC, MIL, System.Drawing, or a portable clipboard.
It does not implement canonical `Bitmap.GetHbitmap`/`Image.FromHbitmap`.

`CreateFromBmp` borrows a complete BMP produced by the existing source encoder.
It admits only a 40-byte BITMAPINFOHEADER, BI_RGB compression and 1/4/8/24/32-bit
pixels. File extent, nonoverlapping palette/pixel offsets, checked row arithmetic,
complete scan lines, declared image size and partial-palette indices are validated
before GDI access. Both BMP row orientations are explicit; padding is not an index.
Other headers/compression/bitfields remain rejected, not silently approximated.

The screen DC is acquired and released locally. `CreateDIBitmap(CBM_INIT)` copies
the input to an actual compatible HBITMAP, returned as an owned SafeHandle.
`Detach` transfers ownership exactly once to a caller such as an OLE STGMEDIUM.
Without transfer, disposal/finalization releases the bitmap with DeleteObject.
Transfer and disposal must not race.

`CopyBgr32` borrows a live, unselected real HBITMAP while its owner retains it.
It reads the actual native BITMAP dimensions and requires GetDIBits to return all
requested scan lines. The output owns top-down Bgr32 bytes independently of the
source handle. CF_BITMAP is an opaque RGB transport: the undefined high byte is
not alpha, and 96-DPI output is not a claim of source-DPI preservation. Neither
alpha interoperability nor color-managed/bit-depth parity is qualified here.

The native contracts follow Microsoft's documentation for
[CreateDIBitmap](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdibitmap),
[GetDIBits](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdibits),
and [clipboard formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats).

## Compilation and required qualification

The helper and unchanged `WindowsGdiBitmapTests.cs` compile in an isolated signed
test project against the actual interop project and xUnit on macOS ARM64, with
zero warnings/errors. The full test-project build in the reused worktree was
blocked by its uninitialized ACadSharp and microsoft-ui-xaml submodules; this is
not a full source-graph compilation claim.

No test bodies, GDI runtime, VM or image workloads were executed during this
implementation-first batch. Existing full CI remains required. The authored
Windows-only cases cover actual 1/4/8/24/32-bit pixel conversion, top-down/bottom-up
orientation, native borrowing, input mutation, transfer and owned pixel lifetime.
Pure cases cover malformed metadata, checked extent overflow and partial palettes.
Non-Windows runs explicitly skip the native cases; they cannot qualify Windows.

The dependent LibreWPF PR connects real OLE FORMATETC/STGMEDIUM ownership and
source BitmapSource storage, with actual Showcase package copy/paste and lifetime
gates. Compile success alone does not resolve the historical Windows image hang.
