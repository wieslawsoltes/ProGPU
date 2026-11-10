# Original color-space implementation

Production port: `image/color_space.rs` and `color_space/{xyz,primaries,icc}.rs`, exported by `image.rs`. No compiler, tests, shader/browser validation or benchmarks were run; formatting only, no generated build artifacts.

Original SHA-256 sources:

| Source under `src/SkiaSharp/` | SHA-256 |
| --- | --- |
| `SKColorSpace.cs` | `39009e68e44861f743eb2e842cae93473e45e3ef5e1e52c3acf6aa2ddf8e0020` |
| `SKColorSpaceIccProfile.cs` | `f3f1331f1ccfd725e0a466fa64d98b5cc8253f12a64f5ebd299f2fdaa03ff5e4` |
| `SKColorSpacePrimaries.cs` | `022ec961efba5ab27e8dda897b1072be98e64162069540ba3506501d7b29c615` |
| `SKColorSpaceXyz.cs` | `c5850589b2d4e4bc09efef60fad1d2889a933f4141674b9ca1dd354034120ade` |
| `SKColorSpaceTransferFn.cs` | `23222c56765b316bbe3137a04f3d0b39b3f11bd9509a202d0f67111d96c63269` |

Includes canonical shared sRGB/linear owners, complete CICP factories, original RGB tolerance/order, all named XYZ matrices, double inverse, independent-lane float concatenation, complete primary solve/Bradford adaptation and exact named exceptions. Primary conversion retains original successful nonfinite boundary-white results; RGB construction separately rejects nonfinite matrices.

ICC retains complete input bytes while parsing only the declared extent. Original first matching tags, independent matrix/transfer flags, non-RGB opaque profiles, XYZ tags, zero/gamma/sampled curves and parametric forms 0–4 remain distinct. Sampled curves recognize only the original strict sRGB error bound, using original standard power evaluation. Unsupported metadata is never replaced with sRGB. Metadata-only `to_profile()` does not fabricate serialized ICC bytes.

`ColorSpaceTransferFunction` and its kind are public aliases of the existing original `image_filter::TransferFunction`; its selected fast arithmetic is not duplicated. `ColorSpace::to_filter_color_space()` shares a per-owner selected-transfer/table generation. Original filter conversion bypass depends on exact sRGB transfer, independently of `ColorSpace::is_srgb()` canonical-owner identity; no new gamut conversion is inferred. Raw unknown nonzero CICP transfer values preserve the source's EMPTY/numerical-classifier behavior.

Original source numerical/profile/pixel qualification remains deferred with the rest of the port.
