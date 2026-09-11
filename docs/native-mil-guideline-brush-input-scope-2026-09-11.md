# Native guideline brush input/material separation

Acceptance is the external LibreWPF SDK application's native render/input path.
After image guideline coverage, target 4901 generation 2 rejects visual 4893's
content 4900. A diagnostic source trace identifies its sole DrawGeometry packet:
DrawingBrush 4898 (type 81), PathGeometry 4899 (type 73), no pen, ten segments,
one contour, bounds 0,0–11,11, under per-point guidelines. The path has supported
guideline geometry; `append_path_tile_brush` rejects its sampled brush through
`resolve_uniform_tile_guidelines`. It is not an input-index compiler failure.
Temporary command/resource probes were removed after locating the path.

## Shared scope prerequisite

`semantic_scene_builder::save` now has an explicit `render_only` mode complementary
to its existing `input_only` mode. A producer can retain actual source path
commands in the input-only scope and separately retain coverage/material commands
in a render-only scope. This avoids inventing a rectangular source hit envelope,
including DrawingBrush internals in input, or classifying geometric coverage as
an alpha-only source opacity mask.

Render-only ranges are balanced save/restore metadata owned by the existing
builder. Native input capture skips their complete interiors without changing
the inherited clip/query state. Explicit owner boundaries remain independent;
this is not an owner-identity override. Raster serialization retains their
commands and ordinary validation still applies. Nested ranges are covered by
their enclosing range; reset clears all metadata. Input-only plus render-only,
or render-only plus a source rectangle annotation, is rejected as contradictory.
No C wire ABI, shader or WPF-local input implementation is added.

The capture walk adds O(C + R) work for C commands and R ordered ranges, with
O(R) retained metadata. Its dependent boundary traversal is not a SIMD pixel
kernel. Existing raster/geometry SIMD implementations remain authoritative.

Both providers compile on macOS ARM64; the native MIL regression passes for an
actual triangular source path, nested isolated material, following source owner,
raster serialization and reset. The full native contract verifier passes.

## Required next connection

This prerequisite alone does not admit the failing DrawingBrush. Connect original
path input, canonical snapped path coverage and the existing tile material under
separate scopes. Preserve original brush-relative mapping while providing enough
paint support for displaced edges; do not change mapping bounds to allocation
bounds. Inherited clips, nested brush ownership and source pen replay remain
independent. Keep boolean/path families outside existing executor support
explicitly rejected. Reuse the image coverage machinery rather than add a second
deformation implementation. Then rerun the unchanged external application.

Final exact-package, platform/VM, pixel/performance and CI qualification remain
required. The application is still blocked at the identified DrawingBrush path.
