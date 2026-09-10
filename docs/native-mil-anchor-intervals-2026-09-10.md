# Native anchored text exclusion intervals

## Concrete dependency

LibreWPF Showcase's unchanged rich document contains DocumentFigure and
DocumentFloater. Surrounding text must wrap around their source-resolved layout
boxes while their original block subtrees remain independently selectable.
The source currently rejects these anchors. Inline control support at LibreWPF
9f119b354 does not supply this different out-of-flow contract.

This batch adds try_resolve_text_line_intervals to the existing native text
implementation, installed header and named module. Given a candidate line band
and resolved exclusion rectangles, it clips contributing horizontal intervals,
sorts and unions them, and returns the complete complement in increasing order.
It returns no intervals for real exhausted width, and the earliest contributing
bottom for a subsequent native fitting attempt. The whole candidate height is
tested; looking only at the baseline can overlap an anchor.

Empty rectangles exclude nothing. Edge contact is half-open and does not consume
adjacent space. Negative origins are valid. NaN, infinity, reversed bounds,
zero-height candidate bands, aliased buffers and insufficient capacity fail
explicitly. Output intervals and next-Y remain untouched on failure; count resets.

## Architecture, provenance and applicability

This is original ProGPU code in progpu_native_text_layout.cpp, not a foreign
implementation port. The shared native text library serves both wgpu and Dawn.
Neither ProGPU.Text.TextLayout nor StyledTextLayout currently has a matching
anchored exclusion implementation. Both WPF renderer modes use the same native
text provider; no managed renderer behavior is replaced or reduced by this
currently unconnected native primitive.

The existing [cross-engine design record](https://github.com/wieslawsoltes/LibreWPF/blob/9b9578d16/reports/native-mil-inline-anchor-design-2026-09-10.md)
covers DirectWrite/Win2D, SkParagraph, WebRender, Vello/Parley and HarfBuzz.
This batch also consulted the [CSS float layout contract](https://www.w3.org/TR/CSS22/visuren.html#floats)
for constrained line space and downward progress, and the
[WPF FlowDocument model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/flow-document-overview)
for the distinction between in-flow controls and anchored content. CSS policy
is not substituted for WPF Figure positioning or Floater sizing.

Shaping, physical-font mapping, fallback/variation state, hinting/DPI, caches,
visibility, demand upload, retained scenes and device-loss ownership are unchanged.
This helper performs no font/GPU initialization, readback, upload or allocation.
It uses NEON/SSE2 coordinate validation and ordered interval sorting/union:
O(E log E) time and O(E) caller-owned scratch per candidate band. Sorting and
union have ordered dependencies; a GPU dispatch would not replace the dependent
line-fitting process. Non-desktop targets retain a fixed four-coordinate scalar
reference. No speed claim or general fallback-policy change is made.

## Evidence and remaining work

The Release native text target builds and its complete include-based suite passes.
Tests cover left/right/interior/overlapping exclusions, exact edges, full blockage,
zero-area boxes, exhausted zero width, invalid input, alias/capacity rejection,
output canaries and 128 generated cases against independent scalar point coverage.
The named-module consumer builds with Homebrew LLVM 22.1.8 and the installed
Xcode sysroot, links the same native text/compression archives, and passes.
The configured native build has CMake module targets disabled; the module
consumer therefore uses a private untracked build directory, not shared BMIs.

Logs: artifacts/anchor-interval-build.log, artifacts/anchor-interval-tests.log.
Module artifacts: artifacts/anchor-text-module.FxeqPf.

Still required: native anchor policy/placement, variable-width measured line
fitting with bounded height-dependent retries, retained/batched paragraph
transport, source subtree/position ownership, source interaction and reflow,
unchanged application acceptance, both provider packaging and final platform/CI
qualification. No source admission or package gate was changed in this batch.
