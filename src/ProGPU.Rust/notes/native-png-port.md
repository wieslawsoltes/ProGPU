# Original native PNG port (2026-11-09; unvalidated)

This increment ports the complete original native PNG decoder and its original
bounded zlib/DEFLATE dependency to owned Rust implementation. It does not use a
foreign image decoder, import dependency source, initialize WebGPU, upload pixels,
or substitute managed PNG admission for native admission. No dependency changes.

The user explicitly requires porting all code before validation. No compilation,
lint, unit/source/differential tests, browser execution, benchmarks or package
validation was run for this increment. Only Rust source formatting was performed.

## Production integration

`image::PngDecoder::new(encoded)` parses and borrows one immutable source inventory.
Its `requirements()` and `encoded()` accessors are O(1). The source lifetime is
statically retained by the decoder. `decode_rgba_into(compressed, filtered, rgba)`
uses caller-owned scratch and output without allocating; source metadata, pass
layout, Huffman tables and decoding history use fixed local storage. All three
capacities are admitted before writing. Caller tails remain untouched.

`image::png_decode_requirements` and `image::decode_png_rgba` expose the native
query/decode boundary with `PngDecodeError` retaining native numeric categories
1 through 7; success uses Rust `Result::Ok` rather than an error-code sentinel.
`PngDecodeRequirements` retains original dimensions, sample/color/interlace fields
and exact compressed/filtered/RGBA capacities. This is a typed Rust API, not a new
native wire-ABI claim.

`Image::decode_png(encoded)` and `PngDecoder::decode_image()` allocate fallibly and
publish one immutable straight-alpha RGBA8 Image. It follows existing image
storage/device-independent ownership and the existing 16384-axis image contract.
The independent caller-scratch decoder keeps the native wider dimension domain;
owned Image admission remains a distinct `PngImageError::Image` outcome rather
than changing native codec error semantics. Allocation failure is also distinct.
The returned Image feeds existing ImageSource, retained drawing, masks and upload
caches without a decoder-specific renderer path or an encoded-source lifetime.
Caller-owned decoder objects can be reused, while decoded Image generations are
retained across warm frames; no per-frame decoding or callback is introduced.

## Original behavior retained

- Complete eight-byte signature; every chunk length and CRC; first/unique IHDR;
  all original palette, transparency and consecutive-IDAT rules; unknown-critical
  rejection; exact IEND and no trailing-byte admission. Optional/unknown ancillary
  behavior remains original, without inventing additional restrictions.
- Original grayscale/RGB/indexed/gray-alpha/RGBA and 1/2/4/8/16-bit combinations;
  checked native-size arithmetic; all seven original Adam7 descriptors, omitted
  empty passes and exact filter-byte/row totals.
- Original zlib CMF/FLG and dictionary rejection, exact DEFLATE byte consumption,
  stored/fixed/dynamic blocks, canonical Huffman oversubscription and incomplete
  table policy, code-length repeat admission, 32-KiB distance limit, dependent
  history copy order and Adler-32 validation. Compression failures map exactly
  to native PNG insufficient-buffer versus invalid-compressed-data categories.
- Original native Up/Sub/Average/Paeth filtering, wrapped byte arithmetic, pass
  reset and Paeth tie order. Full filter admission precedes output conversion;
  every indexed-color value is admitted before the first RGBA byte is published.
- Original packed sample extraction, rounded 16-bit channel scaling, unscaled raw
  transparency comparisons, palette alpha defaults and Adam7 destination scatter.
  Output remains straight alpha, including RGB bytes of fully transparent pixels.

Rust's immutable source borrow allows the admitted chunk inventory to be reused
for the IDAT copy, replacing the native decoder's second CRC parse without changing
bytes, admission or error ordering. No mutable encoded-source callback can run
between passes. The public requirements query and decoder acquire one source once.
Scratch may be partially written on malformed compressed/filter data, as in native;
no successful Image is published on failure. Palette/filter failures do not publish
partial RGBA output.

## Execution policy and independent lanes

`with_execution_preference` accepts the common Automatic, CpuSimd and
ScalarReference policies. NativeCompute/GpuShader are incompatible with this
CPU-only codec contract and reject; forced SIMD rejects targets lacking compiled
SSE2/NEON/SIMD128 support. Automatic chooses intrinsic lanes when compiled,
otherwise the explicit scalar implementation. `execution_path()` records that
selection; it is not a measured performance or platform qualification claim.

Independent Up-filter bytes and RGBA sample components use `wide::u32x4`.
Sixteen-bit scaling retains integer arithmetic: for the bounded original numerator
n = sample*255+32767, floor(n/65535) equals (n+1+(n>>16))>>16. Packed 1/2/4-bit scales
use the exact integral 255/maximum factor. ScalarReference retains the native
division expression. Neither route adds a floating conversion or changes pixels.
Up tails remain bounded scalar. Palette gathers/first-invalid admission and
Adam7 scatter retain source order. Sub/Average/Paeth, variable-bit Huffman reads,
CRC/Adler recurrence and overlapping LZ history have genuine dependencies and
keep their original scalar traversal. Stored DEFLATE bytes use a bounded bulk
copy, retaining native truncated-prefix scratch state before failure.

## Architecture and pending scope

The existing DESIGN.md research remains applicable: Skia/Direct2D resource-domain
separation keeps CPU codec ownership independent from GPU realization; retained
WebRender/Vello-style boundaries keep decoded generations reusable; shaping and
font/image metadata remain independent CPU resources. No new external engine
implementation or algorithm was studied/imported for this direct original port.

Pending validation includes source rejection/error-order/metadata and output
parity; complete color/depth/filter/Adam7 inventory; malformed Huffman, zlib,
checksums, palette and capacity cases; zero-copy/retained ownership and SIMD/scalar
bytes; WASM architecture admission; allocation/performance and release consumers.
The original gzip API is not exposed or claimed by this PNG-private dependency;
other encoded image formats, managed codec/shim factories, scanline/incremental
APIs and native provider/package qualification remain separate port work.

## Original source provenance

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/include/progpu_native_image.hpp` | `0f48cb0b5b75ba050deec23eacd083e64c231a961206d5db0ab80ba306e77aa3` |
| `src/ProGPU.Native/src/Image/progpu_native_image.cppm` | `bac6507ce1b0082155dc095f35aa83a8685ea16d7efc2e57ddbc9e40d50cb9f1` |
| `src/ProGPU.Native/src/Image/progpu_native_png_internal.hpp` | `bc041c944b2cab64c82eaca095b9936f6d689d0fca4f565ced0b8949db64bf44` |
| `src/ProGPU.Native/src/Image/progpu_native_png.cpp` | `526baf0c60bcb1de022932d0fe471f9a5b31763707ea630e6b93b8a0acb7aee7` |
| `src/ProGPU.Native/src/Image/progpu_native_png_layout.cpp` | `854257e78056cc6b7beef1e7926bf7dc54b404842f32e240f2e31c3138b85dc2` |
| `src/ProGPU.Native/src/Image/progpu_native_png_filter.cpp` | `43a1a2535a75645b78d6e3a9f85ea527687618495723a69fe8d6b9c319cb93c6` |
| `src/ProGPU.Native/src/Image/progpu_native_png_crc.cpp` | `3f77df70ec7c6ab634b69ba4d34fb70b54dcdd7cb8c6c9c731a8aecf62819529` |
| `src/ProGPU.Native/src/Image/progpu_native_png_pixels.cpp` | `8d758660b37b23491a0dc6a5582c77972b3279fe6f1fc9dad0f777ed859dd4e8` |
| `src/ProGPU.Native/include/progpu_native_compression.hpp` | `8db8bb5e3d2bcbe84820bfd959885da1d3c8bac99cd6fbd214451d35fbb26b82` |
| `src/ProGPU.Native/src/Compression/progpu_native_zlib.cpp` | `ef666c6152de9d19acd0a62c858556c6ed9fd7ecc30d82c88695c73837d6ea0f` |
| `src/ProGPU.Native/src/Compression/progpu_native_deflate_internal.hpp` | `ade5e0d6ce2e03c6160da743d834614d8c867da285da4f53a90b4a8e9670c112` |
| `src/ProGPU.Native/src/Compression/progpu_native_deflate.cpp` | `bcf8b2b3a2b08d7d4fb40857fb9358d49690cca83dcbacfe5ed361b77702e74b` |
| `src/ProGPU.Native/src/Compression/progpu_native_deflate_huffman.cpp` | `97dcb44db97b9a6def105e41fbc478f191cfb0ed5305b3ee9cb6353f0030d3cc` |
