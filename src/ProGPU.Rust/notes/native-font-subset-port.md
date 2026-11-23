# Original native SFNT subset producers

`font::native_subset` ports both original native cold preparation paths. Raw
functions accept the actual container bytes and selected SFNT directory offset;
`SfntFace` convenience methods retain that original offset. A prepared immutable
`NativeSfntSubset` exposes exact requirements, bytes and compact map, and copies
only after both output capacities pass. Unused tails remain unchanged. Owned
results avoid repeating a cold build between requirements and publication, while
separate source-shaped requirements/into functions remain available.

The glyph-ID-preserving route retains zero and the original breadth-first
transitive composite closure, ignores requested out-of-range IDs, leaves omitted
IDs empty, emits long loca and removes DSIG. Missing required TrueType tables or
a zero glyph count follow the original whole-font copy route. It preserves the
original distinction between dependency traversal and output bounds admission:
equal offset pairs skip traversal, short glyph records do not become composites,
and only selected output ranges are copied.

The compact route orders included IDs by ascending original ID, retains complete
component bytes apart from exact ID substitutions, and validates the original
last-component instruction extent. It rewrites maxp and, when both source tables
exist, hhea/hmtx using the original last-advance and missing-bearing-zero policy.
All twenty-one original discarded table tags remain discarded. No cmap, GSUB,
GPOS, color metadata or other remapping policy is fabricated. Unknown tables retain
the original copy behavior; this does not independently qualify varied/color
subsets or make compact output suitable for source-character shaping.

Directory admission validates every original record and absolute range before
construction, including duplicate records. Final sorting keeps the last original
table of each raw tag, four-byte padding, original search fields (including the
4096-table u16 wrap), wrapping checksums and final head adjustment. The whole-font
fallback keeps the original pre-adjustment head table checksum; it does not repair
source quirks. The existing standalone SFNT writer retains its independent policy.

Private borrowed table views avoid source payload copies until final output;
only modified tables and glyphs acquire temporary owned storage. All explicit
allocations are fallible and map failure to original InvalidFace. Scratch and
composite visits are bounded by the original u16 glyph inventory; directory sort
is O(T log T), followed by original glyph/reference and copied-byte work. No unsafe
code, font interpreter, OS callback or external font engine is used.

Implementation and source formatting only. No compiler, tests, new fixtures,
browser/font execution, benchmark or build/trace artifacts were produced. Exact
original bytes/errors/capacities, malformed inventories, lifetime, package and
performance qualification remain open.

The separate `font::managed_subset` facade also ports the managed producer's
independent malformed-input policies. Its dependency scan reads against the whole
glyf table and keeps already discovered components when a later record truncates;
it does not substitute native glyph-local bounds. Equal output offsets skip even
when outside storage. Compact final argument overruns without instructions retain
the original managed behavior. Managed signed offsets, checked table search fields
and storage bounds stay separate from native 4096-record admission and wrapping
fields. Shared byte writers do not erase these source-policy distinctions. The
Rust immutable glyph slice replaces IEnumerable transport, without introducing
deferred host callbacks or claiming managed exception-type ABI equivalence.

Original source SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Subset/progpu_native_sfnt_subset.cpp` | `25ed15b72aff4ad1e5941d5e716da555da6aa1b29e2b0f0c4dd5d267240d5b4b` |
| `src/ProGPU.Native/src/Text/Subset/progpu_native_sfnt_subset_compact.cpp` | `f77cd79c8d6388abe77ae51aa838daad99c1150cc7776dc63e0d3c4f0ee0ba12` |
| `src/ProGPU.Native/src/Text/Subset/progpu_native_sfnt_subset_glyphs.cpp` | `820da3f388ac33c0936fe71f2db29e12412fd8517a6941d97b7abf570b98cf37` |
| `src/ProGPU.Native/src/Text/Subset/progpu_native_sfnt_subset_internal.hpp` | `3f2e67c3890838130bb342e029c8246145f47e4366daa82d29c0e31dcee89a27` |
| `src/ProGPU.Native/src/Text/Subset/progpu_native_sfnt_subset_tables.cpp` | `8059baa47531d4e7715a9b5ad26d172930d26ced7025131b463c15aa8d985600` |
| `src/ProGPU.Text/SfntFontSubsetter.cs` | `a2707cd93a5750f0adf246bf356bba85bf9af3ff2318a59575c714a57d54b15f` |
