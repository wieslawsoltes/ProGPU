import {Scene, SceneBuilder, Path, scenePacket} from './scene.js';
export {Scene, SceneBuilder, Path};

const canvasOwners = new WeakSet();

// Construct this callback outside createRenderer's lexical environment. A
// pending borrowed-device promise must retain only the detachable token, not
// the factory's canvas, module, renderer methods or other closed-over state.
function deviceLossCallback(lifetime) {
    return (info) => lifetime.report?.(new Error(`WebGPU device lost (${info.reason}): ${info.message}`));
}

/** One isolated native module/renderer. No DOM work or GPU initialization occurs
 * on import. The application owns its animation loop and resize policy. */
export async function createRenderer({canvas, device: suppliedDevice, onError} = {}) {
    if (typeof HTMLCanvasElement === 'undefined' || !(canvas instanceof HTMLCanvasElement) ||
        !canvas.isConnected || canvas.ownerDocument !== document)
        throw new TypeError('canvas must be a connected HTMLCanvasElement in the current document');
    if (!globalThis.navigator?.gpu) throw new Error('WebGPU is unavailable');
    if (canvasOwners.has(canvas) || canvas.hasAttribute('data-progpu-renderer'))
        throw new Error('The canvas already has a ProGPU renderer');
    if (onError !== undefined && typeof onError !== 'function') throw new TypeError('onError must be a function');

    canvasOwners.add(canvas);
    const marker = crypto.randomUUID();
    canvas.setAttribute('data-progpu-renderer', marker);
    let device = suppliedDevice, module, disposed = false, initialized = false;
    let failure = null, listener = null, metricsPointer = 0, errorPointer = 0;
    let currentScene = null, updateMetrics = null;
    const lifetime = {report: null};
    function report(error) {
        if (disposed || failure) return;
        failure = error;
        if (initialized) module._progpu_browser_device_lost();
        if (onError) onError(error);
    }
    lifetime.report = report;
    function check() {
        if (disposed) throw new Error('Renderer is disposed');
        if (failure) throw failure;
        if (!canvas.isConnected || canvas.getAttribute('data-progpu-renderer') !== marker)
            throw new Error('The renderer canvas was removed or its ownership marker changed');
    }
    function nativeCheck(success) {
        if (success) return;
        const heap = module.HEAPU8;
        let end = errorPointer;
        while (end < errorPointer + 1024 && heap[end] !== 0) ++end;
        throw new Error(new TextDecoder().decode(heap.subarray(errorPointer, end)) || 'Native renderer operation failed');
    }
    function withBytes(bytes, call) {
        const pointer = module._malloc(bytes.byteLength);
        if (!pointer) throw new Error('Native transport allocation failed');
        try { module.HEAPU8.set(bytes, pointer); return call(pointer); }
        finally { module._free(pointer); }
    }
    function word(index) { return new DataView(module.HEAPU8.buffer).getBigUint64(metricsPointer + index * 8, true); }
    function count(index) { return Number(word(index)); }
    function resize({width, height, pixelRatio = Math.max(1, Math.min(4, globalThis.devicePixelRatio || 1))}) {
        check();
        for (const value of [width, height, pixelRatio])
            if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) throw new RangeError('Canvas dimensions and pixel ratio must be positive and finite');
        if (pixelRatio < 1 || pixelRatio > 4) throw new RangeError('The native browser host supports pixel ratios from one to four');
        const physicalWidth = Math.max(1, Math.round(width * pixelRatio));
        const physicalHeight = Math.max(1, Math.round(height * pixelRatio));
        if (physicalWidth > device.limits.maxTextureDimension2D || physicalHeight > device.limits.maxTextureDimension2D)
            throw new RangeError('Canvas size exceeds this device maxTextureDimension2D');
        const metrics = Object.freeze({width: physicalWidth, height: physicalHeight,
            logicalWidth: width, logicalHeight: height, scale: pixelRatio});
        // Original browser host contract: physical texture dimensions plus a
        // separate logical extent/DPI. Resizing never recompiles the scene.
        if (canvas.width !== physicalWidth) canvas.width = physicalWidth;
        if (canvas.height !== physicalHeight) canvas.height = physicalHeight;
        module.progpuBrowserMetrics = metrics;
        return metrics;
    }
    function dispose() {
        if (disposed) return;
        disposed = true; lifetime.report = null;
        if (listener && device) device.removeEventListener('uncapturederror', listener);
        try { if (initialized) { module._progpu_browser_dispose(); initialized = false; } }
        finally {
            if (!suppliedDevice && device) device.destroy();
            if (canvas.getAttribute('data-progpu-renderer') === marker) canvas.removeAttribute('data-progpu-renderer');
            canvasOwners.delete(canvas); currentScene = null; updateMetrics = null;
            // Break the retained promise/listener chain without touching a
            // borrowed device or leaving a live callback into freed wasm state.
            module = null;
        }
    }
    try {
        if (!device) {
            const adapter = await navigator.gpu.requestAdapter({powerPreference: 'high-performance'});
            if (!adapter) throw new Error('No WebGPU adapter is available');
            device = await adapter.requestDevice();
        }
        if (typeof device.addEventListener !== 'function' || typeof device.queue?.submit !== 'function')
            throw new TypeError('device must be a GPUDevice');
        listener = (event) => lifetime.report?.(new Error(`WebGPU: ${event.error.message}`));
        device.addEventListener('uncapturederror', listener);
        // The promise captures only the detachable lifetime token, not the
        // renderer/module/device. A borrowed device may long outlive dispose.
        device.lost.then(deviceLossCallback(lifetime));
        const format = navigator.gpu.getPreferredCanvasFormat();
        if (format !== 'rgba8unorm' && format !== 'bgra8unorm') throw new Error(`Unsupported native canvas format: ${format}`);
        const {default: factory} = await import('./progpu-native.mjs');
        module = await factory({canvas, preinitializedWebGPUDevice: device,
            progpuBrowserCanvasFormat: format});
        const rect = canvas.getBoundingClientRect();
        resize({width: Math.max(1, rect.width), height: Math.max(1, rect.height)});
        check();
        const selector = new TextEncoder().encode(`canvas[data-progpu-renderer="${marker}"]\0`);
        errorPointer = module._progpu_browser_error();
        withBytes(selector, (pointer) => nativeCheck(module._progpu_browser_initialize(pointer)));
        initialized = true; metricsPointer = module._progpu_browser_metrics();
        check();
        return Object.freeze({
            device,
            get error() { return failure; },
            resize,
            updateScene(scene) {
                check();
                const packet = scenePacket(scene);
                if (!packet && !(scene instanceof Uint8Array)) throw new TypeError('Expected an immutable Scene or full native stream Uint8Array');
                // A repeated immutable snapshot needs no compilation/crossing.
                // Do not cache caller-owned raw byte arrays by object identity.
                if (packet && scene === currentScene) return updateMetrics;
                const bytes = packet ?? scene;
                if (bytes.byteLength === 0) throw new RangeError('Scene stream must not be empty');
                withBytes(bytes, (pointer) => nativeCheck(module._progpu_browser_update(pointer, bytes.byteLength, packet ? 1 : 0)));
                currentScene = packet ? scene : null;
                updateMetrics = Object.freeze({sceneId: word(0), generation: word(1), commandCount: count(2),
                    resourceCount: count(3), streamBytes: count(4), snapshotReused: word(5) !== 0n,
                    drawCount: count(6), payloadBytes: count(7)});
                return updateMetrics;
            },
            getSceneStream() {
                check();
                const size = module._progpu_browser_stream_size();
                if (!size) throw new Error('Update a scene before exporting its native stream');
                const pointer = module._progpu_browser_stream();
                return module.HEAPU8.slice(pointer, pointer + size);
            },
            render({clearColor = [0, 0, 0, 0]} = {}) {
                check();
                if (!clearColor || clearColor.length !== 4 || Array.from(clearColor).some((v) => typeof v !== 'number' || !Number.isFinite(v) || v < 0 || v > 1))
                    throw new TypeError('clearColor must contain four finite zero-to-one components');
                nativeCheck(module._progpu_browser_render(...clearColor));
                return Object.freeze({commandCount: count(0), drawCallCount: count(1), familySwitchCount: count(2),
                    submissionCount: word(3), vertexUploadBytes: count(4), indexUploadBytes: count(5),
                    textureUploadBytes: count(6), uniformUploadBytes: count(7), coverageStagingBytes: count(8),
                    payloadHash: word(9), brushUploadBytes: count(10), gradientStopUploadBytes: count(11),
                    textStyleUploadBytes: count(12), colorGlyphUploadBytes: count(13)});
            },
            dispose,
        });
    } catch (error) { dispose(); throw error; }
}
