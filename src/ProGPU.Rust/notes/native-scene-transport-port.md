# Native scene stream ownership and transport

`native_scene` implements the original pointer-free stream envelope independently
of managed PictureArchive. It retains the complete actual immutable byte owner,
including unknown optional records, raw descriptor extensions, gaps, aliases and
payload bits. It exposes original 80-byte header, 48-byte resource, 64-byte command
and 64-byte metrics records through explicit checked little-endian IO, without
typed pointer casts or Rust layout assumptions. All nineteen resource values and
nineteen command values retain their original independent wire identities.

`NativeSceneEnvelope` admits the original header/version/endian/size/identity
prefix, table strides/alignment/counts, nonoverlapping table/arena extents, raw
resource/command descriptors, generic flags and payload spans. Resource IDs stay
strictly ordered and generations positive. Command uniqueness uses the original
eight stable byte-radix passes. Unknown required records reject; optional unknown
records remain owned. Empty spans require zero offsets, and payload spans may
alias within the arena exactly as the source permits. All frame coordinates keep
their original bits, including signed zero; negative extent and nonfinite command
bounds retain their original rejection. Immutable borrowed views need no per-item
allocation and cannot outlive their actual envelope.

Generation admission is the original merge walk over sorted resource identities.
Regressions reject at the new record offset. Equal generations require identical
kind, flags, sizes and payload/auxiliary bytes, independently of changed absolute
offsets or descriptor extension storage. New and removed IDs preserve original
behavior; this helper does not invent a scene-generation or command-ID rule.

`NativeScenePacket` owns complete resolved resource and command payloads. Its
canonical writer computes original fixed table placement and eight-byte arena
alignment, appends resource payload/auxiliary pairs before command payloads, keeps
zero offsets for empty spans, and zeroes actual padding/reserved header storage.
It never rewrites source descriptors while inserting output offsets. Basic
Save/Restore and Layer/Pop pairing is preflighted. Whole immutable candidates
precede caller output publication; short buffers leave every destination byte
unchanged. Retain the built envelope for repeated consumers. The copy API uses a
private complete candidate, an explicit extra allocation compared with the native
builder's direct write; allocation/performance qualification remains outstanding.

This is **transport admission only**. The envelope is not a validated semantic
scene and has no Renderer submission API. Original per-resource payload parsing,
nested picture recursion, cross-resource references, source/mask/layer/brush/glyph
policies, materialized depth and actual device/target gates remain mandatory and
are not bypassed by this type. Structural errors preserve original categories and
record offsets among framing checks; no claim is made about full validator error
precedence when omitted payload validation would fail first. The complete semantic
builder/compiler, live external image tables and source adapters remain work.

Source formatting only. No builds, tests, new fixtures, runtime/browser execution,
benchmarks or build/trace artifacts were produced. This production parser is not
new validation evidence for the Rust port. Main-crate unsafe prohibition and
original provider/package/source-application/pixel gates remain unchanged.

Original source SHA-256:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/include/progpu_native.h` | `bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441` |
| `src/ProGPU.Native/src/Scene/progpu_native_scene.hpp` | `781fc4fcae8bb40576dd9ad15abd353e28fe7e49d32f1e5993c3ee5f3d7fb298` |
| `src/ProGPU.Native/src/Scene/progpu_native_scene.cpp` | `a9630f05490363aeb82883a3901394187f6d36985d592faad88b1aa30975ee1c` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_serialization.cpp` | `90e6281a889db0348f3e1a0e3e4ee669de12cfb2983d799859f23a4b61a57662` |
