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
#include "progpu_native_webgpu_resources.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <unordered_map>
#include <vector>

namespace progpu::native::execution {

using semantic_scissor = semantic::scissor;
using semantic_layer_budget = semantic::layer_budget;
using semantic_compilation_budget = semantic::compilation_budget;
inline constexpr std::uint32_t semantic_effect_uniform_alignment =
    semantic::effect_uniform_alignment;

WGPUBindGroup create_image_mask_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, engine.image_mask_uniform_buffer, 0U,
            sizeof(gpu_mask_sampling_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = ::progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.image_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_image_mask_resources(progpu_native_engine& engine) {
    if (engine.image_mask_pipeline != nullptr &&
        engine.image_color_matrix_pipeline != nullptr &&
        engine.image_masked_color_matrix_pipeline != nullptr) {
        return true;
    }
    if (engine.image_pipeline == nullptr || engine.image_shader == nullptr ||
        engine.image_uniform_layout == nullptr ||
        engine.image_texture_layout == nullptr ||
        engine.image_mask_layout != nullptr ||
        engine.image_mask_uniform_buffer != nullptr ||
        engine.image_color_matrix_pipeline != nullptr ||
        engine.image_masked_color_matrix_pipeline != nullptr) {
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
    mask_entries[2].buffer.minBindingSize = sizeof(gpu_mask_sampling_uniforms);
    WGPUBindGroupLayoutDescriptor mask_layout_descriptor{};
    mask_layout_descriptor.label = ::progpu::native::webgpu::string_view("ProGPU native image mask layout");
    mask_layout_descriptor.entryCount = mask_entries.size();
    mask_layout_descriptor.entries = mask_entries.data();
    engine.image_mask_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &mask_layout_descriptor);
    if (engine.image_mask_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.image_mask_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = ::progpu::native::webgpu::string_view("ProGPU native masked image layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        ::progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(::progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint = ::progpu::native::webgpu::string_view("vs_main");
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
    fragment.entryPoint = ::progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = ::progpu::native::webgpu::string_view("ProGPU native retained masked image pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.image_mask_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    fragment.entryPoint = ::progpu::native::webgpu::string_view(
        "fs_main_color_matrix_unmasked");
    pipeline_descriptor.label = ::progpu::native::webgpu::string_view(
        "ProGPU native retained image color-matrix pipeline");
    engine.image_color_matrix_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    const std::array<WGPUBindGroupLayout, 4U> combined_layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.image_mask_layout,
        engine.image_mask_layout
    }};
    WGPUPipelineLayoutDescriptor combined_layout_descriptor{};
    combined_layout_descriptor.label =
        ::progpu::native::webgpu::string_view(
            "ProGPU native masked color-matrix image layout");
    combined_layout_descriptor.bindGroupLayoutCount =
        combined_layouts.size();
    combined_layout_descriptor.bindGroupLayouts =
        combined_layouts.data();
    WGPUPipelineLayout combined_layout =
        wgpuDeviceCreatePipelineLayout(
            engine.device,
            &combined_layout_descriptor);
    if (combined_layout != nullptr) {
        pipeline_descriptor.layout = combined_layout;
        fragment.entryPoint = ::progpu::native::webgpu::string_view(
            "fs_main_color_matrix");
        pipeline_descriptor.label =
            ::progpu::native::webgpu::string_view(
                "ProGPU native retained masked color-matrix image pipeline");
        engine.image_masked_color_matrix_pipeline =
            wgpuDeviceCreateRenderPipeline(
                engine.device,
                &pipeline_descriptor);
        wgpuPipelineLayoutRelease(combined_layout);
    }
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.image_mask_pipeline == nullptr ||
        engine.image_color_matrix_pipeline == nullptr ||
        engine.image_masked_color_matrix_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = ::progpu::native::webgpu::string_view("ProGPU native image mask sampling uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_mask_sampling_uniforms);
    engine.image_mask_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    return engine.image_mask_uniform_buffer != nullptr;
}

bool update_image_mask(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame,
    bool& uploaded_uniforms) {
    WGPUTextureView view = reinterpret_cast<WGPUTextureView>(
        frame.external_mask_view);
    const bool replace = engine.image_mask_view == nullptr ||
        engine.image_mask_view != view ||
        engine.image_mask_width != frame.mask_width ||
        engine.image_mask_height != frame.mask_height;
    if (replace) {
        ::progpu::native::webgpu::texture_view_add_ref(view);
        WGPUBindGroup nearest = create_image_mask_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native nearest image mask bind group");
        WGPUBindGroup linear = create_image_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native linear image mask bind group");
        if (nearest == nullptr || linear == nullptr) {
            if (linear != nullptr) wgpuBindGroupRelease(linear);
            if (nearest != nullptr) wgpuBindGroupRelease(nearest);
            wgpuTextureViewRelease(view);
            return false;
        }
        if (engine.image_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_mask_linear_bind_group);
        }
        if (engine.image_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_mask_nearest_bind_group);
        }
        if (engine.image_mask_view != nullptr) {
            wgpuTextureViewRelease(engine.image_mask_view);
        }
        engine.image_mask_view = view;
        engine.image_mask_nearest_bind_group = nearest;
        engine.image_mask_linear_bind_group = linear;
        engine.image_mask_width = frame.mask_width;
        engine.image_mask_height = frame.mask_height;
    }

    gpu_mask_sampling_uniforms uniforms{};
    uniforms.coordinate0[0] = frame.mask_destination_rect.x * frame.dpi_scale;
    uniforms.coordinate0[1] = frame.mask_destination_rect.y * frame.dpi_scale;
    uniforms.coordinate1[0] = 1.0F /
        (frame.mask_destination_rect.width * frame.dpi_scale);
    uniforms.coordinate1[1] = 1.0F /
        (frame.mask_destination_rect.height * frame.dpi_scale);
    uniforms.options[0] = 1.0F;
    uniforms.options[1] = 1.0F;
    if (!engine.image_mask_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_image_mask_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.image_mask_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_image_mask_uniforms = uniforms;
        engine.image_mask_uniform_cache_valid = true;
        uploaded_uniforms = true;
    }
    engine.image_mask_revision = frame.mask_revision;
    return true;
}

bool upload_image_texture(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame) {
    const bool external = (frame.source_flags &
        PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U;
    WGPUTextureView external_view = reinterpret_cast<WGPUTextureView>(
        frame.external_source_view);
    const bool source_missing = external
        ? engine.image_texture_view == nullptr
        : engine.image_texture == nullptr;
    const bool replace = source_missing ||
        engine.image_source_is_external != external ||
        engine.image_width != frame.image_width ||
        engine.image_height != frame.image_height ||
        (external && engine.image_texture_view != external_view);
    WGPUTexture texture = engine.image_texture;
    WGPUTextureView view = engine.image_texture_view;
    WGPUBindGroup nearest_group = engine.image_nearest_bind_group;
    WGPUBindGroup linear_group = engine.image_linear_bind_group;
    if (replace && external) {
        texture = nullptr;
        view = external_view;
        ::progpu::native::webgpu::texture_view_add_ref(view);
        nearest_group = create_image_texture_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native external nearest image bind group");
        linear_group = create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native external linear image bind group");
        if (nearest_group == nullptr || linear_group == nullptr) {
            if (linear_group != nullptr) {
                wgpuBindGroupRelease(linear_group);
            }
            if (nearest_group != nullptr) {
                wgpuBindGroupRelease(nearest_group);
            }
            wgpuTextureViewRelease(view);
            return false;
        }
    } else if (replace) {
        WGPUTextureDescriptor descriptor{};
        descriptor.label = ::progpu::native::webgpu::string_view("ProGPU native retained RGBA image");
        descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        descriptor.dimension = WGPUTextureDimension_2D;
        descriptor.size = {frame.image_width, frame.image_height, 1U};
        descriptor.format = WGPUTextureFormat_RGBA8Unorm;
        descriptor.mipLevelCount = 1U;
        descriptor.sampleCount = 1U;
        texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
        if (texture == nullptr) {
            return false;
        }
        view = wgpuTextureCreateView(texture, nullptr);
        if (view == nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
        nearest_group = create_image_texture_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native nearest image bind group");
        linear_group = create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native linear image bind group");
        if (nearest_group == nullptr || linear_group == nullptr) {
            if (linear_group != nullptr) {
                wgpuBindGroupRelease(linear_group);
            }
            if (nearest_group != nullptr) {
                wgpuBindGroupRelease(nearest_group);
            }
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
    }

    if (!external) {
        ::progpu::native::webgpu::image_copy_texture destination{};
        destination.texture = texture;
        destination.aspect = WGPUTextureAspect_All;
        ::progpu::native::webgpu::texture_data_layout layout{};
        layout.bytesPerRow = frame.row_bytes;
        layout.rowsPerImage = frame.image_height;
        const WGPUExtent3D extent{frame.image_width, frame.image_height, 1U};
        const std::size_t upload_bytes =
            static_cast<std::size_t>(frame.row_bytes) *
                (frame.image_height - 1U) +
            static_cast<std::size_t>(frame.image_width) * 4U;
        wgpuQueueWriteTexture(
            engine.queue,
            &destination,
            frame.rgba_pixels,
            upload_bytes,
            &layout,
            &extent);
    }

    if (replace) {
        if (engine.image_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_linear_bind_group);
        }
        if (engine.image_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_nearest_bind_group);
        }
        if (engine.image_texture_view != nullptr) {
            wgpuTextureViewRelease(engine.image_texture_view);
        }
        if (engine.image_texture != nullptr) {
            wgpuTextureDestroy(engine.image_texture);
            wgpuTextureRelease(engine.image_texture);
        }
        engine.image_texture = texture;
        engine.image_texture_view = view;
        engine.image_nearest_bind_group = nearest_group;
        engine.image_linear_bind_group = linear_group;
        engine.image_width = frame.image_width;
        engine.image_height = frame.image_height;
        engine.image_source_is_external = external;
    }
    engine.image_revision = frame.image_revision;
    ++engine.image_texture_generation;
    return true;
}


} // namespace progpu::native::execution
