import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { FileBlob, PresentationFile } from '@oai/artifact-tool';

const sourceDir = path.dirname(fileURLToPath(import.meta.url));
const deckDir = path.dirname(sourceDir);
const tmp = path.resolve(
  process.env.PROGPU_PRESENTATION_WORKSPACE
    ?? path.join(os.tmpdir(), 'progpu-core-ui-frameworks-level-400'),
);
const assets = path.resolve(
  process.env.PROGPU_PRESENTATION_ASSETS
    ?? path.join(os.tmpdir(), 'progpu-core-ui-frameworks-level-400-assets'),
);
// Reuse the audited starter rather than duplicating the same binary template.
const starterPptx = path.resolve(
  sourceDir,
  '../../progpu-browser-rendering-level-400/source/template-starter.pptx',
);
const finalPptx = path.resolve(
  process.argv[2]
    ?? path.join(deckDir, 'ProGPU-Core-Rendering-and-UI-Frameworks-Level-400.pptx'),
);

async function writeBlob(filePath, blob) {
  await fs.writeFile(filePath, new Uint8Array(await blob.arrayBuffer()));
}

const presentation = await PresentationFile.importPptx(await FileBlob.load(starterPptx));
const inventory = await presentation.inspect({
  kind: 'slide,textbox,shape,image,notes',
  maxChars: 1000000,
});
const rows = inventory.ndjson.trim().split('\n').filter(Boolean).map(JSON.parse);

function rowFor(slide, name, kind = 'textbox') {
  const row = rows.find((item) => item.slide === slide && item.kind === kind && item.name === name);
  if (!row) throw new Error(`Missing ${kind} '${name}' on slide ${slide}`);
  return row;
}

function setText(slide, name, text) {
  presentation.resolve(rowFor(slide, name).id).text = text;
}

function setPage(slide) {
  const row = rows.find((item) => item.slide === slide && item.kind === 'textbox' && String(item.name || '').startsWith('page-'));
  if (!row) throw new Error(`Missing page number on slide ${slide}`);
  presentation.resolve(row.id).text = String(slide);
}

function setJourney(slide, title, cards, labels) {
  setText(slide, 'title-3', title);
  cards.forEach((text, index) => setText(slide, `journey-text-${index + 1}`, text));
  labels.forEach((text, index) => setText(slide, `journey-label-${index + 1}`, text));
  setPage(slide);
}

function setGrid(slide, title, blocks) {
  setText(slide, 'title-4', title);
  blocks.forEach((text, index) => setText(slide, `retained-${index + 1}`, text));
  setPage(slide);
}

function setComparison(slide, title, left, right, summary) {
  setText(slide, 'title-5', title);
  setText(slide, 'debug-mode', left);
  setText(slide, 'release-mode', right);
  setText(slide, 'mode-summary', summary);
  setPage(slide);
}

async function replaceDiagram(slide, filePath, alt) {
  const row = rows.find((item) => item.slide === slide && item.kind === 'image');
  if (!row) throw new Error(`Missing inherited image on slide ${slide}`);
  const inherited = presentation.resolve(row.id);
  const frame = inherited.frame;
  const geometry = inherited.geometry;
  const borderRadius = inherited.borderRadius;
  const bytes = await fs.readFile(filePath);
  const slideObject = presentation.slides.getItem(slide - 1);
  slideObject.images.deleteById(inherited.id);
  slideObject.images.add({
    blob: bytes,
    contentType: 'image/png',
    alt,
    fit: 'contain',
    position: frame,
    crop: { left: 0, top: 0, right: 0, bottom: 0 },
    geometry,
    borderRadius,
  });
}

function setNotes(slide, text) {
  const row = rows.find((item) => item.slide === slide && item.kind === 'notes');
  if (!row) throw new Error(`Missing notes object on slide ${slide}`);
  presentation.resolve(row.id).setText(text);
}

// 1 — opening thesis
setText(1, 'cover-eyebrow', 'PROGPU CORE RENDERING · LEVEL 400');
setText(1, 'cover-title', 'One GPU substrate\nfor multiple UI frameworks');
setText(1, 'cover-subtitle', 'Retained scene, compositor, resources, text, vectors, hosts, and the adapter seams behind ProGPU.WinUI, LibreWPF, and LibreWinForms.');
setNotes(1, 'Premise: ProGPU is a rendering substrate with reusable framework seams—not three separate renderers. The talk follows one pixel-affecting mutation from framework semantics through retained scene compilation to WebGPU submission. Sources: README.md Architectural Hierarchy; src/ProGPU.Scene; src/ProGPU.Backend.');

// 2 — complete system architecture
setText(2, 'title-2', 'Framework semantics converge before GPU compilation');
setText(2, 'live-copy', 'One execution architecture\nProGPU.WinUI records directly into the retained scene. LibreWPF replays portable drawing/MIL data into retained scene objects. LibreWinForms preserves Control/Paint and System.Drawing semantics, then feeds the same scene command model.\nThe compositor, atlases, buffers, shaders, and WebGPU backend are shared.');
setText(2, 'live-caption', 'FRAMEWORK FRONTENDS  →  RETAINED SCENE  →  COMPOSITOR  →  WEBGPU');
setPage(2);
await replaceDiagram(2, path.join(assets, 'architecture.png'), 'End-to-end ProGPU multi-framework rendering architecture');
setNotes(2, 'Read left to right. The architectural fan-in occurs at ProGPU.Scene.Visual and DrawingContext/RenderCommand—not at final pixels. This allows all frontends to share scene caching, batching, vector/text coverage, effects, and presentation. Sources: README.md project architecture; ProGPU.Scene; ProGPU.Wpf composition bridge; LibreWinForms portable host.');

// 3 — ownership boundaries
setGrid(3, 'Ownership boundaries keep UI policy out of the renderer', [
  '01  Framework layer\nProperties, styling, templates, layout policy, focus, routed input, controls, and application lifetime.',
  '02  Adapter layer\nMaps framework drawing/state into stable Visuals, DrawingContext operations, portable DTOs, and resource identities.',
  '03  Scene/compositor\nRetained nodes, transforms, clips, effects, command IR, cache predicate, batching, uploads, and render passes.',
  '04  Backend/platform\nIWebGpuApi, device/queue/surface, buffers, textures, pipelines, shaders, native/browser host, DPI, and input source.',
]);
setNotes(3, 'The framework owns semantics; the scene owns retained render state; the compositor owns compilation and ordering; the backend owns WebGPU calls. Portable DTOs prevent WPF/WinForms types from leaking into shared hot paths. Sources: README.md Architectural Hierarchy; ProGPU.Wpf.Interop; AGENTS.md A0.');

// 4 — the adapter fan-in
setJourney(4, 'Three frontends, three recording seams, one retained core', [
  '1  ProGPU.WinUI\nFrameworkElement already derives through LayoutNode/Visual. OnRender records public DrawingContext commands directly.',
  '2  LibreWPF\nPortable visual state and render-data snapshots are decoded; WpfVisualTreeRenderer updates retained branches and command sinks.',
  '3  LibreWinForms\nControl.OnPaint uses ProGPU-backed Graphics. WindowsFormsHost connects lifecycle/input while drawing reaches shared commands.',
], ['DIRECT', 'PORTABLE REPLAY', 'PAINT ADAPTER']);
setNotes(4, 'The adapter cost is paid when semantic state changes, not by maintaining a private renderer per framework. Sources: ProGPU.WinUI/Core/FrameworkElement.cs; wpf/src/ProGPU.Wpf/Composition/Mil/WpfVisualTreeRenderer.cs; winforms LibreWinForms portable host and System.Drawing.Common.');

// 5 — host frame
setJourney(5, 'Host frame: the deterministic outer transaction', [
  '1  Service/UI phase\nDrain dispatcher and platform input; advance animations once; execute framework callbacks and invalidations.',
  '2  Layout phase\nMeasure and arrange only invalid nodes. Logical size remains separate from framebuffer pixels and DPI.',
  '3  Render/present phase\nAcquire target, call Compositor.RenderScene with CompositorHostFrame, submit passes, present, release transient handles.',
], ['DISPATCH', 'LAYOUT', 'GPU FRAME']);
setNotes(5, 'Window.RenderFrameCore owns the core ordering. Samples and framework adapters must not update animations twice or request frames from OnRender. Sources: ProGPU.WinUI/Core/Window.cs; ProGPU.Scene/CompositorHostFrame.cs; AGENTS.md Protect Hot Paths.');

// 6 — invalidation
setJourney(6, 'Pixel correctness begins with two independent validity systems', [
  '1  Layout validity\nMeasure/arrange flags propagate according to property semantics; equal cached inputs can skip layout work.',
  '2  Visual validity\nEvery pixel-affecting mutation calls Visual.Invalidate, increments ChangeVersion, marks dirty, and propagates to the root.',
  '3  Scene validity\nThe compositor compares retained versions plus target, DPI, atlas, overlay, effect, and layer state before reuse.',
], ['MEASURE / ARRANGE', 'CHANGEVERSION', 'CACHE PREDICATE']);
setNotes(6, 'Layout validity cannot substitute for render validity. A property may leave final bounds unchanged while changing clipping, brush state, content, or pixels. Sources: ProGPU.Layout/LayoutNode.cs; ProGPU.Scene/Visual.cs; Compositor.CanReuseCompiledScene.');

// 7 — visual tree
setJourney(7, 'Visual is the retained identity and transform boundary', [
  '1  Local state\nOffset, size, transform, opacity, clips, masks, effects, hit-test ID, visibility, and CacheAsLayer live on stable nodes.',
  '2  Tree state\nContainerVisual preserves child identity and ordering. Parent invalidation makes descendant mutations visible at the root.',
  '3  Composite scope\nCompilation derives global transforms, clip/opacity stacks, offscreen layer/effect scopes, then recurses in visual order.',
], ['NODE', 'TREE', 'COMPOSITE']);
setNotes(7, 'Stable tree identity is essential. Reparenting or rebuilding unchanged controls destroys layout, theme, and scene-cache locality. Sources: ProGPU.Scene/Visual.cs; ContainerVisual implementation; Compositor.CompileVisualTree; AGENTS.md navigation-tree identity.');

// 8 — command IR
setJourney(8, 'DrawingContext is a retained command recorder—not an immediate canvas', [
  '1  Framework records intent\nDrawRect, DrawPath, DrawGlyphRun, DrawTexture, state pushes/pops, pictures, static DXF, meshes, and extensions.',
  '2  Commands retain payloads\nRenderCommand carries geometry/resource identity, style, transforms, sampling, providers, and extension payloads.',
  '3  Compositor lowers later\nCompilation resolves atlases, text/vector vertices, textures, masks, blend state, and draw calls against the current target.',
], ['PUBLIC API', 'COMMAND IR', 'LOWERING']);
setNotes(8, 'OnRender records into a pooled DrawingContext. Common text remains one DrawGlyphRun command with shaped indices and positions retained; paths retain analytic geometry. Sources: ProGPU.Scene/RenderCommand.cs; DrawingContext files; Compositor.CompileVisualTree.');

// 9 — compilation stages
setGrid(9, 'Compositor compilation is an ordered lowering pipeline', [
  '01  Traverse + state\nCompose transforms; push/pop rectangular and geometry clips, opacity, blend modes, masks, effects, and layer scopes.',
  '02  Resolve primitives\nMap command kinds to vector/text/texture vertices, static buffers, retained pictures, or ICompositorExtension pipelines.',
  '03  Preserve Z order\nCommit pending vector/text work at material, texture, clip, effect, extension, and layer boundaries—never global-sort pixels.',
  '04  Emit GPU work\nEnsure buffer capacity, upload changed spans, bind cached layouts/pipelines/resources, encode passes, submit, and present.',
]);
setNotes(9, 'The compiler deliberately batches adjacent compatible work while retaining scene order. StaticCompilationContext and provider paths allow large retained datasets to bypass per-primitive regeneration. Sources: ProGPU.Scene/Compositor.cs; StaticCompilationContext.cs; ICompositorExtension.cs.');

// 10 — cache architecture
setText(10, 'title-2', 'A compiled-scene hit skips compilation—not rendering');
setText(10, 'live-copy', 'Pixel-valid reuse\nRenderSceneCore checks root identity/ChangeVersion, logical and physical targets, viewport/DPI, GlyphAtlas and PathAtlas generations, tooltips, external layers, cached-layer textures, diagnostics, masks, effects, and mutable drawing state.\nA hit reuses draw calls and buffers, then still encodes the current pass and presents.');
setText(10, 'live-caption', 'STRICT PREDICATE  →  REPLAY OR RECOMPILE  →  ALWAYS PRESENT');
setPage(10);
await replaceDiagram(10, path.join(assets, 'compositor.png'), 'Compiled-scene cache and compositor frame flow');
setNotes(10, 'Reuse is safe only when every input that can alter pixels remains valid. Recoverable PathAtlas exhaustion resets once and retries the same frame before moved UVs are submitted. Sources: Compositor.RenderSceneCore, CanReuseCompiledScene, CaptureCompiledScene; AGENTS.md PathAtlas recovery contract.');

// 11 — ordered batching
setJourney(11, 'Batching is dynamic, adjacent, and order-preserving', [
  '1  Accumulate compatible spans\nVector and text vertices append into reusable lists while material/clip/blend state remains compatible.',
  '2  Commit at semantic barriers\nTexture draws, state changes, effects, masks, extension passes, and visual layers flush pending spans in place.',
  '3  Replay retained draw calls\nThe draw-call list references stable buffers/resources; unchanged scenes reuse it without recompiling the visual tree.',
], ['APPEND', 'BARRIER', 'REPLAY']);
setNotes(11, 'CommitPendingDrawCalls preserves correct z-order while avoiding one draw per primitive. Buffers grow geometrically and are reused. Sources: Compositor.SwitchBatchType, CommitPendingDrawCalls, EnsureBufferSize.');

// 12 — resources and pipelines
setJourney(12, 'Resource identity is retained above every backend call', [
  '1  Create/cache\nGpuBuffer, GpuTexture, samplers, layouts, bind groups, shader modules, and RenderPipelineCache own device identities.',
  '2  Reuse/age\nStable scene commands reference resources; caches mark frame use and evict bounded stale entries instead of rebuilding per draw.',
  '3  Recover coherently\nResize, atlas generation changes, resource disposal, surface changes, or device loss invalidate the precise dependent state.',
], ['IDENTITY', 'LIFETIME', 'INVALIDATION']);
setNotes(12, 'Shaders are embedded source loaded through ShaderResource and cached. Fixed WGSL is never built from C# strings in a hot path. Sources: ProGPU.Backend/GpuBuffer.cs, GpuTexture.cs, RenderPipelineCache.cs, ShaderResource.cs; AGENTS.md shader contract.');

// 13 — paths, glyphs, images
setText(13, 'title-2', 'Geometry and coverage stay retained all the way to the GPU');
setText(13, 'live-copy', 'Stable identity before placement\nPaths keep analytic segments; glyph runs keep shaped glyph indices and positions; images keep texture identity and sampling. Cache keys include the quality-sensitive scale/phase data.\nPath/Glyph atlas generations move only when UV content moves; final placement remains transform-driven.');
setText(13, 'live-caption', 'RETAIN OUTLINE / TEXTURE  →  CACHE COVERAGE  →  TRANSFORMED REPLAY');
setPage(13);
await replaceDiagram(13, path.join(assets, 'geometry.png'), 'Retained path, glyph, image, atlas, and vertex pipeline');
setNotes(13, 'This is the basis for crisp fast zoom: camera transforms and uniforms change while geometry, glyph outlines, coverage, and textures remain reusable. Sources: RetainedGlyphGeometry.cs; ProGPU.Vector/PathAtlas.cs; ProGPU.Text/GlyphAtlas.cs; Compositor glyph/path/image compilation.');

// 14 — text
setGrid(14, 'Text quality is a retained-data and physical-pixel contract', [
  '01  Shape once\nText layout produces glyph indices and positions. RenderCommand.DrawGlyphRun preserves them—no repeated character-map lookup.',
  '02  Retain outlines\nCommon solid text is one glyph-run command; vector/CFF fallbacks reuse cached outlines with bounded local/device phase keys.',
  '03  Raster at device scale\nGlyph coverage uses physical size and current DPI; positions snap to quarter physical pixels then map back to logical space.',
  '04  Invalidate precisely\nText/font/content mutations rebuild layout; caret, selection, and hover invalidate paint only; atlas generation guards UV reuse.',
]);
setNotes(14, 'The separation prevents missing-glyph first frames, blurry DXF text, and per-frame glyph geometry churn. Sources: AGENTS.md text contracts; ProGPU.WinUI/Controls/TextVisual.cs; ProGPU.Text; Compositor CompileGlyphRunCommand.');

// 15 — layers/effects/extensions
setGrid(15, 'Offscreen composition and extensions remain first-class scene work', [
  '01  CacheAsLayer\nA stable subtree renders once into a GpuTexture; reuse requires the same owner, clean state, texture identity, size, and device.',
  '02  Effects + masks\nDynamic source/destination textures and intermediate passes preserve opacity, blend, clip, backdrop, and shader-effect semantics.',
  '03  Extension pipelines\nICompositorExtension lowers static DXF, charts, splines, 3D, hatches, images, and ShaderToy without forking the scene model.',
  '04  Retained providers\nGpuPicture and IRenderDataProvider replay large immutable command/data sets without reconstructing managed objects per frame.',
]);
setNotes(15, 'Effects and mutable DrawingVisual content deliberately restrict whole-scene reuse unless they expose an immutable version contract. Sources: Compositor effect/layer paths; Extensions directory; SharedCompositorCache.cs; README CacheAsLayer and extension architecture.');

// 16 — input/hit testing
setGrid(16, 'Rendering and interaction share transforms—but not one mandatory hit-test engine', [
  '01  CPU framework hit testing\nProGPU.WinUI InputSystem walks the visual tree with inverse transforms, clips, z-order, enablement, and control bounds.',
  '02  Optional GPU index\nThe compositor can build/query a GPU render-command hit-test index for geometry-heavy hosts and diagnostics.',
  '03  Independent switches\nEnableCompiledSceneCache and EnableGpuHitTesting are separate; WinUI avoids duplicate GPU compilation for its CPU tree path.',
  '04  Platform injection\nHosts normalize pointer, keyboard, text, focus, drag/drop, cursor, DPI, clipboard, and storage into framework-owned services.',
]);
setNotes(16, 'A new framework can use CPU visual-tree hit testing, compositor GPU hit testing, or a hybrid without changing render commands. Sources: ProGPU.WinUI/Input/InputSystem.cs; ProGPU.Scene/GpuRenderCommandHitTestCache.cs; CompositorOptions.cs.');

// 17 — host seam
setJourney(17, 'A platform host supplies services—not rendering policy', [
  '1  Native/browser window\nCreate surface and WgpuContext; publish logical size, physical framebuffer, viewport, display scale, and frame timing.',
  '2  Framework lifetime\nOwn dispatcher integration, activation, popups, input injection, storage/clipboard/launcher, and shutdown semantics.',
  '3  Scene transaction\nProvide root/external layers/tooltips and invoke the same Compositor.RenderScene contract for every frontend.',
], ['SURFACE', 'SERVICES', 'COMPOSITOR']);
setNotes(17, 'The host can be GLFW/Silk, browser canvas, WPF portable window host, or another shell. It must preserve logical/physical/DPI separation and one animation update per frame. Sources: ProGPU.Backend native platforms; ProGPU.Browser host; ProGpuWpfWindowHost.');

// 18 — ProGPU.WinUI mapping
setJourney(18, 'ProGPU.WinUI is the direct framework implementation', [
  '1  Property/layout\nDependencyObject stores values and metadata; FrameworkElement invalidates measure, arrange, or pixels according to semantics.',
  '2  Controls/templates\nControl trees, resources, themes, text, virtualization, and routed input keep stable visual identities across state changes.',
  '3  Rendering\nFrameworkElement/Visual OnRender records commands; Window owns frame order; InputSystem owns CPU tree hit testing.',
], ['DP + LAYOUT', 'UI SEMANTICS', 'DIRECT SCENE']);
setNotes(18, 'WinUI is “closest” to the core because its layout nodes are scene visuals. That is an implementation choice, not a requirement for other frameworks. Sources: ProGPU.WinUI/Core/DependencyObject.cs, FrameworkElement.cs, Window.cs; Controls; ProGPU.Layout.');

// 19 — LibreWPF mapping
setJourney(19, 'LibreWPF: portable semantics into a retained ProGPU scene', [
  '1  Portable state\nTyped visual, layout, window, drawing, and render-data DTOs cross assembly boundaries without reflection.',
  '2  Retained replay\nThe visual-tree renderer and MIL decoder update branch maps, resource registries, and only invalid content.',
  '3  Native sink\nThe composition sink maps brushes, paths, glyphs, images, clips, effects, caches, and 3D into ProGPU.Scene.',
], ['PORTABLE DTO', 'MIL / TREE REPLAY', 'RETAINED SINK']);
setNotes(19, 'No reflection-driven drawing hot path is required. Retained branch maps and invalidation tracking let unchanged WPF content remain unchanged ProGPU scene content. Sources: src/ProGPU.Wpf.Interop; wpf/src/ProGPU.Wpf/Composition/Mil; ProGpuRetainedCompositionCommandSink.cs.');

// 20 — LibreWinForms mapping
setComparison(20, 'LibreWinForms keeps WinForms behavior while replacing native GDI rendering',
  'WinForms semantic surface\n• Application and Control lifecycle\n• Handle-compatible portable hosting\n• Layout, focus, messages, invalidation\n• OnPaint / PaintEventArgs contract\n• System.Drawing.Graphics API\n• WindowsFormsIntegration host',
  'Shared ProGPU execution\n• ProGPU-backed brushes, pens, paths, text, images\n• Graphics records retained drawing intent\n• Host supplies WPF-compatible integration/input\n• Commands converge on ProGPU.Scene\n• Same compositor, atlases, effects, pipelines\n• Same desktop/browser-capable backend seam',
  'Compatibility stays at the API edge; retained GPU identity and batching stay in the shared core.');
setNotes(20, 'LibreWinForms does not need to emulate GDI pixels as the renderer. Its compatible System.Drawing layer maps drawing operations to retained ProGPU primitives, while the portable WindowsFormsHost connects application/control lifetime. Sources: ProGPU/System.Drawing.Common; winforms LibreWinForms.System.Windows.Forms and LibreWinForms.WindowsFormsIntegration.');

// 21 — compare the connected frameworks
setText(21, 'title-2', 'All frontends converge on the same performance substrate');
setText(21, 'live-copy', 'Shared after convergence\nEvery frontend ultimately supplies stable scene identity plus drawing/resource intent. From that point onward, the same ChangeVersion propagation, compiler, z-ordered batching, buffers, textures, path/glyph atlases, effects, extension pipelines, WGSL, WebGPU device, and presentation path apply.');
setText(21, 'live-caption', 'DIFFERENT SEMANTICS  ·  SHARED RETENTION  ·  SHARED GPU EXECUTION');
setPage(21);
await replaceDiagram(21, path.join(assets, 'frameworks.png'), 'Framework-specific adapters converging on shared ProGPU scene and compositor');
setNotes(21, 'This is the key architectural payoff: fixes and optimizations in the compositor, text/vector engines, atlas recovery, device lifecycle, or backend benefit all frameworks—provided adapters preserve retained identity and invalidation. Sources: architecture diagram paths cited throughout the talk.');

// 22 — contract
setComparison(22, 'Performance and correctness are one retained-rendering contract',
  'What framework adapters must preserve\n• Stable visual/resource identity\n• Precise pixel invalidation and ChangeVersion\n• Layout vs paint validity separation\n• Shaped glyph indices and retained outlines\n• Analytic path topology and image sampling\n• Ordered state scopes and dynamic content versions',
  'What the core guarantees\n• Strict compiled-scene pixel-validity checks\n• Adjacent z-ordered batching and reused buffers\n• Bounded atlas/cache generations and recovery\n• Physical-pixel/DPI-aware text and vector quality\n• Independent hit-testing policy\n• Shared shaders, effects, extensions, and hosts',
  'Never improve FPS by dropping invalidation, lowering coverage quality, rebuilding stable trees, or rasterizing retained vectors every frame.');
setNotes(22, 'Measure layout, compile, upload, render, draw count, atlas churn, and memory separately. Required regression coverage includes unchanged reuse, invalidation, target/DPI changes, mutable drawings, atlas reset/repack, and hit-test options. Sources: AGENTS.md Rendering Performance Regression Contract; LayerRenderTests; CompositorReviewRegressionTests.');

// 23 — close / extension playbook
setText(23, 'close-eyebrow', 'IMPLEMENTATION PLAYBOOK');
setText(23, 'close-title', 'Bring semantics and identity.\nReuse the renderer.');
setText(23, 'close-details', 'Frontend → stable Visual / portable adapter\nDrawing → DrawingContext / RenderCommand / provider\nFrame → CompositorHostFrame → Compositor.RenderScene');
setText(23, 'close-sources', 'Code map: ProGPU.Scene · ProGPU.Backend · ProGPU.Layout · ProGPU.WinUI · ProGPU.Wpf.Interop · LibreWPF ProGPU.Wpf · LibreWinForms');
setNotes(23, 'For a new framework: define property/layout/input semantics; preserve stable node and resource identity; map drawing to retained commands; provide a host; choose CPU/GPU hit testing; then adopt the compositor correctness tests. The renderer and shaders remain shared.');

// Export per-slide evidence, montage, inspect data, and editable PPTX.
await fs.mkdir(path.join(tmp, 'final-render'), { recursive: true });
await fs.mkdir(path.join(tmp, 'final-layout'), { recursive: true });
for (const [index, slide] of presentation.slides.items.entries()) {
  const stem = `slide-${String(index + 1).padStart(2, '0')}`;
  await writeBlob(path.join(tmp, 'final-render', `${stem}.png`), await presentation.export({ slide, format: 'png', scale: 1 }));
  const layout = await slide.export({ format: 'layout' });
  await fs.writeFile(path.join(tmp, 'final-layout', `${stem}.layout.json`), await layout.text());
}
await writeBlob(path.join(tmp, 'final-montage.webp'), await presentation.export({ format: 'webp', montage: true, scale: 1 }));

const finalInspect = await presentation.inspect({
  kind: 'slide,textbox,shape,image,notes,layout',
  maxChars: 1000000,
});
await fs.writeFile(path.join(tmp, 'final-inspect.ndjson'), finalInspect.ndjson);

const pptx = await PresentationFile.exportPptx(presentation);
await fs.mkdir(path.dirname(finalPptx), { recursive: true });
await pptx.save(finalPptx);
console.log(finalPptx);
