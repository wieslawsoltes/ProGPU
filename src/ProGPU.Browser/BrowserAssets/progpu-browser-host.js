// Shared browser host contract for the managed and pure C++ ProGPU runtimes.
// It owns only DOM/WebGPU platform integration; scene construction, rendering,
// and presentation remain in the selected managed or native engine.

export function measurePhysicalCanvas(
  canvas, maximumScale = Number.POSITIVE_INFINITY) {
  const scale = Math.max(1, Math.min(maximumScale,
    Number(globalThis.devicePixelRatio) || 1));
  const rect = canvas.getBoundingClientRect();
  return Object.freeze({
    width: Math.max(1, Math.round(rect.width * scale)),
    height: Math.max(1, Math.round(rect.height * scale)),
    scale,
    logicalWidth: Math.max(1, rect.width),
    logicalHeight: Math.max(1, rect.height)
  });
}

export function resizePhysicalCanvas(
  canvas, maximumScale = Number.POSITIVE_INFINITY) {
  const metrics = measurePhysicalCanvas(canvas, maximumScale);
  const changed = canvas.width !== metrics.width ||
    canvas.height !== metrics.height;
  if (changed) {
    canvas.width = metrics.width;
    canvas.height = metrics.height;
  }
  return Object.freeze({ ...metrics, changed });
}

export function updateProGpuVisualViewport() {
  const viewport = globalThis.visualViewport;
  const keyboardRect = navigator.virtualKeyboard?.boundingRect;
  const keyboardInset = keyboardRect && keyboardRect.height > 0
    ? keyboardRect.height
    : 0;
  const rootStyle = document.documentElement.style;
  rootStyle.setProperty('--progpu-viewport-left',
    `${viewport?.offsetLeft || 0}px`);
  rootStyle.setProperty('--progpu-viewport-top',
    `${viewport?.offsetTop || 0}px`);
  rootStyle.setProperty('--progpu-viewport-width',
    `${viewport?.width || globalThis.innerWidth}px`);
  rootStyle.setProperty('--progpu-viewport-height',
    `${viewport?.height || globalThis.innerHeight}px`);
  rootStyle.setProperty('--progpu-keyboard-inset', `${keyboardInset}px`);
}

export function installResponsiveCanvas(canvas, onResize,
  maximumScale = Number.POSITIVE_INFINITY) {
  const update = () => {
    updateProGpuVisualViewport();
    const metrics = resizePhysicalCanvas(canvas, maximumScale);
    onResize?.(metrics);
  };
  const observer = new ResizeObserver(update);
  observer.observe(canvas);
  globalThis.visualViewport?.addEventListener('resize', update);
  globalThis.visualViewport?.addEventListener('scroll', update);
  globalThis.addEventListener('orientationchange', update);
  navigator.virtualKeyboard?.addEventListener('geometrychange', update);
  update();
  return () => {
    observer.disconnect();
    globalThis.visualViewport?.removeEventListener('resize', update);
    globalThis.visualViewport?.removeEventListener('scroll', update);
    globalThis.removeEventListener('orientationchange', update);
    navigator.virtualKeyboard?.removeEventListener('geometrychange', update);
  };
}

export async function requestProGpuWebGpuDevice(options = {}) {
  if (!globalThis.navigator?.gpu) {
    throw new Error(
      'navigator.gpu is unavailable. Enable WebGPU or use a current browser.');
  }
  const adapter = await navigator.gpu.requestAdapter({
    powerPreference: options.powerPreference || 'high-performance'
  });
  if (!adapter) {
    throw new Error('No WebGPU adapter matched the requested power preference.');
  }

  const supportsBgra8UnormStorage =
    adapter.features.has('bgra8unorm-storage');
  const wantsFullProfile = options.gpuProfile === 'Full';
  const requiredFeatures = wantsFullProfile && supportsBgra8UnormStorage
    ? ['bgra8unorm-storage']
    : [];
  const device = await adapter.requestDevice({ requiredFeatures });
  device.addEventListener('uncapturederror', event => {
    const detail = String(event.error?.message || event.error);
    options.onUncapturedError?.(detail);
  });
  device.lost.then(info => {
    if (info.reason === 'destroyed') {
      options.onDeviceDestroyed?.(info);
      return;
    }
    options.onDeviceLost?.(info);
  });

  return Object.freeze({
    adapter,
    device,
    format: navigator.gpu.getPreferredCanvasFormat(),
    supportsBgra8UnormStorage,
    activeProfile: wantsFullProfile && supportsBgra8UnormStorage
      ? 'Full'
      : 'Portable'
  });
}
