# Native text interaction advance geometry

## Problem and observed oracle

LibreWPF's stock-Windows comparison found one remaining internal caret mismatch
in the right-to-left Segoe UI word `עולם`. At UTF-16 insertion 23 in the complete
fixture, stock WPF reported X `147.793` while the native paragraph snapshot
reported `148.142`; the `0.349` DIP difference exceeded the `0.1` DIP gate.

The native shaper and paragraph writer were already correct. Segoe UI applies a
negative OpenType X placement to the lamed glyph while retaining its positive
advance. `NativePositionedTextGlyph.X` is therefore the draw origin (pen plus
placement offset). The original interaction builder treated that draw origin as
the logical cluster-box origin, so a rendering-only GPOS placement moved the
caret and selection geometry.

This distinction is part of the public shaping contracts used by other engines:

- [HarfBuzz `hb_glyph_position_t`](https://harfbuzz.github.io/harfbuzz-hb-buffer.html)
  defines `x_offset` as movement before drawing that must not affect the line
  advance, while `x_advance` moves the current point after the glyph.
- [DirectWrite text-position hit testing](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextposition)
  returns the layout-relative leading or trailing location for an editing
  position. [Direct2D/DirectWrite glyph-run rendering](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  separately retains the baseline origin, glyph advances, and glyph offsets.
- [Win2D `CanvasTextLayout`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  retains layout computation and exposes a separate
  [caret-position query](https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_Text_CanvasTextLayout_GetCaretPosition.htm).
- [Parley line layout](https://github.com/linebender/parley/blob/main/parley/src/layout/line.rs)
  retains a line alignment offset and advances the run pen by each glyph's
  advance; its positioned-glyph iterator applies the glyph placement only when
  producing draw positions.
- [Skia's shaped-text design](https://skia.org/docs/dev/design/text_c2d/)
  separates reusable layout/hit-test information from efficient drawing of
  positioned glyphs.
- [WebRender's retained rendering architecture](https://github.com/servo/servo/wiki/Webrender-Overview)
  keeps text as retained glyph primitives and resolves/rasterizes glyphs in the
  rendering pipeline. It informed retaining the existing draw positions rather
  than rewriting glyph placement to repair interaction.

These sources informed the contract only. No third-party source was copied or
translated.

## Contract

The advance-geometry builders receive one physical pen X origin per retained
line. They construct each cluster box by walking the original visual-order glyph
advances from that origin. OpenType X/Y placement continues to affect only the
positioned glyph used for rendering.

The implementation preserves:

- original glyph, UTF-16 cluster-end, bidi-level, line, fragment, and affinity
  ownership;
- physical visual order for left-to-right and right-to-left runs;
- paragraph left, center, right, and justified line origins;
- fragment-local left and width when exclusion layout is active;
- measured line tops, explicit fragment tops, collapse ownership, and existing
  hit-test/selection/navigation algorithms.

RTL word-space justification has one additional placement fact: retained
trailing whitespace starts before the visible zero edge after the interior word
spaces expand to the requested width. The positioned-line output publishes
`RIGHT_TO_LEFT_JUSTIFIED` only when that expansion actually occurred. The
paragraph wrapper then derives the physical pen origin as container width minus
the complete line width. An oversized unbreakable RTL word does not publish the
flag and therefore remains zero-origin; width alone is never used to guess that
justification happened.

For `G` positioned glyphs and `L` lines, validation and construction remain
`O(G + L)` time. C++ uses only caller-owned spans and `O(1)` internal storage.
The managed paragraph wrapper uses at most 128 stack-resident origins (512 bytes)
and borrows an `ArrayPool<float>` buffer for larger paragraphs. The native call
is synchronous and does not retain any pointer.

## API and compatibility

The existing interaction entry points remain unchanged for callers whose glyph X
coordinates already represent logical interaction origins. The following additive
C ABI functions provide the explicit advance contract:

- `progpu_native_text_interaction_build_advance`
- `progpu_native_text_interaction_build_measured_advance`
- `progpu_native_text_interaction_build_fragment_advance`

The ordinary and fragment C++ APIs and all three managed span APIs expose the
same explicit origin contract. The positioned-line binary size, field names and
ABI version remain unchanged. Bit zero of its first reserved output byte now
identifies an actually expanded RTL justified line; managed consumers use the
typed `LayoutFlags` property rather than reading that byte directly.
Each advance entry point requires
exactly one finite origin per line, validates finite cumulative advances before
writing, preserves zero output counts on failure, and never publishes partial
geometry.

`NativeTextParagraphSnapshot` uses the advance path for ordinary, measured,
fragmented, and collapsed paragraphs. Both the managed and C++ GPU renderers
consume the same retained paragraph snapshot, so no renderer-specific branch,
shader, upload, or device behavior changes. The old C++ interaction path and the
standalone managed `ProGPU.Text.TextLayout` API remain separate compatibility
surfaces; this slice repairs the native paragraph adapter that carries WPF source
interaction.

## Validation

The native unit test supplies an RTL visual run with a nonzero GPOS X placement
and proves that rendering X remains offset while cluster boxes and both caret
affinities follow the pen and advances. The C ABI test repeats that contract and
covers an incorrect origin count and a non-finite origin without partial output.
The managed package-consumer and paragraph tests cover the public API, paragraph
alignment, fragment-local alignment, RTL trailing-whitespace origins, invalid
flag/alignment combinations, and the distinct oversized-unbreakable-word case.

Validated on the delivery branch:

- Apple Clang/macOS ARM64 native text and C ABI interaction tests: pass.
- MSVC 19.51/Windows ARM64 in the Windows 11 Parallels VM: native text and C ABI
  interaction tests pass.
- `NativeTextParagraphSnapshotTests`: 19/19 pass after the RTL-origin addition.
- Source-built LibreWPF native MIL host on macOS ARM64: pass, including styled
  LTR/RTL justification selection, caret and hit geometry.
- Segoe UI RTL managed/C++ shaping and layout differential: pass.
- 6,000-iteration Release interaction call on Apple M3 Pro:
  median `0.084 us`, p95 `0.125 us`, p99 `0.167 us`, and `0 B/run` managed
  allocation. It performs one managed/native crossing per paragraph interaction
  build and copies no glyph data.

The final ProGPU package CI and LibreWPF Windows x64/ARM64 package comparison are
separate merge gates. This result does not by itself qualify unrelated typography,
application startup, or the broader MIL/DirectX delivery.
