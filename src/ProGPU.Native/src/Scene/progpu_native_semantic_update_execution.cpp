#include "progpu_native_frame_execution_common.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <new>
#include <thread>
#include <utility>
#include <vector>

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
    std::uint32_t previous_role = 0U;
    for (std::size_t index = 0U; index < binding_count; ++index) {
        const auto& source = bindings[index];
        if (source.struct_size < sizeof(source) ||
            source.flags > PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_MASK ||
            source.resource_id == 0U ||
            source.resource_id < previous_id ||
            (source.resource_id == previous_id &&
                source.flags <= previous_role) ||
            source.generation == 0U || source.texture_view == 0U ||
            source.width == 0U || source.height == 0U ||
            source.width > 16384U || source.height > 16384U ||
            source.reserved0 != 0U || source.reserved1 != 0U) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "An external-image binding record is invalid or unordered.");
        }
        previous_id = source.resource_id;
        previous_role = source.flags;
        if (identical) {
            const auto& current =
                engine->semantic_external_image_bindings[index];
            identical = current.resource_id == source.resource_id &&
                current.generation == source.generation &&
                current.role == source.flags &&
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
                source.flags,
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
    engine->release_glyph_paint_texture_bindings();
    engine->release_semantic_image_page();
    engine->release_semantic_3d_page();
    engine->release_semantic_external_image_bindings();
    engine->semantic_external_image_bindings.swap(next);
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void apply_group_plan_hash(progpu_native_engine* engine, std::uint64_t plan_hash) noexcept {
    const std::uint64_t change = engine->semantic_group_plan_applied ^ plan_hash;
    if (change == 0U) {
        return;
    }
    auto& hashes = engine->semantic_hashes;
    for (auto* hash : {&hashes.brush, &hashes.text_style, &hashes.analytic, &hashes.path, &hashes.glyph,
             &hashes.image, &hashes.three_d, &hashes.hit_test, &engine->semantic_scene_hash}) {
        *hash ^= change;
    }
    engine->semantic_group_plan_applied = plan_hash;
}

namespace {

semantic_retained_scene* parked_scene(progpu_native_engine* engine, std::uint64_t id) noexcept {
    for (auto& scene : engine->semantic_parked_scenes) {
        if (scene.id == id) {
            return &scene;
        }
    }
    return nullptr;
}

void remove_parked_scene(progpu_native_engine* engine, semantic_retained_scene* scene) noexcept {
    engine->semantic_parked_scene_bytes -= scene->snapshot.size();
    if (scene != &engine->semantic_parked_scenes.back()) {
        std::swap(*scene, engine->semantic_parked_scenes.back());
    }
    engine->semantic_parked_scenes.pop_back();
}

// Drops the scenes used longest ago until the parked scenes fit the budget.
void trim_parked_scenes(progpu_native_engine* engine) noexcept {
    while (engine->semantic_parked_scene_bytes > engine->semantic_scene_budget &&
        !engine->semantic_parked_scenes.empty()) {
        auto oldest = std::min_element(
            engine->semantic_parked_scenes.begin(),
            engine->semantic_parked_scenes.end(),
            [](const auto& left, const auto& right) { return left.last_use < right.last_use; });
        remove_parked_scene(engine, &*oldest);
    }
}

// Moves the active scene into the parked scenes. May throw std::bad_alloc, before
// anything has moved.
void park_active_scene(progpu_native_engine* engine) {
    if (engine->semantic_scene_budget == 0U || engine->semantic_scene_snapshot.empty()) {
        return;
    }
    engine->semantic_parked_scenes.reserve(engine->semantic_parked_scenes.size() + 1U);
    semantic_retained_scene scene{};
    scene.id = engine->semantic_scene_id;
    scene.generation = engine->semantic_scene_generation;
    // What is kept is the content of the scene, without the groups plan of its last frame.
    apply_group_plan_hash(engine, 0U);
    scene.hash = engine->semantic_scene_hash;
    scene.resource_scope = engine->semantic_resource_scope;
    scene.last_use = ++engine->semantic_scene_clock;
    scene.hashes = engine->semantic_hashes;
    scene.header = engine->semantic_scene_header;
    scene.metrics = engine->semantic_scene_metrics;
    scene.snapshot = std::move(engine->semantic_scene_snapshot);
    engine->semantic_scene_snapshot.clear();
    engine->semantic_parked_scene_bytes += scene.snapshot.size();
    engine->semantic_parked_scenes.push_back(std::move(scene));
    trim_parked_scenes(engine);
}

// Makes a parked scene the active one, and the active scene a parked one.
bool activate_parked_scene(progpu_native_engine* engine, std::uint64_t id) noexcept {
    auto* scene = parked_scene(engine, id);
    if (scene == nullptr) {
        return false;
    }
    try {
        auto hashes = scene->hashes;
        if (scene->resource_scope != engine->semantic_resource_scope) {
            hashes = progpu::native::semantic::compute_content_hashes(
                scene->snapshot.data(), scene->header, engine->semantic_resource_scope);
        }
        engine->semantic_parked_scene_bytes -= scene->snapshot.size();
        semantic_retained_scene next = std::move(*scene);
        if (scene != &engine->semantic_parked_scenes.back()) {
            *scene = std::move(engine->semantic_parked_scenes.back());
        }
        engine->semantic_parked_scenes.pop_back();
        // The entry just freed is the capacity this needs, so it does not allocate.
        park_active_scene(engine);
        if (!engine->device_lost) {
            engine->release_semantic_render_bundle();
        }
        engine->semantic_scene_snapshot = std::move(next.snapshot);
        engine->semantic_scene_id = next.id;
        engine->semantic_scene_generation = next.generation;
        engine->semantic_scene_hash = next.hash;
        engine->semantic_hashes = hashes;
        engine->semantic_group_plan_applied = 0U;
        engine->semantic_scene_header = next.header;
        engine->semantic_scene_metrics = next.metrics;
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    }
}

// `owned`, when given, holds the bytes of `stream` and becomes the snapshot.
progpu_native_status commit_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics,
    std::vector<std::byte>* owned) {
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
    // The stream of a scene another target keeps, unchanged: it only becomes active.
    if (stream != nullptr && engine->semantic_scene_budget != 0U &&
        stream_size >= sizeof(progpu_native_scene_header)) {
        progpu_native_scene_header peeked{};
        std::memcpy(&peeked, stream, sizeof(peeked));
        if (peeked.scene_id != engine->semantic_scene_id) {
            const auto* kept = parked_scene(engine, peeked.scene_id);
            if (kept != nullptr && kept->snapshot.size() == stream_size &&
                std::memcmp(kept->snapshot.data(), stream, stream_size) == 0 &&
                activate_parked_scene(engine, peeked.scene_id)) {
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
        }
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

    // The retained state this generation follows: the active scene, or the one kept
    // for its target.
    const bool same_scene = validation.header.scene_id == engine->semantic_scene_id;
    const auto* kept_scene = same_scene ? nullptr : parked_scene(engine, validation.header.scene_id);
    if (same_scene || kept_scene != nullptr) {
        const std::uint64_t base_generation =
            same_scene ? engine->semantic_scene_generation : kept_scene->generation;
        const std::byte* base_snapshot =
            same_scene ? engine->semantic_scene_snapshot.data() : kept_scene->snapshot.data();
        const auto& base_header = same_scene ? engine->semantic_scene_header : kept_scene->header;
        if (validation.header.generation <
            base_generation) {
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
            base_generation) {
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
                base_snapshot,
                base_header,
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
        std::vector<std::byte> next;
        if (owned != nullptr) {
            next = std::move(*owned);
        } else {
            next.resize(stream_size);
            std::memcpy(next.data(), stream, stream_size);
        }
        const std::uint64_t next_hash = append_content_hash64(
            14695981039346656037ULL,
            stream,
            stream_size);
        const auto next_content_hashes =
            progpu::native::semantic::compute_content_hashes(
                static_cast<const std::byte*>(stream),
                validation.header, engine->semantic_resource_scope);
        // A lost device is terminal, so replacing the retained CPU snapshot
        // must not dispatch release calls into that device from this CPU-only
        // update path. The terminal engine destructor owns final handle release;
        // recreation never clones the stale bundle or any other GPU object.
        if (!engine->device_lost) {
            engine->release_semantic_render_bundle();
        }
        if (!same_scene) {
            // The new generation replaces what was kept for this scene; the scene it
            // takes the place of stays for its own target.
            if (auto* replaced = parked_scene(engine, validation.header.scene_id)) {
                remove_parked_scene(engine, replaced);
            }
            park_active_scene(engine);
        }
        engine->semantic_scene_snapshot.swap(next);
        engine->semantic_scene_id = validation.header.scene_id;
        engine->semantic_scene_generation = validation.header.generation;
        engine->semantic_scene_hash = next_hash;
        engine->semantic_hashes = next_content_hashes;
        engine->semantic_group_plan_applied = 0U;
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

// Whether the ranges of `delta` are ascending, apart, inside the new stream, hold
// exactly its bytes, and cover everything past the end of the retained stream.
bool delta_ranges_are_valid(
    const progpu_native_scene_delta& delta,
    std::uint64_t retained_size) noexcept {
    std::uint64_t end = 0U;
    std::uint64_t total = 0U;
    std::uint64_t covered_to = retained_size;
    for (std::uint32_t index = 0U; index < delta.range_count; ++index) {
        const auto& range = delta.ranges[index];
        if (range.size == 0U || range.offset < end ||
            range.offset > delta.stream_size ||
            range.size > delta.stream_size - range.offset) {
            return false;
        }
        end = range.offset + range.size;
        total += range.size;
        if (end > covered_to) {
            if (range.offset > covered_to) {
                return false;
            }
            covered_to = end;
        }
    }
    return total == delta.byte_count && covered_to >= delta.stream_size;
}

} // namespace

void select_retained_scene(progpu_native_engine* engine, std::uint64_t scene_id) noexcept {
    if (scene_id != engine->semantic_scene_id) {
        activate_parked_scene(engine, scene_id);
    }
}

progpu_native_status set_scene_budget(progpu_native_engine* engine, std::uint64_t bytes) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(PROGPU_NATIVE_STATUS_WRONG_THREAD, "Native scene budgets are owner-thread affine.");
    }
    engine->semantic_scene_budget = bytes;
    trim_parked_scenes(engine);
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status release_scene(progpu_native_engine* engine, std::uint64_t scene_id) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(PROGPU_NATIVE_STATUS_WRONG_THREAD, "Native scene releases are owner-thread affine.");
    }
    if (auto* scene = parked_scene(engine, scene_id)) {
        remove_parked_scene(engine, scene);
    }
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status get_scene_store(
    const progpu_native_engine* engine,
    progpu_native_scene_store_metrics* metrics) {
    if (engine == nullptr || metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_scene_store_metrics)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    metrics->kept_scene_count = static_cast<std::uint32_t>(engine->semantic_parked_scenes.size());
    metrics->kept_scene_bytes = engine->semantic_parked_scene_bytes;
    metrics->budget_bytes = engine->semantic_scene_budget;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    return commit_scene(engine, stream, stream_size, metrics, nullptr);
}

progpu_native_status update_scene_delta(
    progpu_native_engine* engine,
    const progpu_native_scene_delta* delta,
    progpu_native_scene_metrics* metrics) {
    if (engine == nullptr || delta == nullptr ||
        delta->struct_size < sizeof(progpu_native_scene_delta) ||
        (delta->range_count != 0U && delta->ranges == nullptr) ||
        (delta->byte_count != 0U && delta->bytes == nullptr) ||
        delta->stream_size == 0U ||
        delta->stream_size > std::numeric_limits<std::size_t>::max()) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native scene updates are owner-thread affine.");
    }
    if (engine->semantic_scene_id != delta->scene_id) {
        activate_parked_scene(engine, delta->scene_id);
    }
    if (engine->semantic_scene_snapshot.empty() ||
        engine->semantic_scene_id != delta->scene_id ||
        engine->semantic_scene_generation != delta->base_generation) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The scene delta does not follow the retained scene.");
    }
    if (!delta_ranges_are_valid(*delta, engine->semantic_scene_snapshot.size())) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The ranges of the scene delta are not valid.");
    }
    std::vector<std::byte> next;
    try {
        const auto size = static_cast<std::size_t>(delta->stream_size);
        next.resize(size);
        std::memcpy(
            next.data(),
            engine->semantic_scene_snapshot.data(),
            std::min(size, engine->semantic_scene_snapshot.size()));
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The patched semantic scene could not be allocated.");
    }
    const auto* source = static_cast<const std::byte*>(delta->bytes);
    for (std::uint32_t index = 0U; index < delta->range_count; ++index) {
        const auto& range = delta->ranges[index];
        std::memcpy(
            next.data() + static_cast<std::size_t>(range.offset),
            source,
            static_cast<std::size_t>(range.size));
        source += range.size;
    }
    const void* patched = next.data();
    const std::size_t patched_size = next.size();
    return commit_scene(engine, patched, patched_size, metrics, &next);
}

} // namespace progpu::native::execution
