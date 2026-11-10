# Actual original FreeType producer binding

This increment adds the optional `progpu-freetype` companion and the safe
`text::hinting::FreeTypeBackendFactory` adapter. `FreeTypeHintedFontCache` is the
existing original sixteen-entry FIFO face/latest-batch cache with that actual
backend. It connects directly to the native device shaper, whole-batch capture,
outline geometry and prepared replay APIs already ported. No ProGPU C++ runtime,
rendering or shaping bridge is used.

The main `progpu` package retains `unsafe_code = "forbid"`. Only the companion's
private `ffi.rs` permits unsafe, with checked C calls/pointer reads documented at
each boundary. Public API owners are exclusive and thread-confined, never cloned
or marked Send/Sync. The exact face/library/source teardown and MM allocation
ownership preserve all error paths. Only copied immutable metrics/outline arrays
leave a native load. An additive owned-slot backend method transfers those arrays
into the existing atomic batch without a second copy; the default borrowed backend
method retains its original immediate-copy admission.

Original source:

- `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font.cpp`: library/image
  ownership, interpreter, variation, nominal size, null phase, exact load flags,
  immediate checked slot capture, metrics and descriptor order.
- `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font_cache.cpp`: existing
  original fixed sixteen-face/latest-batch cache consumed unchanged by the adapter.
- `eng/progpu-verify-freetype.py`, `eng/progpu-prepare-freetype.py` and
  `eng/native-freetype.json`: exact owned support copies packaged with the companion;
  only read-only admission/helpers execute during a future explicit feature build.

Source SHA-256:

| Source | SHA-256 |
| --- | --- |
| `progpu_native_hinted_font.cpp` | `e9f740af00aa3d32a66f37cf4fbf5b130dff9e9e2bb38dc47f8f1d03ae0cab36` |
| `progpu_native_hinted_font_cache.cpp` | `b1939319e389eeb3c073aa3223eecd939f6b39046e8c388931c7818ba977de66` |
| `eng/progpu-verify-freetype.py` | `532107a16f0642760e05bc069bec9c2c0c128bcfd09c99af27f032c2b79d48f6` |
| `eng/progpu-prepare-freetype.py` | `e39e3d5ea65f51f7138ee8e3d5c9df2ce72db78b2db152d87b18cce93b351a4d` |
| `eng/native-freetype.json` | `6560b8c5170efb0448dffc467e0b04b5a4ce13846c2217e95a03eb22bad52fda` |

The pin remains signed FreeType `VER-2-14-3`, tag object
`c740f0fda4274d6ffd2e5b64a25b06ef69803a07`, commit
`0a0221a1347e2f1e07c395263540026e9a0aa7c7`, original signer
`58E0C111E39F5408C5D3EC76C1A60EACE707FDA5`. A future build requires explicit
`PROGPU_FREETYPE_MANIFEST` for the original prepared external installation.
The build checks all original receipt/architecture/header/notices/source-signature
controls, retains and rehashes the actual archive under a private static link name,
and embeds immutable manifest/archive/inventory hashes. It never downloads,
builds FreeType, uses pkg-config, chooses an ambient library or executes fonts.
Actual runtime evidence separately observes the original ordered seventeen symbols
with dladdr/GetModuleHandleExW and requires their executing image identity.

The reviewed [freetype-rs API](https://github.com/PistonDevelopers/freetype-rs)
(version 0.38.0) does not expose safe interpreter readback, exact nominal request,
complete fixed MM selection, format query, outline checking or original library
construction. Its default/bundled dependency policy also does not establish the
original source receipt. No partial wrapper or approximate call was selected.
The companion uses only the documented FreeType public ABI declarations:
[face](https://freetype.org/freetype2/docs/reference/ft2-face_creation.html),
[glyph slot](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html),
[size](https://freetype.org/freetype2/docs/reference/ft2-sizing_and_scaling.html),
[MM](https://freetype.org/freetype2/docs/reference/ft2-multiple_masters.html),
[outline](https://freetype.org/freetype2/docs/reference/ft2-outline_processing.html).
No third-party implementation code is copied or reimplemented.

The adapter shares whole owned font storage. Bounded, static or custom font leases
freeze an immutable byte copy for the native pointer lifetime while the outer
SfntFace and every batch retain the original source identity. All coordinates,
metrics, outline tags/flags, duplicates and no-ink slots remain unchanged.
Only explicitly Unhinted loads omit instructions. No bitmap rendering API is
exposed, and no GDI/GDI+/ClearType/source-policy equivalence is asserted.

Implementation and touched-source formatting only. No compiler, dependency
preparation, verifier execution, font load, test, fixture, browser run or benchmark
was performed. Six-RID ABI/build/package controls and actual rendering/performance
qualification remain deferred. The packaged companion must be published before
an eventual dependent registry release; source packaging is not qualification.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font.cpp` | `e9f740af00aa3d32a66f37cf4fbf5b130dff9e9e2bb38dc47f8f1d03ae0cab36` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font_cache.cpp` | `b1939319e389eeb3c073aa3223eecd939f6b39046e8c388931c7818ba977de66` |
| `eng/progpu-verify-freetype.py` | `532107a16f0642760e05bc069bec9c2c0c128bcfd09c99af27f032c2b79d48f6` |
| `eng/progpu-prepare-freetype.py` | `e39e3d5ea65f51f7138ee8e3d5c9df2ce72db78b2db152d87b18cce93b351a4d` |
| `eng/native-freetype.json` | `6560b8c5170efb0448dffc467e0b04b5a4ce13846c2217e95a03eb22bad52fda` |
