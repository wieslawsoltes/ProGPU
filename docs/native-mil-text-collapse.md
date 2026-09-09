# Native text collapse: Toolkit application blocker

## Status and bounded delivery

LibreWPF's Toolkit application's top header uses CharacterEllipsis. Narrowing the
window calls source `PortableTextLine.Collapse`, which currently throws for an
overflowing line. This is a source-backed blocker, not a reproduced runtime result.
Do not remove the sample's trimming request or replace it with clipping.

The first implementation batch fixes the existing C++ trim boundary/metric scan.
It does **not** yet connect source WPF collapse. Both portable renderer modes use
the same native paragraph service; their source adapter remains closed until the
following requirements are connected:

1. Keep the original paragraph/source map and immutable continuation ownership;
   expose a separate collapsed view with original source length and hidden range.
2. Resolve the actual source collapsing symbol's face, metrics and brush. A
   synthetic glyph must not inherit the last visible glyph's source cluster or
   accidentally use a different physical font.
3. Publish visible glyphs, hidden-range affinities, ellipsis bounds and source
   indices through typed native/neutral contracts. Preserve RTL visual placement,
   wrapped-line ownership, backgrounds, decorations and source run metrics.
4. Connect TextLine drawing, bounds, hit/caret queries and collapsed-range APIs;
   author Toolkit header resize and source-level regressions before freeze.

The existing untruncated NativeTextParagraphSnapshot cannot simply admit trimmed
options: it infers logical cluster ends from visible glyphs, which would assign
hidden text to the last visible source cluster. Its rejection remains intentional.
The native synthetic-sign cluster/RTL placement and oversized single-cluster
behavior also need explicit treatment before exposing a complete collapsed view.

## Shared C++ correction

At parent 91104e9f, `src/ProGPU.Native/src/Text/progpu_native_text_layout.cpp`
removed glyph clusters backwards by subtracting raw font advances. That loses the
resolved width of incremental tabs and may stop at a distinct but unsafe shaping
cluster. The replacement walks the existing forward `layout_advance` policy and
records fitting, shaping-safe character and word boundaries. It preserves per-run
scales and the actual tab origin/interval. Both ordinary and logical/bidi layout
entry points use this helper; the latter now supplies its tab policy.

This is an original modification of ProGPU-owned code. No foreign implementation
text was copied. The prefix and boundary scan is O(G) time, O(1) workspace, and
allocation-free. Width accumulation, tab phase and safe-boundary decisions depend
on preceding state, so this is ordered CPU work, not an independent-lane GPU/SIMD
fallback. Existing NEON/SSE2 metric scaling and validation remain unchanged.
No new interop crossing, face discovery, GPU initialization or upload is added.

Native regression fixtures cover removed/retained tabs, nonzero tab origin,
per-style scale, unsafe shaping boundaries, sign-only output and paired ordinary
versus logical entry-point output. They are authored for final execution; a
successful build is not a test result.

Build-only evidence (2026-09-09): clean detached commit `bf7a0fb4`, Apple Clang,
macOS ARM64, existing strict C++20 header-compatibility CMake configuration:
`cmake --build artifacts/native-core-build.KvxVug/build-osx-arm64 --target progpu_native_text_tests --parallel 3`
completed all four compile/link steps with exit 0. No test executable was run.
Module/platform matrix, managed/native application comparisons and full renderer
qualification remain required. This does not update or qualify staged packages.

## Primary-source design record

- [DirectWrite trimming](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_trimming)
  and [Win2D granularity](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextTrimmingGranularity.htm)
  distinguish character-cluster and word trimming. Retain that distinction;
  delimiter-preserving trimming is not implied by this correction.
- [HarfBuzz buffer concepts](https://harfbuzz.github.io/harfbuzz-hb-buffer.html)
  distinguish input text from positioned glyph/cluster output. Keep real source
  mapping and the existing ProGPU shaping-safe boundary flag, not scalar-count cuts.
- [SkParagraph's public contract](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h)
  separates layout metrics and range/hit queries. A rendered ellipsis alone is
  not enough to implement a source TextLine.
- [Parley layout](https://docs.rs/parley/latest/parley/layout/index.html) separates
  clusters, positioned runs, lines and cursor affinity. Adopt that separation in
  the planned collapsed view instead of rewriting the original paragraph.
- [Vello](https://github.com/linebender/vello) and
  [WebRender](https://github.com/servo/webrender) remain rendering references, not
  a source for host-specific truncation logic. Preserve the existing separation
  of prepared glyph runs and GPU scene rendering.

The [existing cross-engine comparison](native-mil-text-source-integration.md#architecture-sources-and-decisions)
continues to govern lazy context startup, shaping reuse, retained scene reuse,
visibility, font/variable-face and atlas keys/eviction, demand upload, workers,
batching, DPI/subpixel/hinting and device-loss generations. None of those policies
changes in this scan correction. Measured latency, output and lifetime evidence
remain deferred to exact-binary final qualification; no speed claim is made.
