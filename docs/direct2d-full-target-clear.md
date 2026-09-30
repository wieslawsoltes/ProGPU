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

Clear inside an active clip or layer remains explicitly unsupported. Invalid
colors and earlier recording failures cannot be revived by a later clear, and
failed recordings publish no scene bytes.

Focused portable and Windows fixtures cover replacement, repeated/null clear,
resource reuse, retained state, first-failure identity and immutable exports.
Windows command-list counts use an independent native stream summary. The existing
Direct2D GPU fixture exercises actual scene submission/readback, checking every
pixel of translucent and null clears followed by transformed drawing. Existing
provider gates and deadlines are unchanged. These cases are authored; no local
compilation, tests, GPU or VM execution was performed. Hosted CI must qualify the
change before merge.
