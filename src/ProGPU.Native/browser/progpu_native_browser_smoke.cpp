#include "progpu_native_browser.h"

#include <emscripten.h>
#include <emscripten/html5.h>
#include <webgpu/webgpu.h>

#include <array>
#include <cstdint>
#include <cstdio>

namespace {

constexpr std::uint32_t width = 640U;
constexpr std::uint32_t height = 360U;

struct browser_resources final {
    progpu_native_engine* engine = nullptr;
    WGPUInstance instance = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUSurface surface = nullptr;
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
};

browser_resources resources{};

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr, "ProGPU browser smoke failed: %s\n", message);
    EM_ASM({
        document.body.dataset.progpuNative = "failed";
        document.body.dataset.progpuNativeError =
            "The native C++ browser smoke failed; see the console.";
    });
    emscripten_force_exit(1);
}

bool finish_browser_frame(double, void*) {
    EM_ASM({
        document.body.dataset.progpuNative = "passed";
        document.body.dataset.progpuNativeDraws = "1";
        document.body.dataset.progpuNativeVertices = "24";
        document.body.dataset.progpuNativeSubmissions = "1";
        document.body.dataset.progpuNativeBackendAbi = "3";
        document.body.dataset.progpuNativeExplicitTimeline = "0";
        document.getElementById("native-status").textContent =
            "C++ / WebGPU backend active — 1 draw, 24 vertices, GPU submitted";
    });
    // A browser host owns its canvas resource domain for the page lifetime.
    // Keeping these handles alive also lets the browser composite the frame
    // after this requestAnimationFrame callback returns. Navigation releases
    // the WebAssembly instance and the associated Emdawnwebgpu handle table.
    return false;
}

} // namespace

int main() {
    progpu_native_engine_info info{};
    info.struct_size = sizeof(info);
    if (progpu_native_get_info(&info) == 0U ||
        info.abi_version != PROGPU_NATIVE_ABI_VERSION ||
        info.backend_abi !=
            PROGPU_NATIVE_BACKEND_ABI_BROWSER_WEBGPU_2025_10 ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE) != 0U) {
        fail("The ProGPU browser ABI/capability contract is invalid.");
    }

    WGPUDevice device = emscripten_webgpu_get_device();
    if (device == nullptr) {
        fail("The browser did not provide a WebGPU device.");
    }
    WGPUQueue queue = wgpuDeviceGetQueue(device);
    WGPUInstanceDescriptor instance_descriptor = WGPU_INSTANCE_DESCRIPTOR_INIT;
    WGPUInstance instance = wgpuCreateInstance(&instance_descriptor);
    if (instance == nullptr || queue == nullptr) {
        fail("The Emdawnwebgpu instance or queue is unavailable.");
    }

    WGPUEmscriptenSurfaceSourceCanvasHTMLSelector canvas_source =
        WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
    canvas_source.selector = WGPUStringView{"#progpu-native-canvas", 21U};
    WGPUSurfaceDescriptor surface_descriptor = WGPU_SURFACE_DESCRIPTOR_INIT;
    surface_descriptor.nextInChain = &canvas_source.chain;
    WGPUSurface surface = wgpuInstanceCreateSurface(
        instance,
        &surface_descriptor);
    if (surface == nullptr) {
        fail("The browser WebGPU canvas surface could not be created.");
    }
    const WGPUTextureFormat target_format =
        WGPUTextureFormat_BGRA8Unorm;
    WGPUSurfaceConfiguration configuration =
        WGPU_SURFACE_CONFIGURATION_INIT;
    configuration.device = device;
    configuration.format = target_format;
    configuration.usage = WGPUTextureUsage_RenderAttachment;
    configuration.width = width;
    configuration.height = height;
    configuration.presentMode = WGPUPresentMode_Fifo;
    configuration.alphaMode = WGPUCompositeAlphaMode_Opaque;
    wgpuSurfaceConfigure(surface, &configuration);

    WGPUSurfaceTexture surface_texture = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(surface, &surface_texture);
    if (surface_texture.texture == nullptr ||
        (surface_texture.status !=
            WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal &&
         surface_texture.status !=
            WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal)) {
        fail("The browser WebGPU surface texture is unavailable.");
    }
    WGPUTextureView view = wgpuTextureCreateView(
        surface_texture.texture,
        nullptr);
    if (view == nullptr) {
        fail("The browser WebGPU target view could not be created.");
    }

    progpu_native_browser_engine_options options{};
    options.struct_size = sizeof(options);
    options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.adapter_abi_version =
        PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    options.device = reinterpret_cast<std::uintptr_t>(device);
    options.queue = reinterpret_cast<std::uintptr_t>(queue);
    progpu_native_engine* engine = nullptr;
    if (progpu_native_browser_engine_create(&options, &engine) !=
            PROGPU_NATIVE_STATUS_SUCCESS ||
        engine == nullptr) {
        fail("The ProGPU C++ browser engine could not be created.");
    }

    const std::array<progpu_native_rect, 4U> rectangles{{
        {0.0F, 0.0F, 640.0F, 360.0F, {0.025F, 0.035F, 0.07F, 1.0F}},
        {48.0F, 48.0F, 544.0F, 264.0F, {0.03F, 0.12F, 0.20F, 1.0F}},
        {82.0F, 92.0F, 210.0F, 156.0F, {0.0F, 0.55F, 0.95F, 1.0F}},
        {318.0F, 92.0F, 240.0F, 156.0F, {0.15F, 0.85F, 0.55F, 1.0F}}
    }};
    progpu_native_frame frame{};
    frame.struct_size = sizeof(frame);
    frame.width = width;
    frame.height = height;
    frame.dpi_scale = 1.0F;
    frame.target_view = reinterpret_cast<std::uintptr_t>(view);
    frame.clear_color = {0.01F, 0.015F, 0.03F, 1.0F};
    frame.rects = rectangles.data();
    frame.rect_count = rectangles.size();
    progpu_native_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    const auto status = progpu_native_engine_render(
        engine,
        &frame,
        &metrics);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS ||
        metrics.draw_call_count != 1U || metrics.vertex_count != 24U ||
        metrics.submission_count != 1U) {
        std::array<char, 512U> error{};
        progpu_native_engine_get_last_error(
            engine,
            error.data(),
            error.size());
        std::fprintf(stderr,
            "browser frame status=%u draws=%u vertices=%u submissions=%llu error=%s\n",
            static_cast<unsigned>(status),
            metrics.draw_call_count,
            metrics.vertex_count,
            static_cast<unsigned long long>(metrics.submission_count),
            error.data());
        fail("The ProGPU C++ browser frame contract failed.");
    }
    resources = {
        engine,
        instance,
        device,
        queue,
        surface,
        surface_texture.texture,
        view};
    if (emscripten_request_animation_frame(
            finish_browser_frame,
            nullptr) < 0) {
        fail("The browser presentation frame could not be scheduled.");
    }
    return 0;
}
