# Source visual masks and retained input

The managed hit-only traversal used when a visible source ancestor has zero
opacity rejected masked descendants before reaching their original geometry.
The same traversal is used before source effect/cache composition and for cached
descendants. This is separate from the already implemented Hidden/Collapsed
source-visibility fix: an alpha-zero but visible visual still owns source input.

`ISourceGeometryHitTestCommands.SourceOpacityMaskPreservesHitGeometry` is an
additive default-false producer promise. Only an opted-in visual may omit its
`OpacityMask` or `OpacityMaskPicture` from hit-only capture. This operation neither
reads nor mutates the mask, follows its picture commands, borrows GPU pixels, nor
uses its bounds as source geometry. Each descendant must independently publish
the typed source contract. Generic source providers remain rejected by default.

All existing local/outer/composite/geometry clips, original source commands,
owner IDs, point-only scopes, affine placement and singular-transform omission
remain on the same shared capture path. Nonidentity effects, required cached
picture sources, unknown commands, unavailable clip families and invalid nesting
still fault publication. An optional cache or identity-mapped effect does not
change this new source promise or waive those gates. Raster composition is
unchanged; no mask is removed from a rendered frame.

## Provenance and pairing

Original WPF `381194e`, `PresentationCore/System/Windows/Media/Visual.cs`,
`HitTestPoint` (2035–2208) and `HitTestGeometry` (2271–2418), traverse actual
geometry/scroll clips and inverse child transforms without consulting visual
opacity or opacity-mask brushes. Nonidentity point-effect mapping remains a
separate operation. The source was used only as observable contract evidence;
no foreign implementation was copied or adapted.

The actual LibreWPF `ProGpuRetainedDrawingVisual` producer opts in over its
already retained commands. Its additional property can coexist with the old
pinned interface, but that does **not** prove new interface dispatch in an old
compiled adapter. Final producer qualification, dependency staging and an actual
source adapter rebuild against the new interface are required. Qualified pins
are deliberately unchanged in this authoring batch.

The native MIL path already declares `source_opacity_mask` for uncached visual
masks. Existing raw scene 9842 keeps its original masked/unmasked assertions and
adds zero-opacity ancestor and restored-mask phases with the same three source
owners and exact real clip. There is no native renderer change or alternate
managed index used by native hosts.

Managed authored controls cover brush/picture masks, transparent mask bounds
outside source geometry, own/child/sibling ownership, singular and invisible
subtrees, reappearance, generic rejection, required-cache/effect rejection and
failed-index publication. A real compositor fixture exercises the zero-opacity
early-out and subsequent masked/unmasked visible rendering. Original Windows
point/region reference controls are an additive companion in the same PR.

No build, syntax check, test, verifier, probe, GPU/UI/VM operation or CI dispatch
was run. Final original Windows, both provider/package paths and source application
input remain qualification gates. This does not claim general mask/effect/cache
parity or fix any unrelated source clipping limitation.
