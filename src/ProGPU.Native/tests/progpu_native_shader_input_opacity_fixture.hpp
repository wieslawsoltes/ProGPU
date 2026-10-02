#pragma once

#include "progpu_native_shader_local_frame_fixture.hpp"

namespace progpu::native::tests {

// These controls compare the original ordinary mask realization with an input
// sample, not with an independently guessed ideal gradient/UNORM rounding rule.
// Quarter-device-pixel final translation selects exactly the baseline texel
// under the explicitly requested Nearest sampler, without a half-texel tie.
inline progpu_native_mil_status build_shader_input_opacity_scene(
    progpu_native_mil_channel* channel, std::uint32_t variant, bool baseline,
    std::vector<std::byte>& scene, bool sampled_mask = false) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    const float dpi = variant == 7U ? 2.0F : 1.0F;
    const shader_local_case test{
        variant == 2U ? shader_padding_output::constant : shader_padding_output::input,
        dpi, {.25, 1.75, .5, 1.5}, {}};
    std::vector<std::byte> scratch;
    auto status = build_shader_local_scene(channel, 300U + variant, test, scratch,
        baseline, baseline ? 2.0 : 2.25, baseline ? 3.0 : 3.25,
        1.0, 1.0, 0.0, false, true, 128U);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> batch, content;
    if (progpu_native_mil_channel_get_resource_generation(channel, 19U) == 0U) {
        packet(batch, command::channel_create_resource, 19U, 77U);
        packet(batch, command::channel_create_resource, 20U, 78U);
        packet(batch, command::channel_create_resource, 21U, 66U);
    }
    const float first_alpha = variant == 1U ? 1.0F : 0.0F;
    const float last_alpha = variant == 1U || variant == 2U ? 0.0F : 1.0F;
    const double brush_opacity = variant == 3U ? .5 : 1.0;
    const bool absolute = variant == 5U;
    packet(batch, command::matrix_transform, 21U, 1.0, 0.0, 0.0, 1.0, 4.0, 0.0, 0U);
    packet(batch, command::linear_gradient_brush, 19U, brush_opacity,
        absolute ? 16.0 : 0.0, absolute ? 16.0 : 0.0,
        absolute ? 48.0 : 1.0, absolute ? 16.0 : 0.0,
        0U, variant == 6U ? 21U : 0U, 0U, 0U, absolute ? 0U : 1U, 0U, 48U, 0U, 0U,
        0.0, progpu_native_color{1, 1, 1, first_alpha},
        1.0, progpu_native_color{1, 1, 1, last_alpha});
    packet(batch, command::radial_gradient_brush, 20U, 1.0,
        .5, .5, .5, .5, .5, .5, 0U, 0U, 0U, 0U, 1U, 0U, 48U, 0U, 0U, 0U, 0U,
        0.0, progpu_native_color{1, 1, 1, 0},
        1.0, progpu_native_color{1, 1, 1, 1});
    packet(batch, command::visual_set_alpha, 1U, variant == 3U ? .5 : 1.0);
    packet(batch, command::visual_set_alpha_mask, 1U, sampled_mask ? 9U : variant == 4U ? 20U : 19U);
    packet(content, command::draw_rectangle, 16.0, 16.0, 32.0, 16.0, 4U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 2U); append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    const auto old_generation = progpu_native_mil_channel_get_resource_generation(channel, 19U);
    status = progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    if (progpu_native_mil_channel_get_resource_generation(channel, 19U) <= old_generation)
        return PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH;
    status = progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U, 16.0, 16.0, 32.0, 16.0);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    const progpu_native_mil_scene_build_request request{
        sizeof(request), 0U, 3U, 0U, baseline ? 0x94A3U : 0x94A2U,
        variant + 1U, dpi, dpi, 0U, variant + 1U};
    std::size_t size{};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    status = progpu_native_mil_channel_build_scene_with_request(
        channel, &request, nullptr, 0U, &size, nullptr, &result);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> candidate(size);
    status = progpu_native_mil_channel_build_scene_with_request(
        channel, &request, candidate.data(), candidate.size(), &size, nullptr, &result);
    if (status == PROGPU_NATIVE_MIL_STATUS_SUCCESS && size == candidate.size()) scene = std::move(candidate);
    return status;
}

template<class T, class Require>
T read_shader_opacity_record(std::span<const std::byte> bytes, std::size_t offset, Require require) {
    require(offset <= bytes.size() && sizeof(T) <= bytes.size() - offset, "input opacity record is out of bounds");
    T value{}; std::memcpy(&value, bytes.data() + offset, sizeof(value)); return value;
}

template<class Render, class Require>
void verify_shader_input_opacity(Render render, Require require) {
    std::array<std::vector<std::byte>, 8U> scenes, baselines;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
            "input opacity source channel unavailable");
        mil_clip_channel owner(raw);
        for (std::uint32_t variant = 0U; variant < scenes.size(); ++variant) {
            require(build_shader_input_opacity_scene(raw, variant, false, scenes[variant]) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
                build_shader_input_opacity_scene(raw, variant, true, baselines[variant]) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "actual gradient opacity source did not compile");
            const auto bytes = std::span<const std::byte>(scenes[variant]);
            const auto header = read_shader_opacity_record<progpu_native_scene_header>(bytes, 0U, require);
            bool found = false;
            for (std::uint32_t i = 0U; i < header.command_count; ++i) {
                const auto command = read_shader_opacity_record<progpu_native_scene_command>(bytes,
                    header.command_offset + i * header.command_stride, require);
                if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
                const auto layer = read_shader_opacity_record<progpu_native_scene_layer>(bytes, command.payload_offset, require);
                if (layer.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) continue;
                require(layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX,
                    "input alpha mask was moved after shader evaluation");
                const auto effect = read_shader_opacity_record<progpu_native_scene_resource>(bytes,
                    header.resource_offset + layer.effect_resource_index * header.resource_stride, require);
                require(effect.payload_size == sizeof(progpu_native_scene_shader_effect_samples), "input mask lost final-sample wire");
                const auto shader = read_shader_opacity_record<progpu_native_scene_shader_effect_samples>(bytes, effect.payload_offset, require);
                require(shader.version == 5U && shader.input_resource_index < header.resource_count,
                    "input opacity picture is missing");
                const auto picture = read_shader_opacity_record<progpu_native_scene_resource>(bytes,
                    header.resource_offset + shader.input_resource_index * header.resource_stride, require);
                const auto capture = read_shader_opacity_record<progpu_native_scene_picture_image>(bytes, picture.payload_offset, require);
                require(capture.dpi_scale == 1.0F && capture.width == (variant == 7U ? 68U : 35U) &&
                    capture.height == (variant == 7U ? 37U : 19U), "gradient input lost complete padded capture extent");
                require(picture.auxiliary_offset <= bytes.size() && picture.auxiliary_size <= bytes.size() - picture.auxiliary_offset,
                    "input opacity picture bytes are missing");
                const auto nested = bytes.subspan(picture.auxiliary_offset, picture.auxiliary_size);
                const auto input = read_shader_opacity_record<progpu_native_scene_header>(nested, 0U, require);
                bool masked = false;
                for (std::uint32_t j = 0U; j < input.command_count; ++j) {
                    const auto inner = read_shader_opacity_record<progpu_native_scene_command>(nested,
                        input.command_offset + j * input.command_stride, require);
                    if (inner.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
                    const auto opacity = read_shader_opacity_record<progpu_native_scene_layer>(nested, inner.payload_offset, require);
                    if (opacity.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) continue;
                    require(!masked && opacity.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX &&
                        opacity.opacity == (variant == 3U ? .5F : 1.0F), "visual input opacity was duplicated or lost");
                    const auto resource = read_shader_opacity_record<progpu_native_scene_resource>(nested,
                        input.resource_offset + opacity.mask_resource_index * input.resource_stride, require);
                    const auto mask = read_shader_opacity_record<progpu_native_scene_layer_brush_mask>(nested, resource.payload_offset, require);
                    const float dpi = variant == 7U ? 2.0F : 1.0F;
                    require(mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH && mask.opacity == 1.0F &&
                        mask.gradient_stop_count == 2U && mask.bounds.x == 16.0F && mask.bounds.y == 16.0F &&
                        mask.bounds.width == 32.0F && mask.bounds.height == 16.0F &&
                        mask.transform.m11 == dpi && mask.transform.m22 == dpi &&
                        mask.transform.m12 == 0.0F && mask.transform.m21 == 0.0F &&
                        mask.transform.m31 == (variant == 7U ? -31.0F : -15.0F) &&
                        mask.transform.m32 == (variant == 7U ? -31.0F : -15.0F),
                        "source gradient changed unpadded bounds, scale-space frame or stops");
                    require(resource.auxiliary_size == 2U * sizeof(progpu_native_scene_gradient_stop),
                        "gradient input no longer owns exactly its original stops");
                    const auto first = read_shader_opacity_record<progpu_native_scene_gradient_stop>(nested,
                        resource.auxiliary_offset, require);
                    const auto last = read_shader_opacity_record<progpu_native_scene_gradient_stop>(nested,
                        resource.auxiliary_offset + sizeof(first), require);
                    require(first.offset == 0.0F && last.offset == 1.0F &&
                        first.color.a == (variant == 1U ? 1.0F : 0.0F) &&
                        last.color.a == (variant == 1U || variant == 2U ? 0.0F : 1.0F),
                        "retained input stops mixed source generations");
                    masked = true;
                }
                require(masked, "original typed gradient mask was not captured before bytecode");
                found = true;
            }
            require(found, "gradient opacity source shader is missing");
        }
        const std::vector<std::byte> sentinel{std::byte{0x5A}};
        auto unchanged = sentinel;
        require(build_shader_input_opacity_scene(raw, 0U, false, unchanged, true) == PROGPU_NATIVE_MIL_STATUS_UNSUPPORTED_COMMAND &&
            unchanged == sentinel, "sampled opacity mask silently acquired gradient admission");
    } // Retire the real channel, every source brush and all mutable packet data.
    std::vector<std::uint8_t> original;
    for (std::uint32_t variant = 0U; variant < scenes.size(); ++variant) {
        const float dpi = variant == 7U ? 2.0F : 1.0F;
        const auto header = read_shader_opacity_record<progpu_native_scene_header>(scenes[variant], 0U, require);
        const auto source = read_shader_opacity_record<progpu_native_scene_header>(baselines[variant], 0U, require);
        progpu_native_layer_metrics baseline_layers{}; baseline_layers.struct_size = sizeof(baseline_layers);
        progpu_native_scene_frame_metrics baseline_frame{}; baseline_frame.struct_size = sizeof(baseline_frame);
        const auto baseline = render(true, baselines[variant], source, dpi, true, 1U, baseline_layers, baseline_frame);
        require(baseline.size() == 128U * 64U * 4U && baseline_frame.submission_count == 1U,
            "ordinary opacity baseline extent or submissions differ");
        bool ink = false, partial = false;
        for (std::size_t i = 0U; i < baseline.size(); i += 4U) {
            require(baseline[i] == baseline[i + 1U] && baseline[i] == baseline[i + 2U] && baseline[i + 3U] == 255U,
                "ordinary gradient alpha changed white source or opaque target");
            ink |= baseline[i] != 0U; partial |= baseline[i] != 0U && baseline[i] != 255U;
        }
        require(variant == 2U ? !ink : ink && partial, "ordinary opacity baseline did not exercise its actual alpha");
        if (variant == 0U) original = baseline;
        if (variant == 1U) require(baseline != original, "same-brush mutation did not change retained input");
        if (variant == 5U) require(baseline == original, "absolute and relative original gradient mapping differ");
        for (std::uint32_t replay = 0U; replay < 3U; ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            const std::uint32_t submissions = replay == 1U ? 1U : 2U;
            const auto pixels = render(replay == 2U, scenes[variant], header, dpi, false, submissions, layers, frame);
            require(pixels.size() == baseline.size(), "shader input opacity extent differs");
            if (variant != 2U) require(pixels == baseline, "shader input changed exact ordinary gradient opacity bytes");
            else for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 128U; ++x) {
                const std::array<std::uint8_t, 4U> expected = x >= 18U && x < 52U && y >= 19U && y < 37U
                    ? std::array<std::uint8_t, 4U>{64, 128, 191, 255} : std::array<std::uint8_t, 4U>{0, 0, 0, 255};
                require(std::equal(expected.begin(), expected.end(), pixels.data() + (y * 128U + x) * 4U),
                    "zero-alpha input was incorrectly applied to constant shader output or padded coverage");
            }
            require(frame.submission_count == submissions && frame.command_count == header.command_count &&
                layers.effect_count == 1U && layers.effect_pass_count == 1U && layers.effect_cache_hit == 0U &&
                layers.effect_uniform_upload_bytes == (replay == 1U ? 0U : 592U),
                "gradient input capture retention/pass/uniform counters differ");
        }
    }
}
} // namespace progpu::native::tests
