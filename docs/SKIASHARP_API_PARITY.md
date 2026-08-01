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

## Current baseline

The first pinned comparison records 4,222 official entries, 4,362 ProGPU
entries, 3,127 exact matches, 1,095 missing entries, and 1,235 ProGPU-only
entries. The missing surface comprises 73 type identities, 20 fields, 24
interfaces, 653 methods, 135 properties, and 190 semantic attributes. This is
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
