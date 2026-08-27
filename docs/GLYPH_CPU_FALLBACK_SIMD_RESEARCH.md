# Glyph CPU fallback SIMD research

Date: 2026-08-27

## Scope

This checkpoint optimizes only the exact managed CPU fallback used when the
typed compute policy explicitly resolves to `IntrinsicSimdCpu`. It does not
change shaping, glyph selection, atlas identity, the 8x8 sample grid, winding
rules, coverage quantization, or the GPU-first automatic order. The scalar
implementation remains the independent oracle.

## Primary-source review

| Source | Relevant architecture | ProGPU decision |
| --- | --- | --- |
| [.NET SIMD and hardware intrinsics](https://learn.microsoft.com/en-us/dotnet/standard/simd) and [`Vector128.Widen`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector128.widen?view=net-10.0) | Fixed-width vectors support comparison masks, mask extraction, widening, narrowing, and unaligned span load/store operations. | Use explicit `Vector256<T>` when accelerated and a `Vector128<T>` fallback. Convert comparison masks to one scalar bit mask with `ExtractMostSignificantBits`; normalize 16 coverage bytes per iteration with widening and narrowing; retain a bounded scalar tail. |
| [Skia glyph-run painter](https://skia.googlesource.com/skia/+/3275cf5f8fdd3ef6cf4af9175568854bf5c76c3c/src/core/SkGlyphRunPainter.h) | Skia separates source/device glyph caches and selects mask, path, SDF, or fallback representations before atlas placement. | Keep this work behind ProGPU's existing atlas cache and execution policy. Do not mix layout or glyph-cache identity into a raster-kernel optimization. |
| [DirectWrite glyph-run analysis](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriteglyphrunanalysis) and [alpha-texture creation](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwriteglyphrunanalysis-createalphatexture) | DirectWrite exposes glyph-run analysis and bounded alpha-texture generation as a stage after positioned glyph runs exist. | Preserve positioned glyph inputs and optimize only bounded coverage generation. |
| [Windows graphics and Win2D overview](https://learn.microsoft.com/en-us/windows/apps/develop/graphics) | Win2D is an immediate-mode, GPU-accelerated 2D API while DirectWrite/DWriteCore owns device-independent layout and text rendering. | Retain GPU compute/raster stages as the fastest qualified defaults; CPU SIMD stays an explicit fallback, not the primary text path. |
| [Firefox rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | WebRender prepares scenes separately and GPU text uses glyph-atlas resources whose raster work can be prepared independently. | Reuse retained glyph coverage and avoid adding per-frame shaping, readback, or per-glyph submission work. |
| [Vello](https://github.com/linebender/vello/blob/main/README.md) and its [glyph-rendering plan](https://github.com/linebender/vello/issues/204) | Vello separates scene encoding, text layout integrations, and GPU rendering; its glyph work treats outline-to-render representation as a distinct stage. | Keep ProGPU's typed scene/layout boundary unchanged and optimize only the CPU representation fallback. |
| [Parley](https://github.com/linebender/parley) | Parley produces positioned glyph layout from font selection, shaping, and line layout components. | Do not invalidate or rebuild layout during raster fallback selection. |
| [HarfBuzz shaping plans](https://harfbuzz.github.io/shaping-and-shape-plans.html) and [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | Shaping produces glyph identifiers and positions; outline extraction and rendering are subsequent concerns. | Leave shaping, fallback, variation, and glyph positions untouched. |

No implementation text was copied from these projects. The implementation is
an original adaptation of ProGPU's existing analytic crossing algorithm to
the public .NET intrinsic API.

## Implementation and applicability audit

For each destination pixel, eight independent X samples share one crossing
span. The managed fallback now:

- uses one accelerated 256-bit vector or two 128-bit vectors for the eight
  samples;
- adds or subtracts comparison masks directly for the guaranteed `+1` and
  `-1` crossing directions;
- reduces zero winding lanes with a bit mask and population count instead of
  scalar lane extraction;
- converts row sample counts to R8 coverage in 16-byte vector blocks using the
  exact integer identity `(samples * 255 + 32) >> 6`; and
- uses a bounded scalar tail for row widths not divisible by 16.

The corresponding native C++ fallback already has dedicated NEON and SSE2
`intrinsic_winding_16`/`intrinsic_winding_8` implementations. It processes
two pixels (16 samples) per iteration, applies directions directly to masks,
performs intrinsic reduction, and has a dedicated odd-pixel tail. No native
source change is applicable for this managed-specific gap.

## Differential and performance evidence

The focused Release suite passed 19/19 tests on .NET 10.0.5 ARM64. It compares
the intrinsic output byte-for-byte with the scalar oracle for line,
quadratic, and cubic contours, including widths 1, 15, 16, 17, and 31 so the
no-vector, exact-vector, and scalar-tail cases are covered.

The committed benchmark uses one deterministic 64x64, 12-segment glyph,
validates exact output before timing, performs 300 warmups, then records 60
samples of 50 glyphs. Three fresh processes were measured on Apple M3 Pro,
macOS 26.6, .NET 10.0.5:

| Managed implementation | Process p50 values (microseconds/glyph) | Median of process p50 | Allocation |
| --- | --- | ---: | ---: |
| Previous `Vector<T>` path | 519.896, 480.466, 598.320 | 519.896 | 4,120 B/glyph |
| Explicit fixed-width intrinsics | 297.884, 290.504, 293.924 | 293.924 | 4,120 B/glyph |

The representative median improved by 43.5%; this is a machine/workload-bound
claim, not a cross-platform promise. A separate scalar-oracle process measured
49,097.540 microseconds/glyph p50 with the same allocation. That comparison
also includes the SIMD path's existing scanline crossing reuse, so it is not
presented as a pure instruction-width speedup.

Reproduce the managed benchmark with:

```bash
dotnet build src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj \
  -c Release --no-restore -m:1 -nr:false

dotnet run -c Release --no-build \
  --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -- \
  --glyph-cpu-fallback --warmup 300 --samples 60 --iterations 50

dotnet run -c Release --no-build \
  --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -- \
  --glyph-cpu-fallback --scalar --warmup 300 --samples 60 --iterations 50
```

Release qualification still requires x64 hardware coverage and the existing
native, Metal, D3D12, and Vulkan execution-policy lanes. This local checkpoint
does not establish those pending results.
