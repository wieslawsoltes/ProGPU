// Effects on the pixels of a layer (progpu_native_layer_effects.h): the parameters of a pass are
// packed into the block LayerEffects.wgsl reads and the pass is submitted on its own, so the
// queue orders it between the work that drew its inputs and the work that samples its output.

#include "progpu_native.h"
#include "progpu_native_layer_effects.h"
#include "LayerEffectsWgsl.generated.hpp"

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

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <new>
#include <vector>

namespace {

namespace webgpu = progpu::native::webgpu;

constexpr std::size_t block_vectors = 88U;
constexpr std::size_t light_start = 6U;
constexpr std::size_t light_vectors = 10U;
constexpr WGPUTextureFormat output_format = WGPUTextureFormat_RGBA8Unorm;

static_assert(sizeof(progpu_native_layer_color_matrix) == 88U);
static_assert(sizeof(progpu_native_layer_convolution) == 128U);
static_assert(sizeof(progpu_native_layer_composite) == 80U);
static_assert(sizeof(progpu_native_layer_light) == 120U);
static_assert(sizeof(progpu_native_layer_lighting) == 1032U);
static_assert(sizeof(progpu_native_layer_factor_multiply) == 32U);
static_assert(sizeof(progpu_native_layer_material_noise) == 28U);

using parameter_block = std::array<std::array<float, 4U>, block_vectors>;

enum class pass_kind : std::size_t {
    color_matrix,
    convolution,
    composite,
    lighting,
    factor_multiply,
    material_noise,
    count
};

constexpr std::array<const char*, static_cast<std::size_t>(pass_kind::count)> fragment_entries{
    "fs_color_matrix",
    "fs_convolution",
    "fs_composite",
    "fs_lighting",
    "fs_factor_multiply",
    "fs_material_noise"};

bool all_finite(float value) noexcept {
    return std::isfinite(value);
}

template <std::size_t Size>
bool all_finite(const float (&values)[Size]) noexcept {
    for (const float value : values) {
        if (!std::isfinite(value)) return false;
    }
    return true;
}

bool all_finite(const progpu_native_affine_2d& value) noexcept {
    return all_finite(value.m11) && all_finite(value.m12) && all_finite(value.m21) && all_finite(value.m22) &&
        all_finite(value.m31) && all_finite(value.m32);
}

// The rows x' = m11 x + m21 y + m31 and y' = m12 x + m22 y + m32.
void put_rows(parameter_block& block, std::size_t first, const progpu_native_affine_2d& value) noexcept {
    block[first] = {value.m11, value.m21, value.m31, 0.0F};
    block[first + 1U] = {value.m12, value.m22, value.m32, 0.0F};
}

bool valid_target(const progpu_native_layer_effect_target* target) noexcept {
    return target != nullptr && target->struct_size >= sizeof(*target) && target->width != 0U &&
        target->height != 0U && target->output_view != 0U;
}

// A render pipeline of a fragment entry point over a triangle that covers the target.
WGPURenderPipeline create_pipeline(WGPUDevice device, WGPUPipelineLayout layout, WGPUShaderModule shader,
    const char* vertex_entry, const char* fragment_entry) {
    WGPUColorTargetState color{};
    color.format = output_format;
    color.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = shader;
    fragment.entryPoint = webgpu::string_view(fragment_entry);
    fragment.targetCount = 1U;
    fragment.targets = &color;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = webgpu::string_view("ProGPU layer effect");
    descriptor.layout = layout;
    descriptor.vertex.module = shader;
    descriptor.vertex.entryPoint = webgpu::string_view(vertex_entry);
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    return wgpuDeviceCreateRenderPipeline(device, &descriptor);
}

WGPUBindGroupLayoutEntry uniform_entry(std::uint32_t binding, std::uint64_t size) noexcept {
    WGPUBindGroupLayoutEntry entry{};
    entry.binding = binding;
    entry.visibility = WGPUShaderStage_Fragment;
    entry.buffer.type = WGPUBufferBindingType_Uniform;
    entry.buffer.minBindingSize = size;
    return entry;
}

WGPUBindGroupLayoutEntry texture_entry(std::uint32_t binding) noexcept {
    WGPUBindGroupLayoutEntry entry{};
    entry.binding = binding;
    entry.visibility = WGPUShaderStage_Fragment;
    entry.texture.sampleType = WGPUTextureSampleType_UnfilterableFloat;
    entry.texture.viewDimension = WGPUTextureViewDimension_2D;
    return entry;
}

WGPUBindGroupEntry buffer_binding(std::uint32_t binding, WGPUBuffer buffer, std::uint64_t size) noexcept {
    WGPUBindGroupEntry entry{};
    entry.binding = binding;
    entry.buffer = buffer;
    entry.size = size;
    return entry;
}

WGPUBindGroupEntry texture_binding(std::uint32_t binding, WGPUTextureView view) noexcept {
    WGPUBindGroupEntry entry{};
    entry.binding = binding;
    entry.textureView = view;
    return entry;
}

WGPUBuffer create_uniform_buffer(WGPUDevice device, std::uint64_t size) {
    WGPUBufferDescriptor descriptor{};
    descriptor.label = webgpu::string_view("ProGPU layer effect parameters");
    descriptor.size = size;
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    return wgpuDeviceCreateBuffer(device, &descriptor);
}

// Binds, draws and submits one pass into the view of `target`.
bool submit_pass(progpu_native_engine& engine, WGPURenderPipeline pipeline, WGPUBindGroupLayout layout,
    const WGPUBindGroupEntry* entries, std::size_t entry_count, WGPUTextureView output) {
    WGPUBindGroupDescriptor binding{};
    binding.label = webgpu::string_view("ProGPU layer effect");
    binding.layout = layout;
    binding.entryCount = entry_count;
    binding.entries = entries;
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(engine.device, &binding);
    if (group == nullptr) return false;
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(engine.device, nullptr);
    if (encoder == nullptr) {
        wgpuBindGroupRelease(group);
        return false;
    }
    WGPURenderPassColorAttachment attachment{};
    webgpu::initialize_color_attachment(attachment);
    attachment.view = output;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 0.0};
    WGPURenderPassDescriptor descriptor{};
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
    bool submitted = false;
    if (pass != nullptr) {
        wgpuRenderPassEncoderSetPipeline(pass, pipeline);
        wgpuRenderPassEncoderSetBindGroup(pass, 0U, group, 0U, nullptr);
        wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
        WGPUCommandBuffer commands = wgpuCommandEncoderFinish(encoder, nullptr);
        if (commands != nullptr) {
            engine.submit(commands);
            wgpuCommandBufferRelease(commands);
            submitted = true;
        }
    }
    wgpuCommandEncoderRelease(encoder);
    wgpuBindGroupRelease(group);
    return submitted;
}

} // namespace

struct progpu_native_layer_effects final {
    progpu_native_engine* engine = nullptr;
    WGPUShaderModule shader = nullptr;
    WGPUBindGroupLayout layout = nullptr;
    WGPUPipelineLayout pipeline_layout = nullptr;
    std::array<WGPURenderPipeline, static_cast<std::size_t>(pass_kind::count)> pipelines{};
    WGPUBuffer uniforms = nullptr;
    // What an input a pass does not read is bound to.
    WGPUTexture empty_texture = nullptr;
    WGPUTextureView empty_view = nullptr;

    ~progpu_native_layer_effects() {
        const webgpu::dispatch_scope dispatch_scope(&engine->webgpu_dispatch);
        for (WGPURenderPipeline pipeline : pipelines) {
            if (pipeline != nullptr) wgpuRenderPipelineRelease(pipeline);
        }
        if (pipeline_layout != nullptr) wgpuPipelineLayoutRelease(pipeline_layout);
        if (layout != nullptr) wgpuBindGroupLayoutRelease(layout);
        if (shader != nullptr) wgpuShaderModuleRelease(shader);
        if (uniforms != nullptr) wgpuBufferRelease(uniforms);
        if (empty_view != nullptr) wgpuTextureViewRelease(empty_view);
        if (empty_texture != nullptr) wgpuTextureRelease(empty_texture);
    }

    bool initialize() {
        webgpu::wgsl_source source(
            progpu::native::generated::layer_effects_wgsl,
            progpu::native::generated::layer_effects_wgsl_size);
        WGPUShaderModuleDescriptor shader_descriptor{};
        shader_descriptor.nextInChain = source.chain();
        shader_descriptor.label = webgpu::string_view("ProGPU LayerEffects.wgsl");
        shader = wgpuDeviceCreateShaderModule(engine->device, &shader_descriptor);
        if (shader == nullptr) return false;
        const std::array entries{
            uniform_entry(0U, sizeof(parameter_block)),
            texture_entry(1U),
            texture_entry(2U)};
        WGPUBindGroupLayoutDescriptor bind_layout{};
        bind_layout.entryCount = entries.size();
        bind_layout.entries = entries.data();
        layout = wgpuDeviceCreateBindGroupLayout(engine->device, &bind_layout);
        if (layout == nullptr) return false;
        WGPUPipelineLayoutDescriptor pipeline{};
        pipeline.bindGroupLayoutCount = 1U;
        pipeline.bindGroupLayouts = &layout;
        pipeline_layout = wgpuDeviceCreatePipelineLayout(engine->device, &pipeline);
        if (pipeline_layout == nullptr) return false;
        uniforms = create_uniform_buffer(engine->device, sizeof(parameter_block));
        if (uniforms == nullptr) return false;
        WGPUTextureDescriptor texture{};
        texture.label = webgpu::string_view("ProGPU layer effect empty input");
        texture.usage = WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst;
        texture.dimension = WGPUTextureDimension_2D;
        texture.size = {1U, 1U, 1U};
        texture.format = output_format;
        texture.mipLevelCount = 1U;
        texture.sampleCount = 1U;
        empty_texture = wgpuDeviceCreateTexture(engine->device, &texture);
        if (empty_texture == nullptr) return false;
        empty_view = wgpuTextureCreateView(empty_texture, nullptr);
        return empty_view != nullptr;
    }

    WGPUTextureView input(std::uintptr_t view) const noexcept {
        return view != 0U ? reinterpret_cast<WGPUTextureView>(view) : empty_view;
    }

    progpu_native_status run(pass_kind kind, const progpu_native_layer_effect_target& target,
        const parameter_block& block) {
        auto& pipeline = pipelines[static_cast<std::size_t>(kind)];
        if (pipeline == nullptr) {
            pipeline = create_pipeline(engine->device, pipeline_layout, shader, "vs_layer_effect",
                fragment_entries[static_cast<std::size_t>(kind)]);
            if (pipeline == nullptr) {
                return engine->fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A layer effect pipeline could not be created.");
            }
        }
        wgpuQueueWriteBuffer(engine->queue, uniforms, 0U, block.data(), sizeof(block));
        const std::array entries{
            buffer_binding(0U, uniforms, sizeof(block)),
            texture_binding(1U, input(target.first_view)),
            texture_binding(2U, input(target.second_view))};
        if (!submit_pass(*engine, pipeline, layout, entries.data(), entries.size(),
                reinterpret_cast<WGPUTextureView>(target.output_view))) {
            return engine->fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "A layer effect pass could not be submitted.");
        }
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
};

struct progpu_native_layer_effect_program final {
    progpu_native_engine* engine = nullptr;
    WGPUShaderModule shader = nullptr;
    WGPUBindGroupLayout layout = nullptr;
    WGPUPipelineLayout pipeline_layout = nullptr;
    WGPURenderPipeline pipeline = nullptr;
    WGPUBuffer uniforms = nullptr;
    WGPUBuffer pass = nullptr;
    std::uint32_t uniform_size = 0U;
    std::uint32_t image_count = 0U;

    ~progpu_native_layer_effect_program() {
        const webgpu::dispatch_scope dispatch_scope(&engine->webgpu_dispatch);
        if (pipeline != nullptr) wgpuRenderPipelineRelease(pipeline);
        if (pipeline_layout != nullptr) wgpuPipelineLayoutRelease(pipeline_layout);
        if (layout != nullptr) wgpuBindGroupLayoutRelease(layout);
        if (shader != nullptr) wgpuShaderModuleRelease(shader);
        if (uniforms != nullptr) wgpuBufferRelease(uniforms);
        if (pass != nullptr) wgpuBufferRelease(pass);
    }
};

namespace {

// The checks every call of a pass starts with: success when the call may go on.
progpu_native_status check_call(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const void* parameters) {
    if (effects == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    progpu_native_engine& engine = *effects->engine;
    if (!engine.is_owner_thread()) {
        return engine.fail(PROGPU_NATIVE_STATUS_WRONG_THREAD, "Layer effects are owner-thread affine.");
    }
    if (engine.device_lost) {
        return engine.fail(PROGPU_NATIVE_STATUS_DEVICE_LOST, "The device of the layer effects was lost.");
    }
    if (!valid_target(target) || parameters == nullptr) {
        return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "A layer effect needs a target and parameters.");
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status refuse(progpu_native_layer_effects* effects, const char* message) {
    return effects->engine->fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, message);
}

} // namespace

extern "C" {

progpu_native_status progpu_native_layer_effects_create(
    progpu_native_engine* engine, progpu_native_layer_effects** effects) {
    if (engine == nullptr || effects == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *effects = nullptr;
    if (!engine->is_owner_thread()) {
        return engine->fail(PROGPU_NATIVE_STATUS_WRONG_THREAD, "Layer effects are owner-thread affine.");
    }
    if (engine->device_lost) {
        return engine->fail(PROGPU_NATIVE_STATUS_DEVICE_LOST, "The device of the layer effects was lost.");
    }
    const webgpu::dispatch_scope dispatch_scope(&engine->webgpu_dispatch);
    auto* created = new (std::nothrow) progpu_native_layer_effects();
    if (created == nullptr) {
        return engine->fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, "The layer effects could not be allocated.");
    }
    created->engine = engine;
    if (!created->initialize()) {
        delete created;
        return engine->fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "The layer effects could not be created.");
    }
    *effects = created;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void progpu_native_layer_effects_destroy(progpu_native_layer_effects* effects) {
    delete effects;
}

progpu_native_status progpu_native_layer_effects_color_matrix(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const progpu_native_layer_color_matrix* matrix) {
    const auto status = check_call(effects, target, matrix);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    if (matrix->struct_size < sizeof(*matrix) || target->first_view == 0U) {
        return refuse(effects, "A colour matrix needs a complete record and an input.");
    }
    parameter_block block{};
    const bool premultiplied = (matrix->flags & PROGPU_NATIVE_LAYER_COLOR_MATRIX_PREMULTIPLIED_INPUT) != 0U;
    block[0] = {static_cast<float>(target->width), static_cast<float>(target->height),
        premultiplied ? 1.0F : 0.0F, 0.0F};
    for (std::size_t row = 0U; row < 5U; ++row) {
        if (!all_finite(matrix->rows[row])) return refuse(effects, "A colour matrix must be finite.");
        for (std::size_t column = 0U; column < 4U; ++column) block[1U + row][column] = matrix->rows[row][column];
    }
    const webgpu::dispatch_scope dispatch_scope(&effects->engine->webgpu_dispatch);
    return effects->run(pass_kind::color_matrix, *target, block);
}

progpu_native_status progpu_native_layer_effects_convolution(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const progpu_native_layer_convolution* convolution) {
    const auto status = check_call(effects, target, convolution);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    if (convolution->struct_size < sizeof(*convolution) || target->first_view == 0U ||
        convolution->columns == 0U || convolution->rows == 0U ||
        convolution->columns > PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_SIDE ||
        convolution->rows > PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_SIDE ||
        !all_finite(convolution->gain) || !all_finite(convolution->bias) || !all_finite(convolution->elements)) {
        return refuse(effects, "A convolution needs a kernel of 1 to 5 by 1 to 5 finite elements.");
    }
    parameter_block block{};
    block[0] = {static_cast<float>(target->width), static_cast<float>(target->height),
        static_cast<float>(convolution->columns), static_cast<float>(convolution->rows)};
    block[1] = {convolution->gain, convolution->bias,
        (convolution->flags & PROGPU_NATIVE_LAYER_CONVOLUTION_KEEP_ALPHA) != 0U ? 1.0F : 0.0F,
        (convolution->flags & PROGPU_NATIVE_LAYER_CONVOLUTION_REPEAT_EDGES) != 0U ? 1.0F : 0.0F};
    for (std::size_t index = 0U; index < PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_ELEMENTS; ++index) {
        block[2U + index / 4U][index % 4U] = convolution->elements[index];
    }
    const webgpu::dispatch_scope dispatch_scope(&effects->engine->webgpu_dispatch);
    return effects->run(pass_kind::convolution, *target, block);
}

progpu_native_status progpu_native_layer_effects_composite(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const progpu_native_layer_composite* composite) {
    const auto status = check_call(effects, target, composite);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    if (composite->struct_size < sizeof(*composite) || target->first_view == 0U || target->second_view == 0U ||
        composite->operation > PROGPU_NATIVE_LAYER_COMPOSITE_MASK_INVERT || !all_finite(composite->k) ||
        !all_finite(composite->pixel_to_drawing) || !all_finite(composite->grid.x) || !all_finite(composite->grid.y) ||
        !all_finite(composite->grid.width) || !all_finite(composite->grid.height)) {
        return refuse(effects, "A composite needs two inputs and finite parameters.");
    }
    if (composite->operation == PROGPU_NATIVE_LAYER_COMPOSITE_BLEND &&
        composite->blend_mode == PROGPU_NATIVE_LAYER_BLEND_DISSOLVE &&
        (composite->grid_columns == 0U || composite->grid_rows == 0U || composite->grid.width <= 0.0F ||
            composite->grid.height <= 0.0F)) {
        return refuse(effects, "Dissolve needs a grid with an area.");
    }
    parameter_block block{};
    block[0] = {static_cast<float>(target->width), static_cast<float>(target->height),
        static_cast<float>(composite->operation), static_cast<float>(composite->blend_mode)};
    block[1] = {composite->k[0], composite->k[1], composite->k[2], composite->k[3]};
    block[2] = {composite->grid.x, composite->grid.y, composite->grid.width, composite->grid.height};
    block[3] = {static_cast<float>(composite->grid_columns), static_cast<float>(composite->grid_rows), 0.0F, 0.0F};
    put_rows(block, 4U, composite->pixel_to_drawing);
    const webgpu::dispatch_scope dispatch_scope(&effects->engine->webgpu_dispatch);
    return effects->run(pass_kind::composite, *target, block);
}

progpu_native_status progpu_native_layer_effects_lighting(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const progpu_native_layer_lighting* lighting) {
    const auto status = check_call(effects, target, lighting);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    const bool multiply = lighting->output == PROGPU_NATIVE_LAYER_LIGHTING_MULTIPLY;
    if (lighting->struct_size < sizeof(*lighting) || lighting->output > PROGPU_NATIVE_LAYER_LIGHTING_MULTIPLY ||
        (multiply && target->first_view == 0U) || lighting->light_count > PROGPU_NATIVE_LAYER_EFFECT_MAX_LIGHTS ||
        !all_finite(lighting->constant_light) || !all_finite(lighting->diffuse) || !all_finite(lighting->specular) ||
        !all_finite(lighting->shine) || !all_finite(lighting->max_factor) || !all_finite(lighting->pixel_to_drawing)) {
        return refuse(effects, "A lighting pass needs finite parameters and at most eight lights.");
    }
    parameter_block block{};
    block[0] = {static_cast<float>(target->width), static_cast<float>(target->height),
        multiply ? 1.0F : 0.0F, static_cast<float>(lighting->light_count)};
    block[1] = {lighting->constant_light[0], lighting->constant_light[1], lighting->constant_light[2],
        lighting->max_factor};
    block[2] = {0.0F, lighting->diffuse, lighting->specular, lighting->shine};
    put_rows(block, 3U, lighting->pixel_to_drawing);
    block[5] = {static_cast<float>(lighting->origin_x), static_cast<float>(lighting->origin_y), 0.0F, 0.0F};
    for (std::uint32_t index = 0U; index < lighting->light_count; ++index) {
        const auto& light = lighting->lights[index];
        if ((light.kind != PROGPU_NATIVE_LAYER_LIGHT_POINT && light.kind != PROGPU_NATIVE_LAYER_LIGHT_SPOT) ||
            !all_finite(light.cos_inner_cone) || !all_finite(light.cos_outer_cone) || !all_finite(light.color) ||
            !all_finite(light.outer_color) || !all_finite(light.position) || !all_finite(light.direction) ||
            !all_finite(light.attenuation_constant) || !all_finite(light.attenuation_linear) ||
            !all_finite(light.attenuation_quadratic) || !all_finite(light.min_cutoff) ||
            !all_finite(light.max_cutoff) ||
            !all_finite(light.to_light[0]) || !all_finite(light.to_light[1]) || !all_finite(light.to_light[2])) {
            return refuse(effects, "A light must be a point or a spot with finite parameters.");
        }
        const std::size_t at = light_start + index * light_vectors;
        block[at] = {static_cast<float>(light.kind), light.cos_inner_cone, light.cos_outer_cone, 0.0F};
        block[at + 1U] = {light.color[0], light.color[1], light.color[2], 0.0F};
        block[at + 2U] = {light.outer_color[0], light.outer_color[1], light.outer_color[2], 0.0F};
        block[at + 3U] = {light.position[0], light.position[1], light.position[2], 0.0F};
        block[at + 4U] = {light.direction[0], light.direction[1], light.direction[2], 0.0F};
        block[at + 5U] = {light.attenuation_constant, light.attenuation_linear, light.attenuation_quadratic,
            light.min_cutoff};
        block[at + 6U] = {light.max_cutoff, light.has_max_cutoff != 0U ? 1.0F : 0.0F, 0.0F, 0.0F};
        for (std::size_t row = 0U; row < 3U; ++row) {
            block[at + 7U + row] = {light.to_light[row][0], light.to_light[row][1], light.to_light[row][2], 0.0F};
        }
    }
    const webgpu::dispatch_scope dispatch_scope(&effects->engine->webgpu_dispatch);
    return effects->run(pass_kind::lighting, *target, block);
}

progpu_native_status progpu_native_layer_effects_factor_multiply(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const progpu_native_layer_factor_multiply* factors) {
    const auto status = check_call(effects, target, factors);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    if (factors->struct_size < sizeof(*factors) || target->first_view == 0U || target->second_view == 0U ||
        !all_finite(factors->scale) || !all_finite(factors->pixel_to_unit)) {
        return refuse(effects, "A factor multiply needs two inputs and finite parameters.");
    }
    parameter_block block{};
    block[0] = {static_cast<float>(target->width), static_cast<float>(target->height), factors->scale, 0.0F};
    put_rows(block, 1U, factors->pixel_to_unit);
    const webgpu::dispatch_scope dispatch_scope(&effects->engine->webgpu_dispatch);
    return effects->run(pass_kind::factor_multiply, *target, block);
}

progpu_native_status progpu_native_layer_effects_material_noise(progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target, const progpu_native_layer_material_noise* noise) {
    const auto status = check_call(effects, target, noise);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    if (noise->struct_size < sizeof(*noise) || !all_finite(noise->opacity) || noise->opacity < 0.0F ||
        noise->opacity > 1.0F || !all_finite(noise->color) || !all_finite(noise->origin_x) ||
        !all_finite(noise->origin_y)) {
        return refuse(effects, "Material noise needs finite parameters and an opacity of 0 to 1.");
    }
    parameter_block block{};
    block[0] = {static_cast<float>(target->width), static_cast<float>(target->height), noise->opacity, 0.0F};
    block[1] = {noise->color[0], noise->color[1], noise->color[2], 1.0F};
    block[2] = {noise->origin_x, noise->origin_y, 0.0F, 0.0F};
    const webgpu::dispatch_scope dispatch_scope(&effects->engine->webgpu_dispatch);
    return effects->run(pass_kind::material_noise, *target, block);
}

progpu_native_status progpu_native_layer_effects_create_program(progpu_native_layer_effects* effects,
    const char* wgsl, std::size_t wgsl_size, std::uint32_t uniform_size, std::uint32_t image_count,
    progpu_native_layer_effect_program** program) {
    if (effects == nullptr || program == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *program = nullptr;
    progpu_native_engine& engine = *effects->engine;
    if (!engine.is_owner_thread()) {
        return engine.fail(PROGPU_NATIVE_STATUS_WRONG_THREAD, "Layer effects are owner-thread affine.");
    }
    if (engine.device_lost) {
        return engine.fail(PROGPU_NATIVE_STATUS_DEVICE_LOST, "The device of the layer effects was lost.");
    }
    if (wgsl == nullptr || wgsl_size == 0U || uniform_size == 0U || (uniform_size % 16U) != 0U ||
        image_count > PROGPU_NATIVE_LAYER_EFFECT_MAX_PROGRAM_IMAGES) {
        return refuse(effects, "A program needs WGSL, a block in multiples of 16 bytes and at most eight images.");
    }
    const webgpu::dispatch_scope dispatch_scope(&engine.webgpu_dispatch);
    auto* created = new (std::nothrow) progpu_native_layer_effect_program();
    if (created == nullptr) {
        return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, "A layer effect program could not be allocated.");
    }
    created->engine = &engine;
    created->uniform_size = uniform_size;
    created->image_count = image_count;
    webgpu::wgsl_source source(reinterpret_cast<const std::uint8_t*>(wgsl), wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = source.chain();
    shader_descriptor.label = webgpu::string_view("ProGPU layer effect program");
    created->shader = wgpuDeviceCreateShaderModule(engine.device, &shader_descriptor);
    std::vector<WGPUBindGroupLayoutEntry> entries;
    bool made = false;
    try {
        entries.reserve(3U + image_count);
        entries.push_back(uniform_entry(0U, uniform_size));
        entries.push_back(uniform_entry(1U, 16U));
        for (std::uint32_t texture = 0U; texture <= image_count; ++texture) {
            entries.push_back(texture_entry(2U + texture));
        }
        made = true;
    } catch (const std::bad_alloc&) {
    }
    if (!made || created->shader == nullptr) {
        delete created;
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "A layer effect program could not be compiled.");
    }
    WGPUBindGroupLayoutDescriptor bind_layout{};
    bind_layout.entryCount = entries.size();
    bind_layout.entries = entries.data();
    created->layout = wgpuDeviceCreateBindGroupLayout(engine.device, &bind_layout);
    if (created->layout != nullptr) {
        WGPUPipelineLayoutDescriptor pipeline{};
        pipeline.bindGroupLayoutCount = 1U;
        pipeline.bindGroupLayouts = &created->layout;
        created->pipeline_layout = wgpuDeviceCreatePipelineLayout(engine.device, &pipeline);
    }
    if (created->pipeline_layout != nullptr) {
        created->pipeline = create_pipeline(
            engine.device, created->pipeline_layout, created->shader, "vs_main", "fs_main");
        created->uniforms = create_uniform_buffer(engine.device, uniform_size);
        created->pass = create_uniform_buffer(engine.device, 16U);
    }
    if (created->pipeline == nullptr || created->uniforms == nullptr || created->pass == nullptr) {
        delete created;
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "A layer effect program could not be created.");
    }
    *program = created;
    engine.last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void progpu_native_layer_effect_program_destroy(progpu_native_layer_effect_program* program) {
    delete program;
}

progpu_native_status progpu_native_layer_effects_run_program(progpu_native_layer_effects* effects,
    progpu_native_layer_effect_program* program, const progpu_native_layer_effect_target* target,
    const void* uniforms, std::size_t uniform_size, const float pass_block[4],
    const std::uintptr_t* image_views, std::uint32_t image_count) {
    const auto status = check_call(effects, target, uniforms);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
    if (program == nullptr || program->engine != effects->engine || target->first_view == 0U ||
        uniform_size != program->uniform_size || pass_block == nullptr || image_count != program->image_count ||
        (image_count != 0U && image_views == nullptr)) {
        return refuse(effects, "A program runs with its block, its content and its images.");
    }
    progpu_native_engine& engine = *effects->engine;
    const webgpu::dispatch_scope dispatch_scope(&engine.webgpu_dispatch);
    wgpuQueueWriteBuffer(engine.queue, program->uniforms, 0U, uniforms, uniform_size);
    wgpuQueueWriteBuffer(engine.queue, program->pass, 0U, pass_block, 16U);
    std::array<WGPUBindGroupEntry, 3U + PROGPU_NATIVE_LAYER_EFFECT_MAX_PROGRAM_IMAGES> entries{};
    entries[0] = buffer_binding(0U, program->uniforms, program->uniform_size);
    entries[1] = buffer_binding(1U, program->pass, 16U);
    entries[2] = texture_binding(2U, reinterpret_cast<WGPUTextureView>(target->first_view));
    for (std::uint32_t image = 0U; image < image_count; ++image) {
        if (image_views[image] == 0U) return refuse(effects, "An image of a program has no view.");
        entries[3U + image] = texture_binding(3U + image, reinterpret_cast<WGPUTextureView>(image_views[image]));
    }
    if (!submit_pass(engine, program->pipeline, program->layout, entries.data(), 3U + image_count,
            reinterpret_cast<WGPUTextureView>(target->output_view))) {
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "A layer effect program could not be submitted.");
    }
    engine.last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // extern "C"
