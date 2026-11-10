# Typeface and font catalog port

Implementation only; compilation, differential checks, browser execution and performance validation remain deferred by the user's code-first instruction. No build or benchmark artifacts were generated.

## Connected production APIs

- `text::typeface::{Typeface,TypefaceOptions,FontStyle,FontStyleRequest,FontArguments}`: actual immutable selected `ManagedFont` ownership; independently optional requested style metadata; original empty/backing-owner semantics; SFNT/TTC/managed WOFF1 byte/reader/file factories; complete container streams; unique first-insertion table inventory with last-valid payload; exact positive-length table copies; nominal glyph/encoding/kerning facade; variation-axis/position copies, one-entry clone cache, original distinct same-face dedup versus cross-face duplicate overflow; palette cloning (including original face-zero convenience overload).
- `text::font_manager::{FontManager,FontStyleSet}`: immutable catalog and copy-on-write registrations; anonymous/named lazy and browser-owned registration; actual instance/source dedup; first host fallback; per-catalog default/empty face owners; original family/full-name/generic/platform/language precedence; variable wght/wdth/ital/slnt/opsz selection; 256-entry style and variation FIFO caches and 4096-entry versioned positive/negative character cache; language requests bypass character caching; real outline/color/bitmap/no-ink admission; separate italic/oblique style-set distance. Actual source loaders run outside catalog locks and encoded rendering. Registered failures persist; system-file failures retire only their captured attempt and retry later. Managed-manager and SKTypeface system caches retain separate original font roots.
- `FontSource`, `CallbackFontSource`, `OwnedFontSource`, `FileFontSource`, `OwnedFontCatalog`, `NativeFontCatalog`: explicit retained providers and native directory inventories; no invented browser/system font access or default. Metadata discovery reads bounded original TTC/directory/name/style/cmap records through buffered random access before loading a full font. Preserve first-table metadata policy, 4096 face/table bounds, 64 MiB cmap limit, filename fallback records and per-catalog string pooling. Source filesystem enumeration order remains host-owned; symlink cycles are bounded.
- `font::MetadataCharacterMap`: owned compact raw-cmap producer with original symbol precedence and format 12/13/4 lookup; deliberately omits full-face PUA remapping. Shared name decode/ranking and managed cmap group helpers only gained visibility; no existing lookup behavior changed.

The existing [architecture research](../DESIGN.md#architecture-research-2026-11-08) applies: keep reusable CPU font/shaping ownership independent from target DPI/raster providers and GPU device generations; retain lazy materialization and bounded identity caches. Metadata parsing, ordered fallback and cache publication are dependent scalar control flow. Existing bulk glyph/outline math remains in its SIMD/GPU owners; no pixel workload moved to CPU and no foreign implementation was imported.

## Boundaries

WOFF1 uses the root agent's managed normalization API; WOFF2 is unsupported in both originals. Nullable facade factories retain invalid/unsupported-font `None`, while direct container factories retain typed errors. Rust capacity failures remain explicit rather than being confused with missing fonts. Browser hosts supply actual owned fonts or callbacks; native OS outline/hinting/installation permission is not inferred. Mapped/resident-file optimization and glyph-resident SFNT/SBIX extraction are separate original factories, not claimed by this owned-byte facade. Font-family case matching uses allocation-free simple host Unicode uppercase with ordinal UTF-16 sorting; complete Rust/.NET non-ASCII casing qualification remains pending with other runtime-version controls. All original system/provider/package/application and source-pixel gates remain independent.

## Original source SHA-256

- `src/SkiaSharp/SKTypeface.cs`: `75f5301a7f954ea672de0f573e7a4869cf66267b80855bf89b705c1432eb7abe`
- `src/SkiaSharp/SKTypeface.Text.cs`: `48628c15b9a5e61aff928e8d4071ba6ef99ce0870896153ed99212c1eb3c77be`
- `src/SkiaSharp/SKFontStyleSet.cs`: `0d4966b9f3a3879677be92d47749009d97c6fd01d7da8242785e3741eb50ed2f`
- `src/SkiaSharp/SKFontVariations.cs`: `f75092ef577b28308db7e35c6192603551be951c74d8bbadffc98f83a10075d5`
- `src/SkiaSharp/SKFont.cs`: `0c85f493bba6bbee861083e9ceecf61e23e4d187d00bec08f9f1bd35352c88ed`
- `src/ProGPU.Text/FontManager.cs`: `a1207f2850f7317981ea112400cdb5048ac7dacbce8577d53e19fdd33b87648b`
- `src/ProGPU.Text/FontApi.cs`: `6eddbc04feaaa5fa61e31bd96d8ca50859b199ff3bf4a3f2fba3a6d539e30742`
- `src/ProGPU.Text/SfntFontMetadataReader.cs`: `74040d8e2350406bc3270f8607ef693afc9faa417ced062a394f3fc00b2ee0cd`
- `src/ProGPU.Text/SfntFontFace.cs`: `4639127d2390813ae578d93a5a18cd5d4cd613235ac704b8d5d61a1123393e0a`
- `src/ProGPU.Text/TtfFont.cs`: `7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b`
