# Native MIL glyph guideline placement

Acceptance: the external LibreWPF SDK application opens its main window with
NativeMilWgpu and the native input index enabled. Its first-frame preflight
rejected glyph command 3069 under static multi-guideline state. This is a core
application blocker, not general Direct2D or Win2D expansion.

## Source contract and implementation

The source contract trace is LibreWPF's
`src/Microsoft.DotNet.Wpf/src/WpfGfx/core/glyph/baseglyphpainter.cpp`,
`CBaseGlyphRunPainter::PrepareTransforms`: bitmap text uses the snapping frame
at the run origin and retains only its Y displacement. The geometry-realization
branch in `CDrawingContext::DrawGlyphRun` is a separate policy. Only this
observable placement rule informed the change; the implementation reuses
ProGPU's original retained guideline resource and nearest-coordinate search.
No source renderer implementation was copied.

The original nearest-coordinate search is shared between the semantic state
cursor and a typed builder query. It preserves lower-coordinate ties and static
or explicit dynamic physical offsets. It is an allocation-free O(log N) ordered
search with dependent probes, not SIMD-suitable independent pixel work. Existing
SIMD guideline transformation and glyph rasterization remain authoritative.

MIL resolves one vertical displacement at the transformed original run origin,
then uses the existing explicit-offset guideline resource in a balanced local
scope. Horizontal position, glyph outlines, advances, offsets, brushes and
unsnapped source input remain owned by their existing pipelines. A local text
flag prevents a second baseline round during glyph placement, including when
the spatial-brush coverage path consumes the uniform offset. Ordinary unguided
text keeps its previous placement path. The parent guideline state is restored
before subsequent drawing. No generic renderer admission guard is removed and
no wire ABI or shader is added.

## Evidence and remaining work

Both native providers compile on macOS ARM64. Native MIL and internal regressions
pass. Tests cover nearest-coordinate ties/extremes, X-only guidance, explicit
offsets, rejected queries without output mutation, DPI 1/2, original fractional
glyph offsets, unchanged source-owner input bounds, and parent per-point state
restoration. The MIL test target links the production state resolver to inspect
the actual resolved state. The native contract verifier passes after regenerating
the decoder digest.

The external app diagnostic advances past glyph command 3069 and rejects image
command 5179 (kind 19, resource 3057). These runs substitute local binaries into
an existing diagnostic package directory; they do not prove exact-package,
pixel-comparison, Windows/VM or full application qualification. Image guideline
mapping and final qualification remain required. The renderer now includes the
command index/kind/resource in this failure to make further diagnosis direct.

CI run 34562927961 gets past the previous fill-rule narrowing and reports MSVC
C4389 in an earlier curve-capture test's enum comparison. The expected path kind
is now explicitly converted to uint32, retaining the strict warning gate and
the assertion. New-head Windows CI is still required.
