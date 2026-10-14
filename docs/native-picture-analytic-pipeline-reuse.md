# Picture child analytic pipeline reuse

The preserved Gallery diagnostic at LibreWPF `d2e0cf659` uses the complete
qualified ProGPU `340cd628` packages and the unchanged original application.
Its 68,002 CPU samples contain 33,503 FXC leaf samples, 23 missing stacks and
zero lost events. The exact ARM64 DLL maps 5,475 samples to the parent's
analytic primitive pipeline and another 5,428 to the same creation site under
the picture-child render call. Their sampled intervals span about 5.8 and
5.7 seconds. These observations attribute duplicate compilation; they are not
an application latency improvement or Gallery acceptance result. The original
60-second acceptance failed and remains retained separately.

Picture children already borrow their owning parent's immutable base vector
shader, pipeline and layout. Creation validates the same device, target format,
owner thread and live parent and retains the exact provider dispatch. The same
bounded ownership now applies to an analytic pipeline that the parent has
already fully prepared. It does not compile a missing parent pipeline, share
across unrelated owners or introduce a process-wide cache. A child created
without a prepared parent analytic pipeline keeps its original private path.

The borrowed analytic handle remains separate from the child's published
`analytic_pipeline`. Only successful construction of the child's own original
uniforms, brush/gradient buffers, sentinel texture, samplers and bind groups
publishes that readiness. Allocation failures cannot turn a borrowed handle
into ready child state. Every nested child is destroyed before its parent
releases the original pipeline/layouts; borrowed handles are never released by
the child. Existing device-loss propagation and replacement-device creation
retain this ownership order. No resource contents, cache identities, raster
math, shader bytes, draw ordering, submission or physical mapping change.

The authoritative implementation is the original child factory, analytic
pipeline/resource split and destructor at
`57a4f3837bef9ac8a53c85c063357e8d475e7893` (the same source tree as qualified
`340cd628d7b05417b370f35a6f10e24cba9b6197`). Both native providers compile this
shared C++ implementation; no foreign implementation is included. The public
C ABI and package identities are unchanged. Work and storage remain O(1) per
child; retained parent/child resources remain bounded by their existing graph.

## Managed applicability

`Compositor.CompilePicture` and nested `RenderOffscreen`/`RenderOffscreenCore`
execute in the same compositor and use its existing `RenderPipelineCache`.
They do not create a native-style child engine with a second analytic pipeline.
Their existing retained state transactions, target-format keys and disposal
remain authoritative. Sharing unrelated `SharedCompositorCache` scopes would
change a different ownership contract and is outside this fix. Full managed
renderer and headless gates remain required alongside both native providers.

## Independent controls

The additive `--picture-pipelines` provider control uses original state-mask
and picture-mask fixtures in two owner orders, with complete cold/warm frames.
It counts actual provider pipeline creations and releases, requires one parent
analytic pipeline per independent owner, and compares the exact full output
against the unmodified packaged baseline. A third owner injects one child
uniform allocation failure; it must fail closed, preserve subsequent ordinary
parent rendering and release every acquired pipeline reference exactly once.
The baseline mode retains the original two-pipeline expectation. No original
fixture, assertion, pixel reference, corpus inventory or deadline is changed.

Full original native/managed gates, matched final-binary CPU/allocations/Metal
profiling, whole producer and downstream package qualification, and the original
Gallery sequence plus raw capture review remain required. A compiler counter
or a small pixel fixture alone does not qualify application behavior.

Initial local Release validation builds both native providers and passes all
6,143 renderer tests (nine established platform skips) and 230 headless tests.
The additive control preserves all 22 complete cold/warm frames in
baseline/candidate/candidate/baseline order (360,448 bytes per process,
SHA-256 `af9e52e72bf4abe0e44efe063cf28dae9bd87856af1efee6fecf2fa728d60b4b`).
The baseline is the exact complete qualified `340cd628` packaged Dawn library;
candidate validation explicitly verifies the loaded local Release library.
Actual provider calls decrease from two to one per owner. The allocation-fault
control preserves subsequent parent pixels and balanced acquired/released
references. The first draft counter assumed distinct handles per creation;
Dawn's two references to one interned handle exposed that incorrect observer
assumption. Its failed output is retained, and the corrected observer counts
acquired and released references per handle without changing product inputs.
Whole source/package/application and final packaged profiling gates remain open.

The independent [WebScene release exception](webscene-timeout-diagnostics.md)
allows 120 seconds for the original full macOS WebScene CTest case with explicit
owner approval. It does not change this control's 60-second limit, qualify a
performance improvement or resolve the original cold-start objective in #367.

## Research boundaries

Primary references were reviewed before implementation on 13 October 2026:

| Reference | Applicable contract and decision |
| --- | --- |
| [SkParagraph layout/paint contract](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h) | Reuse remains separated from text shaping/layout. Font fallback, variation, DPI, hinting and subpixel state are unchanged. |
| [Direct2D resource reuse](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance) | Reuse an existing immutable resource only within its proven device lifetime. No global cache or eager warmup is added. |
| [DirectWrite layout reuse](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout) | Measured text/layout ownership is independent of this drawing pipeline; no text output is recomputed or altered. |
| [Win2D device-resource lifetime](https://microsoft.github.io/Win2D/WinUI3/html/LoadingResourcesOutsideCreateResources.htm) | Exact device ownership and loss/recreation remain explicit. A replacement device cannot inherit old handles. |
| [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | Retained display lists, culling, upload and cache invalidation stay separate from immutable pipeline reuse. No worker preparation or batching policy changes. |
| [Vello GPU rendering](https://github.com/linebender/vello) | Rasterization/composition remain GPU work with the original shaders and submissions; no CPU coverage or readback substitution. |
| [Parley text layout](https://github.com/linebender/parley) and [HarfBuzz shaping](https://harfbuzz.github.io/what-does-harfbuzz-do.html) | Font/shaping results and interaction generations retain their original CPU ownership, independent of GPU pipeline creation. |

The only adopted concept is immutable resource reuse within the existing
parent-owned device graph. Visibility culling, glyph/texture/path cache keys and
eviction, demand-driven uploads, atlas generations, GPU batch organization and
all fallback policies remain unchanged. Cross-owner resource sharing, changed
shader arithmetic, adapter/compiler switching and deadline extensions are
rejected as solutions to this duplicate creation site.
