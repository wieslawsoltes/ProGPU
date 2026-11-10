# Original picture archive materials and geometry

Direct original ProGPU-owned port of `SKPicture.Serialization.cs` brush, pen,
path, texture-patch and indexed-mesh routines. External implementation was not
used. Existing [architecture research](../DESIGN.md#architecture-research-2026-11-08)
continues to govern immutable recording and source/device separation; archive
transport neither initializes a GPU nor executes a font or source callback.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPicture.Serialization.cs`, lines 560–1297 | `c27d0e563e7bef1c874896d13bf2790d3972bee30fb6d1456992cd1e3e116e46` |
| `src/ProGPU.Vector/Brush.cs`, original constructors and mutable properties | `c3a745a0321ad92d9cbfeb2c3f1529b8f7b6e7a979836df7dd8b8635c8a053e8` |
| `src/ProGPU.Vector/ThemeResourceBrush.cs` | `314537f9f6a8fe2c663846dab61c2ff8417966ed3f648ff698a381d1ec3766ef` |
| `src/ProGPU.Vector/BackdropMaterialBrush.cs` | `de6731e8e9fcedc6fefe86c05d42cd6be655d5b0e5e1c677a553fbf1e672ca2a` |
| `src/ProGPU.Scene/RenderCommand.cs`, `TexturePatch` and `VertexMesh2D` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
| `src/ProGPU.Vector/PathGeometry.cs`, raw combined path and figure state | `a246ffa979b6e21f78a48b84017720d70ece894b61eb976909105da5d365bb88` |

`picture_archive/material.rs` implements all fifteen original byte tags, including
null and the two sweep encodings. `ArchiveBrush` retains actual original vector
brush values, unresolved string theme keys or complete backdrop metadata. Common
opacity has one owner. The path-gradient and hatch-set reads call their existing
original constructor ports, retaining full inventory admission; other raw float
properties remain unchanged. Tile reads restore the original zero origin, because
the original wire omits origin. No theme lookup, backdrop capture, font callback,
shader evaluation or fabricated image resource occurs during transport.

`ArchivePen` retains actual brush ownership, all original `PenOptions` and a
nullable immutable double dash inventory. Reading and conversion use raw property
assignment, preserving values that `Pen::with_options` would normalize, including
nonfinite miter/offset fields. Null and present-empty dashes stay distinct.
Clipped-miter writes reject versions below seven; WPF join semantics reject
versions below eight. Stroke transform appears at version three. Hatch transforms
appear at version four, hatch sets at five, tiles/path gradients at six. Missing
older fields use the exact original defaults. Original older writers that omit
figure caps, stroke transforms or hatch matrices retain their original behavior;
no extra version admission rule is inferred. Read enum admission remains separate
from later rendering admission.

`picture_archive/geometry.rs` keeps `ArchivePath` as a raw owned source record:
`IsCombined`, arbitrary signed `Op`, nullable A/B operands, ordinary figures,
fill rule, and independent segment smooth/stroked flags. Deserialization does not
normalize a malformed-but-originally-accepted combined expression. The original
four segment tags retain endpoint, control, arc radius/rotation/sweep fields;
rational segment kinds remain unsupported by writing. Figure cap overrides enter
at version two; no source geometry finiteness or Boolean validity is invented at
archive read. Explicit `to_geometry` requires actual nonnull operands and a valid
operation before producing renderable geometry. `from_geometry` preserves actual
figure inventories and original deferred operands without reconstruction.

`ArchiveVertexMesh` reuses the immutable actual `VertexMesh` generation. All
three topology values, complete positions/texture coordinates/colors and unsigned
16-bit indices cross in original order; only original optional-attribute length
admission occurs. Invalid triangle indices retain the original later draw policy.

`ArchiveTexturePatch` retains original constructor state, including zero matrices
for absent transforms, independent transform-presence, and exact color blend.
Reading executes the source switch: fixed color discards source/transform/blend,
atlas color always retains the transform and explicit blend, unknown tags select
ordinary Texture, transformed Texture selects original Dst blend, and plain
Texture selects default Clear metadata. It does not retain unused raw wire fields
that the source constructor discards. Explicit `to_image_patch` uses identity
only for absent transforms; no raw matrix or source coordinate is reconstructed.
A rendered `ImagePatch` alone cannot recover an original transform-presence flag,
so no guessed lossless reverse conversion is advertised.

All payload work is linear in the serialized inventories with bounded recursive
path depth, using the root's original count/depth/archive limits and private
Reader/Writer publication. The only added allocations are owned source arrays,
raw path nodes and immutable material/mesh owners. No opaque resource bytes stand
in for a missing material or geometry implementation.

The root command/picture/IO layers and peer Scene bridge own their independent
transport/consumer scope. Original package/source/pixel evidence is not claimed.
Per mandatory port-first instructions, no compiler, tests, fixtures, lint, browser,
benchmark or validation ran. Only these new source files were formatted; no build
or trace artifacts were produced.
