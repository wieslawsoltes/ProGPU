# Original four-corner rounded rectangles

`vector::RoundRect` ports the original `SKRoundRect` value contract: all six native
classifications, complete corner snapshots, finite/standardized rectangle admission,
simple and nine-patch factories, independent four-corner normalization, circular
tolerance, validity, containment, inset/outset, translation and axis-preserving
affine transformations. Fixed `[f32; 2]` corner pairs replace four-field mutable
managed storage; all value operations remain allocation-free and CPU-only.

The original fast fitting classification precedes double-precision proportional
scaling. Ordered side flush/adjust and next-representable decrement retain the
original dependency order. Square-corner collapse, original floating half-widths,
empty origins, centered excessive inset and explicit invalid offset metadata stay
separate. Containment retains the original multiplied ellipse equation; transformed
radii use mapped absolute contour anchors and subsequent subtraction, preserving
the source's translation-sensitive arithmetic rather than multiplying radii alone.
Independent point/component products use SIMD; ordered corner decisions and side
adjustments remain sequential. Default-runtime float/FMA behavior is unqualified.

`to_geometry` connects the value to original `SKPath.AppendRoundRect` line/arc
construction, with explicit 0..7 start slots, both directions, independent final
implicit closure and the original second float normalization. This produces
analytic `PathGeometry` consumed by existing paint/stroke/clip/effect lowering;
it does not approximate the source with a tessellated rounded-rectangle mask.
The separate existing `RoundedRectangleContour` proof remains unchanged.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPath.cs` | `98f474ac8801e7d7b5ceed338191b979753babe0bb5b0aa826a8d1553f7cd660` |
| `src/SkiaSharp/SKPath.Compatibility.cs` | `f350ef96db62afc27bfa5da100f597f3aaefa8a3faefa0c05463d4ad3b633fa8` |
| `src/SkiaSharp/SKRect.cs` | `ec297b0e18f3a1519106292f715bd65429ae495a22aee329e1e2a843ec6ba3a6` |

Source formatting only. No compiler, lint, tests, shader/browser execution,
performance measurements or generated artifacts. Full source numerical/error
controls and provider/package/application pixels remain deferred under the
mandatory implementation-first workflow. Remaining general legacy path-builder
and canvas/paint adapters are separate production scope.
