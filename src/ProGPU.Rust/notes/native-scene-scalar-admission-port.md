# Native scene scalar admission

`native_scene/semantic/values.rs` and its six children port the original native
scalar admission functions. They consume safe raw wire records rather than
managed drawing constructors. The semantic resource/command parser selects these
functions at the original call sites; their availability does not add eager
validation to ordinary analytic/path/image resources that the original scene
parser deliberately leaves to execution.

The port retains the original analytic scale calculation, geometry-specific early
returns, source stroke flags, rational weights, 63-instruction/16-slot Boolean
program, signed winding rules, glyph phase/grid/contrast policy, coverage padding
and row alignment, image address/sampling/alpha policy, picture presentation
suffix, independent layer/cache flags, and complete camera/light/mesh scalar
gates. Coverage dimensions preserve the source float operations before widening;
raw unsigned image-size arithmetic preserves the source wrapping operation.
Picture descriptor and presentation outputs publish together through `Option`.
No source raster policy, source ownership or producer capability is inferred.

Original sources and SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp` | `3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32` |
| `src/ProGPU.Native/src/Scene/progpu_native_path_boolean_validation.hpp` | `c050de5041ea92350f99e94dcedcc542e9eb539a9cfd9ea74c2b99dd4f3384eb` |
| `src/ProGPU.Native/src/Scene/progpu_native_glyph_coverage_transfer.hpp` | `1b72f7adcbe5d59f3306f0825817823343f64811ca982abdbb1ffd1673b7419f` |
| `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp` | `85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672` |
| `src/ProGPU.Native/src/Backend/progpu_native_geometry_analytic.hpp` | `894710d60ddddca102b3e5e4ff0c1ee49d166406c67b8550beeaf83796a43ab2` |
| `src/ProGPU.Native/src/Backend/progpu_native_geometry_stroke.hpp` | `3dbf20d4d9d9fbd9e37e7449cf4ab837c39c8dff8e0704190e49de5dc2a21da4` |

The existing architecture references in `../DESIGN.md` continue to govern CPU
preparation and immutable ownership. No external engine implementation is copied.
Source formatting only: compilation, tests, original stream comparisons, GPU
execution and platform/source/package qualification remain deferred under the
user's implementation-first instruction.
