# Native hinted producer lifecycle, cache and geometry writer

The original actual hint interpreter is external FreeType 2.14.3, not an owned
managed VM. `TtfFont.cs` explicitly skips TrueType instructions; managed
`HintedGlyphGeometry` accepts retained captures through the native adapter.
This increment ports ProGPU's complete owned lifecycle/cache/capture/writer
algorithms around required safe backend interfaces. It does not invent a new
interpreter, install a dependency, use a C++ runtime bridge or claim pixels.

| Connected API in `text::hinting` | Port |
| --- | --- |
| `create_native_hinted_face` | Original request/RID/dependency/library/interpreter/face/axis/size/phase order |
| `NativeHintedBackendFactory`, `NativeHintedLibraryBackend`, `NativeHintedFaceBackend` | Explicit actual dependency ownership and execution boundary |
| `NativeHintedFace::capture` | Whole-ID preflight, exclusive sequential loads, immediate complete raw slot copies and atomic batch publication |
| `NativeHintedFontCache` | Original 16-entry FIFO, exact source/request/latest ordered ID key, unchanged old generation on failure and saturating statistics |
| `native_hinted_{batch,run}_outline_requirements` | Complete original topology/metadata/coordinate/descriptor admission |
| `write_native_hinted_{batch,run}_outlines` | Allocation-free atomic original physical line/quadratic/cubic writer and complete source/final descriptor mappings |
| `NativeHintedOutline::gpu_record` | Canonical renderer record transport; actual phase stays in physical geometry |
| `native_hinted_batch_counts`, `copy_native_hinted_batch` | Exact 136-byte descriptor/16-byte point transport, whole-batch preflight and allocation-free atomic copies |
| `NativeFontOutlineGeometry::{new,copy_into}` | Original owned already-positioned geometry, exact retained batch generation and complete no-ink descriptor copies |

The cache implements `NativeHintedProvider` directly and connects to the existing
`NativeShaper::shape_device` pipeline. Actual backend execution remains required.
Source identity uses the same immutable font allocation and face index. Cache
keys retain both requested device sizes, interpreter, outline target, phases,
all raw axes and complete ordered glyph IDs, including repetitions/no-ink slots.
Only successful new entries advance the FIFO cursor; failed replacement leaves
the original face/key/batch retained. Face/library owners retire before source
bytes and the factory's actual loaded dependency owner.

Typed dependency metadata retains the original pin, exact RID, prepared receipt,
archive/header/notices digests, header/runtime versions and all 17 original
loaded-image origins. Actual signed receipt/archive/architecture/header/legal/
producer-probe verification and platform module observation are mandatory backend
responsibilities. Merely constructing metadata is not evidence. Native admission
remains the original six 64-bit desktop RIDs; no browser/32-bit producer is inferred.
Normal/Monochrome/HorizontalLcd/Unhinted load flags preserve outline-only,
NO_AUTOHINT and PEDANTIC policy; original driver failures retain their error classes.

The writer preserves all raw source coordinates, exact float/64 conversion and
implicit float-add midpoint admission, original contour order and control-point
bounds. Strict/nonzero/antialiased policies remain distinct. Monochrome remains
already-positioned-batch-only, admits original lines/quadratics only and retains
raw SCANTYPE aliases, contour overrides, original outline flags, contour segment
offsets/counts and singleton-contour policy transitions in glyph-only metadata.
Owned batch geometry retains the actual captured generation past context/cache
retirement, copies original records without font execution, and never invents
shaping/paragraph metadata or caller placement. Fixed-width copy preserves all
raw i64 metrics, source-order prefix offsets, point/tag identity and glyph-local
contour ends. Canonical Rust i64 point storage takes the original long64 bulk
route even on Windows; explicit SIMD/scalar routes remain separate.
All topology/policy/capacity checks precede output or scratch writes; unused tails
stay unchanged. Safe Rust borrows exclude the original raw overlap/alignment
hazards without weakening whole-source admission. No generic path policy changes.

Original source paths below are relative to the repository root; only ProGPU
implementation was ported. Prior DESIGN.md architecture references still apply.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font.cpp` | `e9f740af00aa3d32a66f37cf4fbf5b130dff9e9e2bb38dc47f8f1d03ae0cab36` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font_cache.hpp` | `5cb666dbc7133fe10ee0c386694ca9ef366169d3e153f985d2e5499f4b71beea` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_font_cache.cpp` | `b1939319e389eeb3c073aa3223eecd939f6b39046e8c388931c7818ba977de66` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_outline.hpp` | `9fbeaa05402ad8f8203d91e6a4ad9c03dfbc8564223d6eeeb4bf6c646349c022` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_outline.cpp` | `b9043949424cf5f370bd6fa55a40c2e3d570c01db8d5e33193d1958dfbd55e2e` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_transport.hpp` | `0710d1efc41971f91c67d3f1fdc030ffbe1cc80a44784f56e5f2942f4837ec5b` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_transport.cpp` | `85d6178c4439081549a654abbf726eef4cda5c1fda1cbf5b84e335c978bd7f14` |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_text_shaping_interop.cpp` (owned font-outline geometry only) | `501d9e190bd966ce2885ff71960ef1b1b24dbd9ce7f786d86817e6261e5b05dd` |
| `src/ProGPU.Native/cmake/ProGPUFontHinting.cmake` | `352a2439d0707e78b0f2a9be4453c1426fd9ea42d9a17cdd05acbeb8dc8183df` |
| `eng/native-freetype.json` | `6560b8c5170efb0448dffc467e0b04b5a4ce13846c2217e95a03eb22bad52fda` |
| `eng/progpu-verify-freetype.py` | `532107a16f0642760e05bc069bec9c2c0c128bcfd09c99af27f032c2b79d48f6` |

Remaining: actual safe FreeType backend/binding, prepared dependency verifier
integration, independent original producer/pixel evidence and retained shaped-source
geometry/paragraph/provider/package application integration. Paired unfitted replay
and captured font-placement rendering are now connected; see
`native-font-policy-port.md` and `native-hinted-replay-port.md`. No default backend, source
raster-mode equivalence or browser hinted producer is claimed. Native raw long
values remain i64 until original narrowing. Point/key Vec copies are fallible;
Arc allocation follows the existing crate contract. Request axes are copied per
published batch rather than introducing a second shared identity representation.
No compilation, lint, tests, browser, benchmark or new fixtures were run/authored;
formatting only, with no generated build/trace/result artifacts.
