# Original Canvas text replay

Implementation only; compilation, tests, browser execution and performance qualification remain deferred until the port is implemented.

`Canvas::draw_string` retains original nominal rune mapping, sequential selected-font advances, alignment subtraction and immutable positioned arrays. It does not fabricate shaping or fallback. `draw_text_blob` shares ordinary run arrays directly into the existing explicit vector text producer. Rotation-scale runs retain one original glyph slot at zero position under the ordered matrix/command-origin/canvas transform; no combined transform approximation changes placement. The original fast route does not reinterpret font edging or paint antialias as a new text presentation policy.

Shader, non-fill and emboldened blobs use the actual original glyph-path producer, independent placement and full Paint path/effect/stroke handling. The maximum absolute source size and first reflected/skewed font descriptor survive recording. Text-on-path retains both original routes: warped outline controls, or retained rotation-scale midpoint placement on the first contour.

Path raster metadata resolves only after the actual target transform and DPI are known. Source capture and opacity placement preserve the descriptor until compilation. The shared original text gamma calculation does not change Canvas DrawPath's independent defaults: its phase grid is 64, scale quantization is disabled and raster scale is actual target DPI. Direct positioned glyphs retain their separate four-phase/quantized producer. Aliasing retains one raster sample and the original four-sample metadata; antialiased text paths retain eight samples. Original special-shader masks keep their independent ordinary-path policy.

Rust strings supply valid UTF-8 scalar input; malformed UTF-16 source string behavior is not claimed. Existing finite affine Scene admission, independent native/managed atlas ownership, provider, default-runtime arithmetic, pixels, browser, package and performance gates remain open. No shaders, compiler defaults, driver settings or deadlines changed.

Original SHA-256 provenance:

| Source under `src/` | SHA-256 |
| --- | --- |
| `SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `ProGPU.Vector/PathAtlas.cs` | `8b0c65142b0156f505374f1d20a6feff3eea1ed7bec23a1b8ce6f405e7414ccd` |
