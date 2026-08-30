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
and viewport. EMF parsing validates the declared byte count, four-byte record
alignment, every record's in-buffer extent, and terminal `EMR_EOF`. The record
count accepts both specification-shaped producers that include `EMR_HEADER` and
legacy Windows-compatible producers that count only the records following the
header; no other count mismatch is accepted. The header's device-pixel and
physical-millimeter sizes derive DPI only when both denominators are positive.

An `EMR_GDICOMMENT` immediately following the EMF header may contain an EMF+
stream. Its identifier, 12-byte record headers, aligned sizes, data sizes,
header-first ordering, and end marker are validated independently. The EMF+
header flag distinguishes `EmfPlusDual` from `EmfPlusOnly`; its logical DPI and
graphics version populate `MetafileHeader`. The outer EMF transport and inner
EMF+ stream are validated independently. The enumeration table replaces the
`EMR_GDICOMMENT` transport envelope with its decoded EMF+ records at that
envelope's source position, while preserving surrounding outer EMF order and
official `EmfPlusRecordType` identities.

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

Enumeration exposes the owned typed records in source order for all 36 official
destination overloads. It pins the one owned source buffer for the complete
walk; each nonempty callback pointer addresses the record payload inside that
buffer and is valid only for the callback duration. No payload is copied per
record, and a callback returning `false` stops cleanly. The public
`callbackData` pointer remains ABI-compatible; matching the official managed
adapter, the managed callback's `PlayRecordCallback` argument is null.

Direct `Graphics.DrawImage` playback now has bounded EMF and WMF vector tranches.
Rectangle, point, source-rectangle, and three-point affine overloads compose the
metafile source bounds, caller destination mapping, and host graphics transform.
Four-point projective playback and `ImageAttributes` fail explicitly until the
typed vector player can preserve those semantics. Enumeration remains a
separate callback walk; its destination arguments do not trigger rendering, and
`Metafile.PlayRecord` remains unavailable until it can bind to a typed active
enumeration session without accepting arbitrary native addresses.

The implemented EMF record set is deliberately narrow: `EMR_SAVEDC`, relative
negative `EMR_RESTOREDC`, `MM_TEXT` and `MM_ANISOTROPIC` window/viewport state,
world-transform set/modify, polygon fill mode, move/line, rectangle, ellipse,
polygon/polyline/poly-polygon/poly-polyline, intersect-clip rectangles,
transparent/opaque background state, `R2_COPYPEN`, solid or null cosmetic pen
creation, solid or null brush creation, stock/dynamic selection, and safe
deletion. Saved DC state includes the retained clip and restores it through the
typed `GraphicsState` path. EMF/EMF+ structural and comment records are
nonvisual. The byte layouts and playback state follow the
official [EMR_RECTANGLE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/3c471238-0a02-4992-90a2-bfd2afd98f2a),
[EMR_CREATEBRUSHINDIRECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/b9a8ef5d-0089-4e42-b317-e6ebc0ff098f),
[EMR_CREATEPEN](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/2374647f-df67-48e3-86aa-384715c28e71),
[EMR_SELECTOBJECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/145b063d-5f96-41fe-b7ae-1e615b2bc2bf),
[EMR_SETMAPMODE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/aa4ad35d-fa42-4a4f-959a-8b41304e1b05),
[EMR_SETWORLDTRANSFORM](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/985724c0-4db1-48f0-b346-67288b3288cb), and
[EMR_POLYGON](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/eb916781-58b6-4e92-b606-68071aa65733)
contracts. Each supported record lowers to existing typed `Graphics`, brush,
and pen operations. Playback records into a temporary `DrawingContext`; an
unsupported or malformed record reports its type and byte offset and prevents
the entire temporary command stream from being appended.

The initial WMF family follows the official 16-bit record layouts and keeps its
state/object path separate from EMF. It implements background mode/color,
`R2_COPYPEN`, `META_SETRELABS` no-op semantics, polygon fill mode, text-alignment
state, window origin/extent, move, lowest-free object-table allocation and slot
reuse, selection/deletion, solid/null pens and brushes, polygons, polylines,
counterclockwise elliptical arcs, and filled/stroked ellipses. The record
inventory used by the canonical LibreWinForms `telescope_01.wmf` asset remains
fully covered; ellipse playback is an additional typed family. The
implementation is based on the official
[WMF object record rules](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/aeab62b8-03ab-48c0-8176-09c392f3c9da),
[META_POLYGON](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/0982bbfc-feb7-4f06-a8fb-ad03b465ffea),
[META_ARC](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/742097b4-5879-4c36-b57e-77e7cc152253), and
[state record](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/54e4a2e0-5ca9-4c69-b6a8-dc8f938c68ae)
contracts. Paths, clipping records, text output, DIB images, richer GDI objects,
other WMF drawing families, and nonstructural EMF+ drawing records remain
explicit later tranches.

Portable comment recording is a typed encoder over the same immutable record
model. `ProGPU.SystemDrawing.PortableMetafile.Create` accepts a caller-owned,
writable stream and integer pixel bounds without an HDC. The resulting
`Metafile` supplies one exclusive `Graphics.FromImage` session.
`Graphics.AddMetafileComment` copies each caller buffer synchronously and emits
an aligned EMF+ `Comment` record. Disposing the graphics instance builds an
EMF+ header, the ordered comments, and an end record inside one bounded
`EMR_GDICOMMENT`, adds the outer EMF header/end records, validates the complete
owned bytes with the normal parser, writes and flushes the target, and publishes
the immutable document. The stream remains caller-owned.

The comment envelope is capped at the parser's 16 MiB per-record ceiling and
all size, alignment, coordinate, and frame-unit arithmetic is checked. Header
queries and cloning reject incomplete recording; a second recorder, a read-only
target, invalid bounds, a disposed owner, or comments outside the active
session fail explicitly. The initial portable encoder accepts comment records
only. If ordinary retained drawing commands were recorded, finalization aborts
before writing anything rather than publishing a metafile that silently lost
content. Typed drawing-record encoding and official HDC constructors remain
later work; HDC constructors stay Windows-adapter entry points.

## Quality and performance gates

The initial parser gate uses hand-built minimal placeable-WMF, standard-WMF,
EMF, EMF+ only, and EMF+ dual fixtures. Tests cover exact public enum values,
header properties, source ownership, cloning, file/stream equivalence,
non-seekable streams, checksum and signature failures, truncation, integer
overflow, record alignment, count mismatches, missing EOF, nested EMF+ bounds,
and explicit handle seams. Mutation fuzzing must reject malformed input without
access violations, hangs, partial objects, or unbounded allocation.

Rendering gates compare representative primitives and state transitions in the
managed bitmap compositor. The first gate covers retained pixels, destination
scaling and translation, object selection, map/world/save/restore composition,
saved clip restoration, multi-polygon count/point validation,
explicit image-attribute/projective rejection, and transactional rollback after
a supported draw but before an unsupported record. WMF gates cover 16-bit
parameter ordering, lowest-free slot reuse, state, pen/brush selection,
polygon/polyline/arc/ellipse pixels, and transactional rollback. The real
LibreWinForms `telescope_01.wmf` fixture renders end to end into a 200-by-267
bitmap with 6,048 opaque pixels. The focused metafile suite is 30/30 and both
complete Debug and Release drawing suites are 391/391. Windows differential and
headless GPU fixtures remain follow-up evidence. Supported records lower to
ordinary typed ProGPU scene commands; no metafile-specific opaque payload
crosses the renderer boundary.

`MetafileBenchmarks.ParseAndEnumerate4096RecordFixture` measures owned parsing
and record-table creation. The 2026-08-27 ARM64/.NET 10.0.11
ShortRun measured a 47.510 microsecond median (48.029 microsecond mean, 2.068
microsecond standard deviation) with 224.56 KB allocated for the owned 32 KB
source and 4,098 typed records. `Enumerate4098RecordsWithoutPayloadCopies`
isolates the warmed callback walk. On the same host its ShortRun measured a
1.593 microsecond median (1.495 microsecond mean, 0.177 microsecond standard
deviation) with zero managed allocation. One launch, three warmups, and three
measured iterations make these coarse subsystem evidence.
`Playback256RectanglesToRetainedCommands` measures the bounded parser-table
walk, transform/object state, transactional temporary recording, append, and
cleanup for 256 filled rectangles. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun
measured a 154.013 microsecond median (163.161 microsecond mean, 32.602
microsecond standard deviation) and 305.26 KB managed allocation. This is a
first coarse retained-command baseline, not an allocation target; the next
optimization tranche should reduce temporary command/resource copying without
weakening rollback. CI stores BenchmarkDotNet JSON and
focused tests enforce a maximum 4,096 bytes across sixteen warmed 4,098-record
walks. Parser complexity must remain linear in source bytes plus record count;
no record can trigger an unbounded scan of the complete source.

`Playback256WmfPolygonsToRetainedCommands` measures WMF record decoding,
lowest-free object-table setup, 256 four-point polygon lowers, transactional
append, and cleanup. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a
373.387 microsecond median (345.537 microsecond mean, 87.265 microsecond
standard deviation) and 477.6 KB managed allocation. The three-iteration result
is a deliberately coarse first baseline; like the EMF result, it identifies
temporary point arrays and retained-command ownership as later optimization
work rather than claiming allocation-free playback.

`Playback256WmfEllipsesToRetainedCommands` guards the WMF 16-bit bottom/right/top/left parameter order, selected brush and pen lowering, transactional append, and retained curve commands. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 1.060 millisecond median (1.109 millisecond mean, 0.115 millisecond standard deviation) and 622.14 KB managed allocation for 256 filled and stroked ellipses. One launch and three measured iterations make this a coarse first baseline. Focused gates verify selected fill/outline pixels, reject unordered bounds without publication, and prove that a following unsupported text record does not publish a partially lowered ellipse stream. The complete drawing suite passes 394/394, and ApiCompat remains at 0 missing types, 0 missing members, and 13 reviewed platform-annotation differences.

`RecordAndFinalize256PortableComments` measures the complete portable writer:
256 owned 64-byte comment copies, EMF+/EMF assembly, validation, and publication
to a pre-sized memory stream. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun
measured an 11.346 microsecond median (11.348 microsecond mean, 0.406
microsecond standard deviation) with 150.72 KB allocated for the complete 19 KB
document, output stream, and immutable parser tables. The encoder is linear in
comment bytes plus record count. Focused tests additionally reparse emitted
bytes, compare exact copied payloads after caller mutation, exercise a
non-seekable target and zero-length comment, and prove that unsupported drawing
aborts without partial output.

## Delivery checkpoints

1. Restore the eight missing public identities and a bounded header/record
   parser with functional file/stream construction, cloning, and header queries.
2. Add allocation-free record enumeration and callback lifetime rules for all
   destination overloads, then bind `PlayRecord` to the typed playback session.
3. Implement state/object tables and the initial WMF/EMF/EMF+ vector and image
   playback families over typed ProGPU drawing commands.
4. Add portable stream recording and comments, then typed Windows handle/HDC
   adapters where the host explicitly provides them.
5. Remove each ApiCompat suppression only with matching behavior, malformed
   input, pixel, native-boundary, and performance evidence.

API presence alone is not subsystem completion. Until every required record
family in checkpoint three lands, the quality report must describe the current
vector player as partial metafile support and must not claim complete portable
rendering parity.

Checkpoints one, the enumeration half of checkpoint two, the bounded direct EMF
and WMF vector slices of checkpoint three, and the portable comment portion of checkpoint
four are implemented at this revision. Checkpoint
one removes all eight
missing-type diagnostics and 44 `Metafile` missing-member diagnostics, reducing
measured ApiCompat debt from 8 missing types, 98 missing members, 15 other
diagnostics, and 121 total to 0 missing types, 54 missing members, 15 other
diagnostics, and 69 total. Typed enumeration removes the remaining 36 overload
suppressions, leaving 0 missing types, 18 missing members, 15 other diagnostics,
and 33 total at that checkpoint. Later compatibility slices reduce other debt;
portable comment recording removes the final `Graphics.AddMetafileComment`
suppression and leaves 0 missing types, 0 missing members, 13 reviewed shape
diagnostics, and 13 total. Direct playback changes behavior rather than public
API shape, so those counts remain unchanged. No claim is made yet for complete
WMF/EMF/EMF+ playback or drawing-record encoding.
