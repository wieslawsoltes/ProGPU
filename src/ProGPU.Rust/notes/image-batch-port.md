# Original retained image batches

Ported full `SKLatticeLayout`, nine-patch and `SKAtlasLayout` production to
`ImageLattice`, `ImagePatchBatch`, `ImageAtlasTransform` (also exported as the
shared `RotationScaleMatrix`) and Canvas lattice/nine-patch/atlas overloads.
Bitmap adapters freeze actual raster generations; image/texture variants retain
actual source owners. One call records one batch and produces one indexed image
range, never one draw per cell/sprite.

Lattice keeps original source bounds, division validation, skipped-edge cell
indices, fixed/stretch span arithmetic, undersized destination collapse, missing
metadata defaults, transparent omission and ordered color cells. Atlas retains
matching original slots, separate sprite transform arithmetic, source size/UVs,
all color blend modes and independently filtered sprite colors. Cull replaces
only command bounds; it never becomes a sprite crop. Source f64 trig narrows
before float scale and anchor arithmetic. Independent corners/colors use SIMD;
ordered cumulative span endpoints and bounds reduction retain original order.

Preparation performs source/context/size/storage preflight, realizes selected
texture subsets and requested mips, converts selected source transfer, filters the
full image independently of fixed/atlas colors, then records the complete batch
under actual paint alpha/blend and existing clips. Prepared owners use existing
submission retention. No producer callback occurs during encoding. Paint shader,
image-filter and mask-filter metadata remain ignored by this original patch API.

Explicit `ImagePatchPolicy::{ManagedCanvas,ManagedAtlas}` retains separate managed
input/placement rules without changing the native default: out-of-image atlas UVs
use original clamping, reversed lattice destinations remain quads, nonpositive
source spans retain full UV defaults, managed projective defaults are one, and
atlas placement applies sprite and canvas transforms in separate float steps.
Nonfinite GPU publication and bounded inventory overflow reject atomically. The
existing supported bitmap texture formats and device/context gates remain intact.

Also corrected Canvas oval centers to actual source `SKRect.MidX/MidY` arithmetic
(left/top plus original width/height divided by two), independently of other
primitive center expressions.

Original in-repository source SHA-256:

- `src/SkiaSharp/SKLattice.cs`: `fc1616a8681e91047d491a04d30b8aa82560f95cf1442871dee64bd6edcab17b`
- `src/SkiaSharp/SKAtlasLayout.cs`: `b93a98256d77823d38a41163c94b754614cdfdf813ff1f35ff5fc418377a8c79`
- `src/SkiaSharp/SkiaSharp.cs`: `3b43aa657ad5584157b612fd16d2b56d96af352e310d69ff5a004d49e85d216f`
- `src/SkiaSharp/SKCanvas.cs`: `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64`
- `src/ProGPU.Scene/Compositor.cs`: `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd`
- `src/ProGPU.Scene/RenderCommand.cs`: `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8`

Implementation only: no build/compiler/tests/fixtures/runtime/benchmark execution.
No foreign source or generated build artifacts. Existing perspective Canvas source
execution gates remain separate; this increment does not qualify pixels or speed.
