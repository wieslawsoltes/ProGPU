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
| Direct2D `ID2D1*` and DirectWrite text API | Foundation plus bitmap/brush/geometry/stroke/command-list/effect/layer/state/text/SVG resources, geometry analysis/realization, vector drawing, and typed device-loss domains implemented | Windows-only native provider plus AOT-safe `ProGPU.Direct2D` managed owner return genuine factory, device, context, target, uploaded bitmaps, solid/gradient/bitmap/image brushes, gradient-stop collections, primitive/path/transformed/combined geometries, typed bounds/hit-test/relation/area/length/point-at-length, simplify/outline/widen/tessellate operations and `ID2D1GeometryRealization`, clear/transform state and typed line/rectangle/rounded-rectangle/ellipse/geometry draw/fill operations, `ID2D1StrokeStyle1`, recordable `ID2D1CommandList`, typed `ID2D1Effect` graphs and `ID2D1Image` outputs, `ID2D1Layer`, `ID2D1DrawingStateBlock1`, same-device `ID2D1SvgDocument`, shared `IDWriteFactory3`, typed `IDWriteTextFormat1`/`IDWriteTextLayout4`/`IDWriteTypography`/`IDWriteFontFaceReference`/`IDWriteFontFace5`, UTF-16 `DrawText`, retained `DrawTextLayout`, shaped/color `DrawGlyphRun`, mutable range formatting/drawing effects/OpenType features, and generic later-interface queries over a keyed-mutex BGRA DXGI target. Every managed COM handle carries a monotonic resource generation; `ID3D11Device4` removal notification, `GetDeviceRemovedReason`, and `D2DERR_RECREATE_TARGET` terminate that generation and report loss into the shared Dawn domain. There is no fake `d2d1.dll` or `dwrite.dll` |
| Native Win2D binary interop | Device/target/bitmap/brush/geometry/stroke/command-list/effect-output/text-format/text-layout/typography round trips plus layer/state/text draws package-qualified | The official factory/resource-wrapper contracts preserve exact provider identities through real `CanvasDevice`, `CanvasRenderTarget`, `CanvasBitmap`, brush, `CanvasGeometry`, `CanvasStrokeStyle`, `CanvasCommandList`, device-independent `CanvasTextFormat`/`CanvasTypography`, and device-associated `CanvasTextLayout` projections. The packaged Microsoft Win2D 1.4.0 oracle also wraps effect-output image brushes, executes typed ProGPU layer/state and native-text command-list scopes, observes ProGPU range formatting/OpenType features through the projected layout and typography, mutates that same native layout through Win2D, and draws it. It qualifies identities, resource metadata, boolean geometry/styled-stroke/image-brush/command-list/effect/text drawing and pixels, exclusive producer ownership, and zero-copy Dawn import; glyph runs/color fonts, remaining typography, the full effect catalog, custom effects, and full device-loss recreation remain gated work |
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
- [Direct2D `ID2D1CommandList`](https://learn.microsoft.com/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist)
- [Direct2D `ID2D1Layer`](https://learn.microsoft.com/windows/win32/api/d2d1/nn-d2d1-id2d1layer)
- [Direct2D `ID2D1DrawingStateBlock1`](https://learn.microsoft.com/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1drawingstateblock1)
- [Direct2D and DirectWrite](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite)
- [DirectWrite `IDWriteFactory3`](https://learn.microsoft.com/windows/win32/api/dwrite_3/nn-dwrite_3-idwritefactory3)
- [Direct2D `DrawText`](https://learn.microsoft.com/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawtext)
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

ABI v11 includes the managed ownership half as package `ProGPU.Direct2D`.
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

`TryAcquireMicrosoftWin2DCanvasDevice(...)` activates the registered
`Microsoft.Graphics.Canvas.CanvasDevice` factory, queries its official typed
`ICanvasFactoryNative` interface, and calls `GetOrCreate(...)` with the exact
provider-owned `ID2D1Device1`. Success returns one caller-owned genuine Win2D
`CanvasDevice` reference, cached by the native surface so repeated queries stay
in the exact Direct2D resource domain. The same factory wraps the exact target
`ID2D1Bitmap1` as a genuine `CanvasRenderTarget`.

`TryAcquireMicrosoftWin2DNativeDevice(...)` and
`TryAcquireMicrosoftWin2DNativeBitmap(...)` complete the reverse half for
these two resource families. The native provider queries the real wrapper's
official `ICanvasResourceWrapperNative` interface, supplies the surface's
exact CanvasDevice and DPI where required, and requests `ID2D1Device1` or
`ID2D1Bitmap1` by IID. Each result is a caller-owned `SafeHandle` COM
reference. Canonical `IUnknown` comparison in the package gate proves that
Win2D returns the original provider objects rather than a second resource
domain or an adapter-crossing copy.

ABI v7 adds the first non-surface Direct2D resource family. Typed
`CreateSolidColorBrush(...)` creates a genuine device-context-domain
`ID2D1SolidColorBrush` from finite floating-point RGBA values without beginning
a draw or acquiring the shared target. The native C ABI also adds reusable
device-domain `ICanvasFactoryNative::GetOrCreate` and
`ICanvasResourceWrapperNative::GetNativeResource` operations. The public
managed surface keeps those generic raw-pointer operations internal and exposes
kind-checked `TryAcquireMicrosoftWin2DSolidColorBrush(...)` and reverse
`TryAcquireMicrosoftWin2DNativeSolidColorBrush(...)` methods with
`DangerousAddRef` protection around every borrowed safe-handle pointer. This
shape is reusable for later resource families without exposing arbitrary COM
reinterpretation as a managed contract.

ABI v8 adds the gradient dependency chain. A pinned blittable
`ReadOnlySpan<ProGpuDirect2DGradientStop>` flows directly into genuine
`ID2D1GradientStopCollection1` creation with explicit pre/post color spaces,
buffer precision, extend mode, and straight/premultiplied interpolation. There
is no intermediate managed array. Typed linear/radial creation consumes a
kind-checked caller-owned collection, finite geometry, opacity, and affine
transform, while `DangerousAddRef` keeps the collection alive across the native
call. The resulting real `ID2D1LinearGradientBrush` and
`ID2D1RadialGradientBrush` use the same v7 generic native Win2D seam; public
managed wrap/reverse methods remain kind-specific.

ABI v9 adds the device-independent geometry family. The C ABI creates genuine
`ID2D1RectangleGeometry`, `ID2D1RoundedRectangleGeometry`,
`ID2D1EllipseGeometry`, `ID2D1PathGeometry1`, and
`ID2D1TransformedGeometry` objects and lowers union/intersect/XOR/exclude
combinations through `ID2D1Geometry::CombineWithGeometry`. Path figures carry
explicit fill/close state; line, quadratic, cubic, and arc segments retain
stroke/join, sweep, and large-arc flags. The managed surface accepts both the
low-level blittable spans and the existing neutral `PortablePrimitiveGeometry`
and `PortableGeometryPath` contracts shared with LibreWPF retained replay.
Small paths use stack spans and larger paths rent bounded arrays; there is no
reflection, per-segment native submission, or CPU tessellation. Kind-checked
`CanvasGeometry` wrap/reverse methods preserve canonical COM identity.

ABI v10 adds genuine factory-domain `ID2D1StrokeStyle1` creation with typed
cap, join, dash, miter, offset, and transform-behavior metadata. Custom dash
patterns cross the managed/native boundary as one pinned blittable span and
are copied by Direct2D during resource creation, avoiding per-dash COM calls or
an intermediate managed array. Invalid enum values, non-finite lengths,
all-zero custom patterns, missing custom spans, and spans supplied to
predefined dash styles fail closed. The package oracle wraps the exact resource
as Microsoft Win2D `CanvasStrokeStyle`, reverse-unwraps it to the same canonical
`ID2D1StrokeStyle1`, validates custom-dash metadata, and uses it for a genuine
styled geometry draw.

ABI v11 adds one synchronous, pinned-span upload for immutable premultiplied
BGRA8 `ID2D1Bitmap1` resources and genuine device-context-domain
`ID2D1BitmapBrush1` creation. Width, height, stride, byte extent, DPI, extend
modes, interpolation mode, opacity, and transform are validated in both
managed and native layers. Direct2D copies the source bytes during creation;
the provider neither retains the caller span nor allocates an intermediate
managed array. The paired Win2D seams wrap the exact bitmap as `CanvasBitmap`
and the exact bitmap brush as `CanvasImageBrush`, then reverse-unwrap both by
canonical COM identity. This is an explicit image upload path, not a fallback
readback or per-pixel CPU renderer; same-device target sharing remains
zero-copy.

ABI v12 adds genuine device-context-domain `ID2D1ImageBrush` creation over a
typed same-domain image with an explicit image-space source rectangle, extend
modes, interpolation mode, opacity, and transform. Managed and native layers
reject empty/non-finite rectangles and invalid enum values before calling
Direct2D. The existing Win2D `CanvasImageBrush` projection now accepts either
of Win2D's documented native representations while reverse unwrapping requires
the caller to request `ID2D1BitmapBrush1` or `ID2D1ImageBrush` explicitly. The
signed oracle uses a two-color bitmap and selects only its second column, so
the distinct output pixel proves source-rectangle semantics in addition to
exact forward/reverse COM identity.

ABI v13 adds genuine same-device `ID2D1CommandList` creation and a typed
recording transaction. `BeginCommandListDrawing(...)` rejects overlap with
Direct2D, Win2D, another command-list producer, or a deferred texture lease;
sets the command list as the exact `ID2D1DeviceContext1` target; and begins
drawing without taking keyed-mutex ownership because recording does not touch
the shared texture. Session disposal ends drawing, restores the shared bitmap
target, closes the list, retains caller ownership safely across the native
call, and leaves `ContentVersion` unchanged. A failed native completion still
clears the managed producer claim and preserves the original exception stack.
The closed list is a valid typed source for `CreateImageBrush(...)` and can be
wrapped and reverse-unwrapped as Microsoft Win2D `CanvasCommandList` with exact
canonical COM identity. Win2D closes an interop-created command list lazily
when it first realizes it as an image; the signed gate deliberately exercises
that public image-realization path before consuming the same native list as an
`ID2D1ImageBrush`, matching Win2D's
[`CanvasCommandList` implementation](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/lib/images/CanvasCommandList.cpp).

`TryBeginMicrosoftWin2DProducerAccess(...)` ends Dawn ownership, acquires the
keyed mutex without beginning a second native Direct2D drawing context, and
returns the CanvasRenderTarget. The caller creates, uses, and disposes its real
Win2D `CanvasDrawingSession` before disposing this outer scope. Outer disposal
releases producer ownership, resumes Dawn access, advances `ContentVersion`,
and publishes `TextureChanged`. Active Direct2D/Win2D producer scopes and
deferred ProGPU texture leases reject overlap.

Win2D package registration returns `false` plus the activation HRESULT; an
uninitialized Windows Runtime apartment and an incompatible factory remain
distinct typed failures. ProGPU deliberately does not call `RoInitialize` or
`RoUninitialize` on behalf of the host, because apartment lifetime belongs to
the calling thread, and it does not search for or load
`Microsoft.Graphics.Canvas.dll`. Desktop applications must initialize every
thread that activates or uses the returned WinRT object and must make the Win2D
package available through their normal package/dependency graph.

This is not yet the complete native Win2D bridge. Typed effect graphs,
DirectWrite formats/layouts, and basic text drawing are now present. The next
interop boundaries are glyph runs/color fonts, remaining layout typography,
image/effect families, and device-loss recreation of the entire cached
resource domain. Each family must pass the same forward-wrap/reverse-unwrap
identity and actual-draw gate before it is advertised.

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

The current ABI v21 gate includes transactional `BeginDraw`/`EndDraw`, safe COM
release, nested/unmatched draw rejection, zero-key Dawn ownership, and a
generic GUID-based `QueryInterface` export. The latter returns a caller-owned
reference to any later Direct2D interface supported by the installed Windows
runtime and reports `E_NOINTERFACE` explicitly; it does not emulate the
interface or depend on managed COM reflection. ABI v5 additionally gates exact
`ICanvasFactoryNative` CanvasDevice/CanvasRenderTarget wrapping, the exclusive
Win2D producer scope, and typed runtime-unavailable behavior. ABI v6 adds the
typed `ICanvasResourceWrapperNative` reverse query and exact device/bitmap COM
identity. ABI v7 adds typed `ID2D1SolidColorBrush` creation plus reusable
factory-native wrap and resource-wrapper-native unwrap operations. The gate
also requires exact solid-brush projection and drawing. ABI v8 adds
`ID2D1GradientStopCollection1`, linear-gradient brush, and radial-gradient
brush creation while reusing those generic identity operations. The gate
also preserves their typed metadata and exact native identities. ABI v9 adds
primitive/path/transformed/
combined geometry creation, real `FillGeometry`/`DrawGeometry` execution, and
exact `ID2D1Geometry <-> CanvasGeometry` identity. ABI v10 adds typed
`ID2D1StrokeStyle1` creation, one-span custom dashes, styled drawing, and exact
`ID2D1StrokeStyle1 <-> CanvasStrokeStyle` identity. ABI v11 adds pinned-span
`ID2D1Bitmap1` upload, typed `ID2D1BitmapBrush1` creation, strict truncated-row
rejection, a real bitmap-brush fill, and exact
`ID2D1Bitmap1 <-> CanvasBitmap` plus
`ID2D1BitmapBrush1 <-> CanvasImageBrush` identities.
ABI v12 adds strict `ID2D1ImageBrush` source-rectangle creation, malformed
rectangle rejection, a genuine image-brush fill, and exact
`ID2D1ImageBrush <-> CanvasImageBrush` identity.
ABI v13 adds open-list creation, exclusive record/end/restore/close
transactions, unchanged shared-surface content version during offscreen
recording, command-list-backed image-brush drawing, and exact
`ID2D1CommandList <-> CanvasCommandList` plus command-list
`ID2D1ImageBrush <-> CanvasImageBrush` identities.
ABI v14 adds typed system/custom effect creation by CLSID, fixed-layout
property values, direct image and effect-to-effect inputs, caller-owned
`ID2D1Image` outputs, and effect-output image-brush drawing. Pointer-bearing
property types and malformed fixed values fail closed. The gate enforces an
exact 39-export allowlist.
ABI v15 adds genuine `ID2D1Layer` and `ID2D1DrawingStateBlock1` creation,
pointer-free typed layer parameters, optional geometry-mask and opacity-brush
references, drawing-state save/restore, and balanced layer pushes for both
shared-surface and command-list draws. The managed layer scope is an
allocation-free `ref struct`, so normal LIFO use adds no scope allocation.
Out-of-order managed
disposal, unmatched native pops, and unbalanced native EndDraw fail closed;
the native provider unwinds its own typed layer stack before ending or closing
the target so the context does not remain poisoned. The gate enforces an exact
45-export allowlist.
ABI v16 creates one genuine shared `IDWriteFactory3` with the surface and
returns caller-owned `IDWriteTextFormat1` resources from explicit UTF-16
family/locale spans plus a fixed-layout, pointer-free descriptor. The hot draw
path pins the caller's `ReadOnlySpan<char>` and submits it directly to
`ID2D1RenderTarget::DrawText`; it performs no provider-side text copy,
readback, repack, reflection, or per-glyph interop call. Both shared-surface
and command-list sessions expose the same typed operation. Unknown enum/flag
values, malformed descriptors, embedded NUL names, invalid rectangles, wrong
COM resource kinds, and calls outside an active draw fail closed.
Device-independent Win2D wrapping supplies a null CanvasDevice and zero DPI,
as required for `CanvasTextFormat`, while reverse unwrapping requests the
exact `IDWriteTextFormat1` IID and preserves canonical COM identity. The gate
enforces an exact 47-export allowlist.
ABI v17 creates retained caller-owned `IDWriteTextLayout4` resources from one
typed UTF-16 span, an existing text format, and finite positive layout bounds.
DirectWrite copies the text during layout creation; ProGPU retains neither the
caller span nor a duplicate provider buffer. Shared-surface and command-list
sessions draw the retained layout through `ID2D1RenderTarget::DrawTextLayout`.
Win2D wrapping deliberately supplies the surface's exact CanvasDevice because
Microsoft's `CanvasTextLayout` is device-associated even though it derives
from the DirectWrite text-format interfaces. Reverse unwrapping requests the
exact `IDWriteTextLayout4` IID and must preserve canonical COM identity.
Malformed bounds, non-finite origins, wrong resource kinds, unknown options,
and calls outside an active draw fail closed. The gate enforces an exact
49-export allowlist.
ABI v18 adds one typed, pointer-free range-format descriptor over the retained
layout. Selected font size, numeric weight, style, stretch, underline, and
strikethrough state is applied through the genuine `IDWriteTextLayout` mutable
API. A separately kind-checked `ID2D1Brush` becomes the optional DirectWrite
drawing effect, while a null value explicitly restores the draw-call default.
The managed owner holds borrowed COM references across the synchronous call,
rejects unknown flags, zero/overflowing ranges, malformed selected values, and
non-brush drawing effects, and performs no reflection, string conversion, or
per-character interop. The official Win2D gate reads the ProGPU-authored range
through `CanvasTextLayout`, mutates another range back through Win2D, and draws
the same canonical layout. The gate enforces an exact 50-export allowlist.
ABI v19 creates a genuine device-independent `IDWriteTypography` from one
pinned, bounded span of nonzero OpenType name tags and parameters, then applies
that object to a retained-layout UTF-16 range through
`IDWriteTextLayout::SetTypography`. DirectWrite owns its copied feature list;
ProGPU retains no caller span and performs one managed/native crossing rather
than one interop call per feature. The public four-character tag helper accepts
printable ASCII and produces DirectWrite's little-endian tag layout. The
generic Win2D seam now classifies typography as device-independent, wraps it as
official `CanvasTypography`, reverse-unwraps the exact `IDWriteTypography`, and
checks both feature metadata and canonical identity. Empty/oversized feature
sets, zero tags, empty/overflowing ranges, and wrong COM kinds fail closed. The
gate enforces an exact 52-export allowlist.
ABI v20 resolves an installed family/weight/style/stretch tuple through the
shared DirectWrite system collection and returns a genuine device-independent
`IDWriteFontFaceReference`. A second typed operation creates the corresponding
`IDWriteFontFace5`, and both shared-surface and command-list sessions submit
already-shaped index/advance/offset spans directly through
`ID2D1DeviceContext::DrawGlyphRun`. Caller spans remain pinned only for the
synchronous call; the provider performs no glyph copy, text reshaping, CPU
raster fallback, readback, or per-glyph interop submission. Counts are bounded,
optional advance/offset spans must be empty or exact, every floating value must
be finite, and invalid bidi/resource/draw state fails closed. The Win2D seam
uses the official device-independent `CanvasFontFace` mapping, reverse-unwraps
the exact `IDWriteFontFaceReference`, and checks canonical COM identity before
using Win2D-projected glyph indices for a native draw. The gate enforces an
exact 55-export allowlist.
GitHub Actions Build run `33326634929`, MSVC job `99297867722`, qualifies the
ABI v20 implementation on Windows after compiling and linking both the
provider and its native regression with the repository's warning-as-error
policy. The focused `progpu_native_direct2d_tests` executable passes in
0.16 seconds, all 11 native tests pass, and the successful build script also
accepts the exact 55-symbol Direct2D export allowlist.
ABI v21 adds GPU-native color-font drawing for the same already-shaped spans.
The fastest qualified path queries `ID2D1DeviceContext7` and calls
`DrawGlyphRunWithColorSupport`, covering current COLR paint-tree, SVG, bitmap,
layered-color, and monochrome representations inside Direct2D. A down-level
Windows 10 path uses `IDWriteFactory4::TranslateColorGlyphRun` and
`ID2D1DeviceContext4` `DrawColorBitmapGlyphRun`, `DrawSvgGlyphRun`, or
`DrawGlyphRun` per enumerated representation; `DWRITE_E_NOCOLOR` alone selects
the explicit monochrome path. It does not decode font images on the CPU or
read pixels back. The selected context7/translated-context4/no-color path is a
typed diagnostic returned to the caller. The allowlist grows from 55 to
exactly 56 exports.
GitHub Actions Build run `33327156224`, MSVC job `99299265980`, qualifies ABI
v21 on Windows. The warning-as-error provider and regression both compile and
link, the focused `progpu_native_direct2d_tests` passes in 0.14 seconds, all 11
native suites pass, and the same successful job enforces the exact 56-symbol
Direct2D export allowlist.
ABI v22 adds genuine same-device `ID2D1SvgDocument` resources. A bounded
borrowed `IStream` exposes caller-owned UTF-8 bytes directly to
`ID2D1DeviceContext5::CreateSvgDocument`; Direct2D consumes it synchronously,
and the provider neither retains the span nor creates an intermediate XML
copy. Surface and command-list transactions draw the document through
`ID2D1DeviceContext5::DrawSvgDocument` with temporary Win2D-compatible
viewport and origin state that is restored before return. Factory identity,
finite positive viewports, the 64 MiB input bound, concrete COM kind, and
active-draw ownership fail closed. The generic Win2D seam wraps the exact
resource as `CanvasSvgDocument` and reverse-unwraps the canonical
`ID2D1SvgDocument` identity. Direct2D's supported SVG subset remains the
semantic boundary; this is not a browser SVG implementation. The allowlist
grows from 56 to exactly 58 exports.
GitHub Actions Build run `33328289063`, MSVC job `99302278126`, qualifies ABI
v22 on Windows after compiling and linking the warning-as-error provider and
native regression. The focused `progpu_native_direct2d_tests` passes in 0.49
seconds, all 11 native suites pass, and the job accepts the exact 58-symbol
Direct2D export allowlist.
ABI v23 adds a typed persistent device-domain state rather than attempting to
repair stale COM objects in place. Each surface receives a nonzero monotonic
resource generation. The provider registers an `ID3D11Device4` removal event
when the Windows runtime exposes it, polls that event without blocking, and
confirms the terminal HRESULT through `ID3D11Device::GetDeviceRemovedReason`.
`DXGI_ERROR_DEVICE_REMOVED`, `DXGI_ERROR_DEVICE_RESET`, and
`D2DERR_RECREATE_TARGET` are retained even if a later cleanup call succeeds.
Managed `ProGpuDirect2DComReference` instances share the generation's typed
loss token: cross-generation use and direct `QueryInterface` after loss fail
closed. `ProGpuDirect2DSurface.DeviceLost` is one-shot, reports the same loss
to `WgpuContext`, and instructs the host to create a new Dawn/Direct2D domain
and rebuild device-dependent resources, matching Win2D's recovery contract.
The ABI remains allocation-free on polling and grows from 58 to exactly 59
exports. A deterministic native regression covers invalid struct rejection,
initial non-lost state, removal-event registration, and unique replacement
generations. Destructive physical-adapter removal remains a separate opt-in
Windows integration gate; it is not simulated by silently recovering or by
falling back to CPU rendering.
GitHub Actions Build run `33329548704`, dedicated MSVC job `99305585595`,
qualifies exact implementation `d67fe1bf` on Windows x64. MSVC compiles and
links the provider plus regression under the warning-as-error lane, the focused
`progpu_native_direct2d_tests` passes in 0.15 seconds, all 11 configured native
suites pass, and the successful job enforces the exact 59-symbol Direct2D
allowlist. The broader ClangCL Windows job `99305585623` independently compiles
the same provider and passes the focused regression in 0.14 seconds plus all 12
configured native suites. That broader job later loses the unrelated Dawn
device during a long managed Microsoft Basic Render Driver readback, after the
Direct2D DLL, export, and CTest gates completed; it is retained as a separate
software-D3D12 stability failure rather than mislabeled as ABI v23 failure.

ABI v24 adds the first typed `ID2D1Geometry` analysis surface needed to remove
WPF-side CPU bounds and hit-test reconstruction. Eight AOT-safe operations call
the genuine COM resource for bounds, widened bounds, fill and stroke point
containment, geometry relation, area, length, and point/unit-tangent sampling.
Optional affine transforms and `ID2D1StrokeStyle1` references are validated,
borrowed under `DangerousAddRef`, and required to belong to the same monotonic
resource generation. Invalid points, widths, lengths, transforms, and
flattening tolerances fail before COM; native outputs are zero-initialized on
rejection. The rectangle result explicitly converts Direct2D's
`left/top/right/bottom` layout to ProGPU's `x/y/width/height` contract. There
is no reflection, geometry replay, CPU tessellation, pixel readback, or
cross-generation pointer reuse. The export allowlist grows from 59 to exactly
67 symbols. Geometry sinks for simplify, outline, widen, tessellation, and
realizations remain the next versioned slice rather than exposing arbitrary
caller COM sink pointers.
Exact implementation `13f6906b` is qualified by GitHub Actions Build run
`33330942215`. Dedicated MSVC job `99309300180` compiles and links the provider
and regression under warning-as-error, passes the focused Direct2D test in
0.25 seconds and all 11 native suites, and enforces the 67-export allowlist.
ClangCL x64 job `99309300268` independently compiles the DLL and passes the
focused test in 0.14 seconds plus all 12 native suites before the already
tracked unrelated long Dawn readback loses Microsoft Basic Render Driver.

ABI v25 keeps the remaining geometry-sink COM interfaces private to the
provider. `Simplify`, `Outline`, and `Widen` materialize into caller-owned
`ID2D1PathGeometry1` resources created by the same factory. `Tessellate` writes
blittable triangles directly into a caller-owned span and reports the required
count with a typed `InsufficientBuffer` status, enabling an allocation-free
hot path and an explicit size-query/retry path without retaining memory or
allocating per triangle. The same version creates filled and stroked
`ID2D1GeometryRealization` resources through `ID2D1DeviceContext1` and draws
them in either shared-surface or command-list producer scopes. Geometry,
optional stroke style, realization, and brush handles are kind-checked,
generation-checked, and protected across every borrowed call. Invalid
options, widths, transforms, tolerances, destinations, and inactive draws fail
closed. The allowlist grows from 67 to exactly 74 exports. This is native
Direct2D realization, not CPU tessellation fallback; the portable Metal,
Vulkan, and browser paths continue to use ProGPU's shared vector pipeline.
Final implementation `9dc74d09` is qualified by GitHub Actions Build run
`33332388195`: Ubuntu aggregate job `99313260684` builds and passes the managed
suite, while dedicated MSVC job `99313260762` compiles/links the 74-export DLL
and regression under warning-as-error, passes the focused Direct2D test in
0.14 seconds, and passes all 11 native suites. The immediately preceding
native-identical corrected sink commit `84ece34c` also passes ClangCL x64 job
`99312705172` in 0.15 seconds and all 12 native suites; `9dc74d09` changes only
the managed operation-label scope subsequently covered by the aggregate job.

ABI v26 adds the first complete typed immediate vector-drawing slice over the
genuine `ID2D1DeviceContext1`. Shared-target and command-list sessions expose
the same `Clear` and affine `Transform` state plus line, rectangle,
rounded-rectangle, ellipse, and arbitrary geometry fill/stroke operations.
Optional stroke styles and opacity brushes remain explicit typed resources.
Managed code validates finite coordinates, nonnegative sizes/radii/widths,
interface kind, resource generation, and active producer ownership, then
protects every borrowed safe handle across the native call. Native code
repeats the scalar checks, uses `QueryInterface` for every COM argument, and
reports deferred Direct2D failures through the existing `EndDraw` transaction.
The operation path allocates no command arrays, performs no CPU pixel copy,
and works identically while recording an `ID2D1CommandList`. The allowlist
grows from 74 to exactly 86 exports. Clip-stack operations are intentionally
the following ABI because clips and layers require one unified LIFO state model
so their cross-ordering cannot be represented incorrectly by independent
depth counts.
The native regression also copies the shared command-list result to a D3D11
staging texture under keyed-mutex consumer ownership and requires the exact
BGRA pixel produced by the typed vector path. Corrected checkpoint `f1b1ca18`
is qualified by GitHub Actions Build run `33333671491`, dedicated MSVC job
`99316705077`: the warning-as-error provider and regression compile/link, the
focused Direct2D test passes in 0.16 seconds, all 11 native suites pass, and
the Windows build script accepts the exact 86-symbol allowlist. Portable
managed contract coverage passes 5/5 with zero build warnings. The broader
ClangCL/x64 lane remains useful independent coverage when its job completes;
it is not needed to infer the already observed MSVC result.

ABI v27 implements that unified allocation-free draw-scope model. Each surface
owns a fixed-capacity stack tagged as layer or axis-aligned clip. Managed layer
and clip ref scopes share the same depth sequence; native `PopLayer` and
`PopAxisAlignedClip` verify the top tag before touching Direct2D. A mismatched
pop therefore reports `DrawingStateMismatch` and leaves the correct scope live,
while `EndDraw`, command-list completion, and destruction unwind mixed scopes
in exact reverse order. The same ABI adds typed `DrawBitmap` with optional
destination/source rectangles and full 4x4 perspective, plus typed `DrawImage`
with optional target offset/image rectangle and every Direct2D interpolation
and composite mode. Bitmap/image safe handles remain generation checked and
borrowed under `DangerousAddRef`; all optional structs are blittable caller
state with no retained pointer. The export allowlist grows from 86 to exactly
90 symbols. The deterministic command-list gate draws both a bitmap and an
image inside a clip, verifies mixed layer/clip pop rejection, and retains the
exact shared-texture BGRA oracle. Portable managed contracts pass 5/5 with a
zero-warning package build. Exact checkpoint `10ef4c1a` is qualified by GitHub Actions
Build run `33334553038`, dedicated MSVC job `99319045125`: the warning-as-error
provider and native regression compile/link, the focused Direct2D test passes
in 0.16 seconds, all 11 native suites pass in 1.05 seconds, and the successful
Windows build accepts the exact 90-symbol allowlist.

ABI v28 adds typed drawing-session state over the same genuine
`ID2D1DeviceContext1`. Shared-target and command-list sessions can round-trip
geometry antialiasing, text antialiasing, primitive blend, DIP/pixel unit mode,
two 64-bit diagnostic tags, and render-target DPI. Managed and native layers
validate every enum and require either two positive finite DPI values or the
Direct2D `(0, 0)` reset-to-96-DPI form; unknown or half-zero state fails closed
before touching the context. All state operations require the active typed
producer and allocate no command object or callback. The native regression
round-trips every property, rejects an unknown antialias value, then restores
defaults before the existing clipped bitmap/image and exact-BGRA oracle. The
allowlist grows from 90 to exactly 102 exports. Portable managed contracts pass
5/5 and the package builds with zero warnings. Exact checkpoint `ac10d4af` is
qualified by GitHub Actions Build run `33335230522`, dedicated MSVC job
`99320851539`: warning-as-error compile/link succeeds, the focused Direct2D
regression passes in 0.17 seconds, all 11 native suites pass in 1.07 seconds,
and the successful Windows build accepts the exact 102-symbol allowlist.

ABI v29 adds mutable brush state required by real Win2D resource projections.
Typed, generation-checked methods atomically set/query common `ID2D1Brush`
opacity and affine transform, solid color, linear start/end points, and radial
center/origin/radii. Managed and native boundaries validate finite values,
opacity range, radius range, and concrete COM interface kind; borrowed safe
handles remain protected for each call. The native regression round-trips every
property and restores the solid brush before the exact pixel oracle. The exact
allowlist grows from 102 to 110 exports. Managed contracts pass 5/5 and the
package builds with zero warnings. Exact checkpoint `2086632e` is qualified by
Build run `33336026310`, dedicated MSVC job `99322989531`: warning-as-error
compile/link succeeds, the focused Direct2D regression passes in 0.14 seconds,
all 11 native suites pass in 1.02 seconds, and the successful Windows build
accepts the exact 110-export allowlist.

ABI v30 completes mutable bitmap/image-brush resource state. Typed methods
set/query bitmap-brush extend and interpolation modes, image-brush source
rectangle/extend/interpolation state, and nullable bitmap/image bindings.
Resource queries return caller-owned genuine interfaces; native coverage proves
canonical COM identity after detach/rebind and exact null state. Managed code
keeps both brush and optional source handles alive, requires the same resource
generation and concrete kind, and native code independently uses
`QueryInterface` for creation and mutation instead of pointer reinterpretation.
No pixel copy, readback, repack, reflection, or managed command allocation is
introduced. The regression restores all properties and bindings before the
existing rendering/Win2D oracles. The allowlist grows from 110 to exactly 118
exports; managed contracts pass 5/5 and the package builds with zero warnings.
Corrected checkpoint `058f6f1f` is qualified by Build run `33336912843`,
dedicated MSVC job `99325361848`: warning-as-error compile/link succeeds, the
focused Direct2D regression passes in 0.18 seconds, all 11 native suites pass
in 1.33 seconds, and the exact 118-export allowlist is accepted.

ABI v31 adds typed `ID2D1Bitmap1` metadata and update operations without
introducing a readback fallback. Callers can query pixel/DIP size, DPI, pixel
format, alpha mode, and bitmap options; upload a bounded rectangle directly
from a caller-owned byte span with an explicit pitch; or copy a bounded source
rectangle between same-generation bitmaps on the GPU. Managed code pins the
input span only for the synchronous native call and performs no array copy or
repack. Both boundaries validate dimensions, pitch, byte extent, resource
kind/generation, and source/destination bounds; forced self-copy and unsupported
pixel formats fail closed. The native regression verifies separate exact BGRA
pixels for the memory upload and GPU bitmap copy while retaining the existing
shared-texture oracle. The allowlist grows from 118 to exactly 121 exports;
managed contracts pass 5/5 and the package builds with zero warnings. Exact
implementation checkpoint `2d24157d` is qualified from immutable archive
SHA-256 `CBEF4F7F71DE3B61B43CE0A1C2C14941B0589C6440C92F0CD7553FA4DBAE82E3`
in the Windows 11 ARM64 Parallels VM. MSVC 19.44 and Windows SDK 10.0.26100.0
compile/link the focused provider and test under `/W4 /WX`; the exact 121-
export comparison passes and the live Direct2D regression passes 1/1. The DLL
SHA-256 is `07751974494C643CF899F60988AED1335EC10BF493E26142099528D4041B7C1C`.
Build run `33337753262`, x64 native job `99327677774`, independently compiles
the provider, passes the focused regression in 0.17 seconds, and passes all 12
native suites in 1.14 seconds. That job's later managed WebGPU sample lost the
Microsoft Basic Render Driver device; it does not invalidate the preceding
Direct2D compile, export, COM, and exact-pixel gates.

ABI v32 establishes the first Direct2D-command-to-ProGPU translation boundary
through the API designed for that purpose: `ID2D1CommandList::Stream`. An
internal allocation-free `ID2D1CommandSink1` receives genuine Direct2D/Win2D
callbacks and returns one 64-byte pointer-free structural summary. It counts
state, clear, draw, fill, text, image, clip, and layer operations, validates the
mixed clip/layer LIFO stack to depth 4,096, and never retains callback resource
pointers. Audit mode reports unsupported classes; strict mode returns
`E_NOTIMPL` from `EndDraw`, and therefore from `Stream`, for non-null text
rendering parameters, GDI metafiles, meshes, or opacity masks. This is an
operation-set preflight, not yet resource translation or scene execution.
Managed callers use generation-checked AOT APIs to inspect or strictly validate
a closed command list. Exact implementation `3f5078af` plus MSVC oracle fix
`8e812820` builds with zero managed warnings, passes contracts 5/5, and exposes
exactly 122 exports. The incremental Windows 11 ARM64 MSVC 19.44/SDK
10.0.26100.0 gate compiles the complete vtable under `/W4 /WX`, passes the
supported and fail-closed stream regressions 1/1, and produces provider SHA-256
`E2A0F827107450E5C6D0ED8C2CA3C8C20656F6A32C1A6361DB788C14117CD1D3`.
Clean-checkout Build run `33339953074` is pending.

ABI v33 at implementation `bb4818bf` crosses that boundary into actual native
scene emission. A second internal `ID2D1CommandSink1` converts a deliberately
closed first subset—finite transforms, source-over/DIPs state, solid-color
brushes, `FillRectangle`, default-style `DrawRectangle`, default flat-cap
`DrawLine`, aliased/per-primitive edges, and one optional leading `Clear`—into
the same pointer-free semantic stream consumed by ProGPU's C++ renderer. The
clear is explicit frame metadata rather than a fabricated retained draw.
At that checkpoint gradient/image brushes, geometry, text, images, clips,
layers, non-default stroke styles, blend modes other than source-over, pixel
units, and mid-stream or repeated clears returned `E_NOTIMPL` with a typed
failure class and one-based callback index; no partial stream was returned.

The ABI is a two-pass caller-buffer contract: the first call reports the exact
stream size and the second serializes directly into the supplied span. No COM
pointer enters the scene, and there is no managed staging array, pixel
readback, pixel repack, or CPU raster fallback. Managed APIs preserve resource
domain/generation validation and pin the destination only for the synchronous
call. The Direct2D DLL now links the backend-neutral native scene builder, so
the output is reusable by the D3D12, Metal, Vulkan, and WebGPU executors rather
than becoming a Windows-only second renderer.

The managed package builds with zero warnings, portable contracts pass 5/5,
and the allowlist is exactly 123 exports. The incremental Windows 11 ARM64
Parallels gate used MSVC 19.44/SDK 10.0.26100.0 under `/W4 /WX`; it compiled
the full sink vtable and scene builder, passed the live Direct2D regression
1/1 in 3.35 seconds, decoded the emitted three-draw scene header, and verified
typed rejection of non-null DirectWrite rendering parameters. `dumpbin`
reported exactly 123 provider exports. The resulting DLL SHA-256 is
`0C552556B68BDB2F34B9B4ADA552B1DBBC2EB25A247483ED27710787CBF787D2`.
Clean-checkout MSVC compatibility job `99339089791` on checkpoint `b91df2da`
passes; the longer Windows renderer jobs were superseded by the ABI v34 push.

ABI v34 at implementation `c4dca894` adds exact nested aliased
`PushAxisAlignedClip`/`PopAxisAlignedClip` translation. Each finite clip is
transformed by the Direct2D transform active at push time, then intersected
with the prior target-space clip. The translator records that intersection as
a native scene-state resource and emits balanced save/restore commands, so
later transform changes cannot move an already-pushed clip. The admitted depth
is the native scene maximum of 64; overflow has an explicit capacity failure.
Clear inside a clip and unbalanced pops fail closed.

Direct2D per-primitive clip antialiasing remains rejected with a typed
unsupported-state result because ProGPU rectangle clips currently resolve to
an exact scissor. Treating that as an antialiased coverage edge would be a
silent fidelity loss; it will be admitted only with the native path/mask
coverage implementation. The Windows test decodes both state resources from a
seven-command scene and verifies the non-identity transformed outer clip
`[3,5,37.5,22.5]` plus nested intersection `[15.5,12.5,25,15]` exactly. It also
proves that per-primitive antialiased clips return `E_NOTIMPL` without a partial
stream. The managed build remains warning-free, contracts pass 5/5, and the
allowlist remains exactly 123 exports.

Incremental Windows 11 ARM64 Parallels MSVC 19.44/SDK 10.0.26100.0
qualification passes compile/link under `/W4 /WX` and the live Direct2D
regression 1/1. The provider SHA-256 is
`9C38D9BFFC95D7453EDCA5F3D63B53C973C1E24F9DDA2EB3214477BF497464AE`.
Clean-checkout ABI v34 CI qualification is pending.

ABI v35 at implementation `226085da` adds linear and radial gradient brushes
to the command-list translator without adding a Windows-only renderer. The
sink queries genuine `ID2D1LinearGradientBrush`, `ID2D1RadialGradientBrush`,
and `ID2D1GradientStopCollection1` interfaces synchronously, snapshots bounded
stops into the canonical ProGPU brush table, and releases every COM reference
before returning. Linear endpoints, radial center/radii, and the radial origin
(`center + GradientOriginOffset`) remain exact typed values. Clamp, wrap, and
mirror map to the shared pad, repeat, and reflect shader modes.

Brush coordinates are evaluated in Direct2D render-target space. The
translator therefore composes the inverse active draw transform with the
inverse brush transform and stores that affine mapping in the existing ProGPU
gradient record. A per-translation COM-identity cache is also keyed by the
active draw transform, so reuse avoids duplicate stop capture without
incorrectly sharing a transform-dependent material. The Windows regression
uses the same linear brush under two different draw transforms and decodes two
distinct coordinate mappings (`[0.8, 1.333..., -6.4, -4.666...]` and identity
scale with `[-4,2]` translation) from the resulting pointer-free scene.

The admitted color subset is explicit. Pre- and post-interpolation spaces must
both be sRGB. Straight-alpha interpolation is accepted; Direct2D premultiplied
interpolation is accepted only when every stop has the same alpha, where it is
mathematically equivalent to straight interpolation. A varying-alpha
premultiplied collection returns typed unsupported state and no partial
stream. Custom/scRGB conversion and non-invertible brush or draw transforms
also fail closed. Direct2D buffer precision remains a source rasterization
quality choice; ProGPU retains the original finite float stops and the
cross-backend pixel gate, rather than quantizing them on the CPU.

The positive oracle contains six draws, two nested clips, four brushes, and
six gradient stops; it decodes linear/radial parameters, stop offsets, spread,
opacity, and transform-dependent cache separation. The negative oracle proves
the varying-alpha premultiplied boundary. The managed AOT contracts pass 5/5
and `ProGPU.Direct2D` builds with zero warnings. An incremental three-file
Windows payload with SHA-256
`B545679CDCC7C81A826A333D3975C8BB7E8ED977A58FFBFC0601D4431DAAA368`
was applied to the existing qualification tree. Windows 11 ARM64 Parallels,
MSVC 19.44, and SDK 10.0.26100.0 compile provider and test under `/W4 /WX`;
the live COM regression passes 1/1 in 1.70 seconds (2.01 seconds total under
concurrent VM load). The export allowlist remains exactly 123. Provider
SHA-256 is
`E5651DF33F23EB909FF2AB42F2A4E3592CDE81E21B57B3ADABFF38F493FDC2ED`.
Clean-checkout ABI v35 CI qualification is pending.

ABI v36 at implementation `e9788c5e` adds genuine `ID2D1Geometry`
`FillGeometry` translation. A typed `ID2D1SimplifiedGeometrySink` receives
Direct2D's cubic-and-line representation, excludes hollow figures, preserves
open/closed figure topology and alternate/nonzero fill rules, and writes at
most 1,048,576 finite segments into ProGPU's existing pointer-free path
resource. The active Direct2D draw transform remains the retained path
transform, while `ID2D1Geometry::GetBounds` supplies conservative local and
target command bounds. Geometry and every COM callback resource are released
before the stream call returns.

This is retained scene compilation rather than a CPU raster fallback.
Direct2D performs its device-independent geometry simplification once, and
ProGPU's shared GPU path rasterizer executes the retained result on D3D12,
Metal, Vulkan, and WebGPU. Per-primitive antialiasing uses the eight-sample
path quality lane. Aliased path edges, a non-null `FillGeometry` opacity
brush, invalid/empty-area data, and segment-cap overflow either become an
exact no-op where Direct2D has no fill area or fail closed with typed state,
resource, value, or capacity diagnostics. `DrawGeometry` strokes remain an
explicit follow-up because stroke styles, caps, joins, and dashes must be
translated without weakening semantics.

The live positive oracle builds a winding path containing a closed line/cubic
figure plus a hollow figure, records it under a non-identity transform, and
decodes one path resource with the exact two source segments and synthesized
closing line. The negative oracle proves that aliased path fill returns
unsupported state and no partial scene. Managed AOT contracts pass 5/5 and
the package builds with zero warnings. A 96 KiB incremental Windows payload
has SHA-256
`4BD4A70EE6575824BF33F37118434A185405F4BE3B484ADE2AE4B53374820F54`.
Windows 11 ARM64 Parallels with MSVC 19.44/SDK 10.0.26100.0 compiles provider
and test under `/W4 /WX`; CTest passes 1/1 in 3.00 seconds (3.51 seconds
total). No export was added, so the allowlist remains exactly 123; provider
SHA-256 is
`12467CF6BE48235928B396A76AD5AE0AAD15CAA3E1949AB8A4E9BA4323EB744A`.
The same implementation replaces anonymous-union aggregate initialization
with explicit `D2D1_MATRIX_3X2_F` field assignments after ClangCL exposed the
ABI v35 portability warning. Clean-checkout Build run `33345291817`, Windows
x64 job `99348168246`, compiled the provider and focused test under ClangCL
`/W4 /WX` and passed all 12 native CTests; the Direct2D test passed in 0.17
seconds. The job became red only later when a managed renderer sample lost the
Microsoft Basic Render Driver while mapping an unrelated WebGPU readback
buffer. Dedicated clean MSVC compatibility job `99348168261` also passed.

ABI v37 at implementation `163fa686` adds genuine `ID2D1CommandSink::DrawGeometry`
translation. The command-list sink validates the typed geometry, brush, finite
nonnegative stroke width, antialias mode, and then asks `ID2D1Geometry::Widen`
for Direct2D's exact filled stroke outline. The active draw transform and the
original `ID2D1StrokeStyle` are supplied to `Widen`, so caps, line joins, miter
limits, dash pattern/offset, and `ID2D1StrokeStyle1` normal, fixed, or hairline
transform behavior are resolved by the platform implementation rather than
reimplemented approximately. `GetWidenedBounds` supplies the target-space
command bounds.

The widened contour is retained as the same bounded pointer-free ProGPU path
resource used by filled geometry, with an identity path transform because
Direct2D has already applied the draw transform during widening. Brush mapping
continues to use the active draw transform, preserving target-relative solid
and gradient semantics. Playback therefore stays in ProGPU's shared GPU path
rasterizer on D3D12, Metal, Vulkan, and WebGPU. No COM object survives stream
translation and no CPU pixel raster, readback, or repacking path is added.
Aliased path edges, invalid widths/resources, unsupported widening results,
and the existing segment cap fail closed with typed diagnostics.

The native oracle records one transformed winding line/cubic geometry twice:
first filled, then stroked with round/triangle/square caps, bevel join, custom
dashes, and fixed stroke-transform mode. A second genuine Direct2D path
geometry receives the same `Widen` call as the independent topology oracle.
The test requires the translated stroke segment count to match that reference,
decodes the retained stroke with identity transform, and retains the original
three-segment fill path with its nonidentity transform. Managed AOT contracts
pass 5/5 and `ProGPU.Direct2D` builds with zero warnings. The final 95,520-byte
incremental Windows payload has SHA-256
`304477EB0796599D9015E7652DF15AEA53A61A79B69B93CFBD52101F7CA41974`.
Windows 11 ARM64 Parallels with MSVC 19.44/SDK 10.0.26100.0 compiles provider
and test under `/W4 /WX`; the focused CTest passes 1/1 in 40.29 seconds (78.46
seconds total under concurrent guest load). No export is added; ABI v37 only
extends the version and scene-result flag contract.

ABI v38 at implementation `a308e7df` adds the first exact
`ID2D1CommandSink::PushLayer`/`PopLayer` translation. The admitted subset is a
full-target `D2D1::InfiniteRect()` layer with finite uniform opacity, no
geometric mask, no opacity brush, and `D2D1_LAYER_OPTIONS1_NONE`. This maps to
ProGPU's existing isolated semantic layer so opacity is applied once when the
group is composited, rather than incorrectly multiplying every overlapping
draw. The `ID2D1Layer` scratch resource does not enter the scene; ProGPU owns
the pooled cross-backend layer allocation.

The sink now maintains one bounded typed scope stack shared by axis-aligned
clips and layers. It admits Direct2D's properly nested clip/layer pairs and
rejects overlapping or unbalanced pops with `D2DERR_WRONG_STATE`. Depth is
bounded by the native scene maximum of 64. Finite content bounds, geometric
masks, opacity brushes, `INITIALIZE_FROM_BACKGROUND`, and `IGNORE_ALPHA` remain
fail-closed until their exact bounds/mask/backdrop contracts are translated.
This deliberately follows the documented Direct2D layer boundary: uniform
opacity is applied during target composition, while masks and initialization
options have distinct semantics that cannot be treated as ordinary opacity.

The Windows oracle records two overlapping rectangles inside 37.5% opacity,
with the valid ordering `PushAxisAlignedClip`, `PushLayer`, `PopLayer`,
`PopAxisAlignedClip`. It decodes the pointer-free layer payload, exact source-
over blend, absent mask/effect references, and balanced command order. A
second command list proves `INITIALIZE_FROM_BACKGROUND` returns typed
unsupported state without a partial scene. Managed AOT contracts pass 5/5 and
the package builds with zero warnings. Windows 11 ARM64 Parallels with MSVC
19.44/SDK 10.0.26100.0 recompiles provider and test under `/W4 /WX`; the
fresh test executable exits zero. The final 97,082-byte payload SHA-256 is
`84A118A67091ED4DA4854B1B00A4AEB26F760073D22A729DFCE1B8460859C270`.
The 164,864-byte provider SHA-256 is
`305C1D7D3BC72F0CFC016778721CC36D90FDC91ABE1F9FCDE5DA2A8C5CFEF121`;
all 123 exports exactly match the checked-in allowlist.

ABI v39 at implementation `35a8fadc` extends the same grouped-opacity path to
finite `contentBounds` when the active draw transform is axis preserving. The
sink transforms the finite rectangle into exact target-space ProGPU layer
bounds at `PushLayer` time, so a later transform change cannot move the
already-pushed layer. Scale, translation, reflection, and their combinations
are admitted; rotation and shear remain typed unsupported state because an
axis-aligned retained bound would otherwise broaden the transformed region.
Full-target layers continue to accept any finite draw transform because they
have no bound to approximate.

The evolved Windows oracle pushes an outer clip under identity, switches to a
`[2,0,0,0.5,7,9]` transform, and requires Direct2D content bounds
`[1,2,21,22]` to decode as target bounds `[9,10,40,10]`. The grouped 37.5%
opacity, balanced mixed scopes, and background-initialization rejection remain
gated. Managed contracts pass 5/5 and AOT build is warning-free. Windows 11
ARM64 Parallels with MSVC 19.44/SDK 10.0.26100.0 rebuilds provider/test from
deleted objects under `/W4 /WX`; the fresh executable exits zero. The
90,402-byte payload SHA-256 is
`EDCD1850DABE2055AC05B6ACAC5583ADA8899C5A7806FC8A177551FF7D03B282`.
Provider SHA-256 is
`C42A075E13706B42F7AA617CA437A194B20076BB538F5C2E91520A4F28BFE81E`;
all 123 exports exactly match the allowlist.

ABI v40 at implementation `21be13a9` adds per-primitive geometric masks to
the retained Direct2D layer path. This follows the documented
[`D2D1_LAYER_PARAMETERS1`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ns-d2d1_1-d2d1_layer_parameters1)
ordering: the mask transform is relative to the active world transform, so
the sink retains `maskTransform * drawTransform` over geometry captured by
`ID2D1Geometry::Simplify(CUBICS_AND_LINES)`. Filled figures, fill rule, lines,
and cubics become one pointer-free ProGPU vector layer-mask resource. The
Direct2D geometry is not retained after command-list translation.

The mask executes through ProGPU's existing eight-sample GPU vector-mask
rasterizer and isolated-layer compositor on D3D12, Metal, Vulkan, and WebGPU.
There is no CPU pixel rasterization, readback, repack, or per-segment GPU
submission. Direct2D `GetBounds` supplies target mask bounds; finite layer
content bounds are intersected with them, while a full-target layer becomes
mask-bounded. Empty filled geometry becomes an exact empty layer. Non-finite
composition and unsupported geometry fail closed without emitting a partial
scene.

This checkpoint intentionally admits `D2D1_ANTIALIAS_MODE_PER_PRIMITIVE` only.
Aliased masks remain typed unsupported state until the semantic layer-mask ABI
has an exact hard-edge coverage mode. Opacity brushes,
`INITIALIZE_FROM_BACKGROUND`, and `IGNORE_ALPHA` remain separate unsupported
contracts. This preserves the distinction in Microsoft's
[layer overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview)
between geometric coverage, uniform group opacity, and backdrop behavior.

The Windows oracle uses a genuine filled Direct2D line/cubic geometry, a
nonidentity mask transform, and a nonidentity active draw transform. It asks
the same `ID2D1Geometry` for independently transformed bounds, then decodes
the ProGPU layer, intersected bounds, vector-mask payload, three retained path
segments, fill rule, eight-sample mode, and composed transform. A second
command list proves aliased geometric masks emit typed unsupported state and
zero scene bytes. Managed AOT contracts pass 5/5 and the package builds with
zero warnings. Windows 11 ARM64 Parallels rebuilds provider and test from
deleted objects under MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`; the fresh native
test exits zero. The 170,496-byte provider SHA-256 is
`21CB1B6F5DD483A6E6F1F3546D76C1EC158A22F042120AA8A503247CF58B4789`,
and all 123 exports match the allowlist.

`eng/build-progpu-native-windows.ps1` builds and runs
the native test on runnable Windows x64/ARM64 agents, stages
`progpu_native_direct2d.dll` in both Windows runtime packages, and rejects any
export drift against `eng/progpu-native-direct2d-exports.txt`.
`Direct2DInteropContractTests` separately verifies the static AOT ABI, absence
of reflection/dynamic native loading and CPU copies, lock-order boundary, and
typed lease contract on every portable managed test host.

ABI v5 at exact implementation commit `f751cd0b` is qualified in the Windows
11 ARM64 Parallels VM with MSVC 19.44 and Windows SDK 10.0.26100.0. The
isolated provider and regression compile and link with `/W4 /WX`; the
executable exits zero and `dumpbin` reports exactly the 14 allowed exports,
including both typed Win2D wrappers. SHA-256 is
`d9224ee806635ba3086d299912bb7bd2d9cf52a7ef56451ae54656058e7175d8`
for `progpu_native_direct2d.dll` and
`0e8fc690ba5bd4a7a40d461d1691f8efd32dbef7338ae90a1635ccc5b0f2e02d`
for `progpu_native_direct2d_tests.exe`. The VM has no Canvas/Win2D AppX
registration, so this run qualifies the explicit runtime-unavailable branch
for both CanvasDevice and CanvasRenderTarget wrappers but does not by itself
qualify successful Win2D activation. A booted desktop or an unresponsive
Guest Tools login is not accepted as qualification evidence; these hashes came
from the executed regression in the guest.

The separate packaged success oracle is qualified from exact source commit
`d201494a` on the same Windows 11 ARM64 Parallels VM. The gate builds and
installs a full-trust MSIX containing official `Microsoft.Graphics.Win2D`
1.4.0 and `Microsoft.WindowsAppSDK.WinUI` 1.8.260204000, explicitly registers
the package's `Microsoft.Graphics.Canvas.CanvasDevice` activation server, and
projects ProGPU's returned ABI pointer through `CanvasRenderTarget.FromAbi`.
A genuine `CanvasDrawingSession` clears the 64x64 target and fills a 48x48
rectangle. Validation-only `GetPixelColors()` reports a transparent corner and
exact center ARGB `(255,32,96,192)`; shared-surface content version advances
from 0 to 1 before Dawn resumes ownership. Evidence names the real
`Microsoft.Graphics.Canvas.CanvasDevice`, `CanvasRenderTarget`, and
`CanvasDrawingSession` runtime types, reports adapter `Dawn D3D12`, WinRT
initialization HRESULT `S_FALSE`, and native HRESULT `S_OK`.

ABI v6 at exact implementation commit `1be881ca` was then rebuilt in the same
guest with MSVC 19.44 and Windows SDK 10.0.26100.0 under `/W4 /WX`. Its native
regression exits zero and `dumpbin` matches all 15 allowed exports. SHA-256 is
`160037e11339ec6ad38a3cc2bc121ca6da5ba73ad3fd25c29d9eb8d030a132d9`
for `progpu_native_direct2d.dll` and
`46884523bd6ba4700c8113ac9df2f09689b134d429327a07d9fcd083511159ec`
for `progpu_native_direct2d_tests.exe`. The packaged official-Win2D oracle also
passes with `NativeDeviceIdentityMatches=true` and
`NativeBitmapIdentityMatches=true`, proving the exact
`ID2D1Device1 -> CanvasDevice -> ID2D1Device1` and
`ID2D1Bitmap1 -> CanvasRenderTarget -> ID2D1Bitmap1` round trips. The existing
transparent-corner, center ARGB `(255,32,96,192)`, content-version `0 -> 1`,
and `Dawn D3D12` evidence remains unchanged.

ABI v7 at exact implementation commit `4f5e614f` was rebuilt in the same
Windows 11 ARM64 Parallels guest with MSVC 19.44, Windows SDK 10.0.26100.0,
and `/W4 /WX`. The native regression exits zero and `dumpbin` matches all 18
allowed exports. SHA-256 is
`6c35ac88938fbdc483b6a932d1180a1fd041ead3097c4ef51bce2b31ad5e301c`
for `progpu_native_direct2d.dll` and
`edb201be9ab6f1783d679bcafd8872c3f5c1495bcc9b8738c3235b5177f44d42`
for `progpu_native_direct2d_tests.exe`. The packaged official Win2D 1.4.0
oracle projects a real
`Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush`, reports
`NativeSolidColorBrushIdentityMatches=true`, reads exact brush ARGB
`(255,224,48,96)`, and uses that brush overload to produce the same exact
center pixel while the corner stays transparent. Existing exact device/bitmap
identity, content version `0 -> 1`, and `Dawn D3D12` evidence also pass.

ABI v8 at exact implementation commit `8e62b5e5` was rebuilt in the same
Windows 11 ARM64 Parallels guest with MSVC 19.44, Windows SDK
10.0.26100.0, and `/W4 /WX`. The native regression exits zero and `dumpbin`
matches all 21 allowed exports. SHA-256 is
`c291eac6efc959acd39ba1bdea03d80e8e9025b001c145c13b4c174f003ffc96`
for `progpu_native_direct2d.dll` and
`712ba33d7cd121bb8a7d3c68585c3895c00ad5575e4cdc64971783857d2020a3`
for `progpu_native_direct2d_tests.exe`. The signed official Win2D 1.4.0
oracle reports real `CanvasLinearGradientBrush` and
`CanvasRadialGradientBrush` types, exact reverse native identities, two-stop
metadata and geometry, and exact solid/linear/radial sample ARGB values
`(255,224,48,96)`, `(255,32,160,224)`, and `(255,64,192,96)`. The corner
remains transparent; device, target, and solid identities, content version
`0 -> 1`, and `Dawn D3D12` also pass.

ABI v9 at exact ProGPU commit `0b96328e` was rebuilt in the same Windows 11
ARM64 Parallels guest with MSVC 19.44, Windows SDK 10.0.26100.0, and
`/W4 /WX`. The native regression exits zero and `dumpbin` matches all 27
allowed exports. SHA-256 is
`83a67ee9007902ca477bada185ea99d298f879b8798b91aad18d4bf996eda29e`
for `progpu_native_direct2d.dll` and
`eb9cdf5346e8f72ae49b2486051298a7bbce44bd83bde36b554dee50d7b8f0fa`
for `progpu_native_direct2d_tests.exe`; the immutable archive hash is
`3a3726ee61792a98558a02e2cb6a050340fbadf757b0908c5f1b318514f55f5b`.
The signed Microsoft Win2D 1.4.0 oracle built from exact app commit `3a058643`
reports the real `Microsoft.Graphics.Canvas.Geometry.CanvasGeometry` type,
`NativeGeometryIdentityMatches=true`, and an exact boolean-exclude fill sample
ARGB `(255,240,208,32)` while the excluded hole preserves the underlying solid
ARGB `(255,224,48,96)`. Existing solid/linear/radial samples, transparent
corner, all native identities, content version `0 -> 1`, and `Dawn D3D12`
remain green. The gate now persists JSON before package cleanup and records a
best-effort last-stage marker so native termination cannot erase evidence.

ABI v10 was qualified at ProGPU implementation commit `a0febfd3` plus the
typed-IID audit fix `39c947d4` in the same Windows 11 ARM64 Parallels guest.
MSVC 19.44 builds the provider and focused native regression with `/W4 /WX`;
the executable exits zero after validating all stroke metadata, custom-dash
copying, malformed-span rejection, Win2D canonical identity, and an actual
styled `DrawGeometry`. SHA-256 is
`2CBA50FD8C3B2963B46EC5A918DCC8A03CBDA69FA5B47A28D17D9CD528441158`
for `progpu_native_direct2d.dll` and
`287EE1183BA296AE62912EC8692A21F76D7A2044E412387EF1C3E9BEECB9FCE9`
for `progpu_native_direct2d_tests.exe`. The exact `39c947d4` source archive is
`B7C37C6F23D4A1CAD46B4AB6CDD41BE2921AE9E7CBFBD2CBE41BF30CD8BF1976`.
The signed Microsoft Win2D 1.4.0 oracle reports the real
`Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle`,
`NativeStrokeStyleIdentityMatches=true`, the expected four-value custom dash
pattern, and successful styled geometry drawing. All previous device, bitmap,
solid/linear/radial brush, and geometry identities remain true; the transparent
corner and exact solid/linear/radial/geometry pixel probes remain unchanged,
the adapter is `Dawn D3D12`, and content version advances from `0` to `1`.

ABI v11 was rebuilt at ProGPU implementation commit `3df8bba3` in the same
Windows 11 ARM64 Parallels guest. MSVC 19.44 compiles the provider and focused
native regression with `/W4 /WX`; the executable exits zero after validating
bitmap metadata, truncated-row rejection, bitmap-brush metadata and source
identity, generic Win2D wrapper identity, and a real bitmap-brush fill.
SHA-256 is
`B8D1FA66E0FC311804702D1E3D097F6F7DD7A2988C110DDF83C9312523400C9B`
for `progpu_native_direct2d.dll` and
`1A7D600426BE75FEA6C4EED1688104374688FEF07584D54A14FA462B1E710CEF`
for `progpu_native_direct2d_tests.exe`. After the stale-process and durable
evidence hardening at commits `16734c64` and `88bf7765`, the matching ARM64
Microsoft Win2D 1.4.0 integration app compiles, produces and signs an MSIX,
installs, launches, and passes. It reports real `CanvasBitmap` and
`CanvasImageBrush` runtime types, exact
`ID2D1Bitmap1 <-> CanvasBitmap` and
`ID2D1BitmapBrush1 <-> CanvasImageBrush` identities, and image-brush sample
ARGB `(255,144,64,240)`. All ABI v10 and earlier identities and pixel probes
remain green, the adapter is `Dawn D3D12`, and content version advances from
`0` to `1`. The persisted JSON evidence SHA-256 is
`925BFA8B5D1B48F9A06BC433D0444339EBB82915EC5968320EA86FC1DA38644C`.
The gate now removes stale same-name processes before launch, writes progress
to package LocalState plus its fallback directory, and reports the last durable
stage when the current process exits or times out without evidence.

ABI v12 at exact implementation commit `b0cc1b63` was rebuilt in the same
Windows 11 ARM64 Parallels guest with MSVC 19.44 and Windows SDK
10.0.26100.0. Two consecutive `/Brepro` builds produce identical binaries;
the warning-clean focused provider and native regression exit zero after
validating image source identity, source rectangle, tiling,
sampling, opacity, transform, empty-rectangle rejection, exact Win2D
forward/reverse identity, and a real `ID2D1ImageBrush` fill. SHA-256 is
`A4F8116C63BB93C47EE395DF2E5E81BC936AA58D1A48A03004673CB7835FB176`
for `progpu_native_direct2d.dll` and
`420A72C0BB51619B3DEAE02B318F67E4EEAD19236B270A396F758C8598FF7077`
for `progpu_native_direct2d_tests.exe`. The signed ARM64 Microsoft Win2D 1.4.0
oracle passes on `Dawn D3D12`: both native brush representations project as
real `Microsoft.Graphics.Canvas.Brushes.CanvasImageBrush` objects and preserve
canonical COM identity. Its general image brush reports source rectangle
`[1,0,1,2]` and selects the expected second-column ARGB pixel
`(255,48,224,176)` while the existing bitmap-brush probe remains
`(255,144,64,240)`. All earlier identities and pixels remain green, the shared
surface is `68x64`, and content version advances from `0` to `1`. Persisted
JSON evidence SHA-256 is
`17DFB074969889F4144973366B87A996FA9BF30A42BFEE381194FD234E8A40C6`.
Post-run cleanup leaves zero integration processes and zero installed test
packages.

ABI v13 at exact implementation commit `4ec86149` was rebuilt in the same
Windows 11 ARM64 Parallels guest with MSVC 19.44 and Windows SDK
10.0.26100.0. Two consecutive `/Brepro` builds produce identical binaries;
the warning-clean native regression exits zero after validating exact
`ID2D1CommandList` IID and target identity, nested-recording and mismatched-end
rejection, real clear/rectangle recording, target restoration, successful
close, unchanged shared-surface content version, command-list-backed
`ID2D1ImageBrush` creation, exact native/Win2D identities, and real `DrawImage`
plus brush drawing. `dumpbin` matches all 34 allowed exports. SHA-256 is
`1E6AE0F2CDB816F797FAAE55079DA1AA9CF21E1EB704A178C24D7AB12C45CF73`
for `progpu_native_direct2d.dll` and
`5B2AEEEF306AEB21BA1CC1572EBA361999412780CB7F06FA31367AFCFC5EA560`
for `progpu_native_direct2d_tests.exe` in both builds. The signed ARM64
Microsoft Win2D 1.4.0 oracle passes on `Dawn D3D12`, reports the real
`Microsoft.Graphics.Canvas.CanvasCommandList` type, preserves exact native
command-list and command-list image-brush COM identities, and renders the
distinct command-list sample ARGB `(255,248,112,40)`. All earlier identities
and pixels remain green, the shared surface is `72x64`, and only the final
shared-target producer advances content version from `0` to `1`; offscreen
command-list recording does not. Persisted JSON evidence SHA-256 is
`C678B5384632846505C7B864FAACE49476C9EEDD20555FB9210535D697EF5DE5`.
Post-run cleanup leaves zero integration processes and zero installed test
packages across all users.

ABI v14 at exact implementation commit `6d01b206` was rebuilt in the same
Windows 11 ARM64 Parallels guest with MSVC 19.44 and Windows SDK
10.0.26100.0. Two consecutive `/Brepro` builds produce identical binaries;
the warning-clean native regression exits zero after creating genuine
Gaussian-blur and shadow effects, validating exact effect/output interface
identities, setting fixed-layout properties, rejecting a malformed property
size and invalid input index, chaining effect-to-effect input, drawing the
effect output directly, and using that output through a genuine
`ID2D1ImageBrush`. `dumpbin` matches all 39 allowed exports. SHA-256 is
`F847584606485E3D2F77A8A8DDA6C62E5D0DE6050548B08D8D78FBE51BE43D8F`
for `progpu_native_direct2d.dll` and
`484E6CA6DF930F405D18FC09FBD9DAD346F3B248B22DDC7404A6594EDBB6EF2C`
for `progpu_native_direct2d_tests.exe` in both builds. The signed ARM64
Microsoft Win2D 1.4.0 oracle passes on `Dawn D3D12`, projects the provider's
effect-output image brush as a real
`Microsoft.Graphics.Canvas.Brushes.CanvasImageBrush`, preserves exact native
COM identity in both directions, and renders exact ARGB
`(255,112,40,248)`. All ABI v13 and earlier identities and pixels remain
green, the shared surface is `76x64`, and content version advances from `0`
to `1`. Persisted JSON evidence SHA-256 is
`686675F945CEBAF8E1C4661CAF0D42332C3A0E9EC2F5E33267A6B22B1B7804A6`.
Post-run cleanup leaves zero integration processes and zero installed test
packages.

ABI v15 at exact implementation commit `1869bb3c` was rebuilt in the same
Windows 11 ARM64 Parallels guest with MSVC 19.44 and Windows SDK
10.0.26100.0. Two consecutive `/Brepro` builds produce identical binaries;
the warning-clean native regression exits zero after validating explicit
layer size, invalid-size rejection, genuine drawing-state identity/defaults,
transform save/restore, geometry-mask layer drawing, balanced pop, unmatched
pop rejection, and automatic unwind plus command-list close for an unbalanced
layer scope. `dumpbin` matches all 45 allowed exports. SHA-256 is
`897FF518BE445ABA891C237CFDACCAFE9BD221A57F88959F706EA68567C1159C`
for `progpu_native_direct2d.dll` and
`304A03841E62FF6B4ECC1472467D07B1195AB2D4B6806094D41E046E81D986FF`
for `progpu_native_direct2d_tests.exe` in both builds. The signed ARM64
Microsoft Win2D 1.4.0 oracle passes on `Dawn D3D12` with
`TypedLayerStateScopePassed=true`; it creates the layer/state resources,
saves and restores drawing state, and balances a masked/opacity-brush layer
inside a provider-owned command-list transaction without advancing shared
surface content. All ABI v14 and earlier COM identities and exact pixel probes
remain green, the shared surface remains `76x64`, and only the final shared
producer advances content version from `0` to `1`. Persisted JSON evidence
SHA-256 is
`E05456E8D8D52084FE8F85743735BE57C6AEC9D2772216C4CA53122C40E506DC`.
Post-run cleanup leaves zero integration processes and zero installed test
packages.

The native half of ABI v16 at exact implementation commit `6a87f320` was
rebuilt in the same Windows 11 ARM64 Parallels guest with MSVC 19.44 and
Windows SDK 10.0.26100.0 under `/W4 /WX`. Two consecutive `/Brepro` builds
produce identical binaries. The focused native regression exits zero after
querying the shared `IDWriteFactory3`, creating a genuine
`IDWriteTextFormat1`, validating its family/locale/size/alignment/wrapping
state, rejecting a malformed descriptor and pre-draw text call, drawing real
UTF-16 text during an active shared-surface transaction, and rejecting unknown
draw options. `dumpbin` matches all 47 allowed exports, including the two new
typed text entry points. SHA-256 is
`6BC503DBE9BB5506B709CA6D97D8B78F82F302BF33BCE4352B104722DA05FCDC`
for `progpu_native_direct2d.dll` and
`8C634D6EC4963786D87D5E87BEE5FBD83F6B843A8BCE535E0E9149CB806FCDC5`
for `progpu_native_direct2d_tests.exe` in both builds. This evidence qualifies
the native DirectWrite/Direct2D path only. Successful projection through the
official Win2D `CanvasTextFormat` remains a separate signed-package gate and
must not be inferred from the native COM identity test.

Run this qualification with
`eng/progpu-run-direct2d-win2d-integration.ps1`, or opt it into the complete
Windows native lane with `PROGPU_RUN_REAL_WIN2D_INTEGRATION=1`. Package trust is
deployment state: provide `PROGPU_WIN2D_SIGNING_CERTIFICATE_THUMBPRINT` for a
pre-provisioned `CN=ProGPU` certificate with its private key in
`CurrentUser/My` and its public certificate trusted in `CurrentUser/Root` or
`LocalMachine/Root`. The gate verifies those stores, signs by thumbprint, and
fails closed when they are absent. It never creates, exports, trusts, or deletes
a certificate, and no certificate value is stored in source or reports.

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
3. **Geometry analysis and realization implemented:** add image and
   layer-opacity brushes,
   bitmap file/buffer APIs, text-format/layout, and existing-effect
   adapters;
   promote each pinned ExampleGallery group only after differential parity.
4. **Foundation and typed loss domain implemented:** create the genuine Windows
   Direct2D COM resource domain and synchronized shared-DXGI target, bind its
   producer lifecycle to Dawn import and Win2D factory/resource-wrapper
   interop, and invalidate generation-stamped resources on terminal loss. The
   remaining gate is destructive physical-loss injection followed by explicit
   new-device reconstruction in the Windows VM.
5. Add WinUI, LibreWPF, and Avalonia controls as host adapters over the same
   Canvas/scene core.
6. Expand effects, sprite batching, SVG/ink/virtual bitmap, and custom effects
   according to measured application demand. Native custom COM effects remain
   Windows-only; portable custom effects use typed WGSL/HLSL-translated ProGPU
   shader contracts.

The `Microsoft.Direct3D.D3D12` NuGet package remains useful for the native
Windows D3D12/Agility SDK oracle lane. It does not provide Direct2D or make
Win2D portable, so it is not a replacement for either compatibility tier.
