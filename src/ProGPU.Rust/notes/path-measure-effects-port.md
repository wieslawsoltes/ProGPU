# Original path measurement and effects

`vector::PathMeasure` owns raw Move/Line/Quad/Conic/Cubic/Close input and the original per-contour sample evidence. It retains the sqrt precision schedule, degenerate-sample omission, binary-search distance/parameter selection, normalized tangents, matrix flags, equal-range output and analytic de Casteljau/homogeneous slicing. Bounded thread-local reuse preserves the original 64-contour / 2,048-segment / 8,192-sample retention ceilings. Cumulative lengths/control flow remain sequential; independent vector and homogeneous components use SIMD. Measurement samples never become painted approximations.

All nine `PathEffectKind` families are connected: dash, trim, corner, discrete, 1D path, 2D line, 2D path, Compose and Sum. Retained stamps and child graphs are immutable. Original phases, sequential xorshift state, per-contour/generated-element guards, ordered result composition and distinct failure outputs are preserved. Paint style/width metadata stays independent, including composition precedence. Lattice operations retain their original matrix inverse/map, measured scan inventory and source fill query with bounds fallback and inverse-fill policy. The small dash inventory remains inline through four slots.

The raw-verb surface and typed `PathGeometry` bridge are distinct. Native analytic arcs, rational cubics and deferred Boolean geometry still require the original public raw-iterator conversion and reject at that bridge; they are not flattened or relabeled as equivalent. Metadata-bearing original corner fallback figures and Sum outputs retain their typed geometry owners. Full legacy path-builder/iterator and paint-object integration remain ongoing; independent original runtime vector/float.Lerp, transcendental, large conversion and error behavior still require qualification.

The shared desktop/browser `--path-effects` / `?scene=path-effects` sample authors twelve panels covering every family, all 1D stamp policies and trim complement. Source formatting only; no compilation, tests, shader/browser execution or performance validation. No generated build artifacts.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPathMeasure.cs` | `88f0113d654314714d8be21282ff802cd2245fbc3c39e632a38b9985d86c0785` |
| `src/SkiaSharp/SKPathEffect.cs` | `7e8a3cece101c14c7c9c97a7275b08b628e2adaac19d5f6af1fe4259a302b27c` |
| `src/SkiaSharp/SKPathEffect.Apply.cs` | `070a1cb5dac09398cc6ee57ff2ffe173a966c6d24e741582fa759502c08b175b` |
| `src/SkiaSharp/SKMatrix.cs` | `a1a0fb70cb138510b7407bf41de5b3d88fb6e3f34bde01dd948d1128268b54e3` |
| `src/SkiaSharp/SKPath.cs` | `98f474ac8801e7d7b5ceed338191b979753babe0bb5b0aa826a8d1553f7cd660` |
