# Source copies into clipped compatible targets

`ID2D1Bitmap::CopyFromBitmap` and `CopyFromRenderTarget` now admit a compatible
destination whose active drawing scopes are exclusively aliased axis-aligned
clips. This extends the destination-side storage-write contract from
[memory copies](direct2d-scoped-memory-copies.md), not source capture admission.
The branch is stacked on PR #273, exact `4d159e87b6866f510c56abb512e32760d08cfb21`;
that dependency and this change still require their full hosted qualification.

The target uses one shared clip-only preflight for memory and source copies.
The semantic builder's existing suspend/copy/restore transaction is shared by
an additive owned-resource-move overload: no second clip implementation,
renderer, shader or pixel conversion is introduced. Root-only builder APIs
retain their old rejection behavior. The exact state-resource indices and
bounded stack metadata are restored on append failure; prior command/resource
bytes remain unchanged. Full overwrite builds replacement history independently
and restores the target's original captured clip rectangles before publication.
Subsequent source drawing retains its transform, tags and clip-pop order.

Source capture still occurs before destination locking. Original image bytes or
owned picture bytes are moved from the staging builder without another payload
copy; no source COM object or caller pointer is retained. The original physical
crop, source picture presentation, format/alpha identity and destination per-axis
DPI checks remain authoritative. Source active clips/layers, same-storage copies
with active source scopes, antialiased destination clips and materialized layers
remain rejected. An unsuccessful source capture does not clear its original
error or publish destination content/generation. Ordinary upload/WIC storage
and unscoped copy behavior are unchanged.

This is API admission into the existing original ProGPU algorithm, not a new
rendering architecture. Provenance is `copy_image_from_memory_outside_clips` in
`Scene/Builder/progpu_native_scene_builder_image.cpp` and the existing
`copy_from_source` / `record_bitmap_copy_locked` in the Direct2D target. The
shared builder is consumed by both wgpu-native and Dawn. Managed Scene has no
portable Direct2D COM recorder or interpreter for these owned picture payloads;
managed raw native submission reaches this same native implementation. No paired
managed rendering algorithm changes are needed.

Microsoft's original [CopyFromBitmap contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1bitmap-copyfrombitmap)
requires fitting matching-format storage and preserves failed batch errors.
[CopyFromRenderTarget](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1bitmap-copyfromrendertarget)
additionally rejects source clips/layers. Those contracts motivate the unchanged
source gate; they do not alone qualify our destination clip sequence. No foreign
implementation was copied. The original Windows execution below is required.

## Authored controls and pending execution

The original four memory-copy sequences and exact expectations remain intact,
now sharing only the source-independent destination fixture. Eight additional
source sequences cover both copy methods, partial/full destination overwrite,
96/192 destination DPI, independent 192-by-96 source DPI, nonzero physical crops,
transparent SRC replacement, two capture-time clips, changed drawing transform,
post-copy source mutation/disposal and draws after both clip pops. The independent
binary-color expected pixels are unchanged. Both native providers compare every
RGBA byte across cold/warm/independent replay and require 2/1/2 submissions for
upload sources and 3/1/3 for retained render-target sources. Windows runs all eight
sequences through actual system Direct2D/WIC before comparing original BGRA bytes
against the portable native replay and the independent expected pixels.

Twelve portable contract cases retain source-clip/self-copy/source-error,
destination-AA/layer and invalid-crop rejection for both methods, unchanged
destination generation/counts and successful later scope completion. The shared
raw builder controls independently run the complete existing byte-exact rollback
and unsupported-state inventory with moved sources as well as uploads. A named
module consumer calls the additive overload. Public C ABI/COM slots and wire
layouts remain unchanged; no generated declarations are edited.

Implementation and fixtures are committed before focused checks. No local native
binary, GPU, VM, runtime staging or original Windows execution is claimed.

Post-commit source checks: strict Apple Clang C++20 syntax-only compilation
(`-Wall -Wextra -Wpedantic -Werror`, 45-second process bounds) passed the complete
shared image-builder and Direct2D-target implementations plus builder/portable
compatibility test translation units. The old and new pixel templates were
explicitly instantiated under those flags plus `-Wshadow`; syntax passed without
executing either fixture. The memory ownership source guard covers 93 owned
fields and excludes seven non-owning identities. No object/library was produced.
The named-module consumer, Windows-only translation unit, native behavior and
GPU pixel/submission assertions still require hosted execution; source syntax
checks do not establish their result.

Hosted Build `37030458232` exposed one module-only declaration visibility gap:
the new import consumer uses `PROGPU_NATIVE_SCENE_STATE_CLIP_RECT`, whose existing
C enumerator had not been re-exported. The module now exports that exact constant
through its existing `using` pattern; the import-only consumer remains unchanged.
After committing the fix, LLVM Clang 22.1.8 precompiled the real module interface
and passed the complete consumer with `-fsyntax-only`, C++20 and
`-Wall -Wextra -Wpedantic -Wshadow -Werror` (45-second process bounds, actual Xcode
SDK). Apple Clang does not support this configured module path; an initial LLVM
attempt also rejected its stale default SDK path before using the actual SDK.
No object, library, executable or GPU work was produced. Hosted module execution
and whole-Build qualification remain required.
