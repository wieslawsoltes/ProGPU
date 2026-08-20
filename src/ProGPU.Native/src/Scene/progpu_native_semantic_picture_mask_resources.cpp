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

namespace progpu::native::execution {
namespace {

constexpr std::uint64_t dynamic_uniform_alignment = 256U;

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

bool create_semantic_picture_mask_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_picture_mask& picture,
    const std::byte* nested_scene,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation) {
    if (nested_scene == nullptr || picture.stream_size == 0U ||
        target_extent.width == 0U || target_extent.height == 0U ||
        !std::isfinite(dpi_scale) || dpi_scale <= 0.0F ||
        target_extent.x > 16384U - target_extent.width ||
        target_extent.y > 16384U - target_extent.height ||
        !create_clip_chain_resources(engine) ||
        !create_layer_mask_resources(engine)) {
        return false;
    }

    const std::uint32_t source_width =
        target_extent.x + target_extent.width;
    const std::uint32_t source_height =
        target_extent.y + target_extent.height;
    const std::uint64_t source_bytes =
        static_cast<std::uint64_t>(source_width) * source_height * 4U;
    const std::uint64_t mask_bytes =
        static_cast<std::uint64_t>(target_extent.width) * target_extent.height;
    if (source_bytes > PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES ||
        mask_bytes > PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES - source_bytes) {
        return false;
    }

    WGPUTexture source_texture = nullptr;
    WGPUTextureView source_view = nullptr;
    WGPUTexture mask_texture = nullptr;
    WGPUTextureView mask_view = nullptr;
    WGPUBuffer extract_uniform_buffer = nullptr;
    WGPUBuffer sampling_uniform_buffer = nullptr;
    WGPUBindGroup extract_bind_group = nullptr;
    WGPUBindGroup sampling_bind_group = nullptr;
    WGPUCommandEncoder encoder = nullptr;
    WGPUCommandBuffer command = nullptr;
    std::unique_ptr<progpu_native_engine> child;
    const auto cleanup = [&]() noexcept {
        if (command != nullptr) {
            wgpuCommandBufferRelease(command);
            command = nullptr;
        }
        if (encoder != nullptr) {
            wgpuCommandEncoderRelease(encoder);
            encoder = nullptr;
        }
        if (sampling_bind_group != nullptr) {
            wgpuBindGroupRelease(sampling_bind_group);
            sampling_bind_group = nullptr;
        }
        if (extract_bind_group != nullptr) {
            wgpuBindGroupRelease(extract_bind_group);
            extract_bind_group = nullptr;
        }
        release_buffer(sampling_uniform_buffer);
        release_buffer(extract_uniform_buffer);
        release_texture(mask_texture, mask_view);
        release_texture(source_texture, source_view);
    };

    WGPUTextureDescriptor source_descriptor{};
    source_descriptor.label = webgpu::string_view(
        "ProGPU retained picture-mask RGBA source");
    source_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
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
    child_frame.dpi_scale = dpi_scale;
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

    WGPUTextureDescriptor mask_descriptor{};
    mask_descriptor.label = webgpu::string_view(
        "ProGPU retained picture opacity-mask R8 coverage");
    mask_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    mask_descriptor.dimension = WGPUTextureDimension_2D;
    mask_descriptor.size = {
        target_extent.width,
        target_extent.height,
        1U};
    mask_descriptor.format = WGPUTextureFormat_R8Unorm;
    mask_descriptor.mipLevelCount = 1U;
    mask_descriptor.sampleCount = 1U;
    mask_texture = wgpuDeviceCreateTexture(engine.device, &mask_descriptor);
    mask_view = mask_texture == nullptr
        ? nullptr
        : wgpuTextureCreateView(mask_texture, nullptr);
    extract_uniform_buffer = create_uniform_buffer(
        engine,
        "ProGPU retained picture-mask alpha extraction uniforms",
        dynamic_uniform_alignment);
    if (mask_view == nullptr || extract_uniform_buffer == nullptr) {
        cleanup();
        return false;
    }
    const gpu_clip_compose_uniforms extract_uniforms{
        target_extent.x,
        target_extent.y,
        source_width,
        source_height};
    wgpuQueueWriteBuffer(
        engine.queue,
        extract_uniform_buffer,
        0U,
        &extract_uniforms,
        sizeof(extract_uniforms));
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, engine.clip_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, source_view},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, source_view},
        {nullptr, 3U, extract_uniform_buffer, 0U,
            sizeof(gpu_clip_compose_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor bind_descriptor{};
    bind_descriptor.label = webgpu::string_view(
        "ProGPU retained picture-mask alpha extraction binding");
    bind_descriptor.layout = engine.clip_compose_layout;
    bind_descriptor.entryCount = entries.size();
    bind_descriptor.entries = entries.data();
    extract_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_descriptor);
    if (extract_bind_group == nullptr) {
        cleanup();
        return false;
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = webgpu::string_view(
        "ProGPU retained picture-mask alpha extraction encoder");
    encoder = wgpuDeviceCreateCommandEncoder(
        engine.device,
        &encoder_descriptor);
    WGPURenderPassColorAttachment color{};
    webgpu::initialize_color_attachment(color);
    color.view = mask_view;
    color.loadOp = WGPULoadOp_Clear;
    color.storeOp = WGPUStoreOp_Store;
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = webgpu::string_view(
        "ProGPU retained picture-mask alpha extraction pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color;
    WGPURenderPassEncoder pass = encoder == nullptr
        ? nullptr
        : wgpuCommandEncoderBeginRenderPass(encoder, &pass_descriptor);
    if (pass == nullptr) {
        cleanup();
        return false;
    }
    const std::uint32_t dynamic_offset = 0U;
    wgpuRenderPassEncoderSetPipeline(
        pass,
        engine.clip_alpha_extract_pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        extract_bind_group,
        1U,
        &dynamic_offset);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = webgpu::string_view(
        "ProGPU retained picture-mask alpha extraction commands");
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

    gpu_mask_sampling_uniforms sampling{};
    sampling.coordinate1[0] =
        1.0F / static_cast<float>(target_extent.width);
    sampling.coordinate1[1] =
        1.0F / static_cast<float>(target_extent.height);
    sampling.options[0] = 1.0F;
    sampling.options[1] = picture.opacity;
    sampling.options[3] = 1.0F;
    sampling_uniform_buffer = create_uniform_buffer(
        engine,
        "ProGPU retained picture-mask sampling uniforms",
        sizeof(sampling));
    sampling_bind_group = sampling_uniform_buffer == nullptr
        ? nullptr
        : create_layer_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            mask_view,
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

    operation.mask_texture = mask_texture;
    operation.mask_texture_view = mask_view;
    operation.mask_uniform_buffer = sampling_uniform_buffer;
    operation.mask_bind_group = sampling_bind_group;
    operation.mask_uniform_upload_bytes =
        sizeof(sampling) + sizeof(extract_uniforms) +
        child_metrics.uniform_upload_bytes;
    mask_texture = nullptr;
    mask_view = nullptr;
    sampling_uniform_buffer = nullptr;
    sampling_bind_group = nullptr;
    release_buffer(extract_uniform_buffer);
    if (extract_bind_group != nullptr) {
        wgpuBindGroupRelease(extract_bind_group);
        extract_bind_group = nullptr;
    }
    release_texture(source_texture, source_view);
    ++engine.layer_mask_bind_group_generation;
    return true;
}

} // namespace progpu::native::execution
