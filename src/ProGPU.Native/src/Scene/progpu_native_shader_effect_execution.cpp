#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_shader_effect_execution.hpp"
#include "progpu_native_shader_effect_uniforms.hpp"
#include "progpu_native_replay_execution.hpp"
#include "WpfBytecodeEffectWgsl.generated.hpp"
#include "WpfBytecodeSampleEffectWgsl.generated.hpp"
#include "WpfBytecodeAffineEffectWgsl.generated.hpp"

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
    std::array<float, 128U> constants;
    float extent[4];
    float sample_extent[4];
};
static_assert(sizeof(effect_uniforms) == 544U);
struct sample_effect_uniforms {
    effect_uniforms source;
    float quad_scale_offset[4];
    float output_lattice[4];
    float homogeneous[4];
    float physical_clip[4];
};
static_assert(sizeof(sample_effect_uniforms) == 608U);
struct affine_effect_uniforms {
    sample_effect_uniforms sample;
    float quad_cross[4];
};
static_assert(sizeof(affine_effect_uniforms) == 624U);

std::shared_ptr<semantic_shader_program> program_for(progpu_native_engine& engine,
    const progpu_native_scene_shader_effect& descriptor, std::span<const std::byte> bytecode,
    bool final_sample_program = false, bool source_vector_mask = false, bool affine_sample_program = false) {
    if (affine_sample_program && !final_sample_program) return {};
    if (source_vector_mask && (!final_sample_program || !create_layer_mask_resources(engine))) return {};
    for (const auto& program : engine.semantic_shader_programs) {
        if (program->source_sampler == descriptor.source_sampler && program->final_sample_program == final_sample_program &&
            program->affine_sample_program == affine_sample_program &&
            program->source_vector_mask == source_vector_mask &&
            program->target_format == (final_sample_program ? engine.target_format : WGPUTextureFormat_RGBA8Unorm) &&
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
    program->final_sample_program = final_sample_program;
    program->affine_sample_program = affine_sample_program;
    program->source_vector_mask = source_vector_mask;
    program->target_format = final_sample_program ? engine.target_format : WGPUTextureFormat_RGBA8Unorm;
    const auto* source_bytes = affine_sample_program ? generated::wpf_bytecode_affine_effect_wgsl :
        final_sample_program ? generated::wpf_bytecode_sample_effect_wgsl : generated::wpf_bytecode_effect_wgsl;
    const auto source_size = affine_sample_program ? generated::wpf_bytecode_affine_effect_wgsl_size :
        final_sample_program ? generated::wpf_bytecode_sample_effect_wgsl_size : generated::wpf_bytecode_effect_wgsl_size;
    std::string source(reinterpret_cast<const char*>(source_bytes), source_size);
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
    if (final_sample_program) entries[0].visibility |= WGPUShaderStage_Vertex;
    entries[0].buffer.type = WGPUBufferBindingType_Uniform;
    entries[0].buffer.minBindingSize = affine_sample_program ? sizeof(affine_effect_uniforms) :
        final_sample_program ? sizeof(sample_effect_uniforms) : sizeof(effect_uniforms);
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
    const std::array layouts{program->layout, engine.layer_mask_layout};
    pipeline_layout_descriptor.bindGroupLayoutCount = source_vector_mask ? 2U : 1U;
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    const auto pipeline_layout = wgpuDeviceCreatePipelineLayout(engine.device, &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) return {};
    WGPUColorTargetState target{};
    target.format = program->target_format;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUBlendState blend{};
    if (final_sample_program) {
        // Same premultiplied source-over contract as the existing retained
        // effect composite, now evaluated directly at destination samples.
        blend.color.operation = blend.alpha.operation = WGPUBlendOperation_Add;
        blend.color.srcFactor = blend.alpha.srcFactor = WGPUBlendFactor_One;
        blend.color.dstFactor = blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
        target.blend = &blend;
    }
    WGPUFragmentState fragment{};
    fragment.module = program->module;
    fragment.entryPoint = webgpu::string_view(source_vector_mask ? "fs_source_mask" : "fs_main");
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
    std::shared_ptr<semantic_picture_backing> sampler_picture,
    std::uint32_t derivative_register) {
    if (!shader_effect::validate(descriptor, bytecode) || width == 0U || height == 0U ||
        width > slot.width || height > slot.height || slot.view == nullptr) return {};
    if (sampler_picture && (sampler_picture->owner != &engine ||
        sampler_picture->view == nullptr || sampler_picture->descriptor.width == 0U ||
        sampler_picture->descriptor.height == 0U)) return {};
    effect_uniforms uniforms{};
    if (!shader_effect::prepare_constants(descriptor, derivative_register, width, height, uniforms.constants)) return {};
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
    uniforms.extent[0] = static_cast<float>(width); uniforms.extent[1] = static_cast<float>(height);
    uniforms.extent[2] = static_cast<float>(binding->sampler_picture ? binding->sampler_picture->descriptor.width : slot.width);
    uniforms.extent[3] = static_cast<float>(binding->sampler_picture ? binding->sampler_picture->descriptor.height : slot.height);
    uniforms.sample_extent[0] = binding->sampler_picture ? uniforms.extent[2] : uniforms.extent[0];
    uniforms.sample_extent[1] = binding->sampler_picture ? uniforms.extent[3] : uniforms.extent[1];
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

std::shared_ptr<semantic_shader_binding> create_semantic_sample_shader_binding(
    progpu_native_engine& engine, const progpu_native_scene_shader_effect& descriptor,
    std::span<const std::byte> bytecode, const shader_effect::sample_frame& frame,
    const shader_effect::sample_lattice& target,
    std::shared_ptr<semantic_picture_backing> source_picture,
    const progpu_native_scene_shader_sample_frame& source_frame,
    std::shared_ptr<semantic_picture_backing> input_picture, std::uint32_t derivative_register,
    bool source_vector_mask, const progpu_native_scene_shader_affine_frame* affine_frame) {
    if (!shader_effect::validate(descriptor, bytecode) || !source_picture || source_picture->owner != &engine ||
        source_picture->view == nullptr || source_picture->descriptor.width == 0U ||
        source_picture->descriptor.height == 0U || frame.capture.width == 0U || frame.capture.height == 0U ||
        frame.output.width == 0U || frame.output.height == 0U || frame.output.width > 16'384U || frame.output.height > 16'384U ||
        !shader_effect::finite_affine_matrix(frame.unit_to_device) || !shader_effect::finite_affine_matrix(frame.device_to_unit) ||
        frame.unit_to_device.w <= 0.0F || frame.affine != (affine_frame != nullptr) ||
        !(affine_frame ? shader_effect::validate_affine_frame(*affine_frame) : shader_effect::validate_sample_frame(source_frame)) ||
        !input_picture || input_picture->owner != &engine || input_picture->view == nullptr ||
        input_picture->descriptor.width != frame.capture.width || input_picture->descriptor.height != frame.capture.height ||
        (derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX && derivative_register >= 32U)) return {};
    shader_effect::sample_projection projection{};
    if (!(frame.affine ? shader_effect::project_affine_sample_frame(frame, target, projection) :
            shader_effect::project_sample_frame(frame, target, projection))) return {};
    auto program = program_for(engine, descriptor, bytecode, true, source_vector_mask, frame.affine);
    if (!program) return {};
    affine_effect_uniforms extended_uniforms{};
    auto& uniforms = extended_uniforms.sample;
    std::copy(std::begin(descriptor.constants), std::end(descriptor.constants), uniforms.source.constants.begin());
    if (derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX) {
        const auto offset = derivative_register * 4U;
        uniforms.source.constants[offset] = frame.device_to_unit.x;
        uniforms.source.constants[offset + 1U] = frame.affine ? frame.device_to_unit.xy : 0.0F;
        uniforms.source.constants[offset + 2U] = frame.affine ? frame.device_to_unit.yx : 0.0F;
        uniforms.source.constants[offset + 3U] = frame.device_to_unit.y;
    }
    uniforms.source.extent[0] = static_cast<float>(frame.capture.width);
    uniforms.source.extent[1] = static_cast<float>(frame.capture.height);
    uniforms.source.extent[2] = static_cast<float>(source_picture->descriptor.width);
    uniforms.source.extent[3] = static_cast<float>(source_picture->descriptor.height);
    uniforms.source.sample_extent[0] = uniforms.source.extent[2];
    uniforms.source.sample_extent[1] = uniforms.source.extent[3];
    uniforms.quad_scale_offset[0] = projection.unit_to_clip.x;
    uniforms.quad_scale_offset[1] = projection.unit_to_clip.y;
    uniforms.quad_scale_offset[2] = projection.unit_to_clip.tx;
    uniforms.quad_scale_offset[3] = projection.unit_to_clip.ty;
    uniforms.output_lattice[0] = static_cast<float>(target.x);
    uniforms.output_lattice[1] = static_cast<float>(target.y);
    uniforms.output_lattice[2] = static_cast<float>(target.width);
    uniforms.output_lattice[3] = static_cast<float>(target.height);
    uniforms.homogeneous[0] = projection.unit_to_clip.w;
    uniforms.homogeneous[1] = projection.reciprocal_width;
    uniforms.homogeneous[2] = projection.reciprocal_height;
    uniforms.physical_clip[0] = source_frame.clip_left; uniforms.physical_clip[1] = source_frame.clip_top;
    uniforms.physical_clip[2] = source_frame.clip_right; uniforms.physical_clip[3] = source_frame.clip_bottom;
    extended_uniforms.quad_cross[0] = projection.unit_to_clip.xy;
    extended_uniforms.quad_cross[1] = projection.unit_to_clip.yx;
    const auto uniform_size = frame.affine ? sizeof(extended_uniforms) : sizeof(uniforms);
    auto binding = std::make_shared<semantic_shader_binding>();
    binding->program = std::move(program); binding->sampler_picture = std::move(source_picture);
    binding->input_picture = std::move(input_picture);
    binding->width = target.width; binding->height = target.height;
    binding->final_sample_program = true;
    WGPUBufferDescriptor buffer{};
    buffer.label = webgpu::string_view("ProGPU retained final-device shader frame");
    buffer.size = uniform_size; buffer.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    binding->uniforms = wgpuDeviceCreateBuffer(engine.device, &buffer);
    if (binding->uniforms == nullptr) return {};
    wgpuQueueWriteBuffer(engine.queue, binding->uniforms, 0U, &extended_uniforms, uniform_size);
    std::array<WGPUBindGroupEntry, 3U> entries{};
    entries[0].binding = 0U; entries[0].buffer = binding->uniforms; entries[0].size = uniform_size;
    entries[1].binding = 1U; entries[1].sampler = descriptor.sampling_mode == 0U
        ? engine.image_nearest_sampler : engine.image_linear_sampler;
    entries[2].binding = 2U; entries[2].textureView = binding->sampler_picture->view;
    WGPUBindGroupDescriptor group{};
    group.layout = binding->program->layout; group.entryCount = entries.size(); group.entries = entries.data();
    binding->bind_group = wgpuDeviceCreateBindGroup(engine.device, &group);
    return binding->bind_group != nullptr ? binding : nullptr;
}

bool encode_semantic_shader_effect(progpu_native_engine&, WGPUCommandEncoder encoder,
    const semantic_shader_binding& binding, const semantic_layer_slot& slot, std::uint32_t& pass_count) {
    if (binding.final_sample_program || !binding.program || binding.program->pipeline == nullptr || binding.bind_group == nullptr ||
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

bool encode_semantic_sample_shader_draw(WGPURenderPassEncoder pass, const semantic_shader_binding& binding,
    WGPUBindGroup source_vector_mask) {
    if (pass == nullptr || !binding.final_sample_program || !binding.program ||
        binding.program->pipeline == nullptr || binding.bind_group == nullptr ||
        binding.width == 0U || binding.height == 0U ||
        binding.program->source_vector_mask != (source_vector_mask != nullptr)) return false;
    wgpuRenderPassEncoderSetPipeline(pass, binding.program->pipeline);
    wgpuRenderPassEncoderSetBindGroup(pass, 0U, binding.bind_group, 0U, nullptr);
    if (source_vector_mask != nullptr)
        wgpuRenderPassEncoderSetBindGroup(pass, 1U, source_vector_mask, 0U, nullptr);
    wgpuRenderPassEncoderSetViewport(pass, 0.0F, 0.0F, static_cast<float>(binding.width),
        static_cast<float>(binding.height), 0.0F, 1.0F);
    wgpuRenderPassEncoderSetScissorRect(pass, 0U, 0U, binding.width, binding.height);
    wgpuRenderPassEncoderDraw(pass, 6U, 1U, 0U, 0U);
    return true;
}
} // namespace progpu::native::execution
