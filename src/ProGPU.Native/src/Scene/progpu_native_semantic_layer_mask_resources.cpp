#include "progpu_native_semantic_layer_mask_resources.hpp"

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
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_replay.hpp"
#include "progpu_webgpu_compat.hpp"

#include <array>
#include <cmath>
#include <limits>

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

} // namespace progpu::native::execution
