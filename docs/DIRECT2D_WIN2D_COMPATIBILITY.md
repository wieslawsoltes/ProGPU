# Direct2D and Win2D compatibility

## Decision

`ProGPU.DirectX` remains a typed Direct3D-style device/resource/pipeline facade
over WebGPU, with D3D12 selected by the Windows backend. The C++ backend now
also ships an isolated Windows `progpu_native_direct2d` provider. It creates
genuine system Direct2D COM objects and a synchronized DXGI target that ProGPU
can import. ABI v49 extends the deliberately ProGPU-owned COM endpoint from an
`ID2D1CommandSink1` recorder to an explicit `ID2D1Factory1`, immutable
`ID2D1RectangleGeometry`, mutable `ID2D1SolidColorBrush`, and one-shot
`ID2D1PathGeometry1`/`ID2D1GeometrySink` path construction pair, plus immutable
`ID2D1StrokeStyle1` metadata resources and exact normal, fixed-device, and
hairline line/cubic path-stroke recording, including per-segment stroke and
join flags. ABI v50 completes those curved transform policies; ABI v51-v54
add reusable ellipse, rounded-rectangle, transformed, and grouped geometry.
The separately installed portable C++ COM target now exposes rectangle,
transformed, path/sink, ellipse, rounded-rectangle, and grouped geometry on
every desktop target, plus immutable stroke-style resources; broader factory
and device-context families remain staged. Portable drawing-state blocks also
preserve device-independent save/restore metadata and DirectWrite parameter
ownership. Its first device-dependent draw family now includes solid, linear,
radial, and bitmap brushes plus upload-backed RGBA/BGRA bitmaps.
Supported resource callbacks lower directly into the portable semantic scene.
This is an explicit
compatibility facade, not an
impersonation of `d2d1.dll`; unsupported methods fail closed and the full
device-context/resource vtable family remains incremental work. The system
DirectWrite runtime, WIC codecs, and the native Win2D WinRT component remain
Windows native graphics dependencies. Portable callers may instead supply the
documented ABI-compatible WIC source/lock and already-shaped DirectWrite font-face
outline interfaces. The ProGPU native-library resolver must not
impersonate `d2d1.dll`, `dwrite.dll`,
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

## Migration model for COM-heavy applications

A graphics application can keep COM-shaped ownership and most DirectX/
Direct2D call structure, but it must be rebuilt against the ProGPU compatibility
headers or managed projections on non-Windows targets. ProGPU-owned objects
preserve `IUnknown` identity, reference counting, GUID-based `QueryInterface`,
resource parentage, command recording, and fail-closed HRESULT behavior for the
interfaces they advertise. Supported draw and resource calls lower into one
typed, pointer-free semantic scene and execute through D3D12 on Windows, Metal
on macOS, Vulkan on Linux, or browser WebGPU. COM calls do not imply one GPU
submission each; recording and retained compilation preserve batching.

This is not a general Windows COM runtime. An unchanged PE/WinRT binary cannot
be loaded as a native macOS or Linux library, and arbitrary `CoCreateInstance`
activation, registry classes, HWND/DXGI handles, D3D11 keyed mutexes, Media
Foundation, DirectShow, WIC codecs, shell automation, ActiveX, and unrelated
third-party COM servers remain Windows-specific unless a separate typed
portable provider is implemented. Applications should isolate those boundaries
behind platform services and pass portable descriptors, leases, scene handles,
or byte streams into ProGPU. Unsupported graphics interfaces return
`E_NOINTERFACE` or the documented failing HRESULT instead of fabricating a
partial object.

The practical deployment choices are therefore:

1. Run an unchanged Windows binary on Windows (including the qualification VM)
   and use genuine system DirectX/Direct2D/Win2D resources with zero-copy ProGPU
   interop.
2. Rebuild source for every target and use ProGPU-owned DirectX/Direct2D-shaped
   interfaces plus the portable Win2D-style API over the shared renderer.
3. Keep a deliberately Windows-only plugin/process for an unavailable COM
   subsystem and exchange neutral data with the portable application. Wine or
   another whole-Windows compatibility runtime is a separate deployment model,
   not part of the ProGPU ABI.

Validation compares the same scenario in three lanes: native Windows as the
behavior oracle, ProGPU on Windows to separate translation errors from backend
differences, and ProGPU on macOS/Linux for command-stream, resource-lifetime,
diagnostic, and bounded image-differential parity.

### Application compatibility tiers

“Uses COM” and “requires Windows” are not equivalent. COM supplies interface
identity, lifetime, and activation rules; the concrete interface and the data
crossing it determine portability. ProGPU classifies an application at each
interop boundary instead of assigning one portability label to the whole
process:

| Application call shape | macOS/Linux behavior | Required application change |
| --- | --- | --- |
| `IUnknown`, `QueryInterface`, `AddRef`/`Release`, and supported ProGPU-owned `ID2D1*` resources | The installed portable C++ target provides the base `ID2D1Factory` vtable plus ABI-compatible resource, geometry/path/sink, stroke-style, drawing-state, brush/bitmap, and render-target interfaces with implemented behavior. Other resource families still fail closed or use typed scene/Canvas APIs | Rebuild against `progpu_native_direct2d_compat.hpp` for the implemented C++ subset, or use `ProGPU.DirectX`, `ProGPU.Win2D`, or the scene API. Global Windows SDK names and broader factory/device-context families remain incremental |
| Supported Direct3D-style buffers, textures, pipelines, descriptors, and command recording | Lowers to WebGPU and selects Metal on macOS or Vulkan on Linux | Rebuild against `ProGPU.DirectX`; replace raw native handles with typed ProGPU resource leases |
| Win2D drawing expressed through the portable `ProGPU.Win2D` Canvas API | Runs in-process and lowers to the shared scene/vector/text/effect implementation | Rebuild source against the portable Canvas projection |
| System `ID2D1*`, DXGI shared handles, D3D11 keyed mutexes, HWND/HDC targets, or native Win2D resource wrapping | No native in-process equivalent; the call fails closed at the typed platform boundary | Select a ProGPU-owned resource on portable targets, or keep a Windows implementation behind a platform service |
| Arbitrary registered COM servers, ActiveX, shell automation, Media Foundation/DirectShow components, or an unchanged PE/WinRT binary | Not provided by ProGPU | Use a Windows VM/Wine or a Windows helper process/plugin and exchange neutral data |

The target portable Direct2D call follows one batched translation path (the
Windows ABI v54 endpoint already implements its first half):

```text
application ID2D1*/Canvas call
        -> ProGPU-owned COM identity and validated resource domain
        -> retained pointer-free geometry/image/text/effect command
        -> shared scene compilation and resource lifetime tracking
        -> D3D12 (Windows) / Metal (macOS) / Vulkan (Linux) / WebGPU
```

The COM call itself does not cross a process or become a GPU submission. COM
objects remain on the application side of the boundary, while the retained
scene carries stable IDs and value descriptors. This prevents Windows pointer,
registry, apartment, HWND, and DXGI-handle assumptions from leaking into the
portable renderer and allows command batching, resource caching, and device
loss recovery to remain shared.

Applications with a large mixed surface should introduce one adapter boundary,
not wrappers around every call. Keep portable drawing, compute, and resource
creation on ProGPU; isolate genuinely Windows-only capture, codec, shell, or
third-party COM components behind a service whose contract uses byte streams,
shared scene/resource descriptors, or explicit external-texture leases. On
Windows that service may return genuine system DirectX resources for zero-copy
interop. On macOS/Linux it selects a native portable provider or reports the
missing capability before recording work.

Checkpoint `c943222a` identified the concrete build boundary, and ProGPU
`4186a305` starts removing it without pretending that the provider already
exists. `progpu_native_direct2d.h` now exposes its fixed-layout
GUIDs, enums, descriptors, summaries, and function declarations on every
desktop native target and is installed with the other native headers. The
compile-time `PROGPU_NATIVE_DIRECT2D_HAS_WINDOWS_PROVIDER` capability is `1`
only where the genuine system Direct2D/DXGI provider is built and `0` on
macOS/Linux. A standalone warning-as-error CTest includes the header and checks
the cross-platform GUID, point, rectangle, matrix, color, triangle, command
summary, and scene-result layouts without linking Windows SDK libraries.

This is a declaration and packaging seam, not a false implementation claim:
calling the provider functions still requires the Windows shared library.
Windows-only surface, DXGI, WinRT, and native Win2D entry points remain in
their existing provider.

On Apple Silicon/macOS the no-provider native tree builds under
`-Wall -Wextra -Wpedantic -Werror` and passes all 11 CTests, including the new
portable Direct2D header executable. The focused managed Direct2D contract gate
passes 6/6 with zero build warnings and verifies that the capability split,
install rule, and native layout test cannot be removed accidentally.

ProGPU `6c48a2a9` adds the next extraction layer as installed header
`progpu_native_com.hpp`. On Windows its GUID, HRESULT, reference-count, and
`IUnknown` types alias the real SDK ABI. On other targets it provides the same
fixed-width GUID/result layout and three-slot `QueryInterface`/`AddRef`/
`Release` interface shape without importing a Windows runtime. Shared helpers
provide HRESULT success/failure classification, field-wise GUID comparison,
the canonical `IID_IUnknown`, atomic reference counting, and an allocation-free
RAII COM pointer with attach/detach/copy/move/query ownership.

This foundation does not emulate `CoCreateInstance`, apartments, the registry,
marshalling, or arbitrary COM servers. It exists specifically so ProGPU-owned
Direct2D resources can preserve COM identity and lifetime while their current
WRL/Windows declarations are extracted. The native regression validates exact
GUID/result widths, canonical identity, successful and failed
`QueryInterface`, copy/move ownership, balanced references, and final object
destruction. The Apple Silicon/macOS no-provider tree now passes 12/12 CTests;
the focused managed Direct2D source/packaging contract passes 7/7 with zero
build warnings.

The next extraction slice adds installed static target and header
`progpu_native_direct2d_core`/`progpu_native_direct2d_core.hpp`. Rectangle
validation, affine transformation, bounds, fill hit testing, tessellation,
area, perimeter, and point-at-length now execute without Windows headers,
allocation, reflection, or GPU readback. The Windows
`ID2D1RectangleGeometry` implementation delegates those operations to the same
core instead of maintaining a second algorithm. Direct2D's edge-coordinate
rectangle representation is preserved internally, including finite extreme
spans whose mathematical width cannot fit in one `float`.

A portable warning-as-error CTest covers rotated/nonuniform transforms,
inside/outside classification, both triangles, area, perimeter,
point-at-length, invalid values, pointer failures, and degenerate geometry. A
managed source-contract test prevents the Windows adapter from regaining its
old private rectangle transform implementation. This is the first shared
behavior core, not yet a portable literal `ID2D1RectangleGeometry` vtable:
macOS/Linux applications still use the typed ProGPU geometry/Canvas API until
the compatibility interface declarations and factory activation target are
extracted.

Qualification for this slice builds the no-provider Apple Silicon tree with
AppleClang warnings-as-errors and passes 11/11 native CTests. The focused
managed `Direct2DInteropContractTests` pass 8/8. The exact source archive also
builds and runs `progpu_native_direct2d_core_tests.exe` under Windows 11 ARM64,
MSVC 19.44, and `/W4 /WX`. Building the complete Windows Direct2D provider
continues to stop before adapter qualification at the separately tracked
Windows SDK `near` macro collision in
`progpu_native_mil_curve_dash.hpp`; this slice does not hide or work around
that existing failure.

The following compatibility slice adds installed header
`progpu_native_direct2d_compat.hpp` and portable factory activation through
`progpu::native::direct2d::compat::create_factory(...)`. The interfaces carry
the canonical `ID2D1Factory`, `ID2D1Resource`, `ID2D1Geometry`, and
`ID2D1RectangleGeometry` IIDs. Their inherited method order, calling
convention, pointer widths, enums, matrices, rectangles, triangles, HRESULTs,
and signed `BOOL` outputs match the Windows SDK ABI for the implemented subset.
The base factory retains every original vtable slot; unsupported WIC, HWND,
DXGI, and DC creation methods return `E_NOTIMPL` and clear their output instead
of shifting slots or returning fake resources.

Portable rectangle resources keep their factory alive, return canonical COM
identity through every advertised IID, and delegate bounds, fill hit testing,
simplification, tessellation, area, length, and point-at-length to the shared
allocation-free core. On Windows ARM64 the warning-as-error test casts the
portable object directly to the system SDK `ID2D1Factory*`, creates a system-
typed `ID2D1RectangleGeometry*`, and calls `ComputeArea`; no adapter thunk,
reflection, or copied scene is involved. The same test runs through the
portable declarations on macOS. The no-provider tree now passes 12/12 CTests
and the managed contract passes 9/9.

This proves binary call compatibility for the first device-independent
resource family, but it is not yet a drop-in global `d2d1.h`. Portable source
must include the ProGPU compatibility header and use its namespace until the
remaining Direct2D data declarations, resource families, device contexts, and
optional source-name projection are complete.

ProGPU `f1c4879d` expands this ABI-compatible family with
`ID2D1TransformedGeometry`. The shared core now owns finite row-vector affine
composition, and both the Windows provider adapter and portable resource call
that implementation. Portable transformed resources retain their source and
factory, reject cross-factory sources with `D2DERR_WRONG_FACTORY`, expose the
canonical transformed-geometry IID, compose their local transform before a
caller world transform, and delegate bounds, containment, simplify,
tessellate, outline, area, length, point-at-length, and widen behavior through
the source geometry without allocation.

The Windows ARM64 `/W4 /WX` gate calls `CreateTransformedGeometry` through the
real SDK `ID2D1Factory*` vtable, receives an SDK
`ID2D1TransformedGeometry*`, and verifies transformed bounds. It also runs the
portable core test for composition order, null outputs, non-finite matrices,
source identity, and wrong-factory rejection. The macOS no-provider suite
remains 12/12 and the managed Direct2D contract remains 9/9.

ProGPU `bd16f5d8` adds the canonical `ID2D1PathGeometry`,
`ID2D1GeometrySink`, and `ID2D1SimplifiedGeometrySink` interfaces to the same
installed portable COM family. `CreatePathGeometry` returns a one-shot path:
`Open` succeeds exactly once, the retained sink records fill mode, segment
flags, figures, lines, cubic and quadratic Beziers, and arcs, and `Close`
publishes an immutable stream. Premature queries, duplicate opens/closes,
unbalanced figures, invalid values, allocation failure, and abandoned open
sinks leave the path unavailable with the matching HRESULT instead of
publishing partial geometry.

Closed line/cubic/quadratic paths expose figure and Direct2D segment counts,
round-trip their original segment types through `Stream`, compute exact affine
bounds (including curve extrema), and simplify quadratics to equivalent
cubics in source space before applying the caller transform. Arc commands are
retained and streamed without loss, but arc bounds and simplification return
`E_NOTIMPL` before touching the caller sink until the shared native arc
converter is extracted. The remaining path analysis, tessellation, boolean,
outline, metric, and widen methods also fail closed and clear scalar/pointer
outputs where applicable.

The Apple Silicon warning-as-error tree passes all 12 CTests and the focused
managed Direct2D contract passes 9/9. Windows 11 ARM64 with MSVC 19.44 and
`/W4 /WX` builds and runs both focused executables through the real SDK
`ID2D1Factory*`, `ID2D1PathGeometry*`, and sink vtables. That Windows test also
constructs the same closed line/quadratic path with the system
`D2D1CreateFactory` implementation and requires identical segment/figure
counts and exact bounds. The gate caught and removed a legacy Windows SDK
`small` macro collision from the portable arc enum before qualification.

ProGPU `69ecef5e` closes that recorded arc-analysis gap by extracting the
Windows provider's endpoint-arc-to-cubic lowering into
`progpu_native_direct2d_core`. The Windows COM provider and portable path now
call the same finite, allocation-free zero-to-four-piece implementation.
Portable `GetBounds` includes exact extrema of the transformed cubic pieces,
and `Simplify(CUBICS_AND_LINES)` emits those pieces without readback,
repacking, or host-specific geometry. Coincident endpoints remain empty and
zero-radius arcs preserve Direct2D's line equivalence.

The expanded Apple Silicon suite remains 12/12 and the managed contract remains
9/9. Windows 11 ARM64 MSVC 19.44 `/W4 /WX` rebuilds and passes both focused
executables; the SDK-pointer test creates matching semicircular arcs in ProGPU
and the system `D2D1CreateFactory` implementation and requires identical
bounds. Flatten-to-lines and the broader fill/stroke/boolean/metric operations
remain separate path-analysis gates.

ProGPU `6efc8fe6` adds canonical portable `ID2D1EllipseGeometry` creation and
identity in the original base-factory slot. The immutable resource retains its
factory and generated path, returns its exact center/radii through
`GetEllipse`, uses the shared core for validation, four-cubic construction,
three zero-length endpoint markers observed in system simplification, and
inverse-transform fill hit testing, and delegates bounds and
`CUBICS_AND_LINES` simplification to that retained path. Unsupported stroke,
tessellation, boolean, outline, metric, and widen operations continue to fail
closed through the path contract.

The Windows system oracle deliberately uses a non-axis-preserving affine
matrix. It caught that system Direct2D reports the slightly conservative
bounds of its four-cubic retained representation rather than the tighter
analytic ellipse AABB. ProGPU now preserves that observable behavior; both its
portable and Windows provider sources build their ellipse path with the same
shared cubic core. `CUBICS_AND_LINES` also matches system Direct2D's exact
`B,L,B,L,B,L,B` transcript: each of the first three cubic endpoints is followed
by a zero-length line. These markers preserve pixels and metrics while keeping
direct ellipse, group, and nested-group simplification structurally identical.
The fill/stroke scene sinks recognize the zero-length continuation markers and
elide them after a real segment, preserving the exact public transcript without
adding GPU path work or disrupting curved-stroke tangent selection.
Apple Silicon passes 12/12 warning-as-error CTests, the
managed source contract passes 9/9, and Windows 11 ARM64 MSVC 19.44 `/W4 /WX`
builds and passes the focused core/compatibility executables through real SDK
`ID2D1EllipseGeometry*`. Bounds and transformed-center containment must match
the system `D2D1CreateFactory` ellipse.

ProGPU `a536e019` adds canonical portable
`ID2D1RoundedRectangleGeometry` creation in the original factory slot. The
immutable COM resource retains its factory and exact caller metadata, while a
shared allocation-free core validates and clamps radii, constructs the four
straight edges and four cubic quarter-ellipse corners, and performs analytic
fill containment after inverse affine transformation. Both the portable
object and the existing Windows provider now use that core, preventing the two
paths from drifting.

`GetBounds` and `Simplify(CUBICS_AND_LINES)` deliberately delegate to the
retained path, preserving Direct2D's observable curve representation. Invalid
radii clear the factory output and fail closed; stroke, tessellation, boolean,
outline, metric, and widen operations remain explicit path-level gates. The
Apple Silicon warning-as-error tree passes all 14 CTests and the managed
Direct2D source contract passes 9/9. Windows 11 ARM64 with MSVC 19.44 and
`/W4 /WX` builds and runs the focused core/compatibility executables through a
real SDK `ID2D1RoundedRectangleGeometry*`; non-axis-transformed bounds plus
center and rounded-corner containment match a system `D2D1CreateFactory`
geometry.

ProGPU `14be583c` adds the canonical portable `ID2D1GeometryGroup` IID,
factory slot, metadata, source ownership, and retained path composition. Group
creation validates the fill rule, every source pointer, source-factory
identity, and a bounded transformed-source chain. A forwarding simplified sink
concatenates each child as cubic-and-line figures into one group-owned path
while ignoring child fill modes in favor of the group fill mode. Nested groups
reuse that immutable retained path, matching Direct2D's documented rule that a
group concatenates every source figure before applying the outer group mode;
no child graph is traversed again during rendering.

The object returns exact source identities with balanced references and
supports transformed bounds plus cubic simplification through that retained
path. Fill/stroke containment, tessellation, boolean, outline, metrics, and
widening continue to fail closed at the path layer. The Apple Silicon
warning-as-error tree remains 14/14, and the managed Direct2D contract remains
9/9. Windows 11 ARM64 MSVC 19.44 `/W4 /WX` calls the resource through the real
SDK `ID2D1GeometryGroup*` vtable and compares fill mode, source count/identity,
and non-axis-transformed rectangle-plus-ellipse bounds with system
`D2D1CreateFactory` Direct2D.

ProGPU `5e7a4022` adds canonical portable `ID2D1StrokeStyle` creation and
identity in the base-factory slot. The fixed-layout 28-byte property descriptor
preserves cap, join, miter, dash kind, and dash-offset values. Immutable custom
dash arrays are copied once into the resource, returned through the original
metadata methods, and keep the parent factory alive. Validation rejects
unknown enums, nonpositive/nonfinite miter limits, nonfinite offsets, mismatched
custom-dash pointer/count combinations, negative/nonfinite dash entries, and
all-zero custom patterns while clearing the output.

The validation algorithm is part of the installed shared core, and the Windows
provider now calls it rather than keeping a second acceptance policy. This is
bounded resource metadata work, not a CPU pixel or geometry fallback. The
Apple Silicon warning-as-error suite remains 14/14 and managed Direct2D
contracts remain 9/9. Windows 11 ARM64 MSVC 19.44 `/W4 /WX` creates the object
through the real SDK `ID2D1Factory*`/`ID2D1StrokeStyle*` vtables and matches all
properties plus a four-entry custom dash array against system
`D2D1CreateFactory` Direct2D. Portable stroke rendering still depends on the
explicit path-stroke and device-context execution gates.

ProGPU `cb42e99c` adds canonical portable `ID2D1DrawingStateBlock` identity,
factory creation, and mutable state. Its exact 48-byte base descriptor retains
geometry/text antialias modes, two 64-bit tags, and a finite affine transform.
A null creation descriptor supplies Direct2D defaults. Description updates are
serialized and reject null, unknown modes, or nonfinite transforms without
publishing partial state. The optional DirectWrite rendering-parameter pointer
is owned only through `IUnknown` lifetime, so no DirectWrite implementation or
Windows activation leaks into the portable resource.

The Apple Silicon warning-as-error suite remains 14/14 and the managed source
contract remains 9/9. Windows 11 ARM64 MSVC 19.44 `/W4 /WX` creates and mutates
the object through the real SDK `ID2D1Factory*` and
`ID2D1DrawingStateBlock*` vtables. Antialias modes, tags, affine matrix, and
null text-parameter identity match system `D2D1CreateFactory` Direct2D. This
finishes the device-independent resource slots of the portable base factory;
WIC/HWND/DXGI/DC targets and their draw-resource families remain explicit
platform or portable-render-target gates.

ProGPU `a8d94060` begins the portable draw-resource family with canonical
`ID2D1Brush` and `ID2D1SolidColorBrush` identity and vtables. Creation uses the
same stable `IProGpuD2DCompatFactoryNative` extension IID and method slot as
the existing Windows provider because the original `ID2D1Factory` does not
create brushes. The fixed-layout 16-byte color and 28-byte brush-property
descriptors map directly onto `D2D1_COLOR_F` and `D2D1_BRUSH_PROPERTIES`.
Null properties select opacity one and the identity transform.

Color, opacity, and transform mutation is serialized. Nonfinite colors,
opacity outside `[0, 1]`, and nonfinite transforms fail closed at creation or
leave an existing resource unchanged. Each brush owns its parent factory and
can be queried through resource, brush, solid-brush, and canonical `IUnknown`
identity without a Windows COM runtime. This is resource state only: it does
not perform CPU rasterization or promise a portable render target before that
recording gate is implemented.

The Apple Silicon warning-as-error tree remains 14/14. Windows 11 ARM64 MSVC
19.44 `/W4 /WX` creates, queries, reads, and mutates the same object through
real SDK `ID2D1Brush*` and `ID2D1SolidColorBrush*` pointers. A system Direct2D
pixel oracle remains coupled to the next portable render-target checkpoint,
because system solid brushes are device-dependent render-target resources.

ProGPU `ea2a4f8d` adds that first portable target checkpoint. The installed
compatibility target now exposes the complete original `ID2D1RenderTarget`
vtable shape and canonical IID. A new, independently versioned ProGPU scene
factory extension creates a pixel-sized target without pretending that a
WIC bitmap, HWND, HDC, or DXGI surface exists off Windows. A second target
extension reports clear/draw metadata and writes the pointer-free retained
semantic-scene stream directly into caller-owned storage.

The implemented standard calls are target solid-brush creation, `BeginDraw`,
`EndDraw`, `Clear`, transform/antialias/text-antialias/tag/DPI state,
save/restore drawing state, sizing queries, `DrawLine`, and fill/stroke for
rectangles, equal-radius rounded rectangles, and ellipses. These primitive
calls append directly to `semantic_scene_builder`; one deterministic build
produces the same stream consumed by the Metal/Vulkan/D3D12/WebGPU renderer.
There is no CPU rasterization, pixel readback, per-primitive GPU submission,
or Windows handle in this path.

Direct2D drawing methods return `void`, so invalid state and unsupported calls
are latched and returned from `Flush`/`EndDraw` with the active tags. Bitmap,
gradient, text, arbitrary geometry, layer, mesh, and clip slots are present but
failed closed at this first target checkpoint. Zero-width stroke calls are
rejected rather than being confused
with the retained format's zero-width fill marker; unequal rounded radii also
remain gated until the analytic primitive or portable path lowering preserves
them exactly. Each new draw session resets recording and advances generation.

The Apple Silicon warning-as-error tree remains 14/14 and verifies four
primitive records, clear metadata, scene ID/generation, exact serialized size,
invalid zero-stroke rejection, and unsupported-call latching. Windows 11 ARM64
MSVC 19.44 `/W4 /WX` builds and runs the same target through a real SDK
`ID2D1RenderTarget*`, including `CreateSolidColorBrush`, sizing, `BeginDraw`,
`FillRectangle`, and `EndDraw`. Native Direct2D WIC/DXGI pixel comparison and
broader resource coverage remain the next validation and integration gates.

ProGPU `1f6748b4` adds the installed typed submission adapter
`progpu_native_direct2d_scene_submission.hpp` and closes the portable
presentation half of that gate. `update_scene_target(...)` builds into a
caller-owned byte span and transactionally updates the existing native engine.
`render_scene_target(...)` additionally queries the standard target size/DPI,
maps explicit Direct2D clear state into one semantic frame, and renders into a
borrowed same-device WebGPU texture view. A target session without `Clear`
preserves the attachment; an explicit `Clear` wins even if the caller supplied
the preserve flag. Non-isotropic DPI, missing views, insufficient scratch, and
unsupported frame flags fail before engine submission with typed stage,
HRESULT, native-status, and byte-count diagnostics.

This adapter neither owns nor creates a second device, surface, renderer, or
provider ABI. The scene stream crosses the CPU only as retained command data;
no pixel readback or repack occurs, and the engine submits the complete target
once. The pinned WebScene/Dawn hardware gate now constructs the portable COM
factory/target and four solid brushes, records standard Direct2D clear,
rectangle, ellipse, stroked-rectangle, and rounded-rectangle calls, submits
through the adapter, and verifies the resulting BGRA pixels in a 64x48
IOSurface on Apple Metal. Metrics require four retained draws and one GPU
submission. The no-provider Apple Silicon suite remains 14/14, the managed
source contract remains 9/9, and Windows ARM64 MSVC 19.44 `/W4 /WX` compiles
and executes the same installed submission declarations alongside the real
SDK render-target ABI. A Windows system-Direct2D versus ProGPU D3D12 pixel
oracle remains the differential follow-up gate.

ProGPU `d5cb1f71` adds the portable target's immutable
`ID2D1GradientStopCollection` and mutable `ID2D1LinearGradientBrush`/
`ID2D1RadialGradientBrush` resources. Their canonical IIDs, Windows vtable
order, factory ownership, gamma and extend modes, mutable coordinates,
opacity, and transforms are retained as COM state. Recording lowers them to
the existing semantic-scene gradient brush table, including sRGB versus
linear-light interpolation, pad/repeat/reflect spread, and the inverse active
draw/brush coordinate transform. The same 64x48 fixture renders the gradients
through Metal, D3D12, and Vulkan in one retained submission.

ProGPU `b61052c9` through `a754d6c8` adds upload-backed `ID2D1Bitmap` and
`ID2D1RenderTarget::DrawBitmap`. The portable object supports canonical COM
identity, size/pixel-size/format/DPI queries, full or subrectangle
`CopyFromMemory`, same-factory `CopyFromBitmap`, mutation generations, and
same-scene resource reuse. `DXGI_FORMAT_R8G8B8A8_UNORM` and the normal
Direct2D `DXGI_FORMAT_B8G8R8A8_UNORM` premultiplied form are supported.
BGRA bytes remain BGRA from caller-owned creation through retained scene
serialization and `WGPUTextureFormat_BGRA8Unorm` upload; the path performs no
channel repack, CPU rasterization, or readback. Nearest and linear bitmap
sampling, source rectangles in bitmap DIPs, destination rectangles, opacity,
and the active draw transform lower to one semantic image resource and draw.

The expanded fixture has five retained draws and one GPU submission. Metal and
Windows D3D12 produce the identical PPM SHA-256
`8289f940323a2dd242b6dd5870108d75e37ed57ba332b8f4422915d51d61faf4`.
Ubuntu 24.04 ARM64 Vulkan/llvmpipe differs at 140 existing analytic edge
pixels, with maximum channel difference one and mean difference
`0.025607638888888888`; all eight semantic probes, including two bitmap
probes, match exactly. Windows 11 ARM64 builds the complete checkpoint with
MSVC 19.44 `/W4 /WX`, calls the object through the SDK `ID2D1Bitmap*` and
`ID2D1RenderTarget*` vtables, and passes the native Direct2D/WIC differential
with mean byte error `0.3576` over 12,288 BGRA bytes.

ProGPU `f5453920` adds canonical mutable `ID2D1BitmapBrush` state and rendering
to the same target. Creation and `SetBitmap` validate factory ownership;
`QueryInterface`, source identity, clamp/wrap/mirror extend modes,
nearest/linear interpolation, opacity, and affine brush transforms preserve the
original COM contract. Supported line, rectangle, equal-radius rounded-
rectangle, and ellipse fill/stroke calls lower the brush to one retained image
draw with a GPU-generated geometry or analytic coverage mask. Source pixels are
uploaded once per bitmap identity/generation and remain in their original
premultiplied RGBA/BGRA representation. Extended source coordinates and the
existing image address shader implement tiling without CPU replication,
readback, repacking, or per-tile submissions. Mask generation is encoded into
the shared semantic command encoder, so the complete seven-draw fixture still
submits once.

Apple M3 Pro Metal and Windows 11 ARM64 D3D12 produce byte-identical captures
with SHA-256
`08fba84c33ac65590568e8e5209b47c5295eb7a642ad99c05b885f5a9f6b7495`.
Ubuntu 24.04 ARM64 llvmpipe LLVM 20.1.2/Vulkan produces SHA-256
`90459b346415605e57bc828b433b7e64e2ad452fde59985c937a10821d570ad4`;
149 of 3,072 pixels differ by at most one channel value, none differs by more
than one, mean absolute channel difference is `0.026801215277777776`, and all
13 semantic probes are exact. Windows MSVC 19.44 `/W4 /WX` also calls the
portable object through real SDK `ID2D1BitmapBrush*`, `ID2D1Brush*`, and
`ID2D1RenderTarget*` pointers. The independent system-Direct2D/WIC versus
ProGPU D3D12 oracle passes with mean byte error `0.5718` over 12,288 BGRA
bytes.

ProGPU `65a25b4f` extends the portable target with arbitrary
`ID2D1RenderTarget::FillGeometry`. A bounded typed
`ID2D1SimplifiedGeometrySink` captures filled line/cubic contours from any
same-factory portable geometry, so rectangle, path, ellipse, rounded-rectangle,
transformed, and grouped geometry all reuse their existing COM implementation.
The callback is fully consumed before recording; neither the geometry, sink,
nor brush COM pointer enters the retained scene. Solid and gradient fills lower
directly to one pointer-free path command. Bitmap-brush fills reuse the same
path as a GPU vector mask followed by the retained image draw, preserving fill
rule, active transform, source generation caching, sampling, addressing,
opacity, and brush transform.

Ordinary vector-mask rasterization and its image draw now share the semantic
frame command encoder, so the expanded eight-draw fixture still produces one
queue submission. Signed-winding and boolean-mask algorithms retain explicit
phase submissions only where an intermediate texture result is required; they
do not fall back to CPU pixels. Apple M3 Pro Metal and Windows 11 ARM64 D3D12
produce byte-identical captures with SHA-256
`4e277be9cf8613fd9f12b5e7c3cec287e9c70bc49348e0fab305ac55a1ab2d26`.
Ubuntu 24.04 ARM64 llvmpipe LLVM 20.1.2/Vulkan produces SHA-256
`7fb3768723da1a4c1e5f7c16c7a261c70281dc733b46a14a8296f25ca367ec09`;
151 of 3,072 antialiased edge pixels differ by at most 1/255, no pixel exceeds
that bound, mean absolute channel difference is
`0.026801215277777776`, and all 14 semantic probes are exact. The independent
system-Direct2D/WIC versus ProGPU D3D12 oracle passes with mean byte error
`0.7188` over 12,288 BGRA bytes.

The exact checkpoint passes all 15 native CTests on macOS and Linux and all 17
on Windows. The full Windows ARM64 MSVC 19.44 `/W4 /WX` build also qualifies
the actual `progpu_native_direct2d.dll`. That full-DLL gate fixed a legacy
Windows `near` macro collision and added the standard COM output-pointer form
for `ComPtr::As(&result)`. The live D3D12 integration test has a Windows-only
180-second bound because cold shader compilation in the Parallels VM completed
in 81.62 seconds; that VM wall time is gate evidence, not a performance claim.

ProGPU `dda3ca0f` with strict-compiler correction `35fbf05d` adds arbitrary
portable `ID2D1RenderTarget::DrawGeometry`. A separate bounded typed sink
retains every filled or hollow figure, open/closed seam, line/cubic segment,
forced-unstroked edge, and forced-round join while the geometry callback is
active. The resulting data is compiled immediately into pointer-free native
stroke resources; ProGPU does not retain the geometry or stroke-style COM
object and does not call `Widen` or manufacture a CPU-filled outline.

The base portable `ID2D1StrokeStyle` path supports flat/square/round/triangle
caps, miter/bevel/round/miter-or-bevel joins, miter limits, all standard dash
styles, custom dash arrays, and dash offsets. Line-only non-bitmap figures take
the existing `STROKE_BATCH` fast path. Curves, forced join/edge flags, and
dashed runs reuse the shared native semantic path-stroke compiler. Bitmap
brushes use those same compiled primitives as a GPU geometry mask for the
retained image draw. Active target/brush transforms, sampling, addressing,
opacity, and bitmap generation caching remain typed backend-neutral state.
Zero-width base strokes are exact no-ops; negative/non-finite widths and
cross-factory geometry, brush, or style resources fail closed.

The nine-draw fixture still submits once. Apple M3 Pro Metal and Windows 11
ARM64 D3D12 are byte-identical at SHA-256
`42c55b1dc88b1d855f5948d870355af0bcac78975ec26469a90cb531e1f4a131`.
Ubuntu 24.04 ARM64 llvmpipe LLVM 20.1.2/Vulkan produces SHA-256
`2e6babc3968974c3d4769a31388b10c3d043ff897c417d8ec8c1a255e0962dfd`;
153 of 3,072 antialiased edge pixels differ by at most 1/255, no pixel exceeds
that bound, mean absolute channel difference is
`0.026258680555555556`, and all 15 semantic probes are exact. The independent
system-Direct2D/WIC versus ProGPU D3D12 oracle passes with mean byte error
`0.7175` over 12,288 BGRA bytes. Exact-source native suites pass 15/15 on
macOS, 15/15 on Linux, and 16/16 on Windows under full MSVC `/W4 /WX` provider
DLL compilation. A cold newly introduced D3D12 stroke pipeline took 161.85
seconds in the Parallels VM, so the Windows-only integration-test bound is 300
seconds; this is correctness-gate timing, not a performance claim.

The portable target now implements
`ID2D1RenderTarget::CreateBitmapFromWicBitmap` through the canonical
`IWICBitmapSource` IID and five-method COM vtable. Native Windows WIC sources
and ABI-compatible non-Windows decoder sources enter the same path. The
qualified input profiles are `GUID_WICPixelFormat32bppPBGRA`,
`GUID_WICPixelFormat32bppPRGBA`, `GUID_WICPixelFormat32bppBGRA`, and
`GUID_WICPixelFormat32bppRGBA`, mapped to premultiplied BGRA8 and RGBA8
storage. Premultiplied sources copy their rows directly into the final bitmap
allocation. Straight-alpha sources copy once into that same allocation and
are premultiplied in place with eight-pixel NEON or four-pixel SSE2 kernels and
a bounded scalar tail when the requested bitmap is premultiplied. An explicit
alpha-ignore bitmap preserves the straight source RGB and carries typed scene
metadata that forces sampled alpha to one in the shared WebGPU shader. The
integer `(c*a)/255` rounding is byte-exact with the
scalar oracle across zero, near-zero, half, near-opaque, and opaque alpha; the
qualified nine-pixel fixture exercises both the vector body and tail on ARM64
and x64, while a 256x256 fixture exhaustively checks every 8-bit channel/alpha
pair through three independent color lanes. There is no intermediate repack,
reflection, GPU readback, or format guess. Null/default properties infer the
source format and use Direct2D's
specified 96 DPI while embedded WIC DPI is ignored. Explicit format
mismatches, unsupported WIC formats, invalid dimensions, and oversized
payloads fail closed before pixel copy. Portable tests verify exact payload
preservation/conversion and, on Windows, the real SDK IID, all four GUIDs,
`WICRect` layout, and `IWICBitmapSource` vtable call.

`CreateSharedBitmap(IID_ID2D1Bitmap, ...)` now creates a same-factory portable
view over an ordinary upload/WIC bitmap or compatible-target scene bitmap
without copying its payload. An ordinary view retains the source, forwards
mutations to the shared storage, tracks the live generation, and may select
independent valid DPI. Its internal typed storage identity lets source and
views reuse one retained scene image and one GPU upload per generation even
when both are drawn in the same frame. A compatible-target view instead
retains and forwards the independently versioned child scene so later bitmap
draws and opacity masks still render directly into the bounded GPU attachment.
Format mismatches, incompatible alpha reinterpretation, foreign factories,
null data, and non-bitmap IIDs fail closed. An ordinary premultiplied bitmap
may also expose an alpha-ignore view over the same storage and GPU upload;
draw metadata selects the opaque-alpha shader path without copying or mutating
pixels. Windows tests call both ordinary and A8
compatible sources through the actual SDK
`ID2D1RenderTarget::CreateSharedBitmap` vtable.

`CreateSharedBitmap(IID_IWICBitmapLock, ...)` is now a second typed ownership
lane. The installed portable header carries the canonical four-method lock
vtable and IID, so a real Windows lock or an ABI-compatible portable provider
enters the same implementation. PBGRA and PRGBA locks retain their COM owner
and alias the live data pointer without copying; the wrapper preserves padded
stride, exposes the target DPI or an explicit independent DPI, serializes the
current bytes into retained scenes, and forwards `CopyFromMemory` and
`CopyFromBitmap` mutations into the locked allocation. Dimensions, stride,
buffer extent, pixel format, alpha mode, and bounded scene size are validated
before an object is published. Straight BGRA/RGBA locks are accepted only for
an explicit alpha-ignore target, which aliases the original RGB and forces
sampled alpha opaque on the GPU; requesting that storage as premultiplied fails
closed because sharing cannot perform an in-place semantic conversion.
Portable byte-oracle tests cover padded rows, caller-side live mutation, and
both copy paths; Windows ARM64/x64 builds additionally call the object through
the real SDK `IWICBitmapLock` and `ID2D1RenderTarget` vtables. `IDXGISurface`
sharing remains the separate device-domain lane.

The portable target now implements the original
`ID2D1RenderTarget::DrawGlyphRun` vtable slot at the already-shaped text
boundary. The installed header publishes exact `DWRITE_GLYPH_OFFSET` and
`DWRITE_GLYPH_RUN` layouts plus the canonical `IDWriteFontFace` vtable prefix
through `GetGlyphRunOutline`. A genuine Windows DirectWrite font face or a
portable ABI-compatible provider writes the complete run into the existing
one-shot geometry sink. ProGPU translates that outline immediately to its
pointer-free retained path resource, applies the baseline and active target
transform, and fills it with the same solid, gradient, or bitmap brush path as
other Direct2D geometry. It does not remap characters, reshape text, rasterize
pixels on the CPU, read back the target, or submit once per glyph. Optional
advances/offsets, RTL, sideways, and all three measuring-mode values pass
through unchanged to the outline provider; invalid counts, pointers, finite
values, and enum values latch the target error for `Flush`/`EndDraw`.

The portable test exercises the call through both the namespaced interface and
an actual Windows SDK `ID2D1RenderTarget*`/`DWRITE_GLYPH_RUN` vtable call. It
asserts exact SDK structure offsets, one outline callback for a two-glyph run,
one retained path draw, and translated scene bounds. This is the first
portable standard DirectWrite-consumer lane.

`SetTextAntialiasMode(ALIASED)` now selects the shared path rasterizer's exact
one-sample, pixel-center coverage lane for glyph outlines; DEFAULT, GRAYSCALE,
and CLEARTYPE retain the fastest qualified 8x8 GPU coverage lane while their
remaining filter differences are implemented. The one-sample contract is
accepted consistently by scene validation, retained path execution, and
vector-mask execution, and works for solid, gradient, and bitmap-brush glyph
fills without CPU rasterization or readback. Direct2D target transform,
geometry/text antialias modes, tags, and other drawing state persist across
`BeginDraw`/`EndDraw`; only per-session commands, scopes, and latched errors
are reset.

`ID2D1RenderTarget::DrawTextLayout` now supplies the matching canonical
`IDWritePixelSnapping`/`IDWriteTextRenderer` callback vtable to an existing
layout. DirectWrite or a portable layout provider emits glyph runs,
underlines, strikethroughs, and inline-object callbacks synchronously. Glyphs
reuse the complete-run path above; decorations remain analytic rectangles;
inline objects recursively receive the same renderer. A typed drawing effect
may override the default brush only when it exposes the canonical
`ID2D1Brush` IID. `NO_SNAP` is reported through the pixel-snapping callback and
`CLIP` reads the layout's ABI-stable maximum size, then records one exact
aliased scene clip around all callbacks. The target validates the fixed
`IDWriteTextLayout::Draw` slot, all callback data, brush identity, finite
metrics, and balanced clip lifetime. Color-font options fail closed until the
color-glyph callback generation is implemented.

`DISABLE_COLOR_BITMAP_SNAPPING` is accepted independently on the monochrome
layout lane: without `ENABLE_COLOR_FONT` there are no color-bitmap glyphs to
snap, so Direct2D defines no pixel change. Requests that actually enable color
fonts still fail closed until typed color-vector/bitmap/SVG callbacks land.

`ID2D1RenderTarget::DrawText` now uses the explicit
`portable_text_layout_factory` extension on its supplied text-format object.
The target passes the exact UTF-16 span, measuring mode, and layout-rectangle
extent to that typed provider, owns the returned layout for the synchronous
draw, and delegates it to the same `DrawTextLayout` renderer at the rectangle
origin. The extension is deliberately provider-neutral: a portable ProGPU
format can bind the native C++ shaping/layout implementation, while a foreign
format that does not publish the contract fails with `E_NOINTERFACE`. The
portable target does not discover or activate a system DirectWrite factory,
copy or reshape text itself, or select a CPU raster fallback. The genuine
Windows provider remains responsible for system `IDWriteTextFormat` objects.

The portable target also retains the canonical `IDWriteRenderingParams`
object supplied through `SetTextRenderingParams` and returns the identical
strongly owned interface from `GetTextRenderingParams`; passing null clears
the state. The installed interface exposes the original gamma, enhanced
contrast, ClearType level, pixel-geometry, and rendering-mode vtable so the
same object can be used through an actual Windows SDK pointer. ProGPU's
portable glyph lane remains vector-outline/GPU coverage based, so this slice
preserves the Direct2D resource/lifetime contract but does not claim that
DirectWrite raster-filter parameters alter its pixels yet. The corresponding
quality mapping and incompatible-antialias validation remain explicit parity
work, never a CPU raster fallback.

Portable rectangle and path geometry implement the canonical
`ID2D1Geometry::Outline` vtable slot for simple filled contours. Rectangle
outlines are emitted analytically. Path outlines apply the caller's affine and
flattening tolerance, remove duplicate/zero-length edges, normalize each
contour direction, and emit the
fill-invariant Direct2D sink shape: alternate fill mode, filled closed figures,
and explicit closing points. Multiple independent contours, point contact, and
non-touching alternate-fill nesting are qualified in one transaction. A
containment-depth pass reverses every odd-depth contour so holes remain correct
under winding consumers too; hollow-only geometry produces an empty outline.
Winding-rule nesting retains each source contour's signed contribution, sums
ancestor winding, omits boundaries whose two sides remain filled or empty, and
reverses true hole boundaries.

When two simple contours cross or share an edge, `Outline` routes their
normalized polygons through the same native Boolean boundary engine used by
`CombineWithGeometry`. Alternate fill selects xor; winding selects union for
equal signed contributions and xor for opposing contributions. This removes
shared/internal edges before any caller callback, while the existing four-lane
NEON/SSE2 edge-bounds broad phase remains shared. A contact-only T-junction
keeps both figures and transactionally inserts the contact vertex into the
touched edge, matching Direct2D's observable line transcript.
The implementation matches genuine Direct2D fill mode, unchanged segment-flag
state, callback counts, and dense disjoint, corner/T-point-touch, shared-edge,
alternate-overlap, winding-overlap, alternate-hole, and winding-hole regions on
Windows ARM64 and x64.

Three or more interacting simple contours use the generalized native
split/classify/trace transaction. Every contour pair shares the four-lane
NEON/SSE2 edge-AABB broad phase; exact crossings and positive collinear overlap
split edge parameters. Each sub-edge then evaluates alternate parity or signed
winding on both sides against every contour, drops internal edges, deduplicates
coincident directed boundaries, and traces the remaining graph. The dependent
classification and graph walk remain scalar. A bounded one-million-segment cap
fails closed before replay. Local and Windows ARM64/x64 three-rectangle
differentials match Direct2D callback counts, dense pair/triple regions,
alternate XOR area 15, and winding union area 20.

A source contour with exactly one proper transverse self-intersection is split
at the double-precision line intersection into its two simple lobes before the
same orientation/fill normalization. Alternate-fill contours with multiple
distinct proper crossings now split every nonadjacent pair, classify parity on
both sides of each sub-edge, discard internal edges, and trace all filled
lobes. Candidate self-edge pairs use the shared four-lane NEON/SSE2 AABB broad
phase; crossing solves and dependent walks are scalar. Genuine Direct2D
ARM64/x64 bow-tie differentials match the two-figure/six-line transcript,
unchanged flags, dense lobes, and area. Alternate and winding five-crossing
pentagrams also match system callback topology, area, and full dense region
lattices. Winding self-intersections retain each positive and negative integer
winding layer as a signed simple contour before whole-path normalization. A
reverse-wound square inside the pentagram's +2 center therefore subtracts one
layer without opening a hole; local and genuine Direct2D ARM64/x64 callback,
area, and dense-region comparisons match. Repeated or triple crossing points,
collinear or endpoint-ambiguous intersections, and numerically invalid graphs
still fail closed transactionally.

`ID2D1PathGeometry::ComputeArea` now consumes the same transactionally
normalized Outline contours instead of summing each source figure in
isolation. Signed contour reduction therefore subtracts alternate/winding
holes, counts alternate overlap as xor, counts equal-direction winding overlap
as union, and preserves point-contact area without double-counting shared
edges. The dependent signed-area reduction remains scalar by definition; all
independent boundary-pair work stays in the shared NEON/SSE2 normalizer.
Portable hole/overlap fixtures and genuine Direct2D ARM64/x64 shared-edge,
alternate-overlap, winding-overlap, corner-contact, and T-contact comparisons
match exactly. Qualified bow-tie plus alternate and winding five-crossing
pentagram inputs, including mixed-figure signed-layer cancellation, share the
same area path; ambiguous crossings fail closed with initialized zero output.

`ID2D1PathGeometry::Tessellate` now consumes that same normalized Outline
topology rather than triangulating source figures independently. Positive
contours become components; every negative contour is assigned to the
smallest containing positive contour, and rightmost-first zero-area bridges
turn each component plus its holes into a bounded weakly-simple polygon for
the existing dependency-bound ear clipper. Collinear bridge vertices are
removed before clipping, duplicate bridge endpoints are treated as one
topological vertex, and the complete triangle array is prepared before the
caller sink is touched. Disjoint components, alternate/winding overlaps,
self-intersecting pentagrams, holes, multiple holes, and nested islands now
share exact fill semantics with Outline and ComputeArea. Local optimized and
sanitizer tests plus genuine Direct2D ARM64/x64 comparisons match area and
dense triangle coverage for a single hole and a two-hole/nested-island case.
Triangle order and count are deliberately not compared because Direct2D does
not make its valid diagonalization an API contract. Ambiguous topology and
the existing bounded normalization limits still fail closed transactionally.

Portable nondegenerate rectangle geometry also implements exact
`GetWidenedBounds` for the default stroke and same-factory solid stroke
styles. The stroke expands in local geometry space and the caller transform
is applied afterward, matching Direct2D ordering for nonuniform affine
transforms. An axis-preserving `ID2D1TransformedGeometry` first materializes
its intrinsic rectangle transform, then widens by the unscaled stroke width,
then applies the caller world transform; this avoids incorrectly scaling the
stroke with the intrinsic geometry transform. Dashed styles, degenerate
rectangles, and non-axis-preserving transformed rectangles continue to fail
closed until their cap/run/offset bounds share the retained stroke compiler.
The native fixture compares both base and transformed results through genuine
system Direct2D pointers on Windows.

The same default-stroke rectangle lane now implements
`StrokeContainsPoint`. It tests the exact transformed outer miter rectangle
and excludes only the strict transformed inner rectangle, so both centered
stroke boundaries remain included. Non-finite input is rejected; non-null
styles, degenerate rectangles, and singular transforms fail closed until the
shared styled-offset implementation is available. Portable and Windows
system-Direct2D fixtures compare edge and center points for both base and
intrinsically transformed rectangles.

Default-stroke nondegenerate rectangles now also implement `Widen` into a
caller-owned simplified geometry sink. Base rectangles reproduce Direct2D's
alternate-fill pair of closed outer and inner miter contours. Positive
axis-aligned intrinsic transformed rectangles reproduce Direct2D's single
winding-fill, force-unstroked open contour, including its explicit bridge
segments; the caller transform is applied only after widening. The Windows
oracle compares fill mode, segment flags, figure kinds, closure, and every
emitted point against system Direct2D. Zero width, explicit styles, degenerate
rectangles, and transformed cases with a collapsed inner contour or reflected,
swapped-axis, or general-affine intrinsic transforms remain fail closed.

Nondegenerate rectangles now implement `CompareWithGeometry` against
same-factory rectangles and bounded transformed-rectangle chains. The current
geometry stays in its own coordinate space; only the input geometry receives
the caller transform. Axis-aligned, sheared, rotated, and reflected operands
are compared as exact convex quadrilaterals with double-width cross products
and separating-axis projections, without allocation.
Equality follows system Direct2D and reports `IS_CONTAINED`, strict input
containment reports `CONTAINS`, strict outer containment reports
`IS_CONTAINED`, separated rectangles report `DISJOINT`, and intersections or
boundary-only contact report `OVERLAP`. Flattening tolerance is validated but
does not change the analytic result. Cross-factory resources return
`D2DERR_WRONG_FACTORY`; degenerate inputs remain fail closed. The Windows
oracle compares all five observable outcomes plus sheared candidates,
reflected equality, and a sheared transformed source with system Direct2D.

The extended Windows native-renderer job keeps its independent C++ sample and
all native CTests single-shot. Hardware and Parallels execute the managed
retained sample at 640x360. Microsoft Basic Render Driver first compiles and
passes the complete 16-source-command picture through the native C++ stream
validator with the same exact command/resource/draw/stack counters. It then
GPU-executes a bounded four-source-command analytic managed scene at 320x180
and 0.5 DPI: nested and direct solid rectangles plus a linear-gradient
rectangle coalesce into one retained native batch. That lane still requires
native submission, second-frame zero-upload retention, readback, and
DPI-scaled solid/gradient/background pixel probes. It avoids the full managed
path/glyph coverage scene's repeatable roughly 80-second software-device loss
without a retry or CPU renderer. The full C++ D3D12 sample remains mandatory
on that adapter, and every hardware/Parallels lane keeps the complete managed
GPU scene.

The later mixed-picture benchmark is independently bounded on the two known
constrained D3D12 adapters. Microsoft Basic Render Driver validates the full
384-item compiler/parser/retained-snapshot stream without submitting the dense
device-removing profile, then runs a one-item live managed/native pixel
differential after one cache warm frame. Parallels still runs the full
384-item stress through the C++ renderer before that bounded differential, and
hardware Windows retains the full 384-item managed/native differential. The
Basic lane initializes the real D3D12 native compositor for stream update and
the bounded live comparison; it does not treat validation-only or CPU-rendered
output as pixel parity.

The same rectangle domain implements `CombineWithGeometry` for union,
intersection, xor, and exclusion. Axis-preserving inputs keep the exact fixed
three-by-three coordinate-grid tracer: it labels four-connected components,
extracts only exterior and hole edges, traces closed contours, and removes
collinear vertices without heap allocation. The Windows oracle compares that
complete undirected boundary edge set for all four modes.

Independently affine-transformed rectangles now use a second bounded native
topology path. It splits both convex quadrilateral boundaries at pairwise
intersections and coincident-edge endpoints, classifies Boolean membership on
both sides of every resulting sub-edge, deduplicates coincident directed
boundaries, and traces the selected segments into alternate-fill,
force-unstroked closed contours. Ambiguous
crossing vertices select the smallest positive turn so touching xor lobes do
not become a self-crossing contour. The implementation uses fixed arrays only;
there is no heap allocation, CPU pixel raster, readback, or backend-specific
branch. Native probes cover all four Boolean regions for both an affine input
operand and an affine source geometry, plus identical rectangles, full and
partial shared edges, and same-side partial collinear overlap. The Windows
oracle records the same operations through system Direct2D and compares the
result predicates over the complete focused probe lattice. Cross-factory,
degenerate, and non-rectangle inputs fail before touching the sink.

`ID2D1PathGeometry::CombineWithGeometry` now applies all four operations to
arbitrary counts of filled path contours, including disjoint components,
nested holes, shared edges, and interacting contours. Each operand first uses
the same transactional `Outline` normalization as fill and area queries, so
alternate and winding inputs become a canonical alternate-fill boundary set;
the input transform is applied only while normalizing the input operand.
The shared topology engine tags every contour with its operand, splits
crossings and positive collinear overlaps after four-lane ARM64 NEON or SSE2
AABB rejection, evaluates each Boolean mode on both sides of every sub-edge,
deduplicates directed boundaries, and publishes force-unstroked closed
contours only after the complete graph is valid. Empty-operand identities are
handled explicitly. Any count of distinct proper self-crossings is first
normalized through the same alternate/signed-winding layer transaction, so
the four modes also accept alternate pentagrams and mixed-figure winding
pentagrams. Repeated/triple crossing points, ambiguous contacts, and
numerically invalid graphs still fail before sink mutation. Optimized portable
fixtures compare all four modes over dense lattices for concave, identical,
multi-component, nested-hole, and star-center inputs. Genuine system Direct2D
differentials run those component/hole/star lattices under clean Windows 11
ARM64 and x64 MSVC `/W4 /WX` builds.

The same normalized contour-set engine now implements
`ID2D1PathGeometry::CompareWithGeometry` for arbitrary component and hole
counts. Transactional exclusion in both directions establishes equality and
containment, intersection establishes interior overlap, and a four-lane
NEON/SSE2 boundary-contact pass distinguishes touching sets from disjoint
sets. It returns all five observable relations with Direct2D's orientation:
equal paths report `IS_CONTAINED`, a source inside the transformed input
reports `IS_CONTAINED`, an input inside the source reports `CONTAINS`,
separated contours report `DISJOINT`, and proper crossing or boundary-only
contact without containment reports `OVERLAP`. Shared boundaries do not erase
an otherwise exact containment relation. The transformed input is normalized
through the caller's tolerance and same-factory ownership is enforced before
analysis. Distinct proper self-crossings use normalized alternate/signed-
winding layers, including the alternate pentagram's center hole and the mixed
winding pentagram's filled center. Repeated/triple crossings and ambiguous
contacts leave the output `UNKNOWN` and fail closed. Windows ARM64/x64
differentials cover every simple relation plus multi-component containment,
equality, nested-hole separation, shared-boundary containment, and both star
center relations against system Direct2D.

`ID2D1PathGeometry::StrokeContainsPoint` now handles one simple closed path
with the canonical null/default solid miter stroke. The query point is mapped
back through any invertible affine world transform so nonuniform scale and
shear preserve Direct2D's stroke-before-transform ordering. Independent
point-to-segment tests execute four at a time through ARM64 NEON or SSE2, with
a bounded scalar tail only on architectures without either intrinsic family.
The topology-dependent join pass covers bevel wedges and default miter
extensions up to Direct2D's limit. Explicit segment flags, multiple figures,
degeneracy, self-intersection, and singular transforms fail closed. Straight,
miter-corner, interior, exterior, concave-corner, and
nonuniform transformed probes pass locally under optimization and sanitizers
and match genuine system Direct2D on Windows ARM64.

Closed-path stroke containment also accepts same-factory solid bevel, miter,
and miter-or-bevel styles. It reads the typed COM line-join and miter-limit
state, preserves the SIMD segment body, and selects bevel-only or limited-miter
join wedges without changing cap semantics on a closed figure. The Windows
oracle distinguishes a point clipped by a bevel from one inside its wedge and
matches system Direct2D. Round joins reuse an exact vertex disk on top of the
SIMD segment body; inside/outside arc probes also match the system. Dashed
closed-path containment now reuses the renderer's native curve-dash run
splitter for the built-in and custom patterns, including offset and flat,
square, round, or triangle dash caps. All visible run bodies are packed into
the same four-lane NEON/SSE2 distance pass; only ordered joins and caps remain
scalar. Optimized and sanitizer suites pass, and Windows x64 plus ARM64 match
genuine Direct2D for body, gap, flat-cap, and round-cap probes at an explicit
0.001 flattening tolerance. `GetWidenedBounds` now consumes those same runs for
dashed closed paths, includes exact line-body, cap, and join extrema, and adds
affine support points for round geometry before the SIMD transform/reduction
pass. Flat and round styles, including a non-uniform transform, match genuine
Direct2D on Windows x64 and ARM64. `Widen` now emits one closed outline per
joined line dash for all four cap styles. Round caps use two cubic quarter
arcs per semicircle; every line endpoint and cubic control/end point is packed
and transformed through the SIMD point path before the caller's sink is
touched. Ordered line/cubic capture tests compare dense widened-region probes
with `StrokeContainsPoint`, while genuine Direct2D x64 and ARM64 validate cap
queries and accept each widened style. The Windows oracle also confirms that
closed-source seam endpoints keep `DashCap`; they do not acquire open-figure
`StartCap`/`EndCap`. Bevel, qualified miter, and miter-or-bevel joins share the
same offset-side builder; a low-limit miter-or-bevel run emits the bevel pair.
Dense region probes and genuine Direct2D x64/ARM64 corner probes match for both
miter outcomes. Round joins now retain typed arc edges on the outer offset
side and emit one or two cubic circular spans, with dense region probes and
genuine Direct2D x64/ARM64 corner/widen validation. Over-limit `Miter` joins
now intersect a limit-normal clipping plane with both outer offset edges;
inside-tip/outside-tip probes distinguish that shape from both a full miter
and `MiterOrBevel`. The behavior follows Microsoft's documented distinction
between clamped miters and bevel conversion, and passes portable dense-region
plus genuine Direct2D x64/ARM64 validation. All cap/join combinations are now
covered for dashed line runs in this simple closed-path domain; curved,
multi-figure, and general open-path widening remain broader geometry work.

The containment lane now also accepts a single open figure. Consecutive
nondegenerate segments retain the same four-lane NEON/SSE2 body test and typed
bevel, miter, miter-or-bevel, or round join predicates. Unlike a closed
figure, the first and final vertices use `StartCap` and `EndCap`; dash splits
use `DashCap`, and the shared curve-dash walker runs with open-source seam
semantics. Portable tests cover solid body, flat source caps, miter and round
joins, dashed body/gap, and square dash-cap extension. A genuine Direct2D
differential builds and runs the same probes through ProGPU and system
factories on Windows ARM64 and x64. Open `GetWidenedBounds` shares the same
body/cap/join extrema, including clipped miters, round affine support points,
and the SIMD transform/reduction pass. Dashed bounds retain Direct2D's
conservative source-path envelope even when the final endpoint lies in a gap;
default, round-join, dashed-square-cap, and nonuniform transformed cases match
the system on ARM64 and x64. Open `Widen` emits the same transactional
alternate-fill, force-unstroked outlines used by closed dash runs. Solid and
dashed bodies share the typed miter/bevel/round join and flat/square/triangle/
round cap builders; round edges remain cubic and all endpoints and controls
are SIMD-transformed before sink replay. Dense portable regions match
`StrokeContainsPoint`, while default and square-dashed output regions match a
genuine Direct2D sink on Windows ARM64 and x64. The shared dash-run DTO now
records an exact terminal transition from a gap to an on-dash: Direct2D gives
that zero-length run `DashCap` at its start and the source `EndCap` at its end.
Dedicated containment, widened-output, and shared-walker tests preserve this
otherwise easy-to-miss half-cap rule. The retained semantic-scene and native
MIL stroke compilers consume the same bit and append cap-only GPU primitives,
so the compatibility facade and production renderer cannot diverge here.

Read-only stroke queries now accept multiple independent figures as well.
The path is flattened once, partitioned by typed figure index, and normalized
without joining figure endpoints. Each closed figure keeps its seam join; each
open figure keeps source caps; and each figure restarts the typed dash phase.
`StrokeContainsPoint` returns the union predicate while `GetWidenedBounds`
SIMD-reduces each figure and unions the results. A mixed closed-square/open-
polyline fixture covers solid and dashed bodies, gaps, and bounds locally and
against genuine Direct2D on Windows ARM64 and x64.

`Widen` consumes the same figure partition and now publishes mixed closed/open
paths transactionally. It accumulates every validated default closed offset
ring, open solid outline, dashed run, terminal cap, cubic control point, and
world-transformed point before setting any caller-sink state. Replay then uses
one alternate-fill, force-unstroked transaction, with dash phase independently
restarted for every source figure. Dense local lattices match the union from
`StrokeContainsPoint`; genuine Direct2D ARM64 and x64 validate successful
multi-figure output, default line-only output regions, and dashed output
regions directly against the system containment oracle. Convex closed figures
also use paired typed side contours for solid bevel/round/miter styles and for
a dash run that covers the complete closed source; round joins remain cubic
GPU paths. Dense local and Windows ARM64/x64 system-containment differentials
cover bevel, round, and full-cover custom dash output. Convex null/default
strokes also omit the inner alternate-fill ring when inward erosion collapses
or reverses it, so exact-collapse and fully-consumed interiors now match the
system implementation instead of being rejected. Styled bevel, round, miter,
and miter-or-bevel widening also accepts non-convex closed figures when both
offset sides flatten to simple contours and the inner side remains contained
by the outer side. Topology validation completes before caller-sink mutation;
split/self-intersecting erosions and invalid input remain typed fail-closed
domains. Dense concave bevel/round output matches both the portable
`StrokeContainsPoint` oracle and genuine Direct2D on Windows ARM64 and x64.

The same query and widening pipeline now preserves `D2D1_PATH_SEGMENT`
stroking semantics. `FORCE_UNSTROKED` edges partition each figure into
independent stroke runs without joining across the omitted edge; dash phase
restarts for every run, real figure endpoints retain `StartCap`/`EndCap`, and
the artificial endpoints on either side of an omitted edge use `DashCap`,
including a solid dash style. `FORCE_ROUND_LINE_JOIN` overrides only the join
where its incoming source segment ends, never the tolerance-generated
subsegments used to flatten a curve. Closed figures rotate after the last
omitted edge so the remaining cyclic run stays coherent. Dense solid and
dashed local `Widen`/`StrokeContainsPoint` lattices plus genuine Direct2D
ARM64 and x64 bounds, containment, and widened-output differentials cover the
qualified behavior.

Open solid `Widen` is now explicitly qualified over tolerance-flattened cubic
and quadratic segments, in addition to lines. Two numerical correctness fixes
are shared by containment, widened bounds, and output construction: nearly
collinear float directions are treated as a straight continuation instead of
forming a clipped multi-unit miter spike, and round-join containment accepts
only the actual outer circular sector rather than the full vertex disk. A
mixed cubic/quadratic open curve has dense local `Widen` versus
`StrokeContainsPoint` coverage, while portable and genuine Direct2D widening
and containment probes pass on Windows ARM64 and x64.

The identical simple closed/default-miter domain now implements
`GetWidenedBounds`. It derives segment offset endpoints plus qualified miter
extrema in local geometry space, then transforms and reduces independent
candidate points four at a time through ARM64 NEON or SSE2. This preserves
stroke-before-transform ordering without CPU rasterization and returns the
original path bounds for zero width, matching system Direct2D. Ordinary and
concave paths, zero width, and nonuniform affine output pass local optimized
and sanitizer tests and a clean Windows ARM64 system differential. Unsupported
styles and topology retain initialized empty output and fail closed.

The closed-figure lane of `ID2D1PathGeometry::Widen` covers a simple contour
with the null/default solid miter stroke and a nonnegative width, including
concave input whose outer and inner offsets remain simple and non-collapsed.
The path is tolerance-flattened locally;
outer and inner offset intersections are fully validated, including miter
limit and surviving inner topology, before either contour touches the caller
sink. Both contours are transformed four points at a time through NEON or SSE2
and emitted with Direct2D's `WINDING` fill callback; the inner contour is
reversed in place, and caller segment flags are never mutated. A dense lattice
compares the widened fill to `StrokeContainsPoint` locally and to a genuine
system-Direct2D widened sink on Windows ARM64. A second concave-path lattice
qualifies the re-entrant join and surviving narrow inner ring. Zero-width path
widening succeeds with one `WINDING` callback and no figures, exactly matching
the system sink transcript. Rectangle zero-width widening retains Direct2D's
two coincident alternate-fill contours, while transformed rectangles publish
their system-compatible winding/no-figure transcript. System and portable
oracles compare fill-mode and segment-flag callback counts as well as geometry.
Self-intersecting or split non-convex offsets and unsupported figure topology
fail closed transactionally.

The compatibility target passes all 15 macOS native CTests and the 10 managed
Direct2D source/ABI contracts. Windows 11 ARM64 and x64 Parallels builds with
MSVC 19.44 explicitly inject `/W4 /WX` and pass the focused compatibility and
semantic suites; the full ARM64 provider build passes 16/16 CTests, including
the D3D12 pixel oracle for alpha-ignore sampling. This behavior slice changes
neither the COM ABI version nor the export allowlist.

WIC codec activation/decoding itself, render-target-to-bitmap copies,
straight-alpha WIC lock sharing as premultiplied content,
portable `ID2D1Factory1` activation,
multi-contour/boolean/widen geometry operations,
color-glyph translation, and
device-context bitmap generations remain fail
closed. `CopyFromMemory` and copied WIC-source ingestion remain explicit
bounded resource uploads; WIC-lock sharing aliases the retained lock memory;
steady drawing never repacks or reads pixels back from the GPU.

### Portable stroke-transform parity (2026-09-05)

The portable compatibility library now implements the `ID2D1StrokeStyle1`
vtable, IID, inherited resource identity, and immutable transform policy.
`compat::create_stroke_style1` constructs it against an existing portable
factory; base `CreateStrokeStyle` produces normal-transform styles. This
typed constructor does **not** claim portable `ID2D1Factory1` activation:
that wider factory/device-context family remains a separate gap.

The portable render target queries this interface and lowers normal, fixed,
and hairline strokes into the existing retained `STROKE_BATCH` and analytic
`GEOMETRY_BATCH` algorithms. Provenance is the original ProGPU Windows recorder
in `src/ProGPU.Native/src/Direct2D/progpu_native_direct2d.cpp` and the shared
`progpu_native_semantic_path_stroke.hpp`; no external implementation is copied.
Curves, custom dashes, caps, joins, geometry gaps, bitmap-brush masks, and
brush ownership continue through their existing paths. Fixed-stroke bounds
expand after the world transform; bitmap masks inverse-map that envelope.
Hairlines ignore the supplied nonnegative width, including zero. Unequal-axis
DPI fails closed, matching the scene-submission lane's existing scalar-DPI
boundary. Singular bitmap-brush transforms also fail explicitly.

Hairline dash intervals and phase are converted from physical units to target
DIPs once while recording. ARM64 NEON and x64 SSE2 multiply two doubles at a
time, with one scalar tail; targets without double-lane intrinsics retain the
scalar portability path. The odd-three-interval fixture checks every value
against its scalar arithmetic oracle. No speedup is claimed.

Two shared correctness gaps surfaced during validation: both portable and
Windows COM recorders now preserve aliased edges in polyline batches, and the
canonical `ProGPU.Backend/Shaders/Vector.wgsl` converts hairline body/cap/join
width to target DIPs before projection. Fixed widths still respect DPI. Both
managed and native renderers consume that same shader, with no ABI, shader
fork, new pipeline, CPU pixel fallback, or per-segment submission.

The existing cross-platform GPU gate additionally renders straight and cubic
normal/fixed/hairline fixtures at 96 and 192 DPI. Each three-stroke fixture
requires three commands and one submission; neighboring-pixel checks prove
world scaling, DPI scaling, and exactly one physical hairline pixel. Its
original 17-draw/27-command/four-submission comparison capture is unchanged.
Managed `SkiaHairlineRenderingTests` additionally checks 96/192/384 DPI.
The COM oracle checks SDK vtable dispatch on Windows and immutable IID
round-trips, policy rejection, custom dash scaling, and both batch payloads
on every native target.

Design references: [Direct2D stroke transforms](https://learn.microsoft.com/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_stroke_transform_type)
and [Win2D stroke-transform behavior](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasStrokeTransformBehavior.htm)
define the world/DPI/physical-pixel distinction; [Skia paint](https://api.skia.org/classSkPaint.html)
provides the independent hairline contract. [Vello scenes](https://docs.rs/vello/latest/vello/struct.Scene.html)
and [WebRender display lists](https://docs.rs/webrender_api/latest/webrender_api/struct.DisplayListBuilder.html)
support keeping strokes in typed retained records rather than immediate
per-segment submissions. [Parley](https://docs.rs/parley/latest/parley/)
and [HarfBuzz](https://harfbuzz.github.io/what-is-harfbuzz.html) remain shaping/
layout references, not stroke rasterizers: font fallback, shaping caches,
startup discovery, glyph uploads, and atlas/device-loss ownership are unchanged.
This is a wiring/correctness extension of the existing architecture, not a
replacement rendering or text algorithm.

Local validation: macOS ARM64 native 15/15, focused ASan/UBSan compatibility
1/1, x64/Rosetta compatibility 1/1, full managed 3,922/3,922, and headless
240/240. The initial Windows ARM64 `/W4 /WX` build passed 16/16, including
D3D12; final-source Windows and hosted PR checks are recorded separately so
earlier passes are not mistaken for final-head qualification.

The follow-up bounds regression also passes the largest finite float as the
ignored curved-hairline width: device-stroke bounds must not first expand a
normal pen and overflow before applying the hairline policy.

## Current support matrix

### What cross-platform COM compatibility means

An application with source access can rebuild its graphics layer against the
installed ProGPU C++ compatibility headers. The resulting objects preserve the
supported Windows COM ABI details—canonical IIDs, `QueryInterface`, atomic
reference counting, interface inheritance, method order, and resource-domain
ownership—but are in-process ProGPU objects off Windows. Their drawing calls
become pointer-free retained scene resources and commands, then execute through
the selected WebGPU backend. Windows selects D3D12; qualified macOS and Linux
builds select Metal and Vulkan respectively. The portable path does not start a
COM server, emulate the Windows registry, or load a Windows system DLL.

This makes source-level clients portable when all graphics calls stay within
the implemented ProGPU subset. It does not make an unchanged PE binary, an
arbitrary `CoCreateInstance` class, HWND/HDC/WIC ownership, a DXGI shared-handle
protocol, or a third-party COM server portable. Those dependencies need an
explicit typed adapter or a Windows-only provider. Unsupported standard-method
slots remain in their original vtable positions and fail closed; ProGPU does
not silently read pixels back or substitute a CPU rasterizer. Applications can
therefore classify each dependency as portable ProGPU, Windows-provider-only,
or unsupported before enabling a non-Windows build.

| Surface | Status | Contract |
| --- | --- | --- |
| `ProGPU.DirectX` D3D-style device/resources/pipelines | Implemented | Portable typed facade backed by WebGPU; D3D12 on qualified Windows adapters |
| Native C++ MIL/retained scene on D3D12 | Implemented | Same backend-neutral scene ABI used on Metal, Vulkan, and browser WebGPU |
| DXGI shared-handle import | Implemented building block | `ProGpuExternalTextureDescriptor` plus Dawn shared-texture memory, keyed-mutex ownership, and no CPU readback |
| Direct2D `ID2D1*` and DirectWrite text API | Portable COM lifetime, ABI-compatible base factory, geometry/resource families, drawing state, mutable solid/linear-gradient/radial-gradient/bitmap brushes, upload/WIC/shared-view premultiplied or alpha-ignore RGBA/BGRA `ID2D1Bitmap`, and a primitive/image/path-fill/path-stroke/glyph-outline/text-layout semantic-scene `ID2D1RenderTarget`; Windows bitmap/brush/geometry/stroke/command-list/effect/layer/state/text/SVG resources, geometry analysis/realization, vector drawing, and typed device-loss domains implemented | The installed portable C++ target exposes canonical factory/resource/geometry/path/stroke/state/brush/bitmap/render-target/WIC-source/WIC-lock/font-face/text-renderer/text-layout IIDs and original vtable order. The portable target records styled line, rectangle, unequal-radius rounded-rectangle, ellipse fill/stroke, arbitrary same-factory `FillGeometry`/`DrawGeometry`, nearest/linear `DrawBitmap`, opacity masks, meshes, compatible targets, layers/clips, bitmap-brush calls, already-shaped `DrawGlyphRun` outlines, `DrawTextLayout` glyph/decorations/inline callbacks, and typed-layout-factory `DrawText` into the shared pointer-free scene stream. Base stroke styles preserve caps, joins, miter, dash style/custom arrays, dash offset, and path segment flags. WIC PBGRA/PRGBA input performs one checked direct row copy; straight BGRA/RGBA input is premultiplied in that final storage through NEON/SSE2 plus a bounded scalar tail, or retained unchanged for an explicit alpha-ignore GPU draw. `CreateSharedBitmap(IID_ID2D1Bitmap)` retains ordinary storage or compatible child scenes and deduplicates GPU upload identity across ordinary views while supporting premultiplied-to-ignore reinterpretation; `CreateSharedBitmap(IID_IWICBitmapLock)` retains and aliases live padded PBGRA/PRGBA lock memory plus explicit alpha-ignore straight BGRA/RGBA locks. Shared allocation-free primitive/affine/stroke validation and portable path algorithms are qualified through real Windows SDK pointers and system-Direct2D geometry/stroke/state/bitmap oracles. The Windows provider independently supplies the broader ABI v54 resource/recorder family and genuine system device/context/target interop. Portable codec activation, DXGI shared bitmap lanes, render-target bitmap copies, straight-to-premultiplied shared-lock conversion, fixed/hairline stroke transforms, color-glyph translation, device-context generations, presentation, and remaining path operations fail closed; there is no fake `d2d1.dll` or `dwrite.dll` |
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
copy. The read-only stream implements bounded `CopyTo` with standard read/write
accounting and `Clone` with an independent seek position over the same borrowed
span, so a platform parser may use the complete `IStream` read contract without
forcing ownership or a second buffer. Surface and command-list transactions draw the document through
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

ABI v41 at implementation `b84845fb` adds the first typed Direct2D
`opacityBrush` layer path. For finite content bounds, genuine solid, linear-
gradient, and radial-gradient brushes are converted through the same validated
color, opacity, interpolation, spread, stop, and coordinate-transform rules as
draw brushes. The result is ProGPU's existing pointer-free brush-mask resource:
the local content rectangle and active draw transform define its coverage, and
the inverse draw/brush mapping evaluates the brush in Direct2D target space.
Only the mapped brush alpha participates, matching Microsoft's definition that
each mapped brush pixel's alpha multiplies the corresponding layer pixel.

Brush-mask generation and group composition stay entirely on the shared GPU
path. ProGPU rasterizes the solid/gradient rectangle into retained R8 coverage
and consumes it through the same layer-mask binding on D3D12, Metal, Vulkan,
and WebGPU. No CPU pixels, readback, repack, per-stop submission, retained COM
pointer, or second 2D renderer is introduced. At ABI v41, full-target
opacity-brush layers and combined geometric/brush masks still failed closed;
ABI v42 added the combined-mask resource described below.

The Windows oracle records a finite transformed layer with the provider's real
two-stop `ID2D1LinearGradientBrush`. It decodes exact target layer bounds,
local brush-mask bounds, active mask transform, two retained stops, 75% brush
opacity, and the inverse draw/brush coordinate matrix. Managed AOT contracts
pass 5/5 and build with zero warnings. Windows 11 ARM64 Parallels rebuilds the
provider and test from deleted objects under MSVC 19.44/SDK 10.0.26100.0
`/W4 /WX`; the fresh executable exits zero. The 176,640-byte provider SHA-256
is `50FD9745C40EE045B53F06D1CD089B48F20BABC502D48DB014BAD795A3466C7F`,
and all 123 exports match the allowlist.

ABI v42 at implementation `f56ebe75` removes the finite-layer restriction that
only one mask kind may be present. The reusable scene builder added at
`1ce62657` validates and serializes canonical composite-mask resources. A
Direct2D layer carrying both `geometricMask` and `opacityBrush` now places the
brush mask, exact vector path, segments, and shared gradient stops in that
pointer-free resource. The backend independently rasterizes both children to
R8 coverage and multiplies them through `ClipCompose.wgsl` before applying
uniform group opacity.

The Direct2D oracle records a genuine transformed line/cubic geometry and a
real two-stop linear gradient on the same finite layer. It requires both typed
feature flags plus the composite flag, exact content/mask bounds intersection,
two components, one brush, one path, three segments, two stops, and no geometry
primitive or picture child. The managed AOT build is warning-free and focused
contracts pass 5/5. After the Windows VM's existing restart restored Guest
Tools, the exact archive SHA-256
`E01D2B571D8C11CCC41A3639DEBE5C4DB4B08CE571A60B0C4EE4802F80DEFBAC`
was extracted and confirmed as ABI v42. Windows 11 ARM64 Parallels then rebuilt
the provider and test cleanly under MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`;
the native executable exits zero. The 181,248-byte provider SHA-256 is
`D20084AFFC6C8FE39C2F10EBBBA565BB8CA0D6C0771B595A33C5527135F09698`.

The current portable C++ target also admits an infinite-content opacity-brush
layer when the active world transform is finite, invertible, and axis
preserving. It inverse-maps the finite visible target rectangle into local
space once at `PushLayer`, then reuses the existing solid/linear/radial GPU
brush-mask resource. Scale, translation, reflection, and nonuniform DPI remain
exact; rotation, shear, and singular transforms fail closed rather than
broadening coverage. The retained layer remains full-target while its mask is
finite, so no CPU rasterization, readback, or extra submission is introduced.
Portable serialization checks the inverse-mapped bounds under scale and
translation, the Windows build calls the implementation through the genuine
`ID2D1RenderTarget::PushLayer` vtable, and the Metal/D3D12/Vulkan fixture probes
the final composited pixel.

ABI v43 adds the first public ProGPU-implemented `ID2D1*` COM object. A native
caller creates a `progpu_native_direct2d_scene_recorder`, acquires a
caller-owned `ID2D1CommandSink1*`, calls `BeginDraw`, supported state/drawing
callbacks, and `EndDraw`, then performs the existing required-size/write
serialization passes. The recorder retains its own COM reference, exposes
canonical `IUnknown`/`ID2D1CommandSink`/`ID2D1CommandSink1` identity, and never
places a COM pointer in the scene. A capacity summary is only a reserve hint;
the semantic-scene limits remain authoritative and incomplete or unsupported
recordings fail closed with the existing typed reason and HRESULT.

This is the foundation for a broader ProGPU Direct2D COM facade. The next
interfaces will be added by useful dependency slices—factory plus immutable
geometry, device/context plus brushes/bitmaps, then command lists/effects—while
all drawing continues to lower into the shared scene renderer. ProGPU will not
shadow the Windows `D2D1CreateFactory` export or load as `d2d1.dll`; callers
must explicitly request the compatibility facade, so system Direct2D and
ProGPU identities cannot be confused accidentally. The direct-COM oracle
records clear plus fill callbacks without first creating a system command
list, verifies canonical COM identity, rejects serialization before
`EndDraw`, and decodes scene id, generation, command, brush, and clear data.
The exact source archive SHA-256 is
`93F348B9C81F8D8211D24D9D0D145F620DD2EFBF9930D009B3826A8E46B4B05C`.
Windows 11 ARM64 Parallels rebuilds the provider/test cleanly with MSVC
19.44/SDK 10.0.26100.0 `/W4 /WX`; the native oracle exits zero. `dumpbin`
matches all 127 allowlisted exports exactly. The 183,296-byte provider
SHA-256 is
`A6B2D9CFA4222846D91081F793BB3D6BAFC1F8C93854933DDD528BFE988D2533`,
and the test executable SHA-256 is
`08A3E37727EA14A579D6333E3E20914D15DE17F4F016AE10E6EC368F330A474D`.

ABI v44 adds the first explicit ProGPU-owned Direct2D factory/resource slice.
`progpu_native_direct2d_compat_factory_create` returns a caller-owned genuine
`ID2D1Factory1*` with canonical `IUnknown`/`ID2D1Factory` identity and a
factory-owned `ID2D1Multithread` view. `CreateRectangleGeometry` returns an
immutable `ID2D1RectangleGeometry` that owns its factory reference and supports
rectangle retrieval, transformed bounds, fill containment, simplification,
tessellation, area, length, and point-at-length queries. Resource families not
yet implemented return `E_NOTIMPL` and clear output pointers.

The same ProGPU rectangle object can be passed directly to the ABI v43
`ID2D1CommandSink1::FillGeometry` recorder. Its standard Direct2D `Simplify`
and `GetBounds` callbacks produce the existing pointer-free vector-path scene,
so this adds no backend-specific renderer and retains no COM pointer after
serialization. The native oracle covers COM identity and ownership, factory
locking, geometry queries, unsupported-family rejection, and rectangle-to-path
recording. Focused managed contracts pass 5/5. The exact implementation
checkpoint is `123d2371`; its committed source archive SHA-256 is
`7F903F5B62FBA969359F8363E4E7C11495F9F76730CDBCADEAE4EA3AE021071A`.
Windows 11 ARM64 Parallels rebuilds it cleanly with MSVC 19.44/SDK
10.0.26100.0 `/W4 /WX`, and the native oracle exits zero. `dumpbin` matches all
128 allowlisted exports exactly. The 191,488-byte provider SHA-256 is
`3D90668C81E5113EF5A3C1B86EC13CC5B4B6E09B2C070F753CF5276AE8BCB033`;
the 111,104-byte test executable SHA-256 is
`7910843D99080398B21DDD8F383FBEBBCB99E662B76338800C97034844B4C722`.

ABI v45 adds a ProGPU-owned mutable `ID2D1SolidColorBrush` to the compatibility
factory domain. The explicit creation API accepts finite HDR color values plus
optional opacity/transform properties, returns a caller-owned standard COM
brush identity, and preserves canonical `IUnknown`/`ID2D1Resource`/
`ID2D1Brush`/`ID2D1SolidColorBrush` queries and factory ownership. Standard
color, opacity, and transform getters/setters are synchronized; invalid
void-returning mutations retain the last valid state, while invalid creation
fails closed with typed status and HRESULT data.

The direct-COM oracle now uses only ProGPU-owned factory, rectangle, solid
brush, and command sink objects for its two draws. The recorder consumes the
standard brush vtable and still emits one pointer-free semantic brush shared by
the analytic rectangle and vector-path commands on every backend. Focused
managed contracts pass 5/5. The exact implementation checkpoint is `73b6ff5e`;
its committed source archive SHA-256 is
`59A755509F2E3FF32B8A4C5FE5C32CB7C8752C10B2A02F84276393D2FC157DDA`.
Windows 11 ARM64 Parallels rebuilds it cleanly with MSVC 19.44/SDK
10.0.26100.0 `/W4 /WX`, and the native oracle exits zero. `dumpbin` matches all
129 allowlisted exports exactly. The 195,584-byte provider SHA-256 is
`4126FB918B4A577BB728BF1E0B27E35E388185841223BBAD4044FD80DEE836ED`;
the 113,664-byte test executable SHA-256 is
`5B6EC4E52D17BB185A3E513A22628CC9BF93AE98AF28AFFD90F2FC448DFEB45C`.

ABI v46 adds ProGPU-owned `ID2D1PathGeometry1` and `ID2D1GeometrySink`
objects to both standard `ID2D1Factory::CreatePathGeometry` vtables. The sink
records line, cubic, quadratic, and arc segments, fill mode, per-segment flags,
filled/hollow figures, and open/closed figure state. `Close` publishes one
immutable path snapshot; invalid sequencing, invalid data, abandoned open
sinks, and a second open/close attempt fail closed. Public segment indexing
matches Direct2D by counting each implicit closed-figure edge even though
`Stream` preserves the standard `EndFigure(CLOSED)` representation.

The path supports canonical resource/geometry/path/path1 identity and factory
ownership, exact vocabulary `Stream`, line/cubic/arc-aware transformed bounds,
`Simplify` to cubics-and-lines or flattened lines, fill containment, area,
length, point-at-length, and point-plus-segment queries. Fill containment
applies the selected rule across all flattened figures, while area is qualified
for independent, nested, point-touching, shared-edge, and two-contour overlap
through normalized Outline topology. A single proper transverse
self-intersection is also qualified, as are arbitrary counts of interacting
simple contours and qualified distinct proper self-crossings; repeated/triple
or contact-ambiguous self-crossings remain a separate gate.
Dashed, open,
or multi-figure path stroke containment, collapsed/styled/open/
multi-figure path widening, and styled/open/multi-figure widened bounds,
multi-contour outline/Boolean normalization, and unsupported tessellation
topologies still return `E_NOTIMPL` with initialized outputs where applicable.
Single-contour outline, comparison, and Boolean combination have qualified
native lanes.
Unsupported operations must not silently broaden, rasterize on the CPU, or
delegate to a second renderer.

The direct COM recorder oracle now fills the ProGPU path instead of the
rectangle and performs a Windows differential against a genuine system
`ID2D1PathGeometry1` for segment/figure counts, bounds, and flattened length.
The same standard COM callbacks serialize into the existing pointer-free
ProGPU vector scene; no new C export or backend-specific scene path is needed,
so the allowlist remains 129 exports. Focused managed contracts pass 5/5.
The exact implementation checkpoint is `3f42538c`; its committed source
archive SHA-256 is
`32A3ECA03C6C721B505D40A6638A7D55E139C6132E65C296DFFFBD4D2A633EC3`.
Windows 11 ARM64 Parallels rebuilds it cleanly with MSVC 19.44/SDK
10.0.26100.0 `/W4 /WX`, and the native differential oracle exits zero.
`dumpbin` matches all 129 allowlisted exports exactly. The 225,280-byte
provider SHA-256 is
`681EC3239D4B235BDD0E024A9D3C1DCD5D0444F8F1ACD3CB6FE31F0DC8A6940B`;
the 118,272-byte test executable SHA-256 is
`1845C2C96B3B8AA0DA46D909384AB3D417AB607205EAB921C84AC626FB084586`.

ABI v47 adds immutable ProGPU-owned `ID2D1StrokeStyle1` resources to both
standard `ID2D1Factory::CreateStrokeStyle` vtables. Base creation publishes
the Direct2D normal transform policy, while Factory1 creation preserves normal,
fixed, and hairline transform types. Canonical resource/stroke/stroke1 COM
identity, factory ownership, caps, join, miter limit, dash kind/offset, and
caller-provided custom dash intervals are retained without a system Direct2D
resource. Invalid enums, non-finite metadata, invalid miter limits, malformed
custom-dash ownership, negative intervals, and all-zero custom patterns fail
closed.

The Windows native oracle compares every exposed property and the custom dash
array with a genuine system `ID2D1StrokeStyle1`. This slice is deliberately the
resource prerequisite for direct path stroking: the next recorder step maps it
to ProGPU's existing pointer-free retained stroke batch, which already owns
caps, joins, miters, dashes, and transform policy on D3D12, Metal, Vulkan, and
WebGPU. It does not introduce COM-layer CPU widening or another renderer.
Focused managed contracts pass 5/5. The exact implementation checkpoint is
`71118006`; its committed source archive SHA-256 is
`FF58C3EF89AADB24AA5E1A88416F399F75CCD1D9DB559180333B274441AAF999`.
Windows 11 ARM64 Parallels rebuilds it with MSVC 19.44/SDK 10.0.26100.0
`/W4 /WX`, and the native differential oracle exits zero. `dumpbin` matches
all 129 allowlisted exports with zero differences. The 228,864-byte provider
SHA-256 is
`D259FFBF25B8F9B2950A1DBE876901175D4EC31E7BFBE665324678BAEE68E095`;
the 120,320-byte test executable SHA-256 is
`E2C71C12741DB7A71C01EBCE510664BBE20E693131C21DA4DCCC2C1ACAF54CAE`.

ABI v48 routes compatible `ID2D1CommandSink1::DrawGeometry` callbacks into
ProGPU's existing pointer-free `STROKE_BATCH` scene resource. A bounded
`ID2D1SimplifiedGeometrySink` capture preserves figure boundaries, open/closed
topology, normal/fixed/hairline transform policy, caps, joins, miter limit,
dash cap/offset, predefined dash patterns, and copied custom dash intervals.
Flattening tolerance tightens with the active transform, and the scene retains
one brush index per figure without COM pointers or CPU pixel data.

This slice intentionally accepts only paths whose simplified line vocabulary
does not require per-segment `FORCE_UNSTROKED` or
`FORCE_ROUND_LINE_JOIN` semantics. Genuine system Direct2D curved paths that
publish those hints continue through the qualified Windows `Widen` path;
ProGPU-owned resources for which no exact fallback exists fail closed. The
next scene-format gate must carry exact per-segment join/stroke metadata before
that fallback can be removed. The direct COM oracle proves a ProGPU-owned
closed rectangle plus fixed custom style becomes a retained stroke batch,
while the existing system cubic oracle continues to verify the hinted Windows
widening path.

Focused managed contracts pass 5/5. The exact implementation checkpoint is
`2d7809f9`; its committed source archive SHA-256 is
`D010D1EF377FE30D47FCA9411EC1921BDC20A04F69637B53B2DDB53FD25E5F8F`.
Windows 11 ARM64 Parallels rebuilds it with MSVC 19.44/SDK 10.0.26100.0
`/W4 /WX`, and the full native oracle exits zero. `dumpbin` matches all 129
allowlisted exports with zero differences. The 243,712-byte provider SHA-256
is `ECC61FFBA903F53532094CD5A7492CA1F9DEC828CB1C91BE08EB0241FB020587`;
the 121,856-byte test executable SHA-256 is
`21C542CEFF8805DB694A4D449891486F2A4F094BF4A0BD428ABF8F9063B3C23D`.

ABI v49 removes the normal-transform curved-path limitation without expanding
the pointer-free scene ABI. `CommandSceneStrokeSink` requests Direct2D's
`CUBICS_AND_LINES` vocabulary and retains line/cubic control points plus the
active `D2D1_PATH_SEGMENT` flags. `FORCE_UNSTROKED` edges split a figure into
open stroked runs whose artificial endpoints use the dash cap.
`FORCE_ROUND_LINE_JOIN` is shifted from Direct2D's incoming-segment convention
to ProGPU's outgoing-edge convention: an ordinary join reads the next
segment's flag, a closed first segment controls the closing-to-first seam, and
flags set immediately before `EndFigure` control the last-explicit-to-closing
join. These are the semantics published by
[`D2D1_PATH_SEGMENT`](https://learn.microsoft.com/windows/win32/api/d2d1/ne-d2d1-d2d1_path_segment).

Uniform line runs still serialize as one `STROKE_BATCH`, including qualified
normal/fixed/hairline styles. Curves or forced per-segment joins use the shared
`progpu_native_semantic_path_stroke.hpp` compiler. It emits retained analytic
line/cubic, path-cap, and path-join primitives and reuses MIL's bounded curve
dash splitter, which performs distance-to-parameter approximation but keeps
the emitted curve spans native. The compiler validates transform/style flags,
rolls back partial output on failure, and is covered by the portable native MIL
test executable. No COM pointer, widened outline, CPU pixel buffer, readback,
or per-item GPU submission enters the scene.

Normal-transform curved paths are portable through this lane. Fixed or
hairline curves remain gated until their device-space dash metric is qualified:
genuine Windows geometries retain the existing system `Widen` fallback and a
ProGPU-owned geometry without that fallback fails closed. The native oracle
proves a ProGPU-owned mixed path produces a `GEOMETRY_BATCH` containing both a
cubic primitive and the forced round join, while retaining the ABI v48 fixed
custom rectangle `STROKE_BATCH` and system fixed-curve fallback checks.

Focused managed contracts pass 5/5 and the portable native MIL oracle exits
zero on macOS. The exact implementation checkpoint is `aecb6883`; its
committed source archive SHA-256 is
`CDE728391DE0F7EE8F9E504BEE215B4E1B6D6C7A81701864FE3516B07700D51C`.
Windows 11 ARM64 build `10.0.26200.9168` in Parallels 26.4.1 independently
extracts and builds the exact 1,731,087-byte native qualification archive from
that checkpoint; its SHA-256 is
`ED54C0F280595EC92B2D182C3E7AC02E49A494F5D969257DD62D9B3ED0B162F1`.
MSVC 19.44.35228.0 compiles the provider and oracle with `/W4 /WX`, and the
oracle exits zero. The 265,216-byte `progpu_native_direct2d.dll` SHA-256 is
`FDA4E04F94D3DA60C6C8574C6D8196ADCB16ACF654DD6DC1A8AF2342017BAFC9`;
the 122,880-byte test executable SHA-256 is
`3D285A96AA096967ACB5E4A6AA1DCD46B1D040CA6603AFC54804360707B6A7DA`.
`dumpbin /exports` reports exactly the expected 129-symbol provider surface.

ABI v50 removes the remaining fixed/hairline curved-stroke gate. The shared
semantic compiler keeps the original analytic local curve records, but its
bounded dash length table measures fixed and hairline paths through the world
transform's linear component. Translation is excluded from the metric because
it cannot affect distance and can only reduce floating-point precision. Normal
strokes retain local-space distance; fixed strokes retain the requested width
with device-space dash placement; hairlines use a one-device-unit dash scale,
store zero scene thickness, and ignore the supplied width. This follows
Direct2D's documented
[`D2D1_STROKE_TRANSFORM_TYPE`](https://learn.microsoft.com/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_stroke_transform_type)
and
[`D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE`](https://learn.microsoft.com/windows/win32/direct2d/d1139)
behavior.

Fixed and hairline flags are mutually exclusive, invalid zero-width normal or
fixed strokes remain empty, and unsupported forced combinations fail closed.
Uniform line figures retain the existing `STROKE_BATCH` fast path. Curves,
forced joins, and geometry gaps retain analytic primitives in one backend-
neutral `GEOMETRY_BATCH`; the compatibility layer performs no CPU widening,
readback, pixel repacking, or per-segment submission. The portable MIL oracle
differentiates normal, fixed, and hairline dash ends under one non-uniform
transform. The genuine Windows Direct2D oracle records all three policies and
requires the corresponding distinct flags, thickness, and cubic bodies.
Focused managed ABI contracts pass 5/5 and the native MIL oracle exits zero on
macOS.

The exact ABI v50 implementation archive for `0e3906bc` was also rebuilt in
Windows 11 ARM64 Parallels with MSVC 19.44.35228.0. Its 1,732,223-byte archive
has SHA-256
`1CBC6FF1998D2DEB84279E051FF84E82D695C47EC47AD471F4E0EFB89F63D946`.
Compilation and linking completed, but the native oracle exited 1 at
`ProGPU Direct2D COM recorder write pass changed`; this is retained as negative
qualification evidence, not a Windows pass. The resulting 266,240-byte DLL is
`7130146F32B31597C5B716B6569D285EAD4A9D750F6911AD8C0A8F2847094157`
and the 122,880-byte test executable is
`7A39E6AB242413EAF9704FC43C4FCCCF8DE1C507CCCE08BDB7C59B8AE282FF70`.
ABI v52 adds field-level recorder diagnostics so the next Windows run reports
status, HRESULT, written/capacity bytes, command count, and brush count.
That diagnostic subsequently reported successful status/HRESULT, exact
`17,936/17,936` byte production, eight commands, and one brush. The old oracle
incorrectly expected two entries even though the scene builder canonically
deduplicates the unchanged solid brush across draw transforms. ABI v52 now
requires the correct single-brush result; the archived ABI-v50 execution
remains a non-passing run, but its failure is classified as an oracle false
negative rather than provider serialization corruption.
The subsequently exposed command-list assertion was also stale: curved strokes
have intentionally used analytic `GEOMETRY_BATCH` resources since ABI v50, but
the older oracle still demanded a second widened `PATH_BATCH`. ABI v52 now
requires one fill path and one analytic stroke resource, with detailed cubic,
join, dash, fixed-device, and hairline validation retained by the recorder
tests.

ABI v51 extends the ProGPU-owned factory with a genuine
[`ID2D1EllipseGeometry`](https://learn.microsoft.com/windows/win32/api/d2d1/nn-d2d1-id2d1ellipsegeometry)
implementation. The immutable, device-independent COM object follows
Direct2D's documented
[`geometry resource model`](https://learn.microsoft.com/windows/win32/direct2d/direct2d-geometries-overview), preserves
`IUnknown`/`ID2D1Resource`/`ID2D1Geometry` identity, retains the creating
factory, exposes the original `D2D1_ELLIPSE`, and rejects non-finite or negative
radii. Affine bounds use the exact ellipse support function and nonsingular
fill containment maps the test point through the inverse transform. Area,
length, and point-at-length queries reuse the retained cubic path so the
caller's flattening tolerance has the same observable role as system Direct2D.
Ellipse delegation uses Direct2D's observed half-tolerance subdivision
threshold for length and point-at-length: at the default `0.25` tolerance the
Windows oracle reports `19.2537` for the `4 x 2` ellipse perimeter, matching
the retained ProGPU cubic at an effective threshold of `0.125` instead of the
previous `19.1810`. Arbitrary path and rounded-rectangle geometry consume the
caller's tolerance unchanged; the rounded oracle reports `35.6731`, compared
with `35.7652` when the ellipse-only compensation was incorrectly applied.

The geometry owns one closed four-cubic path created at factory time, including
the three zero-length endpoint lines that system Direct2D publishes through
`Simplify(CUBICS_AND_LINES)`. Shared path code therefore supplies
simplification, tolerance-controlled metrics, and
pointer-free fill/stroke scene translation without a per-frame adapter,
reflection, CPU pixel work, or a renderer-specific ellipse sideband. Zero-radius
degenerates use the same fail-closed path semantics. Constant-size construction
is deliberately scalar; it records exactly four cubic segments plus the three
system-observed degenerate endpoint lines and is not a data-parallel buffer
workload. Focused managed ABI contracts pass 5/5. Exact
Windows COM/native qualification passes as part of the ABI-v52 MSVC run below.

ABI v52 adds a genuine ProGPU-owned
[`ID2D1RoundedRectangleGeometry`](https://learn.microsoft.com/windows/win32/api/d2d1/nn-d2d1-id2d1roundedrectanglegeometry).
The immutable resource preserves the caller's `D2D1_ROUNDED_RECT`, COM
identity, and factory parentage. Its retained construction clamps each internal
corner radius to half the corresponding rectangle dimension, matching the
documented quarter-ellipse model, while `GetRoundedRect` continues to return
the original descriptor. Invalid rectangles and non-finite or negative radii
fail closed.

One closed path containing four straight edges and four cubic quarter-ellipse
corners is built at factory time. Bounds, containment, simplification,
tolerance-controlled area/length queries, and command-sink fills reuse the
shared ProGPU path implementation. The hot replay path therefore remains
reflection-free and allocation-free with respect to geometry construction and
does not introduce CPU pixel readback, per-frame path rebuilding, or
backend-specific Direct2D branches. Construction is fixed eight-segment scalar
work rather than a SIMD-eligible bulk loop. The Windows oracle compares bounds,
area, length, containment, COM identity, and semantic scene translation with
system Direct2D.

Hosted Windows qualification passes in the
[`Native C++20 compiler compatibility (MSVC)` job](https://github.com/wieslawsoltes/ProGPU/actions/runs/33417514376/job/99571802634)
for implementation `e5a75a9b`: MSVC builds the provider and all native tests,
and all 11 CTests pass, including the complete ABI-v52 Direct2D COM/system
differential. This qualifies ellipse and rounded-rectangle identity, factory
parentage, metadata, bounds, containment, area, length, semantic fill replay,
analytic curved strokes, recorder serialization, and resource canonicalization
on Windows x64. ClangCL x64/ARM64 remain separately blocked by the three
pre-existing missing-braces warning-as-error sites; ABI v52 added no new
ClangCL diagnostic.

ABI v53 adds a genuine ProGPU-owned
[`ID2D1TransformedGeometry`](https://learn.microsoft.com/windows/win32/api/d2d1/nn-d2d1-id2d1transformedgeometry).
The immutable object retains its source geometry and creating factory, exposes
the original affine transform and source with caller-owned references, and
preserves canonical `IUnknown`/resource/geometry/transformed-geometry
identity. Creation rejects null or non-finite matrices and source geometries
from a different factory with `D2DERR_WRONG_FACTORY`.

Every supported single-geometry operation composes the stored matrix before
the caller's optional world matrix (`stored * world` in Direct2D row-vector
order) and delegates to the retained source. Composition uses double-width
intermediates and fails closed on a non-finite or out-of-float-range result.
Bounds, fill/stroke containment, simplification, tessellation, outline,
area/length/point queries, widening, nested transformed geometries, and normal
semantic-scene fill lowering therefore reuse the source implementation without
copying or rebuilding a path. Rectangle relations and Boolean combinations
also preserve the stored source transform while independently applying the
caller transform to the input operand. Coincident affine boundaries are split
and classified without discarding either transform. General path operands
remain fail closed; no transform is silently ignored.

The native oracle checks source/factory lifetime and COM identity, exact
metadata, bounds, containment, area, length, point-at-length, simplified path
topology, invalid creation, semantic recorder lowering, and a non-commuting
stored-plus-world transform against system Direct2D. Focused managed ABI
contracts pass 5/5. The exact `998c9ec2` checkpoint passes the hosted
[`Native C++20 compiler compatibility (MSVC)` job](https://github.com/wieslawsoltes/ProGPU/actions/runs/33420113029/job/99580305821):
the complete provider and oracle compile with MSVC, the transformed-geometry
system differential passes, and all 11 native CTests pass. This qualifies ABI
v53 on Windows x64.

ABI v54 adds a genuine ProGPU-owned
[`ID2D1GeometryGroup`](https://learn.microsoft.com/windows/win32/api/d2d1/nn-d2d1-id2d1geometrygroup).
It retains its ordered source geometries and factory, preserves canonical
resource/geometry/group COM identity, returns caller-owned source references,
and exposes the original alternate or winding fill mode. Null entries,
invalid fill modes, and cross-factory sources fail closed.

The factory simplifies every immutable source once into one retained
multi-figure path. A typed forwarding sink filters each child's attempted
`SetFillMode` call, because Direct2D geometry sinks accept the authoritative
mode only before the first figure. A transformed child therefore contributes
its already qualified composed path, while later analysis and `FillGeometry`
recording reuse one pointer-free scene resource without per-frame child
traversal, CPU readback, or pixel repacking.
Nested groups are accepted through both the portable factory and the native
Windows provider. Each already-immutable child group publishes its retained
multi-figure path to the outer forwarding sink, which preserves all contour
orientations while applying the one authoritative outer fill mode. A bounded
transformed-source walk still rejects an excessively deep chain before path
construction. The Windows oracle compares nested winding-over-alternate
containment, bounds, and simplified topology with system Direct2D.

The native oracle covers ordered source identity, factory/COM identity,
metadata, two independently positioned children, bounds, containment, area,
length, simplified topology, malformed and incompatible creation, semantic
fill lowering, and a system-Direct2D world-transform differential. Focused
managed ABI contracts pass 5/5.

The first Windows attempt at `0e93f94e` compiled and linked but failed the
Direct2D CTest at group creation. It proved that child `Simplify` calls were
trying to change the underlying sink fill mode after its first figure. The
typed forwarding sink in `ada83ef7` makes the group mode authoritative without
changing child geometry or adding a renderer fallback. That exact corrected
checkpoint passes the hosted
[`Native C++20 compiler compatibility (MSVC)` job](https://github.com/wieslawsoltes/ProGPU/actions/runs/33422845973/job/99589327621):
the provider and oracle compile, the geometry-group system differential and
semantic recorder pass, and all 11 native CTests pass. This qualifies ABI v54
on Windows x64.

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

Microsoft Basic Render Driver keeps the complete Canvas frame and the same
cross-backend differential, but partitions independent feature groups with
`CanvasDrawingSession.Flush()` so its CPU-D3D12 backend does not receive one
oversized path/text/layer batch. All original draw calls and pixel probes stay
live under automatic GPU-first selection; no intermediate readback, repacking,
or CPU composition occurs. The split uncovered a real incremental Canvas
defect: preserved commits containing isolated layers cleared earlier target
contents. The managed backend now requests typed full-target preservation and
the native isolated-layer root pass uses `WGPULoadOp_Load`. A partitioned
Metal run reproduces the exact qualified
`D72F667FCB6AC14B2C28A1C45001734C3B62B85B1816069521C9019985D1B39B`
hash with 17+2 native draws after submission boundaries.

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

### Portable COM render-target differential checkpoint

ProGPU `b2b4e31d` adds a Windows-only hardware differential executable for the
portable C++ `ID2D1*` compatibility target. The test records the same clear,
solid rectangle, ellipse, stroked rectangle, and rounded rectangle through the
portable COM vtables, lowers the retained scene once through
`render_scene_target(...)`, and renders it with the current Dawn D3D12 backend.
It renders the reference fixture independently through the system
`D2D1CreateFactory` and a WIC bitmap render target, then compares five semantic
probes and the complete premultiplied BGRA image. This is a behavioral oracle,
not a shared implementation.

The Windows 11 ARM64 Parallels gate builds the full current native renderer and
test with MSVC 19.44 under `/W4 /WX`. On the Parallels Display Adapter D3D12
backend the 64x48 comparison passes with mean absolute error `0.2727` byte
values across 12,288 BGRA bytes. Dawn mapping uses a `WaitAnyOnly` future and
the explicitly requested `TimedWaitAny` instance feature, so test completion
does not depend on spontaneous callback delivery by a particular driver or VM.

Configure the live CTest by supplying both the matching Dawn headers and the
runtime DLL:

```powershell
cmake -S src/ProGPU.Native -B artifacts/progpu-native/build `
  -G Ninja `
  -DPROGPU_NATIVE_BUILD_WGPU_TARGET=OFF `
  -DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR=C:\path\to\dawn\include `
  -DPROGPU_NATIVE_DIRECT2D_DAWN_RUNTIME=C:\path\to\webgpu_dawn.dll `
  -DBUILD_TESTING=ON
cmake --build artifacts/progpu-native/build `
  --target progpu_native_direct2d_differential_tests
ctest --test-dir artifacts/progpu-native/build --output-on-failure `
  -R '^progpu_native_direct2d_differential_tests$'
```

ProGPU `b22ed672` turns the same fixture into a directly linked, backend-neutral
CTest. It requests only D3D12 on Windows, Metal on macOS, or Vulkan on Linux,
records through the portable COM target, renders through the shared C++ engine,
requires four retained draws and one renderer submission, reads the test image
back only after rendering, and publishes one PPM per platform. The normal
native build jobs upload those frames and a separate aggregate job compares
them; probe-only success cannot conceal a whole-image regression.

The exact Windows 11 ARM64 Parallels D3D12 and Apple M3 Pro Metal captures are
byte-identical, with SHA-256
`f71fc0daeb6f9e9dcb9326b45c4988220befe6981e486035d6075c859c71fa9a`.
Ubuntu 24.04 ARM64 llvmpipe LLVM 20.1.2/Vulkan produces SHA-256
`b0a36a8a7c49e4fbc6ee3f7d4addb998fa2a47355a7532f000c70f8c81095599`.
All five clear/interior/stroke probes are exact. Vulkan changes 140 of 3,072
pixels, every changed channel is exactly 1/255, no channel exceeds that bound,
and mean absolute channel difference is `0.0247395833`. The aggregate contract
therefore caps the full fixture at 160 changed pixels, 1/255 maximum channel
difference, zero pixels above 1/255, and mean `0.03`; displaced geometry, color
drift, missing primitives, and CPU substitutes fail the gate. This is
software-Vulkan correctness evidence, not physical Linux GPU performance.

ProGPU `d5cb1f71` advances the portable target from solid-only brushes to the
standard Direct2D gradient dependency chain. The installed header now exposes
the canonical `ID2D1GradientStopCollection`, `ID2D1LinearGradientBrush`, and
`ID2D1RadialGradientBrush` IIDs, layouts, inheritance, and vtable order.
Collections own immutable validated stops plus gamma/extend state; brushes own
their collection and factory, preserve default opacity/identity transform,
serialize mutation, and reject cross-factory creation. Clamp, wrap, and mirror
map to the existing retained gradient spread modes. Gamma 2.2 and 1.0 map to
the shared sRGB and linear-light shader interpolation paths. Brush and active
target transforms are inverted and composed into device-to-brush coordinates,
so recording stays a single retained scene submission with no pixel readback,
CPU raster fallback, or per-primitive GPU work.

The cross-backend fixture now uses a linear-gradient rectangle and a
radial-gradient ellipse alongside the solid stroke and rounded rectangle. Its
Windows SDK test invokes the portable objects through actual
`ID2D1GradientStopCollection*`, `ID2D1LinearGradientBrush*`,
`ID2D1RadialGradientBrush*`, and `ID2D1RenderTarget*` pointers. Windows 11
ARM64 MSVC 19.44 `/W4 /WX`, Apple M3 Pro Metal, and Ubuntu 24.04 ARM64 GCC 13
plus llvmpipe/Vulkan all pass. D3D12 and Metal are byte-identical at SHA-256
`9faf2dfb22a05fa758f9428ab50e94d76b6fac9425c3928226c9b267d1e9b2f7`.
Vulkan SHA-256 is
`8e410ae092922ff59a76b3aee24d76a7c3955b969585b99cfcb553872ba518ab`;
all six semantic probes are exact, 140 of 3,072 edge pixels differ by exactly
1/255 at most, no pixel exceeds that bound, and the mean channel difference is
`0.0256076389`. Native Direct2D/WIC versus ProGPU D3D12 passes at mean byte
error `0.3576` across 12,288 BGRA bytes.

ProGPU `f5453920` extends that live fixture with a repeated nearest-sampled
BGRA `ID2D1BitmapBrush` rectangle and a bitmap-brush stroked ellipse. The exact
COM tests cover default and mutable state, base-brush queries, source identity,
invalid enum preservation, wrong-factory rejection, SDK vtable calls, and the
serialized image/mask/state dependency chain. The renderer generates coverage
on the GPU and encodes the mask pass in the same semantic command buffer as the
image draw; seven Direct2D draws therefore remain one queue submission.

Apple M3 Pro Metal and Windows 11 ARM64 Parallels D3D12 are byte-identical at
SHA-256
`08fba84c33ac65590568e8e5209b47c5295eb7a642ad99c05b885f5a9f6b7495`.
Ubuntu 24.04 ARM64 GCC plus llvmpipe LLVM 20.1.2/Vulkan passes the same live
fixture at SHA-256
`90459b346415605e57bc828b433b7e64e2ad452fde59985c937a10821d570ad4`:
all 13 probes are exact, 149 edge pixels differ by at most 1/255, no pixel
exceeds that bound, and mean channel difference is
`0.026801215277777776`. Windows MSVC 19.44 `/W4 /WX` passes the portable COM
suite through real SDK `ID2D1BitmapBrush*` pointers, and the independent native
Direct2D/WIC versus ProGPU D3D12 comparison passes at mean byte error `0.5718`.
Arbitrary geometry, clip/layer, opacity-mask, text, and device-context brush
coverage remain explicit follow-up gates.

The Linux GCC 13 warning-as-error build also found an enum/unsigned conditional
in portable antialias flag selection. The implementation now returns the
explicit fixed-width flag value and passes GCC, AppleClang, and Windows ARM64
MSVC 19.44 `/W4 /WX`. Broader bitmap, arbitrary geometry, clip/layer,
text, effect, and device-context families remain explicit parity work. Until
those interfaces are implemented and differentially qualified, an application
using them must select the typed scene/Canvas alternative or a Windows
provider; the portable COM target returns its documented failure instead of
silently rasterizing on the CPU.

### Shared Windows rectangle-query checkpoint

The exported Windows `progpu_native_direct2d_compat_factory_create` facade now
routes `ID2D1RectangleGeometry::GetWidenedBounds`, `StrokeContainsPoint`,
`CompareWithGeometry`, `CombineWithGeometry`, `Outline`, and `Widen` through
the same typed rectangle-query implementation as the portable COM factory.
This removes the remaining facade-only geometry-operation `E_NOTIMPL` results
without duplicating geometry math or adding a CPU raster path. The null/default
stroke lane preserves the qualified alternate-fill outer/inner transcript;
same-factory solid styles are admitted by widened bounds, while unsupported
styled widening continues to fail closed before caller-sink mutation.

Rectangle comparison and combination retain the allocation-free
rectangle/axis-preserving fast paths. Arbitrary same-factory geometry operands
fall through to a transient typed rectangle transcript and the shared
normalized path core, preserving complete fill semantics without penalizing
the common rectangle lane.

The Windows provider test creates these resources through the exported
`ID2D1Factory1`, validates exact widened bounds and stroke inclusion/exclusion,
streams outline and widened contours into provider-owned path sinks, and checks
their bounds and filled hole semantics. It also exercises rectangle-initiated
relation and Boolean combination against a provider path. It repeats the same
operations through a genuine system `D2D1CreateFactory` and requires matching
bounds, regions, relations, and Boolean area. Both Windows 11 ARM64 and x64
builds pass with MSVC `/W4 /WX`; the
portable optimized and sanitizer suites exercise the identical shared
implementation on macOS.

### Shared Windows path-geometry checkpoint

The exported Windows `ID2D1PathGeometry1::ComputeArea` implementation now
replays each immutable provider path once into a cached portable path and
delegates area evaluation to the qualified portable contour normalizer. It no
longer sums absolute per-figure shoelace areas, which over-counted nested holes
and overlapping alternate or winding figures. The cache preserves the original
line, cubic, quadratic, arc, segment-flag, figure, and fill-mode transcript and
is protected for concurrent geometry queries.

That same cached transcript now supplies the exported Windows path facade's
`GetWidenedBounds`, `StrokeContainsPoint`, `Tessellate`, `CompareWithGeometry`,
`CombineWithGeometry`, `Outline`, and `Widen` methods. These methods therefore
use the already-qualified portable geometry algorithms and preserve their
transactional, fail-closed topology limits instead of maintaining a second
Windows-only implementation. Native Windows input geometries, stroke styles,
and caller-owned sinks cross only the ABI-compatible typed Direct2D seam; no
reflection, CPU rasterization, pixel readback, or scalar image fallback is
introduced.

The Windows provider suite compares nested alternate holes, overlapping
alternate XOR regions, and overlapping winding unions against a genuine system
Direct2D factory. Every case is also repeated through an affine scale and
translation. It additionally compares normalized outline area, hole
tessellation coverage, path stroke bounds and probes, widened area, geometry
relation, and Boolean XOR output. Both Windows 11 ARM64 and x64 builds require
identical qualified results; the portable path suite continues to own the wider
self-intersection, contact, hole, styled-stroke, and winding-layer corpus.

Exported Windows transformed geometries also cache an immutable typed path for
the two operations that cannot be expressed by merely composing the source's
world-transform argument: `CompareWithGeometry` and `CombineWithGeometry`.
The cache is produced through the source geometry's typed cubic/line
`Simplify` transcript with the intrinsic transform already applied. Relation
and Boolean evaluation then use the shared path core, while bounds, fill,
stroke, tessellation, outline, metrics, and widening retain their direct
transform-composition lane. Windows ARM64/x64 tests compare transformed-path
overlap and XOR area with system Direct2D.

### Shared Windows drawing-state checkpoint

The base `ID2D1Factory::CreateDrawingStateBlock` slot on the exported Windows
factory now activates the portable typed drawing-state implementation. The
block preserves antialias modes, text antialias mode, tags, affine transform,
optional DirectWrite rendering parameters, mutation, and originating factory
identity through ABI-compatible COM interfaces. Windows ARM64/x64 validation
compares initial and mutated descriptions with a system Direct2D factory. The
`ID2D1Factory1` description-v1 overload now activates the same object through
its complete `ID2D1DrawingStateBlock1` vtable, including primitive-blend and
unit-mode state. Base-interface `SetDescription` updates only the common
prefix and preserves those v1-only fields, matching system Direct2D. The
portable suite checks v1 querying and state projection; Windows ARM64/x64
compare both initial v1 state and mixed base/v1 mutation with the system
factory.

### Windows custom-effect registry checkpoint

The exported Windows `ID2D1Factory1` facade now implements
`RegisterEffectFromString`, `RegisterEffectFromStream`, `UnregisterEffect`,
`GetRegisteredEffects`, and `GetEffectProperties`. Custom COM effects are a
Windows-only Direct2D extension, so the factory owns a private multithreaded
system-Direct2D registry and forwards this dependency slice to it. This keeps
Microsoft's XML validation, property-binding, built-in-effect enumeration,
registration reference counting, and `ID2D1Properties` metadata semantics
exact; it does not delegate ProGPU geometry, scene recording, or rendering and
does not change the ProGPU factory identity exposed by `ID2D1Resource` objects.

Windows ARM64 and x64 validation registers the same unique effect through the
ProGPU and genuine system factories, compares the complete registered CLSID
list and display-name metadata, exercises duplicate registration plus staged
unregistration, and repeats registration from independent UTF-8 `IStream`
instances. Portable custom effects remain typed ProGPU shader contracts using
WGSL or translated HLSL; this Windows registry lane does not pretend that an
arbitrary `ID2D1EffectImpl` COM graph is portable.

### Windows provider-owned scene-target checkpoint

The exported Windows `ID2D1Factory1` compatibility facade now exposes the same
typed `scene_factory_native` extension as the macOS/Linux portable factory.
`CreateSceneRenderTarget` activates the shared provider-owned
`ID2D1RenderTarget` implementation rather than delegating to a system WIC,
HWND, HDC, or DXGI target. The returned target retains the original ProGPU
factory identity, records supported Direct2D resource and draw calls into one
pointer-free semantic scene, and exposes its required size, summary, and
serialization through `scene_render_target_native`.

This closes the platform asymmetry at the intended portable boundary: Windows
COM applications can opt into the same retained render-target contract used by
recompiled macOS/Linux applications, while unrepresentable native-handle
factory methods continue to fail closed. Windows ARM64 and x64 `/W4 /WX`
tests query the extension from the exported factory, verify dimensions and
factory ownership, record and end a clear-only frame, and require a non-empty
serialized scene plus exact scene ID, generation, and clear metadata. The
portable 15-test macOS matrix continues to exercise the shared implementation.

Compatible bitmap targets also preserve independent X/Y DPI when both a DIP
size and a differently proportioned pixel size are requested. The target and
its bitmap report the derived per-axis DPI and the original DIP dimensions;
valid nonuniform density no longer fails merely because the two DPI values
differ.

### Windows GDI-metafile resource checkpoint

The exported Windows factory now implements `CreateGdiMetafile`. EMF parsing
and record validation stay in the genuine system Direct2D dependency, while a
small ProGPU-owned `ID2D1GdiMetafile` facade restores the compatibility
factory's COM identity. `GetBounds` and `Stream` preserve the system behavior;
the object does not delegate any ProGPU geometry, render-target, or scene
ownership and does not claim a portable GDI runtime on macOS or Linux.

The Windows differential records a fresh enhanced metafile through GDI,
creates it through both factories from independent `IStream` instances,
requires exact bounds and record transcript hashes, and verifies each resource
returns its originating factory. When the system object exposes
`ID2D1GdiMetafile1`, the ProGPU resource exposes the same interface and
delegates exact per-axis DPI and source-bounds metadata. Both ARM64 and x64
pass the complete 13-test native matrix under MSVC `/W4 /WX`.

The final hosted aggregate caught a stale comparison-envelope assumption after
the portable Direct2D fixture had grown from 64x48 to 64x64. The comparator now
requires the current 64x64 dimensions and checks 25 semantic probes spanning
the original primitives plus aliased/antialiased clips, opacity masks,
compatible-target composition, and opacity/geometric-mask layers. Against the
same D3D12 reference, hosted Metal differs at 305 pixels (maximum 1/255, mean
`0.0286458333`) and Vulkan at 174 pixels (maximum 1/255, mean `0.015625`). The
whole-frame limit is consequently 320 changed pixels while retaining a strict
1/255 channel maximum, zero channels above it, and mean at most `0.03`.

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
## Implementation-first checkpoint: aliased mesh coverage

The portable C++ `ID2D1RenderTarget::FillMesh` implementation now requires aliased
antialias mode and latches `wrong_state` before retaining draw resources when the
mode is incompatible. Both ordinary brush triangle paths and bitmap-brush vector
masks use a one-sample, pixel-center coverage grid. The bitmap's own texture
sampling policy is unchanged. This follows the public
[FillMesh contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-fillmesh),
which requires aliased mode and deferred error reporting through EndDraw/Flush.

Provenance is the original in-repository portable render-target implementation,
with no foreign implementation port. The state check is O(1); existing O(T)
triangle storage/serialization and the single recorded draw remain for T triangles.
Canonical native path and clip shaders consume the coverage setting; no shader
fork, C ABI, public module, readback, or per-triangle submission is introduced.

Applicability: portable Direct2D COM calls, including Windows ABI consumers, reach
this shared C++ implementation. There is no separate managed FillMesh renderer to
patch. Native Windows command-list resource extraction remains a separate feature
gap; this change does not add arbitrary native ID2D1Mesh extraction or establish
full Win2D compatibility. Authored cases cover rejected antialiased state, recovery,
ordinary triangle coverage, bitmap mask coverage, and explicit Windows ABI state.
The native library and portable Direct2D compatibility test target compile. Tests,
VM/image comparisons, sanitizers, benchmarks, verifiers, and CI qualification remain
unexecuted during the requested implementation-first phase.
## Implementation-first checkpoint: geometry and layer-mask antialias state

Portable C++ `FillGeometry` and geometry-backed shape fills now inherit the
render target's geometry antialias setting. Aliased mode selects one pixel-center
sample; per-primitive mode retains the existing 8x8 coverage policy. Explicit
glyph coverage remains independent: text callers already supply their own grid
and do not inherit the geometry setting. Ordinary paint and bitmap-brush vector
mask paths consume the same selected grid without changing texture filtering.

`PushLayer` now accepts aliased geometric masks. The layer's own
`maskAntialiasMode`, not the target's mode, is passed through for both a vector-only
mask and a vector-plus-opacity-brush composite mask. Existing transforms, exact
geometry, layer opacity, retained bounds, and resource/factory checks remain in
place. ClearType initialization and unsupported layer transform cases are unchanged.

This original ProGPU change follows the public
[SetAntialiasMode contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-setantialiasmode)
and [layer mask parameters](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ns-d2d1-d2d1_layer_parameters).
Selection costs O(1) time/storage; existing O(S) path serialization for S segments
and canonical path/clip shader execution remain shared. No C ABI, public module,
shader fork, CPU pixel fallback, or additional GPU submission is introduced.
Portable Direct2D callers use the same C++ endpoint; no separate managed Direct2D
implementation exists for this state selection. The managed semantic renderer's
existing coverage settings are unchanged.

Authored cases cover both modes, opposite target/mask modes, ordinary/bitmap paint,
and vector-only/composite layer masks. They replace the previous expected
aliased-mask rejection with the newly supported behavior. Compilation is recorded
separately: the native library and Direct2D compatibility test target compile.
Runtime tests, Windows VM/image parity, performance, and CI qualification
remain deferred and are not claimed by this checkpoint.
## Implementation-first checkpoint: affine full-target opacity layers

Full-target layers with an opacity brush now accept finite invertible affine
world transforms, including quarter-turns, general rotation, shear, and reflection.
The native helper inverse-maps all four viewport corners in DIP coordinates using
both target DPI axes. It retains their local envelope as the opacity-mask domain
and preserves the original world transform for brush evaluation. The viewport
remains the visible boundary: this envelope is not a substituted geometric clip.

Corner bounds and origin/extent encoding are rounded outward when narrowed to
float, including cancellation at a distant origin. Non-finite/unrepresentable
domains and singular transforms fail closed. Work and stack storage remain O(1)
for four fixed corners; there is no pixel loop, readback, shader change, or new
submission. Existing solid/gradient mask pipelines and typed ownership are reused.
This extends the original ProGPU `try_resolve_full_target_local_bounds` helper,
consistent with full-target/default bounds and world-relative mask transforms in
the [Direct2D layers overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview).

The bounded-layer rotation/shear restriction is deliberately unchanged: its exact
content-bound clipping semantics still need implementation and qualification.
ClearType initialization and bitmap opacity-brush support also remain separate
gaps. Portable Direct2D callers share this C++ endpoint; no separate managed
Direct2D layer-domain implementation is changed. Authored cases cover solid and
linear-gradient brushes, three affine transforms, equal/unequal DPI, corner-domain
containment, and singular failure. Runtime/Windows image parity and performance
remain unverified during the implementation-first phase.
The native library and Direct2D compatibility test target compile successfully.

## Implementation-first checkpoint: bitmap opacity brushes

Portable Direct2D now accepts native bitmap brushes as opacity brushes in
`PushLayer` and `FillGeometry`, including bitmap-painted geometry and combined
geometric/bitmap layer masks. A child semantic scene captures the bitmap-brush
image draw; the existing picture-mask pipeline consumes its alpha. Geometric
coverage is intersected through the existing composite-mask representation.
The bitmap's DPI, alpha interpretation, addressing, nearest/linear sampling,
brush transform and opacity all use the same encoder as ordinary bitmap paint.
Brush opacity is encoded once in the child image; layer opacity remains separate.

Child resource indices stay local to their scene. Bitmap source COM references
are retained until the parent target's next `BeginDraw`, matching the normal
bitmap resource-cache lifetime. The child capture does not increment public
draw counts. Missing bitmap data, unsupported native bitmap sources, incompatible
factories, and invalid transforms fail closed. Shared/external sources retain
their existing import contract; this change adds no CPU pixel processing or
readback. It does add retained picture capture and mask rendering work, whose
runtime cost is not yet measured. Serialization takes O(B + S) time/storage for
serialized image bytes B and clip segments S; there is no new scalar pixel loop.

This is original ProGPU code based on the public
[layer opacity-brush contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ns-d2d1-d2d1_layer_parameters),
reusing ProGPU image and picture/composite-mask code rather than foreign source.
Portable COM callers share this C++ endpoint; no separate managed Direct2D
implementation or public C ABI/module change is needed. Canonical shaders are
unchanged. This supersedes the earlier bitmap-opacity-brush gap for native
bitmap sources supported by the ordinary bitmap-brush encoder; it does not
qualify arbitrary Windows bitmap providers or make Win2D binaries portable.
Bounded rotated/sheared layers and ClearType layer initialization remain open.

Authored cases cover 16 layer combinations (bounded/full-target, geometry/no
geometry, nearest/linear, and both mask antialias modes), four geometry-fill
combinations, brush mutation after capture, parent/child image coexistence,
draw counts, and missing-source failure. The native library and compatibility
test target compile. Tests are not executed: Windows/native differential images,
macOS/Linux GPU behavior, source lifetime stress, performance, and CI qualification
remain deferred to the final validation phase.

## Implementation-first checkpoint: automatic Direct2D layers

The portable render target now accepts `PushLayer(parameters, nullptr)`, following
the Windows 8-and-later optional-resource behavior documented by
[ID2D1RenderTarget::PushLayer](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-pushlayer%28constd2d1_layer_parameters__id2d1layer%29).
Automatic scopes emit the same semantic layers as explicit resources, allowing
the existing compositor to own intermediate targets. They do not manufacture
COM layer objects or introduce a separate rendering path. Scope membership is
tracked by the existing typed scope stack, independently of whether a public
resource is present. Explicit layers still retain factory checks, exclusive
use leases, size tracking, and release-on-error/destruction behavior.

Original ProGPU implementation: O(1) additional scope bookkeeping in fixed-capacity
storage; existing mask serialization and renderer allocation policies are unchanged.
No CPU pixel work, shader change, C ABI/module change, or managed WPF workaround
is introduced. Portable callers, including Windows COM ABI callers, share this
C++ endpoint. The separate Windows command-list consumer already accepts absent
layer resources and is not changed here. This does not implement its remaining
mask/transform gaps or claim full `ID2D1DeviceContext`/Win2D parity.

Authored regressions compare explicit/automatic scene bytes for unmasked,
geometric, solid-opacity, full-target opacity, composite-gradient, and bitmap
opacity layers; cover mixed clips/layers, unbalanced scopes, overflow, invalid
parameters, explicit-lease recovery; and extend the Windows ABI fixture with a
nested automatic layer. The native library and portable compatibility target
compile. Runtime tests, Windows-specific compilation/execution, GPU/VM image
comparisons, benchmarks, and CI qualification remain deferred. Bounded general
rotation/shear is still rejected: the reviewed documentation did not establish
its exact edge behavior sufficiently to replace the planned Windows oracle.

## Implementation-first checkpoint: compact A8 bitmap uploads

`CreateBitmap` now accepts `DXGI_FORMAT_A8_UNORM` with premultiplied, straight,
or default/unknown alpha (resolved to premultiplied). These are the documented
[Direct2D bitmap format combinations](https://learn.microsoft.com/en-us/windows/win32/direct2d/supported-pixel-formats-and-alpha-modes#specifying-a-pixel-format-for-an-id2d1bitmap).
The alpha byte is identical in straight and premultiplied A8. Ignored alpha is
not accepted for this format. Copy/update rectangles use one-byte pixel offsets
and pitches, including overlap-safe self-copy; shared bitmap aliases retain the
source byte layout and metadata. Existing generation-based scene invalidation
continues to apply.

ProGPU's reusable scene builder now exposes `add_r8_image` through its C++ header
and module. The additive C scene resource flag `PROGPU_NATIVE_SCENE_IMAGE_R8`
selects compact single-channel payloads and `R8Unorm` GPU textures. It cannot be
combined with BGRA8 or external-image flags. Upload sizes and image/payload
validation use one byte per pixel, without relaxing RGBA/BGRA stride checks.
Old consumers reject the unknown resource flag; producer/consumer versions must
be deployed together. No existing structure size or function signature in the
stable C ABI changes. The native C# contract generator was rerun and emitted no
tracked binding changes for this constant-only addition.

R8 samples have `(R, 0, 0, 1)` semantics. Direct2D A8 drawing explicitly uses the
canonical image color-matrix stage to produce `(0, 0, 0, R)`. This works through
ordinary bitmap drawing, bitmap brushes, `FillOpacityMask`, and the retained
bitmap-opacity layer capture, without CPU expansion, readback, or new shaders.
Upload storage/copy work remains O(B) for B source bytes, using the existing
byte-copy implementation; the channel mapping is O(1) CPU metadata and GPU
per-pixel work. No new compute-heavy CPU loop or SIMD fallback is introduced.
Performance is not measured; this does not claim a speedup from byte-count alone.

This is original ProGPU code reusing its resource validator, image builder,
WebGPU uploader, and canonical color-matrix pipeline. Portable COM callers share
the C++ endpoint. There is no separate managed Direct2D implementation to update;
the current managed WPF renderer is unchanged. WIC 8bpp-alpha import/lock support,
arbitrary Windows providers, and render-target readback copies remain separate
gaps; the new upload route does not claim them.

Authored cases cover three alpha modes, padded rows, non-square DPI, short-pitch
rejection, subrectangle updates, overlapping self-copy, shared aliases, compact
scene bytes, red-to-alpha metadata, opacity-mask/layer recording, malformed R8
resource flags/lengths, and a module-import consumer. Native library, Direct2D
compatibility target, internal test target, and C++ module consumer compile.
No tests, GPU scenes, Windows/Linux builds, VM comparisons, benchmarks, or CI
qualification were executed during this implementation-first checkpoint.

## Implementation-first checkpoint: WIC A8 sources and locks

`CreateBitmapFromWicBitmap` and `CreateSharedBitmap(IWICBitmapLock, ...)` now
recognize `GUID_WICPixelFormat8bppAlpha`. The portable compatibility header
publishes its GUID from Microsoft's
[Windows SDK declaration](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/wincodec.h).
The format/alpha combinations follow the public
[supported WIC formats table](https://learn.microsoft.com/en-us/windows/win32/direct2d/supported-pixel-formats-and-alpha-modes#supported-wic-formats):
straight and premultiplied A8 are accepted, unknown resolves to premultiplied,
and ignored alpha is rejected. Existing DPI defaults are unchanged.

WIC source imports call `CopyPixels` into owned compact one-byte rows. Shared
locks retain their original data pointer, padding and COM lifetime; the snapshot
uploaded into a semantic scene remains owned by that scene, as with existing
color locks. Lock copies, subrectangle writes, overlapping self-copy and copies
back to owned bitmaps use format-aware offsets/pitches. The reusable R8 resource
and GPU red-to-alpha encoder from the previous checkpoint handle rendering.
No WIC A8 premultiplication, CPU RGBA expansion, pixel readback, or new shader is
introduced. Copying uses the existing byte-copy path: O(B) work for B bytes,
O(B) owned storage for imports/overlap snapshots, and O(1) retained lock metadata.
There is no new compute-heavy scalar pixel loop and no measured speedup claim.

This is original ProGPU implementation extending its typed WIC COM adapters,
not a foreign implementation or a managed WPF workaround. No stable C ABI or
public module signature changes; the additive GUID is in the C++ compatibility
header. Portable Windows/macOS/Linux callers share this endpoint. Windows ABI
GUID comparison is authored but not compiled/executed on Windows in this phase.

Authored cases cover all three accepted alpha selections, exact compact import
sizes, source mutation independence, padded lock storage, bidirectional copies,
self-overlap, one-byte replacement, aliases, retained-lock lifetime, short stride,
short buffer and ignored-alpha rejection. The native library and compatibility
test target compile; tests are unexecuted. This supersedes the earlier WIC A8
import/lock gap, but not WIC render-target creation, arbitrary format conversion,
render-target readback copies, Windows/GPU parity, performance or CI qualification.
The latest fetched ProGPU `main` is already contained in the feature branch.

## Implementation-first checkpoint: retained picture images and compatible bitmap draws

Compatible render-target bitmaps now implement the private typed bitmap source
contract used by `DrawBitmap`, bitmap brushes and opacity capture. Shared aliases
preserve bitmap and scene-query interfaces, source identity and per-view DPI/alpha
metadata. A completed, cleared source is captured as owned nested scene bytes;
subsequent source redraws get a new generation without changing earlier draws.
Original and shared aliases reuse one parent resource for an unchanged source.
Self-target drawing is rejected before acquiring the source target lock.

The reusable backend primitive is `semantic_scene_builder::add_picture_image`.
Its C wire resource is `IMAGE | IMAGE_PICTURE`, with a fixed 48-byte
`progpu_native_scene_picture_image` payload and a complete auxiliary scene stream.
The descriptor records physical dimensions, uniform DPI and straight clear RGBA.
Reserved words/flags must be zero; dimensions, byte limits and recursive depth
are checked during scene ingestion. Image commands require matching dimensions,
four-byte logical stride and premultiplied-source sampling. Existing C ABI
records/functions are unchanged; this additive resource requires a matching
producer/consumer. The public module exports the same descriptor and builder.

The original ProGPU `progpu_native_semantic_picture_mask_resources.cpp` source
rasterizer is shared, not copied: picture images retain its full RGBA texture
instead of allocating the alpha-mask-only binding. Clear RGB is premultiplied
before the render attachment clear. Exact descriptor DPI avoids reconstructing
DPI by dividing rounded source bounds. Repeated references share one texture
per compiled image page and distinct picture resource; stable page replay retains
that texture. Ordinary sampling, color matrices, effects and brush addressing
reuse the existing image execution and canonical shaders. An A8 picture projects
sampled alpha to transparent black, whereas uploaded R8 still projects sampled red.
There is no CPU pixel readback, pixel expansion, new scalar pixel loop or shader
fork. This is original ProGPU implementation using its existing native picture
and image algorithms, not third-party implementation text.

For S captured stream bytes and P output pixels, recording owns O(S) bytes and
rasterization owns O(P) texture storage, plus the existing child-scene rendering
cost. Parent identity lookup follows the current bitmap resource cache; backend
picture-view lookup is expected O(1) per image command with O(R) temporary entries
for R distinct pictures. Source scene bytes own their uploaded/nested resources,
so picture cache entries do not keep source COM targets alive and cannot introduce
mutual target ownership cycles. Scene identity plus generation protects a cache
entry when a released target address is reused. Pixel-backed bitmap entries keep
their existing source lease. No measured performance claim is made.

Applicability: this implements the native C++ Direct2D endpoint and its reusable
native scene resource. Managed/portable Direct2D callers reach that same endpoint;
there is no second managed Direct2D implementation. Managed WPF's existing retained
render-target path is unchanged. This native wire descriptor is not currently an
opted-in generated managed record; the native contract generator was rerun without
changing generated bindings. No hot-path reflection or WPF-only workaround is added.

Authored coverage includes descriptor/flag/stride/depth rejection, nested stream
ownership and corruption, include/module consumers, RGBA/BGRA/A8 compatible draws,
high DPI, cropped sampling, opacity, shared aliases, source generation changes,
bitmap brushes, A8 opacity-mask projection, self-target and active-source failures.
The Windows COM case now records `DrawBitmap` as well as `FillOpacityMask`.
The native library, internal and portable Direct2D test targets, and LLVM C++
module consumer compile. Tests, Windows ABI execution, GPU image comparisons,
macOS/Linux/Windows parity, benchmarks and CI qualification remain deferred.

Remaining work is explicit: persistent backing for delta-only compatible target
updates (currently rejected, not rendered with lost old pixels), nonuniform source
raster DPI (currently rejected), arbitrary provider scenes/external GPU leases,
render-target copies/readback, broader device-context/Win2D support and full
runtime/resource-lifetime qualification. This checkpoint does not claim full
Direct2D render-target parity or readiness to merge without those final gates.

## Implementation-first checkpoint: retained compatible-target drawing sessions

Successful compatible-target `BeginDraw`/`EndDraw` sessions now append to the
existing semantic builder instead of resetting it. Earlier commands, immutable
brush/pixel/picture resources and the original clear color remain available to
later `GetBitmap` captures. Empty sessions preserve content. There is no recursive
picture wrapper per session: a sequence of sessions produces one flat retained
command/resource stream. Other render-target types retain their existing
per-session recording and external-target preservation behavior.

An unscoped compatible-target `Clear` replaces all retained commands/resources,
including draws earlier in the current session. Multiple full clears in one
session are supported. Fresh compatible recording starts with transparent black
(opaque black for ignored alpha). Previously captured parent scenes own their
bytes and do not change when their source is cleared. Failed/unbalanced sessions
remain non-exportable; the existing new-session error recovery resets recording
instead of exposing partially failed history. This does not promise rollback to
the last successful GPU contents after a drawing error.

The behavioral references are Microsoft's
[BeginDraw contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-begindraw)
and [GetBitmap contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1bitmaprendertarget-getbitmap).
The latter explicitly separates bitmap creation DPI from subsequent render-target
DPI. The native target now stores creation DPI immutably, while picture capture
uses the actual uniform raster DPI. Shared aliases change their logical bitmap
view without overwriting that source raster scale. A full clear can establish a
new raster-DPI history. A DPI change with retained DIP geometry is detected and
rejected for export/EndDraw until Clear, rather than rescaling old pixels.
Nonuniform raster DPI and mixed-DPI retained epochs still require implementation.
No Microsoft source implementation was copied; these references supply behavior,
not code. The algorithm reuses ProGPU's original semantic builder
`advance_generation`/`reset` and native image capture.

Performance and applicability: retaining a session boundary is O(1), and new
recording retains its existing command/resource costs. For H total retained
commands/resources/payload since the last clear, storage and export remain O(H).
A changed picture currently rasterizes the complete retained history; stable
native image-page replay reuses its texture. Incremental device-owned target
backing and bounded GPU history compaction remain required performance work,
not a completed part of this checkpoint. Existing stream/command/resource limits
continue to fail closed. No CPU pixel loop/readback or new shader is introduced.
This is the shared native C++ Direct2D endpoint; managed callers use that endpoint,
and the independent managed WPF renderer is unchanged. No public wire, C ABI,
module or generated managed layout changed.

Authored tests compare 24 separate sessions against one session's command and
resource payloads for RGBA/BGRA/A8, check flat history beyond picture nesting depth,
empty sessions, unchanged captured parents, full/repeated clears, creation-DPI
versus raster-DPI metadata, and fail-closed mixed-DPI history. The Windows COM
case includes a second no-clear drawing session. Native library and portable
compatibility tests compile. Tests, Windows ABI compilation/execution, GPU image
parity, performance measurements and CI qualification remain deferred under the
requested implementation-first sequencing. Delta-session rejection from the
previous checkpoint is superseded for supported uniform-DPI histories; full
Direct2D/Win2D, incremental backing, copy/readback and final gates remain open.

## Implementation-first checkpoint: incremental GPU picture backing

Retained picture images now keep device-owned backing textures across parent
scene changes. Before reusing pixels, ProGPU compares the source descriptor,
engine flags and complete previous command/resource prefix. Immutable resource
payloads must match exactly; brush/text-style tables may append unchanged-prefix
records. Both input streams have already passed scene validation, so the previous
command boundary has balanced scopes. Changed earlier commands/resources, changed
clear color/size/DPI, decreasing generations, 3D depth-dependent scenes and external
image bindings take the existing full rasterization path. No hash collision or
resource-generation assumption alone authorizes pixel reuse.

For an exact append, the renderer allocates a new source texture, copies existing
pixels with a GPU texture-to-texture command, then executes a suffix view of the
new scene with `PRESERVE_TARGET`. Resource indices and arena offsets remain those
of the validated full stream. No earlier command is submitted for drawing in that
suffix. Copy-on-write is intentional: two bitmap generations in the same parent
must preserve distinct pixels, and a later append must not mutate an older draw.
If no commands were appended, the existing texture is shared directly. The original
ProGPU picture-mask source rasterizer and canonical image shaders are reused;
mask behavior is unchanged. There is no pixel readback or CPU repacking.

The engine's FIFO backing cache holds at most eight entries and 64 MiB of combined
texture/owned source-stream storage. Larger pictures still render but are not kept
in that cache. Page-owned shared leases can keep evicted captures alive until their
page is released; those are separate from the additional cache budget. GPU handles
are released under the engine's dispatch scope. Optional cache allocation failure
does not invalidate a completed draw. Device replacement gets a fresh cache;
device-local textures are not included in retained CPU-state cloning.

Exact byte comparison uses unaligned NEON, SSE2 or Wasm SIMD128 loads with a bounded
scalar tail. Other targets delegate to the platform byte-comparison routine. This
is original ProGPU code; tests compare SIMD results against a scalar oracle over
unaligned spans, tails and every mismatch location in representative lengths.
Metadata traversal follows typed indexed scene records; no reflection, per-pixel
scalar loop, public ABI/module change or generated managed layout is introduced.

For H retained source bytes, prefix classification and snapshot/child ingestion
remain O(H) CPU work/storage. GPU work for an eligible change is one O(P) texture
copy for P pixels plus rendering appended commands, instead of rendering the full
history. A cache candidate lookup is bounded by eight entries. Stable parent-page
replay still does no picture compilation. Copy submissions contribute to existing
submission metrics; copies are not reported as CPU uploads. This checkpoint does
not claim a measured speedup, O(delta) CPU submission, history compaction, or
complete persistent target semantics for mixed-DPI epochs/3D depth.

Applicability: the optimization concerns the native serialized-picture transport
used by native Direct2D, MIL and other C++ scene clients. Managed Direct2D callers
use that same endpoint. The managed WPF renderer's independent retained target
implementation is unchanged, and no separate shader algorithm was introduced.
Provenance is the in-repository semantic identity/builder code and
`progpu_native_semantic_picture_mask_resources.cpp` source rasterizer.

Authored regressions cover exact prefix acceptance, appended brush tables,
modified old brush/command/image rejection, suffix stream validity, SIMD/scalar
equality, incremental-versus-fresh-full GPU output, warm reuse and simultaneous
old/new captures. Native library, internal tests, portable Direct2D tests and the
Direct2D WebGPU test executable compile. None of these tests was executed in this
implementation-first checkpoint. SIMD on x86/Wasm, GPU copy-on-write, cache-budget
eviction, device recovery, platform image parity, performance and CI still require
final qualification. CPU history compaction, mixed/nonuniform raster DPI,
render-target copy/readback and full Win2D remain open goal work.

## Implementation-first checkpoint: atomic cached native scene exports

Native scene targets now lazily serialize a completed generation into target-owned
export storage once. Repeated `GetRequiredSceneSize` calls reuse its length, and
`BuildScene` copies those immutable bytes directly into caller-owned storage.
Recording, failed sessions and invalid mixed-DPI histories remain non-exportable;
a later successful generation replaces the cached bytes on its first export.
The first size query can therefore allocate/materialize an export and returns zero
on failure. No pointer into the target-owned cache is exposed to callers.

Compatible targets implement the existing private typed bitmap-source interface.
`GetBitmap` wrappers delegate metadata/capture to that source rather than separately
querying DPI, size, summary and serialized bytes. Metadata snapshots are O(1) under
one target lock. `AddToScene` captures generation, bitmap view, raster DPI, clear
color and scene bytes under the same lock, then performs one ownership copy from
the cached export into the destination semantic builder. The temporary full-scene
vector previously allocated by each bitmap wrapper capture is removed. Shared
aliases still apply their own DPI/alpha view after that atomic source capture.
Existing self-target checks run before source locking, and source-to-own-builder
capture is also rejected explicitly.

This reuses original ProGPU semantic builder serialization, native scene target
ownership and typed bitmap-source contracts. Public Direct2D/scene interfaces,
stable C ABI, modules and generated managed records are unchanged. Native and
managed Direct2D callers share this native endpoint; the independent managed WPF
renderer is not changed. No reflection, pixel conversion, new scalar pixel loop,
readback or shader is introduced. Bulk byte ownership copies use the existing
native byte-copy path.

For H retained scene bytes, the first export after a changed generation still
costs O(H) time/storage. Warm size/bitmap-metadata queries are O(1); public export
and capture into a new destination require O(H) copying but no repeated source
serialization. The target retains one export vector within existing scene-size
limits; allocation capacity may be reused. This does not implement CPU history
compaction, delta-only transport, or an O(delta) update boundary, and no measured
speedup is claimed.

Authored regressions cover repeated exports, caller-buffer mutation independence,
untouched short-buffer/canary storage, active-session rejection, and clearing a
previously cached large history. Existing multi-generation, shared-alias, DPI and
incremental GPU fixtures exercise the same capture path. Native library, portable
compatibility tests and Direct2D WebGPU test executable compile. Tests, concurrent
stress/OOM cases, Windows ABI execution, GPU image comparisons, allocation/latency
measurements and CI qualification remain deferred under implementation-first
sequencing. The full MIL/DirectX/Direct2D/Win2D goal remains open.
