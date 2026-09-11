# Target-aware Direct2D scene recorders

## Implementation-first checkpoint (2026-09-07)

Direct2D provider ABI v55 adds `scene_recorder_create_for_target` and the public
managed `ProGpuDirect2DSceneRecorder` owner. Standalone recording can now supply
the same full-target opacity-brush domain as surface-backed command-list
translation, without creating a device or GPU surface. This closes the explicit
target-descriptor gap in [full-target brush domains](direct2d-target-brush-domains.md).
It does not establish complete Direct2D/Win2D or MIL parity.

The new entry point requires a 24-byte `progpu_native_direct2d_target_extent`:
exact structure size, nonzero uint32 physical width/height, zero reserved field,
and positive finite float DPI X/Y. It copies the derived double DIP dimensions
at creation (`pixels * 96 / dpi` independently per axis). Descriptor storage is
not retained. Invalid input clears the output recorder and reports E_INVALIDARG.
The legacy targetless entry point remains unchanged and rejects full-target
brush callbacks. Both entry points use one existing native recording algorithm.

Target extent is immutable for a scene generation. Size/DPI changes require a
new recorder and generation. `HasTargetDependentMasks` continues reporting actual
target-dependent mask use, not merely presence of a descriptor. Callers must not
reuse such streams on a differently sized/DPI target. No automatic cache
invalidation or general device-independent rebinding is claimed.

## Managed boundary and ownership

`ProGpuDirect2DRecorderTarget.Validate()` is platform-independent and does not
load a library. The C header generates `NativeDirect2DTargetExtent` into
`ProGPU.Backend.Native/Generated/NativeDirect2DContract.g.cs`; generation and the
existing freshness gate include this second marked header. No handwritten
managed field-layout duplicate is added. The export allowlist has one new entry,
and managed/native ABI versions both advance from 54 to 55.

`ProGpuDirect2DSceneRecorder.Create(sceneId, generation, target)` validates before
native loading, requires the matching ABI, and owns the native recorder through
a SafeHandle. Omitting target explicitly selects legacy targetless recording.
The current recorder COM provider is Windows-only; portable C++ render-target
recording remains the cross-platform implementation. This wrapper does not make
the Windows provider library available on macOS/Linux.

`AcquireCommandSink()` returns an independently owned genuine
ID2D1CommandSink1 COM reference. Native clients may stream a closed command list
or perform balanced BeginDraw/callbacks/EndDraw. Its reference remains alive after
the recorder owner is disposed, but serialization still requires a live recorder.
The wrapper holds SafeHandle leases during acquisition and serialization and
releases raw references on construction failure. Raw-handle users must keep the
COM reference alive for their whole native call. Concurrent recording and
measurement/writing are forbidden; lifetime guards do not synchronize callbacks.

Measure once after completing a changed recording, then write into a retained or
pooled caller-owned Span<byte>. The destination is pinned only during the
synchronous call and never retained. Undersized writes report required size;
callers can reuse an adequately sized buffer without measuring again. There is
no array-returning product API, per-draw P/Invoke, managed callback delegate,
runtime reflection, or managed vtable implementation.

## Original implementation, applicability and costs

Original ProGPU provenance is `cb2e5d05db10fa1d25e47c35163996c46c70dc22`:
`src/ProGPU.Native/src/Direct2D/progpu_native_direct2d.cpp` recorder creation,
CommandSceneStreamSink and surface-backed target-domain constructor; plus
`src/ProGPU.Native/src/Direct2D/progpu_native_direct2d_core.cpp` shared validation
and SIMD bounds. This batch factors the existing creation path and supplies its
existing dimensions. It does not port third-party implementation text.

Managed/native applicability is paired at the generated descriptor, validation,
ABI, status/result mapping, and owned lifetime boundary. Surface-backed managed
translation and standalone managed serialization share result conversion. The
ordinary managed renderer and portable C++ target already own target dimensions;
neither needs a second mask algorithm. Shaders and execution-policy defaults are
unchanged. Existing double-lane SIMD domain mapping remains shared.

Descriptor validation, copying and ownership setup are O(1). Creation owns one
recorder plus its sink and existing recording storage. Recording keeps existing
command/resource complexity; serialization writes O(B) bytes for B scene bytes
into the caller buffer. The new metadata adds no pixel workload, readback,
repacking, submission, font discovery or eager GPU initialization. No measured
latency, allocation, SIMD speedup or quality claim is made in this phase.

## Primary research and decisions

- [Direct2D command-list streaming](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1commandlist-stream)
  permits a caller-implemented command sink; retain native streaming and error
  propagation rather than managed per-command callbacks. [DPI](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-getdpi)
  defines separate horizontal/vertical pixel-to-DIP mappings.
- [Skia canvas](https://api.skia.org/classSkCanvas.html) and
  [Win2D command lists](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm)
  inform separate recording ownership and scoped composition. Adopt explicit
  dimensions; reject allocating a rendering surface merely to obtain metadata.
- [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello](https://docs.rs/vello/latest/vello/struct.Scene.html) inform reusable
  retained scenes and separate GPU execution. Keep culling, batching, demand
  uploads and worker scheduling in their existing ProGPU owners; no new worker
  or cache is introduced. Target changes invalidate caller generations; device
  resources still require their own device-loss handling.
- [Parley](https://docs.rs/parley/latest/parley/layout/struct.Layout.html) and
  [HarfBuzz](https://harfbuzz.github.io/shaping-plans-and-caching.html) reinforce
  reusable CPU text state. Shaping/layout reuse, fallback and variable-font keys,
  glyph/path/texture eviction, hinting, subpixel state and text uploads remain
  unchanged. Target DPI is not a reason to rediscover fonts or reshape per frame.

## Authored fixtures and deferred qualification

Native header fixtures cover structure size/offsets, ABI and interface identity;
core fixtures cover invalid dimensions, DPI, reserved bits and null descriptors.
Managed fixtures cover generated layout and validation before native loading.
The Windows fixture compares serialized bytes against surface-backed translation
for 36 DPI/affine/brush/geometry combinations, poisons caller target storage after
creation, checks independent COM lifetime after recorder destruction, and retains
the legacy targetless rejection. Invalid C entry-point cases assert output reset.

Fixtures are authored, not executed. Apple Clang C++20 core/portable COM/header
targets compile/link; ProGPU.Tests Release compiles with zero warnings/errors.
Windows provider/fixture compilation, managed native-runtime lifetime tests,
full renderer packaging, cross-platform/image/VM tests, benchmark measurements,
source verifiers and exact-head PR CI qualification remain pending. The fast
native build excludes the full renderer and Windows provider; its success is not
evidence that either was rebuilt or exercised.
