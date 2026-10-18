# Binary font scan metadata — implementation batch

The prepared font-batch path retains binary scan metadata independently of
antialiased vector geometry. Both renderers consume one shared GPU implementation.
The original authored-font comparisons pass on local macOS ARM64; system-font,
source application and complete package qualification remain open. Existing
source consumers remain unchanged until that qualification.

`NativeHintedCoverage.Monochrome` applies only to captured, already-positioned
TrueType font batches. The original contour writer produces the same physical
lines and quadratics. Cubics, unsupported flags, malformed topology and unknown
policies reject before publication. Existing coverage modes and record layouts
remain unchanged. Old producers reject the new mode.

Every emitted segment carries its actual contour's descriptor:

- `Pad0`: marker bit 8, raw resolved SCANTYPE in bits 0–2, original contour
  override marker in bit 9, and the original admitted outline flags in bits 16–31.
- `Pad1`: contour start relative to this outline's segment range.
- `Pad2`: the complete contour segment count.

Raw SCANTYPE aliases are retained. A marked contour start replaces the current
mode; singleton contours still update that state without manufacturing a segment.
The original batch remains owned, including no-ink descriptors and raw tags.
Generic paths continue to reject nonzero reserved words. Managed import validates
the complete contiguous contour partition, closure, uniform descriptor, original
flags and source bounds. Copies preserve caller tails and atomic rejection.

Implementation provenance: the existing ProGPU contour writer in
`progpu_native_hinted_outline.cpp`; public
[OpenType scan-conversion rules](https://learn.microsoft.com/en-us/typography/opentype/spec/tt_instructions#scantype)
and [FreeType outline metadata](https://freetype.org/freetype2/docs/reference/ft2-outline_processing.html).
No foreign renderer implementation was used.

This uses the existing [cross-engine font architecture research](native-font-hinting.md)
and [retained presentation-policy research](source-grayscale-text-contrast.md).
Adopted: immutable prepared layout, explicit raster identity, lazy device pipelines,
batched uploads and owned submission retirement. Adapted: the original ProGPU atlas
and native raster page carry the additional scan buffer. Rejected: CPU bitmap
fallback, source reshaping, per-glyph interop calls and cross-API mode aliases.
The algorithm comes from public scan contracts and independently observed original
outputs; it does not copy another rasterizer's source or implementation structure.

`BinaryGlyphRasterizer.wgsl` uses one 256-thread workgroup per scanline and two
ordered dispatches. It sorts at most 512 crossings from 256 original line/quadratic
segments, accumulates winding, and writes packed binary R8 coverage. The second
pass reads a separate completed horizontal buffer, avoiding races between dropout
decisions and their output. Pixel boundaries, raw mode aliases and the bounded
bitmap extent are explicit. Vertical gap admission preserves the original bitmap
row direction in the contour's Y-up frame. Continuation counts distinct square
boundary contacts, retains actual orthogonal caps and exact cap contacts, and
keeps with-stubs line-vertex contacts separate from positive winding intervals.
No epsilon, source-coordinate adjustment or AA threshold substitutes for a binary
decision. The independent original system-font comparisons remain required.

Both renderers use the same shader and preserve actual submission ownership.
Managed work uses the existing uniform/output rings, including oversized glyphs;
native work retains both buffers and bindings in its existing raster lease. The
extra buffer participates in native memory inventory, staging metrics and semantic
resource budgeting. Pipelines remain lazy; binary-only demand does not compile the
ordinary vector rasterizer. Immutable source bytes participate in raster identity.
Shared command admission requires aliased integer-origin replay and the complete
bounded outline inventory; the inventory maximum is retained once per generation,
including selected views. Unsupported execution paths reject instead of selecting
AA or a CPU bitmap substitute.

The checked-in fixture contains all 880 original Windows observations of two
ProGPU-authored fonts: five scan modes, eight outline families, eleven phases and
two gasp tables. Both original GDI+ and GGO_BITMAP outputs agreed, and all 1,760
captured hinted/unhinted GDI contours matched the authored source. The fixture
digest is `6d13e632b5f260217bc17d9c86b61e77506f463644eeee923280400a52f3b092`.
The owned font inputs are 20,952 bytes each; the tests verify their original
SHA-256 values before execution. No system-font binary or foreign renderer code
is included.

Local validation on 18 October 2026:

- All 880 complete authored coverage cases match exactly in the managed atlas,
  native wgpu and native Dawn; every cold/warm pixel remains identical.
- Both native providers also pass all 880 cases from actual captured font batches
  under both TrueType interpreters, after disposing the public source owners.
- All 72 focused shader/atlas/integer-origin tests pass. The original native
  contour controls and package-consumer device/font/paragraph ownership controls
  pass with binary preparation included.
- The full native build succeeds for both providers. The first full renderer run
  passes 6,352 tests, retains 12 platform skips, and fails one source-text audit
  that still expects the former no-argument compute-pass signature. The audit now
  requires both the typed selector and its actual call; the failed run is retained.
- After correcting that source audit, the complete renderer passes 6,353 tests
  with the same 12 platform skips and no failures. Generated native contracts and
  the 101-field native memory inventory verification also pass.
- All 230 headless tests pass with zero skips.

The JIT/NativeAOT package scenario groups add both binary provider cases while
preserving every original MIL, text and EDIT scenario. The separate DX12 package
and NativeAOT lanes include the original binary check as well. Whole producer
CI, original system-font pixels, final source wiring, packages and application
acceptance are still required. Authored controls do not establish broad Microsoft
parity or release readiness.
