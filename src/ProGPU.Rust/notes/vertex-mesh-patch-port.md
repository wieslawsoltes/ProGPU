# Original retained vertices and adaptive Coons patches

`VertexMesh` retains one immutable source generation with original topology,
positions, optional local texture coordinates, straight colors and 16-bit indices.
Copy factories copy once; owned factories transfer fresh arrays without another
payload copy. The byte-color factory keeps original exact optional-array lengths
and RGBA/255 arithmetic. Clones share only immutable owned storage.

Triangles, alternating triangle strips and fans retain their original ordered
triples. Invalid source indices omit the complete triangle and never leave unwritten
index ranges. `VertexMeshDraw` and `Scene::draw_vertex_mesh` emit one contiguous
indexed vector range, sharing the existing renderer, materials, masks and blend
scopes. Generated vertices go directly into reserved destination storage without
temporary per-draw vertex/index arrays. A capacity/index preflight precedes mutation;
the index admission pass is O(T), and publication is O(V + T).

Canonical kind 18/1018 transports premultiplied interpolated vertex colors, original
shader-space coordinates, all 29 original color blend discriminants and separate
semantic opacity. Material registration uses unit semantic opacity; the unchanged
shader applies actual draw opacity after brush/vertex blending. Final positions
use original row-vector arithmetic. The original `ClampToClip` helper is a no-op:
positions remain unchanged and retained clip/mask scopes remain independent.

`CoonsPatch` generates one owned triangle mesh from the twelve clockwise cubic
controls. Original top/right/reversed-bottom/reversed-left control order, 10-pixel
device-space density, minimum 8x8 grid, initial checked vertex count, proportional
200-axis-total cap and exact grid traversal/index winding remain intact. Positions
combine both ruled surfaces minus bilinear corners. Color corners interpolate in
premultiplied space then become straight mesh colors; texture coordinates stay local.
Fresh arrays transfer once into the immutable mesh. There is no per-cell path,
command, brush, GPU draw or CPU pixel rasterization.

Independent XY/color lanes use SIMD; original root/grid order and scalar reduction
order remain explicit. Runtime Vector.Lerp/Vector2.Transform FMA behavior and float
bits remain unqualified. Nonfinite transformed control lengths retain the original
empty mesh. Exceptional float-to-int overflow rejects explicitly; it is not claimed
as a managed runtime conversion match. Original GPU mesh hit-query integration,
image-shader material adapters and portable mesh archive transport remain separate
work; the existing renderer path does not qualify those contracts.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKVertices.cs` | `c9e906affc9b457070c8275ad80d538c913b1f2ce54b0c7a260d9f701080681c` |
| `src/SkiaSharp/SKPatchLayout.cs` | `ef5ffb2543843d560048bf58600ab915f0876e5f8ee9014e355232b90f012195` |
| `src/SkiaSharp/SKCanvas.cs` (`DrawVertices`, `DrawPatch`) | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/ProGPU.Scene/RenderCommand.cs` (`VertexMesh2D`, topology/color enums and retained draw) | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
| `src/ProGPU.Scene/Compositor.cs` (`CompileVertexMeshCommand`, `ClampToClip`) | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` (unchanged) | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |

The shared authored 48-panel host gallery selects all 29 vertex-color modes,
strip/fan/Coons production paths, all fourteen fixed destination modes and original
invalid-index skipping. Desktop selects `--meshes`; browser selects `?scene=meshes`.
This sample is authored but unexecuted and is not validation evidence.

Source formatting only. No compilation, lint, tests, shader/browser execution,
benchmarks, new fixtures or build/trace artifacts. All original output, lifetime,
float, provider/package/application and performance qualification remains open.
