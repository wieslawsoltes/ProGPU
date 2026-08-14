#include "progpu_native_frame_execution_common.hpp"

bool create_analytic_bind_group_layouts(progpu_native_engine& engine) {
    if (engine.analytic_uniform_layout != nullptr &&
        engine.analytic_atlas_layout != nullptr) {
        return true;
    }
    if (engine.analytic_uniform_layout != nullptr ||
        engine.analytic_atlas_layout != nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> uniform_entries{};
    uniform_entries[0].binding = 0U;
    uniform_entries[0].visibility =
        WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
    uniform_entries[0].buffer.type = WGPUBufferBindingType_Uniform;
    uniform_entries[0].buffer.minBindingSize =
        sizeof(progpu::native::gpu_uniforms);
    uniform_entries[1].binding = 1U;
    uniform_entries[1].visibility =
        WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
    uniform_entries[1].buffer.type =
        WGPUBufferBindingType_ReadOnlyStorage;
    uniform_entries[1].buffer.minBindingSize =
        progpu::native::gpu_brush_size;
    uniform_entries[2].binding = 2U;
    uniform_entries[2].visibility = WGPUShaderStage_Fragment;
    uniform_entries[2].buffer.type =
        WGPUBufferBindingType_ReadOnlyStorage;
    uniform_entries[2].buffer.minBindingSize =
        sizeof(progpu_native_scene_gradient_stop);
    WGPUBindGroupLayoutDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common vector uniform layout");
    uniform_descriptor.entryCount = uniform_entries.size();
    uniform_descriptor.entries = uniform_entries.data();
    engine.analytic_uniform_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &uniform_descriptor);
    if (engine.analytic_uniform_layout == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 2U> atlas_entries{};
    atlas_entries[0].binding = 0U;
    atlas_entries[0].visibility = WGPUShaderStage_Fragment;
    atlas_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    atlas_entries[1].binding = 1U;
    atlas_entries[1].visibility = WGPUShaderStage_Fragment;
    atlas_entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    atlas_entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    atlas_entries[1].texture.multisampled = false;
    WGPUBindGroupLayoutDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common vector atlas layout");
    atlas_descriptor.entryCount = atlas_entries.size();
    atlas_descriptor.entries = atlas_entries.data();
    engine.analytic_atlas_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &atlas_descriptor);
    if (engine.analytic_atlas_layout == nullptr) {
        wgpuBindGroupLayoutRelease(engine.analytic_uniform_layout);
        engine.analytic_uniform_layout = nullptr;
        return false;
    }
    return true;
}

bool create_analytic_masked_pipeline(progpu_native_engine& engine) {
    if (engine.analytic_masked_pipeline != nullptr) {
        return true;
    }
    if ((engine.analytic_pipeline == nullptr &&
            !create_analytic_pipeline(engine)) ||
        !create_analytic_bind_group_layouts(engine) ||
        !create_layer_mask_resources(engine)) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.analytic_uniform_layout,
        engine.analytic_atlas_layout,
        engine.layer_mask_layout
    }};
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native masked vector pipeline layout");
    layout_descriptor.bindGroupLayoutCount = layouts.size();
    layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &layout_descriptor);
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
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride =
        sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_buffer_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;

    WGPUColorTargetState color_target{};
    color_target.format = engine.target_format;
    color_target.blend = &blend;
    color_target.writeMask = WGPUColorWriteMask_All;

    WGPUFragmentState fragment_state{};
    fragment_state.module = engine.shader;
    fragment_state.entryPoint =
        progpu::native::webgpu::string_view("fs_main");
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native per-draw masked vector pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment_state;
    engine.analytic_masked_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return engine.analytic_masked_pipeline != nullptr;
}
