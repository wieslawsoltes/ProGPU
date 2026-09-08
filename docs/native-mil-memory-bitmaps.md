# Native MIL memory-bitmap source connection

## Core application dependency

LibreWPF's existing SciChart MVP calls `MainWindow.CreateBitmap` after rendering
its chart: construct a Pbgra32 `WriteableBitmap`, write the snapshot, freeze it,
then assign it to `Image.Source`. Its 3D bridge uses the same memory-image path.
The package SDK smoke also constructs `BitmapSource.Create` and WriteableBitmap
images. These are concrete application consumers, not general codec expansion.

Source inspection found that WriteableBitmap's dimension and source-copy
constructors chose Windows' `MILSwDoubleBufferedBitmap.Create` by OS even after
portable media selection. CachedBitmap's memory constructor similarly selected
its WIC path by OS. The source now uses the existing immutable
`PortableWpfRuntime` media choice through `BitmapSource.UsesPortablePixelStorage`.
Portable selection uses source-owned pixels on Windows, macOS and Linux;
ordinary Windows-MIL selection retains the original storage paths. A cached
source with managed pixels is consumed by ownership rather than an OS check.
Decode-failure replacement uses the same storage policy so it cannot request a
WIC handle from a newly created portable memory bitmap.

## Ownership and rendering applicability

Both ProGPU renderer modes consume the existing
`IPortableBitmapSourcePixelsSource` descriptor. The native-MIL compiler converts
the typed format using shared ProGPU pixel conversion and binds an owned RGBA8
sideband with source dimensions, row bytes and independent X/Y DPI. Existing
native resource generation, upload and texture lifetime remain authoritative.
No renderer, C ABI, shader or native bitmap algorithm changes are needed: this
fix selects and owns the source storage that supplies those existing consumers.
It does not send a Windows double-buffer pointer into ProGPU or replace an
unimplemented native draw with managed replay.

Portable WriteableBitmap back buffers are now allocated on the .NET pinned
object heap with `GC.AllocateArray<byte>(length, pinned: true)`. Storage remains
zero initialized and bitmap owned. Pointer exposure during a lock needs no
GCHandle allocation; abandoning a locked bitmap cannot orphan a globally rooted
GCHandle. The existing outermost-unlock dirty notification and public pointer
clearing behavior remain unchanged. Source-copy and clone initialization also
allocate pinned owned storage before any lock can expose it. Source dimensions,
format, palette, stride and DPI remain unchanged by that lifetime choice.

This is caller-visible CPU memory, not a rejected-compute rendering fallback.
The core Pbgra32 copies use the existing runtime-intrinsic bulk-copy path; no new
pixel arithmetic loop, GPU readback or repacking stage is added. Existing
packed-bit pixel algorithms are not claimed optimized or qualified here.
No latency, allocation-throughput or memory-footprint improvement is claimed
without the deferred measurements. Pinned storage is collected with its owner,
not deterministically freed at Unlock; applications must retain the bitmap while
using its pointer and must not write after unlocking or freezing it.

## Evidence and final qualification

Authored source fixtures cover the selected storage family, independent memory
sources/copy constructors/Clone/CloneCurrentValue, DPI, nested locks, dirty-event
coalescing, pointer stability across collection and relocking, and freeze/write
rejection. They exercise whichever media backend the test process selected;
the native host harness explicitly selects Portable before constructing WPF.

The existing source-built native host gate now creates a real WriteableBitmap,
updates it through nested locks, checks independent copies through typed pixel
descriptors, freezes a clone and includes it in its actual drawing visual. Its
native batch assertion requires the bitmap's exact RGBA bytes and 144/192 DPI.
This augments the existing application gate; it is not a replacement small app.
Diagnostic-only public API reflection is confined to the dual-assembly harness,
with removal when it can reference only source-built WPF. Product seams are typed.

Tests, graphical runs, native-Windows comparisons, lifetime stress and exact-head
CI remain deferred to feature freeze. In particular, shared-worker updates,
publication during unrelated visual invalidation while a bitmap is locked,
decoder-backed image construction and complete imaging API parity are not proven
by this memory-source connection. Windows SDK admission remains guarded.

Behavioral references: [WriteableBitmap.Lock](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.writeablebitmap.lock?view=windowsdesktop-10.0)
defines nested locks and publication after full unlock; the
[.NET pinned heap design](https://github.com/dotnet/runtime/blob/main/docs/design/features/PinnedHeap.md)
describes GC-owned pinned allocation. These are contract references, not copied
implementation. ProGPU's existing `WpfNativeMilSceneCompiler` consumer in LibreWPF,
`NativeMilChannel` bitmap sidebands, and `PixelDataConverter` remain unchanged.
