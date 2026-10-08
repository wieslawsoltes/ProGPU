#pragma once

#include "progpu_native_shader_input_opacity_fixture.hpp"

namespace progpu::native::tests {

enum class shader_opacity_source : unsigned { image, drawing, visual };

struct shader_sampled_opacity_case final {
    shader_opacity_source source;
    unsigned variant;
    float dpi;
    double scale_x, scale_y;
};

inline progpu_native_mil_scene_build_request shader_sampled_opacity_request(
    const shader_sampled_opacity_case& test, bool baseline) {
    const auto family = static_cast<unsigned>(test.source);
    return {sizeof(progpu_native_mil_scene_build_request), 0U, 3U, 0U,
        0x94B0U + family * 2U + (baseline ? 1U : 0U), test.variant + 1U,
        test.dpi, test.dpi, 0U, test.variant + 1U};
}

inline progpu_native_mil_status serialize_shader_sampled_opacity(progpu_native_mil_channel* channel,
    const shader_sampled_opacity_case& test, bool baseline, std::vector<std::byte>& scene) {
    const auto request = shader_sampled_opacity_request(test, baseline);
    progpu_native_mil_scene_build_result result{};
    result.struct_size = sizeof(result);
    std::size_t size{};
    auto status = progpu_native_mil_channel_build_scene_with_request(
        channel, &request, nullptr, 0U, &size, nullptr, &result);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> candidate(size);
    status = progpu_native_mil_channel_build_scene_with_request(
        channel, &request, candidate.data(), candidate.size(), &size, nullptr, &result);
    if (status == PROGPU_NATIVE_MIL_STATUS_SUCCESS && size == candidate.size()) scene = std::move(candidate);
    return status;
}

// Packet fields follow the original source brush families, not a fabricated
// picture resource. Source extents and physical image DPI are independently
// owned input; no output baseline is fed back into the compiler.
inline progpu_native_mil_status build_shader_sampled_opacity_scene(progpu_native_mil_channel* channel,
    const shader_sampled_opacity_case& test, bool baseline, std::vector<std::byte>& scene) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    const shader_local_case local{test.variant == 2U ? shader_padding_output::constant : shader_padding_output::input,
        test.dpi, {.25, 1.75, .5, 1.5}, {}};
    std::vector<std::byte> scratch;
    auto status = build_shader_local_scene(channel, 500U + test.variant, local, scratch,
        baseline, baseline ? 2.0 : 2.25, baseline ? 3.0 : 3.25,
        test.scale_x, test.scale_y, 0, false, true, 128U);
    if (status != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return status;
    std::vector<std::byte> batch, content, visual_content;
    if (progpu_native_mil_channel_get_resource_generation(channel, 19U) == 0U) {
        constexpr std::array types{80U, 81U, 82U, 95U, 87U, 39U, 43U, 69U, 77U, 66U};
        for (std::uint32_t i = 0U; i < types.size(); ++i)
            packet(batch, command::channel_create_resource, 19U + i, types[i]);
        packet(batch, command::visual_create, 24U);
    }
    const bool absolute = test.variant == 4U;
    const double opacity = test.variant == 3U ? .5 : 1.0;
    const float first_alpha = test.variant == 1U ? 1.0F : 0.0F;
    const float last_alpha = test.variant == 1U || test.variant == 2U ? 0.0F : 1.0F;
    packet(batch, command::matrix_transform, 28U, 1.0, 0.0, 0.0, 1.0, 4.0, 0.0, 0U);
    packet(batch, command::rectangle_geometry, 26U, 0.0, 0.0, 10.0, 20.0, 20.0, 10.0, 0U, 0U, 0U, 0U);
    packet(batch, command::linear_gradient_brush, 27U, 1.0, 0.0, 0.0, 1.0, 0.0,
        0U, 0U, 0U, 0U, 1U, 0U, 48U, 0U, 0U,
        0.0, progpu_native_color{1, 0, 0, first_alpha},
        1.0, progpu_native_color{0, 1, 0, last_alpha});
    packet(batch, command::geometry_drawing, 23U, 27U, 0U, 26U);
    packet(visual_content, command::draw_drawing, 23U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + visual_content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 25U); append(batch, static_cast<std::uint32_t>(visual_content.size()));
    batch.insert(batch.end(), visual_content.begin(), visual_content.end());
    packet(batch, command::visual_set_content, 24U, 25U);
    packet(batch, command::visual_set_render_options, 24U, 3U, 1U, 0U, 3U, 0U, 0U, 0U);
    const std::array viewport = absolute ? std::array{16.0, 16.0, 32.0, 16.0} : std::array{0.0, 0.0, 1.0, 1.0};
    for (unsigned family = 0U; family < 3U; ++family) {
        const std::array viewbox = !absolute ? std::array{0.0, 0.0, 1.0, 1.0}
            : family == 0U ? std::array{0.0, 0.0, 4.0 * 96.0 / 144.0, 2.0 * 96.0 / 192.0}
                          : std::array{10.0, 20.0, 20.0, 10.0};
        packet(batch, family == 0U ? command::image_brush : family == 1U ? command::drawing_brush : command::visual_brush,
            19U + family, opacity, viewport, viewbox, .707, 1.414,
            0U, test.variant == 5U ? 28U : 0U, 0U, absolute ? 0U : 1U, absolute ? 0U : 1U,
            0U, 0U, 1U, 0U, 1U, 1U, 0U, 22U + family);
    }
    packet(batch, command::visual_set_alpha_mask, 1U, 19U + static_cast<unsigned>(test.source));
    packet(batch, command::visual_set_alpha, 1U, opacity);
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
    std::array<std::uint8_t, 4U * 2U * 4U> pixels{};
    constexpr std::array<std::uint8_t, 4U> alpha{0U, 64U, 192U, 255U};
    for (unsigned y = 0U; y < 2U; ++y) for (unsigned x = 0U; x < 4U; ++x) {
        const auto offset = (y * 4U + x) * 4U;
        pixels[offset] = 255U; // Alpha, not red/luminance, supplies this mask.
        pixels[offset + 3U] = test.variant == 2U ? 0U : alpha[test.variant == 1U ? 3U - x : x];
    }
    if (progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel, 22U, 4U, 2U, 16U,
            pixels.data(), pixels.size(), 144.0, 192.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 24U, 10, 20, 20, 10) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U, 16, 16, 32, 16) != PROGPU_NATIVE_MIL_STATUS_SUCCESS)
        return PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH;
    // Caller storage is not a lifetime source for typed bitmap registration.
    pixels.fill(0xA5U);
    return serialize_shader_sampled_opacity(channel, test, baseline, scene);
}

template<class Require>
void require_shader_sampled_input_ownership(std::span<const std::byte> bytes,
    const shader_sampled_opacity_case& test, Require require) {
    const auto header = read_shader_opacity_record<progpu_native_scene_header>(bytes, 0U, require);
    unsigned effects{};
    for (unsigned i = 0U; i < header.command_count; ++i) {
        const auto command = read_shader_opacity_record<progpu_native_scene_command>(bytes,
            header.command_offset + i * header.command_stride, require);
        if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
        const auto layer = read_shader_opacity_record<progpu_native_scene_layer>(bytes, command.payload_offset, require);
        if (layer.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) continue;
        require(layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX, "sampled opacity moved after shader evaluation");
        const auto resource = read_shader_opacity_record<progpu_native_scene_resource>(bytes,
            header.resource_offset + layer.effect_resource_index * header.resource_stride, require);
        require(resource.payload_size == sizeof(progpu_native_scene_shader_effect_samples), "sampled input lost v5 layout");
        const auto effect = read_shader_opacity_record<progpu_native_scene_shader_effect_samples>(bytes, resource.payload_offset, require);
        require(effect.version == 5U && effect.input_resource_index < header.resource_count, "sampled input owner is absent");
        const auto picture = read_shader_opacity_record<progpu_native_scene_resource>(bytes,
            header.resource_offset + effect.input_resource_index * header.resource_stride, require);
        const auto capture = read_shader_opacity_record<progpu_native_scene_picture_image>(bytes, picture.payload_offset, require);
        const double sx = test.dpi * test.scale_x, sy = test.dpi * test.scale_y;
        const double left = std::floor(15.5 * sx), top = std::floor(15.75 * sy);
        require(capture.dpi_scale == 1.0F && capture.width == std::ceil(49.5 * sx) - left &&
            capture.height == std::ceil(33.75 * sy) - top, "sampled input changed complete padded scale-space extent");
        require(picture.auxiliary_offset <= bytes.size() && picture.auxiliary_size <= bytes.size() - picture.auxiliary_offset,
            "sampled input has no owned original nested scene");
        const auto input = bytes.subspan(picture.auxiliary_offset, picture.auxiliary_size);
        const auto input_header = read_shader_opacity_record<progpu_native_scene_header>(input, 0U, require);
        unsigned masks{};
        for (unsigned j = 0U; j < input_header.command_count; ++j) {
            const auto inner = read_shader_opacity_record<progpu_native_scene_command>(input,
                input_header.command_offset + j * input_header.command_stride, require);
            if (inner.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
            const auto scope = read_shader_opacity_record<progpu_native_scene_layer>(input, inner.payload_offset, require);
            if (scope.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX) continue;
            require(scope.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX &&
                scope.opacity == (test.variant == 3U ? .5F : 1.0F), "sampled input visual opacity was lost/duplicated");
            const auto mask_resource = read_shader_opacity_record<progpu_native_scene_resource>(input,
                input_header.resource_offset + scope.mask_resource_index * input_header.resource_stride, require);
            require(mask_resource.payload_size == sizeof(progpu_native_scene_layer_picture_mask), "sampled source became a gradient mask");
            const auto mask = read_shader_opacity_record<progpu_native_scene_layer_picture_mask>(input, mask_resource.payload_offset, require);
            require(mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE && mask.opacity == 1.0F &&
                mask.bounds.x == 16.0 * sx - left && mask.bounds.y == 16.0 * sy - top &&
                mask.bounds.width == 32.0 * sx && mask.bounds.height == 16.0 * sy &&
                mask.transform.m11 == 1 && mask.transform.m22 == 1 && mask.transform.m12 == 0 &&
                mask.transform.m21 == 0 && mask.transform.m31 == 0 && mask.transform.m32 == 0 &&
                mask_resource.auxiliary_size > sizeof(progpu_native_scene_header),
                "sampled source imported final placement or lost the owned unpadded mask frame");
            ++masks;
        }
        require(masks == 1U, "sampled source mask must be applied exactly once inside input capture");
        ++effects;
    }
    require(effects == 1U, "sampled source effect count changed");
}

template<class Require>
void require_shader_sampled_rejections(progpu_native_mil_channel* channel,
    const shader_sampled_opacity_case& test, Require require) {
    using mil::command;
    using mil_clip_fixture_detail::packet;
    progpu_native_mil_channel* foreign_raw{};
    require(progpu_native_mil_channel_create(&foreign_raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS, "foreign mask owner unavailable");
    mil_clip_channel foreign(foreign_raw);
    std::vector<std::byte> batch;
    packet(batch, command::channel_create_resource, 99U, 75U);
    packet(batch, command::solid_color_brush, 99U, 1.0, progpu_native_color{1, 1, 1, 1}, 0U, 0U, 0U, 0U);
    require(progpu_native_mil_channel_apply(foreign_raw, batch.data(), batch.size(), nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
        "foreign-owned brush control could not be constructed");
    batch.clear();
    packet(batch, command::visual_set_alpha_mask, 1U, 99U);
    const auto generation = progpu_native_mil_channel_get_resource_generation(channel, 1U);
    require(progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) == PROGPU_NATIVE_MIL_STATUS_INVALID_HANDLE &&
        progpu_native_mil_channel_get_resource_generation(channel, 1U) == generation,
        "sampled mask borrowed another channel identity or published a failed packet");
    if (test.source == shader_opacity_source::image) return;
    batch.clear();
    // DrawingBrush -> drawing -> same DrawingBrush, or VisualBrush -> visual
    // drawing -> same VisualBrush. These are actual recursive source resources.
    packet(batch, command::geometry_drawing, 23U, 19U + static_cast<unsigned>(test.source), 0U, 26U);
    require(progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
        "recursive source packet was malformed instead of graph-rejected");
    const std::vector<std::byte> sentinel{std::byte{0x5A}, std::byte{0xA5}};
    auto unchanged = sentinel;
    require(serialize_shader_sampled_opacity(channel, test, false, unchanged) == PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH &&
        unchanged == sentinel, "recursive sampled source published partial input bytes");
}

// Render(family,lane,...) keeps three independent engines across source families:
// lane0 ordinary baseline, lane1 subject, lane2 independent shader. No baseline
// texture is imported into a shader engine. Source channels are already retired.
// Each family has distinct original scene owners, so its first graph must still
// submit every cold picture dependency. Reusing the host engine's immutable
// pipelines also exercises owner replacement instead of hiding it in new engines.
// The first cold counts enumerate every input/brush/source picture dependency;
// later mutations may retain unchanged nested pages. Their bounds describe that
// same finite graph, not a cache-retention performance qualification. Warm counts
// and original effect pass/uniform counts remain exact on every generation.
template<class Render, class Require>
void verify_shader_sampled_input_opacity(Render render, Require require) {
    for (unsigned family = 0U; family < 3U; ++family) {
        std::array<std::vector<std::byte>, 9U> scenes, baselines;
        std::array<shader_sampled_opacity_case, 9U> cases{};
        {
            progpu_native_mil_channel* raw{};
            require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS, "sampled source channel unavailable");
            mil_clip_channel owner(raw);
            for (unsigned variant = 0U; variant < cases.size(); ++variant) {
                auto& test = cases[variant];
                test = {static_cast<shader_opacity_source>(family), variant, variant == 7U ? 2.0F : 1.0F,
                    variant == 6U ? 1.5 : 1.0, variant == 6U ? .5 : 1.0};
                const auto subject_status = build_shader_sampled_opacity_scene(raw, test, false, scenes[variant]);
                if (subject_status != PROGPU_NATIVE_MIL_STATUS_SUCCESS)
                    std::fprintf(stderr, "Sampled opacity source family=%u variant=%u baseline=0 status=%u\n",
                        family, variant, static_cast<unsigned>(subject_status));
                require(subject_status == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                    "actual sampled source family did not compile before effect");
                const auto baseline_status = build_shader_sampled_opacity_scene(raw, test, true, baselines[variant]);
                if (baseline_status != PROGPU_NATIVE_MIL_STATUS_SUCCESS)
                    std::fprintf(stderr, "Sampled opacity source family=%u variant=%u baseline=1 status=%u\n",
                        family, variant, static_cast<unsigned>(baseline_status));
                require(baseline_status == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                    "actual sampled source family did not compile without effect");
                require_shader_sampled_input_ownership(scenes[variant], test, require);
            }
            // Restore the actual effect before negative graph publication; the
            // last independently compiled scene intentionally disabled it.
            std::vector<std::byte> ignored;
            require(build_shader_sampled_opacity_scene(raw, cases.back(), false, ignored) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "sampled source negative setup did not restore effect");
            require_shader_sampled_rejections(raw, cases.back(), require);
        }
        std::vector<std::uint8_t> original;
        // DrawingBrush and VisualBrush record their source content directly
        // into the mask picture. Their isolation layers do not add another
        // picture dependency: cold replay submits the mask and the target.
        constexpr std::uint64_t maximum_baseline_submissions = 2U;
        for (unsigned variant = 0U; variant < cases.size(); ++variant) {
            const auto& test = cases[variant];
            const auto header = read_shader_opacity_record<progpu_native_scene_header>(scenes[variant], 0U, require);
            const auto source = read_shader_opacity_record<progpu_native_scene_header>(baselines[variant], 0U, require);
            progpu_native_layer_metrics baseline_layers{}; baseline_layers.struct_size = sizeof(baseline_layers);
            progpu_native_scene_frame_metrics baseline_frame{}; baseline_frame.struct_size = sizeof(baseline_frame);
            const auto baseline = render(family, 0U, baselines[variant], source, test.dpi, true,
                variant == 0U ? maximum_baseline_submissions : 1U, maximum_baseline_submissions,
                baseline_layers, baseline_frame);
            require(baseline.size() == 128U * 64U * 4U && baseline_frame.submission_count >= 1U &&
                baseline_frame.submission_count <= maximum_baseline_submissions &&
                (variant != 0U || baseline_frame.submission_count == maximum_baseline_submissions),
                "ordinary sampled source exceeded its actual picture dependency graph");
            bool ink = false, partial = false;
            for (std::size_t i = 0U; i < baseline.size(); i += 4U) {
                require(baseline[i] == baseline[i + 1U] && baseline[i] == baseline[i + 2U] && baseline[i + 3U] == 255U,
                    "sampled opacity used source color instead of alpha or changed target opacity");
                ink |= baseline[i] != 0U; partial |= baseline[i] != 0U && baseline[i] != 255U;
            }
            require(variant == 2U ? !ink : ink && partial, "ordinary sampled source did not exercise original spatial alpha");
            if (variant == 0U) original = baseline;
            if (variant == 1U) require(baseline != original, "same sampled source mutation reused old pixels");
            if (variant == 4U || variant == 8U) require(baseline == original, "original absolute mapping/reset changed sampled opacity");
            std::vector<std::uint8_t> cold;
            for (unsigned replay = 0U; replay < 3U; ++replay) {
                progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
                progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
                const bool warm = replay == 1U;
                const std::uint64_t maximum_submissions = warm ? 1U : maximum_baseline_submissions + 1U;
                const std::uint64_t minimum_submissions = warm || variant == 0U ? maximum_submissions : 2U;
                const auto pixels = render(family, replay == 2U ? 2U : 1U, scenes[variant], header, test.dpi, false,
                    minimum_submissions, maximum_submissions, layers, frame);
                require(pixels.size() == baseline.size(), "sampled input effect extent changed");
                if (replay == 0U) cold = pixels;
                else require(pixels == cold, "sampled input cold/warm/independent bytes differ");
                if (variant != 2U) {
                    if (pixels != baseline) {
                        std::size_t first{};
                        while (first < pixels.size() && pixels[first] == baseline[first]) ++first;
                        std::fprintf(stderr, "Sampled opacity family=%u variant=%u replay=%u xy=%zu,%zu channel=%zu actual=%u ordinary=%u\n",
                            family, variant, replay, first / 4U % 128U, first / 512U, first % 4U,
                            first < pixels.size() ? pixels[first] : 999U, first < baseline.size() ? baseline[first] : 999U);
                        std::size_t changed_pixels{};
                        unsigned largest_difference{};
                        for (std::size_t pixel = 0U; pixel < pixels.size(); pixel += 4U) {
                            bool changed = false;
                            for (std::size_t channel = 0U; channel < 4U; ++channel) {
                                const unsigned actual = pixels[pixel + channel], ordinary = baseline[pixel + channel];
                                changed |= actual != ordinary;
                                const unsigned difference = actual > ordinary ? actual - ordinary : ordinary - actual;
                                largest_difference = std::max(largest_difference, difference);
                            }
                            changed_pixels += changed ? 1U : 0U;
                        }
                        std::fprintf(stderr, "Sampled opacity differences: pixels=%zu largest-channel-delta=%u\n",
                            changed_pixels, largest_difference);
                        // Keep a small original/subject neighborhood in CI logs
                        // to distinguish coverage rounding from displaced samples.
                        const auto x = first / 4U % 128U, y = first / 512U, channel = first % 4U;
                        for (auto row = y == 0U ? 0U : y - 1U; row <= std::min(y + 1U, std::size_t{63U}); ++row) {
                            for (auto column = x < 2U ? 0U : x - 2U; column <= std::min(x + 2U, std::size_t{127U}); ++column) {
                                const auto at = (row * 128U + column) * 4U + channel;
                                std::fprintf(stderr, "Sampled opacity neighbor xy=%zu,%zu channel=%zu actual=%u ordinary=%u\n",
                                    column, row, channel, unsigned(pixels[at]), unsigned(baseline[at]));
                            }
                        }
                    }
                    require(pixels == baseline, "nearest original sampled input changed independently compiled ordinary bytes");
                } else for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 128U; ++x) {
                    const std::array<std::uint8_t, 4U> expected = x >= 18U && x < 52U && y >= 19U && y < 37U
                        ? std::array<std::uint8_t, 4U>{64, 128, 191, 255} : std::array<std::uint8_t, 4U>{0, 0, 0, 255};
                    require(std::equal(expected.begin(), expected.end(), pixels.data() + (y * 128U + x) * 4U),
                        "zero sampled alpha incorrectly erased constant shader output/padding");
                }
                require((warm ? frame.submission_count == 1U : frame.submission_count >= 2U &&
                    frame.submission_count <= maximum_baseline_submissions + 1U) &&
                    (variant != 0U || warm || frame.submission_count == maximum_baseline_submissions + 1U) &&
                    frame.command_count == header.command_count && layers.effect_count == 1U &&
                    layers.effect_pass_count == 1U && layers.effect_cache_hit == 0U &&
                    layers.effect_uniform_upload_bytes == (warm ? 0U : 592U),
                    "sampled input lost exact warm/effect work or exceeded its owned dependency graph");
                std::fprintf(stderr, "Sampled opacity family=%u variant=%u replay=%u baseline-submits=%llu effect-submits=%llu exact bytes\n",
                    family, variant, replay, static_cast<unsigned long long>(baseline_frame.submission_count),
                    static_cast<unsigned long long>(frame.submission_count));
            }
        }
    }
}

} // namespace progpu::native::tests
