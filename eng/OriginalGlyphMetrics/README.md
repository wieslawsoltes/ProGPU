# Original sideways glyph numeric observer

Source-only authoring checkpoint. **Not compiled or executed.** Permission for a
separate original font probe has not been granted. This program is not connected
to any workflow or product build. Authoring a launcher does not authorize using it.

`OriginalGlyphMetrics.cpp` calls installed original DirectWrite and CPU-only
Direct2D geometry APIs. It creates no rendering target, GPU device, window or UI,
and links no ProGPU library. Its only product-repository dependency is the owned
font-byte fixture chain; it never invokes expected-value or font-decoder helpers.

The fixed inventory has twelve separately identified font families: static TT,
TT without the vhea/vmtx pair, rectangular CFF with/without VORG, original cubic
CFF with/without the vertical pair (both without VORG), variable TT with gvar,
implicit VVAR, mapped VVAR and deliberately conflicting VVAR/gvar, plus CFF2
with fixed metrics and with a vertical-origin map. Existing fixture bytes stay
unchanged; omissions use their original table assembler in separate artifacts.
Six variable families retain the five existing weights 400/650/900/250/100. Two
mapped families also use the exact binary32 coordinate 650.125 to expose otherwise
hidden fractional design-metric decisions. There is no parameter sweep.

Every successful face records raw integer metrics and advance outputs with a
fourth sentinel entry beyond the requested three logical glyphs `[1,0,2]`.
Original BOOL values 0, 1 and -1 remain distinct inputs. Outline callbacks retain
float bits, batch boundaries, figure flags, and original order. Original CPU path
bounds observe cubic extrema without a control-point envelope approximation.
Null advances, signed literal advances and actual API-returned design advances
scaled by the stated binary32 expression remain separately labeled. No output
assertion assumes they agree. Null and nonzero offsets are both retained.

`GetGlyphRunOutline` observes right-to-left BOOL 0/1/-1. Separately,
`CreateGlyphRunAnalysis` observes actual bidi levels 0/1/2/3 with explicit aliased
rendering, disabled grid fitting and natural measuring; only its bounds are read.
No alpha texture is created or read. These APIs are **not DrawGlyphRun**, and
their HRESULTs cannot define DrawGlyphRun admission. In particular the public
[outline API restriction](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getglyphrunoutline)
on combined sideways/RTL is recorded, not used to skip the original call.

Every original API rejection remains an observation. Absent prerequisite objects
are explicitly marked `*_called:false`; the associated wrapper status is not an
observed source HRESULT. A wrapper failure (budget, registration, unavailable SDK)
terminates without a successful receipt and is never promoted to font policy.
Bounds before/after bits and all metric/advance sentinel words remain available
to evaluate whether an actual failed operation preserved its outputs.

The observer is bounded to 60 seconds, 128 callback records per outline, 16 axes,
1 MiB per authored font, and 16 MiB JSON. The future launcher additionally caps
the owned child process at 128 MiB and records compiler/header/PE, operating-system,
loaded module and original font hashes. `qualified:false` is unconditional: these
numeric observations do not qualify rendering or unsupported font families.
