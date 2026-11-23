# Native recorded source hit capture

`NativeSceneBuilder::add_recorded_hit_test_index(opacity_mode)` ports the original
native retained-command producer into `builder/hit_capture.rs` and six children.
It consumes the actual builder resources, owner boundaries, ink rectangles,
point/region scopes, render-only ranges and explicitly admitted source layers.
The completed primitive/path/node/index arrays enter the existing direct index
producer without a serialize/parse round trip or topology reconstruction.

Implemented source routes include analytic rectangles/ellipses, original path
segments, canonical full-ellipse arc strokes, line and polynomial curve strokes,
connected native joins, complete dashed-polyline traversal, image/patch source
rectangles and the original glyph-run ink rectangle. Native cap/join triangles
preserve MiterOrBevel, clipped-miter and WPF reversal policies, local-affine versus
world-conformal frames, source endpoint caps and closed seams. Dash scratch is
typed, allocated only when needed, and reused across the full capture.

The source mask/clip cache retains actual transformed polynomial controls and
admits rectangle specialization only after the original four-edge topology
proof. Nonredundant rectangle/nonrectangular intersections keep their original
rejection. Bounds remain broad phase. Source opacity mode, ownership changes,
query participation, singular rectangle frames, storage-clear skipping and
source cache-frame mapping preserve their independent original policies.

Four-lane transforms, subtraction/squaring and clip intersection use safe
`wide::f32x4` calls with the original separate multiply/add order. The original
native NEON/SSE2 architecture admission remains explicit; this does not enable
the source preprocessing route on Wasm or unqualified scalar architectures.
The existing pinned dependency is used as a SIMD API only. No foreign geometry,
shaper or hit-test implementation is copied.

The native quadtree preserves the original default depth 8 / leaf threshold 32,
primitive order, fixed child order, first-containing-child admission, bounded
capacity estimate, leaf collapse, and empty root. Input source records are
captured once, then the completed index publishes atomically. Allocation failures
remain separate from unsupported input and capacity failures. No unsupported
command is approximated by its culling/raster envelope.

Original sources and SHA-256, relative to `src/ProGPU.Native/src`:

| Source | SHA-256 |
|---|---|
| `Scene/Builder/progpu_native_scene_builder_hit_test_capture.cpp` | `75c6f81fec88fda5f3c08d514d9516d5d4e72233fd967867711619f7b413c8d4` |
| `HitTesting/progpu_native_hit_testing.cpp` | `b20379dca240100ff2ba7eeaacddaca848459d821c2a987fcf15fddb77582008` |
| `Backend/progpu_native_geometry_dash.hpp` | `5428174d631ee418b246c83c3e1d0210bc887a3379978a61b1e7ca998132b411` |
| `Backend/progpu_native_geometry_stroke.hpp` | `3dbf20d4d9d9fbd9e37e7449cf4ab837c39c8dff8e0704190e49de5dc2a21da4` |
| `Backend/progpu_native_geometry_base.hpp` | `85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672` |

The existing DESIGN.md architecture references continue to govern reusable CPU
source preparation. Only source formatting was performed. Compilation, complete
original stream/query controls, browser execution, timing and platform/source/
package qualification remain deferred under the implementation-first order.
