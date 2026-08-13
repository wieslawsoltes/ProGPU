#include "progpu_native.h"

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
#include "progpu_native_effect_plan.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_webgpu_resources.hpp"
#include "GaussianBlurHorizontalWgsl.generated.hpp"
#include "GaussianBlurVerticalWgsl.generated.hpp"
#include "GroupDropShadowComposeWgsl.generated.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <unordered_map>
#include <vector>

namespace progpu::native::execution {

using semantic_scissor = semantic::scissor;
using semantic_layer_budget = semantic::layer_budget;
using semantic_compilation_budget = semantic::compilation_budget;
inline constexpr std::uint32_t semantic_effect_uniform_alignment =
    semantic::effect_uniform_alignment;

void apply_scissor(
    WGPURenderPassEncoder pass,
    const resolved_draw_state& state) noexcept {
    if (state.has_clip && state.has_drawable_clip) {
        wgpuRenderPassEncoderSetScissorRect(
            pass,
            state.clip_x,
            state.clip_y,
            state.clip_width,
            state.clip_height);
    }
}

bool update_layer_external_mask(
    progpu_native_engine& engine,
    const progpu_native_group_mask& mask,
    bool& replaced) {
    WGPUTextureView view = reinterpret_cast<WGPUTextureView>(
        mask.external_view);
    replaced = engine.layer_external_mask_view == nullptr ||
        engine.layer_external_mask_view != view ||
        engine.layer_external_mask_width != mask.width ||
        engine.layer_external_mask_height != mask.height;
    if (!replaced) {
        return true;
    }

    ::progpu::native::webgpu::texture_view_add_ref(view);
    WGPUBindGroup nearest = create_layer_mask_bind_group(
        engine,
        engine.image_nearest_sampler,
        view,
        "ProGPU native nearest common group mask bind group");
    WGPUBindGroup linear = create_layer_mask_bind_group(
        engine,
        engine.image_linear_sampler,
        view,
        "ProGPU native linear common group mask bind group");
    if (nearest == nullptr || linear == nullptr) {
        if (linear != nullptr) wgpuBindGroupRelease(linear);
        if (nearest != nullptr) wgpuBindGroupRelease(nearest);
        wgpuTextureViewRelease(view);
        return false;
    }
    if (engine.layer_external_mask_linear_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.layer_external_mask_linear_bind_group);
    }
    if (engine.layer_external_mask_nearest_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.layer_external_mask_nearest_bind_group);
    }
    if (engine.layer_external_mask_view != nullptr) {
        wgpuTextureViewRelease(engine.layer_external_mask_view);
    }
    engine.layer_external_mask_view = view;
    engine.layer_external_mask_nearest_bind_group = nearest;
    engine.layer_external_mask_linear_bind_group = linear;
    engine.layer_external_mask_width = mask.width;
    engine.layer_external_mask_height = mask.height;
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool create_rounded_group_mask_uniforms(
    const progpu_native_group_mask& mask,
    float dpi_scale,
    gpu_mask_sampling_uniforms& uniforms) noexcept {
    const double m11 = static_cast<double>(mask.transform.m11) * dpi_scale;
    const double m12 = static_cast<double>(mask.transform.m12) * dpi_scale;
    const double m21 = static_cast<double>(mask.transform.m21) * dpi_scale;
    const double m22 = static_cast<double>(mask.transform.m22) * dpi_scale;
    const double m31 = static_cast<double>(mask.transform.m31) * dpi_scale;
    const double m32 = static_cast<double>(mask.transform.m32) * dpi_scale;
    const double determinant = m11 * m22 - m12 * m21;
    if (!std::isfinite(determinant) || std::abs(determinant) <= 0.000001) {
        return false;
    }
    const double inverse = 1.0 / determinant;
    const double inverse_m11 = m22 * inverse;
    const double inverse_m12 = -m12 * inverse;
    const double inverse_m21 = -m21 * inverse;
    const double inverse_m22 = m11 * inverse;
    const double inverse_m31 = (m21 * m32 - m22 * m31) * inverse;
    const double inverse_m32 = (m12 * m31 - m11 * m32) * inverse;
    const std::array<double, 6U> inverse_values{
        inverse_m11,
        inverse_m12,
        inverse_m21,
        inverse_m22,
        inverse_m31,
        inverse_m32
    };
    if (!std::ranges::all_of(inverse_values, [](double value) {
            return std::isfinite(value) &&
                value >= -std::numeric_limits<float>::max() &&
                value <= std::numeric_limits<float>::max();
        })) {
        return false;
    }

    uniforms.coordinate0[0] = static_cast<float>(inverse_m11);
    uniforms.coordinate0[1] = static_cast<float>(inverse_m21);
    uniforms.coordinate0[2] = static_cast<float>(inverse_m31);
    uniforms.coordinate1[0] = static_cast<float>(inverse_m12);
    uniforms.coordinate1[1] = static_cast<float>(inverse_m22);
    uniforms.coordinate1[2] = static_cast<float>(inverse_m32);
    uniforms.bounds[0] = mask.bounds.x;
    uniforms.bounds[1] = mask.bounds.y;
    uniforms.bounds[2] = mask.bounds.x + mask.bounds.width;
    uniforms.bounds[3] = mask.bounds.y + mask.bounds.height;
    std::copy_n(
        mask.corner_radii_x,
        4U,
        uniforms.corner_radii_x);
    std::copy_n(
        mask.corner_radii_y,
        4U,
        uniforms.corner_radii_y);
    uniforms.options[0] = 2.0F;
    uniforms.options[1] = mask.opacity;
    return true;
}

bool update_layer_group_mask(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state,
    float dpi_scale,
    bool& uploaded_uniforms) {
    uploaded_uniforms = false;
    const bool resources_existed = engine.layer_mask_pipeline != nullptr;
    if (!draw_state.has_group_mask || !create_layer_mask_resources(engine)) {
        return !draw_state.has_group_mask;
    }

    const auto& mask = draw_state.group_mask;
    gpu_mask_sampling_uniforms uniforms{};
    bool binding_replaced = false;
    if (mask.kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE) {
        if (!update_layer_external_mask(
                engine,
                mask,
                binding_replaced)) {
            return false;
        }
        uniforms.coordinate0[0] =
            mask.destination_rect.x * dpi_scale;
        uniforms.coordinate0[1] =
            mask.destination_rect.y * dpi_scale;
        uniforms.coordinate1[0] = 1.0F /
            (mask.destination_rect.width * dpi_scale);
        uniforms.coordinate1[1] = 1.0F /
            (mask.destination_rect.height * dpi_scale);
        uniforms.options[0] = 1.0F;
    } else if (mask.kind == PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN) {
        const bool was_cache_valid = engine.clip_cache_valid &&
            engine.clip_cached_revision == mask.revision &&
            engine.clip_cached_dpi_scale == dpi_scale &&
            engine.clip_width == engine.layer_width &&
            engine.clip_height == engine.layer_height;
        if (!rebuild_vector_clip_chain(
                engine,
                mask,
                engine.layer_width,
                engine.layer_height,
                dpi_scale)) {
            return false;
        }
        binding_replaced = !was_cache_valid;
        uniforms.coordinate1[0] =
            1.0F / static_cast<float>(engine.layer_width);
        uniforms.coordinate1[1] =
            1.0F / static_cast<float>(engine.layer_height);
        uniforms.options[0] = 1.0F;
    } else if (!create_rounded_group_mask_uniforms(
            mask,
            dpi_scale,
            uniforms)) {
        return false;
    }

    if (!engine.layer_mask_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_layer_mask_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.layer_mask_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_layer_mask_uniforms = uniforms;
        engine.layer_mask_uniform_cache_valid = true;
        uploaded_uniforms = true;
    }
    engine.last_layer_metrics.mask_kind = mask.kind;
    engine.last_layer_metrics.mask_revision = mask.revision;
    engine.last_layer_metrics.mask_bind_group_generation =
        engine.layer_mask_bind_group_generation;
    engine.last_layer_metrics.mask_bind_group_cache_hit =
        resources_existed && !binding_replaced ? 1U : 0U;
    engine.last_layer_metrics.mask_uniform_upload_bytes =
        uploaded_uniforms ? sizeof(uniforms) : 0U;
    return true;
}

bool ensure_layer_texture(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (engine.layer_texture != nullptr &&
        engine.layer_width == width && engine.layer_height == height) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native pooled group layer");
    descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = engine.target_format;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        engine.device,
        &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    WGPUBindGroup bind_group = create_image_texture_bind_group(
        engine,
        engine.image_linear_sampler,
        view,
        "ProGPU native pooled group layer bind group");
    if (bind_group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    if (engine.layer_texture_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.layer_texture_bind_group);
    }
    if (engine.layer_texture_view != nullptr) {
        wgpuTextureViewRelease(engine.layer_texture_view);
    }
    if (engine.layer_texture != nullptr) {
        wgpuTextureDestroy(engine.layer_texture);
        wgpuTextureRelease(engine.layer_texture);
    }
    engine.layer_texture = texture;
    engine.layer_texture_view = view;
    engine.layer_texture_bind_group = bind_group;
    engine.layer_width = width;
    engine.layer_height = height;
    engine.layer_content_cache_valid = false;
    engine.layer_vertex_cache_valid = false;
    ++engine.layer_texture_generation;
    ++engine.layer_allocation_count;
    return true;
}

WGPUBindGroup create_semantic_text_uniform_bind_group(
    progpu_native_engine& engine,
    WGPUBuffer uniform_buffer) {
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, uniform_buffer, 0U,
            sizeof(gpu_uniforms), nullptr, nullptr},
        {nullptr, 1U, engine.text_style_buffer, 0U,
            32U, nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU semantic bounded-layer text uniforms");
    descriptor.layout = engine.text_uniform_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

WGPUBindGroup create_semantic_image_uniform_bind_group(
    progpu_native_engine& engine,
    WGPUBuffer uniform_buffer) {
    WGPUBindGroupEntry entry{};
    entry.binding = 0U;
    entry.buffer = uniform_buffer;
    entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU semantic bounded-layer image uniforms");
    descriptor.layout = engine.image_uniform_layout;
    descriptor.entryCount = 1U;
    descriptor.entries = &entry;
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool ensure_semantic_layer_slot_bindings(
    progpu_native_engine& engine,
    semantic_layer_slot& slot) {
    if (engine.image_uniform_layout != nullptr &&
        slot.image_uniform_bind_group == nullptr) {
        slot.image_uniform_bind_group =
            create_semantic_image_uniform_bind_group(
                engine,
                slot.uniform_buffer);
        if (slot.image_uniform_bind_group == nullptr) {
            return false;
        }
    }
    if (engine.analytic_uniform_layout != nullptr &&
        engine.analytic_brush_buffer != nullptr &&
        engine.analytic_gradient_buffer != nullptr &&
        (slot.analytic_uniform_bind_group == nullptr ||
            slot.bound_analytic_brush_buffer !=
                engine.analytic_brush_buffer)) {
        if (slot.analytic_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(slot.analytic_uniform_bind_group);
        }
        slot.analytic_uniform_bind_group =
            create_analytic_uniform_bind_group_for_buffer(
                engine,
                slot.uniform_buffer,
                engine.analytic_brush_buffer,
                engine.analytic_brush_buffer_size,
                "ProGPU semantic bounded-layer analytic uniforms");
        if (slot.analytic_uniform_bind_group == nullptr) {
            slot.bound_analytic_brush_buffer = nullptr;
            return false;
        }
        slot.bound_analytic_brush_buffer = engine.analytic_brush_buffer;
    }
    if (engine.text_uniform_layout != nullptr &&
        engine.text_style_buffer != nullptr &&
        (slot.text_uniform_bind_group == nullptr ||
            slot.bound_text_style_buffer != engine.text_style_buffer)) {
        if (slot.text_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(slot.text_uniform_bind_group);
        }
        slot.text_uniform_bind_group =
            create_semantic_text_uniform_bind_group(
                engine,
                slot.uniform_buffer);
        if (slot.text_uniform_bind_group == nullptr) {
            slot.bound_text_style_buffer = nullptr;
            return false;
        }
        slot.bound_text_style_buffer = engine.text_style_buffer;
    }
    return true;
}

void release_semantic_effect_bindings(
    semantic_layer_slot& slot) noexcept;

bool ensure_semantic_layer_slot(
    progpu_native_engine& engine,
    std::uint32_t index,
    std::uint32_t width,
    std::uint32_t height) {
    if (index >= engine.semantic_layer_slots.size()) {
        return false;
    }
    auto& slot = engine.semantic_layer_slots[index];
    if (slot.texture != nullptr && slot.uniform_buffer != nullptr &&
        slot.width == width && slot.height == height) {
        return ensure_semantic_layer_slot_bindings(engine, slot);
    }

    WGPUTextureDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU semantic depth-indexed isolated layer");
    descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = engine.target_format;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        engine.device,
        &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    WGPUBindGroup bind_group = create_image_texture_bind_group(
        engine,
        engine.image_linear_sampler,
        view,
        "ProGPU semantic isolated-layer texture binding");
    if (bind_group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU semantic bounded-layer target uniforms");
    uniform_descriptor.usage =
        WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    WGPUBuffer uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    if (uniform_buffer == nullptr) {
        wgpuBindGroupRelease(bind_group);
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    release_semantic_effect_bindings(slot);
    ::progpu::native::effects::invalidate_semantic_output_cache(
        slot.effect_output_cache);
    if (slot.analytic_uniform_bind_group != nullptr) {
        wgpuBindGroupRelease(slot.analytic_uniform_bind_group);
    }
    if (slot.text_uniform_bind_group != nullptr) {
        wgpuBindGroupRelease(slot.text_uniform_bind_group);
    }
    if (slot.image_uniform_bind_group != nullptr) {
        wgpuBindGroupRelease(slot.image_uniform_bind_group);
    }
    if (slot.bind_group != nullptr) {
        wgpuBindGroupRelease(slot.bind_group);
    }
    if (slot.view != nullptr) {
        wgpuTextureViewRelease(slot.view);
    }
    if (slot.texture != nullptr) {
        wgpuTextureDestroy(slot.texture);
        wgpuTextureRelease(slot.texture);
    }
    if (slot.uniform_buffer != nullptr) {
        wgpuBufferDestroy(slot.uniform_buffer);
        wgpuBufferRelease(slot.uniform_buffer);
    }
    slot.texture = texture;
    slot.view = view;
    slot.bind_group = bind_group;
    slot.uniform_buffer = uniform_buffer;
    slot.analytic_uniform_bind_group = nullptr;
    slot.text_uniform_bind_group = nullptr;
    slot.image_uniform_bind_group = nullptr;
    slot.bound_analytic_brush_buffer = nullptr;
    slot.bound_text_style_buffer = nullptr;
    slot.uniform_cache_valid = false;
    slot.width = width;
    slot.height = height;
    ++slot.generation;
    ++engine.semantic_layer_allocation_count;
    return ensure_semantic_layer_slot_bindings(engine, slot);
}

bool ensure_semantic_layer_vertex_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_bytes) {
    if (required_bytes == 0U ||
        (engine.semantic_layer_vertex_buffer != nullptr &&
            required_bytes <= engine.semantic_layer_vertex_buffer_size)) {
        return true;
    }
    std::uint64_t capacity = std::max<std::uint64_t>(256U,
        engine.semantic_layer_vertex_buffer_size);
    while (capacity < required_bytes) {
        if (capacity > std::numeric_limits<std::uint64_t>::max() / 2U) {
            return false;
        }
        capacity *= 2U;
    }
    WGPUBufferDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU semantic isolated-layer composite vertices");
    descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
    descriptor.size = capacity;
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (buffer == nullptr) {
        return false;
    }
    if (engine.semantic_layer_vertex_buffer != nullptr) {
        wgpuBufferDestroy(engine.semantic_layer_vertex_buffer);
        wgpuBufferRelease(engine.semantic_layer_vertex_buffer);
    }
    engine.semantic_layer_vertex_buffer = buffer;
    engine.semantic_layer_vertex_buffer_size = capacity;
    return true;
}

bool ensure_semantic_effect_textures(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::uint32_t width,
    std::uint32_t height);

bool prepare_semantic_layer_resources(
    progpu_native_engine& engine,
    const semantic_layer_budget& budget,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale,
    std::uint32_t composite_count,
    std::uint64_t& uploaded_uniform_bytes) {
    uploaded_uniform_bytes = 0U;
    if (!create_layer_resources(engine)) {
        return false;
    }
    for (std::uint32_t index = 0U;
         index < budget.peak_materialized_depth;
         ++index) {
        if (!ensure_semantic_layer_slot(
                engine,
                index,
                budget.slot_widths[index],
                budget.slot_heights[index])) {
            return false;
        }
        auto& slot = engine.semantic_layer_slots[index];
        if (budget.slot_effected[index] &&
            !ensure_semantic_effect_textures(
                engine,
                slot,
                budget.slot_widths[index],
                budget.slot_heights[index])) {
            return false;
        }
        const gpu_uniforms uniforms = create_uniforms(
            slot.width,
            slot.height,
            dpi_scale);
        if (engine.upload_uniform_if_changed(
                slot.uniform_buffer,
                uniforms,
                slot.cached_uniforms,
                slot.uniform_cache_valid)) {
            uploaded_uniform_bytes += sizeof(gpu_uniforms);
        }
    }
    const std::uint64_t required_vertex_bytes =
        static_cast<std::uint64_t>(composite_count) * 4U *
        sizeof(::progpu::native::vector_vertex);
    if (!ensure_semantic_layer_vertex_buffer(
            engine,
            required_vertex_bytes)) {
        return false;
    }
    const gpu_uniforms uniforms = create_uniforms(
        frame_width,
        frame_height,
        dpi_scale);
    const bool uploaded_composite_uniforms =
        engine.upload_uniform_if_changed(
        engine.layer_uniform_buffer,
        uniforms,
        engine.cached_layer_uniforms,
        engine.layer_uniform_cache_valid);
    uploaded_uniform_bytes += uploaded_composite_uniforms
        ? sizeof(gpu_uniforms)
        : 0U;
    return true;
}

void append_semantic_layer_quad(
    std::vector<::progpu::native::vector_vertex>& vertices,
    const semantic_scissor& source,
    const semantic_scissor& target,
    std::uint32_t source_texture_width,
    std::uint32_t source_texture_height,
    float dpi_scale,
    float opacity) {
    const float x0 = static_cast<float>(source.x - target.x) / dpi_scale;
    const float y0 = static_cast<float>(source.y - target.y) / dpi_scale;
    const float x1 = x0 + static_cast<float>(source.width) / dpi_scale;
    const float y1 = y0 + static_cast<float>(source.height) / dpi_scale;
    const float u1 = static_cast<float>(source.width) /
        source_texture_width;
    const float v1 = static_cast<float>(source.height) /
        source_texture_height;
    constexpr std::array<std::array<std::uint32_t, 2U>, 4U> corners{{
        {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
    }};
    for (const auto& corner : corners) {
        ::progpu::native::vector_vertex vertex{};
        vertex.position[0] = corner[0] == 0U ? x0 : x1;
        vertex.position[1] = corner[1] == 0U ? y0 : y1;
        vertex.color[0] = opacity;
        vertex.color[1] = 1.0F;
        vertex.color[2] = 0.0F;
        vertex.color[3] = opacity;
        vertex.texture_coordinate[0] = corner[0] == 0U ? 0.0F : u1;
        vertex.texture_coordinate[1] = corner[1] == 0U ? 0.0F : v1;
        vertex.stroke_thickness = 1.0F;
        vertices.push_back(vertex);
    }
}

bool create_semantic_layer_mask_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_mask& source,
    const semantic_scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation) {
    if (!create_layer_mask_resources(engine)) {
        return false;
    }

    progpu_native_group_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE;
    mask.bounds = source.bounds;
    mask.transform = source.transform;
    mask.transform.m31 -=
        static_cast<float>(target_extent.x) / dpi_scale;
    mask.transform.m32 -=
        static_cast<float>(target_extent.y) / dpi_scale;
    std::copy_n(source.corner_radii_x, 4U, mask.corner_radii_x);
    std::copy_n(source.corner_radii_y, 4U, mask.corner_radii_y);
    mask.opacity = source.opacity;
    normalize_group_mask_radii(mask);

    gpu_mask_sampling_uniforms uniforms{};
    if (!create_rounded_group_mask_uniforms(mask, dpi_scale, uniforms)) {
        return false;
    }

    WGPUBufferDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU retained semantic layer mask uniforms");
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    descriptor.size = sizeof(uniforms);
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (buffer == nullptr) {
        return false;
    }
    WGPUBindGroup bind_group = create_layer_mask_bind_group(
        engine,
        engine.image_linear_sampler,
        engine.layer_mask_dummy_view,
        "ProGPU retained semantic analytic mask binding",
        buffer);
    if (bind_group == nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        buffer,
        0U,
        &uniforms,
        sizeof(uniforms));
    operation.mask_uniform_buffer = buffer;
    operation.mask_bind_group = bind_group;
    ++engine.layer_mask_bind_group_generation;
    return true;
}

WGPUBindGroup create_effect_blur_bind_group(
    progpu_native_engine& engine,
    WGPUTextureView input,
    WGPUTextureView output,
    WGPUBuffer uniforms,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, input},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, output},
        {nullptr, 2U, uniforms, 0U,
            sizeof(gpu_gaussian_blur_params), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.effect_blur_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_gaussian_effect_resources(progpu_native_engine& engine) {
    if (engine.effect_blur_horizontal_pipeline != nullptr &&
        engine.effect_blur_vertical_pipeline != nullptr &&
        engine.effect_blur_layout != nullptr) {
        return true;
    }
    if (engine.effect_blur_horizontal_shader != nullptr ||
        engine.effect_blur_vertical_shader != nullptr ||
        engine.effect_blur_horizontal_pipeline != nullptr ||
        engine.effect_blur_vertical_pipeline != nullptr ||
        engine.effect_blur_layout != nullptr ||
        engine.effect_blur_horizontal_uniform_buffer != nullptr ||
        engine.effect_blur_vertical_uniform_buffer != nullptr) {
        engine.release_effect_resources();
    }

    ::progpu::native::webgpu::wgsl_source horizontal_wgsl(
        ::progpu::native::generated::gaussian_blur_horizontal_wgsl,
        ::progpu::native::generated::gaussian_blur_horizontal_wgsl_size);
    WGPUShaderModuleDescriptor horizontal_descriptor{};
    horizontal_descriptor.nextInChain = horizontal_wgsl.chain();
    horizontal_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU shared GaussianBlurHorizontal.wgsl");
    engine.effect_blur_horizontal_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &horizontal_descriptor);
    ::progpu::native::webgpu::wgsl_source vertical_wgsl(
        ::progpu::native::generated::gaussian_blur_vertical_wgsl,
        ::progpu::native::generated::gaussian_blur_vertical_wgsl_size);
    WGPUShaderModuleDescriptor vertical_descriptor{};
    vertical_descriptor.nextInChain = vertical_wgsl.chain();
    vertical_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU shared GaussianBlurVertical.wgsl");
    engine.effect_blur_vertical_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &vertical_descriptor);
    if (engine.effect_blur_horizontal_shader == nullptr ||
        engine.effect_blur_vertical_shader == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Compute;
    entries[0].texture.sampleType = WGPUTextureSampleType_Float;
    entries[0].texture.viewDimension = WGPUTextureViewDimension_2D;
    entries[0].texture.multisampled = false;
    entries[1].binding = 1U;
    entries[1].visibility = WGPUShaderStage_Compute;
    entries[1].storageTexture.access = WGPUStorageTextureAccess_WriteOnly;
    entries[1].storageTexture.format = WGPUTextureFormat_RGBA8Unorm;
    entries[1].storageTexture.viewDimension = WGPUTextureViewDimension_2D;
    entries[2].binding = 2U;
    entries[2].visibility = WGPUShaderStage_Compute;
    entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    entries[2].buffer.hasDynamicOffset = true;
    entries[2].buffer.minBindingSize = sizeof(gpu_gaussian_blur_params);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.effect_blur_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.effect_blur_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.effect_blur_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.compute.entryPoint =
        ::progpu::native::webgpu::string_view("main");
    pipeline_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native horizontal Gaussian group effect");
    pipeline_descriptor.compute.module =
        engine.effect_blur_horizontal_shader;
    engine.effect_blur_horizontal_pipeline =
        wgpuDeviceCreateComputePipeline(engine.device, &pipeline_descriptor);
    pipeline_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native vertical Gaussian group effect");
    pipeline_descriptor.compute.module = engine.effect_blur_vertical_shader;
    engine.effect_blur_vertical_pipeline =
        wgpuDeviceCreateComputePipeline(engine.device, &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.effect_blur_horizontal_pipeline == nullptr ||
        engine.effect_blur_vertical_pipeline == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_gaussian_blur_params);
    buffer_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native horizontal Gaussian effect uniforms");
    engine.effect_blur_horizontal_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    buffer_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native vertical Gaussian effect uniforms");
    engine.effect_blur_vertical_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (engine.effect_blur_horizontal_uniform_buffer == nullptr ||
        engine.effect_blur_vertical_uniform_buffer == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    return true;
}

bool create_drop_shadow_effect_resources(progpu_native_engine& engine) {
    if (engine.effect_drop_shadow_pipeline != nullptr &&
        engine.effect_drop_shadow_layout != nullptr &&
        engine.effect_drop_shadow_uniform_buffer != nullptr) {
        return true;
    }
    if (engine.effect_drop_shadow_shader != nullptr ||
        engine.effect_drop_shadow_pipeline != nullptr ||
        engine.effect_drop_shadow_layout != nullptr ||
        engine.effect_drop_shadow_uniform_buffer != nullptr) {
        engine.release_effect_resources();
        return false;
    }

    ::progpu::native::webgpu::wgsl_source wgsl(
        ::progpu::native::generated::group_drop_shadow_compose_wgsl,
        ::progpu::native::generated::group_drop_shadow_compose_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU shared GroupDropShadowCompose.wgsl");
    engine.effect_drop_shadow_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.effect_drop_shadow_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    for (std::uint32_t index = 0U; index < 2U; ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Compute;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    entries[2].binding = 2U;
    entries[2].visibility = WGPUShaderStage_Compute;
    entries[2].storageTexture.access = WGPUStorageTextureAccess_WriteOnly;
    entries[2].storageTexture.format = WGPUTextureFormat_RGBA8Unorm;
    entries[2].storageTexture.viewDimension = WGPUTextureViewDimension_2D;
    entries[3].binding = 3U;
    entries[3].visibility = WGPUShaderStage_Compute;
    entries[3].buffer.type = WGPUBufferBindingType_Uniform;
    entries[3].buffer.hasDynamicOffset = true;
    entries[3].buffer.minBindingSize = sizeof(gpu_drop_shadow_params);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.effect_drop_shadow_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.effect_drop_shadow_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts =
        &engine.effect_drop_shadow_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group effect");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.compute.module = engine.effect_drop_shadow_shader;
    pipeline_descriptor.compute.entryPoint =
        ::progpu::native::webgpu::string_view("main");
    engine.effect_drop_shadow_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.effect_drop_shadow_pipeline == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_drop_shadow_params);
    engine.effect_drop_shadow_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (engine.effect_drop_shadow_uniform_buffer == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    return true;
}

bool ensure_drop_shadow_effect_bindings(progpu_native_engine& engine) {
    if (engine.effect_drop_shadow_bind_group != nullptr &&
        engine.effect_drop_shadow_output_bind_group != nullptr) {
        return true;
    }
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, engine.layer_texture_view},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr,
            engine.effect_texture_views[1]},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr,
            engine.effect_texture_views[0]},
        {nullptr, 3U, engine.effect_drop_shadow_uniform_buffer, 0U,
            sizeof(gpu_drop_shadow_params), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect binding");
    descriptor.layout = engine.effect_drop_shadow_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    engine.effect_drop_shadow_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &descriptor);
    engine.effect_drop_shadow_output_bind_group =
        create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            engine.effect_texture_views[0],
            "ProGPU native drop-shadow group-effect output binding");
    if (engine.effect_drop_shadow_bind_group == nullptr ||
        engine.effect_drop_shadow_output_bind_group == nullptr) {
        if (engine.effect_drop_shadow_output_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.effect_drop_shadow_output_bind_group);
            engine.effect_drop_shadow_output_bind_group = nullptr;
        }
        if (engine.effect_drop_shadow_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.effect_drop_shadow_bind_group);
            engine.effect_drop_shadow_bind_group = nullptr;
        }
        return false;
    }
    return true;
}

bool ensure_gaussian_effect_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (engine.effect_textures[0] != nullptr &&
        engine.effect_width == width && engine.effect_height == height &&
        engine.effect_blur_horizontal_bind_group != nullptr &&
        engine.effect_blur_vertical_bind_group != nullptr &&
        engine.effect_output_bind_group != nullptr) {
        return true;
    }

    WGPUTextureDescriptor descriptor{};
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_StorageBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    std::array<WGPUTexture, 2U> textures{};
    std::array<WGPUTextureView, 2U> views{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect horizontal texture");
    textures[0] = wgpuDeviceCreateTexture(engine.device, &descriptor);
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect vertical texture");
    textures[1] = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (textures[0] == nullptr || textures[1] == nullptr) {
        for (auto texture : textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
            }
        }
        return false;
    }
    views[0] = wgpuTextureCreateView(textures[0], nullptr);
    views[1] = wgpuTextureCreateView(textures[1], nullptr);
    if (views[0] == nullptr || views[1] == nullptr) {
        for (auto view : views) {
            if (view != nullptr) wgpuTextureViewRelease(view);
        }
        for (auto texture : textures) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
        return false;
    }
    WGPUBindGroup horizontal = create_effect_blur_bind_group(
        engine,
        engine.layer_texture_view,
        views[0],
        engine.effect_blur_horizontal_uniform_buffer,
        "ProGPU native horizontal Gaussian effect binding");
    WGPUBindGroup vertical = create_effect_blur_bind_group(
        engine,
        views[0],
        views[1],
        engine.effect_blur_vertical_uniform_buffer,
        "ProGPU native vertical Gaussian effect binding");
    WGPUBindGroup output = create_image_texture_bind_group(
        engine,
        engine.image_linear_sampler,
        views[1],
        "ProGPU native Gaussian group-effect output binding");
    if (horizontal == nullptr || vertical == nullptr || output == nullptr) {
        if (output != nullptr) wgpuBindGroupRelease(output);
        if (vertical != nullptr) wgpuBindGroupRelease(vertical);
        if (horizontal != nullptr) wgpuBindGroupRelease(horizontal);
        for (auto view : views) wgpuTextureViewRelease(view);
        for (auto texture : textures) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
        return false;
    }

    if (engine.effect_drop_shadow_output_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_drop_shadow_output_bind_group);
        engine.effect_drop_shadow_output_bind_group = nullptr;
    }
    if (engine.effect_drop_shadow_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_drop_shadow_bind_group);
        engine.effect_drop_shadow_bind_group = nullptr;
    }
    if (engine.effect_output_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_output_bind_group);
    }
    if (engine.effect_blur_vertical_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_blur_vertical_bind_group);
    }
    if (engine.effect_blur_horizontal_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_blur_horizontal_bind_group);
    }
    for (auto view : engine.effect_texture_views) {
        if (view != nullptr) wgpuTextureViewRelease(view);
    }
    for (auto texture : engine.effect_textures) {
        if (texture != nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
    }
    engine.effect_textures = textures;
    engine.effect_texture_views = views;
    engine.effect_blur_horizontal_bind_group = horizontal;
    engine.effect_blur_vertical_bind_group = vertical;
    engine.effect_output_bind_group = output;
    engine.effect_width = width;
    engine.effect_height = height;
    engine.effect_cache_valid = false;
    ++engine.effect_texture_generation;
    ++engine.effect_allocation_count;
    return true;
}

bool prepare_gaussian_effect(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    return create_gaussian_effect_resources(engine) &&
        ensure_gaussian_effect_textures(engine, width, height);
}

void release_effect_chain_node_bindings(
    progpu_native_engine& engine) noexcept {
    for (auto& bind_group : engine.effect_chain_drop_shadow_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : engine.effect_chain_blur_vertical_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : engine.effect_chain_blur_horizontal_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    engine.effect_chain_bindings_valid = false;
}

bool ensure_effect_chain_uniform_buffers(progpu_native_engine& engine) {
    if (engine.effect_chain_blur_horizontal_uniform_buffers[0] != nullptr &&
        engine.effect_chain_blur_vertical_uniform_buffers[0] != nullptr &&
        engine.effect_chain_drop_shadow_uniform_buffers[0] != nullptr) {
        return true;
    }
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS> horizontal{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS> vertical{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS> drop{};
    const auto release = [](auto& buffers) {
        for (auto buffer : buffers) {
            if (buffer != nullptr) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
            }
        }
    };
    WGPUBufferDescriptor descriptor{};
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    for (std::uint32_t index = 0U;
         index < PROGPU_NATIVE_MAX_GROUP_EFFECTS;
         ++index) {
        descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU native effect-chain horizontal uniforms");
        descriptor.size = sizeof(gpu_gaussian_blur_params);
        horizontal[index] = wgpuDeviceCreateBuffer(
            engine.device,
            &descriptor);
        descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU native effect-chain vertical uniforms");
        vertical[index] = wgpuDeviceCreateBuffer(engine.device, &descriptor);
        descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU native effect-chain drop-shadow uniforms");
        descriptor.size = sizeof(gpu_drop_shadow_params);
        drop[index] = wgpuDeviceCreateBuffer(engine.device, &descriptor);
        if (horizontal[index] == nullptr || vertical[index] == nullptr ||
            drop[index] == nullptr) {
            release(drop);
            release(vertical);
            release(horizontal);
            return false;
        }
    }
    engine.effect_chain_blur_horizontal_uniform_buffers = horizontal;
    engine.effect_chain_blur_vertical_uniform_buffers = vertical;
    engine.effect_chain_drop_shadow_uniform_buffers = drop;
    return true;
}

bool ensure_effect_chain_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (engine.effect_chain_textures[0] != nullptr &&
        engine.effect_chain_width == width &&
        engine.effect_chain_height == height) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_StorageBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    std::array<WGPUTexture, 3U> textures{};
    std::array<WGPUTextureView, 3U> views{};
    std::array<WGPUBindGroup, 3U> outputs{};
    for (std::uint32_t index = 0U; index < 3U; ++index) {
        descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU native bounded effect-chain texture");
        textures[index] = wgpuDeviceCreateTexture(engine.device, &descriptor);
        if (textures[index] != nullptr) {
            views[index] = wgpuTextureCreateView(textures[index], nullptr);
        }
        if (views[index] != nullptr) {
            outputs[index] = create_image_texture_bind_group(
                engine,
                engine.image_linear_sampler,
                views[index],
                "ProGPU native bounded effect-chain output binding");
        }
        if (textures[index] == nullptr || views[index] == nullptr ||
            outputs[index] == nullptr) {
            for (auto output : outputs) {
                if (output != nullptr) wgpuBindGroupRelease(output);
            }
            for (auto view : views) {
                if (view != nullptr) wgpuTextureViewRelease(view);
            }
            for (auto texture : textures) {
                if (texture != nullptr) {
                    wgpuTextureDestroy(texture);
                    wgpuTextureRelease(texture);
                }
            }
            return false;
        }
    }

    release_effect_chain_node_bindings(engine);
    for (auto& output : engine.effect_chain_output_bind_groups) {
        if (output != nullptr) wgpuBindGroupRelease(output);
    }
    for (auto& view : engine.effect_chain_texture_views) {
        if (view != nullptr) wgpuTextureViewRelease(view);
    }
    for (auto& texture : engine.effect_chain_textures) {
        if (texture != nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
    }
    engine.effect_chain_textures = textures;
    engine.effect_chain_texture_views = views;
    engine.effect_chain_output_bind_groups = outputs;
    engine.effect_chain_width = width;
    engine.effect_chain_height = height;
    engine.effect_cache_valid = false;
    ++engine.effect_chain_texture_generation;
    ++engine.effect_chain_allocation_count;
    return true;
}

WGPUBindGroup create_effect_chain_drop_shadow_bind_group(
    progpu_native_engine& engine,
    WGPUTextureView source,
    WGPUTextureView blurred,
    WGPUTextureView output,
    WGPUBuffer uniforms) {
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, source},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, blurred},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, output},
        {nullptr, 3U, uniforms, 0U,
            sizeof(gpu_drop_shadow_params), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native bounded effect-chain drop-shadow binding");
    descriptor.layout = engine.effect_drop_shadow_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

void release_semantic_effect_bindings(
    semantic_layer_slot& slot) noexcept {
    for (auto& bind_group : slot.effect_drop_shadow_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : slot.effect_blur_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
}

void release_semantic_effect_textures(
    semantic_layer_slot& slot) noexcept {
    ::progpu::native::effects::invalidate_semantic_output_cache(
        slot.effect_output_cache);
    release_semantic_effect_bindings(slot);
    for (auto& bind_group : slot.effect_output_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& view : slot.effect_views) {
        if (view != nullptr) {
            wgpuTextureViewRelease(view);
            view = nullptr;
        }
    }
    for (auto& texture : slot.effect_textures) {
        if (texture != nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            texture = nullptr;
        }
    }
    slot.effect_width = 0U;
    slot.effect_height = 0U;
}

bool ensure_semantic_effect_uniform_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_bytes) {
    if (required_bytes == 0U) {
        return true;
    }
    if (engine.semantic_effect_uniform_buffer != nullptr &&
        required_bytes <= engine.semantic_effect_uniform_buffer_size) {
        return true;
    }
    std::uint64_t capacity = std::max<std::uint64_t>(
        semantic_effect_uniform_alignment,
        engine.semantic_effect_uniform_buffer_size);
    while (capacity < required_bytes) {
        if (capacity > std::numeric_limits<std::uint64_t>::max() / 2U) {
            return false;
        }
        capacity *= 2U;
    }
    WGPUBufferDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU retained semantic effect uniforms");
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    descriptor.size = capacity;
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (buffer == nullptr) {
        return false;
    }
    for (auto& slot : engine.semantic_layer_slots) {
        release_semantic_effect_bindings(slot);
    }
    if (engine.semantic_effect_uniform_buffer != nullptr) {
        wgpuBufferDestroy(engine.semantic_effect_uniform_buffer);
        wgpuBufferRelease(engine.semantic_effect_uniform_buffer);
    }
    engine.semantic_effect_uniform_buffer = buffer;
    engine.semantic_effect_uniform_buffer_size = capacity;
    ++engine.semantic_effect_allocation_count;
    return true;
}

bool ensure_semantic_effect_textures(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::uint32_t width,
    std::uint32_t height) {
    if (slot.effect_textures[0] != nullptr &&
        slot.effect_width == width && slot.effect_height == height) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU semantic depth-indexed effect intermediate");
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_StorageBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    std::array<WGPUTexture, 3U> textures{};
    std::array<WGPUTextureView, 3U> views{};
    std::array<WGPUBindGroup, 3U> outputs{};
    for (std::uint32_t index = 0U; index < textures.size(); ++index) {
        textures[index] = wgpuDeviceCreateTexture(engine.device, &descriptor);
        if (textures[index] != nullptr) {
            views[index] = wgpuTextureCreateView(textures[index], nullptr);
        }
        if (views[index] != nullptr) {
            outputs[index] = create_image_texture_bind_group(
                engine,
                engine.image_linear_sampler,
                views[index],
                "ProGPU semantic effect output binding");
        }
        if (textures[index] == nullptr || views[index] == nullptr ||
            outputs[index] == nullptr) {
            for (auto output : outputs) {
                if (output != nullptr) wgpuBindGroupRelease(output);
            }
            for (auto view : views) {
                if (view != nullptr) wgpuTextureViewRelease(view);
            }
            for (auto texture : textures) {
                if (texture != nullptr) {
                    wgpuTextureDestroy(texture);
                    wgpuTextureRelease(texture);
                }
            }
            return false;
        }
    }
    release_semantic_effect_textures(slot);
    slot.effect_textures = textures;
    slot.effect_views = views;
    slot.effect_output_bind_groups = outputs;
    slot.effect_width = width;
    slot.effect_height = height;
    ++slot.effect_generation;
    ++engine.semantic_effect_allocation_count;
    return true;
}

WGPUTextureView semantic_effect_source_view(
    const semantic_layer_slot& slot,
    std::int32_t source) noexcept {
    return source < 0
        ? slot.view
        : source < static_cast<std::int32_t>(slot.effect_views.size())
            ? slot.effect_views[static_cast<std::uint32_t>(source)]
            : nullptr;
}

WGPUBindGroup get_or_create_semantic_effect_blur_binding(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::int32_t source,
    std::uint32_t output) {
    const std::uint32_t source_index = source < 0
        ? 0U
        : static_cast<std::uint32_t>(source) + 1U;
    if (source_index >= 4U || output >= 3U) {
        return nullptr;
    }
    const std::uint32_t binding_index = source_index * 3U + output;
    auto& binding = slot.effect_blur_bind_groups[binding_index];
    if (binding == nullptr) {
        binding = create_effect_blur_bind_group(
            engine,
            semantic_effect_source_view(slot, source),
            slot.effect_views[output],
            engine.semantic_effect_uniform_buffer,
            "ProGPU semantic dynamic effect blur binding");
        engine.semantic_effect_allocation_count += binding != nullptr
            ? 1U
            : 0U;
    }
    return binding;
}

WGPUBindGroup get_or_create_semantic_effect_drop_shadow_binding(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::int32_t source,
    std::uint32_t blurred,
    std::uint32_t output) {
    const std::uint32_t source_index = source < 0
        ? 0U
        : static_cast<std::uint32_t>(source) + 1U;
    if (source_index >= 4U || blurred >= 3U || output >= 3U) {
        return nullptr;
    }
    const std::uint32_t binding_index =
        source_index * 9U + blurred * 3U + output;
    auto& binding = slot.effect_drop_shadow_bind_groups[binding_index];
    if (binding == nullptr) {
        binding = create_effect_chain_drop_shadow_bind_group(
            engine,
            semantic_effect_source_view(slot, source),
            slot.effect_views[blurred],
            slot.effect_views[output],
            engine.semantic_effect_uniform_buffer);
        engine.semantic_effect_allocation_count += binding != nullptr
            ? 1U
            : 0U;
    }
    return binding;
}

bool ensure_effect_chain_bindings(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state) {
    bool same_topology = engine.effect_chain_bindings_valid &&
        engine.effect_chain_cached_count == draw_state.effect_count;
    for (std::uint32_t index = 0U;
         same_topology && index < draw_state.effect_count;
         ++index) {
        same_topology = engine.effect_chain_cached_kinds[index] ==
            draw_state.group_effects[index].kind;
    }
    if (same_topology) {
        return true;
    }

    const auto plan = ::progpu::native::effects::create_chain_plan(
        draw_state.group_effects.data(),
        draw_state.effect_count);
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS> horizontal{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS> vertical{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS> drop{};
    const auto release = [](auto& bindings) {
        for (auto binding : bindings) {
            if (binding != nullptr) wgpuBindGroupRelease(binding);
        }
    };
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        const auto& entry = plan[index];
        WGPUTextureView source = entry.source < 0
            ? engine.layer_texture_view
            : engine.effect_chain_texture_views[
                static_cast<std::uint32_t>(entry.source)];
        horizontal[index] = create_effect_blur_bind_group(
            engine,
            source,
            engine.effect_chain_texture_views[entry.horizontal],
            engine.effect_chain_blur_horizontal_uniform_buffers[index],
            "ProGPU native bounded effect-chain horizontal binding");
        vertical[index] = create_effect_blur_bind_group(
            engine,
            engine.effect_chain_texture_views[entry.horizontal],
            engine.effect_chain_texture_views[entry.vertical],
            engine.effect_chain_blur_vertical_uniform_buffers[index],
            "ProGPU native bounded effect-chain vertical binding");
        if (draw_state.group_effects[index].kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
            drop[index] = create_effect_chain_drop_shadow_bind_group(
                engine,
                source,
                engine.effect_chain_texture_views[entry.vertical],
                engine.effect_chain_texture_views[entry.output],
                engine.effect_chain_drop_shadow_uniform_buffers[index]);
        }
        if (horizontal[index] == nullptr || vertical[index] == nullptr ||
            (draw_state.group_effects[index].kind ==
                 PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
             drop[index] == nullptr)) {
            release(drop);
            release(vertical);
            release(horizontal);
            return false;
        }
    }

    release_effect_chain_node_bindings(engine);
    engine.effect_chain_blur_horizontal_bind_groups = horizontal;
    engine.effect_chain_blur_vertical_bind_groups = vertical;
    engine.effect_chain_drop_shadow_bind_groups = drop;
    engine.effect_chain_cached_count = draw_state.effect_count;
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        engine.effect_chain_cached_kinds[index] =
            draw_state.group_effects[index].kind;
    }
    engine.effect_chain_final_texture_index =
        plan[draw_state.effect_count - 1U].output;
    engine.effect_chain_bindings_valid = true;
    engine.effect_cache_valid = false;
    return true;
}

bool prepare_effect_chain(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    const resolved_draw_state& draw_state) {
    bool requires_drop_shadow = false;
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        requires_drop_shadow = requires_drop_shadow ||
            draw_state.group_effects[index].kind ==
                PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    }
    return create_gaussian_effect_resources(engine) &&
        (!requires_drop_shadow ||
         create_drop_shadow_effect_resources(engine)) &&
        ensure_effect_chain_uniform_buffers(engine) &&
        ensure_effect_chain_textures(engine, width, height) &&
        ensure_effect_chain_bindings(engine, draw_state);
}

bool prepare_group_effect(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    const resolved_draw_state& draw_state) {
    if (draw_state.effect_count > 1U) {
        return prepare_effect_chain(engine, width, height, draw_state);
    }
    if (!prepare_gaussian_effect(engine, width, height)) {
        return false;
    }
    return draw_state.group_effect.kind !=
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW ||
        (create_drop_shadow_effect_resources(engine) &&
         ensure_drop_shadow_effect_bindings(engine));
}

bool encode_group_effect(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const resolved_draw_state& draw_state,
    float dpi_scale) {
    if (!draw_state.has_group_effect) {
        return true;
    }
    const auto& effect = draw_state.group_effect;
    engine.last_layer_metrics.effect_kind = effect.kind;
    engine.last_layer_metrics.effect_revision =
        draw_state.effect_chain_revision;
    engine.last_layer_metrics.effect_count = draw_state.effect_count;
    engine.last_layer_metrics.effect_chain_revision =
        draw_state.effect_chain_revision;
    engine.last_layer_metrics.effect_texture_generation =
        draw_state.effect_count > 1U
            ? engine.effect_chain_texture_generation
            : engine.effect_texture_generation;
    engine.last_layer_metrics.effect_allocation_count =
        draw_state.effect_count > 1U
            ? engine.effect_chain_allocation_count
            : engine.effect_allocation_count;
    engine.last_layer_metrics.effect_texture_bytes =
        draw_state.effect_count > 1U
            ? static_cast<std::uint64_t>(engine.effect_chain_width) *
                engine.effect_chain_height * 12U
            : static_cast<std::uint64_t>(engine.effect_width) *
                engine.effect_height * 8U;
    const bool cache_hit = draw_state.group_revision != 0U &&
        engine.effect_cache_valid &&
        engine.effect_cached_kind == effect.kind &&
        engine.effect_cached_revision == draw_state.effect_chain_revision &&
        engine.effect_cached_content_revision == draw_state.group_revision &&
        engine.effect_cached_dpi_scale == dpi_scale;
    if (cache_hit) {
        engine.last_layer_metrics.effect_cache_hit = 1U;
        return true;
    }

    if (draw_state.effect_count > 1U) {
        const auto create_parameters = [dpi_scale](float sigma) {
            gpu_gaussian_blur_params parameters{};
            parameters.sigma = sigma * dpi_scale;
            parameters.radius = static_cast<std::uint32_t>(std::clamp(
                static_cast<int>(std::ceil(parameters.sigma * 3.0F)),
                0,
                128));
            return parameters;
        };
        const auto run_pass = [&](WGPUComputePipeline pipeline,
                                  WGPUBindGroup bind_group,
                                  const char* label) {
            WGPUComputePassDescriptor pass_descriptor{};
            pass_descriptor.label =
                ::progpu::native::webgpu::string_view(label);
            WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(
                encoder,
                &pass_descriptor);
            if (pass == nullptr) return false;
            wgpuComputePassEncoderSetPipeline(pass, pipeline);
            constexpr std::uint32_t uniform_offset = 0U;
            wgpuComputePassEncoderSetBindGroup(
                pass,
                0U,
                bind_group,
                1U,
                &uniform_offset);
            wgpuComputePassEncoderDispatchWorkgroups(
                pass,
                (engine.effect_chain_width + 15U) / 16U,
                (engine.effect_chain_height + 15U) / 16U,
                1U);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            return true;
        };
        std::uint64_t uploaded_uniform_bytes = 0U;
        for (std::uint32_t index = 0U;
             index < draw_state.effect_count;
             ++index) {
            const auto& node = draw_state.group_effects[index];
            const auto horizontal = create_parameters(node.sigma_x);
            const auto vertical = create_parameters(node.sigma_y);
            if (!engine.effect_chain_blur_horizontal_uniform_cache_valid[
                    index] ||
                std::memcmp(
                    &engine.cached_effect_chain_blur_horizontal[index],
                    &horizontal,
                    sizeof(horizontal)) != 0) {
                wgpuQueueWriteBuffer(
                    engine.queue,
                    engine.effect_chain_blur_horizontal_uniform_buffers[index],
                    0U,
                    &horizontal,
                    sizeof(horizontal));
                engine.cached_effect_chain_blur_horizontal[index] = horizontal;
                engine.effect_chain_blur_horizontal_uniform_cache_valid[
                    index] = true;
                uploaded_uniform_bytes += sizeof(horizontal);
            }
            if (!engine.effect_chain_blur_vertical_uniform_cache_valid[
                    index] ||
                std::memcmp(
                    &engine.cached_effect_chain_blur_vertical[index],
                    &vertical,
                    sizeof(vertical)) != 0) {
                wgpuQueueWriteBuffer(
                    engine.queue,
                    engine.effect_chain_blur_vertical_uniform_buffers[index],
                    0U,
                    &vertical,
                    sizeof(vertical));
                engine.cached_effect_chain_blur_vertical[index] = vertical;
                engine.effect_chain_blur_vertical_uniform_cache_valid[
                    index] = true;
                uploaded_uniform_bytes += sizeof(vertical);
            }
            if (!run_pass(
                    engine.effect_blur_horizontal_pipeline,
                    engine.effect_chain_blur_horizontal_bind_groups[index],
                    "ProGPU native bounded effect-chain horizontal pass") ||
                !run_pass(
                    engine.effect_blur_vertical_pipeline,
                    engine.effect_chain_blur_vertical_bind_groups[index],
                    "ProGPU native bounded effect-chain vertical pass")) {
                return false;
            }
            engine.last_layer_metrics.effect_pass_count += 2U;
            if (node.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
                gpu_drop_shadow_params drop_shadow{};
                drop_shadow.offset[0] = node.offset_x * dpi_scale;
                drop_shadow.offset[1] = node.offset_y * dpi_scale;
                drop_shadow.color[0] = node.color_r;
                drop_shadow.color[1] = node.color_g;
                drop_shadow.color[2] = node.color_b;
                drop_shadow.color[3] = node.color_a;
                if (!engine.effect_chain_drop_shadow_uniform_cache_valid[
                        index] ||
                    std::memcmp(
                        &engine.cached_effect_chain_drop_shadow[index],
                        &drop_shadow,
                        sizeof(drop_shadow)) != 0) {
                    wgpuQueueWriteBuffer(
                        engine.queue,
                        engine.effect_chain_drop_shadow_uniform_buffers[index],
                        0U,
                        &drop_shadow,
                        sizeof(drop_shadow));
                    engine.cached_effect_chain_drop_shadow[index] = drop_shadow;
                    engine.effect_chain_drop_shadow_uniform_cache_valid[
                        index] = true;
                    uploaded_uniform_bytes += sizeof(drop_shadow);
                }
                if (!run_pass(
                        engine.effect_drop_shadow_pipeline,
                        engine.effect_chain_drop_shadow_bind_groups[index],
                        "ProGPU native bounded effect-chain drop-shadow pass")) {
                    return false;
                }
                ++engine.last_layer_metrics.effect_pass_count;
            }
        }
        engine.last_layer_metrics.effect_uniform_upload_bytes =
            uploaded_uniform_bytes;
        return true;
    }

    const auto create_parameters = [dpi_scale](float sigma) {
        gpu_gaussian_blur_params parameters{};
        parameters.sigma = sigma * dpi_scale;
        parameters.radius = static_cast<std::uint32_t>(std::clamp(
            static_cast<int>(std::ceil(parameters.sigma * 3.0F)),
            0,
            128));
        return parameters;
    };
    const auto horizontal = create_parameters(effect.sigma_x);
    const auto vertical = create_parameters(effect.sigma_y);
    std::uint64_t uploaded_uniform_bytes = 0U;
    if (!engine.effect_blur_horizontal_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_effect_blur_horizontal,
            &horizontal,
            sizeof(horizontal)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.effect_blur_horizontal_uniform_buffer,
            0U,
            &horizontal,
            sizeof(horizontal));
        engine.cached_effect_blur_horizontal = horizontal;
        engine.effect_blur_horizontal_uniform_cache_valid = true;
        uploaded_uniform_bytes += sizeof(horizontal);
    }
    if (!engine.effect_blur_vertical_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_effect_blur_vertical,
            &vertical,
            sizeof(vertical)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.effect_blur_vertical_uniform_buffer,
            0U,
            &vertical,
            sizeof(vertical));
        engine.cached_effect_blur_vertical = vertical;
        engine.effect_blur_vertical_uniform_cache_valid = true;
        uploaded_uniform_bytes += sizeof(vertical);
    }

    const auto run_pass = [&](WGPUComputePipeline pipeline,
                              WGPUBindGroup bind_group,
                              const char* label) {
        WGPUComputePassDescriptor pass_descriptor{};
        pass_descriptor.label = ::progpu::native::webgpu::string_view(label);
        WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(
            encoder,
            &pass_descriptor);
        if (pass == nullptr) {
            return false;
        }
        wgpuComputePassEncoderSetPipeline(pass, pipeline);
        constexpr std::uint32_t uniform_offset = 0U;
        wgpuComputePassEncoderSetBindGroup(
            pass,
            0U,
            bind_group,
            1U,
            &uniform_offset);
        wgpuComputePassEncoderDispatchWorkgroups(
            pass,
            (engine.effect_width + 15U) / 16U,
            (engine.effect_height + 15U) / 16U,
            1U);
        wgpuComputePassEncoderEnd(pass);
        wgpuComputePassEncoderRelease(pass);
        return true;
    };
    if (!run_pass(
            engine.effect_blur_horizontal_pipeline,
            engine.effect_blur_horizontal_bind_group,
            "ProGPU native horizontal Gaussian group-effect pass") ||
        !run_pass(
            engine.effect_blur_vertical_pipeline,
            engine.effect_blur_vertical_bind_group,
            "ProGPU native vertical Gaussian group-effect pass")) {
        return false;
    }
    engine.last_layer_metrics.effect_pass_count = 2U;
    if (effect.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
        gpu_drop_shadow_params drop_shadow{};
        drop_shadow.offset[0] = effect.offset_x * dpi_scale;
        drop_shadow.offset[1] = effect.offset_y * dpi_scale;
        drop_shadow.color[0] = effect.color_r;
        drop_shadow.color[1] = effect.color_g;
        drop_shadow.color[2] = effect.color_b;
        drop_shadow.color[3] = effect.color_a;
        if (!engine.effect_drop_shadow_uniform_cache_valid ||
            std::memcmp(
                &engine.cached_effect_drop_shadow,
                &drop_shadow,
                sizeof(drop_shadow)) != 0) {
            wgpuQueueWriteBuffer(
                engine.queue,
                engine.effect_drop_shadow_uniform_buffer,
                0U,
                &drop_shadow,
                sizeof(drop_shadow));
            engine.cached_effect_drop_shadow = drop_shadow;
            engine.effect_drop_shadow_uniform_cache_valid = true;
            uploaded_uniform_bytes += sizeof(drop_shadow);
        }
        if (!run_pass(
                engine.effect_drop_shadow_pipeline,
                engine.effect_drop_shadow_bind_group,
                "ProGPU native drop-shadow group-effect composition pass")) {
            return false;
        }
        engine.last_layer_metrics.effect_pass_count = 3U;
    }
    engine.last_layer_metrics.effect_uniform_upload_bytes =
        uploaded_uniform_bytes;
    return true;
}

void retain_group_effect(
    progpu_native_engine& engine,
    float dpi_scale,
    const resolved_draw_state& draw_state) noexcept {
    if (!draw_state.has_group_effect || draw_state.group_revision == 0U) {
        engine.effect_cache_valid = false;
        return;
    }
    engine.effect_cached_revision = draw_state.effect_chain_revision;
    engine.effect_cached_content_revision = draw_state.group_revision;
    engine.effect_cached_kind = draw_state.group_effect.kind;
    engine.effect_cached_dpi_scale = dpi_scale;
    engine.effect_cache_valid = true;
}

bool prepare_layer_composite(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale,
    float opacity) {
    if (!create_layer_resources(engine) ||
        !ensure_layer_texture(engine, width, height)) {
        return false;
    }
    std::array<::progpu::native::vector_vertex, 4U> vertices{};
    const float logical_width = static_cast<float>(width) / dpi_scale;
    const float logical_height = static_cast<float>(height) / dpi_scale;
    constexpr std::array<std::array<std::uint32_t, 2U>, 4U> corners{{
        {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
    }};
    for (std::size_t index = 0U; index < corners.size(); ++index) {
        auto& vertex = vertices[index];
        vertex.position[0] = corners[index][0] == 0U
            ? 0.0F
            : logical_width;
        vertex.position[1] = corners[index][1] == 0U
            ? 0.0F
            : logical_height;
        vertex.color[0] = opacity;
        vertex.color[1] = 1.0F;
        vertex.color[2] = 0.0F;
        vertex.color[3] = opacity;
        vertex.texture_coordinate[0] =
            static_cast<float>(corners[index][0]);
        vertex.texture_coordinate[1] =
            static_cast<float>(corners[index][1]);
        vertex.stroke_thickness = 1.0F;
    }
    bool uploaded_vertices = false;
    if (!engine.layer_vertex_cache_valid ||
        std::memcmp(
            engine.layer_vertices.data(),
            vertices.data(),
            sizeof(vertices)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.layer_vertex_buffer,
            0U,
            vertices.data(),
            sizeof(vertices));
        engine.layer_vertices = vertices;
        engine.layer_vertex_cache_valid = true;
        uploaded_vertices = true;
    }
    const gpu_uniforms uniforms = create_uniforms(width, height, dpi_scale);
    const bool uploaded_uniforms = engine.upload_uniform_if_changed(
        engine.layer_uniform_buffer,
        uniforms,
        engine.cached_layer_uniforms,
        engine.layer_uniform_cache_valid);
    engine.last_layer_metrics = {};
    engine.last_layer_metrics.struct_size =
        sizeof(progpu_native_layer_metrics);
    engine.last_layer_metrics.texture_width = engine.layer_width;
    engine.last_layer_metrics.texture_height = engine.layer_height;
    engine.last_layer_metrics.texture_generation =
        engine.layer_texture_generation;
    engine.last_layer_metrics.allocation_count =
        engine.layer_allocation_count;
    engine.last_layer_metrics.texture_bytes =
        static_cast<std::uint64_t>(width) * height * 4U;
    engine.last_layer_metrics.vertex_upload_bytes = uploaded_vertices
        ? sizeof(vertices)
        : 0U;
    engine.last_layer_metrics.uniform_upload_bytes = uploaded_uniforms
        ? sizeof(uniforms)
        : 0U;
    return true;
}

void reset_layer_metrics(progpu_native_engine& engine) noexcept {
    engine.last_layer_metrics = {};
    engine.last_layer_metrics.struct_size =
        sizeof(progpu_native_layer_metrics);
    engine.last_layer_metrics.texture_width = engine.layer_width;
    engine.last_layer_metrics.texture_height = engine.layer_height;
    engine.last_layer_metrics.texture_generation =
        engine.layer_texture_generation;
    engine.last_layer_metrics.allocation_count =
        engine.layer_allocation_count;
    engine.last_layer_metrics.texture_bytes =
        static_cast<std::uint64_t>(engine.layer_width) *
        engine.layer_height * 4U;
}

WGPUBindGroup select_layer_source_bind_group(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state) noexcept {
    if (!draw_state.has_group_effect) {
        return engine.layer_texture_bind_group;
    }
    if (draw_state.effect_count > 1U) {
        return engine.effect_chain_output_bind_groups[
            engine.effect_chain_final_texture_index];
    }
    return draw_state.group_effect.kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW
        ? engine.effect_drop_shadow_output_bind_group
        : engine.effect_output_bind_group;
}

WGPUBindGroup select_layer_mask_bind_group(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state) noexcept {
    if (!draw_state.has_group_mask) {
        return nullptr;
    }
    if (draw_state.group_mask.kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE) {
        return draw_state.group_mask.sampling ==
                PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
            ? engine.layer_external_mask_nearest_bind_group
            : engine.layer_external_mask_linear_bind_group;
    }
    if (draw_state.group_mask.kind ==
        PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN) {
        return engine.layer_clip_mask_bind_groups[engine.clip_final_index];
    }
    return engine.layer_analytic_mask_bind_group;
}

std::uint64_t calculate_group_blend_source_signature(
    const resolved_draw_state& draw_state) noexcept {
    std::uint64_t hash = 14695981039346656037ULL;
    hash = append_fnv1a64(
        hash,
        &draw_state.group_revision,
        sizeof(draw_state.group_revision));
    hash = append_fnv1a64(
        hash,
        &draw_state.group_opacity,
        sizeof(draw_state.group_opacity));
    hash = append_fnv1a64(
        hash,
        &draw_state.has_group_mask,
        sizeof(draw_state.has_group_mask));
    if (draw_state.has_group_mask) {
        const auto& mask = draw_state.group_mask;
        hash = append_fnv1a64(
            hash,
            &mask.kind,
            sizeof(mask.kind));
        hash = append_fnv1a64(
            hash,
            &mask.external_view,
            sizeof(mask.external_view));
        hash = append_fnv1a64(hash, &mask.width, sizeof(mask.width));
        hash = append_fnv1a64(hash, &mask.height, sizeof(mask.height));
        hash = append_fnv1a64(hash, &mask.sampling, sizeof(mask.sampling));
        hash = append_fnv1a64(
            hash,
            &mask.texture_format,
            sizeof(mask.texture_format));
        hash = append_fnv1a64(hash, &mask.revision, sizeof(mask.revision));
        hash = append_fnv1a64(
            hash,
            &mask.destination_rect,
            sizeof(mask.destination_rect));
        hash = append_fnv1a64(hash, &mask.bounds, sizeof(mask.bounds));
        hash = append_fnv1a64(hash, &mask.transform, sizeof(mask.transform));
        hash = append_fnv1a64(
            hash,
            mask.corner_radii_x,
            sizeof(mask.corner_radii_x));
        hash = append_fnv1a64(
            hash,
            mask.corner_radii_y,
            sizeof(mask.corner_radii_y));
        hash = append_fnv1a64(hash, &mask.opacity, sizeof(mask.opacity));
    }
    hash = append_fnv1a64(
        hash,
        &draw_state.effect_count,
        sizeof(draw_state.effect_count));
    hash = append_fnv1a64(
        hash,
        &draw_state.effect_chain_revision,
        sizeof(draw_state.effect_chain_revision));
    if (draw_state.effect_count != 0U) {
        hash = append_fnv1a64(
            hash,
            draw_state.group_effects.data(),
            draw_state.effect_count * sizeof(progpu_native_group_effect));
    }
    return hash;
}

float quantize_unorm8(float value) noexcept {
    return std::round(std::clamp(value, 0.0F, 1.0F) * 255.0F) / 255.0F;
}

bool encode_layer_quad(
    progpu_native_engine& engine,
    WGPURenderPassEncoder pass,
    WGPURenderPipeline pipeline,
    const resolved_draw_state& draw_state,
    bool apply_final_clip) {
    WGPUBindGroup mask_bind_group = select_layer_mask_bind_group(
        engine,
        draw_state);
    WGPUBindGroup source_bind_group = select_layer_source_bind_group(
        engine,
        draw_state);
    if (pipeline == nullptr || source_bind_group == nullptr ||
        (draw_state.has_group_mask && mask_bind_group == nullptr)) {
        return false;
    }
    if (apply_final_clip) {
        apply_scissor(pass, draw_state);
    }
    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        engine.layer_uniform_bind_group,
        0U,
        nullptr);
    if (draw_state.has_group_mask) {
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            2U,
            mask_bind_group,
            0U,
            nullptr);
    }
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        1U,
        source_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderSetVertexBuffer(
        pass,
        0U,
        engine.layer_vertex_buffer,
        0U,
        sizeof(engine.layer_vertices));
    wgpuRenderPassEncoderSetIndexBuffer(
        pass,
        engine.layer_index_buffer,
        WGPUIndexFormat_Uint32,
        0U,
        6U * sizeof(std::uint32_t));
    wgpuRenderPassEncoderDrawIndexed(pass, 6U, 1U, 0U, 0, 0U);
    return true;
}

bool encode_semantic_layer_composite(
    progpu_native_engine& engine,
    WGPURenderPassEncoder pass,
    const semantic_render_bundle_span& operation) {
    const bool masked = operation.mask_bind_group != nullptr;
    bool blend_pipeline_cache_hit = false;
    WGPURenderPipeline pipeline = get_or_create_fixed_group_blend_pipeline(
        engine,
        operation.blend_mode,
        masked,
        blend_pipeline_cache_hit);
    WGPUBindGroup target_uniform_group =
        operation.target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.layer_uniform_bind_group
        : operation.target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[operation.target_layer]
                .image_uniform_bind_group
            : nullptr;
    if (operation.kind != semantic_replay_kind::pop_layer ||
        operation.source_layer >= engine.semantic_layer_slots.size() ||
        pipeline == nullptr ||
        target_uniform_group == nullptr ||
        engine.layer_index_buffer == nullptr ||
        engine.semantic_layer_vertex_buffer == nullptr) {
        return false;
    }
    const auto& slot = engine.semantic_layer_slots[
        operation.source_layer];
    WGPUBindGroup source_bind_group = operation.effect_count == 0U
        ? slot.bind_group
        : operation.final_effect_texture <
                slot.effect_output_bind_groups.size()
            ? slot.effect_output_bind_groups[
                operation.final_effect_texture]
            : nullptr;
    if (source_bind_group == nullptr) {
        return false;
    }
    const std::uint64_t vertex_offset =
        static_cast<std::uint64_t>(operation.first_composite_vertex) *
        sizeof(::progpu::native::vector_vertex);
    if (vertex_offset + 4U * sizeof(::progpu::native::vector_vertex) >
        engine.semantic_layer_vertex_buffer_size) {
        return false;
    }
    wgpuRenderPassEncoderSetPipeline(
        pass,
        pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        target_uniform_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        1U,
        source_bind_group,
        0U,
        nullptr);
    if (masked) {
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            2U,
            operation.mask_bind_group,
            0U,
            nullptr);
    }
    wgpuRenderPassEncoderSetVertexBuffer(
        pass,
        0U,
        engine.semantic_layer_vertex_buffer,
        vertex_offset,
        4U * sizeof(::progpu::native::vector_vertex));
    wgpuRenderPassEncoderSetIndexBuffer(
        pass,
        engine.layer_index_buffer,
        WGPUIndexFormat_Uint32,
        0U,
        6U * sizeof(std::uint32_t));
    wgpuRenderPassEncoderDrawIndexed(pass, 6U, 1U, 0U, 0, 0U);
    return true;
}

bool encode_semantic_effect_chain(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const semantic_render_bundle_span& operation,
    std::uint32_t& pass_count) {
    if (operation.effect_count == 0U) {
        return true;
    }
    if (operation.source_layer >= engine.semantic_layer_slots.size() ||
        operation.first_effect_dispatch >
            engine.semantic_effect_dispatches.size() ||
        operation.effect_count >
            engine.semantic_effect_dispatches.size() -
                operation.first_effect_dispatch) {
        return false;
    }
    auto& slot = engine.semantic_layer_slots[operation.source_layer];
    const auto run_pass = [&](WGPUComputePipeline pipeline,
                              WGPUBindGroup binding,
                              std::uint32_t uniform_offset,
                              const char* label) {
        if (pipeline == nullptr || binding == nullptr ||
            uniform_offset % semantic_effect_uniform_alignment != 0U) {
            return false;
        }
        WGPUComputePassDescriptor descriptor{};
        descriptor.label = ::progpu::native::webgpu::string_view(label);
        WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(
            encoder,
            &descriptor);
        if (pass == nullptr) {
            return false;
        }
        wgpuComputePassEncoderSetPipeline(pass, pipeline);
        wgpuComputePassEncoderSetBindGroup(
            pass,
            0U,
            binding,
            1U,
            &uniform_offset);
        wgpuComputePassEncoderDispatchWorkgroups(
            pass,
            (slot.effect_width + 15U) / 16U,
            (slot.effect_height + 15U) / 16U,
            1U);
        wgpuComputePassEncoderEnd(pass);
        wgpuComputePassEncoderRelease(pass);
        ++pass_count;
        return true;
    };
    for (std::uint32_t index = 0U;
         index < operation.effect_count;
         ++index) {
        const auto& dispatch = engine.semantic_effect_dispatches[
            operation.first_effect_dispatch + index];
        WGPUBindGroup horizontal =
            get_or_create_semantic_effect_blur_binding(
                engine,
                slot,
                dispatch.source_texture,
                dispatch.horizontal_texture);
        WGPUBindGroup vertical =
            get_or_create_semantic_effect_blur_binding(
                engine,
                slot,
                static_cast<std::int32_t>(dispatch.horizontal_texture),
                dispatch.vertical_texture);
        if (!run_pass(
                engine.effect_blur_horizontal_pipeline,
                horizontal,
                dispatch.horizontal_uniform_offset,
                "ProGPU semantic effect horizontal pass") ||
            !run_pass(
                engine.effect_blur_vertical_pipeline,
                vertical,
                dispatch.vertical_uniform_offset,
                "ProGPU semantic effect vertical pass")) {
            return false;
        }
        if (dispatch.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
            WGPUBindGroup drop =
                get_or_create_semantic_effect_drop_shadow_binding(
                    engine,
                    slot,
                    dispatch.source_texture,
                    dispatch.vertical_texture,
                    dispatch.output_texture);
            if (!run_pass(
                    engine.effect_drop_shadow_pipeline,
                    drop,
                    dispatch.drop_shadow_uniform_offset,
                    "ProGPU semantic effect drop-shadow pass")) {
                return false;
            }
        }
    }
    return true;
}

bool encode_advanced_group_blend(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state) {
    const std::uint64_t source_signature =
        calculate_group_blend_source_signature(draw_state);
    const bool source_cache_hit = draw_state.group_revision != 0U &&
        engine.group_blend_source_cache_valid &&
        engine.group_blend_source_signature == source_signature;
    if (!source_cache_hit) {
        WGPURenderPassColorAttachment source_attachment{};
        ::progpu::native::webgpu::initialize_color_attachment(source_attachment);
        source_attachment.view = engine.group_blend_source_view;
        source_attachment.loadOp = WGPULoadOp_Clear;
        source_attachment.storeOp = WGPUStoreOp_Store;
        source_attachment.clearValue = {0.0, 0.0, 0.0, 0.0};
        WGPURenderPassDescriptor source_descriptor{};
        source_descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU native advanced group-blend source pass");
        source_descriptor.colorAttachmentCount = 1U;
        source_descriptor.colorAttachments = &source_attachment;
        WGPURenderPassEncoder source_pass = wgpuCommandEncoderBeginRenderPass(
            encoder,
            &source_descriptor);
        if (source_pass == nullptr) {
            return false;
        }
        const bool source_encoded = encode_layer_quad(
            engine,
            source_pass,
            draw_state.has_group_mask
                ? engine.layer_mask_pipeline
                : engine.layer_composite_pipeline,
            draw_state,
            false);
        wgpuRenderPassEncoderEnd(source_pass);
        wgpuRenderPassEncoderRelease(source_pass);
        if (!source_encoded) {
            return false;
        }
        engine.last_layer_metrics.blend_source_pass_count = 1U;
        engine.group_blend_source_signature = source_signature;
        engine.group_blend_source_cache_valid =
            draw_state.group_revision != 0U;
    }

    const gpu_group_blend_uniforms uniforms{{
        quantize_unorm8(clear_color.r),
        quantize_unorm8(clear_color.g),
        quantize_unorm8(clear_color.b),
        quantize_unorm8(clear_color.a)
    }, draw_state.group_blend_mode, {0U, 0U, 0U}};
    if (!engine.group_blend_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_group_blend_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.group_blend_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_group_blend_uniforms = uniforms;
        engine.group_blend_uniform_cache_valid = true;
        engine.last_layer_metrics.uniform_upload_bytes += sizeof(uniforms);
    }

    WGPURenderPassColorAttachment attachment{};
    ::progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = target_view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {
        clear_color.r,
        clear_color.g,
        clear_color.b,
        clear_color.a
    };
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend composite pass");
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &descriptor);
    if (pass == nullptr) {
        return false;
    }
    apply_scissor(pass, draw_state);
    wgpuRenderPassEncoderSetPipeline(pass, engine.group_blend_pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        engine.group_blend_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    engine.last_layer_metrics.composite_pass_count = 1U;
    return true;
}

bool encode_layer_composite(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state) {
    if (draw_state.group_opacity != 0.0F &&
        draw_state.has_drawable_clip &&
        is_advanced_group_blend(draw_state.group_blend_mode)) {
        return encode_advanced_group_blend(
            engine,
            encoder,
            target_view,
            clear_color,
            draw_state);
    }
    WGPURenderPassColorAttachment attachment{};
    ::progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = target_view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {
        clear_color.r,
        clear_color.g,
        clear_color.b,
        clear_color.a
    };
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native group composite pass");
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &descriptor);
    if (pass == nullptr) {
        return false;
    }
    if (draw_state.group_opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        bool ignored_cache_hit = false;
        WGPURenderPipeline pipeline =
            get_or_create_fixed_group_blend_pipeline(
                engine,
                draw_state.group_blend_mode,
                draw_state.has_group_mask,
                ignored_cache_hit);
        if (!encode_layer_quad(
                engine,
                pass,
                pipeline,
                draw_state,
                true)) {
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            return false;
        }
        engine.last_layer_metrics.composite_pass_count = 1U;
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return true;
}

progpu_native_status prepare_group_layer(
    progpu_native_engine& engine,
    layer_family family,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state,
    bool& use_group_layer,
    bool& submitted_cache_hit) {
    use_group_layer = draw_state.group_opacity < 1.0F ||
        draw_state.group_revision != 0U ||
        draw_state.has_group_mask ||
        draw_state.has_group_effect ||
        draw_state.group_blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER;
    submitted_cache_hit = false;
    if (!use_group_layer) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    if (!prepare_layer_composite(
            engine,
            width,
            height,
            dpi_scale,
            draw_state.group_opacity)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The pooled group layer could not be prepared.");
    }
    if (draw_state.has_group_effect &&
        !prepare_group_effect(engine, width, height, draw_state)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group effect could not be prepared.");
    }
    bool uploaded_mask_uniforms = false;
    if (draw_state.has_group_mask &&
        !update_layer_group_mask(
            engine,
            draw_state,
            dpi_scale,
            uploaded_mask_uniforms)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The common group mask could not be prepared.");
    }
    if (uploaded_mask_uniforms) {
        engine.last_layer_metrics.uniform_upload_bytes +=
            sizeof(gpu_mask_sampling_uniforms);
    }
    engine.last_layer_metrics.blend_mode = draw_state.group_blend_mode;
    if (draw_state.group_opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        if (is_advanced_group_blend(draw_state.group_blend_mode)) {
            const bool cache_hit = engine.group_blend_pipeline != nullptr &&
                engine.group_blend_source_texture != nullptr &&
                engine.group_blend_source_width == width &&
                engine.group_blend_source_height == height;
            if (!ensure_advanced_group_blend_source(
                    engine,
                    width,
                    height)) {
                return engine.fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The advanced group-blend resources could not be prepared.");
            }
            engine.last_layer_metrics.blend_pipeline_cache_hit =
                cache_hit ? 1U : 0U;
            engine.last_layer_metrics.blend_source_texture_generation =
                engine.group_blend_source_texture_generation;
            engine.last_layer_metrics.blend_source_allocation_count =
                engine.group_blend_source_allocation_count;
            engine.last_layer_metrics.blend_source_texture_bytes =
                static_cast<std::uint64_t>(width) * height * 4U;
        } else {
            bool pipeline_cache_hit = false;
            if (get_or_create_fixed_group_blend_pipeline(
                    engine,
                    draw_state.group_blend_mode,
                    draw_state.has_group_mask,
                    pipeline_cache_hit) == nullptr) {
                return engine.fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The fixed-function group-blend pipeline could not be prepared.");
            }
            engine.last_layer_metrics.blend_pipeline_cache_hit =
                pipeline_cache_hit ? 1U : 0U;
        }
    }
    const bool cache_hit = draw_state.group_revision != 0U &&
        engine.layer_content_cache_valid &&
        engine.layer_cached_family ==
            static_cast<std::uint32_t>(family) &&
        engine.layer_cached_revision == draw_state.group_revision &&
        engine.layer_cached_dpi_scale == dpi_scale &&
        engine.layer_cached_primitive_opacity == draw_state.opacity;
    if (!cache_hit) {
        engine.effect_cache_valid = false;
        engine.group_blend_source_cache_valid = false;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native retained group replay encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine.device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group replay encoder could not be created.");
    }
    if (!encode_group_effect(
            engine,
            encoder,
            draw_state,
            dpi_scale) ||
        !encode_layer_composite(
            engine,
            encoder,
            target_view,
            clear_color,
            draw_state)) {
        wgpuCommandEncoderRelease(encoder);
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group replay pass could not be created.");
    }
    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native retained group replay commands");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group replay command buffer could not be finished.");
    }
    engine.submit(command);
    wgpuCommandBufferRelease(command);
    retain_group_effect(engine, dpi_scale, draw_state);
    engine.last_layer_metrics.cache_hit = 1U;
    submitted_cache_hit = true;
    engine.last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void retain_group_layer_content(
    progpu_native_engine& engine,
    layer_family family,
    float dpi_scale,
    const resolved_draw_state& draw_state) noexcept {
    if (draw_state.group_revision == 0U) {
        engine.layer_content_cache_valid = false;
        retain_group_effect(engine, dpi_scale, draw_state);
        return;
    }
    engine.layer_cached_family = static_cast<std::uint32_t>(family);
    engine.layer_cached_revision = draw_state.group_revision;
    engine.layer_cached_dpi_scale = dpi_scale;
    engine.layer_cached_primitive_opacity = draw_state.opacity;
    engine.layer_content_cache_valid = true;
    retain_group_effect(engine, dpi_scale, draw_state);
}


} // namespace progpu::native::execution
