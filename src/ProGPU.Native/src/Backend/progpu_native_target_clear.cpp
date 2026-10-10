#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_target_clear.hpp"
#include "TargetClearWgsl.generated.hpp"

namespace progpu::native::execution {
namespace {

bool ensure_target_clear_pipeline(progpu_native_engine& engine) {
    auto& owned = engine.target_clear_pipelines;
    if (owned.pipeline != nullptr) return true;
    const auto create = [&]() {
        webgpu::wgsl_source source(generated::target_clear_wgsl, generated::target_clear_wgsl_size);
        WGPUShaderModuleDescriptor shader{};
        // The pinned Windows FXC path requires a nonempty source identity.
        shader.label = webgpu::string_view("ProGPU target-storage Clear shader");
        shader.nextInChain = source.chain();
        owned.shader = wgpuDeviceCreateShaderModule(engine.device, &shader);
        if (owned.shader == nullptr) return false;
        WGPUBindGroupLayoutEntry entry{};
        entry.binding = 0U;
        entry.visibility = WGPUShaderStage_Fragment;
        entry.buffer.type = WGPUBufferBindingType_Uniform;
        entry.buffer.minBindingSize = sizeof(progpu_native_color);
        WGPUBindGroupLayoutDescriptor layout{};
        layout.entryCount = 1U;
        layout.entries = &entry;
        owned.binding_layout = wgpuDeviceCreateBindGroupLayout(engine.device, &layout);
        if (owned.binding_layout == nullptr) return false;
        WGPUPipelineLayoutDescriptor pipeline_layout{};
        pipeline_layout.bindGroupLayoutCount = 1U;
        pipeline_layout.bindGroupLayouts = &owned.binding_layout;
        owned.pipeline_layout = wgpuDeviceCreatePipelineLayout(engine.device, &pipeline_layout);
        if (owned.pipeline_layout == nullptr) return false;
        WGPUColorTargetState target{};
        target.format = engine.target_format;
        target.writeMask = WGPUColorWriteMask_All;
        // No blend: transparent Clear must replace storage, not source-over it.
        WGPUFragmentState fragment{};
        fragment.module = owned.shader;
        fragment.entryPoint = webgpu::string_view("fs_target_clear");
        fragment.targetCount = 1U;
        fragment.targets = &target;
        WGPURenderPipelineDescriptor pipeline{};
        pipeline.label = webgpu::string_view("ProGPU target-storage Clear replace pipeline");
        pipeline.layout = owned.pipeline_layout;
        pipeline.vertex.module = owned.shader;
        pipeline.vertex.entryPoint = webgpu::string_view("vs_target_clear");
        pipeline.primitive.topology = WGPUPrimitiveTopology_TriangleList;
        pipeline.primitive.frontFace = WGPUFrontFace_CCW;
        pipeline.primitive.cullMode = WGPUCullMode_None;
        pipeline.multisample.count = 1U;
        pipeline.multisample.mask = 0xFFFFFFFFU;
        pipeline.fragment = &fragment;
        owned.pipeline = wgpuDeviceCreateRenderPipeline(engine.device, &pipeline);
        return owned.pipeline != nullptr;
    };
    if (create()) return true;
    owned.reset();
    return false;
}

} // namespace

progpu_native_status encode_target_clear(progpu_native_engine& engine,
    WGPUTextureView target, std::uint32_t width, std::uint32_t height,
    const semantic::scissor& scissor, const progpu_native_color& straight_color,
    bool ignores_alpha) {
    if (engine.semantic_encoder == nullptr || target == nullptr || width == 0U || height == 0U ||
        !scissor.drawable || scissor.width == 0U || scissor.height == 0U ||
        scissor.x >= width || scissor.y >= height || scissor.width > width - scissor.x ||
        scissor.height > height - scissor.y || !std::isfinite(straight_color.r) ||
        !std::isfinite(straight_color.g) || !std::isfinite(straight_color.b) ||
        !std::isfinite(straight_color.a) || straight_color.a < 0.0F || straight_color.a > 1.0F)
        return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "The actual target Clear frame is invalid.");
    if (!ensure_target_clear_pipeline(engine))
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "The source target Clear pipeline could not be created.");
    const float alpha = ignores_alpha ? 1.0F : straight_color.a;
    const progpu_native_color color{straight_color.r * alpha, straight_color.g * alpha,
        straight_color.b * alpha, alpha};
    progpu_native_engine::raster_resource_lease lease(engine, true);
    auto& resources = lease.get();
    WGPUBufferDescriptor storage{};
    storage.size = sizeof(color);
    storage.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    resources.uniforms = wgpuDeviceCreateBuffer(engine.device, &storage);
    if (resources.uniforms == nullptr)
        return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, "Source target Clear storage could not be retained.");
    wgpuQueueWriteBuffer(engine.queue, resources.uniforms, 0U, &color, sizeof(color));
    const WGPUBindGroupEntry entry{nullptr, 0U, resources.uniforms, 0U, sizeof(color), nullptr, nullptr};
    WGPUBindGroupDescriptor binding{};
    binding.layout = engine.target_clear_pipelines.binding_layout;
    binding.entryCount = 1U;
    binding.entries = &entry;
    resources.bind_group = wgpuDeviceCreateBindGroup(engine.device, &binding);
    if (resources.bind_group == nullptr)
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "Source target Clear binding could not be created.");
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = target;
    attachment.loadOp = WGPULoadOp_Load;
    attachment.storeOp = WGPUStoreOp_Store;
    WGPURenderPassDescriptor descriptor{};
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    auto pass = wgpuCommandEncoderBeginRenderPass(engine.semantic_encoder, &descriptor);
    if (pass == nullptr)
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "Source target Clear pass could not be created.");
    wgpuRenderPassEncoderSetPipeline(pass, engine.target_clear_pipelines.pipeline);
    wgpuRenderPassEncoderSetBindGroup(pass, 0U, resources.bind_group, 0U, nullptr);
    wgpuRenderPassEncoderSetViewport(pass, 0.0F, 0.0F, static_cast<float>(width), static_cast<float>(height), 0.0F, 1.0F);
    wgpuRenderPassEncoderSetScissorRect(pass, scissor.x, scissor.y, scissor.width, scissor.height);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution
