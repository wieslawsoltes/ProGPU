import { dotnet } from './_framework/dotnet.js';
import {
  measurePhysicalCanvas,
  requestProGpuWebGpuDevice,
  updateProGpuVisualViewport
} from './progpu-browser-host.js';

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
  pointerLockActive: false,
  pointerLockRequestPending: false,
  dispatchImmediatePointer: null,
  dispatchTextInput: null,
  textSink: null,
  textInputBounds: null,
  isComposing: false,
  clipboardText: '',
  clipboardRtf: '',
  clipboardHtml: '',
  pickedStorageBytes: null,
  nextStorageHandle: 1,
  openFileHandles: new Map(),
  saveFileHandles: new Map(),
  directoryHandles: new Map(),
  mediaElements: new Map(),
  pendingMediaFrames: new Map(),
  dispatchMediaEvent: null,
  dispatchMediaTimedMetadata: null,
  audioWorkletNodeCreationCount: 0,
  audioWorkletSignalMaximumError: Number.NaN,
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
  benchmarkScrollStep: 'PROGPU_SAMPLE_BENCHMARK_SCROLL_STEP',
  benchmarkPreconditionPages: 'PROGPU_SAMPLE_BENCHMARK_PRECONDITION_PAGES',
  benchmarkPreconditionFrames: 'PROGPU_SAMPLE_BENCHMARK_PRECONDITION_FRAMES'
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
  const metrics = measurePhysicalCanvas(state.canvas);
  const width = metrics.width;
  const height = metrics.height;
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
  updateProGpuVisualViewport();
  resizeCanvas();
  positionTextInput();
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
  if (kind === 10 && last?.kind === 10) {
    last.a += a; last.b += b;
    last.timestamp = timestamp;
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

function queueRelativePointerEvent(event) {
  queueInputEvent(10, 0, 0, event.movementX || 0, event.movementY || 0,
    event.pointerId, eventModifiers(event), event.buttons,
    event.timeStamp, 0, 0, 0, 0, -1);
}

function notifyPointerLockLost() {
  if (!state.pointerLockActive && !state.pointerLockRequestPending) return;
  state.pointerLockActive = false;
  state.pointerLockRequestPending = false;
  queueInputEvent(11);
}

function requestCanvasPointerLock() {
  if (!state.canvas || typeof state.canvas.requestPointerLock !== 'function') return false;
  if (document.pointerLockElement === state.canvas) {
    state.pointerLockActive = true;
    state.pointerLockRequestPending = false;
    return true;
  }

  try {
    state.pointerLockRequestPending = true;
    // The baseline request is supported more broadly than unadjustedMovement and
    // still supplies unbounded relative movementX/Y required by first-person look.
    const request = state.canvas.requestPointerLock();
    if (request && typeof request.catch === 'function') {
      request.catch(() => notifyPointerLockLost());
    }
    return true;
  } catch {
    state.pointerLockRequestPending = false;
    return false;
  }
}

function exitCanvasPointerLock() {
  state.pointerLockRequestPending = false;
  if (document.pointerLockElement === state.canvas && typeof document.exitPointerLock === 'function') {
    document.exitPointerLock();
  }
  state.pointerLockActive = false;
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

function flushQueuedPointerMoves(pointerId) {
  let flushed = true;
  const retained = [];
  for (let index = 0; index < state.inputEvents.length; index++) {
    const queued = state.inputEvents[index];
    if (queued.kind !== 1 || queued.data !== pointerId) {
      retained.push(queued);
      continue;
    }

    try {
      if (state.dispatchImmediatePointer(queued.kind, queued.x, queued.y, queued.button,
        queued.flags & 0xffff, queued.data, queued.pointerType, queued.pressure,
        queued.width, queued.height, (queued.flags & 0x10000) !== 0,
        queued.timestamp, queued.modifiers)) continue;
    } catch (error) {
      globalThis.console.error('[ProGPU] Immediate queued pointer move dispatch failed.', error);
    }

    retained.push(queued);
    flushed = false;
  }
  state.inputEvents = retained;
  return flushed;
}

function dispatchTerminalPointerEvent(kind, event, point) {
  if (state.dispatchImmediatePointer && !flushQueuedPointerMoves(event.pointerId)) {
    queuePointerEvent(kind, event, point);
    return;
  }
  dispatchPointerEvent(kind, event, point);
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
    if (document.pointerLockElement === state.canvas) {
      const coalesced = typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [];
      if (coalesced.length > 1) {
        for (const sample of coalesced) queueRelativePointerEvent(sample);
      } else {
        queueRelativePointerEvent(event);
      }
      return;
    }
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
    if (!state.pointerLockActive && !state.pointerLockRequestPending) {
      try { state.canvas.setPointerCapture(event.pointerId); } catch { }
    }
    event.preventDefault();
  });
  state.canvas.addEventListener('pointerup', event => {
    const point = pointerPosition(event);
    dispatchTerminalPointerEvent(3, event, point);
    try { state.canvas.releasePointerCapture(event.pointerId); } catch { }
    event.preventDefault();
  });
  state.canvas.addEventListener('pointercancel', event => {
    const point = pointerPosition(event);
    dispatchTerminalPointerEvent(9, event, point);
    try { state.canvas.releasePointerCapture(event.pointerId); } catch { }
    event.preventDefault();
  });
  state.canvas.addEventListener('contextmenu', event => event.preventDefault());
  state.canvas.addEventListener('wheel', event => {
    const point = pointerPosition(event);
    const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 :
      event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? state.canvas.clientHeight : 1;
    queueInputEvent(4, point.x, point.y, -event.deltaX * unit, -event.deltaY * unit,
      1, eventModifiers(event), 0, event.timeStamp, 0, 0, 0, 0, -1);
    event.preventDefault();
  }, { passive: false });

  globalThis.addEventListener('keydown', event => {
    const textSinkOwnsPaste = document.activeElement === textSink && event.code === 'KeyV' && (event.ctrlKey || event.metaKey);
    if (document.activeElement === textSink &&
      (event.key === 'Backspace' || event.key === 'Delete' || event.key === 'Enter' || textSinkOwnsPaste)) return;
    const key = browserKeyCodes.get(event.code) || 0;
    if (key !== 0) queueInputEvent(5, 0, 0, 0, 0, key, eventModifiers(event), event.repeat ? 1 : 0);
    if (event.code === 'F5' || event.code === 'F8' || event.code === 'F10' ||
      (event.code === 'KeyE' && event.ctrlKey) ||
      ['Tab', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', 'Space'].includes(event.code)) {
      event.preventDefault();
    }
  }, true);
  globalThis.addEventListener('keyup', event => {
    const textSinkOwnsPaste = document.activeElement === textSink && event.code === 'KeyV' && (event.ctrlKey || event.metaKey);
    if (document.activeElement === textSink &&
      (event.key === 'Backspace' || event.key === 'Delete' || event.key === 'Enter' || textSinkOwnsPaste)) return;
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
    state.clipboardRtf = event.clipboardData?.getData('text/rtf') ||
      event.clipboardData?.getData('application/rtf') || '';
    state.clipboardHtml = event.clipboardData?.getData('text/html') || '';
    if ((state.clipboardText || state.clipboardRtf || state.clipboardHtml) && state.dispatchTextInput) {
      state.dispatchTextInput(8, '', false);
    }
    event.preventDefault();
  });
  globalThis.addEventListener('blur', () => queueInputEvent(8));
  document.addEventListener('pointerlockchange', () => {
    if (document.pointerLockElement === state.canvas) {
      state.pointerLockActive = true;
      state.pointerLockRequestPending = false;
    } else {
      notifyPointerLockLost();
    }
  });
  document.addEventListener('pointerlockerror', () => notifyPointerLockLost());
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

function positionTextInput() {
  const textSink = state.textSink;
  const bounds = state.textInputBounds;
  if (!textSink || !bounds) return;
  const viewport = globalThis.visualViewport;
  const viewportLeft = viewport?.offsetLeft || 0;
  const viewportTop = viewport?.offsetTop || 0;
  const viewportWidth = viewport?.width || globalThis.innerWidth;
  const viewportHeight = viewport?.height || globalThis.innerHeight;
  const canvasBounds = state.canvas?.getBoundingClientRect();
  const sinkX = (canvasBounds?.left || 0) + bounds.x;
  const sinkY = (canvasBounds?.top || 0) + bounds.y + bounds.height;
  textSink.style.left = `${Math.max(viewportLeft, Math.min(viewportLeft + viewportWidth - 2, sinkX))}px`;
  textSink.style.top = `${Math.max(viewportTop, Math.min(viewportTop + viewportHeight - 2, sinkY))}px`;
  textSink.style.width = `${Math.max(2, Math.min(bounds.width || 2, viewportWidth))}px`;
  textSink.style.height = '2px';
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
  state.textInputBounds = { x, y, width, height };
  positionTextInput();
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
  state.textInputBounds = null;
  sink.value = '';
  sink.blur();
  if (navigator.virtualKeyboard) {
    try { navigator.virtualKeyboard.hide(); } catch { }
  }
}

function setClipboardText(text) {
  state.clipboardText = String(text ?? '');
  state.clipboardRtf = '';
  state.clipboardHtml = '';
  if (navigator.clipboard?.writeText) navigator.clipboard.writeText(state.clipboardText).catch(() => { });
}

function getClipboardText() {
  return state.clipboardText;
}

function setClipboardRichText(plainText, rtf, html) {
  state.clipboardText = String(plainText ?? '');
  state.clipboardRtf = String(rtf ?? '');
  state.clipboardHtml = String(html ?? '');
  if (!navigator.clipboard?.write || typeof ClipboardItem === 'undefined') {
    if (navigator.clipboard?.writeText) navigator.clipboard.writeText(state.clipboardText).catch(() => { });
    return;
  }
  const formats = {
    'text/plain': new Blob([state.clipboardText], { type: 'text/plain' }),
    'text/html': new Blob([state.clipboardHtml], { type: 'text/html' })
  };
  if (typeof ClipboardItem.supports === 'function' && ClipboardItem.supports('text/rtf')) {
    formats['text/rtf'] = new Blob([state.clipboardRtf], { type: 'text/rtf' });
  }
  navigator.clipboard.write([new ClipboardItem(formats)])
    .catch(() => navigator.clipboard?.writeText?.(state.clipboardText).catch(() => { }));
}

function getClipboardRtf() {
  return state.clipboardRtf;
}

function getClipboardHtml() {
  return state.clipboardHtml;
}

async function stagePickedFile(file) {
  const token = rememberStorageHandle(
    state.openFileHandles,
    'open',
    file);
  try {
    state.pickedStorageBytes =
      new Uint8Array(await file.arrayBuffer());
    return encodeStorageSelection(token, file.name);
  } catch (error) {
    state.openFileHandles.delete(token);
    throw error;
  }
}

async function stageBrowserMediaSource(stagingId, uri, maximumBytes) {
  const controller = new AbortController();
  const entry = { controller, bytes: null };
  state.stagedMediaSources ??= new Map();
  state.stagedMediaSources.set(stagingId, entry);
  try {
    const response = await fetch(uri, {
      credentials: 'same-origin',
      mode: 'cors',
      signal: controller.signal
    });
    if (!response.ok) {
      throw new Error(`Media source request failed with HTTP ${response.status}.`);
    }
    const declaredLength = Number(response.headers.get('content-length'));
    if (Number.isFinite(declaredLength) &&
        declaredLength > maximumBytes) {
      throw new RangeError('The browser media source exceeds the bounded staging limit.');
    }
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength > maximumBytes) {
      throw new RangeError('The browser media source exceeds the bounded staging limit.');
    }
    entry.bytes = bytes;
    return bytes.byteLength;
  } catch (error) {
    if (state.stagedMediaSources.get(stagingId) === entry) {
      state.stagedMediaSources.delete(stagingId);
    }
    throw error;
  }
}

function startStageBrowserMediaSource(
  stagingId,
  uri,
  maximumBytes) {
  void stageBrowserMediaSource(
    stagingId,
    uri,
    maximumBytes).then(
      length =>
        state.dispatchMediaStageCompletion(
          stagingId,
          length),
      error => {
        if (error?.name !== 'AbortError') {
          console.error(
            '[ProGPU] Browser media staging failed.',
            error);
        }
        state.dispatchMediaStageCompletion(
          stagingId,
          -1);
      });
}

function copyStagedBrowserMediaSource(stagingId, destination, length) {
  const bytes = state.stagedMediaSources?.get(stagingId)?.bytes;
  if (!bytes || bytes.byteLength !== length) return -1;
  const heap = runtime.localHeapViewU8();
  if (destination < 0 ||
      destination + length > heap.byteLength) {
    throw new RangeError('Media staging destination is outside WASM memory.');
  }
  heap.set(bytes, destination);
  return length;
}

function clearStagedBrowserMediaSource(stagingId) {
  const entry = state.stagedMediaSources?.get(stagingId);
  entry?.controller.abort();
  state.stagedMediaSources?.delete(stagingId);
}

function cancelStagedBrowserMediaSource(stagingId) {
  clearStagedBrowserMediaSource(stagingId);
}

function browserMp4RecorderMimeType(includeAudio) {
  if (typeof MediaRecorder !== 'function') return '';
  const candidates = includeAudio
    ? ['video/mp4;codecs="avc1.42E01E,mp4a.40.2"']
    : [
        'video/mp4;codecs="avc1.42E01E"',
        'video/mp4'
      ];
  return candidates.find(
    value => MediaRecorder.isTypeSupported(value)) || '';
}

function waitForMediaEvent(element, eventName) {
  return new Promise((resolve, reject) => {
    const loaded = () => {
      cleanup();
      resolve();
    };
    const failed = () => {
      cleanup();
      reject(new Error(
        element.error?.message ||
        `The browser failed while waiting for ${eventName}.`));
    };
    const cleanup = () => {
      element.removeEventListener(eventName, loaded);
      element.removeEventListener('error', failed);
    };
    element.addEventListener(eventName, loaded, { once: true });
    element.addEventListener('error', failed, { once: true });
  });
}

function resolveBrowserStorageMediaSource(uri) {
  const parsed = new URL(uri, globalThis.location.href);
  if (parsed.protocol !== 'progpu-browser-storage:') {
    return { uri, objectUrl: null };
  }

  const token = decodeURIComponent(parsed.hostname);
  const file = state.openFileHandles.get(token);
  if (!file) {
    throw new Error(
      `Browser storage media handle '${token}' is no longer available.`);
  }
  const objectUrl = URL.createObjectURL(file);
  return { uri: objectUrl, objectUrl };
}

function releaseBrowserStorageMediaSource(element) {
  if (!element?.progpuBrowserStorageObjectUrl) return;
  URL.revokeObjectURL(
    element.progpuBrowserStorageObjectUrl);
  element.progpuBrowserStorageObjectUrl = null;
}

async function createCompositionMediaElement(uri, audioOnly = false) {
  const element = document.createElement(audioOnly ? 'audio' : 'video');
  element.preload = 'auto';
  element.crossOrigin = 'anonymous';
  if (!audioOnly) element.playsInline = true;
  element.style.display = 'none';
  document.body.appendChild(element);
  try {
    const source = resolveBrowserStorageMediaSource(uri);
    element.progpuBrowserStorageObjectUrl =
      source.objectUrl;
    const metadata = waitForMediaEvent(
      element,
      'loadedmetadata');
    element.src = source.uri;
    element.load();
    await metadata;
    return element;
  } catch (error) {
    element.removeAttribute('src');
    releaseBrowserStorageMediaSource(element);
    element.remove();
    throw error;
  }
}

async function seekCompositionElement(
  element,
  seconds,
  approximateForSpeed = false) {
  const duration = Number.isFinite(element.duration)
    ? element.duration
    : Number.MAX_SAFE_INTEGER;
  const target = Math.max(0, Math.min(seconds, Math.max(0, duration - 0.001)));
  if (new URLSearchParams(
        globalThis.location.search)
      .get('progpuSavePicker') === 'memory') {
    document.documentElement.dataset.progpuMediaSeekState =
      `${element.readyState}:${element.currentTime}:${target}:${duration}`;
  }
  if (Math.abs(element.currentTime - target) <= 0.001) {
    if (element.readyState >=
        HTMLMediaElement.HAVE_CURRENT_DATA) {
      return;
    }
    const primingTarget = Math.min(
      Math.max(0, duration - 0.001),
      target + 0.001);
    if (Math.abs(
          primingTarget -
          element.currentTime) <= 0.0001) {
      await waitForMediaEvent(
        element,
        'loadeddata');
      return;
    }
    const primed =
      waitForMediaEvent(
        element,
        'seeked');
    element.currentTime = primingTarget;
    await primed;
    return;
  }
  if (approximateForSpeed &&
      typeof element.fastSeek === 'function') {
    const seeked =
      waitForMediaEvent(
        element,
        'seeked');
    element.fastSeek(target);
    await seeked;
    return;
  }
  const seeked = waitForMediaEvent(element, 'seeked');
  element.currentTime = target;
  await seeked;
}

function writeCompositionUniform(device, visual) {
  const destination = visual.destination;
  const red = visual.redTransform;
  const green = visual.greenTransform;
  const blue = visual.blueTransform;
  device.queue.writeBuffer(
    visual.uniform,
    0,
    new Float32Array([
      destination.x,
      destination.y,
      destination.width,
      destination.height,
      red[0],
      red[1],
      red[2],
      red[3],
      green[0],
      green[1],
      green[2],
      green[3],
      blue[0],
      blue[1],
      blue[2],
      blue[3],
      visual.opacity,
      0,
      0,
      0
    ]));
}

function createCompositionGaussianUniform(
  standardDeviation,
  directionX,
  directionY) {
  const sigma =
    Math.max(
      0.0001,
      Math.min(32, Number(standardDeviation)));
  const radius = Math.min(96, Math.ceil(3 * sigma));
  const weights = new Float64Array(radius + 1);
  weights[0] = 1;
  let total = 1;
  const denominator = 2 * sigma * sigma;
  for (let index = 1; index <= radius; index++) {
    const weight =
      Math.exp(-(index * index) / denominator);
    weights[index] = weight;
    total += 2 * weight;
  }
  // TextureGaussianBlur.wgsl owns a fixed 228-float layout:
  // axis + RGB rows + planar-YUV metadata + 48 packed tap pairs.
  // Browser composition uses the RGB lane, so the YUV fields remain zero.
  const values = new Float32Array(228);
  values[0] = directionX;
  values[1] = directionY;
  values[2] = 1 / total;
  const pairCount = Math.ceil(radius / 2);
  values[3] = pairCount;
  values[4] = 1;
  values[9] = 1;
  values[14] = 1;
  for (let pair = 0; pair < pairCount; pair++) {
    const first = pair * 2 + 1;
    const second = first + 1;
    const firstWeight = weights[first];
    const secondWeight =
      second <= radius ? weights[second] : 0;
    const combined = firstWeight + secondWeight;
    const offset =
      combined > 0
        ? (first * firstWeight +
           second * secondWeight) /
          combined
        : first;
    const uniformIndex = 36 + pair * 4;
    values[uniformIndex] = offset;
    values[uniformIndex + 1] = combined / total;
  }
  return values;
}

async function writeBrowserCompositionBlob(token, name, blob) {
  const handle = state.saveFileHandles.get(token);
  if (handle) {
    const writable = await handle.createWritable();
    try {
      await writable.write(blob);
      await writable.close();
    } catch (error) {
      try { await writable.abort(); } catch { }
      throw error;
    }
    return;
  }

  const pickerMode =
    new URLSearchParams(globalThis.location.search)
      .get('progpuSavePicker');
  if (pickerMode === 'memory') {
    const prefix = new Uint8Array(
      await blob.slice(0, 12).arrayBuffer());
    document.documentElement.dataset.progpuDownloadName =
      name || 'download.mp4';
    document.documentElement.dataset.progpuDownloadLength =
      String(blob.size);
    document.documentElement.dataset.progpuDownloadPrefix =
      [...prefix]
        .map(value => value.toString(16).padStart(2, '0'))
        .join('');
    void blob.arrayBuffer().then(buffer => {
      const encodedBytes = new Uint8Array(buffer);
      const containsAscii = text => {
        const pattern =
          [...text].map(
            character =>
              character.charCodeAt(0));
        for (let offset = 0;
             offset <=
               encodedBytes.length -
                 pattern.length;
             offset++) {
          let matches = true;
          for (let index = 0;
               index < pattern.length;
               index++) {
            if (encodedBytes[offset + index] !==
                pattern[index]) {
              matches = false;
              break;
            }
          }
          if (matches) return true;
        }
        return false;
      };
      document.documentElement.dataset.progpuDownloadHasVideo =
        String(
          containsAscii('vide') &&
          containsAscii('avc1'));
      document.documentElement.dataset.progpuDownloadHasAudio =
        String(
          containsAscii('soun') &&
          containsAscii('mp4a'));
    }).catch(error =>
      console.error(
        '[ProGPU] Browser MP4 track probe failed.',
        error));
    if (state.lastMediaExportUrl) {
      URL.revokeObjectURL(
        state.lastMediaExportUrl);
    }
    state.lastMediaExportProbe?.remove();
    const url = URL.createObjectURL(blob);
    state.lastMediaExportUrl = url;
    const probe = document.createElement('video');
    state.lastMediaExportProbe = probe;
    try {
      const metadata =
        waitForMediaEvent(
          probe,
          'loadedmetadata');
      probe.src = url;
      probe.load();
      await metadata;
      document.documentElement.dataset.progpuDownloadDuration =
        String(probe.duration);
      document.documentElement.dataset.progpuDownloadWidth =
        String(probe.videoWidth);
      document.documentElement.dataset.progpuDownloadHeight =
        String(probe.videoHeight);
      probe.muted = true;
      probe.playsInline = true;
      probe.style.position = 'fixed';
      probe.style.right = '8px';
      probe.style.bottom = '8px';
      probe.style.width = '320px';
      probe.style.height = '180px';
      probe.style.objectFit = 'contain';
      probe.style.background = 'black';
      probe.style.zIndex = '100000';
      probe.style.border = '2px solid #18a0fb';
      document.body.appendChild(probe);
      await seekCompositionElement(
        probe,
        Math.min(
          2,
          Math.max(0, probe.duration / 2)));
      document.documentElement.dataset.progpuDownloadPreview =
        String(probe.currentTime);
    } catch (error) {
      probe.remove();
      state.lastMediaExportProbe = null;
      URL.revokeObjectURL(url);
      state.lastMediaExportUrl = null;
      throw error;
    }
    return;
  }

  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name || 'download.mp4';
  anchor.click();
  queueMicrotask(() => URL.revokeObjectURL(url));
}

function compositionTimelineEntry(
  visual,
  start,
  duration,
  trimStart,
  volume,
  audioEnabled = true,
  audioGraph = []) {
  return {
    visual,
    element: visual?.element || null,
    start,
    end: start + Math.max(0, duration),
    trimStart,
    volume,
    audioEnabled,
    audioGraph:
      Array.isArray(audioGraph)
        ? audioGraph
        : [],
    active: false,
    audioNodes: null
  };
}

function parseAudioWorkletNodeOptions(nodeOptionsJson) {
  const options = JSON.parse(
    typeof nodeOptionsJson === 'string'
      ? nodeOptionsJson
      : '{}');
  if (!options ||
      Array.isArray(options) ||
      typeof options !== 'object') {
    throw new TypeError(
      'AudioWorklet node options must be a JSON object.');
  }
  return options;
}

async function loadAudioWorkletModule(
  audioContext,
  modulePromises,
  moduleUri) {
  if (!audioContext.audioWorklet ||
      typeof AudioWorkletNode !== 'function') {
    throw new DOMException(
      'AudioWorklet is unavailable.',
      'NotSupportedError');
  }
  let promise = modulePromises.get(moduleUri);
  if (!promise) {
    promise = audioContext.audioWorklet.addModule(
      moduleUri).catch(error => {
        if (modulePromises.get(moduleUri) === promise) {
          modulePromises.delete(moduleUri);
        }
        throw error;
      });
    modulePromises.set(moduleUri, promise);
  }
  await promise;
}

async function createAudioWorkletEffectNode(
  audioContext,
  modulePromises,
  workletState) {
  const moduleUri =
    typeof workletState?.moduleUri === 'string'
      ? workletState.moduleUri
      : '';
  const processorName =
    typeof workletState?.processorName === 'string'
      ? workletState.processorName
      : '';
  if (!moduleUri || !processorName) {
    throw new TypeError(
      'AudioWorklet effects require a module URI and processor name.');
  }
  await loadAudioWorkletModule(
    audioContext,
    modulePromises,
    moduleUri);
  const node = new AudioWorkletNode(
    audioContext,
    processorName,
    parseAudioWorkletNodeOptions(
      workletState.nodeOptionsJson));
  state.audioWorkletNodeCreationCount++;
  return node;
}

// Algorithm: render a fixed two-channel saw sequence through the same
// application-supplied AudioWorklet construction path used by playback and
// compare its browser-owned output samples with the analytically expected
// scalar-gain result.
// Time complexity: O(F * C) for F offline frames and C channels.
// Space complexity: O(F * C) browser-owned input/output samples and O(1)
// application state; only the scalar maximum error crosses the WASM boundary.
function browserMediaAudioWorkletSignalValue(
  frame,
  channel) {
  const amplitude =
    channel === 0
      ? 1
      : 0.5;
  return (((frame % 17) - 8) / 16) * amplitude;
}

async function runBrowserMediaAudioWorkletSignalSmoke(
  moduleUri,
  processorName,
  nodeOptionsJson,
  expectedGain) {
  const channelCount = 2;
  const frameCount = 512;
  const sampleRate = 48000;
  const gain = Number(expectedGain);
  state.audioWorkletSignalMaximumError = Number.NaN;
  if (typeof OfflineAudioContext !== 'function') {
    throw new DOMException(
      'OfflineAudioContext is unavailable.',
      'NotSupportedError');
  }
  if (!Number.isFinite(gain)) {
    throw new TypeError(
      'The expected AudioWorklet signal gain must be finite.');
  }

  const context = new OfflineAudioContext(
    channelCount,
    frameCount,
    sampleRate);
  const source = context.createBufferSource();
  const buffer = context.createBuffer(
    channelCount,
    frameCount,
    sampleRate);
  let node = null;
  try {
    for (let channel = 0;
         channel < channelCount;
         channel++) {
      const samples = buffer.getChannelData(channel);
      for (let frame = 0;
           frame < frameCount;
           frame++) {
        samples[frame] =
          browserMediaAudioWorkletSignalValue(
            frame,
            channel);
      }
    }

    node = await createAudioWorkletEffectNode(
      context,
      new Map(),
      {
        moduleUri,
        processorName,
        nodeOptionsJson
      });
    source.buffer = buffer;
    source.connect(node);
    node.connect(context.destination);
    source.start(0);
    const rendered = await context.startRendering();
    let maximumError = 0;
    for (let channel = 0;
         channel < channelCount;
         channel++) {
      const samples = rendered.getChannelData(channel);
      for (let frame = 0;
           frame < frameCount;
           frame++) {
        const expected =
          browserMediaAudioWorkletSignalValue(
            frame,
            channel) * gain;
        maximumError = Math.max(
          maximumError,
          Math.abs(samples[frame] - expected));
      }
    }
    if (!Number.isFinite(maximumError)) {
      throw new Error(
        'AudioWorklet signal verification produced a non-finite error.');
    }
    state.audioWorkletSignalMaximumError = maximumError;
    return maximumError;
  } finally {
    source.disconnect();
    if (node) {
      node.disconnect();
      node.port?.close();
    }
  }
}

async function renderBrowserMediaComposition(
  operationId,
  requestJson,
  shaderSource,
  gaussianShaderSource) {
  const operation = {
    aborted: false,
    recorder: null,
    animationFrame: 0,
    thumbnailBytes: null
  };
  state.mediaCompositionExports ??= new Map();
  state.mediaCompositionExports.set(operationId, operation);
  const elements = [];
  const resources = [];
  const streamTracks = [];
  const audioWorkletModulePromises = new Map();
  let audioContext = null;
  let exportDevice = null;
  let exportCanvas = null;
  let captureCanvas = null;
  let isThumbnail = false;

  const progress = value => {
    if (state.dispatchMediaExportProgress) {
      state.dispatchMediaExportProgress(
        operationId,
        Math.max(0, Math.min(100, value)));
    }
  };
  const phase = value => {
    if (new URLSearchParams(
          globalThis.location.search)
        .get('progpuSavePicker') === 'memory') {
      document.documentElement.dataset.progpuMediaExportPhase =
        value;
    }
  };

  try {
    phase('starting');
    const request = JSON.parse(requestJson);
    isThumbnail =
      Array.isArray(request.thumbnailPositions);
    const mimeType =
      isThumbnail
        ? 'image/png'
        : browserMp4RecorderMimeType(
            Boolean(request.includeAudio));
    if ((!isThumbnail && !mimeType) ||
        !navigator.gpu ||
        typeof OffscreenCanvas !== 'function' ||
        (isThumbnail &&
         typeof OffscreenCanvas.prototype.convertToBlob !==
           'function') ||
        (!isThumbnail &&
         typeof HTMLCanvasElement.prototype.captureStream !==
           'function')) {
      return 3;
    }
    if (!(request.width > 0 && request.height > 0 &&
          request.frameRate > 0 &&
          Array.isArray(request.clips) &&
          request.clips.length > 0 &&
          (!isThumbnail ||
           request.thumbnailPositions.length > 0))) {
      return 2;
    }
    const requestedVisualClips = [
      ...request.clips,
      ...(request.overlayLayers || [])
        .flatMap(layer =>
          layer.map(overlay => overlay.clip))
    ];
    const hasGaussian =
      requestedVisualClips.some(
        clip =>
          clip.argb === undefined &&
          Number(clip.blurStandardDeviation) > 0);
    if (request.includeAudio) {
      // Construct and unlock Web Audio before the first asynchronous GPU or
      // media wait. Browsers consume transient user activation at task
      // boundaries, and a delayed resume can otherwise remain pending.
      audioContext = new AudioContext({
        latencyHint: 'playback'
      });
      await audioContext.resume();
    }

    const adapter = await navigator.gpu.requestAdapter({
      powerPreference: 'high-performance'
    });
    if (!adapter) return 3;
    phase('adapter');
    exportDevice = await adapter.requestDevice();
    let exportGpuError = null;
    exportDevice.addEventListener(
      'uncapturederror',
      event => {
        const message =
          String(
            event.error?.message ||
            event.error);
        exportGpuError = new Error(message);
        if (isMemoryProbe) {
          document.documentElement.dataset.progpuMediaExportGpuError =
            message;
        }
        console.error(
          `[ProGPU] Browser media export WebGPU validation error: ${message}`);
      });
    exportCanvas =
      new OffscreenCanvas(
        request.width,
        request.height);
    exportCanvas.width = request.width;
    exportCanvas.height = request.height;
    const isMemoryProbe =
      new URLSearchParams(
        globalThis.location.search)
        .get('progpuSavePicker') === 'memory';
    const isClearOnlyProbe =
      new URLSearchParams(
        globalThis.location.search)
        .get('progpuGpuClearOnly') === '1';
    const context = exportCanvas.getContext('webgpu');
    if (!context) return 3;
    const format = navigator.gpu.getPreferredCanvasFormat();
    context.configure({
      device: exportDevice,
      format,
      alphaMode: 'opaque'
    });
    const shader = exportDevice.createShaderModule({
      label: 'ProGPU browser media composition',
      code: shaderSource
    });
    const pipeline = await exportDevice.createRenderPipelineAsync({
      label: 'ProGPU browser media composition pipeline',
      layout: 'auto',
      vertex: {
        module: shader,
        entryPoint: 'vs_main'
      },
      fragment: {
        module: shader,
        entryPoint: 'fs_main',
        targets: [{
          format,
          blend: {
            color: {
              operation: 'add',
              srcFactor: 'one',
              dstFactor: 'one-minus-src-alpha'
            },
            alpha: {
              operation: 'add',
              srcFactor: 'one',
              dstFactor: 'one-minus-src-alpha'
            }
          }
        }]
      },
      primitive: {
        topology: 'triangle-list'
      }
    });
    let gaussianPipeline = null;
    if (hasGaussian) {
      const gaussianShader =
        exportDevice.createShaderModule({
          label: 'ProGPU browser media Gaussian blur',
          code: gaussianShaderSource
        });
      gaussianPipeline =
        await exportDevice.createRenderPipelineAsync({
          label:
            'ProGPU browser media Gaussian blur pipeline',
          layout: 'auto',
          vertex: {
            module: gaussianShader,
            entryPoint: 'vs_main'
          },
          fragment: {
            module: gaussianShader,
            entryPoint: 'fs_main',
            targets: [{
              format: 'rgba8unorm'
            }]
          },
          primitive: {
            topology: 'triangle-list'
          }
        });
    }
    phase('pipeline');
    const sampler = exportDevice.createSampler({
      addressModeU: 'clamp-to-edge',
      addressModeV: 'clamp-to-edge',
      minFilter: 'linear',
      magFilter: 'linear'
    });

    const createVisual = async (
      clip,
      destination,
      opacity = 1) => {
      const visual = {
        element: null,
        texture: null,
        uniform: exportDevice.createBuffer({
          size: 80,
          usage:
            GPUBufferUsage.UNIFORM |
            GPUBufferUsage.COPY_DST
        }),
        bindGroup: null,
        destination,
        redTransform:
          clip.redTransform ?? [1, 0, 0, 0],
        greenTransform:
          clip.greenTransform ?? [0, 1, 0, 0],
        blueTransform:
          clip.blueTransform ?? [0, 0, 1, 0],
        blurStandardDeviation:
          Math.max(
            0,
            Math.min(
              32,
              Number(
                clip.blurStandardDeviation) || 0)),
        blurIntermediate: null,
        blurOutput: null,
        blurHorizontalUniform: null,
        blurVerticalUniform: null,
        blurHorizontalBindGroup: null,
        blurVerticalBindGroup: null,
        opacity,
        isColor: clip.argb !== undefined
      };
      resources.push(visual.uniform);
      if (visual.isColor) {
        visual.texture = exportDevice.createTexture({
          size: [1, 1, 1],
          format: 'rgba8unorm',
          usage:
            GPUTextureUsage.TEXTURE_BINDING |
            GPUTextureUsage.COPY_DST |
            GPUTextureUsage.RENDER_ATTACHMENT
        });
        const argb = Number(clip.argb) >>> 0;
        exportDevice.queue.writeTexture(
          { texture: visual.texture },
          new Uint8Array([
            (argb >>> 16) & 0xff,
            (argb >>> 8) & 0xff,
            argb & 0xff,
            (argb >>> 24) & 0xff
          ]),
          { bytesPerRow: 4 },
          { width: 1, height: 1 });
      } else {
        visual.element =
          await createCompositionMediaElement(
            clip.uri,
            false);
        elements.push(visual.element);
        visual.texture = exportDevice.createTexture({
          size: [
            Math.max(1, visual.element.videoWidth),
            Math.max(1, visual.element.videoHeight),
            1
          ],
          format: 'rgba8unorm',
          usage:
            GPUTextureUsage.TEXTURE_BINDING |
            GPUTextureUsage.COPY_DST |
            GPUTextureUsage.RENDER_ATTACHMENT
        });
      }
      resources.push(visual.texture);
      if (!visual.isColor &&
          visual.blurStandardDeviation > 0) {
        if (!gaussianPipeline) {
          throw new Error(
            'The Gaussian media pipeline was not created.');
        }
        const blurSize = [
          Math.max(1, visual.element.videoWidth),
          Math.max(1, visual.element.videoHeight),
          1
        ];
        const blurOutputWidth =
          Math.max(
            1,
            request.width *
              Math.abs(destination.width));
        const blurOutputHeight =
          Math.max(
            1,
            request.height *
              Math.abs(destination.height));
        const blurUsage =
          GPUTextureUsage.TEXTURE_BINDING |
          GPUTextureUsage.RENDER_ATTACHMENT;
        visual.blurIntermediate =
          exportDevice.createTexture({
            size: blurSize,
            format: 'rgba8unorm',
            usage: blurUsage
          });
        visual.blurOutput =
          exportDevice.createTexture({
            size: blurSize,
            format: 'rgba8unorm',
            usage: blurUsage
          });
        visual.blurHorizontalUniform =
          exportDevice.createBuffer({
            size: 912,
            usage:
              GPUBufferUsage.UNIFORM |
              GPUBufferUsage.COPY_DST
          });
        visual.blurVerticalUniform =
          exportDevice.createBuffer({
            size: 912,
            usage:
              GPUBufferUsage.UNIFORM |
              GPUBufferUsage.COPY_DST
          });
        resources.push(
          visual.blurIntermediate,
          visual.blurOutput,
          visual.blurHorizontalUniform,
          visual.blurVerticalUniform);
        exportDevice.queue.writeBuffer(
          visual.blurHorizontalUniform,
          0,
          createCompositionGaussianUniform(
            visual.blurStandardDeviation,
            1 / blurOutputWidth,
            0));
        exportDevice.queue.writeBuffer(
          visual.blurVerticalUniform,
          0,
          createCompositionGaussianUniform(
            visual.blurStandardDeviation,
            0,
            1 / blurOutputHeight));
        const gaussianLayout =
          gaussianPipeline.getBindGroupLayout(0);
        visual.blurHorizontalBindGroup =
          exportDevice.createBindGroup({
            layout: gaussianLayout,
            entries: [
              { binding: 0, resource: sampler },
              {
                binding: 1,
                resource:
                  visual.texture.createView()
              },
              {
                binding: 2,
                resource:
                  visual.texture.createView()
              },
              {
                binding: 3,
                resource: {
                  buffer:
                    visual.blurHorizontalUniform
                }
              }
            ]
          });
        visual.blurVerticalBindGroup =
          exportDevice.createBindGroup({
            layout: gaussianLayout,
            entries: [
              { binding: 0, resource: sampler },
              {
                binding: 1,
                resource:
                  visual.blurIntermediate
                    .createView()
              },
              {
                binding: 2,
                resource:
                  visual.blurIntermediate
                    .createView()
              },
              {
                binding: 3,
                resource: {
                  buffer:
                    visual.blurVerticalUniform
                }
              }
            ]
          });
      }
      writeCompositionUniform(exportDevice, visual);
      const compositionTexture =
        visual.blurOutput || visual.texture;
      visual.bindGroup = exportDevice.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [
          { binding: 0, resource: sampler },
          {
            binding: 1,
            resource:
              compositionTexture.createView()
          },
          {
            binding: 2,
            resource: {
              buffer: visual.uniform
            }
          }
        ]
      });
      return visual;
    };

    const baseEntries = [];
    let totalDuration = 0;
    for (const clip of request.clips) {
      const visual = await createVisual(
        clip,
        { x: 0, y: 0, width: 1, height: 1 });
      const duration = Math.max(0, Number(clip.duration) || 0);
      baseEntries.push(
        compositionTimelineEntry(
          visual,
          totalDuration,
          duration,
          Math.max(0, Number(clip.trimStart) || 0),
          Math.max(0, Number(clip.volume) || 0),
          true,
          clip.audioGraph));
      totalDuration += duration;
    }
    phase('media');
    if (!(totalDuration > 0)) return 2;

    const overlayEntries = [];
    for (const layer of request.overlayLayers || []) {
      for (const overlay of layer) {
        const clip = overlay.clip;
        const visual = await createVisual(
          clip,
          {
            x: Number(overlay.x) / request.width,
            y: Number(overlay.y) / request.height,
            width: Number(overlay.width) / request.width,
            height: Number(overlay.height) / request.height
          },
          Math.max(0, Math.min(1, Number(overlay.opacity))));
        overlayEntries.push(
          compositionTimelineEntry(
            visual,
            Math.max(0, Number(overlay.delay) || 0),
            Math.max(0, Number(clip.duration) || 0),
            Math.max(0, Number(clip.trimStart) || 0),
            Math.max(0, Number(clip.volume) || 0),
            Boolean(overlay.audioEnabled),
            clip.audioGraph));
      }
    }

    const backgroundEntries = [];
    for (const track of request.backgroundAudio || []) {
      const element =
        await createCompositionMediaElement(
          track.uri,
          true);
      elements.push(element);
      const delay = Number(track.delay) || 0;
      const advance = Math.max(0, -delay);
      backgroundEntries.push({
        visual: null,
        element,
        start: Math.max(0, delay),
        end:
          Math.max(0, delay) +
          Math.max(
            0,
            (Number(track.duration) || 0) -
              advance),
        trimStart:
          Math.max(0, Number(track.trimStart) || 0) +
          advance,
        volume:
          Math.max(0, Number(track.volume) || 0),
        audioEnabled: true,
        active: false,
        audioGraph:
          Array.isArray(track.audioGraph)
            ? track.audioGraph
            : [],
        audioNodes: null
      });
    }
    const allEntries = [
      ...baseEntries,
      ...overlayEntries,
      ...backgroundEntries
    ];
    const visualEntries = [
      ...baseEntries,
      ...overlayEntries
    ];

    let audioDestination = null;
    if (request.includeAudio) {
      audioDestination =
        audioContext.createMediaStreamDestination();
      for (const entry of allEntries) {
        if (!entry.element ||
            !entry.audioEnabled) continue;
        const source =
          audioContext.createMediaElementSource(
            entry.element);
        const audioNodes = [];
        const volumeNode = audioContext.createGain();
        volumeNode.gain.value = entry.volume;
        source.connect(volumeNode);
        audioNodes.push(volumeNode);
        let tail = volumeNode;
        for (const state of entry.audioGraph) {
          let node = null;
          if (state?.audioWorklet === true) {
            node = await createAudioWorkletEffectNode(
              audioContext,
              audioWorkletModulePromises,
              state);
          } else {
            const kind = Number(state?.[0]);
            const parameter0 = Number(state?.[1]);
            if (kind === 1) {
              node = audioContext.createGain();
              node.gain.value = parameter0;
            } else if (kind === 2) {
              if (typeof audioContext.createStereoPanner !==
                  'function') {
                throw new DOMException(
                  'StereoPannerNode is unavailable.',
                  'NotSupportedError');
              }
              node = audioContext.createStereoPanner();
              node.pan.value = parameter0;
            } else {
              throw new DOMException(
                'The browser media audio graph contains an unsupported node.',
                'NotSupportedError');
            }
          }
          tail.connect(node);
          audioNodes.push(node);
          tail = node;
        }
        tail.connect(audioDestination);
        entry.audioNodes = audioNodes;
      }
      await audioContext.resume();
    } else {
      for (const entry of allEntries) {
        if (entry.element) entry.element.muted = true;
      }
    }

    await Promise.all(
      allEntries
        .filter(entry => entry.element)
        .map(entry =>
          seekCompositionElement(
            entry.element,
            entry.trimStart)));
    phase('seeked');

    const updateEntry = (entry, timelineSeconds) => {
      const active =
        timelineSeconds >= entry.start &&
        timelineSeconds < entry.end;
      if (!entry.element) {
        entry.active = active;
        return;
      }
      if (active) {
        const desired =
          entry.trimStart +
          timelineSeconds -
          entry.start;
        if (!entry.active ||
            Math.abs(
              entry.element.currentTime -
              desired) > 0.25) {
          entry.element.currentTime = desired;
        }
        if (entry.element.paused) {
          void entry.element.play().catch(error => {
            // Chromium rejects a still-pending play promise with AbortError
            // when the timeline reaches its end and pauses the element. That
            // is normal completion, not an encoder or decode failure.
            if (error?.name !== 'AbortError') {
              operation.playError = error;
            }
          });
        }
      } else if (!entry.element.paused) {
        entry.element.pause();
      }
      entry.active = active;
    };

    const encodeGaussianPass = (
      encoder,
      target,
      bindGroup) => {
      const blurPass = encoder.beginRenderPass({
        colorAttachments: [{
          view: target.createView(),
          clearValue: {
            r: 0,
            g: 0,
            b: 0,
            a: 0
          },
          loadOp: 'clear',
          storeOp: 'store'
        }]
      });
      blurPass.setPipeline(gaussianPipeline);
      blurPass.setBindGroup(0, bindGroup);
      blurPass.draw(3);
      blurPass.end();
    };
    const commandBufferSubmission = [null];

    const renderActiveVisuals = async () => {
      if (!isClearOnlyProbe) {
        for (let index = 0;
             index < visualEntries.length;
             index++) {
          const entry = visualEntries[index];
          const visual = entry.visual;
          if (!entry.active ||
              !visual?.element ||
              visual.element.readyState <
                HTMLMediaElement.HAVE_CURRENT_DATA) {
            continue;
          }
          exportDevice.queue.copyExternalImageToTexture(
            { source: visual.element },
            { texture: visual.texture },
            {
              width: visual.element.videoWidth,
              height: visual.element.videoHeight
            });
        }
      }

      const encoder =
        exportDevice.createCommandEncoder({
          label: 'ProGPU browser media composition frame'
        });
      let activeBase = null;
      if (!isClearOnlyProbe) {
        for (let index = 0;
             index < baseEntries.length;
             index++) {
          if (baseEntries[index].active) {
            activeBase = baseEntries[index];
            break;
          }
        }
      }
      if (activeBase?.visual.blurOutput) {
        const visual = activeBase.visual;
        encodeGaussianPass(
          encoder,
          visual.blurIntermediate,
          visual.blurHorizontalBindGroup);
        encodeGaussianPass(
          encoder,
          visual.blurOutput,
          visual.blurVerticalBindGroup);
      }
      if (!isClearOnlyProbe) {
        for (let index = 0;
             index < overlayEntries.length;
             index++) {
          const entry = overlayEntries[index];
          if (!entry.active ||
              !entry.visual.blurOutput) {
            continue;
          }
          const visual = entry.visual;
          encodeGaussianPass(
            encoder,
            visual.blurIntermediate,
            visual.blurHorizontalBindGroup);
          encodeGaussianPass(
            encoder,
            visual.blurOutput,
            visual.blurVerticalBindGroup);
        }
      }
      const pass = encoder.beginRenderPass({
        colorAttachments: [{
          view:
            context.getCurrentTexture().createView(),
          clearValue: {
            r: isClearOnlyProbe ? 0.2 : 0,
            g: 0,
            b: isClearOnlyProbe ? 0.2 : 0,
            a: 1
          },
          loadOp: 'clear',
          storeOp: 'store'
        }]
      });
      pass.setPipeline(pipeline);
      if (activeBase) {
        pass.setBindGroup(
          0,
          activeBase.visual.bindGroup);
        pass.draw(6);
      }
      for (let index = 0;
           !isClearOnlyProbe &&
           index < overlayEntries.length;
           index++) {
        const entry = overlayEntries[index];
        if (!entry.active) continue;
        pass.setBindGroup(
          0,
          entry.visual.bindGroup);
        pass.draw(6);
      }
      pass.end();
      commandBufferSubmission[0] =
        encoder.finish();
      exportDevice.queue.submit(
        commandBufferSubmission);
      await exportDevice.queue.onSubmittedWorkDone();
      if (exportGpuError) {
        throw exportGpuError;
      }
    };

    if (isThumbnail) {
      const frameDuration =
        1 / Math.max(1, Number(request.frameRate));
      const finalEntry =
        baseEntries[baseEntries.length - 1];
      const finalFrameStart =
        Math.max(
          finalEntry.start,
          totalDuration - frameDuration);
      const approximateForSpeed =
        Number(request.thumbnailPrecision) === 1;
      const thumbnailBytes = [];
      for (const requestedPosition of
           request.thumbnailPositions) {
        if (operation.aborted) return 1;
        const requested =
          Number(requestedPosition);
        if (!Number.isFinite(requested) ||
            requested < 0 ||
            requested > totalDuration) {
          return 2;
        }
        const timelineSeconds =
          requested >= totalDuration
            ? finalFrameStart
            : requested;
        const seeks = [];
        for (const entry of visualEntries) {
          const active =
            timelineSeconds >= entry.start &&
            timelineSeconds < entry.end;
          if (entry.element) {
            entry.element.pause();
            if (active) {
              const desired =
                entry.trimStart +
                timelineSeconds -
                entry.start;
              seeks.push(
                seekCompositionElement(
                  entry.element,
                  desired,
                  approximateForSpeed));
            }
          }
          entry.active = active;
        }
        await Promise.all(seeks);
        if (operation.aborted) return 1;
        await renderActiveVisuals();
        const blob =
          await exportCanvas.convertToBlob({
            type: 'image/png'
          });
        if (blob.type !== 'image/png' ||
            blob.size === 0) {
          throw new Error(
            'The browser returned an invalid PNG thumbnail.');
        }
        thumbnailBytes.push(
          new Uint8Array(
            await blob.arrayBuffer()));
      }
      operation.thumbnailBytes =
        thumbnailBytes;
      const metadata = JSON.stringify({
        contentType: 'image/png',
        width: request.width,
        height: request.height,
        lengths:
          thumbnailBytes.map(
            bytes => bytes.byteLength)
      });
      state.dispatchMediaThumbnailCompletion(
        operationId,
        0,
        metadata);
      return 0;
    }

    captureCanvas =
      document.createElement('canvas');
    captureCanvas.width = request.width;
    captureCanvas.height = request.height;
    captureCanvas.style.position = 'fixed';
    captureCanvas.style.left = '-10000px';
    captureCanvas.style.top = '0';
    captureCanvas.style.width = '1px';
    captureCanvas.style.height = '1px';
    if (isMemoryProbe) {
      captureCanvas.style.left = 'auto';
      captureCanvas.style.right = '8px';
      captureCanvas.style.bottom = '8px';
      captureCanvas.style.top = 'auto';
      captureCanvas.style.width = '320px';
      captureCanvas.style.height = '180px';
      captureCanvas.style.zIndex = '99999';
      captureCanvas.style.border =
        '2px solid #22c55e';
    }
    document.body.appendChild(captureCanvas);
    const captureContext =
      captureCanvas.getContext('2d', {
        alpha: false,
        desynchronized: true
      });
    if (!captureContext) return 3;
    const canvasStream =
      captureCanvas.captureStream(0);
    const captureTrack =
      canvasStream.getVideoTracks()[0] || null;
    if (!captureTrack) return 3;

    const renderFrame = async timelineSeconds => {
      for (const entry of allEntries) {
        updateEntry(entry, timelineSeconds);
      }
      if (operation.playError) {
        throw operation.playError;
      }
      await renderActiveVisuals();
      if (!operation.aborted) {
        const bitmap =
          exportCanvas.transferToImageBitmap();
        captureContext.drawImage(
          bitmap,
          0,
          0,
          request.width,
          request.height);
        bitmap.close();
        captureTrack.requestFrame?.();
      }
    };

    await renderFrame(0);
    phase('captured-first-frame');
    for (const track of canvasStream.getTracks()) {
      streamTracks.push(track);
    }
    const outputStream = new MediaStream(
      canvasStream.getVideoTracks());
    if (audioDestination) {
      for (const track of
           audioDestination.stream.getAudioTracks()) {
        outputStream.addTrack(track);
        streamTracks.push(track);
      }
    }

    const chunks = [];
    const recorderOptions = {
      mimeType,
      videoBitsPerSecond:
        Math.max(1, Number(request.videoBitrate) || 8_000_000)
    };
    if (request.includeAudio &&
        request.audioBitrate > 0) {
      recorderOptions.audioBitsPerSecond =
        request.audioBitrate;
    }
    const recorder =
      new MediaRecorder(
        outputStream,
        recorderOptions);
    operation.recorder = recorder;
    recorder.addEventListener(
      'dataavailable',
      event => {
        if (event.data.size !== 0) {
          chunks.push(event.data);
        }
      });
    const stopped = new Promise((resolve, reject) => {
      recorder.addEventListener(
        'stop',
        resolve,
        { once: true });
      recorder.addEventListener(
        'error',
        event =>
          reject(
            event.error ||
            new Error(
              'The browser media recorder failed.')),
        { once: true });
    });

    recorder.start(1_000);
    phase('recording');
    const clockStart = performance.now();
    let lastProgressUpdate = -1;
    await new Promise((resolve, reject) => {
      const tick = async now => {
        try {
          if (operation.aborted) {
            resolve();
            return;
          }
          const elapsed =
            Math.min(
              totalDuration,
              (now - clockStart) / 1_000);
          await renderFrame(elapsed);
          const percent =
            elapsed * 100 / totalDuration;
          if (percent - lastProgressUpdate >= 5) {
            progress(percent);
            lastProgressUpdate = percent;
          }
          if (elapsed >= totalDuration) {
            resolve();
            return;
          }
          operation.animationFrame =
            requestAnimationFrame(tick);
        } catch (error) {
          reject(error);
        }
      };
      operation.animationFrame =
        requestAnimationFrame(tick);
    });
    if (recorder.state !== 'inactive') {
      recorder.stop();
    }
    await stopped;
    phase('recorded');
    if (operation.aborted) return 1;
    const blob = new Blob(
      chunks,
      { type: recorder.mimeType || mimeType });
    if (blob.size === 0) {
      throw new Error(
        'The browser media recorder produced no bytes.');
    }
    await writeBrowserCompositionBlob(
      request.token,
      request.name,
      blob);
    phase('written');
    progress(100);
    state.dispatchMediaExportCompletion(
      operationId,
      0);
    return 0;
  } catch (error) {
    if (!operation.aborted) {
      console.error(
        isThumbnail
          ? '[ProGPU] Browser WebGPU media thumbnails failed.'
          : '[ProGPU] Browser WebGPU media export failed.',
        error);
    }
    const result =
      error?.name === 'NotSupportedError' ? 3 : 1;
    if (!isThumbnail) {
      state.dispatchMediaExportCompletion(
        operationId,
        result);
    }
    return result;
  } finally {
    if (operation.animationFrame) {
      cancelAnimationFrame(
        operation.animationFrame);
    }
    if (operation.recorder &&
        operation.recorder.state !== 'inactive') {
      try { operation.recorder.stop(); } catch { }
    }
    for (const entry of elements) {
      try { entry.pause(); } catch { }
      entry.removeAttribute('src');
      try { entry.load(); } catch { }
      releaseBrowserStorageMediaSource(entry);
      entry.remove();
    }
    for (const track of streamTracks) {
      try { track.stop(); } catch { }
    }
    for (const resource of resources) {
      try { resource.destroy(); } catch { }
    }
    if (audioContext) {
      try { await audioContext.close(); } catch { }
    }
    if (exportCanvas?.remove) exportCanvas.remove();
    if (captureCanvas) captureCanvas.remove();
    if (exportDevice) {
      try { exportDevice.destroy(); } catch { }
    }
    if (!operation.thumbnailBytes ||
        operation.aborted) {
      state.mediaCompositionExports.delete(operationId);
    }
  }
}

function startBrowserMediaCompositionExport(
  operationId,
  requestJson,
  shaderSource,
  gaussianShaderSource) {
  void renderBrowserMediaComposition(
    operationId,
    requestJson,
    shaderSource,
    gaussianShaderSource).then(
      result =>
        state.dispatchMediaExportCompletion(
          operationId,
          result),
      error => {
        console.error(
          '[ProGPU] Browser media export callback failed.',
          error);
        state.dispatchMediaExportCompletion(
          operationId,
          1);
      });
}

function startBrowserMediaCompositionThumbnails(
  operationId,
  requestJson,
  shaderSource,
  gaussianShaderSource) {
  void renderBrowserMediaComposition(
    operationId,
    requestJson,
    shaderSource,
    gaussianShaderSource).then(
      result => {
        if (result !== 0) {
          state.dispatchMediaThumbnailCompletion(
            operationId,
            result,
            '');
        }
      },
      error => {
        console.error(
          '[ProGPU] Browser media thumbnail callback failed.',
          error);
        state.dispatchMediaThumbnailCompletion(
          operationId,
          1,
          '');
      });
}

function copyBrowserMediaCompositionThumbnail(
  operationId,
  index,
  destination,
  length) {
  const bytes =
    state.mediaCompositionExports
      ?.get(operationId)
      ?.thumbnailBytes?.[index];
  if (!bytes ||
      bytes.byteLength !== length) {
    return -1;
  }
  const heap = runtime.localHeapViewU8();
  if (destination < 0 ||
      destination + length > heap.byteLength) {
    throw new RangeError(
      'Thumbnail destination is outside WASM memory.');
  }
  heap.set(bytes, destination);
  return length;
}

function clearBrowserMediaCompositionThumbnails(
  operationId) {
  const operation =
    state.mediaCompositionExports?.get(operationId);
  if (operation) {
    operation.thumbnailBytes = null;
  }
  state.mediaCompositionExports?.delete(operationId);
}

function cancelBrowserMediaCompositionExport(operationId) {
  const operation =
    state.mediaCompositionExports?.get(operationId);
  if (!operation) return;
  operation.aborted = true;
  if (operation.animationFrame) {
    cancelAnimationFrame(
      operation.animationFrame);
  }
  if (operation.recorder &&
      operation.recorder.state !== 'inactive') {
    try { operation.recorder.stop(); } catch { }
  }
}

function storagePickerTypes(filters) {
  const extensions = String(filters || '')
    .split(',')
    .map(value => value.trim())
    .filter(value => /^\.[A-Za-z0-9][A-Za-z0-9._+-]{0,15}$/.test(value));
  return extensions.length === 0
    ? undefined
    : [{ description: 'Supported files', accept: { 'application/octet-stream': extensions } }];
}

function rememberStorageHandle(handles, prefix, handle) {
  const token = `${prefix}-${state.nextStorageHandle++}`;
  handles.set(token, handle);
  if (handles.size > 32) handles.delete(handles.keys().next().value);
  return token;
}

function encodeStorageSelection(token, name) {
  return `${token}\n${encodeURIComponent(name)}`;
}

function usesNativeSaveStoragePicker() {
  const savePickerMode =
    new URLSearchParams(globalThis.location.search)
      .get('progpuSavePicker');
  return savePickerMode !== 'download' &&
    savePickerMode !== 'memory' &&
    typeof globalThis.showSaveFilePicker === 'function';
}

function pickStorageWithInput(filters, directory = false) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    let settled = false;
    input.type = 'file';
    if (directory) {
      input.multiple = true;
      input.webkitdirectory = true;
    } else {
      input.accept = filters || '';
    }
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
      if (directory) {
        const relativePath = file.webkitRelativePath || file.name;
        resolve(encodeStorageSelection('virtual', relativePath.split('/')[0] || 'folder'));
        return;
      }
      try { resolve(await stagePickedFile(file)); }
      catch { state.pickedStorageBytes = null; resolve(''); }
    };
    input.addEventListener('change', () => finish(input.files?.[0] || null), { once: true });
    input.addEventListener('cancel', () => finish(null), { once: true });
    try { input.click(); }
    catch { finish(null); }
  });
}

async function pickOpenStorage(filters) {
  state.pickedStorageBytes = null;
  if (typeof globalThis.showOpenFilePicker !== 'function') return await pickStorageWithInput(filters);
  try {
    const types = storagePickerTypes(filters);
    const handles = await globalThis.showOpenFilePicker(types ? { multiple: false, types } : { multiple: false });
    const file = await handles[0]?.getFile();
    return file ? await stagePickedFile(file) : '';
  } catch (error) {
    if (error?.name !== 'AbortError') globalThis.console.error('[ProGPU] Browser open-file picker failed.', error);
    return '';
  }
}

async function pickSaveStorage(filters, defaultName) {
  const name = defaultName || 'untitled.txt';
  const savePickerMode =
    new URLSearchParams(globalThis.location.search)
      .get('progpuSavePicker');
  document.documentElement.dataset.progpuSavePickerObserved =
    savePickerMode || 'native';
  if (savePickerMode === 'download' ||
      savePickerMode === 'memory' ||
      typeof globalThis.showSaveFilePicker !== 'function') {
    document.documentElement.dataset.progpuSavePickerResult =
      'download';
    if (savePickerMode === 'memory') {
      document.documentElement.dataset.progpuSavePickerYielded =
        'promise';
    }
    const selection =
      encodeStorageSelection('download', name);
    if (savePickerMode === 'memory') {
      document.documentElement.dataset.progpuSavePickerSelection =
        selection;
    }
    return selection;
  }
  try {
    const types = storagePickerTypes(filters);
    const options = types ? { suggestedName: name, types } : { suggestedName: name };
    const handle = await globalThis.showSaveFilePicker(options);
    const token = rememberStorageHandle(state.saveFileHandles, 'save', handle);
    return encodeStorageSelection(token, handle.name || name);
  } catch (error) {
    if (error?.name !== 'AbortError') globalThis.console.error('[ProGPU] Browser save-file picker failed.', error);
    return '';
  }
}

async function pickFolderStorage() {
  if (typeof globalThis.showDirectoryPicker !== 'function') return await pickStorageWithInput('', true);
  try {
    const handle = await globalThis.showDirectoryPicker({ mode: 'readwrite' });
    const token = rememberStorageHandle(state.directoryHandles, 'folder', handle);
    return encodeStorageSelection(token, handle.name || 'folder');
  } catch (error) {
    if (error?.name !== 'AbortError') globalThis.console.error('[ProGPU] Browser directory picker failed.', error);
    return '';
  }
}

function pickStorage(mode, filters, defaultName) {
  if (mode === 0) return pickOpenStorage(filters);
  if (mode === 1) return pickSaveStorage(filters, defaultName);
  if (mode === 2) return pickFolderStorage();
  return Promise.resolve('');
}

async function writePickedStorageText(token, text) {
  const handle = state.saveFileHandles.get(token);
  if (!handle) return false;
  try {
    const writable = await handle.createWritable();
    await writable.write(text);
    await writable.close();
    return true;
  } catch (error) {
    globalThis.console.error('[ProGPU] Browser file write failed.', error);
    return false;
  }
}

async function writePickedStorageBytes(token, source, length) {
  const handle = state.saveFileHandles.get(token);
  if (!handle) return false;
  const heap = runtime.localHeapViewU8();
  if (source < 0 || length < 0 || source + length > heap.byteLength) throw new RangeError('File source is outside WASM memory.');
  // Copy before the first await because WASM memory can grow while the browser picker is open.
  const bytes = heap.slice(source, source + length);
  try {
    const writable = await handle.createWritable();
    await writable.write(bytes);
    await writable.close();
    return true;
  } catch (error) {
    globalThis.console.error('[ProGPU] Browser file write failed.', error);
    return false;
  }
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

function downloadBytes(name, source, length) {
  const heap = runtime.localHeapViewU8();
  if (source < 0 || length < 0 || source + length > heap.byteLength) throw new RangeError('Download source is outside WASM memory.');
  const bytes = heap.slice(source, source + length);
  if (new URLSearchParams(globalThis.location.search)
      .get('progpuSavePicker') === 'memory') {
    document.documentElement.dataset.progpuDownloadName =
      name || 'download.bin';
    document.documentElement.dataset.progpuDownloadLength =
      String(bytes.byteLength);
    document.documentElement.dataset.progpuDownloadPrefix =
      [...bytes.subarray(0, 12)]
        .map(value => value.toString(16).padStart(2, '0'))
        .join('');
    return;
  }
  const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name || 'download.bin';
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

  const webGpu = await requestProGpuWebGpuDevice({
    powerPreference: request.powerPreference,
    gpuProfile: request.gpuProfile,
    onUncapturedError: detail => {
      globalThis.console.error(`[ProGPU] WebGPU validation error: ${detail}`);
      if (state.firstGpuError === null) {
        state.firstGpuError = detail;
        reportGpuStatus('WebGPU validation error', detail, true);
      }
    },
    onDeviceLost: info => {
      reportGpuStatus('WebGPU device lost', `${info.reason}: ${info.message}. Reloading the retained application to reconstruct GPU resources.`, true);
      if (isDispatcherWorker) globalThis.postMessage({ type: 'device-lost' });
      else globalThis.setTimeout(() => globalThis.location?.reload(), 250);
    }
  });
  state.adapter = webGpu.adapter;
  state.device = webGpu.device;
  state.format = webGpu.format;
  const supportsBgraStorage = webGpu.supportsBgra8UnormStorage;
  let activeProfile = webGpu.activeProfile === 'Full' ? 1 : 0;
  if (request.gpuProfile === 'Full' && !supportsBgraStorage) {
    activeProfile = 0;
    diagnostics.push('Full profile downgraded: bgra8unorm-storage is unavailable; dependent Wavefront storage output is disabled.');
  }
  state.context.configure({ device: state.device, format: state.format, alphaMode: 'premultiplied' });

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
    maxBufferSize: state.device.limits.maxBufferSize,
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

function requireMediaElement(id) {
  const entry = state.mediaElements.get(id);
  if (!entry) throw new Error(`Unknown ProGPU media element ${id}.`);
  return entry;
}

function mediaProgress(video) {
  if (!Number.isFinite(video.duration) || video.duration <= 0 || video.buffered.length === 0) return 0;
  return Math.max(0, Math.min(1, video.buffered.end(video.buffered.length - 1) / video.duration));
}

function dispatchMediaEvent(id, kind, video, message = '') {
  if (!state.dispatchMediaEvent) return;
  state.dispatchMediaEvent(
    id,
    kind,
    Number.isFinite(video.currentTime) ? video.currentTime : 0,
    Number.isFinite(video.duration) ? video.duration : 0,
    video.videoWidth || 0,
    video.videoHeight || 0,
    mediaProgress(video),
    message);
}

function browserMediaTextTrackId(entry, track) {
  let value = entry.textTrackIds.get(track);
  if (!value) {
    value = `htmlmedia:text:${entry.id}:${entry.nextTextTrackId++}`;
    entry.textTrackIds.set(track, value);
  }
  return value;
}

function browserMediaCueId(entry, cue) {
  let value = entry.cueIds.get(cue);
  if (!value) {
    value = `htmlmedia:cue:${entry.id}:${entry.nextCueId++}`;
    entry.cueIds.set(cue, value);
  }
  return value;
}

function captureBrowserMediaTextTracks(entry) {
  return Array.from(entry.video.textTracks || []).map(track => ({
    providerTrackId: browserMediaTextTrackId(entry, track),
    kind: track.kind || '',
    label: track.label || '',
    language: track.language || '',
    mode: track.mode || 'disabled',
    cues: Array.from(track.cues || []).map(cue => {
      const startTime = Number.isFinite(cue.startTime)
        ? Math.max(0, cue.startTime)
        : 0;
      const endTime = Number.isFinite(cue.endTime)
        ? Math.max(startTime, cue.endTime)
        : startTime;
      return {
        id: browserMediaCueId(entry, cue),
        startTime,
        duration: endTime - startTime,
        text: typeof cue.text === 'string' ? cue.text : ''
      };
    })
  }));
}

function captureBrowserMediaTimedMetadata(entry) {
  return JSON.stringify({
    textTracks: captureBrowserMediaTextTracks(entry)
  });
}

function dispatchBrowserMediaTimedMetadata(entry) {
  if (entry.disposed || !state.dispatchMediaTimedMetadata) return;
  state.dispatchMediaTimedMetadata(
    entry.id,
    captureBrowserMediaTimedMetadata(entry));
}

function observeBrowserMediaTextTracks(entry) {
  const currentTracks = new Set(
    Array.from(entry.video.textTracks || []));
  for (const [track, listener] of entry.textTrackListeners) {
    if (!currentTracks.has(track)) {
      track.removeEventListener('cuechange', listener);
      entry.textTrackListeners.delete(track);
    }
  }
  for (const track of currentTracks) {
    browserMediaTextTrackId(entry, track);
    if (!entry.textTrackListeners.has(track)) {
      const listener = () =>
        dispatchBrowserMediaTimedMetadata(entry);
      track.addEventListener('cuechange', listener);
      entry.textTrackListeners.set(track, listener);
    }
  }
}

function onBrowserMediaTextTracksChanged(entry) {
  observeBrowserMediaTextTracks(entry);
  dispatchBrowserMediaTimedMetadata(entry);
}

function scheduleMediaFrame(entry) {
  if (entry.disposed || entry.frameCallbackActive) return;
  entry.frameCallbackActive = true;
  const callback = () => {
    entry.frameCallbackActive = false;
    if (entry.disposed) return;
    dispatchMediaEvent(entry.id, 1, entry.video);
    if (typeof entry.video.requestVideoFrameCallback === 'function' ||
        (!entry.video.paused && !entry.video.ended)) {
      scheduleMediaFrame(entry);
    }
  };
  if (typeof entry.video.requestVideoFrameCallback === 'function') {
    entry.frameCallbackHandle = entry.video.requestVideoFrameCallback(callback);
  } else {
    entry.frameCallbackHandle = requestAnimationFrame(callback);
  }
}

async function createBrowserMedia(id, uri) {
  if (state.mediaElements.has(id)) throw new Error(`ProGPU media element ${id} already exists.`);
  const video = document.createElement('video');
  video.preload = 'auto';
  video.playsInline = true;
  video.crossOrigin = 'anonymous';
  video.style.display = 'none';
  document.body.appendChild(video);

  const entry = {
    id,
    video,
    disposed: false,
    frameCallbackActive: false,
    frameCallbackHandle: 0,
    audioContext: null,
    audioSource: null,
    panner: null,
    audioEffects: new Map(),
    audioWorkletModulePromises: new Map(),
    textTrackIds: new WeakMap(),
    cueIds: new WeakMap(),
    nextTextTrackId: 1,
    nextCueId: 1,
    textTrackListeners: new Map(),
    textTrackListChanged: null,
    objectUrl: null
  };
  state.mediaElements.set(id, entry);

  video.addEventListener('playing', () => {
    dispatchMediaEvent(id, 2, video);
    scheduleMediaFrame(entry);
  });
  video.addEventListener('pause', () => {
    if (!video.ended) dispatchMediaEvent(id, 3, video);
  });
  video.addEventListener('waiting', () => dispatchMediaEvent(id, 4, video));
  video.addEventListener('ended', () => dispatchMediaEvent(id, 5, video));
  video.addEventListener('timeupdate', () => dispatchMediaEvent(id, 6, video));
  video.addEventListener('progress', () => dispatchMediaEvent(id, 7, video));
  video.addEventListener('seeked', () => dispatchMediaEvent(id, 9, video));
  video.addEventListener('error', () => {
    const error = video.error;
    dispatchMediaEvent(id, 8, video, error ? `${error.code}: ${error.message || 'media error'}` : 'Unknown browser media error.');
  });
  video.addEventListener('loadeddata', () => {
    dispatchMediaEvent(id, 1, video);
    scheduleMediaFrame(entry);
  });

  const metadata = new Promise((resolve, reject) => {
    video.addEventListener('loadedmetadata', resolve, { once: true });
    video.addEventListener('error', () => reject(new Error(video.error?.message || 'The browser rejected the media source.')), { once: true });
  });
  try {
    const source = resolveBrowserStorageMediaSource(uri);
    entry.objectUrl = source.objectUrl;
    video.src = source.uri;
    video.load();
    await metadata;
  } catch (error) {
    disposeBrowserMedia(id);
    throw error;
  }
  entry.textTrackListChanged = () =>
    onBrowserMediaTextTracksChanged(entry);
  video.textTracks.addEventListener(
    'change',
    entry.textTrackListChanged);
  video.textTracks.addEventListener(
    'addtrack',
    entry.textTrackListChanged);
  video.textTracks.addEventListener(
    'removetrack',
    entry.textTrackListChanged);
  observeBrowserMediaTextTracks(entry);
  scheduleMediaFrame(entry);
  return JSON.stringify({
    duration: Number.isFinite(video.duration) ? video.duration : 0,
    width: video.videoWidth || 0,
    height: video.videoHeight || 0,
    textTracks: captureBrowserMediaTextTracks(entry)
  });
}

function playBrowserMedia(id) {
  const entry = requireMediaElement(id);
  if (entry.audioContext?.state === 'suspended') void entry.audioContext.resume();
  void entry.video.play().catch(error => dispatchMediaEvent(id, 8, entry.video, String(error?.message || error)));
}

function pauseBrowserMedia(id) {
  requireMediaElement(id).video.pause();
}

function seekBrowserMedia(id, seconds) {
  const video = requireMediaElement(id).video;
  video.currentTime = Math.max(0, Number.isFinite(video.duration) ? Math.min(seconds, video.duration) : seconds);
}

function setBrowserMediaRate(id, rate) {
  requireMediaElement(id).video.playbackRate = rate;
}

function setBrowserMediaLooping(id, looping) {
  requireMediaElement(id).video.loop = looping;
}

function setBrowserMediaAudio(id, volume, balance, muted) {
  const entry = requireMediaElement(id);
  entry.video.volume = Math.max(0, Math.min(1, volume));
  entry.video.muted = muted;
  if (balance !== 0) ensureBrowserMediaAudioGraph(entry);
  if (entry.panner) entry.panner.pan.value = Math.max(-1, Math.min(1, balance));
}

function setBrowserMediaTimedMetadataMode(id, index, mode) {
  const entry = requireMediaElement(id);
  if (!Number.isInteger(index) ||
      index < 0 ||
      index >= entry.video.textTracks.length) {
    throw new RangeError(
      `Browser media text-track index ${index} is out of range.`);
  }
  if (mode !== 'disabled' &&
      mode !== 'hidden' &&
      mode !== 'showing') {
    throw new RangeError(
      `Unsupported browser media text-track mode '${mode}'.`);
  }
  entry.video.textTracks[index].mode = mode;
  observeBrowserMediaTextTracks(entry);
  return captureBrowserMediaTimedMetadata(entry);
}

function ensureBrowserMediaAudioGraph(entry) {
  if (entry.audioContext) return;
  if (typeof AudioContext === 'undefined') throw new Error('This browser does not expose Web Audio.');
  entry.audioContext = new AudioContext({ latencyHint: 'interactive' });
  entry.audioSource = entry.audioContext.createMediaElementSource(entry.video);
  entry.panner = entry.audioContext.createStereoPanner();
  rebuildBrowserMediaAudioGraph(entry);
}

function rebuildBrowserMediaAudioGraph(entry) {
  if (!entry.audioContext || !entry.audioSource || !entry.panner) return;
  entry.audioSource.disconnect();
  entry.panner.disconnect();
  for (const effect of entry.audioEffects.values()) {
    if (effect.node) effect.node.disconnect();
  }

  let tail = entry.audioSource;
  for (const effect of entry.audioEffects.values()) {
    if (!effect.node) continue;
    tail.connect(effect.node);
    tail = effect.node;
  }
  tail.connect(entry.panner);
  entry.panner.connect(entry.audioContext.destination);
}

function configureBrowserMediaAudioEffect(id, effectId, kind, parameter0, parameter1, parameter2, parameter3) {
  const entry = requireMediaElement(id);
  ensureBrowserMediaAudioGraph(entry);
  let effect = entry.audioEffects.get(effectId);
  if (!effect || effect.kind !== kind) {
    if (effect?.node) {
      effect.node.disconnect();
      effect.node.port?.close();
    }
    if (kind === 1) {
      effect = {
        kind,
        node: entry.audioContext.createGain()
      };
    } else if (kind === 2) {
      effect = {
        kind,
        node: entry.audioContext.createStereoPanner()
      };
    } else {
      throw new RangeError(`Unsupported browser media audio effect kind ${kind}.`);
    }
    entry.audioEffects.set(effectId, effect);
  }

  if (kind === 1) {
    const gain = Number.isFinite(parameter0) ? Math.max(0, parameter0) : 1;
    effect.node.gain.setValueAtTime(gain, entry.audioContext.currentTime);
  } else if (kind === 2) {
    const balance =
      Number.isFinite(parameter0)
        ? Math.max(-1, Math.min(1, parameter0))
        : 0;
    effect.node.pan.setValueAtTime(
      balance,
      entry.audioContext.currentTime);
  }
  rebuildBrowserMediaAudioGraph(entry);
}

function configureBrowserMediaAudioWorkletEffect(
  id,
  effectId,
  moduleUri,
  processorName,
  nodeOptionsJson,
  optional) {
  const entry = requireMediaElement(id);
  ensureBrowserMediaAudioGraph(entry);
  const previous = entry.audioEffects.get(effectId);
  if (previous?.node) {
    previous.node.disconnect();
    previous.node.port?.close();
  }
  const effect = {
    kind: 'audio-worklet',
    node: null
  };
  entry.audioEffects.set(effectId, effect);
  rebuildBrowserMediaAudioGraph(entry);

  void createAudioWorkletEffectNode(
    entry.audioContext,
    entry.audioWorkletModulePromises,
    {
      moduleUri,
      processorName,
      nodeOptionsJson
    }).then(
      node => {
        if (entry.disposed ||
            entry.audioEffects.get(effectId) !== effect) {
          node.disconnect();
          node.port?.close();
          return;
        }
        effect.node = node;
        rebuildBrowserMediaAudioGraph(entry);
      },
      error => {
        if (entry.audioEffects.get(effectId) === effect) {
          entry.audioEffects.delete(effectId);
          rebuildBrowserMediaAudioGraph(entry);
        }
        if (!optional && !entry.disposed) {
          dispatchMediaEvent(
            id,
            8,
            entry.video,
            `Browser AudioWorklet effect failed: ${
              error?.message || error}`);
        }
      });
}

function removeAllBrowserMediaAudioEffects(id) {
  const entry = requireMediaElement(id);
  for (const effect of entry.audioEffects.values()) {
    if (effect.node) {
      effect.node.disconnect();
      effect.node.port?.close();
    }
  }
  entry.audioEffects.clear();
  rebuildBrowserMediaAudioGraph(entry);
}

function copyBrowserMediaFrame(id, width, height) {
  const entry = requireMediaElement(id);
  const video = entry.video;
  if (video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || width <= 0 || height <= 0) return false;

  if (state.worker) {
    if (typeof VideoFrame === 'undefined') return false;
    const frame = new VideoFrame(video);
    let transferred = false;
    try {
      state.worker.postMessage(
        { type: 'media-frame-ready', mediaId: id, frame },
        [frame]);
      transferred = true;
    } finally {
      // A successful transfer moves the resource reference into the worker and
      // closes this object. If postMessage fails before transfer, ownership
      // remains here and must be released explicitly.
      if (!transferred) frame.close();
    }
  }
  return true;
}

function disposeBrowserMedia(id) {
  const entry = state.mediaElements.get(id);
  if (!entry) return;
  entry.disposed = true;
  if (entry.textTrackListChanged) {
    entry.video.textTracks.removeEventListener(
      'change',
      entry.textTrackListChanged);
    entry.video.textTracks.removeEventListener(
      'addtrack',
      entry.textTrackListChanged);
    entry.video.textTracks.removeEventListener(
      'removetrack',
      entry.textTrackListChanged);
  }
  for (const [track, listener] of entry.textTrackListeners) {
    track.removeEventListener('cuechange', listener);
  }
  entry.textTrackListeners.clear();
  if (entry.frameCallbackHandle) {
    if (typeof entry.video.cancelVideoFrameCallback === 'function') entry.video.cancelVideoFrameCallback(entry.frameCallbackHandle);
    else cancelAnimationFrame(entry.frameCallbackHandle);
  }
  entry.video.pause();
  entry.video.removeAttribute('src');
  entry.video.load();
  entry.video.remove();
  if (entry.objectUrl) {
    URL.revokeObjectURL(entry.objectUrl);
    entry.objectUrl = null;
  }
  if (entry.audioSource) entry.audioSource.disconnect();
  if (entry.panner) entry.panner.disconnect();
  for (const effect of entry.audioEffects.values()) {
    if (effect.node) {
      effect.node.disconnect();
      effect.node.port?.close();
    }
  }
  entry.audioEffects.clear();
  entry.audioWorkletModulePromises.clear();
  if (entry.audioContext) void entry.audioContext.close();
  state.mediaElements.delete(id);
  if (state.worker) {
    // Worker messages and command packets use the same ordered port. This
    // arrives after every already-posted frame/copy command and releases the
    // final coalesced frame when the provider closes before consuming it.
    state.worker.postMessage({ type: 'media-disposed', mediaId: id });
  }
}

function getBrowserMediaElementCount() {
  return state.mediaElements.size;
}

function getBrowserMediaAudioWorkletNodeCreationCount() {
  return state.audioWorkletNodeCreationCount;
}

function getBrowserMediaAudioWorkletSignalMaximumError() {
  return state.audioWorkletSignalMaximumError;
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
    case 61:
      requireResource(view.getUint32(payload, true)).copyBufferToTexture(imageCopyBuffer(view, payload + 4), imageCopyTexture(view, payload + 28), extent3d(view, payload + 52));
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
    case 74: {
      const mediaId = view.getUint32(payload, true);
      const textureHandle = view.getUint32(payload + 4, true);
      const width = view.getUint32(payload + 8, true);
      const height = view.getUint32(payload + 12, true);
      const source = isDispatcherWorker
        ? state.pendingMediaFrames.get(mediaId)
        : requireMediaElement(mediaId).video;
      if (!source) throw new Error(`No decoded browser media frame is ready for ${mediaId}.`);
      if (isDispatcherWorker) state.pendingMediaFrames.delete(mediaId);
      try {
        state.device.queue.copyExternalImageToTexture(
          { source },
          { texture: requireResource(textureHandle) },
          { width, height, depthOrArrayLayers: 1 });
      } finally {
        if (isDispatcherWorker) source.close();
      }
      break;
    }
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

function publishBrowserMediaCapabilities() {
  const mp4Candidates = [
    'video/mp4;codecs="avc1.42E01E,mp4a.40.2"',
    'video/mp4;codecs="avc1.42E01E"',
    'video/mp4'
  ];
  const selected =
    typeof MediaRecorder === 'function'
      ? mp4Candidates.find(
          value =>
            MediaRecorder.isTypeSupported(value)) || ''
      : '';
  document.documentElement.dataset.progpuMediaRecorderMp4 =
    selected;
  document.documentElement.dataset.progpuCanvasCapture =
    String(typeof HTMLCanvasElement.prototype.captureStream ===
      'function');
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
      case 'media-frame-ready': {
        const previous = state.pendingMediaFrames.get(message.mediaId);
        previous?.close();
        state.pendingMediaFrames.set(message.mediaId, message.frame);
        break;
      }
      case 'media-disposed': {
        const pending = state.pendingMediaFrames.get(message.mediaId);
        pending?.close();
        state.pendingMediaFrames.delete(message.mediaId);
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
  publishBrowserMediaCapabilities();
  runtime = await dotnet.withEnvironmentVariables(readBenchmarkEnvironment()).create();
  runtime.setModuleImports('progpu-browser', { initialize, dispatch, dispatchUpload, mapBuffer, copyMappedBuffer, writeMappedBuffer, releaseMappedBuffer, nextAnimationFrame, writeCanvasMetrics, drainInputEvents, setCanvasCursor, requestCanvasPointerLock, exitCanvasPointerLock, configureTextInput, hideTextInput, setClipboardText, getClipboardText, setClipboardRichText, getClipboardRtf, getClipboardHtml, pickStorage, usesNativeSaveStoragePicker, getPickedStorageLength, copyPickedStorage, clearPickedStorage, writePickedStorageText, writePickedStorageBytes, downloadText, downloadBytes, startStageBrowserMediaSource, copyStagedBrowserMediaSource, clearStagedBrowserMediaSource, cancelStagedBrowserMediaSource, startBrowserMediaCompositionExport, startBrowserMediaCompositionThumbnails, copyBrowserMediaCompositionThumbnail, clearBrowserMediaCompositionThumbnails, cancelBrowserMediaCompositionExport, createBrowserMedia, playBrowserMedia, pauseBrowserMedia, seekBrowserMedia, setBrowserMediaRate, setBrowserMediaLooping, setBrowserMediaAudio, setBrowserMediaTimedMetadataMode, configureBrowserMediaAudioEffect, configureBrowserMediaAudioWorkletEffect, removeAllBrowserMediaAudioEffects, copyBrowserMediaFrame, disposeBrowserMedia, getBrowserMediaElementCount, getBrowserMediaAudioWorkletNodeCreationCount, runBrowserMediaAudioWorkletSignalSmoke, getBrowserMediaAudioWorkletSignalMaximumError, getDiagnosticsVisible, setDiagnosticsVisible, setStatus, updateCounters });
  const browserExports = await runtime.getAssemblyExports('ProGPU.Browser.dll');
  state.dispatchImmediatePointer = browserExports.ProGPU.Browser.BrowserInputDispatcher.DispatchImmediatePointer;
  state.dispatchTextInput = browserExports.ProGPU.Browser.BrowserInputDispatcher.DispatchTextInput;
  state.dispatchMediaEvent = browserExports.ProGPU.Browser.BrowserMediaCallbacks.DispatchEvent;
  state.dispatchMediaTimedMetadata = browserExports.ProGPU.Browser.BrowserMediaCallbacks.DispatchTimedMetadata;
  state.dispatchMediaExportProgress = browserExports.ProGPU.Browser.BrowserMediaExportCallbacks.DispatchProgress;
  state.dispatchMediaExportCompletion = browserExports.ProGPU.Browser.BrowserMediaExportCallbacks.DispatchCompletion;
  state.dispatchMediaThumbnailCompletion = browserExports.ProGPU.Browser.BrowserMediaThumbnailCallbacks.DispatchCompletion;
  state.dispatchMediaStageCompletion = browserExports.ProGPU.Browser.BrowserMediaStagingCallbacks.DispatchCompletion;
  const playbackSmoke =
    new URLSearchParams(globalThis.location.search)
      .get('progpuMediaPlaybackSmoke');
  if (playbackSmoke === '1') {
    const button = document.createElement('button');
    button.dataset.testid = 'progpu-media-playback-smoke';
    button.textContent = 'Run browser playback smoke';
    button.style.position = 'fixed';
    button.style.right = '8px';
    button.style.top = '8px';
    button.style.zIndex = '100000';
    button.addEventListener(
      'click',
      async () => {
        button.disabled = true;
        button.textContent = 'Running browser playback smoke…';
        try {
          const playbackSource =
            new URLSearchParams(globalThis.location.search)
              .get('progpuMediaPlaybackSource') ||
            'https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4';
          const result =
            await browserExports.ProGPU.Browser
              .BrowserMediaPlaybackSmokeTest
              .RunAsync(playbackSource);
          document.documentElement.dataset.progpuMediaPlaybackSmokeResult =
            String(result);
          document.documentElement.dataset.progpuMediaPlaybackElementCount =
            String(getBrowserMediaElementCount());
          document.documentElement.dataset.progpuMediaPlaybackAudioWorkletNodes =
            String(getBrowserMediaAudioWorkletNodeCreationCount());
          document.documentElement.dataset.progpuMediaPlaybackAudioWorkletSignalMaximumError =
            String(getBrowserMediaAudioWorkletSignalMaximumError());
          button.textContent = 'Browser playback smoke passed';
        } catch (error) {
          document.documentElement.dataset.progpuMediaPlaybackSmokeResult =
            'exception';
          document.documentElement.dataset.progpuMediaPlaybackElementCount =
            String(getBrowserMediaElementCount());
          document.documentElement.dataset.progpuMediaPlaybackAudioWorkletNodes =
            String(getBrowserMediaAudioWorkletNodeCreationCount());
          document.documentElement.dataset.progpuMediaPlaybackAudioWorkletSignalMaximumError =
            String(getBrowserMediaAudioWorkletSignalMaximumError());
          button.textContent = 'Browser playback smoke failed';
          console.error(
            '[ProGPU] Browser media playback smoke test failed.',
            error);
        }
      },
      { once: true });
    document.body.appendChild(button);
  }
  const thumbnailSmoke =
    new URLSearchParams(globalThis.location.search)
      .get('progpuMediaThumbnailSmoke');
  if (thumbnailSmoke === '1' ||
      thumbnailSmoke === 'effect') {
    globalThis.setTimeout(
      async () => {
        try {
          const thumbnailSource =
            new URLSearchParams(globalThis.location.search)
              .get('progpuMediaExportSource') ||
            'https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4';
          const result =
            thumbnailSmoke === 'effect'
              ? await browserExports.ProGPU.Browser
                  .BrowserMediaThumbnailSmokeTest
                  .RunEffectAsync(thumbnailSource)
              : await browserExports.ProGPU.Browser
                  .BrowserMediaThumbnailSmokeTest
                  .RunAsync();
          document.documentElement.dataset.progpuMediaThumbnailSmokeResult =
            String(result);
        } catch (error) {
          document.documentElement.dataset.progpuMediaThumbnailSmokeResult =
            'exception';
          console.error(
            '[ProGPU] Browser media thumbnail smoke test failed.',
            error);
        }
      },
      0);
  }
  const exportSmoke =
    new URLSearchParams(globalThis.location.search)
      .get('progpuMediaExportSmoke');
  if (exportSmoke === 'fast' ||
      exportSmoke === 'effect' ||
      exportSmoke === 'effect-audio') {
    const runExportSmoke = async () => {
      try {
        const exportSource =
          new URLSearchParams(globalThis.location.search)
            .get('progpuMediaExportSource') ||
          'https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4';
        const result =
          await browserExports.ProGPU.Browser
            .BrowserMediaExportSmokeTest.RunAsync(
              exportSource,
              exportSmoke !== 'fast',
              exportSmoke === 'effect-audio');
        document.documentElement.dataset.progpuMediaExportSmokeResult =
          String(result);
        document.documentElement.dataset.progpuMediaExportAudioWorkletNodes =
          String(getBrowserMediaAudioWorkletNodeCreationCount());
      } catch (error) {
        document.documentElement.dataset.progpuMediaExportSmokeResult =
          'exception';
        document.documentElement.dataset.progpuMediaExportAudioWorkletNodes =
          String(getBrowserMediaAudioWorkletNodeCreationCount());
        console.error(
          '[ProGPU] Browser media export smoke test failed.',
          error);
      }
    };
    if (exportSmoke === 'effect-audio') {
      const button = document.createElement('button');
      button.dataset.testid = 'progpu-media-export-audio-smoke';
      button.textContent = 'Run browser audio export smoke';
      button.style.position = 'fixed';
      button.style.right = '8px';
      button.style.top = '8px';
      button.style.zIndex = '100000';
      button.addEventListener(
        'click',
        () => void runExportSmoke(),
        { once: true });
      document.body.appendChild(button);
    } else {
      globalThis.setTimeout(
        () => void runExportSmoke(),
        1_000);
    }
  }
  await runtime.runMain();
}
