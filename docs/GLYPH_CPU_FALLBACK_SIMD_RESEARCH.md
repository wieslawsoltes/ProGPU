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

Checkpoint `2960fb39`, refined by `ffb285af`, removes the next managed-only
traversal gap. The rasterizer now collects the eight Y-subscanline crossing
spans for a row before visiting X, creates each pixel's horizontal sample
vectors once, applies all eight independent winding spans to those vectors,
and writes the final quantized byte directly. The crossing arena is still one
pooled allocation sized from the typed segment bound, the nine span offsets
remain stack-resident, and the output allocation is unchanged. Vector setup is
qualified by `Vector256.IsHardwareAccelerated`; a Vector128-only machine does
not execute unsupported 256-bit setup work. The scalar implementation remains
structurally independent.

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

## Eight-subscanline managed traversal follow-up

The final `ffb285af` implementation passed all 19 focused execution-policy and
SIMD/scalar differential tests. Eight alternating processes on Apple M3 Pro,
macOS 26.6, and .NET 10.0.5 used 300 warmups followed by 60 samples of 50
glyphs. Median-of-process p50 improved from 218.649 to 205.471 us/glyph
(6.0%); median p95 improved from 227.590 to 212.347 us/glyph (6.7%). Every
process retained checksum 175 and 4,120 B/glyph, so the change neither alters
coverage nor adds allocation.

The exact commit archive then built with zero warnings under .NET SDK 10.0.400
in the Windows 11 ARM64 Parallels guest. Three fresh Vector128 runs under .NET
10.0.11 retained checksum 175 and 4,120 B/glyph, with p50 values 247.424,
235.354, and 228.988 us/glyph. Host and guest source SHA-256 matched at
`45BA556F...CD3FE0C`; the archive matched at
`C6A295B3...E1E242F`. This is exact Windows correctness evidence, not a VM
performance comparison. The guest and the Rosetta x64 runtime both reported
`Vector256=False`; actual Vector256 runtime qualification therefore remains an
x64 CI/hardware gate rather than an inferred local result.

## Direction-partitioned crossing follow-up

Checkpoint `f8c6cc7e` removes the remaining direction branch and transient
span construction from the managed winding hot loop. A baseline managed CPU
trace on Apple M3 Pro attributed 44.74% exclusive CPU to
`CountCoveredSamplesSimd128`, 28.83% to its rasterizer caller, and 22.95% to
crossing construction. The accepted layout keeps one pooled float arena with
one fixed-capacity block per Y subscanline. Positive crossings grow from the
front of each block and negative crossings grow from the back. The two
direction-specific intrinsic loops can then add or subtract comparison masks
without loading a direction field or branching per crossing.

Integer winding addition is commutative, so reordering crossings by direction
does not change nonzero-winding semantics. The scalar oracle remains
structurally independent. The intrinsic loop receives the already-validated
arena by reference plus bounded starts/counts, avoiding two `ReadOnlySpan`
constructions for every pixel and subscanline. `Unsafe.Add` cannot escape the
rented arena: all offsets are derived from the checked
`segmentCount * 3 * 8` capacity, and collection advances the positive and
negative cursors only for roots that the previous implementation would have
stored. The requested pooled payload also falls from one eight-byte
`CpuCrossing` per root to one four-byte X coordinate per root.

The focused Release suite is now 21/21. In addition to the existing line,
quadratic, cubic, vector-width, and scalar-tail cases, it compares opposed
outer/inner contour directions and a zero-segment glyph byte-for-byte with the
scalar oracle. The 64x64 benchmark validates equality before timing and every
measured process retained checksum 36 and 4,120 B/glyph.

Eight alternating baseline/candidate pairs on Apple M3 Pro, macOS 26.6, and
.NET 10.0.5 used 300 warmups followed by 80 samples of 100 glyphs:

| Metric | Baseline median of process percentiles | `f8c6cc7e` median | Change |
| --- | ---: | ---: | ---: |
| p50 | 208.648 us/glyph | 174.606 us/glyph | -16.3% |
| p95 | 240.219 us/glyph | 222.808 us/glyph | -7.2% |
| p99 | 302.034 us/glyph | 262.180 us/glyph | -13.2% |

All eight candidate p50 values beat their immediately preceding baseline.
These are machine/workload-specific results, not a cross-platform speed
claim. An explicit replicated-crossing-vector temporary was exact but slower,
and precomputing one additional negative-count stack array was exact but
effectively flat with mixed paired results; both experiments were rejected.

The exact commit archive SHA-256 is
`DC1F64A71366336D447C72333DD87BA0839D214967596FA11BC73C08EFA3180E`.
It restored and built the benchmark with zero warnings under .NET SDK
10.0.400 in Windows 11 ARM64 Parallels. The archive's pinned WinUI submodule
content was hydrated from the unchanged submodule checkout; `generic.xaml`
matched SHA-256
`4C4085838721C0AFCB1A9EE17591C0655CDDDADB26D330788E08BCD7F1AF8285`.
The Windows ARM64 focused suite passed 21/21 under .NET 10.0.11. Three
Vector128 benchmark processes reported p50 226.390, 211.794, and 217.026
us/glyph with checksum 175 and 4,120 B/glyph.

Ubuntu 24.04 ARM64, detached at the same commit, built the benchmark with zero
warnings and passed the same 21/21 tests under the available .NET 11 preview
host with `DOTNET_ROLL_FORWARD=Major`. Three Vector128 processes reported p50
204.072, 235.584, and 225.172 us/glyph with the same checksum and allocation.
The repository's pre-existing untracked `external/ACadSharp/` directory was
left untouched.

Finally, a self-contained `win-x64` publish ran under Windows-on-ARM emulation
and reported `Vector128=True`, `Vector256=True`, and `Vector512=False`. Three
Vector256 processes retained checksum 175 and 4,120 B/glyph, with p50 values
556.960, 620.446, and 522.980 us/glyph. The executable SHA-256 was
`938C78099A766487717B7E23B73220A1746D49646893FA16C599EC7ECB223EA5`.
This closes functional coverage of the 256-bit branch; emulated timings are
correctness evidence only and do not replace a physical-x64 performance gate.
