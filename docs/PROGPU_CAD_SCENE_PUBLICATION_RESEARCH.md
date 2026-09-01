# ProGPU CAD Scene Publication Research

## Scope and clean-room provenance

This slice adds an original ProGPU generation gate for publishing fully
prepared immutable CAD snapshots and retained pictures. It does not copy or
translate another renderer's implementation. The consulted primary sources
informed only the retained-state and ownership contracts:

- [Skia `SkPictureRecorder`](https://api.skia.org/classSkPictureRecorder.html)
  distinguishes an immutable finished picture from a drawable that can retain
  live nested references. Adopted: publish only a fully frozen `GpuPicture`.
- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  separate reusable CPU geometry from device-dependent drawing resources, and
  [Win2D device-loss guidance](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm)
  recreates device resources after loss. Adopted: the gate arbitrates CPU scene
  identity only and does not retain a backend device.
- [WebRender's Scene-to-Frame boundary](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  separates scene construction from frame building. Adapted: ProGPU prepares
  immutable generation state before one bounded publication callback.
- [Vello's retained-scene design](https://github.com/linebender/vello/blob/main/doc/vision.md)
  motivates reusable scene fragments and late transforms, while its current
  [`Scene`](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  is an owned command/resource encoding. Adopted: publication changes retained
  ownership, never mutable commands in place.
- [DirectWrite text layout](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritetextlayout),
  [Skia shaped text](https://skia.org/docs/dev/design/text_shaper/),
  [Parley layout](https://docs.rs/parley/latest/parley/), and
  [HarfBuzz buffers](https://harfbuzz.github.io/harfbuzz-hb-buffer.html)
  retain reusable text results independently from scene scheduling. Rejected:
  reshaping, font discovery, or glyph-cache mutation at publication time.

No third-party source text, helper layout, naming, or control flow was used.
The exact implementation provenance is `CadDocumentSession.cs`, the new
`CadScenePublicationGate.cs`, and the shared `CadSampleCanvas.cs` integration in
this repository.

## Publication contract

`CadScenePublicationGate.Begin` captures one session identity, content
generation, and monotonic request identity. A newer request supersedes every
older ticket, including tickets for the same document generation. `Invalidate`
rejects all outstanding work when the canvas releases resources.

`TryPublish` rejects before mutation when the ticket belongs to another gate or
session, has been superseded or already published, the prepared generation does
not match, or the document has advanced. The final generation comparison and
bounded retained-reference exchange run under the document session gate, so an
edit cannot interleave between validation and publication. Publication is
one-shot and O(1), with O(1) storage and no document, entity, glyph, shader, or
resource traversal.

The current shared canvas uses the gate on its synchronous complete-scene
replacement path. This establishes the correctness boundary needed by future
worker preparation without claiming that compilation has already moved off the
UI thread. Generation-keyed reusable chunks, worker scheduling, and matched
edit-latency measurements remain separate work.

## Managed/native applicability

Both renderers consume the same immutable published snapshot/picture content.
This change alters neither the managed nor native rendering algorithm, shader,
packed scene record, ABI, upload policy, cache identity, DPI/subpixel behavior,
device-loss behavior, or submission count. A C++ implementation would duplicate
host-side document arbitration without adding native coverage, so it is not
applicable. Native parity remains the existing matched compilation of the
accepted immutable generation.

Focused regressions cover one-shot success, same-generation request
supersession, explicit invalidation, document-generation advance, prepared
generation mismatch, and cross-gate/session ticket rejection.
