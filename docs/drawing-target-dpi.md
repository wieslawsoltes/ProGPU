# Drawing target resolution

The portable Forms paired Windows capture used 192-DPI device-pixel client
coordinates, but `Graphics.DpiX/Y` were fixed at 96. A 9-point source font was
therefore measured and recorded at 12 pixels rather than 24. This is a target
unit conversion defect, not evidence for replacing fonts or scaling the driver.

The additive recorder overload captures finite positive X/Y target DPI. Existing
overloads retain 96 DPI. Image recorders capture the image resolution at creation.
Point/inch/millimeter font conversion and measurement share the existing Drawing
path; pixel fonts remain unchanged. Neither the source Font nor outer transform
is modified. Logical-coordinate hosts must continue to pass 96 and use their
existing presentation scaling; device-pixel hosts supply actual target DPI.

## Design references

This is clean-room unit-boundary work, not imported rendering implementation.

- [Direct2D/DirectWrite units](https://learn.microsoft.com/en-us/windows/win32/learnwin32/dpi-and-device-independent-pixels)
  and [Win2D DPI](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips)
  distinguish 96-based logical units from physical target pixels. Adopt that
  distinction; do not apply both a font multiplier and logical scene scaling.
- [SkFont](https://api.skia.org/classSkFont.html) and
  [SkParagraph style](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/TextStyle.h)
  keep font size explicit in font/style state. Retain the original source Font;
  only its conversion into the destination coordinate space changes here.
- [WebRender font instances](https://github.com/servo/webrender/blob/main/wr_glyph_rasterizer/src/rasterizer.rs)
  distinguish raster/device size. No raster-cache or glyph identity policy change
  is needed: existing recorded FontSize already carries the converted size.
- [Parley layout](https://docs.rs/parley/latest/parley/editing/struct.PlainEditor.html)
  has an explicit layout scale, while [Vello glyph runs](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html)
  accept pixels per em. Adopt explicit units; reject inferred framebuffer ratios.
- [HarfBuzz font scale](https://harfbuzz.github.io/harfbuzz-hb-font.html#hb-font-set-scale)
  is a caller-owned convention; its ppem metadata is not a substitute for
  converting points into the intended target coordinates.

Managed and native providers consume the same recorded glyph size. No native
backend DPI multiplier or separate shaping policy is added. Focused tests cover
96/144/192 measurement and command size, unchanged pixel/default fonts, image
resolution, invalid values, and existing clip/transform/callback ownership.
Native appearance, installed-package and paired application acceptance remain
separate and unqualified by these source tests. No performance claim is made.

## Observed validation

Exact `22f83ea5529e0d975a3b9a48b17371f79e6f003d` passed the16 new tests and
75 selected Drawing/font/context regressions locally on .NET10.0.5 ARM64;
the focused project build had zero warnings/errors.
[Linux job108539280464](https://github.com/wieslawsoltes/ProGPU/actions/runs/36290413946/job/108539280464)
passed API debt0missingtypes/0missingmembers/13existing differences and all706
quality cases. Its unchanged full4453-case upstream corpus produced
3297passed/1122failed/34skipped: exactly `MonoTests.System.Drawing.GraphicsTest.Dpi`
moved from failure to pass. The skip inventory is byte-identical. Only that
observed Linux-x64 failure entry and summary are removed/updated; Linux ARM64
is not inferred from a macOS host run.

Original failed producer Build36290413946 remains failure evidence (the inventory
gate correctly rejected an unrecorded improvement). Artifact10922475222 has
SHA256 `309c4e39e92c67b4a55ddc2a29ea9d3b09892fe8466a7bedf52934b2115bf249`.
Passing source/corpus cases do not qualify a failed whole Build or native Forms
appearance; a complete exact-head producer is still required.
