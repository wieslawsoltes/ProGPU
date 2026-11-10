# Native ordered replay program

Implementation-only port of the original native replay compilation stage. No
build, test, fixture, GPU execution or performance run was performed for this
increment, following the required port-first order.

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` (ordinary bundle/layer/effect compilation, approximately lines 4380–5920) | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_draw_merge.hpp` (reused through the existing Rust native draw records) | `9babc7b9197f3be3652a5fe976696bbaa53f243e8539e2b3625ce3e7ecebda15` |
| `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp` (occupied tile/captured-page quad writers) | `85f794649c452e40c177d06aedd341098f0c73cbb56bfe1aabed4b907128a672` |

`NativeReplayPlan::compile` consumes the complete immutable
`NativeSceneResourcePlan` and its actual assigned `NativeLayerTargetPlan`. The
result retains both owners, the ordered operation inventory, exact shared layer
vertices, matched push/pop indices, source cache identities/revisions, and
source compilation metrics. Candidate errors publish no program or cache
mutation. Bounded Rust scratch holds scope and output records; it does not
fabricate render attachments, sampled images or source callbacks.

The compiler preserves all ordinary vector/path/glyph/image/3D family page
indices, original contiguous merge predicates, first-command provenance for
merged draws, physical target/scissor/depth/mask transitions, and source family
switch counts. Page indices advance for clipped draws. Clear and RGB remain
bundle barriers even when no GPU work survives clipping. Visible RGB retains
the original occurrence page, actual materialized opaque target allocation and
per-draw mask request; it never falls back to scalar glyphs or root opacity.

Layer handling preserves materialized versus state-only scopes, transient and
retained-cache slots, matched content/effect cache eligibility, background
initialization, root destination copy, and distinct push/pop effect ordering.
Composite geometry retains original local-cache transforms, guideline
placement, tile and occupied-page sampling, alias bounds and linear-byte
policies. Advanced operations retain both original resolve/copy quads and the
exact consecutive source axis-clip ancestry, including first-operand min/max
behavior. Masks use their actual source/parent frame and optional original
composite guideline state; no coverage envelope replaces them.

Builtin effects use the original outer presentation and the full preflight
256-byte uniform slab accounting. Shader descriptors retain all six raw wire
versions, owned bytecode, input/sampler resource identities, derivative and
capture/sample/affine frames, actual parent presentation, initial source-state
DPI, and original parent lattice. Fully clipped final-sample shaders retain
their source descriptor without capturing unused dependencies. Actual shader
translation, dependency capture and GPU ownership belong to the separate
native shader adapter. Original replay bookkeeping reports 528/592/608 bytes;
the actual shader records are 544/608/624 bytes and must be sized/reported by
that adapter. These independent source facts are not silently conflated.

The CPU program describes cache eligibility, never a fabricated cache hit.
Actual submitted output generations, render-bundle realization, effect/mask
GPU resources, attachment replay and queue retirement are separate consumers.
`draw_calls` preserves the original ordinary compilation count; RGB direct-pass
execution reports its own actual draws. Existing provider, native package,
browser, source application, pixel and performance qualification gates remain
open independently of this source port.
