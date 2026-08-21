#include "progpu_native_frame_execution_common.hpp"

bool create_analytic_brush_mask_pipeline(progpu_native_engine& engine) {
    if (engine.analytic_brush_mask_pipeline != nullptr) {
        return true;
    }
    if ((engine.analytic_pipeline == nullptr &&
            !create_analytic_pipeline(engine)) ||
        !create_analytic_bind_group_layouts(engine)) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 2U> layouts{{
        engine.analytic_uniform_layout,
        engine.analytic_atlas_layout
    }};
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native brush-mask pipeline layout");
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

    WGPUColorTargetState color_target{};
    color_target.format = WGPUTextureFormat_R8Unorm;
    color_target.blend = nullptr;
    color_target.writeMask = WGPUColorWriteMask_All;

    WGPUFragmentState fragment_state{};
    fragment_state.module = engine.shader;
    fragment_state.entryPoint =
        progpu::native::webgpu::string_view("fs_mask_unmasked");
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained brush opacity-mask pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment_state;
    engine.analytic_brush_mask_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return engine.analytic_brush_mask_pipeline != nullptr;
}
