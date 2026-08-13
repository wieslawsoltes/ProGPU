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

using progpu::native::gpu_glyph_record;
using progpu::native::gpu_glyph_uniforms;
using progpu::native::gpu_path_record;
using progpu::native::gpu_path_uniforms;
using progpu::native::gpu_uniforms;
using progpu::native::native_initial_atlas_size;

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
    if (engine.glyph_raster_pipeline != nullptr &&
        engine.text_pipeline != nullptr &&
        engine.text_uniform_bind_group != nullptr &&
        engine.text_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.glyph_raster_shader != nullptr ||
        engine.glyph_raster_pipeline != nullptr ||
        engine.glyph_raster_layout != nullptr ||
        engine.glyph_raster_pipeline_layout != nullptr ||
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

    std::array<WGPUBindGroupLayoutEntry, 4U> compute_entries{};
    for (std::uint32_t index = 0U; index < compute_entries.size(); ++index) {
        compute_entries[index].binding = index;
        compute_entries[index].visibility = WGPUShaderStage_Compute;
        compute_entries[index].buffer.type = index == 0U
            ? WGPUBufferBindingType_Uniform
            : index == 3U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
    }
    compute_entries[0].buffer.hasDynamicOffset = true;
    compute_entries[0].buffer.minBindingSize = sizeof(gpu_glyph_uniforms);
    compute_entries[1].buffer.minBindingSize = sizeof(gpu_glyph_record);
    compute_entries[2].buffer.minBindingSize = sizeof(progpu_native_path_segment);
    compute_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
    WGPUBindGroupLayoutDescriptor compute_layout_descriptor{};
    compute_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster bindings");
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
    compute_pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster pipeline");
    compute_pipeline_descriptor.layout = engine.glyph_raster_pipeline_layout;
    compute_pipeline_descriptor.compute.module = engine.glyph_raster_shader;
    compute_pipeline_descriptor.compute.entryPoint = progpu::native::webgpu::string_view("cs_main");
    engine.glyph_raster_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &compute_pipeline_descriptor);
    if (engine.glyph_raster_pipeline == nullptr) {
        return false;
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

    const std::array<WGPUVertexAttribute, 8U> text_attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 16U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 24U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 40U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 56U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 72U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 88U, 7U)
    }};
    WGPUVertexBufferLayout text_vertex_layout{};
    text_vertex_layout.arrayStride = sizeof(gpu_glyph_instance);
    text_vertex_layout.stepMode = WGPUVertexStepMode_Instance;
    text_vertex_layout.attributeCount = text_attributes.size();
    text_vertex_layout.attributes = text_attributes.data();
    WGPUVertexState text_vertex_state{};
    text_vertex_state.module = engine.text_shader;
    text_vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    text_vertex_state.bufferCount = 1U;
    text_vertex_state.buffers = &text_vertex_layout;
    WGPUBlendState text_blend{};
    text_blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    text_blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    text_blend.color.operation = WGPUBlendOperation_Add;
    text_blend.alpha.srcFactor = WGPUBlendFactor_One;
    text_blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    text_blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState text_target{};
    text_target.format = engine.target_format;
    text_target.blend = &text_blend;
    text_target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState text_fragment_state{};
    text_fragment_state.module = engine.text_shader;
    text_fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    text_fragment_state.targetCount = 1U;
    text_fragment_state.targets = &text_target;
    WGPURenderPipelineDescriptor text_pipeline_descriptor{};
    text_pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph pipeline");
    text_pipeline_descriptor.vertex = text_vertex_state;
    text_pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    text_pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    text_pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    text_pipeline_descriptor.multisample.count = 1U;
    text_pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    text_pipeline_descriptor.fragment = &text_fragment_state;
    engine.text_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &text_pipeline_descriptor);
    if (engine.text_pipeline == nullptr) {
        return false;
    }
    engine.text_uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.text_pipeline,
        0U);
    engine.text_atlas_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.text_pipeline,
        1U);
    if (engine.text_uniform_layout == nullptr ||
        engine.text_atlas_layout == nullptr) {
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
    std::array<std::byte, 32U> style_bytes{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.text_style_buffer,
        0U,
        style_bytes.data(),
        style_bytes.size());
    const std::array<WGPUBindGroupEntry, 2U> uniform_entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U,
            sizeof(gpu_uniforms), nullptr, nullptr},
        {nullptr, 1U, engine.text_style_buffer, 0U, 32U, nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text uniform bind group");
    uniform_group_descriptor.layout = engine.text_uniform_layout;
    uniform_group_descriptor.entryCount = uniform_entries.size();
    uniform_group_descriptor.entries = uniform_entries.data();
    engine.text_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.text_uniform_bind_group == nullptr) {
        return false;
    }

    WGPUTextureDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    atlas_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
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
    const std::array<WGPUBindGroupEntry, 3U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.glyph_atlas_texture_view},
        {nullptr, 2U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_group_descriptor{};
    atlas_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text atlas bind group");
    atlas_group_descriptor.layout = engine.text_atlas_layout;
    atlas_group_descriptor.entryCount = atlas_entries.size();
    atlas_group_descriptor.entries = atlas_entries.data();
    engine.text_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_group_descriptor);
    if (engine.text_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.glyph_atlas_generation;
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
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor group_descriptor{};
    group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text atlas bind group");
    group_descriptor.layout = engine.text_atlas_layout;
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

    wgpuBindGroupRelease(engine.text_atlas_bind_group);
    wgpuTextureViewRelease(engine.glyph_atlas_texture_view);
    wgpuTextureDestroy(engine.glyph_atlas_texture);
    wgpuTextureRelease(engine.glyph_atlas_texture);
    engine.text_atlas_bind_group = group;
    engine.glyph_atlas_texture_view = view;
    engine.glyph_atlas_texture = texture;
    engine.glyph_atlas_size = requested_size;
    ++engine.glyph_atlas_generation;
    ++engine.glyph_atlas_growth_count;
    return true;
}
