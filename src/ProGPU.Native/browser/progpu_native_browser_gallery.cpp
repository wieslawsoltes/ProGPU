#include "Host/progpu_native_browser_window_host.hpp"
#include "progpu_native_browser.h"
#include "progpu_native_motion_mark.hpp"
#include "progpu_native_text_shaping_showcase.hpp"

#include <emscripten.h>
#include <emscripten/html5.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <new>
#include <vector>

namespace {

using progpu::native::browser::browser_frame;
using progpu::native::browser::browser_window_host;
using progpu::native::samples::motion_mark_scene;
using progpu::native::samples::motion_mark_scene_metrics;
using progpu::native::samples::text_shaping_showcase_metrics;
using progpu::native::samples::text_shaping_showcase_scene;

enum class gallery_sample : std::uint32_t {
    motion_mark = 0U,
    text_shaping = 1U
};

struct gallery_application final {
    browser_window_host host{};
    motion_mark_scene motion_sample{};
    motion_mark_scene_metrics motion_metrics{};
    text_shaping_showcase_scene text_sample{};
    text_shaping_showcase_metrics text_metrics{};
    std::vector<std::byte> font_staging{};
    std::vector<std::byte> scene_stream{};
    progpu_native_engine* engine = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    std::uint64_t frame_count = 0U;
    std::uint32_t regeneration = 1U;
    double previous_timestamp = 0.0;
    double metrics_timestamp = 0.0;
    double fps_timestamp = 0.0;
    double scene_update_milliseconds = 0.0;
    std::uint64_t fps_frame = 0U;
    float fps = 0.0F;
    gallery_sample active_sample = gallery_sample::motion_mark;
    bool paused = false;
};

gallery_application* application = nullptr;

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdollar-in-identifier-extension"
void fail(const char* message) noexcept {
    EM_ASM({
        const message = UTF8ToString($0);
        document.body.dataset.progpuNative = 'failed';
        document.body.dataset.progpuNativeError = message;
        document.querySelector('#status-message').textContent = message;
        console.error('[ProGPU] ' + message);
    }, message);
}
#pragma clang diagnostic pop

bool update_scene(gallery_application& app) noexcept {
    const bool dirty = app.active_sample == gallery_sample::motion_mark
        ? app.motion_sample.dirty()
        : app.text_sample.dirty();
    if (!dirty) {
        return true;
    }
    const double start = emscripten_get_now();
    const bool compiled = app.active_sample == gallery_sample::motion_mark
        ? app.motion_sample.compile(app.scene_stream, app.motion_metrics)
        : app.text_sample.compile(app.scene_stream, app.text_metrics);
    if (!compiled) {
        return false;
    }
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    const auto status = progpu_native_engine_update_scene(
        app.engine,
        app.scene_stream.data(),
        app.scene_stream.size(),
        &metrics);
    app.scene_update_milliseconds = emscripten_get_now() - start;
    return status == PROGPU_NATIVE_STATUS_SUCCESS &&
        metrics.scene_id != 0U && metrics.generation != 0U;
}

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdollar-in-identifier-extension"
void publish_metrics(
    const gallery_application& app,
    const browser_frame& frame,
    const progpu_native_scene_frame_metrics& render) noexcept {
    EM_ASM({
        const set = (id, value) => {
          const element = document.querySelector(id);
          if (element) element.textContent = value;
        };
        set('#metric-fps', Number($0).toFixed(0));
        set('#metric-elements', Number($1).toLocaleString());
        set('#metric-groups', Number($2).toLocaleString());
        set('#metric-draws', Number($3).toLocaleString());
        set('#metric-bytes', Number($4).toLocaleString() + ' B');
        set('#metric-dpr', Number($5).toFixed(2));
        const status = document.querySelector('#status-message');
        if (status) status.textContent =
          'native update ' + Number($6).toFixed(3) + ' ms / ' +
          Number($7).toLocaleString() + ' submissions';
        document.body.dataset.progpuNative = 'running';
        document.body.dataset.progpuNativeRenderer = 'pure-cpp-webgpu';
        document.body.dataset.progpuNativePresentation = 'canvas-swapchain';
        document.body.dataset.progpuNativeAot = 'emscripten-wasm';
        document.body.dataset.progpuNativeElements = String($1);
        document.body.dataset.progpuNativeGroups = String($2);
        document.body.dataset.progpuNativeDraws = String($3);
        document.body.dataset.progpuNativeStreamBytes = String($4);
        document.body.dataset.progpuNativeBackingWidth = String($8);
        document.body.dataset.progpuNativeBackingHeight = String($9);
        document.body.dataset.progpuNativeDpiScale = String($5);
        document.body.dataset.progpuNativeFrames = String($10);
        document.body.dataset.progpuNativeSample = Number($11) === 0
          ? 'motion-mark'
          : 'text-shaping';
        document.body.dataset.progpuNativeGlyphs = String($12);
        document.body.dataset.progpuNativeOutlines = String($13);
        document.body.dataset.progpuNativeUpdateMilliseconds = String($6);
        document.body.dataset.progpuNativeTextPreset = String($14);
    },
        static_cast<double>(app.fps),
        app.active_sample == gallery_sample::motion_mark
            ? app.motion_metrics.element_count
            : app.text_metrics.shaped_glyph_count,
        app.active_sample == gallery_sample::motion_mark
            ? app.motion_metrics.group_count
            : app.text_metrics.unique_outline_count,
        render.draw_call_count,
        static_cast<double>(app.active_sample == gallery_sample::motion_mark
            ? app.motion_metrics.stream_bytes
            : app.text_metrics.stream_bytes),
        static_cast<double>(frame.dpi_scale),
        app.scene_update_milliseconds,
        static_cast<double>(render.submission_count),
        frame.width,
        frame.height,
        static_cast<double>(app.frame_count),
        static_cast<std::uint32_t>(app.active_sample),
        app.text_metrics.shaped_glyph_count,
        app.text_metrics.unique_outline_count,
        app.text_metrics.preset_index);
}
#pragma clang diagnostic pop

EM_BOOL render_frame(double timestamp, void*) noexcept {
    auto& app = *application;
    const float delta = app.previous_timestamp > 0.0
        ? static_cast<float>(std::clamp(
            (timestamp - app.previous_timestamp) / 1000.0,
            0.0,
            0.1))
        : 1.0F / 60.0F;
    app.previous_timestamp = timestamp;

    browser_frame frame{};
    if (!app.host.begin_frame(frame)) {
        return EM_TRUE;
    }
    if (app.active_sample == gallery_sample::motion_mark) {
        app.motion_sample.resize(frame.logical_width, frame.logical_height);
        if (!app.paused) {
            app.motion_sample.advance(delta);
        }
    } else {
        app.text_sample.resize(
            frame.logical_width,
            frame.logical_height,
            frame.dpi_scale);
    }
    if (!update_scene(app)) {
        app.host.end_frame(frame);
        fail("The pure C++ gallery scene update failed.");
        return EM_FALSE;
    }

    progpu_native_scene_frame native_frame{};
    native_frame.struct_size = sizeof(native_frame);
    native_frame.width = frame.width;
    native_frame.height = frame.height;
    native_frame.dpi_scale = frame.dpi_scale;
    native_frame.target_view =
        reinterpret_cast<std::uintptr_t>(frame.view);
    // Match the managed gallery's dark ControlBackground contrast. The default
    // Vello palette deliberately contains near-black strokes, which disappear
    // against the shell chrome's almost-black background and make connected
    // curves look like isolated blocks.
    native_frame.clear_color = {0.125F, 0.125F, 0.15F, 1.0F};
    native_frame.scene_id = app.active_sample == gallery_sample::motion_mark
        ? 0x4D4F54494F4E4D4BULL
        : 0x5445585453484150ULL;
    native_frame.generation = app.active_sample == gallery_sample::motion_mark
        ? app.motion_sample.generation()
        : app.text_sample.generation();

    progpu_native_scene_frame_metrics render_metrics{};
    render_metrics.struct_size = sizeof(render_metrics);
    const auto status = progpu_native_engine_render_scene(
        app.engine,
        &native_frame,
        &render_metrics);
    const browser_frame presented_frame = frame;
    app.host.end_frame(frame);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
        fail("The pure C++ ProGPU browser render failed.");
        return EM_FALSE;
    }

    ++app.frame_count;
    if (app.fps_timestamp == 0.0) {
        app.fps_timestamp = timestamp;
        app.fps_frame = app.frame_count;
    } else if (timestamp - app.fps_timestamp >= 500.0) {
        app.fps = static_cast<float>(
            (app.frame_count - app.fps_frame) * 1000.0 /
            (timestamp - app.fps_timestamp));
        app.fps_timestamp = timestamp;
        app.fps_frame = app.frame_count;
    }
    if (timestamp - app.metrics_timestamp >= 200.0) {
        app.metrics_timestamp = timestamp;
        publish_metrics(app, presented_frame, render_metrics);
    }
    return EM_TRUE;
}

} // namespace

extern "C" {

EMSCRIPTEN_KEEPALIVE void progpu_native_gallery_set_element_count(
    std::uint32_t count) noexcept {
    if (application != nullptr) {
        application->motion_sample.set_element_count(count);
    }
}

EMSCRIPTEN_KEEPALIVE void progpu_native_gallery_set_color_mode(
    std::uint32_t mode) noexcept {
    if (application != nullptr) {
        application->motion_sample.set_color_mode(mode);
    }
}

EMSCRIPTEN_KEEPALIVE std::int32_t
progpu_native_gallery_toggle_paused() noexcept {
    if (application == nullptr) {
        return 0;
    }
    application->paused = !application->paused;
    return application->paused ? 1 : 0;
}

EMSCRIPTEN_KEEPALIVE void progpu_native_gallery_regenerate() noexcept {
    if (application != nullptr) {
        application->motion_sample.regenerate(
            0x50A7C0DEU + application->regeneration++ * 0x9E3779B9U);
    }
}

EMSCRIPTEN_KEEPALIVE std::uintptr_t
progpu_native_gallery_prepare_font(std::uint32_t size) noexcept {
    if (application == nullptr || size == 0U || size > 8U * 1024U * 1024U) {
        return 0U;
    }
    try {
        application->font_staging.resize(size);
        return reinterpret_cast<std::uintptr_t>(
            application->font_staging.data());
    } catch (...) {
        application->font_staging.clear();
        return 0U;
    }
}

EMSCRIPTEN_KEEPALIVE std::int32_t
progpu_native_gallery_commit_font(std::uint32_t size) noexcept {
    if (application == nullptr ||
        size == 0U || size != application->font_staging.size() ||
        !application->text_sample.load_font(application->font_staging)) {
        return 0;
    }
    application->font_staging.clear();
    return 1;
}

EMSCRIPTEN_KEEPALIVE std::uint32_t
progpu_native_gallery_set_sample(std::uint32_t sample) noexcept {
    if (application == nullptr) {
        return 0U;
    }
    const auto requested = sample == 1U
        ? gallery_sample::text_shaping
        : gallery_sample::motion_mark;
    if (requested == gallery_sample::text_shaping &&
        !application->text_sample.ready()) {
        return static_cast<std::uint32_t>(application->active_sample);
    }
    if (application->active_sample != requested) {
        application->active_sample = requested;
        if (requested == gallery_sample::motion_mark) {
            application->motion_sample.invalidate();
        } else {
            application->text_sample.invalidate();
        }
    }
    return static_cast<std::uint32_t>(application->active_sample);
}

EMSCRIPTEN_KEEPALIVE void progpu_native_gallery_set_text_preset(
    std::uint32_t preset) noexcept {
    if (application != nullptr) {
        application->text_sample.set_preset(preset);
    }
}

} // extern "C"

int main() {
    auto* app = new (std::nothrow) gallery_application{};
    if (app == nullptr) {
        fail("The native gallery could not allocate its application state.");
        return 1;
    }
    application = app;
    app->device = emscripten_webgpu_get_device();
    if (app->device == nullptr ||
        !app->host.initialize(app->device, "#progpu-canvas")) {
        fail("The shared browser host could not create a WebGPU canvas surface.");
        return 1;
    }
    app->queue = wgpuDeviceGetQueue(app->device);
    if (app->queue == nullptr) {
        fail("The browser WebGPU queue is unavailable.");
        return 1;
    }

    progpu_native_browser_engine_options options{};
    options.struct_size = sizeof(options);
    options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.adapter_abi_version =
        PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
    options.target_format = app->host.native_format();
    options.device = reinterpret_cast<std::uintptr_t>(app->device);
    options.queue = reinterpret_cast<std::uintptr_t>(app->queue);
    if (progpu_native_browser_engine_create(&options, &app->engine) !=
            PROGPU_NATIVE_STATUS_SUCCESS ||
        app->engine == nullptr) {
        fail("The ProGPU pure C++ browser engine could not be created.");
        return 1;
    }
    app->scene_stream.reserve(1024U * 1024U);
    emscripten_request_animation_frame_loop(render_frame, nullptr);
    return 0;
}
