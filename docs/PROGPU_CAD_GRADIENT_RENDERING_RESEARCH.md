# Gradient hatch rendering investigation

2026-09-08; implementation is still open. Do not count a gradient as rendered
merely because its boundary or a substitute solid fill appears.

## Representative input and established contract

The ACadSharp `sample_AC1032_ascii.dxf` records have:

| Handle | Name | Angle / shift | Stops |
| --- | --- | --- | --- |
| `371` | `LINEAR` | `0 / 0` | `0: RGB(0,0,255)`, `1: RGB(255,255,0)` |
| `376` | `SPHERICAL` | `0 / 0` | Same |

Both have enabled gradients, solid-fill geometry, and two-color dialog state.
These values were read from the actual DXF tags, not inferred from the entity
color or the pattern name. The earlier [data-preservation fix](PROGPU_CAD_GRADIENT_DATA_PRESERVATION.md)
preserves those endpoints through copy and serialization.

Primary CAD contracts consulted:

- [Autodesk HATCH tags](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-C6C71CED-CE0F-4184-82A5-07AD6241F15B.htm):
  gradient enablement, radians, two endpoint colors, and dialog-only single-color
  state/tint. Adopt authored colors; do not derive a new endpoint from tint.
- [Autodesk gradient colors](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbHatch__getGradientColors_unsigned_int__AcCmColor___float__.html):
  two stops at zero and one. Reject unsupported stop layouts explicitly.
- [Autodesk shift contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-AcDbHatch__setGradientShift_float.html):
  a continuous blend of shifted/unshifted definitions, bounded to `[0,1]`.
  Do not collapse it to a Boolean.
- [Autodesk gradient UI](https://help.autodesk.com/cloudhelp/2015/ENU/AutoCAD-Core/files/GUID-264748AE-55FB-453A-8366-DB4C3A3D4723.htm):
  centered symmetry, up/left shift, and a gradient angle separate from the hatch
  pattern angle. This describes appearance but not the spatial equations.

The consulted contracts do not establish exact spherical falloff, normalization
against irregular boundary extents, or interpolation of the shifted field. A
generic radial brush is not yet proven equivalent to `SPHERICAL`. Reference
AutoCAD PDF/PNG exports were requested to resolve these questions. The dependency
`samples/preview.png` was inspected and is unrelated artwork, not a CAD reference.

## Cross-engine applicability and implementation direction

- [Skia gradient contracts](https://api.skia.org/classSkGradientShader.html)
  separate shape coverage, shader-local transforms, stops, spread, and color
  interpolation. Adopt that separation, not source implementation.
- [Direct2D brushes](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-brushes-overview)
  and [Win2D brush contracts](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/archive/windows/win2d-path-mini-language)
  distinguish coordinate transforms, alpha, and pre/post interpolation color
  spaces. Reuse typed ProGPU brushes; reject runtime brush-text parsing.
- [WebRender architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  separates retained scene, spatial transforms, clipping, visibility, and GPU
  preparation. Keep one immutable CAD hatch, one retained analytic boundary,
  generation-based scene invalidation, and demand-driven uploads.
- [Vello scene contracts](https://docs.rs/vello/latest/vello/struct.Scene.html)
  carry independent shape and brush transforms. Reuse ProGPU's corresponding
  command/brush contracts rather than materializing one CPU mesh per gradient.
- [Skia text scope](https://skia.org/docs/dev/design/text_overview/),
  [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/introducing-directwrite),
  [Parley](https://github.com/linebender/parley), and
  [HarfBuzz](https://harfbuzz.github.io/what-is-harfbuzz.html) keep shaping/layout
  separate from paint. Gradient hatches must not initialize fonts, change
  fallback/variable-font state, invalidate glyph caches, or alter text snapping.

`CadSnapshotCompiler.Hatch.cs` already retains analytic OCS loops and exact bounds;
`CadPlanSceneCompiler.RecordHatch` records a single transformed path. Existing
linear/radial brushes have local coordinate transforms. Any new gradient field
must retain immutable typed parameters, preserve holes/island rules, and flow
through managed/native scene compilation and printing equally. Existing startup,
worker scheduling, cache keys/eviction, demand uploads, GPU batching, DPI, atlas
generations, and device-loss contracts remain unchanged until an implementation
is designed and measured. No renderer, shader, or ABI changed in this investigation.

Before declaring support, require paired managed/native semantics and pixels,
rotated/reflected/nonuniform INSERTs, holes and curved boundaries, transparency,
shift endpoints/intermediate values, zoom/DPI, retained reuse, and bounded
Release allocation/upload evidence. This research record is not that validation.
