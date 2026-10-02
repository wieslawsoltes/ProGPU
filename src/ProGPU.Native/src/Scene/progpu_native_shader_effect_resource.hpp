#pragma once

#include "progpu_native_shader_effect.hpp"
#include "progpu_native_shader_capture_frame.hpp"
#include "progpu_native_shader_sample_frame.hpp"
#include "progpu_native_shader_affine_frame.hpp"

#include <cstring>

namespace progpu::native::shader_effect {

// Only used by traversals after complete scene validation. The target cursor
// consumes the signed physical output directly, not a logical-DPI reconstruction.
inline bool layer_output_frame(const std::byte* bytes, const progpu_native_scene_layer& layer,
    progpu_native_scene_shader_sample_frame& output) noexcept {
    if (layer.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) return false;
    progpu_native_scene_header header{};
    std::memcpy(&header, bytes, sizeof(header));
    if (layer.effect_resource_index >= header.resource_count) return false;
    progpu_native_scene_resource resource{};
    std::memcpy(&resource, bytes + header.resource_offset +
        static_cast<std::size_t>(layer.effect_resource_index) * header.resource_stride, sizeof(resource));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_WPF_SHADER_EFFECT) return false;
    // Only target allocation/physical clip consumers use this common prefix;
    // a shader executor must use the full version-aware reader below.
    if (resource.payload_size == sizeof(progpu_native_scene_shader_effect_affine)) {
        progpu_native_scene_shader_effect_affine source{};
        std::memcpy(&source, bytes + resource.payload_offset, sizeof(source));
        if (source.struct_size != sizeof(source) || source.version != 6U) return false;
        output = source.frame.placement;
        return true;
    }
    if (resource.payload_size != sizeof(progpu_native_scene_shader_effect_samples)) return false;
    progpu_native_scene_shader_effect_samples source{};
    std::memcpy(&source, bytes + resource.payload_offset, sizeof(source));
    if (source.struct_size != sizeof(source) || source.version != 5U) return false;
    output = source.frame;
    return true;
}

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

// The final-sample reader explicitly returns both owned pictures and the new
// frame. All older overloads keep rejecting version 5 by its distinct size.
inline bool read_resource(std::span<const std::byte> payload,
    std::span<const std::byte> bytecode,
    progpu_native_scene_shader_effect& program, std::uint32_t& sampler_resource_index,
    std::uint32_t& derivative_register, progpu_native_scene_shader_capture_frame& capture_frame,
    std::uint32_t& input_resource_index, progpu_native_scene_shader_sample_frame& sample_frame) noexcept {
    if (payload.size() != sizeof(progpu_native_scene_shader_effect_samples)) {
        progpu_native_scene_shader_effect candidate{};
        progpu_native_scene_shader_capture_frame capture{};
        std::uint32_t sampler{}, derivatives{};
        if (!read_resource(payload, bytecode, candidate, sampler, derivatives, capture)) return false;
        program = candidate; sampler_resource_index = sampler; derivative_register = derivatives;
        capture_frame = capture; input_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX; sample_frame = {};
        return true;
    }
    progpu_native_scene_shader_effect_samples source{};
    std::memcpy(&source, payload.data(), sizeof(source));
    if (source.struct_size != sizeof(source) || source.version != 5U || source.flags != 0U ||
        source.reserved[0] != 0U || source.reserved[1] != 0U ||
        source.input_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX ||
        (source.derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX && source.derivative_register >= 32U) ||
        !validate_sample_frame(source.frame) || !validate(source.program, bytecode)) return false;
    program = source.program; sampler_resource_index = source.sampler_resource_index;
    derivative_register = source.derivative_register; capture_frame = {};
    input_resource_index = source.input_resource_index; sample_frame = source.frame;
    return true;
}

// The full affine reader never sends v6 through a diagonal-frame consumer.
inline bool read_resource(std::span<const std::byte> payload, std::span<const std::byte> bytecode,
    progpu_native_scene_shader_effect& program, std::uint32_t& sampler_resource_index,
    std::uint32_t& derivative_register, progpu_native_scene_shader_capture_frame& capture_frame,
    std::uint32_t& input_resource_index, progpu_native_scene_shader_sample_frame& sample_frame,
    progpu_native_scene_shader_affine_frame& affine_frame) noexcept {
    if (payload.size() != sizeof(progpu_native_scene_shader_effect_affine)) {
        if (!read_resource(payload, bytecode, program, sampler_resource_index, derivative_register,
                capture_frame, input_resource_index, sample_frame)) return false;
        affine_frame = {};
        return true;
    }
    progpu_native_scene_shader_effect_affine source{};
    std::memcpy(&source, payload.data(), sizeof(source));
    if (source.struct_size != sizeof(source) || source.version != 6U || source.flags != 0U ||
        source.reserved[0] != 0U || source.reserved[1] != 0U ||
        source.input_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX ||
        (source.derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX && source.derivative_register >= 32U) ||
        !validate_affine_frame(source.frame) || !validate(source.program, bytecode)) return false;
    program = source.program; sampler_resource_index = source.sampler_resource_index;
    derivative_register = source.derivative_register; capture_frame = {};
    input_resource_index = source.input_resource_index;
    sample_frame = source.frame.placement; affine_frame = source.frame;
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
