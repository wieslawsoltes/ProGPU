#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

progpu_native_status update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        &engine->webgpu_dispatch);
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native scene updates are owner-thread affine.");
    }
    if (stream != nullptr && !engine->semantic_scene_snapshot.empty() &&
        engine->semantic_scene_snapshot.size() == stream_size &&
        std::memcmp(
            engine->semantic_scene_snapshot.data(),
            stream,
            stream_size) == 0) {
        if (metrics != nullptr &&
            metrics->struct_size >= sizeof(progpu_native_scene_metrics)) {
            const std::uint32_t struct_size = metrics->struct_size;
            *metrics = engine->semantic_scene_metrics;
            metrics->struct_size = struct_size;
            metrics->flags |= PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED;
        }
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    const auto validation =
        progpu::native::scene::validate(stream, stream_size);
    progpu::native::scene::write_metrics(validation, metrics);
    if (validation.status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return engine->fail(
            validation.status,
            "The semantic scene stream failed transactional validation.");
    }

    if (validation.header.scene_id == engine->semantic_scene_id) {
        if (validation.header.generation <
            engine->semantic_scene_generation) {
            if (metrics != nullptr &&
                metrics->struct_size >= sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The semantic scene generation regressed.");
        }
        if (validation.header.generation ==
            engine->semantic_scene_generation) {
            if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "One semantic scene generation must be immutable.");
        }

        std::uint32_t error_offset = 0U;
        if (!progpu::native::scene::generations_do_not_regress(
                engine->semantic_scene_snapshot.data(),
                engine->semantic_scene_header,
                stream,
                validation.header,
                error_offset)) {
            if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
                metrics->error_offset = error_offset;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "A retained semantic resource generation regressed.");
        }
    }

    try {
        std::vector<std::byte> next(stream_size);
        std::memcpy(next.data(), stream, stream_size);
        const std::uint64_t next_hash = append_fnv1a64(
            14695981039346656037ULL,
            stream,
            stream_size);
        // A lost device is terminal, so replacing the retained CPU snapshot
        // must not dispatch release calls into that device from this CPU-only
        // update path. The terminal engine destructor owns final handle release;
        // recreation never clones the stale bundle or any other GPU object.
        if (!engine->device_lost) {
            engine->release_semantic_render_bundle();
        }
        engine->semantic_scene_snapshot.swap(next);
        engine->semantic_scene_id = validation.header.scene_id;
        engine->semantic_scene_generation = validation.header.generation;
        engine->semantic_scene_hash = next_hash;
        engine->semantic_scene_header = validation.header;
        engine->semantic_scene_metrics = {};
        engine->semantic_scene_metrics.struct_size =
            sizeof(progpu_native_scene_metrics);
        progpu::native::scene::write_metrics(
            validation,
            &engine->semantic_scene_metrics);
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The immutable semantic scene snapshot could not be allocated.");
    } catch (...) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The immutable semantic scene snapshot could not be committed.");
    }
}

} // namespace progpu::native::execution
