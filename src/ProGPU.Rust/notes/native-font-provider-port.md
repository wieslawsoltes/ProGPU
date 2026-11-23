# Native host/browser font selection and style policy

Production implementation only. No build, tests, font execution, shader/browser
run, benchmark, new fixtures or generated build artifacts accompanied this port.

`text::native_font_provider` ports the original platform-neutral count/face
callbacks and caller-owned positive/negative cache. A selected result retains the
actual immutable byte owner, selected face index, provider identities and parsed
native SFNT view. `OwnedNativeFontProvider` supplies the same interface for host
or browser byte inventories without introducing filesystem discovery. Provider
callbacks need neither Send nor Sync; caller generation numbers remain explicit.

Resolution validates the request before callbacks or cache mutation. An exact
negative cache hit performs no callbacks. A positive hit reacquires that original
slot and rechecks family, cmap and renderability; failure restarts the complete
source-order scan. Raw provider weight stays unclamped. Italic/Oblique remain
distinct cache keys while sharing the original style penalty. First ties, the
zero-score early return, bounded round-robin replacement and empty-cache cursor
behavior are unchanged. Fallback uses one source pass, ordered nonzero family
identities, registered fallback faces, then other faces, retaining the original
excluded identity and family/style tie rules. Selection itself allocates no heap
storage; provider callbacks remain responsible for their own work.

The renderability helper uses original native TrueType, CFF1/CFF2, COLR, SVG,
sbix/CBDT and whitespace-category order. It never substitutes the system catalog's
distinct nominal-cmap-only matching policy. See the separate
[glyph query](native-font-glyph-query-port.md) and
[catalog](native-font-catalog-port.md) inventories.

Fallback preferences preserve all nine original static family inventories, fixed
64-slot storage, language-before-scalar ordering, ASCII-only case/separator/trim
rules and stable first deduplication. Both UTF-8 string and raw native byte views
are supported without allocation or invented Unicode normalization. Caller-output
capacity is checked before any output slot changes; unused slots remain intact.

Style variations preserve the original unspecified-base condition, independent
weight/width clamps, optical-size admission, exact fixed width percentages,
italic extrema and 64-bit slant-magnitude tie behavior. Every original axis is
visited, unknown tags remain skipped, and recognized duplicate tags retain their
source slots. The output records retain unclamped user coordinates alongside the
original normalized coordinate and axis index. Whole-axis/normalization admission
and output capacity precede publication. Catalog nonpositive-style defaults and
provider raw style scores stay separate policies.

All source callback/error/order/float/ownership controls, actual host adapters,
browser/native pixels and package/performance qualification remain open. This
selection API does not claim system discovery, SVG rendering or bitmap decoding
qualification. Main-crate unsafe prohibition and original canonical shaders are
unchanged.

Original source SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_font_provider.cpp` | `22e5c057e8a44c4e7904a031564ca0091ffca6f1585c62a72615846a7509c199` |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_font_fallback_preferences.cpp` | `3e6d9fc9d2c720d49260ad82cc388c128b68b7464af1ed5a1ecc549c3e7e8529` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_font_style.cpp` | `0d582151f4bded656eb444731e841e5a64d27421e9a304eb49274cb436a05e86` |
