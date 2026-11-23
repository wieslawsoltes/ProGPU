# Native OpenType positioning port (2026-11-09)

Implementation only. The user explicitly requires porting all code before
validation. No compiler, Clippy, test, source oracle, browser, GPU, benchmark or
package validation has run for this increment. Rustfmt only edits formatting.

## Original source provenance

Only original ProGPU implementation is ported. No foreign implementation or
external font/shaping engine is used. Source hashes capture the actual inputs.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gpos.cpp` | `54221a2c05837eaf06b9354aceee8fcfaf9f5b4a202bb5977a5a56d012de52c7` |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gpos_context.cpp` | `f314aa7ded594c34c44ffe2e4d7a554c70c112d30e2fb3280572bf91b531d615` |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gpos_internal.hpp` | `de21d043f05858b5bbbfbad30203e12dc10a9c648aa07b087dd30d0841554a8b` |
| `src/ProGPU.Native/src/Text/progpu_native_gdef_variations.cpp` | `fffb317b1e8fef1533b7579a85f51171e6b5cd33263c1ce4e4dc5a847c0b1939` |
| `src/ProGPU.Native/include/progpu_native_text.hpp` | `3fe6431abed09f1f15e0ebfc5ee18687ba8fc0ed9a5d65e13ff582c1c3d5c0a7` |
| `src/ProGPU.Rust/src/font/layout/selection.rs` | `ac1c3c6890e9c09a5fd08861de68b90c528f340a98dce7ac6d9fbfc615e3c24c` |
| `src/ProGPU.Rust/src/font/layout/digest.rs` | `acc11795fd1d1c491307cf85e28bc354ad7c33b58dbda1ddd25614418486e922` |

## Production implementation

- `LayoutTable::apply_gpos` executes native Single, Pair, Cursive, Mark-to-Base,
  Mark-to-Ligature, Mark-to-Mark, Context, Chaining Context and Extension lookups.
  Both whole-lookup and single-position entry paths preserve distinct filter
  behavior, original forward iteration, no-match continuation and partial writes.
- Context/chaining formats 1, 2 and 3 retain original glyph/class/coverage rules,
  ordered backward/input/lookahead visits, filtered sequence indices, dependency
  flags, nested lookups and 64-level bound. Zero-record matched rules remain applied.
- Primitive raw coverage/class searches retain extent-only validation and exclusive
  binary-search endpoints. Context and mark arrays retain the original stricter
  coverage/class generation admission. These are not interchangeable parsers.
- Independent X/Y Device formats 1/2/3 and VariationIndex records retain original
  packing/sign extension, ppem selection, absent-font behavior and native GDEF
  ItemVariationStore policy. Empty normalized coordinates avoid GDEF traversal;
  explicit all-zero inventories still validate the native store. Managed active-
  instance policy remains separate.
- Anchor formats 1/2/3 retain original device-delta/projection ordering. Device
  format-2 anchors read the actual original descriptor/glyph contour point from
  the synchronous retained owner; no point is reconstructed from an outline box.
- `GposDeviceSource` and `GposDeviceFrame` bind an actual borrowed immutable font
  object and exact normalized coordinates. Rust borrows replace raw nullable
  owner pointers. Projection/contour access must use captured native data and
  retain original generation lifetime; native provider capture itself is not
  supplied or qualified by this trait.
- Original independent four-metric additions use `wide::i32x4` and the source
  signed-overflow mask. Explicit scalar-reference addition remains available.
  Intrinsic admission is limited to original SSE2/ARM64 architectures plus
  explicitly enabled WASM SIMD128; unsupported forced SIMD rejects.
- Cursive direction and reciprocal-link retirement retain the original horizontal/
  vertical rules. Mark-to-ligature retains component metadata; a null target
  anchor is a valid non-match. Attachment records retain their exact eight-byte
  layout, component count/index and prior-positioned byte.
- Attachment resolution preserves recursive dependency order, original per-add
  design clamping versus checked device arithmetic, 64-level/cycle rejection,
  once-per-glyph publication and caller-owned scratch. Earlier published glyphs
  survive a later failure, matching the source mutation contract.
- `NativePositioningPlan` connects immutable native lookup selection and actual
  GPOS/GDEF/font ownership to allocation-free selected-lookup execution. It adds
  no shaping defaults and does not replace the independent managed shaper. Caller
  direction/ppem are independent of frozen source/coordinates. Attachments resolve
  explicitly after the selected positioning stages.

The target-dependent Rust bounds arithmetic rejects unrepresentable class-matrix
and component offsets before indexing, including WASM32. Original valid bytes and
arithmetic are preserved. Undefined C++ signed-overflow cases in ordinary design
addition have explicit wrapping behavior; nonfinite/out-of-i32 VariationIndex
conversion rejects instead of relying on host narrowing. Those invalid-domain
boundaries require original controls before qualification.

## Architecture and complexity

The existing architecture research in `../DESIGN.md` remains applicable:
[DirectWrite reusable layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal),
[HarfBuzz shaping contracts](https://harfbuzz.github.io/shaping-concepts.html),
[Skia text separation](https://skia.org/docs/dev/design/text_overview/),
[Direct2D domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains),
[WebRender retained state](https://github.com/servo/webrender/wiki),
[Vello](https://github.com/linebender/vello) and [Parley](https://github.com/linebender/parley).
Only contract-level boundaries are adopted: selected-font shaping remains reusable
CPU state and actual raster/upload remains GPU-owned. Lookup rule/attachment
traversal is sequential and dependent; four independent metric lanes use SIMD.
Lookup execution allocates no glyph/coverage arrays. Prepared selection owns its
bounded source inventories; recursive execution/attachment storage is bounded by
64 frames, and caller state/scratch remains explicit.

## Remaining scope and deferred evidence

This completes the authored native GPOS executor/device-frame boundary, not the
entire native shaper or hinted producer. Full native run orchestration, feature-
range dispatch, nominal/device capture providers, legacy kern/fallback-mark
integration and retained source-font transport remain separate work. Existing
managed shaping is unchanged. The native plan is directly usable with the existing
`ShapingGlyph`, native `LookupSelection`, `OwnedLayoutTable` and `OwnedGdef` APIs.

Deferred validation includes build/lint, all original valid/malformed lookup and
context inventories, exact partial-write/error/dependency behavior, device/variable
font identity, packed bytes and SIMD/reference overflow, original ppem/anchor
pixels, attachment cycles, native/WASM controls, allocation and performance.
No speed, parity, Windows/system-source or release qualification is claimed.
