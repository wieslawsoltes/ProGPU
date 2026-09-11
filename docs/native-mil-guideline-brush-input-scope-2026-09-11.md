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

## Sampled path connection

The sampled-path producer now records original path input in the input-only
scope, reuses the image helper's canonical snapped path coverage, and paints the
existing tile material in the render-only scope. Paint support expands by two
physical pixels projected back through the inverse transform; the original use
bounds still own viewport, viewbox and relative brush mapping. Inherited clips,
nested brush ownership and source pen replay remain independent. Per-point
boolean programs outside existing executor support remain explicitly rejected.
There is no second deformation algorithm or new CPU pixel kernel.

Both native providers compile. Native MIL regressions pass for bitmap,
DrawingBrush, DrawingImage and VisualBrush path fills at fractional DPI, with
and without an inherited source rectangle: only the actual unsnapped source path belongs
to the native input owner, while rendering retains picture coverage.

Correction to the initial checkpoint: CI exposed missing namespace qualifications
in the new test. Its initial local CTest invocation had run an older executable
after an unconfirmed rebuild, so that result did not cover this regression.
After explicit qualifications and a successful rebuild, the native MIL suite
passes in 0.64 seconds. The clipped fixture now asserts a genuinely intersecting
source rectangle (right edge 10 rather than the path's original 14) and four
retained clip segments. The attempted ellipse clip remains unsupported by native
input-index compilation even though rendering without an index succeeds; it is
an explicit remaining curved-clip contract, not qualified by the rectangle test.
GCC and Linux ARM64 CI must rerun on this correction.

The diagnostic external application gets beyond its previous unsupported
DrawingBrush exception. It remains live without a success marker. A two-second
process sample shows native rendering, Metal command-buffer semaphore waits
and picture-mask preparation; this is not evidence of completed presentation,
input validation or acceptable performance. Keep that same run alive while
investigating completion. Its locally substituted binaries are diagnostic, not
exact-package qualification.

Final exact-package, platform/VM, pixel/performance and CI qualification remain
required. Live application completion remains the immediate merge blocker.
