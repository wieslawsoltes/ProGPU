# ProGPU Rust

Native Rust port in progress. This is not yet a parity-qualified release.
The Cargo manifest is at the repository root so packaged crates can embed the
same canonical shader files as C# and C++ without copied shader forks.

Latest parallel implementation batch: native PNG decoding, all nine native GPOS
families and connected native Latin shaping; immutable color filters and five
original GPU filter kernels; programmable shader translation, v1–v6 resources,
owned captures/final-device draws; retained ROP3 and uniform-backdrop blending.
The continuation connects managed WGSL/16 samplers, full retained color-filter
graphs, exact Texture matrices and connected native Indic/USE/Myanmar/Khmer
orchestration. Unified native ordinary/Arabic/Hangul/Hebrew/Thai-Lao shaping,
fallback marks, stretch, fragment Verify and explicit captured-device arithmetic
are now connected. Full 24-kind image-filter descriptions/planning and five compute
engines are now connected to retained execution and actual source captures. Owned
nested shader recipes retain per-target captures/parameters with rollback and
completed-submission cache retirement. Actual hinted-device run orchestration
consumes the explicit owned provider; the optional original pinned FreeType
producer is now connected through a separate safe API companion.
Shared hosts also author 16 color-filter, 12 managed shader, 24 full image-filter
and 12 owned nested-shader panels. These additions have not been compiled or run.

Latest connected continuation (2026-11-10), implementation only:

| Port area | Newly connected implementation | Remaining qualification |
| --- | --- | --- |
| Native document flow | Nested blocks, shared table tracks/cells, exact width/anchor/collision passes, page/column breaks, retained paragraph generations and atomic import/copy/reflow | Original source/error/lifetime/browser/performance controls |
| Native hinted font producer | Optional original signed FreeType dependency, exclusive safe library/face ownership, interpreter/MM/nominal-size/phase control, checked owned slots and existing cache/shaper/replay integration | Six-RID ABI, native dependency provisioning, original source/pixel/lifetime/package/performance gates; portable/browser providers remain independent |
| Native font providers and catalog | Exact host/browser callback selection/cache, raw fallback preferences and fixed style variations; separate native metadata discovery, nominal cmap matching, file ownership, names/style/embedding and resident SFNT/SBIX writers | Native mmap/path-encoding host adapters and all original byte/error/callback/lifetime/browser/performance qualification |
| Native color/bitmap font queries | Original COLR/CPAL, SVG/raw-gzip, sbix and CBLC/CBDT views, capacity-first decode and source renderability ordering | Actual bitmap/source/provider/pixel qualification |
| Native SVG glyph layers | Bounded byte XML/path/primitives/reference/gradient production, atomic four-buffer output, retained source/font owners and canonical path/material drawing | Host f64 versus native long-double arithmetic, actual text placement/provider integration and all original byte/pixel/browser/performance gates |
| Font subsets | Native and managed glyph-ID-preserving/compact producers, independent malformed-composite policies, original remap/long loca/horizontal metrics/table checksums and atomic caller output | Original byte/error/capacity/ownership/browser/package/performance controls |
| Font storage and residency | Safe owned/static/shared leases, exact SFNT/SBIX glyph extraction, demand bitmap cache, instance-identity matching and actual execution metrics | Native mapped storage explicitly unavailable; original byte/error/lifetime/performance and host-provider controls |
| ImageEffect reuse | Completion-owned parameter/blur pools, original module/pipeline/sampler caches, tracked source/mip/view generations, explicit host-frame aging and actual reuse metrics | Original runtime/pixel/lifetime/browser/performance controls |
| Surface root flushing | Flush/snapshot of the actual root while save layers remain open, retaining child state and atomic prepared ownership | Existing Rust clip continuation remains distinct; original repeated-flush/clip/backdrop/source pixels |
| Picture archive transport | Versions 1–10, all 39 command tags and fields, complete brush/pen/path/mesh/patch transport, typed recording and safe bounded scalar/vector IO | Exact bytes/version/error/stream/ownership controls |
| Archive rendering and capture | Ordered retained Canvas/Scene replay, original analytic triangle/quad/point/grid routes and explicit source-preserving capture; recorded-picture serialization | Unsupported GPU/provider/source-history records stay explicit; complete native/browser/pixel/performance qualification |
| Image filter and picture factories | Exact subset/clip/offset filter application, optional context transfer, painted picture captures and full-sampling image/bitmap/surface Canvas producers | Original source/UV/lifetime/format/filter/browser/pixel/performance controls |
| Target-dependent path and spline replay | Original rounded-path recognition/triangulation, final-target rectangle admission, text-path gamma and complete De Boor NURBS producers | Original numerical/target/byte/pixel/performance controls |
| Hatch boundaries and execution | Exact retained segment/record inventories and canonical dedicated pipeline, preserving ordinary Vector versus explicit extension dispatch; immutable static-Hatch producer, buffers and per-target MSAA execution | Mixed static-DXF/source-provider integration and all source/runtime qualification |
| Late-camera vectors/text | Original ordinary +100 route for fourteen vector/path/stroke kinds; text/glyph runs retain original shared-style/flag ordering, per-command coverage camera, final shared target camera and exact source placement | Remaining provider/extension families and all source/runtime/pixel/performance qualification; original hinted restrictions remain |
| Mixed static DXF | Connected analytic/path/stroke/spline/mesh/Hatch compilation, original +200/clip/flag ordering, common immutable atlas/material uploads and ordered Vector/Hatch GPU draws | Scene extension0, text/providers/nested/3D and special mask/clip cases; all original qualification |
| GPU line/scatter series | Separate built-in/extension shaders, exact side-buffer selection, immutable CPU/GPU generations, same-device ownership, bounded reuse and actual upload metrics | Original cold/warm, byte/NaN/float/pixel/device/browser/performance controls |
| Bounded opacity masks | Original declared bounds, active clip, 2-pixel padding/16-pixel storage and direct R8 Vector/Text/HintedGlyphPaint/Texture capture | Unsupported original mask source families stay explicit; all original source/pixel/lifetime/performance gates |
| Retained interaction | Original command cache/quadtree, strict immutable source-Visual/command/glyph ownership, exact clip/template traversal and paragraph-backed hinted interaction | Actual platform producer adapters and all source/input/lifetime qualification |
| GPU hit queries | Unchanged point/rectangle/ellipse shaders, single-pass and ordered-stage dispatch, same-device immutable index, raw 256-slot outputs/counters and asynchronous mapping | Original full-result/overflow/tie/compiler/runtime/browser/performance/package controls |
| 3D line and ACIS | Owned source arrays, exact generic/extension dispatch, dedicated pipelines, complete ACIS records and actual target camera generation | General late-camera text/path, static-DXF providers and all runtime/source qualification |
| Mesh3D viewport | Complete geometry/material/light/edge producers, canonical solid/wireframe/edge pipelines, depth/MSAA, frozen resource budgets, source texture leases, retained output and six native/browser panels | Original compile/byte/pixel/lifetime/runtime/browser/performance controls; encoder-wide blur dedup and actual platform providers |
| Mesh3D cameras | Original perspective/orthographic projection, raw property/change semantics, view/inverse transform and orbit/pan/wheel/key navigation; shared sample uses both cameras | Exact source/runtime math and host pointer/focus/capture/event dispatch; all pixels and performance |
| Archive materials and scopes | Original immutable theme/alias environments, backdrop factory/owned parameters, independent axis/shared-mask restoration and frozen source scissors | Actual platform snapshots/non-string keys/mutation producers and all source/provider/pixel gates |
| CustomGrid and spline extension | Original fullscreen grid shader/quad, and compile-only/no-draw spline extension4 behavior | Original primary multisample, source/runtime/browser/pixel/performance controls |


Estimated scope completion: **overall about 68%, implementation about 87%, validation
and qualification about 20%**. These are judgment estimates, not measured coverage.

The shared `--series` / browser `?scene=series` gallery authors twelve panels for
both original line/scatter shader families, explicit and packed scatter radii,
NaN line breaks, mirrored data and immutable CPU/static GPU generations. It has
not been compiled or run. See [series provenance](notes/gpu-series-port.md),
[bounded masks](notes/bounded-opacity-mask-port.md),
[geometry](notes/archive-geometry-port.md) and [Hatch](notes/picture-archive-hatch-port.md).
The sample now uses the original [CustomGrid](notes/custom-grid-port.md) extension
as its background. Further connected producers are documented in the
[interaction](notes/picture-archive-interaction-port.md),
[GPU query](notes/gpu-hit-query-port.md), [3D](notes/archive-3d-port.md) and
[material/scope](notes/archive-material-scopes-port.md) notes.
The shared `--mesh3d` / browser `?scene=mesh3d` gallery has six retained 3D panels.
See [viewport integration](notes/mesh3d-retained-port.md),
[Mesh3D sources](notes/mesh3d-source-port.md), [Mesh3D GPU](notes/mesh3d-gpu-port.md)
and [source input](notes/source-visual-input-port.md). These additions remain unexecuted.
The [camera/navigation](notes/mesh3d-camera-port.md) and
[actual hinted producer](notes/native-freetype-binding-port.md) are also implemented.
`native-freetype` is opt-in and requires `PROGPU_FREETYPE_MANIFEST` for an original
prepared native installation; default and browser builds do not load it. The main
crate still forbids unsafe code; the [companion](freetype/README.md) isolates the
private C ABI and exports exclusive safe owners. Existing all-feature CI needs
that explicit dependency provisioning during the deferred qualification phase.
The dependency lock was updated offline without building or running any verifier.
The [late-camera vector route](notes/late-camera-vector-port.md) and separate
[static Hatch producer](notes/static-hatch-port.md) now preserve their distinct
original +100 and +200 shader markers. Both are implementation-only; no build,
shader, browser, pixel or performance validation has run for these additions.
The [native host/browser provider](notes/native-font-provider-port.md),
[system catalog](notes/native-font-catalog-port.md) and
[color/bitmap font queries](notes/native-font-glyph-query-port.md) preserve their
separate original selection and ownership policies. Safe host mapping and original
Windows non-ASCII path encoding remain explicit provider boundaries.
Both original [native subset producers](notes/native-font-subset-port.md) now
expose immutable prepared output and capacity-first caller copies without invoking
a font interpreter. Their full original byte/error/performance gates remain open.
The separate managed subset facade retains its original malformed-input policies.
[Ordinary late-camera text](notes/late-camera-text-port.md) and
[mixed static DXF](notes/static-dxf-port.md) now have connected production routes;
all new execution remains unvalidated.
The [native SVG layer producer](notes/native-svg-layer-port.md) now connects
owned font documents through parsing to retained canonical drawing. Its reported
host-f64 number policy remains an explicit source-arithmetic qualification gap.

The shared `--archives` / browser `?scene=archives` gallery authors twelve
recorded-and-reloaded panels: rectangles, ellipses, circles, rounded rectangles,
triangles, quads, paths, cubic strokes, side-buffer point batches, dot grids,
clipped nested pictures and indexed vertex colors. A supplied font adds positioned
glyph replay. This is an unexecuted sample, not source or byte-roundtrip evidence.
Browser selection now explicitly rebuilds the newly selected archive, backdrop,
visual, mesh, path-effect and cached-picture scenes.

Original runtime-effect source/reflection/binding support is retained. The source
itself rejects executable runtime shaders and blenders; no missing source compiler
is inferred from those explicit unsupported routes. See the
[producer note](notes/image-filter-factories-port.md).

**Mandatory implementation-first workflow:** complete the remaining code before
validation. This entire batch is unvalidated; no compilation, lint, test, shader,
source-differential, browser or performance pass is claimed. Original gates remain
open. Three subagents port independent production stacks while the primary agent
connects retained rendering, samples and package source inventory.

| Latest parallel slice | Implemented | Remaining integration |
| --- | --- | --- |
| Native GPOS and shaping | All nine families/unified script-device orchestration, measured single-run layout and captured-font replay; owned styled/fallback hinted paragraphs, source digit/bidi policy, suffix reflow and raw advance interaction | Actual verified hinted backend, combined paragraph geometry/import, source-double/ideal-unit/exclusion/vertical layers and all qualification |
| Image codecs/raster/surfaces | Exact native PNG plus ICO/CUR/DIB/RLE, codec state, 29 pixel formats, pixmap/bitmap snapshots and CPU producers; owned/borrowed/adopted texture images, portable context caches, readback/mips, surfaces/snapshot COW and actual submission-completion source retention | Original codec backend byte/quality parity, foreign backend/framebuffer providers, remaining format/projective frames and all qualification |
| Color filters | Immutable graph/factories, CPU brush colors, exact matrix packets, direct Texture drawing and full retained matrix/table/nonlinear/constant/compose/lerp graph execution with live mipmaps | Original runtime GPU identity / CPU Clear policy connected; all qualification |
| Programmable shader effects | Original bytecode/v1–v6 plus managed WGSL/16 samplers, owned source/recording recipes, actual-target cache/rollback, immutable captures and typed Wpf visual source-opacity/padding/rebase/cache ordering | Remaining source provider/transport contracts, perspective source compilation and all qualification |
| Full image-filter DAG | All 24 original kinds, immutable factories, source-rebound bounded planning and actual retained GPU result table, Gaussian/color/five compute engines, source captures, full clips and wrappers | Original runtime sources, perspective picture frames and non-affine nested shader transforms remain explicit gaps; all qualification |
| Source captures | Original independent physical/projection/logical/DPI frames, pre-raster source placement, exact clip/mask edges, complete device preflight and owned raw-cache source/revision/device generations | Full source transport and original pixels/lifetime/performance qualification |
| Color spaces and animation | Original ICC ownership/parser, XYZ/primaries/CICP/RGB factories and conversion adapters; complete keyframe/easing/spring/expression timing | Visual property binding, full source integration and all numerical/runtime qualification |
| Cached pictures and visuals | Transactional caches, hierarchy/animation/pages, actual-target traversal/layers/effects/culling/masks and typed Wpf shader captures with exact source opacity/padding; separate twelve-panel cached-picture and visual galleries | Shadow-mask and remaining brush scopes, perspective/camera traversal, source/platform policies and all qualification |
| Hinted fonts and runtime effects | Native backend/FIFO/transport and paired unfitted scan replay; exact captured-font Scene/atlas/GPU replay with solid/spatial/image paint and original shaped-source occurrence ownership; complete runtime reflection/uniforms/children | Actual verified FreeType binding, multi-style and late-camera adapters, advanced destination/R8 variants; runtime shader/blender execution stays unsupported |
| Path measurement/effects | Original contour sampling, analytic slicing, bounded scratch and all nine families; raw/force-close iterators, analytic arc-run conversion and typed stamp metadata; twelve shared host panels | Remaining legacy builder/paint-object integration, rational-cubic/deferred raw policy and all numerical/runtime/performance qualification |
| Four-corner rounded rectangles | Original six classifications, radius normalization, exact containment, inset/outset, axis-preserving transforms and analytic line/arc output | Remaining legacy canvas/paint adapters and all source numerical/runtime/pixel qualification |
| Integer regions | All six rectangle/region Boolean operations, canonical bands, bounded scratch, complete query/iterator snapshots, original clipped path scan and analytic boundary output; explicit SIMD/scalar scan routes | Legacy canvas/clip adapters, exceptional float conversion and all source numerical/browser/performance qualification |
| Destination operations | Retained all-code ROP3 with solid/tile/texture patterns, canonical source/compose/copy passes and explicit uniform-backdrop engine | Automatic uniform-target selection/cache, complete source commands and platform policies |
| Host samples | Shared compositor/bytecode galleries plus 16 color-filter, 12 managed WGSL, 24 full image-filter, 12 owned nested-shader and 12 actual visual-tree panels | Authored only; compilation and execution deferred |

Exact sources, ownership and gaps are recorded in [implementation notes](notes/)
and the [design ledger](DESIGN.md). No existing evidence qualifies this checkpoint.

The desktop `--visuals` and browser `?scene=visuals` sample selects twelve actual
visual-tree panels: direct/per-draw opacity, rotation, optional/required cache,
rectangle/rounded clips, Gaussian/Box blur, shadow, blend and opacity masks. Both
hosts retain the complete `PreparedVisual` snapshot/capture generation through
presentation and expose actual visual preparation counters. The source tree stays
alive across resize; this authored sample has not been compiled or executed.

Current implementation batch (2026-11-09): the managed TrueType and owned CFF1
routes now connect original script/direction/feature planning, complete GSUB stage
ordering, outline/variation metrics, GPOS, kerning, fallback marks, visual ordering,
Arabic stretch and safe-boundary verification. Use
`text::managed_pipeline::ManagedShapingPlan::shape` with explicit
`ManagedPreparationOptions`; the result owns its complete source generation and
retains original font-space records. `scaled` applies the original output rounding
and Y inversion; `shape_design_units` writes reusable screen-space design records.
The explicit `shape_true_type` entry point remains available. `substitute` exposes
the pre-positioning sequence, and plans can be reused for their selected script.

| Current connected port | Implementation | Validation still required |
| --- | --- | --- |
| Managed shaping orchestration | Original complete stage and feature ordering | Matched whole-pipeline source corpus, browser and performance |
| Managed TrueType/CFF1 outlines | Float contours, composite attachment, selected variations, managed CFF INDEX/DICT/FDSelect and Type 2 policy, owned outline/flipped caches | Original geometry/error/cache/float controls; independent legacy CFF fallback remains unfinished |
| Managed positioning/output | Metrics/ignorables/spaces, vertical origins, GPOS, kern, marks, direction, stretch, Verify, scaled/design-unit output and typed font-face boundary | Complete original positioned records, scaling and text pixels |
| Paragraph bidi | Original UTF-16 levels/runs, inline isolates, line L1 and cluster-group L2 visual ordering | Full managed paragraph source controls |
| Native source paragraph widths | Original source-double and SourceIdealUnits fitting with separate integer measures, actual GPOS slice witnesses, soft-wrap space frames, double writer/interaction/reflow and paired version-two resource import/copy through captured geometry | Unvalidated; original nominal GlyphRun raw/source frame admission and selected replay implemented; generic/fragment/vertical paragraphs, actual verified FreeType backend and full source/default-runtime/pixel/performance gates |
| Basic paragraph writer | Original font-run shaping, ASCII path, safe wrapping, tabs, alignment, vertical columns, CRLF/blank rows and atomic reflow | Whole-layout source/browser/performance controls; system fallback providers remain unfinished |
| Styled paragraph writer | Strict source/style/inline-box admission, actual font runs, width/tracking/baseline shifts, safe wrapping, indents/spacing, four tab alignments, justification and atomic reflow | Complete styled source/browser/performance corpus, platform fallback and GPU text integration |
| Retained text interaction | Original advance boxes, carets/affinities, hits, selection, row/vertical navigation and explicit EDIT grapheme ownership | Source controls for empty/wrapped/bidi/multi-font/malformed input and lifetime/error cases |
| Source font metadata | Whole-inventory VDMX/gasp readers, raw-directory validation, independent raster recommendation flags and exact immutable source line-frame ownership | Original metadata/line-frame controls; independent source/package/pixel qualification |
| GPU glyph raster engine | Exact records/uniforms, lazy owned outlines, sparse upload slots, bounded staging/growth, canonical grayscale compute/fragment and binary two-pass dispatch, typed R8/RGBA targets and bitmap uploads | Runtime GPU/source/browser/performance controls; hinted-source ownership, CPU modes and full text compositor integration remain unfinished |
| Text paint and rendering | Original planning/placement, integer-origin preflight, 96/32-byte instances/styles, COLR/SVG/vector fallback, positioned-outline cache, atlas realization and mixed painter-ordered Text/Vector shader execution; shared native/browser text gallery | Late camera/MVP, full clip/layer/mask composition, hinted owners, full managed atlas recovery and source/browser/pixel/lifetime/performance controls remain open |
| Text path placement | Original per-axis scale, exponent-based quantization, phase-grid rounding, padded integer bounds, local quad expressions, aliasing and explicit coverage transfer; owned per-frame source deduplication | Complete managed PathAtlas caching/recovery, viewport figure culling, original float/pixel/performance controls |
| Dynamic glyph atlases | Original independent R8/RGBA shelves and size classes, growth-before-LRU, exact font-instance cache, bitmap/COLR/SVG/outline routing, capacity retry, nested batches, retained residency and cancellation cleanup | Full original allocation/cache/pixel/lifetime controls deferred; native hinted-source integration and complete retained recovery remain unfinished |
| Color font data | sbix strikes/duplicates, CBLC index formats 1–5, CBDT images 17–19, original bitmap presentation metrics, legacy COLR layers/CPAL selection and bounded gzip/UTF-8 SVG documents | Codec byte/error/performance parity, original mapped bitmap provider, COLR paint programs and complete text compositor integration |
| SVG color layers | Original glyph-ID selection, inherited state, bounded references, path/primitive geometry, transforms, solid/linear/radial fills, stable stops, negative-result cache and COLR-before-SVG selection | XML/runtime primitive arithmetic, whole-layer source controls and native/browser layer pixels |
| Canvas nominal fonts and immutable text blobs | Selected managed-font ownership, exact metric/advance/bounds policies, strict encoded glyph APIs, original glyph/bold/warped paths, immutable multi-run blob factories/bounds/placement, safe mutable builder leases, bounded O(1) positioned array reuse and original ordered glyph intercepts; Canvas nominal/blob/warped-path replay with actual-target raster metadata | Unvalidated; mapped storage remains explicitly unavailable; resident sources now connected; all qualification |
| Floating canvas layers and drawables | Actual RGBA16Float source/prior captures and format-owned renderer sessions; format-preserving copies/no-op filters; original CPU drawable generation, scoped callbacks and immutable snapshots; exclusive reusable picture recorder, cull/identity ownership and retained playback/drawable adapters | Unvalidated; four-byte source readback remains separate; complete native/browser/source/pixel/lifetime/performance qualification |
| Native fragment interaction | Exact positioned/advance box and caret writer, retained import, hit/affinity/selection and physical row navigation, with actual paragraph/font owners and caller cluster/level arrays | Unvalidated; complete source/runtime/error/lifetime/browser/performance corpus and independent source coverage production (the original native producer has no multi-style vertical paragraph API) |
| Native ordinary atlas and retained color | Original static-TrueType header bounds, 4..128 raster em, four phases, double affine placement and explicit owned RGBA records; canonical atlas/text execution with no-ink generation leases | Unvalidated; original standalone page packing/counters remain distinct from shared Rust allocator; native/browser/source/ownership/pixel/performance qualification |
| Native design vector replay | Actual captured generic/fragment/vertical font generations, TrueType/gvar/CFF paths, original no-ink slots and explicit vector Scene replay with actual-target raster policy | Unvalidated; ordinary native atlas/color routes and full source/runtime/ownership/pixel/performance qualification |
| Typeface and font catalogs | Owned selected-face/style/table/glyph/variation/palette/stream factories, metadata-only system discovery, browser host registration, ordered language/style/renderability fallback and bounded owner caches | Unvalidated; safe resident sources connected, mapped storage unavailable, host Unicode casing, original OS hinted providers and all native/browser/pixel/performance qualification |
| WOFF1 containers | Exact native bounded two-pass normalization and independent managed zlib policy, original table order/checksum/padding, immutable normalized owners and managed font/factory integration | Unvalidated; original bytes/error/stream/admission/browser/performance controls; WOFF2 is unsupported in the original implementations |
| Canvas image batches | Original lattice/nine-patch/atlas layout, owned sprite transforms and colors, separate managed placement/UV policy, full-texture source/filter preparation and one retained indexed batch | Unvalidated; full source/float/pixel/ownership/performance controls and explicit source-format/perspective gates |
| Canvas and Paint | Immutable paint/mask-filter ownership, independent matrix/clip/save state, actual source/save-layer/filter captures, path/image/picture/text/mesh adapters, exclusive SurfaceCanvas flush/snapshot and complete original CPU fill-path/dash/body/cap/join/winding producer | Unvalidated; actual RGBA16 layers and synchronous drawable snapshots now connected; perspective, remaining font/convenience facades and all qualification |
| Packed path recording | Original contiguous common verbs, O(1) snapshots, used-prefix copy-on-write, lazy translation, derivative bounds, bulk triangles and analytic geometry/measure/effect bridges | Original runtime float/ownership/allocation/performance controls; legacy writer and retained conics now connect below |
| Legacy path builder and source topology | CPU-only packed-to-legacy writer, all arc/conic/relative/primitive APIs, exact sixteen conic render spans with retained source metadata, append/extend/reverse/transform, immutable snapshots and one-time materialization; raw point/topology/SVG queries and Canvas/measure/effect bridges | Unvalidated; source default-runtime arithmetic, exact SVG numeric spelling, unresolved Boolean solver transport and all ownership/pixel/performance qualification |
| Source vector geometry | Complete ordinary figure/segment records, path grammar, primitives/recognizers, finite bounds, full matrix retention, analytic arc transforms/extrema, explicit flattening and polynomial Bezier subcurves | Exact source/default-runtime float controls and final retained GPU lowering |
| Source fill and Boolean painting | Original all-six-family fill/ordinary/stroke-query compiler, open-fill closure, bounded direct/postfix Boolean raster input, explicit GPU reconstruction fallback, lazy general evaluator and retained material drawing; shared gallery | Original record/float/admission/pixel/lifetime/performance controls; full managed atlas cache/recovery, complete stroke/compositor integration remain unfinished; new native signed/staged routes are unvalidated |
| Stroke geometry | Original bounded cap/join writers, separate generic/clipped-Miter/WPF semantics, solid/dashed line-only widening, gap/closed-seam traversal and matching triangle hit tests | Source float/topology/capacity/rejection/performance controls and complete owning API integration |
| Solid stroke painting | Original retained figure/gap/seam traversal; analytic line/quadratic/cubic/arc bodies, bounded affine sections, source-local/legacy/fixed/hairline widths, cap/join topology descriptors and retained material drawing; shared gallery | Original descriptor/float/pixel/lifetime/performance controls; clips and full compositor remain unfinished |
| Dashed stroke painting | Original analytic line/Bezier/arc run construction, gap resets, endpoint overrides, closed seams, bounded linear coverage/bounds, directed terminal-cap filled unions and immutable preparation caches; retained rendering and shared gallery | Original state/topology/float/cache/pixel/lifetime/performance controls; full compositor and release qualification remain unfinished |
| Geometry hit queries | Original line/rectangle/ellipse fill and stroke tests, path fill with polynomial/rational/arc samples, relative/absolute tolerance, boundary/winding and both deferred operands | Complete source query/admission/default-runtime controls; these input queries do not qualify raster coverage |
| Boolean geometry | Original canonical rounded-contour containment, deferred query/bounds classification, immediate results, exact solver transport, two canonical GPU passes, readback and ordered contour reconstruction; retained fill preparation fallback | Full source/default-runtime/GPU/browser output, cancellation/lifetime/performance controls and complete compositor integration |
| Source dashes and materials | Double-to-float dash construction, original ordered line/arc/Bezier traversal, all non-image Brush.cs payloads and admission, pen flags and immutable dash ownership | Source rejection/state/ownership controls, images/effects/interception and full stroke/render pixels |
| Material GPU integration | Original 256/32-byte registration, all eleven non-image kinds, ordered deduplication/capacity policy, path/hatch/gradient records, retained Perlin tables, immutable pages and shared renderer bindings; native/browser gallery | Complete original byte/default-runtime/pixel/lifetime/performance controls; image/effect materials and full compositor remain unfinished |
| Retained vertices and Coons patches | Original immutable triangle/strip/fan inventories, ordered invalid-index rejection, all 29 shader color blends, direct indexed batch generation and adaptive device-density Coons grids; shared authored 48-panel winit/browser `--meshes` / `?scene=meshes` gallery | Unvalidated; GPU hit queries, image-shader material/archive adapters and original float/output/ownership/performance controls |
| Composition surface/mask/shadow sources | Original surface interpolation/stretch/anchor mapping, mutable resource revisions, immutable mask bindings, actual target geometry/source masks, independent shadow opacity sources and owned offscreen-depth/cache identity | Unvalidated; BackdropMaterial/host-backdrop provider, mutable standalone shadow adapter and full source/pixel/performance qualification |
| Per-draw destination blend scopes | Independent retained blend stack, all fourteen fixed modes, canonical straight/premultiplied vector/text/hinted pipelines, original mask materialization and image/tile scope transport | Unvalidated; destination-reading vector resolve and remaining direct filter/shader scope transport; all original pixel/ownership/performance qualification |
| Retained image drawing | Native image/patch admission, all 29 patch color blends, eleven reconstruction modes, opacity/alpha policies, projective quads, snapping, extended sampler domains and ordered canonical Texture drawing | Full original record/pixel/ownership/performance controls; managed command-opacity differences, retained color matrices/filter graphs and complete compositor integration |
| Image resources and mips | Immutable strided RGBA/BGRA/R8 owners, device-bound GPU handles, weak-owner upload cache, actual sampler policy, canonical GPU mip generation and occupied-tile/brush mapping; native/browser gallery | Runtime/source/browser validation; full mutable generation, subresource, capture, codec and provider ownership APIs |
| Native signed/staged path programs | Original 63-instruction/16-stack model, raw winding/predicate distinction, native XOR/translated-leaf splitting, explicit inline/staged signed policy, ordered original GPU phases and shared painting/clip gallery | **Unvalidated:** compilation, source bytes/float/dispatch/pixels/lifetime/performance; full native scene/archive transport and managed atlas recovery remain unfinished |
| Direct and materialized masks | Original direct 96/288-byte analytic/texture chains and binary clips, actual GPU red/alpha sources, native clip atlas/transfer, explicit brush/picture producers and automatic 64-component mixed/difference composition; native/browser gallery | Original bytes/float/pixels/replay/retirement/performance controls; source-scene recording/invalidation, native semantic/picture transport and complete source policies remain unfinished |
| Managed ImageEffect commands | Immutable RGB/planar/matrix/luma/spherical metadata, original rect/Matrix4 placement and QuadClipper, actual source-alpha/blend factors, sampler fallbacks, live/inline blur admission and format-aware accounting; Visual ColorMatrix uses this route | Unvalidated; completion-owned pools/tracked-generation deduplication now connected; media providers and full source/browser/package/pixel/performance qualification |
| Owned captures and group effects | Complete eight-node native Gaussian/box/drop-shadow plan, original physical parameters and canonical GPU passes, owned job resources, same-submission scene capture and final image composition; shared effect gallery | Whole-source/runtime/browser/lifetime/performance controls; alternate execution routes, output caches, shader/image filters and source-specific compositing remain unfinished |
| Bounded nested layers | Immutable source scenes, 16-target depth, native outward target bounds, original managed projection/render-origin mapping, nested captures before parent text, final group opacity and all 29 native destination blends; native/browser gallery | Original target/float/pixel/ownership/performance controls; source-specific AA/Clear/opacity policies, revision caches/pools and complete scene compiler remain unfinished |
| Destination-aware blend passes | All fifteen advanced modes use canonical AdvancedBlend.wgsl, exact 96-byte records/256-byte slots, bounded source resolution, actual destination reads and ordered parent/root copies; immutable prepared resources and lazy pipelines | Original bytes/float/pixel/performance/retirement controls; automatic uniform-backdrop selection/caches and complete semantic transport remain unfinished; explicit uniform-backdrop and retained ROP3 implementations are newly unvalidated |
| Target-storage Clear | Original straight colors, target-local binary scissors, canonical replace pass, fresh 16-byte upload per occurrence, bounded/advanced-target ordering and replay counters; shared native/browser gallery | Native open-scope/semantic transport, scene Ignore-alpha targets and nested picture transport remain unfinished; original whole-frame/runtime/performance gates deferred |
| Explicit source AA execution | Original independent-axis projection, outward allocation, exact area masks, consecutive-parent ratios, real background copies, canonical linear-byte/sRGB composition and loaded-scene replay; shared gallery | Complete native open-scope/scene transport, Ignore-alpha targets, original pixels and performance remain unfinished |
| Retained source AA scenes | Immutable typed subtrees, complete closed depth preflight, actual binary-scoped Clear propagation, shared live parent targets and ordered resolves; whole-tree text pinning/final bindings; native/browser gallery | Original builder streams and pixels, small-atlas stress/cancellation/device-loss, replay/resource/performance controls and full source transport remain unqualified |
| Fused image effects | Exact 288-byte records, original color/matrix/luma/spherical operations, RGB/planar bindings, alpha modes, supported direct masks and painter-ordered retained draws; native/browser gallery | Original bytes/pixels/lifetime/performance controls; source command/extension transport, source mask ownership, reconstruction and filter graphs remain unfinished |
| Live image Gaussian | Separate original native/managed builders, 912-byte paired-tap records, unchanged filterable and integer-load planar shaders, two retained GPU passes, native RGBA8 versus managed source/RGBA16F storage | Original host-transcendental/SIMD/format/planar pixels, live-source mutation and allocation/replay/performance controls; imported video/provider generations and full source caches remain unfinished |
| Other font/layout/render work | Earlier independent foundations remain available | Original OS hinted providers, mapped storage, remaining text/compositor/source adapters and complete qualification |


`Scene::draw_image_effect(ImageEffectDraw::new(source, bounds))` records a fused
image draw without GPU work. The original color operations, matrix and spherical
rows live in `ImageEffectUniforms`; source alpha/YUV and primary-mask flags resolve
from the actual retained inputs. Exact compiled inputs remain inspectable through
`CompiledScene::image_effects()`. The direct path accepts one primary mask or up
to three analytic masks; an explicit image mask overrides that chain while binary
clips remain active. Area and mixed/composite chains now use the original retained materialization
path. Cubic/Fant reconstruction remains a separate unsupported fused-shader mode.

`ImageBlurEngine::prepare` retains the original two live GPU passes and actual
RGB, NV12 or Tier-1 P010 sources. Every `encode` evaluates current pixels. Native
RGBA8/double-exponential and managed source-format/float-exponential paths remain
explicit policies. The fused renderer invokes them before consumers and disables
only the completed blur/YUV operations. Ordinary captures remain frozen until
reprepared; deferred AA children replay their owned source work.

Run `cargo run --locked --example winit --features window -- --image-effects`,
or `?scene=image-effects` in the browser. Twenty authored panels exercise color
operations, matrices, spherical rows, native/managed blur, masks, ordinary/AA
composition and RGB/NV12. This gallery is wired and compiled, not yet runtime-
qualified. `image_effect_metrics()` reports direct draws, blur passes/storage,
parameter and geometry bytes; layer/AA counters separately report child work.

`ClipPlan::new` freezes original path-atlas placement and intersect/difference
records. `MaskComposer::prepare_clip` rasterizes unique paths, and `encode` transfers
the original quads and composes their coverage on the GPU. Sampled composition
accepts actual red/alpha textures and original target-local uniform records.
`Renderer::capture_geometry_mask` uses the canonical R8 brush-alpha entry points
with explicit target/local brush frames; `CapturedScene::into_mask` directly retains
an ordinary picture capture's alpha. These producers are explicit prepared-resource
APIs; full source-scene mask recording and revision caches remain unfinished.

`TextureMask` retains CPU pixels or GPU handles with independent mapping, sampling,
channel and opacity. `push_mask_difference` keeps original complement semantics,
including the first difference against an opaque target. Larger or mixed retained
scopes automatically use the composition path. Final coverage remains independent
of binary clips and isolated AA group policy. Run `--materialized-masks` in winit
or `?scene=materialized-masks` in the browser for sixteen shared authored panels.
`mask_composition_metrics()` separates preparation passes/storage from stable replay;
layer/AA metrics carry the same child accounting. Runtime pixels remain unqualified.

`Scene::push_mask`, `push_clip_rect` and `pop_scope` retain direct coverage across
vector/text/image commands. Four analytic masks, or one primary R8 source followed
by three analytic masks, use the unchanged canonical chain shaders. Mixed, difference and longer chains
now materialize through the original GPU composition shader, up to 64 components. These are per-draw
controls; source isolated-layer opacity, AA group composition and target-storage
Clear remain separate. Binary target-axis clips use exact physical pixel-center
admission. `Mask::uniforms` and compiled coverage state transitions are inspectable
without a GPU; complete scopes are validated before text atlas work.

Run the shared gallery with `cargo run --locked --example winit --features window
-- --masks`, or `?scene=masks` in the browser. Add `--text FONT_PATH` or select an
explicit local font in browser mask mode for masked text. Prepared scenes retain
actual mask/pipeline resources, exact uniform/source bindings are shared within
the frame, and weak-owner coverage uploads are reused across preparation. Metrics
report actual mask uploads/storage/bindings and exclude empty-scissor draws.
Runtime and original-source qualification remain deferred.

`effects::EffectChain::plan` exposes original bounded Gaussian/box/shadow
schedules and parameter records without a GPU. `EffectEngine::prepare/encode`
retain original 16x16 passes and aligned uniforms; `apply` submits a complete
chain. `Renderer::capture` renders a prepared scene to a fresh transparent target,
optionally executes its effects in the same submission, and returns an owned
premultiplied image for later opacity/mask/placement. Capture frames and padding
are explicit; `Scene::draw_layer` now resolves declared bounds and nested isolated subtrees.

The shared `--effects` / `?scene=effects` gallery covers six cases including an
eight-node mixed chain; an explicit local font also exercises captured text.
The module inventory now contains eighteen unchanged canonical shaders, including
Gaussian horizontal/vertical, shadow composition, advanced blending and target Clear. Runtime pixels, full source
comparisons and performance remain deferred. See the design ledger for exact
sources, resource ownership and the remaining layer/effect contracts.

`Scene::draw_layer(LayerDraw::new(child))` retains an isolated child in
root logical coordinates. Optional declared bounds include caller-owned padding;
preparation projects and outward-rounds them in the original physical frame and
intersects the actual parent target. Inner masks affect source painting; outer
masks and clips affect the final composite. Effects precede final group opacity.
All twenty-nine original native destination modes are available through `LayerBlend`: fourteen fixed states and fifteen destination-reading modes. The latter
resolve source opacity/masks, blend against the actual parent, then replace the
parent before the next painter-ordered command. Prepared root/source/output
textures support ordinary window/browser attachments without requiring a sampled
swapchain or CPU readback. Each replay shares the caller's submission.

Run `cargo run --locked --example winit --features window -- --layers`, or
`?scene=layers` in the browser. Optional explicit-font input adds bounded masked
text. The gallery covers nested group opacity, all 29 blends, nonzero/fractional
bounds, masks/clips, post-layer painter order, shadows and empty targets.
`PreparedScene::layer_metrics()` reports actual cumulative capture/effect allocation
and subtree upload/draw work separately from root preparation; these are not peak
resident-memory or performance measurements. Captures submit during preparation;
replaying the prepared root performs no layer recapture. Explicit root Clear commands
retain their original per-occurrence uploads, reported separately.
`advanced_blend_metrics()` separately reports root advanced operations, the three
passes per operation plus final root copy, actual scratch bytes and immutable
uniform/geometry uploads. Layer metrics include the same work inside captures.
Root draw counts now include actual advanced source/blend/copy draws; disjoint
scissors suppress the corresponding operation.

`Scene::clear_target([r, g, b, a])` records an explicit storage replacement;
transparent alpha erases existing pixels inside the current binary clip. It does
not inherit draw opacity or geometry transforms. Direct mask scopes reject during
whole-scene preflight, including empty clips; an ordinary layer instead owns its
clear target and applies final opacity/masks when composited. Original straight
colors remain inspectable through `target_clears()` without a GPU.

Each actual Clear allocates and uploads its original 16-byte uniform even during
warm replay. `render_with_metrics` / `encode_with_metrics` report encoded draws,
clears, upload bytes and allocations; `PreparedScene::replay_metrics()` exposes the
frozen work plan. Captures and layer metrics report the same source work separately
from preparation. These counters do not prove completion, pixels or performance.
`Renderer::encode_target_clear` accepts an actual same-device texture and physical
scissor with an explicit `TargetAlpha` contract, including Ignore-alpha. It does
not infer that policy from a clear color, ancestor or sampled image.

Run `cargo run --locked --example winit --features window -- --clears`, or
`?scene=clears` in the browser. The twelve authored panels cover opaque/transparent/
partial clears, fractional/half/adjacent binary edges, nested/empty clips, bounded
masked layers, advanced-blend interleaving, shadows and optional explicit-font
text. Retained AA background preservation is connected below; native scene
Ignore-alpha transport, independent axis viewport/DPI scene transport and nested
picture Clear remain separate unfinished policies. This gallery has static checks only at this checkpoint.

`composition::AxisClipPlan` retains original source bounds, independent DPI axes,
viewport, semantic scalar DPI and actual parent domain. `child` retains all
consecutive AA ancestors; a new plan starts after an ordinary layer. Exact
projected edges, outward allocation, area-mask records and parent/child ratio
bounds are inspectable without a GPU. Background initialization is an explicit
source flag, never an inference from ordinary opacity or masks.

`Renderer::prepare_axis_clip` retains an actual sampled parent and fresh child
storage without reading or painting either during preparation. `begin` clears the
child and optionally copies the parent's current bytes; callers paint into the
actual child via `parts` and consume the token with `finish`. Linear RGBA8/BGRA8
uses the original independent R32Float coverage and byte resolve; initialized
sRGB uses floating replacement, while ordinary sRGB keeps masked source-over.
An unfinished token requires discarding its encoder; dropping it submits nothing.
`Renderer::encode_loaded` draws a prepared scene into initialized storage, including
copying it into an advanced scene's owned root before destination-aware operations.
Original text owner/generation/residency admission remains mandatory.

Run `--axis-clips` in winit or `?scene=axis-clips` in the browser. This shared authored
sample executes twelve explicit groups, including nested ratios, transparent
Clear, independent axes/viewport and loaded advanced layers, then displays the
owned result. Counters describe actual preparation and encoded AA work separately
from the displayed root image. It is an execution gallery, not the automatic
retained AA command compiler. The separate retained integration below connects
immutable subtrees, nearest-owner Clear marking, binary inheritance and text
ownership; full native open scopes/transport remain unfinished. Static checks do not qualify
original source pixels, all target formats, browser reliability or performance.

At the user's request, implementation proceeds in connected batches with
compile/static checks; exhaustive differential/browser/performance/package
validation is deferred until assembly. These additions are **not parity-qualified**.
Managed CFF admission/figure semantics remain separate from native CFF1/CFF2.
Programs requiring the unported legacy CFF fallback return `UnsupportedOutline`;
they are not published as empty glyphs. Earlier measurements below describe their
named checkpoints and do not validate this new connected pipeline.

`text::layout::ManagedTextLayout::new` connects the original basic paragraph
writer to the owned shaper. It takes an explicit `ManagedFontFallback` resolver;
an explicitly supplied `[ManagedFont]` list works for portable/browser hosts.
This is not the original system font discovery provider. Completed generations
own their selected fonts and expose immutable glyph membership. `reflow` creates
a complete candidate; `create_interaction_snapshot` freezes source-row/advance
geometry independently of GPU text realization. `ManagedTextContext` retains
neighboring-context views over the original owner instead of copying a paragraph
prefix/suffix for each font run. `SourceDeviceLineMetrics::capture` admits only
the original exact static square-pixel VDMX frame. These new additions have
compile/static checks only; full source differential validation is deferred.

`text::layout::styled::StyledTextLayout` ports the original separate styled writer,
including strict UTF-16, complete style partitions, U+FFFC boxes, LF-only source
paragraphs, exact paragraph records, NBSP whitespace groups, decimal-field tabs
and non-final-line justification. Output retains original style/font identities
and independent glyph/box/line inventories; tabs emit no glyph. Its original
source does not expose the basic writer's interaction snapshot API. The explicit
`ManagedFont::static_rasterization_recommendations` query returns exact original
gasp metadata only; missing coverage is distinct from a zero recommendation,
and it neither selects a source rendering policy nor executes hinting.

With the `gpu` feature, `text::glyph::GlyphRasterizer` uploads actual selected-font
glyphs or exact raw outline ranges and rasterizes request batches into owned R8
targets. Its 64 KiB uniform / 256 KiB coverage rings retain original 256-byte row
pitch and 512-byte copy placement; oversized compute requests use the original
standalone route. Grayscale supports canonical compute and fragment entries;
binary retains native compute, both ordered passes and its 256-segment bound.
Unsupported forced CPU modes fail explicitly. Target textures and record slots
carry owner/generation identity, and diagnostics count actual writes/passes.
`text::glyph::GlyphAtlas` now connects managed font outlines and decoded embedded
bitmap glyphs to independent R8/RGBA atlases with original packing, growth, LRU
reuse, nested frame batches and opaque retained residency. Call `flush_pending` before submitting a draw that
samples the atlas; `end_batch` also flushes at the outermost boundary. Consumers
must track texture revisions when retaining normalized UVs. Actual wgpu texture
owners survive growth, while failed/cancelled raster claims return their reserved
regions and invalidate upload-cache identities. The current entry points require
finite positive size. Bitmap decoding/allocation precedes the original COLR vector
sentinel and outline route. `ManagedFont::bitmap_glyph`, `decode_bitmap_glyph`,
`colr_glyph_layers` and `svg_glyph_document` expose the original font-data boundaries.
`color_layers`, `svg_color_layers` and `has_color_layers` now connect original SVG
geometry/brush parsing and its per-selected-font positive/negative cache. A matching
legacy COLR base keeps precedence, including an empty base. Successful SVG layers
use the original vector sentinel in the atlas; missing/invalid SVG can proceed to
ordinary outlines. The shared renderer now consumes ordered layer draws. The normal
`image`/`flate2`/`roxmltree` dependencies have independent codec/XML parity
gates; JPEG/16-bit/HDR/error behavior is not certified against StbImageSharp, and PSD
payload decoding is not implemented. Native hinted-source owners, CPU raster modes,
late text transforms, masks and **complete text compositing remain unfinished**. These connected batches have
compile/static checks only; exhaustive validation is deferred as requested.

`vector` now provides the original managed ordinary `PathGeometry`, typed
line/quadratic/cubic/rational/arc segments, complete `Matrix4` frames, primitive
construction and recognition, and the source SVG/WPF path grammar. Arc bounds
and transforms stay analytic; explicit flattening is an opt-in utility. The
shared `DashPattern`/`PathSegment` helpers produce original line or analytic
subcurve spans with exact source dash state and independent stroke flags.
`Brush`/`Pen` retain the non-image source material families, original constructor
admission, independent join policies and double dash intervals. These are
production input records/utilities, not yet a complete vector renderer. Boolean
signed programs, complete compositing and image/effect contracts remain unfinished. Matched source/default-
runtime floating-point, runtime/browser, quality and performance gates are
deferred. Explicit nonprogress rejection replaces malformed source loops; see
the source/ownership/difference ledger in DESIGN.md.

`StrokeCap`, `StrokeJoin` and `Pen::write_join` expose bounded original triangle
writers with full capacity checks before writes. Actual tangent frames remain
separate from line-endpoint subtraction. Generic, explicit clipped-Miter,
MiterOrBevel and WPF smooth/reversal policies retain their original distinctions.
`PathGeometry::widen_stroke` and `contains_stroke` share the original solid/dashed
line traversal and geometry/hit sinks. Widening returns `WidenedPath { path,
complete }`: callers must inspect `complete`, because the original API retains
earlier output when a later figure rejects. Curves must be flattened by the
owning API under its own tolerance/frame contract. Original figure cap overrides
are not consumed by this utility. `contains_fill` and the primitive hit functions
retain the original geometric input queries, including rational curves and both
deferred operands. `None` is source rejection, distinct from a valid miss. All
new stroke/query runtime, source-pixel and performance validation is deferred.

`PathGeometry::combine_deferred` now retains immutable operands and the original
query classification without materialization. `immediate_boolean` exposes the
original resolved-operand fast routes; None means GPU reconstruction is needed.
`compile_boolean_input` and `reconstruct_figures` retain the original solver wire
and contour ordering. With `gpu`, reuse a `BooleanSolver` for the unchanged
geometry/finalizer compute kernels. `begin(...).await` returns owned `BooleanWork`;
`complete().await` uses host-driven polling on native targets and the WebGPU event
loop in browsers. Native `complete_blocking` retains the original five-second
wait bound. `resolve` materializes complete deferred trees in A-before-B order.
Dropped work cancels/unmaps its own staging buffer without polling or destroying
submission-owned resources. Unsupported forced execution modes reject. These
new paths have static checks only; original runtime, pixel, performance and
package qualification remains outstanding.

`PathGeometry::compile_fill_path` now lowers all six source segment families,
retaining filled-figure policy and implicit closure. `compile_path` and
`compile_stroke_query` keep their separate closure/metadata contracts.
`compile_raster_path` produces an immutable `path::Path` for `PathDraw`, including
direct Boolean operands and original bounded postfix programs. None means empty;
`UnsupportedExecution` requires reconstruction through
`BooleanSolver::prepare_raster_path`. Standalone `PathRasterizer` users call
`prepare_boolean().await` before Boolean batches; `Renderer` does so on demand.
The shared material gallery includes all six source families, subtraction and
nested Boolean painting. New runtime/source-pixel/performance validation remains
deferred, as do full managed atlas caching/recovery and signed programs.

`StrokeDraw` and `Scene::draw_stroke` now retain original solid-path painting,
including line/quadratic/cubic/arc bodies, separate local/affine/fixed/hairline
expansion, source gaps, smooth joins, cap overrides and closed seams. `StrokeFrame`
keeps the complete target transform and explicit legacy width-recovery metadata.
`Matrix4::stroke_scales` preserves the managed minimum-scale expression separately
from the native geometry API. Standalone `StrokeDraw::compile` produces CPU mesh
records; scene compilation supplies the actual material binding and final mesh
vertex ordinals. Dashed inputs now use cached source preparation; deferred stroke
inputs still require owning-API resolution. The original solid compositor's
rational-body omission is preserved. The gallery
includes ordinary, affine, fixed-width and aliased-hairline curves. These new
paint routes have static checks only; source pixels and performance remain open.

`PathGeometry::dashed_stroke` now preserves original analytic spans, gap resets,
source endpoint caps, closed seams and optional `DirectedStrokeCaps` metadata.
`prepare_linear_stroke` performs the original whole-inventory/dash-density admission,
run compaction and independent material-bound measurement. Its optional
`fill_coverage` must replace stroke painting when terminal intervals need directed
caps; it is not supplementary geometry. `Scene::draw_stroke` selects this route,
caches complete preparations by immutable source and exact geometry inputs, and
uses the ordinary path atlas for filled unions. Paint changes retain the current
brush while sharing geometry. Normal, fixed and hairline curved dashes keep their
original separate width frames. Standalone `StrokeDraw::compile` accepts dashed
spines but rejects unions requiring atlas coverage; use the scene API for those.
The shared native/browser gallery includes all these routes. Consolidated original
source, default-runtime, browser, pixel and performance controls remain deferred.

`text::paint::TextPainter` now plans original layout and explicit positioned glyph
commands without reshaping or changing source positions. `TextDraw`,
`TextPaintContext` and `TextPathPolicy` retain the actual frame, clip, rendering,
contrast, physical origin and vector-raster contracts. Plans preserve painter
order between atlas requests and COLR/SVG/outline paths. With `gpu`, `resolve`
acquires actual atlas entries inside a caller-owned active batch, flushes before
publication and retains both texture generations and exact residency. Callers
must end or abandon their batch on failure/cancellation and check `is_current`
before replay. `GlyphInstance` and `GpuTextStyle` retain the original 96/32-byte
shader records. `Scene::draw_text` now records these source draws for
`Renderer::prepare_scene`; the actual frame supplies DPI. The renderer interleaves
canonical Text/Vector draws in one pass, batches glyph coverage across all runs,
rebases original style indices, and retains the final R8/RGBA atlas generations.
`Scene::draw_text_path` preserves original per-axis raster scale, exponent-based
scale quantization, ties-to-even phase rounding, integer padding, local quad
arithmetic, aliasing and coverage transfer. Ordinary native path defaults stay
independent. CPU-only `Scene::compile` rejects unresolved text; `text_commands`
allows CPU hosts to inspect and plan recorded inputs.

Prepared text replay checks actual owner, generation, texture revision and every
residency key. Call `Renderer::is_scene_current` and reprepare a stale scene.
Failure/cancellation retires the privately taken text state; no destructor submits,
polls or explicitly destroys live resources. `configure_text_atlas` selects typed
execution/budgets before first text use. The current direct renderer accepts
finite affine placement and semantic opacity; late MVP/camera, clips and retained
zoom contexts reject until their owning consumers are connected. Its existing
projection does not receive the distinct source root glyph-frame certificate.
Full managed path-atlas caching/recovery and viewport figure culling remain open.
Runtime/source/pixel/performance qualification is deferred.

Run the authored shared text gallery with
`cargo run --locked --features window --example winit -- --text FONT_PATH`.
The browser page's **Text font** input selects a local TTF/OTF/TTC (face zero);
the test bridge also exposes `load_text_scene(bytes, face_index)` followed by
`resize`. Both hosts exercise shaping, grayscale atlas text, vector/gradient
outlines, explicit contrast, synthetic styles, affine placement and interleaved
vector overlays. Eligible TrueType inputs additionally exercise integer-origin
positioned aliased text; other font families use a separately labeled fixed row.
Unicode profiles are explicit authored sample inputs. No fonts are copied into
build artifacts. Metrics report actual instance/style bytes, glyph uploads,
raster requests, atlas residency and ordered draw counts. These are integration
samples, not original-source pixel or performance qualification.

`image::Image` owns an immutable strided RGBA/BGRA/R8 pixel generation and its
alpha interpretation. `ImageDraw` retains the original native effective-opacity,
source/domain, patch, sampling, addressing and pixel-snapping contracts; `ImageQuad`
adds the original managed explicit destination/projective transport. `ImageBrush`
lowers axis-preserving fill mapping, and `TileImageDraw` explicitly preserves the
occupied premultiplied tile extent independently of texture-pool padding.
`Scene::draw_image` / `draw_tile_image` preserve order between image, text and vector
commands. CPU compilation can select `compile_with_sampling` using the resolved
native or explicit-shader path; cubic/mip algorithms are never downgraded.

Before preparing images, call `Renderer::configure_image_sampling` with the actual
adapter info and requested `ImageSamplingPreference`. Automatic retains the original
Parallels D3D12 policy; forced native sampling rejects that adapter and explicit
occupied-tile primitives. The native/browser hosts supply this receipt. CPU-owned
images upload without changing channel or alpha bytes, and mip filters lazily build
the original GPU-only linear chain. `GpuImage::from_texture` retains an actual
same-device 2D handle and caller-selected alpha interpretation; it does not snapshot
external pixel mutations or take explicit destruction ownership.

Prepared image scenes retain their exact pipeline/layouts, bindings and textures.
Clearing an upload cache or cancelling another preparation cannot replace those
resources. Upload cache keys retain weak CPU generation owners; `trim_image_cache`
retires dead owners without affecting live submitted bindings. Failed/cancelled
preparation drops its private candidate, with no destructor submission or explicit
texture destruction. Metrics count actual upload payload, texture bytes and mip
passes; replay performs no uploads. The renderer currently uses the original native
patch opacity/admission/attribute route, preserving its sampled-patch V metadata.
This is not qualification of the separate managed command-opacity behavior.

`cargo run --locked --features window --example winit -- --images` and browser
`?scene=images` select the shared authored image gallery. Eleven filters, patch
colors/blends, projective placement, repeat/mirror brush mapping and occupied tiles
use generated tiny input patterns, with no external image assets. The ninth
canonical shader composition, MipGenerator, passes the offline Naga inventory.
Live native/browser pixels, warm-cache/cancellation, source differential controls,
full codec/mutable/subresource/capture ownership and image effects remain open.

`MaterialTable` ports original managed brush registration, including ordered
first-equal lookup, unchanged stop order, source capacity fallbacks, independent
opacity, exact hatch/path/tile packing and a bounded retained Perlin table cache.
`snapshot` returns immutable `MaterialPage` records; `clear` retains allocations
and noise tables. `Scene::draw_material_analytic` takes the actual source center
for the local brush frame; `draw_material_path` retains source coordinates
independently of atlas UVs. `MaterialPaint` owns the brush and semantic opacity.
`compile_with_materials` allows CPU callers to reuse table/cache storage, and the
renderer reuses it internally. Prepared scenes own immutable material bindings
and report actual uploaded bytes/brushes/records; replay performs no uploads.
These integrations and the gallery below have compile/static checks only.

Run `cargo test --locked` and `cargo clippy --all-targets -- -D warnings`.
The CPU geometry API also builds with `--no-default-features`.

```sh
# Desktop window (add -- --frames 4 for a bounded presentation smoke test)
cargo run --locked --features window --example winit
# Shared authored gallery: all eleven non-image materials and painted paths
cargo run --locked --features window --example winit -- --materials
# Actual adapter pixels and lifetime checks; missing adapters fail this gate
cargo test --locked --test gpu_rendering -- --ignored --nocapture
# Original C++ byte-level differential oracle (requires clang++ and checkout)
cargo test --locked --test native_geometry -- --ignored --nocapture
cargo test --locked --test font -- --ignored --nocapture
cargo test --locked --test outline -- --ignored --nocapture
cargo test --locked --test composite -- --ignored --nocapture
cargo test --locked --test cff -- --ignored --nocapture
cargo test --locked --test variations -- --ignored --nocapture
cargo test --locked --test item_variation -- --ignored --nocapture
cargo test --locked --test gvar -- --ignored --nocapture
cargo test --locked --test horizontal -- --ignored --nocapture
cargo test --locked --test vertical -- --ignored --nocapture
# Simple text also requires .NET 10 SDK for the original managed-source control
cargo test --locked --test simple_text -- --ignored --nocapture
# Strict Unicode + original C++/C# properties; .NET 10 SDK and checkout required
python3 eng/progpu-generate-rust-unicode.py --check
cargo test --locked --test unicode -- --ignored --nocapture
cargo test --locked --test bidi -- --ignored --nocapture
# Independent official Unicode 17 levels (hash-pinned temporary downloads)
python3 eng/progpu-test-rust-bidi-conformance.py
cargo test --locked --test grapheme -- --ignored --nocapture
python3 eng/progpu-test-rust-grapheme-conformance.py
cargo test --locked --test line_break -- --ignored --nocapture
python3 eng/progpu-test-rust-line-break-conformance.py
cargo test --locked --test normalization -- --ignored --nocapture
python3 eng/progpu-test-rust-normalization-conformance.py
python3 eng/progpu-test-rust-normalization-profiles.py
python3 eng/progpu-generate-rust-normalization-profiles.py --check
cargo test --locked --test layout -- --ignored --nocapture
cargo test --locked --test digest -- --ignored --nocapture
cargo test --locked --test selection -- --ignored --nocapture
cargo test --locked --test managed_feature_selection -- --ignored --nocapture
cargo test --locked --test managed_context -- --ignored --nocapture
cargo test --locked --test gsub -- --ignored --nocapture
cargo test --locked --test managed_gpos -- --ignored --nocapture
cargo test --locked --test managed_positioning -- --ignored --nocapture
cargo test --locked --test managed_variations -- --ignored --nocapture
cargo test --locked --test managed_names -- --ignored --nocapture
python3 eng/progpu-test-rust-indic-syllables.py
cargo test --locked --lib -- --ignored --nocapture
# Release baseline; reported CPU submission latency excludes the GPU wait
cargo run --release --locked --example retained_benchmark
cargo run --release --locked --example retained_benchmark -- --paths
# CPU-only original cmap + metrics workload (supply a real font file)
cargo run --release --locked --no-default-features --example font_benchmark -- FONT_PATH
# Matched strict decode + script itemization baseline with reusable buffers
cargo run --release --locked --no-default-features --example unicode_benchmark -- mixed 16 4096
cargo run --release --locked --no-default-features --example bidi_benchmark -- mixed 2048
cargo run --release --locked --no-default-features --example grapheme_benchmark -- emoji clusters 2048
python3 eng/progpu-benchmark-rust-line-break.py
python3 eng/progpu-benchmark-rust-normalization.py
python3 eng/progpu-benchmark-rust-normalization-profiles.py
python3 eng/progpu-benchmark-rust-digest.py
python3 eng/progpu-benchmark-rust-selection.py
python3 eng/progpu-benchmark-rust-managed-features.py
python3 eng/progpu-benchmark-rust-managed-context.py
python3 eng/progpu-benchmark-rust-managed-gpos.py
python3 eng/progpu-benchmark-rust-managed-positioning.py
python3 eng/progpu-benchmark-rust-managed-variations.py
python3 eng/progpu-benchmark-rust-managed-names.py
PROGPU_RUST_ACTIONS_INDIC_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py
python3 eng/progpu-benchmark-rust-layout.py
```

The renderer accepts a device/queue pair and produces immutable prepared scenes.
Prepare again when geometry, DPI or physical dimensions change; reuse a prepared
scene for stable frames. `Scene` records analytic and filled-path commands;
`Scene::compile` performs CPU-only placement and atlas planning at the actual DPI.
`Renderer::prepare_compiled` uploads that frozen generation and rasterizes its
unique paths into one owned atlas. Replay uses one indexed draw with original
painter order and no further path rasterization or geometry/frame uploads.
The optional path pipeline is created only when a path scene is prepared.
The example handles resize, scale changes, zero extents, suspend/resume and lost
surfaces. Full device-loss recovery is still pending. GPU/reference tests are
explicit opt-in tests so CPU-only consumers do not need a graphics adapter or C++
compiler; both must run in the port qualification lanes.

## Browser WebGPU

From the source checkout:

```sh
rustup target add wasm32-unknown-unknown
cargo install wasm-bindgen-cli --version 0.2.130 --locked
eng/progpu-test-rust-browser.sh
```

The script builds the browser example, generates matching bindings, installs the
pinned Playwright browser, serves an ephemeral loopback URL and runs real WebGPU
checks. Set `PROGPU_RUST_WASM_BINDGEN` to an isolated matching CLI executable if
another version is already installed. Linux hosts also need Playwright's Chromium
system libraries (`npx --yes playwright@1.62.1 install-deps chromium`). No missing
adapter or shader failure is skipped. On Linux the harness explicitly enables
Chromium Vulkan compositing with its SwiftShader driver. Linux CI runs a visible
browser under Xvfb: `PROGPU_RUST_BROWSER_HEADED=1 xvfb-run -a eng/progpu-test-rust-browser.sh`.
That configuration passes presentation on Ubuntu ARM64 and hosted x64; headless Linux still
produces a black canvas and is not qualified. The receipt records the actual
mode and launch flags. Device loss fails the run, with no automatic fallback.

The reusable site is in `artifacts/progpu-rust/browser`; serve that directory over
localhost HTTP or HTTPS to explore it interactively. The desktop sample uses
winit; the browser sample uses an HTML canvas and requestAnimationFrame with the
same renderer and scene. It serializes asynchronous preparation, physical resize
and tests so mutable WASM borrows cannot overlap. Readback is confined to the
validation bridge; normal canvas replay performs one retained draw.
Add `?scene=materials` to select the same authored gallery as winit's `--materials`.
It exposes material upload/record counts and uses the same retained renderer.
Its source comparisons and native/browser runtime execution are deferred.

The current browser harness includes all nine canonical shader compositions, including the original mip generator;
expanded runtime compilation is deferred. Earlier six-module checkpoints checked
204,800 authored pixels across DPR 1 and 2, including three physical scene DPIs,
all thirteen grayscale contrast values, cold/warm equality, later-generation
retirement, resize, and zero-extent suspension/resume. It also requires actual
compositor screenshot pixels for the background, analytic shapes and GPU paths;
offscreen success alone cannot pass presentation. PNGs and a compact actual
adapter/result receipt are written under `artifacts/progpu-rust/browser-results`.
Chromium software WebGPU is a reproducible additional gate; it does not establish
hardware-browser, original source-font, or complete engine parity. A WASM CPU control also verifies source-retained simple glyph points, instructions,
exact quadratic/composite path bytes, CFF1 Type 2 closure and truncated-record
rejection, CFF2 blended path bytes and variation metadata. CFF1 and variable
CFF2 paths also pass through retained GPU rendering at three
DPIs. Text/image shader
compilation alone does not implement their pending host stacks.

Builds and browser tools stay outside the distributable crate. Delete verified
`target/package` extractions and obsolete test outputs when no longer needed;
keep compact qualification receipts and reusable active dependencies. Use a fresh
disposable `CARGO_TARGET_DIR` for extracted-package checks: normalized archive
mtimes can otherwise reuse a stale compiled library in a shared local target.

## Earlier validation checkpoints

These rows retain the scope of the earlier measured checkpoints. The current
implementation table above includes later connected batches whose full runtime,
source and performance qualification is deferred; older evidence does not qualify
those additions.

| Domain | Implementation | Remaining qualification |
| --- | --- | --- |
| Shader composition | Compile-time canonical vector, path, glyph, binary glyph, text, image modules | Device pipeline and pixel tests for each host path |
| Analytic geometry | Native layout and all three analytic shape expansions; 288 native differential cases | Broader managed/native corpus |
| wgpu resources and rendering | Immutable mixed analytic/path scenes and per-generation atlas bindings; GPU pixels, owner-domain and replay lifetime controls | Cross-provider pixel corpus, device-loss recovery |
| Browser sample and tests | WebAssembly canvas host sharing the winit scene; Chromium WebGPU shader, pixel, DPI/resize and retained replay gates | Additional browser/hardware matrix, device-loss recovery, complete stack |
| winit sample | Native window, DPI/resize, suspend/resume, lost surface handling; mixed Metal and Linux Vulkan/Xvfb presentation pass | Windows runtime and interactive lifecycle controls |
| Paths, strokes and Boolean geometry | All six path segment records; canonical batched masks and retained shared atlases; native extent grouping and physical path placement | Strokes, Boolean/signed programs and original corpus |
| Fonts and outlines | Owned SFNT/TTC, native metrics and cmap 4/12/13/14, standalone reconstruction, simple/composite TrueType records and recursive unhinted paths; native source-arithmetic differential controls | Compressed containers, interpreter, source policies and compiler-default native pixel qualification |
| CFF outlines | Owned CFF1/CFF2 containers, encoded INDEX/FDSelect, private/global subroutines and bounded Type 2 execution; original CFF2 variation blends and source-owned closed paths | Full text integration, original provider/source pixels and broader variable-font corpus |
| Font variation metadata | Allocation-free fvar/avar, item variation stores, encoded deltas and delta-index maps; original fixed/design and CFF2 selection contracts; native differential tests | Full text/metrics integration and compiler-default arithmetic qualification |
| TrueType variation decoding | Owned gvar tables/regions, packed iterators, circular IUP, complete payload preflight, simple-point accumulation, component offsets, horizontal phantom deltas and recursive varied outlines; 462,131 native source-arithmetic cases, twelve browser controls per DPR and three-DPI GPU varied paths | Vertical metrics integration and compiler-default arithmetic qualification |
| Horizontal variable metrics | Legacy HVAR advances, independent strict LSB preparation, cached source-owned scalar/map generations, original base fallback and HVAR-over-gvar precedence; 205,966 native cases and eight browser controls per DPR | Full source placement and compiler-default arithmetic qualification |
| Static vertical metrics and VVAR | Original vhea/vmtx/VORG and static glyf origin fallbacks; independent strict owned VVAR with complete preflight and atomic batches; native differential and ten browser controls per DPR | Source CFF/sideways integration, default arithmetic and full provider/package qualification |
| Simple UTF-16 glyph runs | Owned source/text/cluster/glyph generations, allocation-free mapping and exact scaled advances; 30,086 C++ cases and 10,683 original C# controls; ten browser controls per DPR | Full Unicode/OpenType shaping, bidi, fallback, paragraph layout and text GPU replay |
| Unicode decoding and properties | Strict UTF-8/UTF-16, exact scalar/source records, owned text, initial script runs and nine frozen property inventories; 552,778 C++ cases, 1,114,132 C# property values and fourteen browser controls per DPR | Full shaping/layout and GPU text |
| Bidi level resolution | Complete original embedding/isolate/bracket/weak/neutral/implicit/L1 resolver; reusable caller scratch and owned source generations; 168,077 C++ cases, 102,541 C# controls, Unicode 17 level conformance and fifteen browser controls per DPR | Line/cluster L2 visual ordering, inline direction policies, shaping/layout integration and actual text pixels |
| Grapheme segmentation | Original Unicode 17 state and separate managed compatibility; immutable source clusters and allocation-free UTF-16 starts; 1,888,801 native controls, 89,921 managed controls and 766 official fixtures | Word boundaries, fallback/shaping/layout integration and retained interaction |
| Line breaking | Original Unicode 17 and separate native EDIT selection profile; owned source, caller buffers and exact failure/tail behavior; 2,262,599 C++ cases, 1,688,365 C# controls, 19,338 official cases and fourteen browser checks per DPR | Word selection, width fitting, hyphenation, shaping/layout integration and text pixels |
| Canonical normalization | Original frozen plan plus explicit Unicode 15.1/16.0/17.0 source profiles; native source/tail semantics and original output metadata preserved; 3,668,863 native controls, 4,476,001 managed controls on both macOS and Ubuntu, 1,181,460 profile fixture checks, 36 browser controls per DPR | Source runtime selection and full shaping integration; performance and hosted/platform qualification |
| OpenType common tables and GDEF | Borrowed/owned coverage, classes, lazy lookup validation, GDEF classification/mark sets and exact independent blocklist; 6,415,296 native and 5,374,312 actual C# queries; 26 browser controls per DPR | Complete shaping-plan integration and original text pixels |
| Lookup accelerators | Original five-mask native and distinct three-mask managed filters; exact single coverage and owned format-3 context preparation; 39,859 C++ records / 57,233,864 bytes and 15,728,640 C# glyph queries / 201,347,072 bytes; 15 browser controls per DPR | Complete shaping sequence, native positioning integration and full text pixels |
| Native feature selection | Exact script/language fallback, required/requested/excluded features, ordered variation records and owned selections; 50,956 native controls / 7,157,396 bytes; 1,728 actual C# shared-policy controls; 16 browser checks per DPR | Complete staged shaping and native-selection performance gap |
| Current managed feature policy | Separate immutable UTF-16 request, full fallback/value/global precedence, variation substitutions and optional managed digest; 327,015 actual C# controls / 27,595,740 bytes; 20 browser checks per DPR | Complete script-stage orchestration and full text pixels |
| Managed context and ordered matching | Owned raw managed format-3 contexts, exact weak coverage admission and original glyph/indexer/skip rules; 56,996 actual C# source records, 22,558,608 coverage queries and 1,191,586 ordered matches; 70 browser checks per DPR | Complete shaping/layout/GPU text integration and large-query baseline gap |
| Native GSUB execution | All eight lookup types; contextual formats 1–3; caller-owned 32-byte glyph records, original errors/tails, nested state, GDEF and metadata; 117,973 C++ records / 60,322,260 bytes and 3,968 actual C# shared-policy records / 975,232 bytes | Native GPOS/device positioning, complete shaping, fallback/layout/GPU text and performance qualification |
| Current managed positioning | All nine raw GPOS types, complete attachment state, owned feature/digest plan and layout variation dispatch; 32,008 complete C# plan cases and 760,086 exact delta queries; 151 browser controls per DPR | Metric/default-ignorable preparation, complete varied outlines/vertical origins, native device GPOS, fallback and full shaping/GPU text |
| Current managed variation data | Complete original fvar/avar/gvar/HVAR/GDEF/MVAR admission, normalized immutable instances, lazy tuple cache, simple/component/phantom deltas and MVAR/HVAR/layout queries; 8,772 C# cases / 8,192,752 exact bytes; 96 browser controls per DPR | Full varied-outline/metrics integration and query/frame performance |
| Current managed font names | Original name-table decoding, UTF-16 character ranking, stable ordinal deduplication and real axis-name resolution; 236,128 C# cases / 12,151,088 exact bytes; 93 browser controls per DPR | Broader discovery/source policies, complete outline/metric preparation and end-to-end performance |
| Current managed font and preparation | Owned font/cache/metadata, nominal/symbol/UVS mapping, explicit runtime grapheme and normalization profiles, complete original buffer creation | Varied outlines, source raster policies, fallback and complete shaping orchestration |
| Common managed script actions | Original dotted circle, directional fallback, Hangul, modified combining order, Hebrew, USE diacritics, Thai/Lao, fractions, Arabic joining, presentation fallback, vowel constraints, Khmer/Myanmar/USE stages, Indic preparation and initial/final reordering; 1,882,608 complete C# cases plus complete raw property queries | Full plan sequencing, positioning initialization and full text pixels |
| Indic/Khmer/Myanmar/USE properties | Typed original Indic category/position and USE classification; 2,228,228 actual C# queries plus all 65,536 raw words match native and browser output | Complete shaping-plan integration |
| Khmer preparation | Independent syllable parser; original unsafe flags, broken circles, full-record reordering, cluster merging and feature masks; 1,398,562 private parser observations plus full action controls | Complete shaping-plan sequencing, font GSUB/GPOS integration and GPU text |
| Myanmar preparation/reordering | Separate original stages; independent syllable parser, kinzi/base positions, stable whole-record sorting, matra ordering and bounded scratch reuse; 12,981,319 parser cases and full action controls | Full shaping-plan sequencing, font GSUB/GPOS integration and GPU text |
| USE stages | Original filtered-index preparation, repha/pre-base recording, topographical forms, dotted circles and full-record reordering; independent parser matches 5,870,757 cases | Full shaping-plan orchestration, Indic stages, font integration and GPU text |
| Indic stages | Original preparation, actual selected-font GSUB probes, initial/final whole-record reordering, clusters, masks and init boundaries; 4,768,385 parser cases and complete action controls | Complete shaping-plan sequencing and GPU text; expanded browser and package validation deferred to whole-stack validation |
| Images, brushes, clips, layers and effects | Image/patch/tile drawing, GPU mips and non-image brushes connected in later batches | Full clip/layer/effect, capture, resource and compositing contracts remain unfinished |
| Scene compiler, interaction and archives | Ordered CPU compilation, immutable source/atlas generations and within-frame raster sharing | Interaction, archives, culling, cross-generation cache eviction and transport |
| Performance and distribution | Reproducible retained Release baseline | Matched C#/C++ measurements, platform gates and package consumer |

No C/C++ FFI renderer, third-party text/raster engine, or CPU raster substitute is
used. Existing managed/native provider, source application and package gates
remain independent and mandatory.

The new Rust CI runs CPU/shader validation and package verification on Linux,
macOS and Windows. Linux additionally requires the native C++ oracle, Vulkan
software-adapter pixels and Xvfb winit presentation; macOS also runs the C++
oracle. A separate Linux Chromium job requires the WebAssembly pixel and
presentation harness. These are additive gates. They do not qualify the existing pinned
wgpu-native/Dawn packages, Windows compiler patches, or original source adapters.

## Provenance

Initial source baseline: `cbfd65c6a69c49e10a5dbe2fe35967311efd80cc`.
Geometry ports `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp`
and `progpu_native_geometry_analytic.hpp`; GPU records and projection follow
`progpu_native_gpu_records.hpp` and `progpu_native_pipeline.cpp`.
Shader compositions follow `src/ProGPU.Backend/Shaders.cs` and native CMake.
Path records and validation follow `progpu_native.h` and
`src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp`;
batch grouping follows `progpu_native_path_dispatch.hpp`. Scene path placement,
phase, atlas shelves and exact texel-load admission follow
`progpu_native_path_execution.cpp`, `progpu_native_gpu_records.hpp` and
`progpu_native_path_pixel_mapping.hpp`. Analytic scene padding retains the
original `progpu_native_vector_execution.cpp` formula. Explicit grayscale contrast
retains `progpu_native_glyph_coverage_transfer.hpp` metadata and the canonical
shader transfer; this is not source font or Microsoft pixel qualification.
The compute kernel is
the unchanged canonical `cs_main_single_path` with operation zero.
Font access ports `src/ProGPU.Native/src/Text/progpu_native_sfnt.cpp` and
`Text/Metadata/progpu_native_sfnt_standalone.cpp`. `SfntFace` owns shared immutable
bytes and supports explicit collection selection; missing tables, unmapped glyphs,
and unsupported variation sequences retain distinct results. A cmap lookup is
not shaping, and raw glyph bytes are not a hinted or rendered outline.
Only original ProGPU implementation is ported; external crates remain normal
Cargo dependencies with a committed lockfile.

Simple TrueType records decode through `SfntFace::simple_glyph`. The returned
immutable geometry owns the selected face, preserves full flags/instructions and
lowers to original line/quadratic segments. `glyph_requirements` preflights without
allocation. Single-point contours retain their points and emit no path segments;
composite and empty records retain distinct classifications. This is unhinted
design geometry, not shaped text or a source raster-policy implementation.

Static vertical APIs port `Text/Font/progpu_native_font_vertical_metrics.cpp`.
Neutral VVAR instances port `Text/progpu_native_vvar.cpp` and reuse the original
item-store arithmetic. They retain exact source/coordinate ownership, validate
all raw VVAR directory records and mappings, and expose optional deltas separately
from static origin selection. `VerticalMetricsInstance::variations_into` preserves
the caller's tail and rejects the whole batch before writing. These APIs do not
select CFF source control bounds or admit sideways rendering. A matched baseline
is available with `cargo run --release --locked --no-default-features --example
vertical_metrics_benchmark -- FONT_PATH FACE_INDEX NORMALIZED_COORDINATE`.

`text::SimpleGlyphRun` owns its original font and UTF-16 source; the free
`build_simple_glyph_run_into` / `fill_simple_glyph_advances` functions reuse caller
buffers without allocation. Unpaired surrogates, control mapping, soft hyphens,
first-cluster ownership, original scale order and ties-to-even rounding match
`progpu_native_sfnt_simple_glyph_shaper.cpp`. Advance metrics align with glyph
occurrences; sideways selects supplied heights and does not rotate outlines.
This is the original simple-run API, not full shaping. The managed differential
control links `src/ProGPU.Text/SfntSimpleGlyphShaper.cs` directly; set
`PROGPU_RUST_DOTNET` when using an isolated .NET 10 SDK. No C# or C++ runtime bridge
is part of the Rust crate. A reusable-buffer baseline is available with
`cargo run --release --locked --no-default-features --example simple_text_benchmark
-- FONT_PATH FACE_INDEX REPEATS` (repeats 1..4096).

Browser failures retain the active phase and console errors in `results.json`.
For bounded submission/completion diagnostics, set
`PROGPU_RUST_BROWSER_DIAGNOSTICS=1` when running the browser script. CI runs the
original gate first and only adds a separate diagnostic observation after failure;
that observation cannot replace the failed result. `DEBUG=pw:browser` retains
Chromium process-exit evidence in the job log. Compiler settings, launch flags,
shaders, pixel checks and deadlines are unchanged.

On Linux the separate diagnostic can additionally set
`PROGPU_RUST_BROWSER_GPU_STACKS=1`. It samples CPU counters/wait channels three
times for the test's unique live descendant GPU process and captures one bounded
GDB thread backtrace during final DPR 2 completion. `gpu-process.json` records
actual process generations and any unavailable debugger/permissions. Attachment
briefly pauses the process and can perturb timing; this observation never
qualifies performance or replaces the original gate. No heap/core dump or browser
profile is retained. CI enables it only in the failure-only diagnostic run.

Selection validates the actual browser executable's device/inode, owned ancestry
and original PID generation. Both a separate exact GPU-role argument and a packed
title beginning with the exact executable path followed by the GPU-role token
are supported; an embedded marker alone is insufficient. Identity is rechecked
before sampling and attachment. `capture-gpu-test.py` covers misleading titles,
different executable images, reparenting and PID reuse. For an isolated Linux
diagnostic with an already installed browser, run
`python3 src/ProGPU.Rust/browser/capture-gpu-live-test.py --executable /path/to/chrome --output /tmp/progpu-gpu-probe.json`.
It owns and cleans its temporary browser/profile and records bounded samples and
debugger output. This probe does not run the rendering gate or qualify its pixels.

The line-break module exposes logical scalar boundaries and resolved classes,
with the original context-sensitive rule order and separate EDIT projection.
It preserves scratch-prefix writes on a later invalid scalar while never
publishing break output on failure. Owned text keeps the original encoding and
positions. The default profile matches the original managed resolver; this does
not supply line fitting, dictionary segmentation, shaping or text rendering.

Browser checkpoint `8e5702a52` passes hosted Linux/Windows and Vulkan/winit,
but both original and diagnostic browser runs time out at final DPR 2 completion.
The observed run has four pending submissions and no reported device loss;
prior completions take 22–29 seconds. Compact evidence is retained in
`perf/browser-8e5702-hosted.json`. This narrows investigation without closing the
original gate or changing deadlines, shaders, compiler flags or pixel assertions.

Canonical normalization ships the exact original `UnicodeNormalizationData.bin`.
`NormalizationData` borrows a validated plan, `NormalizationPlan` owns one, and
`NormalizedText` retains original encoded source alongside the normalized records.
The native API admits U+FFFE; explicit `new_managed` / `normalize_managed_into`
preserve the original managed canonical call's rejection. Both use the frozen
ProGPU plan, with no promise to track arbitrary host/runtime Unicode versions.
The managed shaper's grapheme and script exceptions remain separate integration.

`font::layout` exposes validated borrowed views and immutable owned GSUB/GPOS
and GDEF generations. `OwnedLayoutTable::from_face` and `OwnedGdef::from_face`
retain the selected `SfntFace` without copying its table bytes. Lookups validate
all declared subtable offsets on demand; their raw types/flags remain unchanged.
GDEF blocklist policy is explicit and independent of parsing. These APIs provide
shaping data boundaries. `LayoutTable::apply_gsub` now executes the original native
substitution contracts; GPOS and complete shaping remain separate.

Hosted normalization checkpoint `672d632b9` passes Windows, browser and GPU;
Linux's actual managed comparison fails at U+105C9, while its native comparison
passes. The frozen resource decomposes that scalar to U+105D2 U+0307, whereas
that runtime returned its original two UTF-16 units. macOS was cancelled. This
historical frozen-plan mismatch remains recorded; the additive profile API below
addresses explicit source-data selection without changing that frozen API.

A reproducible diagnostic, `python3 eng/progpu-audit-rust-normalization-runtime.py
--output /tmp/progpu-normalization-runtime.json`, rebuilds the original managed
generator under the current runtime defaults. Local Ubuntu/ICU 74.2 lacks 20
of the frozen plan's decomposition records and 20 composition pairs; macOS
reproduces the frozen resource exactly. Compact `perf/normalization-runtime-*.json`
receipts retain the differences. CI runs this audit after an oracle failure while
preserving the failed result. The subsequent explicit profiles below receive full
canonical comparisons; no original test input is excluded.

Explicit source normalization uses `ManagedNormalizationProfile` with
`NormalizedText::with_managed_profile`, `managed_profile_capacity`, or
`normalize_managed_profile_into`. Choose Unicode15_1, Unicode16_0 or Unicode17_0
from evidence about the source runtime. The library does not guess from an OS
or SDK version. Source classes govern ordering/composition; published records
retain ProGPU's original class/script metadata and exact source-range contract.
The owned result retains its chosen profile identity. Existing native, custom-plan
and `new_managed` frozen-plan entry points keep their original behavior.

The managed differential gate observes candidate source facts, then compares the
entire original corpus plus every scalar between combining marks and every
original composition pair. No original input is excluded. Local macOS selected
Unicode17_0 and Ubuntu/ICU 74.2 selected Unicode15_1; both pass 4,476,001 controls.
A new source/runtime still requires full comparison. Script exceptions, font
fallback and complete managed shaping are independent unfinished work.

Profile facts come from hash-pinned UnicodeData files, with no imported engine
implementation. The generator verifies every original canonical decomposition,
the exact older runtime plan inventory, and the original Unicode 17 class table.
The crate's code is MIT; the added Unicode facts carry the bundled
`UNICODE-LICENSE.txt` notice. Microsoft USE specification facts retain their
MIT notice in `MICROSOFT-FONT-TOOLS-LICENSE.txt`. Cargo declares `MIT AND Unicode-3.0`.

Lookup preparation preserves the original native five masks and the independent
managed three masks; `GlyphSetDigest::managed()` projects the exact managed state.
Their positive results remain approximate and can differ. Raw lookup digests keep
the original offset interpretation and require independent executor eligibility.
Exact single-coverage preparation and complete format-3 context preparation retain
their own stricter coverage validation. Invalid lookup headers are errors; an
unsupported optional accelerator is `None`, which never approves lookup execution.

`LayoutTable::build_context_into` validates the entire inventory and both caller
capacities before writing. `PreparedContext` owns the selected layout/font generation,
ordered descriptors and coverage offsets/digests; its borrowed exact coverage queries
do not reparse or copy font bytes. Full ordered glyph traversal and GDEF filtering
remain with the future executor, rather than being inferred from these set filters.

`SelectionRequest` selects the original native script/language/feature policy.
`LayoutTable` exposes allocation-free capacity, bulk/excluded, single-feature and
membership queries. Bulk success sorts unique IDs; single-feature success retains
source order. Required features survive exclusions. Capacity preflight intentionally
does not validate lookup IDs: a later invalid ID can retain the original written
prefix on failure, while `LookupSelection::new` publishes an owned result only on
complete success. Owned selections freeze the actual table/font generation, exact
requested/excluded tags and normalized coordinates.

Feature variations preserve first matching records, signed inclusive conditions,
first duplicate substitutions and original malformed-record behavior. Invalid
alternate pointers skip that feature rather than selecting its original table.
These native APIs do not select shaping defaults or translate language names.
The distinct current managed selection API below supplies C#'s additional policies;
the 1,728 shared-policy comparisons qualify only the common native inventory.
Rust's larger native-selection baseline is slower than C++; the compact
receipt records that gap and the different work performed by the C# control.

`ManagedLookupSelection` retains original raw table/font bytes and a frozen
`ManagedSelectionRequest`. Four-unit UTF-16 `ManagedFeatureSetting` tags preserve
ordinal identity and arbitrary signed values. It follows original C# last-setting,
required/global precedence, exact/DFLT/dflt/latn fallback, language normalization and
last-duplicate alternate substitution. Null matched substitutions still report
active variation use. It preserves managed partial-table admission independently
of the stricter native parser. Selected IDs do not imply executable lookup validity.
Original source indexing exceptions become atomic `FontError::InvalidFace` errors.

The actual original method passes 327,015 records / 27,595,740 metadata/digest bytes,
including actual font-instance coordinates, every fixture prefix, 25,000 mutations
and an exhaustive UTF-16 language-folding witness. Browser controls cover both DPRs,
signed values, global/Jamo rules, distinct native/managed fallbacks, reversed digest
ranges and extreme 32-bit variation offsets. The eight-workload baseline verifies
all selected metadata hashes; 512 static features measure p50 1889.542 µs Rust and
2715.100 µs C#. Ownership and optional context work differ, and compilation activity
was present: these are diagnostic construction measurements, not full shaping or
stable speedup qualification. The later context stage below retains its own evidence;
full lookup execution and staged shaping remain pending.


`PreparedManagedContext` retains original raw table/font bytes and optional
format-3 context metadata. `ManagedLookupSelection::context(position)` borrows
prepared context for that selected entry; absent contexts require no separate
allocation. The returned view preserves original backtrack/input/lookahead order,
lookup flags, three-mask filters and managed linear/binary coverage membership.
Unsorted/overlapping source inventories retain their original query behavior.

`ManagedContextInput` borrows the original accelerator fields from an immutable
glyph generation, freezes active joiner/syllable state and builds the source buffer
digest once. Existing source digests can be reused with `with_digest`; they must
cover all current glyph IDs. Ordered queries allocate nothing. Input/context
joiner rules differ, and the original first-position indexer maps out-of-syllable
glyphs to 0xffff. Flagged GDEF contexts keep the original conservative fast path
after digest intersection. A positive result still requires exact lookup execution.
This API neither manufactures complete shaping records nor supplies a full shaper.

The actual C# context parser, matcher and GlyphSubstitutionBuffer pass 95,829,796
exact bytes, including every scalar through ordered matching, raw visibility and
syllable bytes, mutations and every fixture prefix. Eighteen preparation/query
baseline workloads match complete metadata/result bytes. At 4,096 repeated glyphs,
Rust ordered search is about 39 µs versus C# 29 µs; the gap remains open. Queries
allocate nothing in Rust by construction and zero measured bytes in C#. Broader
performance claims require matched Instruments and complete shaping workloads.

`LayoutTable::apply_gsub(index, position, storage, options, state)` allocates no
memory. `None` visits the whole live prefix; `Some(position)` preserves original
lookup-at policy. `GsubState::glyph_count` selects the live prefix and slice length
supplies capacity. On errors, original earlier mutations and unused storage tails
remain observable. The optional context boundary publishes only after successful
application. This low-level contract is deliberately not transactional. Source,
32-bit offsets, recursion bounds and explicit GDEF/filter generations remain checked.

Run `python3 eng/progpu-benchmark-rust-gsub.py` for 24 matched native/Rust execution
baselines. Buffer reset, allocation and serialization are outside timing; every
complete state/storage byte must match. Native and managed policies still differ
in default ignorables, already-ligated expansion, one-component ligature flags,
malformed-table admission and staged feature handling. The current managed source
comparison qualifies only their explicitly shared mutation contracts.


`ManagedGsubTable` and `ManagedGlyphBuffer` provide the separate current C# nested
executor and reverse-chaining subtable. `ManagedShapingGlyph` retains all 31
original fields, including independent grapheme/character clusters, signed
advances/offsets, script metadata and attachment state. Raw managed coverage,
class lookup, joiner filtering, syllable admission, ligature merging and
already-ligated expansion follow the original ordered operations. Errors retain
previous mutations; private nesting depth and mark coverage restore on return.

`ManagedMarkProfile` explicitly selects the source Rune mark repertoire. It is
independent of canonical normalization and defaults to Unicode 16, matching the
frozen native mark inventory. A complete actual-runtime probe selects the
source profile in differential tests; no operating-system inference is used.
GDEF classes narrow through the original byte enum before zero-class fallback.
Invalid Unicode scalars reject only when original Rune classification is reached.

The original 64-level nested lookup rule remains. Rust additionally bounds
extension chains at 64 hops and nested lookup attempts at 65,536 per operation.
`apply_lookup_at_with_limits` accepts an explicit `ManagedGsubLimits` budget.
Exhaustion returns `FontError::Capacity` with preceding mutations retained; this
is a separate safety policy, not original source equivalence. Two branching
mutation witnesses exceed the original C# control's three-second observation;
they are reported separately from the 74,981 exact-output source cases.

Forty-four browser controls per DPR exercise these current managed contracts.
Run `python3 eng/progpu-benchmark-rust-managed-gsub.py` for 24 full-record matched
C#/Rust nested/reverse execution baselines. Five warmups precede 40 measured
rounds; source font construction, buffer reset/preallocation and serialization
are untimed, while production allocations remain timed. The complete raw stage dispatcher described below builds on this core. Script
preparation/reordering, GPOS, fallback, paragraph layout and GPU text rendering
remain separate work.


`ManagedFeatureOptions` freezes the complete current managed baseline, ranged
feature values and explicit-tag policy. UTF-16 tags retain exact ordinal identity;
ranges retain byte tags, unsigned values and half-open input intervals. Default
list identity remains distinct from a custom list with null explicit tags. The
original signed-cluster and last-applicable-range rules are shared by per-record
script admission and feature-valued alternate execution.

`ManagedSubstitutionPlan` owns original selection, optional context filters,
feature options and exact raw GSUB/font bytes. `apply_stage` executes any of the
42 `ManagedSubstitutionStage` values using original lookup/filter order. It
retains required-feature bypass, already-inserted output skipping, original
forward deletion progress, context continuation, accumulated buffer digests,
reverse order and wrapping random-alternate state. Full records feed context
filters directly through the sealed `ManagedContextRecord` interface; matching
adds no intermediate glyph inventory or allocation.

The nested-call budget applies independently to each root invocation. The source
comparison includes a 131,073-glyph run, which must not fail merely because its
ordinary root calls exceed one recursive lookup's budget. Top-level source
errors preserve their actual partial writes and traversal state; reverse errors
can retain selected mark coverage, unlike nested calls' finally restoration.
The independent original fraction-preparation marker is explicit buffer state.

The source dispatcher passes 262,144 feature queries, 1,974 stage-policy queries
and 26,753 complete stages / 46,818,828 bytes. Browser validation adds 145 controls
per DPR. `python3 eng/progpu-benchmark-rust-managed-dispatch.py` compares 24 full
raw-stage workloads against the original C# executor, including all record and
retained state output. The initial diagnostic capture had high host contention;
its timings are not performance qualification. This API executes already-prepared
metadata; it does not supply full script preparation, reordering or complete
paragraph shaping.


`font::layout::ManagedGposTable` executes all nine current managed raw positioning
lookup types on an owned `ManagedPositionBuffer`. It retains all 31 record fields,
original per-add saturation, filtering, partial error state and actual source
ownership. `resolve_attachments()` and `zero_mark_advances()` preserve original
attachment cycles, direction and late-zero policy. Contextual and attachment
traversals use explicit stacks; the separate per-root work budget is configurable.
Inputs are already-prepared design-unit records. Feature/variation dispatch,
metric initialization, fallback, native device GPOS and complete shaping remain
separate work. The original C# oracle verifies 63,557 full cases; browser validation
replays 80 exact C# fixtures plus a cyclic-work control at both DPRs. See the design
ledger and `perf/managed-gpos-baseline.json` for scope and matched baseline evidence.


`ManagedPositioningPlan` retains an immutable selected font, original lookup
selection, feature options and already-normalized instance coordinates. `apply`
performs the original raw lookup and variation pass in order and returns the
plan's kerning presence, independently of whether any glyph changed. Attachment
resolution stays explicit. `ManagedLayoutVariations` preserves complete-font
reads, eager metadata admission, lazy selected-row failures, exact ordered float
arithmetic and original missing-store behavior. Oversized inventories fail safely.

The prepared-instance boundary is deliberate: the oracle calls the actual C#
`WithVariations`, captures its actual normalized coordinates, and compares the
resulting plan, deltas and complete records. **32,008 cases, 760,086 delta queries
and 44,353,296 output bytes** match, including 2/3-axis products, malformed leaf
and store data, unusual dispatch policies and a 131,073-glyph run. This does not
port or qualify C# instance creation, its fallback on invalid metadata, full
metric preparation or complete shaping. **151 captured C# controls** replay in
WASM at both DPRs; the mandatory source oracle verifies the fixture byte-for-byte.

The complete-plan benchmark compares 30 exact-output workloads with actual C#,
using cached plans and prepared buffers. Five warmups and 40 measured rounds
retain production allocation; preparation/reset/serialization stay outside timing.
`perf/managed-positioning-baseline.json` records hashes, load and all quantiles.
This is a diagnostic baseline, not an optimization or frame-performance claim.
The hosted `deaf86ae6` Linux source/package, Windows and Vulkan/winit jobs pass;
its original and diagnostic browser runs fail at final DPR 2 capture with GPU
process exit 512 and device loss. Original flags, deadlines and qualification
gates remain unchanged.


`font::ManagedVariationData::from_face` ports the complete original managed
process-neutral metadata constructor. Its name resolver is explicit, as in the
original constructor; callers supply the selected source's name policy. Invalid
metadata returns `None`; capacity failures stay errors. Axis ordering is validated
at the original instance boundary. `create_instance` retains last matching UTF-16
settings, original float/fixed conversions, ordered avar mappings, exact normalized
cache-key bits and HVAR scalar inventory. Instances and lazy glyph caches retain
the same immutable selected source across clones.

`ManagedVariationInstance` exposes original HVAR/MVAR/layout deltas and cached gvar
simple, component and phantom operations. Glyph parse failures retain the original
once-only error followed by cached empty state; the first query freezes item count.
Simple IUP uses coordinates changed by earlier tuples. Source scalar arithmetic,
partial writes and complete-font reads remain unchanged. The source comparison
covers **8,772 cases / 8,192,752 exact bytes** and **96 captured browser fixtures**.
`ManagedPositioningPlan::from_variation_instance` now accepts Rust-selected state;
all **32,008 original positioning cases** independently verify its computed
coordinates against actual C# selection before comparing complete outputs.

This ports the process-neutral variation component. Full `TtfFont` root/FIFO cache,
instance metadata, hinted/varied outline construction and
metric preparation remain separate required work. Its default instance retains a
complete zero-coordinate inventory; the full font wrapper still owns default-root
selection and skipped variation application. The 48-workload admission/instance
baseline retains allocation and original compiler/JIT behavior. Query throughput,
full shaping/frame performance and previously recorded native FP gaps remain open.


`SfntFace::managed_names` and `managed_name` preserve original `SfntFontFace`
name selection: declared-table bounds, whole-record inventory admission, individual
invalid-record skipping, UTF-16BE/Latin-1/UTF-8 replacement decoding, NUL removal,
whitespace trimming, stable source ranking and ordinal deduplication. Platform 1,
encoding 0 retains the original Latin-1 route. Ranking examines UTF-16 code units;
supplementary letters do not lose the original ASCII/Latin score bonus. Character
facts use the existing frozen Unicode 16 inventory, independently checked against
all 65,536 original managed Char inputs; this does not infer a future runtime policy.
Names own their bytes after the font is dropped.

`ManagedVariationData::from_named_face` resolves axis names from that same selected
face, retaining original decoded name selection and missing-name tag fallback. The
explicit callback constructor remains independent. Actual C# `TtfFont.VariationAxes`
controls cover the integration. **236,128 source cases / 12,151,088 exact bytes**
include exhaustive two-byte UTF-8/Latin-1 input, odd/truncated UTF-16, malformed
strings, duplicate inventories, header/record bounds and ranking. The **93-case /
61,979-byte** browser fixture is captured from original C# and revalidated in CI.
`eng/progpu-benchmark-rust-managed-names.py` measures complete `GetNames`/`TryGetName`
queries against the source at 1/8/64/512 records. It excludes SFNT construction and
serialization, retaining production allocations and original runtime behavior;
this initial baseline does not qualify full font/shaping/frame performance.

`font::ManagedFont` now owns original uncompressed SFNT/TTC constructor metadata,
selected variation instances and the original strong 32-entry FIFO cache. Separate
roots have separate identities; clones retain the same instance. Normalized-key
equality survives eviction, while `same_instance` distinguishes a recreated entry.
Cache values hold immutable snapshots without root back-references, so retained
instances outlive their caller's root without an Arc cycle. Failed metadata
selection publishes no cache entry. Every TTC face directory validates before
the selected constructor runs, including malformed unselected faces.

The wrapper applies original wght/ital/slnt and MVAR metadata, preserves owned
palette cloning and no-op identity, and exposes horizontal/vertical advances,
layout and simple/component variation queries. `ManagedPositioningPlan::from_font`
uses this exact source/coordinate generation; all 32,008 original positioning
controls now exercise it. Full varied outline construction, vertical origins,
hinting, color-layer rendering, compressed/mapped/resident factories, discovery
and complete shaping/layout/GPU text remain separate required ports.

The constructor/cache oracle compares **10,369 actual C# cases / 81,025,616 bytes**,
including complete metadata, reference/key identity matrices, palette overrides,
FIFO eviction, malformed metadata and TTCs, source errors and partial mutation.
Its **104-case / 1,805,194-byte** browser capture is checked against actual source
in CI and replayed at both DPRs. Run `cargo test --locked --no-default-features
--test managed_font --test managed_positioning -- --include-ignored --nocapture`
with .NET 10; `PROGPU_RUST_DOTNET` selects its executable. The initial diagnostic
benchmark is `python3 eng/progpu-benchmark-rust-managed-font.py`; it compares
construction, cache hits, FIFO rotation and owned palette clones with original C#.

`font::ManagedCharacterMap` retains original managed nominal mapping separately
from the native `SfntFace` policy. It preserves format 12/13/4 precedence, original
checked counts and wrapping arithmetic, legacy version-zero symbol pages, and
ordered whole-cmap variation-selector reads. `Some(0)` from `variation_glyph`
means an explicit nondefault glyph-zero mapping; `None` means unsupported. Default
sequences require nonzero nominal coverage. The source's first matching selector
and malformed-read behavior remain intact, including duplicate/unsorted records.

`ManagedFont::glyph_index`, `has_glyph` and `variation_glyph` use that same immutable
source. ASCII lookup lazily allocates the original per-instance 128-slot cache;
one atomic word per slot holds both initialized state and glyph, including zero.
Clones retain the instance cache; new variation instances have independent caches.
Failed queries never publish a value. Non-ASCII mapping stays on its original
direct path. These APIs perform nominal mapping, not shaping or fallback discovery.

The source oracle compares **8,436 cases / 5,143,568 queries / 61,756,560 exact
bytes**, including complete BMP inventories, both legacy pages, every PUA coverage
hole, malformed maps and selector ordering. **88 source-captured browser cases**
run at both DPRs. Run `cargo test --locked --no-default-features --test
managed_mapping -- --include-ignored --nocapture` with .NET 10. The legacy mapping
data are regenerated from actual API observations by
`eng/progpu-generate-rust-symbol-maps.py`; CI verifies them with `--check`.
`eng/progpu-benchmark-rust-managed-mapping.py` records matched nominal, cached-font
and selector batches, including first-call cache costs. Complete script
preparation, varied outlines, hinting, fallback/layout and GPU text remain pending.

`unicode::grapheme::ManagedGraphemeProfile::Unicode16_0` selects the observed
original managed grapheme repertoire independently of normalization and category
profiles. `managed_next_grapheme_length(&[u16], profile)` returns the first text
element's original UTF-16 length, treating each unpaired surrogate as a one-unit
replacement scalar. Empty input returns zero. It visits only that cluster and its
following scalar, uses O(1) scratch, and allocates nothing.

For strict complete capture, construct `DecodedText` and use
`GraphemePolicy::ManagedRuntime(profile)` with `GraphemeText` or `segment_into`.
Malformed text rejects before publication. This source policy omits GB9c and is
not full Unicode 16 conformance. Existing Unicode 17 and native
`ManagedCompatible` behavior remain separate. Select profiles from actual source
observations; unsupported numeric profile values reject.

Run `cargo test --locked --release --no-default-features --test managed_grapheme
-- --include-ignored --nocapture` with .NET 10 (`PROGPU_RUST_DOTNET` selects it).
The original source comparison covers 22,256,392 cases and 899,691,464 exact bytes.
The browser replays 49 frozen source captures plus unsupported-profile rejection
at both DPRs, alongside existing rendering gates. Regenerate/check pinned public
facts with `python3 eng/progpu-generate-rust-grapheme-profile.py --check`.
`python3 eng/progpu-benchmark-rust-managed-grapheme.py` compares complete boundary
walks with C#; `PROGPU_RUST_GRAPHEME_BEFORE` adds a retained before binary and three
rotating-order process trials. Source parity here does not establish complete
shaping, layout, fallback or GPU text parity.

`font::layout::ManagedGlyphBuffer::from_text` performs the complete original
managed text preparation before script-specific shaping stages. Pass an owned
`ManagedFont`, UTF-16 source, original script and
`text::preparation::ManagedPreparationOptions`. The options explicitly select
normalization, mark and grapheme profiles, cluster level, flags and retained
pre/post UTF-16 context. `buffer.preparation()` exposes the immutable original
source generation; `glyphs()` / `glyphs_mut()` use the same complete records as
the existing GSUB executor. Already-mapped buffer constructors remain separate.

Preparation includes conditional NFC, malformed-unit replacement, source cluster
rules, UVS including explicit glyph zero, Indic/Khmer splits, missing-glyph NFD,
space/hyphen fallback and original unsafe-interior flags. Input and output retain
the original 1,048,576-record bound. Owned prepared buffers recycle up to four
4,096-capacity record arrays per thread; font/text/context owners retire normally.
`into_glyphs` transfers records without pooling them. Full script stages, shaping
sequencing, positioning preparation and layout remain separate unfinished work.

Run `cargo test --locked --release --no-default-features --test managed_preparation
-- --include-ignored --nocapture` with .NET 10 (`PROGPU_RUST_DOTNET` selects it).
The complete source comparison covers 312,627 cases / 873,278,732 bytes; browser
controls replay 110 source captures under all three normalization profiles.
`python3 eng/progpu-capture-rust-preparation.py --check` rechecks every original
Khmer predicate observation. `python3 eng/progpu-benchmark-rust-preparation.py`
compares creation/retirement and complete output with C#; optional
`PROGPU_RUST_PREPARATION_BEFORE` includes a retained unpooled binary. The driver
selects a normalization profile from actual source behavior, never an OS name.


`ManagedGlyphBuffer::apply_script_action` applies one explicit
`text::script_actions::ManagedScriptAction` to an owned prepared buffer. The
operations retain the actual font/text/context generation, all unrelated glyph
fields, lookup state and original writes made before an error. An already-mapped
buffer without preparation metadata rejects before mutation. Plan-dependent
choices such as vertical-substitution presence, Hebrew mark-feature presence and
USE selection are explicit inputs; they must come from the eventual full planner.
Fractions select independently witnessed `ManagedDigitProfile::Unicode16_0`.

These actions cover dotted-circle insertion, direction fallback, Hangul
composition/decomposition and Jamo masks, modified combining-class ordering,
Hebrew presentation composition, USE diacritic decomposition, Thai/Lao expansion,
fraction masks, substitution/syllable clearing, Arabic stretch recording and
whole-record reversal. They do not implement a complete shaping pipeline.
Remaining Indic/USE syllables and reordering,
ordered stage orchestration, positioning initialization, paragraphs and GPU text
remain required work.

Run `cargo test --locked --release --no-default-features --test
managed_script_actions -- --include-ignored --nocapture` with .NET 10. The oracle
compares 658,040 complete action cases / 704,161,720 bytes on the observed Unicode
17 normalization runtime and 1,114,113 modified-class/digit queries. The browser
replays 267 frozen original cases under each explicit normalization profile, plus
invalid-profile, truncated-input and overflow-length controls. `python3
eng/progpu-benchmark-rust-script-actions.py` records three rotating process trials
of 40 matching C#/Rust action workloads, including complete outputs. Actual
preparation and raw-record restoration are outside action timers. The original
stable insertion sort and vector shifts retain worst-case quadratic work; no
end-to-end performance qualification follows from this baseline.


`ManagedScriptAction::ArabicJoining` adds original contextual joining for Arabic,
Syriac and the other admitted cursive scripts. It preserves Syriac Alaph and
Dalath/Rish forms, transparent glyphs, exact five-scalar pre/post context bounds,
malformed-unit replacement and source cluster safety/tatweel flags. Joining uses
frozen Unicode 17 facts independently of normalization and runtime Rune profiles.
`managed_joining_type` exposes the original raw classification; invalid raw values
retain NonJoining. Regenerate and compare all 1,114,114 original queries with
`python3 eng/progpu-generate-rust-joining.py --check` (.NET 10 required).

The expanded `managed_script_actions` source test retains all earlier controls and
now compares 736,501 cases / 896,557,796 exact bytes on the witnessed Unicode 17
normalization runtime. The browser fixture contains 368 complete original cases.
Set `PROGPU_RUST_ACTIONS_JOINING_ONLY=1` when running the action benchmark for the
16 joining-only C#/Rust workloads. This action assigns contextual feature forms;
complete shaping orchestration and rendered text remain separate work.


`ManagedScriptAction::ArabicFallback { enabled, features }` retains the original
presentation fallback action on an actual prepared generation. Capture its five
whole-buffer feature switches with `ManagedArabicFallbackFeatures::from_features`;
the last exact UTF-16 setting wins, any nonzero signed value enables it, and missing
settings remain disabled. The caller supplies the original plan's fallback decision.
Feature ranges and baseline defaults do not participate in this source operation.

The port retains all 76 presentation-form bases and all three ordered ligature
passes: three triples, 58 ordinary pairs and five mark pairs, including the original
private-use targets. Existing GSUB traversal preserves GDEF, ignored marks,
joiners, syllable bounds, component metadata and full cluster mutation. Invalid
Unicode keeps the original action error and any preceding mutations.
`python3 eng/progpu-capture-rust-arabic-fallback.py --check` verifies hash-pinned
Unicode facts and independently observes the original API; no foreign generated
source array is transcribed. This check needs .NET 10.

The complete source corpus now matches 914,017 cases / 1,018,793,660 bytes on the
observed Unicode 17 normalization runtime, retaining every prior case. Its 546
frozen browser cases run under all three explicit normalization profiles.
Set `PROGPU_RUST_ACTIONS_FALLBACK_ONLY=1` in the action benchmark for 24 matched
fallback workloads, including joined text, private-use targets and skipped marks.
These are action baselines; full source stage sequencing, layout and GPU text
remain unfinished.


`ManagedScriptAction::VowelConstraints` ports the original prohibited-sequence
insertion before complex-script preparation. It preserves exact script spelling,
source scan order and two-code-point precedence over a longer rule with the same
prefix. The inserted dotted circle uses the final source cluster and original
constructor defaults; it is retained even when its font glyph is zero. Buffer
flags, existing grapheme metadata and unrelated records are unchanged.

The 103 constraints across 16 scripts come from pinned public Microsoft USE
specification data and Unicode 17 script facts. Their complete inventory and
4,863,144 matcher queries are independently verified with the original APIs by
`python3 eng/progpu-generate-rust-vowels.py --check` (.NET 10 required).
The public data's MIT notice is retained in `MICROSOFT-FONT-TOOLS-LICENSE.txt`;
Unicode facts retain `UNICODE-LICENSE.txt`. No generated foreign engine table or
implementation is copied.

The complete source corpus now compares 1,064,105 cases / 1,150,599,296 bytes on
the witnessed Unicode 17 runtime. All 914,017 previous cases remain; 771 frozen
browser cases also run in ordinary native CI under three explicit profiles.
`PROGPU_RUST_ACTIONS_VOWEL_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 20 matched workloads. This is a new-action baseline, not complete shaping
or rendered-text qualification. Complex syllable analysis/reordering, full source
stage orchestration and positioning initialization remain required.

`unicode::ManagedIndicProperties` retains exact source category/position bits.
`managed_indic_properties` preserves the public Unicode query's zero result for
invalid scalars; `ManagedIndicProperties::for_raw_code_point` preserves the raw
script-stage default word `0x0e00`. `managed_use_category` preserves the original
numeric USE classification, including zero for invalid/unclassified values.
These are immutable point queries, independent of runtime normalization profiles.

The generator observes actual original public/raw APIs without reading their
foreign generated arrays. Its independent packed ranges occupy 11,176 bytes.
`python3 eng/progpu-capture-rust-script-properties.py --check --rust` compares all
13,500,440 original output bytes, including every Unicode-domain raw value, every
high-bit alias and all 16-bit extraction inputs. Ordinary native tests replay
5,672 captured boundary/interior records. Browser tests compare the complete
source-output SHA-256 at both DPRs without bundling the 13.5 MB response.

`python3 eng/progpu-benchmark-rust-script-properties.py` runs 36 matched C#/Rust
lookup workloads in three rotating trials. This capture has 33 lower Rust medians
and three higher ones; it is a new-feature baseline, not complete shaping or
allocation-event qualification. Remaining Indic stages and full plan sequencing are pending.

`ManagedScriptAction::Khmer` performs the original `PrepareKhmerShaping` operation
on the actual prepared `khmr` generation. Exact other script spellings are no-ops.
It classifies original code points, assigns syllable bytes and unsafe-break flags,
inserts admitted broken-cluster dotted circles, then reorders complete records
and assigns the original feature masks. Source clusters, unrelated fields,
font/text/context owners and original mutation-before-error behavior survive.

The independent parser uses public grammar and directly observed original API
results. No foreign generated transition tables or lexer structure are copied.
`python3 eng/progpu-test-rust-khmer-syllables.py` compares 1,398,562 sequences /
9,586,614 exact syllable bytes, including arbitrary categories, long joiner runs,
subscript chains and serial rollover. The complete source action corpus now
matches 1,163,438 cases / 1,280,069,720 bytes on the witnessed Unicode 17 runtime;
all 1,064,105 prior cases remain. The 1,041 frozen cases replay under three
explicit profiles on native and browser builds.

`PROGPU_RUST_ACTIONS_KHMER_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 28 matched workloads across full/missing glyph coverage and short/long
runs. All complete outputs match in three rotating process trials. The 24 active
workloads have lower Rust medians; four disabled-script cases are clock-limited.
This is a new-stage baseline, not an allocation-event or full shaping/frame
qualification. Remaining Indic stages and full source orchestration remain
unfinished.


`ManagedScriptAction::MyanmarPreparation { enabled }` classifies the original
code points, assigns syllable bytes and marks unsafe breaks. It preserves the
original position bytes. `MyanmarReordering` is a separate later operation over
the current records: it inserts admitted broken-cluster circles, assigns kinzi,
base, medial and vowel positions, merges clusters, stably sorts complete records
and restores original pre-base vowel/selector grouping. Neither operation infers
planner admission from script spelling. The planner must supply the original
stage order around substitutions.

`python3 eng/progpu-test-rust-myanmar-syllables.py` compares 12,981,319 sequences /
75,858,807 exact bytes with the actual original API. The independent parser uses
public grammar and observed source behavior, without foreign transition tables.
The full action corpus retains every earlier case and now matches 1,324,919 cases /
1,481,007,028 bytes on the witnessed Unicode 17 runtime. Its 1,398 frozen source
cases run under all three profiles, giving 4,197 checks per browser DPR including
invalid requests. Reordering controls include all raw category pairs, ligated
records, arbitrary syllable/position bytes, neighboring cluster extension, flags,
missing dotted glyphs, repeated stages and long selector-bearing vowel sequences.

`PROGPU_RUST_ACTIONS_MYANMAR_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 32 matched workloads in three rotating trials. All 28 active workloads
have lower Rust median times; four disabled-preparation no-ops remain clock-limited.
This new-stage baseline does not qualify allocator events or complete shaping.
The stable counting sort uses owned O(N) scratch and retains at most one empty
allocation of capacity 4,096 per thread, independently of prepared-buffer storage.
Nested leases, thread teardown and oversized storage retire without source owners.
Original repeated cluster merges and dotted insertions retain their worst-case
quadratic cost. Remaining script families, full orchestration, paragraph/layout,
GPU text and original provider/package/source/pixel gates remain unfinished.


The explicit USE actions preserve the original separation between
`UsePreparation { enabled }`, `UseRecordRepha`, `UseRecordPrebase` and
`UseReordering { enabled }`. Preparation classifies raw code points, filters CGJ
and conditional ZWNJ only from recognition, preserves original record positions,
assigns syllables/unsafe flags/repha eligibility and selects source topographical
forms for non-Arabic-joining scripts. Leading or wholly filtered records retain
their original syllable bytes. Failed mark queries leave earlier classification
writes intact. Recording actions consume actual substituted/eligible metadata;
reordering consumes the current records after basic substitutions.

`python3 eng/progpu-test-rust-use-syllables.py` compares 5,870,757 independent
parser cases / 35,631,822 exact bytes with the original API. The full source action
corpus now contains 1,532,882 cases / 1,715,629,716 bytes on the witnessed Unicode
17 runtime, retaining all 1,324,919 earlier cases. The 1,910 frozen source captures
are shared by native and browser replay under all three normalization profiles.
These are explicit stage operations; the full planner must still select the
original lookup groups and interleave substitutions in their original order.


`PROGPU_RUST_ACTIONS_USE_ONLY=1 python3 eng/progpu-benchmark-rust-script-actions.py`
selects 40 workloads across full/missing coverage, short/long runs, filtered
records, hieroglyphs, recording/reordering and disabled stages. A six-second
Instruments CPU profile identified unnecessary alternative-family scans for
unambiguous hieroglyph prefixes. The direct prefix route preserves all original
results. Three rotating uninstrumented trials compare the original Rust binary,
current Rust and actual C#: all 36 active workloads have lower current Rust
medians than C# in this capture; the four hieroglyph after/before ratios are
0.523–0.571. Four disabled no-ops remain clock-limited. Complete exported CPU
tables, before source, input and binary hashes are retained in compact form;
allocation, process-memory, GPU and full shaping/frame qualification remain open.

The hosted Myanmar checkpoint's browser failure remains recorded in
`perf/browser-41b320-hosted.json`: all action/offscreen checks passed before DPR 2
device loss during final presentation; the diagnostic rerun also lost its device.
The bounded process inventory found a packed GPU marker but no eligible separate
GPU argument, so there is no backtrace or established crash attribution. A later
local/hosted pass does not by itself resolve this reliability gate.

`Scene::draw_axis_clip(bounds, source)` records a balanced immutable AA subtree.
Bounds retain original logical bits; preparation projects against the actual target.
Clear initializes only consecutive AA ancestors with live clipped Clear, stopping
at an ordinary `LayerDraw` owner. Binary clip scopes inherit through AA boundaries.
Direct masks around this typed AA group reject; an ordinary layer may independently
mask/composite the result. The full native open-scope builder and semantic archive
are separate unfinished ports.

Use `--retained-aa` (optionally `--text FONT_PATH`) or browser
`?scene=retained-aa`; selecting a local font file adds deferred child and root text.
The twelve-panel shared gallery includes fractional/nested AA, transparent and
partial Clear, disjoint closed children, inherited binary clips, offset ordinary
captures, opacity/masks and advanced blends. It replays the retained tree each
frame. `--axis-clips` remains the separate low-level execution gallery.

`PreparedScene::retained_aa_metrics()` reports live deferred AA resources and child
uploads/advanced work. `layer_metrics().source_aa` reports AA work inside eager
ordinary captures; child upload and advanced totals are also included in the
existing capture counters. `replay_metrics()` reports actual Clear uploads and
background copy bytes on every replay. These counters are implementation diagnostics,
not pixel, performance or package qualification. All original deferred gates remain.

## Native signed and staged path programs

`Path::from_program` retains `PathInstruction` leaves, empty values, the five
original Boolean operations and signed Add/Negate. Signed roots and raw winding
leaves require NonZero; predicate leaves retain their independent fill rule.
The original native limit remains 63 instructions and 16 stack entries. The
retained `Path` contract requires at least one nonempty leaf. Source bounds are
caller-owned; no contours, segment coordinates or signs are reconstructed.

`PathRasterizer::prepare_programs` prepares the families required by an actual
batch. `SignedWindingPolicy::Inline` is the original default; `Staged` selects the
original explicit signed flag. Renderer and MaskComposer expose the same policy
for future preparation. Prepared scenes retain their existing atlas generations.
The original pure left-fold XOR and overlapping translated-equivalent classifiers
independently select staged ordinary Boolean coverage. Existing managed Boolean
lowering keeps its prior independent route.

The staged route retains 64 signed words per leaf pixel (or two Boolean mask
words), original 40-byte combine records and 48-byte leaf uniforms. Each original
leaf phase keeps its own queue submission boundary; signed row evaluation, Boolean
packing, signed packing and final texture copies preserve their original order.
There is no CPU raster, readback, perturbed geometry or compiler-flag change.

Winit `--path-programs` and browser `?scene=path-programs` select 24 authored
panels: eight input programs, each painted directly and through inline/staged clip
factories. This sample has been authored, **not compiled or executed**. Validation
is deferred until the complete port implementation, as explicitly requested.
