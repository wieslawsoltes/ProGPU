#include "progpu_native_frame_execution_common.hpp"

namespace progpu::native::execution {

progpu_native_status bind_scene_external_images(
    progpu_native_engine* engine,
    const progpu_native_scene_external_image_binding* bindings,
    std::size_t binding_count) {
    if (engine == nullptr ||
        (binding_count != 0U && bindings == nullptr) ||
        binding_count > PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        &engine->webgpu_dispatch);
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native external-image bindings are owner-thread affine.");
    }
    if (engine->device_lost || engine->device == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_DEVICE_LOST,
            "A lost native device cannot retain external-image views.");
    }

    bool identical =
        binding_count == engine->semantic_external_image_bindings.size();
    std::uint64_t previous_id = 0U;
    for (std::size_t index = 0U; index < binding_count; ++index) {
        const auto& source = bindings[index];
        if (source.struct_size < sizeof(source) || source.flags != 0U ||
            source.resource_id == 0U || source.resource_id <= previous_id ||
            source.generation == 0U || source.texture_view == 0U ||
            source.width == 0U || source.height == 0U ||
            source.width > 16384U || source.height > 16384U ||
            source.reserved0 != 0U || source.reserved1 != 0U) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "An external-image binding record is invalid or unordered.");
        }
        previous_id = source.resource_id;
        if (identical) {
            const auto& current =
                engine->semantic_external_image_bindings[index];
            identical = current.resource_id == source.resource_id &&
                current.generation == source.generation &&
                current.view == reinterpret_cast<WGPUTextureView>(
                    source.texture_view) &&
                current.width == source.width &&
                current.height == source.height;
        }
    }
    if (identical) {
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    std::vector<semantic_external_image_binding> next;
    try {
        next.reserve(binding_count);
        for (std::size_t index = 0U; index < binding_count; ++index) {
            const auto& source = bindings[index];
            auto view = reinterpret_cast<WGPUTextureView>(
                source.texture_view);
            progpu::native::webgpu::texture_view_add_ref(view);
            next.push_back({
                source.resource_id,
                source.generation,
                view,
                source.width,
                source.height});
        }
    } catch (const std::bad_alloc&) {
        for (auto& binding : next) {
            wgpuTextureViewRelease(binding.view);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The external-image binding table could not be allocated.");
    }

    engine->release_semantic_render_bundle();
    engine->release_semantic_image_page();
    engine->release_semantic_external_image_bindings();
    engine->semantic_external_image_bindings.swap(next);
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

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
        const auto next_content_hashes =
            progpu::native::semantic::compute_content_hashes(
                static_cast<const std::byte*>(stream),
                validation.header);
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
        engine->semantic_hashes = next_content_hashes;
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
