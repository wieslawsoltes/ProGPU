#include "progpu_native_semantic_layer_mask_resources.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_native_child_engine.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_layer_mask.hpp"
#include "progpu_native_semantic_replay.hpp"
#include "progpu_native_semantic_state.hpp"
#include "progpu_webgpu_compat.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <memory>
#include <new>
#include <vector>

semantic_picture_backing::~semantic_picture_backing() {
    if (view != nullptr) wgpuTextureViewRelease(view);
    if (texture != nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
    }
}

namespace progpu::native::execution {
namespace {

WGPUBuffer create_uniform_buffer(
    progpu_native_engine& engine,
    const char* label,
    std::uint64_t size) {
    WGPUBufferDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    descriptor.size = size;
    return wgpuDeviceCreateBuffer(engine.device, &descriptor);
}

void release_buffer(WGPUBuffer& buffer, bool destroy = true) noexcept {
    if (buffer == nullptr) {
        return;
    }
    if (destroy) {
        wgpuBufferDestroy(buffer);
    }
    wgpuBufferRelease(buffer);
    buffer = nullptr;
}

void release_texture(WGPUTexture& texture, WGPUTextureView& view) noexcept {
    if (view != nullptr) {
        wgpuTextureViewRelease(view);
        view = nullptr;
    }
    if (texture != nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        texture = nullptr;
    }
}

constexpr std::uint64_t retained_picture_mask_cache_budget =
    64ULL * 1024U * 1024U;
// Real WPF surfaces commonly retain more than eight picture-mask descriptors
// for one nested scene (the Toolkit/AvalonDock gate uses nine). Keep a bounded
// lookup ceiling for tiny rasters, but let the byte budget govern ordinary
// desktop working sets so a sequential rebuild cannot evict every next entry.
constexpr std::size_t retained_picture_mask_cache_entries = 64U;
constexpr std::size_t retained_picture_image_cache_entries = 8U;

bool same_external_image_identity(
    const semantic_picture_backing& backing,
    const progpu_native_engine& engine) noexcept {
    if (backing.external_images.size() !=
        engine.semantic_external_image_bindings.size()) {
        return false;
    }
    for (std::size_t index = 0U; index < backing.external_images.size();
         ++index) {
        const auto& prior = backing.external_images[index];
        const auto& current = engine.semantic_external_image_bindings[index];
        if (prior.resource_id != current.resource_id ||
            prior.generation != current.generation ||
            prior.role != current.role ||
            prior.view != reinterpret_cast<std::uintptr_t>(current.view) ||
            prior.width != current.width || prior.height != current.height) {
            return false;
        }
    }
    return true;
}

void capture_external_image_identity(
    semantic_picture_backing& backing,
    const progpu_native_engine& engine) {
    backing.external_images.reserve(
        engine.semantic_external_image_bindings.size());
    for (const auto& binding : engine.semantic_external_image_bindings) {
        backing.external_images.push_back({binding.resource_id,
            binding.generation, binding.role,
            reinterpret_cast<std::uintptr_t>(binding.view), binding.width,
            binding.height});
    }
}

bool has_external_image_dependency(const std::byte* scene,
    const progpu_native_scene_header& header, std::uint32_t depth = 0U) noexcept {
    // Validation has already checked every range. Retention remains optional,
    // so reject an unexpectedly deep graph instead of weakening its identity.
    if (scene == nullptr || depth >= 64U) {
        return true;
    }
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        progpu_native_scene_resource resource{};
        std::memcpy(&resource,
            scene + header.resource_offset + index * header.resource_stride,
            sizeof(resource));
        if ((resource.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U) {
            return true;
        }
        if ((resource.flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) != 0U) {
            progpu_native_scene_header nested{};
            std::memcpy(&nested, scene + resource.auxiliary_offset,
                sizeof(nested));
            if (has_external_image_dependency(
                    scene + resource.auxiliary_offset, nested, depth + 1U)) {
                return true;
            }
        }
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            continue;
        }
        std::uint32_t error_offset = resource.payload_offset;
        semantic::semantic_layer_mask parsed{};
        if (!semantic::validate_layer_mask_resource(
                scene, resource, error_offset, &parsed)) {
            return true;
        }
        const auto nested_depends_on_external =
            [&](const std::byte* nested_scene,
                std::uint32_t nested_size) noexcept {
                if (nested_scene == nullptr ||
                    nested_size < sizeof(progpu_native_scene_header)) {
                    return true;
                }
                progpu_native_scene_header nested{};
                std::memcpy(&nested, nested_scene, sizeof(nested));
                return has_external_image_dependency(
                    nested_scene, nested, depth + 1U);
            };
        if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE &&
            nested_depends_on_external(parsed.composite_picture_streams,
                parsed.picture.stream_size)) {
            return true;
        }
        if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
            for (std::uint32_t picture_index = 0U;
                 picture_index < parsed.composite.picture_mask_count;
                 ++picture_index) {
                const auto& picture =
                    parsed.composite_picture_masks[picture_index];
                if (nested_depends_on_external(
                        parsed.composite_picture_streams +
                            picture.stream_offset,
                        picture.stream_size)) {
                    return true;
                }
            }
        }
    }
    return false;
}

bool supports_retained_picture_raster(
    const std::byte* scene,
    const progpu_native_scene_header& header) noexcept {
    std::uint32_t first_command = 0U;
    return semantic::find_append_only_scene_suffix(
               scene, header, scene, header, first_command) &&
        first_command == header.command_count &&
        !has_external_image_dependency(scene, header);
}

std::shared_ptr<semantic_picture_backing> find_retained_picture_raster(
    progpu_native_engine& engine,
    const progpu_native_scene_picture_image& descriptor,
    const std::byte* nested_scene,
    const progpu_native_scene_header& header) noexcept {
    for (const auto& entry : engine.semantic_picture_mask_cache) {
        if (!entry ||
            entry->scene.size() < sizeof(progpu_native_scene_header) ||
            entry->engine_flags != engine.engine_flags ||
            !semantic::scene_bytes_equal(
                std::as_bytes(std::span(&entry->descriptor, 1U)),
                std::as_bytes(std::span(&descriptor, 1U)))) {
            continue;
        }
        progpu_native_scene_header prior{};
        std::memcpy(&prior, entry->scene.data(), sizeof(prior));
        std::uint32_t first_command = 0U;
        if (semantic::find_append_only_scene_suffix(
                entry->scene.data(), prior, nested_scene, header,
                first_command) &&
            first_command == header.command_count) {
            return entry;
        }
    }
    return {};
}

void retain_picture_raster(
    progpu_native_engine& engine,
    const progpu_native_scene_header& header,
    const std::shared_ptr<semantic_picture_backing>& backing) noexcept {
    if (!backing || backing->scene.empty() ||
        backing->byte_cost() > retained_picture_mask_cache_budget) {
        return;
    }
    auto& cache = engine.semantic_picture_mask_cache;
    std::uint64_t retained_bytes = 0U;
    for (auto it = cache.begin(); it != cache.end();) {
        if (!*it ||
            (*it)->scene.size() < sizeof(progpu_native_scene_header)) {
            it = cache.erase(it);
            continue;
        }
        progpu_native_scene_header prior{};
        std::memcpy(&prior, (*it)->scene.data(), sizeof(prior));
        std::uint32_t first_command = 0U;
        if (prior.scene_id == header.scene_id &&
            semantic::scene_bytes_equal(
                std::as_bytes(std::span(&(*it)->descriptor, 1U)),
                std::as_bytes(std::span(&backing->descriptor, 1U))) &&
            semantic::find_append_only_scene_suffix(
                (*it)->scene.data(), prior, backing->scene.data(), header,
                first_command) &&
            first_command == header.command_count) {
            it = cache.erase(it);
        } else {
            retained_bytes += (*it)->byte_cost();
            ++it;
        }
    }
    const auto cost = backing->byte_cost();
    while (!cache.empty() &&
        (cache.size() >= retained_picture_mask_cache_entries ||
            retained_bytes > retained_picture_mask_cache_budget - cost)) {
        retained_bytes -= cache.front()->byte_cost();
        cache.erase(cache.begin());
    }
    try {
        cache.push_back(backing);
    } catch (const std::bad_alloc&) {
        // Retention is optional; the current bundle still owns backing.
    }
}

} // namespace

static bool create_semantic_picture_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_picture_mask& picture,
    const std::byte* nested_scene,
    const semantic::scissor& target_extent,
    float dpi_scale,
    const semantic::semantic_state_cursor* composite_state_cursor,
    const progpu_native_scene_state* composite_state,
    semantic_render_bundle_span& operation,
    semantic_image_draw* image_output,
    progpu_native_scene_frame_metrics* image_metrics,
    const progpu_native_color* source_clear,
    WGPUTexture seed_texture = nullptr, std::uint32_t first_command = 0U,
    const progpu_native_scene_presentation* presentation = nullptr) {
    static const bool trace_picture_masks = [] {
#if defined(_WIN32)
        char* value = nullptr;
        std::size_t length = 0U;
        if (_dupenv_s(&value, &length,
                "PROGPU_NATIVE_TRACE_PICTURE_MASK") != 0) {
            return false;
        }
        const bool enabled = value != nullptr &&
            std::strcmp(value, "1") == 0;
        std::free(value);
        return enabled;
#else
        const char* value = std::getenv("PROGPU_NATIVE_TRACE_PICTURE_MASK");
        return value != nullptr && std::strcmp(value, "1") == 0;
#endif
    }();
    const bool trace_picture = trace_picture_masks && image_output == nullptr;
    using cpu_clock = std::chrono::steady_clock;
    const auto picture_begin = trace_picture
        ? cpu_clock::now() : cpu_clock::time_point{};
    progpu_native_scene_frame child_frame{};
    if (nested_scene == nullptr || picture.stream_size == 0U ||
        !semantic::try_resolve_semantic_picture_frame(picture, target_extent,
            dpi_scale, presentation, child_frame) ||
        (image_output == nullptr && !create_layer_mask_resources(engine))) {
        return false;
    }

    const bool source_extent =
        (picture.flags & PROGPU_NATIVE_SCENE_PICTURE_MASK_SOURCE_EXTENT) != 0U;
    const std::uint32_t source_width = child_frame.width;
    const std::uint32_t source_height = child_frame.height;
    const std::uint64_t source_bytes =
        static_cast<std::uint64_t>(source_width) * source_height * 4U;
    if (source_bytes > PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES) {
        return false;
    }

    WGPUTexture source_texture = nullptr;
    WGPUTextureView source_view = nullptr;
    WGPUBuffer sampling_uniform_buffer = nullptr;
    WGPUBindGroup sampling_bind_group = nullptr;
    std::unique_ptr<progpu_native_engine> child;
    std::shared_ptr<semantic_picture_backing> mask_picture_backing;
    progpu_native_scene_header nested_header{};
    std::memcpy(&nested_header, nested_scene, sizeof(nested_header));
    const bool raster_cache_eligible =
        supports_retained_picture_raster(nested_scene, nested_header);
    progpu_native_scene_frame_metrics child_metrics{};
    child_metrics.struct_size = sizeof(child_metrics);
    const progpu_native_scene_picture_image raster_descriptor{
        sizeof(progpu_native_scene_picture_image),
        0U,
        source_width,
        source_height,
        child_frame.dpi_scale,
        {0U, 0U, 0U},
        child_frame.clear_color};
    if (image_output == nullptr && seed_texture == nullptr &&
        raster_cache_eligible) {
        mask_picture_backing = find_retained_picture_raster(
            engine, raster_descriptor, nested_scene, nested_header);
        if (mask_picture_backing) {
            source_view = mask_picture_backing->view;
            webgpu::texture_view_add_ref(source_view);
        }
    }
    const bool raster_cache_hit = mask_picture_backing != nullptr;
    const auto cleanup = [&]() noexcept {
        if (sampling_bind_group != nullptr) {
            wgpuBindGroupRelease(sampling_bind_group);
            sampling_bind_group = nullptr;
        }
        release_buffer(sampling_uniform_buffer);
        release_texture(source_texture, source_view);
    };

    if (!raster_cache_hit) {
        WGPUTextureDescriptor source_descriptor{};
        source_descriptor.label = webgpu::string_view(
            "ProGPU retained picture-mask RGBA source");
        source_descriptor.usage = WGPUTextureUsage_RenderAttachment |
            WGPUTextureUsage_TextureBinding;
        if (image_output != nullptr) {
            source_descriptor.usage |=
                WGPUTextureUsage_CopySrc | WGPUTextureUsage_CopyDst;
        }
        source_descriptor.dimension = WGPUTextureDimension_2D;
        source_descriptor.size = {source_width, source_height, 1U};
        source_descriptor.format = engine.target_format;
        source_descriptor.mipLevelCount = 1U;
        source_descriptor.sampleCount = 1U;
        source_texture =
            wgpuDeviceCreateTexture(engine.device, &source_descriptor);
        source_view = source_texture == nullptr
            ? nullptr
            : wgpuTextureCreateView(source_texture, nullptr);
        if (source_view == nullptr) {
            cleanup();
            return false;
        }

        progpu_native_engine* child_raw = nullptr;
        const auto child_create_begin = trace_picture
            ? cpu_clock::now() : cpu_clock::time_point{};
        if (create_child_engine(engine, engine.target_format, &child_raw) !=
                PROGPU_NATIVE_STATUS_SUCCESS ||
            child_raw == nullptr) {
            cleanup();
            return false;
        }
        child.reset(child_raw);
        const auto child_create_end = trace_picture
            ? cpu_clock::now() : cpu_clock::time_point{};
        std::vector<progpu_native_scene_external_image_binding> bindings;
        try {
            bindings.reserve(engine.semantic_external_image_bindings.size());
            for (const auto& source : engine.semantic_external_image_bindings) {
                bindings.push_back({
                    sizeof(progpu_native_scene_external_image_binding),
                    source.role,
                    source.resource_id,
                    source.generation,
                    reinterpret_cast<std::uintptr_t>(source.view),
                    source.width,
                    source.height,
                    0U,
                    0U});
            }
        } catch (const std::bad_alloc&) {
            cleanup();
            return false;
        }
        std::vector<std::byte> suffix_scene;
        if (seed_texture != nullptr) {
            // Preserve immutable earlier captures: copy on the GPU into a fresh
            // backing, then render only the appended commands with attachment load.
            try {
                suffix_scene.assign(
                    nested_scene, nested_scene + picture.stream_size);
            } catch (const std::bad_alloc&) {
                cleanup();
                return false;
            }
            progpu_native_scene_header suffix_header{};
            std::memcpy(
                &suffix_header, suffix_scene.data(), sizeof(suffix_header));
            if (first_command > suffix_header.command_count) {
                cleanup();
                return false;
            }
            suffix_header.command_offset +=
                first_command * suffix_header.command_stride;
            suffix_header.command_count -= first_command;
            std::memcpy(
                suffix_scene.data(), &suffix_header, sizeof(suffix_header));
            nested_scene = suffix_scene.data();
            WGPUCommandEncoder copy_encoder =
                wgpuDeviceCreateCommandEncoder(engine.device, nullptr);
            if (copy_encoder == nullptr) {
                cleanup();
                return false;
            }
            webgpu::image_copy_texture source_copy{}, destination_copy{};
            source_copy.texture = seed_texture;
            source_copy.aspect = WGPUTextureAspect_All;
            destination_copy.texture = source_texture;
            destination_copy.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{source_width, source_height, 1U};
            wgpuCommandEncoderCopyTextureToTexture(copy_encoder, &source_copy,
                &destination_copy, &extent);
            WGPUCommandBuffer copy_commands =
                wgpuCommandEncoderFinish(copy_encoder, nullptr);
            wgpuCommandEncoderRelease(copy_encoder);
            if (copy_commands == nullptr) {
                cleanup();
                return false;
            }
            engine.submit(copy_commands);
            wgpuCommandBufferRelease(copy_commands);
        }
        const auto child_update_begin = trace_picture
            ? cpu_clock::now() : cpu_clock::time_point{};
        if (progpu_native_engine_bind_scene_external_images(child.get(),
                bindings.data(), bindings.size()) !=
                PROGPU_NATIVE_STATUS_SUCCESS ||
            progpu_native_engine_update_scene(child.get(), nested_scene,
                picture.stream_size, nullptr) != PROGPU_NATIVE_STATUS_SUCCESS) {
            cleanup();
            return false;
        }
        const auto child_update_end = trace_picture
            ? cpu_clock::now() : cpu_clock::time_point{};
        std::memcpy(&nested_header, nested_scene, sizeof(nested_header));
        if (seed_texture != nullptr) {
            child_frame.flags |= PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET;
        }
        if (source_clear != nullptr) {
            child_frame.clear_color = {
                source_clear->r * source_clear->a,
                source_clear->g * source_clear->a,
                source_clear->b * source_clear->a,
                source_clear->a};
        }
        child_frame.target_view = reinterpret_cast<std::uintptr_t>(source_view);
        child_frame.scene_id = nested_header.scene_id;
        child_frame.generation = nested_header.generation;
        const auto child_render_begin = trace_picture
            ? cpu_clock::now() : cpu_clock::time_point{};
        if (progpu_native_engine_render_scene(
                child.get(), &child_frame, &child_metrics) !=
            PROGPU_NATIVE_STATUS_SUCCESS) {
            cleanup();
            return false;
        }
        const auto child_render_end = trace_picture
            ? cpu_clock::now() : cpu_clock::time_point{};
        engine.submission_count += child->submission_count;
        child.reset();
        if (trace_picture) {
            const auto to_ms = [](cpu_clock::duration duration) noexcept {
                return std::chrono::duration<double, std::milli>(duration)
                    .count();
            };
            std::fprintf(stderr,
                "ProGPU native picture mask child: scene=%llu, generation=%llu, "
                "streamBytes=%u, source=%ux%u, target=%u,%u/%ux%u, flags=%u, "
                "cacheHit=0, createMs=%.3f, bindUpdateMs=%.3f, "
                "otherPrepareMs=%.3f, renderMs=%.3f\n",
                static_cast<unsigned long long>(nested_header.scene_id),
                static_cast<unsigned long long>(nested_header.generation),
                picture.stream_size, source_width, source_height,
                target_extent.x, target_extent.y, target_extent.width,
                target_extent.height, picture.flags,
                to_ms(child_create_end - child_create_begin),
                to_ms(child_update_end - child_update_begin),
                to_ms((child_create_begin - picture_begin) +
                    (child_update_begin - child_create_end) +
                    (child_render_begin - child_update_end)),
                to_ms(child_render_end - child_render_begin));
            std::fflush(stderr);
        }
    } else if (trace_picture) {
        std::fprintf(stderr,
            "ProGPU native picture mask child: scene=%llu, generation=%llu, "
            "streamBytes=%u, source=%ux%u, target=%u,%u/%ux%u, flags=%u, "
            "cacheHit=1, createMs=0.000, bindUpdateMs=0.000, "
            "otherPrepareMs=0.000, renderMs=0.000\n",
            static_cast<unsigned long long>(nested_header.scene_id),
            static_cast<unsigned long long>(nested_header.generation),
            picture.stream_size, source_width, source_height,
            target_extent.x, target_extent.y, target_extent.width,
            target_extent.height, picture.flags);
        std::fflush(stderr);
    }

    if (image_output != nullptr) {
        // Shared source rasterization; ordinary picture images retain full RGBA
        // and do not allocate the mask-only sampling uniform/bind group.
        image_output->texture = source_texture;
        image_output->view = source_view;
        if (image_metrics != nullptr) *image_metrics = child_metrics;
        return true;
    }

    if (!mask_picture_backing && raster_cache_eligible) {
        try {
            auto backing = std::make_shared<semantic_picture_backing>();
            backing->descriptor = raster_descriptor;
            backing->engine_flags = engine.engine_flags;
            backing->scene.assign(
                nested_scene, nested_scene + picture.stream_size);
            backing->texture = source_texture;
            backing->view = source_view;
            source_texture = nullptr;
            source_view = backing->view;
            webgpu::texture_view_add_ref(source_view);
            mask_picture_backing = std::move(backing);
            retain_picture_raster(engine, nested_header, mask_picture_backing);
        } catch (const std::bad_alloc&) {
            // The current render remains valid with span-owned raw resources.
        }
    }

    gpu_mask_sampling_uniforms sampling{};
    const progpu_native_scene_presentation legacy_presentation{
        sizeof(legacy_presentation), 0U, 0U, target_extent.x + target_extent.width,
        target_extent.y + target_extent.height, dpi_scale, dpi_scale, 0U};
    const auto& parent_presentation = presentation != nullptr ? *presentation : legacy_presentation;
    if (source_extent) {
        std::array<double, 6U> uv_transform{};
        if (!semantic::try_resolve_semantic_mask_uv(picture.transform, picture.bounds,
                target_extent, parent_presentation, dpi_scale, uv_transform)) {
            cleanup();
            return false;
        }
        sampling.coordinate0[0] = static_cast<float>(uv_transform[0]);
        sampling.coordinate0[1] = static_cast<float>(uv_transform[1]);
        sampling.coordinate0[2] = static_cast<float>(uv_transform[2]);
        sampling.coordinate1[0] = static_cast<float>(uv_transform[3]);
        sampling.coordinate1[1] = static_cast<float>(uv_transform[4]);
        sampling.coordinate1[2] = static_cast<float>(uv_transform[5]);
    } else {
        // The child raster uses global parent coordinates. A standalone mask
        // shader receives target-local fragment positions, so include the crop
        // in its UV map instead of relying on composite-only texture offsets.
        sampling.coordinate0[0] =
            1.0F / static_cast<float>(source_width);
        sampling.coordinate1[1] =
            1.0F / static_cast<float>(source_height);
        sampling.coordinate0[2] = static_cast<float>(target_extent.x) / source_width;
        sampling.coordinate1[2] = static_cast<float>(target_extent.y) / source_height;
    }
    sampling.options[0] = 1.0F;
    sampling.options[1] = picture.opacity;
    sampling.options[2] = 1.0F;
    if (composite_state_cursor != nullptr && composite_state != nullptr &&
        (composite_state->flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) != 0U &&
        !composite_state_cursor->has_per_point_guidelines(*composite_state)) {
        // The MIL picture is already rendered in target space. Resample it
        // through the inverse cache-quad deformation, not a second glyph/path
        // rasterization. Canonical sampled-mask shaders already accept this
        // affine UV form. Four scalar coefficients add O(1) work/storage.
        auto bounds = picture.bounds;
        if (source_extent) {
            if (picture.transform.m12 != 0.0F || picture.transform.m21 != 0.0F) {
                cleanup();
                return false;
            }
            const float x0 = bounds.x * picture.transform.m11 + picture.transform.m31;
            const float y0 = bounds.y * picture.transform.m22 + picture.transform.m32;
            const float x1 = (bounds.x + bounds.width) * picture.transform.m11 + picture.transform.m31;
            const float y1 = (bounds.y + bounds.height) * picture.transform.m22 + picture.transform.m32;
            bounds = {std::min(x0, x1), std::min(y0, y1), std::abs(x1 - x0), std::abs(y1 - y0)};
        }
        progpu_native_affine_2d inverse{};
        bool visible = true;
        if (!composite_state_cursor->try_composite_rectangle_inverse(*composite_state, bounds, inverse, visible)) {
            cleanup();
            return false;
        }
        if (!visible) sampling.options[1] = 0.0F;
        // Conjugate logical inverse deformation into target-local physical
        // coordinates. Integer viewport translation is not a snapping phase.
        const float origin_x = static_cast<float>(static_cast<double>(parent_presentation.viewport_x) - target_extent.x);
        const float origin_y = static_cast<float>(static_cast<double>(parent_presentation.viewport_y) - target_extent.y);
        const float tx = inverse.m31 * parent_presentation.dpi_scale_x + (1.0F - inverse.m11) * origin_x;
        const float ty = inverse.m32 * parent_presentation.dpi_scale_y + (1.0F - inverse.m22) * origin_y;
        for (auto* row : {sampling.coordinate0, sampling.coordinate1}) {
            row[2] += row[0] * tx + row[1] * ty;
            row[0] *= inverse.m11;
            row[1] *= inverse.m22;
            if (!std::isfinite(row[0]) || !std::isfinite(row[1]) || !std::isfinite(row[2])) {
                cleanup();
                return false;
            }
        }
        sampling.options[2] = 1.0F;
    }
    // Two selects the RGBA source alpha channel; one retains the existing R8
    // red-channel contract for all other sampled masks.
    sampling.options[3] = 2.0F;
    sampling_uniform_buffer = create_uniform_buffer(
        engine,
        "ProGPU retained picture-mask sampling uniforms",
        sizeof(sampling));
    sampling_bind_group = sampling_uniform_buffer == nullptr
        ? nullptr
        : create_layer_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            source_view,
            "ProGPU retained picture-mask sampling binding",
            sampling_uniform_buffer);
    if (sampling_bind_group == nullptr) {
        cleanup();
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        sampling_uniform_buffer,
        0U,
        &sampling,
        sizeof(sampling));

    operation.mask_picture_backing = std::move(mask_picture_backing);
    operation.mask_texture =
        operation.mask_picture_backing ? nullptr : source_texture;
    operation.mask_texture_view = source_view;
    operation.mask_uniform_buffer = sampling_uniform_buffer;
    operation.mask_bind_group = sampling_bind_group;
    const std::uint64_t uniform_upload_bytes =
        sizeof(sampling) + child_metrics.uniform_upload_bytes;
    operation.mask_uniform_upload_bytes = static_cast<std::uint32_t>(
        std::min<std::uint64_t>(
            uniform_upload_bytes,
            std::numeric_limits<std::uint32_t>::max()));
    operation.mask_source_x = source_extent ? 0U : target_extent.x;
    operation.mask_source_y = source_extent ? 0U : target_extent.y;
    operation.mask_uses_alpha_channel = true;
    source_texture = nullptr;
    source_view = nullptr;
    sampling_uniform_buffer = nullptr;
    sampling_bind_group = nullptr;
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool create_semantic_picture_mask_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_picture_mask& picture,
    const std::byte* nested_scene,
    const semantic::scissor& target_extent, float dpi_scale,
    const semantic::semantic_state_cursor* composite_state_cursor,
    const progpu_native_scene_state* composite_state,
    semantic_render_bundle_span& operation,
    const progpu_native_scene_presentation* presentation) {
    return create_semantic_picture_binding(engine, picture, nested_scene, target_extent,
        dpi_scale, composite_state_cursor, composite_state, operation, nullptr, nullptr, nullptr,
        nullptr, 0U, presentation);
}

bool create_semantic_picture_image(
    progpu_native_engine& engine,
    const progpu_native_scene_picture_image& source,
    const std::byte* nested_scene, std::uint32_t scene_size,
    semantic_image_draw& draw,
    progpu_native_scene_frame_metrics& child_metrics) {
    constexpr std::uint64_t cache_budget = 64ULL * 1024U * 1024U;
    progpu_native_scene_header header{};
    std::memcpy(&header, nested_scene, sizeof(header));
    auto& cache = engine.semantic_picture_cache;
    std::shared_ptr<semantic_picture_backing> previous;
    std::uint32_t first_command = 0U;
    const bool cache_eligible =
        supports_retained_picture_raster(nested_scene, header);
    if (cache_eligible) {
        for (const auto& entry : cache) {
            progpu_native_scene_header prior{};
            std::memcpy(&prior, entry->scene.data(), sizeof(prior));
            if (entry->copy_source_compatible &&
                prior.scene_id == header.scene_id &&
                entry->engine_flags == engine.engine_flags &&
                same_external_image_identity(*entry, engine) &&
                semantic::scene_bytes_equal(std::as_bytes(std::span(&entry->descriptor, 1U)),
                    std::as_bytes(std::span(&source, 1U))) &&
                semantic::find_append_only_scene_suffix(entry->scene.data(), prior, nested_scene, header, first_command)) {
                previous = entry;
                break;
            }
        }
    }
    if (previous && first_command == header.command_count) {
        draw.picture_backing = previous;
        draw.view = previous->view;
        webgpu::texture_view_add_ref(draw.view);
        return true;
    }
    std::shared_ptr<semantic_picture_backing> backing;
    const auto cost = static_cast<std::uint64_t>(source.width) * source.height * 4U + scene_size;
    const bool retain_history = cache_eligible && cost <= cache_budget;
    try {
        backing = std::make_shared<semantic_picture_backing>();
        backing->descriptor = source;
        backing->engine_flags = engine.engine_flags;
        backing->copy_source_compatible = true;
        if (retain_history) backing->scene.assign(nested_scene, nested_scene + scene_size);
        if (retain_history) capture_external_image_identity(*backing, engine);
    } catch (const std::bad_alloc&) {
        return false;
    }
    progpu_native_scene_layer_picture_mask picture{};
    picture.struct_size = sizeof(picture);
    picture.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE;
    picture.stream_size = scene_size;
    picture.bounds = {0.0F, 0.0F, static_cast<float>(source.width), static_cast<float>(source.height)};
    picture.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    picture.opacity = 1.0F;
    semantic_render_bundle_span unused_mask{};
    if (!create_semantic_picture_binding(engine, picture, nested_scene,
        {0U, 0U, source.width, source.height, true}, source.dpi_scale,
        nullptr, nullptr, unused_mask, &draw, &child_metrics, &source.clear_color,
        previous ? previous->texture : nullptr, first_command)) return false;
    backing->texture = draw.texture;
    backing->view = draw.view;
    draw.texture = nullptr;
    webgpu::texture_view_add_ref(draw.view);
    draw.picture_backing = backing;
    if (retain_history) {
        // FIFO eviction is bounded to eight entries. Page draws own independent
        // shared leases, so replacing a cache slot never changes older captures.
        std::uint64_t retained_bytes = 0U;
        for (auto it = cache.begin(); it != cache.end();) {
            progpu_native_scene_header prior{};
            std::memcpy(&prior, (*it)->scene.data(), sizeof(prior));
            if (prior.scene_id == header.scene_id) it = cache.erase(it);
            else { retained_bytes += (*it)->byte_cost(); ++it; }
        }
        while (!cache.empty() &&
            (cache.size() >= retained_picture_image_cache_entries ||
                retained_bytes > cache_budget - cost)) {
            retained_bytes -= cache.front()->byte_cost();
            cache.erase(cache.begin());
        }
        try { cache.push_back(backing); }
        catch (const std::bad_alloc&) { /* Cache retention is optional, drawing is not. */ }
    }
    return true;
}

} // namespace progpu::native::execution
