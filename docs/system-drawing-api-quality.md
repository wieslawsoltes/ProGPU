# ProGPU System.Drawing API, Quality, and Performance Contract

## Objective

`ProGPU.System.Drawing.Common` is the portable `System.Drawing.Common` implementation used by LibreWinForms. Public API presence, managed behavior, rendering quality, and hot-path performance are one compatibility contract.

The implementation remains clean-room. Official reference assemblies and documentation define public contracts and observable behavior; implementation code is original ProGPU code built on typed ProGPU services. Upstream implementation source must not be copied or mechanically ported into this repository.

## Pinned API contract

The current contract is `System.Drawing.Common.dll` from `Microsoft.WindowsDesktop.App.Ref` 10.0.11. The repository pins Microsoft ApiCompat 10.0.400 through the local tool manifest.

Run:

```bash
./eng/progpu-verify-system-drawing-api.sh
```

The verifier:

1. restores the pinned reference pack and local ApiCompat tool;
2. builds the exact Release implementation assembly;
3. writes the complete current diagnostic report under `artifacts/system-drawing-api-compat`;
4. prints missing-type, missing-member, other-shape, and total counts;
5. rejects every incompatibility not present in the reviewed suppression file; and
6. rejects stale suppressions when an incompatibility has been fixed.

Only regenerate the baseline after reviewing the complete diff:

```bash
./eng/progpu-verify-system-drawing-api.sh --update-baseline
```

The suppression file is debt, not acceptance of permanent incompatibility. Pull requests should normally remove suppressions and must never add suppressions merely to make CI green.
Baseline regeneration removes machine-specific left/right assembly paths, so suppressions are keyed by diagnostic and API target and behave identically in local clones and hosted CI. The verifier rejects a committed baseline that still contains absolute assembly paths.

## Current measured debt

After the managed bitmap pixel-memory compatibility slice:

| Diagnostic group | Count |
| --- | ---: |
| Missing types (`CP0001`) | 68 |
| Missing members (`CP0002`) | 437 |
| Other shape diagnostics | 47 |
| Total | 552 |

The starting measured baseline was 121 missing types, 906 missing members, 25 other diagnostics, and 1,052 total. Completing `Brushes`, `Pens`, `SystemBrushes`, and `SystemPens`, correcting the assembly version, and adding coherent region, graphics, imaging, affine-matrix, linear-gradient, path, font, icon, buffered-graphics, and printing groups reduced the current debt to 68 missing types, 437 missing members, 47 other diagnostics, and 552 total. The imaging slice includes the official `ColorMap`, `ColorPalette`, `PaletteFlags`, `PaletteType`, `DitherType`, complete `PixelFormat` identities, and `PropertyItem` shapes; defensively snapshotted/cloned image metadata and `ImageAttributes` state; behaviorally applied bitmap and palette remap/matrix operations rather than API-only storage; CPU-only image resolution/tag/frame/bounds contracts; deterministic fixed and optimal palette generation; typed scan0/caller-buffer pixel-memory conversion across packed, indexed, premultiplied, and high-depth formats; and functional `ConvertFormat` palette, alpha-threshold, ordered/spiral/error-diffusion, and reduced-direct-color quantization. `Drawing2D.Matrix` now has its official base/sealed shape and functional parallelogram, composition, pivot, shear, inverse, point/vector, array/span, value, cloning, and disposal contracts. `Blend`, `ColorBlend`, and `LinearGradientBrush` now provide the official public surface plus functional scalable-angle geometry, state ownership, transforms, gamma/spread mapping, custom stops, and renderable triangular/bell falloffs. `GraphicsPath`, `PathData`, `PathPointType`, and `GraphicsPathIterator` now expose source-compatible path construction, shaped text outlines, cardinal curves, clone/composition, point/type export and iteration, analytic bounds, transforms, fill and outline hit-testing, widening, perspective/bilinear warping, reversal, and adaptive flattening directly over retained ProGPU geometry. The missing-member and other-shape subtotals are not monotonic: once a formerly absent type is added, ApiCompat can report the still-missing members and shape details on that type. The committed suppression file is the reviewed current debt and the gate rejects both new and stale suppressions.

The managed compatibility slice also adds typed deferred path boolean operations used by `Region` and `Graphics` clipping. It does not change the native command wire, C++ backend, shader ABI, text shaping, or image codec boundaries. Managed/native rendering parity therefore remains guarded by the repository renderer and headless suites rather than by a new native implementation fork.

## Quality gates

Focused managed tests live in `src/System.Drawing.Common.Tests`:

```bash
dotnet test src/System.Drawing.Common.Tests/System.Drawing.Common.Tests.csproj -c Release
```

Every API slice should cover:

- public signature and assembly-shape changes through ApiCompat;
- state, validation, disposal, cloning, events, and exception semantics where applicable;
- concurrency when resources or registries are shared;
- deterministic pixel or geometry output for rendering behavior;
- lazy GPU initialization and bounded resource ownership; and
- platform-boundary behavior for unsupported local-OS operations.

The known-color slice uses a 256-entry indexed cache per resource kind. Lookup is O(1), first access creates at most one retained resource, concurrent races publish one instance, and warmed access allocates zero bytes.

## Performance gates

Allocation-sensitive performance assertions run with the focused test suite. BenchmarkDotNet measurements provide review evidence for latency and allocation changes:

```bash
dotnet run --project src/System.Drawing.Common.Benchmarks/System.Drawing.Common.Benchmarks.csproj \
  -c Release -- --job short --filter '*'
```

CI uploads the JSON benchmark results with the raw ApiCompat report. For performance-sensitive rendering changes, also run the repository-wide Release renderer/headless suites and the applicable GPU workload from `agents.md`. Compare the same final binaries and hardware; investigate statistically repeatable regressions rather than accepting a single timing sample.

The 2026-08-21 ARM64 ShortRun checkpoint measured warmed cached brushes at 2.763 ns/op with 0 B allocated and warmed cached pens at 2.857 ns/op with 0 B allocated. Fresh `SolidBrush` and `Pen` construction measured 4.487 ns/op with 40 B and 11.195 ns/op with 112 B respectively. These are local microbenchmark observations, not broad renderer performance claims; the allocation-free warmed-resource invariant is also enforced by tests.

`ImageAttributesBenchmarks.RemapCpuBackedIcon64x64` guards the canonical WinForms recoloring path. Remapping is one source snapshot, one destination bitmap/pixel buffer, one O(M) lookup table, and one O(P) pixel pass for M mappings and P pixels. CPU-backed icons do not initialize a GPU device; a GPU-backed source requires one explicit readback because arbitrary exact color maps are not representable by the existing color-matrix shader.

`ColorPaletteBenchmarks.CreateOptimalPalette16From64x64` guards the CPU-only quantization path. The implementation takes one straight-pixel snapshot, builds a weighted unique-color histogram in O(P), and performs deterministic weighted median-cut partitioning with a palette size bounded to 256. It does not initialize a GPU device. Fixed-palette cardinalities and palette/property ownership boundaries are enforced by focused tests; the public contract was checked against the official [`ColorPalette` constructors](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.colorpalette.-ctor?view=windowsdesktop-10.0), [`PaletteType`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.palettetype?view=windowsdesktop-10.0), [`CreateOptimalPalette`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.colorpalette.createoptimalpalette?view=windowsdesktop-10.0), and [`Image.Palette`](https://learn.microsoft.com/dotnet/api/system.drawing.image.palette?view=windowsdesktop-10.0) documentation. The quantizer is original ProGPU code and does not copy framework implementation source.

`BitmapPixelMemoryBenchmarks.CopyRgbaToCallerOwnedLockBuffer` guards the CPU-only 256×256 BGRA export path used by caller-owned `LockBits`. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun checkpoint measured a 111.658 µs median (120.210 µs mean) with zero managed allocation. The three measured iterations make this coarse subsystem evidence rather than a universal throughput claim. The focused suite independently requires at most 512 bytes across 32 warmed 64×64 exports and covers packed/indexed/high-depth round trips. Public contract research and the managed/GPU boundary audit are recorded in [`docs/research/system-drawing-bitmap-pixel-memory-contract.md`](research/system-drawing-bitmap-pixel-memory-contract.md).

`BitmapPixelMemoryBenchmarks.ConvertRgbaToErrorDiffusedIndexedClone` guards a CPU-only 256×256 clone converted to 4-bit indexed color with a fixed custom palette and Floyd-Steinberg diffusion. Removing a redundant straight-alpha full-frame copy reduced the isolated ShortRun median from 4.549 ms to 3.844 ms (15.5%) and allocation from 519.62 KB to 263.54 KB (49.3%) on the same host. The three measured iterations are coarse subsystem evidence. The focused suite independently enforces an 18,000–24,000-byte window for the matching 64×64 clone-and-convert workload.

The 2026-08-21 ARM64 in-process ShortRun checkpoint measured 16-color quantization of the deterministic 64×64 gradient fixture at 1.491 ms/op with 496.75 KB allocated. The focused test independently enforces deterministic output and a 400,000–600,000-byte post-warmup allocation window. As with the recoloring checkpoint, this is local regression evidence from the restricted development environment rather than a renderer-wide claim.

`MatrixBenchmarks.TransformPointBatch` guards the managed affine hot path. It updates a preallocated 1,024-point span in place through the same `Matrix3x2` value consumed by the renderer. The 2026-08-21 ARM64 in-process ShortRun checkpoint measured 0.9072 ns per point with zero managed allocation. The focused suite independently requires exactly zero bytes across 64 warmed 1,024-point transforms. Contract sources and the managed/native applicability audit are recorded in [`docs/research/system-drawing-matrix-contract.md`](research/system-drawing-matrix-contract.md).

`LinearGradientBrushBenchmarks.LowerEightStopGradient` guards typed lowering of a custom eight-stop gradient, including spread, gamma mode, and coordinate transform state. The 2026-08-21 ARM64 in-process ShortRun checkpoint measured 62.66 ns/op with 304 B allocated. The focused suite independently enforces a 288–352-byte warmed allocation window. Public contract research, scalable-angle math, and the managed/native applicability audit are recorded in [`docs/research/system-drawing-linear-gradient-contract.md`](research/system-drawing-linear-gradient-contract.md).

`GraphicsPathBenchmarks` guards caller-owned point/type export, allocation-free iterator enumeration, analytic retained-geometry bounds, outline queries, curve widening, retained path deformation, and shaped text-outline materialization. The 2026-08-21 ARM64 isolated ShortRun checkpoint measured export of sixteen retained ellipses at 7.620 µs/op and bounds at 2.220 µs/op, both with zero managed allocation. The 2026-08-22 iterator checkpoint measured enumeration of the same 208-point snapshot into caller storage at 37.59 ns/op with zero managed allocation. The 2026-08-22 stroke checkpoint measured a retained four-point outline query at a 54.53 ns median and 112 B/op. Replacing two triangle figures per stroke rectangle with one closed retained quad reduced the sixteen-ellipse clone-and-widen median from 143.06 µs to 119.02 µs (16.8%) and allocation from 345,744 B to 256,120 B (25.9%) on the same .NET 10.0.11 ARM64 host. The first bilinear clone-and-warp checkpoint measured a 28.260 µs median (28.733 µs mean) with 55.99 KB allocated. The warmed `LibreWinForms` shaped-outline checkpoint measured an 11.439 µs median (11.200 µs mean) with 17.45 KB allocated. These ShortRun measurements have only three measured iterations, so raw artifacts remain the evidence and the results are coarse subsystem checkpoints rather than universal timing claims. The focused suite independently requires exactly zero bytes across warmed span exports and iterator enumeration, at most 256 B per line-outline query, at most 280,000 B for the fixed sixteen-ellipse widening workload, at most 72,000 B for the matching bilinear warp workload, and at most 24,000 B for the warmed shaped-outline workload. Public contract research, curve mathematics, text-outline architecture, and the managed/native applicability audit are recorded in [`docs/research/system-drawing-graphics-path-contract.md`](research/system-drawing-graphics-path-contract.md).

The 2026-08-21 ARM64 in-process ShortRun checkpoint measured the 64×64 remap at 19.59 µs/op with 16.48 KB allocated. The focused test independently enforces a bounded 16,384–20,000-byte allocation window after warmup. The in-process result is local diagnostic evidence for the restricted development environment; CI uses BenchmarkDotNet's normal isolated toolchain and publishes its JSON result.

## Implementation order

API work should proceed in dependency groups:

1. base ownership and shape (`Brush`, `Pen`, `Image`, `Graphics`, `Matrix`);
2. imaging codecs, pixel formats, palettes, locking, and image attributes;
3. drawing primitives, paths, regions, transforms, text, and fonts;
4. complete managed printing model with a typed backend boundary;
5. icons, cursors, native-handle adapters, and platform-specific escape hatches; and
6. remaining design-time converters and metadata.

Adding a type without its managed contract can increase the member-diagnostic count because ApiCompat begins inspecting that type. A subsystem is complete only when its full public shape and normal managed semantics are present, even if an unavailable OS operation fails explicitly at the backend boundary.
