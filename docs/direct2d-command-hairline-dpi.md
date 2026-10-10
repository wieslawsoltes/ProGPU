# Direct2D command-stream hairline DPI

The Windows command sink owns a copy of the actual target's two DPI values,
alongside its DIP extent. `create_for_target` copies the immutable descriptor;
surface-backed command-list translation reads the original context DPI while
holding the surface access lock. No caller memory or mutable context is retained
by the sink. Source command coordinates remain DIPs: pixel-unit callbacks are
still rejected.

Hairline intervals and dash offset use the same owned SIMD implementation as the
portable target: each physical length is multiplied by `96 / actualDpi` once.
Hairline bounds use the original portable per-axis float arithmetic. Normal and
fixed strokes retain their existing width, transform and targetless behavior.
Unknown target DPI and unequal-axis hairlines fail explicitly before geometry or
brush publication; a failed hairline cannot fall back to DPI-unaware `Widen`.

Original SDK bounds queries omit hollow figures even though they contribute
strokes. The command adapter therefore queries a separate bounds-only SDK path
from its captured original lines, cubics and figure closures. Filled flags on
that temporary query expose each centerline; the retained drawing keeps all
original hollow/closed, gap, dash and smooth-join metadata. The SDK still computes
actual curve extrema in the requested frame before the existing pen/DPI padding.
No control-point envelope, widened dash bound or fabricated inverse replaces it.
An original SDK probe reproduces the hollow-query failure, while eight exact
literal and failed-output controls pass for the private helper on Windows ARM64.
The complete command-sink fixture and cross-platform pipeline remain required.

`HasTargetDependentStrokes` (bit 11) reports retained hairline geometry separately
from `HasTargetDependentMasks`. The C constant feeds the generated native managed
contract; the public result preserves that flag. A new DPI requires a new
recorder/scene generation, not reuse of the converted dash lengths. Full-target
Clear removes the dependency with the discarded scene, without resetting the
original translated-draw/callback accounting. Existing descriptor and result
layouts are unchanged.

Implementation provenance is ProGPU `77b60ae78c416851858527bf355a947a8752ee25`,
`progpu_native_direct2d_render_target.cpp::scale_hairline_dashes` and the adjoining
device-stroke bounds. This is a shared source-adapter correction, not a new
rasterizer, managed rendering algorithm, pipeline or fallback. Both native
providers and managed rendering consume the unchanged semantic scene/shaders.

Primary contracts: [stroke transform types](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_stroke_transform_type)
separate transformed geometry from the one-device-pixel pen;
[dash arrays](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1strokestyle-getdashes)
retain stroke-relative lengths; [target DPI](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-setdpi)
owns the DIP/pixel mapping. The existing cross-engine architectural comparison
in `DIRECT2D_WIN2D_COMPATIBILITY.md`, “Portable stroke-transform parity”, remains
applicable: retained source geometry, no per-dash submission, no text/cache change.

Authored Windows source controls cover 18 explicit-target combinations
(96/192/384 DPI, normal/fixed/hairline, line/cubic), two targetless normal/fixed
controls, three failed-frame atomicity controls, full-target Clear accounting,
and two actual command-list/surface-DPI generations. They read literal scene
bounds and every odd dash interval/phase, mutate the caller descriptor after
creation, and read back unchanged original SDK style values. Prior positive
hairline tests now supply explicit 96-DPI metadata; their assertions remain.

The paired pixel inventory is four configurations: 96/192 DPI times line or
collinear cubic. Each contains four independently colored, one-physical-pixel
bands with flat/square caps, zero/nonzero phase and ignored requested widths.
The original Windows path owns genuine SDK hairline styles and compares every
BGRA byte against independently authored physical rectangles. Both native
providers replay the corresponding portable scenes cold, warm and as independent
rectangles, comparing every RGBA byte. Four source commands remain distinct in
the scene; compatible draws merge to one GPU draw and one submission per replay.
No product dash walker or captured output generates the rectangle oracle.

The integrated original SDK control measured one additional square-cap pixel at
the exact final dash boundary, for both line and cubic paths at both DPIs on
Windows ARM64 and x64. The literal square-band oracle now includes that terminal
zero-length dash. The shared native polyline walker emits its incoming dash cap
and original source-end cap, matching the existing curve walker. Its capacity,
render and source-input consumers use the same walk; closed seams are unchanged.

The 192-DPI independent fill exposed a separate path-atlas bug: path raster scale
and translation phase omitted target DPI. They now use physical units, matching
the existing clip path implementation. Final quads retain logical coordinates,
and the unit-DPI-only exact-load gate remains unchanged. This fixes raster input
resolution rather than changing filtering or accepting approximate pixels.

This source-correctness change also includes the separately scoped
[WIC interface ownership correction](direct2d-wic-source-query.md): a successful
query without its interface cannot publish successful bitmap creation.

The original SDK four-configuration pixel control ran in the local Windows VM
on both architectures. The local native provider passed all four configurations,
including complete cold/warm/independent RGBA comparisons, after these repairs.
The complete Windows, second-provider, package and application gates remain open.
