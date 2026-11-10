# Native fragment interaction port

This implementation directly ports the original ProGPU native fragment interaction
writer and queries. It does not use the managed layout executor, infer source
clusters from geometry, shape new runs, or query a font provider. The existing
architecture research in `DESIGN.md` informs the same immutable layout/query
boundary: CPU interaction retains an actual native layout generation independently
of glyph geometry, GPU residency and target-dependent rendering.

| Original repository source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Native/src/Text/progpu_native_text_interaction_impl.hpp` | `65266bdae4fe8939bc485a83abb2c6a5a7034b108fcdfbc8659cfde13355eeb0` | Full fragment input admission, requirements, single box/caret emitter, retained record verification, hit testing, source-affinity caret lookup, sequential visual movement, selection and physical fragment navigation |
| `src/ProGPU.Native/src/Text/progpu_native_text_interaction.cpp` | `5a051b1588479b03401df22b71dc044cc213ca6fb0415796152a56866089e1cc` | Distinct positioned and advance-origin fragment entry points; exact placement/origin counts |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_text_interaction_interop.cpp` | `dc6a3a0823cd2b9f314b4076c02152b5986139147c283df70ebbe71f97511f8e` | Bounded request/count semantics and source direction enumeration; pointer/alignment and reserved-byte ABI concerns become safe slice/enum ownership |

The public borrowed `NativeFragmentInteractionInput` admits the complete original
glyph/line/fragment inventory before output writes. `requirements`, `build_into`
and `validate_retained_records` share the original emitter. The two-per-cluster
caret capacity remains an upper bound; actual duplicate suppression preserves
input position, affinity and the original strict 0.0001 coordinate comparisons.
No caret is invented for a fragment with no glyphs. Fragment tops remain double
until the original conversion into the emitted float record. All original
first-operand min/max ties, signed zero, glyph-position versus advance-sum
arithmetic, row order and baseline/height checks are preserved.

`native_move_fragment_caret` implements the complete original physical left,
right, up and down algorithm. It keeps the source index as the final within-row
tie breaker, retains paragraph direction only for horizontal row transitions,
skips rows with no carets, compares vertical preferred-X distances in double and
retains matching affinity on equal distances. The navigation frame checks remain
independent of the construction checks. Selection keeps fragment index identity,
including separate rectangles for distinct fragments on the same physical row;
only the original same-fragment half-pixel rule merges rectangles.

`NativeFragmentInteraction::new` and `NativeFragmentParagraph::interaction`
retain the exact `Arc<NativeFragmentParagraph>`, including hidden source runs,
selected fonts, inline objects, tabs, floating sources and actual positioned
occurrence owners. They copy the caller's exact positioned cluster ends, resolved
bidi levels and optional line origins. These arrays are explicit in the original
native fragment API; the adapter does not relabel logical run levels as emitted
L1 levels or derive source coverage from outline bounds. Reflow creates a new
paragraph and therefore requires its own positioned metadata and interaction
generation. Old interaction owners remain valid independently.

`from_retained_records` admits the entire record inventory against the same
authoritative emitter, comparing each coordinate bit, affinity, index and final
deduplicated count before publication. `copy_into` validates every destination
capacity before writing source metadata or interaction records. Safe disjoint
mutable slices replace native pointer-alias checks. Raw borrowed algorithms do
not allocate; owned construction uses fallible vector reservations and publishes
only after every owned array is complete. Standard `Arc` header allocation retains
Rust's process allocation-failure behavior. This is retained interaction import,
not a font or whole-paragraph wire archive.

The raw typed functions follow the native C++ core's coordinates, which permit
negative fragment positions when their complete frame is valid. The original C
ABI's additional nonnegative-coordinate and reserved-word checks are not exposed
as a pretend raw wire reader. The connected paragraph route already receives the
actual admitted exclusion/floating writer frames. Undefined enum values and
non-Boolean affinity bytes are unrepresentable in this safe API.

Complexity remains O(G + L) for construction/record verification, O(C + L) for
physical navigation, O(B) for hit/selection and O(C) for source-position lookup,
with no query allocation except the explicit `selection` convenience result.
The immutable owner stores O(G + L + B + C) metadata/records in addition to its
shared paragraph; borrowed writing needs only caller output buffers.

No compiler, tests, browser execution, benchmark, fixture generation or performance
validation ran, as required by the implementation-first instruction. Only touched
source formatting was performed; no build, benchmark or trace artifacts were
created. Original differential, provider/package/source and performance gates
remain open. Generic source coverage/L1 production beyond the caller-owned native
fragment contract, full paragraph wire import and multi-style vertical paragraph
orchestration remain independent implementation scopes.
