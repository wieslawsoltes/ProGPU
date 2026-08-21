Module.preRun = Module.preRun || [];
Module.preRun.push(function () {
  const dependency = 'progpu-native-gallery-webgpu';
  addRunDependency(dependency);
  import('./progpu-browser-host.js')
    .then(async host => {
      const canvas = document.querySelector('#progpu-canvas');
      if (!(canvas instanceof HTMLCanvasElement)) {
        throw new Error('The ProGPU native gallery canvas is unavailable.');
      }
      Module.progpuBrowserMetrics = host.resizePhysicalCanvas(canvas, 4);
      Module.progpuBrowserDisposeResize = host.installResponsiveCanvas(
        canvas,
        metrics => {
          Module.progpuBrowserMetrics = metrics;
        },
        4);
      const webGpu = await host.requestProGpuWebGpuDevice({
        powerPreference: 'high-performance',
        gpuProfile: 'Full',
        onUncapturedError: detail => {
          console.error(`[ProGPU] WebGPU validation error: ${detail}`);
          document.body.dataset.progpuNativeError = detail;
        },
        onDeviceLost: info => {
          const detail = `${info.reason}: ${info.message}`;
          console.error(`[ProGPU] WebGPU device lost: ${detail}`);
          document.body.dataset.progpuNative = 'device-lost';
          document.body.dataset.progpuNativeError = detail;
        }
      });
      Module.preinitializedWebGPUDevice = webGpu.device;
      Module.progpuBrowserCanvasFormat = webGpu.format;
      document.body.dataset.progpuNativeProfile = webGpu.activeProfile;
      removeRunDependency(dependency);
    })
    .catch(error => {
      document.body.dataset.progpuNative = 'failed';
      document.body.dataset.progpuNativeError = String(error);
      document.querySelector('#status-message').textContent = String(error);
      removeRunDependency(dependency);
    });
});
