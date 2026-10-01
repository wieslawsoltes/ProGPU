#pragma once

#include "progpu_native_shader_effect.hpp"

#include <cstring>

namespace progpu::native::shader_effect {

// Both wire versions normalize to one immutable program plus a same-scene
// picture reference. Caller outputs change only after complete validation.
inline bool read_resource(std::span<const std::byte> payload,
    std::span<const std::byte> bytecode,
    progpu_native_scene_shader_effect& program,
    std::uint32_t& sampler_resource_index) noexcept {
    progpu_native_scene_shader_effect candidate{};
    std::uint32_t picture = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (payload.size() == sizeof(candidate)) {
        std::memcpy(&candidate, payload.data(), sizeof(candidate));
    } else if (payload.size() == sizeof(progpu_native_scene_shader_effect_picture)) {
        progpu_native_scene_shader_effect_picture source{};
        std::memcpy(&source, payload.data(), sizeof(source));
        if (source.struct_size != sizeof(source) || source.version != 2U ||
            source.reserved != 0U || source.sampler_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX)
            return false;
        candidate = source.program;
        picture = source.sampler_resource_index;
    } else return false;
    if (!validate(candidate, bytecode)) return false;
    program = candidate;
    sampler_resource_index = picture;
    return true;
}
} // namespace progpu::native::shader_effect
