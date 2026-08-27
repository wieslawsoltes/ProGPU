# System.Drawing imaging effects contract

Date: 2026-08-27

## Scope and source reuse

This slice restores all 23 .NET 10.0.11 types in `System.Drawing.Imaging.Effects` and the official `Bitmap.ApplyEffect(Effect, Rectangle)` member. Public type names, inheritance, constructors, properties, enum identities, validation ranges, matrix constants, lookup-table layout, ownership, and XML contract descriptions are based on the upstream `dotnet/winforms` managed source already vendored by LibreWinForms and the pinned `Microsoft.WindowsDesktop.App.Ref` reference assembly.

The upstream implementation creates opaque GDI+ effect handles and passes a native bitmap pointer to `GdipBitmapApplyEffect`. That is the portable seam: ProGPU keeps the upstream managed API identity but replaces handle creation and execution with typed pixel operations. Product code uses no runtime reflection, private-field scan, HDC, GDI+ handle, or fake WinForms-shaped object.

## Typed execution

`Bitmap.ApplyEffect` synchronizes pending retained drawing through the bitmap's existing lock-protected pixel path. `Rectangle.Empty` selects the full image; nonempty rectangles are intersected with bitmap bounds using overflow-safe arithmetic, and an empty intersection is a no-op. A CPU-resident bitmap is mutated in place. A bitmap whose texture has been materialized performs one explicit texture readback and one writeback through the existing `GpuTexture` contract.

Pointwise effects operate on straight RGBA channel values. Premultiplied backing pixels are unpremultiplied before the transform and premultiplied again after it, including effects that change alpha. This path covers:

- `ColorMatrixEffect`, `GrayScaleEffect`, `SepiaEffect`, `InvertEffect`, and `VividEffect`;
- `ColorLookupTableEffect` with copied, zero-padded 256-entry channel tables;
- black/white saturation, contrast, density, exposure, highlight, midtone, and shadow curves with typed channel selection;
- brightness/contrast, color balance, levels, and tint.

The color matrix is snapshotted for execution at construction time while the official `Matrix` property retains the caller's object identity. Lookup tables are independently owned. Disposed effects fail explicitly.

Blur and sharpen use three `ArrayPool<byte>` buffers and separable moving-window box passes. Their work is O(width × height), independent of the squared kernel area, with bounded kernel radius. `ExpandEdge` selects clamped-edge versus transparent-edge sampling. Sharpen applies an unsharp mask over the blurred result, and zero amount is an exact no-op. This initial portable implementation deliberately remains on the typed bitmap synchronization seam; a future GPU effect pipeline can implement the same effect data without changing the public API.

## Gates and measured debt

Seventeen focused cases cover the canonical white-to-sepia pixel, clipped rectangles, straight/premultiplied alpha handling, lookup-table ownership and padding, color-matrix snapshot behavior, deterministic blur pixels, zero-amount sharpening, disposed use, official validation ranges, and exactly zero managed bytes across 128 warmed pointwise applications. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured 256×256 pointwise inversion at a 774.835 µs median and radius-eight blur at a 1.230 ms median, both with zero managed allocation. One launch and three measured iterations make this coarse local evidence rather than a renderer-wide claim.

Strict ApiCompat removes 23 missing-type suppressions and the `Bitmap.ApplyEffect` member suppression, with no new breaking or stale diagnostics. Measured debt moves from 40 missing types, 127 missing members, 17 other diagnostics, and 184 total to 17 missing types, 126 missing members, 17 other diagnostics, and 160 total.

The initial CPU implementation is the correctness baseline. A GPU continuation should add a typed immutable effect payload, fuse compatible pointwise operations into one compute or render pass, retain CPU execution for small/CPU-owned images, define a measured upload/readback crossover, and compare exact or tolerance-bounded pixels against these tests before changing dispatch policy.
