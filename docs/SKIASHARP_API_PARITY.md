# SkiaSharp API parity

ProGPU validates its clean-room `SkiaSharp` shim against the public ECMA-335
metadata in the official `SkiaSharp` NuGet package. The lock in
`eng/skiasharp-api-baseline.json` pins the package URI, SHA-512, target-framework
reference assembly, namespace, and monotonic regression budget.

The current contract is `SkiaSharp` `4.151.0`, using
`ref/net10.0/SkiaSharp.dll`. This advances the previous implementation record
from Skia m148 to the current stable SkiaSharp package without consulting or
copying its implementation source.

Run the complete gate with:

```bash
./eng/progpu-verify-skiasharp-api.sh
```

The gate verifies the official package hash, extracts only its public reference
metadata, self-tests the canonical metadata reader, builds the ProGPU shim, and
writes deterministic JSON and Markdown reports under
`artifacts/skiasharp-api/`. CI fails if exact matches decrease or missing entries
increase.

API equality is necessary but not sufficient. Every implementation slice must
also include independent behavioral tests, Svg.Skia/Avalonia.Skia compatibility
evidence where applicable, and matched Release benchmarks for native SkiaSharp
and ProGPU. Rendering work must preserve ProGPU's WebGPU ownership, quality,
device-loss, bounded-resource, and allocation contracts.

The matched benchmark runner is
`eng/progpu-run-skiasharp-benchmarks.sh`. It compiles identical source against
official SkiaSharp and ProGPU, alternates process order, verifies semantic
checksums, and preserves raw median/p95 timing and allocation distributions plus
environment metadata. Its scheduled workflow runs on macOS, Linux, and Windows;
small timing deltas on shared runners remain informational until calibrated on
dedicated hardware.

The initial local Release run on an Apple M3 Pro, .NET 10.0.5, macOS 26.4.1,
using three alternating process pairs and 72 measured samples per backend,
produced the following diagnostic baseline:

| Workload | Native median ns/op | ProGPU median ns/op | ProGPU/native | Native B/op | ProGPU B/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| point arithmetic | 2.076 | 2.158 | 1.039 | 0 | 0 |
| matrix map point | 8.713 | 4.547 | 0.522 | 0 | 0 |
| path builder, detach, and bounds | 808.542 | 3,284.499 | 4.062 | 168 | 3,520 |

These figures identify path construction/ownership as the first measured CPU
and allocation hotspot. They are not a cross-platform performance claim; raw
distributions and environment records remain in generated artifacts, and the
path work requires matched profiling plus equivalent before/after runs.

## Current baseline

The current pinned comparison records 4,222 official entries, 4,412 ProGPU
entries, 3,177 exact matches, 1,045 missing entries, and 1,235 ProGPU-only
entries. The missing surface comprises 69 type identities, 20 fields, 22
interfaces, 620 methods, 129 properties, and 185 semantic attributes. This is
a starting point, not a compatibility claim, and the matching/missing budget
is ratcheted after every reviewed slice. ProGPU-only entries are audited and
removed when accidental; explicitly documented extension seams remain outside
the official parity claim.

## Planned implementation order

1. Close metadata-only value, enum, descriptor, and ownership contracts that do
   not require GPU initialization.
2. Complete bitmap, pixmap, image, codec, stream, and color-space contracts with
   explicit CPU/GPU ownership and no accidental readback or upload.
3. Complete paths, regions, paint, text, picture, document, and canvas behavior
   over reusable ProGPU primitives.
4. Complete shaders, filters, blenders, masks, vertices, atlas, surface, and GPU
   context contracts through retained WebGPU pipelines and embedded shaders.
5. Prove source-level Avalonia.Skia substitution, close the full Svg.Skia corpus,
   and enforce representative CPU, GPU, frame-time, and memory advantages over
   the official runtime on supported platforms.

Primary public contracts:

- <https://www.nuget.org/packages/SkiaSharp/4.151.0>
- <https://learn.microsoft.com/dotnet/api/skiasharp>
- <https://www.w3.org/TR/SVG2/>
- <https://www.w3.org/TR/webgpu/>

## Implemented parity checkpoints

### Premultiplied color values

`SKPMColor` now matches the complete 4.151.0 public metadata contract. Scalar
premultiply and unpremultiply are allocation-free fixed-work operations; array
overloads allocate exactly one result array and process `N` colors in `O(N)`
time with `O(1)` auxiliary storage. The implementation retains native RGBA
memory packing, rounded divide-by-255 premultiplication, and a generated
read-only 8.24 reciprocal table for deterministic unpremultiplication without
per-channel division. It is CPU-only and cannot initialize WebGPU.

Independent tests cover packed identity, logical channels, formatting,
operators, allocation ownership, transparent input, and component bounds. The
matched benchmark exhaustively checks every alpha/component pair and separately
measures scalar and 64-element array overloads against the official package.
On the recorded Apple M3 Pro Release run, all four semantic checksums and
managed allocations matched exactly. ProGPU/native median ratios were `1.014`
for scalar premultiply, `1.121` for scalar unpremultiply, `1.080` for the
64-element premultiply array, and `1.183` for the unpremultiply array. These
small but repeatable remaining CPU gaps are retained as optimization work; this
checkpoint establishes parity without claiming a performance win.
The design used the public
[SkiaSharp contract](https://learn.microsoft.com/dotnet/api/skiasharp.skpmcolor)
and Skia's documented
[premultiplied color](https://api.skia.org/SkColor_8h.html) and
[unpremultiply scale](https://api.skia.org/classSkUnPreMultiply.html)
contracts. No foreign implementation code, source layout, or helper structure
was incorporated.

### OpenType four-byte tags

`SKFourByteTag` now matches all 18 entries in its 4.151.0 metadata contract.
The four-byte readonly value uses OpenType's big-endian display order, preserves
packed `uint` identity, pads non-empty short tags with trailing spaces, truncates
long tags, and preserves native zero identity for null or empty input. Character
construction narrows each UTF-16 code unit to its low byte, matching the
observable API behavior without validating font-table policy at this value
boundary.

Construction, parsing, equality, hashing, and conversions are allocation-free
fixed-work operations. Formatting allocates only its four-character result.
Matched Release checksums cover string/span parsing, construction, conversion,
and formatting. Across three alternating Apple M3 Pro process pairs, value
operations measured `1.127` ProGPU/native and formatting measured `0.127`, with
`32` versus `280` managed bytes per formatted tag. These local figures are evidence for the
slice, not a cross-platform claim. The clean-room design follows the
[OpenType Tag data type](https://learn.microsoft.com/en-us/typography/opentype/spec/otff)
and the public
[SkiaSharp parsing contract](https://learn.microsoft.com/dotnet/api/skiasharp.skfourbytetag.parse).

### Red/blue pixel channel swizzle

`SKSwizzle` now matches all six entries in the 4.151.0 public metadata
contract. The reusable `PixelChannelSwizzler` core operates on tightly packed
four-byte pixels in `O(N)` time and `O(1)` auxiliary storage, supports bounded
overlapping copies, and never initializes WebGPU. On ARM64, copy and in-place
paths use fixed 32-bit and 16-bit byte reversals followed by a mask select,
avoiding both an intermediate buffer and table-lookup stalls. Other targets use the
portable hardware-accelerated `Vector128` shuffle and scalar tails.

Independent tests cover in-place and copy overloads, pointer entry points,
count clamping, overlap direction, stable replay allocations, and incomplete
trailing pixels. Valid complete-pixel inputs match the official behavior. The
span-only overload deliberately preserves an incomplete trailing pixel rather
than allowing the official wrapper's observable out-of-bounds native access;
this is a memory-safety improvement outside the documented complete-pixel
contract. Three alternating Apple M3 Pro Release process pairs retained equal
managed allocations and exact semantic checksums. Copy measured `0.962`
ProGPU/native and in-place measured `1.213`; the latter remains an explicit CPU
optimization target. Matched Time Profiler and Allocations traces from the same
Release binaries retained stable checksums and `0.824`/`0.412` managed bytes per
operation for both implementations. The raw distributions, trace bundles, and
exported sample tables remain diagnostic evidence rather than a cross-platform
claim.
The design follows the public
[SkiaSharp swizzle contract](https://learn.microsoft.com/dotnet/api/skiasharp.skswizzle)
and Skia's documented
[RGBA/BGRA transform](https://api.skia.org/SkSwizzle_8h.html).

### Native compatibility version

`SkiaSharpVersion` now matches all four entries in its 4.151.0 metadata
contract. The clean-room shim reports the observed `151.0` native and minimum
compatibility levels and succeeds in both throwing and non-throwing check modes
because ProGPU supplies the complete implementation without loading a separate
native Skia binary. Both properties share one immutable process-wide `Version`,
so repeated queries are allocation-free fixed `O(1)` operations.

Independent tests cover exact version values, compatibility modes, stable
identity, and one million allocation-free queries. Three alternating Apple M3
Pro Release process pairs produced exact semantic checksums; ProGPU measured
`0.066` of native time and `0` versus `32` managed bytes per operation. The
clean-room behavior follows the public
[SkiaSharpVersion contract](https://learn.microsoft.com/dotnet/api/skiasharp.skiasharpversion)
and retains no native-library discovery or loader side effects.
