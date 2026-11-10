# Original encoded/raster port (2026-11-10; unvalidated)

Implemented original ICO/CUR directory ranking, core/info DIB headers, indexed
1/4/8-bit and 16/24/32-bit pixels, contiguous/disjoint bitfields, RLE4/RLE8 command
order/alignment/end markers, independent declared compressed extents, bottom-up
rows, actual-alpha versus AND-mask selection and missing-mask opaque fallback.
PNG selected color metadata retains first sRGB, last nonzero gAMA, IEND stop,
the original gamma tolerance and selected RGB transfer owner. No selected-frame
fallback or cursor hotspot ranking is introduced.

`EncodedImageDecoder` keeps original ordinary/embedded-PNG raster decoding behind
`EncodedRasterDecoder`. The default uses the already-present image crate public
API; it is not claimed to be StbImageSharp-bit-identical and does not replace the
independent exact native PNG parser. Unsupported dependency formats, malformed
admission, 16-bit/HDR narrowing and JPEG output remain backend differential gates.
No foreign implementation text or new dependency was introduced.

`ImageCodec` ports original signature recognition, five-family CPU admission,
unknown/unimplemented versus malformed/incomplete results, top-left/zero-animation
metadata, JPEG eighth-size requests, original target options, row/padding copies,
and one-shot incremental target leases (including original zero rows-decoded).
Original subset/scanline/animation operations remain unavailable. Incremental
destinations use an exclusive Rust borrow rather than an unretained raw pointer.

`image::raster` includes all 29 pixel types and four alpha types, exact byte/stride
metadata and color-space owner equality, original metadata-only GL mappings,
safe borrowed pixmaps, complete original ten-format color conversion, strided
typed views, clipped copy/erase/subsets, opacity and encoder staging. Owned Bitmap
includes legacy pixel/erase semantics, aligned alpha extraction, conversion-copy,
decode-to-size integer mapping and the original nearest/linear/cubic resize
producer. Resize retains stored nearest copies, pixel-center formulas, source
premultiplication, cubic tap/reduction order, independent mip/anisotropy policy
and floor-half-up output bytes. Pixmap float-color erase keeps ties-to-even.

Live bitmap subsets share actual locked storage. Frozen snapshots share an Arc
byte generation; the next mutable lease copies once. Image publication retains
the actual producer owner through its private pixel lease. Once-only release
callbacks survive subsets/snapshots/images; explicit last-owner reset propagates
errors, while abandoned Drop contains callback failures. No source callback,
queue submission, readback or finalizer GPU work was added. Safe slice extents
and alignment replace pointer arithmetic. Original unsafe legacy bitmap calls
on incompatible sub-four-byte layouts reject explicitly rather than reproducing
out-of-bounds access; these formats retain their real pixmap implementations.

Explicit CPU SIMD/scalar-reference policies expose the route. Component conversion
and interpolation use intrinsic lanes; packed palette/24/32-bit conversion and
AND masks use four pixel lanes with a bounded tail. Exact bounded integer channel
quotients use representable f64 lanes plus floor; scalar reference keeps original
integer division. RLE command state and cubic tap/reduction order remain scalar
dependencies. Rendering continues to use its GPU-first image sampling path; these
are explicit original CPU raster producer APIs.

JPEG/PNG/WebP option metadata and original alpha handling are ported. Original
ignored downsample/PNG options stay ignored and original WebP encoding stays
unavailable. `RasterEncoder` is the explicit StbImageWriteSharp boundary; the
default uses the existing image dependency and does not claim byte/quality parity.

Remaining separate production scope: full GPU-backed SKImage/SKSurface context
transfer, cache/readback and borrowed/adopted backend factories, source canvas
attachment/flush wrappers, and source-host pointer adapters. Renderer image/filter
owners already remain independent; this increment does not impersonate these
missing APIs. Existing DESIGN.md research/ownership boundaries apply. Source
runtime float/FMA, SIMD/scalar, callbacks, allocation/error and codec/backend
differential checks remain deferred. No compiler, tests, validation, browser,
benchmark or artifact generation ran; touched files were formatted only.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKEncodedImageDecoder.cs` | `d0d2baa0ff768bbf4e6434cb48653a8f4f5295544eb2dbe74e5b04af7f95d4d7` |
| `src/SkiaSharp/SKImage.cs` | `96827d04df213d63249cb3fb4005bcc4e811367ced4a16c27a0d9c21b66c2273` |
| `src/SkiaSharp/SKImage.Api.cs` | `42b67d84e9cb98679221ee4232f1c6d49405cc9627a42e8f0535a14498643cd1` |
| `src/SkiaSharp/SkiaSharp.cs` | `3b43aa657ad5584157b612fd16d2b56d96af352e310d69ff5a004d49e85d216f` |
| `src/SkiaSharp/SkiaExtensions.cs` | `d073d1c559cf042f7530350ab8eebef330bacf566817d0e4d1c185101d0260f7` |
| `src/SkiaSharp/SKEncoderOptions.cs` | `4c0ca16ba18da0779e9d1f97e94007845a936f9042a16e943116442c8d8a0241` |
| `src/SkiaSharp/SKCodecOptions.cs` | `928f2154c79cad7cf4cec3adae8952c0b2f534c9ea8f9b669c8c7d5d54393574` |
