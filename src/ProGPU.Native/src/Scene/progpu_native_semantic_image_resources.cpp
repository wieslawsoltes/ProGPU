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
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_semantic_image_resources.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <array>
#include <cstring>

namespace progpu::native::semantic {

WGPUSampler resolve_semantic_image_sampler(
    progpu_native_engine& engine,
    std::uint32_t sampling,
    std::uint32_t max_anisotropy,
    std::uint32_t image_flags) noexcept {
    semantic_image_sampler_options options{};
    if (!resolve_semantic_image_sampler_options(
            sampling, max_anisotropy, options)) {
        return nullptr;
    }
    const std::uint32_t address_u =
        (image_flags & PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_MASK) >>
        PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT;
    const std::uint32_t address_v =
        (image_flags & PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_MASK) >>
        PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT;
    if (address_u > PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT ||
        address_v > PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT) {
        return nullptr;
    }

    WGPUSampler* cache = nullptr;
    if (address_u == PROGPU_NATIVE_IMAGE_ADDRESS_CLAMP &&
        address_v == PROGPU_NATIVE_IMAGE_ADDRESS_CLAMP) {
        if (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST) {
            return engine.image_nearest_sampler;
        }
        if (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
            sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC ||
            sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT) {
            return engine.image_linear_sampler;
        }
        if (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP) {
            cache = options.max_anisotropy > 1U
                ? &engine.image_anisotropic_samplers[
                    options.max_anisotropy - 2U]
                : &engine.image_mipmap_sampler;
        } else {
            cache = &engine.image_filtered_samplers[
                sampling -
                    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_LINEAR_MIP_NEAREST];
        }
    } else {
        const std::size_t address_index =
            static_cast<std::size_t>(address_v * 3U + address_u);
        const std::size_t cache_index =
            (static_cast<std::size_t>(sampling) * 17U +
                options.max_anisotropy) * 9U + address_index;
        if (cache_index >= engine.image_addressed_samplers.size()) {
            return nullptr;
        }
        cache = &engine.image_addressed_samplers[cache_index];
    }
    if (*cache != nullptr) {
        return *cache;
    }

    const auto map_address = [](std::uint32_t mode) noexcept {
        switch (mode) {
            case PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT:
                return WGPUAddressMode_Repeat;
            case PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT:
                return WGPUAddressMode_MirrorRepeat;
            default:
                return WGPUAddressMode_ClampToEdge;
        }
    };

    WGPUSamplerDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic retained image sampling");
    descriptor.addressModeU = map_address(address_u);
    descriptor.addressModeV = map_address(address_v);
    descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    descriptor.magFilter = options.mag_linear
        ? WGPUFilterMode_Linear
        : WGPUFilterMode_Nearest;
    descriptor.minFilter = options.min_linear
        ? WGPUFilterMode_Linear
        : WGPUFilterMode_Nearest;
    descriptor.mipmapFilter = options.mip_linear
        ? WGPUMipmapFilterMode_Linear
        : WGPUMipmapFilterMode_Nearest;
    descriptor.lodMinClamp = 0.0F;
    descriptor.lodMaxClamp = 32.0F;
    descriptor.maxAnisotropy = options.max_anisotropy;
    *cache = wgpuDeviceCreateSampler(engine.device, &descriptor);
    return *cache;
}

bool create_semantic_image_color_matrix_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    const progpu_native_scene_image_color_matrix& matrix,
    WGPUBuffer& uniform_buffer,
    WGPUBindGroup& bind_group) noexcept {
    uniform_buffer = nullptr;
    bind_group = nullptr;
    if (image_view == nullptr || engine.image_mask_layout == nullptr ||
        engine.image_linear_sampler == nullptr) {
        return false;
    }

    gpu_mask_sampling_uniforms uniforms{};
    std::memcpy(uniforms.coordinate0, matrix.red, sizeof(matrix.red));
    std::memcpy(uniforms.coordinate1, matrix.green, sizeof(matrix.green));
    std::memcpy(uniforms.bounds, matrix.blue, sizeof(matrix.blue));
    std::memcpy(
        uniforms.corner_radii_x,
        matrix.alpha,
        sizeof(matrix.alpha));
    std::memcpy(
        uniforms.corner_radii_y,
        matrix.offset,
        sizeof(matrix.offset));
    uniforms.options[0] =
        (matrix.flags &
            PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA) != 0U
        ? 1.0F
        : 0.0F;

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = webgpu::string_view(
        "ProGPU semantic image color-matrix uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(uniforms);
    uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (uniform_buffer == nullptr) {
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        uniform_buffer,
        0U,
        &uniforms,
        sizeof(uniforms));

    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.image_linear_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, image_view},
        {nullptr, 2U, uniform_buffer, 0U,
            sizeof(uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image color-matrix bind group");
    descriptor.layout = engine.image_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    bind_group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);
    if (bind_group == nullptr) {
        wgpuBufferDestroy(uniform_buffer);
        wgpuBufferRelease(uniform_buffer);
        uniform_buffer = nullptr;
        return false;
    }
    return true;
}

} // namespace progpu::native::semantic
