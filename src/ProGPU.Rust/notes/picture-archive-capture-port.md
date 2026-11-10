# Original source picture capture

Source SHA-256:

- `src/SkiaSharp/SKPicture.Serialization.cs`: `c27d0e563e7bef1c874896d13bf2790d3972bee30fb6d1456992cd1e3e116e46`
- `src/SkiaSharp/SKCanvas.cs`: `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64`
- `src/SkiaSharp/SKPicture.cs`: `10f7fe2fd1682989e8baff48afe1c911c5b8f166b9e754772a210269e1d1c26c`
- `src/ProGPU.Scene/RenderCommand.cs`: `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8`

`CanvasPicture::to_archive`, `Scene::to_archive`, and
`RecordedPicture::{to_archive,serialize,deserialize}` connect immutable Rust
recordings to the original v1-v10 command transport. Capture builds a private
candidate and publishes only after complete source admission. Original paths,
analytic source records, pens, meshes, complete positioned glyph arrays, ordered
opacity/blend scopes and representable clips retain their actual source data.
Inverse fills and difference clips retain the original canvas-bounds subtraction.
Full glyph runs retain font ownership, rendering/hinting, integer-origin flags,
font transform and explicit grayscale contrast; selected-range metadata absent
from the original wire rejects instead of being dropped.

An identity raw-archive Canvas command returns the same complete archive owner,
including inactive fields and all four side buffers. Nested archives preserve
that owner under original DrawPicture and opacity commands. Receiving transforms
and opacity have wire representation; nondefault text preparation, fallback,
DPI/camera/clip/raster environments reject because they have no archive fields.
Raw archive transport is independent of subsequent execution admission.

Capture rejects text-path raster policy, sampled/ordinary GPU images, filters,
prepared/hinted text, compiled paths without source history, physical inherited
clips, open scopes and unsupported resources. Separate analytic fill/pen records
reject when recombining would duplicate original paint. No pixels, shaping or
lost source history are reconstructed. Decoded empty cull metadata survives via
`RecordedPicture::from_archive`; ordinary recorder empty normalization is intact.
This is an explicit supported source capture boundary, not a general inverse of
arbitrary compiled Scene or Canvas content.

No compiler, tests, fixtures, browser runs or benchmarks were executed. Only
touched source formatting was performed; no build or result artifacts were made.
