#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_shader_effect_execution.hpp"
#include "WpfBytecodeEffectWgsl.generated.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <string_view>

semantic_shader_program::~semantic_shader_program() {
    if (pipeline != nullptr) wgpuRenderPipelineRelease(pipeline);
    if (module != nullptr) wgpuShaderModuleRelease(module);
    if (layout != nullptr) wgpuBindGroupLayoutRelease(layout);
}
semantic_shader_binding::~semantic_shader_binding() {
    if (bind_group != nullptr) wgpuBindGroupRelease(bind_group);
    if (uniforms != nullptr) { wgpuBufferDestroy(uniforms); wgpuBufferRelease(uniforms); }
}

namespace progpu::native::execution {
namespace {
constexpr std::size_t maximum_programs = 64U;
struct effect_uniforms {
    float constants[128];
    float extent[4];
};
static_assert(sizeof(effect_uniforms) == 528U);

std::shared_ptr<semantic_shader_program> program_for(progpu_native_engine& engine,
    const progpu_native_scene_shader_effect& descriptor, std::span<const std::byte> bytecode) {
    for (const auto& program : engine.semantic_shader_programs) {
        if (program->source_sampler == descriptor.source_sampler &&
            std::ranges::equal(program->bytecode, bytecode)) return program;
    }
    std::string body;
    if (!shader_effect::translate(bytecode, descriptor.source_sampler, &body)) return {};
    // Evict only cache-owned programs. Published and in-flight retained spans
    // keep their independent shared ownership; no generation aliases a hash.
    if (engine.semantic_shader_programs.size() == maximum_programs) {
        const auto unused = std::ranges::find_if(engine.semantic_shader_programs,
            [](const auto& program) { return program.use_count() == 1; });
        if (unused == engine.semantic_shader_programs.end()) return {};
        engine.semantic_shader_programs.erase(unused);
    }
    auto program = std::make_shared<semantic_shader_program>();
    program->bytecode.assign(bytecode.begin(), bytecode.end());
    program->source_sampler = descriptor.source_sampler;
    std::string source(reinterpret_cast<const char*>(generated::wpf_bytecode_effect_wgsl),
        generated::wpf_bytecode_effect_wgsl_size);
    constexpr std::string_view marker = "// PROGPU_VALIDATED_BYTECODE_BODY";
    const auto position = source.find(marker);
    if (position == std::string::npos) return {};
    source.replace(position, marker.size(), body);
    webgpu::wgsl_source wgsl(reinterpret_cast<const std::uint8_t*>(source.data()), source.size());
    WGPUShaderModuleDescriptor shader{};
    shader.nextInChain = wgsl.chain();
    shader.label = webgpu::string_view("ProGPU validated original WPF ps_2_0 program");
    program->module = wgpuDeviceCreateShaderModule(engine.device, &shader);
    if (program->module == nullptr) return {};
    std::array<WGPUBindGroupLayoutEntry, 3U> entries{};
    entries[0].binding = 0U; entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].buffer.type = WGPUBufferBindingType_Uniform;
    entries[0].buffer.minBindingSize = sizeof(effect_uniforms);
    entries[1].binding = 1U; entries[1].visibility = WGPUShaderStage_Fragment;
    entries[1].sampler.type = WGPUSamplerBindingType_Filtering;
    entries[2].binding = 2U; entries[2].visibility = WGPUShaderStage_Fragment;
    entries[2].texture.sampleType = WGPUTextureSampleType_Float;
    entries[2].texture.viewDimension = WGPUTextureViewDimension_2D;
    WGPUBindGroupLayoutDescriptor layout{};
    layout.entryCount = entries.size(); layout.entries = entries.data();
    program->layout = wgpuDeviceCreateBindGroupLayout(engine.device, &layout);
    if (program->layout == nullptr) return {};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &program->layout;
    const auto pipeline_layout = wgpuDeviceCreatePipelineLayout(engine.device, &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) return {};
    WGPUColorTargetState target{};
    target.format = WGPUTextureFormat_RGBA8Unorm;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = program->module; fragment.entryPoint = webgpu::string_view("fs_main");
    fragment.targetCount = 1U; fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline{};
    pipeline.label = webgpu::string_view("ProGPU owned WPF bytecode effect pipeline");
    pipeline.layout = pipeline_layout;
    pipeline.vertex.module = program->module;
    pipeline.vertex.entryPoint = webgpu::string_view("vs_main");
    pipeline.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline.primitive.cullMode = WGPUCullMode_None;
    pipeline.multisample.count = 1U; pipeline.multisample.mask = 0xFFFFFFFFU;
    pipeline.fragment = &fragment;
    program->pipeline = wgpuDeviceCreateRenderPipeline(engine.device, &pipeline);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (program->pipeline == nullptr) return {};
    engine.semantic_shader_programs.push_back(program);
    return program;
}
} // namespace

std::shared_ptr<semantic_shader_binding> create_semantic_shader_binding(
    progpu_native_engine& engine, const progpu_native_scene_shader_effect& descriptor,
    std::span<const std::byte> bytecode, const semantic_layer_slot& slot,
    std::uint32_t width, std::uint32_t height,
    std::shared_ptr<semantic_picture_backing> sampler_picture) {
    if (!shader_effect::validate(descriptor, bytecode) || width == 0U || height == 0U ||
        width > slot.width || height > slot.height || slot.view == nullptr) return {};
    if (sampler_picture && (sampler_picture->owner != &engine ||
        sampler_picture->view == nullptr || sampler_picture->descriptor.width != width ||
        sampler_picture->descriptor.height != height)) return {};
    auto program = program_for(engine, descriptor, bytecode);
    if (!program) return {};
    auto binding = std::make_shared<semantic_shader_binding>();
    binding->program = std::move(program);
    binding->sampler_picture = std::move(sampler_picture);
    binding->width = width; binding->height = height;
    WGPUBufferDescriptor buffer{};
    buffer.label = webgpu::string_view("ProGPU retained WPF bytecode constants and capture frame");
    buffer.size = sizeof(effect_uniforms);
    buffer.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    binding->uniforms = wgpuDeviceCreateBuffer(engine.device, &buffer);
    if (binding->uniforms == nullptr) return {};
    effect_uniforms uniforms{};
    std::memcpy(uniforms.constants, descriptor.constants, sizeof(uniforms.constants));
    uniforms.extent[0] = static_cast<float>(width); uniforms.extent[1] = static_cast<float>(height);
    uniforms.extent[2] = static_cast<float>(binding->sampler_picture ? width : slot.width);
    uniforms.extent[3] = static_cast<float>(binding->sampler_picture ? height : slot.height);
    wgpuQueueWriteBuffer(engine.queue, binding->uniforms, 0U, &uniforms, sizeof(uniforms));
    std::array<WGPUBindGroupEntry, 3U> entries{};
    entries[0].binding = 0U; entries[0].buffer = binding->uniforms; entries[0].size = sizeof(uniforms);
    entries[1].binding = 1U; entries[1].sampler = descriptor.sampling_mode == 0U
        ? engine.image_nearest_sampler : engine.image_linear_sampler;
    entries[2].binding = 2U;
    entries[2].textureView = binding->sampler_picture ? binding->sampler_picture->view : slot.view;
    WGPUBindGroupDescriptor group{};
    group.layout = binding->program->layout; group.entryCount = entries.size(); group.entries = entries.data();
    binding->bind_group = wgpuDeviceCreateBindGroup(engine.device, &group);
    return binding->bind_group != nullptr ? binding : nullptr;
}

bool encode_semantic_shader_effect(progpu_native_engine&, WGPUCommandEncoder encoder,
    const semantic_shader_binding& binding, const semantic_layer_slot& slot, std::uint32_t& pass_count) {
    if (!binding.program || binding.program->pipeline == nullptr || binding.bind_group == nullptr ||
        slot.effect_views[0] == nullptr || binding.width > slot.effect_width || binding.height > slot.effect_height)
        return false;
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = slot.effect_views[0];
    attachment.loadOp = WGPULoadOp_Clear; attachment.storeOp = WGPUStoreOp_Store;
    WGPURenderPassDescriptor descriptor{};
    descriptor.colorAttachmentCount = 1U; descriptor.colorAttachments = &attachment;
    const auto pass = wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
    if (pass == nullptr) return false;
    wgpuRenderPassEncoderSetPipeline(pass, binding.program->pipeline);
    wgpuRenderPassEncoderSetBindGroup(pass, 0U, binding.bind_group, 0U, nullptr);
    wgpuRenderPassEncoderSetViewport(pass, 0.0F, 0.0F, static_cast<float>(binding.width),
        static_cast<float>(binding.height), 0.0F, 1.0F);
    wgpuRenderPassEncoderSetScissorRect(pass, 0U, 0U, binding.width, binding.height);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass); wgpuRenderPassEncoderRelease(pass);
    ++pass_count;
    return true;
}
} // namespace progpu::native::execution
