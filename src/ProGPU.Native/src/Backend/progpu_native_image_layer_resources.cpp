#include "progpu_native.h"
#include "progpu_native_geometry_spline.hpp"
#include "progpu_native_gpu_records.hpp"
#include "GroupBlendWgsl.generated.hpp"
#include "TextureWgsl.generated.hpp"

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

using progpu::native::gpu_group_blend_uniforms;
using progpu::native::gpu_mask_sampling_uniforms;
using progpu::native::gpu_uniforms;

WGPUBindGroup create_image_texture_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.image_texture_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_image_resources(progpu_native_engine& engine) {
    if (engine.image_pipeline != nullptr) {
        return true;
    }
    if (engine.image_shader != nullptr ||
        engine.image_uniform_layout != nullptr ||
        engine.image_texture_layout != nullptr ||
        engine.image_uniform_buffer != nullptr ||
        engine.image_uniform_bind_group != nullptr ||
        engine.image_nearest_sampler != nullptr ||
        engine.image_linear_sampler != nullptr ||
        engine.image_vertex_buffer != nullptr ||
        engine.image_index_buffer != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::texture_wgsl,
        progpu::native::generated::texture_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Texture.wgsl");
    engine.image_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.image_shader == nullptr) {
        return false;
    }

    WGPUBindGroupLayoutEntry uniform_layout_entry{};
    uniform_layout_entry.binding = 0U;
    uniform_layout_entry.visibility = WGPUShaderStage_Vertex |
        WGPUShaderStage_Fragment;
    uniform_layout_entry.buffer.type = WGPUBufferBindingType_Uniform;
    uniform_layout_entry.buffer.minBindingSize = sizeof(gpu_uniforms);
    WGPUBindGroupLayoutDescriptor uniform_layout_descriptor{};
    uniform_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image uniform layout");
    uniform_layout_descriptor.entryCount = 1U;
    uniform_layout_descriptor.entries = &uniform_layout_entry;
    engine.image_uniform_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &uniform_layout_descriptor);

    std::array<WGPUBindGroupLayoutEntry, 2U> texture_layout_entries{};
    texture_layout_entries[0].binding = 0U;
    texture_layout_entries[0].visibility = WGPUShaderStage_Fragment;
    texture_layout_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    texture_layout_entries[1].binding = 1U;
    texture_layout_entries[1].visibility = WGPUShaderStage_Fragment;
    texture_layout_entries[1].texture.sampleType =
        WGPUTextureSampleType_Float;
    texture_layout_entries[1].texture.viewDimension =
        WGPUTextureViewDimension_2D;
    texture_layout_entries[1].texture.multisampled = false;
    WGPUBindGroupLayoutDescriptor texture_layout_descriptor{};
    texture_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image texture layout");
    texture_layout_descriptor.entryCount = texture_layout_entries.size();
    texture_layout_descriptor.entries = texture_layout_entries.data();
    engine.image_texture_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &texture_layout_descriptor);
    if (engine.image_uniform_layout == nullptr ||
        engine.image_texture_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 2U> pipeline_layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native unmasked image layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = pipeline_layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = pipeline_layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.image_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.image_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.image_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    WGPUBufferDescriptor vertex_descriptor{};
    vertex_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image vertices");
    vertex_descriptor.usage = WGPUBufferUsage_Vertex |
        WGPUBufferUsage_CopyDst;
    vertex_descriptor.size = sizeof(engine.image_vertices);
    engine.image_vertex_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &vertex_descriptor);
    WGPUBufferDescriptor index_descriptor{};
    index_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image indices");
    index_descriptor.usage = WGPUBufferUsage_Index |
        WGPUBufferUsage_CopyDst;
    index_descriptor.size = 6U * sizeof(std::uint32_t);
    engine.image_index_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &index_descriptor);
    if (engine.image_uniform_buffer == nullptr ||
        engine.image_vertex_buffer == nullptr ||
        engine.image_index_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.image_uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image uniform bind group");
    uniform_group_descriptor.layout = engine.image_uniform_layout;
    uniform_group_descriptor.entryCount = 1U;
    uniform_group_descriptor.entries = &uniform_entry;
    engine.image_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.image_uniform_bind_group == nullptr) {
        return false;
    }

    const auto create_sampler = [&](WGPUFilterMode filter) {
        WGPUSamplerDescriptor descriptor{};
        descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
        descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
        descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
        descriptor.magFilter = filter;
        descriptor.minFilter = filter;
        descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
        descriptor.lodMinClamp = 0.0F;
        descriptor.lodMaxClamp = 0.0F;
        descriptor.maxAnisotropy = 1U;
        return wgpuDeviceCreateSampler(engine.device, &descriptor);
    };
    engine.image_nearest_sampler = create_sampler(WGPUFilterMode_Nearest);
    engine.image_linear_sampler = create_sampler(WGPUFilterMode_Linear);
    if (engine.image_nearest_sampler == nullptr ||
        engine.image_linear_sampler == nullptr) {
        return false;
    }

    constexpr std::array<std::uint32_t, 6U> indices{
        0U, 1U, 2U, 0U, 2U, 3U};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.image_index_buffer,
        0U,
        indices.data(),
        sizeof(indices));
    return true;
}

bool create_layer_resources(progpu_native_engine& engine) {
    if (engine.layer_composite_pipeline != nullptr) {
        return true;
    }
    if (!create_image_resources(engine) ||
        engine.layer_uniform_buffer != nullptr ||
        engine.layer_uniform_bind_group != nullptr ||
        engine.layer_vertex_buffer != nullptr ||
        engine.layer_index_buffer != nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 2U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label =
        progpu::native::webgpu::string_view(
            "ProGPU native group composite layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_One;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint =
        progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native premultiplied group composite pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    engine.layer_composite_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.layer_composite_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.layer_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    WGPUBufferDescriptor vertex_descriptor{};
    vertex_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite vertices");
    vertex_descriptor.usage = WGPUBufferUsage_Vertex |
        WGPUBufferUsage_CopyDst;
    vertex_descriptor.size = sizeof(engine.layer_vertices);
    engine.layer_vertex_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &vertex_descriptor);
    WGPUBufferDescriptor index_descriptor{};
    index_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite indices");
    index_descriptor.usage = WGPUBufferUsage_Index |
        WGPUBufferUsage_CopyDst;
    index_descriptor.size = 6U * sizeof(std::uint32_t);
    engine.layer_index_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &index_descriptor);
    if (engine.layer_uniform_buffer == nullptr ||
        engine.layer_vertex_buffer == nullptr ||
        engine.layer_index_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.layer_uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite uniform bind group");
    uniform_group_descriptor.layout = engine.image_uniform_layout;
    uniform_group_descriptor.entryCount = 1U;
    uniform_group_descriptor.entries = &uniform_entry;
    engine.layer_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.layer_uniform_bind_group == nullptr) {
        return false;
    }
    constexpr std::array<std::uint32_t, 6U> indices{
        0U, 1U, 2U, 0U, 2U, 3U};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.layer_index_buffer,
        0U,
        indices.data(),
        sizeof(indices));
    return true;
}

WGPUBindGroup create_layer_mask_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label,
    WGPUBuffer uniform_buffer) {
    uniform_buffer = uniform_buffer != nullptr
        ? uniform_buffer
        : engine.layer_mask_uniform_buffer;
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, uniform_buffer, 0U,
            sizeof(gpu_mask_sampling_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.layer_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_layer_mask_resources(progpu_native_engine& engine) {
    if (engine.layer_mask_pipeline != nullptr) {
        return true;
    }
    if (!create_layer_resources(engine) ||
        engine.layer_mask_layout != nullptr ||
        engine.layer_mask_uniform_buffer != nullptr ||
        engine.layer_mask_dummy_texture != nullptr ||
        engine.layer_mask_dummy_view != nullptr ||
        engine.layer_analytic_mask_bind_group != nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> mask_entries{};
    mask_entries[0].binding = 0U;
    mask_entries[0].visibility = WGPUShaderStage_Fragment;
    mask_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    mask_entries[1].binding = 1U;
    mask_entries[1].visibility = WGPUShaderStage_Fragment;
    mask_entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    mask_entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    mask_entries[1].texture.multisampled = false;
    mask_entries[2].binding = 2U;
    mask_entries[2].visibility = WGPUShaderStage_Fragment;
    mask_entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    mask_entries[2].buffer.minBindingSize =
        sizeof(gpu_mask_sampling_uniforms);
    WGPUBindGroupLayoutDescriptor mask_layout_descriptor{};
    mask_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common group mask layout");
    mask_layout_descriptor.entryCount = mask_entries.size();
    mask_layout_descriptor.entries = mask_entries.data();
    engine.layer_mask_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &mask_layout_descriptor);
    if (engine.layer_mask_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.layer_mask_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native masked group composite layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_One;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native masked premultiplied group composite pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.layer_mask_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.layer_mask_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common group mask uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_mask_sampling_uniforms);
    engine.layer_mask_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (engine.layer_mask_uniform_buffer == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native analytic group mask sentinel");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {1U, 1U, 1U};
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.layer_mask_dummy_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.layer_mask_dummy_texture == nullptr) {
        return false;
    }
    engine.layer_mask_dummy_view = wgpuTextureCreateView(
        engine.layer_mask_dummy_texture,
        nullptr);
    if (engine.layer_mask_dummy_view == nullptr) {
        return false;
    }
    engine.layer_analytic_mask_bind_group = create_layer_mask_bind_group(
        engine,
        engine.image_linear_sampler,
        engine.layer_mask_dummy_view,
        "ProGPU native analytic group mask bind group");
    if (engine.layer_analytic_mask_bind_group == nullptr) {
        return false;
    }
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool is_advanced_group_blend(std::uint32_t blend_mode) noexcept {
    return (blend_mode >= PROGPU_NATIVE_BLEND_MULTIPLY &&
            blend_mode <= PROGPU_NATIVE_BLEND_EXCLUSION) ||
        (blend_mode >= PROGPU_NATIVE_BLEND_OVERLAY &&
            blend_mode <= PROGPU_NATIVE_BLEND_LUMINOSITY);
}

bool configure_fixed_group_blend(
    std::uint32_t blend_mode,
    WGPUBlendState& blend) noexcept {
    blend = {};
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.operation = WGPUBlendOperation_Add;
    switch (blend_mode) {
        case PROGPU_NATIVE_BLEND_SRC_OVER:
            blend.color.srcFactor = WGPUBlendFactor_One;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_One;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_SRC:
            blend.color.srcFactor = WGPUBlendFactor_One;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_One;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_DST:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_One;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_One;
            return true;
        case PROGPU_NATIVE_BLEND_SRC_IN:
            blend.color.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_DST_IN:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_SrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_SrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_SRC_OUT:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_DST_OUT:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_SRC_ATOP:
            blend.color.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_DST_ATOP:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_SrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_SrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_XOR:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_DST_OVER:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_One;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_One;
            return true;
        case PROGPU_NATIVE_BLEND_PLUS:
            blend.color.srcFactor = WGPUBlendFactor_One;
            blend.color.dstFactor = WGPUBlendFactor_One;
            blend.alpha.srcFactor = WGPUBlendFactor_One;
            blend.alpha.dstFactor = WGPUBlendFactor_One;
            return true;
        case PROGPU_NATIVE_BLEND_CLEAR:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_MODULATE:
            blend.color.srcFactor = WGPUBlendFactor_Dst;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        default:
            return false;
    }
}

WGPURenderPipeline get_or_create_fixed_group_blend_pipeline(
    progpu_native_engine& engine,
    std::uint32_t blend_mode,
    bool masked,
    bool& cache_hit) {
    if (blend_mode == PROGPU_NATIVE_BLEND_SRC_OVER) {
        cache_hit = true;
        return masked
            ? engine.layer_mask_pipeline
            : engine.layer_composite_pipeline;
    }
    if (blend_mode > PROGPU_NATIVE_BLEND_MODULATE ||
        is_advanced_group_blend(blend_mode)) {
        return nullptr;
    }
    auto& pipelines = masked
        ? engine.layer_mask_blend_pipelines
        : engine.layer_blend_pipelines;
    if (pipelines[blend_mode] != nullptr) {
        cache_hit = true;
        return pipelines[blend_mode];
    }
    cache_hit = false;
    if (!create_layer_resources(engine) ||
        (masked && !create_layer_mask_resources(engine))) {
        return nullptr;
    }

    std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.layer_mask_layout
    }};
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native fixed group-blend layout");
    layout_descriptor.bindGroupLayoutCount = masked ? 3U : 2U;
    layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &layout_descriptor);
    if (pipeline_layout == nullptr) {
        return nullptr;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;

    WGPUBlendState blend{};
    if (!configure_fixed_group_blend(blend_mode, blend)) {
        wgpuPipelineLayoutRelease(pipeline_layout);
        return nullptr;
    }
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view(
        masked ? "fs_main" : "fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native fixed group-blend pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    pipelines[blend_mode] = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return pipelines[blend_mode];
}

bool create_advanced_group_blend_resources(progpu_native_engine& engine) {
    if (engine.group_blend_pipeline != nullptr &&
        engine.group_blend_layout != nullptr &&
        engine.group_blend_uniform_buffer != nullptr) {
        return true;
    }
    if (engine.group_blend_shader != nullptr ||
        engine.group_blend_pipeline != nullptr ||
        engine.group_blend_layout != nullptr ||
        engine.group_blend_uniform_buffer != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::group_blend_wgsl,
        progpu::native::generated::group_blend_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared GroupBlend.wgsl");
    engine.group_blend_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.group_blend_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 2U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].texture.sampleType = WGPUTextureSampleType_Float;
    entries[0].texture.viewDimension = WGPUTextureViewDimension_2D;
    entries[0].texture.multisampled = false;
    entries[1].binding = 1U;
    entries[1].visibility = WGPUShaderStage_Fragment;
    entries[1].buffer.type = WGPUBufferBindingType_Uniform;
    entries[1].buffer.minBindingSize = sizeof(gpu_group_blend_uniforms);
    WGPUBindGroupLayoutDescriptor bind_layout_descriptor{};
    bind_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend bind layout");
    bind_layout_descriptor.entryCount = entries.size();
    bind_layout_descriptor.entries = entries.data();
    engine.group_blend_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &bind_layout_descriptor);
    if (engine.group_blend_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.group_blend_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.group_blend_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = nullptr;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.group_blend_shader;
    fragment.entryPoint =
        progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.group_blend_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.group_blend_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_group_blend_uniforms);
    engine.group_blend_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    return engine.group_blend_uniform_buffer != nullptr;
}

bool ensure_advanced_group_blend_source(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (!create_advanced_group_blend_resources(engine)) {
        return false;
    }
    if (engine.group_blend_source_texture != nullptr &&
        engine.group_blend_source_width == width &&
        engine.group_blend_source_height == height) {
        return true;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend source");
    texture_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {width, height, 1U};
    texture_descriptor.format = engine.target_format;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
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
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 1U, engine.group_blend_uniform_buffer, 0U,
            sizeof(gpu_group_blend_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor bind_group_descriptor{};
    bind_group_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend bind group");
    bind_group_descriptor.layout = engine.group_blend_layout;
    bind_group_descriptor.entryCount = entries.size();
    bind_group_descriptor.entries = entries.data();
    WGPUBindGroup bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_group_descriptor);
    if (bind_group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    if (engine.group_blend_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.group_blend_bind_group);
    }
    if (engine.group_blend_source_view != nullptr) {
        wgpuTextureViewRelease(engine.group_blend_source_view);
    }
    if (engine.group_blend_source_texture != nullptr) {
        wgpuTextureDestroy(engine.group_blend_source_texture);
        wgpuTextureRelease(engine.group_blend_source_texture);
    }
    engine.group_blend_source_texture = texture;
    engine.group_blend_source_view = view;
    engine.group_blend_bind_group = bind_group;
    engine.group_blend_source_width = width;
    engine.group_blend_source_height = height;
    engine.group_blend_source_cache_valid = false;
    ++engine.group_blend_source_texture_generation;
    ++engine.group_blend_source_allocation_count;
    return true;
}
