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
identity. The existing portable font-face prefix is unchanged; no call to
`GetRecommendedRenderingMode` or another undeclared vtable tail is made.

When the original face supports the genuine optional `IDWriteFontFace5` IID,
the capture also owns its immutable axis identity. The private typed interface
declares every inherited slot before calling the original axis methods; it is
never obtained by casting an older interface to an assumed vtable tail.
`axis_values_available` distinguishes a successful Face5 query (including an
empty static list) from legacy `E_NOINTERFACE`. Other query/getter failures remain
failures. `has_variations` records the original `HasVariations` result, separately
from the ordered `axis_values` list. A static face may expose nonempty standard
design attributes, so a false result neither rejects that list nor invents a
default variable-font instance.

Each captured axis retains the original tag case/order and exact finite float
user-coordinate bits, including signed zero. Tags use the original
`DWRITE_FONT_AXIS_TAG` numeric byte order (`wght` is `0x74686777`), not the native
big-endian OpenType integer representation. The original API's canonical,
clamped values are retained, not reconstructed from the caller's font-creation
array. Capture performs no fvar normalization or default-coordinate substitution.
The face and Face5 interface must share the same canonical `IUnknown` identity;
both are held while files are read. Axis values/count/variation identity are read
before and after file callbacks and compared bit-for-bit before publication.
Same-face recursive capture is rejected before another external callback;
canonical identity also rejects recursion through an alternate interface.

File capture is explicit, generation-level preparation. It is not inserted into
ordinary `DrawGlyphRun`, recording, replay or an address-keyed global cache. One
successful owned font can supply repeated request snapshots without another font
read. Complexity is expected O(B + F + A) time/storage for B original bytes,
F files and A captured axis values (duplicate tags use a bounded hash set), with
16 files, 64 KiB per opaque loader key and **64 MiB total** bytes as explicit
admission bounds. Opaque keys are copied before loader callbacks and used only
with that exact source loader. The owner copies each fragment before its release;
successful reads release once even when the fragment context is null. Failed
reads do not grant a fragment lease. Every partially acquired COM reference is
scoped. Original source HRESULTs survive unchanged, and failure never replaces a
previous successful output.

Axis capture has a separate **65,535 + 5 entry** storage bound: the existing
native fvar domain is uint16, while Face5 can additionally expose the five
standard static design attributes. This bound does not assert that these are
fvar axes, synthesize missing values or require the two counts to match. The
64 MiB original-file bound is unchanged. Unknown/custom printable tags remain
ordered source identities; duplicate or malformed tags and nonfinite values
fail atomically. No per-glyph font callback is introduced.

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
- [IDWriteFontFace5::GetFontAxisValues](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nf-dwrite_3-idwritefontface5-getfontaxisvalues)
  and [HasVariations](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/nf-dwrite_3-idwritefontface5-hasvariations):
  actual canonical axis values and the independent variable-face identity.
- [DWRITE_FONT_AXIS_TAG](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_3/ne-dwrite_3-dwrite_font_axis_tag)
  and [OpenType fvar](https://learn.microsoft.com/en-us/typography/opentype/spec/fvar):
  the five standard attributes, custom tag identity and the native axis-count
  domain. Neither specifies that a static Face5 list must be empty.
- The [published Windows SDK declarations](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/dwrite.h)
  and [Face1–Face5 declarations](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/dwrite_3.h)
  define the genuine interface slots/IIDs. No foreign implementation, renderer
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

Additional authored axis controls cover exact order/tag/coordinate bits,
immutable output, static nonempty and empty Face5 lists, legacy absence, a
128-axis positive inventory, query/getter failures, malformed/nonfinite/duplicate
values, over-budget counts, canonical identity mismatch, callback mutation and
recursive capture. Rejections preserve the previous output and release acquired
references/fragments. Windows additionally compares the captured list directly
with the same genuine original Face5 instance's SDK axis readback; this is not
variable-instance pixel qualification. Prepared variable contour/metric
consumption is a separate implementation, and this capture checkpoint does not
widen its existing admission gates.

The existing 54 original RGB observations also create the request from the actual
original target/run/parameter objects and compare its captured values, while
their independent file receipt and complete direct/command-list pixel assertions
remain intact. This does not use ProGPU to manufacture expected Windows pixels.
All fixtures, compile checks and runtime/platform/GPU/package qualification are
**deferred to the final integrated stack tip**. No execution or parity result is
claimed by this checkpoint.
