# Original retained per-draw blend scopes

`Scene::{push_blend,pop_blend}` records the original independent destination-blend
stack. Its state is carried through coverage changes, text realization, actual
source placement and ordered compiled draw states. Each push/pop closes a prior
batch boundary. Coverage pops restore coverage while retaining the live blend;
blend pops restore blend while retaining live coverage. Open recordings cannot
be appended or compiled. Unmatched public pops reject before mutation.

All fourteen original fixed-function modes select actual vector/text/hinted-glyph
pipelines. Original `CreateBlendState` color and alpha factors remain separate:
SrcOver/Src/Plus use SrcAlpha for straight color and One for alpha, while modes
requiring premultiplied output select the existing canonical fragment entry.
Hinted spatial/image paint retains its original branch and full mask-chain entry.
Vector/Text premultiplied entries consume one primary mask; longer ordered mask
chains use the existing owned GPU materialization before that draw. No shader
source, coverage expression, geometric boundary, compiler default or driver changes.

Pipelines are lazy per-renderer/device/format/family/mode/mask variants. Warm replay
selects retained handles without new geometry or uniforms. Image/tile commands
inherit scoped destination blending through their original premultiplied texture
pipelines. Target-storage Clear remains its independent retained operator.
Compiled state materialization publishes the state and then its original consuming
draw; a state change attached to a real draw must not suppress that draw.

This does not implement destination-reading vector blend resolution or approximate
it with a rectangular isolated layer. Such scope modes reject explicitly. Existing
explicit advanced image/layer blending remains independent. Direct filter/shader
commands retain their separate blend contracts and reject unported nondefault
outer scopes; already captured image output can use the ordinary image route.
No arithmetic-blender support is invented: the original Canvas also rejects it.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKCanvas.cs` (`PushPaintBlendMode`, independent state/paint semantics) | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/ProGPU.Scene/RenderCommand.cs` (retained push/pop transport) | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
| `src/ProGPU.Scene/Compositor.cs` (ordered blend stack, source-output and destination-read policies) | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/RenderPipelineCache.cs` (`CreateBlendState`) | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` (unchanged canonical entries) | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
| `src/ProGPU.Backend/Shaders/Text.wgsl` (unchanged canonical entries) | `432bcabab2d670297ceba2a720de8ca2351ffb4609bd619f62ead1fd7cf84d4a` |
| `src/ProGPU.Backend/Shaders/HintedGlyphPaint.wgsl` (unchanged canonical entries) | `1afaa272899c9f498602a9e21e9101540497afa37a8ac0cd44343b1f050014d2` |

Implementation only. No compilation, lint, tests, shader/browser execution,
benchmarks or new fixtures; source formatting only. Original float/coverage,
pixel/provider/application/package and allocation/performance gates remain open.
