#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>

namespace progpu::native::shader_effect {

// Original clean-room bounded D3D9 ps_2_0/ps_3_0 decoder. Validation is O(T) time and
// O(1) scratch for T tokens. Optional WGSL emission is O(T) owned output; it
// never executes a pixel, creates a device, or publishes partial shader text.
bool translate(
    std::span<const std::byte> bytecode,
    std::uint32_t source_sampler,
    std::string* wgsl_body = nullptr) noexcept;

bool validate(
    const progpu_native_scene_shader_effect& effect,
    std::span<const std::byte> bytecode) noexcept;

} // namespace progpu::native::shader_effect
