# Original DrawingImage shader sampler reference

`eng/WpfShaderEffectReference/DrawingImageSamplers.cs` is an additive original
Microsoft WPF source reference for the paired native DrawingImage sampler
connection. It creates real `DrawingImage`, `DrawingGroup`, `GeometryDrawing`,
`RectangleGeometry` and `SolidColorBrush` objects. No bitmap substitutes for the
source drawing, and no product graph/bounds evaluator supplies expected pixels.
The existing reference families, complete-frame assertions, ARM negative control
and shared 60-second deadline remain unchanged.

## Same-owner source sequence

The original `ImageBrush` binds the existing authored identity shader's nearest
sampler. The actual visual also emits nearest bitmap scaling. Content and final
clip are `(8,10,32,24)` in a 64-by-64 opaque black target at 96 DPI. The initial
group contains a red rectangle `(10,20,4,6)`, then a green rectangle `(14,20,4,6)`,
without pens. The original drawing bounds are `(10,20,8,6)`; the image's public
width and height are 8 and 6. Those extents do **not** erase the drawing origin
from an absolute ImageBrush viewbox.

| State | Actual source change | Independent local-X colors |
| --- | --- | --- |
| original origin | Full relative viewbox/viewport | red `[0,16)`, green `[16,32)` |
| overlap and opacity | First width 6; group .5, brush .5 | same intervals, channel value 64; later green owns overlap |
| absolute viewbox | Restore widths/opacities; viewbox `(14,20,4,6)`, viewport `(8,0,16,24)` | green `[8,24)`, black elsewhere |
| tiled relative | Full relative viewbox, half-width relative viewport, Tile | red/green alternating 8-column bands |
| retained color | Full non-tiled mapping; mutate original red brush to blue | blue `[0,16)`, green `[16,32)` |
| detached drawing | Set the original image's Drawing to null | black |
| reattached empty | Clear original group, reattach that same group | black |
| refilled origin | Reuse original children at `(-6,9,4,6)` and `(-2,9,4,6)` | same complete raster as retained color |
| absolute origin miss | Restore original rectangle positions, absolute viewbox `(4,0,4,6)` | black despite nonempty drawing and image extents |

All intervals add receiving X=8 and span physical Y `[10,34)`. Every exterior
pixel and final alpha is checked. The overlap case distinguishes group opacity
from independently alpha-blending overlapping children. The last case retains
positive drawing/image extents; a black result is a mapping assertion, not an
empty-image substitute.

Each of nine states has two captures on the retained object graph and one with a
new graph constructed directly from independent literal state: 27 replays. The
fixture requires exact full-byte equality and independent literal color checks.
Equivalent restoration states additionally match earlier rasters. Source brush,
image, group, geometry, child order, effect, shader, dimensions, bounds and mapping
are checked before and after every replay. Clear/refill keeps the same group and
child identities. No managed drawing or provider success is manufactured.

Inputs, original bounds/dimensions, exact bounds/opacity bits, each PNG and raw
BGRA, assembly/producer identities and SHA-256 hashes are preserved with
`CreateNew`. Empty bounds are recorded as their exact bit strings rather than
invalid JSON numeric infinities. Pixel failures preserve a failed receipt before
failing the application. The existing native ARM64 SoftwareOnly-unavailable lane
requires opaque black output and zero qualified shader cases.

## Ordinary ImageBrush counterpart

A separate family renders states 0, 2 and 7 through an ordinary ImageBrush fill,
without creating or attaching a PixelShader/ShaderEffect. It draws the source
rectangle at local `(0,0,32,24)` under the actual visual offset `(8,10)` and final
clip `(8,10,32,24)`. This preserves the same viewport frame as the shader's
secondary capture without replacing the source coordinate contract.

The same objects traverse all intervening mutations, including null, empty and
refilled Drawing state. The three required nonempty generations each have
retained, warm and independent-literal renders: three configurations / nine
replays in a separate receipt. All use the same independent literal full-byte
oracle. They are ordinary positive drawing controls on both architectures;
ARM64's separate unavailable SoftwareOnly shader gate cannot turn these into
black-output success. The nine shader states / 27 shader replays above remain
unchanged.

Paired native controls replay those three states with both inferred bounds and
exact nonzero DrawingImage bounds metadata. All source channels retire before
replay. Their strict source command sequence has ten commands: visual save,
forced tile layer, image and group saves, two analytic draws, corresponding
restores/pop. Adjacent analytic children share one replay draw and the tile
composite adds one: two draws and one submission on cold, warm and independent
engines. These are authored source-derived assertions, not measured performance.

## Source frame and clean-room provenance

The reference reuses the original ProGPU-owned identity shader and capture helpers
from the existing WPF reference project at authoring base
`f78c5bd6f5146569067f515b5133c6618bd7d402`. No foreign implementation was copied or
adapted. Public API contracts and immutable original WPF behavior distinguish
three facts that must not be conflated:

- [DrawingImage](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.drawingimage)
  is an ImageSource containing a Drawing; width/height do not define a new source
  coordinate system.
- [TileBrush.Viewbox](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.tilebrush.viewbox)
  and its units select original source content. Relative coordinates refer to
  content bounds; absolute coordinates remain source coordinates.
- [DrawingGroup.Opacity](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.drawinggroup.opacity)
  is a group operation, distinct from changing each child brush's opacity.

Immutable original WPF `381194e` supplies the concrete source-frame evidence:

- `WpfGfx/core/resources/drawingimage.cpp`, `GetBounds` (30–41) and `Draw`
  (58–67), retain and draw the original Drawing, including its bounds origin.
- `imagebrush.cpp`, `GetContentBounds` (290–316), forwards those bounds. Its
  intermediate admission (374–384) treats DrawingImage as content requiring
  rasterization, not an already available bitmap source.
- `tilebrush.cpp` (178–223) and `TileBrushUtils.cpp` (226–244) adjust the viewbox
  from content bounds only when its units are relative.
- `TileBrushUtils.cpp`, the intermediate source-clip contract (390–395) and
  device-aligned `None` branch (609–640), clip that intermediate to the viewport.
  Therefore the existing bitmap-only `None` full-source reference cannot supply
  this DrawingImage oracle. The positive absolute case is green only within its
  viewport; the deliberately origin-missing viewbox sees no drawing content.
- `ShaderEffect.cpp`, `DrawIntoIntermediate` (794–868), retains the secondary
  brush's zero-origin complete implicit-input capture frame. That capture origin
  is separate from the DrawingImage's original source coordinates.

These are behavioral and ownership constraints, not a port of original renderer
code. Literal input/expected geometry is independently authored. The reference
remains `SoftwareOnly`; it neither changes its established software UV phase nor
claims hardware/native filtering, capture-frame, package or source-host parity.

## Status

Authored only. No build, syntax check, test, verifier, original Windows execution,
GPU/VM operation or CI dispatch was performed. Both original and native-provider
full-byte controls remain required on the final consolidated implementation tip.
