# Windows system font settings

The portable `SystemFonts` implementation previously returned generic sans serif
at 8.25 points for every role, including Windows. An original Microsoft WinForms
DataGrid sample observed at 192 DPI uses a 9-point Segoe UI MessageBoxFont. Its
fixed 21-pixel rows and narrow columns also clip in the Microsoft reference;
changing those source dimensions would not correct the portable font policy.

## Implementation and ownership

On Windows, query the actual nonclient font settings for caption, small caption,
menu, status and message roles, and the icon-title LOGFONT for icon titles.
DefaultFont uses the borrowed DEFAULT_GUI_FONT stock descriptor and the canonical
Tahoma/generic-sans fallback policy. DialogFont uses the canonical MS Shell Dlg 2
policy and the system-locale branch. Do not replace these with a Segoe UI constant
or the current managed culture. Unknown names remain null.

`WindowsSystemFontDescriptor` is a private CPU metadata reader. Native GDI+
interprets the OS LOGFONT using an owned screen DC, including positive cell
heights, font aliases/substitution and style. Point conversion uses the native
font's actual line spacing/em height and that DC's DPI, with the same float
operation order as the Microsoft API. No hard-coded 96-DPI conversion is used.
Native startup, font, family, graphics and DC lifetimes are bounded by the query;
the borrowed stock HFONT is never destroyed. There is no cached role descriptor
that could outlive a settings or DPI-awareness change.

Only family name, point size, style, charset and vertical identity leave the
native reader. The resulting Font and FontFamily are owned ProGPU objects using
the existing font manager, shaping and rendering. No Microsoft managed Font or
Image crosses an assembly boundary. This does not enable the public HDC/HFONT
interop APIs, bypass their explicit adapter requirement, or draw with GDI+.
API/query failures do not silently select the non-Windows 8.25-point policy.
Linux/macOS selection is unchanged and still requires its own settings contract.

## Design sources

- Microsoft's [SystemFonts implementation](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/src/System/Drawing/SystemFonts.cs)
  and [Font implementation](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/src/System/Drawing/Font.cs)
  define the role mappings, fallback policy, owned requests and point conversion.
  The [native font API](https://learn.microsoft.com/en-us/windows/win32/gdiplus/-gdiplus-font-flat)
  and [startup lifetime](https://learn.microsoft.com/en-us/windows/win32/api/gdiplusinit/nf-gdiplusinit-gdiplusstartup)
  inform the scoped metadata query. This is original code; no managed framework
  implementation or extra native redistributable is shipped.
- [Skia font options](https://api.skia.org/classSkFont.html) and
  [SkParagraph FontCollection](https://github.com/google/skia/blob/main/modules/skparagraph/include/FontCollection.h)
  separate font selection/cache ownership from size, hinting and paragraph reuse.
  Retain that separation: OS settings are not an atlas or shaped-paragraph key.
- [DirectWrite enumeration](https://learn.microsoft.com/en-us/windows/win32/directwrite/font-enumeration)
  and [Win2D CanvasTextFormat](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextFormat.htm)
  distinguish family discovery from layout formatting. Reuse ProGPU discovery;
  enumeration cannot substitute for querying a user's configured system role.
- [HarfBuzz faces/fonts](https://harfbuzz.github.io/fonts-and-faces.html) and
  [Parley's text stack](https://github.com/linebender/parley/blob/main/README.md)
  separate discovery, reusable font data, shaping and layout. The descriptor
  changes only the input selection; fallback/shaped-cluster and variation state
  remain owned by the existing text pipeline.
- [WebRender's retained rendering](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello glyph runs](https://docs.rs/vello/latest/vello/struct.DrawGlyphs.html)
  keep scene reuse, GPU work and glyph preparation downstream. This settings
  query initializes no GPU device and changes no culling, worker preparation,
  upload, batching, atlas cache/eviction or device-loss/generation behavior.

The query is lazy (only an actual SystemFonts request enters Windows), fixed-work
in role count with bounded native ownership; ordinary font discovery retains its
existing cold/warm costs. No startup, scrolling, memory or raster performance
improvement is claimed from changing the metadata. Hinting, subpixel placement,
variable-font rendering and complete UI comparison remain separate validation.

## Validation

`eng/progpu-verify-windows-system-fonts.ps1` builds an independent Microsoft
WindowsDesktop probe and the portable probe from one read-only observer. They run
in separate processes and exchange JSON, not Drawing objects. Each of the eight
roles is compared field-for-field in unaware, system-aware and per-monitor-v2
thread contexts. The gate rejects wrong process architecture, missing/duplicate
roles and a reference that did not load the official WindowsDesktop assembly;
assembly paths/hashes and all JSON receipts are retained even after a failure.
No SPI settings are changed and no display mode is changed by the test.

The Build workflow runs this gate independently on Windows x64 and ARM64, plus
the existing ownership tests and new descriptor/ABI tests. These include exact
92-byte LOGFONT and 504-byte NONCLIENTMETRICS layouts, complete style/charset/
vertical propagation and independent family/font disposal. Non-Windows ownership
tests retain the original 8.25-point assertion. The full existing Drawing corpus
and all original CI jobs are unchanged.

The Windows CI result and actual original-sample UI comparison are required
before claiming Windows qualification; this document does not replace those
receipts or close the source sample/UI issue.
