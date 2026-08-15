#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "ImageEffectWgsl.generated.hpp"
#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_semantic_image_resources.hpp"

#include <array>
#include <cstddef>

namespace webgpu = progpu::native::webgpu;
namespace generated = progpu::native::generated;
using progpu::native::gpu_mask_sampling_uniforms;
using progpu::native::vector_vertex;

namespace {

constexpr std::uint64_t effect_uniform_size =
    offsetof(progpu_native_scene_image_effect, struct_size);
static_assert(effect_uniform_size == 288U);
static_assert(sizeof(progpu_native_scene_image_effect) == 304U);

WGPUBindGroupLayout create_effect_uniform_layout(
    progpu_native_engine& engine) noexcept {
    WGPUBindGroupLayoutEntry entry{};
    entry.binding = 0U;
    entry.visibility = WGPUShaderStage_Fragment;
    entry.buffer.type = WGPUBufferBindingType_Uniform;
    entry.buffer.minBindingSize = effect_uniform_size;
    WGPUBindGroupLayoutDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect uniform layout");
    descriptor.entryCount = 1U;
    descriptor.entries = &entry;
    return wgpuDeviceCreateBindGroupLayout(engine.device, &descriptor);
}

WGPUBindGroupLayout create_effect_texture_layout(
    progpu_native_engine& engine) noexcept {
    std::array<WGPUBindGroupLayoutEntry, 3U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    for (std::uint32_t index = 1U; index < entries.size(); ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Fragment;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    WGPUBindGroupLayoutDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect texture layout");
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroupLayout(engine.device, &descriptor);
}

WGPURenderPipeline create_effect_pipeline(
    progpu_native_engine& engine,
    WGPUPipelineLayout layout,
    const char* entry_point,
    const char* label) noexcept {
    const std::array<WGPUVertexAttribute, 3U> attributes{{
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 0U, 0U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 8U, 1U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 24U, 2U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex{};
    vertex.module = engine.image_effect_shader;
    vertex.entryPoint = webgpu::string_view("vs_main");
    vertex.bufferCount = 1U;
    vertex.buffers = &vertex_layout;
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
    fragment.module = engine.image_effect_shader;
    fragment.entryPoint = webgpu::string_view(entry_point);
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.layout = layout;
    descriptor.vertex = vertex;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    return wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
}

} // namespace

bool create_semantic_image_effect_pipelines(
    progpu_native_engine& engine) {
    if (engine.image_effect_pipeline != nullptr) {
        return true;
    }
    if (!create_image_resources(engine) ||
        !progpu::native::execution::create_image_mask_resources(engine) ||
        engine.image_effect_shader != nullptr ||
        engine.image_effect_uniform_layout != nullptr ||
        engine.image_effect_texture_layout != nullptr) {
        return false;
    }

    webgpu::wgsl_source wgsl(
        generated::image_effect_wgsl,
        generated::image_effect_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = webgpu::string_view(
        "ProGPU shared ImageEffect.wgsl");
    engine.image_effect_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    engine.image_effect_uniform_layout =
        create_effect_uniform_layout(engine);
    engine.image_effect_texture_layout =
        create_effect_texture_layout(engine);
    if (engine.image_effect_shader == nullptr ||
        engine.image_effect_uniform_layout == nullptr ||
        engine.image_effect_texture_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 4U> layouts{{
        engine.image_uniform_layout,
        engine.image_effect_uniform_layout,
        engine.image_effect_texture_layout,
        engine.image_mask_layout
    }};
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect layout");
    layout_descriptor.bindGroupLayoutCount = layouts.size();
    layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &layout_descriptor);
    if (layout == nullptr) {
        return false;
    }
    engine.image_effect_pipeline = create_effect_pipeline(
        engine,
        layout,
        "fs_main",
        "ProGPU semantic image-effect pipeline");
    wgpuPipelineLayoutRelease(layout);
    return engine.image_effect_pipeline != nullptr;
}

namespace progpu::native::semantic {

bool create_semantic_image_effect_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    WGPUTextureView chroma_view,
    WGPUTextureView mask_view,
    std::uint32_t mask_width,
    std::uint32_t mask_height,
    WGPUSampler sampler,
    const progpu_native_scene_image_effect& effect,
    WGPUBuffer& uniform_buffer,
    WGPUBuffer& mask_uniform_buffer,
    WGPUBindGroup& uniform_bind_group,
    WGPUBindGroup& texture_bind_group,
    WGPUBindGroup& dummy_mask_bind_group) noexcept {
    uniform_buffer = nullptr;
    mask_uniform_buffer = nullptr;
    uniform_bind_group = nullptr;
    texture_bind_group = nullptr;
    dummy_mask_bind_group = nullptr;
    if (image_view == nullptr || sampler == nullptr ||
        !create_semantic_image_effect_pipelines(engine)) {
        return false;
    }
    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = effect_uniform_size;
    uniform_buffer = wgpuDeviceCreateBuffer(engine.device, &buffer_descriptor);
    if (uniform_buffer == nullptr) {
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        uniform_buffer,
        0U,
        &effect,
        effect_uniform_size);

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = uniform_buffer;
    uniform_entry.size = effect_uniform_size;
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect uniform bind group");
    descriptor.layout = engine.image_effect_uniform_layout;
    descriptor.entryCount = 1U;
    descriptor.entries = &uniform_entry;
    uniform_bind_group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);

    const std::array<WGPUBindGroupEntry, 3U> texture_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, image_view},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr,
            chroma_view != nullptr ? chroma_view : image_view}
    }};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect texture bind group");
    descriptor.layout = engine.image_effect_texture_layout;
    descriptor.entryCount = texture_entries.size();
    descriptor.entries = texture_entries.data();
    texture_bind_group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);

    WGPUBuffer selected_mask_uniform = engine.image_mask_uniform_buffer;
    if (mask_view != nullptr) {
        if (mask_width == 0U || mask_height == 0U) {
            if (texture_bind_group != nullptr)
                wgpuBindGroupRelease(texture_bind_group);
            if (uniform_bind_group != nullptr)
                wgpuBindGroupRelease(uniform_bind_group);
            wgpuBufferDestroy(uniform_buffer);
            wgpuBufferRelease(uniform_buffer);
            uniform_buffer = nullptr;
            uniform_bind_group = nullptr;
            texture_bind_group = nullptr;
            return false;
        }
        gpu_mask_sampling_uniforms sampling{};
        sampling.coordinate1[0] = 1.0F / static_cast<float>(mask_width);
        sampling.coordinate1[1] = 1.0F / static_cast<float>(mask_height);
        sampling.options[0] = 1.0F;
        WGPUBufferDescriptor mask_buffer_descriptor{};
        mask_buffer_descriptor.label = webgpu::string_view(
            "ProGPU semantic image-effect mask uniforms");
        mask_buffer_descriptor.usage = WGPUBufferUsage_Uniform |
            WGPUBufferUsage_CopyDst;
        mask_buffer_descriptor.size = sizeof(sampling);
        mask_uniform_buffer = wgpuDeviceCreateBuffer(
            engine.device,
            &mask_buffer_descriptor);
        if (mask_uniform_buffer == nullptr) {
            if (texture_bind_group != nullptr)
                wgpuBindGroupRelease(texture_bind_group);
            if (uniform_bind_group != nullptr)
                wgpuBindGroupRelease(uniform_bind_group);
            wgpuBufferDestroy(uniform_buffer);
            wgpuBufferRelease(uniform_buffer);
            uniform_buffer = nullptr;
            uniform_bind_group = nullptr;
            texture_bind_group = nullptr;
            return false;
        }
        wgpuQueueWriteBuffer(
            engine.queue,
            mask_uniform_buffer,
            0U,
            &sampling,
            sizeof(sampling));
        selected_mask_uniform = mask_uniform_buffer;
    }
    const std::array<WGPUBindGroupEntry, 3U> mask_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.image_linear_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr,
            mask_view != nullptr ? mask_view : image_view},
        {nullptr, 2U, selected_mask_uniform, 0U,
            sizeof(gpu_mask_sampling_uniforms), nullptr, nullptr}
    }};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image-effect identity-mask bind group");
    descriptor.layout = engine.image_mask_layout;
    descriptor.entryCount = mask_entries.size();
    descriptor.entries = mask_entries.data();
    dummy_mask_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &descriptor);
    if (uniform_bind_group == nullptr || texture_bind_group == nullptr ||
        dummy_mask_bind_group == nullptr) {
        if (dummy_mask_bind_group != nullptr)
            wgpuBindGroupRelease(dummy_mask_bind_group);
        if (texture_bind_group != nullptr)
            wgpuBindGroupRelease(texture_bind_group);
        if (uniform_bind_group != nullptr)
            wgpuBindGroupRelease(uniform_bind_group);
        if (mask_uniform_buffer != nullptr) {
            wgpuBufferDestroy(mask_uniform_buffer);
            wgpuBufferRelease(mask_uniform_buffer);
        }
        wgpuBufferDestroy(uniform_buffer);
        wgpuBufferRelease(uniform_buffer);
        uniform_buffer = nullptr;
        mask_uniform_buffer = nullptr;
        uniform_bind_group = nullptr;
        texture_bind_group = nullptr;
        dummy_mask_bind_group = nullptr;
        return false;
    }
    return true;
}

} // namespace progpu::native::semantic
