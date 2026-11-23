# Ordinary native atlas text and retained color transport

This connected slice adds the ordinary native atlas route for actual
`NativeDesignGeometry` paragraph, fragment and vertical owners, independently of
`NativeDesignVectorDraw` and actual hinted captures. It directly ports original
ProGPU-owned code. The existing `DESIGN.md` architecture research informs source
generation ownership, deferred actual-target preparation, immutable publication
and separate CPU font/GPU coverage responsibilities; no foreign engine source,
managed shaping executor or CPU glyph coverage producer is introduced.

| Original source | SHA-256 | Scope |
| --- | --- | --- |
| `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp` | `904db81b61d4cd3e9618d6377c4aa3ad593ecfab07e978df5efe0f41f09e6460` | `append_glyph_run`, native double affine scale/inverse/placement, 4..128 raster em, TrueType header admission, four quarter-phase descriptors, original bold/italic and physical-to-logical ratio |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_execution.cpp` | `9b131d1891faa278bb217a4ffb5558a426185dbc9ce2c48e15c5195d8dedd51b` | Native raster extents, descriptor/source slots, instance bearing/size/flags and canonical GPU transport |
| `src/ProGPU.Native/src/Backend/progpu_native_glyph_raster_identity.hpp` | `e5da3db8815e7eb7cd73791a1f4419e4d73af11f2f53dcf6672c64f74f03d1cb` | Exact bounds/scale/phase/segment-byte coverage identity, independent of arena offset |
| `src/ProGPU.Native/src/Backend/progpu_native_color_glyph_resources.cpp` | `6e0fdd20a9951a857422c0099c1762cc92f9c5e697c1cc860414fa119bbcff60` | Complete retained bitmap inventory and original row-stride/final-row GPU upload transport |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp` | `3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32` | Full color descriptor bounds, extents, reserved words and style transfer admission |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_state.cpp` | `2b801e9cc4673fa49092bdee28ac7c6275672c000f635798ba5767d759b30dd5` | Original float semantic glyph position/basis transformation after double source placement |

`NativeDesignAtlasDraw::{all,selected}` retains actual geometry, native font/run
owners, complete selected occurrence indices and original per-style em sizes.
`NativeAtlasFrame` retains the original double affine coefficients and independent
hinting/baseline policy. Recording changes no source positions. At the actual
target boundary `prepare` admits all selected slots and styles, computes the
native maximum affine singular scale and clamps the original raster em to 4..128.
Ordinary placement preserves native ties-even physical quarter phases through
inverse mapping, the independent baseline-preservation flag and Animated policy.
Reflected/off-diagonal placement retains the original unsnapped phase-zero route.
Prepared instances preserve source ordering, actual basis and synthetic passes.

The original native MIL producer reads `glyf`/`loca` header bounds and static
TrueType outlines. This adapter retains precisely that ordinary admission:
nonzero variation coordinates and CFF outlines reject on this route instead of
being silently replaced or assigned a different bounds policy. The existing
explicit native vector route still owns native gvar/CFF1/CFF2 decoding. Header
bounds remain separate from segment/control-point envelopes. Every original
nonempty glyph/raster-size group retains four descriptor slots; no-ink source
occurrences remain in the retained owner without a fake raster.

`NativeColorGlyphBatch` transports actual caller-owned RGBA bytes and the original
48-byte descriptors, complete per-source-slot references and explicit frozen
atlas-to-logical ratios/DPI. All descriptors, unused records, reserved words,
row strides and pixel extents are admitted before publication. Complete original
copies preflight all capacities. Already-positioned color records receive no
second glyph-placement snap. The native original color consumer accepts retained
pixels; it does not contain an automatic font-to-COLR/SVG/bitmap producer for this
MIL route. No such producer is fabricated by this API. Color source provenance,
source font selection and original ratio remain caller responsibilities.

`Scene::draw_native_design_atlas` records deferred text. `Scene::resolve_text`
prepares all native plans before beginning atlas acquisition, then appends ordered
shared text styles/instances through the existing canonical `Text.wgsl` pipeline.
Actual source visual/capture transforms compose in double before target planning;
color DPI must still match its frozen source. Source opacity and cached ClearType
suppression remain independent metadata. No source arrays, shader scan arithmetic
or glyph bounds are changed by replay.

The Rust integration deliberately shares the existing original ProGPU dynamic
atlas allocator, batch scheduling and retirement mechanism with other text
families, instead of copying C++'s separate mutable whole-page shelf texture.
Consequently atlas coordinates, capacity competition, allocation/growth counters
and warm-cache residency can differ from a standalone C++ page. The original
coverage extent/phase and canonical GPU request bytes remain unchanged except
for actual atlas/record offsets. Exact native coverage keys compare all canonical
segment bytes and bounds/scale/phase bits. A separate upload key shares the exact
outline buffer slot across all four phases and raster sizes. Keys retain actual
native decoder owners; records from different sources only share coverage when
their complete original coverage identity is equal. Color keys retain the exact
immutable batch/index. Existing failure guards, frame pins, generation resets,
texture growth and submission lifetime apply to these entries.

`GlyphAtlasOptions::native()` supplies the original native 1024/4096 coverage and
256/4096 color dimensions for explicit `Renderer::configure_text_atlas` use;
existing generic defaults remain unchanged. Smaller configured or actual device
limits return capacity errors. There is no scalar/vector fallback when a native
atlas request cannot fit. The independent original whole-page native packing
algorithm and allocation-counter parity are not claimed by this shared allocator
integration. Original glyph providers, source/application/package and pixel
qualification remain open.

`NativeAtlasLease` is an ownership-only compiled command. Scene, CompiledScene
and PreparedScene retain the complete plan/paragraph/color generation even when
all selected slots have no ink. The marker contributes no painted bounds, GPU
draw, device callback or diagnostic source command count. Prepared atlas
residency retains actual texels separately. No ownership cycle exists: native
source geometry/outline owners contain no draw, plan or Scene back-reference.

CPU preparation is linear in selected occurrences plus source segment admission;
canonical hashing/equality visits the original segment bytes. Borrowed source
arrays are never reconstructed. Owned metadata reservations are fallible; standard
Arc allocations keep Rust's process allocation-failure behavior. GPU raster work
uses the existing canonical bounded batching and shaders. No compiler, tests,
browser run, benchmark, fixture generation or performance validation ran under
the implementation-first instruction. Only touched source formatting was used;
no build, benchmark or trace artifacts were created.
