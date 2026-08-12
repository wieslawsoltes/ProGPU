#include "progpu_native.h"
#include "progpu_native_geometry.hpp"
#include "VectorWgsl.generated.hpp"

#include <webgpu.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr std::uint64_t initial_vertex_buffer_size = 64U * 1024U;
constexpr std::uint64_t initial_index_buffer_size = 16U * 1024U;
constexpr float antialias_padding_pixels = 1.5F;

struct gpu_uniforms {
    float projection[16];
    float model_view_projection[16];
    float view[16];
    float canvas_size[2];
    float dpi_scale;
    float pad0;
    float render_origin[2];
    float pad1[2];
};

static_assert(sizeof(gpu_uniforms) == 224U);

WGPUTextureFormat texture_format(std::uint32_t value) noexcept {
    switch (value) {
        case PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM:
            return WGPUTextureFormat_RGBA8Unorm;
        case PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM:
            return WGPUTextureFormat_BGRA8Unorm;
        case PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM_SRGB:
            return WGPUTextureFormat_RGBA8UnormSrgb;
        case PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM_SRGB:
            return WGPUTextureFormat_BGRA8UnormSrgb;
        default:
            return WGPUTextureFormat_Undefined;
    }
}

void set_identity(float* matrix) noexcept {
    std::fill_n(matrix, 16U, 0.0F);
    matrix[0] = 1.0F;
    matrix[5] = 1.0F;
    matrix[10] = 1.0F;
    matrix[15] = 1.0F;
}

gpu_uniforms create_uniforms(
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale) noexcept {
    gpu_uniforms uniforms{};
    set_identity(uniforms.projection);
    set_identity(uniforms.model_view_projection);
    set_identity(uniforms.view);

    const float logical_width = static_cast<float>(width) / dpi_scale;
    const float logical_height = static_cast<float>(height) / dpi_scale;
    uniforms.projection[0] = 2.0F / logical_width;
    uniforms.projection[5] = -2.0F / logical_height;
    uniforms.projection[10] = -1.0F;
    uniforms.projection[12] = -1.0F;
    uniforms.projection[13] = 1.0F;
    uniforms.canvas_size[0] = logical_width;
    uniforms.canvas_size[1] = logical_height;
    uniforms.dpi_scale = dpi_scale;
    return uniforms;
}

} // namespace

struct progpu_native_engine {
    std::thread::id owner_thread;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUTextureFormat target_format = WGPUTextureFormat_Undefined;
    WGPUShaderModule shader = nullptr;
    WGPURenderPipeline pipeline = nullptr;
    WGPURenderPipeline analytic_pipeline = nullptr;
    WGPUBindGroupLayout uniform_layout = nullptr;
    WGPUBindGroupLayout analytic_uniform_layout = nullptr;
    WGPUBindGroupLayout analytic_atlas_layout = nullptr;
    WGPUBuffer uniform_buffer = nullptr;
    WGPUBindGroup uniform_bind_group = nullptr;
    WGPUBuffer analytic_uniform_buffer = nullptr;
    WGPUBuffer analytic_brush_buffer = nullptr;
    WGPUBuffer analytic_gradient_buffer = nullptr;
    WGPUBindGroup analytic_uniform_bind_group = nullptr;
    WGPUBindGroup analytic_atlas_bind_group = nullptr;
    WGPUSampler analytic_sentinel_sampler = nullptr;
    WGPUTexture analytic_sentinel_texture = nullptr;
    WGPUTextureView analytic_sentinel_texture_view = nullptr;
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    std::uint64_t vertex_buffer_size = 0;
    std::uint64_t index_buffer_size = 0;
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    std::string last_error;
    std::uint64_t submission_count = 0;

    ~progpu_native_engine() {
        if (index_buffer != nullptr) {
            wgpuBufferDestroy(index_buffer);
            wgpuBufferRelease(index_buffer);
        }
        if (vertex_buffer != nullptr) {
            wgpuBufferDestroy(vertex_buffer);
            wgpuBufferRelease(vertex_buffer);
        }
        if (uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(uniform_bind_group);
        }
        if (analytic_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(analytic_uniform_bind_group);
        }
        if (analytic_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(analytic_atlas_bind_group);
        }
        if (analytic_sentinel_texture_view != nullptr) {
            wgpuTextureViewRelease(analytic_sentinel_texture_view);
        }
        if (analytic_sentinel_texture != nullptr) {
            wgpuTextureDestroy(analytic_sentinel_texture);
            wgpuTextureRelease(analytic_sentinel_texture);
        }
        if (analytic_sentinel_sampler != nullptr) {
            wgpuSamplerRelease(analytic_sentinel_sampler);
        }
        if (analytic_gradient_buffer != nullptr) {
            wgpuBufferDestroy(analytic_gradient_buffer);
            wgpuBufferRelease(analytic_gradient_buffer);
        }
        if (analytic_brush_buffer != nullptr) {
            wgpuBufferDestroy(analytic_brush_buffer);
            wgpuBufferRelease(analytic_brush_buffer);
        }
        if (analytic_uniform_buffer != nullptr) {
            wgpuBufferDestroy(analytic_uniform_buffer);
            wgpuBufferRelease(analytic_uniform_buffer);
        }
        if (uniform_buffer != nullptr) {
            wgpuBufferDestroy(uniform_buffer);
            wgpuBufferRelease(uniform_buffer);
        }
        if (uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(uniform_layout);
        }
        if (analytic_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(analytic_uniform_layout);
        }
        if (analytic_atlas_layout != nullptr) {
            wgpuBindGroupLayoutRelease(analytic_atlas_layout);
        }
        if (analytic_pipeline != nullptr) {
            wgpuRenderPipelineRelease(analytic_pipeline);
        }
        if (pipeline != nullptr) {
            wgpuRenderPipelineRelease(pipeline);
        }
        if (shader != nullptr) {
            wgpuShaderModuleRelease(shader);
        }
        if (queue != nullptr) {
            wgpuQueueRelease(queue);
        }
        if (device != nullptr) {
            wgpuDeviceRelease(device);
        }
    }

    progpu_native_status fail(
        progpu_native_status status,
        const char* message) {
        last_error = message == nullptr ? "Unknown native renderer error." : message;
        return status;
    }

    bool is_owner_thread() const noexcept {
        return owner_thread == std::this_thread::get_id();
    }

    bool ensure_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= vertex_buffer_size && vertex_buffer != nullptr) {
            return true;
        }

        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }

        WGPUBufferDescriptor descriptor{};
        descriptor.label = "ProGPU native vector vertex buffer";
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }

        if (vertex_buffer != nullptr) {
            wgpuBufferDestroy(vertex_buffer);
            wgpuBufferRelease(vertex_buffer);
        }
        vertex_buffer = replacement;
        vertex_buffer_size = new_size;
        return true;
    }

    bool ensure_index_buffer(std::uint64_t required_size) {
        if (required_size <= index_buffer_size && index_buffer != nullptr) {
            return true;
        }

        std::uint64_t new_size = std::max(
            initial_index_buffer_size,
            index_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }

        WGPUBufferDescriptor descriptor{};
        descriptor.label = "ProGPU native vector index buffer";
        descriptor.usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }

        if (index_buffer != nullptr) {
            wgpuBufferDestroy(index_buffer);
            wgpuBufferRelease(index_buffer);
        }
        index_buffer = replacement;
        index_buffer_size = new_size;
        return true;
    }
};

namespace {

bool create_pipeline(progpu_native_engine& engine) {
    WGPUShaderModuleWGSLDescriptor wgsl{};
    wgsl.chain.sType = WGPUSType_ShaderModuleWGSLDescriptor;
    wgsl.code = reinterpret_cast<const char*>(
        progpu::native::generated::vector_wgsl);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = &wgsl.chain;
    shader_descriptor.label = "ProGPU shared Vector.wgsl";
    engine.shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.shader == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        {WGPUVertexFormat_Float32x2, 0U, 0U},
        {WGPUVertexFormat_Float32x4, 8U, 1U},
        {WGPUVertexFormat_Float32x2, 24U, 2U},
        {WGPUVertexFormat_Float32, 32U, 3U},
        {WGPUVertexFormat_Float32x2, 36U, 4U},
        {WGPUVertexFormat_Float32, 44U, 5U},
        {WGPUVertexFormat_Float32, 48U, 6U},
        {WGPUVertexFormat_Float32, 52U, 7U}
    }};
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint = "vs_solid_rect";
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
    fragment_state.entryPoint = "fs_solid_rect_main_unmasked";
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = "ProGPU native solid rectangle pipeline";
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment_state;
    engine.pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.pipeline == nullptr) {
        return false;
    }

    engine.uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.pipeline,
        0U);
    if (engine.uniform_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = "ProGPU native frame uniforms";
    uniform_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    if (engine.uniform_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor bind_group_descriptor{};
    bind_group_descriptor.label = "ProGPU native frame uniform bind group";
    bind_group_descriptor.layout = engine.uniform_layout;
    bind_group_descriptor.entryCount = 1U;
    bind_group_descriptor.entries = &uniform_entry;
    engine.uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_group_descriptor);
    return engine.uniform_bind_group != nullptr;
}

bool create_analytic_pipeline(progpu_native_engine& engine) {
    const std::array<WGPUVertexAttribute, 8U> attributes{{
        {WGPUVertexFormat_Float32x2, 0U, 0U},
        {WGPUVertexFormat_Float32x4, 8U, 1U},
        {WGPUVertexFormat_Float32x2, 24U, 2U},
        {WGPUVertexFormat_Float32, 32U, 3U},
        {WGPUVertexFormat_Float32x2, 36U, 4U},
        {WGPUVertexFormat_Float32, 44U, 5U},
        {WGPUVertexFormat_Float32, 48U, 6U},
        {WGPUVertexFormat_Float32, 52U, 7U}
    }};
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint = "vs_main";
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
    fragment_state.entryPoint = "fs_main_unmasked";
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = "ProGPU native analytic primitive pipeline";
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment_state;
    engine.analytic_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.analytic_pipeline == nullptr) {
        return false;
    }

    engine.analytic_uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.analytic_pipeline,
        0U);
    engine.analytic_atlas_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.analytic_pipeline,
        1U);
    if (engine.analytic_uniform_layout == nullptr ||
        engine.analytic_atlas_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = "ProGPU native analytic frame uniforms";
    uniform_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.analytic_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);

    WGPUBufferDescriptor brush_descriptor{};
    brush_descriptor.label = "ProGPU native analytic solid brush";
    brush_descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    brush_descriptor.size = 256U;
    engine.analytic_brush_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &brush_descriptor);

    WGPUBufferDescriptor gradient_descriptor{};
    gradient_descriptor.label = "ProGPU native analytic gradient sentinel";
    gradient_descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    gradient_descriptor.size = 32U;
    engine.analytic_gradient_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &gradient_descriptor);

    if (engine.analytic_uniform_buffer == nullptr ||
        engine.analytic_brush_buffer == nullptr ||
        engine.analytic_gradient_buffer == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = "ProGPU native analytic sentinel texture";
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {1U, 1U, 1U};
    texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.analytic_sentinel_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.analytic_sentinel_texture == nullptr) {
        return false;
    }
    engine.analytic_sentinel_texture_view = wgpuTextureCreateView(
        engine.analytic_sentinel_texture,
        nullptr);

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = "ProGPU native analytic sentinel sampler";
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Nearest;
    sampler_descriptor.minFilter = WGPUFilterMode_Nearest;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.analytic_sentinel_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.analytic_sentinel_texture_view == nullptr ||
        engine.analytic_sentinel_sampler == nullptr) {
        return false;
    }

    std::array<std::byte, 256U> solid_brush{};
    constexpr float opacity = 1.0F;
    std::memcpy(solid_brush.data() + 4U, &opacity, sizeof(opacity));
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.analytic_brush_buffer,
        0U,
        solid_brush.data(),
        solid_brush.size());
    std::array<std::byte, 32U> gradient_sentinel{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.analytic_gradient_buffer,
        0U,
        gradient_sentinel.data(),
        gradient_sentinel.size());

    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U, sizeof(gpu_uniforms),
            nullptr, nullptr},
        {nullptr, 1U, engine.analytic_brush_buffer, 0U, 256U,
            nullptr, nullptr},
        {nullptr, 2U, engine.analytic_gradient_buffer, 0U, 32U,
            nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor bind_group_descriptor{};
    bind_group_descriptor.label = "ProGPU native analytic bind group";
    bind_group_descriptor.layout = engine.analytic_uniform_layout;
    bind_group_descriptor.entryCount = entries.size();
    bind_group_descriptor.entries = entries.data();
    engine.analytic_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_group_descriptor);
    if (engine.analytic_uniform_bind_group == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupEntry, 2U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.analytic_sentinel_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_bind_group_descriptor{};
    atlas_bind_group_descriptor.label =
        "ProGPU native analytic atlas sentinel bind group";
    atlas_bind_group_descriptor.layout = engine.analytic_atlas_layout;
    atlas_bind_group_descriptor.entryCount = atlas_entries.size();
    atlas_bind_group_descriptor.entries = atlas_entries.data();
    engine.analytic_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_bind_group_descriptor);

    return engine.analytic_atlas_bind_group != nullptr;
}

void clear_metrics(progpu_native_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_analytic_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_analytic_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

} // namespace

extern "C" {

uint32_t progpu_native_get_abi_version(void) {
    return PROGPU_NATIVE_ABI_VERSION;
}

uint8_t progpu_native_get_info(progpu_native_engine_info* info) {
    if (info == nullptr || info->struct_size < sizeof(progpu_native_engine_info)) {
        return 0U;
    }
    *info = {};
    info->struct_size = sizeof(progpu_native_engine_info);
    info->abi_version = PROGPU_NATIVE_ABI_VERSION;
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
    info->capabilities =
        PROGPU_NATIVE_CAPABILITY_SOLID_RECT_BATCH |
        PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_TARGET |
        PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH |
        PROGPU_NATIVE_CAPABILITY_AFFINE_2D;
    constexpr char name[] = "ProGPU C++ core renderer / wgpu-native";
    std::memcpy(info->name, name, sizeof(name));
    return 1U;
}

progpu_native_status progpu_native_engine_create(
    const progpu_native_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_engine_options) ||
        options->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->backend_abi !=
            PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 ||
        options->device == 0U || options->queue == 0U ||
        texture_format(options->target_format) == WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    try {
        auto result = new progpu_native_engine();
        result->owner_thread = std::this_thread::get_id();
        result->device = reinterpret_cast<WGPUDevice>(options->device);
        result->queue = reinterpret_cast<WGPUQueue>(options->queue);
        result->target_format = texture_format(options->target_format);
        wgpuDeviceReference(result->device);
        wgpuQueueReference(result->queue);
        if (!create_pipeline(*result) ||
            !result->ensure_vertex_buffer(initial_vertex_buffer_size)) {
            result->last_error =
                "The shared vector shader or native WebGPU pipeline could not be created.";
            delete result;
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }
        *engine = result;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

void progpu_native_engine_destroy(progpu_native_engine* engine) {
    delete engine;
}

progpu_native_status progpu_native_engine_render(
    progpu_native_engine* engine,
    const progpu_native_frame* frame,
    progpu_native_frame_metrics* metrics) {
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < sizeof(progpu_native_frame) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->rect_count != 0U && frame->rects == nullptr) ||
        !std::isfinite(frame->clear_color.r) ||
        !std::isfinite(frame->clear_color.g) ||
        !std::isfinite(frame->clear_color.b) ||
        !std::isfinite(frame->clear_color.a)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The frame descriptor is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    if (frame->rect_count >
            std::numeric_limits<std::size_t>::max() / 6U ||
        frame->rect_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The rectangle batch is too large.");
    }

    try {
        engine->vertices.clear();
        engine->vertices.reserve(frame->rect_count * 6U);
        const float local_padding =
            antialias_padding_pixels / frame->dpi_scale;
        for (std::size_t index = 0; index < frame->rect_count; ++index) {
            if (!progpu::native::append_solid_rect(
                    frame->rects[index],
                    local_padding,
                    engine->vertices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A rectangle contains invalid geometry or color values.");
            }
        }
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native rectangle batch could not be allocated.");
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    if (!engine->ensure_vertex_buffer(vertex_bytes)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native WebGPU vertex buffer could not be allocated.");
    }

    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    wgpuQueueWriteBuffer(
        engine->queue,
        engine->uniform_buffer,
        0U,
        &uniforms,
        sizeof(uniforms));
    if (vertex_bytes != 0U) {
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = "ProGPU native frame encoder";
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native frame command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    color_attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = "ProGPU native solid rectangle pass";
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native render pass could not be created.");
    }

    if (!engine->vertices.empty()) {
        wgpuRenderPassEncoderSetPipeline(pass, engine->pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderDraw(
            pass,
            static_cast<std::uint32_t>(engine->vertices.size()),
            1U,
            0U,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);

    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = "ProGPU native frame commands";
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native frame command buffer could not be finished.");
    }

    wgpuQueueSubmit(engine->queue, 1U, &command);
    wgpuCommandBufferRelease(command);
    ++engine->submission_count;
    engine->last_error.clear();

    if (metrics != nullptr &&
        metrics->struct_size >= sizeof(progpu_native_frame_metrics)) {
        metrics->draw_call_count = engine->vertices.empty() ? 0U : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->vertex_upload_bytes = vertex_bytes;
        metrics->uniform_upload_bytes = sizeof(uniforms);
        metrics->submission_count = engine->submission_count;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_analytic(
    progpu_native_engine* engine,
    const progpu_native_analytic_frame* frame,
    progpu_native_analytic_frame_metrics* metrics) {
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < sizeof(progpu_native_analytic_frame) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->primitive_count != 0U && frame->primitives == nullptr) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The analytic frame descriptor is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    if (frame->primitive_count >
            std::numeric_limits<std::uint32_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The analytic primitive batch is too large.");
    }

    try {
        engine->vertices.clear();
        engine->indices.clear();
        engine->vertices.reserve(frame->primitive_count * 4U);
        engine->indices.reserve(frame->primitive_count * 6U);
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            float minimum_scale = 0.0F;
            if (!progpu::native::try_get_minimum_scale(
                    frame->primitives[index].transform,
                    minimum_scale)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "An analytic primitive has a non-invertible affine transform.");
            }
            const float local_padding =
                antialias_padding_pixels / minimum_scale;
            if (!progpu::native::append_analytic_primitive(
                    frame->primitives[index],
                    local_padding,
                    engine->vertices,
                    engine->indices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "An analytic primitive contains invalid geometry, color, or flags.");
            }
        }
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native analytic batch could not be allocated.");
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes =
        engine->indices.size() * sizeof(std::uint32_t);
    if (vertex_bytes != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native analytic WebGPU pipeline could not be created.");
        }
        if (!engine->ensure_vertex_buffer(vertex_bytes) ||
            !engine->ensure_index_buffer(index_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native analytic WebGPU buffers could not be allocated.");
        }

        const gpu_uniforms uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->analytic_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->index_buffer,
            0U,
            engine->indices.data(),
            static_cast<std::size_t>(index_bytes));
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = "ProGPU native analytic frame encoder";
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    color_attachment.view = reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = {
        frame->clear_color.r,
        frame->clear_color.g,
        frame->clear_color.b,
        frame->clear_color.a
    };
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = "ProGPU native indexed analytic primitive pass";
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic render pass could not be created.");
    }

    if (!engine->indices.empty()) {
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->analytic_uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            engine->analytic_atlas_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(engine->indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);

    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = "ProGPU native analytic frame commands";
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic command buffer could not be finished.");
    }

    wgpuQueueSubmit(engine->queue, 1U, &command);
    wgpuCommandBufferRelease(command);
    ++engine->submission_count;
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_analytic_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ? 0U : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->index_count =
            static_cast<std::uint32_t>(engine->indices.size());
        metrics->vertex_upload_bytes = vertex_bytes;
        metrics->index_upload_bytes = index_bytes;
        metrics->uniform_upload_bytes =
            engine->indices.empty() ? 0U : sizeof(gpu_uniforms);
        metrics->submission_count = engine->submission_count;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

size_t progpu_native_engine_get_last_error(
    const progpu_native_engine* engine,
    char* destination,
    size_t destination_size) {
    if (engine == nullptr) {
        return 0U;
    }
    const std::size_t required = engine->last_error.size() + 1U;
    if (destination != nullptr && destination_size != 0U) {
        const std::size_t copy_size = std::min(
            engine->last_error.size(),
            destination_size - 1U);
        std::memcpy(destination, engine->last_error.data(), copy_size);
        destination[copy_size] = '\0';
    }
    return required;
}

} // extern "C"
