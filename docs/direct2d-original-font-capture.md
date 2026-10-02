# Owned original Direct2D font requests

This private source preparation boundary owns original font files and a source
glyph request. It does **not** admit modern DirectWrite raster modes, select the
historical RGB box-filter model, register a public provider, or enable legacy
`INITIALIZE_FOR_CLEARTYPE`. Those remain part of the complete rendering task.

`capture_original_font` retains the actual typed font face and records its exact
type, collection index, simulations, symbol flag and glyph count. It obtains the
ordered original file objects through `GetFiles`, verifies `IDWriteFontFile`
with its declared IID, then calls the genuine loader/stream interfaces. It owns
every byte of every complete original file. It does not concatenate files into a
new font, reconstruct tables, map characters, or infer face/variation identity
from filenames. Original face ownership remains distinct from file/index
identity: no variable-axis equivalence is claimed without an explicit later
typed axis contract. The existing portable font-face prefix is unchanged; no
call to `GetRecommendedRenderingMode` or another undeclared vtable tail is made.

File capture is explicit, generation-level preparation. It is not inserted into
ordinary `DrawGlyphRun`, recording, replay or an address-keyed global cache. One
successful owned font can supply repeated request snapshots without another font
read. Complexity is O(B + F) time/storage for B original bytes and F files, with
16 files, 64 KiB per opaque loader key and **64 MiB total** bytes as explicit
admission bounds. Opaque keys are copied before loader callbacks and used only
with that exact source loader. The owner copies each fragment before its release;
successful reads release once even when the fragment context is null. Failed
reads do not grant a fragment lease. Every partially acquired COM reference is
scoped. Original source HRESULTs survive unchanged, and failure never replaces a
previous successful output.

`capture_original_glyph_request` borrows that immutable owned font, verifies the
exact retained face used by the run, and owns the supplied glyph IDs, signed
advances, offsets, em, sideways value and full bidi level. Absent advances and
offsets stay absent. The actual caller rendering parameters remain typed values,
including their supplied/absent distinction, geometry, gamma, contrast,
ClearType level and rendering mode. There is no nominal-metric substitution,
second shaping pass, interpreter selection, OS-default query or pixel work.
Request creation costs O(G) time/storage for G glyphs under the unchanged
one-million-glyph bound.

The target snapshot retains its actual COM identity, source-owned generation,
transform, baseline, physical extent, independent DPI, format/alpha, text AA,
unit mode, primitive blend and tags. Source arrays and target values are copied
before any target/parameter AddRef or getter can reenter the owner. The caller
must hold its original live argument references and recorder generation guard
through the complete factory call and publication. A request is not evidence that
the recorder remained current. The caller still owns the captured clip/layer
command scopes; this metadata cannot discard them or prove an opaque target.
No native target, renderer, font context or device is constructed here.

## Contracts and clean-room provenance

The implementation is original ProGPU code using its existing COM owners and
`glyph_run_capture`. The following primary public contracts supply only ABI,
ownership and source identity requirements:

- [IDWriteFontFace::GetFiles](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getfiles):
  ordered file inventory and AddRef ownership.
- [IDWriteFontFile::GetReferenceKey](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontfile-getreferencekey)
  and [GetLoader](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontfile-getloader):
  the borrowed key is meaningful to its owning loader, not a global path.
- [IDWriteFontFileStream](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwritefontfilestream):
  whole-file size, borrowed fragments and paired release.
- The [published Windows SDK declarations](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/dwrite.h)
  define the genuine interface prefixes/IIDs. No foreign implementation, renderer
  helper, coefficient table or code structure is copied or adapted.

The cross-engine architecture research and separate native/managed RGB lifecycle
scope remain in [Owned RGB glyph coverage](native-rgb-glyph-coverage.md). This
factory is a private native DirectWrite/Direct2D source boundary, not a new managed
wire record or renderer algorithm. Managed callers cannot exchange these COM/STL
owners across the stable C ABI.

## Authored, not executed

The existing compatibility test process includes a new source fixture covering
multi-file byte order/immutability, exact metadata, every acquisition failure,
partial reference cleanup, changed inventories, total/key budgets, null fragment
contexts, source callbacks, absent optional values, run/target preflight and
original face identity. Windows additionally captures a genuine system font and
compares every byte against an independent original SDK stream read.

The existing 54 original RGB observations also create the request from the actual
original target/run/parameter objects and compare its captured values, while
their independent file receipt and complete direct/command-list pixel assertions
remain intact. This does not use ProGPU to manufacture expected Windows pixels.
All fixtures, compile checks and runtime/platform/GPU/package qualification are
**deferred to the final integrated stack tip**. No execution or parity result is
claimed by this checkpoint.
