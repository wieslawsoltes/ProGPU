#pragma once

#include "progpu_native_shader_sampler_gpu_reference.hpp"
#include <cmath>
#include <cstring>

namespace progpu::native::tests {

// Independent raw GPU observation, not a replacement oracle. Preserve every
// original scene pixel/counter assertion after reporting the numeric stages.
// The authored R8 ramp visits every byte at its exact texel center. A float
// attachment separates texture normalization from the UNORM blend operation.
template<class Readback, class Require>
void diagnose_unorm_mask_precision(sampler_reference_api api, WGPUDevice device,
    WGPUQueue queue, Readback readback, Require require) {
    static constexpr char shader[] = R"(
@group(0) @binding(0) var source: texture_2d<f32>;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var<uniform> parameters: vec4<f32>;
@vertex fn vs(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    if (index == 1u) { return vec4(3.0, -1.0, 0.0, 1.0); }
    if (index == 2u) { return vec4(-1.0, 3.0, 0.0, 1.0); }
    return vec4(-1.0, -1.0, 0.0, 1.0);
}
fn values(position: vec2<f32>) -> vec4<f32> {
    let sampled = textureSample(source, source_sampler, position / vec2(256.0, 1.0)).r;
    let loaded = textureLoad(source, vec2<i32>(position), 0).r;
    return vec4(sampled, loaded, sampled * parameters.x, loaded * parameters.x);
}
@fragment fn measure(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    return values(position.xy);
}
fn alpha(position: vec2<f32>) -> f32 {
    let observed = values(position);
    if (parameters.y == 1.0) { return observed.w; }
    if (parameters.y == 2.0) { return f32(u32(position.x)) / 255.0 * parameters.x; }
    return observed.z;
}
@fragment fn blend(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    return vec4(0.0, 0.0, 0.0, alpha(position.xy));
}
@fragment fn manual(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    return vec4(vec3(1.0 - alpha(position.xy)), 1.0);
}
)";
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
    const auto text = [](const char* value) { return WGPUStringView{value, std::strlen(value)}; };
    WGPUShaderSourceWGSL code{};
    code.chain.sType = WGPUSType_ShaderSourceWGSL;
#else
    const auto text = [](const char* value) { return value; };
    WGPUShaderModuleWGSLDescriptor code{};
    code.chain.sType = WGPUSType_ShaderModuleWGSLDescriptor;
#endif
    code.code = text(shader);
    WGPUShaderModuleDescriptor module_descriptor{};
    module_descriptor.label = text("ProGPU independent UNORM mask numeric diagnostic");
    module_descriptor.nextInChain = &code.chain;
    auto module = api.DeviceCreateShaderModule(device, &module_descriptor);
    require(module != nullptr, "UNORM diagnostic shader unavailable");
    WGPUTextureDescriptor texture{};
    texture.dimension = WGPUTextureDimension_2D;
    texture.size = {256U, 1U, 1U};
    texture.format = WGPUTextureFormat_R8Unorm;
    texture.mipLevelCount = texture.sampleCount = 1U;
    texture.usage = WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst;
    auto source = api.DeviceCreateTexture(device, &texture);
    require(source != nullptr, "UNORM diagnostic source unavailable");
    auto source_view = api.TextureCreateView(source, nullptr);
    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.addressModeU = sampler_descriptor.addressModeV =
        sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.minFilter = sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.maxAnisotropy = 1U;
    auto sampler = api.DeviceCreateSampler(device, &sampler_descriptor);
    WGPUBufferDescriptor buffer{};
    buffer.size = 16U;
    buffer.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    auto uniform = api.DeviceCreateBuffer(device, &buffer);
    buffer.size = 4096U;
    buffer.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst;
    auto staging = api.DeviceCreateBuffer(device, &buffer);
    require(source_view && sampler && uniform && staging, "UNORM diagnostic resources unavailable");
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
    WGPUTexelCopyTextureInfo upload{};
    WGPUTexelCopyBufferLayout pitch{};
#else
    WGPUImageCopyTexture upload{};
    WGPUTextureDataLayout pitch{};
#endif
    upload.texture = source;
    upload.aspect = WGPUTextureAspect_All;
    pitch.bytesPerRow = 256U;
    pitch.rowsPerImage = 1U;
    std::array<std::uint8_t, 256U> ramp{};
    for (std::size_t i = 0U; i < ramp.size(); ++i) ramp[i] = static_cast<std::uint8_t>(i);
    api.QueueWriteTexture(queue, &upload, ramp.data(), ramp.size(), &pitch, &texture.size);
    std::array<float, 4U> parameters{0.37F * 0.61F, 0, 0, 0};
    std::array<float, 1024U> observed{};
    std::array<std::array<std::uint8_t, 256U>, 4U> blended{};
    for (unsigned variant = 0U; variant < 5U; ++variant) {
        const bool floating = variant == 0U;
        parameters[1] = variant >= 1U && variant <= 3U ? static_cast<float>(variant - 1U) : 0.0F;
        api.QueueWriteBuffer(queue, uniform, 0U, parameters.data(), sizeof(parameters));
        texture.format = floating ? WGPUTextureFormat_RGBA32Float : WGPUTextureFormat_RGBA8Unorm;
        texture.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
        auto target = api.DeviceCreateTexture(device, &texture);
        require(target != nullptr, "UNORM diagnostic target unavailable");
        auto target_view = api.TextureCreateView(target, nullptr);
        WGPUBlendState blend{};
        blend.color = {WGPUBlendOperation_Add, WGPUBlendFactor_SrcAlpha, WGPUBlendFactor_OneMinusSrcAlpha};
        blend.alpha = {WGPUBlendOperation_Add, WGPUBlendFactor_Zero, WGPUBlendFactor_One};
        WGPUColorTargetState color{};
        color.format = texture.format;
        color.writeMask = WGPUColorWriteMask_All;
        color.blend = variant >= 1U && variant <= 3U ? &blend : nullptr;
        WGPUFragmentState fragment{};
        fragment.module = module;
        fragment.entryPoint = text(floating ? "measure" : variant == 4U ? "manual" : "blend");
        fragment.targetCount = 1U;
        fragment.targets = &color;
        WGPURenderPipelineDescriptor pipeline_descriptor{};
        pipeline_descriptor.label = text("ProGPU independent UNORM numeric pipeline");
        pipeline_descriptor.vertex.module = module;
        pipeline_descriptor.vertex.entryPoint = text("vs");
        pipeline_descriptor.fragment = &fragment;
        pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
        pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
        pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
        pipeline_descriptor.multisample.count = 1U;
        pipeline_descriptor.multisample.mask = UINT32_MAX;
        auto pipeline = api.DeviceCreateRenderPipeline(device, &pipeline_descriptor);
        require(pipeline != nullptr, "UNORM diagnostic pipeline unavailable");
        auto layout = api.RenderPipelineGetBindGroupLayout(pipeline, 0U);
        std::array<WGPUBindGroupEntry, 3U> entries{};
        entries[0].binding = 0U; entries[0].textureView = source_view;
        entries[1].binding = 1U; entries[1].sampler = sampler;
        entries[2].binding = 2U; entries[2].buffer = uniform; entries[2].size = 16U;
        WGPUBindGroupDescriptor group_descriptor{};
        group_descriptor.layout = layout;
        group_descriptor.entryCount = entries.size();
        group_descriptor.entries = entries.data();
        auto group = api.DeviceCreateBindGroup(device, &group_descriptor);
        auto encoder = api.DeviceCreateCommandEncoder(device, nullptr);
        require(target_view && layout && group && encoder, "UNORM diagnostic pass resources unavailable");
        WGPURenderPassColorAttachment attachment{};
        attachment.view = target_view;
        attachment.depthSlice = WGPU_DEPTH_SLICE_UNDEFINED;
        attachment.loadOp = WGPULoadOp_Clear;
        attachment.storeOp = WGPUStoreOp_Store;
        attachment.clearValue = {1, 1, 1, 1};
        WGPURenderPassDescriptor pass_descriptor{};
        pass_descriptor.colorAttachmentCount = 1U;
        pass_descriptor.colorAttachments = &attachment;
        auto pass = api.CommandEncoderBeginRenderPass(encoder, &pass_descriptor);
        require(pass != nullptr, "UNORM diagnostic render pass unavailable");
        api.RenderPassEncoderSetPipeline(pass, pipeline);
        api.RenderPassEncoderSetBindGroup(pass, 0U, group, 0U, nullptr);
        api.RenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
        api.RenderPassEncoderEnd(pass);
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
        WGPUTexelCopyTextureInfo copy_source{};
        WGPUTexelCopyBufferInfo copy_target{};
#else
        WGPUImageCopyTexture copy_source{};
        WGPUImageCopyBuffer copy_target{};
#endif
        copy_source.texture = target;
        copy_source.aspect = WGPUTextureAspect_All;
        copy_target.buffer = staging;
        copy_target.layout.bytesPerRow = floating ? 4096U : 1024U;
        copy_target.layout.rowsPerImage = 1U;
        api.CommandEncoderCopyTextureToBuffer(encoder, &copy_source, &copy_target, &texture.size);
        auto commands = api.CommandEncoderFinish(encoder, nullptr);
        require(commands != nullptr, "UNORM diagnostic command buffer unavailable");
        api.QueueSubmit(queue, 1U, &commands);
        const auto bytes = readback(staging, copy_target.layout.bytesPerRow);
        require(bytes.size() == copy_target.layout.bytesPerRow, "UNORM diagnostic readback size differs");
        if (floating) std::memcpy(observed.data(), bytes.data(), sizeof(observed));
        else for (std::size_t i = 0U; i < ramp.size(); ++i) blended[variant - 1U][i] = bytes[i * 4U];
        api.CommandBufferRelease(commands);
        api.RenderPassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.BindGroupRelease(group);
        api.BindGroupLayoutRelease(layout);
        api.RenderPipelineRelease(pipeline);
        api.TextureViewRelease(target_view);
        api.TextureRelease(target);
    }
    for (const unsigned value : {0U, 85U, 128U, 170U, 174U, 175U, 176U, 255U}) {
        std::fprintf(stderr, "UNORM mask numeric byte=%u alpha=%.9g sample=%.9g load=%.9g "
            "sampleAlpha=%.9g loadAlpha=%.9g blendSample=%u blendLoad=%u blendExact=%u manualSample=%u scalar=%u\n",
            value, parameters[0], observed[value * 4U], observed[value * 4U + 1U],
            observed[value * 4U + 2U], observed[value * 4U + 3U], blended[0][value],
            blended[1][value], blended[2][value], blended[3][value],
            static_cast<unsigned>(std::floor(255.0 - value * static_cast<double>(parameters[0]) + 0.5)));
    }
    std::fflush(stderr);
    api.BufferRelease(staging);
    api.BufferRelease(uniform);
    api.SamplerRelease(sampler);
    api.TextureViewRelease(source_view);
    api.TextureRelease(source);
    api.ShaderModuleRelease(module);
}

} // namespace progpu::native::tests
