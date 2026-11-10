# Owned JPEG bitmap decode integration

Implementation only. No compiler/build/test/shader/browser/benchmark/fixture run,
no dependency resolution and no generated artifacts. Cargo.toml/Cargo.lock are
unchanged; TIFF is not enabled.

Original `TtfFont.TryDecodeBitmapGlyph` selects sbix before CBLC/CBDT, decodes the
selected bytes once through StbImageSharp 2.30.15, suppresses non-allocation decoder
failure, verifies the full straight-RGBA inventory and computes the existing
strike/origin/outline presentation. A failed selected sbix image does not retry
CBDT. C++ owns payload/strike queries and caller-supplied color resources, not an
automatic JPEG/TIFF codec. The original sbix TIFF tag admits encoded storage; it
is not evidence of a TIFF decoder. StbImageSharp's public format inventory lists
JPEG/PNG/BMP/TGA/PSD/GIF/HDR, not TIFF
([provider documentation](https://github.com/StbSharp/StbImageSharp)). The default
TIFF decode miss remains intentional; explicit caller-owned providers can supply
it independently. Missing TIFF default decoding is not an unported original
producer.

New `image::JpegDecoder` retains its actual immutable borrowed bytes and admitted
public dependency decoder. `JpegDecodeLimits`, `JpegDecodeRequirements` and
`JpegPixelLayout` expose checked dimensions, exact native decode bytes and exact
RGBA publication bytes. The managed signed-byte-array limit is preserved; immutable
Image additionally retains its existing 16384-axis bound. Caller output capacity
is admitted before pixel decode. Decoding touches private storage; only successful
complete decode begins infallible byte conversion. Trailing caller storage and all
caller output on errors remain untouched. Gray/RGB to RGBA uses independent SIMD
lanes with an explicit scalar reference and bounded tail. That policy describes
our conversion only; compressed JPEG execution remains the dependency's own policy.
There is no CPU rendering fallback, ICC transform, EXIF rotation, resize or
premultiplication. `Image::decode_jpeg` publishes ordinary owned straight-alpha
pixels only after complete admission.

The existing image 0.25.10 dependency is consumed through its public
[JpegDecoder](https://docs.rs/image/0.25.10/image/codecs/jpeg/struct.JpegDecoder.html)
and [Limits](https://docs.rs/image/0.25.10/image/struct.Limits.html) APIs. Its
JPEG coefficient/color conversion, malformed-input behavior, internal allocation
and target SIMD behavior remain independent of StbImageSharp qualification; no
foreign implementation text was copied. Own fallible allocations map to capacity,
while dependency-reported limits remain capacity failures. Internal allocator
behavior is still the dependency's contract.

`ImageCrateRasterDecoder` dispatches actual JPEG content to this boundary, retains
its original headerless-TGA fallback and explicit no-default-allocation-limit
policy, checks RGBA inventory before publication, and explicitly rejects TIFF even
if another dependency feature is later enabled. Original `Image::decode_png` and
all native PNG parsing/filter/CRC/palette/interlace behavior remain unchanged.
The native color adapter preserves its declared PNG/JPEG routes, reports
`NativeBitmapDecodeExecution::JpegCodec`, and retains the exact selected font and
bitmap source. Default unsupported TIFF becomes a selected-source decode miss;
the old public capability error value remains available for explicit callers.

`ManagedFont::decode_bitmap_glyph_with` adds explicit owned-provider injection;
the ordinary method shares the checked default backend. Returned
`DecodedBitmapGlyph::source` retains the selected font/table/demand-loaded payload.
Both managed and native paths keep original strike ordering, decoded-size versus
strike-metric distinctions, bearings, raster scale, source selection and
non-capacity failure handling. Native default cache keys remain actual source/
instance/target identities; explicit borrowed providers remain uncached globally.

JPEG provider pixels, malformed sources, native/managed/browser execution,
performance and full package/source qualification remain outstanding. TIFF
providers and any different platform codec are separately explicit capabilities.

Original SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Text/TtfFont.cs` | `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b` |
| `src/ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.Glyphs.cs` | `093976562d04f82a5f16b08731eed3e7587fef5a85cede17717caef410d5121d` |
| `src/ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.BitmapGlyphs.cs` | `992b504dbf48344076a716e751b851973c215d02353d0216ec0d2d4d822a9e40` |
| `src/ProGPU.Text/GlyphAtlas.cs` | `5fb66ddeba150535a48c760b18a584dd161af38c6fc3ed46accc8e35f3f64582` |
| `src/SkiaSharp/SKEncodedImageDecoder.cs` | `d0d2baa0ff768bbf4e6434cb48653a8f4f5295544eb2dbe74e5b04af7f95d4d7` |
| `src/ProGPU.Native/src/Text/progpu_native_sbix.cpp` | `2d76621a394ac89b2654d949055c0798b0ae3c63cfd399941c5399e6fedf0759` |
| `src/ProGPU.Native/src/Text/progpu_native_cbdt.cpp` | `1fe2ac388f9a696d0960a6b60ffeca781c7073c000200faac703cbddabd3ffb7` |
| `Directory.Packages.props` | `7033a7fd8688e3e46f35d0b14c2d17c636b0e0f0bc785d17c050b9626013c5e6` |
