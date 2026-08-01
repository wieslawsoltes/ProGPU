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

The current pinned comparison records 4,222 official entries, 4,503 ProGPU
entries, 3,285 exact matches, 937 missing entries, and 1,218 ProGPU-only
entries. The missing surface comprises 60 type identities, 19 fields, 19
interfaces, 575 methods, 116 properties, and 148 semantic attributes. This is
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

### OpenGL and Metal backend handle descriptors

`GRGlFramebufferInfo`, `GRGlTextureInfo`, and `GRMtlTextureInfo` now match the
official 4.151.0 value surfaces and sequential ABI layouts. OpenGL framebuffer
and texture descriptors retain their unsigned object identifiers and formats
inline, with protection state normalized into the final byte field. The Metal
descriptor retains one native texture handle. Official constructor and property
names, overloads, typed equality, object equality, hashing, operators, readonly
accessors, and the two declared `IEquatable<T>` interfaces are preserved; the
former accidental aliases and optional-parameter signature have been removed.

These structs are CPU-only borrowed-handle metadata. Construction, mutation,
comparison, and hashing are fixed `O(1)` work, allocate nothing, do not claim
ownership of the referenced native resource, and cannot initialize GL, Metal,
or WebGPU. ProGPU rendering continues through its typed WebGPU resource model;
these compatibility values do not introduce a second renderer. Independent
tests verify private field order/types, byte protection normalization, official
parameter names, pointer identity, and complete value behavior. Three
alternating Apple M3 Pro Release process pairs retained exact checksums and zero
managed allocations at `0.996` ProGPU/native (`2.401` versus `2.410` ns/op).
Matched Time Profiler captures measured `2.373` versus `2.385` ns/op and matched
Allocations captures measured zero bytes per operation. The clean-room contract
uses the public
[framebuffer API](https://learn.microsoft.com/dotnet/api/skiasharp.grglframebufferinfo),
[OpenGL texture API](https://learn.microsoft.com/dotnet/api/skiasharp.grgltextureinfo),
[Metal texture API](https://learn.microsoft.com/dotnet/api/skiasharp.grmtltextureinfo),
[OpenGL framebuffer model](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glBindFramebuffer.xhtml),
and [Metal resource ownership model](https://developer.apple.com/documentation/metal/resource-fundamentals).

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

### Pixel-format and LCD geometry metadata

`SkiaExtensions` now matches all 18 entries in the 4.151.0 metadata contract,
replacing the former non-official `SKGlExtensions` identity. Pixel-geometry
classification, byte and bit-shift sizes, alpha compatibility, and OpenGL sized
formats cover all 29 declared color types. Unknown declared formats retain
their documented zero values, while out-of-range enum values fail with the
official `colorType` argument boundary. `SKImageInfo` now delegates to the same
single format-size contract instead of retaining a second mapping.

Every valid query is allocation-free fixed `O(1)` CPU work and cannot initialize
WebGPU. Independent tests exhaust the color-type and alpha-type matrices,
geometry categories, GL mappings, invalid enums, and one million stable queries.
The source-built Avalonia.Skia projects continue to compile for net8 and net10
against the official extension identity. Three alternating Apple M3 Pro Release
process pairs produced exact checksums and zero allocations; ProGPU measured
`0.683` of native time for the combined workload. Matched Time Profiler and
Allocations captures from the same binaries preserved that ordering, exact
checksums, and zero managed bytes per operation. The clean-room contract uses
the public
[SkiaExtensions API](https://learn.microsoft.com/dotnet/api/skiasharp.skiaextensions),
[Skia color-type documentation](https://api.skia.org/SkColorType_8h.html), and
[Khronos sized internal formats](https://registry.khronos.org/OpenGL-Refpages/gl4/html/glTexStorage2D.xhtml).

### UTF text conversion utilities

`StringUtilities` now matches all ten entries in the 4.151.0 metadata contract.
UTF-8, little-endian UTF-16, and little-endian UTF-32 conversion use replacement
fallbacks, return exactly one owned byte array or string, expose bounded array,
span, slice, and pointer decode overloads, and reject glyph-ID or out-of-range
encodings before conversion. Encoding is `O(C + B)` and decoding is `O(B + C)`
for `C` UTF-16 code units and `B` encoded bytes, with only the caller-owned
result allocation and no WebGPU initialization.

`GetUnicodeCharacterCode` validates exactly one complete Unicode scalar and
returns it allocation-free for every supported UTF encoding. This intentionally
corrects the official 4.151 wrapper's observable short-buffer failure for
ordinary UTF-8/UTF-16 characters while retaining the documented API contract;
incomplete surrogates and multiple scalars fail before returning partial data.
Independent tests cover exact byte forms, supplementary scalars, replacement
fallbacks, pointer/slice boundaries, null/empty ownership, invalid encodings,
and glyph-ID rejection. Three alternating Apple M3 Pro Release process pairs
produced exact checksums for matched workloads: roundtrip conversion measured
`0.960` ProGPU/native with equal `290.651` managed bytes per operation, while
the scalar query measured `0.041` and `0` versus `256` bytes. The clean-room
Matched Time Profiler and Allocations traces from the same Release binaries
retained the checksum, allocation, and timing ordering. The clean-room design
follows the public
[StringUtilities contract](https://learn.microsoft.com/dotnet/api/skiasharp.stringutilities),
[Unicode encoding forms](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-3/),
and [.NET Encoding contract](https://learn.microsoft.com/dotnet/api/system.text.encoding).

### Color-space chromaticity primaries

`SKColorSpacePrimaries` now matches all 35 entries in the 4.151.0 metadata
contract. Eight mutable inline floats retain the red, green, blue, and white
chromaticities; constructors and the public `Values` snapshot preserve caller
ownership. Conversion solves one homogeneous 3x3 primary matrix and applies
Bradford chromatic adaptation into the ICC D50 profile-connection space. The
general conversion is fixed `O(1)` CPU work with no heap allocation or WebGPU
initialization. Degenerate matrices and non-finite or out-of-unit coordinates
fail transactionally with an empty result.

The common sRGB and Display P3/D65 combinations use immutable matrices computed
from the same public chromaticities and D50 model. This keeps the dominant path
at fixed comparisons plus one inline struct copy without weakening arbitrary
gamut support. Independent tests cover value ownership, every mutable scalar,
equality, invalid and degenerate inputs, a zero-y boundary primary, and sRGB/P3
conversion. Three alternating Apple M3 Pro Release process pairs retained exact
semantic checksums and zero managed allocations: the common-gamut workload
measured `0.172` ProGPU/native (`6.001` versus `34.830` ns/op). Matched Time
Profiler and Allocations captures from the same binaries measured `5.904`–
`6.022` versus `33.791`–`33.861` ns/op with the same checksum and zero bytes per
operation. The clean-room design follows the public
[SkiaSharp primaries contract](https://learn.microsoft.com/dotnet/api/skiasharp.skcolorspaceprimaries),
[Skia public color-space contract](https://skia.googlesource.com/skia/+/fc75b5a/include/core/SkColorSpace.h),
and the
[ICC.1:2022 D50/Bradford model](https://www.color.org/specifications/ICC.1-2022-05.pdf).

### Animated-codec frame ABI

`SKCodecFrameInfo` now matches the official sequential layout as well as its
existing public value behavior. Its two Boolean properties use normalized
one-byte storage in the declared native field order, preserving the compact
codec interop contract without exposing the storage fields. All eight public
properties, equality, hashing, and operators remain allocation-free fixed
`O(1)` CPU operations and do not initialize a decoder or WebGPU.

Independent tests inspect the compiled private layout, verify byte rather than
managed-Boolean storage, and exercise property normalization and full-value
equality. Three alternating Apple M3 Pro Release process pairs retained exact
checksums and zero allocations at `1.001` ProGPU/native (`1.255` versus `1.254`
ns/op), which is performance-neutral at timer resolution. Matched Time Profiler
and Allocations captures retained exact checksums and zero bytes per operation;
their medians ranged from `1.220`–`1.291` ns/op for ProGPU and `1.196`–`1.226`
ns/op for native. The clean-room contract follows the public
[SKCodecFrameInfo API](https://learn.microsoft.com/dotnet/api/skiasharp.skcodecframeinfo)
and the official package's ECMA-335 sequential field metadata.

### Encoder and XPS descriptor ABI

`SKJpegEncoderOptions`, `SKPngEncoderOptions`, and `SKDocumentXpsOptions` now
match their official sequential layouts in addition to retaining their existing
public value contracts. JPEG keeps its three value fields followed by zeroed
metadata pointer/length/origin transport slots; PNG keeps its filter and level
followed by three zeroed native pointer slots; XPS uses one float and a
normalized byte-backed Boolean. The private transport fields are never exposed,
dereferenced, or used to add an external encoder dependency.

Construction, property access, equality, and hashing remain fixed `O(1)` CPU
work with zero allocation and no codec or WebGPU initialization. Independent
tests inspect the compiled private field order/types and verify all public
values. Three alternating Apple M3 Pro Release process pairs retained exact
checksums and zero allocations at `1.024` ProGPU/native (`1.261` versus `1.232`
ns/op), within timer noise for the combined value workload. Matched Time
Profiler and Allocations captures retained the exact checksum and zero bytes;
ProGPU measured `1.169`–`1.217` versus native `1.190`–`1.221` ns/op. The
clean-room contract follows the public
[JPEG options API](https://learn.microsoft.com/dotnet/api/skiasharp.skjpegencoderoptions),
[PNG options API](https://learn.microsoft.com/dotnet/api/skiasharp.skpngencoderoptions),
[XPS options API](https://learn.microsoft.com/dotnet/api/skiasharp.skdocumentxpsoptions),
and the pinned package's ECMA-335 sequential field metadata.
