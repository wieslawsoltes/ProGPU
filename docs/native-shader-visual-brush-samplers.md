# Owned VisualBrush shader samplers

Original ShaderEffect sampler properties admit VisualBrush. Microsoft's public
[registration API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.effects.shadereffect.registerpixelshadersamplerproperty)
defines the Brush-valued sampler; the original
[ShaderEffect.UpdateShaderSampler contract](https://source.dot.net/PresentationCore/System/Windows/Media/Effects/ShaderEffect.cs.html)
explicitly permits VisualBrush alongside ImageBrush, implicit input and
BitmapCacheBrush. DrawingBrush is not a legal sampler property. This change does
not admit BitmapCacheBrush or broaden any other brush family.

## Retained ownership and source frame

The native sampler routes an owned VisualBrush through the existing full-RGBA
picture capture, vector tile and `append_visual` renderer. Original visual
content, child order, clips, opacity, transforms and admitted effects use the
same retained rendering path as ordinary VisualBrush paint. It preserves the
complete zero-origin physical sampler extent separately from original source
descendant bounds and from final effect placement. No CPU raster, borrowed
texture/device handle, alternate renderer or shader wire is introduced.

Non-null sources require a genuine initialized same-channel Visual and exact
typed descendant-bounds metadata. A declared but uninitialized Visual fails at
capture. Missing bounds are unsupported, not a request to infer geometry from
the viewport. An initialized source with explicitly empty bounds is distinct
from both; a null Visual is also genuine transparent content.

A separate shader ownership policy traverses every retained visual/drawing
dependency, including hidden descendants and empty paint, using the existing
active-resource cycle and depth budget. It rejects cache, 3D, media and external
image dependencies before publication. Nested DrawingBrush remains outside this
source family. A self-referential VisualBrush cannot manufacture a transparent
capture to evade cycle rejection. The DrawingImage shader policy from #339 and
all ordinary consumers keep their original behavior.

This is complete ownership preflight, not eager semantic evaluation of every
registered current-value leaf. Existing replay owns nested transform/effect,
clip, guideline and source-frame admission. Existing static-angle and animated
sampler-transform restrictions are unchanged. The usual ordered revision walk
still owns capture invalidation; shader preflight does not replace it with a
separate synthetic generation.

## Provenance and status

The connection reuses ProGPU's tile capture, visual replay and resource walker
at `ec94bf3181e04b378294397fe04472928cef6830`. Original API/source research is
used only to establish admission and behavioral constraints; no foreign
implementation is copied. Both native providers consume the same retained child
picture. The real WPF source compiler needs paired VisualBrush sampler admission
through its existing typed visual graph/bounds exporter; a native-only change
does not claim application connectivity.

Controls are authored alongside the implementation. No build, test, syntax
check, verifier, provider/original execution, probe, VM or CI dispatch is
performed. Mechanical MIL ledger regeneration is the only generated operation.
All qualification remains deferred to the final integrated stack tip.
