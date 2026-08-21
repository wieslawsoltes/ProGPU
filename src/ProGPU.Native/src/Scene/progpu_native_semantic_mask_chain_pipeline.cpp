#include "progpu_native_frame_execution_common.hpp"

namespace {

bool create_chain_layouts(progpu_native_engine& engine) {
    if (engine.semantic_mask_chain_layout != nullptr) {
        return true;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    entries[1].binding = 1U;
    entries[1].visibility = WGPUShaderStage_Fragment;
    entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    entries[1].texture.multisampled = false;
    entries[2].binding = 2U;
    entries[2].visibility = WGPUShaderStage_Fragment;
    entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    entries[2].buffer.minBindingSize =
        sizeof(progpu::native::gpu_mask_sampling_uniforms);
    entries[3].binding = 3U;
    entries[3].visibility = WGPUShaderStage_Fragment;
    entries[3].buffer.type = WGPUBufferBindingType_Uniform;
    entries[3].buffer.minBindingSize =
        sizeof(progpu::native::gpu_mask_chain_uniforms);
    WGPUBindGroupLayoutDescriptor chain_descriptor{};
    chain_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native bounded analytic mask-chain layout");
    chain_descriptor.entryCount = entries.size();
    chain_descriptor.entries = entries.data();
    engine.semantic_mask_chain_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &chain_descriptor);
    return engine.semantic_mask_chain_layout != nullptr;
}

WGPURenderPipeline create_chain_pipeline(
    progpu_native_engine& engine,
    WGPUShaderModule shader,
    const WGPUBindGroupLayout* layouts,
    std::size_t layout_count,
    const WGPUVertexAttribute* attributes,
    std::size_t attribute_count,
    std::uint64_t stride,
    WGPUVertexStepMode step_mode,
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

    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = stride;
    vertex_layout.stepMode = step_mode;
    vertex_layout.attributeCount = attribute_count;
    vertex_layout.attributes = attributes;
    WGPUVertexState vertex_state{};
    vertex_state.module = shader;
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
    fragment.module = shader;
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
    WGPURenderPipeline pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return pipeline;
}

bool create_vector_chain_pipeline(progpu_native_engine& engine) {
    if (engine.analytic_mask_chain_pipeline != nullptr) {
        return true;
    }
    if (!create_analytic_pipeline(engine) ||
        !create_analytic_bind_group_layouts(engine) ||
        !create_layer_mask_resources(engine) ||
        !create_chain_layouts(engine)) {
        return false;
    }
    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.analytic_uniform_layout,
        engine.analytic_atlas_layout,
        engine.semantic_mask_chain_layout
    }};
    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 52U, 7U)
    }};
    engine.analytic_mask_chain_pipeline = create_chain_pipeline(
        engine,
        engine.shader,
        layouts.data(),
        layouts.size(),
        attributes.data(),
        attributes.size(),
        sizeof(progpu::native::vector_vertex),
        WGPUVertexStepMode_Vertex,
        "fs_main_chain",
        "ProGPU native bounded analytic mask-chain vector pipeline");
    return engine.analytic_mask_chain_pipeline != nullptr;
}

bool create_text_chain_pipeline(progpu_native_engine& engine) {
    if (engine.text_mask_chain_pipeline != nullptr) {
        return true;
    }
    if (!create_text_pipeline(engine) || !create_layer_mask_resources(engine) ||
        !create_chain_layouts(engine)) {
        return false;
    }
    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.text_uniform_layout,
        engine.text_atlas_layout,
        engine.semantic_mask_chain_layout
    }};
    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 16U, 2U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 24U, 3U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 40U, 4U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 56U, 5U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 72U, 6U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 88U, 7U)
    }};
    engine.text_mask_chain_pipeline = create_chain_pipeline(
        engine,
        engine.text_shader,
        layouts.data(),
        layouts.size(),
        attributes.data(),
        attributes.size(),
        sizeof(progpu::native::gpu_glyph_instance),
        WGPUVertexStepMode_Instance,
        "fs_main_chain",
        "ProGPU native bounded analytic mask-chain text pipeline");
    return engine.text_mask_chain_pipeline != nullptr;
}

bool create_image_chain_pipelines(progpu_native_engine& engine) {
    if (engine.image_mask_chain_pipeline != nullptr &&
        engine.image_mask_chain_color_matrix_pipeline != nullptr) {
        return true;
    }
    if (engine.image_mask_chain_pipeline != nullptr ||
        engine.image_mask_chain_color_matrix_pipeline != nullptr) {
        return false;
    }
    if (!progpu::native::execution::create_image_mask_resources(engine) ||
        !create_chain_layouts(engine)) {
        return false;
    }
    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(WGPUVertexFormat_Float32, 48U, 6U)
    }};
    const std::array<WGPUBindGroupLayout, 3U> plain_layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.semantic_mask_chain_layout
    }};
    WGPURenderPipeline plain_pipeline = create_chain_pipeline(
        engine,
        engine.image_shader,
        plain_layouts.data(),
        plain_layouts.size(),
        attributes.data(),
        attributes.size(),
        sizeof(progpu::native::vector_vertex),
        WGPUVertexStepMode_Vertex,
        "fs_main_chain",
        "ProGPU native bounded analytic mask-chain image pipeline");
    const std::array<WGPUBindGroupLayout, 4U> matrix_layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.semantic_mask_chain_layout,
        engine.image_mask_layout
    }};
    WGPURenderPipeline matrix_pipeline = plain_pipeline == nullptr
        ? nullptr
        : create_chain_pipeline(
            engine,
            engine.image_shader,
            matrix_layouts.data(),
            matrix_layouts.size(),
            attributes.data(),
            attributes.size(),
            sizeof(progpu::native::vector_vertex),
            WGPUVertexStepMode_Vertex,
            "fs_main_color_matrix_chain",
            "ProGPU native bounded analytic mask-chain color-matrix image pipeline");
    if (plain_pipeline == nullptr || matrix_pipeline == nullptr) {
        if (matrix_pipeline != nullptr) {
            wgpuRenderPipelineRelease(matrix_pipeline);
        }
        if (plain_pipeline != nullptr) {
            wgpuRenderPipelineRelease(plain_pipeline);
        }
        return false;
    }
    engine.image_mask_chain_pipeline = plain_pipeline;
    engine.image_mask_chain_color_matrix_pipeline = matrix_pipeline;
    return true;
}

} // namespace

WGPUBindGroup create_semantic_mask_chain_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    WGPUBuffer primary_uniform_buffer,
    WGPUBuffer chain_uniform_buffer) {
    if (!create_chain_layouts(engine) || sampler == nullptr ||
        view == nullptr || primary_uniform_buffer == nullptr ||
        chain_uniform_buffer == nullptr) {
        return nullptr;
    }
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, primary_uniform_buffer, 0U,
            sizeof(progpu::native::gpu_mask_sampling_uniforms),
            nullptr, nullptr},
        {nullptr, 3U, chain_uniform_buffer, 0U,
            sizeof(progpu::native::gpu_mask_chain_uniforms),
            nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained analytic mask-chain binding");
    descriptor.layout = engine.semantic_mask_chain_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_semantic_vector_mask_chain_pipeline(progpu_native_engine& engine) {
    return create_vector_chain_pipeline(engine);
}

bool create_semantic_text_mask_chain_pipeline(progpu_native_engine& engine) {
    return create_text_chain_pipeline(engine);
}

bool create_semantic_image_mask_chain_pipelines(progpu_native_engine& engine) {
    return create_image_chain_pipelines(engine);
}
