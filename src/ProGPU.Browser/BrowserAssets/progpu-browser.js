import { dotnet } from './_framework/dotnet.js';

const isDispatcherWorker = typeof WorkerGlobalScope !== 'undefined' && globalThis instanceof WorkerGlobalScope;
let runtime = null;

const state = {
  canvas: null,
  context: null,
  adapter: null,
  device: null,
  format: null,
  encoder: null,
  renderPass: null,
  computePass: null,
  resources: new Map(),
  resourceMetadata: new Map(),
  mappedBuffers: new Map(),
  uploads: null,
  inputEvents: [],
  inputInstalled: false,
  dispatchImmediatePointer: null,
  dispatchTextInput: null,
  textSink: null,
  isComposing: false,
  clipboardText: '',
  pickedStorageBytes: null,
  worker: null,
  workerRequests: new Map(),
  nextWorkerRequest: 1,
  workerMappedBuffers: new Map(),
  workerPackets: [],
  workerDispatchScheduled: false,
  diagnosticsVisible: false,
  framebufferWidth: 1,
  framebufferHeight: 1,
  firstGpuError: null,
  decoder: new TextDecoder('utf-8', { fatal: true })
};

const MAGIC = 0x55504750;
const VERSION = 1;
const HEADER_SIZE = 16;
const COMMAND_HEADER_SIZE = 8;
const DIAGNOSTICS_VISIBILITY_KEY = 'progpu.browser.diagnostics.visible';
const BENCHMARK_QUERY_VARIABLES = Object.freeze({
  benchmarkPage: 'PROGPU_SAMPLE_BENCHMARK_PAGE',
  benchmarkWarmupFrames: 'PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES',
  benchmarkMeasureFrames: 'PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES',
  benchmarkVsync: 'PROGPU_SAMPLE_BENCHMARK_VSYNC',
  benchmarkScroll: 'PROGPU_SAMPLE_BENCHMARK_SCROLL',
  benchmarkScrollStep: 'PROGPU_SAMPLE_BENCHMARK_SCROLL_STEP'
});
const uncappedFrameResolvers = [];
const uncappedGpuFenceResolvers = new Map();
const uncappedGpuCompletions = [];
// Keep two rolling three-frame completion groups. Each fence snapshots only the
// work submitted before it, so waiting for the oldest group preserves bounded
// latency while the newer group remains queued. Capturing once per group also
// avoids paying WebGPU promise/task-source overhead on every frame. The former
// single-group scheme drained the entire queue at each checkpoint and serialized
// WASM scene preparation, browser IPC, and GPU execution.
const UNCAPPED_FRAMES_PER_COMPLETION = 3;
const MAX_UNCAPPED_COMPLETION_GROUPS = 2;
let uncappedFramesStarted = 0;
let uncappedFramesSinceCompletion = 0;
let nextUncappedGpuFenceId = 1;
const uncappedFrameChannel = new MessageChannel();
uncappedFrameChannel.port1.onmessage = async () => {
  const resolve = uncappedFrameResolvers.shift();
  if (!resolve) return;

  if (uncappedFramesStarted > 0) {
    uncappedFramesSinceCompletion++;
    if (uncappedFramesSinceCompletion >= UNCAPPED_FRAMES_PER_COMPLETION) {
      uncappedFramesSinceCompletion = 0;
      uncappedGpuCompletions.push(captureUncappedGpuCompletion());
      if (uncappedGpuCompletions.length >= MAX_UNCAPPED_COMPLETION_GROUPS) {
        await uncappedGpuCompletions.shift();
      }
    }
  }

  uncappedFramesStarted++;
  resolve(performance.now());
};

function captureUncappedGpuCompletion() {
  if (state.worker) {
    flushWorkerPackets();
    const id = nextUncappedGpuFenceId++;
    return new Promise(resolve => {
      uncappedGpuFenceResolvers.set(id, resolve);
      state.worker.postMessage({ type: 'uncapped-frame-fence', id });
    });
  }

  return state.device
    ? state.device.queue.onSubmittedWorkDone()
    : Promise.resolve();
}

const textureFormats = [
  undefined,
  'r8unorm', 'r8snorm', 'r8uint', 'r8sint', 'r16uint', 'r16sint', 'r16float',
  'rg8unorm', 'rg8snorm', 'rg8uint', 'rg8sint', 'r32float', 'r32uint', 'r32sint',
  'rg16uint', 'rg16sint', 'rg16float', 'rgba8unorm', 'rgba8unorm-srgb', 'rgba8snorm',
  'rgba8uint', 'rgba8sint', 'bgra8unorm', 'bgra8unorm-srgb', 'rgb10a2uint',
  'rgb10a2unorm', 'rg11b10ufloat', 'rgb9e5ufloat', 'rg32float', 'rg32uint', 'rg32sint',
  'rgba16uint', 'rgba16sint', 'rgba16float', 'rgba32float', 'rgba32uint', 'rgba32sint',
  'stencil8', 'depth16unorm', 'depth24plus', 'depth24plus-stencil8', 'depth32float',
  'depth32float-stencil8', 'bc1-rgba-unorm', 'bc1-rgba-unorm-srgb', 'bc2-rgba-unorm',
  'bc2-rgba-unorm-srgb', 'bc3-rgba-unorm', 'bc3-rgba-unorm-srgb', 'bc4-r-unorm',
  'bc4-r-snorm', 'bc5-rg-unorm', 'bc5-rg-snorm', 'bc6h-rgb-ufloat', 'bc6h-rgb-float',
  'bc7-rgba-unorm', 'bc7-rgba-unorm-srgb', 'etc2-rgb8unorm', 'etc2-rgb8unorm-srgb',
  'etc2-rgb8a1unorm', 'etc2-rgb8a1unorm-srgb', 'etc2-rgba8unorm', 'etc2-rgba8unorm-srgb',
  'eac-r11unorm', 'eac-r11snorm', 'eac-rg11unorm', 'eac-rg11snorm', 'astc-4x4-unorm',
  'astc-4x4-unorm-srgb', 'astc-5x4-unorm', 'astc-5x4-unorm-srgb', 'astc-5x5-unorm',
  'astc-5x5-unorm-srgb', 'astc-6x5-unorm', 'astc-6x5-unorm-srgb', 'astc-6x6-unorm',
  'astc-6x6-unorm-srgb', 'astc-8x5-unorm', 'astc-8x5-unorm-srgb', 'astc-8x6-unorm',
  'astc-8x6-unorm-srgb', 'astc-8x8-unorm', 'astc-8x8-unorm-srgb', 'astc-10x5-unorm',
  'astc-10x5-unorm-srgb', 'astc-10x6-unorm', 'astc-10x6-unorm-srgb', 'astc-10x8-unorm',
  'astc-10x8-unorm-srgb', 'astc-10x10-unorm', 'astc-10x10-unorm-srgb', 'astc-12x10-unorm',
  'astc-12x10-unorm-srgb', 'astc-12x12-unorm', 'astc-12x12-unorm-srgb'
];
const textureDimensions = ['1d', '2d', '3d'];
const textureViewDimensions = [undefined, '1d', '2d', '2d-array', 'cube', 'cube-array', '3d'];
const textureAspects = ['all', 'stencil-only', 'depth-only'];
const addressModes = ['repeat', 'mirror-repeat', 'clamp-to-edge'];
const filterModes = ['nearest', 'linear'];
const compareFunctions = [undefined, 'never', 'less', 'less-equal', 'greater', 'greater-equal', 'equal', 'not-equal', 'always'];
const bufferBindingTypes = [undefined, 'uniform', 'storage', 'read-only-storage'];
const samplerBindingTypes = [undefined, 'filtering', 'non-filtering', 'comparison'];
const textureSampleTypes = [undefined, 'float', 'unfilterable-float', 'depth', 'sint', 'uint'];
const storageTextureAccess = [undefined, 'write-only', 'read-only', 'read-write'];
const loadOps = [undefined, 'clear', 'load'];
const storeOps = [undefined, 'store', 'discard'];
const indexFormats = [undefined, 'uint16', 'uint32'];
const vertexFormats = [
  undefined, 'uint8x2', 'uint8x4', 'sint8x2', 'sint8x4', 'unorm8x2', 'unorm8x4',
  'snorm8x2', 'snorm8x4', 'uint16x2', 'uint16x4', 'sint16x2', 'sint16x4',
  'unorm16x2', 'unorm16x4', 'snorm16x2', 'snorm16x4', 'float16x2', 'float16x4',
  'float32', 'float32x2', 'float32x3', 'float32x4', 'uint32', 'uint32x2', 'uint32x3',
  'uint32x4', 'sint32', 'sint32x2', 'sint32x3', 'sint32x4'
];
const vertexStepModes = ['vertex', 'instance'];
const primitiveTopologies = ['point-list', 'line-list', 'line-strip', 'triangle-list', 'triangle-strip'];
const frontFaces = ['ccw', 'cw'];
const cullModes = ['none', 'front', 'back'];
const blendOperations = ['add', 'subtract', 'reverse-subtract', 'min', 'max'];
const blendFactors = ['zero', 'one', 'src', 'one-minus-src', 'src-alpha', 'one-minus-src-alpha', 'dst', 'one-minus-dst', 'dst-alpha', 'one-minus-dst-alpha', 'src-alpha-saturated', 'constant', 'one-minus-constant'];
const stencilOperations = ['keep', 'zero', 'replace', 'invert', 'increment-clamp', 'decrement-clamp', 'increment-wrap', 'decrement-wrap'];

function enumValue(values, value, name, allowUndefined = false) {
  const result = values[value];
  if (result === undefined && !(allowUndefined && value === 0)) throw new Error(`Unsupported ${name} value ${value}.`);
  return result;
}

function wholeSize(value) {
  return value === 0xffffffffffffffffn ? undefined : Number(value);
}

function imageCopyTexture(view, offset) {
  return {
    texture: requireResource(view.getUint32(offset, true)),
    mipLevel: view.getUint32(offset + 4, true),
    origin: {
      x: view.getUint32(offset + 8, true),
      y: view.getUint32(offset + 12, true),
      z: view.getUint32(offset + 16, true)
    },
    aspect: enumValue(textureAspects, view.getUint32(offset + 20, true), 'texture aspect')
  };
}

function imageCopyBuffer(view, offset) {
  return {
    buffer: requireResource(view.getUint32(offset, true)),
    offset: Number(view.getBigUint64(offset + 8, true)),
    bytesPerRow: view.getUint32(offset + 16, true),
    rowsPerImage: view.getUint32(offset + 20, true)
  };
}

function extent3d(view, offset) {
  return {
    width: view.getUint32(offset, true),
    height: view.getUint32(offset + 4, true),
    depthOrArrayLayers: view.getUint32(offset + 8, true)
  };
}

class PacketCursor {
  constructor(view, offset, length) {
    this.view = view;
    this.offset = offset;
    this.end = offset + length;
  }

  require(count) {
    if (count < 0 || this.offset + count > this.end) throw new Error('Truncated pipeline descriptor.');
  }

  u32() { this.require(4); const value = this.view.getUint32(this.offset, true); this.offset += 4; return value; }
  i32() { this.require(4); const value = this.view.getInt32(this.offset, true); this.offset += 4; return value; }
  u64() { this.require(8); const value = this.view.getBigUint64(this.offset, true); this.offset += 8; return value; }
  f32() { this.require(4); const value = this.view.getFloat32(this.offset, true); this.offset += 4; return value; }
  f64() { this.require(8); const value = this.view.getFloat64(this.offset, true); this.offset += 8; return value; }
  string() {
    const length = this.u32();
    this.require(length);
    const bytes = new Uint8Array(this.view.buffer, this.view.byteOffset + this.offset, length);
    this.offset += length;
    return state.decoder.decode(bytes);
  }
  done() { if (this.offset !== this.end) throw new Error(`Pipeline descriptor has ${this.end - this.offset} trailing bytes.`); }
}

function readConstants(cursor) {
  const count = cursor.u32();
  if (count === 0) return undefined;
  const constants = {};
  for (let index = 0; index < count; index++) constants[cursor.string()] = cursor.f64();
  return constants;
}

function readStage(cursor) {
  const stage = { module: requireResource(cursor.u32()) };
  const entryPoint = cursor.string();
  const constants = readConstants(cursor);
  if (entryPoint.length !== 0) stage.entryPoint = entryPoint;
  if (constants !== undefined) stage.constants = constants;
  return stage;
}

function readVertexState(cursor) {
  const vertex = readStage(cursor);
  const bufferCount = cursor.u32();
  const buffers = [];
  for (let bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++) {
    const arrayStride = Number(cursor.u64());
    const stepModeValue = cursor.u32();
    const attributeCount = cursor.u32();
    const attributes = [];
    for (let attributeIndex = 0; attributeIndex < attributeCount; attributeIndex++) {
      attributes.push({
        format: enumValue(vertexFormats, cursor.u32(), 'vertex format'),
        offset: Number(cursor.u64()),
        shaderLocation: cursor.u32()
      });
    }
    if (stepModeValue > 1) throw new Error(`Unsupported vertex step mode ${stepModeValue}.`);
    buffers.push({ arrayStride, stepMode: vertexStepModes[stepModeValue], attributes });
  }
  vertex.buffers = buffers;
  return vertex;
}

function readStencilFace(cursor) {
  const compare = cursor.u32();
  const face = {
    failOp: enumValue(stencilOperations, cursor.u32(), 'stencil operation'),
    depthFailOp: enumValue(stencilOperations, cursor.u32(), 'stencil operation'),
    passOp: enumValue(stencilOperations, cursor.u32(), 'stencil operation')
  };
  if (compare !== 0) face.compare = enumValue(compareFunctions, compare, 'compare function');
  return face;
}

function readDepthStencil(cursor) {
  const format = cursor.u32();
  const result = {
    format: enumValue(textureFormats, format, 'depth-stencil format'),
    depthWriteEnabled: cursor.u32() !== 0
  };
  const depthCompare = cursor.u32();
  if (depthCompare !== 0) result.depthCompare = enumValue(compareFunctions, depthCompare, 'depth compare function');
  result.stencilFront = readStencilFace(cursor);
  result.stencilBack = readStencilFace(cursor);
  result.stencilReadMask = cursor.u32();
  result.stencilWriteMask = cursor.u32();
  result.depthBias = cursor.i32();
  result.depthBiasSlopeScale = cursor.f32();
  result.depthBiasClamp = cursor.f32();
  return result;
}

function readBlendComponent(cursor) {
  return {
    operation: enumValue(blendOperations, cursor.u32(), 'blend operation'),
    srcFactor: enumValue(blendFactors, cursor.u32(), 'blend factor'),
    dstFactor: enumValue(blendFactors, cursor.u32(), 'blend factor')
  };
}

function readFragmentState(cursor) {
  const fragment = readStage(cursor);
  const targetCount = cursor.u32();
  const targets = [];
  for (let index = 0; index < targetCount; index++) {
    const target = { format: enumValue(textureFormats, cursor.u32(), 'color-target format') };
    if (cursor.u32() !== 0) target.blend = { color: readBlendComponent(cursor), alpha: readBlendComponent(cursor) };
    target.writeMask = cursor.u32();
    targets.push(target);
  }
  fragment.targets = targets;
  return fragment;
}

function chooseExecutionMode(request, diagnostics) {
  if (request.executionMode === 'MainThread') return 'MainThread';
  if (request.executionMode === 'Auto') {
    if (typeof OffscreenCanvas !== 'undefined' && HTMLCanvasElement.prototype.transferControlToOffscreen) {
      return globalThis.crossOriginIsolated && typeof SharedArrayBuffer !== 'undefined' ? 'IsolatedWorker' : 'Worker';
    }
    return 'MainThread';
  }
  if (typeof OffscreenCanvas === 'undefined' || !HTMLCanvasElement.prototype.transferControlToOffscreen) {
    diagnostics.push(`${request.executionMode} was requested, but OffscreenCanvas transfer is unavailable; using the main thread.`);
    return 'MainThread';
  }
  if (request.executionMode === 'IsolatedWorker' && !globalThis.crossOriginIsolated) {
    diagnostics.push('IsolatedWorker requires COOP/COEP response headers; using the ordinary worker transport.');
    return 'Worker';
  }
  return request.executionMode;
}

function resizeCanvas() {
  const dpr = globalThis.devicePixelRatio || 1;
  const rect = state.canvas.getBoundingClientRect();
  const width = Math.max(1, Math.round(rect.width * dpr));
  const height = Math.max(1, Math.round(rect.height * dpr));
  const changed = state.framebufferWidth !== width || state.framebufferHeight !== height;
  state.framebufferWidth = width;
  state.framebufferHeight = height;
  if (state.worker) {
    if (changed) state.worker.postMessage({ type: 'resize', width, height });
    return;
  }
  if (state.canvas.width !== width || state.canvas.height !== height) {
    state.canvas.width = width;
    state.canvas.height = height;
  }
}

function updateVisualViewport() {
  if (!state.canvas) return;
  const viewport = globalThis.visualViewport;
  const keyboardRect = navigator.virtualKeyboard?.boundingRect;
  const keyboardInset = keyboardRect && keyboardRect.height > 0 ? keyboardRect.height : 0;
  const rootStyle = document.documentElement.style;
  rootStyle.setProperty('--progpu-viewport-left', `${viewport?.offsetLeft || 0}px`);
  rootStyle.setProperty('--progpu-viewport-top', `${viewport?.offsetTop || 0}px`);
  rootStyle.setProperty('--progpu-viewport-width', `${viewport?.width || globalThis.innerWidth}px`);
  rootStyle.setProperty('--progpu-viewport-height', `${viewport?.height || globalThis.innerHeight}px`);
  rootStyle.setProperty('--progpu-keyboard-inset', `${keyboardInset}px`);
  resizeCanvas();
}

const browserKeyCodes = (() => {
  const result = new Map();
  for (let index = 0; index < 26; index++) result.set(`Key${String.fromCharCode(65 + index)}`, 1 + index);
  for (let index = 0; index < 10; index++) result.set(`Digit${index}`, 27 + index);
  for (let index = 1; index <= 12; index++) result.set(`F${index}`, 36 + index);
  Object.entries({
    Backspace: 49, Tab: 50, Enter: 51, Escape: 52, Space: 53, Insert: 54, Delete: 55,
    Home: 56, End: 57, PageUp: 58, PageDown: 59, ArrowLeft: 60, ArrowRight: 61,
    ArrowUp: 62, ArrowDown: 63, ShiftLeft: 64, ShiftRight: 65, ControlLeft: 66,
    ControlRight: 67, AltLeft: 68, AltRight: 69, MetaLeft: 70, MetaRight: 71
  }).forEach(([code, value]) => result.set(code, value));
  for (let index = 0; index < 10; index++) result.set(`Numpad${index}`, 72 + index);
  return result;
})();

function queueInputEvent(kind, x = 0, y = 0, a = 0, b = 0, data = 0, modifiers = 0, flags = 0,
  timestamp = performance.now(), pressure = 0, width = 0, height = 0, pointerType = 0, button = -1) {
  const event = { kind, x, y, a, b, data, modifiers, flags, timestamp, pressure, width, height, pointerType, button };
  const last = state.inputEvents[state.inputEvents.length - 1];
  if (kind === 1 && last?.kind === 1 && last.data === data) {
    state.inputEvents[state.inputEvents.length - 1] = event;
    return;
  }
  if (kind === 4 && last?.kind === 4) {
    last.x = x; last.y = y; last.a += a; last.b += b;
    return;
  }
  state.inputEvents.push(event);
  if (state.inputEvents.length > 4096) state.inputEvents.splice(0, state.inputEvents.length - 4096);
}

function pointerPosition(event) {
  const bounds = state.canvas.getBoundingClientRect();
  return { x: event.clientX - bounds.left, y: event.clientY - bounds.top };
}

function eventModifiers(event) {
  return (event.shiftKey ? 1 : 0) | (event.ctrlKey ? 2 : 0) |
    (event.altKey ? 4 : 0) | (event.metaKey ? 8 : 0);
}

function pointerTypeValue(type) {
  return type === 'touch' ? 1 : type === 'pen' ? 2 : 0;
}

function queuePointerEvent(kind, event, point) {
  const flags = event.buttons | (event.isPrimary ? 0x10000 : 0);
  queueInputEvent(kind, point.x, point.y, 0, 0, event.pointerId, eventModifiers(event), flags,
    event.timeStamp, event.pressure || 0, event.width || 0, event.height || 0,
    pointerTypeValue(event.pointerType), event.button);
}

function dispatchPointerEvent(kind, event, point) {
  if (state.dispatchImmediatePointer) {
    try {
      if (state.dispatchImmediatePointer(kind, point.x, point.y, event.button, event.buttons,
        event.pointerId, pointerTypeValue(event.pointerType), event.pressure || 0,
        event.width || 0, event.height || 0, event.isPrimary, event.timeStamp,
        eventModifiers(event))) return;
    } catch (error) {
      globalThis.console.error('[ProGPU] Immediate pointer dispatch failed.', error);
    }
  }
  queuePointerEvent(kind, event, point);
}

function installBrowserInput() {
  if (state.inputInstalled) return;
  state.inputInstalled = true;
  state.canvas.style.touchAction = 'none';

  const textSink = document.createElement('textarea');
  textSink.id = 'progpu-text-input';
  textSink.setAttribute('aria-label', 'ProGPU canvas keyboard input');
  textSink.tabIndex = -1;
  textSink.autocapitalize = 'off';
  textSink.autocomplete = 'off';
  textSink.spellcheck = false;
  document.body.appendChild(textSink);
  state.textSink = textSink;

  state.canvas.addEventListener('pointermove', event => {
    const point = pointerPosition(event);
    const coalesced = typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [];
    if (coalesced.length > 1) {
      for (const sample of coalesced) queuePointerEvent(1, sample, pointerPosition(sample));
    } else {
      queuePointerEvent(1, event, point);
    }
  });
  state.canvas.addEventListener('pointerdown', event => {
    const point = pointerPosition(event);
    dispatchPointerEvent(2, event, point);
    try { state.canvas.setPointerCapture(event.pointerId); } catch { }
    event.preventDefault();
  });
  state.canvas.addEventListener('pointerup', event => {
    const point = pointerPosition(event);
    dispatchPointerEvent(3, event, point);
    try { state.canvas.releasePointerCapture(event.pointerId); } catch { }
    event.preventDefault();
  });
  state.canvas.addEventListener('pointercancel', event => {
    const point = pointerPosition(event);
    dispatchPointerEvent(9, event, point);
    try { state.canvas.releasePointerCapture(event.pointerId); } catch { }
    event.preventDefault();
  });
  state.canvas.addEventListener('contextmenu', event => event.preventDefault());
  state.canvas.addEventListener('wheel', event => {
    const point = pointerPosition(event);
    const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 :
      event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? state.canvas.clientHeight : 1;
    queueInputEvent(4, point.x, point.y, -event.deltaX * unit / 100, -event.deltaY * unit / 100,
      1, eventModifiers(event), 0, event.timeStamp, 0, 0, 0, 0, -1);
    event.preventDefault();
  }, { passive: false });

  globalThis.addEventListener('keydown', event => {
    if (document.activeElement === textSink && (event.key === 'Backspace' || event.key === 'Delete' || event.key === 'Enter')) return;
    const key = browserKeyCodes.get(event.code) || 0;
    if (key !== 0) queueInputEvent(5, 0, 0, 0, 0, key, eventModifiers(event), event.repeat ? 1 : 0);
    if (['Tab', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', 'Space'].includes(event.code)) {
      event.preventDefault();
    }
  }, true);
  globalThis.addEventListener('keyup', event => {
    if (document.activeElement === textSink && (event.key === 'Backspace' || event.key === 'Delete' || event.key === 'Enter')) return;
    const key = browserKeyCodes.get(event.code) || 0;
    if (key !== 0) queueInputEvent(6, 0, 0, 0, 0, key, eventModifiers(event));
  }, true);
  textSink.addEventListener('beforeinput', event => {
    if (!state.dispatchTextInput || event.isComposing) return;
    const kind = event.inputType === 'deleteContentBackward' ? 1 :
      event.inputType === 'deleteContentForward' ? 2 :
      event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph' ? 3 : -1;
    if (kind >= 0) {
      state.dispatchTextInput(kind, '', false);
      event.preventDefault();
    }
  });
  textSink.addEventListener('input', event => {
    if (state.isComposing || event.isComposing) return;
    if (event.inputType === 'insertCompositionText' || event.inputType === 'insertFromComposition') {
      textSink.value = '';
      return;
    }
    const text = event.data ?? textSink.value;
    if (text && state.dispatchTextInput) state.dispatchTextInput(0, text, false);
    textSink.value = '';
  });
  textSink.addEventListener('compositionstart', () => {
    state.isComposing = true;
    if (state.dispatchTextInput) state.dispatchTextInput(4, '', true);
  });
  textSink.addEventListener('compositionupdate', event => {
    if (state.dispatchTextInput) state.dispatchTextInput(5, event.data || '', true);
  });
  textSink.addEventListener('compositionend', event => {
    if (state.dispatchTextInput) state.dispatchTextInput(6, event.data || '', false);
    state.isComposing = false;
    textSink.value = '';
  });
  textSink.addEventListener('paste', event => {
    state.clipboardText = event.clipboardData?.getData('text/plain') || '';
    if (state.clipboardText && state.dispatchTextInput) state.dispatchTextInput(0, state.clipboardText, false);
    event.preventDefault();
  });
  globalThis.addEventListener('blur', () => queueInputEvent(8));
}

function drainInputEvents(destination, capacity) {
  const count = Math.min(Math.max(0, capacity | 0), state.inputEvents.length);
  const byteLength = count * 64;
  const heap = runtime.localHeapViewU8();
  if (destination < 0 || destination + byteLength > heap.byteLength) throw new RangeError('Input destination is outside WASM memory.');
  const view = new DataView(heap.buffer, heap.byteOffset + destination, byteLength);
  for (let index = 0; index < count; index++) {
    const event = state.inputEvents[index];
    const offset = index * 64;
    view.setUint32(offset, event.kind, true);
    view.setFloat32(offset + 4, event.x, true);
    view.setFloat32(offset + 8, event.y, true);
    view.setFloat32(offset + 12, event.a, true);
    view.setFloat32(offset + 16, event.b, true);
    view.setUint32(offset + 20, event.data, true);
    view.setUint32(offset + 24, event.modifiers, true);
    view.setUint32(offset + 28, event.flags, true);
    view.setFloat64(offset + 32, event.timestamp, true);
    view.setFloat32(offset + 40, event.pressure, true);
    view.setFloat32(offset + 44, event.width, true);
    view.setFloat32(offset + 48, event.height, true);
    view.setUint32(offset + 52, event.pointerType, true);
    view.setInt32(offset + 56, event.button, true);
    view.setUint32(offset + 60, 0, true);
  }
  if (count !== 0) state.inputEvents.splice(0, count);
  return count;
}

function setCanvasCursor(cursor) {
  if (state.canvas) state.canvas.style.cursor = cursor;
}

function configureTextInput(inputMode, enterKeyHint, autoCapitalize, spellCheck, isPassword, acceptsReturn, x, y, width, height) {
  const textSink = state.textSink;
  if (!textSink) return;
  textSink.inputMode = inputMode || 'text';
  textSink.enterKeyHint = enterKeyHint || (acceptsReturn ? 'enter' : 'done');
  textSink.autocapitalize = autoCapitalize || 'off';
  textSink.spellcheck = Boolean(spellCheck);
  textSink.autocomplete = isPassword ? 'current-password' : 'off';
  textSink.setAttribute('aria-label', isPassword ? 'ProGPU password input' : 'ProGPU text input');
  const viewport = globalThis.visualViewport;
  const viewportLeft = viewport?.offsetLeft || 0;
  const viewportTop = viewport?.offsetTop || 0;
  const viewportWidth = viewport?.width || globalThis.innerWidth;
  const viewportHeight = viewport?.height || globalThis.innerHeight;
  textSink.style.left = `${Math.max(viewportLeft, Math.min(viewportLeft + viewportWidth - 2, x))}px`;
  textSink.style.top = `${Math.max(viewportTop, Math.min(viewportTop + viewportHeight - 2, y + height))}px`;
  textSink.style.width = `${Math.max(2, Math.min(width || 2, viewportWidth))}px`;
  textSink.style.height = '2px';
  textSink.value = '';
  textSink.focus({ preventScroll: true });
  if (navigator.virtualKeyboard) {
    try { navigator.virtualKeyboard.overlaysContent = true; } catch { }
    try { navigator.virtualKeyboard.show(); } catch { }
  }
}

function hideTextInput() {
  const sink = state.textSink;
  if (!sink) return;
  if (state.isComposing && state.dispatchTextInput) state.dispatchTextInput(7, '', false);
  state.isComposing = false;
  sink.value = '';
  sink.blur();
  if (navigator.virtualKeyboard) {
    try { navigator.virtualKeyboard.hide(); } catch { }
  }
}

function setClipboardText(text) {
  state.clipboardText = String(text ?? '');
  if (navigator.clipboard?.writeText) navigator.clipboard.writeText(state.clipboardText).catch(() => { });
}

function getClipboardText() {
  return state.clipboardText;
}

async function stagePickedFile(file) {
  state.pickedStorageBytes = new Uint8Array(await file.arrayBuffer());
  return encodeURIComponent(file.name);
}

function pickStorageWithInput(filters) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    let settled = false;
    input.type = 'file';
    input.accept = filters || '';
    input.style.display = 'none';
    document.body.appendChild(input);

    const cleanup = () => {
      input.remove();
    };
    const finish = async file => {
      if (settled) return;
      settled = true;
      cleanup();
      if (!file) { resolve(''); return; }
      try { resolve(await stagePickedFile(file)); }
      catch { state.pickedStorageBytes = null; resolve(''); }
    };
    input.addEventListener('change', () => finish(input.files?.[0] || null), { once: true });
    input.addEventListener('cancel', () => finish(null), { once: true });
    try { input.click(); }
    catch { finish(null); }
  });
}

async function pickStorage(mode, filters, defaultName) {
  if (mode === 1) return Promise.resolve(defaultName || 'untitled.txt');
  if (mode !== 0) return Promise.resolve('');
  state.pickedStorageBytes = null;

  return await pickStorageWithInput(filters);
}

function getPickedStorageLength() {
  return state.pickedStorageBytes?.byteLength ?? -1;
}

function copyPickedStorage(destination, length) {
  const bytes = state.pickedStorageBytes;
  if (!bytes || length !== bytes.byteLength) return -1;
  const heap = runtime.localHeapViewU8();
  if (destination < 0 || destination + length > heap.byteLength) throw new RangeError('File destination is outside WASM memory.');
  heap.set(bytes, destination);
  return length;
}

function clearPickedStorage() {
  state.pickedStorageBytes = null;
}

function downloadText(name, text) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name || 'download.txt';
  anchor.click();
  queueMicrotask(() => URL.revokeObjectURL(url));
}

async function initializeGpu(request, canvas, executionMode, diagnostics) {
  if (!globalThis.navigator?.gpu) {
    return {
      isSupported: false,
      executionMode: 1,
      requestedProfile: request.gpuProfile === 'Full' ? 1 : 0,
      activeProfile: 0,
      diagnostics: ['navigator.gpu is unavailable. Enable WebGPU or use a current WebGPU browser.']
    };
  }

  state.canvas = canvas;
  state.context = state.canvas.getContext('webgpu');
  if (!state.context) throw new Error('The selected canvas cannot create a WebGPU context.');

  state.adapter = await navigator.gpu.requestAdapter({ powerPreference: request.powerPreference });
  if (!state.adapter) throw new Error('No WebGPU adapter matched the requested power preference.');

  const supportsBgraStorage = state.adapter.features.has('bgra8unorm-storage');
  const requiredFeatures = [];
  let activeProfile = request.gpuProfile === 'Full' ? 1 : 0;
  if (request.gpuProfile === 'Full' && supportsBgraStorage) requiredFeatures.push('bgra8unorm-storage');
  if (request.gpuProfile === 'Full' && !supportsBgraStorage) {
    activeProfile = 0;
    diagnostics.push('Full profile downgraded: bgra8unorm-storage is unavailable; dependent Wavefront storage output is disabled.');
  }

  state.device = await state.adapter.requestDevice({ requiredFeatures });
  state.format = navigator.gpu.getPreferredCanvasFormat();
  state.context.configure({ device: state.device, format: state.format, alphaMode: 'premultiplied' });
  state.device.addEventListener('uncapturederror', event => {
    const detail = String(event.error?.message || event.error);
    globalThis.console.error(`[ProGPU] WebGPU validation error: ${detail}`);
    if (state.firstGpuError === null) {
      state.firstGpuError = detail;
      reportGpuStatus('WebGPU validation error', detail, true);
    }
  });
  state.device.lost.then(info => {
    reportGpuStatus('WebGPU device lost', `${info.reason}: ${info.message}. Reloading the retained application to reconstruct GPU resources.`, true);
    if (isDispatcherWorker) globalThis.postMessage({ type: 'device-lost' });
    else globalThis.setTimeout(() => globalThis.location?.reload(), 250);
  });

  const adapterInfo = state.adapter.info;
  return {
    isSupported: true,
    adapterName: adapterInfo?.description || adapterInfo?.device || adapterInfo?.vendor || 'WebGPU adapter',
    canvasFormat: state.format,
    executionMode: executionMode === 'IsolatedWorker' ? 3 : executionMode === 'Worker' ? 2 : 1,
    requestedProfile: request.gpuProfile === 'Full' ? 1 : 0,
    activeProfile,
    isCrossOriginIsolated: !!globalThis.crossOriginIsolated,
    supportsSharedArrayBuffer: typeof SharedArrayBuffer !== 'undefined',
    supportsOffscreenCanvas: typeof OffscreenCanvas !== 'undefined',
    supportsBgra8UnormStorage: supportsBgraStorage,
    features: [...state.adapter.features],
    diagnostics
  };
}

function reportGpuStatus(title, detail, isError) {
  if (isError) globalThis.console.error(`[ProGPU] ${title}: ${detail}`);
  if (isDispatcherWorker) globalThis.postMessage({ type: 'status', title, detail, isError });
  else setStatus(title, detail, isError);
}

function handleWorkerMessage(event) {
  const message = event.data;
  if (message.type === 'status') {
    if (message.isError) globalThis.console.error(`[ProGPU] ${message.title}: ${message.detail}`);
    setStatus(message.title, message.detail, message.isError);
  } else if (message.type === 'device-lost') {
    globalThis.setTimeout(() => globalThis.location.reload(), 250);
  } else if (message.type === 'uncapped-frame-ready') {
    const resolve = uncappedGpuFenceResolvers.get(message.id);
    uncappedGpuFenceResolvers.delete(message.id);
    resolve?.();
  } else if (message.type === 'response') {
    const pending = state.workerRequests.get(message.id);
    if (!pending) return;
    state.workerRequests.delete(message.id);
    if (message.error) pending.reject(new Error(message.error));
    else pending.resolve(message);
  }
}

function workerRequest(type, payload = {}, transfer = []) {
  flushWorkerPackets();
  const id = state.nextWorkerRequest++;
  return new Promise((resolve, reject) => {
    state.workerRequests.set(id, { resolve, reject });
    state.worker.postMessage({ type, id, ...payload }, transfer);
  });
}

function flushWorkerPackets() {
  if (!state.worker || state.workerPackets.length === 0) {
    state.workerDispatchScheduled = false;
    return;
  }
  const packets = state.workerPackets;
  state.workerPackets = [];
  state.workerDispatchScheduled = false;
  state.worker.postMessage({ type: 'dispatch-batch', packets }, packets);
}

async function initialize(requestJson) {
  const request = JSON.parse(requestJson);
  const diagnostics = [];
  const executionMode = chooseExecutionMode(request, diagnostics);
  state.canvas = document.querySelector(request.canvasSelector);
  if (!(state.canvas instanceof HTMLCanvasElement)) throw new Error(`Canvas not found: ${request.canvasSelector}`);
  updateVisualViewport();
  globalThis.visualViewport?.addEventListener('resize', updateVisualViewport);
  globalThis.visualViewport?.addEventListener('scroll', updateVisualViewport);
  globalThis.addEventListener('orientationchange', updateVisualViewport);
  navigator.virtualKeyboard?.addEventListener('geometrychange', updateVisualViewport);
  installBrowserInput();
  resizeCanvas();
  new ResizeObserver(resizeCanvas).observe(state.canvas);

  if (executionMode === 'Worker' || executionMode === 'IsolatedWorker') {
    const offscreen = state.canvas.transferControlToOffscreen();
    state.worker = new Worker(new URL(import.meta.url), { type: 'module', name: 'ProGPU WebGPU dispatcher' });
    state.worker.addEventListener('message', handleWorkerMessage);
    state.worker.addEventListener('error', event => setStatus('WebGPU worker failed', event.message, true));
    const response = await workerRequest('initialize', {
      request,
      executionMode,
      canvas: offscreen,
      width: state.framebufferWidth,
      height: state.framebufferHeight,
      diagnostics
    }, [offscreen]);
    return JSON.stringify(response.capabilities);
  }

  if (request.syncReadbackMode === 'IsolatedWorkerOnly') {
    diagnostics.push('Synchronous readback is disabled because the active dispatcher is not an isolated worker.');
  }
  return JSON.stringify(await initializeGpu(request, state.canvas, executionMode, diagnostics));
}

function requireResource(handle) {
  const value = state.resources.get(handle);
  if (!value) throw new Error(`Stale or unknown WebGPU handle 0x${handle.toString(16)}.`);
  return value;
}

function dispatch(address, length) {
  const heap = runtime.localHeapViewU8();
  if (state.worker) {
    if (address < 0 || length < HEADER_SIZE || address + length > heap.byteLength) throw new RangeError('Command packet is outside WASM memory.');
    const packet = heap.slice(address, address + length).buffer;
    state.workerPackets.push(packet);
    if (!state.workerDispatchScheduled) {
      state.workerDispatchScheduled = true;
      queueMicrotask(flushWorkerPackets);
    }
    return;
  }
  dispatchPacket(heap, address, length);
}

function dispatchPacket(heap, address, length) {
  if (!state.device) throw new Error('WebGPU has not been initialized.');
  if (address < 0 || length < HEADER_SIZE || address + length > heap.byteLength) throw new RangeError('Command packet is outside WASM memory.');
  const view = new DataView(heap.buffer, heap.byteOffset + address, length);
  if (view.getUint32(0, true) !== MAGIC) throw new Error('Invalid ProGPU command packet magic.');
  if (view.getUint16(4, true) !== VERSION) throw new Error(`Unsupported ProGPU command protocol ${view.getUint16(4, true)}.`);
  const packetLength = view.getUint32(8, true);
  const commandCount = view.getUint32(12, true);
  if (packetLength !== length) throw new Error('Command packet length mismatch.');

  let offset = HEADER_SIZE;
  for (let index = 0; index < commandCount; index++) {
    if (offset + COMMAND_HEADER_SIZE > length) throw new Error('Truncated command header.');
    const opcode = view.getUint16(offset, true);
    const commandLength = view.getUint32(offset + 4, true);
    if (commandLength < COMMAND_HEADER_SIZE || offset + commandLength > length) throw new Error(`Invalid command length at ${index}.`);
    execute(opcode, view, offset + COMMAND_HEADER_SIZE, commandLength - COMMAND_HEADER_SIZE, heap.byteOffset + address);
    offset += (commandLength + 7) & ~7;
  }
  if (offset !== length) throw new Error('Command count does not consume the packet.');
}

function execute(opcode, view, payload, payloadLength, absoluteBase) {
  switch (opcode) {
    case 1: {
      resizeCanvas();
      state.encoder = state.device.createCommandEncoder({ label: 'ProGPU frame' });
      const clearValue = {
        r: view.getFloat32(payload, true), g: view.getFloat32(payload + 4, true),
        b: view.getFloat32(payload + 8, true), a: view.getFloat32(payload + 12, true)
      };
      state.renderPass = state.encoder.beginRenderPass({
        colorAttachments: [{ view: state.context.getCurrentTexture().createView(), clearValue, loadOp: 'clear', storeOp: 'store' }]
      });
      break;
    }
    case 2:
      if (state.renderPass) { state.renderPass.end(); state.renderPass = null; }
      if (state.computePass) { state.computePass.end(); state.computePass = null; }
      state.device.queue.submit([state.encoder.finish()]);
      state.encoder = null;
      break;
    case 10: {
      const handle = view.getUint32(payload, true);
      const size = Number(view.getBigUint64(payload + 8, true));
      const usage = view.getUint32(payload + 16, true);
      const mappedAtCreation = view.getUint32(payload + 20, true) !== 0;
      state.resources.set(handle, state.device.createBuffer({ size, usage, mappedAtCreation }));
      break;
    }
    case 11: {
      const handle = view.getUint32(payload, true);
      const viewFormatCount = view.getUint32(payload + 36, true);
      if (44 + viewFormatCount * 4 > payloadLength) throw new Error('Truncated texture view-format list.');
      const viewFormats = [];
      for (let index = 0; index < viewFormatCount; index++) {
        viewFormats.push(enumValue(textureFormats, view.getUint32(payload + 44 + index * 4, true), 'texture format'));
      }
      const dimension = enumValue(textureDimensions, view.getUint32(payload + 8, true), 'texture dimension');
      state.resources.set(handle, state.device.createTexture({
        usage: view.getUint32(payload + 4, true),
        dimension,
        size: {
          width: view.getUint32(payload + 12, true),
          height: view.getUint32(payload + 16, true),
          depthOrArrayLayers: view.getUint32(payload + 20, true)
        },
        format: enumValue(textureFormats, view.getUint32(payload + 24, true), 'texture format'),
        mipLevelCount: view.getUint32(payload + 28, true),
        sampleCount: view.getUint32(payload + 32, true),
        viewFormats
      }));
      state.resourceMetadata.set(handle, { kind: 'texture', dimension });
      break;
    }
    case 12: {
      const handle = view.getUint32(payload, true);
      const textureHandle = view.getUint32(payload + 4, true);
      const texture = requireResource(textureHandle);
      const formatValue = view.getUint32(payload + 8, true);
      const dimensionValue = view.getUint32(payload + 12, true);
      const mipLevelCount = view.getUint32(payload + 20, true);
      const arrayLayerCount = view.getUint32(payload + 28, true);
      const descriptor = {
        baseMipLevel: view.getUint32(payload + 16, true),
        baseArrayLayer: view.getUint32(payload + 24, true),
        aspect: enumValue(textureAspects, view.getUint32(payload + 32, true), 'texture aspect')
      };
      if (formatValue !== 0) descriptor.format = enumValue(textureFormats, formatValue, 'texture format');
      if (dimensionValue !== 0) descriptor.dimension = enumValue(textureViewDimensions, dimensionValue, 'texture-view dimension');
      if (mipLevelCount !== 0xffffffff) descriptor.mipLevelCount = mipLevelCount;
      if (arrayLayerCount !== 0xffffffff) descriptor.arrayLayerCount = arrayLayerCount;
      state.resources.set(handle, texture.createView(descriptor));
      state.resourceMetadata.set(handle, {
        kind: 'texture-view',
        dimension: descriptor.dimension || state.resourceMetadata.get(textureHandle)?.dimension || '2d'
      });
      break;
    }
    case 13: {
      const handle = view.getUint32(payload, true);
      const compare = view.getUint32(payload + 36, true);
      const maxAnisotropy = view.getUint32(payload + 40, true);
      const descriptor = {
        addressModeU: enumValue(addressModes, view.getUint32(payload + 4, true), 'address mode'),
        addressModeV: enumValue(addressModes, view.getUint32(payload + 8, true), 'address mode'),
        addressModeW: enumValue(addressModes, view.getUint32(payload + 12, true), 'address mode'),
        magFilter: enumValue(filterModes, view.getUint32(payload + 16, true), 'filter mode'),
        minFilter: enumValue(filterModes, view.getUint32(payload + 20, true), 'filter mode'),
        mipmapFilter: enumValue(filterModes, view.getUint32(payload + 24, true), 'mipmap filter mode'),
        lodMinClamp: view.getFloat32(payload + 28, true),
        lodMaxClamp: view.getFloat32(payload + 32, true)
      };
      if (compare !== 0) descriptor.compare = enumValue(compareFunctions, compare, 'compare function');
      if (maxAnisotropy > 1) descriptor.maxAnisotropy = maxAnisotropy;
      state.resources.set(handle, state.device.createSampler(descriptor));
      break;
    }
    case 14: {
      const handle = view.getUint32(payload, true);
      const byteCount = view.getUint32(payload + 4, true);
      if (8 + byteCount > payloadLength) throw new Error('Truncated WGSL command.');
      const bytes = new Uint8Array(view.buffer, view.byteOffset + payload + 8, byteCount);
      state.resources.set(handle, state.device.createShaderModule({ code: state.decoder.decode(bytes) }));
      break;
    }
    case 15: {
      const handle = view.getUint32(payload, true);
      const count = view.getUint32(payload + 4, true);
      if (8 + count * 52 > payloadLength) throw new Error('Truncated bind-group-layout entries.');
      const entries = [];
      for (let index = 0; index < count; index++) {
        const offset = payload + 8 + index * 52;
        const entry = { binding: view.getUint32(offset, true), visibility: view.getUint32(offset + 4, true) };
        const bufferType = view.getUint32(offset + 8, true);
        const samplerType = view.getUint32(offset + 24, true);
        const textureType = view.getUint32(offset + 28, true);
        const storageAccess = view.getUint32(offset + 40, true);
        if (bufferType !== 0) {
          entry.buffer = {
            type: enumValue(bufferBindingTypes, bufferType, 'buffer binding type'),
            hasDynamicOffset: view.getUint32(offset + 12, true) !== 0,
            minBindingSize: Number(view.getBigUint64(offset + 16, true))
          };
        } else if (samplerType !== 0) {
          entry.sampler = { type: enumValue(samplerBindingTypes, samplerType, 'sampler binding type') };
        } else if (textureType !== 0) {
          entry.texture = {
            sampleType: enumValue(textureSampleTypes, textureType, 'texture sample type'),
            viewDimension: enumValue(textureViewDimensions, view.getUint32(offset + 32, true), 'texture-view dimension'),
            multisampled: view.getUint32(offset + 36, true) !== 0
          };
        } else if (storageAccess !== 0) {
          entry.storageTexture = {
            access: enumValue(storageTextureAccess, storageAccess, 'storage texture access'),
            format: enumValue(textureFormats, view.getUint32(offset + 44, true), 'texture format'),
            viewDimension: enumValue(textureViewDimensions, view.getUint32(offset + 48, true), 'texture-view dimension')
          };
        } else {
          throw new Error(`Bind-group-layout entry ${index} has no resource layout.`);
        }
        entries.push(entry);
      }
      state.resources.set(handle, state.device.createBindGroupLayout({ entries }));
      break;
    }
    case 16: {
      const handle = view.getUint32(payload, true);
      const count = view.getUint32(payload + 4, true);
      if (8 + count * 4 > payloadLength) throw new Error('Truncated pipeline-layout handles.');
      const bindGroupLayouts = [];
      for (let index = 0; index < count; index++) bindGroupLayouts.push(requireResource(view.getUint32(payload + 8 + index * 4, true)));
      state.resources.set(handle, state.device.createPipelineLayout({ bindGroupLayouts }));
      break;
    }
    case 17: {
      const handle = view.getUint32(payload, true);
      const layout = requireResource(view.getUint32(payload + 4, true));
      const count = view.getUint32(payload + 8, true);
      if (12 + count * 32 > payloadLength) throw new Error('Truncated bind-group entries.');
      const entries = [];
      for (let index = 0; index < count; index++) {
        const offset = payload + 12 + index * 32;
        const binding = view.getUint32(offset, true);
        const kind = view.getUint32(offset + 4, true);
        const resource = requireResource(view.getUint32(offset + 8, true));
        if (kind === 1) {
          const size = wholeSize(view.getBigUint64(offset + 24, true));
          const bufferBinding = { buffer: resource, offset: Number(view.getBigUint64(offset + 16, true)) };
          if (size !== undefined) bufferBinding.size = size;
          entries.push({ binding, resource: bufferBinding });
        } else if (kind === 2 || kind === 3) {
          entries.push({ binding, resource });
        } else {
          throw new Error(`Unsupported bind-group resource kind ${kind}.`);
        }
      }
      state.resources.set(handle, state.device.createBindGroup({ layout, entries }));
      break;
    }
    case 18: {
      const handle = view.getUint32(payload, true);
      if (view.getUint32(payload + 4, true) === 0x4c4c5546) {
        const cursor = new PacketCursor(view, payload + 8, payloadLength - 8);
        const layoutHandle = cursor.u32();
        const vertex = readVertexState(cursor);
        const topology = cursor.u32();
        const stripIndexFormat = cursor.u32();
        const frontFace = cursor.u32();
        const cullMode = cursor.u32();
        const descriptor = {
          layout: layoutHandle === 0 ? 'auto' : requireResource(layoutHandle),
          vertex,
          primitive: {
            topology: enumValue(primitiveTopologies, topology, 'primitive topology'),
            frontFace: enumValue(frontFaces, frontFace, 'front face'),
            cullMode: enumValue(cullModes, cullMode, 'cull mode')
          }
        };
        if (stripIndexFormat !== 0) descriptor.primitive.stripIndexFormat = enumValue(indexFormats, stripIndexFormat, 'strip index format');
        if (cursor.u32() !== 0) descriptor.depthStencil = readDepthStencil(cursor);
        descriptor.multisample = { count: cursor.u32(), mask: cursor.u32(), alphaToCoverageEnabled: cursor.u32() !== 0 };
        if (cursor.u32() !== 0) descriptor.fragment = readFragmentState(cursor);
        cursor.done();
        state.resources.set(handle, state.device.createRenderPipeline(descriptor));
        break;
      }
      const shader = requireResource(view.getUint32(payload + 4, true));
      const sampleCount = view.getUint32(payload + 8, true);
      const byteCount = view.getUint32(payload + 12, true);
      if (16 + byteCount > payloadLength) throw new Error('Truncated render pipeline format.');
      const bytes = new Uint8Array(view.buffer, view.byteOffset + payload + 16, byteCount);
      const format = state.decoder.decode(bytes);
      state.resources.set(handle, state.device.createRenderPipeline({
        label: 'ProGPU browser smoke pipeline',
        layout: 'auto',
        vertex: { module: shader, entryPoint: 'vsMain' },
        fragment: { module: shader, entryPoint: 'fsMain', targets: [{ format }] },
        primitive: { topology: 'triangle-list' },
        multisample: { count: sampleCount }
      }));
      break;
    }
    case 19: {
      const cursor = new PacketCursor(view, payload, payloadLength);
      const handle = cursor.u32();
      const layoutHandle = cursor.u32();
      const descriptor = { layout: layoutHandle === 0 ? 'auto' : requireResource(layoutHandle), compute: readStage(cursor) };
      cursor.done();
      state.resources.set(handle, state.device.createComputePipeline(descriptor));
      break;
    }
    case 20:
      state.resources.delete(view.getUint32(payload, true));
      state.resourceMetadata.delete(view.getUint32(payload, true));
      break;
    case 21:
    case 22:
      requireResource(view.getUint32(payload, true)).destroy();
      break;
    case 23: {
      const handle = view.getUint32(payload, true);
      const pipeline = requireResource(view.getUint32(payload + 4, true));
      state.resources.set(handle, pipeline.getBindGroupLayout(view.getUint32(payload + 8, true)));
      break;
    }
    case 24: {
      const handle = view.getUint32(payload, true);
      state.resources.set(handle, state.device.createCommandEncoder());
      break;
    }
    case 25: {
      const handle = view.getUint32(payload, true);
      const encoder = requireResource(view.getUint32(payload + 4, true));
      state.resources.set(handle, encoder.finish());
      break;
    }
    case 26: {
      const handle = view.getUint32(payload, true);
      state.resources.set(handle, state.context.getCurrentTexture());
      state.resourceMetadata.set(handle, { kind: 'texture', dimension: '2d' });
      break;
    }
    case 30: {
      const passHandle = view.getUint32(payload, true);
      const encoder = requireResource(view.getUint32(payload + 4, true));
      const colorCount = view.getUint32(payload + 8, true);
      const hasDepthStencil = view.getUint32(payload + 12, true) !== 0;
      const requiredLength = 16 + colorCount * 56 + (hasDepthStencil ? 36 : 0);
      if (requiredLength > payloadLength) throw new Error('Truncated render-pass attachments.');
      const colorAttachments = [];
      for (let index = 0; index < colorCount; index++) {
        const offset = payload + 16 + index * 56;
        const resolveHandle = view.getUint32(offset + 4, true);
        const depthSlice = view.getUint32(offset + 8, true);
        const attachmentHandle = view.getUint32(offset, true);
        const attachment = {
          view: requireResource(attachmentHandle),
          loadOp: enumValue(loadOps, view.getUint32(offset + 12, true), 'load op'),
          storeOp: enumValue(storeOps, view.getUint32(offset + 16, true), 'store op'),
          clearValue: {
            r: view.getFloat64(offset + 24, true), g: view.getFloat64(offset + 32, true),
            b: view.getFloat64(offset + 40, true), a: view.getFloat64(offset + 48, true)
          }
        };
        if (resolveHandle !== 0) attachment.resolveTarget = requireResource(resolveHandle);
        if (depthSlice !== 0xffffffff && state.resourceMetadata.get(attachmentHandle)?.dimension === '3d') attachment.depthSlice = depthSlice;
        colorAttachments.push(attachment);
      }
      const descriptor = { colorAttachments };
      if (hasDepthStencil) {
        const offset = payload + 16 + colorCount * 56;
        const depthLoad = view.getUint32(offset + 4, true);
        const depthStore = view.getUint32(offset + 8, true);
        const stencilLoad = view.getUint32(offset + 20, true);
        const stencilStore = view.getUint32(offset + 24, true);
        const depthStencilAttachment = { view: requireResource(view.getUint32(offset, true)) };
        if (depthLoad !== 0) {
          depthStencilAttachment.depthLoadOp = enumValue(loadOps, depthLoad, 'depth load op');
          depthStencilAttachment.depthClearValue = view.getFloat32(offset + 12, true);
          depthStencilAttachment.depthReadOnly = view.getUint32(offset + 16, true) !== 0;
        }
        if (depthStore !== 0) depthStencilAttachment.depthStoreOp = enumValue(storeOps, depthStore, 'depth store op');
        if (stencilLoad !== 0) {
          depthStencilAttachment.stencilLoadOp = enumValue(loadOps, stencilLoad, 'stencil load op');
          depthStencilAttachment.stencilClearValue = view.getUint32(offset + 28, true);
          depthStencilAttachment.stencilReadOnly = view.getUint32(offset + 32, true) !== 0;
        }
        if (stencilStore !== 0) depthStencilAttachment.stencilStoreOp = enumValue(storeOps, stencilStore, 'stencil store op');
        descriptor.depthStencilAttachment = depthStencilAttachment;
      }
      state.resources.set(passHandle, encoder.beginRenderPass(descriptor));
      break;
    }
    case 31: {
      if (payloadLength === 0) {
        state.renderPass?.end(); state.renderPass = null;
      } else {
        requireResource(view.getUint32(payload, true)).end();
      }
      break;
    }
    case 32:
      if (payloadLength === 4) state.renderPass.setPipeline(requireResource(view.getUint32(payload, true)));
      else requireResource(view.getUint32(payload, true)).setPipeline(requireResource(view.getUint32(payload + 4, true)));
      break;
    case 33: {
      const pass = requireResource(view.getUint32(payload, true));
      const index = view.getUint32(payload + 4, true);
      const group = requireResource(view.getUint32(payload + 8, true));
      const count = view.getUint32(payload + 12, true);
      if (16 + count * 4 > payloadLength) throw new Error('Truncated dynamic bind-group offsets.');
      if (count === 0) pass.setBindGroup(index, group);
      else {
        const offsets = new Uint32Array(count);
        for (let offsetIndex = 0; offsetIndex < count; offsetIndex++) offsets[offsetIndex] = view.getUint32(payload + 16 + offsetIndex * 4, true);
        pass.setBindGroup(index, group, offsets);
      }
      break;
    }
    case 34: {
      const size = wholeSize(view.getBigUint64(payload + 24, true));
      const pass = requireResource(view.getUint32(payload, true));
      const args = [view.getUint32(payload + 4, true), requireResource(view.getUint32(payload + 8, true)), Number(view.getBigUint64(payload + 16, true))];
      if (size !== undefined) args.push(size);
      pass.setVertexBuffer(...args);
      break;
    }
    case 35: {
      const size = wholeSize(view.getBigUint64(payload + 24, true));
      const pass = requireResource(view.getUint32(payload, true));
      const args = [requireResource(view.getUint32(payload + 4, true)), enumValue(indexFormats, view.getUint32(payload + 8, true), 'index format'), Number(view.getBigUint64(payload + 16, true))];
      if (size !== undefined) args.push(size);
      pass.setIndexBuffer(...args);
      break;
    }
    case 36:
      requireResource(view.getUint32(payload, true)).setViewport(
        view.getFloat32(payload + 4, true), view.getFloat32(payload + 8, true),
        view.getFloat32(payload + 12, true), view.getFloat32(payload + 16, true),
        view.getFloat32(payload + 20, true), view.getFloat32(payload + 24, true));
      break;
    case 37:
      requireResource(view.getUint32(payload, true)).setScissorRect(
        view.getUint32(payload + 4, true), view.getUint32(payload + 8, true),
        view.getUint32(payload + 12, true), view.getUint32(payload + 16, true));
      break;
    case 39:
      requireResource(view.getUint32(payload, true)).setStencilReference(view.getUint32(payload + 4, true));
      break;
    case 40:
      if (payloadLength === 16) state.renderPass.draw(view.getUint32(payload, true), view.getUint32(payload + 4, true), view.getUint32(payload + 8, true), view.getUint32(payload + 12, true));
      else requireResource(view.getUint32(payload, true)).draw(view.getUint32(payload + 4, true), view.getUint32(payload + 8, true), view.getUint32(payload + 12, true), view.getUint32(payload + 16, true));
      break;
    case 41:
      requireResource(view.getUint32(payload, true)).drawIndexed(
        view.getUint32(payload + 4, true), view.getUint32(payload + 8, true),
        view.getUint32(payload + 12, true), view.getInt32(payload + 16, true), view.getUint32(payload + 20, true));
      break;
    case 50: {
      const handle = view.getUint32(payload, true);
      const encoder = requireResource(view.getUint32(payload + 4, true));
      state.resources.set(handle, encoder.beginComputePass());
      break;
    }
    case 51:
      if (payloadLength === 0) { state.computePass?.end(); state.computePass = null; }
      else requireResource(view.getUint32(payload, true)).end();
      break;
    case 52:
      requireResource(view.getUint32(payload, true)).setPipeline(requireResource(view.getUint32(payload + 4, true)));
      break;
    case 53:
      if (payloadLength === 12) state.computePass.dispatchWorkgroups(view.getUint32(payload, true), view.getUint32(payload + 4, true), view.getUint32(payload + 8, true));
      else requireResource(view.getUint32(payload, true)).dispatchWorkgroups(view.getUint32(payload + 4, true), view.getUint32(payload + 8, true), view.getUint32(payload + 12, true));
      break;
    case 60:
      requireResource(view.getUint32(payload, true)).copyBufferToBuffer(
        requireResource(view.getUint32(payload + 4, true)), Number(view.getBigUint64(payload + 8, true)),
        requireResource(view.getUint32(payload + 16, true)), Number(view.getBigUint64(payload + 24, true)),
        Number(view.getBigUint64(payload + 32, true)));
      break;
    case 62:
      requireResource(view.getUint32(payload, true)).copyTextureToBuffer(imageCopyTexture(view, payload + 4), imageCopyBuffer(view, payload + 28), extent3d(view, payload + 52));
      break;
    case 63:
      requireResource(view.getUint32(payload, true)).copyTextureToTexture(imageCopyTexture(view, payload + 4), imageCopyTexture(view, payload + 28), extent3d(view, payload + 52));
      break;
    case 70: {
      if (payloadLength === 0) {
        state.device.queue.submit([state.encoder.finish()]); state.encoder = null;
      } else {
        const count = view.getUint32(payload, true);
        if (4 + count * 4 > payloadLength) throw new Error('Truncated command-buffer submission.');
        const commandBuffers = [];
        for (let index = 0; index < count; index++) commandBuffers.push(requireResource(view.getUint32(payload + 4 + index * 4, true)));
        state.device.queue.submit(commandBuffers);
      }
      break;
    }
    case 71: {
      const length = view.getUint32(payload + 16, true);
      if (24 + length > payloadLength) throw new Error('Truncated queue buffer upload.');
      const bytes = new Uint8Array(view.buffer, view.byteOffset + payload + 24, length);
      state.device.queue.writeBuffer(requireResource(view.getUint32(payload, true)), Number(view.getBigUint64(payload + 8, true)), bytes);
      break;
    }
    case 72: {
      const length = view.getUint32(payload + 52, true);
      if (56 + length > payloadLength) throw new Error('Truncated queue texture upload.');
      const bytes = new Uint8Array(view.buffer, view.byteOffset + payload + 56, length);
      state.device.queue.writeTexture(
        imageCopyTexture(view, payload), bytes,
        { offset: Number(view.getBigUint64(payload + 24, true)), bytesPerRow: view.getUint32(payload + 32, true), rowsPerImage: view.getUint32(payload + 36, true) },
        extent3d(view, payload + 40));
      break;
    }
    case 73:
      requireResource(view.getUint32(payload, true)).unmap();
      state.mappedBuffers.delete(view.getUint32(payload, true));
      break;
    default:
      throw new Error(`Unsupported ProGPU browser opcode ${opcode}.`);
  }
}

function dispatchUpload(address, length) {
  const heap = runtime.localHeapViewU8();
  if (address < 0 || length < 0 || address + length > heap.byteLength) throw new RangeError('Upload is outside WASM memory.');
  if (state.worker) {
    const upload = heap.slice(address, address + length).buffer;
    state.worker.postMessage({ type: 'upload', upload }, [upload]);
    return;
  }
  state.uploads = heap.subarray(address, address + length);
}

async function mapBuffer(handle, mode, offset, size) {
  if (state.worker) {
    const response = await workerRequest('map-buffer', { handle, mode, offset, size });
    state.workerMappedBuffers.set(handle, { offset, size, bytes: response.bytes });
    return true;
  }
  const buffer = requireResource(handle);
  await buffer.mapAsync(mode, offset, size);
  state.mappedBuffers.set(handle, { offset, size });
  return true;
}

function copyMappedBuffer(handle, destination, size) {
  if (state.worker) {
    const mapped = state.workerMappedBuffers.get(handle);
    if (!mapped || size > mapped.size) throw new Error(`Buffer 0x${handle.toString(16)} is not mapped for ${size} bytes.`);
    runtime.localHeapViewU8().set(new Uint8Array(mapped.bytes, 0, size), destination);
    return;
  }
  const mapped = state.mappedBuffers.get(handle);
  if (!mapped || size > mapped.size) throw new Error(`Buffer 0x${handle.toString(16)} is not mapped for ${size} bytes.`);
  const source = new Uint8Array(requireResource(handle).getMappedRange(mapped.offset, size));
  runtime.localHeapViewU8().set(source, destination);
}

function writeMappedBuffer(handle, source, size) {
  if (state.worker) {
    const mapped = state.workerMappedBuffers.get(handle);
    if (!mapped || size > mapped.size) throw new Error(`Buffer 0x${handle.toString(16)} is not mapped for ${size} bytes.`);
    const heap = runtime.localHeapViewU8();
    if (source < 0 || source + size > heap.byteLength) throw new RangeError('Mapped-buffer source is outside WASM memory.');
    const bytes = heap.slice(source, source + size).buffer;
    state.worker.postMessage({ type: 'mapped-write', handle, bytes }, [bytes]);
    state.workerMappedBuffers.delete(handle);
    return;
  }
  const mapped = state.mappedBuffers.get(handle);
  if (!mapped || size > mapped.size) throw new Error(`Buffer 0x${handle.toString(16)} is not mapped for ${size} bytes.`);
  const heap = runtime.localHeapViewU8();
  if (source < 0 || source + size > heap.byteLength) throw new RangeError('Mapped-buffer source is outside WASM memory.');
  new Uint8Array(requireResource(handle).getMappedRange(mapped.offset, size)).set(heap.subarray(source, source + size));
}

function releaseMappedBuffer(handle) {
  state.workerMappedBuffers.delete(handle);
}

function nextAnimationFrame(vsync) {
  if (vsync) return new Promise(resolve => requestAnimationFrame(resolve));

  // A MessageChannel task yields to the browser event loop without coupling the
  // renderer to the display refresh rate. Unlike a microtask loop it leaves input,
  // canvas presentation, and other browser task sources eligible between frames;
  // unlike nested zero-delay timers it is not forced onto the HTML 4 ms timer floor.
  return new Promise(resolve => {
    uncappedFrameResolvers.push(resolve);
    uncappedFrameChannel.port2.postMessage(0);
  });
}

function writeCanvasMetrics(destination) {
  resizeCanvas();
  const heap = runtime.localHeapViewU8();
  if (destination < 0 || destination + 16 > heap.byteLength) throw new RangeError('Canvas metrics destination is outside WASM memory.');
  const view = new DataView(heap.buffer, heap.byteOffset + destination, 16);
  view.setUint32(0, state.framebufferWidth, true);
  view.setUint32(4, state.framebufferHeight, true);
  view.setFloat64(8, globalThis.devicePixelRatio || 1, true);
}

function applyDiagnosticsVisibility(visible, persist) {
  state.diagnosticsVisible = !!visible;
  document.querySelector('#diagnostics').hidden = !state.diagnosticsVisible;
  if (persist) {
    try {
      globalThis.localStorage?.setItem(DIAGNOSTICS_VISIBILITY_KEY, state.diagnosticsVisible ? 'true' : 'false');
    } catch {
      // Storage can be unavailable in private or restricted browser contexts.
    }
  }
}

function initializeDiagnosticsVisibility() {
  let visible = false;
  try {
    visible = globalThis.localStorage?.getItem(DIAGNOSTICS_VISIBILITY_KEY) === 'true';
  } catch {
    // Hidden remains the safe default when storage access is unavailable.
  }
  applyDiagnosticsVisibility(visible, false);
}

function getDiagnosticsVisible() {
  return state.diagnosticsVisible;
}

function setDiagnosticsVisible(visible) {
  applyDiagnosticsVisibility(visible, true);
}

function setStatus(title, detail, isError) {
  document.querySelector('#status-title').textContent = title;
  document.querySelector('#status-detail').textContent = detail;
  document.querySelector('#diagnostics').classList.toggle('error', !!isError);
  if (isError) applyDiagnosticsVisibility(true, false);
}

function updateCounters(frames, dispatches, commandBytes) {
  document.querySelector('#counter-frames').textContent = String(frames);
  document.querySelector('#counter-dispatches').textContent = String(dispatches);
  document.querySelector('#counter-bytes').textContent = Number(commandBytes).toLocaleString();
}

function readBenchmarkEnvironment() {
  const query = new URLSearchParams(globalThis.location.search);
  const environment = {};
  for (const [queryName, variableName] of Object.entries(BENCHMARK_QUERY_VARIABLES)) {
    const value = query.get(queryName)?.trim();
    if (value) environment[variableName] = value;
  }
  return environment;
}

async function handleDispatcherWorkerMessage(event) {
  const message = event.data;
  try {
    switch (message.type) {
      case 'initialize': {
        state.canvas = message.canvas;
        state.canvas.width = message.width;
        state.canvas.height = message.height;
        const diagnostics = [...message.diagnostics];
        if (message.request.syncReadbackMode === 'IsolatedWorkerOnly' && message.executionMode !== 'IsolatedWorker') {
          diagnostics.push('Synchronous readback is disabled because cross-origin isolation is unavailable.');
        }
        const capabilities = await initializeGpu(message.request, state.canvas, message.executionMode, diagnostics);
        globalThis.postMessage({ type: 'response', id: message.id, capabilities });
        break;
      }
      case 'resize':
        if (state.canvas.width !== message.width || state.canvas.height !== message.height) {
          await state.device.queue.onSubmittedWorkDone();
          state.canvas.width = message.width;
          state.canvas.height = message.height;
        }
        break;
      case 'dispatch-batch': {
        for (const packetBuffer of message.packets) {
          const packet = new Uint8Array(packetBuffer);
          dispatchPacket(packet, 0, packet.byteLength);
        }
        break;
      }
      case 'uncapped-frame-fence':
        await state.device.queue.onSubmittedWorkDone();
        globalThis.postMessage({ type: 'uncapped-frame-ready', id: message.id });
        break;
      case 'upload':
        state.uploads = new Uint8Array(message.upload);
        break;
      case 'map-buffer': {
        await mapBuffer(message.handle, message.mode, message.offset, message.size);
        const mapped = state.mappedBuffers.get(message.handle);
        const source = new Uint8Array(requireResource(message.handle).getMappedRange(mapped.offset, mapped.size));
        const bytes = source.slice().buffer;
        globalThis.postMessage({ type: 'response', id: message.id, bytes }, [bytes]);
        break;
      }
      case 'mapped-write': {
        const mapped = state.mappedBuffers.get(message.handle);
        if (!mapped) throw new Error(`Buffer 0x${message.handle.toString(16)} is not mapped.`);
        new Uint8Array(requireResource(message.handle).getMappedRange(mapped.offset, mapped.size)).set(new Uint8Array(message.bytes));
        break;
      }
    }
  } catch (error) {
    if (message.id) globalThis.postMessage({ type: 'response', id: message.id, error: String(error?.stack || error) });
    else reportGpuStatus('WebGPU worker dispatch failed', String(error?.stack || error), true);
  }
}

if (isDispatcherWorker) {
  let messageChain = Promise.resolve();
  globalThis.addEventListener('message', event => {
    messageChain = messageChain.then(() => handleDispatcherWorkerMessage(event));
  });
} else {
  initializeDiagnosticsVisibility();
  runtime = await dotnet.withEnvironmentVariables(readBenchmarkEnvironment()).create();
  runtime.setModuleImports('progpu-browser', { initialize, dispatch, dispatchUpload, mapBuffer, copyMappedBuffer, writeMappedBuffer, releaseMappedBuffer, nextAnimationFrame, writeCanvasMetrics, drainInputEvents, setCanvasCursor, configureTextInput, hideTextInput, setClipboardText, getClipboardText, pickStorage, getPickedStorageLength, copyPickedStorage, clearPickedStorage, downloadText, getDiagnosticsVisible, setDiagnosticsVisible, setStatus, updateCounters });
  const browserExports = await runtime.getAssemblyExports('ProGPU.Browser.dll');
  state.dispatchImmediatePointer = browserExports.ProGPU.Browser.BrowserInputDispatcher.DispatchImmediatePointer;
  state.dispatchTextInput = browserExports.ProGPU.Browser.BrowserInputDispatcher.DispatchTextInput;
  await runtime.runMain();
}
