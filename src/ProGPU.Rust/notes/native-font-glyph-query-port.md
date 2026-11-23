# Original native color, bitmap and font renderability queries

Production source only; formatted without compiler/tests/runtime/fixtures. No
build output, benchmark results or traces were generated.

`SfntFace::native_*` queries retain actual borrowed source bytes independently of
managed color/font probes. COLR v0-prefix lookup admits versions zero/one, keeps
native binary-search semantics and validates declared base/layer inventories.
CPAL validates every palette range, selects palette zero for an invalid index,
retains opaque-white defaults/foreground independently and applies the last
matching override only to valid entries. Layer decode preflights output capacity
and CPAL before writing. Query storage is O(1); output belongs to the caller.

sbix preserves all strike candidates, native nearest-ppem/larger-size tie policy,
NaN target selection, bounded duplicate depth, original requesting-glyph origin,
PNG/JPEG/TIFF tags and raw payloads. CBLC ports all five dense/sparse index formats
with 64-bit checked image ranges; CBDT ports formats 17/18/19, first matching
subtable policy, horizontal-format flags, metrics requirements and exact signed
bearings. Image bytes are not decoded or treated as pixel qualification.

SVG returns the first matching raw document range with the original encoded
16 MiB bound and three-byte gzip recognition. It does not impose UTF-8 validation.
Caller-output decode ports native single-member gzip optional fields/header CRC,
exact DEFLATE consumption, data CRC and ISIZE, sharing the existing owned raw
DEFLATE implementation through crate-only visibility. Plain-copy capacity errors
leave output untouched; gzip failure may leave partial bytes but publishes no
written count, as in native. Size lookup validates framing without inflation.

`native_has_renderable_glyph(code_point, glyph)` follows native candidate order:
nonempty TrueType data; exact default outline requirements; COLR layer count; SVG
bytes; sbix at64; CBDT at64; original control/format/space/line/paragraph categories.
Both glyf+loca tables select TrueType even without ink. CFF1 presence prevents CFF2
fallback after failure. CFF1/CFF2 use allocation-free outline_segment_count and
CFF2 receives the actual empty coordinate slice without padding. The separate
native_default_outline_requirements query exposes only this default-coordinate
request; varied outline decoding/bounds are not claimed by that convenience API.

Sequential table/header/strike/duplicate/sparse parsing retains original early
exit/error order, using checked scalar reads and O(1) scratch. It is structural
lookup rather than a substituted pixel renderer. Native SVG XML/layer execution,
bitmap pixel decoder/provider selection, platform font discovery and final native/
browser/provider/package pixels and performance retain their separate gates.

Original owned source SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Text/progpu_native_colr.cpp` | `fceaf8650f7e9029cf827c1cf889a299af874a1da23d455414efbef3fb93a524` |
| `src/ProGPU.Native/src/Text/progpu_native_cpal.cpp` | `b41d91e1276746c4492142bec91b4db6ce95d2d931dbe4b937725df9b8c31518` |
| `src/ProGPU.Native/src/Text/progpu_native_svg_glyph.cpp` | `bfb25599be92bcc7f8e5d43f17fb2a3a1ef83561093ba6a7b19b250f741e0ec7` |
| `src/ProGPU.Native/src/Text/progpu_native_sbix.cpp` | `2d76621a394ac89b2654d949055c0798b0ae3c63cfd399941c5399e6fedf0759` |
| `src/ProGPU.Native/src/Text/progpu_native_cblc.cpp` | `89f0034111702e2111049c404dad409132322c52d7130a4101aa1ec7a9d6b2b4` |
| `src/ProGPU.Native/src/Text/progpu_native_cblc_index.cpp` | `bf019abce966b0705981ca17524897a362ee24d48de338ffc0290cbaf6ae002e` |
| `src/ProGPU.Native/src/Text/progpu_native_cbdt.cpp` | `1fe2ac388f9a696d0960a6b60ffeca781c7073c000200faac703cbddabd3ffb7` |
| `src/ProGPU.Native/src/Text/progpu_native_cbdt_internal.hpp` | `dccbb039f398e9a035625752ee52831a064ecd4ee5cb09c24dcca08505b58092` |
| `src/ProGPU.Native/src/Text/progpu_native_colr_internal.hpp` | `bad8016ccf6d9cd9692a9f7858a7944a66daa10bb7a060450d6679c9ce30e17a` |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_font_provider.cpp` | `22e5c057e8a44c4e7904a031564ca0091ffca6f1585c62a72615846a7509c199` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_font_bounds.cpp` | `5b548ac58ffcb322683f703e551eaccb6ebbfb5ab3ca39b8df47a53eed15d584` |
| `src/ProGPU.Native/src/Compression/progpu_native_gzip.cpp` | `665e4e377ca3e5125c941bc6a2bbf33168d35967778487b18469bc6d7bcb4870` |
| `src/ProGPU.Native/include/progpu_native_text.hpp` | `3fe6431abed09f1f15e0ebfc5ee18687ba8fc0ed9a5d65e13ff582c1c3d5c0a7` |
