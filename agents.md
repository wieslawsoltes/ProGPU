# Agent Guidelines & Reference Handbook (agents.md)

Welcome, agent! This document serves as a specialized developer guide and architectural handbook for AI coding agents pair-programming on the **ProGPU** codebase. Read this document carefully to align with our established design patterns, mathematical conventions, and diagnostic tools.

---

## 1. Core Architectural Rules & Conventions

### A0. Reflection-Free WPF Port Support
When adding ProGPU APIs for the WPF port, keep hot paths typed and source-integrated. Runtime reflection is allowed only for diagnostics, compatibility probes, or transitional adapters with a documented removal path; rendering, text, image upload, clipping, hit testing, shader effects, DirectX shims, cache metadata, and platform services should be implemented as reusable ProGPU/Silk.NET primitives or neutral DTO contracts instead of WPF bridge workarounds.

Cross-assembly WPF bridge contracts must not expose shim-owned WPF structs or classes when package-mode apps load the real WPF transport assemblies. Prefer primitive values, package-neutral DTOs, typed registrars, and source-integrated WPF interfaces such as the portable geometry, brush, pen, effect, bitmap-effect input, shader-effect sampler kind/image-source metadata, drawing-content, render-data, invalidation, visual-state, visual-bounds, visual-layout, and bitmap-source pixel seams.

### A. Rendering Quality & DPI-Aware Text Snapping
ProGPU achieves high-performance vector graphics and text rendering matching macOS Retina quality. When modifying or extending text visuals (`TextVisual.cs`, `Compositor.cs`, `GlyphAtlas.cs`):
* **Framebacks**: Always back the swapchain with physical framebuffer pixels (`FramebufferSize`) rather than logical window coordinates to prevent OS-level linear stretching.
* **Glyph Atlas scale**: Rasterize glyphs into the atlas at their actual physical dimensions by applying the current `DpiScale`.
* **4-Way Subpixel Snapping**: Snapped positions must be calculated in physical coordinates snapped to 1/4th of a physical pixel, then snap-backed (divided by `DpiScale`) for logical projection matrix orthographic drawing.

### B. Precise Winding Intersection Rules (Anti-Artifacting)
When modifying GPGPU compute rasterizers (`Shaders.cs`):
* **Scanline Boundary Crossings**: To prevent horizontal line artifacts and drop-out seams at transition vertices (e.g. line-to-curve, curve-to-line, or curve-to-curve), the ray-casting algorithm must use **Precise Direction-Aware Half-Open Winding Intervals** based on the vertical derivative sign (`deriv_y`) at the intersection point:
  * **Upward Crossing (`deriv_y > 0.0`)**: Use the half-open interval `[0.0, 1.0)` (inclusive of start, exclusive of end).
  * **Downward Crossing (`deriv_y < 0.0`)**: Use the half-open interval `(0.0, 1.0]` (exclusive of start, inclusive of end).
* Apply this rule consistently across both `GlyphRasterizerShader` and `PathRasterizerShader` for all quadratic and cubic segments to avoid boundary vertex double-counting or zero-counting.

### C. WinUI Dynamic Theming & Styling
When creating or refactoring UI controls:
* **No Static Color Brushes**: Never instantiate solid brushes statically or assign them directly via static references in layout constructors (e.g., `new SolidColorBrush(...)` or `ThemeManager.GetBrush(...)`).
* **Dynamic Brushes**: Always bind brushes dynamically using `new ThemeResourceBrush("BrushKey")`. This registers the resource with the control's theme map (`_localThemeResources` / `_styleThemeResources`) so they automatically invalidate and re-resolve against the palette during theme switches (Light/Dark).
* **Property Invalidation**: Ensure visual elements invalidate both their measure/arrange states and visual caches on theme updates to force high-DPI assets and colors to repaint.

### D. Mandatory Shader Source and Complexity Contract

Static production shader source must be reusable, reviewable source code rather than a C# string literal.

* Put each fixed WGSL, GLSL, or HLSL module or composition template in its owning project's `Shaders/` directory, using the proper `.wgsl`, `.glsl`, or `.hlsl` extension. Keep one logical module or template per file.
* Load embedded shader source through `ProGPU.Backend.ShaderResource` into a `static readonly string`. The shared build glob in `Directory.Build.props` embeds the files, and the loader caches UTF-8 decoding by assembly and filename. Never open a manifest stream, read a shader file, concatenate fixed helpers, or decode source in a frame, draw, dispatch, atlas, or pipeline-cache hot path.
* Every shader file must begin with meaningful `// Algorithm:`, `// Time complexity:`, and `// Space complexity:` lines. Describe the actual algorithm, define symbols such as segment count or kernel radius, state average and worst-case behavior when they differ, and include material private-storage, output-storage, texture-sample, or bandwidth costs.
* Document fixed loop bounds, workgroup dimensions, numerical assumptions, alpha conventions, and quality-sensitive approximations next to the relevant shader code. Complexity documentation must be updated when an algorithm, sample footprint, loop bound, or storage layout changes.
* Runtime generation is allowed only when the output structurally depends on caller input, such as HLSL translation, dynamic WPF sampler declarations, or user ShaderToy code. Move every fixed prefix, wrapper, and helper used by that generator to a shader resource, cache it, and document the generated algorithm and cost model. Do not use generation as an exception for ordinary fixed shaders.
* Small test-only shader literals may remain inline when they are the direct input under test. Reusable fixtures and all production modules belong in shader files.
* Run `ShaderResourceTests` after adding or moving a shader. Its source audit rejects new inline production stage modules and verifies that every resource is embedded and carries the required algorithm and complexity contract.

---

## 2. Rendering Performance Regression Contract

Rendering performance and pixel quality are one contract. A change is not acceptable when it improves FPS by bypassing required invalidation, lowering raster quality, changing DPI/subpixel behavior, or silently dropping dynamic content.

### A. Preserve Compiled-Scene Correctness

When editing `Compositor`, `Visual`, atlases, effects, layers, or host frame code:

* Every mutation that can affect pixels must invalidate the owning visual and propagate `ChangeVersion` to the compiled-scene root. Never mutate retained command data behind the cache without invalidation.
* Keep cache validation sensitive to logical/physical target dimensions, viewport, DPI, glyph/path atlas generations, tooltips, external layers, and `CacheAsLayer` texture identity/dirty state.
* Increment atlas `Generation` whenever cached UV contents are cleared, moved, or repacked. Do not increment it for a no-op capacity probe.
* Do not make mutable `DrawingVisual`, masks, effects, or dynamic diagnostics cacheable until they have an explicit immutable version contract and regression tests.
* Keep `EnableCompiledSceneCache` and `EnableGpuHitTesting` independently configurable. WinUI uses CPU visual-tree hit testing and must not rebuild the duplicate GPU index by default.
* A compiled-scene hit may skip CPU compilation and uploads, but it must still execute the render pass and current clear/presentation behavior.

Required focused tests live in `LayerRenderTests` and `CompositorReviewRegressionTests`. Cover at least unchanged reuse, visual invalidation, resize/DPI/target invalidation, mutable drawing visuals, atlas reset/repack, and hit-testing option behavior when changing these contracts.

### B. Protect Hot Paths

* Never unconditionally invalidate the sample root each frame. Invalidate only the visual whose content changed.
* Do not update animations twice. `Window.RenderFrameCore` owns the core `UpdateAnimations` call; sample callbacks should update only sample-specific state.
* Keep status/diagnostic text updates rate-limited. Rebuilding `RichTextBlock` inlines every frame defeats both its command cache and whole-scene reuse.
* Preserve `TextRunGlyph.GlyphIndex`; do not repeat character-map lookup during compositor compilation. Cache font table capability flags and hoist transform, raster-size, basis, and bold invariants out of glyph loops.
* Framework backends must record ordinary solid outline text as one retained `DrawGlyphRun` command. Preserve the shaped glyph-index array, convert shaped positions once when the platform glyph run is created, and reuse both arrays across invalidations. Never expand common text into one `DrawPath` command or one path-atlas entry per glyph. Recording must stay O(1) with no glyph-count-dependent allocation; compositor compilation remains O(G) for G glyphs. Gradient brushes and color/bitmap fonts may use their dedicated path or texture fallbacks.
* Classify vector-text coverage once per text command, reusing the already computed transform scale. Physical size must include font size, transform scale, current display DPI, and static-buffer zoom. Keep the calibrated small/large device-pixel policy and transformed-text branch out of per-glyph loops.
* Preserve Skia font-transform separation. `GetGlyphPath`/`GetTextPath` materialize `x' = x * ScaleX + y * SkewX`, while retained fill glyph runs bypass those APIs and carry the same local transform on the command. Never feed an already transformed path through the command shear again. Neither representation may rescale explicit glyph-run positions or shaped advances a second time. Include local stretch and shear in vector glyph cache keys, and use the existing atlas vertex stretch/shear fields instead of allocating transformed position or outline arrays in the common fill-text path. Keep y-up TrueType and y-down SVG color-glyph shear signs explicit and covered by CPU-only tests.
* Preserve `SKFont` encoded-text semantics. Validate an entire UTF-8/16/32 run before producing partial output, count supplementary scalars once, treat glyph-ID buffers as native-endian 16-bit values, and return consumed code units/bytes from `BreakText`. Keep span overloads allocation-free, array overloads single-allocation, caller tails untouched, and undersized outputs guarded instead of reproducing native wrapper overreads. Path fallbacks, intercepts, and `GetGlyphPaths` receive already transformed outlines. Text-on-path must cache repeated glyph paths and morph contour points; do not replace it with a single tangent transform per glyph.
* Preserve legacy `SKPaint` text as a thin adapter over its owned `SKFont` snapshot. Constructor, clone, reset, and `ToFont` must keep snapshots independent; `IsAntialias` and `LcdRenderText` jointly derive font edging. Route every UTF, glyph-ID, pointer, measurement, break, position, width, path, and intercept overload through the existing `SKFont`/`SKTextBlob` engines. Never add a second text decoder or glyph loop. State operations stay `O(1)`, encoded adapters stay `O(N)` with `O(1)` parsing workspace, returned arrays stay `O(G)`, and intercepts stay `O(G * S)` without initializing WebGPU.
* Preserve `SKTypeface` ownership and metadata semantics. Read glyph counts, PostScript names, fixed-pitch state, and SFNT table tags from the parsed font; keep table payloads borrowed internally and copy only at API ownership boundaries. Legacy glyph APIs must delegate to the validated `SKFont` encoding path. Empty typefaces must return empty glyph, metric, and table results rather than leaking the backing sentinel font. Do not expose variable-font clone or axis APIs until `fvar` positions are applied to both outlines and metrics.
* Preserve Skia canvas and stream state semantics. Public canvas save counts are one-based even though the internal stack is zero-based; `Save`/`SaveLayer` return the prior count, `RestoreToCount` clamps at one, and auto-restore is idempotent. Stream peeks and snapshots must preserve cursor position, missing file streams must remain invalid without throwing, and memory-backed stream pointers must stay pinned until disposal. Do not add per-read file I/O to codec or rendering paths.
* Preserve Skia writable-stream encodings and ownership. Fixed-width values are little-endian; packed unsigned values use the one-byte/`0xfe`+16-bit/`0xff`+32-bit format; scalar text is invariant and uses `inf`, `-inf`, and `nan`. Keep primitive writes allocation-free, keep `WriteStream` linear with bounded pooled memory, and zero-fill a short source rather than leaking native uninitialized bytes. Managed writers leave the backing stream open unless ownership is explicit. Dynamic copy operations preserve buffered bytes; detach returns owned data or a seekable stream and resets the writer. Invalid file writers must reject writes without leaving a partial file.
* Preserve document export lifecycle and ownership. Empty or aborted PDF/XPS documents emit zero bytes; managed streams remain caller-owned, path overloads own their writer, close is idempotent, and content rectangles clip the page canvas. Capture each page at most once, and apply raster DPI to both target dimensions and the retained-command transform so logical geometry does not shrink. PDF metadata strings must be escaped before entering the object graph, cross-reference offsets must be computed from final byte positions, and XPS entries must retain stable package paths. Keep export `O(P + B)` time and storage for P captured pixels and B encoded bytes; metadata, options, abort, and empty-document paths remain `O(1)` and CPU-only.
* Preserve `SKData` ownership semantics. Managed data must stay pinned for the lifetime of every shared view, valid subsets must remain zero-copy and outlive their parent, exact-length stream factories must return null after a short read, and external release delegates must run exactly once after the final view is disposed. Keep `Data`, `Span`, `AsSpan`, size, and emptiness queries allocation-free; copy only at APIs whose names or ownership contracts require a copy.
* Preserve `SKBitmap`/`SKPixmap` storage semantics. Bitmap `ByteCount` and pixel spans include row padding between rows but not trailing padding after the final logical row; pixmap `BytesSize` remains tightly packed while its span is stride-aware. Unknown color types may carry non-empty metadata with a null pixel pointer. Failed installs must not replace existing storage, and installed release delegates must run exactly once even when the context is null.
* Keep pixmap operations CPU-only and stride-aware. Subsets intersect source bounds, retain their pixel owner, and remain zero-copy; erase clips partial rectangles and rejects disjoint ones. Pixel reads return unpremultiplied colors, read/scale paths convert destination color and alpha representation, and PNG/JPEG encode must never upload a bitmap or initialize WebGPU. Preserve explicit false/null results for unavailable encoders rather than emitting a mislabeled format. Keep views and pixel queries `O(1)`, read/erase `O(P)`, scaling at the selected kernel complexity, and encoding `O(P + B)` time/storage.
* Preserve transactional bitmap copies and shared subsets. Convert into temporary owned storage and swap only after full success, leaving an existing destination untouched on failure. Subsets retain the original pixel owner and row stride; alpha extraction owns a one-byte mask with four-byte-aligned rows and zero offset. Pixel arrays are row-major and require exactly width times height entries. These paths stay CPU-only: subsets are `O(1)`, validation failures are `O(1)`, and copies, arrays, and alpha extraction are `O(P)`.
* Preserve bitmap shader sampling end to end. Every `ToShader` overload must retain tile modes and local matrix, and sampling must flow from `SKSamplingOptions` through `ImageShaderData` into each tiled `RenderCommand`. Nearest must not become linear; mipmap intent must remain explicit; custom cubic B/C coefficients must reach the texture shader unchanged. Upload the bitmap once per shader, not once per tile. Creation is `O(P)` for P pixels and tiled command recording is `O(T)` for T visible tiles.
* Preserve bitmap decode ownership and alpha semantics. Every file, byte/span, stream, data, and codec overload must converge on the same cached CPU decode result. Invalid stream/data/file factories return null, bounds queries return `SKImageInfo.Empty`, and direct byte/span wrappers retain native SkiaSharp's `ArgumentNullException` after failed codec creation. Default decode changes codec `Unpremul` metadata to a premultiplied bitmap and must not parse the same encoded payload twice or initialize WebGPU.
* Preserve `SKCodec` result, scaling, and state semantics. Missing files return `InternalError`, unknown encodings return `Unimplemented`, and recognized truncated inputs return `IncompleteInput`. Static images report zero animation frames; subset and scanline decode remain explicitly `Unimplemented`. Non-JPEG codecs reject scaled output, while JPEG dimensions use the nearest native eighth-resolution step. Keep the common full-size RGBA/unpremultiplied decode as a direct stride-aware row copy from cached pixels, keep incremental state `O(1)`, and never initialize WebGPU from codec factories, metadata, pixel decode, or failure paths. Factory acquisition is `O(B)` and decode/copy is `O(B + P)` for B encoded bytes and P output pixels.
* Preserve `SKImageInfo` as a pure value contract. Keep platform channel shifts aligned with `PlatformColorType`, keep every `SKColorType` numeric value and byte/bit width aligned with native SkiaSharp, report unknown or invalid types as zero bytes per pixel, use checked arithmetic for 32-bit row/image sizes, retain non-overflowing 64-bit variants, and include color-space identity in equality. Metadata queries must never allocate pixel buffers or initialize WebGPU.
* Preserve `SKRectI` as Skia's exact allocation-free value contract. Point containment and ordinary intersection are half-open, inclusive intersection retains boundary-touch rectangles, and `IsEmpty` means equality with `SKRectI.Empty`, not generic zero area. Keep reversed-extent standardization and inward/outward rounding direction-sensitive, keep floating-point aspect fit/fill centered before flooring edges, and keep overflow checked. Every rectangle operation remains `O(1)` and CPU-only.
* Preserve `SKRect` as Skia's exact allocation-free floating-point value contract. Point containment and ordinary intersection are half-open, inclusive intersection retains boundary-touch rectangles, and `IsEmpty` means equality with `SKRect.Empty`, not generic zero area. Union is the unconditional coordinate envelope and therefore includes the empty rectangle's origin; bounds accumulators must track their first value separately. Keep reversed extents and zero-size aspect resizing native-compatible. Every rectangle operation remains `O(1)` and CPU-only.
* Preserve `SKRoundRect` as Skia's fixed four-corner geometry contract. Keep UL/UR/LR/LL order, native type classification, square-corner clamping, CSS/W3C proportional radius scaling, double-precision side adjustment, ellipse containment, centered collapse on excessive inset, and axis-preserving transform rejection. Transform radii must be derived from mapped absolute anchors and ellipse centers so translation and corner subtraction retain native float-ULP behavior. Public `Radii` returns an independent four-point snapshot; path/canvas hot paths use the internal read-only span. All operations remain fixed-work `O(1)`, CPU-only, and allocation-free except the documented public snapshot and returned transformed object.
* Preserve `SKPoint` and `SKPointI` as Skia's allocation-free point contracts. Keep normalization, distance, arithmetic, offsets, vector conversion, equality, hashing, and formatting `O(1)` and CPU-only. Their native `Reflect` compatibility formula uses the point's squared length rather than a point-normal dot product; do not silently replace it with the conventional vector-reflection formula. Zero-vector normalization retains native NaN or integer-conversion behavior.
* Preserve `SKSize` and `SKSizeI` as Skia's allocation-free size contracts. Constructors and conversions must retain their native directionality; float-to-integer narrowing truncates under checked overflow semantics, including NaN and infinity rejection. Arithmetic, point conversion, equality, hashing, emptiness, and formatting remain `O(1)` and CPU-only.
* Preserve `SKMatrix` as Skia's full row-major 3x3 projective contract. Keep concat order exact, use double-precision inversion cofactors with finite validation, preserve the specialized tiny-scale path, map perspective vectors relative to the mapped origin, and map zero homogeneous `w` to zero. Perspective rectangle bounds must clip against `w >= 1/16384` in fixed stack storage before projection; do not replace this with unbounded corner division or flatten perspective through `Matrix4x4`. Scalar and rectangle operations remain allocation-free `O(1)`; span mapping is `O(N)` with `O(1)` auxiliary storage, and only result-array APIs may allocate `O(N)`.
* Preserve `SKMatrix44` as Skia's contiguous 16-scalar value contract. Keep `SKMatrix` translation/perspective placement, row and column major conversion, index validation, `System.Numerics.Matrix4x4` determinant/inversion/transpose/mapping semantics, and native pre/post concat order exact. Singular inversion returns `Empty`. Scalar, factory, mapping, algebra, inversion, and exact-length span-copy paths remain allocation-free fixed-work `O(1)` operations; only array-returning major-order APIs may allocate one 16-float result. Do not expose shim-only public conversion helpers.
* Preserve `SKColor` and `SKColorF` as Skia's allocation-free color contracts. Integer storage and uint conversion use packed AARRGGBB order. Float-to-byte conversion clamps to `[0, 1]`, maps NaN and negative values to zero, maps positive infinity to 255, and rounds with `value * 255 + 0.5`; byte-to-float conversion divides by 255. Keep the native 0.001 HSL/HSV chroma epsilon, hue wrapping, achromatic branches, and byte-returning factory truncation. Parsing, packing, conversion, channel replacement, equality, hashing, and color arithmetic remain `O(1)` and CPU-only.
* Preserve `SKColorSpaceXyz` and `SKColorSpaceTransferFn` as scalar CPU value contracts. Keep named D50 matrices and seven-parameter transfer definitions byte-compatible with native SkiaSharp. Matrix concat is row-major; inversion uses double-precision cofactors and returns `Empty` for singular or non-finite results. Transfer evaluation must retain signed inputs, `skcms` PQ/HLG markers, bounded fast log/exp behavior, NaN/infinity results, continuity checks, and inverse rejection semantics. Constructors, indexing, concat, inversion, evaluation, equality, and hashing remain allocation-free `O(1)` operations; only `Values` may allocate its fixed-size ownership snapshot.
* Preserve `SKColorSpace` and `SKColorSpaceIccProfile` as CPU-only construction and metadata contracts. Keep sRGB/linear singletons, native near-sRGB/gamma-2.2/linear snapping, gamma and numerical-function classification, CICP enum values, exact D50 matrices and transfer tables, PQ/HLG out-parameter behavior, and null/exception semantics. ICC parsing must validate every declared bound before slicing, copy caller bytes once into stable profile-owned storage, reject non-RGB profiles as color spaces while retaining valid profile metadata, and compare all three channel TRCs. Parametric curves stay `O(1)`; sampled tables are accepted only when all channels are close enough to sRGB and may perform one bounded `O(S)` comparison with `O(1)` workspace at profile construction, never during a frame, draw, texture conversion, or color-table lookup. Profile parsing remains `O(T + B)` time and `O(B)` owned storage for T tags and B bytes; all color-space queries remain `O(1)` and must not initialize WebGPU.
* Preserve Skia fake-bold geometry. `SKFont.Embolden` uses a `Size / 32` mitered stroke-and-fill path, with generated stroke contours normalized to the dominant outer glyph winding so overlap cannot cancel the original interior. Positive fractional `SKPaint.GetFillPath` stroke widths must remain fractional; a zero-width device hairline has no device-independent fill path. Keep emboldened text on its widened-path quality path and ordinary text on the glyph-run fast path.
* Preserve `SKTextBlob` intercept semantics. Return at most one pair per intersecting positioned glyph in run order, do not merge overlapping glyph pairs, skip complete RSXform runs, apply font scale/skew/embolden and y placement, keep band point tests strict, and let span overloads truncate without touching the remaining destination. Quadratic and cubic band crossings must use bounded analytic roots with constant workspace; intercept queries must not initialize WebGPU or invoke GPU path operations.
* Preserve `SKCubicResampler` as SkiaSharp's immutable two-float value contract with read-only B/C properties, native float equality and hashing, and exact Mitchell/Catmull-Rom constants. Retain both coefficients through `RenderCommand` and texture vertices. Keep the Catmull-Rom fast path pixel-identical; do not collapse Mitchell and custom resamplers into a generic cubic mode. Value construction, reads, comparison, and hashing remain allocation-free `O(1)` CPU work.
* Preserve Skia pixel-center and alpha-representation semantics in `SKBitmap.Resize`. Keep nearest sampling free of source-sized temporary allocations and retain raw same-format texel copies; linear and cubic paths must remain stride-aware, and cubic must evaluate the requested B/C kernel rather than a generic bicubic approximation.
* Preserve the allocation-free double-queue dispatcher. Do not copy the pending queue into a new `List<Action>` per frame, execute newly posted work recursively in the same drain, or enqueue one delegate per benchmark element.
* Keep producer backpressure bounded. The LOL/s workload batches element mutations and limits pending work separately for VSync and uncapped runs so background production cannot starve rendering.
* Before reserving atlas capacity, prove that the requested entries fit in an empty atlas. An impossible reservation must not reset a useful atlas every frame.
* Never clear or repack the glyph atlas automatically while retained scenes or static buffers can reference its UVs. Capacity exhaustion must preserve every existing coordinate and generation, then render an uncached outline through the vector fallback. Tests must sample a color channel against the clear color; opaque target alpha cannot prove that a glyph was drawn.
* Glyph rasterization batches are nestable. Keep the normal compile path to one queue submission, but flush before the 256-byte-aligned uniform ring wraps; never reuse a uniform offset while an unsubmitted command encoder still references it. For G newly rasterized glyphs and ring capacity R, submission count is O(ceil(G/R)), raster work is O(total glyph pixels), and transient bind-group storage is O(min(G, R)).

### C. Required Performance and Quality Gates

Run the full renderer gates from the ProGPU repository root:

```bash
dotnet test src/ProGPU.Tests/ProGPU.Tests.csproj -c Release
dotnet test src/ProGPU.Tests.Headless/ProGPU.Tests.Headless.csproj -c Release
```

The current baseline is 1,370 renderer tests and 149 headless tests. Update these counts only when tests are intentionally added or removed.

Build once, then measure the exact final binaries:

```bash
dotnet build src/ProGPU.Samples/ProGPU.Samples.csproj -c Release

PROGPU_SAMPLE_BENCHMARK_PAGE='LOL/s Benchmark' \
PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES=240 \
PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES=480 \
PROGPU_SAMPLE_BENCHMARK_VSYNC=true \
dotnet run --project src/ProGPU.Samples/ProGPU.Samples.csproj -c Release --no-build

PROGPU_SAMPLE_BENCHMARK_PAGE='LOL/s Benchmark' \
PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES=300 \
PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES=600 \
PROGPU_SAMPLE_BENCHMARK_VSYNC=false \
dotnet run --project src/ProGPU.Samples/ProGPU.Samples.csproj -c Release --no-build
```

On the current 120 Hz reference machine, the protected targets are about 120 FPS and 12,000 LOL/s with VSync, and at least 36,000 LOL/s uncapped. The current measured values are 12,001 and 40,429 LOL/s. Compare on the same machine and window state; investigate a repeatable drop greater than 10% before merging.

Also run `Markdown Playground` and `DXF CAD Viewer` uncapped. Stable pages should miss once and then report nearly all `sceneCacheHits`; animated pages and LOL/s should report a useful miss reason rather than a false hit.

Validate Svg.Skia from its repository with `UseProGpuSkiaSharpShim=true`:

```bash
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -f net10.0 -c Release \
  -p:UseProGpuSkiaSharpShim=true -p:ProGpuSourceRoot=/absolute/path/to/ProGPU \
  --filter 'FullyQualifiedName~resvgTests'

dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -f net10.0 -c Release --no-restore \
  -p:UseProGpuSkiaSharpShim=true -p:ProGpuSourceRoot=/absolute/path/to/ProGPU \
  --filter 'FullyQualifiedName!~W3CTestSuiteTests&FullyQualifiedName!~resvgTests'
```

The current shim baseline is 927 resvg passes with 37 explicit inventory skips and 1,147 remaining passes. The W3C image lane has 47 established threshold differences, 483 passes, and 3 skips versus native SkiaSharp's 530 passes and 3 skips. Performance-only work must not add a fixture, increase an image error, or change a previously matching result. Compare the exact fixture/error list against the parent commit; intentional parity improvements should reduce the difference set and include their own image-focused review.

Record benchmark result lines and test totals in the commit or task summary. Do not claim a performance fix from subjective interaction alone.

---

## 3. Text Outline Diagnostic Tool (`TtfDiag`)

To debug text rendering quality, glyph geometry errors, or parsing inconsistencies, we maintain a generic command-line outline extractor tool under `tools/TtfDiag/`.

### How It Works
The tool loads any standard TrueType Font (`.ttf`) file, decodes its index tables, parses the outline contours using `TtfFont` parsing loops, and dumps the exact figure coordinates, segment types (Lines/Quadratic Beziers), endpoint coordinates, and control points in clean `CultureInfo.InvariantCulture` formatting.

### How to Run
Execute the tool from the root directory using the `dotnet` CLI:
```bash
# Run with macOS system Arial fallback to inspect specific glyphs (e.g., 'G' and 'g')
dotnet run --project tools/TtfDiag -- Arial Gg

# Run with an absolute path to a custom font and custom character sequence
dotnet run --project tools/TtfDiag -- /System/Library/Fonts/Supplemental/Georgia.ttf ABC
```

### Typical Output Format
Use the precise printed segments to check if coordinate coordinates contain anomalies, bounds issues, or flat segment joints:
```text
[TtfDiag] Loading font: /System/Library/Fonts/Supplemental/Arial.ttf
[TtfDiag] Units per Em: 2048, Total Glyphs: 3381

================================================================================
Glyph: 'G' | Unicode: U+0047 | Glyph Index: 42
================================================================================
Figures count: 1
  Figure 0: StartPoint = (844, 575), Closed = True, Filled = True
    Segments count: 29
      Segment 00: [Line]  -> End: (844, 747)
      Segment 03: [Quad]  -> End: (1170, 32.5) | Ctrl: (1322, 90)
```

Use this tool immediately whenever user reports "jagged glyph edges", "horizontal/vertical line artifacts crossing characters", or "weird transformations".
