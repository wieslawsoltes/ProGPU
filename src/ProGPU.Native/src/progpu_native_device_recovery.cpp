#include "progpu_native_device_recovery.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_native_engine.hpp"

#include <new>
#include <thread>

namespace progpu::native::recovery {

progpu_native_status mark_device_lost(
    progpu_native_engine* engine) noexcept {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native device loss must be reported from the engine owner thread.");
    }
    if (!engine->device_lost) {
        engine->device_lost = true;
        ++engine->device_loss_generation;
    }
    engine->last_error =
        "The native WebGPU device was lost; recreate the engine on a replacement device.";
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status clone_retained_cpu_state(
    const progpu_native_engine* source,
    progpu_native_engine* replacement) noexcept {
    if (source == nullptr || replacement == nullptr || source == replacement) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (std::this_thread::get_id() != source->owner_thread ||
        std::this_thread::get_id() != replacement->owner_thread) {
        return PROGPU_NATIVE_STATUS_WRONG_THREAD;
    }
    if (!source->device_lost || replacement->device_lost ||
        source->target_format != replacement->target_format) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    try {
        replacement->semantic_scene_snapshot =
            source->semantic_scene_snapshot;
        replacement->semantic_brush_cache =
            source->semantic_brush_cache;
        replacement->semantic_text_style_cache =
            source->semantic_text_style_cache;
        replacement->semantic_scene_id = source->semantic_scene_id;
        replacement->semantic_scene_generation =
            source->semantic_scene_generation;
        replacement->semantic_scene_hash = source->semantic_scene_hash;
        replacement->semantic_scene_header =
            source->semantic_scene_header;
        replacement->semantic_scene_metrics =
            source->semantic_scene_metrics;
        replacement->device_loss_generation =
            source->device_loss_generation;
        replacement->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return replacement->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The retained native scene could not be cloned for the replacement device.");
    } catch (...) {
        return replacement->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained native scene could not be committed to the replacement device.");
    }
}

} // namespace progpu::native::recovery
