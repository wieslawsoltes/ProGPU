# Local source visual visibility

ProGPU issue [#230](https://github.com/wieslawsoltes/ProGPU/issues/230) reports a
previously rendered masked descendant throwing during hit-only capture after its
WPF ancestor is collapsed. WPF represents local Hidden/Collapsed rendering with
`VisualOpacity = 0`; that is not sufficient to distinguish an excluded subtree
from an intentionally transparent, input-bearing visual.

`PortableVisualState` therefore carries optional local `PortableVisualVisibility`:
Visible (0), Hidden (1), and Collapsed (2). Omitted state retains existing visible
behavior. Source adapters must publish local visibility, not effective visibility
that additionally depends on presentation-source attachment. Detached visible
VisualBrush and cache targets remain valid source roots.

Managed Scene already excludes `Visual.IsVisible == false` before source mask,
effect and cache input admission. The WPF consumer connects the new local state
to that existing policy. Visible opacity-zero geometry is still input-bearing;
an empty own point region still leaves descendants and region geometry intact.
No visible-mask input restrictions are removed.

Native MIL transports a complete sorted visibility sideband, independently of
canonical MIL bytes and point-hit regions. Ordinary visual traversal excludes
Hidden/Collapsed before render/input work. Explicit BitmapCacheBrush capture
continues to omit the root's outer state, including visibility, while its
descendants follow ordinary visibility. The same source handle can participate
in both paths; it must not be replaced by an empty record in the producer.

The change is source implementation and authored regression coverage, not an
application/package qualification. No local builds, tests, GPU or desktop runs
were performed for this change. Dependency-ordered full CI and the original
masked-child visible/collapse/re-show application action remain required.
