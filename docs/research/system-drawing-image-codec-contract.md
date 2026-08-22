# System.Drawing managed image-codec contract

## Scope

This slice restores the source-compatible managed descriptor and save surface used by canonical WinForms and designer/resource code:

- `ImageFormat` official identities and value semantics;
- `ImageCodecInfo`, `ImageCodecFlags`, and defensive encoder/decoder discovery;
- `Encoder`, `EncoderValue`, `EncoderParameterValueType`, `EncoderParameter`, and `EncoderParameters`;
- `Image.Save` codec/parameter overloads and `GetEncoderParameterList`; and
- functional managed PNG, BMP, and JPEG encoding, including integral JPEG quality selection.

The authoritative API shapes and managed ownership behavior come from the upstream files under `src/System.Drawing.Common/src/System/Drawing/Imaging` and `src/System.Drawing.Common/src/System/Drawing/Image.cs` in the LibreWinForms source tree. ProGPU changes only the platform seam: GDI+ codec discovery and native encoder calls become an explicit managed codec registry and typed CPU encoders.

## Capability registry

`GetImageDecoders` reports only formats the current CPU decoder accepts: BMP, JPEG, GIF, PNG, and ICO. `GetImageEncoders` reports only working encoders: BMP, JPEG, and PNG. Each call returns new descriptor objects and deep-cloned signature arrays, because the public descriptor properties are mutable. Mutating one caller's result therefore cannot corrupt later discovery or another caller.

The registry uses the official image-format GUIDs and stable GDI+ codec CLSIDs so existing encoder-selection code continues to match by identity. A caller-created descriptor must match both a registered CLSID and its format ID before saving; arbitrary descriptors do not silently select a different codec.

## Ownership and behavior

`EncoderParameter` retains the upstream sequential managed layout and owns a copied unmanaged value buffer. All array and pointer constructors copy caller data. `Dispose` releases that buffer. `EncoderParameters.Dispose` releases its contained parameters.

PNG and BMP reuse the existing CPU encoders. JPEG reads one canonical straight-RGBA snapshot and passes it directly to the already-used managed STB encoder. This path does not create a GPU device, does not stage through `SKBitmap`, and does not retain caller buffers. `Encoder.Quality` accepts integral values and is clamped to the public 0–100 domain before the encoder's effective 1–100 range is applied.

Multi-frame `SaveAdd`, TIFF/GIF encoding, HEIF/WebP encoding, metadata emission, and non-quality JPEG encoder parameters are explicit remaining work. The public multi-frame methods throw `NotSupportedException`; unsupported codecs are not advertised as encoders.

## Native and GPU applicability

This slice changes no render command, shader ABI, texture format, native C++ structure, or window-system seam. A GPU-backed bitmap crosses the existing single explicit readback boundary before encoding; a CPU-backed bitmap remains CPU-only. Codec quality and payload behavior are guarded by managed round-trip/signature tests, allocation tests, ApiCompat, and the isolated BenchmarkDotNet workload.
