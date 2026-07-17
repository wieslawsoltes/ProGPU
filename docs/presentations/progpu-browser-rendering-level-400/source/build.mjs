import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { FileBlob, PresentationFile } from '@oai/artifact-tool';

const sourceDir = path.dirname(fileURLToPath(import.meta.url));
const deckDir = path.dirname(sourceDir);
const tmp = path.resolve(
  process.env.PROGPU_PRESENTATION_WORKSPACE
    ?? path.join(os.tmpdir(), 'progpu-browser-rendering-level-400'),
);
const assets = path.join(sourceDir, 'assets');
const starterPptx = path.join(sourceDir, 'template-starter.pptx');
const finalPptx = path.resolve(
  process.argv[2]
    ?? path.join(deckDir, 'ProGPU-Browser-Rendering-Level-400.pptx'),
);

async function writeBlob(path, blob) {
  await fs.writeFile(path, new Uint8Array(await blob.arrayBuffer()));
}

const presentation = await PresentationFile.importPptx(
  await FileBlob.load(starterPptx),
);

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

async function replaceDiagram(slide, path, alt) {
  const row = rows.find((item) => item.slide === slide && item.kind === 'image');
  if (!row) throw new Error(`Missing inherited image on slide ${slide}`);
  const image = presentation.resolve(row.id);
  const frame = image.frame;
  const geometry = image.geometry;
  const borderRadius = image.borderRadius;
  const bytes = await fs.readFile(path);
  const slideObject = presentation.slides.getItem(slide - 1);
  slideObject.images.deleteById(image.id);
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

// 1 — opening
setText(1, 'cover-eyebrow', 'PROGPU / BROWSER WEBGPU · LEVEL 400');
setText(1, 'cover-title', 'ProGPU browser rendering\nfrom C# to navigator.gpu');
setText(1, 'cover-subtitle', 'Architecture, binary transport, retained GPU state, worker ordering, async readback, Hot Reload, and Release AOT.');
setNotes(1, 'Level 400 premise: the browser is not a second renderer. It is an execution lane beneath the same retained compositor. Source: README.md Browser implementation architecture.');

// 2 — architecture
setText(2, 'title-2', 'One renderer; only the host and transport are browser-specific');
setText(2, 'live-copy', 'The end-to-end boundary\nShared ProGPU.Samples and ProGPU.Scene execute inside .NET WebAssembly. BrowserWebGpuApi serializes the same typed WebGPU calls used by SilkWebGpuApi.\nprogpu-browser.js decodes them into navigator.gpu; embedded WGSL crosses unchanged.');
setText(2, 'live-caption', 'C# RETAINED SCENE  →  BINARY PACKET  →  NAVIGATOR.GPU');
setPage(2);
await replaceDiagram(2, `${assets}/architecture.png`, 'End-to-end ProGPU browser rendering architecture');
setNotes(2, 'Walk left-to-right. Application, controls, scene compilation, text, vector, and compute are shared. IWebGpuApi is the typed seam. Desktop selects SilkWebGpuApi; browser selects BrowserWebGpuApi. Source: README.md; IWebGpuApi.cs; BrowserWebGpuApi.cs; progpu-browser.js.');

// 3 — ownership
setGrid(3, 'The solution split keeps browser code at the platform edge', [
  '01  ProGPU.Samples\nShared application, windows, pages, retained visuals, controls, and every shader-using sample.',
  '02  ProGPU.Backend / Scene\nIWebGpuApi, WgpuContext, compositor, atlases, resources, render and compute pipelines.',
  '03  ProGPU.Browser\nCapability runtime, binary protocol, browser WebGPU API, host, input, storage, and fallback font.',
  '04  ProGPU.Samples.Browser\nThin browser-wasm entry point plus canvas, JavaScript module, CSS, diagnostics, and deployment assets.',
]);
setNotes(3, 'The project boundary is deliberately asymmetric: browser-specific code is narrow, while renderer code remains shared. Source: README.md project table and Browser WebGPU sample; ProGPU.Samples.Browser.csproj.');

// 4 — bootstrap
setJourney(4, 'Startup establishes one external WgpuContext', [
  '1  Bootstrap .NET\nprogpu-browser.js creates the .NET runtime, registers JS module imports, then runs Program.Main in browser-wasm.',
  '2  Negotiate capabilities\nSelect canvas; install input; choose transport; request adapter/device; select profile and canvas format.',
  '3  Install the shared app\nInitializeExternal installs opaque device/queue/surface tokens. BrowserWindowHost runs the shared app through AppBuilder.',
], ['DOTNET.JS', 'CAPABILITIES', 'APPBUILDER']);
setNotes(4, 'BrowserGpuRuntime.InitializeAsync is the one JSON startup seam because initialization is infrequent and capability-shaped. Frame rendering does not use JSON. Source: Program.cs; BrowserGpuRuntime.cs; progpu-browser.js initialize/initializeGpu; WgpuContext.InitializeExternal.');

// 5 — frame loop
setJourney(5, 'Every animation frame reuses the desktop RenderFrameCore path', [
  '1. Browser host tick\nrequestAnimationFrame supplies time. Drain DOM input, read physical canvas pixels and devicePixelRatio.',
  '2. Ordinary WinUI frame\nRun UIThread work, callbacks, animations, measure/arrange, and framebuffer/DPI reconfiguration.',
  '3  Acquire and render\nGet current surface texture, create its view, call Compositor.RenderScene, submit, present, and release tokens.',
], ['HOST', 'WINDOW', 'GPU FRAME']);
setNotes(5, 'The critical point is Window.RenderExternalFrame delegates to the same RenderFrameCore used by desktop. Physical framebuffer dimensions remain separate from logical layout dimensions. Source: BrowserWindowHost.cs; Window.cs.');

// 6 — typed seam
setJourney(6, 'IWebGpuApi is the only renderer-facing platform seam', [
  '1  Typed call sites\nRenderer code passes Silk.NET WebGPU descriptors and opaque pointer-shaped handles through IWebGpuApi.',
  '2  Desktop implementation\nSilkWebGpuApi is a zero-policy forwarding layer into wgpu-native; no command serialization is needed.',
  '3  Browser implementation\nBrowserWebGpuApi reads the same descriptors and writes versioned resource, pass, upload, copy, and lifetime opcodes.',
], ['CALLER', 'SILK / NATIVE', 'BROWSER / WASM']);
setNotes(6, 'The interface covers creation, render and compute passes, copies, writes, submission, mapping, surface acquisition, and destruction. This keeps higher layers backend-neutral without weakening type information. Source: IWebGpuApi.cs.');

// 7 — protocol
setJourney(7, 'The packet is a compact, self-validating little-endian stream', [
  '1  Packet header — 16 B\nPGPU magic u32, protocol version u16, flags u16, byte length u32, command count u32.',
  '2  Command header — 8 B\nOpcode u16, flags u16, command length u32. Each record advances to the next 8-byte boundary.',
  '3  Typed payload\nFixed-width descriptor fields, handle values, variable arrays, UTF-8 WGSL, and upload bytes are inlined.',
], ['OFFSET 0', 'EACH OPCODE', 'PAYLOAD']);
setNotes(7, 'Magic is 0x55504750 and version is 1. Both C# and JavaScript validate length and command consumption. Alignment makes 64-bit fields safe and decoding predictable. Source: BrowserGpuProtocol.cs; progpu-browser.js dispatchPacket.');

// 8 — flush
setJourney(8, 'Flush points preserve ordering without per-draw JavaScript interop', [
  '1  Record in reusable memory\nBrowserGpuCommandEncoder owns a NativeMemory arena, starts at 256 KiB, grows, seals, dispatches, then resets.',
  '2  Cross coarse seams\nQueueSubmit flushes the packet. mapAsync also flushes first so all prior writes and copies are visible.',
  '3  Preserve frame order\nA worker transport copies packet bytes, microtask-coalesces them, and posts one ordered dispatch-batch task.',
], ['ARENA', 'FLUSH', 'WORKER ORDER']);
setNotes(8, 'Resource creation can cause additional coarse packets, but steady rendering converges on one handoff per managed frame. WorkerRequest flushes queued frame packets before asynchronous requests. Source: BrowserWebGpuApi.Flush/QueueSubmit/BufferMapAsync; progpu-browser.js dispatch/flushWorkerPackets.');

// 9 — handles
setGrid(9, 'Opaque handles preserve resource identity across the boundary', [
  '01  32-bit token\nBits 0–19 are the table index; bits 20–31 are a 12-bit generation. Zero remains null.',
  '02  Managed pool\nAllocation increments generation; release validates the current generation before returning an index to the free stack.',
  '03  Pointer-shaped ABI\nSilk pointer values carry tokens only. Browser code never dereferences them; C# converts token ↔ pointer.',
  '04  JavaScript table\nresources is keyed by the complete token. requireResource rejects stale or unknown handles before WebGPU execution.',
]);
setNotes(9, 'Generations prevent an old retained command from accidentally addressing a new GPUBuffer or GPUTexture that reused the same index. Source: BrowserGpuHandle and BrowserGpuHandlePool; BrowserWebGpuApi class comment; requireResource in progpu-browser.js.');

// 10 — worker topology
setText(10, 'title-2', 'Transport selection is feature-based, not browser-branded');
setText(10, 'live-copy', 'Three execution modes\nAuto prefers an isolated OffscreenCanvas worker when cross-origin isolation and SharedArrayBuffer exist, then an ordinary worker, then main-thread decode.\nAll modes consume the same PGPU packet. Worker messages are serialized so surface texture and submission order cannot interleave.');
setText(10, 'live-caption', 'ONE PACKET FORMAT  ·  THREE EXECUTION MODES');
setPage(10);
await replaceDiagram(10, `${assets}/workers.png`, 'Main thread, worker, and isolated worker transport selection');
setNotes(10, 'COOP same-origin and COEP require-corp enable crossOriginIsolated. An explicit IsolatedWorker request downgrades to Worker when headers are absent and records the reason. Source: docs/browser.md; chooseExecutionMode; handleDispatcherWorkerMessage.');

// 11 — decoder
setJourney(11, 'JavaScript decodes PGPU into deterministic WebGPU calls', [
  '1  Validate packet\nCheck WASM bounds, magic, version, declared length, command count, and final byte consumption.',
  '2  Decode records\nRead opcode and length through DataView, execute payload fields with explicit little-endian reads, then align by 8.',
  '3  Issue WebGPU calls\nResolve handles and call GPUDevice, command encoders, render/compute passes, GPUQueue, textures, and buffers.',
], ['VALIDATE', 'DECODE', 'EXECUTE']);
setNotes(11, 'The decoder avoids JSON, reflection, and a JS interop call per draw. Descriptor enums are converted through explicit lookup tables, and truncation fails before issuing a partial command. Source: progpu-browser.js dispatchPacket/execute.');

// 12 — surface lifetime
setJourney(12, 'A frame batch owns one current surface texture lifetime', [
  '1  Acquire token\nSurfaceGetCurrentTexture allocates a handle and records CreateSurfaceTexture; Window creates a texture view from it.',
  '2  Resolve current texture\nOpcode 26 calls context.getCurrentTexture(). Render-pass attachments reference the resulting texture view handle.',
  '3  Submit and retire\nQueueSubmit flushes. Browser presentation is implicit; deferred view/texture releases join the next ordered packet.',
], ['ACQUIRE', 'ENCODE', 'SUBMIT']);
setNotes(12, 'This is why worker packets created by one managed frame are coalesced into one task: a current canvas texture cannot safely span unrelated worker work. SurfacePresent is intentionally a no-op in the browser backend. Source: BrowserWebGpuApi.SurfaceGetCurrentTexture/SurfacePresent; opcode 26; docs/browser.md.');

// 13 — retained cache architecture
setText(13, 'title-2', 'Retained rendering determines whether the CPU recompiles a scene');
setText(13, 'live-copy', 'ChangeVersion is the contract\nPixel-affecting mutations invalidate the visual and propagate a monotonically changing version to the root. RenderSceneCore snapshots target and atlas state.\nA cache hit skips scene compilation and uploads but still executes the current render pass and present path.');
setText(13, 'live-caption', 'INVALIDATION  →  CACHE PREDICATE  →  REPLAY OR RECOMPILE');
setPage(13);
await replaceDiagram(13, `${assets}/retained.png`, 'Retained scene invalidation and compiled scene cache flow');
setNotes(13, 'Compiled reuse is not “skip rendering.” It is “reuse compiled CPU/GPU-facing batch data.” Dynamic diagnostics, mutable drawing visuals, masks, effects, and stale layers deliberately force misses. PathAtlas recoverable exhaustion resets once and retries the same frame. Source: Visual.cs; Compositor.cs.');

// 14 — cache predicate
setGrid(14, 'Compiled scene reuse is a strict pixel-validity predicate', [
  '01  Visual state\nSame root identity and root ChangeVersion; no dynamic diagnostics; retained content must have an immutable contract.',
  '02  Target state\nSame logical size, physical framebuffer, viewport, and DPI scale. Resize and display changes invalidate reuse.',
  '03  Atlas state\nGlyphAtlas.Generation and PathAtlas.Generation must match the versions captured during compilation.',
  '04  Layer state\nTooltip and external layer identity/version plus every CacheAsLayer texture, ownership, visibility, and dirty flag must remain valid.',
]);
setNotes(14, 'The predicate is intentionally conservative because performance and pixel correctness are one contract. Source: Compositor.CanReuseCompiledScene and CaptureCompiledScene.');

// 15 — WGSL
setGrid(15, 'WGSL is shared source—not translated browser output', [
  'A  Embedded modules\nProduction WGSL lives in each owning Shaders directory and is loaded once through ShaderResource.',
  'B  Typed descriptor\nThe compositor supplies ShaderModuleWGSLDescriptor through IWebGpuApi; the browser backend accepts WGSL only.',
  'C  Exact UTF-8 path\nBrowserWebGpuApi encodes the source in opcode 14; JavaScript decodes and calls GPUDevice.createShaderModule({ code }).',
  'D  Stable pipelines\nShader modules, layouts, pipelines, bind groups, samplers, buffers, and textures keep generation-bearing identities across packets.',
]);
setNotes(15, 'There is no GLSL/HLSL translation step and no browser fork of Text.wgsl, Vector.wgsl, GlyphRasterizer.wgsl, or PathRasterizer.wgsl. Optional feature gaps downgrade the requested GPU profile rather than rewriting shader source.');

// 16 — paths/glyphs
setGrid(16, 'Retained path and glyph geometry survives the browser boundary', [
  '01  Geometry identity\nPath segments and font glyph outlines are compiled once into retained records keyed by geometry, style, scale, and bounded phase.',
  '02  GPU coverage atlases\nPathAtlas and GlyphAtlas keep coverage textures and GPU buffers alive; generations advance only when UV content actually moves.',
  '03  Quality invariants\nPhysical framebuffer pixels, DPI-aware glyph raster size, quarter-physical-pixel text snapping, and precise half-open winding stay intact.',
  '04  Cheap camera changes\nZoom and transforms update matrices, uniforms, or instances. They do not reparse DXF text, clone glyph paths, or lower analytic coverage.',
]);
setNotes(16, 'This is the retained-GPU answer to high-quality DXF zoom and crisp text: reuse glyph/path geometry and atlas residency, while changing placement transforms. Browser transport preserves those resources; it does not force raster snapshots. Source: AGENTS.md rendering contract; PathAtlas.cs; GlyphRasterizer.wgsl; PathRasterizer.wgsl.');

// 17 — mapping
setJourney(17, 'Mapped buffers cross a separate asynchronous seam', [
  '1  Order prior GPU work\nBufferMapAsync flushes pending commands before invoking the JavaScript mapBuffer import.',
  '2  Await WebGPU mapping\nMain thread awaits GPUBuffer.mapAsync. Worker mode sends a request and transfers a copied ArrayBuffer response.',
  '3  Expose managed range\nC# pins a managed byte array; reads copy into it. Writes copy back before unmap; callback/task completion remains asynchronous.',
], ['FLUSH', 'MAPASYNC', 'COPY BACK']);
setNotes(17, 'This coarse seam is intentionally separate from the packet protocol because WebGPU mapping is asynchronous and returns CPU-visible storage. Source: BrowserWebGpuApi mapping methods; progpu-browser.js mapBuffer/copyMappedBuffer/writeMappedBuffer.');

// 18 — input
setJourney(18, 'DOM input is batched into the neutral WinUI input seam', [
  '1  Queue DOM records\nListeners append fixed 32-byte events. Pointer moves replace the previous move; adjacent wheel deltas accumulate; queue caps at 4096.',
  '2  Drain per frame\nBrowserInputDispatcher stackallocs 256 records and drains at most four batches before injecting InputSystem events.',
  '3  Install platform services\nBrowserWindowHost wires cursor, focus/text, clipboard, storage/file pickers, canvas metrics, and an embedded Roboto fallback.',
], ['DOM', 'FRAME BATCH', 'HOST SERVICES']);
setNotes(18, 'High-frequency DOM events never call managed code directly. Popups, flyouts, tooltips, and dialogs remain compositor layers in the one top-level canvas window. Source: BrowserInputDispatcher.cs; BrowserWindowHost.cs; progpu-browser.js input helpers.');

// 19 — device loss
setJourney(19, 'WebGPU device loss rebuilds device-owned state', [
  '1  Detect\nuncapturederror records the first validation failure. device.lost reports reason and message through diagnostics.',
  '2  Signal the owner\nA dispatcher worker posts device-lost to the main page; main-thread mode schedules the same reload path.',
  '3  Rebuild cleanly\nReload reinitializes adapter/device, handle tables, pipelines, buffers, textures, atlases, and the retained shared application.',
], ['WEBGPU', 'HOST', 'RECONSTRUCT']);
setNotes(19, 'The browser cannot repair device-owned objects in place after device loss. Reloading from retained application state gives every GPU resource a coherent new device identity. Source: initializeGpu device.lost handler; handleWorkerMessage.');

// 20 — Debug vs AOT
setComparison(20, 'Debug Hot Reload and Release AOT converge on the same renderer',
  'Debug / iterate\n• Managed WebAssembly interpreter\n• WasmEnableHotReload = true\n• dotnet watch delivers metadata / IL deltas\n• UIThread coalesces one Hot Reload generation\n• Active and cached UI reconcile selectively\n• Same packets, workers, resources, WGSL',
  'Release / ship\n• RunAOTCompilation = true\n• Trimming, SIMD, and native relinking\n• ProGPU assemblies compile to WebAssembly AOT\n• netDxf remains supported mixed interpretation\n• Metadata update handlers are inactive\n• Same packets, workers, resources, WGSL',
  'Build mode changes managed execution and deployment—not renderer semantics.');
setNotes(20, 'Hot Reload preserves retained work by invalidating only affected roots and reserving recursive theme re-evaluation for theme/resource edits. Release AOT is trim-safe because the reflection fallback is gated off. Source: README.md Hot Reload architecture; ProGPU.Samples.Browser.csproj.');

// 21 — AOT publish
setText(21, 'title-2', 'Release AOT changes code generation, not graphics architecture');
setText(21, 'live-copy', 'The publish pipeline\nRelease enables AOT, trimming, SIMD, and native relinking. ProGPU code becomes native WebAssembly; netDxf is explicitly kept interpreted due to a Mono AOT compiler assertion.\nDeploy only artifacts/browser-aot/wwwroot from HTTP(S). Relative assets support static hosts and GitHub Pages.');
setText(21, 'live-caption', 'PUBLISH-TIME MODE  ·  STATIC HOST  ·  SAME WEBGPU PATH');
setPage(21);
await replaceDiagram(21, `${assets}/aot.png`, 'Release WebAssembly AOT publish and static deployment architecture');
setNotes(21, 'AOT is a publish-time mode: dotnet publish, not dotnet run. Verify logs for AOTing and native linking. The browser still starts Program.Main, initializes BrowserGpuRuntime, and uses the same navigator.gpu decoder. Source: README.md AOT mode; docs/browser.md; project file.');

// 22 — performance contract
setComparison(22, '120+ FPS is a retained-work contract, not a quality preset',
  'What must remain retained\n• Compiled scene batches and draw ordering\n• GPU buffers, textures, layouts, and pipelines\n• Path and glyph outline identity\n• Path/Glyph atlas coverage and generations\n• Camera/zoom through transforms and uniforms\n• One coarse worker task per managed frame',
  'What legitimately forces work\n• Visual ChangeVersion from pixel mutations\n• Logical/physical target, viewport, or DPI change\n• Atlas reset/repack generation change\n• Mutable drawing, effects, masks, diagnostics\n• Invalid cached-layer texture identity\n• Resource destruction or WebGPU device loss',
  'Never hit the target by skipping invalidation, lowering coverage, or rasterizing crisp vector text every frame.');
setNotes(22, 'For DXF, retained entity and text geometry must survive zoom; only transform data should change. Measure steady-state compilation, upload, packet bytes, atlas churn, and frame time separately. Source: AGENTS.md Rendering Performance Regression Contract; Compositor cache predicate; PathAtlas tests.');

// 23 — close
setText(23, 'close-eyebrow', 'IMPLEMENTATION CODE MAP');
setText(23, 'close-title', 'Same retained renderer.\nA browser-native execution lane.');
setText(23, 'close-details', 'C#: IWebGpuApi → BrowserWebGpuApi → BrowserGpuProtocol\nJS: progpu-browser.js → navigator.gpu\nHost: BrowserWindowHost → Window.RenderExternalFrame');
setText(23, 'close-sources', 'Start: docs/browser.md · README Browser architecture · src/ProGPU.Browser · src/ProGPU.Samples.Browser');
setNotes(23, 'Final principle: keep application and renderer semantics shared; make platform differences explicit in startup, transport, and host services. The implementation is reviewable end to end from typed descriptor to JavaScript WebGPU call.');

// Export complete visual evidence and the editable PPTX.
await fs.mkdir(`${tmp}/final-render`, { recursive: true });
await fs.mkdir(`${tmp}/final-layout`, { recursive: true });
for (const [index, slide] of presentation.slides.items.entries()) {
  const stem = `slide-${String(index + 1).padStart(2, '0')}`;
  await writeBlob(`${tmp}/final-render/${stem}.png`, await presentation.export({ slide, format: 'png', scale: 1 }));
  const layout = await slide.export({ format: 'layout' });
  await fs.writeFile(`${tmp}/final-layout/${stem}.layout.json`, await layout.text());
}
await writeBlob(`${tmp}/final-montage.webp`, await presentation.export({ format: 'webp', montage: true, scale: 1 }));

const finalInspect = await presentation.inspect({
  kind: 'slide,textbox,shape,image,notes,layout',
  maxChars: 1000000,
});
await fs.writeFile(`${tmp}/final-inspect.ndjson`, finalInspect.ndjson);

const pptx = await PresentationFile.exportPptx(presentation);
await fs.mkdir(path.dirname(finalPptx), { recursive: true });
await pptx.save(finalPptx);
console.log(finalPptx);
