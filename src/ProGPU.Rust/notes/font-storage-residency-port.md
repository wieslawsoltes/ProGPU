# Safe font storage and glyph residency port

Implementation-only increment. Formatting was the only tooling pass; no compiler, tests, fixtures, browser runs or benchmarks were added/executed and no build artifacts were generated.

`font::{FontData,FontDataLease,FontStorageKind,FontFileStorage}` retains actual immutable owned, static-embedded or shared source owners and bounded zero-copy ranges. `SfntFace::from_storage` and `ManagedFont::from_storage` use those bytes directly; original `new` APIs remain compatible. Embedded/resource/reader/slice factories preserve complete source selection and compressed normalization; borrowed readers remain caller-owned and the separately named consuming reader API transfers ownership explicitly. Shared leases retire through Arc ownership, including retained face/table/layout/variation/bitmap consumers; no raw pointer or false OS-mapping identity is introduced.

`font::resident::{extract_glyph_resident_font,extract_glyph_sbix}` ports complete original bounded random-access extraction: all selected-face table extents; original TTC/4096 limits; first sbix/maxp selection; every original strike and glyph-offset slot; depth-16 duplicate resolution and outermost origin override; original supported PNG/JPEG/TIFF record tags; empty bitmap preservation; exact table order/duplicates/checksums/alignment/search parameters; original uint limit arithmetic; recomputation of every sbix-tag checksum. Outputs publish atomically. The typed execution preference/metrics distinguish intrinsic SIMD checksums and offset generation from the scalar reference. Header/record traversal remains scalar because reads, first-match selection and duplicate continuation are dependent. Forced GPU paths reject: this is a stream/container producer, not a pixel renderer.

`ManagedFont::load_glyph_resident_file/data` selects a compact face-zero SFNT and retains the actual original source face index separately. `try_bitmap_glyph` first checks the selected resident glyph, then the retained original bitmap source, then the original CBDT route. A supplied safe source lease borrows its actual full sbix table after whole-collection admission. Native files use the original non-mappable fallback with a one-glyph demand cache; returned bitmap slices own their exact bytes beyond cache replacement. Variation instances retain this shared source; real palette clones deliberately copy the compact bytes and drop the resident fallback, matching the original constructor. Decode, catalog renderability and text paint use fallible bitmap selection so capacity errors survive; legacy `bitmap_glyph` remains a documented nullable convenience.

Original reference identity now keys the new manager's style/variation/character/exclusion caches explicitly with `same_instance`; the existing ManagedFont semantic equality based on normalized coordinates is unchanged.

Mapping performance remains a separate provider boundary. No directly usable safe mmap constructor is available in this crate: the transitive memmap2 file mapping API requires unsafe. `RequireMapping` fails explicitly; automatic file loading reports Owned, and a caller-supplied shared lease reports SharedLease without asserting OS mapping. This implements real source ownership and the original owned/demand-read fallback, not an mmap performance claim. Browser sources are static/owned/shared resources; native-file entry points reject on Wasm. Original provider/package/source-pixel gates and all comparative validation remain pending. Existing [architecture research](../DESIGN.md#architecture-research-2026-11-08) continues to apply: retain immutable font ownership and lazy caches independently of GPU/device/raster policy. Only original ProGPU source was ported.

## Original source SHA-256

- `src/ProGPU.Text/MappedFontData.cs`: `5760648ed5cf72bcec63a7c5700648ce11840d061dcd0a722be83a8a3d1b7456`
- `src/ProGPU.Text/EmbeddedFontData.cs`: `8a30c7a1462c3b08a78ca49f93b8e52cf26895100dae25202dc10469c9867478`
- `src/ProGPU.Text/TtfFont.cs`: `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b`
- `src/ProGPU.Text/TtfFont.Variations.cs`: `318cae60429ea6f74da43bc1338b81b8c8dec2c94dde3da4ce750e705aec5035`
- `src/ProGPU.Text/SfntFontMetadataReader.cs`: `74040d8e2350406bc3270f8607ef693afc9faa417ced062a394f3fc00b2ee0cd`
- `src/ProGPU.Text/SfntFontFace.cs`: `4639127d2390813ae578d93a5a18cd5d4cd613235ac704b8d5d61a1123393e0a`
- `src/ProGPU.Text/FontManager.cs`: `a1207f2850f7317981ea112400cdb5048ac7dacbce8577d53e19fdd33b87648b`
