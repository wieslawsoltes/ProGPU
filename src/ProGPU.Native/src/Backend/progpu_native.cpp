#include "progpu_native.h"
#if defined(PROGPU_NATIVE_BROWSER)
#include "progpu_native_browser.h"
#endif
#include "progpu_native_frame_execution.hpp"
#include "progpu_native_device_recovery.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_scene.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_child_engine.hpp"

#include <algorithm>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>
#include <thread>

namespace {

using progpu::native::initial_vertex_buffer_size;

constexpr bool valid_engine_flags(std::uint64_t flags) noexcept {
    constexpr std::uint64_t glyph_flags =
        PROGPU_NATIVE_ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK |
        PROGPU_NATIVE_ENGINE_GLYPH_RASTER_SHADER_FALLBACK |
        PROGPU_NATIVE_ENGINE_GLYPH_SCALAR_CPU_FALLBACK;
    constexpr std::uint64_t supported_flags = glyph_flags |
        PROGPU_NATIVE_ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING;
    return (flags & ~supported_flags) == 0U &&
        std::popcount(flags & glyph_flags) <= 1;
}

progpu_native_status create_engine(
    WGPUInstance instance,
    WGPUDevice device,
    WGPUQueue queue,
    WGPUTextureFormat target_format,
    std::uint64_t engine_flags,
    const progpu::native::webgpu::dispatch& webgpu_dispatch,
    progpu_native_engine** engine) {
    try {
        auto result = std::make_unique<progpu_native_engine>();
        result->owner_thread = std::this_thread::get_id();
        result->webgpu_dispatch = webgpu_dispatch;
        result->instance = instance;
        result->device = device;
        result->queue = queue;
        result->target_format = target_format;
        result->engine_flags = engine_flags;
        const progpu::native::webgpu::dispatch_scope dispatch_scope(
            &result->webgpu_dispatch);
        if (result->instance != nullptr) {
            progpu::native::webgpu::instance_add_ref(result->instance);
        }
        progpu::native::webgpu::device_add_ref(result->device);
        progpu::native::webgpu::queue_add_ref(result->queue);
        if (!create_pipeline(*result) ||
            !result->ensure_vertex_buffer(initial_vertex_buffer_size)) {
            result->last_error =
                "The shared vector shader or native WebGPU pipeline could not be created.";
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }
        *engine = result.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status require_gpu_engine(
    progpu_native_engine* engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native GPU operations are owner-thread affine.");
    }
    if (engine->device_lost) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_DEVICE_LOST,
            "The native WebGPU device was lost; recreate the engine on a replacement device.");
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status finish_recreation(
    const progpu_native_engine* source,
    progpu_native_engine** replacement) {
    const progpu_native_status clone_status =
        progpu::native::recovery::clone_retained_cpu_state(
            source,
            *replacement);
    if (clone_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        delete *replacement;
        *replacement = nullptr;
    }
    return clone_status;
}

progpu_native_status validate_recreation_source(
    const progpu_native_engine* source,
    progpu_native_engine** replacement) {
    if (source == nullptr || replacement == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *replacement = nullptr;
    if (!source->is_owner_thread()) {
        return PROGPU_NATIVE_STATUS_WRONG_THREAD;
    }
    return source->device_lost
        ? PROGPU_NATIVE_STATUS_SUCCESS
        : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

} // namespace

namespace progpu::native::execution {

progpu_native_status create_child_engine(
    const progpu_native_engine& parent,
    WGPUTextureFormat target_format,
    progpu_native_engine** child) {
    return create_engine(
        parent.instance,
        parent.device,
        parent.queue,
        target_format,
        parent.engine_flags,
        parent.webgpu_dispatch,
        child);
}

} // namespace progpu::native::execution

extern "C" {

uint32_t progpu_native_get_abi_version(void) {
    return PROGPU_NATIVE_ABI_VERSION;
}

uint8_t progpu_native_get_info(progpu_native_engine_info* info) {
    if (info == nullptr || info->struct_size < sizeof(progpu_native_engine_info)) {
        return 0U;
    }
    *info = {};
    info->struct_size = sizeof(progpu_native_engine_info);
    info->abi_version = PROGPU_NATIVE_ABI_VERSION;
#if defined(PROGPU_NATIVE_BROWSER)
    info->backend_abi =
        PROGPU_NATIVE_BACKEND_ABI_BROWSER_WEBGPU_2025_10;
#elif defined(PROGPU_NATIVE_DAWN_ABI)
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_DAWN_WEBSCENE_2026_07;
#else
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
#endif
    info->capabilities =
        PROGPU_NATIVE_CAPABILITY_SOLID_RECT_BATCH |
        PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_TARGET |
        PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH |
        PROGPU_NATIVE_CAPABILITY_AFFINE_2D |
        PROGPU_NATIVE_CAPABILITY_INDEXED_GEOMETRY_BATCH |
        PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES |
        PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES |
        PROGPU_NATIVE_CAPABILITY_STROKE_CAPS |
        PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES |
        PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES |
        PROGPU_NATIVE_CAPABILITY_DASHED_STROKES |
        PROGPU_NATIVE_CAPABILITY_RETAINED_GEOMETRY_REPLAY |
        PROGPU_NATIVE_CAPABILITY_PATH_FILL_ATLAS |
        PROGPU_NATIVE_CAPABILITY_POSITIONED_GLYPH_ATLAS |
        PROGPU_NATIVE_CAPABILITY_RESIZABLE_ATLASES |
        PROGPU_NATIVE_CAPABILITY_RETAINED_RGBA_IMAGE |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_RGBA_VIEW |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_IMAGE_MASK |
#if !defined(PROGPU_NATIVE_BROWSER)
        PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE |
#endif
        PROGPU_NATIVE_CAPABILITY_RETAINED_GPU_HIT_TESTING |
        PROGPU_NATIVE_CAPABILITY_FRAME_DRAW_STATE |
        PROGPU_NATIVE_CAPABILITY_GROUP_OPACITY |
        PROGPU_NATIVE_CAPABILITY_COMMON_GROUP_MASK |
        PROGPU_NATIVE_CAPABILITY_ANALYTIC_ROUNDED_GROUP_MASK |
        PROGPU_NATIVE_CAPABILITY_RETAINED_VECTOR_CLIP_CHAIN |
        PROGPU_NATIVE_CAPABILITY_GROUP_GAUSSIAN_BLUR |
        PROGPU_NATIVE_CAPABILITY_GROUP_BOX_BLUR |
        PROGPU_NATIVE_CAPABILITY_GROUP_DROP_SHADOW |
        PROGPU_NATIVE_CAPABILITY_BOUNDED_GROUP_EFFECT_CHAIN |
        PROGPU_NATIVE_CAPABILITY_GROUP_BLEND_MODES |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_SNAPSHOTS |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_RENDERING |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_BRUSHES |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_TEXT_STYLES |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_COLOR_GLYPH_ATLAS |
        PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_GEOMETRY_BATCH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_POINT_BATCH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_VERTEX_MESH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_STROKE_BATCH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_LINE_3D_BATCH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_MESH_3D_BATCH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_MESH_3D_MATERIALS |
        PROGPU_NATIVE_CAPABILITY_BULK_TEXT_SHAPING |
        PROGPU_NATIVE_CAPABILITY_BULK_TEXT_LAYOUT |
        PROGPU_NATIVE_CAPABILITY_BULK_TEXT_LINE_BREAKING |
        PROGPU_NATIVE_CAPABILITY_BULK_TEXT_BIDI |
        PROGPU_NATIVE_CAPABILITY_BULK_TEXT_PARAGRAPH |
        PROGPU_NATIVE_CAPABILITY_BULK_TEXT_VERTICAL_LAYOUT |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_IMAGE_PATCH_BATCH |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_IMAGE_MIPMAP_SAMPLING |
        PROGPU_NATIVE_CAPABILITY_IMAGE_FRAME_MIPMAP_SAMPLING |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_VECTOR_CLIP_MASK |
        PROGPU_NATIVE_CAPABILITY_WPF_MIL_CHANNEL;
#if defined(PROGPU_NATIVE_BROWSER)
    constexpr char name[] = "ProGPU C++ core renderer / browser WebGPU";
#elif defined(PROGPU_NATIVE_DAWN_ABI)
    constexpr char name[] = "ProGPU C++ core renderer / Dawn provider";
#else
    constexpr char name[] = "ProGPU C++ core renderer / wgpu-native";
#endif
    std::memcpy(info->name, name, sizeof(name));
    return 1U;
}

progpu_native_status progpu_native_scene_validate(
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    const auto result = progpu::native::scene::validate(stream, stream_size);
    progpu::native::scene::write_metrics(result, metrics);
    return result.status;
}

progpu_native_status progpu_native_engine_create(
    const progpu_native_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    (void)options;
    return PROGPU_NATIVE_STATUS_UNSUPPORTED;
#else
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_engine_options) ||
        options->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->backend_abi !=
            PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 ||
        options->device == 0U || options->queue == 0U ||
        !valid_engine_flags(options->flags) ||
        texture_format(options->target_format) == WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch webgpu_dispatch{};
    return create_engine(
        nullptr,
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        options->flags,
        webgpu_dispatch,
        engine);
#endif
}

progpu_native_status progpu_native_engine_recreate(
    const progpu_native_engine* source,
    const progpu_native_engine_options* options,
    progpu_native_engine** replacement) {
    const progpu_native_status validation =
        validate_recreation_source(source, replacement);
    if (validation != PROGPU_NATIVE_STATUS_SUCCESS) {
        return validation;
    }
    const progpu_native_status status =
        progpu_native_engine_create(options, replacement);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? finish_recreation(source, replacement)
        : status;
}

#if defined(PROGPU_NATIVE_DAWN_ABI) && !defined(PROGPU_NATIVE_BROWSER)
static_assert(sizeof(progpu_native_dawn_engine_options) == 72U);

uint32_t progpu_native_dawn_get_adapter_abi_version(void) {
    return PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
}

progpu_native_status progpu_native_dawn_engine_create(
    const progpu_native_dawn_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_dawn_engine_options) ||
        options->native_abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->adapter_abi_version !=
            PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION ||
        options->provider_abi_version !=
            PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION ||
        options->reserved != 0U ||
        !valid_engine_flags(options->flags) ||
        options->resolver_context == nullptr ||
        options->resolve_proc == nullptr ||
        options->instance == 0U || options->device == 0U ||
        options->queue == 0U ||
        texture_format(options->target_format) ==
            WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    progpu::native::webgpu::dispatch webgpu_dispatch{};
    if (!webgpu_dispatch.load(
            options->resolver_context,
            options->resolve_proc)) {
        return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    }
    return create_engine(
        reinterpret_cast<WGPUInstance>(options->instance),
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        options->flags,
        webgpu_dispatch,
        engine);
}

progpu_native_status progpu_native_dawn_engine_recreate(
    const progpu_native_engine* source,
    const progpu_native_dawn_engine_options* options,
    progpu_native_engine** replacement) {
    const progpu_native_status validation =
        validate_recreation_source(source, replacement);
    if (validation != PROGPU_NATIVE_STATUS_SUCCESS) {
        return validation;
    }
    const progpu_native_status status =
        progpu_native_dawn_engine_create(options, replacement);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? finish_recreation(source, replacement)
        : status;
}
#endif

#if defined(PROGPU_NATIVE_BROWSER)
uint32_t progpu_native_browser_get_adapter_abi_version(void) {
    return PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
}

progpu_native_status progpu_native_browser_engine_create(
    const progpu_native_browser_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_browser_engine_options) ||
        options->native_abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->adapter_abi_version !=
            PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION ||
        options->reserved0 != 0U || options->reserved1 != 0U ||
        (options->flags & ~static_cast<std::uint64_t>(
            PROGPU_NATIVE_ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING)) != 0U ||
        options->device == 0U ||
        options->queue == 0U ||
        texture_format(options->target_format) ==
            WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    progpu::native::webgpu::dispatch webgpu_dispatch{};
    if (!webgpu_dispatch.load(nullptr, nullptr)) {
        return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    }
    return create_engine(
        nullptr,
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        options->flags,
        webgpu_dispatch,
        engine);
}

progpu_native_status progpu_native_browser_engine_recreate(
    const progpu_native_engine* source,
    const progpu_native_browser_engine_options* options,
    progpu_native_engine** replacement) {
    const progpu_native_status validation =
        validate_recreation_source(source, replacement);
    if (validation != PROGPU_NATIVE_STATUS_SUCCESS) {
        return validation;
    }
    const progpu_native_status status =
        progpu_native_browser_engine_create(options, replacement);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? finish_recreation(source, replacement)
        : status;
}
#endif

void progpu_native_engine_destroy(progpu_native_engine* engine) {
    delete engine;
}

progpu_native_status progpu_native_engine_mark_device_lost(
    progpu_native_engine* engine) {
    return progpu::native::recovery::mark_device_lost(engine);
}

progpu_native_status progpu_native_engine_update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    return progpu::native::execution::update_scene(
        engine, stream, stream_size, metrics);
}

progpu_native_status progpu_native_engine_bind_scene_external_images(
    progpu_native_engine* engine,
    const progpu_native_scene_external_image_binding* bindings,
    size_t binding_count) {
    return progpu::native::execution::bind_scene_external_images(
        engine, bindings, binding_count);
}

progpu_native_status progpu_native_engine_render(
    progpu_native_engine* engine,
    const progpu_native_frame* frame,
    progpu_native_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_solid(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_render_analytic(
    progpu_native_engine* engine,
    const progpu_native_analytic_frame* frame,
    progpu_native_analytic_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_analytic(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_render_geometry(
    progpu_native_engine* engine,
    const progpu_native_geometry_frame* frame,
    progpu_native_geometry_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_geometry(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_render_paths(
    progpu_native_engine* engine,
    const progpu_native_path_frame* frame,
    progpu_native_path_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_paths(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_render_glyphs(
    progpu_native_engine* engine,
    const progpu_native_glyph_frame* frame,
    progpu_native_glyph_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_glyphs(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_render_image(
    progpu_native_engine* engine,
    const progpu_native_image_frame* frame,
    progpu_native_image_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_image(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_render_scene(
    progpu_native_engine* engine,
    const progpu_native_scene_frame* frame,
    progpu_native_scene_frame_metrics* metrics) {
    const progpu_native_status status = require_gpu_engine(engine);
    return status == PROGPU_NATIVE_STATUS_SUCCESS
        ? progpu::native::execution::render_scene(
            engine, frame, metrics)
        : status;
}

progpu_native_status progpu_native_engine_get_last_submission(
    progpu_native_engine* engine,
    std::uint64_t* submission_index) {
    if (engine == nullptr || submission_index == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer submission timeline must be queried from its owner thread.");
    }
    if (engine->device_lost) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_DEVICE_LOST,
            "Submission tokens from the lost native device are invalid.");
    }
    *submission_index = engine->last_submission_index;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_get_layer_metrics(
    progpu_native_engine* engine,
    progpu_native_layer_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    constexpr std::uint32_t legacy_size =
        offsetof(progpu_native_layer_metrics, mask_kind);
    if (engine == nullptr || metrics == nullptr ||
        metrics->struct_size < legacy_size) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer layer metrics must be queried from its owner thread.");
    }
    const std::uint32_t requested_size = metrics->struct_size;
    std::memcpy(
        metrics,
        &engine->last_layer_metrics,
        std::min<std::size_t>(
            requested_size,
            sizeof(progpu_native_layer_metrics)));
    metrics->struct_size = sizeof(progpu_native_layer_metrics);
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_poll_submission(
    progpu_native_engine* engine,
    std::uint64_t submission_index,
    std::uint8_t wait,
    std::uint8_t* complete) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    if (engine == nullptr || complete == nullptr || wait > 1U ||
        submission_index == 0U ||
        submission_index > engine->last_submission_index) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The native renderer submission token is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer submission timeline must be polled from its owner thread.");
    }
    if (engine->device_lost) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_DEVICE_LOST,
            "Submissions from the lost native device cannot be polled.");
    }
    const bool completed = progpu::native::webgpu::poll_submission(
        engine->instance,
        engine->device,
        engine->queue,
        submission_index,
        wait != 0U);
    *complete = completed ? 1U : 0U;
    if (completed && submission_index == engine->last_submission_index) {
        engine->submission_retirement.observe_latest_completion(
            engine->submission_count);
    }
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

size_t progpu_native_engine_get_last_error(
    const progpu_native_engine* engine,
    char* destination,
    size_t destination_size) {
    if (engine == nullptr) {
        return 0U;
    }
    const std::size_t required = engine->last_error.size() + 1U;
    if (destination != nullptr && destination_size != 0U) {
        const std::size_t copy_size = std::min(
            engine->last_error.size(),
            destination_size - 1U);
        std::memcpy(destination, engine->last_error.data(), copy_size);
        destination[copy_size] = '\0';
    }
    return required;
}

} // extern "C"
