# System.Drawing TextureBrush Contract and Typed Rendering Audit

## Contract sources

The public surface is pinned to `System.Drawing.Common.dll` and XML from `Microsoft.WindowsDesktop.App.Ref` 10.0.11. The observable constructor, ownership, transform, validation, and wrap-mode cases were checked against the canonical WinForms `TextureBrushTests` already vendored in LibreWinForms. The implementation in ProGPU is original managed code over typed ProGPU bitmap, texture-command, transform, and clip services; it does not copy the GDI+ handle implementation.

The complete public group is the sealed `System.Drawing.TextureBrush` type, its eight constructors, `ICloneable`, `Clone`, `Image`, `Transform`, `WrapMode`, `ResetTransform`, and the multiply/translate/scale/rotate overload pairs. The five `WrapMode` values remain the existing official enum identities.

## Ownership and image preparation

A brush owns a bitmap snapshot rather than retaining the caller's mutable/disposable `Image`. `Image` returns a new caller-owned clone, and `Clone` duplicates the bitmap, transform, and wrap state. Rectangle constructors validate and crop to a premultiplied 32-bit snapshot. `ImageAttributes` is defensively cloned before its wrap, remap-table, and color-matrix state is applied once to that snapshot. Brush disposal releases both the owned image and matrix; caller image, returned image, original attributes, brush clone, and recorded GPU lease lifetimes are independent.

## Typed rendering policy

Texture fills do not impersonate a vector brush or carry native GDI+ handles. `Graphics` obtains one typed retained `GpuTexture` lease from the brush bitmap, emits `DrawTexture` commands for the required tiles, and brackets them with either the existing rectangular clip or retained `PathGeometry` clip. Each tile has a full source rectangle and an explicit affine transform composed as tile mirror/translation, brush transform, then graphics transform.

`Tile` repeats normally. `TileFlipX`, `TileFlipY`, and `TileFlipXY` alternate signed scale and compensating translation on the corresponding tile index. `Clamp` emits only the un-repeated image and leaves the remaining clipped shape transparent. Bounds are mapped through the inverse brush transform before tile indices are selected, so translation, scale, rotation, shear, negative coordinates, and an independent graphics transform share one path. A one-million-command safety limit rejects pathological retained recordings explicitly rather than exhausting memory.

Rectangle, ellipse, path, polygon, closed curve, rounded rectangle, and region entry points converge on the same typed implementation. The command, clip, texture lifetime, and native/compiler paths already existed in ProGPU; this slice does not add a renderer ABI, archive version, native struct, shader binding, reflection path, or private-field probe.

## Validation evidence

- ApiCompat debt moved from 59/425/47/531 to 59/409/46/514 with no breaking changes and no stale suppressions.
- The focused `System.Drawing.Common.Tests` suite passes 115/115 cases. Its hosted Linux lane provisions Mesa's software Vulkan adapter because the texture cases intentionally execute the same typed WebGPU recording and readback path as production rather than substituting a fake drawing context.
- Texture-specific tests cover all constructors represented by the behavior groups, source and clone ownership, crop/remap state, wrap validation, transform order/reset, disposal, rectangular and geometry clips, exact 4×4 pixels for all five wrap modes, clamp transparency, retained-resource count, zero-allocation warmed transform mutation, and bounded fill recording allocation.
- The affected ProGPU headless GDI tests pass 3/3 with the new owned-snapshot and shape-general behavior.
- ARM64/.NET 10.0.11 BenchmarkDotNet ShortRun measured the four-tile record/release cycle at a 556.757 ns median, 556.451 ns mean, and 96 B/op. This is a three-iteration local regression checkpoint, not a renderer-wide throughput claim.
