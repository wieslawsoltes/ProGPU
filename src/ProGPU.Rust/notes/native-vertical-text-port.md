# Native vertical text production

Original ProGPU sources and SHA-256:

| Source beneath `src/ProGPU.Native/src/Text/` | SHA-256 |
| --- | --- |
| `progpu_native_vertical_text_layout.cpp` | `8a2fb1952ba9d9bff52ad3bfd94ba38a5669a5bbd7b6b6239377a2c1733edbdc` |
| `progpu_native_vertical_text_interaction.cpp` | `a4ae76e01af4fced5606d209299d766648dfec4f1b0baa8978d00a935d706eae` |
| `progpu_native_text_metrics.cpp` | `fad0dbca5d0c8f76ef66e64a7ad924a366f5b24f7ecc23e2036d950b5923c326` |
| `Shaping/progpu_native_open_type_projection.cpp` | `14b7b0f5f11fecb02ff8f17deedd91ea0dc03efc460afc56101710cb310ef4c8` |

This ports complete original vertical requirements, raw writer, font-aware writer, OpenType projection, column metrics and interaction/query algorithms. Existing DESIGN.md architecture references inform immutable CPU ownership only; implementation uses original ProGPU sources exclusively.

`native_vertical_requirements`, `native_layout_vertical_into` and `native_layout_vertical_open_type_into` preserve mandatory-only column breaks, maximum-column clipping, original centered column pen, per-glyph additions, alignment, input ranges and output indices. The raw ABI route consumes already Y-down integer metrics. The font-aware route consumes actual native Y-up glyphs and has separate projection. It retains original static vertical height/origin, variation-aware horizontal width, independent ties-to-even base rounding, integer half-width division, residual shaping deltas and final Y negation. Font-size projection is additionally exposed through `native_project_open_type_into` with exact glyph/cluster/flag retention.

`native_vertical_interaction_requirements` and `native_build_vertical_interaction_into` preserve full source ends/levels, contiguous column ownership, original advance extents and minimum extent direction, full column width, physical leading/trailing carets and ordered deduplication. Hit testing keeps squared-distance first selection and vertical half-box affinity. Caret lookup/movement and selection retain the original tie, weighting and same-column merging arithmetic. Input admission intentionally stays the original contract: it does not substitute outline bounds or stronger geometric assumptions.

`NativeVerticalText::from_run` connects an actual immutable `NativeRun`, selected font/variation instance and complete explicit original break/end/level maps to the writer and interaction. The complete source is retained beyond clipped columns. `reflow` reprojects the same generation; changing direction requires a separately shaped run. This is a run-to-columns producer, not a fabricated multi-style paragraph. Source maps remain caller-supplied as in the original native low-level APIs. Every owned candidate completes privately before publication, and `copy_into` preflights all capacities before used-prefix copies. All queries run on retained original records without font execution.

Raw writer and interaction APIs use caller storage. Existing Rust variation access owns internal phantom decoding scratch; no allocation-free claim is made for font projection. Public complete font-size projection stages privately before copying so an allocation failure cannot partially publish results. Native C++ performs two passes over externally supplied scratch; Rust retains the same arithmetic with an owned candidate. The retained text object also owns separate output/interaction arrays and does not expose native pointer aliases.

This is not multi-style vertical paragraph itemization, exclusion/floating fragment flow, generic unhinted Scene geometry transport, a hinted provider or original source/package/browser qualification. These remain independent implementation/qualification boundaries. Per the implementation-first instruction, no compilation, tests, fixtures, browser execution or performance validation ran; only source formatting was performed.
