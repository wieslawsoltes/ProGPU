# Implicit Windows font metric DPI

`Font.GetHeight()` and `Font.Height` use the current Windows screen DC's logical
vertical DPI, including the calling thread's DPI-awareness virtualization.
Pixel/world `Font.SizeInPoints` uses the same screen reference. Point sizes and
physical-unit conversion remain source values; no font size is mutated.
Non-Windows implicit metrics retain 96 DPI. Explicit `GetHeight(float)`,
`GetHeight(Graphics)`, image DPI and recorder defaults are unchanged.

The internal query acquires and releases one screen DC synchronously, with no
retained handle, GDI+ startup, font enumeration, GPU initialization or managed
Drawing bridge. Invalid native results fail explicitly. Never cache one DPI
across awareness changes or substitute monitor/framebuffer geometry for this API's
screen-DC contract. Public HDC/HFONT APIs are not admitted by this private query.

## Design references

The independent Microsoft `Font` source uses a screen DC for implicit height and
non-point point conversion. The actual Windows oracle below confirms the unit
boundary. [GetDeviceCaps](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicecaps)
defines the screen `LOGPIXELSY` value. The following primary sources were reviewed
for the same separation of source size, layout scale and raster target:

- [DirectWrite/Direct2D units](https://learn.microsoft.com/en-us/windows/win32/learnwin32/dpi-and-device-independent-pixels)
  and [Win2D DPI](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips):
  adopt explicit logical-versus-physical conversion; reject changing image or
  explicit target DPI to the ambient screen DPI.
- [SkFont](https://api.skia.org/classSkFont.html) and
  [SkParagraph TextStyle](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/TextStyle.h):
  retain source font/style size rather than multiplying the Font object.
- [WebRender FontInstance](https://github.com/servo/webrender/blob/main/wr_glyph_rasterizer/src/rasterizer.rs):
  device/raster size belongs to raster identity; no glyph-cache change is needed
  for this implicit API query.
- [Parley layout scale](https://docs.rs/parley/latest/parley/editing/struct.PlainEditor.html)
  and [Vello glyph size](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html):
  keep layout scale and pixels-per-em explicit, not inferred from a window ratio.
- [HarfBuzz scale](https://harfbuzz.github.io/harfbuzz-hb-font.html#hb-font-set-scale):
  shaping scale remains caller-owned; this query does not change shaping context.

This is an O(1) unit-boundary correction, not a renderer/performance redesign.
Startup remains lazy; layout/display-list reuse, culling, upload, worker preparation,
GPU batching, fallback/variation state, subpixel/hinting, atlas eviction and device
loss are unchanged. No startup, scrolling or GPU performance claim is made.

## Actual Windows evidence

An ARM64 Windows 11 desktop at 192 DPI ran isolated Microsoft and portable
processes, preserving assembly hashes and comparing the same Segoe UI values.
The baseline portable payload came from successful LibreWinForms Build
[36352993959](https://github.com/wieslawsoltes/LibreWinForms/actions/runs/36352993959),
exact head `e22bafe20ea7e49cd8bd5c2ca41ed4f2d515cb67`, with ProGPU
`1cf58aea8170e642b4ce3ef89000975b00bb2e75`. Artifact `10944050346` SHA256:
`5c8d46fc502135e01793e4f7be2a6ce10fc96a8afa3cb00a4cd3460e201f541d`.

At system/per-monitor-v2 awareness, a 9-point font incorrectly returned Height 16
instead of Microsoft's 32 (exact height 31.921875). A 12-pixel/world font returned
9 points instead of 4.5. Explicit 96/192 DPI heights already matched and were
left unchanged. At unaware DPI 96 the original values matched.

The corrected source library matched every captured field for all six units
(Point, Pixel, World, Inch, Document, Millimeter) in all three awareness contexts,
including the system MessageBoxFont. This differential deliberately used a fresh
private copy with the source-built Drawing DLL, not an altered package/cache:
source DLL SHA256 `4787194e65e0d1cc1a46562514d84f946e74c384eee6794b3eba9088466fd504`.
It is source evidence, not qualification of a newly built package or full UI.
Receipts remain in `/Volumes/1TB-macOS/progpu-font-screen-metrics.Y0MHi7Su`.

The focused host regression suite passes 62/62 with no skips. Six added cases
reuse the same Font across unaware/system/per-monitor-v2/unaware transitions on
Windows, retain explicit 96/192 metrics and independent 144-by-168 image DPI,
and check immutable source size/unit. Non-Windows exercises the same controls at
96 DPI. Both probe projects compile with zero warnings/errors.

The existing Windows x64 and ARM64 system-font CI jobs retain all eight role and
ownership checks, add the independent six-unit comparison described below, and require
all 23 ownership/layout/screen-DPI tests to execute and pass. Their original
12-minute bound is unchanged. Full exact-head Build, Docs, package consumption
and application appearance remain separate requirements.

## Native floating-point precision versus portable arithmetic

The initial x64 gate on `ec3c9b95f` exposed a mistaken bit-equality assumption in
the new oracle, not a remaining factor-of-two DPI error. Microsoft's own 10.0.12
GDI+ implementation returns `15.960936` from implicit Point `GetHeight()` but
`15.9609375` from `GetHeight(96)` at screen DPI 96. Document/Millimeter point
conversion returns `8.999998` rather than 9; the explicit Millimeter path also
differs by two single-precision ULPs and was not changed by this fix. All captured
integer heights, units, sizes and design metrics agree. The original failing
receipt is artifact `10943597894`, SHA256
`65de8133be4a7e49b7ec31c5f8670f2a5d50331abb6593aa02cbffecb0ff8fba`.
ARM64's corresponding exact-field job passed, including all 23 tests.

Portable metrics remain defined by the existing typed design metrics and explicit
unit arithmetic; they do not adopt a platform-specific GDI+ rounding algorithm or
delegate privately loaded font metrics to a native font with a matching name.
The revised oracle verifies that arithmetic **exactly**, independently in the
Microsoft process using its public family metrics. It also compares all four
computed floating metrics against GDI+ with a maximum distance of two positive,
finite single-precision ULPs. Identity, source size, integer Height, family metrics,
screen DPI, roles and units still compare exactly. This is not a pixel tolerance,
absolute epsilon, rounding-to-integers comparison or native rendering exception.

Sixteen comparison controls accept offsets -2 through +2, reject either three-ULP
boundary, invalid/nonpositive metrics and half/double DPI errors, and reject even
a one-ULP error in the portable arithmetic. Both Windows jobs run these controls
before the real oracle. The original failing receipt is preserved; correcting
the new oracle's numeric contract does not retroactively qualify that producer.
