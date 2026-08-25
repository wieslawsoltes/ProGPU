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
    if (!operation.composite_drawable) {
        return true;
    }
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
    if (operation.has_composite_scissor) {
        wgpuRenderPassEncoderSetScissorRect(
            pass,
            operation.clip_x,
            operation.clip_y,
            operation.clip_width,
            operation.clip_height);
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
