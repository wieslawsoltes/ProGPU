# Original source-mask hit geometry reference

`eng/WpfShaderEffectReference/VisualMaskInput.cs` adds actual Microsoft WPF
`VisualTreeHelper.HitTest` point and geometry callbacks to the existing reference
runner. The drawing tree uses owners 10/1/7/9 from the native masked-source input
fixture. Expected owners and order are literal, not read from ProGPU scenes or
computed by another renderer. Each positive one-unit region is strictly inside
the expected source geometry; the exact `FullyContains` detail is required.
Every callback, including unexpected owners and details, is retained in the
receipt before assertion failures are reported.

Fifteen states retain the same actual drawing identities through ancestor/local
zero opacity, transparent solid and partial-gradient masks, a DrawingBrush whose
transparent drawing lies outside the source geometry, mask removal/restoration,
singular transform/restoration, triangle and outer scroll clips, and source
detachment/reattachment. A sibling outside the masked local scope distinguishes
clip restoration and result order. Five point/one-unit-region pairs are queried
twice on each retained state and once on a separate source tree: 45 replays and
450 queries. No source `IsVisible` property is invented for DrawingVisual.

The original public contracts are
[visual-layer hit testing](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/hit-testing-in-the-visual-layer),
[IntersectionDetail](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.intersectiondetail?view=windowsdesktop-10.0),
and [ContainerVisual](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.containervisual?view=windowsdesktop-10.0).
These define transparent visual participation, z-order, clip properties and the
direction of region containment. This is original fixture code using public
APIs and literal geometry, not a port of WPF hit-testing implementation.

The additive family shares the existing 60-second deadline, Microsoft assembly
identity check and architecture/source provenance. It queries source geometry
without rendering a bitmap, activating a window, or installing a different
shader/GPU policy. Existing shader families and the ARM64 unavailable-software
negative control are unchanged; that shader control is not an exemption from
source-geometry assertions. The receipt identifies source semantics separately
from GPU pixels, native hit indices, desktop routing and package qualification.

Authored only: no build, test, syntax check, VM, SDK probe, UI, GPU or CI execution
was performed. Original execution and matching managed/native/provider/source
application gates remain required at the final integrated tip.
