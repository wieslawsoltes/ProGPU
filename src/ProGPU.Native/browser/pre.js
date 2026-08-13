Module.preRun = Module.preRun || [];
Module.preRun.push(function () {
  const dependency = "progpu-native-webgpu-device";
  let dependencyReleased = false;
  const releaseDependency = () => {
    if (!dependencyReleased) {
      dependencyReleased = true;
      removeRunDependency(dependency);
    }
  };
  addRunDependency(dependency);
  navigator.gpu.requestAdapter({ powerPreference: "high-performance" })
    .then((adapter) => {
      if (!adapter) {
        throw new Error("No browser WebGPU adapter is available.");
      }
      return adapter.requestDevice();
    })
    .then((device) => {
      device.addEventListener("uncapturederror", (event) => {
        console.error(`WebGPU validation error: ${event.error.message}`);
      });
      device.lost.then((info) => {
        if (info.reason !== "destroyed") {
          console.error(
            `WebGPU device lost (${info.reason}): ${info.message}`);
        }
      });
      Module.preinitializedWebGPUDevice = device;
      releaseDependency();
    })
    .catch((error) => {
      document.body.dataset.progpuNative = "failed";
      document.body.dataset.progpuNativeError = String(error);
      releaseDependency();
    });
});
