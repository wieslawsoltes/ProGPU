#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "TextureGaussianBlurWgsl.generated.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_semantic_image_resources.hpp"
#include "progpu_webgpu_compat.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace webgpu = progpu::native::webgpu;
namespace generated = progpu::native::generated;

namespace {

constexpr std::uint32_t maximum_blur_radius = 96U;
constexpr std::uint32_t maximum_blur_pairs = 48U;

struct alignas(16) semantic_image_blur_uniforms {
    float axis[4]{};
    float red[4]{};
    float green[4]{};
    float blue[4]{};
    float options[4]{};
    float yuv_range[4]{};
    float yuv_red[4]{};
    float yuv_green[4]{};
    float yuv_blue[4]{};
    float taps[maximum_blur_pairs][4]{};
};

static_assert(sizeof(semantic_image_blur_uniforms) == 912U);

void set_identity_rows(semantic_image_blur_uniforms& uniforms) noexcept {
    uniforms.red[0] = 1.0F;
    uniforms.green[1] = 1.0F;
    uniforms.blue[2] = 1.0F;
}

void copy_row(float (&destination)[4], const float (&source)[4]) noexcept {
    std::copy_n(source, 4U, destination);
}

bool build_blur_uniforms(
    float sigma,
    float direction_x,
    float direction_y,
    const progpu_native_scene_image_effect& effect,
    bool decode_yuv,
    semantic_image_blur_uniforms& uniforms) noexcept {
    if (!std::isfinite(sigma) || sigma <= 0.01F || sigma > 32.0F) {
        return false;
    }
    const auto radius = static_cast<std::uint32_t>(std::min(
        static_cast<int>(maximum_blur_radius),
        static_cast<int>(std::ceil(3.0F * sigma))));
    std::array<float, maximum_blur_radius + 1U> weights{};
    const double denominator = 2.0 * sigma * sigma;
    weights[0] = 1.0F;
    double total = 1.0;
    for (std::uint32_t index = 1U; index <= radius; ++index) {
        weights[index] = static_cast<float>(std::exp(
            -static_cast<double>(index * index) / denominator));
        total += 2.0 * weights[index];
    }
    const float inverse_total = static_cast<float>(1.0 / total);
    for (std::uint32_t index = 0U; index <= radius; ++index) {
        weights[index] *= inverse_total;
    }

    uniforms = {};
    uniforms.axis[0] = direction_x;
    uniforms.axis[1] = direction_y;
    uniforms.axis[2] = weights[0];
    const std::uint32_t pair_count = (radius + 1U) / 2U;
    uniforms.axis[3] = static_cast<float>(pair_count);
    set_identity_rows(uniforms);
    uniforms.options[0] = decode_yuv ? 1.0F : 0.0F;
    if (decode_yuv) {
        copy_row(uniforms.yuv_range, effect.yuv_range);
        copy_row(uniforms.yuv_red, effect.yuv_red);
        copy_row(uniforms.yuv_green, effect.yuv_green);
        copy_row(uniforms.yuv_blue, effect.yuv_blue);
    }
    for (std::uint32_t pair = 0U; pair < pair_count; ++pair) {
        const std::uint32_t first = pair * 2U + 1U;
        const std::uint32_t second = first + 1U;
        const float first_weight = weights[first];
        const float second_weight = second <= radius
            ? weights[second]
            : 0.0F;
        const float combined = first_weight + second_weight;
        uniforms.taps[pair][0] = combined > 0.0F
            ? (first * first_weight + second * second_weight) / combined
            : static_cast<float>(first);
        uniforms.taps[pair][1] = combined;
        uniforms.taps[pair][2] = first_weight;
        uniforms.taps[pair][3] = second_weight;
    }
    return true;
}

WGPUBindGroup create_blur_bind_group(
    progpu_native_engine& engine,
    WGPUTextureView source,
    WGPUTextureView chroma,
    WGPUBuffer uniforms,
    const char* label) noexcept {
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, engine.image_linear_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, source},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, chroma},
        {nullptr, 3U, uniforms, 0U,
            sizeof(semantic_image_blur_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.layout = engine.semantic_image_blur_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_blur_pipeline(progpu_native_engine& engine) noexcept {
    if (engine.semantic_image_blur_pipeline != nullptr) {
        return true;
    }
    if (engine.semantic_image_blur_shader != nullptr ||
        engine.semantic_image_blur_layout != nullptr) {
        return false;
    }
    webgpu::wgsl_source wgsl(
        generated::texture_gaussian_blur_wgsl,
        generated::texture_gaussian_blur_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = webgpu::string_view(
        "ProGPU shared TextureGaussianBlur.wgsl");
    engine.semantic_image_blur_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.semantic_image_blur_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    for (std::uint32_t index = 1U; index <= 2U; ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Fragment;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    entries[3].binding = 3U;
    entries[3].visibility = WGPUShaderStage_Fragment;
    entries[3].buffer.type = WGPUBufferBindingType_Uniform;
    entries[3].buffer.minBindingSize = sizeof(semantic_image_blur_uniforms);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = webgpu::string_view(
        "ProGPU semantic image live-blur layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.semantic_image_blur_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.semantic_image_blur_layout == nullptr) {
        return false;
    }
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = webgpu::string_view(
        "ProGPU semantic image live-blur pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts =
        &engine.semantic_image_blur_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }
    WGPUColorTargetState target{};
    target.format = WGPUTextureFormat_RGBA8Unorm;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.semantic_image_blur_shader;
    fragment.entryPoint = webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = webgpu::string_view(
        "ProGPU semantic image live Gaussian blur");
    descriptor.layout = pipeline_layout;
    descriptor.vertex.module = engine.semantic_image_blur_shader;
    descriptor.vertex.entryPoint = webgpu::string_view("vs_main");
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    engine.semantic_image_blur_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return engine.semantic_image_blur_pipeline != nullptr;
}

bool encode_blur_pass(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    WGPUBindGroup bind_group,
    const char* label) noexcept {
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = target_view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {0.0, 0.0, 0.0, 0.0};
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = webgpu::string_view(label);
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &descriptor);
    if (pass == nullptr) {
        return false;
    }
    wgpuRenderPassEncoderSetPipeline(
        pass,
        engine.semantic_image_blur_pipeline);
    wgpuRenderPassEncoderSetBindGroup(pass, 0U, bind_group, 0U, nullptr);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return true;
}

} // namespace

namespace progpu::native::semantic {

void release_semantic_image_blur_resources(
    semantic_image_draw& draw) noexcept {
    if (draw.blur_vertical_bind_group != nullptr)
        wgpuBindGroupRelease(draw.blur_vertical_bind_group);
    if (draw.blur_horizontal_bind_group != nullptr)
        wgpuBindGroupRelease(draw.blur_horizontal_bind_group);
    if (draw.blur_vertical_uniform_buffer != nullptr) {
        wgpuBufferDestroy(draw.blur_vertical_uniform_buffer);
        wgpuBufferRelease(draw.blur_vertical_uniform_buffer);
    }
    if (draw.blur_horizontal_uniform_buffer != nullptr) {
        wgpuBufferDestroy(draw.blur_horizontal_uniform_buffer);
        wgpuBufferRelease(draw.blur_horizontal_uniform_buffer);
    }
    if (draw.blur_output_view != nullptr)
        wgpuTextureViewRelease(draw.blur_output_view);
    if (draw.blur_output_texture != nullptr) {
        wgpuTextureDestroy(draw.blur_output_texture);
        wgpuTextureRelease(draw.blur_output_texture);
    }
    if (draw.blur_intermediate_view != nullptr)
        wgpuTextureViewRelease(draw.blur_intermediate_view);
    if (draw.blur_intermediate_texture != nullptr) {
        wgpuTextureDestroy(draw.blur_intermediate_texture);
        wgpuTextureRelease(draw.blur_intermediate_texture);
    }
    draw.blur_vertical_bind_group = nullptr;
    draw.blur_horizontal_bind_group = nullptr;
    draw.blur_vertical_uniform_buffer = nullptr;
    draw.blur_horizontal_uniform_buffer = nullptr;
    draw.blur_output_view = nullptr;
    draw.blur_output_texture = nullptr;
    draw.blur_intermediate_view = nullptr;
    draw.blur_intermediate_texture = nullptr;
    draw.has_live_blur = false;
}

bool create_semantic_image_blur_resources(
    progpu_native_engine& engine,
    WGPUTextureView image_view,
    WGPUTextureView chroma_view,
    std::uint32_t width,
    std::uint32_t height,
    const progpu_native_scene_image_effect& effect,
    semantic_image_draw& draw) noexcept {
    if (image_view == nullptr || width == 0U || height == 0U ||
        effect.effects1[2] <= 0.01F ||
        !create_blur_pipeline(engine)) {
        return false;
    }
    semantic_image_blur_uniforms horizontal{};
    semantic_image_blur_uniforms vertical{};
    const bool decode_yuv = effect.flags0[0] > 0.5F;
    if (decode_yuv && chroma_view == nullptr) {
        return false;
    }
    if (!build_blur_uniforms(
            effect.effects1[2],
            1.0F / static_cast<float>(width),
            0.0F,
            effect,
            decode_yuv,
            horizontal) ||
        !build_blur_uniforms(
            effect.effects1[2],
            0.0F,
            1.0F / static_cast<float>(height),
            effect,
            false,
            vertical)) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_RenderAttachment;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {width, height, 1U};
    texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    texture_descriptor.label = webgpu::string_view(
        "ProGPU semantic image live-blur intermediate");
    draw.blur_intermediate_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    texture_descriptor.label = webgpu::string_view(
        "ProGPU semantic image live-blur output");
    draw.blur_output_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (draw.blur_intermediate_texture != nullptr) {
        draw.blur_intermediate_view = wgpuTextureCreateView(
            draw.blur_intermediate_texture,
            nullptr);
    }
    if (draw.blur_output_texture != nullptr) {
        draw.blur_output_view = wgpuTextureCreateView(
            draw.blur_output_texture,
            nullptr);
    }
    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(horizontal);
    buffer_descriptor.label = webgpu::string_view(
        "ProGPU semantic image horizontal live-blur uniforms");
    draw.blur_horizontal_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    buffer_descriptor.label = webgpu::string_view(
        "ProGPU semantic image vertical live-blur uniforms");
    draw.blur_vertical_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (draw.blur_intermediate_view == nullptr ||
        draw.blur_output_view == nullptr ||
        draw.blur_horizontal_uniform_buffer == nullptr ||
        draw.blur_vertical_uniform_buffer == nullptr) {
        release_semantic_image_blur_resources(draw);
        return false;
    }
    wgpuQueueWriteBuffer(
        engine.queue,
        draw.blur_horizontal_uniform_buffer,
        0U,
        &horizontal,
        sizeof(horizontal));
    wgpuQueueWriteBuffer(
        engine.queue,
        draw.blur_vertical_uniform_buffer,
        0U,
        &vertical,
        sizeof(vertical));
    draw.blur_horizontal_bind_group = create_blur_bind_group(
        engine,
        image_view,
        decode_yuv ? chroma_view : image_view,
        draw.blur_horizontal_uniform_buffer,
        "ProGPU semantic image horizontal live-blur binding");
    draw.blur_vertical_bind_group = create_blur_bind_group(
        engine,
        draw.blur_intermediate_view,
        draw.blur_intermediate_view,
        draw.blur_vertical_uniform_buffer,
        "ProGPU semantic image vertical live-blur binding");
    if (draw.blur_horizontal_bind_group == nullptr ||
        draw.blur_vertical_bind_group == nullptr) {
        release_semantic_image_blur_resources(draw);
        return false;
    }
    draw.has_live_blur = true;
    return true;
}

bool encode_semantic_image_blurs(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    semantic_image_page& page) noexcept {
    for (auto& draw : page.draws) {
        if (!draw.has_live_blur) {
            continue;
        }
        if (!encode_blur_pass(
                engine,
                encoder,
                draw.blur_intermediate_view,
                draw.blur_horizontal_bind_group,
                "ProGPU semantic image horizontal live-blur pass") ||
            !encode_blur_pass(
                engine,
                encoder,
                draw.blur_output_view,
                draw.blur_vertical_bind_group,
                "ProGPU semantic image vertical live-blur pass")) {
            return false;
        }
    }
    return true;
}

} // namespace progpu::native::semantic
