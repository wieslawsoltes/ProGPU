# Direct2D and Win2D compatibility

## Decision

`ProGPU.DirectX` remains a typed Direct3D-style device/resource/pipeline facade
over WebGPU, with D3D12 selected by the Windows backend. The C++ backend now
also ships an isolated Windows `progpu_native_direct2d` provider. It creates
genuine system Direct2D COM objects and a synchronized DXGI target that ProGPU
can import; it does not reimplement Direct2D vtables or impersonate
`d2d1.dll`. Direct2D, DirectWrite, WIC, and the native Win2D WinRT component
remain Windows native graphics dependencies. The ProGPU native-library
resolver must not impersonate `d2d1.dll`, `dwrite.dll`,
`windowscodecs.dll`, or `Microsoft.Graphics.Canvas.dll`.

Win2D support is feasible in two deliberately separate forms:

1. On Windows, the real Win2D/Direct2D runtime can render into a shared DXGI
   allocation that ProGPU imports and composites without CPU readback.
2. On Windows, macOS, Linux, mobile, and the browser, a source-compatible
   ProGPU Canvas API can lower the useful Win2D drawing model to the existing
   ProGPU scene, vector, text, image, effect, and WebGPU implementation.

The existing Microsoft Win2D binary cannot run unmodified on macOS or Linux.
It is a WinRT component layered over Windows Direct2D and Direct3D. Portable
support therefore means recompiling application/sample source against the
ProGPU Canvas implementation, not emulating Windows COM or loading the Win2D
binary.

## Current support matrix

| Surface | Status | Contract |
| --- | --- | --- |
| `ProGPU.DirectX` D3D-style device/resources/pipelines | Implemented | Portable typed facade backed by WebGPU; D3D12 on qualified Windows adapters |
| Native C++ MIL/retained scene on D3D12 | Implemented | Same backend-neutral scene ABI used on Metal, Vulkan, and browser WebGPU |
| DXGI shared-handle import | Implemented building block | `ProGpuExternalTextureDescriptor` plus Dawn shared-texture memory, keyed-mutex ownership, and no CPU readback |
| Direct2D `ID2D1*` API | Foundation implemented | Windows-only native provider plus AOT-safe `ProGPU.Direct2D` managed owner return genuine `ID2D1Factory1/2`, `ID2D1Device/1`, `ID2D1DeviceContext/1`, and `ID2D1Bitmap/1` objects over a keyed-mutex BGRA DXGI target; no fake `d2d1.dll` |
| Native Win2D binary interop | In progress | The real COM device/context/bitmap resource domain, WinRT `IDirect3DDevice` over the same `IDXGIDevice`, transactional drawing session, and zero-copy Dawn import are implemented; Canvas factory/resource wrapping and full device-loss recreation remain gated work |
| Portable Win2D-style Canvas source API | MVP implemented | `ProGPU.Win2D` records Win2D-shaped commands, compiles them with `ProGPU.Scene.Native`, and submits the retained scene to the C++ renderer |
| Portable Win2D bitmap in LibreWPF native MIL | Implemented | Wrap a same-device `CanvasBitmap` lease source in `IPortableNativeImageSource`; canonical `TYPE_BITMAPSOURCE` lowers to a zero-payload external scene image with no readback or repack |
| Arbitrary Win2D native-resource wrapping (`GetOrCreate(IUnknown*)`) off Windows | Unsupported by design | Fail closed; there is no portable COM object identity to preserve |

This split follows Win2D's published architecture. Its repository describes an
immediate-mode WinRT API over Direct2D, its `CanvasDevice` ABI accepts an
`IDIRECT3DDEVICE` and locks through `ID2D1MultiThread`, and its interop contract
wraps native `ID2D1Device1`/`ID2D1Bitmap1` resources:

- [Win2D repository](https://github.com/microsoft/Win2D)
- [Microsoft Win2D overview](https://learn.microsoft.com/windows/apps/develop/win2d/)
- [Win2D `CanvasDevice` ABI](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/lib/drawing/CanvasDevice.abi.idl)
- [Win2D native interop contract](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/docsrc/Interop.aml)
- [Win2D custom effects](https://learn.microsoft.com/windows/apps/develop/win2d/custom-effects)

## Windows native interop lane

The Windows adapter package owns a real D3D11/Direct2D device on the
same DXGI adapter as ProGPU's Dawn D3D12 device. A Win2D drawing session renders
to a BGRA8 premultiplied shared texture. The adapter publishes a typed
`DxgiSharedHandle` descriptor and owner lease to the existing Dawn import path.
The producer and consumer use explicit keyed-mutex keys or a qualified shared
fence; adapter LUID, dimensions, format, alpha mode, usage, and resource state
are validated before the first submission.

The render path is:

```text
real Win2D / Direct2D (Windows D3D11)
        -> shared BGRA8 DXGI allocation
        -> keyed mutex or shared fence
        -> Dawn shared-texture-memory import (D3D12)
        -> ProGPU retained image/layer/effect composition
```

Checkpoint `59045316` implements the first half of this path as a separate
Windows native library. Its versioned C ABI creates one D3D11 device
with BGRA support, a multithreaded `ID2D1Factory2`, `ID2D1Device1`,
`ID2D1DeviceContext1`, and target `ID2D1Bitmap1`, plus a BGRA8-unorm
premultiplied D3D11 texture carrying `SHARED_NTHANDLE` and keyed-mutex flags.
The caller may request a specific adapter LUID, hardware with explicit WARP
fallback, or forced WARP. The resulting descriptor reports the actual adapter
LUID, dimensions, DPI, format, alpha mode, NT handle, initial synchronization
keys, software-adapter state, and monotonic content version.

The provider returns caller-owned references to the genuine base and versioned
COM interfaces, including the D3D11/DXGI objects needed by an interop adapter.
Those pointers are deliberately confined to `progpu_native_direct2d.h` and
Windows process state; they never enter the portable scene, MIL, WebGPU, or
package-neutral ABI. Producer acquire/release is serialized, nested access and
unmatched release fail closed, and every successful producer release advances
the content version. The consumer can reopen the NT handle and use the same
keyed-mutex ownership sequence through Dawn shared-texture memory.

The same provider also calls the system
`CreateDirect3D11DeviceFromDXGIDevice(...)` entry point once for the owned
`IDXGIDevice` and exposes the resulting genuine WinRT `IDirect3DDevice` as
`WinRtDirect3D11Device`. The native regression unwraps it through
`IDirect3DDxgiInterfaceAccess` and requires exact `ID3D11Device` identity. That
is the device argument required by Win2D's
`CanvasDevice.CreateFromDirect3D11Device`; it avoids a second adapter/resource
domain and establishes the activation input for the next Canvas factory lane.

ABI v3 includes the managed ownership half as package `ProGPU.Direct2D`.
`ProGpuDirect2DSurface.Create(...)` validates a live Dawn D3D12 context, exact
adapter LUID when requested, BGRA8-unorm premultiplied format, dimensions, DPI,
NT-handle and keyed-mutex flags before importing the allocation through
`DawnSharedTextureMemory`. `BeginDrawing()` ends Dawn ownership, acquires key
zero, calls the genuine `ID2D1DeviceContext1::BeginDraw`, and returns a drawing
session whose `DeviceContext` is a caller-owned safe COM reference. Disposing
the session calls native `EndDraw`, records tags/HRESULT on failure, releases
key zero, resumes Dawn ownership, refreshes the monotonic content version, and
publishes `TextureChanged`. The surface implements the typed context-aware
texture-lease source consumed by ProGPU images. LibreWPF D3DImage uses the
explicit `ProGpuDirect2DD3DImageSource` adapter, which publishes the surface as
the frame's native image only after `ContentVersion` becomes nonzero and
forwards `TextureChanged` through `IPortableInvalidationSource`:

```csharp
using ProGPU.Direct2D;
using System.Windows.Interop;

using ProGpuDirect2DSurface surface =
    ProGpuDirect2DSurface.Create(context, options);
var source = new ProGpuDirect2DD3DImageSource(surface);
var image = new D3DImage();
PortableD3DImageSourceFactory.Attach(image, source);
```

The application owns and disposes the surface after the image is detached or
otherwise no longer reachable by rendering; the adapter deliberately does not
take independent lifetime ownership.

Key zero on both sides is deliberate: Dawn's qualified DXGI shared-texture
memory path owns the keyed-mutex transition internally and uses the zero-key
profile. Content initialization is represented independently by
`ContentVersion` (`0` before the first successful producer draw), not by
inventing a 0/1/2/3 key protocol that Dawn does not advertise. Active deferred
ProGPU leases reject a Direct2D draw, and the ownership transitions occur
outside the provider state lock so a renderer holding the WebGPU render lock
cannot deadlock against Direct2D session creation or completion.

The public managed ABI is reflection-free and AOT-safe: source-generated
`LibraryImport` calls bind the versioned native C ABI, `SafeHandle` owns every
returned COM reference, and the native surface owner is transferred to the
Dawn import only after all descriptor checks pass. No `NativeLibrary.Load`,
delegate synthesis, COM-vtable reimplementation, pixel readback, repack, or
CPU synchronization loop is used.

This shape follows Microsoft's documented device-context construction and
resource-domain model: Direct2D is created from the D3D11 `IDXGIDevice`, the
bitmap target is created from the same-device `IDXGISurface`, a multithreaded
factory serializes Direct2D calls, and the application still owns Direct3D/
DXGI synchronization around mixed API access:

- [Direct2D devices and device contexts](https://learn.microsoft.com/en-us/windows/win32/direct2d/devices-and-device-contexts)
- [`CreateBitmapFromDxgiSurface`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1devicecontext-createbitmapfromdxgisurface%28idxgisurface_constd2d1_bitmap_properties1_id2d1bitmap1%29)
- [Direct2D/Direct3D interoperability](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-direct3d-interoperation-overview)
- [multithreaded Direct2D applications](https://learn.microsoft.com/en-us/windows/win32/direct2d/multi-threaded-direct2d-apps)
- [Windows graphics surface sharing](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/surface-sharing-between-windows-graphics-apis)

It must not perform `CopyPixels`, staging readback, CPU color conversion,
per-primitive cross-API synchronization, or adapter-crossing copies. A forced
native-interoperability policy fails closed if the adapter, format, sharing, or
synchronization contract is unavailable. Automatic policy may select the
portable ProGPU Canvas implementation instead, but must report the selected
path through diagnostics.

## Portable Canvas lane

The portable API will be a thin recording layer rather than a second renderer:

| Win2D concept | ProGPU implementation |
| --- | --- |
| `CanvasDevice` | `WgpuContext` device domain and capability policy |
| `CanvasRenderTarget` | renderable/sampleable `GpuTexture` with retained lifetime and DPI |
| `CanvasDrawingSession` | allocation-conscious recorder over `ProGPU.Scene.DrawingContext` |
| `CanvasCommandList` | immutable/retained `RenderCommandList` picture |
| `CanvasBitmap` | typed same-device texture source/lease with deferred lifetime ownership |
| `CanvasGeometry` | reusable `PathGeometry` retained directly by native vector draw and clip commands |
| brushes and stroke styles | existing typed brush/gradient/pen resources and native material pages |
| text formats/layouts | reusable ProGPU shaping, glyph-run, atlas, and text-style resources |
| layers and clips | retained layer/mask/clip resources in the shared compositor |
| effects | retained typed effect graph using GPU compute first, compatible GPU shader second, intrinsic SIMD CPU third |
| sprite batches | one typed span upload and bounded batched submission |

The first shipping subset is in the `ProGPU.Win2D` package. It implements
`ICanvasResourceCreator`, `ICanvasResourceCreatorWithDpi`, `CanvasDevice`,
`CanvasBitmap`, `CanvasRenderTarget`, `CanvasCommandList`, `CanvasGeometry`,
`CanvasPathBuilder`, `CanvasActiveLayer`, and `CanvasDrawingSession`, with the
Win2D namespace and overload shapes used by the pinned SimpleSample and selected
Shapes, ArcOptions, and VectorArt sample bodies. It currently supports:

- BGRA8-unorm premultiplied render targets, exact Win2D DIP/pixel rounding,
  full-target clear, transforms, and typed same-device texture exposure;
- stroked lines, rectangles, rounded rectangles, ellipses, and circles;
- filled rectangles, rounded rectangles, ellipses, and circles;
- point-origin default text using deterministic Inter 20 DIP glyphs until the
  Segoe UI/native-font selection contract is added;
- same-device `CanvasBitmap` drawing at an offset or into a destination
  rectangle, optional DIP source rectangle, opacity, and qualified nearest,
  linear, multisample-linear, or cubic sampling; typed texture leases keep a
  source alive through deferred native submission without a staging copy;
- typed `CanvasBitmap.CreateFromBytes(...)` plus full and subrectangle
  `SetPixelBytes(...)` for the qualified BGRA8-unorm premultiplied profile.
  Caller bytes go directly to `GpuTexture.WritePixels`/
  `WritePixelsSubRect` and therefore to one WebGPU queue texture upload; an
  oversized Win2D-compatible array exposes only its required prefix to the
  backend. There is no WIC/reflection adapter, staging readback, CPU repack, or
  per-pixel scalar loop. Mutation fails closed while a deferred drawing owns a
  typed texture lease, and render-target mutation also fails while a drawing
  session is active, so later writes cannot silently alter previously recorded
  Win2D work;
- typed `CanvasBitmap.CreateFromColors(...)` plus full and subrectangle
  `SetPixelColors(...)`. The exact upstream operation is an ARGB-to-BGRA byte
  swizzle, not a premultiplication pass. `Automatic` selects AVX2/Vector256 for
  eight or more pixels, portable Vector128 for four or more remaining pixels,
  and scalar only for the bounded 1–3-pixel tail. `IntrinsicSimd` and
  `ScalarReference` are explicit configurable modes; a forced intrinsic mode
  fails closed when Vector128 hardware support is unavailable. The selected
  path is published by `CanvasDevice.LastPixelConversionPath`. Conversion uses
  a pooled buffer, writes directly to the same queue-upload path, and is
  allocation-free after pool warmup;
- all three `CopyPixelsFromBitmap(...)` overloads lower whole, destination-
  offset, and source-subrectangle copies to one same-device base-level WebGPU
  texture-to-texture command. A typed source lease covers submission and the
  destination uses the same mutation guard as pixel uploads. Active source or
  destination render sessions, destination deferred leases, cross-device
  copies, and self-copy fail closed. ProGPU does not reproduce Win2D's
  cross-device system-memory fallback because that would introduce a GPU
  readback/upload round trip;

- single-recording `CanvasCommandList` creation, multi-chunk recording across
  `Flush()`, `ICanvasImage.GetBounds(...)`, and origin/offset or cropped and
  destination-scaled drawing as nested immutable pictures. The reusable
  `GpuPictureBounds` reader walks typed retained commands without materializing
  their compatibility command arrays, reuses clip-aware hit-test primitive
  bounds, composes nested affine transforms, and fails closed for unbalanced
  state, cycles, GPU/3D transforms, or unsupported commands. Source cropping
  becomes one retained destination clip plus one affine nested-picture
  transform; command lists are cloned into the destination ownership graph, so
  no intermediate bitmap is allocated and public disposal before submission
  remains safe;
- reusable rectangle, rounded-rectangle, ellipse, circle, polygon, and consumed
  path-builder geometries; path figures support line, quadratic, cubic, both
  Win2D arc forms, fill-rule, filled-figure, segment-stroke, and smooth-join
  state, and lower directly to retained native vector commands;
- `CreateGroup`, affine `Transform`, and `CombineWith` union/intersection/xor/
  exclusion operations. Boolean operands remain an immutable combined-geometry
  DAG consumed by the native vector-mask evaluator; the Canvas operation does
  not invoke the synchronous `PathOpGeometrySolver`, read a GPU buffer back, or
  flatten curves on the CPU. Identity-transformed operands are retained without
  cloning. Groups currently fail closed if an entry is itself a combined DAG,
  because flattening it would change `GeometryGroup` fill semantics;
- color `DrawGeometry`/`FillGeometry` overloads with origin or offset, plus
  scoped opacity layers with exact rectangle or path-geometry clips; layers
  must close LIFO and cannot cross a `Flush`, so malformed retained stacks fail
  before native submission;
- mutable `CanvasStrokeStyle` state for start/end/dash caps, miter/bevel/round
  joins, miter limit, standard or custom dash patterns, dash offset, normal,
  fixed, and hairline transform behavior. Each style caches its last immutable
  typed `Pen` realization and invalidates that cache on mutation, so repeated
  ArcOptions-style drawing is allocation-free after warmup. Custom dashes take
  precedence over the standard dash enum. `MiterOrBevel` fails closed until it
  has a distinct retained semantic;
- typed `ICanvasBrush`, `CanvasSolidColorBrush`,
  `CanvasLinearGradientBrush`, `CanvasRadialGradientBrush`, and
  `CanvasImageBrush` resources plus color/HDR gradient-stop DTOs. Primitive,
  geometry, stroke-style, and default text overloads consume the same brush
  contract. Each mutable Canvas brush
  caches an immutable ProGPU brush realization by version, while stroke pens
  cache by realized brush identity and width, so steady drawing allocates no
  new brush or pen and an earlier recorded picture cannot be changed by later
  brush mutation or disposal. Opacity, affine brush transforms, clamp/wrap/
  mirror spread, radial origin offset, and same-device ownership are typed.
  The qualified portable gradient lane is premultiplied sRGB with 8-bit
  normalized precision; straight alpha, scRGB/custom conversion, and other
  precisions fail closed instead of changing interpolation;
- same-device `CanvasBitmap`/`CanvasRenderTarget` image-brush fills with an
  optional DIP source rectangle, independent clamp/wrap/mirror X/Y addressing,
  opacity, and nearest, linear, multisample-linear, or cubic sampling. A
  positive axis-preserving scale/translation lowers to one native external
  image draw whose extended source coordinates are resolved by a flat
  shader-address mode plus the GPU sampler. This keeps D3D12, Metal, and Vulkan
  repeat/mirror semantics identical without CPU tiling or extra submissions.
  The drawing context retains the texture lease, so public bitmap and brush
  disposal before session commit cannot invalidate recorded work. This path
  performs no CPU readback, repacking, or per-tile submission.
  Command-list/effect image sources, rotation/skew/reflection, anisotropic
  sampling, and high-quality cubic currently fail closed pending their typed
  retained contracts;
- target-preserving later drawing sessions and `Flush()` without retaining or
  replaying all earlier command lists;
- Win2D-compatible `GetPixelBytes()` for validation and explicit diagnostics
  proving `NativeCppWebGpu` execution.

`cfebce57` also makes a same-device `CanvasBitmap` consumable by LibreWPF's
native MIL compositor through the existing neutral image carrier. An adapter
publishes the bitmap as `IPortableNativeImageSource`, whose native payload is
the bitmap's `IProGpuTextureLeaseSource`. ProGPU binds the canonical
`TYPE_BITMAPSOURCE` handle with
`progpu_native_mil_channel_set_bitmap_source_external_image`, emits a semantic
external-image resource in global MIL-handle order, and leaves lifetime/device
validation to the consuming host's typed lease. The bitmap and MediaPlayer
external resources share one deterministic resource-ID table. No WIC pointer,
CPU pixels, readback, repack, or staging upload crosses the boundary.

The canonical D3DImage consumer is now also in place. `TYPE_D3DIMAGE`,
`MilCmdD3DImage`, and `MilCmdD3DImagePresent` retain their exact WPF packet
layouts with zero process-local pointer/event fields, while
`PortableD3DImageFrame` and the D3DImage external-image sideband carry the
typed lease and content version. Lease acquisition/release is the explicit
synchronization boundary and raw COM pointers remain outside the portable ABI.

This is portable ProGPU Win2D source compatibility and the Direct2D/D3DImage
consumer contract, not native Direct2D COM import. A real Windows
`ID2D1Bitmap1` or `CanvasBitmap` produced by Microsoft Win2D still requires the
same-adapter DXGI provider, including keyed-mutex/shared-fence synchronization,
format/alpha validation, and device-loss handling. That provider can now bind
directly to canonical native MIL without a CPU readback or a new scene path.

Every closed or flushed session becomes an immutable `GpuPicture`, is compiled
by `GpuPictureNativeSceneCompiler`, and is installed/rendered by
`NativeCompositor`. Stable rendering therefore crosses the managed/native
boundary through the same pointer-free scene ABI as native MIL. The normal path
does not read pixels back, copy through the CPU, or use the managed compositor.
Readback is requested only by `GetPixelBytes()` and the validation gate.

The current package is source compatible, not binary compatible with
`Microsoft.Graphics.Canvas.dll`. It intentionally fails closed for software
devices, straight/ignored alpha, non-BGRA render targets, Dawn/browser device
factories, portable Direct2D COM wrapping, cross-device resources, self-referential
texture feedback, anisotropic sampling, and high-quality cubic sampling.
Bitmap file decoding, buffer creation and updates, `MiterOrBevel`, geometry
query/stroke/outline operations,
command-list/effect image brushes, opacity
brush layers, text formats/layouts, effects, sprite batches, and XAML controls
remain the next incremental compatibility groups.
Command-list `Clear` currently fails closed because portable unbounded-clear
semantics have not been qualified. No portable API surfaces raw COM pointers.

## Validation gate

`eng/win2d.lock.json` pins both the MIT-licensed Win2D implementation and the
separate Win2D-Samples repository. `eng/progpu-verify-win2d-source.py` verifies
their exact commits, selected source hashes, the native Direct2D/Direct3D
interop contract, and the SimpleSample/shapes drawing contract before any
oracle capture or source-compatibility test.

The gate has a completed portable-core layer and three expanding oracle layers:

The Windows-native COM foundation has its own strict gate. The archived ABI v1
baseline at exact checkpoint
`59045316`, Windows 11 ARM64 under MSVC 19.44 compiles the provider and test
with `/W4 /WX`. The executable validates bad-argument rejection, all advertised
base/versioned COM queries, `ID2D1Multithread` protection, target size/DPI/
options, one real clear plus brush/rectangle draw, NT-handle reopen through
`ID3D11Device1`, and the complete keyed-mutex handoff `0 -> 1 -> 2 -> 3`.
CTest passes 1/1 in 7.74 seconds. Its eight C exports are present. SHA-256 is
`f115ea21f43c218444a2d9fd9ebb622e073a5b3cafb52ec1745990e7984e498c`
for `progpu_native_direct2d.dll` and
`cab7f76311cd5115a0f8f84ee680115eb6481c6842eb45a85eea0633c08292fc`
for `progpu_native_direct2d_tests.exe`.

The current ABI v3 gate adds transactional `BeginDraw`/`EndDraw`, safe COM
release, nested/unmatched draw rejection, zero-key Dawn ownership, and a
generic GUID-based `QueryInterface` export. The latter returns a caller-owned
reference to any later Direct2D interface supported by the installed Windows
runtime and reports `E_NOINTERFACE` explicitly; it does not emulate the
interface or depend on managed COM reflection. The gate enforces an exact
12-export allowlist. `eng/build-progpu-native-windows.ps1` builds and runs
the native test on runnable Windows x64/ARM64 agents, stages
`progpu_native_direct2d.dll` in both Windows runtime packages, and rejects any
export drift against `eng/progpu-native-direct2d-exports.txt`.
`Direct2DInteropContractTests` separately verifies the static AOT ABI, absence
of reflection/dynamic native loading and CPU copies, lock-order boundary, and
typed lease contract on every portable managed test host. The ABI v3 manual
Parallels rerun must produce fresh hashes before replacing the archived ABI v1
hashes above; a booted desktop or an unresponsive Guest Tools login is not
accepted as qualification evidence.

The pinned Win2D source audit also prevents us from calling this full Win2D
binary compatibility prematurely. Its production library contains references
to `ID2D1Effect`, bitmap/image/brush interfaces, device-context generations,
geometry and geometry-sink families, command lists, DirectWrite/WIC interop,
SVG, gradient meshes, sprite batches, and custom effects. Each group therefore
gets an explicit interface/method gate; unsupported wrapping or creation fails
closed rather than returning a partial COM object.

0. `eng/progpu-prepare-win2d-source.py` fetches the two exact locked commits,
   refuses modified tracked checkouts, and runs
   `eng/progpu-verify-win2d-source.py`. The native build then compiles the pinned
   SimpleSample plus pinned geometry/layer bodies unchanged, checks
   DPI/fail-closed contracts, and
   renders two live sessions through `ProGPU.Win2D` plus the C++ engine. The
   second session omits `Clear`, proving that the first session remains in the
   GPU target without display-list growth or CPU copies. The Apple M3 Pro
   pre-geometry Metal and Windows 11 ARM64 Parallels Display Adapter D3D12
   result was byte-identical BGRA8 premultiplied output. The expanded geometry
   frame is exact except for two antialiased curve-edge pixels differing by one
   channel level: Metal SHA-256 `BE7227D7224576EC3C74963CD18CA9736FAC67657350CC739170E496AE28991A`
   and D3D12 SHA-256 `6FEC0F3EF3F628E18395542383E487C5D8CDA6FE0B49906299A6CDB9D19BE502`.
   Ubuntu 24.04 ARM64 llvmpipe/Vulkan produced SHA-256
   `4443D80D541A386DEEEB6B35933550FE6FA437FDCC2ABA54BE8CA66E16877DF9`;
   versus D3D12 it changes 42 antialiased-edge pixels, all by exactly 1/255,
   with mean absolute channel difference `0.0003602431`. Metal changes two
   pixels, also by exactly 1/255.
   The subsequent typed-stroke frame at exact ProGPU `db43e5eb` adds a cached
   dashed/capped native geometry stroke and keeps the same differential counts:
   Metal `0D9BB2695BF85767A0AFF3683392172D9A02EE1C17D5362C38EB060E848C69BB`,
   D3D12 `CA50647DD915E8D42B4F5DD724BC96DE74383689157824186C52BF12D6B1577E`,
   and Vulkan `AABC336A0F851925C70566E1CFFEC64BE943E29B41127CE7233386C930782FF2`.
   The retained boolean-geometry frame at `d9431558` adds an exact
   rectangle-minus-circle fill: Metal
   `32F9926D292FB2A109268B42D5CC01B17EE7449EE69CEBC2CD7F2E14B24A063A`,
   D3D12 `A48B37AE5DE4E77CE0FE8F69C0C7D4E9FCC93179CAA852B247D6C41B7072D9DD`,
   and Vulkan `3191C015FF87F1FC4899DEFDFCBC5B518754B2908350E4BE47B81796C7D3C7E5`.
   Exact ProGPU `ee84a0b3` adds retained linear and radial gradient draws and
   advances the live frame to `13+2` native draws: Apple M3 Pro Metal
   `25829098701BE31CADAD8A3306D0AE4E66D50088891CD446A2B35A568108A295`,
   Parallels WDDM D3D12
   `2B516B3243BEF0C59BD0428035B748E07E737679809B505F9FCF57AE3F74F005`,
   and Ubuntu llvmpipe/Vulkan
   `FAB68DBDD8997E364EBDA6833F8F825825945DE7230F110CABC4F653C0D91E46`.
   D3D12 versus Metal still changes only two pixels by 1/255. Vulkan changes
   84 pixels by 1/255 with mean absolute channel difference
   `0.0005946181`; the added gradient interiors, endpoints, radial center,
   geometry hole, and clips satisfy their exact probes.
   Exact ProGPU `f86481b5` adds a wrapped same-device `CanvasImageBrush` checker
   as one addressed external-image draw and advances the frame to `14+2`
   native draws. Apple M3 Pro Metal produces SHA-256
   `09BA76F11AD8477D3D4852CE09B816FA84176DA8461DB5C974C2A8C6B6AC47F8`,
   Parallels WDDM D3D12 produces
   `0D1EC07A46B5CCB9495C3BB30FFE20D78CE3AD7DD5CABE03BBE7B52DA7D088A9`,
   and Ubuntu llvmpipe/Vulkan produces
   `60BD4E94ED3BBBD99A34F6577CD1FA6EF7693263E040B304D4166F0227520C64`.
   D3D12 versus Metal retains only two pixels at 1/255; Vulkan retains the
   previous 84 pixels at 1/255 with mean absolute channel difference
   `0.0005946181`. The entire image-brush region is exact across all three
   backends. The gate caught D3D12 initially clamping extended UVs; the final
   GPU-only shader address normalization fixed that backend difference without
   a CPU fallback.
   Exact ProGPU `2196beaa` adds typed bitmap and command-list image bounds plus
   cropped/destination-scaled command-list drawing. The scaled source still
   expands as the two existing immutable picture chunks, so the frame advances
   to `16+2` native draws without a render-to-texture pass, readback, upload, or
   per-primitive submission. Apple M3 Pro Metal produces SHA-256
   `AFF6CBF059B5F2CDBF24243B1DA94E41F227A4E348FD0B76F07E9D1F239C5497`,
   Parallels WDDM D3D12 produces
   `82592978570D34A2E5D110B95D963E051F01026184C23E0DF4703D7B6DEDA2B5`,
   and Ubuntu llvmpipe/Vulkan produces
   `59E132D93DDE652E0FE569162B248178F7EEA83806BA2CE0F3A7A81600B89617`.
   Metal versus D3D12 changes two pixels by 1/255 with mean absolute channel
   difference `0.0000173611`; Vulkan versus D3D12 changes the same 84 existing
   antialiased-edge pixels by 1/255 with mean `0.0005946181`. The new
   command-list bounds, crop, scale, and interior probes are exact across all
   three backends. The exact Windows archive SHA-256 is
   `7FCD5A09E672C61102066C60FEB0F9EDBEEE279521AF0251015F17AE3C5942EF`,
   and its rebuilt ARM64 `progpu_native.dll` SHA-256 is
   `39C0FD9F5B13CF277581C64096668CAF3673742719B55D6C6252AC9EB009262D`.
   The isolated typed/source contract suite passes 10/10, retained-picture
   bounds pass 4/4 on macOS, Windows ARM64, and Linux ARM64, and the exact C++
   renderer passes 10/10 native suites on Windows and Linux.
   Exact ProGPU `3390388e` adds the direct byte-upload bitmap subset without
   changing the retained draw stream. The gate creates the 2x2 checker from a
   zeroed byte array, performs one full update and one 1x1 subrectangle update,
   then verifies all four checker cells on Metal, D3D12, and Vulkan. It also
   proves that mutation after the image brush acquires its deferred texture
   lease fails closed. The live frame and `16+2` draw counts retain the three
   hashes above; Metal versus D3D12 remains two pixels at 1/255 and D3D12
   versus Vulkan remains 84 pixels at 1/255. The Win2D contract suite passes
   10/10 on macOS, Windows ARM64, and Linux ARM64, and all three benchmark builds are
   warning-free. The exact Windows source archive SHA-256 is
   `24FD8FC118952E4C51B857C01D476E06873472F43DEEBE46490C443510A98248`.
   Native C++ and its ABI are unchanged, so Windows reuses the already exact
   ARM64 DLL SHA-256
   `39C0FD9F5B13CF277581C64096668CAF3673742719B55D6C6252AC9EB009262D`;
   the pinned WinUI `generic.xaml` SHA-256 is
   `4C4085838721C0AFCB1A9EE17591C0655CDDDADB26D330788E08BCD7F1AF8285`.
   Exact ProGPU `d51b289b` adds the Color overloads and intrinsic converter.
   The 11-test contract suite verifies the four-byte WinRT ARGB layout,
   automatic and forced SIMD output against the scalar oracle, an 11-pixel
   non-vector-aligned input, destination canaries, and the 1–3-pixel automatic
   scalar tail on macOS, Windows ARM64, and Linux ARM64. The live gate replaces
   the checker with asymmetric Color values, records Vector128 for the
   four-pixel update and ScalarReference for its 1x1 update, and retains `16+2`
   draws. Metal SHA-256 is
   `D72F667FCB6AC14B2C28A1C45001734C3B62B85B1816069521C9019985D1B39B`,
   Parallels WDDM D3D12 is
   `319939D4E5CC8544502BE837B04FDD8DD68D4F54ADB8D8AB83B49D86A4120122`,
   and Ubuntu llvmpipe/Vulkan is
   `D2410112CF400C826A4855C134AE93E236932C879F690F93AA5B4422075B09C8`.
   The checker is exact across all three; the full-frame differential remains
   two Metal/D3D12 pixels and 84 D3D12/Vulkan pixels, all at 1/255 with means
   `0.0000173611` and `0.0005946181` respectively.

   The allocation-free 262,144-pixel scalar/Vector128 p50 values are
   `241.740/28.657 us` on Apple M3 Pro, `469.310/63.055 us` in the Windows ARM64
   VM, and `237.920/29.545 us` in the Ubuntu ARM64 VM, with identical checksums.
   Apple ARM64 also measures `1.742/0.240 us` at 256 pixels and
   `2.601/0.320 us` at 4,096 pixels. These results qualify the automatic SIMD
   default; VM timing remains correctness/dispatch evidence rather than a
   physical Windows performance claim. The exact Windows archive SHA-256 is
   `C8B1C7949EDE5BF18D85ED1B0E159E2C7B52056D4CA2721A4BDD493420B0477E`.
   Native C++ is unchanged and continues to use the exact qualified DLL and
   WinUI theme hashes recorded above.
   Exact ProGPU `3dad29a9` adds the three typed GPU bitmap-copy overloads. The
   live checker now reaches its destination through a whole-texture copy, a
   destination-offset 1x1 copy, and a source-subrectangle 1x1 copy before the
   same retained image-brush draw. It also verifies self-copy rejection and
   rejects destination mutation after the brush owns a deferred texture lease.
   macOS, Windows ARM64, and Linux ARM64 each pass 11/11 contracts and a
   warning-free benchmark build. Their live frame hashes remain the exact
   `d51b289b` Metal, D3D12, and Vulkan hashes above, so the asymmetric checker
   is exact and the named 2-pixel/84-pixel differential is unchanged. The
   exact Windows source archive SHA-256 is
   `C545A591DBBE3FFBE274BF6D11DED211BCC5DA41CF34107E14E2A78A9434BD01`.
   Native C++/ABI, the qualified ARM64 DLL, and the WinUI theme are unchanged.
   The frame includes full-opacity and half-opacity same-device bitmap draws;
   the source is publicly disposed before the destination session closes to
   prove that the typed GPU lease, rather than a CPU copy, owns deferred use.
   It also records a command list in two chunks separated by `Flush()`, draws
   it with an offset and with a cropped destination scale, disposes the public
   list before target submission, draws a retained quadratic/cubic path, and
   validates exact circle and rectangle layer clipping.
   VM timing is not treated as physical D3D12 performance evidence.

   CI uploads the D3D12, Metal, and Vulkan Canvas frames and runs
   `eng/progpu-compare-win2d-canvas.py`. The named differential requires at
   most 3/255 channel error, at most 0.5% changed pixels, at most 0.1% pixels
   beyond 1/255, and mean absolute channel error at most 0.01. This keeps solid
   interiors and clips exact while allowing only bounded shader rounding on
   antialiased edges.

1. Build and capture the pinned unmodified SimpleSample plus selected
   ExampleGallery scenes with real Win2D on Windows. Shapes, geometry
   operations, layers, effects, text layout, and Direct3D interop are separate
   oracle groups.
2. Compile the equivalent sample source against ProGPU Canvas, render on the
   same Windows adapter through D3D12, and compare normalized pixel output and
   resource/submission diagnostics with the native Win2D capture.
3. Run that ProGPU Canvas source on Metal and Vulkan. Compare to the Windows
   oracle after common color-space, DPI, premultiplication, and crop
   normalization. Solid primitives and bitmap sampling require exact output;
   antialiased geometry, text, and effects use named bounded differential
   contracts with zero unexplained outliers.
4. On Windows, render a Win2D command list into a shared DXGI texture, import it
   into ProGPU, composite it with a native ProGPU layer, and prove same-adapter
   identity, explicit synchronization, device-loss recovery, stable texture
   reuse, and zero CPU readback/upload.

Every oracle report records repository commits, package versions, Windows build,
adapter name/LUID, Direct2D/Direct3D feature data, DPI, pixel format, color
space, image hash, and ProGPU execution policy. macOS or Linux results are
reported as Metal/Vulkan ProGPU source-compatibility evidence, never as native
Win2D execution.

## Delivery order

1. Keep the dependency classifier and resolver fail-closed invariants green.
2. **Implemented:** add the minimal portable Canvas
   device/render-target/drawing-session API and pass pinned SimpleSample source
   plus live headless native pixels on Metal and D3D12.
3. Add the remaining geometry operations/image and layer-opacity brushes,
   bitmap file/buffer APIs, text-format/layout, and existing-effect
   adapters;
   promote each pinned ExampleGallery group only after differential parity.
4. **Foundation implemented:** create the genuine Windows Direct2D COM resource
   domain and synchronized shared-DXGI target. Next bind its producer lifecycle
   to Dawn import and Win2D `ICanvasFactoryNative`/resource-wrapper interop,
   then qualify same-adapter hardware and device loss.
5. Add WinUI, LibreWPF, and Avalonia controls as host adapters over the same
   Canvas/scene core.
6. Expand effects, sprite batching, SVG/ink/virtual bitmap, and custom effects
   according to measured application demand. Native custom COM effects remain
   Windows-only; portable custom effects use typed WGSL/HLSL-translated ProGPU
   shader contracts.

The `Microsoft.Direct3D.D3D12` NuGet package remains useful for the native
Windows D3D12/Agility SDK oracle lane. It does not provide Direct2D or make
Win2D portable, so it is not a replacement for either compatibility tier.
