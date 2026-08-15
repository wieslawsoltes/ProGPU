#include "progpu_native.h"
#include "progpu_native_geometry_spline.hpp"
#include "progpu_native_gpu_records.hpp"
#include "ClipComposeWgsl.generated.hpp"

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
#include "progpu_native_pipeline.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>

using progpu::native::gpu_clip_compose_uniforms;
using progpu::native::gpu_clip_vertex;

bool create_clip_chain_resources(progpu_native_engine& engine) {
    if (engine.clip_path_pipeline != nullptr &&
        engine.clip_compose_pipeline != nullptr &&
        engine.clip_compose_layout != nullptr &&
        engine.clip_sampler != nullptr) {
        return true;
    }
    if (!create_layer_mask_resources(engine) ||
        !create_path_resources(engine) ||
        engine.clip_compose_shader != nullptr ||
        engine.clip_path_pipeline != nullptr ||
        engine.clip_compose_pipeline != nullptr ||
        engine.clip_compose_layout != nullptr ||
        engine.clip_sampler != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::clip_compose_wgsl,
        progpu::native::generated::clip_compose_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared ClipCompose.wgsl");
    engine.clip_compose_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.clip_compose_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    for (std::uint32_t index = 1U; index <= 2U; ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Fragment;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    entries[3].binding = 3U;
    entries[3].visibility = WGPUShaderStage_Fragment;
    entries[3].buffer.type = WGPUBufferBindingType_Uniform;
    entries[3].buffer.hasDynamicOffset = true;
    entries[3].buffer.minBindingSize = sizeof(gpu_clip_compose_uniforms);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.clip_compose_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.clip_compose_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.clip_compose_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 2U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 8U, 1U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(gpu_clip_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUColorTargetState target{};
    target.format = WGPUTextureFormat_R8Unorm;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState path_fragment{};
    path_fragment.module = engine.clip_compose_shader;
    path_fragment.entryPoint = progpu::native::webgpu::string_view("fs_path");
    path_fragment.targetCount = 1U;
    path_fragment.targets = &target;
    WGPURenderPipelineDescriptor path_descriptor{};
    path_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip path pipeline");
    path_descriptor.layout = pipeline_layout;
    path_descriptor.vertex.module = engine.clip_compose_shader;
    path_descriptor.vertex.entryPoint =
        progpu::native::webgpu::string_view("vs_path");
    path_descriptor.vertex.bufferCount = 1U;
    path_descriptor.vertex.buffers = &vertex_layout;
    path_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    path_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    path_descriptor.primitive.cullMode = WGPUCullMode_None;
    path_descriptor.multisample.count = 1U;
    path_descriptor.multisample.mask = 0xFFFFFFFFU;
    path_descriptor.fragment = &path_fragment;
    engine.clip_path_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &path_descriptor);

    WGPUFragmentState compose_fragment{};
    compose_fragment.module = engine.clip_compose_shader;
    compose_fragment.entryPoint =
        progpu::native::webgpu::string_view("fs_compose");
    compose_fragment.targetCount = 1U;
    compose_fragment.targets = &target;
    WGPURenderPipelineDescriptor compose_descriptor{};
    compose_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip composition pipeline");
    compose_descriptor.layout = pipeline_layout;
    compose_descriptor.vertex.module = engine.clip_compose_shader;
    compose_descriptor.vertex.entryPoint =
        progpu::native::webgpu::string_view("vs_compose");
    compose_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    compose_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    compose_descriptor.primitive.cullMode = WGPUCullMode_None;
    compose_descriptor.multisample.count = 1U;
    compose_descriptor.multisample.mask = 0xFFFFFFFFU;
    compose_descriptor.fragment = &compose_fragment;
    engine.clip_compose_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &compose_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.clip_path_pipeline == nullptr ||
        engine.clip_compose_pipeline == nullptr) {
        return false;
    }

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.clip_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    return engine.clip_sampler != nullptr;
}

bool ensure_clip_buffer(
    progpu_native_engine& engine,
    WGPUBuffer& buffer,
    std::uint64_t& capacity,
    std::uint64_t required,
    progpu::native::webgpu::buffer_usage_flags usage,
    const char* label) {
    if (buffer != nullptr && required <= capacity) {
        return true;
    }
    std::uint64_t replacement_size = std::max<std::uint64_t>(256U, capacity);
    while (replacement_size < required) {
        if (replacement_size >
            std::numeric_limits<std::uint64_t>::max() / 2U) {
            return false;
        }
        replacement_size *= 2U;
    }
    WGPUBufferDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.usage = usage;
    descriptor.size = replacement_size;
    WGPUBuffer replacement = wgpuDeviceCreateBuffer(
        engine.device,
        &descriptor);
    if (replacement == nullptr) {
        return false;
    }
    if (buffer != nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
    }
    buffer = replacement;
    capacity = replacement_size;
    return true;
}

void release_clip_bind_groups(progpu_native_engine& engine) noexcept {
    for (auto& bind_group : engine.layer_clip_mask_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : engine.clip_compose_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    if (engine.clip_path_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.clip_path_bind_group);
        engine.clip_path_bind_group = nullptr;
    }
}

bool rebuild_clip_bind_groups(progpu_native_engine& engine) {
    release_clip_bind_groups(engine);
    const std::array<WGPUBindGroupEntry, 4U> path_entries{{
        {nullptr, 0U, nullptr, 0U, 0U, engine.clip_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, engine.clip_atlas_view},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, engine.clip_atlas_view},
        {nullptr, 3U, engine.clip_compose_uniform_buffer, 0U,
            sizeof(gpu_clip_compose_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor path_descriptor{};
    path_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip path bind group");
    path_descriptor.layout = engine.clip_compose_layout;
    path_descriptor.entryCount = path_entries.size();
    path_descriptor.entries = path_entries.data();
    engine.clip_path_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &path_descriptor);
    if (engine.clip_path_bind_group == nullptr) {
        return false;
    }

    for (std::size_t index = 0U; index < 2U; ++index) {
        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, nullptr, 0U, 0U, engine.clip_sampler, nullptr},
            {nullptr, 1U, nullptr, 0U, 0U, nullptr, engine.clip_node_view},
            {nullptr, 2U, nullptr, 0U, 0U, nullptr,
                engine.clip_accumulation_views[index]},
            {nullptr, 3U, engine.clip_compose_uniform_buffer, 0U,
                sizeof(gpu_clip_compose_uniforms), nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip composition bind group");
        descriptor.layout = engine.clip_compose_layout;
        descriptor.entryCount = entries.size();
        descriptor.entries = entries.data();
        engine.clip_compose_bind_groups[index] =
            wgpuDeviceCreateBindGroup(engine.device, &descriptor);
        engine.layer_clip_mask_bind_groups[index] =
            create_layer_mask_bind_group(
                engine,
                engine.image_linear_sampler,
                engine.clip_accumulation_views[index],
                "ProGPU native retained clip final mask bind group");
        if (engine.clip_compose_bind_groups[index] == nullptr ||
            engine.layer_clip_mask_bind_groups[index] == nullptr) {
            return false;
        }
    }
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool ensure_clip_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t atlas_size) {
    const bool atlas_changed = engine.clip_atlas_texture == nullptr ||
        engine.clip_atlas_size != atlas_size;
    const bool target_changed = engine.clip_node_texture == nullptr ||
        engine.clip_width != width || engine.clip_height != height;
    if (!atlas_changed && !target_changed) {
        return true;
    }
    release_clip_bind_groups(engine);

    if (atlas_changed) {
        WGPUTextureDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip atlas");
        descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        descriptor.dimension = WGPUTextureDimension_2D;
        descriptor.size = {atlas_size, atlas_size, 1U};
        descriptor.format = WGPUTextureFormat_R8Unorm;
        descriptor.mipLevelCount = 1U;
        descriptor.sampleCount = 1U;
        WGPUTexture texture = wgpuDeviceCreateTexture(
            engine.device,
            &descriptor);
        WGPUTextureView view = texture == nullptr
            ? nullptr
            : wgpuTextureCreateView(texture, nullptr);
        if (texture == nullptr || view == nullptr) {
            if (view != nullptr) wgpuTextureViewRelease(view);
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
            }
            return false;
        }
        if (engine.clip_atlas_view != nullptr) {
            wgpuTextureViewRelease(engine.clip_atlas_view);
        }
        if (engine.clip_atlas_texture != nullptr) {
            wgpuTextureDestroy(engine.clip_atlas_texture);
            wgpuTextureRelease(engine.clip_atlas_texture);
        }
        engine.clip_atlas_texture = texture;
        engine.clip_atlas_view = view;
        engine.clip_atlas_size = atlas_size;
        ++engine.clip_atlas_generation;
    }

    if (target_changed) {
        const auto create_texture = [&](const char* label) {
            WGPUTextureDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(label);
            descriptor.usage = WGPUTextureUsage_RenderAttachment |
                WGPUTextureUsage_TextureBinding;
            descriptor.dimension = WGPUTextureDimension_2D;
            descriptor.size = {width, height, 1U};
            descriptor.format = WGPUTextureFormat_R8Unorm;
            descriptor.mipLevelCount = 1U;
            descriptor.sampleCount = 1U;
            return wgpuDeviceCreateTexture(engine.device, &descriptor);
        };
        WGPUTexture node = create_texture(
            "ProGPU native retained clip node mask");
        WGPUTextureView node_view = node == nullptr
            ? nullptr
            : wgpuTextureCreateView(node, nullptr);
        std::array<WGPUTexture, 2U> accumulation{{
            create_texture("ProGPU native retained clip accumulation A"),
            create_texture("ProGPU native retained clip accumulation B")
        }};
        std::array<WGPUTextureView, 2U> accumulation_views{{
            accumulation[0] == nullptr
                ? nullptr
                : wgpuTextureCreateView(accumulation[0], nullptr),
            accumulation[1] == nullptr
                ? nullptr
                : wgpuTextureCreateView(accumulation[1], nullptr)
        }};
        if (node == nullptr || node_view == nullptr ||
            accumulation[0] == nullptr || accumulation[1] == nullptr ||
            accumulation_views[0] == nullptr ||
            accumulation_views[1] == nullptr) {
            if (node_view != nullptr) wgpuTextureViewRelease(node_view);
            if (node != nullptr) {
                wgpuTextureDestroy(node);
                wgpuTextureRelease(node);
            }
            for (std::size_t index = 0U; index < 2U; ++index) {
                if (accumulation_views[index] != nullptr) {
                    wgpuTextureViewRelease(accumulation_views[index]);
                }
                if (accumulation[index] != nullptr) {
                    wgpuTextureDestroy(accumulation[index]);
                    wgpuTextureRelease(accumulation[index]);
                }
            }
            return false;
        }
        if (engine.clip_node_view != nullptr) {
            wgpuTextureViewRelease(engine.clip_node_view);
        }
        if (engine.clip_node_texture != nullptr) {
            wgpuTextureDestroy(engine.clip_node_texture);
            wgpuTextureRelease(engine.clip_node_texture);
        }
        for (std::size_t index = 0U; index < 2U; ++index) {
            if (engine.clip_accumulation_views[index] != nullptr) {
                wgpuTextureViewRelease(
                    engine.clip_accumulation_views[index]);
            }
            if (engine.clip_accumulation_textures[index] != nullptr) {
                wgpuTextureDestroy(
                    engine.clip_accumulation_textures[index]);
                wgpuTextureRelease(
                    engine.clip_accumulation_textures[index]);
            }
        }
        engine.clip_node_texture = node;
        engine.clip_node_view = node_view;
        engine.clip_accumulation_textures = accumulation;
        engine.clip_accumulation_views = accumulation_views;
        engine.clip_width = width;
        engine.clip_height = height;
        ++engine.clip_texture_generation;
    }
    engine.clip_cache_valid = false;
    return true;
}
