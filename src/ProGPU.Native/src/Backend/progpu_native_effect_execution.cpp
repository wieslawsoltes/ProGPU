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

} // namespace progpu::native::execution
