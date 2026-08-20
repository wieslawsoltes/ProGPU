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
#include "progpu_native_semantic_layer_mask.hpp"
#include "progpu_native_semantic_layer_mask_resources.hpp"
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
        uniforms.options[1] = 1.0F;
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
        uniforms.options[1] = 1.0F;
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
        WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopySrc |
        WGPUTextureUsage_CopyDst;
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
            engine.text_style_buffer_size, nullptr, nullptr}
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
                engine.analytic_brush_buffer ||
            slot.bound_analytic_gradient_buffer !=
                engine.analytic_gradient_buffer)) {
        if (slot.analytic_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(slot.analytic_uniform_bind_group);
        }
        slot.analytic_uniform_bind_group =
            create_analytic_uniform_bind_group_for_buffer(
                engine,
                slot.uniform_buffer,
                engine.analytic_brush_buffer,
                engine.analytic_brush_buffer_size,
                engine.analytic_gradient_buffer,
                engine.analytic_gradient_buffer_size,
                "ProGPU semantic bounded-layer analytic uniforms");
        if (slot.analytic_uniform_bind_group == nullptr) {
            slot.bound_analytic_brush_buffer = nullptr;
            slot.bound_analytic_gradient_buffer = nullptr;
            return false;
        }
        slot.bound_analytic_brush_buffer = engine.analytic_brush_buffer;
        slot.bound_analytic_gradient_buffer =
            engine.analytic_gradient_buffer;
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

bool ensure_semantic_texture_slot(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::uint32_t width,
    std::uint32_t height,
    const char* label) {
    if (slot.texture != nullptr && slot.uniform_buffer != nullptr &&
        slot.width == width && slot.height == height) {
        return ensure_semantic_layer_slot_bindings(engine, slot);
    }

    WGPUTextureDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(label);
    descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopySrc |
        WGPUTextureUsage_CopyDst;
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

bool ensure_semantic_layer_slot(
    progpu_native_engine& engine,
    std::uint32_t index,
    std::uint32_t width,
    std::uint32_t height) {
    return index < engine.semantic_layer_slots.size() &&
        ensure_semantic_texture_slot(
            engine,
            engine.semantic_layer_slots[index],
            width,
            height,
            "ProGPU semantic depth-indexed isolated layer");
}

bool ensure_semantic_depth_slot(
    progpu_native_engine& engine,
    semantic_layer_slot& slot,
    std::uint32_t width,
    std::uint32_t height,
    const char* label) {
    if (slot.depth_texture != nullptr && slot.depth_view != nullptr &&
        slot.depth_width == width && slot.depth_height == height) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(label);
    descriptor.usage = WGPUTextureUsage_RenderAttachment;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_Depth24Plus;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    if (slot.depth_view != nullptr) {
        wgpuTextureViewRelease(slot.depth_view);
    }
    if (slot.depth_texture != nullptr) {
        wgpuTextureDestroy(slot.depth_texture);
        wgpuTextureRelease(slot.depth_texture);
    }
    slot.depth_texture = texture;
    slot.depth_view = view;
    slot.depth_width = width;
    slot.depth_height = height;
    ++engine.semantic_layer_allocation_count;
    return true;
}

bool prepare_semantic_depth_resources(
    progpu_native_engine& engine,
    const semantic_layer_budget& budget,
    std::uint32_t frame_width,
    std::uint32_t frame_height) {
    if (!ensure_semantic_depth_slot(
            engine,
            engine.semantic_root_slot,
            frame_width,
            frame_height,
            "ProGPU semantic root 3D depth")) {
        return false;
    }
    for (std::uint32_t index = 0U;
         index < budget.peak_materialized_depth;
         ++index) {
        if (!ensure_semantic_depth_slot(
                engine,
                engine.semantic_layer_slots[index],
                budget.slot_widths[index],
                budget.slot_heights[index],
                "ProGPU semantic isolated-layer 3D depth")) {
            return false;
        }
    }
    return true;
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
    engine.semantic_layer_vertex_content_hash = 0U;
    engine.semantic_layer_vertex_content_bytes = 0U;
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
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    const semantic_scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation,
    std::uint64_t& texture_upload_bytes) {
    texture_upload_bytes = 0U;
    semantic::semantic_layer_mask parsed{};
    std::uint32_t error_offset = resource.payload_offset;
    if (!semantic::validate_layer_mask_resource(
            bytes, resource, error_offset, &parsed)) {
        return false;
    }
    if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP) {
        return create_semantic_coverage_mask_binding(
            engine,
            parsed.coverage,
            bytes + resource.auxiliary_offset,
            target_extent,
            dpi_scale,
            operation,
            texture_upload_bytes);
    }
    if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN) {
        return create_semantic_vector_mask_binding(
            engine,
            parsed,
            resource,
            target_extent,
            dpi_scale,
            operation);
    }
    if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH) {
        return create_semantic_brush_mask_binding(
            engine,
            parsed,
            target_extent,
            dpi_scale,
            operation);
    }
    if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY) {
        return create_semantic_geometry_mask_binding(
            engine,
            parsed,
            target_extent,
            dpi_scale,
            operation);
    }
    if (parsed.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
        return create_semantic_composite_mask_binding(
            engine,
            parsed,
            resource,
            target_extent,
            dpi_scale,
            operation);
    }
    if (!create_layer_mask_resources(engine)) {
        return false;
    }
    const auto create_uniforms_for = [&](
        const progpu_native_scene_layer_mask& source,
        gpu_mask_sampling_uniforms& uniforms) noexcept {
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
        return create_rounded_group_mask_uniforms(mask, dpi_scale, uniforms);
    };
    const bool chained = parsed.kind ==
        PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    const auto& primary_source = chained
        ? parsed.chain.masks[0]
        : parsed.analytic;
    gpu_mask_sampling_uniforms uniforms{};
    if (!create_uniforms_for(primary_source, uniforms)) {
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
    wgpuQueueWriteBuffer(
        engine.queue,
        buffer,
        0U,
        &uniforms,
        sizeof(uniforms));
    operation.mask_uniform_buffer = buffer;
    operation.mask_uniform_upload_bytes = sizeof(uniforms);
    if (chained) {
        progpu::native::gpu_mask_chain_uniforms chain_uniforms{};
        for (std::uint32_t index = 1U;
             index < parsed.chain.mask_count;
             ++index) {
            if (!create_uniforms_for(
                    parsed.chain.masks[index],
                    chain_uniforms.masks[index - 1U])) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
                operation.mask_uniform_buffer = nullptr;
                operation.mask_uniform_upload_bytes = 0U;
                return false;
            }
        }
        WGPUBufferDescriptor chain_descriptor{};
        chain_descriptor.label = ::progpu::native::webgpu::string_view(
            "ProGPU retained semantic analytic mask-chain uniforms");
        chain_descriptor.usage =
            WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
        chain_descriptor.size = sizeof(chain_uniforms);
        WGPUBuffer chain_buffer = wgpuDeviceCreateBuffer(
            engine.device,
            &chain_descriptor);
        WGPUBindGroup chain_bind_group = chain_buffer == nullptr
            ? nullptr
            : create_semantic_mask_chain_bind_group(
                engine,
                engine.image_linear_sampler,
                engine.layer_mask_dummy_view,
                buffer,
                chain_buffer);
        if (chain_bind_group == nullptr) {
            if (chain_buffer != nullptr) {
                wgpuBufferDestroy(chain_buffer);
                wgpuBufferRelease(chain_buffer);
            }
            wgpuBufferDestroy(buffer);
            wgpuBufferRelease(buffer);
            operation.mask_uniform_buffer = nullptr;
            operation.mask_uniform_upload_bytes = 0U;
            return false;
        }
        wgpuQueueWriteBuffer(
            engine.queue,
            chain_buffer,
            0U,
            &chain_uniforms,
            sizeof(chain_uniforms));
        operation.mask_chain_uniform_buffer = chain_buffer;
        operation.mask_chain_bind_group = chain_bind_group;
        operation.mask_uniform_upload_bytes += sizeof(chain_uniforms);
    } else {
        WGPUBindGroup bind_group = create_layer_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            engine.layer_mask_dummy_view,
            "ProGPU retained semantic analytic mask binding",
            buffer);
        if (bind_group == nullptr) {
            wgpuBufferDestroy(buffer);
            wgpuBufferRelease(buffer);
            operation.mask_uniform_buffer = nullptr;
            operation.mask_uniform_upload_bytes = 0U;
            return false;
        }
        operation.mask_bind_group = bind_group;
    }
    ++engine.layer_mask_bind_group_generation;
    return true;
}

} // namespace progpu::native::execution
