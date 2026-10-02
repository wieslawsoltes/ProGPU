# Direct2D full-target Clear

An unscoped `ID2D1RenderTarget::Clear` replaces all earlier commands and retained
resources in the current recording. Compatible targets also discard their retained
history. The portable recorder and Windows command-list translator use the shared
semantic scene reset, retire recorder-owned resource indices, and retain transform,
antialias, text, brush and tag state for subsequent drawing. Previously exported
scene bytes keep their independent ownership.

The last full clear becomes the frame's clear color; only the surviving commands
and resources are serialized. Windows `TranslatedDrawCount` still counts all
successfully translated draw callbacks, including discarded draws. Original
callback indices and the first failure remain authoritative.

Microsoft specifies [straight-alpha clear colors and transparent black for a null
color](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-clear%28constd2d1_color_f%29).
Public clear metadata retains that representation. IGNORE targets set alpha to one;
ordinary scene submission premultiplies RGB once before the engine attachment
clear. Compatible picture submission retains its existing conversion. Recordings
without Clear preserve the target attachment.

Clear inside an all-aliased axis-aligned clip stack appends a bounded replacement
through the shared semantic builder's ordinary SRC layer. The already-intersected
clip belongs to the target frame captured at each push. A later source transform,
including a singular finite transform, cannot move that clear. Pixels outside the
clip, preceding commands/resources, leading-clear metadata and source drawing
state survive. A null color replaces the clipped pixels with transparent black;
IGNORE targets instead retain alpha one. An empty intersection appends no draw.

This is retained drawing, not a history reset: the portable retained draw count
includes its primitive and preserves the existing mixed-DPI-history rejection.
Windows translated draw counts still describe original draw callbacks, not the
new internal clear primitive. There is no new public ABI, readback, CPU compositor
or transfer of resource ownership. Both native providers use their existing shared
layer execution. Ordinary uniform-DPI picture capture uses that same execution;
this admission does not enable mapped/nonuniform picture-layer combinations.

Antialiased clips (including an aliased child beneath one) and source layers remain
explicitly unsupported for Clear. Invalid colors and earlier recording failures
cannot be revived by a later clear; failed recordings publish no scene bytes.
This change applies to the native portable COM recorder and Windows command-list
translator. It does not change the separate managed CanvasDrawingSession or
CanvasCommandList Clear contracts, nor admit automatic source-host routing.

Focused portable and Windows fixtures cover replacement, repeated/null clear,
resource reuse, retained state, first-failure identity and immutable exports.
Windows command-list counts use an independent native stream summary. The existing
Direct2D GPU fixture exercises actual scene submission/readback, checking every
pixel of translucent and null clears followed by transformed drawing. Existing
provider gates and deadlines are unchanged. These cases are authored; no local
GPU or VM execution was performed. Additional aliased clipped-clear cases cover
the captured nested clip, singular later transform, retained prefix/suffix,
immutable export, empty intersection, null/straight/IGNORE alpha and mixed-DPI
history. The same eight integral/fractional physical reference variants run cold and warm on both
native providers, with exactly three semantic draws, nine commands and one
submission. Windows additionally compares every pixel against the original
Microsoft WIC render target and exercises original command-list streaming plus
direct sink callbacks. These are authored acceptance gates, not successful
execution evidence; hosted CI must qualify the change before merge.

Fractional aliased clips preserve their original float bounds while checking
the independently expected physical sample coverage. Exterior/history pixels,
binary colors and binary alpha are exact; only nonbinary UNORM8 color conversion
allows one byte of rounding difference against the original WIC reference.
