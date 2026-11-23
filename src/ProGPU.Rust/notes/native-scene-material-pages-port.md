# Native material and text-style pages

`NativeBrushPage` and `NativeTextStylePage` compile actual admitted scene owners
into the original packed page records and per-command mappings. Construction
publishes one immutable candidate after all allocations and source decoding;
previously retained pages remain independent. The exact scene owner is retained.

The brush compiler installs original zero sentinels, walks the native state cursor
at its original default DPI, and keys variants by resource ordinal/local index/
exact opacity bits. Mesh and explicit source-coverage commands retain brush source
opacity because their separate state opacity applies after vertex-color blending.
Painted glyph materials use their exact paint resource and index. Physical stop
inventories share ranges only under the original resource/offset/count key.
The original native integer hash is retained using a private Rust HashMap hasher;
output order remains command/source order, independent of table buckets.

Text styles retain their own sentinel and raw rendering/coverage metadata; only
the original alpha component receives state opacity. Exact keys preserve signed
zero. A missing command style remains `NO_INDEX`, separate from invalid lookup.
`native_glyph_payload` retains the original unstyled/styled/painted prefixes and
positioned glyph bytes without shaping or rebuilding glyph geometry.

The convenience material resolver returns no material only for an actually empty
command payload. A nonempty payload whose packed index is zero still returns the
original sentinel record; these source cases are not silently conflated.

Expected work is O(C+M+G) for commands C, remap occurrences M and newly retained
stops G, with bounded native inventory limits. All page data and remaps are owned;
stable consumers need no new lookup allocations. Actual GPU binding/upload/cache
consumption belongs to the next execution integration, and is not qualified by
these CPU page records. Generic Rust allocation failures become the crate's
capacity error; no mutable native last-error buffer is fabricated.

The shared `draw` module retains exact original contiguous analytic byte ranges,
path index ranges and glyph instance/paint ranges. Merges preflight both nonempty
records and count overflow, preserve original unsigned endpoint arithmetic and
leave the retained record unchanged on failure. Equal state/target and source
ordering remain the caller's independent conditions.

| Original source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_brush.cpp` | `bee6831bf6b8a40a7956918d54c5c668642e8cd4078ae30f09407641e34a9b8d` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_text_style.cpp` | `52c3a8904530a6d09ed0853afe865ffa3edb785967e58a315f85088b9b8548c6` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_draw_merge.hpp` | `9babc7b9197f3be3652a5fe976696bbaa53f243e8539e2b3625ce3e7ecebda15` |

Implementation/source formatting only; original bytes, errors, cold/warm memory,
runtime, browser, shader and performance qualification remains deferred.
