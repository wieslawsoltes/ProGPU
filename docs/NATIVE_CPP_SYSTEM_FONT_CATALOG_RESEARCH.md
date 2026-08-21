# Native C++ system font catalog: architecture and research

## Scope

`system_font_catalog` gives native C++ consumers the same owned system-font
discovery, family/style selection, per-character fallback, and lazy face loading
boundary already provided by the managed `FontApi` and `FontManager`. It uses
ProGPU's SFNT parser and Unicode fallback policy directly. It does not call a
platform text, shaping, or graphics API.

Discovery is an initialization operation. Once it finishes, face enumeration,
matching, character fallback, and selected-face loading may be called
concurrently. A new discovery must not race those reads because it atomically
replaces the catalog's owned strings and metadata.

## Data flow and ownership

1. Conventional platform font directories or caller-provided directories are
   recursively enumerated for OpenType font and collection files.
2. Discovery streams the SFNT directory plus only `name`, `OS/2`, and `head`.
   A compact temporary SFNT view is passed through the existing validated
   parser, so metadata parsing has one implementation.
3. Each face retains absolute file identity, collection face index, family and
   full names, weight, width, and slant. Complete font files are not retained.
4. Character matching lazily streams and retains only the selected face's
   `cmap`. The bounded result cache is used when language tags do not change the
   fallback decision.
5. Loading a selected face reads its complete file once, shares immutable bytes
   among live handles, and creates the existing `sfnt_font_view` at the recorded
   collection index. Borrowed table views remain valid for the handle lifetime.

For directories `D`, faces `F`, selected metadata tables `T`, and queried
character maps `C`, discovery is `O(D + F*T)` and retains `O(F + metadata)`.
Family matching is `O(F)`. Cached character matching is amortized `O(1)`;
first-use fallback is `O(F*C)` in the worst case. Complete bytes are loaded only
for a selected face.

## Managed/native parity

The native implementation is a direct port of ProGPU-owned
`FontApi.ScanSystemFonts`, `SfntFontMetadataReader`, and `FontManager` matching
policy at checkpoint `497afbb3`. The managed implementation already has the
same capability and therefore needs no source change. Both sides use:

- the same conventional platform directory policy;
- metadata-only discovery and lazy character-map reads;
- collection face indices and style-distance matching;
- the shared language/script fallback-family policy;
- lazy full-face ownership for only selected fonts.

The native implementation adds C++ ownership and concurrency semantics rather
than a second font parser or a platform-dependent adapter.

## Primary-source comparison

- The [OpenType font file specification](https://learn.microsoft.com/en-us/typography/opentype/spec/otff)
  defines standalone SFNT fonts and TrueType/OpenType collections, including
  per-face table directories. The catalog preserves the collection face index
  and reads table offsets from the container rather than extracting a face.
- The [OpenType `name` table](https://learn.microsoft.com/en-us/typography/opentype/otspec190/name)
  defines family, preferred-family, and full-name records. The catalog prefers
  the preferred family and falls back to family and then file stem.
- The [OpenType `OS/2` table](https://learn.microsoft.com/en-us/typography/opentype/spec/os2)
  defines weight, width, and style metadata used by the existing ProGPU style
  parser and matching distance.
- [Fontconfig](https://fontconfig.pages.freedesktop.org/fontconfig/fontconfig-user.html)
  shows the value of centralized discovery, matching, and configuration on
  Unix systems. ProGPU adopts the reusable catalog boundary but deliberately
  does not take a Fontconfig runtime dependency; callers can supply additional
  configured directories.
- [DirectWrite font fallback](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_2/nf-dwrite_2-idwritefontfallback-mapcharacters)
  treats fallback as mapping text, locale, base family, and style to a suitable
  face. ProGPU keeps those inputs at its own deterministic font-provider
  boundary and performs mapping with its Unicode/OpenType data.
- [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  keep font selection outside the shaping operation. The catalog returns an
  owned SFNT face which can be passed to the existing ProGPU shaper; it does not
  mix discovery, shaping, layout, or rendering.
- [Fontique](https://github.com/linebender/parley/blob/main/fontique/README.md)
  provides font collection, fallback, source caching, and matching as a
  subsystem used by Parley. ProGPU adopts that separation while keeping its
  current explicit native provider and SFNT contracts.

## Adopted, adapted, and rejected

Adopted:

- one reusable owned catalog with lazy selected resources;
- explicit locale, requested family, style, and excluded-face inputs for
  character fallback;
- stable collection face identities and immutable selected-face handles;
- metadata streaming and bounded caches.

Adapted:

- directory discovery remains the managed ProGPU cross-platform policy rather
  than using a different native-only configuration source;
- matching uses ProGPU's deterministic fallback-family data and existing SFNT
  parser rather than OS services;
- cache entries omit language-tagged queries to avoid conflating distinct
  locale preferences.

Rejected:

- CoreText, DirectWrite, Fontconfig, FreeType, Skia, or browser font APIs;
- eagerly reading every installed font into memory;
- a native/managed callback on each glyph or fallback query;
- hidden shaping or rendering work inside catalog operations.

## Validation and performance gates

- synthetic standalone and collection fonts cover metadata streaming, face
  index preservation, family/style matching, character fallback, and shared
  full-file ownership;
- both header and named-module consumers instantiate the public types;
- managed `SfntFontFaceTests` and `FontManagerTests` remain the parity gate;
- an opt-in native benchmark reports discovery time, files, faces, skipped
  files, and bytes streamed for the real machine catalog;
- privacy and platform-API scans cover every changed file.

On an Apple Silicon macOS development machine with 434 font files and 851
faces, the final Release benchmark streamed 2,711,629 bytes. Ten warm-cache
runs had a 30.668 ms median and 32.931 ms maximum. These machine-specific
observations are recorded as a regression baseline, not as a platform
guarantee.
