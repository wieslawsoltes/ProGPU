# Shared effect capture frame

`ProGPU.Scene.EffectCaptureFrame` is a pure value describing the actual managed
effect-input capture. It has no WPF, device, texture or source-lease dependency
and does not select a renderer or admit a shader/resource family. Source adapters
can use it before acquiring external sampler resources; the existing compositor
uses the same core before allocating or resizing an effect texture.

The authority is original ProGPU `Compositor.PrepareAndDrawEffect` at
`2ab3aeac9aa3efc74c1ad42f72a7dfcd96017011`, not a foreign rendering algorithm.
For shader effects, padding is `MathF.Ceiling(MathF.Max(0f, padding))`. The optional
`Visual.EffectRasterPadding` override instead retains fractional
`MathF.Max(0f, padding)` and maps a nonfinite override to zero. Blur and shadow
retain their original effect-specific, potentially unequal X/Y padding. All
paths then share the original expression order:

1. Construct the padded float rectangle with `x - paddingX`, `y - paddingY`,
   `width + paddingX * 2f`, `height + paddingY * 2f`.
2. Retain that rectangle separately from the `MathF.Max(1f, extent)` logical
   capture width/height. A sub-unit source rectangle is not enlarged as geometry.
3. Logical render dimensions are `MathF.Ceiling(logicalExtent)`; physical pixel
   dimensions are `MathF.Ceiling(logicalExtent * dpiScale)`, with the original
   **float multiplication before ceiling**. Neither an intermediate logical
   integer nor a double multiplication is equivalent.

The public shader overload accepts content bounds, shader padding and actual
capture DPI. Its second overload also accepts the nullable raster override.
The result retains padded bounds, minimum logical extents, logical integer render
dimensions, physical integer dimensions and the original DPI. Values returned
from `PaddedBounds` are copies. The existing `Rect` declaration was moved verbatim
from `RenderCommand.cs` into `Rect.cs` so the actual pure production types can be
tested without a renderer dependency graph; its layout/API/semantics are unchanged.

Empty, nonfinite or uint-unrepresentable capture frames return false and a default
output, never a partially initialized frame. The dimension check compares a
float ceiling to `uint.MaxValue` in double: converting that maximum to float
would round to 2^32 and incorrectly admit overflow. No arbitrary device limit is
invented; actual allocation/device limits and source ownership remain with their
existing owners. The compositor retains its existing empty-content early return
and current-DPI selection, and reports invalid nonempty frames explicitly before
texture ownership changes rather than converting invalid extents into dimensions.

This factorization is O(1), allocation-free CPU arithmetic, not a CPU render path.
Managed shader captures and source sampler sizing share it. Native shader
captures already derive their own scene-owned physical extent from validated
native layer/presentation metadata; this helper does not override that basis,
alter the native ABI, widen fractional capture admission or change native code.
Existing shader/resource/clip/cache/device and application qualification gates
remain in force. No performance, pixel or desktop qualification is inferred.

Authored CPU controls cover the original 1,800-case bounds/padding/DPI expression
matrix, fractional overrides, negative and nonfinite padding policy, sub-unit
geometry, float-vs-double multiplication, exact uint conversion boundaries,
invalid/overflow atomicity and value ownership. Full provider/render/source tests
remain hosted gates; a pure frame result alone cannot qualify an external sampler.
