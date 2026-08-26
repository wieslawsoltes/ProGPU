#include "progpu_native.h"
#include "progpu_native_geometry_spline.hpp"
#include "progpu_native_gpu_records.hpp"
#include "GlyphRasterizerWgsl.generated.hpp"
#include "PathRasterizerWgsl.generated.hpp"
#include "TextWgsl.generated.hpp"

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

#include <array>
#include <cstdint>
#include <limits>

using progpu::native::gpu_glyph_record;
using progpu::native::gpu_glyph_uniforms;
using progpu::native::gpu_path_record;
using progpu::native::gpu_path_uniforms;
using progpu::native::gpu_uniforms;
using progpu::native::native_initial_atlas_size;

namespace {

progpu::native::webgpu::texture_usage_flags glyph_atlas_usage(
    bool raster_shader_fallback) noexcept {
    using usage_flags = progpu::native::webgpu::texture_usage_flags;
    usage_flags usage =
        static_cast<usage_flags>(WGPUTextureUsage_TextureBinding) |
        static_cast<usage_flags>(WGPUTextureUsage_CopyDst);
    if (raster_shader_fallback) {
        usage |= static_cast<usage_flags>(
            WGPUTextureUsage_RenderAttachment);
    }
    return usage;
}

WGPUBindGroup create_text_uniform_bind_group(
    progpu_native_engine& engine,
    WGPUBuffer style_buffer,
    std::uint64_t style_buffer_size,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U,
            sizeof(gpu_uniforms), nullptr, nullptr},
        {nullptr, 1U, style_buffer, 0U,
            style_buffer_size, nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.text_uniform_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

WGPUBindGroup create_text_atlas_bind_group(
    progpu_native_engine& engine,
    const char* label) {
    if (engine.glyph_atlas_sampler == nullptr ||
        engine.glyph_atlas_texture_view == nullptr ||
        engine.text_atlas_layout == nullptr) {
        return nullptr;
    }
    WGPUTextureView color_view = engine.color_glyph_atlas_texture_view !=
            nullptr
        ? engine.color_glyph_atlas_texture_view
        : engine.analytic_sentinel_texture_view;
    if (color_view == nullptr) {
        return nullptr;
    }
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.glyph_atlas_texture_view},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, color_view}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.text_atlas_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

} // namespace

bool create_path_resources(progpu_native_engine& engine) {
    if (engine.path_raster_pipeline != nullptr &&
        engine.path_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.path_raster_shader != nullptr ||
        engine.path_raster_pipeline != nullptr ||
        engine.path_raster_layout != nullptr ||
        engine.path_raster_pipeline_layout != nullptr ||
        engine.path_atlas_sampler != nullptr ||
        engine.path_atlas_texture != nullptr ||
        engine.path_atlas_texture_view != nullptr ||
        engine.path_atlas_bind_group != nullptr) {
        return false;
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::path_rasterizer_wgsl,
        progpu::native::generated::path_rasterizer_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared PathRasterizer.wgsl");
    engine.path_raster_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.path_raster_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> layout_entries{};
    for (std::uint32_t index = 0U; index < layout_entries.size(); ++index) {
        layout_entries[index].binding = index;
        layout_entries[index].visibility = WGPUShaderStage_Compute;
        layout_entries[index].buffer.hasDynamicOffset = false;
        layout_entries[index].buffer.type = index == 3U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
    }
    layout_entries[0].buffer.minBindingSize = sizeof(gpu_path_uniforms);
    layout_entries[1].buffer.minBindingSize = sizeof(gpu_path_record);
    layout_entries[2].buffer.minBindingSize = sizeof(progpu_native_path_segment);
    layout_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster bindings");
    layout_descriptor.entryCount = layout_entries.size();
    layout_descriptor.entries = layout_entries.data();
    engine.path_raster_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.path_raster_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.path_raster_layout;
    engine.path_raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (engine.path_raster_pipeline_layout == nullptr) {
        return false;
    }

    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster pipeline");
    pipeline_descriptor.layout = engine.path_raster_pipeline_layout;
    pipeline_descriptor.compute.module = engine.path_raster_shader;
    pipeline_descriptor.compute.entryPoint = progpu::native::webgpu::string_view("cs_main");
    engine.path_raster_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.path_raster_pipeline == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path atlas");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {
        engine.path_atlas_size,
        engine.path_atlas_size,
        1U
    };
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.path_atlas_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.path_atlas_texture == nullptr) {
        return false;
    }
    engine.path_atlas_texture_view = wgpuTextureCreateView(
        engine.path_atlas_texture,
        nullptr);

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.path_atlas_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.path_atlas_texture_view == nullptr ||
        engine.path_atlas_sampler == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupEntry, 2U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.path_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.path_atlas_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas bind group");
    atlas_descriptor.layout = engine.analytic_atlas_layout;
    atlas_descriptor.entryCount = atlas_entries.size();
    atlas_descriptor.entries = atlas_entries.data();
    engine.path_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_descriptor);
    if (engine.path_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.path_atlas_generation;
    return true;
}

bool create_glyph_resources(progpu_native_engine& engine) {
    const bool raster_shader_fallback =
        (engine.engine_flags &
            PROGPU_NATIVE_ENGINE_GLYPH_RASTER_SHADER_FALLBACK) != 0U;
    const bool glyph_raster_pipeline_ready = raster_shader_fallback
        ? engine.glyph_raster_fallback_pipeline != nullptr
        : engine.glyph_raster_pipeline != nullptr;
    if (glyph_raster_pipeline_ready &&
        engine.text_pipeline != nullptr &&
        engine.text_uniform_bind_group != nullptr &&
        engine.text_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.glyph_raster_shader != nullptr ||
        engine.glyph_raster_pipeline != nullptr ||
        engine.glyph_raster_layout != nullptr ||
        engine.glyph_raster_pipeline_layout != nullptr ||
        engine.glyph_raster_fallback_pipeline != nullptr ||
        engine.glyph_raster_fallback_layout != nullptr ||
        engine.glyph_raster_fallback_pipeline_layout != nullptr ||
        engine.text_shader != nullptr || engine.text_pipeline != nullptr ||
        engine.text_uniform_layout != nullptr ||
        engine.text_atlas_layout != nullptr ||
        engine.text_style_buffer != nullptr ||
        engine.text_uniform_bind_group != nullptr ||
        engine.glyph_atlas_sampler != nullptr ||
        engine.glyph_atlas_texture != nullptr ||
        engine.glyph_atlas_texture_view != nullptr ||
        engine.text_atlas_bind_group != nullptr) {
        return false;
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return false;
    }

    progpu::native::webgpu::wgsl_source glyph_wgsl(
        progpu::native::generated::glyph_rasterizer_wgsl,
        progpu::native::generated::glyph_rasterizer_wgsl_size);
    WGPUShaderModuleDescriptor glyph_shader_descriptor{};
    glyph_shader_descriptor.nextInChain = glyph_wgsl.chain();
    glyph_shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared GlyphRasterizer.wgsl");
    engine.glyph_raster_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &glyph_shader_descriptor);
    if (engine.glyph_raster_shader == nullptr) {
        return false;
    }

    if (raster_shader_fallback) {
        std::array<WGPUBindGroupLayoutEntry, 3U> raster_entries{};
        for (std::uint32_t index = 0U;
             index < raster_entries.size();
             ++index) {
            raster_entries[index].binding = index;
            raster_entries[index].visibility = WGPUShaderStage_Fragment;
            raster_entries[index].buffer.type = index == 0U
                ? WGPUBufferBindingType_Uniform
                : WGPUBufferBindingType_ReadOnlyStorage;
        }
        raster_entries[0].buffer.hasDynamicOffset = true;
        raster_entries[0].buffer.minBindingSize =
            sizeof(gpu_glyph_uniforms);
        raster_entries[1].buffer.minBindingSize =
            sizeof(gpu_glyph_record);
        raster_entries[2].buffer.minBindingSize =
            sizeof(progpu_native_path_segment);
        WGPUBindGroupLayoutDescriptor raster_layout_descriptor{};
        raster_layout_descriptor.label =
            progpu::native::webgpu::string_view(
                "ProGPU native glyph raster shader bindings");
        raster_layout_descriptor.entryCount = raster_entries.size();
        raster_layout_descriptor.entries = raster_entries.data();
        engine.glyph_raster_fallback_layout =
            wgpuDeviceCreateBindGroupLayout(
                engine.device,
                &raster_layout_descriptor);
        if (engine.glyph_raster_fallback_layout == nullptr) {
            return false;
        }
        WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
        pipeline_layout_descriptor.label =
            progpu::native::webgpu::string_view(
                "ProGPU native glyph raster shader layout");
        pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
        pipeline_layout_descriptor.bindGroupLayouts =
            &engine.glyph_raster_fallback_layout;
        engine.glyph_raster_fallback_pipeline_layout =
            wgpuDeviceCreatePipelineLayout(
                engine.device,
                &pipeline_layout_descriptor);
        if (engine.glyph_raster_fallback_pipeline_layout == nullptr) {
            return false;
        }
        WGPUVertexState vertex{};
        vertex.module = engine.glyph_raster_shader;
        vertex.entryPoint = progpu::native::webgpu::string_view(
            "vs_raster_fallback");
        WGPUColorTargetState target{};
        target.format = WGPUTextureFormat_R8Unorm;
        target.writeMask = WGPUColorWriteMask_All;
        WGPUFragmentState fragment{};
        fragment.module = engine.glyph_raster_shader;
        fragment.entryPoint = progpu::native::webgpu::string_view(
            "fs_raster_fallback");
        fragment.targetCount = 1U;
        fragment.targets = &target;
        WGPURenderPipelineDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native glyph raster shader fallback pipeline");
        descriptor.layout =
            engine.glyph_raster_fallback_pipeline_layout;
        descriptor.vertex = vertex;
        descriptor.primitive.topology =
            WGPUPrimitiveTopology_TriangleList;
        descriptor.primitive.frontFace = WGPUFrontFace_CCW;
        descriptor.primitive.cullMode = WGPUCullMode_None;
        descriptor.multisample.count = 1U;
        descriptor.multisample.mask = 0xFFFFFFFFU;
        descriptor.fragment = &fragment;
        engine.glyph_raster_fallback_pipeline =
            wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
        if (engine.glyph_raster_fallback_pipeline == nullptr) {
            return false;
        }
    } else {
        std::array<WGPUBindGroupLayoutEntry, 4U> compute_entries{};
        for (std::uint32_t index = 0U;
             index < compute_entries.size();
             ++index) {
            compute_entries[index].binding = index;
            compute_entries[index].visibility = WGPUShaderStage_Compute;
            compute_entries[index].buffer.type = index == 0U
                ? WGPUBufferBindingType_Uniform
                : index == 3U
                ? WGPUBufferBindingType_Storage
                : WGPUBufferBindingType_ReadOnlyStorage;
        }
        compute_entries[0].buffer.hasDynamicOffset = true;
        compute_entries[0].buffer.minBindingSize =
            sizeof(gpu_glyph_uniforms);
        compute_entries[1].buffer.minBindingSize =
            sizeof(gpu_glyph_record);
        compute_entries[2].buffer.minBindingSize =
            sizeof(progpu_native_path_segment);
        compute_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
        WGPUBindGroupLayoutDescriptor compute_layout_descriptor{};
        compute_layout_descriptor.label =
            progpu::native::webgpu::string_view(
                "ProGPU native glyph raster bindings");
        compute_layout_descriptor.entryCount = compute_entries.size();
        compute_layout_descriptor.entries = compute_entries.data();
        engine.glyph_raster_layout = wgpuDeviceCreateBindGroupLayout(
            engine.device,
            &compute_layout_descriptor);
        if (engine.glyph_raster_layout == nullptr) {
            return false;
        }
        WGPUPipelineLayoutDescriptor compute_pipeline_layout_descriptor{};
        compute_pipeline_layout_descriptor.label =
            progpu::native::webgpu::string_view(
                "ProGPU native glyph raster layout");
        compute_pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
        compute_pipeline_layout_descriptor.bindGroupLayouts =
            &engine.glyph_raster_layout;
        engine.glyph_raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(
            engine.device,
            &compute_pipeline_layout_descriptor);
        if (engine.glyph_raster_pipeline_layout == nullptr) {
            return false;
        }
        WGPUComputePipelineDescriptor compute_pipeline_descriptor{};
        compute_pipeline_descriptor.label =
            progpu::native::webgpu::string_view(
                "ProGPU native glyph raster pipeline");
        compute_pipeline_descriptor.layout =
            engine.glyph_raster_pipeline_layout;
        compute_pipeline_descriptor.compute.module =
            engine.glyph_raster_shader;
        compute_pipeline_descriptor.compute.entryPoint =
            progpu::native::webgpu::string_view("cs_main");
        engine.glyph_raster_pipeline = wgpuDeviceCreateComputePipeline(
            engine.device,
            &compute_pipeline_descriptor);
        if (engine.glyph_raster_pipeline == nullptr) {
            return false;
        }
    }

    progpu::native::webgpu::wgsl_source text_wgsl(
        progpu::native::generated::text_wgsl,
        progpu::native::generated::text_wgsl_size);
    WGPUShaderModuleDescriptor text_shader_descriptor{};
    text_shader_descriptor.nextInChain = text_wgsl.chain();
    text_shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Text.wgsl");
    engine.text_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &text_shader_descriptor);
    if (engine.text_shader == nullptr) {
        return false;
    }

    if (!create_text_pipeline(engine)) {
        return false;
    }

    WGPUBufferDescriptor style_descriptor{};
    style_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text style sentinel");
    style_descriptor.usage = WGPUBufferUsage_Storage |
        WGPUBufferUsage_CopyDst;
    style_descriptor.size = 32U;
    engine.text_style_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &style_descriptor);
    if (engine.text_style_buffer == nullptr) {
        return false;
    }
    engine.text_style_buffer_size = style_descriptor.size;
    std::array<std::byte, 32U> style_bytes{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.text_style_buffer,
        0U,
        style_bytes.data(),
        style_bytes.size());
    engine.text_uniform_bind_group = create_text_uniform_bind_group(
        engine,
        engine.text_style_buffer,
        engine.text_style_buffer_size,
        "ProGPU native text uniform bind group");
    if (engine.text_uniform_bind_group == nullptr) {
        return false;
    }

    WGPUTextureDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    atlas_descriptor.usage = glyph_atlas_usage(raster_shader_fallback);
    atlas_descriptor.dimension = WGPUTextureDimension_2D;
    atlas_descriptor.size = {
        engine.glyph_atlas_size,
        engine.glyph_atlas_size,
        1U
    };
    atlas_descriptor.format = WGPUTextureFormat_R8Unorm;
    atlas_descriptor.mipLevelCount = 1U;
    atlas_descriptor.sampleCount = 1U;
    engine.glyph_atlas_texture = wgpuDeviceCreateTexture(
        engine.device,
        &atlas_descriptor);
    if (engine.glyph_atlas_texture == nullptr) {
        return false;
    }
    engine.glyph_atlas_texture_view = wgpuTextureCreateView(
        engine.glyph_atlas_texture,
        nullptr);
    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.glyph_atlas_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.glyph_atlas_texture_view == nullptr ||
        engine.glyph_atlas_sampler == nullptr) {
        return false;
    }
    engine.text_atlas_bind_group = create_text_atlas_bind_group(
        engine,
        "ProGPU native text atlas bind group");
    if (engine.text_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.glyph_atlas_generation;
    return true;
}

bool ensure_text_style_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_size) {
    if (required_size == 0U || engine.text_uniform_layout == nullptr ||
        engine.analytic_uniform_buffer == nullptr) {
        return false;
    }
    if (engine.text_style_buffer != nullptr &&
        required_size <= engine.text_style_buffer_size) {
        return true;
    }
    std::uint64_t capacity = 0U;
    if (!progpu::native::try_calculate_buffer_capacity(
            engine.text_style_buffer_size,
            required_size,
            32U,
            engine.max_buffer_size,
            capacity)) {
        return false;
    }
    WGPUBufferDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained text style page");
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    descriptor.size = capacity;
    WGPUBuffer replacement = wgpuDeviceCreateBuffer(
        engine.device,
        &descriptor);
    if (replacement == nullptr) {
        return false;
    }
    WGPUBindGroup replacement_group = create_text_uniform_bind_group(
        engine,
        replacement,
        capacity,
        "ProGPU native retained text uniform bind group");
    if (replacement_group == nullptr) {
        wgpuBufferDestroy(replacement);
        wgpuBufferRelease(replacement);
        return false;
    }
    engine.release_semantic_layer_text_bindings();
    if (engine.text_uniform_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.text_uniform_bind_group);
    }
    if (engine.text_style_buffer != nullptr) {
        wgpuBufferDestroy(engine.text_style_buffer);
        wgpuBufferRelease(engine.text_style_buffer);
    }
    engine.text_style_buffer = replacement;
    engine.text_style_buffer_size = capacity;
    engine.text_uniform_bind_group = replacement_group;
    engine.text_style_owner_hash = 0U;
    return true;
}

bool resize_path_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size) {
    if (requested_size <= engine.path_atlas_size) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path atlas");
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {requested_size, requested_size, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
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
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.path_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view}
    }};
    WGPUBindGroupDescriptor group_descriptor{};
    group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas bind group");
    group_descriptor.layout = engine.analytic_atlas_layout;
    group_descriptor.entryCount = entries.size();
    group_descriptor.entries = entries.data();
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(
        engine.device,
        &group_descriptor);
    if (group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    wgpuBindGroupRelease(engine.path_atlas_bind_group);
    wgpuTextureViewRelease(engine.path_atlas_texture_view);
    wgpuTextureDestroy(engine.path_atlas_texture);
    wgpuTextureRelease(engine.path_atlas_texture);
    engine.path_atlas_bind_group = group;
    engine.path_atlas_texture_view = view;
    engine.path_atlas_texture = texture;
    engine.path_atlas_size = requested_size;
    ++engine.path_atlas_generation;
    return true;
}

bool resize_glyph_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size) {
    if (requested_size <= engine.glyph_atlas_size) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    descriptor.usage = glyph_atlas_usage(
        (engine.engine_flags &
            PROGPU_NATIVE_ENGINE_GLYPH_RASTER_SHADER_FALLBACK) != 0U);
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {requested_size, requested_size, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
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
    WGPUTexture old_texture = engine.glyph_atlas_texture;
    WGPUTextureView old_view = engine.glyph_atlas_texture_view;
    engine.glyph_atlas_texture_view = view;
    engine.glyph_atlas_texture = texture;
    WGPUBindGroup group = create_text_atlas_bind_group(
        engine,
        "ProGPU native resized text atlas bind group");
    if (group == nullptr) {
        engine.glyph_atlas_texture = old_texture;
        engine.glyph_atlas_texture_view = old_view;
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    wgpuBindGroupRelease(engine.text_atlas_bind_group);
    engine.text_atlas_bind_group = group;
    wgpuTextureViewRelease(old_view);
    wgpuTextureDestroy(old_texture);
    wgpuTextureRelease(old_texture);
    engine.glyph_atlas_size = requested_size;
    ++engine.glyph_atlas_generation;
    ++engine.glyph_atlas_growth_count;
    return true;
}

bool refresh_text_atlas_bind_group(progpu_native_engine& engine) {
    WGPUBindGroup replacement = create_text_atlas_bind_group(
        engine,
        "ProGPU native refreshed text atlas bind group");
    if (replacement == nullptr) {
        return false;
    }
    if (engine.text_atlas_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.text_atlas_bind_group);
    }
    engine.text_atlas_bind_group = replacement;
    return true;
}
