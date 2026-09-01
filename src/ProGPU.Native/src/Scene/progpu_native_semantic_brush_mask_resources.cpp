#include "progpu_native_semantic_layer_mask_resources.hpp"
#include "progpu_native_semantic_layer_mask.hpp"
#include "progpu_native_semantic_state.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_native_engine.hpp"
#include "progpu_native_geometry_analytic.hpp"
#include "progpu_native_geometry_stroke.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_semantic_replay.hpp"
#include "progpu_webgpu_compat.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <new>
#include <vector>

namespace progpu::native::execution {
namespace {

WGPUBuffer create_buffer(
    progpu_native_engine& engine,
    const char* label,
    std::uint64_t size,
    webgpu::buffer_usage_flags usage) {
    WGPUBufferDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.size = size;
    descriptor.usage = usage;
    return wgpuDeviceCreateBuffer(engine.device, &descriptor);
}

void release_transient_buffer(WGPUBuffer buffer, bool submitted) noexcept {
    if (buffer == nullptr) {
        return;
    }
    if (!submitted) {
        wgpuBufferDestroy(buffer);
    }
    wgpuBufferRelease(buffer);
}

semantic::scissor geometry_mask_scissor(
    const progpu_native_scene_layer_geometry_mask& mask,
    const semantic::scissor& target_extent,
    float dpi_scale) noexcept {
    constexpr double mask_scissor_padding_pixels = 2.0;
    const auto transform_point = [&](double x, double y) noexcept {
        return std::array<double, 2U>{
            (x * mask.transform.m11 + y * mask.transform.m21 +
                mask.transform.m31) * dpi_scale - target_extent.x,
            (x * mask.transform.m12 + y * mask.transform.m22 +
                mask.transform.m32) * dpi_scale - target_extent.y};
    };
    const double right = mask.bounds.x + mask.bounds.width;
    const double bottom = mask.bounds.y + mask.bounds.height;
    const std::array<std::array<double, 2U>, 4U> corners{
        transform_point(mask.bounds.x, mask.bounds.y),
        transform_point(right, mask.bounds.y),
        transform_point(mask.bounds.x, bottom),
        transform_point(right, bottom)};
    double minimum_x = corners[0][0];
    double minimum_y = corners[0][1];
    double maximum_x = corners[0][0];
    double maximum_y = corners[0][1];
    for (std::size_t index = 1U; index < corners.size(); ++index) {
        minimum_x = std::min(minimum_x, corners[index][0]);
        minimum_y = std::min(minimum_y, corners[index][1]);
        maximum_x = std::max(maximum_x, corners[index][0]);
        maximum_y = std::max(maximum_y, corners[index][1]);
    }
    minimum_x = std::clamp(
        std::floor(minimum_x - mask_scissor_padding_pixels),
        0.0,
        static_cast<double>(target_extent.width));
    minimum_y = std::clamp(
        std::floor(minimum_y - mask_scissor_padding_pixels),
        0.0,
        static_cast<double>(target_extent.height));
    maximum_x = std::clamp(
        std::ceil(maximum_x + mask_scissor_padding_pixels),
        0.0,
        static_cast<double>(target_extent.width));
    maximum_y = std::clamp(
        std::ceil(maximum_y + mask_scissor_padding_pixels),
        0.0,
        static_cast<double>(target_extent.height));
    if (maximum_x <= minimum_x || maximum_y <= minimum_y) {
        return semantic::scissor{0U, 0U, 1U, 1U, false};
    }
    const auto x = static_cast<std::uint32_t>(minimum_x);
    const auto y = static_cast<std::uint32_t>(minimum_y);
    return semantic::scissor{
        x,
        y,
        static_cast<std::uint32_t>(maximum_x) - x,
        static_cast<std::uint32_t>(maximum_y) - y,
        true};
}

} // namespace

bool create_semantic_brush_mask_binding(
    progpu_native_engine& engine,
    const semantic::semantic_layer_mask& parsed,
    const semantic::scissor& target_extent,
    float dpi_scale,
    const semantic::semantic_state_cursor* composite_state_cursor,
    const progpu_native_scene_state* composite_state,
    semantic_render_bundle_span& operation) {
    const bool geometry_mask = parsed.kind ==
        PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY;
    const auto& source_brush = geometry_mask
        ? parsed.geometry.brush
        : parsed.brush.brush;
    const std::uint32_t gradient_stop_count = geometry_mask
        ? parsed.geometry.gradient_stop_count
        : parsed.brush.gradient_stop_count;
    const float mask_opacity = geometry_mask
        ? parsed.geometry.opacity
        : parsed.brush.opacity;
    if (target_extent.width == 0U || target_extent.height == 0U ||
        !std::isfinite(dpi_scale) || dpi_scale <= 0.0F ||
        !create_layer_mask_resources(engine) ||
        !create_analytic_brush_mask_pipeline(engine)) {
        return false;
    }

    std::vector<vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    try {
        if (geometry_mask) {
            for (std::uint32_t index = 0U;
                 index < parsed.geometry.primitive_count;
                 ++index) {
                auto primitive = parsed.composite_geometry_primitives[
                    parsed.geometry.primitive_offset + index];
                primitive.transform.m31 -=
                    static_cast<float>(target_extent.x) / dpi_scale;
                primitive.transform.m32 -=
                    static_cast<float>(target_extent.y) / dpi_scale;
                if (source_brush.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
                    primitive.color = source_brush.colors[0];
                }
                if (!append_geometry_primitive(
                        primitive,
                        0.0F,
                        vertices,
                        indices)) {
                    return false;
                }
            }
        } else {
            progpu_native_analytic_primitive primitive{};
            primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
            primitive.x = parsed.brush.bounds.x;
            primitive.y = parsed.brush.bounds.y;
            primitive.width = parsed.brush.bounds.width;
            primitive.height = parsed.brush.bounds.height;
            primitive.color =
                source_brush.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID
                    ? source_brush.colors[0]
                    : progpu_native_color{
                        primitive.x + primitive.width * 0.5F,
                        primitive.y + primitive.height * 0.5F,
                        0.0F,
                        1.0F};
            primitive.transform = parsed.brush.transform;
            primitive.transform.m31 -=
                static_cast<float>(target_extent.x) / dpi_scale;
            primitive.transform.m32 -=
                static_cast<float>(target_extent.y) / dpi_scale;
            if (composite_state_cursor != nullptr &&
                composite_state != nullptr) {
                if (primitive.width <= 0.0F || primitive.height <= 0.0F) {
                    return false;
                }
                // A WPF cache-root guideline deforms the retained bitmap and
                // opacity-mask coverage as one post-cache shape. Visual
                // guidelines are disabled under rotation/shear, so snapping
                // the exact mask rectangle corners yields the separable
                // affine frame used by the composite quad. Brush coordinates
                // intentionally remain in their original target-space frame.
                const float target_x =
                    static_cast<float>(target_extent.x) / dpi_scale;
                const float target_y =
                    static_cast<float>(target_extent.y) / dpi_scale;
                float left = primitive.x * primitive.transform.m11 +
                    primitive.y * primitive.transform.m21 +
                    primitive.transform.m31 + target_x;
                float top = primitive.x * primitive.transform.m12 +
                    primitive.y * primitive.transform.m22 +
                    primitive.transform.m32 + target_y;
                float right =
                    (primitive.x + primitive.width) *
                        primitive.transform.m11 +
                    (primitive.y + primitive.height) *
                        primitive.transform.m21 +
                    primitive.transform.m31 + target_x;
                float bottom =
                    (primitive.x + primitive.width) *
                        primitive.transform.m12 +
                    (primitive.y + primitive.height) *
                        primitive.transform.m22 +
                    primitive.transform.m32 + target_y;
                composite_state_cursor->snap_composite_point(
                    *composite_state,
                    left,
                    top);
                composite_state_cursor->snap_composite_point(
                    *composite_state,
                    right,
                    bottom);
                primitive.transform.m11 =
                    (right - left) / primitive.width;
                primitive.transform.m12 = 0.0F;
                primitive.transform.m21 = 0.0F;
                primitive.transform.m22 =
                    (bottom - top) / primitive.height;
                primitive.transform.m31 = left - target_x -
                    primitive.x * primitive.transform.m11;
                primitive.transform.m32 = top - target_y -
                    primitive.y * primitive.transform.m22;
            }
            float minimum_scale = 0.0F;
            vertices.reserve(4U);
            indices.reserve(6U);
            if (!try_get_minimum_scale(primitive.transform, minimum_scale) ||
                !append_analytic_primitive(
                        primitive,
                        antialias_padding_pixels / minimum_scale,
                        vertices,
                        indices)) {
                return false;
            }
        }
    } catch (const std::bad_alloc&) {
        return false;
    }

    const std::uint64_t vertex_bytes = vertices.size() * sizeof(vector_vertex);
    const std::uint64_t index_bytes = indices.size() * sizeof(std::uint32_t);
    const std::uint64_t gradient_bytes = std::max<std::uint64_t>(
        static_cast<std::uint64_t>(gradient_stop_count) *
            sizeof(progpu_native_scene_gradient_stop),
        sizeof(progpu_native_scene_gradient_stop));
    WGPUBuffer vertex_buffer = create_buffer(
        engine,
        "ProGPU retained brush-mask vertices",
        vertex_bytes,
        WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst);
    WGPUBuffer index_buffer = create_buffer(
        engine,
        "ProGPU retained brush-mask indices",
        index_bytes,
        WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst);
    WGPUBuffer frame_buffer = create_buffer(
        engine,
        "ProGPU retained brush-mask frame uniforms",
        sizeof(gpu_uniforms),
        WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst);
    WGPUBuffer brush_buffer = create_buffer(
        engine,
        "ProGPU retained brush-mask material",
        sizeof(progpu_native_scene_brush),
        WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
    WGPUBuffer gradient_buffer = create_buffer(
        engine,
        "ProGPU retained brush-mask gradient stops",
        gradient_bytes,
        WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
    WGPUBindGroup draw_bind_group = nullptr;
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
    WGPUBuffer mask_uniform_buffer = nullptr;
    WGPUBindGroup mask_bind_group = nullptr;
    bool submitted = false;

    const auto cleanup = [&]() noexcept {
        if (draw_bind_group != nullptr) {
            wgpuBindGroupRelease(draw_bind_group);
        }
        release_transient_buffer(vertex_buffer, submitted);
        release_transient_buffer(index_buffer, submitted);
        release_transient_buffer(frame_buffer, submitted);
        release_transient_buffer(brush_buffer, submitted);
        release_transient_buffer(gradient_buffer, submitted);
        if (mask_bind_group != nullptr) {
            wgpuBindGroupRelease(mask_bind_group);
        }
        if (mask_uniform_buffer != nullptr) {
            wgpuBufferDestroy(mask_uniform_buffer);
            wgpuBufferRelease(mask_uniform_buffer);
        }
        if (view != nullptr) {
            wgpuTextureViewRelease(view);
        }
        if (texture != nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
    };

    if (vertex_buffer == nullptr || index_buffer == nullptr ||
        frame_buffer == nullptr || brush_buffer == nullptr ||
        gradient_buffer == nullptr) {
        cleanup();
        return false;
    }
    draw_bind_group = create_analytic_uniform_bind_group_for_buffer(
        engine,
        frame_buffer,
        brush_buffer,
        sizeof(progpu_native_scene_brush),
        gradient_buffer,
        gradient_bytes,
        "ProGPU retained brush-mask draw binding");
    if (draw_bind_group == nullptr) {
        cleanup();
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = webgpu::string_view(
        "ProGPU retained GPU-generated brush opacity mask");
    texture_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {
        target_extent.width,
        target_extent.height,
        1U};
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    texture = wgpuDeviceCreateTexture(engine.device, &texture_descriptor);
    view = texture == nullptr ? nullptr : wgpuTextureCreateView(texture, nullptr);
    if (texture == nullptr || view == nullptr) {
        cleanup();
        return false;
    }

    gpu_mask_sampling_uniforms mask_uniforms{};
    mask_uniforms.coordinate1[0] =
        1.0F / static_cast<float>(target_extent.width);
    mask_uniforms.coordinate1[1] =
        1.0F / static_cast<float>(target_extent.height);
    mask_uniforms.options[0] = 1.0F;
    mask_uniforms.options[1] = mask_opacity;
    mask_uniforms.options[2] = 0.0F;
    mask_uniforms.options[3] = 1.0F;
    mask_uniform_buffer = create_buffer(
        engine,
        "ProGPU retained brush-mask sampling uniforms",
        sizeof(mask_uniforms),
        WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst);
    mask_bind_group = mask_uniform_buffer == nullptr
        ? nullptr
        : create_layer_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU retained brush-mask sampling binding",
            mask_uniform_buffer);
    if (mask_bind_group == nullptr) {
        cleanup();
        return false;
    }

    const gpu_uniforms frame_uniforms = create_uniforms(
        target_extent.width,
        target_extent.height,
        dpi_scale);
    const std::array<std::byte, sizeof(progpu_native_scene_gradient_stop)>
        gradient_sentinel{};
    wgpuQueueWriteBuffer(
        engine.queue, vertex_buffer, 0U, vertices.data(), vertex_bytes);
    wgpuQueueWriteBuffer(
        engine.queue, index_buffer, 0U, indices.data(), index_bytes);
    wgpuQueueWriteBuffer(
        engine.queue, frame_buffer, 0U, &frame_uniforms,
        sizeof(frame_uniforms));
    wgpuQueueWriteBuffer(
        engine.queue, brush_buffer, 0U, &source_brush,
        sizeof(source_brush));
    wgpuQueueWriteBuffer(
        engine.queue,
        gradient_buffer,
        0U,
        gradient_stop_count == 0U
            ? static_cast<const void*>(gradient_sentinel.data())
            : static_cast<const void*>(parsed.brush_stops),
        static_cast<std::size_t>(gradient_bytes));
    wgpuQueueWriteBuffer(
        engine.queue, mask_uniform_buffer, 0U, &mask_uniforms,
        sizeof(mask_uniforms));

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = webgpu::string_view(
        "ProGPU retained brush-mask encoder");
    const bool owns_encoder = engine.semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = owns_encoder
        ? wgpuDeviceCreateCommandEncoder(engine.device, &encoder_descriptor)
        : engine.semantic_encoder;
    if (encoder == nullptr) {
        cleanup();
        return false;
    }
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 1.0};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = webgpu::string_view(
        "ProGPU retained brush-mask pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        cleanup();
        return false;
    }
    wgpuRenderPassEncoderSetPipeline(
        pass,
        engine.analytic_brush_mask_pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass, 0U, draw_bind_group, 0U, nullptr);
    wgpuRenderPassEncoderSetBindGroup(
        pass, 1U, engine.analytic_atlas_bind_group, 0U, nullptr);
    wgpuRenderPassEncoderSetVertexBuffer(
        pass, 0U, vertex_buffer, 0U, vertex_bytes);
    wgpuRenderPassEncoderSetIndexBuffer(
        pass, index_buffer, WGPUIndexFormat_Uint32, 0U, index_bytes);
    const semantic::scissor draw_extent = geometry_mask
        ? geometry_mask_scissor(parsed.geometry, target_extent, dpi_scale)
        : semantic::scissor{
            0U,
            0U,
            target_extent.width,
            target_extent.height,
            true};
    if (draw_extent.drawable) {
        wgpuRenderPassEncoderSetScissorRect(
            pass,
            draw_extent.x,
            draw_extent.y,
            draw_extent.width,
            draw_extent.height);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = webgpu::string_view(
            "ProGPU retained brush-mask commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            cleanup();
            return false;
        }
        engine.submit(command);
        wgpuCommandBufferRelease(command);
    }
    // The command encoder retains every transient GPU object referenced by
    // the pass. Release the handles without destroying their backing storage;
    // the shared semantic encoder will submit them with the final image draw.
    submitted = true;

    operation.mask_texture = texture;
    operation.mask_texture_view = view;
    operation.mask_uniform_buffer = mask_uniform_buffer;
    operation.mask_bind_group = mask_bind_group;
    operation.mask_uniform_upload_bytes = sizeof(mask_uniforms);
    texture = nullptr;
    view = nullptr;
    mask_uniform_buffer = nullptr;
    mask_bind_group = nullptr;
    cleanup();
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool create_semantic_geometry_mask_binding(
    progpu_native_engine& engine,
    const semantic::semantic_layer_mask& parsed,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation) {
    return create_semantic_brush_mask_binding(
        engine,
        parsed,
        target_extent,
        dpi_scale,
        nullptr,
        nullptr,
        operation);
}

} // namespace progpu::native::execution
