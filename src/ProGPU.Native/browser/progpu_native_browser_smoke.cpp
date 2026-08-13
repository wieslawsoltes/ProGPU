#include "progpu_native_browser.h"
#include "progpu_native_semantic_advanced_blend_scene.hpp"

#include <emscripten.h>
#include <emscripten/html5.h>
#include <webgpu/webgpu.h>

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
        document.body.dataset.progpuNativeSemanticCommands = "4";
        document.body.dataset.progpuNativeSemanticResources = "2";
        document.body.dataset.progpuNativeSemanticDraws = "3";
        document.body.dataset.progpuNativeTotalSubmissions = "1";
        document.body.dataset.progpuNativeBackendAbi = "3";
        document.body.dataset.progpuNativeExplicitTimeline = "0";
        document.getElementById("native-status").textContent =
            "C++ / WebGPU semantic backend active — isolated layer submitted";
    });
    // A browser host owns its canvas resource domain for the page lifetime.
    // Keeping these handles alive also lets the browser composite the frame
    // after this requestAnimationFrame callback returns. Navigation releases
    // the WebAssembly instance and the associated Emdawnwebgpu handle table.
    return false;
}

bool render_browser_frame(double, void*) {
    WGPUSurfaceTexture surface_texture = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(resources.surface, &surface_texture);
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

    progpu_native_scene_frame semantic_frame{};
    semantic_frame.struct_size = sizeof(semantic_frame);
    semantic_frame.width = width;
    semantic_frame.height = height;
    semantic_frame.dpi_scale = 1.0F;
    semantic_frame.target_view = reinterpret_cast<std::uintptr_t>(view);
    semantic_frame.clear_color = {0.01F, 0.015F, 0.03F, 1.0F};
    semantic_frame.scene_id = 97U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics semantic_metrics{};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &semantic_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        semantic_metrics.command_count != 4U ||
        semantic_metrics.draw_call_count != 3U ||
        semantic_metrics.submission_count != 1U) {
        fail("The ProGPU C++ browser semantic blend render failed.");
    }
    progpu_native_layer_metrics layer_metrics{};
    layer_metrics.struct_size = sizeof(layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &layer_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        layer_metrics.content_pass_count != 1U ||
        layer_metrics.composite_pass_count != 1U ||
        layer_metrics.texture_bytes == 0U) {
        fail("The ProGPU C++ browser semantic layer metrics are invalid.");
    }
    resources.texture = surface_texture.texture;
    resources.view = view;
    if (emscripten_request_animation_frame(
            finish_browser_frame,
            nullptr) < 0) {
        fail("The browser presentation completion frame could not be scheduled.");
    }
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

    auto semantic_scene =
        progpu::native::tests::create_semantic_advanced_blend_scene_stream(
            width,
            height,
            PROGPU_NATIVE_BLEND_SRC_OVER);
    progpu_native_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    if (progpu_native_engine_update_scene(
            engine,
            semantic_scene.data(),
            semantic_scene.size(),
            &scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        scene_metrics.command_count != 4U ||
        scene_metrics.resource_count != 2U ||
        scene_metrics.draw_count != 2U ||
        scene_metrics.maximum_stack_depth != 1U) {
        fail("The ProGPU C++ browser semantic scene update failed.");
    }
    resources = {
        engine,
        instance,
        device,
        queue,
        surface,
        nullptr,
        nullptr};
    if (emscripten_request_animation_frame(
            render_browser_frame,
            nullptr) < 0) {
        fail("The browser presentation frame could not be scheduled.");
    }
    return 0;
}
