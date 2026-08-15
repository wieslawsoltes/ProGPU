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
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"

#include <cstdint>

namespace progpu::native::execution {
namespace {

WGPURenderPassEncoder begin_backdrop_pass(
    WGPUCommandEncoder encoder,
    WGPUTextureView view,
    const char* label) {
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {0.0, 0.0, 0.0, 0.0};
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    return wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
}

bool draw_backdrop_quad(
    progpu_native_engine& engine,
    WGPURenderPassEncoder pass,
    WGPURenderPipeline pipeline,
    WGPUBindGroup target_uniform_group,
    WGPUBindGroup source_group,
    std::uint32_t first_vertex) {
    if (pass == nullptr || pipeline == nullptr ||
        target_uniform_group == nullptr || source_group == nullptr ||
        engine.layer_index_buffer == nullptr ||
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

bool prepare_semantic_backdrop_resources(
    progpu_native_engine& engine,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    std::uint32_t operation_count,
    float dpi_scale,
    std::uint64_t& uploaded_uniform_bytes) {
    uploaded_uniform_bytes = 0U;
    if (operation_count == 0U) {
        return true;
    }
    if (!ensure_semantic_texture_slot(
            engine,
            engine.semantic_root_slot,
            frame_width,
            frame_height,
            "ProGPU semantic destination-sampling root")) {
        return false;
    }
    const gpu_uniforms uniforms = create_uniforms(
        engine.semantic_root_slot.width,
        engine.semantic_root_slot.height,
        dpi_scale);
    if (engine.upload_uniform_if_changed(
            engine.semantic_root_slot.uniform_buffer,
            uniforms,
            engine.semantic_root_slot.cached_uniforms,
            engine.semantic_root_slot.uniform_cache_valid)) {
        uploaded_uniform_bytes += sizeof(gpu_uniforms);
    }
    return true;
}

bool encode_semantic_backdrop_capture(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const semantic_render_bundle_span& operation,
    std::uint32_t& effect_pass_count) {
    if (!operation.backdrop || encoder == nullptr ||
        operation.source_layer >= engine.semantic_layer_slots.size()) {
        return false;
    }
    auto& child = engine.semantic_layer_slots[operation.source_layer];
    const semantic_layer_slot* parent = operation.parent_layer ==
            PROGPU_NATIVE_SCENE_NO_INDEX
        ? &engine.semantic_root_slot
        : operation.parent_layer < engine.semantic_layer_slots.size()
            ? &engine.semantic_layer_slots[operation.parent_layer]
            : nullptr;
    if (parent == nullptr || parent->texture == nullptr ||
        child.texture == nullptr || child.view == nullptr ||
        child.image_uniform_bind_group == nullptr) {
        return false;
    }

    WGPURenderPassEncoder pass = begin_backdrop_pass(
        encoder,
        child.view,
        "ProGPU semantic bounded backdrop clear");
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (operation.source_width == 0U || operation.source_height == 0U) {
        return true;
    }
    webgpu::image_copy_texture source{};
    source.texture = parent->texture;
    source.mipLevel = 0U;
    source.origin = {
        operation.backdrop_source_x,
        operation.backdrop_source_y,
        0U};
    source.aspect = WGPUTextureAspect_All;
    webgpu::image_copy_texture destination{};
    destination.texture = child.texture;
    destination.mipLevel = 0U;
    destination.origin = {0U, 0U, 0U};
    destination.aspect = WGPUTextureAspect_All;
    const WGPUExtent3D extent{
        operation.source_width,
        operation.source_height,
        1U};
    wgpuCommandEncoderCopyTextureToTexture(
        encoder,
        &source,
        &destination,
        &extent);
    if (operation.effect_count == 0U) {
        return true;
    }
    bool ignored_cache_hit = false;
    WGPURenderPipeline copy_pipeline =
        get_or_create_fixed_group_blend_pipeline(
            engine,
            PROGPU_NATIVE_BLEND_SRC,
            false,
            ignored_cache_hit);
    if (!encode_semantic_effect_chain(
            engine,
            encoder,
            operation,
            effect_pass_count) ||
        operation.final_effect_texture >=
            child.effect_output_bind_groups.size()) {
        return false;
    }
    pass = begin_backdrop_pass(
        encoder,
        child.view,
        "ProGPU semantic filtered backdrop resolve");
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderSetScissorRect(
        pass,
        0U,
        0U,
        operation.source_width,
        operation.source_height);
    const bool resolved = draw_backdrop_quad(
        engine,
        pass,
        copy_pipeline,
        child.image_uniform_bind_group,
        child.effect_output_bind_groups[
            operation.final_effect_texture],
        operation.first_backdrop_resolve_vertex);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return resolved;
}

} // namespace progpu::native::execution
