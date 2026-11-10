# Native document flow

This is a direct port of original ProGPU document flow, under `agents.md` A-1's
in-repository port exception. No foreign implementation was used. The existing
[architecture research](../DESIGN.md#architecture-research-2026-11-08) remains
applicable: immutable CPU formatting/placement owns its actual source generation;
rendering and GPU realization stay separate. This increment changes neither
font execution nor rendering architecture.

| Authoritative original | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_document_flow_interop.cpp` (complete) | `d8aac726aa08a93cb3fd959bc6dc334acb06474643bde4caa27965ef58f5ff67` |
| `src/ProGPU.Native/include/progpu_native_document_flow.h` (complete record/API contract) | `21dce86064e4f8566aac14510485fa275cb242810470ba4b829d5119449da889` |
| `src/ProGPU.Backend.Native/NativeDocumentFlow.cs` (source/caller ownership and API contracts) | `6c636db7aba786e19c59a50ad92b86302bca08c287d4790652691d74537584f6` |
| `src/ProGPU.Native/src/Text/progpu_native_text_layout.cpp` (existing shared interval/anchor port reused) | `49a7106575011056c3ca3c4362138d210cefddf53492f3e5a61558ddbc664ef2` |

`text::native_document` implements all original document operations: standalone
width resolution, fixed shared table widths, nested block/line/object placement,
positioned paragraph fragments, independent actual content width, ordered anchor
width/collision batches, and legal-break sequential column/page pagination.

- Preorder forest validation retains the original 128-level and 2^20-item limits,
  complete line partitioning, sorted unique object/row/cell/paragraph slots,
  identical-or-disjoint shared column slices, and every later metric admission.
- Double arithmetic and evaluation order retain positive-margin collapse, empty
  through boxes, cell edge isolation, half-spacing, tallest-cell row height,
  stretched cell boxes, independent content width, nonmonotonic line Y, and
  explicit paragraph-local source positions. `std::max` first-operand tie behavior
  preserves signed zero. Independent double pairs use existing safe `wide` SIMD.
- Anchor sizing uses the original allocation-free two passes. Placement validates
  all exclusions even for an empty request batch, adds accepted boxes in source
  order, and preserves attempt exhaustion as unsupported. Actual child measurement
  and wrap policy remain source responsibilities.
- Pagination retains ordered prefixes, previous legal-break and next-forced-break
  indices, binary-search fitting, explicit source leading/preceding spacing,
  column/page forcing, and exact nonfitting indivisible-range rejection. There is
  no balancing, implicit emergency split, or inferred keep/widow/orphan policy.
- Source arrays are borrowed safely; enums/bools remove undefined numeric values
  and reserved ABI words. Rust slice borrowing supplies alignment and mutable
  nonaliasing, instead of repeating raw-pointer region checks. All output arrays
  are admitted before publication; fallible vectors map allocation failure to a
  typed error. There is no unsafe Rust or C++ runtime bridge.

The added Rust ownership layer is explicit, not an invented native binary wire:
`NativeDocumentLayout::{capture,import,rearrange,copy_into}` retains immutable
`NativeDocumentSource`, all input/result arrays and optional typed actual
`NativeDocumentParagraphOwner` bindings for design, fragment, hinted and
source-double paragraphs. Every bound complete line range must match the actual
paragraph's metric bits. These Arcs retain original fonts, source, run/style/bidi,
occurrence and interaction ownership; no glyph or shaping record is fabricated.
Explicit local positions remain caller-resolved document placement, as in the
original API. Sparse bindings are allowed for original external formatted lines;
they do not claim an owned font source. Measured objects retain measurements only;
the caller retains its actual visual and source interaction owner.

`rearrange` changes document placement while sharing the exact formatted source.
Text reflow is the original two-pass contract: resolve widths, format actual new
paragraphs, then capture a new candidate. It never silently reshapes retained
paragraphs. Full copies preflight every output including optional owner slots;
imports rerun the original arrangement and require bit-exact complete outputs,
including signed zero. `NativeDocumentPagination` retains that exact document,
complete source pagination policies and output positions; every pagination height
must match its document line. Repagination shares the source generation, and
full copy/import retains the same atomic publication contract.

Block/table work remains O(B + L + C + R + E) time/storage; ordered tree and prefix
dependencies remain scalar. Pagination remains O(N + F log N), O(N) storage. The
safe retained wrapper adds one owned source copy and Arc leases; `rearrange` shares
that copy. These are implementation properties, not measured performance claims.

## Scope correction and remaining evidence

The original native multi-style paragraph entry points admit horizontal
left/right directions only (`valid_paragraph_layout_options`, line 1205 of
`progpu_native_text_shaping_interop.cpp`). Native vertical entry points take
already-shaped glyph/break arrays and explicit vertical options
(`valid_vertical_layout_request`, line 1070); their writer, retained run and
interaction are already in `native_layout::vertical`. There is no original native
multi-style vertical paragraph producer to port. The separately owned managed
vertical layout is not substituted or relabeled as native. An additional such
product API would be new design work, not missing original implementation.

Original native shaping interop source SHA-256:
`501d9e190bd966ce2885ff71960ef1b1b24dbd9ce7f786d86817e6261e5b05dd`.
Original native text header SHA-256:
`3fe6431abed09f1f15e0ebfc5ee18687ba8fc0ed9a5d65e13ff582c1c3d5c0a7`.

Actual source adapters, source paragraph-to-page visual/interaction application,
provider/package/source pixels, native vertical source qualification, and the
external actual hinted producer remain independent. The raw arranger intentionally
does not shape text, generate glyph geometry, select fallback fonts, infer table
intrinsic widths/row spans, or invent source pagination policy. Existing native
paragraph geometry/Scene APIs remain the rendering consumers of retained owners.

Per the user's mandatory port-first instruction, no compiler, tests, fixtures,
lint, browser, pixel, performance or package validation ran for this increment.
Only source formatting was performed; no build or benchmark artifacts were made.
