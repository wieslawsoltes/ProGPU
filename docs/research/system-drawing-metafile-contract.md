# System.Drawing metafile contract

## Objective and compatibility boundary

This subsystem restores the .NET 10 `System.Drawing.Imaging` metafile public
model without routing the portable product path through GDI+, HDCs, opaque
native handles, reflection, or WinForms-shaped compatibility objects. The
pinned `System.Drawing.Common` 10.0.11 reference assembly defines the public
type and member shape. Microsoft Open Specifications define the byte-level
[WMF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/ba5458c6-e885-41e6-b5d7-d54ef9e1065f),
[EMF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/e0137630-f3ad-492c-bde9-e68866e255ba), and
[EMF+](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emfplus/8b9363ba-cf5b-4d35-bced-0620e3b5b5ef)
contracts. Parser, validation, record storage, lowering, and rendering code is
original ProGPU code.

File and stream construction is the portable path. It snapshots the source,
parses it transactionally into immutable typed records, and exposes metadata
without retaining caller-owned streams. Handle import/export and HDC-backed
recording remain explicit Windows adapter operations. Those members preserve
their official public shape but must throw a descriptive
`PlatformNotSupportedException` until a typed Windows adapter is supplied; a
raw pointer is never treated as a portable image or drawing context.

## Bounded parser model

All formats are little-endian. A reader first identifies one of three roots:

- placeable WMF starts with key `0x9AC6CDD7`, followed by the standard
  18-byte `META_HEADER`;
- standard WMF starts directly with `META_HEADER`; and
- EMF starts with an `EMR_HEADER` whose type is one, size is aligned to four
  bytes, and signature is `0x464D4520`.

Placeable WMF parsing validates the XOR checksum, nonzero units-per-inch,
ordered bounds, the declared word count, maximum-record declaration, record
word lengths, and terminal `META_EOF`. Standard WMF has no device-independent
frame; its initial public bounds remain empty until playback derives a window
and viewport. EMF parsing validates the declared byte and record counts,
four-byte record alignment, every record's in-buffer extent, and terminal
`EMR_EOF`. The header's device-pixel and physical-millimeter sizes derive DPI
only when both denominators are positive.

An `EMR_GDICOMMENT` immediately following the EMF header may contain an EMF+
stream. Its identifier, 12-byte record headers, aligned sizes, data sizes,
header-first ordering, and end marker are validated independently. The EMF+
header flag distinguishes `EmfPlusDual` from `EmfPlusOnly`; its logical DPI and
graphics version populate `MetafileHeader`. The outer EMF record table and
inner EMF+ record table remain typed and distinct even though public
enumeration presents their official `EmfPlusRecordType` identities.

The parser fails closed before publishing a `Metafile`. Initial limits are
256 MiB per source, 1,000,000 outer records, 1,000,000 nested EMF+ records,
16 MiB per record, 65,535 WMF object slots, and checked arithmetic for every
offset or unit conversion. Limits are implementation safety ceilings rather
than permission to allocate their maxima eagerly. Records retain offsets and
lengths into one owned source buffer; enumeration creates no per-record byte
array unless a callback requests the public unmanaged view.

## Public records and playback

`EmfPlusRecordType`, `EmfType`, `MetafileFrameUnit`, `MetafileType`,
`MetaHeader`, `MetafileHeader`, `PlayRecordCallback`, and
`Graphics.EnumerateMetafileProc` preserve their official identities. Header
objects are defensive managed snapshots. `GetMetafileHeader` for file, stream,
and instance inputs uses the same parser as construction, so validation cannot
diverge between inspection and playback.

Enumeration applies the destination point, rectangle, or parallelogram as one
typed world-to-device mapping and exposes records in source order. Callback
data is valid only for the duration of the callback. A callback returning
false stops cleanly. `PlayRecord` accepts only a record currently supplied by
enumeration and lowers it through the same typed playback state machine; it
does not accept arbitrary native addresses.

The first rendering tranche covers state and vector primitives needed by
LibreWinForms resources: save/restore, map/window/viewport transforms, world
transform, clip rectangles, object creation/selection/deletion, color and fill
state, move/line, rectangles, ellipses, polygons/polylines, paths, text, and
embedded DIB images. EMF+ adds header/end, object tables, transforms, clips,
clear, lines, rectangles, ellipses, paths, images, and strings. Each supported
record lowers to existing typed `Graphics`, `GraphicsPath`, brush, pen, font,
image, and clip operations. Unsupported records fail with the record type and
offset before the destination command list is committed. They are never
silently skipped during rendering.

Metafile recording is a later typed encoder over the same immutable record
model. Portable recording will target a stream without an HDC; official HDC
constructors remain Windows-adapter entry points. `AddMetafileComment` becomes
a typed encoder operation only while a graphics instance is actively recording.

## Quality and performance gates

The initial parser gate uses hand-built minimal placeable-WMF, standard-WMF,
EMF, EMF+ only, and EMF+ dual fixtures. Tests cover exact public enum values,
header properties, source ownership, cloning, file/stream equivalence,
non-seekable streams, checksum and signature failures, truncation, integer
overflow, record alignment, count mismatches, missing EOF, nested EMF+ bounds,
and explicit handle seams. Mutation fuzzing must reject malformed input without
access violations, hangs, partial objects, or unbounded allocation.

Rendering gates compare representative primitives and state transitions in the
managed bitmap compositor and headless GPU renderer. Windows differential
fixtures compare record enumeration, headers, bounds, and pixels against
official `System.Drawing.Common`. Native compiler snapshots prove that
supported records lower to ordinary typed ProGPU scene commands and that no
metafile-specific opaque payload crosses the renderer boundary.

`MetafileBenchmarks.ParseAndEnumerate4096RecordFixture` measures a warmed
bounded record walk without payload copies. The 2026-08-27 ARM64/.NET 10.0.11
ShortRun measured a 47.510 microsecond median (48.029 microsecond mean, 2.068
microsecond standard deviation) with 224.56 KB allocated for the owned 32 KB
source and 4,098 typed records. One launch, three warmups, and three measured
iterations make this coarse subsystem evidence. A later second benchmark will
measure playback into a retained `DrawingContext`. CI stores BenchmarkDotNet
JSON and focused tests enforce allocation ceilings after warmup. Parser
complexity must remain linear in source bytes plus record count; no record can
trigger an unbounded scan of the complete source.

## Delivery checkpoints

1. Restore the eight missing public identities and a bounded header/record
   parser with functional file/stream construction, cloning, and header queries.
2. Add allocation-free record enumeration and `PlayRecord` callback lifetime
   rules for all destination overloads.
3. Implement state/object tables and the initial WMF/EMF/EMF+ vector and image
   playback families over typed ProGPU drawing commands.
4. Add portable stream recording and comments, then typed Windows handle/HDC
   adapters where the host explicitly provides them.
5. Remove each ApiCompat suppression only with matching behavior, malformed
   input, pixel, native-boundary, and performance evidence.

API presence alone is not subsystem completion. Until checkpoints two and
three land, the quality report must describe header decoding as partial
metafile support and must not claim portable playback parity.

Checkpoint one is implemented at this revision. It removes all eight
missing-type diagnostics and 44 `Metafile` missing-member diagnostics, reducing
measured ApiCompat debt from 8 missing types, 98 missing members, 15 other
diagnostics, and 121 total to 0 missing types, 54 missing members, 15 other
diagnostics, and 69 total. The remaining 36 enumeration overloads and
`Graphics.AddMetafileComment` stay suppressed until checkpoints two and four;
no playback claim is made by the header-parser checkpoint.
