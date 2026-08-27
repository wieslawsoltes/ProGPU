# System.Drawing destination-point image contract

Date: 2026-08-27

## Scope and sources

This slice restores all ten .NET 10.0.11 `Graphics.DrawImage` overloads whose destination is a `Point[]` or `PointF[]`. The public signatures and three/four-point ordering follow the pinned `Microsoft.WindowsDesktop.App.Ref` contract and the official [`Graphics.DrawImage`](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimage?view=windowsdesktop-10.0) documentation. Three points map the source top-left, top-right, and bottom-left corners to a parallelogram. Four points additionally supply the bottom-right corner and define a projective quadrilateral for bitmap images.

The implementation is original ProGPU code. It uses no GDI+ handle, HDC, runtime reflection, private-field scan, or bounds-based compatibility approximation.

## Typed retained mapping

`System.Drawing.Graphics` validates the array length before reading it, snapshots its coordinates into scalar `Vector2` values, converts the source rectangle to pixels, and records one `DrawTexture` command. The command owns four destination vertices in top-left, top-right, bottom-right, bottom-left render order plus four positive homogeneous interpolation weights. The fields are part of the typed retained texture payload, survive immutable `GpuPicture` compaction, and compose translation through the ordinary command transform.

For a three-point request, the fourth vertex is `topRight + bottomLeft - topLeft` and all weights are one. For a four-point request, the diagonal intersection parameters `s` and `t` produce weights proportional to:

```text
top-left     1 / (1 - s)
top-right    1 / (1 - t)
bottom-right 1 / s
bottom-left  1 / t
```

The common scale is normalized away. The texture vertex shader interpolates `uv * q` and `q`; the fragment shader samples `(uv * q) / q` and derives gradients from that resolved coordinate. This makes the two indexed triangles one continuous projective mapping instead of two unrelated affine mappings and retains the existing vertex-buffer ABI by consuming the already-present location-seven scalar in the texture pipeline. Ordinary texture vertices encode zero there and resolve to `q = 1`, so their path is unchanged.

Axis-aligned commands keep the existing exact CPU quad clip. Arbitrary mapped quads use coarse bounds rejection and the draw call's typed scissor/mask state; clipping their bounding rectangle as though it were the image would corrupt geometry and interpolation. Current graphics transforms are applied after destination mapping.

The managed compositor supports affine and perspective quads. The native scene protocol currently exposes only an affine `Matrix3x2` image transform: equal-weight parallelograms lower exactly to a unit destination rectangle plus an affine matrix, while a genuinely projective native-picture compile fails transactionally with `UnsupportedCommand`. It never substitutes the command's axis-aligned bounds. A future native protocol revision can add four vertices and homogeneous weights without changing the public `System.Drawing` contract.

## Attributes, callbacks, and validation

Null images and arrays fail before coordinate access. Arrays must contain exactly three or four entries. Coordinates must be finite, affine areas must be nonzero, and four-point diagonals must intersect strictly inside a finite convex quad. Invalid geometry fails at recording time rather than reaching a shader as NaN or infinity.

Abort callbacks receive the official integer callback data as `IntPtr`; returning `true` records and retains nothing. Attribute-free draws and empty `ImageAttributes` remain on the zero-copy retained-texture path. Effective remap, matrix, key, gamma, threshold, or channel adjustments use the existing exact managed bitmap-adjustment implementation before the mapped texture is retained, so no supported attribute is silently ignored.

## Gates and evidence

Twelve focused image-overload tests cover:

- three-point affine rotation and exact corner pixels;
- a trapezoidal four-point sample that distinguishes projective interpolation from two-triangle affine interpolation, including the shared diagonal;
- recorded vertex ordering, nonuniform weights, immutable picture retention, and translated context append;
- integer and floating-point source rectangles;
- remap attributes, callback data, and abort-without-recording behavior;
- null, length, non-finite, and degenerate geometry validation; and
- exactly zero managed bytes across 1,000 warmed perspective command recordings; and
- successful native affine compilation plus transactional projective rejection.

Two deeper native-picture compiler tests cover the exact affine wire transform and explicit projective rejection. `ImageConvenienceBenchmarks.RecordPerspectiveDrawImage` provides an isolated BenchmarkDotNet latency/allocation checkpoint for the warmed recording path.

The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 116.578 ns median (119.490 ns mean, 7.541 ns standard deviation) with zero managed allocation. The run used one launch and three measured iterations, and process-priority elevation was denied, so it is a coarse local subsystem checkpoint. The focused 1,000-operation zero-allocation assertion is the deterministic regression gate.

ApiCompat removes ten exact missing-member suppressions. Measured debt moves from 40 missing types, 137 missing members, 17 other diagnostics, and 194 total to 40 missing types, 127 missing members, 17 other diagnostics, and 184 total, with no breaking changes or stale suppressions.
