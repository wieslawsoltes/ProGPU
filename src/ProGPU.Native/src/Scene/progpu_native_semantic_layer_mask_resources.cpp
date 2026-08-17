#include "progpu_native_semantic_layer_mask_resources.hpp"
#include "progpu_native_semantic_layer_mask.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_native_engine.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_replay.hpp"
#include "progpu_webgpu_compat.hpp"

#include <array>
#include <cmath>
#include <limits>
#include <new>
#include <vector>

namespace progpu::native::execution {

bool create_semantic_coverage_mask_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_coverage_mask& source,
    const std::byte* coverage,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation,
    std::uint64_t& texture_upload_bytes) {
    texture_upload_bytes = 0U;
    if (coverage == nullptr || !create_layer_mask_resources(engine)) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = webgpu::string_view(
        "ProGPU retained semantic R8 coverage mask");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {source.width, source.height, 1U};
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        engine.device, &texture_descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    const double m11 = source.transform.m11;
    const double m12 = source.transform.m12;
    const double m21 = source.transform.m21;
    const double m22 = source.transform.m22;
    const double m31 = source.transform.m31 -
        static_cast<double>(target_extent.x) / dpi_scale;
    const double m32 = source.transform.m32 -
        static_cast<double>(target_extent.y) / dpi_scale;
    const double determinant = m11 * m22 - m12 * m21;
    const double inverse_m11 = m22 / determinant;
    const double inverse_m12 = -m12 / determinant;
    const double inverse_m21 = -m21 / determinant;
    const double inverse_m22 = m11 / determinant;
    const double inverse_m31 = (m21 * m32 - m22 * m31) / determinant;
    const double inverse_m32 = (m12 * m31 - m11 * m32) / determinant;
    const double physical_x = dpi_scale * source.bounds.width;
    const double physical_y = dpi_scale * source.bounds.height;
    gpu_mask_sampling_uniforms uniforms{};
    const std::array<double, 6U> uv_transform{
        inverse_m11 / physical_x,
        inverse_m21 / physical_x,
        (inverse_m31 - source.bounds.x) / source.bounds.width,
        inverse_m12 / physical_y,
        inverse_m22 / physical_y,
        (inverse_m32 - source.bounds.y) / source.bounds.height};
    for (double value : uv_transform) {
        if (!std::isfinite(value) ||
            value < -std::numeric_limits<float>::max() ||
            value > std::numeric_limits<float>::max()) {
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
    }
    uniforms.coordinate0[0] = static_cast<float>(uv_transform[0]);
    uniforms.coordinate0[1] = static_cast<float>(uv_transform[1]);
    uniforms.coordinate0[2] = static_cast<float>(uv_transform[2]);
    uniforms.coordinate1[0] = static_cast<float>(uv_transform[3]);
    uniforms.coordinate1[1] = static_cast<float>(uv_transform[4]);
    uniforms.coordinate1[2] = static_cast<float>(uv_transform[5]);
    uniforms.options[0] = 1.0F;
    uniforms.options[1] = source.opacity;
    uniforms.options[2] = 1.0F;
    uniforms.options[3] = 1.0F;

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = webgpu::string_view(
        "ProGPU retained semantic coverage-mask uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(uniforms);
    WGPUBuffer buffer = wgpuDeviceCreateBuffer(
        engine.device, &buffer_descriptor);
    if (buffer == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    WGPUSampler sampler = source.sampling ==
            PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
        ? engine.image_nearest_sampler
        : engine.image_linear_sampler;
    WGPUBindGroup bind_group = create_layer_mask_bind_group(
        engine,
        sampler,
        view,
        "ProGPU retained semantic coverage-mask binding",
        buffer);
    if (bind_group == nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    webgpu::image_copy_texture destination{};
    destination.texture = texture;
    destination.aspect = WGPUTextureAspect_All;
    webgpu::texture_data_layout layout{};
    layout.bytesPerRow = source.row_bytes;
    layout.rowsPerImage = source.height;
    const WGPUExtent3D extent{source.width, source.height, 1U};
    const std::uint64_t upload_size =
        static_cast<std::uint64_t>(source.row_bytes) *
            (source.height - 1U) + source.width;
    wgpuQueueWriteTexture(
        engine.queue,
        &destination,
        coverage,
        static_cast<std::size_t>(upload_size),
        &layout,
        &extent);
    wgpuQueueWriteBuffer(
        engine.queue, buffer, 0U, &uniforms, sizeof(uniforms));
    operation.mask_texture = texture;
    operation.mask_texture_view = view;
    operation.mask_uniform_buffer = buffer;
    operation.mask_bind_group = bind_group;
    operation.mask_uniform_upload_bytes = sizeof(uniforms);
    texture_upload_bytes = upload_size;
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool create_semantic_vector_mask_binding(
    progpu_native_engine& engine,
    const semantic::semantic_layer_mask& parsed,
    const progpu_native_scene_resource& resource,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation) {
    if (target_extent.width == 0U || target_extent.height == 0U ||
        !std::isfinite(dpi_scale) || dpi_scale <= 0.0F ||
        !create_layer_mask_resources(engine)) {
        return false;
    }

    try {
        std::vector<progpu_native_clip_path> paths;
        std::vector<progpu_native_path_boolean_node> boolean_nodes;
        paths.reserve(parsed.vector.path_count);
        boolean_nodes.reserve(parsed.vector.boolean_node_count);
        const float logical_offset_x =
            static_cast<float>(target_extent.x) / dpi_scale;
        const float logical_offset_y =
            static_cast<float>(target_extent.y) / dpi_scale;
        for (std::uint32_t index = 0U;
             index < parsed.vector.path_count;
             ++index) {
            const auto& source = parsed.vector_paths[index];
            if (source.segment_offset >
                    std::numeric_limits<std::size_t>::max() ||
                source.segment_count >
                    std::numeric_limits<std::size_t>::max() ||
                source.boolean_node_offset >
                    std::numeric_limits<std::size_t>::max() ||
                source.boolean_node_count >
                    std::numeric_limits<std::size_t>::max()) {
                return false;
            }
            progpu_native_clip_path path{};
            path.segment_offset =
                static_cast<std::size_t>(source.segment_offset);
            path.segment_count =
                static_cast<std::size_t>(source.segment_count);
            path.boolean_node_offset =
                static_cast<std::size_t>(source.boolean_node_offset);
            path.boolean_node_count =
                static_cast<std::size_t>(source.boolean_node_count);
            path.min_x = source.min_x;
            path.min_y = source.min_y;
            path.max_x = source.max_x;
            path.max_y = source.max_y;
            path.transform = source.transform;
            path.transform.m31 -= logical_offset_x;
            path.transform.m32 -= logical_offset_y;
            path.fill_rule = source.fill_rule;
            path.sample_grid = source.sample_grid;
            path.operation = source.operation;
            paths.push_back(path);
        }
        for (std::uint32_t index = 0U;
             index < parsed.vector.boolean_node_count;
             ++index) {
            const auto& source = parsed.vector_boolean_nodes[index];
            if (source.segment_offset >
                    std::numeric_limits<std::size_t>::max() ||
                source.segment_count >
                    std::numeric_limits<std::size_t>::max()) {
                return false;
            }
            progpu_native_path_boolean_node node{};
            node.segment_offset =
                static_cast<std::size_t>(source.segment_offset);
            node.segment_count =
                static_cast<std::size_t>(source.segment_count);
            node.min_x = source.min_x;
            node.min_y = source.min_y;
            node.max_x = source.max_x;
            node.max_y = source.max_y;
            node.fill_rule = source.fill_rule;
            node.kind = source.kind;
            boolean_nodes.push_back(node);
        }

        progpu_native_clip_chain chain{};
        chain.struct_size = sizeof(chain);
        chain.paths = paths.data();
        chain.path_count = paths.size();
        chain.segments = parsed.vector_segments;
        chain.segment_count = parsed.vector.segment_count;
        chain.boolean_nodes = boolean_nodes.data();
        chain.boolean_node_count = boolean_nodes.size();
        progpu_native_group_mask mask{};
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN;
        std::uint64_t mixed_revision =
            resource.resource_id * 0x9E3779B185EBCA87ULL ^
            resource.generation;
        mixed_revision ^= static_cast<std::uint64_t>(target_extent.x) *
            0xC2B2AE3D27D4EB4FULL;
        mixed_revision ^= static_cast<std::uint64_t>(target_extent.y) *
            0x165667B19E3779F9ULL;
        mask.revision = static_cast<std::uint32_t>(
            mixed_revision ^ (mixed_revision >> 32U));
        if (mask.revision == 0U) {
            mask.revision = 1U;
        }
        mask.opacity = parsed.vector.opacity;
        mask.clip_chain = &chain;
        if (!rebuild_vector_clip_chain(
                engine,
                mask,
                target_extent.width,
                target_extent.height,
                dpi_scale)) {
            return false;
        }

        WGPUTextureDescriptor texture_descriptor{};
        texture_descriptor.label = webgpu::string_view(
            "ProGPU retained semantic vector coverage mask");
        texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        texture_descriptor.dimension = WGPUTextureDimension_2D;
        texture_descriptor.size = {
            target_extent.width,
            target_extent.height,
            1U};
        texture_descriptor.format = WGPUTextureFormat_R8Unorm;
        texture_descriptor.mipLevelCount = 1U;
        texture_descriptor.sampleCount = 1U;
        WGPUTexture texture = wgpuDeviceCreateTexture(
            engine.device, &texture_descriptor);
        WGPUTextureView view = texture == nullptr
            ? nullptr
            : wgpuTextureCreateView(texture, nullptr);
        if (texture == nullptr || view == nullptr) {
            if (view != nullptr) wgpuTextureViewRelease(view);
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
            }
            return false;
        }

        WGPUCommandEncoderDescriptor encoder_descriptor{};
        encoder_descriptor.label = webgpu::string_view(
            "ProGPU retain semantic vector mask copy");
        WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
            engine.device, &encoder_descriptor);
        if (encoder == nullptr) {
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
        webgpu::image_copy_texture copy_source{};
        copy_source.texture = engine.clip_accumulation_textures[
            engine.clip_final_index];
        copy_source.aspect = WGPUTextureAspect_All;
        webgpu::image_copy_texture copy_destination{};
        copy_destination.texture = texture;
        copy_destination.aspect = WGPUTextureAspect_All;
        const WGPUExtent3D copy_extent{
            target_extent.width,
            target_extent.height,
            1U};
        wgpuCommandEncoderCopyTextureToTexture(
            encoder,
            &copy_source,
            &copy_destination,
            &copy_extent);
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = webgpu::string_view(
            "ProGPU retained semantic vector mask copy commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder, &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
        engine.submit(command);
        wgpuCommandBufferRelease(command);

        gpu_mask_sampling_uniforms uniforms{};
        uniforms.coordinate1[0] =
            1.0F / static_cast<float>(target_extent.width);
        uniforms.coordinate1[1] =
            1.0F / static_cast<float>(target_extent.height);
        uniforms.options[0] = 1.0F;
        uniforms.options[1] = parsed.vector.opacity;
        uniforms.options[2] = 1.0F;
        uniforms.options[3] = 1.0F;
        WGPUBufferDescriptor buffer_descriptor{};
        buffer_descriptor.label = webgpu::string_view(
            "ProGPU retained semantic vector-mask uniforms");
        buffer_descriptor.usage = WGPUBufferUsage_Uniform |
            WGPUBufferUsage_CopyDst;
        buffer_descriptor.size = sizeof(uniforms);
        WGPUBuffer buffer = wgpuDeviceCreateBuffer(
            engine.device, &buffer_descriptor);
        WGPUBindGroup bind_group = buffer == nullptr
            ? nullptr
            : create_layer_mask_bind_group(
                engine,
                engine.image_linear_sampler,
                view,
                "ProGPU retained semantic vector-mask binding",
                buffer);
        if (bind_group == nullptr) {
            if (buffer != nullptr) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
            }
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
        wgpuQueueWriteBuffer(
            engine.queue, buffer, 0U, &uniforms, sizeof(uniforms));
        operation.mask_texture = texture;
        operation.mask_texture_view = view;
        operation.mask_uniform_buffer = buffer;
        operation.mask_bind_group = bind_group;
        operation.mask_uniform_upload_bytes = sizeof(uniforms);
        ++engine.layer_mask_bind_group_generation;
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    }
}

} // namespace progpu::native::execution
