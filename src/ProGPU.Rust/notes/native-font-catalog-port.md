# Native catalog, selected-file owners and metadata producers

The static gasp, native hdmx, whole-table VDMX and exact horizontal
`SourceDeviceLineMetrics` paths were already implemented. Original native C++ has
hdmx/gasp queries; its VDMX/source-horizontal producer is the separately ported
managed API. No additional native VDMX policy is invented by this increment.

This increment ports the distinct original C++ `system_font_catalog` and its
missing native name/style/resident producers. The existing managed FontManager
catalog keeps its own semantics.

| Public API | Original implementation retained |
| --- | --- |
| `NativeSystemFontCatalog::{system_font_directories,discover_system_fonts,discover_fonts}` | Native platform directories, recursive permission handling, no directory-symlink traversal, exact path dedup and final name/path/face ordering; whole discovery publishes atomically |
| `face_info`, `match_family[_bytes]`, `match_character[_bytes]` | Actual metadata owners, ASCII byte folding/FNV identity, independent catalog style defaults, preferred families then exact-style/all-face passes, excluded source identity and nominal native cmap selection |
| `load_face`, `load_face_with_mapper`, `NativeLoadedFontFace` | Selected-file-only load, weak whole-file cache, mapper outside the lock followed by exact slot recheck, actual immutable owner shared across collection faces and all downstream native SfntFace clones |
| `SfntFace::native_name_requirements`, `native_decode_name_into`, `native_name` | Full name inventory admission, first best score, original malformed UTF-8 one-byte consumption, UTF-16 replacement/trim/NUL semantics and original generated BMP letter inventory |
| `native_face_style`, `native_embedding_rights` | Original OS/2/head precedence, native default/clamp policy and independent missing-versus-zero fsType bits |
| `native_glyph_resident_requirements`, `native_glyph_resident_sbix_into`, `native_glyph_resident_font_into` | Full raw directory and SBIX strike admission; exact selected/duplicate/no-ink slots, outer duplicate origins, first raw SBIX replacement, original tags/order/checksums and untouched other tables |
| `NativeLoadedFontFace::create_glyph_resident_face` | Actual face-zero resident generation retaining original catalog index and source identity |

Discovery reads only the original 12-byte headers, complete bounded TTC/table
inventories and selected name/OS2/head payloads. Metadata limits remain 4,096
faces/tables and 64 MiB per selected payload. One damaged face rejects the whole
file candidate; malformed files do not become filename-only fallback entries.
Scan errors retain actual partial counters and the previous catalog. Filename
fallback applies only after a valid metadata-only face was constructed.

Character lookup lazily freezes each native cmap attempt, including absence and
failure. Actual allocation failure is reported on that attempt. It deliberately
checks nominal cmap coverage rather than the independent provider renderability
probe. Requested-family best style, original ordered fallback families, original
visited slots and both inventory passes retain source order. Language-tag requests
bypass character caching; the no-language cache retains both positive and negative
results in the original 4,096 FIFO. Catalog normalization remains distinct from
the native style-variation request's unspecified-base normalization.

Loaded faces and downstream clones lease the actual whole-file storage owner;
there is no storage-to-font back edge and no owner cycle. Rediscovery retires only
catalog caches, leaving already selected fonts alive. Mapped and resident storage
are separate actual generations. Native resident output preflights all raw table
extents and capacities before writing; it neither ASCII-normalizes tags nor
substitutes the managed extractor's signed-size contract. The native name writer
also leaves output untouched on unavailable data, malformed inventory or capacity
failure. Requirements remain independently queryable before caller allocation.

## Explicit host boundaries

The built-in safe loader uses the original full owned-file fallback and reports
`is_memory_mapped() == false`. `NativeFontFileMapper` accepts an actual host-owned
`NativeMappedFontLease`; no OS mmap implementation or fake mapped evidence is
introduced. Actual Unix filesystem path bytes are retained. Windows non-ASCII
`filesystem::path::string()` conversion depends on the original host code page:
`NativeCatalogPathEncoder` provides that explicit producer. Built-in Windows path
encoding admits ASCII and otherwise leaves the file skipped, rather than hashing
invented UTF-8 bytes as source evidence. Raw names and source paths therefore use
owned bytes with optional UTF-8 accessors; OS file access separately retains the
actual PathBuf. Browser-owned byte discovery/selection uses the independent native
font-provider API, not a fabricated system filesystem catalog.

Vec/HashMap/VecDeque candidate capacities use fallible reservation. Standard Arc,
PathBuf and platform directory/environment allocations retain the crate's existing
Rust allocation behavior; this is not a claim of C++ exception parity on global
allocator exhaustion. Cache allocations remain optional where the original made
them optional. No foreign implementation is used; existing DESIGN.md ownership
and source/resource boundary architecture remains applicable.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Font/progpu_native_system_font_catalog.cpp` | `03cc26bab51d4c8dbc552abbda853c1f81fb15e7451e008d652e002067f03c4d` |
| `src/ProGPU.Native/src/Text/Metadata/progpu_native_sfnt_names.cpp` | `495db58daf638828f50bff02001906eccd99b993a292a9f600356a68a3afd393` |
| `src/ProGPU.Native/src/Text/Metadata/progpu_native_sfnt_metadata.cpp` | `4a735ad268adbf1b5a99424088990829839dd2d4d85c7163c8f41953518ba398` |
| `src/ProGPU.Native/src/Text/Metadata/progpu_native_sfnt_resident.cpp` | `3b8d1442d4eb400f09cd8d87d82b584b389fcd696b6ba00b73621e578b2ef57a` |
| `src/ProGPU.Native/src/Text/Unicode/progpu_native_unicode_categories.generated.hpp` (`sfnt_name_letter_ranges` only) | `1d97335b6954ec294436f56ba28537b22a0f6c54c48f7997a3f5174dfa6f008c` |

Implementation and source formatting only. No builds, compiler checks, tests,
fixtures, filesystem font discovery, font execution, browser runs or benchmarks
were performed. No build/result/trace artifacts were created. Actual source,
platform/package, browser, pixel and performance qualification remains deferred.
