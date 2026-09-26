#include "scene_packet.hpp"
#include "Host/progpu_native_browser_window_host.hpp"
#include "progpu_native_browser.h"

#include <emscripten.h>
#include <emscripten/html5.h>

#include <array>
#include <cmath>
#include <cstdio>
#include <memory>
#include <vector>

namespace {

// A modularized wasm instance owns precisely one engine/surface. The imported
// C WebGPU handles belong to this module's emdawn table, never another module
// or a numeric cast of a JavaScript GPU object. Releasing them does not call
// GPUDevice.destroy(): caller-supplied devices remain borrowed.
struct renderer final {
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    std::unique_ptr<progpu::native::browser::browser_window_host> host;
    progpu_native_engine* engine = nullptr;
    std::vector<std::byte> stream;
    std::uint64_t scene_id = 0U;
    std::uint64_t generation = 0U;
    std::array<std::uint64_t, 16U> metrics{};
    ~renderer() {
        if (engine != nullptr) progpu_native_engine_destroy(engine);
        host.reset();
        if (queue != nullptr) wgpuQueueRelease(queue);
        if (device != nullptr) wgpuDeviceRelease(device);
    }
};

std::unique_ptr<renderer> active;
std::array<char, 1024U> error{};

int fail(const char* message) noexcept {
    std::snprintf(error.data(), error.size(), "%s", message);
    return 0;
}
int native_failure(const char* message) noexcept {
    fail(message);
    if (active && active->engine) {
        std::array<char, 768U> detail{};
        progpu_native_engine_get_last_error(active->engine, detail.data(), detail.size());
        if (detail[0] != '\0') std::snprintf(error.data(), error.size(), "%s: %s", message, detail.data());
    }
    return 0;
}

} // namespace

extern "C" {

EMSCRIPTEN_KEEPALIVE int progpu_browser_initialize(const char* selector) noexcept {
    if (active) return fail("This module already owns a renderer");
    try {
        auto candidate = std::make_unique<renderer>();
        candidate->device = emscripten_webgpu_get_device();
        if (!candidate->device) return fail("No preinitialized WebGPU device was imported");
        candidate->host = std::make_unique<progpu::native::browser::browser_window_host>();
        if (!candidate->host->initialize(candidate->device, selector)) return fail("WebGPU canvas surface initialization failed");
        candidate->queue = wgpuDeviceGetQueue(candidate->device);
        if (!candidate->queue) return fail("WebGPU queue import failed");
        progpu_native_browser_engine_options options{};
        options.struct_size = sizeof(options);
        options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
        options.adapter_abi_version = PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
        options.target_format = candidate->host->native_format();
        options.device = reinterpret_cast<std::uintptr_t>(candidate->device);
        options.queue = reinterpret_cast<std::uintptr_t>(candidate->queue);
        if (progpu_native_browser_engine_create(&options, &candidate->engine) != PROGPU_NATIVE_STATUS_SUCCESS)
            return fail("Native WebGPU renderer creation failed");
        active = std::move(candidate); error[0] = '\0'; return 1;
    } catch (...) { return fail("Native renderer allocation failed"); }
}

EMSCRIPTEN_KEEPALIVE void progpu_browser_dispose() noexcept { active.reset(); }
EMSCRIPTEN_KEEPALIVE const char* progpu_browser_error() noexcept { return error.data(); }
EMSCRIPTEN_KEEPALIVE const std::uint64_t* progpu_browser_metrics() noexcept {
    return active ? active->metrics.data() : nullptr;
}
EMSCRIPTEN_KEEPALIVE const std::byte* progpu_browser_stream() noexcept {
    return active ? active->stream.data() : nullptr;
}
EMSCRIPTEN_KEEPALIVE std::size_t progpu_browser_stream_size() noexcept {
    return active ? active->stream.size() : 0U;
}
EMSCRIPTEN_KEEPALIVE void progpu_browser_device_lost() noexcept {
    if (active) progpu_native_engine_mark_device_lost(active->engine);
}

EMSCRIPTEN_KEEPALIVE int progpu_browser_update(const std::byte* bytes, std::size_t length, int typed) noexcept {
    if (!active || !bytes || length == 0U || (typed != 0 && typed != 1)) return fail("Invalid scene update");
    try {
        std::vector<std::byte> candidate;
        if (typed != 0) {
            const char* detail = nullptr;
            if (!progpu::native::browser::compile_scene_packet({bytes, length}, candidate, detail)) return fail(detail);
        } else {
            candidate.assign(bytes, bytes + length);
        }
        progpu_native_scene_metrics metrics{};
        metrics.struct_size = sizeof(metrics);
        if (progpu_native_engine_update_scene(active->engine, candidate.data(), candidate.size(), &metrics) != PROGPU_NATIVE_STATUS_SUCCESS)
            return native_failure("Native scene update rejected");
        // Publish adapter identity and exported stream only after the original
        // native validator accepts the complete replacement transaction.
        active->stream.swap(candidate);
        active->scene_id = metrics.scene_id; active->generation = metrics.generation;
        active->metrics = {metrics.scene_id, metrics.generation, metrics.command_count, metrics.resource_count,
            metrics.snapshot_bytes, (metrics.flags & PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED) != 0U ? 1U : 0U,
            metrics.draw_count, metrics.payload_bytes};
        error[0] = '\0'; return 1;
    } catch (...) { return fail("Scene update allocation failed"); }
}

EMSCRIPTEN_KEEPALIVE int progpu_browser_render(float r, float g, float b, float a) noexcept {
    if (!active || active->scene_id == 0U) return fail("Update a scene before rendering");
    for (const float value : {r, g, b, a})
        if (!std::isfinite(value) || value < 0.0F || value > 1.0F) return fail("Invalid clear color");
    progpu::native::browser::browser_frame surface{};
    if (!active->host->begin_frame(surface)) return fail("Could not acquire the WebGPU canvas texture");
    progpu_native_scene_frame frame{};
    frame.struct_size = sizeof(frame); frame.width = surface.width; frame.height = surface.height;
    frame.dpi_scale = surface.dpi_scale; frame.target_view = reinterpret_cast<std::uintptr_t>(surface.view);
    frame.clear_color = {r, g, b, a}; frame.scene_id = active->scene_id; frame.generation = active->generation;
    progpu_native_scene_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    const auto status = progpu_native_engine_render_scene(active->engine, &frame, &metrics);
    active->host->end_frame(surface);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return native_failure("Native scene rendering failed");
    active->metrics = {metrics.command_count, metrics.draw_call_count, metrics.family_switch_count,
        metrics.submission_count, metrics.vertex_upload_bytes, metrics.index_upload_bytes,
        metrics.texture_upload_bytes, metrics.uniform_upload_bytes, metrics.coverage_staging_bytes,
        metrics.payload_hash, metrics.brush_upload_bytes, metrics.gradient_stop_upload_bytes,
        metrics.text_style_upload_bytes, metrics.color_glyph_upload_bytes, frame.width, frame.height};
    error[0] = '\0'; return 1;
}

} // extern "C"
