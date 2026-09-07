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
#include "progpu_native_semantic_replay.hpp"
#include "progpu_native_semantic_state.hpp"
#include "progpu_webgpu_compat.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
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
    WGPUTexture seed_texture = nullptr, std::uint32_t first_command = 0U) {
    if (nested_scene == nullptr || picture.stream_size == 0U ||
        target_extent.width == 0U || target_extent.height == 0U ||
        !std::isfinite(dpi_scale) || dpi_scale <= 0.0F ||
        target_extent.x > 16384U - target_extent.width ||
        target_extent.y > 16384U - target_extent.height ||
        (image_output == nullptr && !create_layer_mask_resources(engine))) {
        return false;
    }

    const bool source_extent =
        (picture.flags & PROGPU_NATIVE_SCENE_PICTURE_MASK_SOURCE_EXTENT) != 0U;
    const std::uint32_t source_width = source_extent
        ? picture.reserved0
        : target_extent.x + target_extent.width;
    const std::uint32_t source_height = source_extent
        ? picture.reserved1
        : target_extent.y + target_extent.height;
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
    const auto cleanup = [&]() noexcept {
        if (sampling_bind_group != nullptr) {
            wgpuBindGroupRelease(sampling_bind_group);
            sampling_bind_group = nullptr;
        }
        release_buffer(sampling_uniform_buffer);
        release_texture(source_texture, source_view);
    };

    WGPUTextureDescriptor source_descriptor{};
    source_descriptor.label = webgpu::string_view(
        "ProGPU retained picture-mask RGBA source");
    source_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    if (image_output != nullptr)
        source_descriptor.usage |= WGPUTextureUsage_CopySrc | WGPUTextureUsage_CopyDst;
    source_descriptor.dimension = WGPUTextureDimension_2D;
    source_descriptor.size = {source_width, source_height, 1U};
    source_descriptor.format = engine.target_format;
    source_descriptor.mipLevelCount = 1U;
    source_descriptor.sampleCount = 1U;
    source_texture = wgpuDeviceCreateTexture(engine.device, &source_descriptor);
    source_view = source_texture == nullptr
        ? nullptr
        : wgpuTextureCreateView(source_texture, nullptr);
    if (source_view == nullptr) {
        cleanup();
        return false;
    }

    progpu_native_engine* child_raw = nullptr;
    if (create_child_engine(engine, engine.target_format, &child_raw) !=
            PROGPU_NATIVE_STATUS_SUCCESS ||
        child_raw == nullptr) {
        cleanup();
        return false;
    }
    child.reset(child_raw);
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
            suffix_scene.assign(nested_scene, nested_scene + picture.stream_size);
        } catch (const std::bad_alloc&) {
            cleanup();
            return false;
        }
        progpu_native_scene_header suffix_header{};
        std::memcpy(&suffix_header, suffix_scene.data(), sizeof(suffix_header));
        if (first_command > suffix_header.command_count) { cleanup(); return false; }
        suffix_header.command_offset += first_command * suffix_header.command_stride;
        suffix_header.command_count -= first_command;
        std::memcpy(suffix_scene.data(), &suffix_header, sizeof(suffix_header));
        nested_scene = suffix_scene.data();
        WGPUCommandEncoder copy_encoder = wgpuDeviceCreateCommandEncoder(engine.device, nullptr);
        if (copy_encoder == nullptr) { cleanup(); return false; }
        webgpu::image_copy_texture source_copy{}, destination_copy{};
        source_copy.texture = seed_texture;
        source_copy.aspect = WGPUTextureAspect_All;
        destination_copy.texture = source_texture;
        destination_copy.aspect = WGPUTextureAspect_All;
        const WGPUExtent3D extent{source_width, source_height, 1U};
        wgpuCommandEncoderCopyTextureToTexture(copy_encoder, &source_copy, &destination_copy, &extent);
        WGPUCommandBuffer copy_commands = wgpuCommandEncoderFinish(copy_encoder, nullptr);
        wgpuCommandEncoderRelease(copy_encoder);
        if (copy_commands == nullptr) { cleanup(); return false; }
        wgpuQueueSubmit(engine.queue, 1U, &copy_commands);
        wgpuCommandBufferRelease(copy_commands);
        ++engine.submission_count;
    }
    if (progpu_native_engine_bind_scene_external_images(
            child.get(),
            bindings.data(),
            bindings.size()) != PROGPU_NATIVE_STATUS_SUCCESS ||
        progpu_native_engine_update_scene(
            child.get(),
            nested_scene,
            picture.stream_size,
            nullptr) != PROGPU_NATIVE_STATUS_SUCCESS) {
        cleanup();
        return false;
    }
    progpu_native_scene_header nested_header{};
    std::memcpy(&nested_header, nested_scene, sizeof(nested_header));
    progpu_native_scene_frame child_frame{};
    child_frame.struct_size = sizeof(child_frame);
    child_frame.width = source_width;
    child_frame.height = source_height;
    const float source_dpi_scale = source_extent
        ? static_cast<float>(source_width) / picture.bounds.width
        : dpi_scale;
    const float source_dpi_scale_y = source_extent
        ? static_cast<float>(source_height) / picture.bounds.height
        : dpi_scale;
    if (!std::isfinite(source_dpi_scale) || source_dpi_scale <= 0.0F ||
        !std::isfinite(source_dpi_scale_y) || source_dpi_scale_y <= 0.0F ||
        std::abs(source_dpi_scale - source_dpi_scale_y) > 0.0001F) {
        cleanup();
        return false;
    }
    child_frame.dpi_scale = source_dpi_scale;
    if (seed_texture != nullptr) child_frame.flags |= PROGPU_NATIVE_SCENE_FRAME_PRESERVE_TARGET;
    if (source_clear != nullptr) {
        child_frame.clear_color = {source_clear->r * source_clear->a,
            source_clear->g * source_clear->a, source_clear->b * source_clear->a, source_clear->a};
    }
    child_frame.target_view = reinterpret_cast<std::uintptr_t>(source_view);
    child_frame.scene_id = nested_header.scene_id;
    child_frame.generation = nested_header.generation;
    progpu_native_scene_frame_metrics child_metrics{};
    child_metrics.struct_size = sizeof(child_metrics);
    if (progpu_native_engine_render_scene(
            child.get(),
            &child_frame,
            &child_metrics) != PROGPU_NATIVE_STATUS_SUCCESS) {
        cleanup();
        return false;
    }
    engine.submission_count += child->submission_count;
    child.reset();

    if (image_output != nullptr) {
        // Shared source rasterization; ordinary picture images retain full RGBA
        // and do not allocate the mask-only sampling uniform/bind group.
        image_output->texture = source_texture;
        image_output->view = source_view;
        if (image_metrics != nullptr) *image_metrics = child_metrics;
        return true;
    }

    gpu_mask_sampling_uniforms sampling{};
    if (source_extent) {
        const double m11 = picture.transform.m11;
        const double m12 = picture.transform.m12;
        const double m21 = picture.transform.m21;
        const double m22 = picture.transform.m22;
        const double m31 = picture.transform.m31;
        const double m32 = picture.transform.m32;
        const double determinant = m11 * m22 - m12 * m21;
        if (!std::isfinite(determinant) || std::abs(determinant) < 1.0e-12) {
            cleanup();
            return false;
        }
        const double inverse_m11 = m22 / determinant;
        const double inverse_m12 = -m12 / determinant;
        const double inverse_m21 = -m21 / determinant;
        const double inverse_m22 = m11 / determinant;
        const double inverse_m31 =
            (m21 * m32 - m22 * m31) / determinant;
        const double inverse_m32 =
            (m12 * m31 - m11 * m32) / determinant;
        const std::array<double, 6U> uv_transform{
            inverse_m11 / (dpi_scale * picture.bounds.width),
            inverse_m21 / (dpi_scale * picture.bounds.width),
            (inverse_m31 - picture.bounds.x) / picture.bounds.width,
            inverse_m12 / (dpi_scale * picture.bounds.height),
            inverse_m22 / (dpi_scale * picture.bounds.height),
            (inverse_m32 - picture.bounds.y) / picture.bounds.height};
        for (double value : uv_transform) {
            if (!std::isfinite(value) ||
                value < -std::numeric_limits<float>::max() ||
                value > std::numeric_limits<float>::max()) {
                cleanup();
                return false;
            }
        }
        sampling.coordinate0[0] = static_cast<float>(uv_transform[0]);
        sampling.coordinate0[1] = static_cast<float>(uv_transform[1]);
        sampling.coordinate0[2] = static_cast<float>(uv_transform[2]);
        sampling.coordinate1[0] = static_cast<float>(uv_transform[3]);
        sampling.coordinate1[1] = static_cast<float>(uv_transform[4]);
        sampling.coordinate1[2] = static_cast<float>(uv_transform[5]);
        sampling.options[2] = 1.0F;
    } else {
        sampling.coordinate1[0] =
            1.0F / static_cast<float>(source_width);
        sampling.coordinate1[1] =
            1.0F / static_cast<float>(source_height);
    }
    sampling.options[0] = 1.0F;
    sampling.options[1] = picture.opacity;
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
        if (!source_extent) {
            sampling.coordinate0[0] = 1.0F / static_cast<float>(source_width);
            sampling.coordinate0[1] = 0.0F;
            sampling.coordinate0[2] = 0.0F;
            sampling.coordinate1[0] = 0.0F;
            sampling.coordinate1[1] = 1.0F / static_cast<float>(source_height);
            sampling.coordinate1[2] = 0.0F;
        }
        const float tx = inverse.m31 * dpi_scale;
        const float ty = inverse.m32 * dpi_scale;
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

    operation.mask_texture = source_texture;
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
    semantic_render_bundle_span& operation) {
    return create_semantic_picture_binding(engine, picture, nested_scene, target_extent,
        dpi_scale, composite_state_cursor, composite_state, operation, nullptr, nullptr, nullptr);
}

bool create_semantic_picture_image(
    progpu_native_engine& engine,
    const progpu_native_scene_picture_image& source,
    const std::byte* nested_scene, std::uint32_t scene_size,
    semantic_image_draw& draw,
    progpu_native_scene_frame_metrics& child_metrics) {
    constexpr std::uint64_t cache_budget = 64ULL * 1024U * 1024U;
    constexpr std::size_t maximum_entries = 8U;
    progpu_native_scene_header header{};
    std::memcpy(&header, nested_scene, sizeof(header));
    auto& cache = engine.semantic_picture_cache;
    std::shared_ptr<semantic_picture_backing> previous;
    std::uint32_t first_command = 0U;
    const bool cache_eligible = engine.semantic_external_image_bindings.empty();
    if (cache_eligible) {
        for (const auto& entry : cache) {
            progpu_native_scene_header prior{};
            std::memcpy(&prior, entry->scene.data(), sizeof(prior));
            if (prior.scene_id == header.scene_id && entry->engine_flags == engine.engine_flags &&
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
        if (retain_history) backing->scene.assign(nested_scene, nested_scene + scene_size);
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
        while (!cache.empty() && (cache.size() >= maximum_entries || retained_bytes > cache_budget - cost)) {
            retained_bytes -= cache.front()->byte_cost();
            cache.erase(cache.begin());
        }
        try { cache.push_back(backing); }
        catch (const std::bad_alloc&) { /* Cache retention is optional, drawing is not. */ }
    }
    return true;
}

} // namespace progpu::native::execution
