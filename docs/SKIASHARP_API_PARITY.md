# SkiaSharp API parity

ProGPU validates its clean-room `SkiaSharp` shim against the public ECMA-335
metadata in the official `SkiaSharp` NuGet package. The lock in
`eng/skiasharp-api-baseline.json` pins the package URI, SHA-512, target-framework
reference assembly, namespace, and monotonic regression budget.

The current contract is `SkiaSharp` `4.151.0`, using
`ref/net10.0/SkiaSharp.dll`. This advances the previous implementation record
from Skia m148 to the current stable SkiaSharp package without consulting or
copying its implementation source.

Run the complete gate with:

```bash
./eng/progpu-verify-skiasharp-api.sh
```

The gate verifies the official package hash, extracts only its public reference
metadata, self-tests the canonical metadata reader, builds the ProGPU shim, and
writes deterministic JSON and Markdown reports under
`artifacts/skiasharp-api/`. CI fails if exact matches decrease or missing entries
increase.

API equality is necessary but not sufficient. Every implementation slice must
also include independent behavioral tests, Svg.Skia/Avalonia.Skia compatibility
evidence where applicable, and matched Release benchmarks for native SkiaSharp
and ProGPU. Rendering work must preserve ProGPU's WebGPU ownership, quality,
device-loss, bounded-resource, and allocation contracts.

The matched benchmark runner is
`eng/progpu-run-skiasharp-benchmarks.sh`. It compiles identical source against
official SkiaSharp and ProGPU, alternates process order, verifies semantic
checksums, and preserves raw median/p95 timing and allocation distributions plus
environment metadata. Its scheduled workflow runs on macOS, Linux, and Windows;
small timing deltas on shared runners remain informational until calibrated on
dedicated hardware.

The initial local Release run on an Apple M3 Pro, .NET 10.0.5, macOS 26.4.1,
using three alternating process pairs and 72 measured samples per backend,
produced the following diagnostic baseline:

| Workload | Native median ns/op | ProGPU median ns/op | ProGPU/native | Native B/op | ProGPU B/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| point arithmetic | 2.076 | 2.158 | 1.039 | 0 | 0 |
| matrix map point | 8.713 | 4.547 | 0.522 | 0 | 0 |
| path builder, detach, and bounds | 808.542 | 3,284.499 | 4.062 | 168 | 3,520 |

These figures identify path construction/ownership as the first measured CPU
and allocation hotspot. They are not a cross-platform performance claim; raw
distributions and environment records remain in generated artifacts, and the
path work requires matched profiling plus equivalent before/after runs.

## Current baseline

The current pinned comparison records 4,222 official entries, 5,219 ProGPU
entries, all 4,222 exact matches, zero missing entries, and 997 ProGPU-only
entries. The matching/missing budget is now locked at full official coverage.
ProGPU-only entries are audited and removed when accidental; explicitly
documented extension seams remain outside the official parity claim.

The continuation branch regenerated the original 97-entry gap from the pinned
official package at `v0.1.0-preview.34` (`39b53dbb`) before implementation.
Public metadata closure does not by itself complete rendering compatibility:
GPU-visible families still require original retained WebGPU implementations
and quality/performance tests, while unsupported platform codecs must fail
explicitly rather than silently emit another format.

## Rendering continuation research record

The remaining mask-filter and forwarding-canvas slice is a clean-room design
based on public contracts and independently observable behavior. No
implementation source from SkiaSharp or another renderer is used.

Primary sources consulted:

- [Skia `SkMaskFilter`](https://api.skia.org/classSkMaskFilter.html): mask
  filters transform coverage before compositing; Gaussian sigma must be
  positive and may be transformed by the current matrix.
- [Skia `SkCanvas`](https://api.skia.org/classSkCanvas.html) and
  [`SkOverdrawCanvas`](https://api.skia.org/classSkOverdrawCanvas.html): canvas
  state is a matrix/clip stack, while overdraw records every touched pixel
  rather than final source color.
- [Direct2D Gaussian blur](https://learn.microsoft.com/windows/win32/direct2d/gaussian-blur):
  a separable GPU blur uses transparent soft borders and a conservative
  three-sigma radius.
- [Direct2D effects overview](https://learn.microsoft.com/windows/win32/direct2d/effects-overview)
  and [custom effects](https://learn.microsoft.com/windows/win32/direct2d/custom-effects):
  retained effect graphs compose GPU transforms and explicitly expand input
  rectangles for non-local sampling.
- [Win2D GaussianBlurEffect](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Effects_GaussianBlurEffect.htm):
  retained effect nodes expose bounds/invalidation and may cache their output.
- [WebRender](https://github.com/servo/webrender): retained display-list
  rendering keeps scene preparation separate from GPU raster/composition.
- [Vello](https://github.com/linebender/vello) and its
  [image-filter status](https://github.com/linebender/vello/tree/main/image_filters):
  GPU compute is the intended parallel execution boundary, while filter
  semantics remain an explicit renderer concern.
- [Parley](https://docs.rs/parley/latest/parley/) and
  [HarfBuzz shaping](https://harfbuzz.github.io/harfbuzz-hb-shape.html): shaped
  glyph IDs, positions, and layout are reusable CPU results and are not
  recomputed by a coverage effect.
- [DirectWrite glyph runs](https://learn.microsoft.com/windows/win32/directwrite/glyphs-and-glyph-runs)
  and [Direct2D/DirectWrite integration](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite):
  text layout remains independent from the renderer that consumes the retained
  glyph run.

Adopted: immutable filter snapshots, conservative three-sigma bounds, retained
command replay, transparent out-of-bounds sampling, and GPU effect composition.
Adapted: mask filters route through ProGPU's existing WebGPU save-layer/effect
graph so paths, glyphs, images, and custom visuals share one backend. Rejected:
CPU pixel fallbacks, moving Unicode or OpenType shaping to the GPU, unbounded
per-frame filter allocation, and source-shaped ports of another engine's
implementation.

## Planned implementation order

1. Close metadata-only value, enum, descriptor, and ownership contracts that do
   not require GPU initialization.
2. Complete bitmap, pixmap, image, codec, stream, and color-space contracts with
   explicit CPU/GPU ownership and no accidental readback or upload.
3. Complete paths, regions, paint, text, picture, document, and canvas behavior
   over reusable ProGPU primitives.
4. Complete shaders, filters, blenders, masks, vertices, atlas, surface, and GPU
   context contracts through retained WebGPU pipelines and embedded shaders.
5. Prove source-level Avalonia.Skia substitution, close the full Svg.Skia corpus,
   and enforce representative CPU, GPU, frame-time, and memory advantages over
   the official runtime on supported platforms.

Primary public contracts:

- <https://www.nuget.org/packages/SkiaSharp/4.151.0>
- <https://learn.microsoft.com/dotnet/api/skiasharp>
- <https://www.w3.org/TR/SVG2/>
- <https://www.w3.org/TR/webgpu/>

## Implemented parity checkpoints

### Complete official metadata and retained mask/canvas contracts

All 4,222 public entries in the pinned SkiaSharp 4.151 reference metadata now
match exactly, including declaring type, inheritance, fields, signatures,
parameter names, layout, ownership hooks, obsolete payloads, and nullable
metadata. The regression gate is ratcheted to zero missing entries.

`SKMaskFilter` now retains immutable blur, table, gamma, clip, and shader
coverage descriptions; conversions and fast paint bounds are fixed `O(1)`,
while table construction is bounded `O(256)`. Overdraw color filters retain
their six-color palette and clamp positive coverage counts to the last color.
No factory initializes WebGPU or reads pixels.

At draw time, a typed retained-brush marker intercepts only commands that carry
a mask filter. Ordinary commands perform two marker checks and never invoke the
mask delegate. Filtered commands render their retained geometry or glyph run to
an offscreen texture and reuse the existing WebGPU image-effect graph for
separable blur, alpha tables, shader masks, and solid/inner/outer composition.
Overdraw palette mapping uses a dedicated 16x16 WebGPU compute pass with one
texture read and write per texel and a fixed 96-byte six-color uniform. Pixel
tests cover Gaussian falloff and exact zero/one/saturated overdraw counts; the
shader resource audit enforces embedding and complexity documentation.

`SKNoDrawCanvas`, `SKNWayCanvas`, and `SKOverdrawCanvas` share a typed retained
command-forwarding seam in `DrawingContext`. Every newly recorded command is
forwarded immediately, including its packed buffer slices and retained resource
leases. Fan-out costs `O(T * (B + R))` for `T` targets, referenced packed data
`B`, and retained resources `R`; ordinary canvases pay one direct delegate-null
check per recorded command. Overdraw forwards additive 1/255 coverage rather
than source paint color. Focused tests cover immutable tables, conversion and
bounds behavior, immediate target removal/fan-out, and additive coverage
commands.

### Compact font metrics and pinned raw text-run buffers

`SKFontMetrics` now uses the official sequential flags-plus-fifteen-floats ABI.
The four nullable decoration metrics are represented by validity bits and
inline values, preserving null semantics in a fixed 64-byte value with no heap
storage. `SKRawRunBuffer<T>` now uses the official readonly pointer/length
layout. Builder arrays are allocated directly in pinned managed storage and
remain owned by the builder, so glyph, position, text, and cluster spans stay
valid across compacting collections without `GCHandle`, copying, or per-access
allocation.

Focused tests verify the exact field types and size, nullable metric behavior,
raw span lengths and snapshots, compacting-GC stability, and exactly zero
managed bytes across 10,000 warmed position reads. The matched benchmark suite
includes the same public raw-buffer access workload for official SkiaSharp and
ProGPU. Three alternating clean Release pairs at `c78266ec` retained matching
checksums and measured 4.614 ns/op for official SkiaSharp versus 4.701 ns/op for
ProGPU (1.019 ratio). One-time sample setup amortized to 0.002 versus 0.004
B/op; the warmed access loop itself remains allocation-free. This is neutral
timer-floor evidence, not a performance-win claim. The exact metadata gate
advances from 4,183 to 4,186 matches, reduces
missing entries from 39 to 36, and removes six accidental ProGPU-only metadata
entries.

### Platform codec and managed stream contracts

The WebP frame value, static encoder surface, SVG canvas type shape, managed
stream fork/duplicate declarations, and memory-stream native-disposal hook now
match the pinned public metadata. WebP frames borrow pixmaps directly; the
bitmap constructor reuses `PeekPixels`, while the image constructor performs
the explicit image-to-raster readback requested by that API and retains its
pixel owner through the pixmap. Static and animated WebP encoding currently
return `null` or `false` without writing because the reviewed dependency-free
platform layer does not yet expose a WebP encoder on every supported target.
This is an explicit capability failure and never emits PNG/JPEG bytes under a
WebP contract.

Focused tests cover frame layout and mutation, borrowed pixels, zero-byte
failure behavior, and the non-static/non-constructible SVG helper shape. The
exact metadata gate advances from 4,157 to 4,183 matches, reduces missing
entries from 65 to 39, and removes one accidental ProGPU-only metadata entry.

### Explicit managed ownership and disposal declarations

Thirty-two official protected ownership hooks now appear on their declaring
SkiaSharp types while retaining ProGPU's single `SKNativeObject` lifetime
engine. The declarations cover managed/read/write streams, bitmap and codec
wrappers, color and image filters, color spaces, drawables, font styles, paint,
paths, pictures, surfaces, and text blobs. They delegate to the existing
idempotent base implementation; no native handle model, allocation, rendering
path, or GPU initialization was added. The protected `SKDrawable(bool owns)`
constructor now preserves borrowed ownership without adding a public adapter.

Independent reflection and lifetime tests verify every declaring type,
virtual override shape, borrowed drawable ownership, and post-disposal handle
state. The exact metadata gate advances from 4,125 to 4,157 matches and
ratchets missing entries from 97 to 65 without increasing the 1,019 documented
ProGPU-only entries.

### Exact signatures and managed ownership hierarchy

The current checkpoint closes 62 official metadata gaps without importing a
native ownership model. `SKData`, `SKFont`, `SKRegion`, its three iterators, and
`SKPixmap` now participate in the shared `SKObject` lifetime contract. Data
subsets still share one pinned reference-counted store, release callbacks still
run once after the final view, and the protected empty singleton remains usable
after public disposal. Region iterators retain their bounded snapshots and
pixmap disposal resets only its borrowed CPU view; none of these operations
initializes WebGPU or takes ownership of caller memory.

Public parameter names, optional metadata, and overloads now match the pinned
4.151 reference for color values, sampling values, color spaces, pixmaps,
regions, discrete path effects, and the remaining image-filter factories.
`CreateEmpty` maps to the existing transparent GPU shader-filter path, while
the legacy crop factories map to the existing input graph plus retained crop
rectangle. Object creation and signature adapters are fixed `O(1)` work;
`SKData` final release is fixed work plus its caller-owned callback, and region
iterator snapshots remain `O(R)` time/storage for `R` normalized rectangles.

The public contract was derived only from the pinned official NuGet reference
metadata. Independent focused tests cover exact parameter names, transparent
and crop graph state, shared data ownership, disposed pixmap views, region
operations/iterators, and font behavior. Legacy path iterators and the path-
operation builder are extensible `SKObject` instances with exact disposal
overrides, while disposed temporary paths continue to preserve geometry already
owned by retained commands. The shared `SKObject` disposal declarations,
read-only public-disposal policy, and matrix equality parameter metadata also
match the pinned contract. The exact metadata gate advances from 4,063 to 4,125
matches and ratchets missing entries from 159 to 97. No shader,
rendering algorithm, text-shaping boundary, cache policy, or GPU submission path
changed, so the prior cross-engine rendering research and matched performance
evidence remain applicable.

### Runtime-effect contracts and typed uniform transport

`SKRuntimeEffect`, its shader/color-filter/blender builders, uniform and child
collections, stack-only uniform values, and typed child values now match the
official SkiaSharp 4.151.0 public metadata. The clean-room parser validates a
top-level `main` function, records scalar/vector/matrix uniform layout in source
order, separates shader, color-filter, and blender children, and snapshots both
uniform bytes and child references into immutable effect instances. Construction
and lookup are CPU-only; parsing is `O(S)` for `S` source characters, uniform
snapshots are `O(U)` time/storage for `U` bytes, and child snapshots are `O(C)`
for `C` children. Invalid names, sizes, kinds, and sources fail explicitly.

The matched Release benchmark preserves an exact native/ProGPU packing checksum
for float, float2, and float4 uniforms. A preliminary nine-sample Apple M3 Pro
run measured `641.042` ns/op and `584.928` managed bytes for ProGPU versus
`2,770.375` ns/op and `968.744` bytes for native (`0.231` time ratio). This is a
contract checkpoint rather than the final renderer claim: SkSL-to-WGSL lowering,
child sampling, runtime color-filter execution, and destination-aware blender
execution remain in the active GPU slice and will receive matched three-pair
and Instruments evidence before release.

The design follows the public [Skia runtime-effect contract](https://docs.skia.org/docs/user/sksl/),
[WGSL](https://www.w3.org/TR/WGSL/), and [WebGPU](https://www.w3.org/TR/webgpu/)
execution and resource models. ProGPU adopts immutable compiled programs and
typed byte-packed uniforms, adapts execution to retained WebGPU pipelines, and
rejects native source-code reuse, runtime reflection, and CPU pixel fallback.

### Variable-font descriptors and immutable typeface instances

`SKFontVariationAxis`, `SKFontVariationPositionCoordinate`,
`SKFontPaletteOverride`, and the stack-only `SKFontArguments` now match the
official 4.151.0 sequential value contracts, mutable properties, readonly
accessors, typed equality, hashing, and operators. `SKTypeface` now uses the
official `SKObject` ownership hierarchy and exposes allocation-free span APIs
for variation axes and current positions. Variation cloning maps four-byte axis
tags directly onto ProGPU's existing immutable OpenType instances; unknown axes
are ignored, omitted axes use their defaults, and user coordinates are clamped
and normalized by the existing `fvar`/`avar` engine. Array properties allocate
only their documented result, while warmed span queries are `O(A)` with zero
managed allocation for `A` axes. Typeface cloning is `O(A + R)` for `R`
requested coordinates and preserves distinct font-instance identity for shaping
and retained glyph/cache keys. A thread-safe immutable last-position entry
turns repeated clones into bounded `O(R)` comparison plus one required wrapper,
without weakening the existing 32-instance normalized-coordinate cache. It
remains CPU-only and cannot initialize WebGPU.

Three alternating Apple M3 Pro Release process pairs, 72 samples per backend,
retained exact semantic checksums. Span queries measured `11.850` ns/op for
ProGPU versus `594.391` ns/op for native (`0.020` ratio), both at zero managed
allocation. Repeated clones measured `137.959` versus `31,021.542` ns/op
(`0.004` ratio) and `88` versus `112` managed bytes per clone. The value-only
contract measured `3.658` versus `3.654` ns/op with zero allocation, neutral at
timer resolution. Matched Xcode Time Profiler captures measured queries at
`11.819` versus `580.759` ns/op and clones at `134.521` versus `31,392.875`
ns/op. Allocations plus VM Tracker retained zero bytes per query and `88` versus
`112` managed bytes per clone while preserving the same ordering. Metal System
Trace completed both exact-binary workloads with zero target-process command
buffer submissions and no `MTLDevice.currentAllocatedSize` samples, confirming
that font instance selection does not initialize a GPU.

The clean-room design used the public
[SkiaSharp font-arguments contract](https://learn.microsoft.com/dotnet/api/skiasharp.skfontarguments),
[Skia font-argument model](https://api.skia.org/structSkFontArguments.html),
[OpenType `fvar`](https://learn.microsoft.com/typography/opentype/spec/fvar) and
[`avar`](https://learn.microsoft.com/typography/opentype/spec/avar) contracts,
[DirectWrite axis selection](https://learn.microsoft.com/windows/win32/directwrite/font-selection),
[Core Text variation descriptors](https://developer.apple.com/documentation/coretext/ctfontdescriptorcreatecopywithvariation(_:_:_:)),
and [HarfBuzz variation settings](https://harfbuzz.github.io/harfbuzz-hb-font.html).
ProGPU adopts their shared immutable axis/value instance model and the rule that
unspecified axes resolve to defaults. It adapts that model to bounded managed
instance caches and retained WebGPU glyph resources. It rejects per-draw font
mutation and GPU shaping: Skia's
[text architecture](https://docs.skia.org/docs/dev/design/text_overview/),
[Parley](https://docs.rs/parley/latest/parley/), and
[WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
all reinforce reusable CPU shaping/layout followed by cached glyph preparation
and GPU composition. Color-palette clone behavior remains an explicit follow-up;
the descriptor contract is present but no incomplete palette renderer is
advertised by this checkpoint.

### OpenGL and Metal backend handle descriptors

`GRGlFramebufferInfo`, `GRGlTextureInfo`, and `GRMtlTextureInfo` now match the
official 4.151.0 value surfaces and sequential ABI layouts. OpenGL framebuffer
and texture descriptors retain their unsigned object identifiers and formats
inline, with protection state normalized into the final byte field. The Metal
descriptor retains one native texture handle. Official constructor and property
names, overloads, typed equality, object equality, hashing, operators, readonly
accessors, and the two declared `IEquatable<T>` interfaces are preserved; the
former accidental aliases and optional-parameter signature have been removed.

These structs are CPU-only borrowed-handle metadata. Construction, mutation,
comparison, and hashing are fixed `O(1)` work, allocate nothing, do not claim
ownership of the referenced native resource, and cannot initialize GL, Metal,
or WebGPU. ProGPU rendering continues through its typed WebGPU resource model;
these compatibility values do not introduce a second renderer. Independent
tests verify private field order/types, byte protection normalization, official
parameter names, pointer identity, and complete value behavior. Three
alternating Apple M3 Pro Release process pairs retained exact checksums and zero
managed allocations at `0.996` ProGPU/native (`2.401` versus `2.410` ns/op).
Matched Time Profiler captures measured `2.373` versus `2.385` ns/op and matched
Allocations captures measured zero bytes per operation. The clean-room contract
uses the public
[framebuffer API](https://learn.microsoft.com/dotnet/api/skiasharp.grglframebufferinfo),
[OpenGL texture API](https://learn.microsoft.com/dotnet/api/skiasharp.grgltextureinfo),
[Metal texture API](https://learn.microsoft.com/dotnet/api/skiasharp.grmtltextureinfo),
[OpenGL framebuffer model](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glBindFramebuffer.xhtml),
and [Metal resource ownership model](https://developer.apple.com/documentation/metal/resource-fundamentals).

### Vulkan allocation, image, and YCbCr descriptors

`GRVkAlloc`, `GRVkImageInfo`, `GRVkYcbcrComponents`, and
`GRVkYcbcrConversionInfo` now match the complete 4.151.0 metadata contract,
including sequential nested field layouts, byte-backed Boolean transport,
readonly accessors, value equality, hashing, and operators. The obsolete
`GrVkYcbcrConversionInfo` spelling is retained as one inline current-value
wrapper with exact conversion operators and an intentionally inert obsolete
`FormatFeatures` property. Allocation metadata includes device memory, size,
offset, flags, backend memory, and its hidden transport byte in official order;
image metadata carries allocation, tiling/layout/format/usage, sample and mip
counts, queue ownership, protection, YCbCr conversion, and sharing mode.

These values describe caller-owned Vulkan resources without creating, mapping,
destroying, or submitting them. All getters, setters, and comparisons are
allocation-free fixed `O(1)` CPU work and cannot initialize Vulkan or WebGPU.
Field-wise equality is aggressively inlined so both equal values and a
last-field mismatch avoid boxing and reflection while preserving every public
field's observable contribution. Independent tests inspect every private field
type/order and cover full mutation, nested equality, byte normalization, and
legacy/current conversion. Three alternating Apple M3 Pro Release process
pairs retained exact checksums and zero managed allocations at `0.976`
ProGPU/native (`2.844` versus `2.915` ns/op). Matched Time Profiler and
Allocations captures ranged from `2.784`–`2.879` for ProGPU and
`2.808`–`2.813` ns/op for native, straddling at sub-nanosecond timer resolution,
with zero bytes per operation. The clean-room contract uses the public
[allocation API](https://learn.microsoft.com/dotnet/api/skiasharp.grvkalloc),
[image API](https://learn.microsoft.com/dotnet/api/skiasharp.grvkimageinfo),
[YCbCr API](https://learn.microsoft.com/dotnet/api/skiasharp.grvkycbcrconversioninfo),
and Vulkan's
[sampler-conversion structure](https://registry.khronos.org/vulkan/specs/latest/man/html/VkSamplerYcbcrConversionCreateInfo.html)
and [image-view rules](https://registry.khronos.org/vulkan/specs/latest/man/html/VkImageViewCreateInfo.html).

### Direct3D resource descriptors and backend state

`GRD3DTextureResourceInfo` and `GRBackendState` now match their complete
4.151.0 contracts. The extensible disposable descriptor retains the borrowed
D3D resource pointer, resource state, DXGI format, mip count, sample count,
quality pattern, and protection flag. Disposal follows the official observable
contract: every call dispatches through the protected virtual hook and leaves
the caller-owned resource metadata intact. The unsigned flags enum preserves
exact `None` and all-bits `All` values.

Construction allocates only the required descriptor object; all subsequent
property reads/writes and disposal dispatch are fixed `O(1)` CPU work with no
incremental allocation, COM call, resource transition, device creation, or
WebGPU initialization. Independent tests cover defaults, all mutable values,
post-disposal retention, repeated virtual dispatch, enum width, and flags.
Three alternating Apple M3 Pro Release process pairs retained exact checksums
at `0.985` ProGPU/native (`1.506` versus `1.528` ns/op) with the same amortized
`0.00048` bytes per operation from the one required descriptor object per
100,000-operation sample. Matched Time Profiler/Allocations captures measured
`1.495`–`1.506` versus `1.516`–`1.524` ns/op with identical allocation. The
clean-room contract uses the public
[D3D resource-info API](https://learn.microsoft.com/dotnet/api/skiasharp.grd3dtextureresourceinfo),
[backend-state API](https://learn.microsoft.com/dotnet/api/skiasharp.grbackendstate),
and Microsoft's
[D3D12 resource-state model](https://learn.microsoft.com/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12).

### Borrowed GPU backend wrappers

`GRBackendTexture` and `GRBackendRenderTarget` now derive from the official
`SKObject` ownership base and match the public GL, Vulkan, Metal, and Direct3D
constructor, backend, dimensions, size, rectangle, validity, mip, sample,
stencil, GL-query, and protected-disposal contracts. The wrappers retain only
typed descriptor metadata and one synthetic managed wrapper identity; they
never create, upload, transition, submit, or destroy the caller-owned native
resource. The existing ProGPU `GpuTexture` constructors remain explicit Dawn
extensions so Avalonia, LibreWPF, LibreWinForms, and media composition can
share a typed zero-copy WebGPU texture without reflection or an intermediate
pixel copy.

All property and descriptor queries are fixed `O(1)` CPU work and allocate
nothing after wrapper construction. GL `TryGet` overloads fail closed with a
default descriptor for non-GL backends. Disposal invalidates only the wrapper
and does not dispose the supplied D3D descriptor or WebGPU/native texture.
Independent tests cover the exact non-sealed hierarchy, declared protected
overrides, constructor parameter names, backend classification, immutable
geometry, GL success/failure, mip/sample propagation, invalidation, and
borrowed ownership across all five backend identities.

Three alternating Apple M3 Pro Release process pairs retained the exact native
checksum. The combined metadata query measured `2.340` ns/op for ProGPU versus
`14.863` ns/op for native (`0.157` ratio), with amortized construction at
`0.001` versus `0.002` bytes per operation. Matched Xcode Time Profiler runs
measured `2.352` versus `14.737` ns/op; matched Allocations runs measured
`2.329` versus `14.540` ns/op with the same bounded construction allocation.
The clean-room contract uses the public
[backend texture](https://learn.microsoft.com/dotnet/api/skiasharp.grbackendtexture),
[backend render target](https://learn.microsoft.com/dotnet/api/skiasharp.grbackendrendertarget),
[GL texture query](https://learn.microsoft.com/dotnet/api/skiasharp.grbackendtexture.getgltextureinfo),
[GL framebuffer query](https://learn.microsoft.com/dotnet/api/skiasharp.grbackendrendertarget.getglframebufferinfo),
[Vulkan external-memory rules](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html#memory-external),
[D3D12 resource ownership](https://learn.microsoft.com/windows/win32/direct3d12/creating-committed-resources),
and [WebGPU object ownership](https://www.w3.org/TR/webgpu/#object-model).

### Premultiplied color values

`SKPMColor` now matches the complete 4.151.0 public metadata contract. Scalar
premultiply and unpremultiply are allocation-free fixed-work operations; array
overloads allocate exactly one result array and process `N` colors in `O(N)`
time with `O(1)` auxiliary storage. The implementation retains the official
platform-native N32 packing (RGBA on Apple targets and BGRA on the official
Windows/Linux assets), rounded divide-by-255 premultiplication, and a generated
read-only 8.24 reciprocal table for deterministic unpremultiplication without
per-channel division. It is CPU-only and cannot initialize WebGPU.

Independent tests cover packed identity, logical channels, formatting,
operators, allocation ownership, transparent input, and component bounds. The
matched benchmark exhaustively checks every alpha/component pair and separately
measures scalar and 64-element array overloads against the official package.
On the recorded Apple M3 Pro Release run, all four semantic checksums and
managed allocations matched exactly. ProGPU/native median ratios were `1.014`
for scalar premultiply, `1.121` for scalar unpremultiply, `1.080` for the
64-element premultiply array, and `1.183` for the unpremultiply array. These
small but repeatable remaining CPU gaps are retained as optimization work; this
checkpoint establishes parity without claiming a performance win.
The design used the public
[SkiaSharp contract](https://learn.microsoft.com/dotnet/api/skiasharp.skpmcolor)
and Skia's documented
[premultiplied color](https://api.skia.org/SkColor_8h.html) and
[unpremultiply scale](https://api.skia.org/classSkUnPreMultiply.html)
contracts. No foreign implementation code, source layout, or helper structure
was incorporated.

### OpenType four-byte tags

`SKFourByteTag` now matches all 18 entries in its 4.151.0 metadata contract.
The four-byte readonly value uses OpenType's big-endian display order, preserves
packed `uint` identity, pads non-empty short tags with trailing spaces, truncates
long tags, and preserves native zero identity for null or empty input. Character
construction narrows each UTF-16 code unit to its low byte, matching the
observable API behavior without validating font-table policy at this value
boundary.

Construction, parsing, equality, hashing, and conversions are allocation-free
fixed-work operations. Formatting allocates only its four-character result.
Matched Release checksums cover string/span parsing, construction, conversion,
and formatting. Across three alternating Apple M3 Pro process pairs, value
operations measured `1.127` ProGPU/native and formatting measured `0.127`, with
`32` versus `280` managed bytes per formatted tag. These local figures are evidence for the
slice, not a cross-platform claim. The clean-room design follows the
[OpenType Tag data type](https://learn.microsoft.com/en-us/typography/opentype/spec/otff)
and the public
[SkiaSharp parsing contract](https://learn.microsoft.com/dotnet/api/skiasharp.skfourbytetag.parse).

### Red/blue pixel channel swizzle

`SKSwizzle` now matches all six entries in the 4.151.0 public metadata
contract. The reusable `PixelChannelSwizzler` core operates on tightly packed
four-byte pixels in `O(N)` time and `O(1)` auxiliary storage, supports bounded
overlapping copies, and never initializes WebGPU. On ARM64, copy and in-place
paths use fixed 32-bit and 16-bit byte reversals followed by a mask select,
avoiding both an intermediate buffer and table-lookup stalls. Other targets use the
portable hardware-accelerated `Vector128` shuffle and scalar tails.

Independent tests cover in-place and copy overloads, pointer entry points,
count clamping, overlap direction, stable replay allocations, and incomplete
trailing pixels. Valid complete-pixel inputs match the official behavior. The
span-only overload deliberately preserves an incomplete trailing pixel rather
than allowing the official wrapper's observable out-of-bounds native access;
this is a memory-safety improvement outside the documented complete-pixel
contract. Three alternating Apple M3 Pro Release process pairs retained equal
managed allocations and exact semantic checksums. Copy measured `0.962`
ProGPU/native and in-place measured `1.213`; the latter remains an explicit CPU
optimization target. Matched Time Profiler and Allocations traces from the same
Release binaries retained stable checksums and `0.824`/`0.412` managed bytes per
operation for both implementations. The raw distributions, trace bundles, and
exported sample tables remain diagnostic evidence rather than a cross-platform
claim.
The design follows the public
[SkiaSharp swizzle contract](https://learn.microsoft.com/dotnet/api/skiasharp.skswizzle)
and Skia's documented
[RGBA/BGRA transform](https://api.skia.org/SkSwizzle_8h.html).

### Native compatibility version

`SkiaSharpVersion` now matches all four entries in its 4.151.0 metadata
contract. The clean-room shim reports the observed `151.0` native and minimum
compatibility levels and succeeds in both throwing and non-throwing check modes
because ProGPU supplies the complete implementation without loading a separate
native Skia binary. Both properties share one immutable process-wide `Version`,
so repeated queries are allocation-free fixed `O(1)` operations.

Independent tests cover exact version values, compatibility modes, stable
identity, and one million allocation-free queries. Three alternating Apple M3
Pro Release process pairs produced exact semantic checksums; ProGPU measured
`0.066` of native time and `0` versus `32` managed bytes per operation. The
clean-room behavior follows the public
[SkiaSharpVersion contract](https://learn.microsoft.com/dotnet/api/skiasharp.skiasharpversion)
and retains no native-library discovery or loader side effects.

### Pixel-format and LCD geometry metadata

`SkiaExtensions` now matches all 18 entries in the 4.151.0 metadata contract,
replacing the former non-official `SKGlExtensions` identity. Pixel-geometry
classification, byte and bit-shift sizes, alpha compatibility, and OpenGL sized
formats cover all 29 declared color types. Unknown declared formats retain
their documented zero values, while out-of-range enum values fail with the
official `colorType` argument boundary. `SKImageInfo` now delegates to the same
single format-size contract instead of retaining a second mapping.

Every valid query is allocation-free fixed `O(1)` CPU work and cannot initialize
WebGPU. Independent tests exhaust the color-type and alpha-type matrices,
geometry categories, GL mappings, invalid enums, and one million stable queries.
The source-built Avalonia.Skia projects continue to compile for net8 and net10
against the official extension identity. Three alternating Apple M3 Pro Release
process pairs produced exact checksums and zero allocations; ProGPU measured
`0.683` of native time for the combined workload. Matched Time Profiler and
Allocations captures from the same binaries preserved that ordering, exact
checksums, and zero managed bytes per operation. The clean-room contract uses
the public
[SkiaExtensions API](https://learn.microsoft.com/dotnet/api/skiasharp.skiaextensions),
[Skia color-type documentation](https://api.skia.org/SkColorType_8h.html), and
[Khronos sized internal formats](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glTexStorage2D.xhtml).

### UTF text conversion utilities

`StringUtilities` now matches all ten entries in the 4.151.0 metadata contract.
UTF-8, little-endian UTF-16, and little-endian UTF-32 conversion use replacement
fallbacks, return exactly one owned byte array or string, expose bounded array,
span, slice, and pointer decode overloads, and reject glyph-ID or out-of-range
encodings before conversion. Encoding is `O(C + B)` and decoding is `O(B + C)`
for `C` UTF-16 code units and `B` encoded bytes, with only the caller-owned
result allocation and no WebGPU initialization.

`GetUnicodeCharacterCode` validates exactly one complete Unicode scalar and
returns it allocation-free for every supported UTF encoding. This intentionally
corrects the official 4.151 wrapper's observable short-buffer failure for
ordinary UTF-8/UTF-16 characters while retaining the documented API contract;
incomplete surrogates and multiple scalars fail before returning partial data.
Independent tests cover exact byte forms, supplementary scalars, replacement
fallbacks, pointer/slice boundaries, null/empty ownership, invalid encodings,
and glyph-ID rejection. Three alternating Apple M3 Pro Release process pairs
produced exact checksums for matched workloads: roundtrip conversion measured
`0.960` ProGPU/native with equal `290.651` managed bytes per operation, while
the scalar query measured `0.041` and `0` versus `256` bytes. The clean-room
Matched Time Profiler and Allocations traces from the same Release binaries
retained the checksum, allocation, and timing ordering. The clean-room design
follows the public
[StringUtilities contract](https://learn.microsoft.com/dotnet/api/skiasharp.stringutilities),
[Unicode encoding forms](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-3/),
and [.NET Encoding contract](https://learn.microsoft.com/dotnet/api/system.text.encoding).

### Color-space chromaticity primaries

`SKColorSpacePrimaries` now matches all 35 entries in the 4.151.0 metadata
contract. Eight mutable inline floats retain the red, green, blue, and white
chromaticities; constructors and the public `Values` snapshot preserve caller
ownership. Conversion solves one homogeneous 3x3 primary matrix and applies
Bradford chromatic adaptation into the ICC D50 profile-connection space. The
general conversion is fixed `O(1)` CPU work with no heap allocation or WebGPU
initialization. Degenerate matrices and non-finite or out-of-unit coordinates
fail transactionally with an empty result.

The common sRGB and Display P3/D65 combinations use immutable matrices computed
from the same public chromaticities and D50 model. This keeps the dominant path
at fixed comparisons plus one inline struct copy without weakening arbitrary
gamut support. Independent tests cover value ownership, every mutable scalar,
equality, invalid and degenerate inputs, a zero-y boundary primary, and sRGB/P3
conversion. Three alternating Apple M3 Pro Release process pairs retained exact
semantic checksums and zero managed allocations: the common-gamut workload
measured `0.172` ProGPU/native (`6.001` versus `34.830` ns/op). Matched Time
Profiler and Allocations captures from the same binaries measured `5.904`–
`6.022` versus `33.791`–`33.861` ns/op with the same checksum and zero bytes per
operation. The clean-room design follows the public
[SkiaSharp primaries contract](https://learn.microsoft.com/dotnet/api/skiasharp.skcolorspaceprimaries),
[Skia public color-space contract](https://skia.googlesource.com/skia/+/fc75b5a/include/core/SkColorSpace.h),
and the
[ICC.1:2022 D50/Bradford model](https://www.color.org/specifications/ICC.1-2022-05.pdf).

### Animated-codec frame ABI

`SKCodecFrameInfo` now matches the official sequential layout as well as its
existing public value behavior. Its two Boolean properties use normalized
one-byte storage in the declared native field order, preserving the compact
codec interop contract without exposing the storage fields. All eight public
properties, equality, hashing, and operators remain allocation-free fixed
`O(1)` CPU operations and do not initialize a decoder or WebGPU.

Independent tests inspect the compiled private layout, verify byte rather than
managed-Boolean storage, and exercise property normalization and full-value
equality. Three alternating Apple M3 Pro Release process pairs retained exact
checksums and zero allocations at `1.001` ProGPU/native (`1.255` versus `1.254`
ns/op), which is performance-neutral at timer resolution. Matched Time Profiler
and Allocations captures retained exact checksums and zero bytes per operation;
their medians ranged from `1.220`–`1.291` ns/op for ProGPU and `1.196`–`1.226`
ns/op for native. The clean-room contract follows the public
[SKCodecFrameInfo API](https://learn.microsoft.com/dotnet/api/skiasharp.skcodecframeinfo)
and the official package's ECMA-335 sequential field metadata.

### Encoder and XPS descriptor ABI

`SKJpegEncoderOptions`, `SKPngEncoderOptions`, and `SKDocumentXpsOptions` now
match their official sequential layouts in addition to retaining their existing
public value contracts. JPEG keeps its three value fields followed by zeroed
metadata pointer/length/origin transport slots; PNG keeps its filter and level
followed by three zeroed native pointer slots; XPS uses one float and a
normalized byte-backed Boolean. The private transport fields are never exposed,
dereferenced, or used to add an external encoder dependency.

Construction, property access, equality, and hashing remain fixed `O(1)` CPU
work with zero allocation and no codec or WebGPU initialization. Independent
tests inspect the compiled private field order/types and verify all public
values. Three alternating Apple M3 Pro Release process pairs retained exact
checksums and zero allocations at `1.024` ProGPU/native (`1.261` versus `1.232`
ns/op), within timer noise for the combined value workload. Matched Time
Profiler and Allocations captures retained the exact checksum and zero bytes;
ProGPU measured `1.169`–`1.217` versus native `1.190`–`1.221` ns/op. The
clean-room contract follows the public
[JPEG options API](https://learn.microsoft.com/dotnet/api/skiasharp.skjpegencoderoptions),
[PNG options API](https://learn.microsoft.com/dotnet/api/skiasharp.skpngencoderoptions),
[XPS options API](https://learn.microsoft.com/dotnet/api/skiasharp.skdocumentxpsoptions),
and the pinned package's ECMA-335 sequential field metadata.

### Primitive overload and rounded-rectangle ownership checkpoint

The point, size, rectangle, color, and rounded-rectangle families now match 41
additional entries in the official 4.151.0 reference contract. This checkpoint
preserves the existing fixed-work value algorithms while aligning official
parameter metadata, adds allocation-free `ReadOnlySpan<char>` color parsing,
and makes `SKRoundRect` participate in the official `SKObject` ownership and
idempotent-disposal hierarchy. Primitive arithmetic and parsing remain `O(1)`
CPU work with no WebGPU initialization; rounded-rectangle construction owns one
bounded four-corner array and one managed handle, with no native resource.

The clean-room contract was derived from the public
[SkiaSharp primitive API documentation](https://learn.microsoft.com/dotnet/api/skiasharp.skpoint),
[SKColor parsing API](https://learn.microsoft.com/dotnet/api/skiasharp.skcolor),
[SKRoundRect API](https://learn.microsoft.com/dotnet/api/skiasharp.skroundrect),
and the pinned package's ECMA-335 public reference metadata. Independent tests
cover all newly aligned parameter names, span parsing output and steady-state
allocation, and the `SKObject` handle lifetime. Repeatable matched workloads
exercise point arithmetic, span parsing, and rounded-rectangle construction and
disposal against the official package. Three alternating Apple M3 Pro Release
process pairs retained exact checksums: canonical span parsing measured `0.491`
ProGPU/native (`11.358` versus `23.147` ns/op) with zero allocation, while
rounded-rectangle lifetime measured `0.535` (`44.656` versus `83.425` ns/op)
with `120` versus `80` managed bytes per owned instance. The extra 40 bytes are
the managed handle/lifetime state required by the official `SKObject` contract.
Matched Time Profiler captures measured parsing at `11.185` versus `22.316`
ns/op and rounded-rectangle lifetime at `44.827` versus `86.215` ns/op;
Allocations retained the same zero/120 versus zero/80 byte ordering. Metal
System Trace exported zero target command-buffer, device-allocation, and Metal
resource-allocation rows for both CPU-only binaries.

The pinned Svg.Skia `03f64b67badfca9fca216dc25896d0c0ee04e7b7`
validation remained stable after the preceding variable-font slice: native W3C
reported 530 passed and 3 skipped; the ProGPU raw lane reported 485 passed, 45
reviewed known differences, and 3 skipped; the resvg lane reported 927 passed
and 37 intentional skips; and the remaining suite passed 1,147 of 1,147 tests.
The repository's parity verifier accepted the complete reviewed-difference
inventory.

### Typeface arguments and path-builder metadata checkpoint

`SKTypeface` and `SKPathBuilder` now match 16 additional entries in the
official 4.151.0 contract. Typeface cloning combines a collection face,
variation coordinates, a CPAL palette, and caller overrides into one immutable
font instance. ProGPU.Text owns the package-neutral `FontPaletteOverride` and
`TtfFont.WithColorPalette` primitive so WinUI, WPF, WinForms, and the SkiaSharp
shim share the same color-glyph path. The first selected non-default palette is
`O(B + A + P)` time and `O(B + P)` storage for font bytes `B`, variation axes
`A`, and palette entries `P`; non-color fonts and the default palette reuse the
font in `O(1)`. Repeated variation instances continue to use the bounded
32-entry normalized-coordinate cache. `SKPathBuilder` changes in this slice are
metadata-only delegates and preserve its retained analytic geometry behavior.

The clean-room design follows the public
[SKFontArguments contract](https://learn.microsoft.com/dotnet/api/skiasharp.skfontarguments),
[SKTypeface clone contract](https://learn.microsoft.com/dotnet/api/skiasharp.sktypeface.clone),
and the authoritative
[OpenType CPAL table](https://learn.microsoft.com/typography/opentype/spec/cpal)
and [COLR table](https://learn.microsoft.com/typography/opentype/spec/colr)
formats. It adopts CPAL's base-zero palette and entry indices, contiguous BGRA
records, unpremultiplied sRGB values, and palette-zero fallback; it adapts them
to immutable linear-float render colors and rejects out-of-range override
entries without mutating the source typeface. Independent tests cover combined
collection/variation/palette arguments, non-color reuse, official legacy
parameter names, and path-builder ownership metadata. The matched
`font-arguments-clone` workload uses the same Inter variable-font bytes and
semantic checksum in both binaries.
Three alternating Apple M3 Pro Release process pairs measured the combined
arguments clone at `0.005` ProGPU/native (`156.791` versus `30,604.417` ns/op)
and `88` versus `112` managed bytes per operation. This workload exercises the
bounded repeated-instance path; a first non-default CPAL materialization is
reported separately because it necessarily copies font storage and palette
records. Matched Time Profiler captures measured `168.188` versus `30,561.271`
ns/op; Allocations retained `88` versus `112` bytes. Metal System Trace exported
zero target command-buffer, device-allocation, and resource-allocation rows for
both CPU-only binaries.

### Global graphics controls and OpenGL state checkpoint

The official `SKGraphics`, `SKTraceMemoryDump`, `GRGlBackendState`, and
`SKBlender.CreateArithmetic` contracts close 41 additional 4.151.0 metadata
entries. Cache budgets use atomic process-wide values; setters return the prior
budget, reads are fixed `O(1)`, and the compatibility counters and dump callbacks
do not initialize WebGPU. Purge entry points are safe idempotent boundaries for
the shim's process caches. `GRGlBackendState` preserves the official 16-bit
OpenGL invalidation mask exactly, while ProGPU's WebGPU backend continues to use
its typed resource ownership instead of interpreting GL state bits.

Independent tests cover every state-mask group, atomic budget round trips,
negative-budget rejection, cache accounting, and protected memory-dump
callbacks. The repeatable `graphics-cache-controls` workload performs two
atomic setter/getter pairs per operation with identical native and ProGPU
checksums and zero managed allocation. The design follows the public
[SKGraphics API](https://learn.microsoft.com/dotnet/api/skiasharp.skgraphics),
[SKTraceMemoryDump API](https://learn.microsoft.com/dotnet/api/skiasharp.sktracememorydump),
and the pinned package's ECMA-335 enum and method metadata.
Three alternating Apple M3 Pro Release process pairs measured `0.131`
ProGPU/native (`2.373` versus `18.165` ns/op), with zero allocation. Matched
Time Profiler captures measured `2.408` versus `17.892` ns/op; Allocations
retained zero bytes per operation. Metal System Trace exported zero target
command-buffer, device-allocation, and resource-allocation rows for both
CPU-only binaries.

### Typed platform runtime checkpoint

`PlatformConfiguration`, `IPlatformLock`, `PlatformLock`, and
`SKAutoCoInitialize` close 26 additional entries in the official 4.151.0
metadata contract. Runtime flags use the platform and process-architecture
information supplied by .NET, while the mutable Linux flavor remains an atomic
process-wide compatibility setting. The default lock is a typed
`ReaderWriterLockSlim` adapter supporting read, upgradeable-read, write, and
recursive entry without reflection or per-entry allocation. Lock entry and
exit are fixed `O(1)` work when uncontended and use the runtime lock's bounded
per-instance state; contention has scheduler-dependent wait time. On Windows,
`SKAutoCoInitialize` balances each successful multithreaded-apartment
initialization, including `S_FALSE`, with exactly one `CoUninitialize` call.
Other platforms use the same idempotent object lifetime without loading a
Windows library or initializing WebGPU.

The clean-room design follows the public
[RuntimeInformation contract](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.runtimeinformation),
[ReaderWriterLockSlim contract](https://learn.microsoft.com/dotnet/fundamentals/runtime-libraries/system-threading-readerwriterlockslim),
[CoInitializeEx contract](https://learn.microsoft.com/windows/win32/api/combaseapi/nf-combaseapi-coinitializeex),
and [CoUninitialize balance rule](https://learn.microsoft.com/windows/win32/api/combaseapi/nf-combaseapi-couninitialize).
Independent tests cover platform-flag consistency, factory replacement,
recursive read, upgradeable/read/write modes, one million steady-state lock
pairs with zero managed allocation, and idempotent COM lifetime behavior.
Three alternating Apple M3 Pro Release process pairs retained the exact native
checksum. The read-lock pair measured `1.194` ProGPU/native (`9.241` versus
`7.737` ns/op); both harnesses reported only the same amortized `0.0012` B/op
one-time measurement overhead. Matched Time Profiler captures measured `9.194`
versus `7.757` ns/op, while Allocations captures measured `9.171` versus
`7.907` ns/op with the same checksum and allocation result. Metal System Trace
exported zero target command-buffer, device-allocation, and resource-allocation
rows for both CPU-only binaries.

### WebGPU surface ownership and snapshot checkpoint

`SKSurface` and `SKSurfaceReleaseDelegate` now close all 65 missing entries in
their official 4.151.0 contracts. Surfaces participate in the shared
`SKObject` lifetime, snapshot immutable surface properties, retain the typed
`GRRecordingContext`, and expose the complete raster, recording-context,
backend-texture, backend-render-target, sample-count, origin, color-space, and
mipmap overload families. Caller WebGPU textures and external pixel pointers
remain borrowed and zero-copy. External release callbacks run exactly once.
Ordinary WebGPU surfaces allocate no CPU mirror until `PeekPixels` is requested;
the first peek performs one explicit readback and retains a stable pointer,
while later GPU flushes update that view. A bounded snapshot performs a direct
texture-to-texture region copy and stays GPU-backed until an explicit readback.

Wrapped-surface creation is `O(1)` CPU work and storage. Rendering remains
`O(C + P)` for retained commands `C` and affected pixels `P`. A snapshot uses
`O(1)` command-encoding work, `O(P)` GPU bandwidth, and one destination texture;
readback and first peek use `O(P)` transfer/conversion work. Null surfaces own no
texture or GPU context, do not initialize WebGPU, and discard retained commands
at flush. Independent tests cover the
official ownership hierarchy, surface-property isolation, typed contexts,
every overload family through the metadata verifier, stable lazy CPU views,
one-shot release callbacks, null surfaces, bounded GPU snapshots, and existing
backend target/origin/readback behavior.

The clean-room architecture review used Skia's public
[surface contract](https://api.skia.org/classSkSurface.html) and
[canvas/surface model](https://skia.org/docs/user/api/), Direct2D's
[device-dependent render-target model](https://learn.microsoft.com/windows/win32/direct2d/render-targets-overview),
Win2D's
[incremental offscreen target contract](https://learn.microsoft.com/windows/apps/develop/win2d/offscreen-drawing),
WebRender's
[display-list, scene, frame, and GPU submission split](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
and Vello's
[explicit wgpu scene-to-texture pipeline](https://github.com/linebender/vello).
ProGPU adopts explicit device ownership, retained commands, incremental target
contents, immutable snapshots, and GPU-native copies; it rejects API-specific
GL/Vulkan/Metal handle interpretation in favor of typed WebGPU resources. The
required text-stack review also covered Skia's
[text architecture](https://skia.org/docs/dev/design/text_overview/),
DirectWrite's
[layout/render separation](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite),
and HarfBuzz's
[buffer shaping contract](https://harfbuzz.github.io/harfbuzz-hb-shape.html).
Those CPU-reusable shaping/layout boundaries remain unchanged by this surface
slice.

Three alternating Apple M3 Pro Release process pairs retained the exact native
checksum for 32-by-32 bounded snapshots from a stable 64-by-64 surface. Native
raster copy-on-write measured `453.540` ns/op and `120.8` B/op; ProGPU's current
explicit WebGPU copy-and-submit path measured `65,455.835` ns/op and `442`
B/op. Removing per-snapshot native label marshalling reduced the ProGPU managed
cost from `666.4` to `442` B/op. Matched Time Profiler captures measured
`437.705` versus `67,390.415` ns/op, and Allocations captures measured
`2,217.085` versus `95,860.205` ns/op with the same byte counts. Metal System
Trace correctly reported no native raster work and recorded 3,275 ProGPU
command-buffer rows, 4,204 current-allocation rows, and 175 resource-allocation
rows. This is a documented
performance blocker for the final parity release: repeated immutable snapshots
still need deferred/batched submission and shared copy-on-write texture
ownership before ProGPU can meet the goal's matched native latency and
allocation criterion.

### Immutable image ownership and GPU subset checkpoint

`SKImage`, `SKImageRasterReleaseDelegate`, and
`SKImageTextureReleaseDelegate` now close all 65 missing entries in their
official 4.151.0 contracts. The complete factory surface covers raster
creation, immutable pixel copies, caller-owned pixmaps, encoded data and files,
pictures, borrowed and adopted backend textures, recording contexts, color and
alpha metadata, release callbacks, raster/texture conversion, filter
application, shaders, and subsets. Caller pixel and texture release callbacks
run exactly once with their original pointer/context. Encoded images retain an
independent encoded snapshot. Raster `PeekPixels` materializes one stable
pinned CPU view; a GPU-backed image does not silently claim a CPU pointer.

Contained subsets are immutable `O(1)` texture-region views. One atomic
reference retains the source texture storage and the view composes bounded
CPU-pixel and GPU-texture origins; creating, nesting, or disposing a subset
performs no pixel copy, command encoding, queue submission, or GPU allocation.
The final owner releases an adopted texture and invokes its borrowed-texture
release callback exactly once, so a subset remains valid after its parent is
disposed. Raster provenance remains observable as
raster and texture provenance remains observable as texture; texture-backed
subsets require the matching recording context.

Region materialization is deferred to the operation that requires an independent
resource. Same-context texture conversion and retained image drawing issue one
typed base-level WebGPU rectangle copy, and texture conversion can generate
mip levels afterward. A CPU read requests only the view rectangle; immutable
raster-backed views copy directly from their retained row-stride storage, while
GPU-only views use one bounded readback texture. Cross-context conversion uses
one explicit tight upload because WebGPU resources cannot be copied between
devices. Filter application runs through ProGPU's retained WebGPU filter graph
and clips its output to the caller's expected device bounds. Creation and
wrapping validation are `O(1)` apart from required pixel ownership; view
creation is `O(1)` time/storage and one managed wrapper; materialization and
cross-device transfers are `O(P)` bandwidth and storage for `P` view pixels.

Independent tests cover stride-aware immutable copies, stable raster views,
encoded ownership, exact-once raster/texture callbacks, borrowed versus adopted
textures, shared and nested region views, parent-before-child disposal,
contained GPU rectangle materialization, invalid subsets, mip generation, and
filtered output bounds. The focused image/surface contract selection passes 87
tests. The metadata verifier at this image checkpoint reported 4,222 official
entries,
4,933 candidate entries, 3,756 exact matches, 466 missing entries, and 1,177
documented extensions.
The isolated package gate also produced the runtime and Avalonia 11/12
integration packages in a fresh feed, then restored and built the package-only
Avalonia consumer with zero warnings or errors.

The clean-room architecture uses Skia's public
[image contract](https://api.skia.org/classSkImage.html),
[image factory contract](https://api.skia.org/namespaceSkImages.html), and
[filter-bounds model](https://api.skia.org/classSkImageFilter.html), WebGPU's
[texture-copy validation and ordering model](https://www.w3.org/TR/webgpu/#dom-gpucommandencoder-copytexturetotexture),
Direct2D's
[source-rectangle bitmap model](https://learn.microsoft.com/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1devicecontext-drawbitmap%28id2d1bitmap_constd2d1_rect_f_float_d2d1_interpolation_mode_constd2d1_rect_f_constd2d1_matrix_4x4_f%29),
Win2D's
[CanvasBitmap contract](https://learn.microsoft.com/uwp/api/microsoft.graphics.canvas.canvasbitmap),
WebRender's
[external-image and frame split](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
and Vello's
[explicit wgpu scene-to-texture pipeline](https://github.com/linebender/vello).
The Skia/SkParagraph, DirectWrite/Direct2D, Win2D, WebRender, Vello/Parley,
and HarfBuzz shaping/layout review recorded by the surface checkpoint remains
unchanged: image ownership does not move Unicode/OpenType shaping onto the GPU.

Three alternating Apple M3 Pro Release process pairs retained the exact native
checksum for 32-by-32 subsets of a stable 64-by-64 image. The final shared-view
implementation measured `399.790` ns/op and `402.64` managed B/op versus native
raster copy-on-write at `675.210` ns/op and `106.08` B/op (`0.592` latency
ratio). Relative to the previous ProGPU immediate-copy result, this reduces
median latency from `38,778.335` ns/op by 99.0% and managed allocation from
`722.08` B/op by 44.2%. ProGPU's remaining managed-byte difference is its
visible managed image/view ownership while the native counter excludes Skia's
native object allocation, so no total-memory advantage is inferred.

Matched final-binary Time Profiler, Allocations plus VM Tracker, and Metal
System Trace captures all completed. For the same workload, Xcode's persistent
native heap plus anonymous VM fell from `165,785,280` to `110,526,736` bytes,
and total native heap bytes fell from `728,675,488` to `196,383,680` bytes.
The former Metal trace exported 6,429 command-buffer submission rows, 4,509
`currentAllocatedSize` rows, and 268 resource-allocation rows; the final trace
contains no modeled target Metal track because subset creation no longer
records or submits GPU work. These whole-process Instruments numbers include
runtime/device startup and are correlated evidence rather than per-operation
allocation claims. Before/final raw traces, TOCs, exported tables, and exact-run
JSON are retained under `artifacts/performance/skiasharp-image-api-instruments`
and `artifacts/performance/skiasharp-image-subset-zero-copy-instruments`.

The benchmark workflow now installs the same Linux Vulkan prerequisites as the
main build and resolves the packaged RID-native WebGPU directory on Linux,
macOS, and Windows. This fixes the prior Ubuntu `libwgpu_native` loader failure
without skipping the GPU workload or relaxing comparison evidence.

### Retained canvas contract and empty-clip checkpoint

`SKCanvas` now closes all 45 missing entries in its official 4.151.0 owner
contract plus the two missing readonly matrix-parameter attributes. It derives
from `SKObject`, owns one stable compatibility handle, and clears that handle
through the shared idempotent lifetime. Official parameter names, optional
values, and compile-time-obsolete text overloads now match the reference
metadata. Rectangle and path clips use the official non-antialiased default;
explicit antialias choices continue through the same typed retained API.

Bitmap, image, surface, lattice, nine-patch, picture, primitive, and text
overloads remain thin routes into the existing retained WebGPU command graph.
An empty saved clip scope is now removed transactionally on restore instead of
retaining a large general push/pop command pair. This peephole is fixed `O(1)`
time and storage and is valid only when no command was recorded after the push;
a scope containing drawing retains its balanced push, content, and pop. After
one capacity warmup, 100,000 empty save/clip/restore cycles allocate exactly
zero managed bytes and leave no commands. Drawn clips remain `O(C)` retained
storage for commands `C`; lattice construction remains `O((X + 1)(Y + 1))`
patch work for `X` and `Y` divider counts and submits those patches through one
retained image source rather than uploading once per patch.

The clean-room design follows Skia's public
[canvas and lattice contract](https://api.skia.org/classSkCanvas.html),
Direct2D's
[device-context bitmap contract](https://learn.microsoft.com/windows/win32/direct2d/id2d1devicecontext-drawbitmap-overload),
Win2D's
[retained offscreen drawing model](https://learn.microsoft.com/windows/apps/develop/win2d/offscreen-drawing),
WebRender's
[display-list, spatial-tree, clip-tree, and frame split](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
and Vello's
[wgpu scene-to-texture architecture](https://github.com/linebender/vello).
ProGPU adopts retained draw routing, separate transform/clip state, one image
source per lattice, and GPU submission after scene recording; it rejects
immediate CPU rasterization and API-specific native-handle branches. The
required text review used Skia's
[text architecture](https://docs.skia.org/docs/dev/design/text_overview/),
DirectWrite's
[layout/render separation](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite),
and HarfBuzz's
[buffer shaping contract](https://harfbuzz.github.io/harfbuzz-hb-shape.html).
Canvas overload alignment therefore leaves reusable shaping and glyph
placement on the existing CPU-result boundary and changes only retained draw
routing.

The isolated package gate produced all runtime and Avalonia 11/12 integration
packages in a fresh feed, then restored and built the package-only Avalonia
consumer with zero warnings or errors.

The exact-checksum Apple M3 Pro Release workload performs 10,000
save/scale/concat/clip/restore cycles per sample. Before empty-scope elision,
ProGPU measured `3,839.419` ns/op and `6,979.893` B/op. Afterward it measured
`679.500` ns/op and `0.7792` amortized B/op, versus native `213.525` ns/op and
`0.1752` B/op: an 82.3% ProGPU latency reduction and more than 99.98% allocation
reduction, while the remaining direct-run latency is still a documented
optimization target. Matched Time Profiler captures measured `190.623` versus
`195.177` ns/op; Allocations captures `184.444` versus `198.840` ns/op with
the same managed allocation counts; Metal System Trace captures `194.098`
versus `203.783` ns/op. Both Metal traces export zero target command-buffer,
device-allocation, and resource-allocation rows, confirming this state-only
path does not initialize WebGPU. Raw traces, TOCs, exported Metal tables, and
exact-run JSON are retained under
`artifacts/performance/skiasharp-canvas-api-instruments`.

### Retained shader factory contract checkpoint

`SKShader` now closes all 33 entries that remained missing from its official
4.151.0 owner contract. The public bitmap, image, and picture factories use the
official `src`, `tmx`, `tmy`, and `tile` parameter names; float-color gradient
factories consistently expose `colorspace`; and compose/filter wrappers expose
the official `shaderA`, `shaderB`, and `filter` names. This is metadata parity
over the existing original retained implementation, not a native Skia call or
source port.

Color, gradient, picture, image, local-matrix, color-filter, noise, and composed
shader nodes keep immutable ownership. Gradient colors and offsets are
converted and clamped once in `O(S)` time and `O(S)` retained storage for `S`
stops. Every `ToBrush` call returns an independent compact stop array so caller
mutation cannot alter the shader. Linear-color spaces select scRGB-linear
interpolation; tile modes and the inverse local matrix survive through linear,
radial, two-point conical, and sweep gradients. Image shaders continue to own
one retained texture snapshot with explicit nearest/linear/mipmap/cubic
sampling rather than uploading once per tile. Actual gradient evaluation,
tiled texture sampling, composition, and post-filter work remain in ProGPU's
retained WebGPU render/compute paths; factory construction is intentionally
CPU-only and does not initialize WebGPU.

The clean-room design follows Skia's public
[shader contract](https://api.skia.org/classSkShader.html) and
[gradient degeneracy rules](https://api.skia.org/classSkGradientShader.html),
Direct2D's
[solid, gradient, image, and bitmap brush model](https://learn.microsoft.com/windows/win32/direct2d/direct2d-brushes-overview),
Win2D's
[color-space-aware linear gradient contract](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Brushes_CanvasLinearGradientBrush.htm),
and WebGPU's
[immutable samplers, addressing, filtering, and external-texture model](https://gpuweb.github.io/gpuweb/).
ProGPU adopts immutable factory state, explicit interpolation and addressing,
and deferred GPU evaluation; it rejects render-target-bound public resources,
per-tile uploads, CPU raster fallbacks, and backend-specific public handles.
The required Skia/SkParagraph, DirectWrite/Direct2D, Win2D, WebRender,
Vello/Parley, and HarfBuzz review recorded by the canvas checkpoint remains the
shaping/layout boundary: this shader-only slice does not change text shaping,
glyph caching, or CPU layout reuse.

The exact-checksum Apple M3 Pro Release workload creates and disposes linear,
radial, sweep, and two-point conical float-color gradients with three stops,
different tile modes, one sRGB color space, and one local matrix.
ProGPU measured `850.979` ns/op and `1,448` managed B/op versus native
`2,353.083` ns/op and `416` managed B/op (`0.362` latency ratio). The extra
managed bytes are ProGPU's visible immutable stop/closure ownership, whereas
the native harness does not count Skia's native allocations; reducing the
managed representation remains an optimization target and no total-memory
advantage is claimed from this counter alone. Matched Time Profiler captures
measured `853.521` versus `2,389.021` ns/op, Allocations captures `838.396`
versus `5,591.000` ns/op with the same `1,448` versus `416` managed B/op, and
Metal System Trace captures `846.980` versus `2,414.667` ns/op. Both Metal
traces export zero target command-buffer, current-allocation-size, and resource
allocation rows, confirming factory construction remains CPU-only. Raw traces,
TOCs, exported Metal tables, and exact-run JSON are retained under
`artifacts/performance/skiasharp-shader-api-instruments`.

### WebGPU recording-context and backend descriptor checkpoint

The `GRContext` cluster now closes 75 official 4.151.0 metadata entries across
the direct recording context, its options, GL interface, Vulkan extensions,
typed GL/Vulkan/Metal/Direct3D descriptors, procedure-address delegates, and
their disposal contracts. Backend descriptors are CPU-only borrowed-handle
DTOs. Their disposal never releases caller-owned API objects, while
`GRGlInterface` and `GRVkExtensions` own only their managed compatibility
handles and immutable extension metadata.

Every public factory maps to ProGPU's process-wide typed `WgpuContext`; the
foreign GL, Vulkan, Metal, or Direct3D descriptor selects a compatibility entry
point but is never exposed as ProGPU's device ownership. A `GRContext` wrapper
does not own that shared WebGPU device. Abandonment is local and idempotent, so
abandoning or disposing one wrapper cannot invalidate another wrapper or an
Avalonia/WinUI/WPF/WinForms host sharing the device. `Flush` and asynchronous
`Submit` poll the queue without an idle wait because ProGPU submits recorded
render/compute work at the owning surface/compositor boundary; synchronous
submission uses the existing device wait. Reset is an `O(1)` state-coherency
acknowledgement because WebGPU tracks explicit immutable pipeline and bind-group
state rather than a mutable GL state vector.

The compatibility cache budget is an atomic `O(1)` wrapper value. Usage reports
the exact process-device shader-module, bind-group-layout, pipeline-layout,
render-pipeline, and compute-pipeline counts and reports zero bytes when the
backend cannot attribute shared GPU residency to one wrapper. Purging processes
the context's deferred resource-release queue but never destroys leased shared
pipelines or another presentation context's atlases. The memory dump therefore
reports bounded counts, the configured limit, and the WebGPU backend without
inventing per-wrapper native allocation totals.

The clean-room design follows Skia's public
[direct-context submission, abandonment, and cache contract](https://api.skia.org/classGrDirectContext.html),
WebGPU's
[device/queue timeline and completion semantics](https://gpuweb.github.io/gpuweb/),
and Direct3D 12's
[explicit command-list, queue, and fence ownership](https://learn.microsoft.com/windows/win32/direct3d12/executing-and-synchronizing-command-lists).
It adopts explicit submission, shared-device lifetime, device-loss observation,
and bounded deferred cleanup; it rejects fake native-backend ownership,
unconditional idle waits, and eviction of live cross-host resources. The
required Skia/SkParagraph, DirectWrite/Direct2D, Win2D, WebRender,
Vello/Parley, and HarfBuzz review recorded above remains unchanged because this
slice does not alter scene compilation, shaping, layout, or glyph residency.

The exact-checksum Apple M3 Pro Release workload constructs and reads every
official `GRContextOptions` property 100,000 times per sample. Native measured
`8.389` ns/op and `32` B/op; ProGPU measured `8.252` ns/op and `32` B/op
(`0.984` latency ratio). Matched Time Profiler captures measured `7.906` versus
`8.135` ns/op, Allocations captures `7.906` versus `7.820` ns/op, and both
retain exactly `32` managed B/op. Metal System Trace captures measured `8.115`
versus `8.357` ns/op. The ProGPU trace exports zero target command-buffer,
current-allocation-size, and resource-allocation rows. The native Metal trace
and TOC were retained, but exporting its individual Metal tables reports an
Instruments run error, so no unsupported native row-count claim is made. Raw
traces, TOCs, available exported tables, and exact-run JSON are retained under
`artifacts/performance/skiasharp-gr-context-api-instruments`.

### Legacy path-builder migration contract checkpoint

The 43 legacy `SKPath` mutation overloads now carry the official
`Obsolete("Use SKPathBuilder instead.")` contract without changing their
existing clean-room behavior. The attribute is advisory rather than an error,
so source compatibility remains intact while new callers receive the same
migration signal as the official 4.151.0 surface. An independent metadata test
enumerates every declared public obsolete method, fixes the count at 43, and
verifies the exact message and non-error policy.

This is a metadata-only closure over ProGPU's already validated CPU path view.
Path mutation remains retained, CPU-only `O(1)` work per line/curve operation
and `O(N)` storage for `N` segments; it does not initialize WebGPU, flatten
analytic arcs, or change renderer cache keys. The original clean-room path and
builder checkpoints above continue to define topology, ownership, conic,
iterator, transform, serialization, and performance behavior. Because no
algorithm, allocation path, shader, or rendered output changed, the matched
performance and Instruments evidence for those checkpoints remains applicable;
this slice introduces no executable hot-path work to benchmark.

The public migration policy was derived solely from the pinned NuGet reference
metadata and the official
[`SKPathBuilder` API contract](https://learn.microsoft.com/dotnet/api/skiasharp.skpathbuilder).
No implementation source was consulted. The required cross-engine rendering
review remains unchanged because this checkpoint neither changes scene/path
compilation nor text shaping, caching, or GPU submission.

## Preview.35 full metadata closure and WebGPU mask execution

The pinned official SkiaSharp 4.151.0 comparison now reports 4,222 exact
matches of 4,222 reference entries and zero missing entries. The final slice
closes nullable/obsolete metadata, managed disposal, WebP frame/encoder, pinned
raw text-run buffers, `SKMaskFilter`, `SKNoDrawCanvas`, `SKNWayCanvas`, and
`SKOverdrawCanvas` contracts. Metadata equality is the contract-ledger result;
behavior and performance remain independently gated.

Mask filters retain immutable blur, alpha-table/gamma/clip, and shader
descriptions. Ordinary draw commands remain on the existing direct retained
path. A typed marker activates interception only for filtered brushes; the
source command renders once into a bounded offscreen target and the existing
WebGPU image-filter graph performs separable blur, alpha lookup, or `DstIn`
shader masking. Overdraw uses a dedicated 16-by-16 WebGPU compute shader and a
96-byte six-color uniform, mapping transparent input to transparent output,
counts one through five to their palette entries, and saturated counts to the
last entry. No CPU readback, external codec, reflection, or per-pixel managed
loop is introduced.

The clean-room design used the public
[`SkMaskFilter`](https://api.skia.org/classSkMaskFilter.html) and
[`SkCanvas`](https://api.skia.org/classSkCanvas.html) contracts,
[Direct2D Gaussian blur](https://learn.microsoft.com/windows/win32/direct2d/gaussian-blur),
[Win2D Gaussian blur](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Effects_GaussianBlurEffect.htm),
[WebRender's retained frame architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's wgpu renderer](https://github.com/linebender/vello),
[Parley's reusable layout model](https://github.com/linebender/parley), and
[HarfBuzz shaping](https://harfbuzz.github.io/harfbuzz-hb-shape.html). ProGPU
adopts retained filter descriptions, bounded GPU intermediates, and explicit
compute/composite stages; it rejects copied engine structure, CPU bitmap
fallback, per-frame reflection, and changes to reusable shaping/layout output.

Focused mask/forwarding/shader-resource tests pass, including GPU blur-tail and
overdraw pixel checks. The complete macOS core suite passes 3,167/3,167 and the
headless suite passes 225/225. Three alternating matched Release process pairs
preserve every semantic checksum. The final Apple M3 Pro run records retained
canvas routing at `738.425` versus native `207.625` ns/op (`3.557`), path build
and bounds at `3,793.146` versus `711.500` ns/op (`5.331`), and bounded surface
snapshot at `66,053.955` versus `482.085` ns/op (`137.017`). These gaps remain
explicit optimization work; full metadata closure does not claim an overall
performance win.

The filtered-command marker is now gated behind the presence of an interceptor,
so ordinary framework-neutral command lists do not pay two type tests. Matched
macOS Instruments runs against exact pre-change commit `65cc9641` retained the
same checksum and `0.788` managed B/op: Time Profiler measured `169.313` before
and `165.688` ns/op after, Allocations measured `171.219` and `166.627` ns/op,
and Metal System Trace measured `173.121` and `166.933` ns/op. Target Metal
command-buffer submissions, current device allocation, and resource-allocation
exports are empty in both runs, as expected for state-only recording. Raw
traces, TOCs, table exports, and exact-run JSON are retained under
`artifacts/performance/skiasharp-interceptor-instruments`.

## Preview.36 retained path and immutable snapshot continuation

This continuation closes the three explicit Preview.35 performance slices
without changing the complete 4,222-of-4,222 official 4.151.0 metadata ledger.

Common `SKPathBuilder` move, line, quadratic, cubic, and close operations now
write one pooled contiguous command stream. Bounds are maintained
incrementally, immutable detach transfers ownership in `O(1)`, and the public
`PathGeometry` graph materializes only when requested. Complex conic,
analytic-arc, add-path, transform, reverse, and iterator paths retain the typed
geometry implementation. Construction is CPU-only `O(N)` time and storage for
`N` commands, bounds are `O(1)`, and storage retention is bounded to one
thread-local array of at most 1,024 commands; larger arrays return to the
shared pool.

Surface snapshots now create one immutable full-surface WebGPU texture per
content generation. Bounded images are constant-time shared views with
composed origins and reference-counted lifetime. The next surface command
invalidates only the cache reference; returned images retain the old
generation and preserve the immutable snapshot contract. A generation
performs one GPU texture copy and requires copy-source, copy-destination, and
texture-binding usage; repeated snapshots allocate no texture, submit no copy,
and perform no CPU readback. Borrowed externally mutable targets remain
uncached. Cold cross-context, raster, encoded-data, and release-callback state
is held lazily, so ordinary views do not allocate unrelated locks or maps.

The clean-room design uses Skia's public
[`SkPathBuilder`](https://api.skia.org/classSkPathBuilder.html),
[`SkPath`](https://api.skia.org/classSkPath.html), and
[`SkSurface::makeImageSnapshot`](https://api.skia.org/classSkSurface.html)
contracts; Direct2D's
[path geometry model](https://learn.microsoft.com/windows/win32/direct2d/path-geometries-overview);
Win2D's
[offscreen target model](https://learn.microsoft.com/windows/apps/develop/win2d/offscreen-drawing);
WebGPU's
[texture usage, lifetime, and texel-copy rules](https://gpuweb.github.io/gpuweb/);
WebRender's
[retained display-list architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html);
and Vello's
[compute-centric renderer](https://github.com/linebender/vello). ProGPU adopts
immutable generations, explicit GPU ownership, lazy typed materialization, and
retained-resource reuse. It rejects copied source structure, per-view GPU
copies, CPU readback, unbounded exact-position caches, and GPU initialization
in path construction. SkParagraph, Parley, DirectWrite, and HarfBuzz were also
reviewed at the architecture boundary; this slice does not alter shaping or
line layout, so their reusable CPU result boundary remains unchanged.

Three alternating exact-checksum Apple M3 Pro Release process pairs at commit
`c989623c` produced these medians:

| Workload | Native SkiaSharp | ProGPU | Ratio | Managed B/op, native/ProGPU |
| --- | ---: | ---: | ---: | ---: |
| retained canvas state routing | 206.148 ns | 215.754 ns | 1.047 | 0.175 / 0.788 |
| path build and bounds | 766.542 ns | 512.875 ns | 0.669 | 168 / 224 |
| bounded surface snapshot | 411.052 ns | 223.758 ns | 0.544 | 104.168 / 104.890 |

The former canvas ratio was a Tier-0 measurement artifact. Thirty-two full
warmups stabilize dynamic PGO before sampling; the steady route is within 4.7%
of native and remains below one managed byte per operation. Against exact
Preview.35, packed path construction fell from `2,868.104` to `534.312` ns/op
and from `3,520` to `224` managed B/op. It is faster than the native `764.459`
ns/op result, while native's `168` managed B/op excludes its native
allocations; no unsupported total-memory comparison is made.

Matched macOS profiling compares exact Preview.35 product commit `561a5bd2`
with exact product commit `c989623c`; the baseline harness contains only the
dependency-reference and operation-count changes needed to run the same case.
Time Profiler measured snapshots at `34,701.302` versus `114.981` ns/op.
Allocations plus VM Tracker measured `78,493.290` versus `124.304` ns/op and
`512.834` versus `104.890` managed B/op; raw native tables are retained because
`xctrace` does not expose an allocation-table export schema for this template.
EventPipe measured `34,994.175` versus `147.485` ns/op and attributes the
baseline to per-call `SKSurface.Snapshot`, `GpuTexture.Allocate`, and queue
submission, while the candidate samples the shared-view/reference-count path.

A bounded 100-operation Metal System Trace avoids an unusable multi-gigabyte
baseline while exercising the same path. Baseline/candidate exports contain
4,090/131 command-buffer-submission rows and 198/121 resource allocation or
deallocation rows. The candidate creates the surface backing texture and one
labelled immutable snapshot generation rather than a texture per view. Raw
traces, TOCs, exported tables, exact-run JSON, EventPipe, and Speedscope files
are retained under `artifacts/performance/skiasharp-surface-c989623c`; packed
path evidence is under
`artifacts/performance/skiasharp-packed-path-0cba9fb9`.

The three Preview.35 blockers are closed. Residual matched CPU ratios above
native remain separately visible: platform-lock read `1.139`, PM-color array
unpremultiply `1.165`, string round-trip `1.140`, and in-place 4 KiB swizzle
`1.094`. Image-subset and gradient-factory managed representations also remain
larger where their latency is faster. These are future optimization slices,
not blockers for this release boundary.

## Compact retained gradients after Preview.36

Product commit `4be7dbb1` closes the Preview.36 gradient-factory allocation
item without changing the complete 4,222-of-4,222 SkiaSharp 4.151.0 metadata
ledger or the existing WebGPU gradient renderer. `SKShader` now stores one
typed payload plus a compact kind instead of retaining eight nullable payload
references. Linear, radial, sweep, and two-point-conical gradients use original
typed descriptors rather than closure-backed brush factories. The descriptors
pack spread/interpolation options into one byte and preserve the exact local
matrix, geometry, color-space selection, and immutable stop snapshot.

The common zero-to-three-stop path uses a bounded per-thread last-input lookup.
It reuses compact immutable stop storage only while the exact source array,
positions array, and values remain unchanged. Caller mutation creates a new
snapshot, so existing shaders cannot observe later input changes. More than
three stops always receive an independent owned array. The lookup retains at
most one three-element `SKColor` input and one three-element `SKColorF` input,
their optional positions and immutable snapshots, plus one matrix result per
thread. Factory validation and snapshotting are `O(S)` time for `S` stops;
unchanged common inputs are bounded `O(S)` comparisons with `S <= 3` and no
stop-array allocation. Overflow storage and the public `ToBrush` ownership
boundary remain `O(S)` time and storage. Matrix inversion is fixed `O(1)` work
and is reused only for an exact unchanged matrix. No reflection, unbounded
cache, CPU raster fallback, GPU initialization, or backend-specific public
handle is introduced.

The clean-room review used Skia's public
[`SkGradientShader` contract](https://api.skia.org/classSkGradientShader.html),
[Skia's shaped-text architecture](https://skia.org/docs/dev/design/text_shaper/),
[Direct2D brushes](https://learn.microsoft.com/windows/win32/direct2d/direct2d-brushes-overview),
[Direct2D/DirectWrite separation](https://learn.microsoft.com/windows/win32/direct2d/direct2d-and-directwrite),
[Win2D linear gradients](https://microsoft.github.io/Win2D/WinUI2/html/T_Microsoft_Graphics_Canvas_Brushes_CanvasLinearGradientBrush.htm),
the [WebGPU specification](https://gpuweb.github.io/gpuweb/),
[WebRender's retained-frame model](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's wgpu renderer](https://github.com/linebender/vello),
[Parley's reusable layout model](https://github.com/linebender/parley), and
[HarfBuzz shaping](https://harfbuzz.github.io/harfbuzz-hb-shape.html). ProGPU
adopts immutable retained parameters, explicit interpolation/addressing, typed
ownership, and deferred GPU evaluation. It adapts those contracts to one
framework-neutral descriptor shared by Avalonia, WinUI, WPF, and WinForms. It
rejects copied implementation structure, render-target-bound factory objects,
per-tile uploads, source-array aliasing, and moving Unicode shaping or line
layout onto this shader path. Actual gradient sampling and compositing remain
in the existing WebGPU pipeline.

Three alternating exact-checksum Apple M3 Pro Release process pairs compare
exact Preview.36 commit `7a94fb3c` with the candidate implementation. The
median of run medians fell from `369.452` to `218.160` ns/op (`40.95%`), while
managed allocation fell from `1,480` to `472` B/op (`68.11%`). At 2,000 warmup
passes the same binaries measured `243.584` versus `137.938` ns/op, confirming
the ordering after final dynamic PGO. The stabilized official SkiaSharp
4.151.0 differential measured native `1,283.516` versus ProGPU `198.848`
ns/op with the same checksum. Managed allocation was `416` versus `472` B/op;
the native counter excludes Skia's native heap work, so no unsupported total
memory comparison is made. The baseline harness changes are limited to the
direct backend reference required by a clean worktree and increasing this
case from 1,000 to 16,000 operations to clear the sub-millisecond timer-noise
floor.

Matched macOS Time Profiler captures measured exact Preview.36 at `329.578`
ns/op and the candidate at `182.654` ns/op. Allocations plus VM Tracker measured
`329.314` versus `184.297` ns/op and the same `1,480` versus `472` managed B/op.
EventPipe measured `327.648` versus `189.569` ns/op. Metal System Trace measured
`330.810` versus `183.625` ns/op; both exported zero target command-buffer,
current-allocation-size, and resource-allocation rows, confirming construction
is CPU-only. Raw process JSON, Instruments traces and TOCs, Metal table exports,
EventPipe traces, and Speedscope conversions are retained under
`artifacts/performance/skiasharp-gradient-typed-final`.

Mutation, overflow ownership, colorspace, tile-mode, local-matrix, degeneracy,
paint-alpha, transformed-picture, and GPU pixel-coverage tests pass. The full
macOS core suite passes 3,237/3,237 and the headless suite passes 225/225. The
official API metadata gate still reports `reference=4222`, `matching=4222`,
`missing=0`, and `extra=997` ProGPU extensions.

## Inline rounded-rectangle storage after Preview.37

`SKRoundRect` now keeps its fixed four `SKPoint` corner radii in an inline value
buffer instead of allocating a second managed array for every instance. The
copy constructor transfers those four values directly, uniform initialization
uses four bounded stores, and internal `SKPath`/canvas consumers borrow a
`ReadOnlySpan<SKPoint>`. The public `Radii` property still returns a fresh
caller-owned four-point array, preserving the official ownership boundary.
Construction, copying, normalization, classification, and radius access remain
fixed `O(1)` CPU work and storage. They do not initialize WebGPU, allocate a
native geometry object, or change retained path topology.

This clean-room change was designed from Skia's public
[`SkRRect` contract](https://api.skia.org/classSkRRect.html), the official
[`SKRoundRect` API](https://learn.microsoft.com/dotnet/api/skiasharp.skroundrect),
[`GetRadii`](https://learn.microsoft.com/dotnet/api/skiasharp.skroundrect.getradii),
and
[`SetRectRadii`](https://learn.microsoft.com/dotnet/api/skiasharp.skroundrect.setrectradii)
contracts, plus the official .NET
[`InlineArrayAttribute`](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.inlinearrayattribute?view=net-10.0)
and [C# inline-array specification](https://learn.microsoft.com/dotnet/csharp/language-reference/proposals/csharp-12.0/inline-arrays).
ProGPU adopts the observable four-corner value and ownership contracts and
adapts them to its typed CPU-only geometry model. It rejects source-array
aliasing, an unbounded cache, native allocation, reflection, and GPU setup for
metadata operations. No foreign implementation source was consulted. This is
an object-storage change rather than a renderer, text, scene, or GPU-pipeline
change, so the existing cross-engine rendering architecture review remains
unchanged.

Three alternating Apple M3 Pro Release process pairs compared exact
Preview.37 commit `d510dd5c` with the final candidate after 2,000 dynamic-PGO
warmups and 192 samples per binary. The exact semantic checksum remained
`13947687467187634243`. Aggregate median construction/disposal latency fell
from `31.0687` to `25.1271` ns/op, a `19.12%` latency reduction or `23.65%`
more operations per second. Managed allocation fell from `120` to `88` B/op,
a `26.67%` reduction. Scheduler interruptions dominate the raw p95 values
(`111.9750` versus `112.3459` ns/op), so no tail-latency improvement is
claimed. A separate three-pair differential against official SkiaSharp
4.151.0 measured `63.417` versus `25.038` ns/op with the same checksum.
Official managed allocation was `80` B/op versus ProGPU's `88` B/op, but that
counter excludes Skia's native `SkRRect` allocation, so it is not treated as a
total-memory comparison. The complete default-warmup matrix also preserved all
checksums and measured this case at `90.442` versus `35.606` ns/op.

Matched macOS 26.4.1 profiling used the same 400-million-construction Release
workload for Preview.37 and the candidate. Time Profiler and Allocations plus
VM Tracker each sampled 12 seconds. EventPipe sampled-thread-time plus verbose
GC attributed `99.23%`/`99.64%` exclusive CPU to the benchmark body; the
baseline's `SpanHelpers.ClearWithoutReferences` entry (`0.23%`) disappeared
from the candidate hot list. Three-second Metal System Trace captures exported
zero target application-encoder and zero target Metal-driver rows for both
binaries, confirming the value path remains CPU-only. Raw tracing artifacts
were removed after extracting these summaries to recover local disk space, as
requested; reproducible benchmark JSON and Markdown remain under
`artifacts/performance/skiasharp-roundrect-inline`.

The focused rounded-rectangle/path/canvas tests pass, including a 10,000-object
allocation guard requiring at most `96` managed B/op. The complete macOS core
suite passes 3,238/3,238 and the headless suite passes 225/225. The official
SkiaSharp metadata gate reports `reference=4222`, `matching=4222`, `missing=0`,
and `extra=998`; the one-entry extension-count movement is compiler-emitted
nullable metadata redistribution, not a new public member.

## Compact immutable image views after Preview.38

`SKImage` subsets now share one root-invariant `TextureStorage` containing the
texture, pixel format, alpha format, color space, portable pixel snapshot, row
width, texture-backed classification, ownership callbacks, and atomic lifetime.
Each immutable view retains only that storage reference, its width and height,
one composed origin pair, and lazily created view-specific state. `Info` remains
an official value-returning boundary and is reconstructed from those fields.
Nested subsets compose checked origins in `O(1)` time, never copy pixels, never
upload another texture, and remain valid after their parent view is disposed.
The compare/exchange retain loop deliberately preserves the existing
no-resurrection ownership rule; a tempting increment-and-rollback shortcut was
rejected because concurrent retainers could observe the rollback after final
release.

This clean-room design used Skia's public
[`SkImage` immutability and subset contract](https://api.skia.org/classSkImage.html),
the [WebGPU texture-view model](https://gpuweb.github.io/gpuweb/),
[Direct2D source rectangles](https://learn.microsoft.com/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1devicecontext-drawbitmap%28id2d1bitmap_constd2d1_rect_f__float_d2d1_interpolation_mode_constd2d1_rect_f__constd2d1_matrix_4x4_f_%29),
[Win2D image source rectangles](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawImage_8.htm),
[WebRender's retained-scene model](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
and [Vello's wgpu renderer](https://github.com/linebender/vello). ProGPU adopts
immutable shared backing storage, cheap typed views, explicit ownership, and
deferred source-rectangle evaluation. It rejects copied implementation
structure, per-view pixel buffers, texture duplication, reflection, and an
unbounded view cache. The text boundary was reviewed against
[Skia's shaping architecture](https://skia.org/docs/dev/design/text_shaper/),
[Parley](https://github.com/linebender/parley), and
[HarfBuzz](https://harfbuzz.github.io/harfbuzz-hb-shape.html); this storage
change does not move shaping, layout, glyph caching, or renderer work. No
foreign implementation source was consulted.

Three alternating Apple M3 Pro Release process pairs compared exact
Preview.38 commit `65f86cf4` with implementation commit `c7046673` after 2,000
dynamic-PGO warmups and 192 samples. The exact checksum remained
`15041971963811491075`. Aggregate median subset latency fell from `29.312` to
`26.450` ns/op (`9.76%`), or `10.82%` more operations per second. Managed
allocation fell from `105.694` to `65.693` B/op (`37.85%`). Scheduler
interruptions dominate the raw p95 values, so no tail-latency improvement is
claimed. A stabilized official SkiaSharp 4.151.0 differential measured
`339.927` versus `26.450` ns/op with the same checksum. Official managed
allocation was `104.021` B/op versus ProGPU's `65.693` B/op, but that counter
excludes Skia's native heap, so it is not treated as a total-memory comparison.

Matched macOS 26.4.1 profiling isolated one long-lived immutable source image
from source upload by overriding only the benchmark operation count. Each Time
Profiler and EventPipe process constructed 400 million subset views. Time
Profiler measured exact Preview.38 at `36.429` and the candidate at `32.553`
ns/op (`10.64%` lower); EventPipe measured `40.414` versus `35.315` ns/op
(`12.62%` lower), with about `96%` exclusive sampled CPU in the intended
benchmark body. The same sustained path allocated exactly `104` versus `64`
managed B/op (`38.46%` lower). A bounded 40-million-view Allocations plus VM
Tracker lane measured `37.198` versus `33.065` ns/op. Its native heap and VM
totals include runtime/device startup and did not show a retained regression;
they are not used as evidence for the managed object-size claim.

The matched 40-million-view Metal System Trace lane measured `37.882` versus
`33.152` ns/op. Both binaries exported exactly `26` target resource-allocation
rows, `42` current-allocation-size intervals, the same `1,196,032`-byte peak,
and zero target command-buffer submissions, errors, compiler spills, or hangs.
Thus source creation remains the only GPU work and view count does not multiply
GPU resources. Raw Instruments traces, table exports, EventPipe traces, and
temporary exact-baseline binaries were removed after these summaries were
extracted. Reproducible benchmark distributions remain under
`artifacts/performance/skiasharp-image-view-c7046673`.

A 10,000-view focused allocation guard requires at most `72` managed B/view;
image, surface, pixmap, and effect ownership tests pass. The complete macOS
core suite passes 3,239/3,239 and the headless suite passes 225/225. The
official API metadata gate remains `reference=4222`, `matching=4222`,
`missing=0`, and `extra=998`; this implementation and its benchmark operation
override add no public API.
