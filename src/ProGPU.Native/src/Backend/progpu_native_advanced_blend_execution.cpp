#include "progpu_native.h"
#include "AdvancedBlendWgsl.generated.hpp"

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
#include "progpu_native_replay_execution.hpp"

#include <algorithm>
#include <array>
#include <cstdint>

namespace progpu::native::execution {

namespace {

constexpr std::uint64_t advanced_uniform_alignment = 256U;

bool create_advanced_blend_pipeline(progpu_native_engine& engine) {
    if (engine.semantic_advanced_blend_pipeline != nullptr &&
        engine.semantic_advanced_blend_layout != nullptr) {
        return true;
    }
    if (engine.semantic_advanced_blend_shader != nullptr ||
        engine.semantic_advanced_blend_pipeline != nullptr ||
        engine.semantic_advanced_blend_layout != nullptr) {
        return false;
    }

    webgpu::wgsl_source wgsl(
        generated::advanced_blend_wgsl,
        generated::advanced_blend_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = webgpu::string_view(
        "ProGPU shared AdvancedBlend.wgsl");
    engine.semantic_advanced_blend_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.semantic_advanced_blend_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> entries{};
    for (std::uint32_t index = 0U; index < 2U; ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Fragment;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    entries[2].binding = 2U;
    entries[2].visibility = WGPUShaderStage_Fragment;
    entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    entries[2].buffer.minBindingSize =
        sizeof(gpu_advanced_blend_sampling_uniforms);
    WGPUBindGroupLayoutDescriptor bind_layout_descriptor{};
    bind_layout_descriptor.label = webgpu::string_view(
        "ProGPU semantic advanced-blend bind layout");
    bind_layout_descriptor.entryCount = entries.size();
    bind_layout_descriptor.entries = entries.data();
    engine.semantic_advanced_blend_layout =
        wgpuDeviceCreateBindGroupLayout(
            engine.device,
            &bind_layout_descriptor);
    if (engine.semantic_advanced_blend_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = webgpu::string_view(
        "ProGPU semantic advanced-blend pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts =
        &engine.semantic_advanced_blend_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.semantic_advanced_blend_shader;
    vertex_state.entryPoint = webgpu::string_view("vs_main");
    WGPUColorTargetState color_target{};
    color_target.format = engine.target_format;
    color_target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.semantic_advanced_blend_shader;
    fragment.entryPoint = webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &color_target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic destination-aware blend pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    engine.semantic_advanced_blend_pipeline =
        wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return engine.semantic_advanced_blend_pipeline != nullptr;
}

bool ensure_uniform_buffer(
    progpu_native_engine& engine,
    std::uint32_t operation_count) {
    const std::uint64_t required =
        static_cast<std::uint64_t>(operation_count) *
        advanced_uniform_alignment;
    if (required == 0U ||
        (engine.semantic_advanced_blend_uniform_buffer != nullptr &&
            required <=
                engine.semantic_advanced_blend_uniform_buffer_size)) {
        return true;
    }
    std::uint64_t capacity = 0U;
    if (!progpu::native::try_calculate_buffer_capacity(
            engine.semantic_advanced_blend_uniform_buffer_size,
            required,
            advanced_uniform_alignment,
            engine.max_buffer_size,
            capacity)) {
        return false;
    }
    WGPUBufferDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic advanced-blend uniforms");
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    descriptor.size = capacity;
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (buffer == nullptr) {
        return false;
    }
    if (engine.semantic_advanced_blend_uniform_buffer != nullptr) {
        wgpuBufferDestroy(
            engine.semantic_advanced_blend_uniform_buffer);
        wgpuBufferRelease(
            engine.semantic_advanced_blend_uniform_buffer);
    }
    engine.semantic_advanced_blend_uniform_buffer = buffer;
    engine.semantic_advanced_blend_uniform_buffer_size = capacity;
    return true;
}

WGPURenderPassEncoder begin_pass(
    WGPUCommandEncoder encoder,
    WGPUTextureView view,
    WGPULoadOp load_op,
    const char* label) {
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = view;
    attachment.loadOp = load_op;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {0.0, 0.0, 0.0, 0.0};
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    return wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
}

bool draw_texture_quad(
    progpu_native_engine& engine,
    WGPURenderPassEncoder pass,
    WGPURenderPipeline pipeline,
    WGPUBindGroup target_uniform_group,
    WGPUBindGroup source_group,
    WGPUBindGroup mask_group,
    std::uint32_t first_vertex) {
    if (pipeline == nullptr || target_uniform_group == nullptr ||
        source_group == nullptr || engine.layer_index_buffer == nullptr ||
        engine.semantic_layer_vertex_buffer == nullptr) {
        return false;
    }
    const std::uint64_t vertex_offset =
        static_cast<std::uint64_t>(first_vertex) * sizeof(vector_vertex);
    if (vertex_offset + 4U * sizeof(vector_vertex) >
        engine.semantic_layer_vertex_buffer_size) {
        return false;
    }
    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass, 0U, target_uniform_group, 0U, nullptr);
    wgpuRenderPassEncoderSetBindGroup(
        pass, 1U, source_group, 0U, nullptr);
    if (mask_group != nullptr) {
        wgpuRenderPassEncoderSetBindGroup(
            pass, 2U, mask_group, 0U, nullptr);
    }
    wgpuRenderPassEncoderSetVertexBuffer(
        pass,
        0U,
        engine.semantic_layer_vertex_buffer,
        vertex_offset,
        4U * sizeof(vector_vertex));
    wgpuRenderPassEncoderSetIndexBuffer(
        pass,
        engine.layer_index_buffer,
        WGPUIndexFormat_Uint32,
        0U,
        6U * sizeof(std::uint32_t));
    wgpuRenderPassEncoderDrawIndexed(pass, 6U, 1U, 0U, 0, 0U);
    return true;
}

} // namespace

bool prepare_semantic_advanced_blend_resources(
    progpu_native_engine& engine,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    std::uint32_t source_width,
    std::uint32_t source_height,
    std::uint32_t operation_count,
    float dpi_scale,
    std::uint64_t& uploaded_uniform_bytes) {
    uploaded_uniform_bytes = 0U;
    if (operation_count == 0U) {
        return true;
    }
    if (!create_advanced_blend_pipeline(engine) ||
        !ensure_uniform_buffer(engine, operation_count) ||
        !ensure_semantic_texture_slot(
            engine,
            engine.semantic_root_slot,
            frame_width,
            frame_height,
            "ProGPU semantic destination-aware root") ||
        !ensure_semantic_texture_slot(
            engine,
            engine.semantic_advanced_source_slot,
            source_width,
            source_height,
            "ProGPU semantic bounded advanced-blend source") ||
        !ensure_semantic_texture_slot(
            engine,
            engine.semantic_advanced_output_slot,
            frame_width,
            frame_height,
            "ProGPU semantic advanced-blend output")) {
        return false;
    }
    for (semantic_layer_slot* slot : {
            &engine.semantic_root_slot,
            &engine.semantic_advanced_source_slot,
            &engine.semantic_advanced_output_slot}) {
        const gpu_uniforms uniforms = create_uniforms(
            slot->width,
            slot->height,
            dpi_scale);
        if (engine.upload_uniform_if_changed(
                slot->uniform_buffer,
                uniforms,
                slot->cached_uniforms,
                slot->uniform_cache_valid)) {
            uploaded_uniform_bytes += sizeof(gpu_uniforms);
        }
    }
    return true;
}

bool create_semantic_advanced_blend_binding(
    progpu_native_engine& engine,
    WGPUTextureView destination_view,
    const gpu_advanced_blend_sampling_uniforms& uniforms,
    semantic_render_bundle_span& operation) {
    if (destination_view == nullptr ||
        engine.semantic_advanced_source_slot.view == nullptr ||
        engine.semantic_advanced_blend_uniform_buffer == nullptr ||
        engine.semantic_advanced_blend_layout == nullptr) {
        return false;
    }
    const std::uint64_t offset = operation.advanced_uniform_offset;
    if (offset + sizeof(uniforms) >
        engine.semantic_advanced_blend_uniform_buffer_size) {
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.semantic_advanced_blend_uniform_buffer,
        offset,
        &uniforms,
        sizeof(uniforms));
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, destination_view},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr,
            engine.semantic_advanced_source_slot.view},
        {nullptr, 2U, engine.semantic_advanced_blend_uniform_buffer,
            offset, sizeof(uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic advanced-blend binding");
    descriptor.layout = engine.semantic_advanced_blend_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    operation.advanced_blend_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &descriptor);
    return operation.advanced_blend_bind_group != nullptr;
}

bool encode_semantic_advanced_blend(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView parent_view,
    WGPUBindGroup parent_uniform_group,
    const semantic_render_bundle_span& operation) {
    if (operation.source_layer >= engine.semantic_layer_slots.size() ||
        operation.advanced_blend_bind_group == nullptr ||
        parent_view == nullptr || parent_uniform_group == nullptr) {
        return false;
    }
    const auto& source_slot =
        engine.semantic_layer_slots[operation.source_layer];
    WGPUBindGroup source_group = operation.effect_count == 0U
        ? source_slot.bind_group
        : operation.final_effect_texture <
                source_slot.effect_output_bind_groups.size()
            ? source_slot.effect_output_bind_groups[
                operation.final_effect_texture]
            : nullptr;
    const bool masked = operation.mask_bind_group != nullptr;
    bool ignored_cache_hit = false;
    WGPURenderPipeline resolve_pipeline =
        get_or_create_fixed_group_blend_pipeline(
            engine,
            PROGPU_NATIVE_BLEND_SRC,
            masked,
            ignored_cache_hit);
    WGPURenderPassEncoder pass = begin_pass(
        encoder,
        engine.semantic_advanced_source_slot.view,
        WGPULoadOp_Clear,
        "ProGPU semantic advanced-blend source resolve");
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderSetScissorRect(
        pass,
        0U,
        0U,
        operation.source_width,
        operation.source_height);
    const bool source_resolved = draw_texture_quad(
        engine,
        pass,
        resolve_pipeline,
        engine.semantic_advanced_source_slot.image_uniform_bind_group,
        source_group,
        operation.mask_bind_group,
        operation.first_resolve_vertex);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (!source_resolved) {
        return false;
    }

    pass = begin_pass(
        encoder,
        engine.semantic_advanced_output_slot.view,
        WGPULoadOp_Clear,
        "ProGPU semantic destination-aware blend");
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderSetScissorRect(
        pass,
        0U,
        0U,
        operation.target_width,
        operation.target_height);
    wgpuRenderPassEncoderSetPipeline(
        pass,
        engine.semantic_advanced_blend_pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        operation.advanced_blend_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);

    bool copy_cache_hit = false;
    WGPURenderPipeline copy_pipeline =
        get_or_create_fixed_group_blend_pipeline(
            engine,
            PROGPU_NATIVE_BLEND_SRC,
            false,
            copy_cache_hit);
    pass = begin_pass(
        encoder,
        parent_view,
        WGPULoadOp_Load,
        "ProGPU semantic advanced-blend parent replace");
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderSetScissorRect(
        pass,
        0U,
        0U,
        operation.target_width,
        operation.target_height);
    const bool copied = draw_texture_quad(
        engine,
        pass,
        copy_pipeline,
        parent_uniform_group,
        engine.semantic_advanced_output_slot.bind_group,
        nullptr,
        operation.first_copy_vertex);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return copied;
}

bool encode_semantic_root_copy(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    std::uint32_t first_vertex) {
    bool ignored_cache_hit = false;
    WGPURenderPipeline pipeline = get_or_create_fixed_group_blend_pipeline(
        engine,
        PROGPU_NATIVE_BLEND_SRC,
        false,
        ignored_cache_hit);
    WGPURenderPassEncoder pass = begin_pass(
        encoder,
        target_view,
        WGPULoadOp_Clear,
        "ProGPU semantic destination-aware root copy");
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderSetScissorRect(
        pass,
        0U,
        0U,
        engine.semantic_root_slot.width,
        engine.semantic_root_slot.height);
    const bool copied = draw_texture_quad(
        engine,
        pass,
        pipeline,
        engine.layer_uniform_bind_group,
        engine.semantic_root_slot.bind_group,
        nullptr,
        first_vertex);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return copied;
}

} // namespace progpu::native::execution
