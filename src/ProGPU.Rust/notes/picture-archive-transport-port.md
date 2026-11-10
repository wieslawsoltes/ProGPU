# Original picture archive transport

Owned Rust port of original `SKPicture.Serialization.cs`, with no foreign
serialization/rendering engine and no unsafe code.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPicture.Serialization.cs` | `c27d0e563e7bef1c874896d13bf2790d3972bee30fb6d1456992cd1e3e116e46` |
| `src/ProGPU.Scene/RenderCommand.cs` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |

`PictureArchive` owns its original version and independent LTRB cull rectangle;
`ArchivePicture` shares complete immutable point/double/Line3D/float arrays and
commands. All 39 RenderCommandType tags and every original serialized field
are typed, including inactive metadata, nullable arrays, fonts, nested pictures,
texture patch descriptors and vertex colors. Default command values are source
zero initialization, including zero matrices and Clear vertex-color blend.
GPU textures, source-input geometry overrides, buffers, custom payloads and cache
keys are not fabricated as archive data. Explicit capture retains the original
unsupported boundary.

The codec preserves original magic, versions 1 through 10, little-endian scalar
bits, four-byte counts, byte booleans and UTF-8 strings. Null (-1) remains separate
from empty arrays/strings; invalid UTF-8 uses replacement decoding. Archive bytes
are capped at 256 MiB, arrays and strings at 16,000,000 entries or bytes, commands
at 1,000,000 and original nested depth at 64. Declared array extents are checked
against remaining bytes before allocation. Output capacity is reserved fallibly
before appending; complete private candidates publish only after payload work.
Little-endian array IO uses safe POD bulk writes and unaligned reads; other
endian targets retain explicit scalar conversion. No float arithmetic or unsafe
pointer reinterpretation participates. Performance remains unmeasured.
Trailing input is rejected. Stream reading retains bounded 8192-byte chunks;
writes complete serialization before the caller stream sees any bytes.

Font reading constructs the original default face-zero managed owner from exact
serialized bytes. Writing copies actual source font data; face index, variations
and palette are not extra source wire fields. Transport does not rasterize fonts
or create a GPU. Original constructor/table errors propagate.

Version nine carries both atlas flags and admits IntegerPixel placement. Version
ten carries independent grayscale coverage transfer (legacy zero, explicit one
through thirteen). Only DrawGlyphRun + Grayscale admits an explicit transfer.
Older writes reject lossy new policies. Material/geometry version policies remain
in the [separate source note](picture-archive-material-geometry-port.md). Rendering
admission stays distinct from original archive read admission.

The command/path ownership graph retains immutable source owners without copying
complete arrays. Depth guards apply independently to nested pictures and raw path
operands. Derived geometry caches are rebuilt by replay conversion; serialized
fields do not pretend to capture GPU cache ownership.

`ArchivePicture` exposes checked signed side-buffer slices and O(1) command
borrows/storage identity, and replacement command arrays retain original side
buffers. Nested operation counting follows original DrawPicture replacement and
signed saturation. Byte diagnostics measure actual Rust owner/string/command
storage, not C# packed-record layout. `ArchiveRecorder` is an additive safe raw
command/side-buffer producer with bounded fallible appends and ownership-transfer
completion; it is not a replacement for Canvas paint lowering or original
DrawingContext event/provider policies. It deliberately makes no source lease
count inference to trigger GpuPictureRecorder's single-child flatten shortcut.

Mandatory implementation-first workflow: source formatting only. No compiler,
source-differential, tests, browser, benchmark, pixel or package validation ran.
Invalid UTF-8 replacement grouping, original finite/overflow and stream-fault
behavior, exact version bytes, ownership/error cases and full renderer/source/
package/pixel qualification remain open.

The shared native/browser archive gallery is authored in
`examples/support/archive_scene.rs`: twelve geometry/side-buffer/scope/mesh panels,
with optional positioned glyphs. It will serialize v10, deserialize complete source
owners, then invoke ordinary retained replay at the actual host DPI. No run or
roundtrip pass is claimed. Host query selection explicitly rebuilds the selected
scene; existing backdrop/visual/mesh/path-effect/cached-picture selectors share
that source setup correction.

Canvas diagnostics include retained archive payloads and traverse their nested
operations when requested. Excessive manually constructed archive depth saturates
the existing infallible Canvas diagnostic at i32::MAX; explicit archive diagnostics
return their bounded error. No missing payload is counted as zero.
