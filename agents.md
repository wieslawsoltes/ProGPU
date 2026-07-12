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
* Classify vector-text coverage once per text command, reusing the already computed transform scale. Physical size must include font size, transform scale, current display DPI, and static-buffer zoom. Keep the calibrated small/large device-pixel policy and transformed-text branch out of per-glyph loops.
* Preserve Skia font-transform separation. `GetGlyphPath`/`GetTextPath` materialize `x' = x * ScaleX + y * SkewX`, while retained fill glyph runs bypass those APIs and carry the same local transform on the command. Never feed an already transformed path through the command shear again. Neither representation may rescale explicit glyph-run positions or shaped advances a second time. Include local stretch and shear in vector glyph cache keys, and use the existing atlas vertex stretch/shear fields instead of allocating transformed position or outline arrays in the common fill-text path. Keep y-up TrueType and y-down SVG color-glyph shear signs explicit and covered by CPU-only tests.
* Preserve `SKFont` encoded-text semantics. Validate an entire UTF-8/16/32 run before producing partial output, count supplementary scalars once, treat glyph-ID buffers as native-endian 16-bit values, and return consumed code units/bytes from `BreakText`. Keep span overloads allocation-free, array overloads single-allocation, caller tails untouched, and undersized outputs guarded instead of reproducing native wrapper overreads. Path fallbacks, intercepts, and `GetGlyphPaths` receive already transformed outlines. Text-on-path must cache repeated glyph paths and morph contour points; do not replace it with a single tangent transform per glyph.
* Preserve `SKTypeface` ownership and metadata semantics. Read glyph counts, PostScript names, fixed-pitch state, and SFNT table tags from the parsed font; keep table payloads borrowed internally and copy only at API ownership boundaries. Legacy glyph APIs must delegate to the validated `SKFont` encoding path. Empty typefaces must return empty glyph, metric, and table results rather than leaking the backing sentinel font. Do not expose variable-font clone or axis APIs until `fvar` positions are applied to both outlines and metrics.
* Preserve Skia canvas and stream state semantics. Public canvas save counts are one-based even though the internal stack is zero-based; `Save`/`SaveLayer` return the prior count, `RestoreToCount` clamps at one, and auto-restore is idempotent. Stream peeks and snapshots must preserve cursor position, missing file streams must remain invalid without throwing, and memory-backed stream pointers must stay pinned until disposal. Do not add per-read file I/O to codec or rendering paths.
* Preserve `SKImageInfo` as a pure value contract. Keep platform channel shifts aligned with `PlatformColorType`, report `Unknown` as zero bytes per pixel, use checked arithmetic for 32-bit row/image sizes, retain non-overflowing 64-bit variants, and include color-space identity in equality. Metadata queries must never allocate pixel buffers or initialize WebGPU.
* Preserve Skia fake-bold geometry. `SKFont.Embolden` uses a `Size / 32` mitered stroke-and-fill path, with generated stroke contours normalized to the dominant outer glyph winding so overlap cannot cancel the original interior. Positive fractional `SKPaint.GetFillPath` stroke widths must remain fractional; a zero-width device hairline has no device-independent fill path. Keep emboldened text on its widened-path quality path and ordinary text on the glyph-run fast path.
* Preserve `SKTextBlob` intercept semantics. Return at most one pair per intersecting positioned glyph in run order, do not merge overlapping glyph pairs, skip complete RSXform runs, apply font scale/skew/embolden and y placement, keep band point tests strict, and let span overloads truncate without touching the remaining destination. Quadratic and cubic band crossings must use bounded analytic roots with constant workspace; intercept queries must not initialize WebGPU or invoke GPU path operations.
* Preserve `SKCubicResampler` B/C coefficients from the compatibility API through `RenderCommand` and texture vertices. Keep the Catmull-Rom fast path pixel-identical; do not collapse Mitchell and custom resamplers into a generic cubic mode.
* Preserve Skia pixel-center and alpha-representation semantics in `SKBitmap.Resize`. Keep nearest sampling free of source-sized temporary allocations and retain raw same-format texel copies; linear and cubic paths must remain stride-aware, and cubic must evaluate the requested B/C kernel rather than a generic bicubic approximation.
* Preserve the allocation-free double-queue dispatcher. Do not copy the pending queue into a new `List<Action>` per frame, execute newly posted work recursively in the same drain, or enqueue one delegate per benchmark element.
* Keep producer backpressure bounded. The LOL/s workload batches element mutations and limits pending work separately for VSync and uncapped runs so background production cannot starve rendering.
* Before reserving atlas capacity, prove that the requested entries fit in an empty atlas. An impossible reservation must not reset a useful atlas every frame.

### C. Required Performance and Quality Gates

Run the full renderer gates from the ProGPU repository root:

```bash
dotnet test src/ProGPU.Tests/ProGPU.Tests.csproj -c Release
dotnet test src/ProGPU.Tests.Headless/ProGPU.Tests.Headless.csproj -c Release
```

The current baseline is 1,229 renderer tests and 149 headless tests. Update these counts only when tests are intentionally added or removed.

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

On the current 120 Hz reference machine, the protected targets are about 120 FPS and 12,000 LOL/s with VSync, and at least 36,000 LOL/s uncapped. The current measured values are 11,996 and 43,224 LOL/s. Compare on the same machine and window state; investigate a repeatable drop greater than 10% before merging.

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
