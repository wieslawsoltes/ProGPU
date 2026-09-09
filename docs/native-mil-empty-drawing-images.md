# Retained empty drawing images

## Contract

`IPortableDrawingBoundsSource.TryGetPortableDrawingBounds` has an explicit
availability result separate from `PortableRect.IsEmpty`. A successful empty
result represents known empty content. False represents unavailable metadata;
consumers must not hide the resource as an empty image. A finite rectangle with a
zero dimension remains a distinct value and is not admitted to positive-area
image mapping by this change.

LibreWPF source `Drawing` publishes its actual `Rect.Empty` through this contract.
Clearing a `DrawingGroup` does not clear the `DrawingImage.Drawing` reference.
The source-owned object graph and its invalidation notifications must survive so
refilling the same group restores its image without replacing the visual root.

## Existing native and managed paths

Native MIL already supports `DrawingImage` with a zero drawing handle. Both
`append_drawing_image` and the drawing-image branch of tile-brush compilation
return successfully without emitting its content for that state. The C++ source
provenance is `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp` at `b56c949e`;
this work does not add another C++ renderer, image buffer, or shader algorithm.
LibreWPF's typed compiler now selects that existing contract for authoritative
empty bounds and emits no image-bounds sideband. Missing bounds remain rejected.

Managed portable drawing-image and image/drawing-tile replay also skip known
empty content before scaling or tile enumeration. Failed drawing-image tile
mapping reports unsupported instead of inheriting an initial skipped status.
The shared source graph still supplies retained dependencies in both modes.

This is a source-contract connection, not a scene/GPU architecture or performance
change. It adds no compute-heavy loop, native crossing, GPU fallback, readback,
synthetic bitmap, or product reflection. Existing native/managed rendering,
resource invalidation, GPU-first and SIMD policies are unchanged.

## Acceptance and qualification

The existing LibreWPF MVP uses a `DrawingImage` both as an `Image.Source` and as
an `ImageBrush`; Toolkit uses drawing-image icons. Their content-update contract
includes clearing and refilling a retained drawing. The source-built native host
harness now authors this exact transition using real DrawingGroup, DrawingImage,
ImageBrush and DrawingVisual objects: populated → empty → unchanged empty →
refilled. It checks source identity, invalidation, emitted scene draws, stable
empty reuse and restored scene bytes. Native and managed adapter fixtures cover
known-empty versus unavailable/zero-sized bounds.

Fixture authoring and compilation precede execution. No runtime, pixel, GPU,
package-mode, performance or Windows parity result is claimed here. These remain
part of the existing final host/SDK/CI gates. Diagnostic public-API reflection is
limited to the dual-assembly test harness and is removed when that harness moves
to direct source-WPF references.

## Primary contracts

- [WPF Rect.Empty](https://learn.microsoft.com/en-us/dotnet/api/system.windows.rect.empty):
  empty is a dedicated no-position/no-area state, not arithmetic mapping bounds.
- [WPF DrawingGroup](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.drawinggroup):
  a mutable collection of drawing content; retain its source object identity.

No third-party implementation is copied. The native no-op behavior is original
in-repository ProGPU code; WPF exposes its own bounds and ownership.
