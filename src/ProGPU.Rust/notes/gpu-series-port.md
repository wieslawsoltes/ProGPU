# Original retained GPU series

Authoritative ProGPU sources (SHA-256):

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/Extensions/GpuLineSeriesExtensionPipeline.cs` | `b7818e1f55f10f101589604437e856c1d9e6c1659bf3bf05d0dcc0064fa514b9` |
| `src/ProGPU.Scene/Extensions/GpuScatterSeriesExtensionPipeline.cs` | `2cf2ba5ebd68f2859261678dec9d3e21d51cb3e6064aa2a6e191dd637725bac4` |
| `src/ProGPU.Backend/GpuSeriesBuffer.cs` | `acec5c1018e8dc4b8f38faf542b8312235fe32f807326e4ccd0bcfb6cc3db22e` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/RenderPipelineCache.cs` | `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4` |
| `src/ProGPU.Backend/Shaders/ChartLine.wgsl` | `17e4bebe453f45c58726974dc77937d1989afa8711f5d89ce13c76e957841979` |
| `src/ProGPU.Backend/Shaders/ChartScatter.wgsl` | `77c09beef5c2060454c282077e7f4466431535800fd51793b81a011b9a99125c` |
| `src/ProGPU.Scene/Shaders/GpuLineSeries.wgsl` | `b9881b8cfa8dce203fab56e41ead0f9695575b052c22b1a7ac6ddef34def3062` |
| `src/ProGPU.Scene/Shaders/GpuScatterSeries.wgsl` | `7d8d37194f8be43540af9ae4012f6101b1d5474c02aa8a5bace01d9a54399681` |

`SeriesDraw` retains the original immutable point generation, complete matrix,
data scale/translation, width or radius, selected shader family and paint opacity.
Line data stays XY storage and draws six vertices per adjacent pair; scatter
stays twelve-byte XY-radius instancing. NaN line breaks remain shader behavior.
The two line shaders have different original width/coverage arithmetic and are
never aliased. Scatter radius remains physical pixels. Both built-in and explicit
extension routes retain their separate canonical shader identities unchanged.

Archive replay preserves the provider side-buffer's priority over inline data,
including an empty provider range. Exact count-times-two scatter ranges expand
to XY-radius; other admitted ranges retain their original XY-radius prefix.
Built-in command kinds and extension IDs 5/6 connect to painter-ordered Scene
commands. CPU generations can be captured without copying their retained full
source buffer. GPU-only static generations retain the original archive rejection.
Unknown execution preferences are not added to the original archive wire.

GPU realization uses the actual creating device, checked counts/device limits,
original minimum 64-byte allocation and original storage/vertex/upload usages.
`GpuSeriesBuffer` clones retain immutable same-device storage. Cache entries use
exact Arc generation/slice/layout/count/radius identity, retain weak source owners
and are bounded to 4,096 entries and 512 MiB. Eviction drops only the cache owner;
prepared draws and submissions retain their own storage. No global current
context, destructor polling or explicit destruction of a live shared handle is
used. Source replacement creates a new generation instead of mutating a prepared
frame. Original mutable C# cache equality, including NaN and signed-zero warmth,
remains an independent performance/identity qualification requirement.

Each prepared draw owns original 96-byte vertex and 16-byte fragment uniforms,
with the complete source matrix followed by the actual target projection. This
adapts the original mutable uniform cache to independently retained Rust frames;
stable replay writes no series resources. Actual upload, allocation, reuse,
packing, cache residency and uniform counters are exposed by `SeriesMetrics`.
The source's fixed straight-alpha SrcOver and rectangular clip remain separate
from generic retained blend and alpha-mask scopes: the original series drawcalls
do not attach those fields. No extra masked-series shader is fabricated.

Original CPU XY packing has a typed SIMD path with independent four-point lanes
and a bounded scalar tail, plus an explicit scalar reference. Forced GPU packing
rejects because no equivalent original producer is implemented; the resulting
series rendering always uses the original GPU shader. Direct XY/XY-radius upload
does not perform packing. First realization is O(points), warm immutable lookup
is expected O(1), and bounded cache eviction is O(entries). These are algorithmic
properties, not measured throughput claims.

The shared `--series` / browser `?scene=series` sample authors twelve panels for
both shader families, explicit and packed scatter radii, NaN line breaks,
mirrored data, repeated CPU owners and explicit GPU generations. It has not run.

Architecture references were revisited from the primary links in `DESIGN.md`:
Skia canvas and WebRender informed ordered immutable recording; Direct2D and
Win2D informed actual device/host ownership; Vello/Parley and DirectWrite/HarfBuzz
informed keeping GPU drawing independent of source geometry and text ownership.
Only these boundaries were adopted. All executable algorithms and WGSL above
come from original ProGPU; no foreign renderer or shaper implementation is used.

Implementation only: no build, lint, tests, shader execution, native/browser run,
benchmark or package validation. Original exact uniforms/coordinates, cold/warm
pixels, cache lifetime, allocation, device-loss and whole-provider/package gates
remain open. SIMD output and speed, browser execution and source parity are not
qualified by the authored sample or this source translation.
