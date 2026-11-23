# Original native metadata and paired unfitted font replay

This increment ports the missing native raw-face hdmx/gasp policies and complete
explicit paired unfitted replay. Existing Rust VDMX parsing/static managed-font
admission, managed gasp and SourceDeviceLineMetrics/layout integration are reused,
not duplicated. Outline-load targets already belong to the hinted producer port.

| Public API in `text::font_policy` | Original behavior |
| --- | --- |
| `native_horizontal_device_metrics` | Exact native hdmx size, borrowed full glyph-width inventory and selected source owner |
| `native_static_rasterization_recommendations` | Native raw-face gasp admission, independent of managed outline metadata |
| `FontReplayGeometry::from_batch` | Complete captured descriptor inventory with explicit caller positions and retained original physical geometry |
| `FontReplayGeometry::select_occurrences` | Shared actual source/raster generation, original identities/positions and duplicate/no-ink selection |
| `capture_unfitted_font_replay` | Same-provider/font frozen Unhinted + Monochrome captures, complete topology rejection and scan-only metadata transfer |

Native hdmx validates raw directory duplicates/extents, version, exact stride and
size inventory, every row maximum and every padding byte before returning an
exact selected size. Native gasp uses the already ported identical complete-range
parser, retains raw fvar presence and native glyf/loca admission, and distinguishes
missing coverage from explicit zero flags. These queries do not execute fonts,
select raster modes or modify ordinary source metrics/placement. Existing VDMX
keeps full ratio/group/offset validation, original version-zero charset termination,
first applicable ratio, static square-pixel nonsymbol admission and exact source
line owner/size/horizontal-direction checks before layout replacement.

Paired replay snapshots IDs, positions and raw axes before either capture. Both
captures use one exclusive provider, exact original font allocation/face and the
same frozen size/interpreter/phase/axes. Only the explicit outline target differs.
The producer's actual batch identity, request and every ID are checked. Both
original geometries use the explicit batch-only Monochrome contract. Complete
raw glyph/point/tag/contour counts, contiguous offsets, curve-kind topology,
contour ends, source/no-ink descriptor slots, outline segment ranges and per-segment
kind/contour identity must match before constructing transferred output.

Only `7 | ContourOverride | (0x38 << 16)` transfers from the monochrome witness.
Original coordinate bits, outline bounds, non-scan/precision flags, ordering,
physical phase, no-ink records and caller positions stay unchanged. The original
source-adapter geometry admission preserves finite control points, exact bounds,
closure/continuity, complete binary contour metadata and the original 256-segment
binary limit. Local witness geometry/batch owners retire before publication;
returned views retain only the actual original Unhinted geometry generation.
Provider cache entries keep their independent original ownership. Arbitrary public
snapshot pairs are not accepted. These metadata-free placements do not acquire
invented shaping/paragraph records or source raster-policy qualification.

Original sources and SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Font/progpu_native_font_device_metrics.cpp` | `30d2824222e51fe66c3f270006b58194d6646e1c0a792603a5dd8f9b2d434d36` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_font_raster_recommendations.cpp` | `84c76dc5fec6182ee314a0f478e3f28afe36c34b374a7bb38e47f0c6df11a782` |
| `src/ProGPU.Scene.Native/NativeUnhintedFontGlyphGeometryFactory.cs` | `eae132749468924041af74f6183de2fb2c6e77aa8c8c718c99179163afe58d8d` |
| `src/ProGPU.Scene.Native/NativeFontGlyphGeometryFactory.cs` | `c46ad8730a120426264f59fc5c069df5e7d2af2dfe6de944e65c6c9faa572df7` |
| `src/ProGPU.Scene.Native/NativeHintedGlyphGeometryFactory.cs` (geometry admission/conversion) | `eabe87387706bf6623ab760389dae75e17889ab1452b55cfb738415efb194a7b` |
| `src/ProGPU.Text/HintedGlyphGeometry.cs` (font placements/selected ownership) | `ac9fa8aa8aff77fcd081e31e5ae65b8e73d5ae04f690761f276888c43f54dbec` |

No foreign implementation is used. Prior DESIGN.md CPU ownership/render-resource
architecture references remain applicable. Safe immutable owners and exclusive
borrows replace disposal/locking hazards; all variable-sized Vec copies reserve
fallibly, with Arc allocations following the existing crate contract. Actual
FreeType backend/receipt/origin verification, shaped paragraph/source adapters and original provider/package/
browser/pixel/performance evidence remain separate. No compiler, tests, lint,
browser, benchmarks or fixtures were run/authored; formatting only. No build,
trace or result artifacts were generated.

Captured placement renderer binding is now connected; see `native-hinted-replay-port.md`.
