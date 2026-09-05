#include "progpu_native_semantic_layer_mask_resources.hpp"
#include "progpu_native_semantic_layer_mask.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_native_engine.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_replay.hpp"
#include "progpu_webgpu_compat.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

namespace progpu::native::execution {
namespace {

void release_child_mask(
    semantic_render_bundle_span& child,
    bool submitted) noexcept {
    if (child.mask_bind_group != nullptr) {
        wgpuBindGroupRelease(child.mask_bind_group);
        child.mask_bind_group = nullptr;
    }
    if (child.mask_chain_bind_group != nullptr) {
        wgpuBindGroupRelease(child.mask_chain_bind_group);
        child.mask_chain_bind_group = nullptr;
    }
    if (child.mask_uniform_buffer != nullptr) {
        wgpuBufferDestroy(child.mask_uniform_buffer);
        wgpuBufferRelease(child.mask_uniform_buffer);
        child.mask_uniform_buffer = nullptr;
    }
    if (child.mask_chain_uniform_buffer != nullptr) {
        wgpuBufferDestroy(child.mask_chain_uniform_buffer);
        wgpuBufferRelease(child.mask_chain_uniform_buffer);
        child.mask_chain_uniform_buffer = nullptr;
    }
    if (child.mask_texture_view != nullptr) {
        wgpuTextureViewRelease(child.mask_texture_view);
        child.mask_texture_view = nullptr;
    }
    if (child.mask_texture != nullptr) {
        if (!submitted) {
            wgpuTextureDestroy(child.mask_texture);
        }
        wgpuTextureRelease(child.mask_texture);
        child.mask_texture = nullptr;
    }
}

WGPUTexture create_composition_texture(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    const char* label) {
    WGPUTextureDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    return wgpuDeviceCreateTexture(engine.device, &descriptor);
}

} // namespace

bool create_semantic_composite_mask_binding(
    progpu_native_engine& engine,
    const semantic::semantic_layer_mask& parsed,
    const progpu_native_scene_resource& resource,
    const semantic::scissor& target_extent,
    float dpi_scale,
    const semantic::semantic_state_cursor* composite_state_cursor,
    const progpu_native_scene_state* composite_state,
    semantic_render_bundle_span& operation) {
    const auto& source = parsed.composite;
    if (source.component_count < 2U ||
        source.component_count > 64U || target_extent.width == 0U ||
        target_extent.height == 0U || !std::isfinite(dpi_scale) ||
        dpi_scale <= 0.0F || !create_layer_mask_resources(engine) ||
        !create_clip_chain_resources(engine)) {
        return false;
    }

    std::vector<semantic_render_bundle_span> children;
    std::vector<WGPUBindGroup> compose_bind_groups;
    std::vector<std::byte> compose_uniform_bytes;
    std::array<WGPUTexture, 2U> accumulation_textures{};
    std::array<WGPUTextureView, 2U> accumulation_views{};
    WGPUBuffer compose_uniform_buffer = nullptr;
    WGPUBuffer sampling_uniform_buffer = nullptr;
    WGPUBindGroup sampling_bind_group = nullptr;
    WGPUCommandEncoder encoder = nullptr;
    WGPUCommandBuffer command = nullptr;
    bool submitted = false;

    const auto cleanup = [&]() noexcept {
        if (command != nullptr) {
            wgpuCommandBufferRelease(command);
            command = nullptr;
        }
        if (encoder != nullptr) {
            wgpuCommandEncoderRelease(encoder);
            encoder = nullptr;
        }
        for (auto bind_group : compose_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
            }
        }
        compose_bind_groups.clear();
        if (compose_uniform_buffer != nullptr) {
            if (!submitted) {
                wgpuBufferDestroy(compose_uniform_buffer);
            }
            wgpuBufferRelease(compose_uniform_buffer);
            compose_uniform_buffer = nullptr;
        }
        for (auto& child : children) {
            release_child_mask(child, submitted);
        }
        children.clear();
        if (sampling_bind_group != nullptr) {
            wgpuBindGroupRelease(sampling_bind_group);
            sampling_bind_group = nullptr;
        }
        if (sampling_uniform_buffer != nullptr) {
            wgpuBufferDestroy(sampling_uniform_buffer);
            wgpuBufferRelease(sampling_uniform_buffer);
            sampling_uniform_buffer = nullptr;
        }
        for (std::size_t index = 0U; index < accumulation_views.size();
             ++index) {
            if (accumulation_views[index] != nullptr) {
                wgpuTextureViewRelease(accumulation_views[index]);
                accumulation_views[index] = nullptr;
            }
            if (accumulation_textures[index] != nullptr) {
                if (!submitted) {
                    wgpuTextureDestroy(accumulation_textures[index]);
                }
                wgpuTextureRelease(accumulation_textures[index]);
                accumulation_textures[index] = nullptr;
            }
        }
    };

    try {
        children.reserve(source.component_count);
        compose_bind_groups.reserve(source.component_count);
        compose_uniform_bytes.resize(
            static_cast<std::size_t>(source.component_count) * 256U);
    } catch (const std::bad_alloc&) {
        cleanup();
        return false;
    }

    if (source.path_count != 0U) {
        semantic::semantic_layer_mask child{};
        child.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN;
        child.vector = {
            sizeof(progpu_native_scene_layer_vector_mask),
            PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN,
            0U,
            source.path_count,
            source.segment_count,
            1.0F,
            source.boolean_node_count,
            0U};
        child.vector_paths = parsed.composite_paths;
        child.vector_segments = parsed.composite_segments;
        child.vector_boolean_nodes = parsed.composite_boolean_nodes;
        semantic_render_bundle_span child_operation{};
        if (!create_semantic_vector_mask_binding(
                engine,
                child,
                resource,
                target_extent,
                dpi_scale,
                child_operation)) {
            cleanup();
            return false;
        }
        children.push_back(child_operation);
    }
    for (std::uint32_t index = 0U;
         index < source.picture_mask_count;
         ++index) {
        const auto& picture = parsed.composite_picture_masks[index];
        semantic_render_bundle_span child_operation{};
        if (!create_semantic_picture_mask_binding(
                engine,
                picture,
                parsed.composite_picture_streams + picture.stream_offset,
                target_extent,
                dpi_scale,
                child_operation)) {
            cleanup();
            return false;
        }
        children.push_back(child_operation);
    }
    for (std::uint32_t index = 0U;
         index < source.geometry_mask_count;
         ++index) {
        semantic::semantic_layer_mask child{};
        child.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY;
        child.geometry = parsed.composite_geometry_masks[index];
        child.composite_geometry_primitives =
            parsed.composite_geometry_primitives;
        const std::uint32_t stop_offset = child.geometry.brush.stop_offset;
        child.geometry.brush.stop_offset = 0U;
        child.brush_stops = parsed.composite_stops + stop_offset;
        semantic_render_bundle_span child_operation{};
        if (!create_semantic_geometry_mask_binding(
                engine,
                child,
                target_extent,
                dpi_scale,
                child_operation)) {
            cleanup();
            return false;
        }
        children.push_back(child_operation);
    }
    for (std::uint32_t index = 0U; index < source.brush_mask_count; ++index) {
        semantic::semantic_layer_mask child{};
        child.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
        child.brush = parsed.composite_brushes[index];
        const std::uint32_t stop_offset = child.brush.brush.stop_offset;
        child.brush.brush.stop_offset = 0U;
        child.brush_stops = parsed.composite_stops + stop_offset;
        semantic_render_bundle_span child_operation{};
        if (!create_semantic_brush_mask_binding(
                engine,
                child,
                target_extent,
                dpi_scale,
                composite_state_cursor,
                composite_state,
                child_operation)) {
            cleanup();
            return false;
        }
        children.push_back(child_operation);
    }
    if (children.size() != source.component_count) {
        cleanup();
        return false;
    }

    for (std::size_t index = 0U; index < accumulation_textures.size();
         ++index) {
        accumulation_textures[index] = create_composition_texture(
            engine,
            target_extent.width,
            target_extent.height,
            "ProGPU retained composite opacity mask");
        accumulation_views[index] = accumulation_textures[index] == nullptr
            ? nullptr
            : wgpuTextureCreateView(accumulation_textures[index], nullptr);
        if (accumulation_textures[index] == nullptr ||
            accumulation_views[index] == nullptr) {
            cleanup();
            return false;
        }
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = webgpu::string_view(
        "ProGPU retained composite-mask uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = compose_uniform_bytes.size();
    compose_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device, &uniform_descriptor);
    if (compose_uniform_buffer == nullptr) {
        cleanup();
        return false;
    }
    for (std::uint32_t index = 0U; index < source.component_count; ++index) {
        const gpu_clip_compose_uniforms uniforms{
            (children[index].mask_source_x << 16U) |
                (children[index].mask_uses_alpha_channel ? 2U : 0U),
            (children[index].mask_source_y << 16U) |
                (index == 0U ? 1U : 0U),
            target_extent.width,
            target_extent.height};
        std::memcpy(
            compose_uniform_bytes.data() +
                static_cast<std::size_t>(index) * 256U,
            &uniforms,
            sizeof(uniforms));
        const std::uint32_t previous_index = (index + 1U) & 1U;
        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, nullptr, 0U, 0U, engine.clip_sampler, nullptr},
            {nullptr, 1U, nullptr, 0U, 0U, nullptr,
                children[index].mask_texture_view},
            {nullptr, 2U, nullptr, 0U, 0U, nullptr,
                index == 0U
                    ? children[index].mask_texture_view
                    : accumulation_views[previous_index]},
            {nullptr, 3U, compose_uniform_buffer, 0U,
                sizeof(gpu_clip_compose_uniforms), nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor descriptor{};
        descriptor.label = webgpu::string_view(
            "ProGPU retained composite-mask binding");
        descriptor.layout = engine.clip_compose_layout;
        descriptor.entryCount = entries.size();
        descriptor.entries = entries.data();
        WGPUBindGroup bind_group = wgpuDeviceCreateBindGroup(
            engine.device, &descriptor);
        if (bind_group == nullptr) {
            cleanup();
            return false;
        }
        compose_bind_groups.push_back(bind_group);
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        compose_uniform_buffer,
        0U,
        compose_uniform_bytes.data(),
        compose_uniform_bytes.size());

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = webgpu::string_view(
        "ProGPU retained composite-mask encoder");
    const bool owns_encoder = engine.semantic_encoder == nullptr;
    encoder = owns_encoder
        ? wgpuDeviceCreateCommandEncoder(engine.device, &encoder_descriptor)
        : engine.semantic_encoder;
    if (encoder == nullptr) {
        cleanup();
        return false;
    }
    for (std::uint32_t index = 0U; index < source.component_count; ++index) {
        WGPURenderPassColorAttachment attachment{};
        webgpu::initialize_color_attachment(attachment);
        attachment.view = accumulation_views[index & 1U];
        attachment.loadOp = WGPULoadOp_Clear;
        attachment.storeOp = WGPUStoreOp_Store;
        attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 1.0};
        WGPURenderPassDescriptor pass_descriptor{};
        pass_descriptor.label = webgpu::string_view(
            "ProGPU retained composite-mask pass");
        pass_descriptor.colorAttachmentCount = 1U;
        pass_descriptor.colorAttachments = &attachment;
        WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
            encoder, &pass_descriptor);
        if (pass == nullptr) {
            cleanup();
            return false;
        }
        const std::uint32_t dynamic_offset = index * 256U;
        wgpuRenderPassEncoderSetPipeline(pass, engine.clip_compose_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            compose_bind_groups[index],
            1U,
            &dynamic_offset);
        wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
    }
    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = webgpu::string_view(
            "ProGPU retained composite-mask commands");
        command = wgpuCommandEncoderFinish(encoder, &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        encoder = nullptr;
        if (command == nullptr) {
            cleanup();
            return false;
        }
        engine.submit(command);
        wgpuCommandBufferRelease(command);
        command = nullptr;
    } else {
        // Child masks are encoded on the semantic encoder. Keep composition
        // on that encoder as well so the GPU observes child production before
        // sampling without forcing an otherwise unnecessary queue submit.
        encoder = nullptr;
    }
    // The owning command buffer or shared encoder retains all transient child
    // resources referenced above; release handles without destroying storage.
    submitted = true;

    const std::uint32_t final_index = (source.component_count - 1U) & 1U;
    gpu_mask_sampling_uniforms sampling{};
    sampling.coordinate1[0] =
        1.0F / static_cast<float>(target_extent.width);
    sampling.coordinate1[1] =
        1.0F / static_cast<float>(target_extent.height);
    sampling.options[0] = 1.0F;
    sampling.options[1] = source.opacity;
    sampling.options[2] = 0.0F;
    sampling.options[3] = 1.0F;
    WGPUBufferDescriptor sampling_descriptor{};
    sampling_descriptor.label = webgpu::string_view(
        "ProGPU retained composite-mask sampling uniforms");
    sampling_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    sampling_descriptor.size = sizeof(sampling);
    sampling_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device, &sampling_descriptor);
    sampling_bind_group = sampling_uniform_buffer == nullptr
        ? nullptr
        : create_layer_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            accumulation_views[final_index],
            "ProGPU retained composite-mask sampling binding",
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

    std::uint64_t upload_bytes = sizeof(sampling) +
        compose_uniform_bytes.size();
    for (const auto& child : children) {
        upload_bytes += child.mask_uniform_upload_bytes;
    }
    operation.mask_texture = accumulation_textures[final_index];
    operation.mask_texture_view = accumulation_views[final_index];
    operation.mask_uniform_buffer = sampling_uniform_buffer;
    operation.mask_bind_group = sampling_bind_group;
    operation.mask_uniform_upload_bytes = static_cast<std::uint32_t>(
        std::min<std::uint64_t>(
            upload_bytes,
            std::numeric_limits<std::uint32_t>::max()));
    accumulation_textures[final_index] = nullptr;
    accumulation_views[final_index] = nullptr;
    sampling_uniform_buffer = nullptr;
    sampling_bind_group = nullptr;
    cleanup();
    ++engine.layer_mask_bind_group_generation;
    return true;
}

} // namespace progpu::native::execution
