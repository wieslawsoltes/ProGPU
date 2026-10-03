# Original VisualBrush shader sampler reference

The additive `VisualBrushSamplers.cs` family uses an actual Microsoft WPF
VisualBrush in the original identity shader. It retains one sampled
ContainerVisual root, one inner ContainerVisual, and two ordered DrawingVisual
leaves. The root remains identity mapped, unparented and fully opaque; inner
opacity, clipping and translation are actual original visual state. No source
bitmap, managed substitute renderer or source-local layout approximation is used.

Nine states exercise nonzero descendant bounds, overlapping child group opacity
combined with brush opacity, an absolute right-half viewbox, an inner geometry
clip with an independently fixed viewbox, retained color changes with tiled
addressing, null Visual, the same attached root with no children, refilling at a
negative source origin, and reordered overlapping children with an inner
translation. The source visual, leaves, mutable geometry/color resources, brush,
effect and PixelShader identities remain retained across updates. Original
descendant and content bounds, child order, clips and mappings are recorded;
they do not supply the expected pixel values.

Literal physical color bands compare every BGRA byte of the complete 64x64
opaque-black target over same-owner, warm and independent-literal replays:
nine states and 27 captures. The clipped state deliberately retains an eight-unit
absolute viewbox while its real clipped descendant bounds are only six units
wide. Reversing the final overlap produces a 24-pixel blue band followed by an
eight-pixel green band, independently of the changed bounds origin. These are
authored expectations, not reported observations until original execution passes.

The shared identity shader, evidence writer, strict full-frame convention and
60-second deadline reuse ProGPU-owned `ImageSamplers.cs`,
`ImageSamplerAnimations.cs` and `DrawingImageSamplers.cs` at
`ec94bf3181e04b378294397fe04472928cef6830`. Existing original shader families and
the explicit ARM64 software-unavailable negative control remain unchanged.
Unavailable software shaders qualify zero shader cases in this family too.

The public [VisualBrush contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.visualbrush?view=windowsdesktop-10.0)
supports retained visual content. [AutoLayoutContent](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.visualbrush.autolayoutcontent?view=windowsdesktop-10.0)
concerns unparented UIElements; this fixture uses no UIElements and explicitly
disables that layout policy. It does not qualify arbitrary UIElement layout,
root-placement filtering, cyclic self-sampling, BitmapCacheBrush, external images,
native providers, packages or application hosts.

Authored only: no build, syntax check, test, VM, reference probe, UI, GPU or CI
execution was performed. Complete original, native/provider, source and package
gates remain required on the final integrated tips.
