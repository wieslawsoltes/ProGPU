#include "progpu_native_browser.h"
#include "progpu_native_browser_evidence.hpp"
#include "progpu_native_semantic_backdrop_scene.hpp"

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
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUTexture render_texture = nullptr;
    WGPUTextureView render_view = nullptr;
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

[[noreturn]] void fail_engine(const char* message) {
    char detail[512]{};
    if (resources.engine != nullptr) {
        progpu_native_engine_get_last_error(
            resources.engine,
            detail,
            sizeof(detail));
    }
    std::fprintf(
        stderr,
        "ProGPU browser engine detail: %s\n",
        detail[0] == '\0' ? "unavailable" : detail);
    fail(message);
}

bool finish_evidence_frame(double, void*) {
    EM_ASM({
        document.body.dataset.progpuNative = "passed";
        document.body.dataset.progpuNativeSemanticCommands = "6";
        document.body.dataset.progpuNativeSemanticResources = "4";
        document.body.dataset.progpuNativeSemanticDraws = "6";
        document.body.dataset.progpuNativeRendererSubmissions = "1";
        document.body.dataset.progpuNativeEvidenceTarget =
            "offscreen-texture-readback";
        document.body.dataset.progpuNativeBackendAbi = "3";
        document.body.dataset.progpuNativeExplicitTimeline = "0";
        document.getElementById("native-status").textContent =
            "C++ / WebGPU semantic backend active — backdrop effect verified";
    });
    // The test page owns the offscreen texture until navigation releases the
    // WebAssembly instance and its Emdawnwebgpu handle table. The visible
    // canvas is populated only from the mapped test evidence.
    return false;
}

void finish_browser_evidence(bool success) {
    if (!success) {
        fail("The browser WebGPU evidence readback failed.");
    }
    if (emscripten_request_animation_frame(
            finish_evidence_frame,
            nullptr) < 0) {
        fail("The browser evidence completion frame could not be scheduled.");
    }
}

bool render_browser_frame(double, void*) {
    WGPUTexture render_texture = nullptr;
    WGPUTextureView render_view = nullptr;
    if (!progpu::native::browser::create_evidence_target(
            resources.device,
            WGPUTextureFormat_BGRA8Unorm,
            width,
            height,
            &render_texture,
            &render_view)) {
        fail("The browser WebGPU render target could not be created.");
    }

    progpu_native_scene_frame semantic_frame{};
    semantic_frame.struct_size = sizeof(semantic_frame);
    semantic_frame.width = width;
    semantic_frame.height = height;
    semantic_frame.dpi_scale = 1.0F;
    semantic_frame.target_view = reinterpret_cast<std::uintptr_t>(render_view);
    semantic_frame.clear_color = {0.01F, 0.015F, 0.03F, 1.0F};
    semantic_frame.scene_id = 98U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics semantic_metrics{};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &semantic_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        semantic_metrics.command_count != 6U ||
        semantic_metrics.draw_call_count != 6U ||
        semantic_metrics.submission_count != 1U ||
        semantic_metrics.brush_upload_bytes !=
            4U * sizeof(progpu_native_scene_brush) ||
        semantic_metrics.gradient_stop_upload_bytes !=
            3U * sizeof(progpu_native_scene_gradient_stop)) {
        fail_engine(
            "The ProGPU C++ browser semantic backdrop render failed.");
    }
    progpu_native_layer_metrics layer_metrics{};
    layer_metrics.struct_size = sizeof(layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &layer_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        layer_metrics.content_pass_count != 2U ||
        layer_metrics.composite_pass_count != 2U ||
        layer_metrics.effect_count != 1U ||
        layer_metrics.effect_pass_count != 2U ||
        layer_metrics.effect_cache_hit != 0U ||
        layer_metrics.texture_bytes == 0U) {
        fail("The ProGPU C++ browser semantic layer metrics are invalid.");
    }
    progpu_native_scene_frame_metrics stable_metrics{};
    stable_metrics.struct_size = sizeof(stable_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &stable_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        stable_metrics.brush_upload_bytes != 0U ||
        stable_metrics.gradient_stop_upload_bytes != 0U) {
        fail_engine(
            "The stable browser semantic brush page was uploaded again.");
    }
    resources.render_texture = render_texture;
    resources.render_view = render_view;
    if (!progpu::native::browser::begin_evidence_readback(
            resources.device,
            resources.queue,
            resources.render_texture,
            width,
            height,
            finish_browser_evidence)) {
        fail("The browser WebGPU evidence copy could not be scheduled.");
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
    if (queue == nullptr) {
        fail("The Emdawnwebgpu queue is unavailable.");
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

    auto semantic_scene =
        progpu::native::tests::create_semantic_backdrop_scene_stream(
            width,
            height);
    progpu_native_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    if (progpu_native_engine_update_scene(
            engine,
            semantic_scene.data(),
            semantic_scene.size(),
            &scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        scene_metrics.command_count != 6U ||
        scene_metrics.resource_count != 4U ||
        scene_metrics.draw_count != 2U ||
        scene_metrics.maximum_stack_depth != 1U) {
        fail("The ProGPU C++ browser semantic scene update failed.");
    }
    resources = {
        engine,
        device,
        queue,
        nullptr,
        nullptr};
    if (emscripten_request_animation_frame(
            render_browser_frame,
            nullptr) < 0) {
        fail("The browser render frame could not be scheduled.");
    }
    return 0;
}
