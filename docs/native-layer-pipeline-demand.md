# Native layer pipeline demand

The original Gallery acceptance at LibreWPF `7c4b2a9` still failed its
60-second bound. The subsequent bounded
[CPU diagnostic](https://github.com/wieslawsoltes/LibreWPF/actions/runs/38540530202)
retained the original application, source scenes, actions and deadline. Its
108,904 process samples have 21 missing stacks and zero lost events. Exact
loaded-DLL disassembly attributes 2,209 FXC samples to the default unmasked
group compositor, 2,496 to its masked counterpart and 2,353 to a later fixed
group-blend variant. These are sampled compiler stacks, not GPU timings or
proof that the application meets its deadline.

Layer bindings previously required the default unmasked pipeline. Masked layer
preparation also required the default masked pipeline. A specialized compositor
therefore compiled both defaults before selecting its actual pipeline.

## Resource and draw contract

`create_layer_binding_resources` now prepares the original buffers, bind group
and index contents independently of a rendering pipeline. Its readiness flag
publishes only after complete construction. Partial handles remain owned by the
engine and rejected until teardown; a failed construction cannot be mistaken
for a ready binding set or silently reallocated over retained handles.

The existing group-blend selector creates the exact requested default or fixed
variant on first actual demand. Mask bindings no longer imply either default
pipeline. Advanced group blending explicitly acquires its required source
pipeline before opening the source render pass. Later ordinary layers still
create and retain their masked and unmasked defaults.

Both wgpu-native and Dawn compile these shared C++ sources. The sole
implementation provenance is ProGPU's original layer creation, execution,
selector and mask-demand fixtures at
`c254ada12050e934ec7954a8a6d27a0fb76df07b`; no foreign implementation is included.
Original pipeline descriptors, shaders, blend equations, byte-opacity policy,
write channels, geometry, source masks, sample count, uploads, command order,
submission ownership and retirement remain unchanged. There is no new ABI,
adapter/compiler selection, pixel tolerance, cache or asynchronous warm-up.
Selection and readiness checks remain O(1), with one boolean per engine and
the existing bounded pipeline inventory.

The managed audit covers `ProGPU.Scene/Compositor.cs`, its existing
`RenderPipelineCache` and `ProGPU.Backend/GpuTextureLayerCompositor.cs`.
Scene layouts are prepared independently and actual drawing selects the
appropriate cached pipeline. `PrecompileBasePipelines` remains an explicit
caller policy. The separate texture-layer compositor has one constructor-owned
pipeline, rather than a specialized selector with unrelated defaults as
prerequisites. Neither managed path has the corrected native dependency; its
existing preparation and retirement contract therefore remains intact.

## Independent validation

The additive real-provider test observes successful pipeline creation while
forwarding the original descriptors unchanged. Two independent engine owners
exercise specialized-first and ordinary-first sequences, with original mask,
image and nested-layer fixtures. The specialized case explicitly selects the
existing transient linear-byte-opacity policy; it does not change source
geometry or weaken a pixel assertion. Every scene has cold and warm replay,
zero retained warm vertex uploads, exact expected default-pipeline counts and
complete image output.

A separate failure control rejects the uniform bind group after all three layer
buffers are allocated. A second render must reject the partial owner without
reallocation or pipeline construction. Engine teardown must release each buffer
exactly once. Failed submissions are never used as pixel references.

The hosted Metal-provider script explicitly runs the layer-demand CTest under
its original 60-second bound and rejects an absent test. The hardware-evidence
artifact retains its complete raw frames alongside the existing 3D, mask and
picture controls. Registering the test in CMake alone does not select it in
this script's explicit test list.

Local Release validation passed all 53 configured native CTests, including the
original full WebScene corpus, Direct2D GPU tests and both provider contract
suites. The new test's negative control detects eager default compilation in
the exact whole-qualified original producer. Predetermined original/candidate/
candidate/original processes verify their loaded libraries and match every byte
of all 20 frames (SHA-256
`d86fc576d8638a1e209ad8f6341ca4764cd1f2bbfcb0af38400aeb446a39769d`).
The baseline is the actual runtime from whole Build `38522657518`, not a local
reconstruction. These local results do not qualify final hosted packages,
Windows pixels, Instruments performance or Gallery. Those gates remain required.

## Research boundaries

Primary references were revisited on 14 October 2026:
[Direct2D resource reuse and batching](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance)
and [Win2D deferred resource ownership](https://microsoft.github.io/Win2D/WinUI3/html/LoadingResourcesOutsideCreateResources.htm)
support retaining exact device resources when work first requires them.
[SkParagraph's layout/paint API](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h),
[DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
[Parley](https://github.com/linebender/parley) and
[HarfBuzz](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
keep text preparation separate from this compositor change.
[WebRender's retained scene/frame boundary](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
and [Vello's renderer architectures](https://github.com/linebender/vello)
inform that separation without importing their implementation or execution policy.

The adopted concept is lazy, device-owned pipeline acquisition. Layout and
retained-scene reuse, visibility culling, glyph/texture/path cache keys and
eviction, uploads, worker preparation, GPU batching, DPI/subpixel/hinting,
font fallback and variation, and device/atlas invalidation keep the existing
contracts documented in [pipeline research](single-path-raster-pipeline.md#architecture-research).
No timing or memory improvement is claimed from these sample counts or local
functional checks; matched final-binary measurements remain independent gates.
