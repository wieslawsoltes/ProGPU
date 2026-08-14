#include "progpu_native_browser.h"
#include "progpu_native_browser_evidence.hpp"
#include "progpu_native_semantic_backdrop_scene.hpp"
#include "progpu_native_semantic_color_glyph_scene.hpp"
#include "progpu_native_semantic_coverage_mask_scene.hpp"
#include "progpu_native_semantic_geometry_scene.hpp"
#include "progpu_native_semantic_image_scene.hpp"
#include "progpu_native_semantic_text_scene.hpp"

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
        document.body.dataset.progpuNativeSemanticCommands = "3";
        document.body.dataset.progpuNativeSemanticResources = "2";
        document.body.dataset.progpuNativeSemanticDraws = "2";
        document.body.dataset.progpuNativeRendererSubmissions = "1";
        document.body.dataset.progpuNativeRetainedTextStyles = "passed";
        document.body.dataset.progpuNativeColorGlyphAtlas = "passed";
        document.body.dataset.progpuNativeCubicImages = "passed";
        document.body.dataset.progpuNativeCoverageMasks = "passed";
        document.body.dataset.progpuNativeSemanticGeometry = "passed";
        document.body.dataset.progpuNativeDeviceRecovery = "passed";
        document.body.dataset.progpuNativeEvidenceTarget =
            "offscreen-texture-readback";
        document.body.dataset.progpuNativeBackendAbi = "3";
        document.body.dataset.progpuNativeExplicitTimeline = "0";
        document.getElementById("native-status").textContent =
            "C++ / WebGPU semantic backend active — retained coverage mask verified";
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
    auto geometry_scene =
        progpu::native::tests::create_semantic_geometry_scene_stream(
            width,
            height);
    progpu_native_scene_metrics geometry_scene_metrics{};
    geometry_scene_metrics.struct_size = sizeof(geometry_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            geometry_scene.data(),
            geometry_scene.size(),
            &geometry_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        geometry_scene_metrics.command_count != 1U ||
        geometry_scene_metrics.resource_count != 3U ||
        geometry_scene_metrics.draw_count != 1U) {
        fail_engine("The browser retained geometry scene update failed.");
    }
    semantic_frame.scene_id = 101U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics geometry_metrics{};
    geometry_metrics.struct_size = sizeof(geometry_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &geometry_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        geometry_metrics.command_count != 1U ||
        geometry_metrics.draw_call_count != 1U ||
        geometry_metrics.submission_count != 1U ||
        geometry_metrics.brush_upload_bytes !=
            2U * sizeof(progpu_native_scene_brush) ||
        geometry_metrics.vertex_upload_bytes == 0U) {
        fail_engine("The browser retained geometry render failed.");
    }
    geometry_metrics = {};
    geometry_metrics.struct_size = sizeof(geometry_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &geometry_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        geometry_metrics.brush_upload_bytes != 0U ||
        geometry_metrics.vertex_upload_bytes != 0U ||
        geometry_metrics.index_upload_bytes != 0U) {
        fail_engine("The stable browser geometry page was rebuilt.");
    }

    auto color_glyph_scene =
        progpu::native::tests::create_semantic_color_glyph_scene_stream(
            width,
            height);
    progpu_native_scene_metrics color_scene_metrics{};
    color_scene_metrics.struct_size = sizeof(color_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            color_glyph_scene.data(),
            color_glyph_scene.size(),
            &color_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        color_scene_metrics.command_count != 3U ||
        color_scene_metrics.resource_count != 5U ||
        color_scene_metrics.draw_count != 3U) {
        fail_engine(
            "The ProGPU C++ browser color-glyph scene update failed.");
    }
    semantic_frame.scene_id = 97U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics color_metrics{};
    color_metrics.struct_size = sizeof(color_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &color_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        color_metrics.command_count != 3U ||
        color_metrics.draw_call_count != 3U ||
        color_metrics.submission_count != 1U ||
        color_metrics.color_glyph_upload_bytes != 16U) {
        fail_engine(
            "The ProGPU C++ browser color-glyph render failed.");
    }
    color_metrics = {};
    color_metrics.struct_size = sizeof(color_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &color_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        color_metrics.color_glyph_upload_bytes != 0U ||
        color_metrics.vertex_upload_bytes != 0U ||
        color_metrics.coverage_staging_bytes != 0U) {
        fail_engine(
            "The stable browser color-glyph page was rebuilt.");
    }

    auto cubic_image_scene =
        progpu::native::tests::create_semantic_cubic_image_scene_stream(
            width,
            height);
    progpu_native_scene_metrics image_scene_metrics{};
    image_scene_metrics.struct_size = sizeof(image_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            cubic_image_scene.data(),
            cubic_image_scene.size(),
            &image_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        image_scene_metrics.command_count != 1U ||
        image_scene_metrics.resource_count != 1U ||
        image_scene_metrics.draw_count != 1U) {
        fail_engine("The browser cubic image scene update failed.");
    }
    semantic_frame.scene_id = 96U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics image_metrics{};
    image_metrics.struct_size = sizeof(image_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &image_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        image_metrics.draw_call_count != 1U ||
        image_metrics.submission_count != 1U ||
        image_metrics.texture_upload_bytes != 16U ||
        image_metrics.uniform_upload_bytes <
            sizeof(progpu_native_scene_image_color_matrix)) {
        fail_engine("The browser cubic image render failed.");
    }
    image_metrics = {};
    image_metrics.struct_size = sizeof(image_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &image_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        image_metrics.texture_upload_bytes != 0U ||
        image_metrics.vertex_upload_bytes != 0U ||
        image_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser cubic image page was rebuilt.");
    }

    auto text_scene =
        progpu::native::tests::create_semantic_text_scene_stream(
            width,
            height);
    progpu_native_scene_metrics text_scene_metrics{};
    text_scene_metrics.struct_size = sizeof(text_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            text_scene.data(),
            text_scene.size(),
            &text_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        text_scene_metrics.command_count != 1U ||
        text_scene_metrics.resource_count != 2U ||
        text_scene_metrics.draw_count != 1U) {
        fail_engine(
            "The ProGPU C++ browser retained text scene update failed.");
    }
    semantic_frame.scene_id = 99U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics text_metrics{};
    text_metrics.struct_size = sizeof(text_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &text_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        text_metrics.command_count != 1U ||
        text_metrics.draw_call_count != 1U ||
        text_metrics.submission_count != 1U ||
        text_metrics.text_style_upload_bytes !=
            2U * sizeof(progpu_native_scene_text_style)) {
        fail_engine(
            "The ProGPU C++ browser retained text render failed.");
    }
    text_metrics = {};
    text_metrics.struct_size = sizeof(text_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &text_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        text_metrics.text_style_upload_bytes != 0U ||
        text_metrics.vertex_upload_bytes != 0U ||
        text_metrics.coverage_staging_bytes != 0U) {
        fail_engine(
            "The stable browser retained text page was rebuilt.");
    }

    auto backdrop_scene =
        progpu::native::tests::create_semantic_backdrop_scene_stream(
            width,
            height);
    progpu_native_scene_metrics backdrop_scene_metrics{};
    backdrop_scene_metrics.struct_size = sizeof(backdrop_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            backdrop_scene.data(),
            backdrop_scene.size(),
            &backdrop_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS) {
        fail_engine(
            "The ProGPU C++ browser backdrop scene restore failed.");
    }
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

    auto coverage_scene =
        progpu::native::tests::create_semantic_coverage_mask_scene_stream(
            width,
            height);
    progpu_native_scene_metrics coverage_scene_metrics{};
    coverage_scene_metrics.struct_size = sizeof(coverage_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            coverage_scene.data(),
            coverage_scene.size(),
            &coverage_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_scene_metrics.command_count != 3U ||
        coverage_scene_metrics.resource_count != 2U ||
        coverage_scene_metrics.draw_count != 1U) {
        fail_engine("The browser coverage-mask scene update failed.");
    }
    semantic_frame.scene_id = 100U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics coverage_metrics{};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.command_count != 3U ||
        coverage_metrics.draw_call_count != 2U ||
        coverage_metrics.submission_count != 1U ||
        coverage_metrics.texture_upload_bytes != 64U ||
        coverage_metrics.uniform_upload_bytes <
            24U * sizeof(float)) {
        fail_engine("The browser coverage-mask render failed.");
    }
    coverage_metrics = {};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.texture_upload_bytes != 0U ||
        coverage_metrics.vertex_upload_bytes != 0U ||
        coverage_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser coverage mask was rebuilt.");
    }

    if (progpu_native_engine_mark_device_lost(resources.engine) !=
            PROGPU_NATIVE_STATUS_SUCCESS ||
        progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_DEVICE_LOST) {
        fail_engine("The browser device-loss gate did not fail closed.");
    }
    progpu_native_browser_engine_options replacement_options{};
    replacement_options.struct_size = sizeof(replacement_options);
    replacement_options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    replacement_options.adapter_abi_version =
        PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
    replacement_options.target_format =
        PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    replacement_options.device =
        reinterpret_cast<std::uintptr_t>(resources.device);
    replacement_options.queue =
        reinterpret_cast<std::uintptr_t>(resources.queue);
    progpu_native_engine* replacement = nullptr;
    if (progpu_native_browser_engine_recreate(
            resources.engine,
            &replacement_options,
            &replacement) != PROGPU_NATIVE_STATUS_SUCCESS ||
        replacement == nullptr) {
        fail_engine("The browser engine could not be recreated.");
    }
    progpu_native_engine_destroy(resources.engine);
    resources.engine = replacement;
    coverage_metrics = {};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.texture_upload_bytes != 64U ||
        coverage_metrics.vertex_upload_bytes == 0U ||
        coverage_metrics.uniform_upload_bytes == 0U) {
        fail_engine("The recreated browser engine did not rebuild the retained scene.");
    }
    coverage_metrics = {};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.texture_upload_bytes != 0U ||
        coverage_metrics.vertex_upload_bytes != 0U ||
        coverage_metrics.uniform_upload_bytes != 0U) {
        fail_engine("Stable browser replay after device recovery rebuilt resources.");
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
            PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_TEXT_STYLES) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_SEMANTIC_GEOMETRY_BATCH) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION) == 0U ||
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
