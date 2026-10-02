#pragma once

// Test-only, independent native filtering reference. Include after the selected
// provider's WebGPU header. No product shader, scene descriptor, address helper
// or captured product pixel contributes to this reference.
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdint>
#include <vector>

#define PROGPU_SAMPLER_REFERENCE_PROCS(X) \
    X(DeviceCreateTexture) X(TextureCreateView) X(TextureViewRelease) X(TextureRelease) \
    X(DeviceCreateBuffer) X(BufferRelease) X(DeviceCreateShaderModule) X(ShaderModuleRelease) \
    X(DeviceCreateRenderPipeline) X(RenderPipelineGetBindGroupLayout) X(RenderPipelineRelease) \
    X(DeviceCreateBindGroup) X(BindGroupRelease) X(BindGroupLayoutRelease) \
    X(DeviceCreateSampler) X(SamplerRelease) X(QueueWriteTexture) X(QueueWriteBuffer) \
    X(DeviceCreateCommandEncoder) X(CommandEncoderBeginRenderPass) \
    X(RenderPassEncoderSetPipeline) X(RenderPassEncoderSetBindGroup) X(RenderPassEncoderDraw) \
    X(RenderPassEncoderEnd) X(RenderPassEncoderRelease) X(CommandEncoderCopyTextureToBuffer) \
    X(CommandEncoderFinish) X(CommandEncoderRelease) X(CommandBufferRelease) X(QueueSubmit)

namespace progpu::native::tests {

struct sampler_reference_api final {
#define PROGPU_DECLARE_SAMPLER_PROC(name) WGPUProc##name name{};
    PROGPU_SAMPLER_REFERENCE_PROCS(PROGPU_DECLARE_SAMPLER_PROC)
#undef PROGPU_DECLARE_SAMPLER_PROC
};

class shader_sampler_gpu_reference final {
public:
    shader_sampler_gpu_reference(sampler_reference_api api, WGPUDevice device, WGPUQueue queue)
        : api_(api), device_(device), queue_(queue) {}
    shader_sampler_gpu_reference(const shader_sampler_gpu_reference&) = delete;
    shader_sampler_gpu_reference& operator=(const shader_sampler_gpu_reference&) = delete;

    ~shader_sampler_gpu_reference() {
        // render() has completed the real readback callback before returning;
        // no queued draw borrows these resources when the fixture releases them.
        for (auto sampler : samplers_) if (sampler) api_.SamplerRelease(sampler);
        for (auto view : source_views_) if (view) api_.TextureViewRelease(view);
        for (auto texture : sources_) if (texture) api_.TextureRelease(texture);
        if (target_view_) api_.TextureViewRelease(target_view_);
        if (target_) api_.TextureRelease(target_);
        if (uniform_) api_.BufferRelease(uniform_);
        if (readback_) api_.BufferRelease(readback_);
        if (layout_) api_.BindGroupLayoutRelease(layout_);
        if (pipeline_) api_.RenderPipelineRelease(pipeline_);
    }

    template<class Readback, class Require>
    std::vector<std::uint8_t> render(std::uint32_t variant, Readback readback, Require require) {
        require(variant >= 13U && variant <= 19U, "native sampler reference variant is unsupported");
        phase("raw render begin", variant);
        if (!pipeline_) initialize(require);
        const bool checker = variant >= 16U;
        const std::uint32_t source_height = checker ? 2U : 1U;
        // Original immutable input, authored independently from MIL packets.
        // Rows are red/green then green/red; variant 15 substitutes blue for red.
        std::array<std::uint8_t, 16U> pixels{
            255U, 0U, 0U, 255U, 0U, 255U, 0U, 255U,
            0U, 255U, 0U, 255U, 255U, 0U, 0U, 255U};
        if (variant == 15U) { pixels[0] = 0U; pixels[2] = 255U; }
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
        WGPUTexelCopyTextureInfo upload{};
        WGPUTexelCopyBufferLayout pitch{};
#else
        WGPUImageCopyTexture upload{};
        WGPUTextureDataLayout pitch{};
#endif
        upload.texture = sources_[checker ? 1U : 0U];
        upload.aspect = WGPUTextureAspect_All;
        pitch.bytesPerRow = 8U;
        pitch.rowsPerImage = source_height;
        const WGPUExtent3D source_extent{2U, source_height, 1U};
        phase("raw texture upload", variant);
        api_.QueueWriteTexture(queue_, &upload, pixels.data(), source_height * 8U, &pitch, &source_extent);
        const std::array<float, 4U> parameters{
            variant == 14U || variant == 19U ? 8.0F : 0.0F,
            variant == 15U ? 1.0F : 0.5F, 0.0F, 0.0F};
        api_.QueueWriteBuffer(queue_, uniform_, 0U, parameters.data(), sizeof(parameters));
        const bool mirror_u = variant == 16U || variant == 18U || variant == 19U;
        const bool mirror_v = variant == 17U || variant == 18U;
        std::array<WGPUBindGroupEntry, 3U> entries{};
        entries[0].binding = 0U; entries[0].textureView = source_views_[checker ? 1U : 0U];
        entries[1].binding = 1U; entries[1].sampler = samplers_[(mirror_u ? 1U : 0U) | (mirror_v ? 2U : 0U)];
        entries[2].binding = 2U; entries[2].buffer = uniform_; entries[2].size = sizeof(parameters);
        WGPUBindGroupDescriptor group_descriptor{};
        group_descriptor.layout = layout_;
        group_descriptor.entryCount = entries.size();
        group_descriptor.entries = entries.data();
        phase("raw bind group creation", variant);
        auto group = api_.DeviceCreateBindGroup(device_, &group_descriptor);
        require(group != nullptr, "native sampler reference bind group creation failed");
        phase("raw encoder creation", variant);
        auto encoder = api_.DeviceCreateCommandEncoder(device_, nullptr);
        require(encoder != nullptr, "native sampler reference encoder creation failed");
        WGPURenderPassColorAttachment color{};
        color.view = target_view_;
        color.depthSlice = WGPU_DEPTH_SLICE_UNDEFINED;
        color.loadOp = WGPULoadOp_Clear;
        color.storeOp = WGPUStoreOp_Store;
        color.clearValue = {0.0, 0.0, 0.0, 0.0};
        WGPURenderPassDescriptor pass_descriptor{};
        pass_descriptor.colorAttachmentCount = 1U;
        pass_descriptor.colorAttachments = &color;
        phase("raw pass begin", variant);
        auto pass = api_.CommandEncoderBeginRenderPass(encoder, &pass_descriptor);
        require(pass != nullptr, "native sampler reference pass creation failed");
        api_.RenderPassEncoderSetPipeline(pass, pipeline_);
        api_.RenderPassEncoderSetBindGroup(pass, 0U, group, 0U, nullptr);
        phase("raw draw", variant);
        api_.RenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
        api_.RenderPassEncoderEnd(pass);
        phase("raw pass ended", variant);
        auto result = read_target(encoder, readback, require);
        api_.RenderPassEncoderRelease(pass);
        api_.BindGroupRelease(group);
        phase("raw render complete", variant);
        return result;
    }

    template<class Draw, class Readback, class Require>
    std::vector<std::uint8_t> capture(Draw draw, Readback readback, Require require) {
        phase("product capture begin");
        if (!pipeline_) initialize(require);
        // Actual product capture replay uses the same *format*, not a converted
        // presentation surface. It cannot supply oracle inputs or shader state.
        draw(target_view_);
        phase("product capture submitted");
        auto encoder = api_.DeviceCreateCommandEncoder(device_, nullptr);
        require(encoder != nullptr, "sampler capture readback encoder unavailable");
        return read_target(encoder, readback, require);
    }

private:
    static void phase(const char* name, std::uint32_t ordinal = 0U) {
        std::fprintf(stderr, "Sampler raw GPU phase: %s (%u)\n", name, ordinal);
        std::fflush(stderr);
    }

    template<class Readback, class Require>
    std::vector<std::uint8_t> read_target(WGPUCommandEncoder encoder, Readback readback, Require require) {
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
        WGPUTexelCopyTextureInfo source{};
        WGPUTexelCopyBufferInfo destination{};
#else
        WGPUImageCopyTexture source{};
        WGPUImageCopyBuffer destination{};
#endif
        source.texture = target_;
        source.aspect = WGPUTextureAspect_All;
        destination.buffer = readback_;
        destination.layout.bytesPerRow = 256U;
        destination.layout.rowsPerImage = 24U;
        const WGPUExtent3D extent{32U, 24U, 1U};
        phase("readback copy encoding");
        api_.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
        phase("readback encoder finish");
        auto command = api_.CommandEncoderFinish(encoder, nullptr);
        require(command != nullptr, "native sampler reference command creation failed");
        phase("readback queue submit");
        api_.QueueSubmit(queue_, 1U, &command);
        phase("readback map begin");
        const auto mapped = readback(readback_, 256U * 24U);
        phase("readback map completed");
        require(mapped.size() == 256U * 24U, "native sampler reference readback size differs");
        std::vector<std::uint8_t> result(32U * 24U * 4U);
        for (std::size_t row = 0U; row < 24U; ++row)
            std::copy_n(mapped.data() + row * 256U, 128U, result.data() + row * 128U);
        api_.CommandBufferRelease(command);
        api_.CommandEncoderRelease(encoder);
        phase("readback handles released");
        return result;
    }
    template<class Require> void initialize(Require require) {
        // Fullscreen triangle and fragment-position UVs avoid production vertex
        // data/interpolation and all product address folding. Native Repeat or
        // MirrorRepeat alone owns the independent four-neighbour topology.
        static constexpr char shader[] = R"(
@group(0) @binding(0) var image: texture_2d<f32>;
@group(0) @binding(1) var image_sampler: sampler;
@group(0) @binding(2) var<uniform> parameters: vec4<f32>;
@vertex fn vs(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    if (index == 1u) { return vec4(3.0, -1.0, 0.0, 1.0); }
    if (index == 2u) { return vec4(-1.0, 3.0, 0.0, 1.0); }
    return vec4(-1.0, -1.0, 0.0, 1.0);
}
@fragment fn fs(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let uv = (position.xy - vec2(parameters.x, 0.0)) / vec2(16.0, 24.0);
    let sampled = textureSampleGrad(image, image_sampler, uv, vec2(1.0 / 16.0, 0.0), vec2(0.0, 1.0 / 24.0));
    let alpha = sampled.a * parameters.y;
    return vec4(sampled.rgb * alpha, alpha);
}
)";
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
        WGPUShaderSourceWGSL code{};
        code.chain.sType = WGPUSType_ShaderSourceWGSL;
        code.code = {shader, sizeof(shader) - 1U};
#else
        WGPUShaderModuleWGSLDescriptor code{};
        code.chain.sType = WGPUSType_ShaderModuleWGSLDescriptor;
        code.code = shader;
#endif
        WGPUShaderModuleDescriptor module_descriptor{};
        module_descriptor.nextInChain = &code.chain;
        // The pinned FXC bridge forwards the module label as a C source name;
        // its absent-label path instead forwards an empty Rust str pointer.
        // Retain an actual nonempty name without changing either shader stage.
        static constexpr char source_name[] = "ProGPU independent native sampler reference";
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
        module_descriptor.label = {source_name, sizeof(source_name) - 1U};
#else
        module_descriptor.label = source_name;
#endif
        phase("shader module creation");
        auto module = api_.DeviceCreateShaderModule(device_, &module_descriptor);
        require(module != nullptr, "native sampler reference shader creation failed");
        // Retained semantic image capture converts its straight source to
        // premultiplied fragment output, then uses ONE / ONE_MINUS_SRC_ALPHA.
        // This is deliberately not the separate straight-alpha direct-image API.
        WGPUBlendState blend{};
        blend.color = {WGPUBlendOperation_Add, WGPUBlendFactor_One, WGPUBlendFactor_OneMinusSrcAlpha};
        blend.alpha = {WGPUBlendOperation_Add, WGPUBlendFactor_One, WGPUBlendFactor_OneMinusSrcAlpha};
        WGPUColorTargetState color{};
        color.format = WGPUTextureFormat_RGBA8Unorm;
        color.blend = &blend;
        color.writeMask = WGPUColorWriteMask_All;
        WGPUFragmentState fragment{};
        fragment.module = module;
        fragment.targetCount = 1U;
        fragment.targets = &color;
        WGPURenderPipelineDescriptor pipeline{};
        pipeline.vertex.module = module;
#if defined(PROGPU_SAMPLER_REFERENCE_DAWN)
        fragment.entryPoint = {"fs", 2U};
        pipeline.vertex.entryPoint = {"vs", 2U};
#else
        fragment.entryPoint = "fs";
        pipeline.vertex.entryPoint = "vs";
#endif
        pipeline.fragment = &fragment;
        pipeline.primitive.topology = WGPUPrimitiveTopology_TriangleList;
        pipeline.primitive.frontFace = WGPUFrontFace_CCW;
        pipeline.primitive.cullMode = WGPUCullMode_None;
        pipeline.multisample.count = 1U;
        pipeline.multisample.mask = UINT32_MAX;
        phase("render pipeline creation");
        pipeline_ = api_.DeviceCreateRenderPipeline(device_, &pipeline);
        phase("render pipeline returned");
        api_.ShaderModuleRelease(module);
        require(pipeline_ != nullptr, "native sampler reference pipeline creation failed");
        phase("pipeline bind group layout acquisition");
        layout_ = api_.RenderPipelineGetBindGroupLayout(pipeline_, 0U);
        require(layout_ != nullptr, "native sampler reference layout unavailable");
        WGPUTextureDescriptor texture{};
        texture.dimension = WGPUTextureDimension_2D;
        texture.size = {32U, 24U, 1U};
        texture.format = WGPUTextureFormat_RGBA8Unorm;
        texture.mipLevelCount = 1U;
        texture.sampleCount = 1U;
        texture.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
        phase("target texture creation");
        target_ = api_.DeviceCreateTexture(device_, &texture);
        require(target_ != nullptr, "native sampler reference target creation failed");
        phase("target view creation");
        target_view_ = api_.TextureCreateView(target_, nullptr);
        require(target_view_ != nullptr, "native sampler reference target view creation failed");
        texture.usage = WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst;
        for (std::uint32_t i = 0U; i < sources_.size(); ++i) {
            texture.size = {2U, i + 1U, 1U};
            phase("source texture creation", i);
            sources_[i] = api_.DeviceCreateTexture(device_, &texture);
            require(sources_[i] != nullptr, "native sampler reference source creation failed");
            phase("source view creation", i);
            source_views_[i] = api_.TextureCreateView(sources_[i], nullptr);
            require(source_views_[i] != nullptr, "native sampler reference source view creation failed");
        }
        WGPUBufferDescriptor buffer{};
        buffer.size = 16U;
        buffer.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
        phase("uniform buffer creation");
        uniform_ = api_.DeviceCreateBuffer(device_, &buffer);
        buffer.size = 256U * 24U;
        buffer.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst;
        phase("readback buffer creation");
        readback_ = api_.DeviceCreateBuffer(device_, &buffer);
        require(uniform_ != nullptr && readback_ != nullptr, "native sampler reference buffers unavailable");
        for (std::uint32_t i = 0U; i < samplers_.size(); ++i) {
            WGPUSamplerDescriptor sampler{};
            sampler.addressModeU = (i & 1U) != 0U ? WGPUAddressMode_MirrorRepeat : WGPUAddressMode_Repeat;
            sampler.addressModeV = (i & 2U) != 0U ? WGPUAddressMode_MirrorRepeat : WGPUAddressMode_Repeat;
            sampler.addressModeW = WGPUAddressMode_ClampToEdge;
            sampler.magFilter = WGPUFilterMode_Linear;
            sampler.minFilter = WGPUFilterMode_Linear;
            sampler.mipmapFilter = WGPUMipmapFilterMode_Nearest;
            sampler.lodMaxClamp = 32.0F;
            sampler.maxAnisotropy = 1U;
            phase("sampler creation", i);
            samplers_[i] = api_.DeviceCreateSampler(device_, &sampler);
            require(samplers_[i] != nullptr, "native sampler reference sampler creation failed");
        }
        phase("initialization complete");
    }

    sampler_reference_api api_;
    WGPUDevice device_;
    WGPUQueue queue_;
    WGPURenderPipeline pipeline_{};
    WGPUBindGroupLayout layout_{};
    WGPUTexture target_{};
    WGPUTextureView target_view_{};
    std::array<WGPUTexture, 2U> sources_{};
    std::array<WGPUTextureView, 2U> source_views_{};
    std::array<WGPUSampler, 4U> samplers_{};
    WGPUBuffer uniform_{};
    WGPUBuffer readback_{};
};
} // namespace progpu::native::tests
