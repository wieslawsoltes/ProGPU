# Native whole-draw execution preflight

Original ProGPU-owned source provenance (SHA-256):

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp` | `3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_binary_glyph_contract.hpp` | `7f6d3c2a1c003ef60c9282371db2b262ff813d1cc4fa1ec5cfc123a4e6b14de4` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_rgb_glyph.hpp` | `ad6fd9c66013c53901668f99a1ef7a2eb433fd41c82637e8bc943275230169fa` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp` | `759fa316f751c098485eb89aab0508ed059ee6c1ed64516862a9815ec9c8b6d5` |

`execution/draw_plan` ports the complete typed-draw inventory pass in original
render execution lines 827–1928 and its separate aggregate page limits. The plan
consumes the actual ordered execution cursor, retains every command frame and
source generation, and publishes ordered family selections only after the entire
candidate succeeds. It retains independent original brush/text-style hash domains;
their exact admitted scene owner must match.

All original vector, path, glyph, image, 3D, RGB and target-Clear families are
covered. Vector counting reuses the exact original count-only producer; it emits
no output geometry. Path and glyph admission use generic scalar record readers
over the original immutable bytes, retaining absolute Boolean slots, original
segment ranges, scan flags and float arithmetic without copying whole inventories.
The previous slice-based helpers delegate to the same algorithms. Retained frame
arrays, per-resource first-use bits, and existing bounded stroke scratch are the
only preparation allocations; no output geometry or GPU work occurs here.

The pass preserves localized versus source frames, static per-point guideline
gates, physical source path/coverage placement, source material resolution,
positioned glyph transformation, actual external image IDs/roles/generations,
picture-image metadata, image suffix admission and painted-glyph unit-basis
policy. Complete glyph outlines/segments and color pixels are charged on the first
resource-index use; every positioned occurrence is independently admitted. RGB
source budgets retain their own zero-based tile alignment and original shelf;
their actual format/attachment proof belongs to the later format-aware resource
and GPU stages.

Generated per-draw masks retain the original eight-family gate, first-resource
budget deduplication, picture/composite multipliers and independent chain/text/
image flags. Size multiplication is checked at this safe CPU boundary before
publishing a byte cost; actual device target limits remain independent. Clear
rejects per-draw masks, contributes the original draw count, and does not consume
an ordinary typed-draw compilation-budget entry.

`budget_input()` supplies the original per-command `NativeCompilationBudget`,
material bytes, generated-mask bytes, complete glyph coverage and binary inventory
to `NativeLayerExecutionPlan::finish_budget`. That stage adds the complete binary
horizontal snapshot exactly once, then combines layer/effect/material costs.
`validate_page_limits()` follows that combined budget and precedes actual page
allocation, preserving the original aggregate path/glyph/color-inventory stage.
The plan reports conservative source costs separately from actual raster/upload
metrics; no source extent is replaced by emitted geometry or a mask envelope.

GPU ownership/provider checks, actual linear RGB target admission, attachment
opacity proof, source-ordered bundle encoding, submissions and original pixel/
package qualification remain their independent consuming stages. This increment
does not claim successful rendering from stream admission or CPU preparation.

Implementation and touched-source formatting only. No compiler, tests, fixtures,
shader runs, browser runs, benchmarks, generated artifacts or validation executed.
