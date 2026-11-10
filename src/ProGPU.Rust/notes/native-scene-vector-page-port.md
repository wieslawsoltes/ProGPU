# Native retained vector page

`NativeVectorPage::compile(scene, frames, materials, antialias_padding_pixels)`
owns the admitted stream, immutable material generation, actual ordered command
frames, canonical 56-byte vertices and 32-bit indices. The selected frames must
belong to the same scene and remain strictly ordered. It ports all six original
packed-vector families: analytic, geometry, point batch, vertex mesh, explicit
physical source coverage, and polyline/spline stroke batch.

The complete page is preflighted against original vertex/index/draw budgets before
output emission. Actual bytes and preflight upper bounds remain separate metrics;
zero-output commands retain their ordered draw record. Mesh and stroke resource
layout admission is shared with their builder through `semantic/vertex_mesh.rs`
and `semantic/stroke.rs`. Safe little-endian decoding uses temporary typed arrays
instead of native pointer reinterpretation; no GPU/provider callbacks occur.

Primitive production preserves native direct line endpoint descriptors ±1/±2,
curve descriptor global vertex ordinals, direct 24-section curves, original
24–1024 affine subdivision, arc tangents, independent cap and join topology,
WPF reversal/clipped miter/MiterOrBevel policies, aliased markers and coverage
ownership. Dashed capacity and paint share one bounded-source traversal with the
original float distance progression, odd pattern expansion, double source
intervals, endpoint zero-length dash and closed-seam ordering. NURBS retains the
original target extent thresholds, first applicable knot span, float homogeneous
de Boor work and invalid-domain control-polyline route. Collapsed source stroke
transforms keep their original early-out before widening admission.

Physical source-coverage meshes share the source path placement admission and
require full target containment before clipping. Their actual triangle metadata
and source UVs remain intact; independent XY SIMD additions preserve source
operation order. Per-draw indices reference the complete immutable vertex page,
so GPU encoding must bind the full page with base vertex zero. Target scissor,
mask, blend, alpha policy, submission ownership and renderer cache identity stay
with the actual command frames and the separate GPU adapter.

An integration metadata correction retains the initial source state cursor DPI
on every `NativeCommandFrame`, independently of nested target presentation DPI.
This supplies the original frame for per-point path guideline deformation.

Implementation only: no build, test, browser, benchmark or qualification run was
performed. GPU adapter integration is separate; this page does not claim source,
provider, native-package or pixel qualification. Canonical shaders are unchanged.

Original SHA-256 inventory:

| Source | SHA-256 |
| --- | --- |
| `Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `Scene/progpu_native_semantic_stroke.hpp` | `65137f961b2250456dcf0d5a61fb1896f657d7c9f52c8f9f6f35a610933ca082` |
| `Backend/progpu_native_geometry_analytic.hpp` | `894710d60ddddca102b3e5e4ff0c1ee49d166406c67b8550beeaf83796a43ab2` |
| `Backend/progpu_native_geometry_base.hpp` | `85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672` |
| `Backend/progpu_native_geometry_stroke.hpp` | `3dbf20d4d9d9fbd9e37e7449cf4ab837c39c8dff8e0704190e49de5dc2a21da4` |
| `Backend/progpu_native_geometry_dash.hpp` | `5428174d631ee418b246c83c3e1d0210bc887a3379978a61b1e7ca998132b411` |
| `Backend/progpu_native_geometry_spline.hpp` | `71a92abcdcba7ea31dec9cd227eda17878e198738a9fac14f89a741f3d4556d5` |
| `Backend/progpu_native_point_batch.hpp` | `bb3adf546e7bee541b4c8b05c68eaba85b982dfe2576f9138ff70204f1006fcc` |
| `Backend/progpu_native_vertex_mesh.hpp` | `ec6cd5443cfbff74b1261e026e030a0b42728b0bc3199d5a495bfaa09abb3861` |

Paths above are relative to `src/ProGPU.Native/src/`.
