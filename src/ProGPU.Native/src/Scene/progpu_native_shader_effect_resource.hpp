#pragma once

#include "progpu_native_shader_effect.hpp"
#include "progpu_native_shader_capture_frame.hpp"

#include <cstring>

namespace progpu::native::shader_effect {

// Wire versions normalize to one immutable program plus explicit optional
// source metadata. Caller outputs change only after complete validation.
inline bool read_resource(std::span<const std::byte> payload,
    std::span<const std::byte> bytecode,
    progpu_native_scene_shader_effect& program,
    std::uint32_t& sampler_resource_index,
    std::uint32_t& derivative_register,
    progpu_native_scene_shader_capture_frame& capture_frame) noexcept {
    progpu_native_scene_shader_effect candidate{};
    std::uint32_t picture = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t derivatives = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_shader_capture_frame frame{};
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
    } else if (payload.size() == sizeof(progpu_native_scene_shader_effect_derivatives)) {
        progpu_native_scene_shader_effect_derivatives source{};
        std::memcpy(&source, payload.data(), sizeof(source));
        if (source.struct_size != sizeof(source) || source.version != 3U || source.derivative_register >= 32U ||
            source.flags != 0U || source.reserved[0] != 0U || source.reserved[1] != 0U || source.reserved[2] != 0U)
            return false;
        candidate = source.program;
        picture = source.sampler_resource_index;
        derivatives = source.derivative_register;
    } else if (payload.size() == sizeof(progpu_native_scene_shader_effect_capture)) {
        progpu_native_scene_shader_effect_capture source{};
        std::memcpy(&source, payload.data(), sizeof(source));
        if (source.struct_size != sizeof(source) || source.version != 4U ||
            (source.derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX && source.derivative_register >= 32U) ||
            source.flags != 0U || source.reserved[0] != 0U || source.reserved[1] != 0U || source.reserved[2] != 0U ||
            !validate_capture_frame(source.frame)) return false;
        candidate = source.program;
        picture = source.sampler_resource_index;
        derivatives = source.derivative_register;
        frame = source.frame;
    } else return false;
    if (!validate(candidate, bytecode)) return false;
    program = candidate;
    sampler_resource_index = picture;
    derivative_register = derivatives;
    capture_frame = frame;
    return true;
}

// An older reader cannot silently discard explicit source-frame metadata.
inline bool read_resource(std::span<const std::byte> payload,
    std::span<const std::byte> bytecode, progpu_native_scene_shader_effect& program,
    std::uint32_t& sampler_resource_index, std::uint32_t& derivative_register) noexcept {
    progpu_native_scene_shader_effect candidate{};
    progpu_native_scene_shader_capture_frame frame{};
    std::uint32_t picture{}, derivatives{};
    if (!read_resource(payload, bytecode, candidate, picture, derivatives, frame) || frame.capture_width != 0U)
        return false;
    program = candidate;
    sampler_resource_index = picture;
    derivative_register = derivatives;
    return true;
}

// Original internal reader remains fail-closed when it cannot consume new
// derivative metadata; rejecting it must not publish either earlier output.
inline bool read_resource(std::span<const std::byte> payload,
    std::span<const std::byte> bytecode, progpu_native_scene_shader_effect& program,
    std::uint32_t& sampler_resource_index) noexcept {
    progpu_native_scene_shader_effect candidate{};
    std::uint32_t picture{}, derivatives{};
    if (!read_resource(payload, bytecode, candidate, picture, derivatives) ||
        derivatives != PROGPU_NATIVE_SCENE_NO_INDEX) return false;
    program = candidate;
    sampler_resource_index = picture;
    return true;
}
} // namespace progpu::native::shader_effect
