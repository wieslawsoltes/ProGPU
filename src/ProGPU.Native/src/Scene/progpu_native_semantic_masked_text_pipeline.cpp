#include "progpu_native_frame_execution_common.hpp"

namespace {

bool create_text_bind_group_layouts(progpu_native_engine& engine) {
    if (engine.text_uniform_layout != nullptr &&
        engine.text_atlas_layout != nullptr) {
        return true;
    }
    if (engine.text_uniform_layout != nullptr ||
        engine.text_atlas_layout != nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 2U> uniform_entries{};
    uniform_entries[0].binding = 0U;
    uniform_entries[0].visibility =
        WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
    uniform_entries[0].buffer.type = WGPUBufferBindingType_Uniform;
    uniform_entries[0].buffer.minBindingSize =
        sizeof(progpu::native::gpu_uniforms);
    uniform_entries[1].binding = 1U;
    uniform_entries[1].visibility = WGPUShaderStage_Vertex;
    uniform_entries[1].buffer.type =
        WGPUBufferBindingType_ReadOnlyStorage;
    uniform_entries[1].buffer.minBindingSize =
        sizeof(progpu_native_scene_text_style);
    WGPUBindGroupLayoutDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common text uniform layout");
    uniform_descriptor.entryCount = uniform_entries.size();
    uniform_descriptor.entries = uniform_entries.data();
    engine.text_uniform_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &uniform_descriptor);
    if (engine.text_uniform_layout == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> atlas_entries{};
    atlas_entries[0].binding = 0U;
    atlas_entries[0].visibility = WGPUShaderStage_Fragment;
    atlas_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    atlas_entries[1].binding = 1U;
    atlas_entries[1].visibility = WGPUShaderStage_Fragment;
    atlas_entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    atlas_entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    atlas_entries[1].texture.multisampled = false;
    atlas_entries[2].binding = 2U;
    atlas_entries[2].visibility = WGPUShaderStage_Fragment;
    atlas_entries[2].texture.sampleType = WGPUTextureSampleType_Float;
    atlas_entries[2].texture.viewDimension = WGPUTextureViewDimension_2D;
    atlas_entries[2].texture.multisampled = false;
    WGPUBindGroupLayoutDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common text atlas layout");
    atlas_descriptor.entryCount = atlas_entries.size();
    atlas_descriptor.entries = atlas_entries.data();
    engine.text_atlas_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &atlas_descriptor);
    if (engine.text_atlas_layout == nullptr) {
        wgpuBindGroupLayoutRelease(engine.text_uniform_layout);
        engine.text_uniform_layout = nullptr;
        return false;
    }
    return true;
}

WGPURenderPipeline create_text_render_pipeline(
    progpu_native_engine& engine,
    const WGPUBindGroupLayout* layouts,
    std::size_t layout_count,
    const char* fragment_entry,
    const char* label) {
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(label);
    layout_descriptor.bindGroupLayoutCount = layout_count;
    layout_descriptor.bindGroupLayouts = layouts;
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
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::gpu_glyph_instance);
    vertex_layout.stepMode = WGPUVertexStepMode_Instance;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.text_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
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
    fragment.module = engine.text_shader;
    fragment.entryPoint =
        progpu::native::webgpu::string_view(fragment_entry);
    fragment.targetCount = 1U;
    fragment.targets = &target;

    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    WGPURenderPipeline result = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return result;
}

} // namespace

bool create_text_pipeline(progpu_native_engine& engine) {
    if (engine.text_pipeline != nullptr) {
        return true;
    }
    if (engine.text_shader == nullptr ||
        !create_text_bind_group_layouts(engine)) {
        return false;
    }
    const std::array<WGPUBindGroupLayout, 2U> layouts{{
        engine.text_uniform_layout,
        engine.text_atlas_layout
    }};
    engine.text_pipeline = create_text_render_pipeline(
        engine,
        layouts.data(),
        layouts.size(),
        "fs_main_unmasked",
        "ProGPU native positioned glyph pipeline");
    return engine.text_pipeline != nullptr;
}

bool create_text_masked_pipeline(progpu_native_engine& engine) {
    if (engine.text_masked_pipeline != nullptr) {
        return true;
    }
    if (!create_text_pipeline(engine) ||
        !create_layer_mask_resources(engine)) {
        return false;
    }
    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.text_uniform_layout,
        engine.text_atlas_layout,
        engine.layer_mask_layout
    }};
    engine.text_masked_pipeline = create_text_render_pipeline(
        engine,
        layouts.data(),
        layouts.size(),
        "fs_main",
        "ProGPU native per-draw masked glyph pipeline");
    return engine.text_masked_pipeline != nullptr;
}
