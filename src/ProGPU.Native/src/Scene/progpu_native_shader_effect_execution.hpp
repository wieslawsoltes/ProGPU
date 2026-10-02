#pragma once

#include "progpu_native_shader_effect.hpp"
#include "progpu_native_shader_sample_frame.hpp"

#include <memory>
#include <vector>

struct progpu_native_engine;
struct semantic_layer_slot;
struct semantic_picture_backing;

// Declarations follow the selected provider's WebGPU header. Every handle is
// owned by the same engine/device; compiled spans retain bindings/programs.
struct semantic_shader_program {
    semantic_shader_program() = default;
    semantic_shader_program(const semantic_shader_program&) = delete;
    semantic_shader_program& operator=(const semantic_shader_program&) = delete;
    std::vector<std::byte> bytecode;
    std::uint32_t source_sampler = 0U;
    bool final_sample_program = false;
    WGPUTextureFormat target_format = WGPUTextureFormat_Undefined;
    WGPUBindGroupLayout layout = nullptr;
    WGPUShaderModule module = nullptr;
    WGPURenderPipeline pipeline = nullptr;
    ~semantic_shader_program();
};

struct semantic_shader_binding {
    semantic_shader_binding() = default;
    semantic_shader_binding(const semantic_shader_binding&) = delete;
    semantic_shader_binding& operator=(const semantic_shader_binding&) = delete;
    std::shared_ptr<semantic_shader_program> program;
    // The bind group and its full-RGBA sampler retain one engine-owned picture
    // through the same retained-span/submission lifetime as the effect itself.
    std::shared_ptr<semantic_picture_backing> sampler_picture;
    // V5 retains captured source even when an independent ImageBrush is sampled.
    std::shared_ptr<semantic_picture_backing> input_picture;
    WGPUBuffer uniforms = nullptr;
    WGPUBindGroup bind_group = nullptr;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    bool final_sample_program = false;
    ~semantic_shader_binding();
};

namespace progpu::native::execution {
std::shared_ptr<semantic_shader_binding> create_semantic_shader_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_shader_effect& descriptor,
    std::span<const std::byte> bytecode,
    const semantic_layer_slot& slot,
    std::uint32_t width, std::uint32_t height,
    std::shared_ptr<semantic_picture_backing> sampler_picture = {},
    std::uint32_t derivative_register = PROGPU_NATIVE_SCENE_NO_INDEX);

// The existing retained-picture path owns the complete scale-space input.
// Output uses a separate final device lattice, never a resized old shader result.
std::shared_ptr<semantic_shader_binding> create_semantic_sample_shader_binding(
    progpu_native_engine& engine, const progpu_native_scene_shader_effect& descriptor,
    std::span<const std::byte> bytecode,
    const shader_effect::sample_frame& frame,
    const shader_effect::sample_lattice& target,
    std::shared_ptr<semantic_picture_backing> source_picture,
    const progpu_native_scene_shader_sample_frame& source_frame,
    std::shared_ptr<semantic_picture_backing> input_picture,
    std::uint32_t derivative_register = PROGPU_NATIVE_SCENE_NO_INDEX);

bool encode_semantic_shader_effect(progpu_native_engine& engine,
    WGPUCommandEncoder encoder, const semantic_shader_binding& binding,
    const semantic_layer_slot& slot, std::uint32_t& pass_count);

// Executes only the v5 effect draw on the actual current parent target. The
// caller owns pass ordering/load state and restores its viewport afterward.
bool encode_semantic_sample_shader_draw(WGPURenderPassEncoder pass,
    const semantic_shader_binding& binding);
} // namespace progpu::native::execution
