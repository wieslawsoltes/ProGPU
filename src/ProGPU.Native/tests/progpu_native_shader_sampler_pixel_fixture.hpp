#pragma once

#include "progpu_native_mil_visual_clip_fixture.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace progpu::native::tests {

// MIL visual publication retains the source state around its one effect layer.
// Preserve the SAVE/RESTORE pair instead of counting only the layer body.
inline constexpr std::array<std::uint32_t, 5U> shader_sampler_visual_commands{
    PROGPU_NATIVE_SCENE_COMMAND_SAVE, PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC, PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
    PROGPU_NATIVE_SCENE_COMMAND_RESTORE};

template<class Require>
void verify_shader_sampler_visual_commands(const std::vector<std::byte>& scene,
    const progpu_native_scene_header& header, Require require) {
    require(header.command_count == shader_sampler_visual_commands.size() &&
        header.command_stride >= sizeof(progpu_native_scene_command) &&
        header.command_offset <= scene.size() && header.command_stride <=
            (scene.size() - header.command_offset) / shader_sampler_visual_commands.size(),
        "shader sampler source visual command extent changed");
    for (std::size_t index = 0U; index < shader_sampler_visual_commands.size(); ++index) {
        progpu_native_scene_command command{};
        std::memcpy(&command, scene.data() + header.command_offset + index * header.command_stride,
            sizeof(command));
        require(command.kind == shader_sampler_visual_commands[index],
            "shader sampler source state/layer/draw command identity changed");
    }
}

// Original MIL packets, owned upload and original shader bytes. The expected
// result below is an independent source-color oracle, not another renderer.
inline bool build_original_shader_sampler_scene(progpu_native_mil_channel* channel,
    std::uint32_t variant, std::vector<std::byte>& scene) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    if (variant >= 23U) return false;
    const auto source_variant = variant < 4U ? variant
        : variant >= 13U ? variant <= 15U ? variant - 12U : 1U
        : variant == 6U || variant == 9U || variant == 10U ? 1U : 0U;
    const bool full_source = variant == 11U || variant == 12U;
    const bool two_axis = variant >= 16U && variant <= 19U;
    const bool inherited = variant >= 5U && variant <= 10U;
    const auto extent = full_source ? 100.0 : 32.0;
    const auto input_height = full_source ? 100.0 : 24.0;
    std::vector<std::byte> batch, content;
    const std::array types{39U, 43U, 95U, 47U, 80U, 33U, 38U, 75U, 66U, 69U};
    if (variant == 0U) {
        for (std::uint32_t i = 0U; i < types.size(); ++i)
            packet(batch, command::channel_create_resource, i + 1U, types[i]);
        packet(batch, command::visual_create, 1U);
        packet(batch, command::generic_target_create, 4U, std::uint64_t{0U}, std::uint64_t{0U}, 64U, 64U, 0U);
        packet(batch, command::target_set_root, 4U, 1U);
    }
    if (variant == 5U) {
        packet(batch, command::channel_create_resource, 11U, 39U);
        packet(batch, command::visual_create, 11U);
        packet(batch, command::visual_insert_child_at, 11U, 1U, 0U);
        packet(batch, command::target_set_root, 4U, 11U);
    }
    if (variant >= 5U)
        packet(batch, command::visual_set_render_options, 11U, 1U, 0U, 0U,
            inherited ? 3U : 0U, 0U, 0U, 0U);
    if (variant == 11U) {
        packet(batch, command::channel_create_resource, 12U, 47U);
        packet(batch, command::generic_target_create, 12U, std::uint64_t{0U}, std::uint64_t{0U}, 128U, 128U, 0U);
        packet(batch, command::target_set_root, 12U, 11U);
    }
    // A flagged Unspecified update is real source state, including when an
    // already-published child resets its own Nearest mode. It inherits, not
    // forces Linear; the root's default remains Linear.
    packet(batch, command::visual_set_render_options, 1U, 3U, 1U, 0U,
        variant < 4U || variant == 7U || variant == 9U ? 3U : 0U, 0U, 0U, 0U);
    packet(batch, command::solid_color_brush, 8U, 1.0, progpu_native_color{1, 1, 1, 1}, 0U, 0U, 0U, 0U);
    packet(batch, command::matrix_transform, 9U, 1.0, 0.0, 0.0, 1.0, 8.0, 0.0, 0U);
    const std::array viewbox = variant == 20U ? std::array{0.0, 0.0, 0.5, 1.0}
        : !full_source ? std::array{0.0, 0.0, 1.0, 1.0}
        : variant == 11U ? std::array{50.0, 10.0, 100.0, 20.0} : std::array{0.25, 0.2, 0.5, 0.4};
    packet(batch, command::image_brush, 5U, full_source ? 0.25 : source_variant == 3U ? 1.0 : 0.5,
        std::array{0.0, 0.0, full_source || source_variant == 0U ? 1.0 : 0.5, 1.0}, viewbox,
        0.707, 1.414, 0U, !full_source && (source_variant == 2U || variant == 19U) ? 9U : 0U, 0U,
        1U, variant == 11U ? 0U : 1U, 0U, 0U, full_source ? 0U : variant == 21U ? 2U : 1U,
        full_source || source_variant == 0U ? 0U : two_axis ? (variant == 19U ? 1U : variant - 15U) : 4U,
        1U, 1U, 0U, 3U);
    constexpr std::array<std::uint32_t, 15U> program{
        0xFFFF0200U, 0x0200001FU, 0x80000000U, 0xB0030000U,
        0x0200001FU, 0x90000000U, 0xA00F0800U,
        0x03000042U, 0x800F0000U, 0xB0E40000U, 0xA0E40800U,
        0x02000001U, 0x800F0800U, 0x80E40000U, 0xFFFFU};
    packet(batch, command::pixel_shader, 6U, 0U, static_cast<std::uint32_t>(sizeof(program)), 0U, program);
    packet(batch, command::shader_effect, 7U, 0.0, 0.0, 0.0, 0.0, 6U, 0xFFFFFFFFU,
        std::array<std::uint32_t, 8U>{0U, 0U, 0U, 0U, 0U, 0U, 8U, 4U}, 0U, (source_variant & 1U) == 0U ? 1U : 2U, 5U);
    packet(batch, command::visual_set_effect, 1U, 7U);
    packet(batch, command::rectangle_geometry, 10U, 0.0, 0.0,
        full_source ? 8.0 : 16.0, full_source ? 10.0 : 12.0,
        full_source ? 100.0 : 16.0, full_source ? 100.0 : 16.0, 0U, 0U, 0U, 0U);
    packet(batch, command::visual_set_clip, 1U, 10U);
    packet(content, command::draw_rectangle, 8.0, 10.0, extent, input_height, 8U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 2U); append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    packet(batch, command::visual_set_content, 1U, 2U);
    const auto old_bitmap_generation = progpu_native_mil_channel_get_resource_generation(channel, 3U);
    const auto old_brush_generation = progpu_native_mil_channel_get_resource_generation(channel, 5U);
    const auto bitmap_width = full_source ? 400U : 2U;
    const auto bitmap_height = full_source ? 200U : two_axis ? 2U : 1U;
    std::vector<std::uint8_t> pixels(bitmap_width * bitmap_height * 4U);
    for (std::uint32_t y = 0U; y < bitmap_height; ++y)
        for (std::uint32_t x = 0U; x < bitmap_width; ++x) {
            const auto at = (y * bitmap_width + x) * 4U;
            const bool green = (x >= bitmap_width / 2U) != (two_axis && y != 0U);
            pixels[at + (green ? 1U : !full_source && source_variant == 3U ? 2U : 0U)] = 255U;
            pixels[at + 3U] = 255U;
        }
    if (progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U, 8, 10, extent, input_height) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel, 3U, bitmap_width, bitmap_height, bitmap_width * 4U,
            pixels.data(), pixels.size(), full_source ? 192.0 : variant == 22U ? 123.456789012345 : 144.0,
            full_source ? 384.0 : variant == 22U ? 183.456789012345 : 192.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (progpu_native_mil_channel_get_resource_generation(channel, 3U) <= old_bitmap_generation ||
        progpu_native_mil_channel_get_resource_generation(channel, 5U) <= old_brush_generation) return false;
    const progpu_native_mil_scene_build_request request{
        sizeof(request), 0U, full_source ? 12U : 4U, 0U, 0x9494U, variant + 1U, 1.0, 1.0, 0U, variant + 1U};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(channel, &request, nullptr, 0U, &written,
        nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel, &request, scene.data(), scene.size(),
        &written, nullptr, &result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

// Failure-only diagnostic: replay the original immutable capture before the
// effect samples it. The provider callback uses a separate engine and the exact
// retained physical frame; this never changes the original replay timeline.
template<class Diagnose, class Require>
void diagnose_original_shader_sampler_capture(const std::vector<std::byte>& scene,
    std::uint32_t variant, std::uint32_t failed_x, std::uint32_t failed_y,
    const std::array<std::uint8_t, 4U>& expected,
    const std::vector<std::uint8_t>& effected, Diagnose diagnose, Require require) {
    require(effected.size() == 64U * 64U * 4U && failed_x < 64U && failed_y < 64U,
        "sampler diagnostic final image is outside the original frame");
    const auto read = [&scene, &require]<class T>(std::size_t offset, T& value) {
        require(offset <= scene.size() && sizeof(T) <= scene.size() - offset,
            "sampler diagnostic record is outside the original scene");
        std::memcpy(&value, scene.data() + offset, sizeof(T));
    };
    progpu_native_scene_header outer{};
    read(0U, outer);
    std::uint32_t pictures = 0U;
    for (std::uint32_t i = 0U; i < outer.resource_count; ++i) {
        progpu_native_scene_resource resource{};
        read(outer.resource_offset + static_cast<std::size_t>(i) * outer.resource_stride, resource);
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_IMAGE ||
            (resource.flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U) continue;
        ++pictures;
        progpu_native_scene_picture_image picture{};
        read(resource.payload_offset, picture);
        require(picture.flags == 0U && picture.width == 32U && picture.height == 24U &&
            picture.dpi_scale == 1.0F && picture.clear_color.r == 0.0F &&
            picture.clear_color.g == 0.0F && picture.clear_color.b == 0.0F &&
            picture.clear_color.a == 0.0F,
            "sampler diagnostic changed the original capture frame");
        require(resource.auxiliary_offset <= scene.size() &&
            resource.auxiliary_size <= scene.size() - resource.auxiliary_offset,
            "sampler diagnostic nested scene is outside the original capture");
        const std::vector<std::byte> nested(scene.begin() + resource.auxiliary_offset,
            scene.begin() + resource.auxiliary_offset + resource.auxiliary_size);
        progpu_native_scene_header header{};
        read(resource.auxiliary_offset, header);
        std::fprintf(stderr, "Sampler direct diagnostic variant=%u scene=%llu/%llu extent=%ux%u dpi=%g\n",
            variant, static_cast<unsigned long long>(header.scene_id),
            static_cast<unsigned long long>(header.generation), picture.width, picture.height,
            static_cast<double>(picture.dpi_scale));
        for (std::uint32_t j = 0U; j < header.command_count; ++j) {
            progpu_native_scene_command command{};
            read(resource.auxiliary_offset + header.command_offset +
                static_cast<std::size_t>(j) * sizeof(command), command);
            if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) continue;
            progpu_native_scene_image_draw image{};
            read(resource.auxiliary_offset + command.payload_offset, image);
            std::fprintf(stderr, "Sampler direct image sampling=%u flags=%u size=%ux%u source=(%g,%g,%g,%g)\n",
                image.sampling, image.flags, image.image_width, image.image_height,
                static_cast<double>(image.source_rect.x), static_cast<double>(image.source_rect.y),
                static_cast<double>(image.source_rect.width), static_cast<double>(image.source_rect.height));
        }
        const auto direct = diagnose(nested, header, picture);
        require(direct[0] == direct[1] && direct[0].size() == picture.width * picture.height * 4U,
            "sampler direct diagnostic cold/warm pixels differ");
        const auto* failed_effect = effected.data() + (failed_y * 64U + failed_x) * 4U;
        if (failed_x >= 8U && failed_x < 40U && failed_y >= 10U && failed_y < 34U) {
            const auto* failed_direct = direct[0].data() +
                ((failed_y - 10U) * picture.width + failed_x - 8U) * 4U;
            std::fprintf(stderr,
                "Sampler direct failing pixel=(%u,%u) capture=(%u,%u,%u,%u) effect=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                failed_x, failed_y, failed_direct[0], failed_direct[1], failed_direct[2], failed_direct[3],
                failed_effect[0], failed_effect[1], failed_effect[2], failed_effect[3],
                expected[0], expected[1], expected[2], expected[3]);
        } else {
            // A leaked pixel may be the original failure. It has no sample in
            // the receiving visual's capture; never underflow that local map.
            std::fprintf(stderr,
                "Sampler direct failing pixel=(%u,%u) is outside capture frame; effect=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                failed_x, failed_y, failed_effect[0], failed_effect[1], failed_effect[2], failed_effect[3],
                expected[0], expected[1], expected[2], expected[3]);
        }
        bool different = false;
        for (std::uint32_t y = 12U; y < 28U && !different; ++y)
            for (std::uint32_t x = 16U; x < 32U && !different; ++x) {
                const auto* before = direct[0].data() + ((y - 10U) * picture.width + x - 8U) * 4U;
                const auto* after = effected.data() + (y * 64U + x) * 4U;
                different = !std::equal(before, before + 3U, after);
                if (different)
                    std::fprintf(stderr, "Sampler direct first RGB difference pixel=(%u,%u) capture=(%u,%u,%u,%u) effect=(%u,%u,%u,%u)\n",
                        x, y, before[0], before[1], before[2], before[3], after[0], after[1], after[2], after[3]);
            }
        if (!different) std::fprintf(stderr, "Sampler direct and effect RGB are identical throughout the original clip\n");
    }
    require(pictures == 1U, "sampler diagnostic requires the one actual retained capture");
}

// This reader selects actual retained capture ownership only. Expected native
// pixels are produced separately, from the original variant, never this wire.
template<class Require>
void replay_original_shader_sampler_capture(progpu_native_engine* engine, std::uintptr_t target_view,
    const std::vector<std::byte>& scene, const progpu_native_scene_header& header,
    const progpu_native_scene_picture_image& picture, Require require) {
    progpu_native_scene_metrics update{};
    update.struct_size = sizeof(update);
    require(progpu_native_engine_update_scene(engine, scene.data(), scene.size(), &update) ==
        PROGPU_NATIVE_STATUS_SUCCESS && update.draw_count == 1U,
        "sampler capture immutable scene update failed");
    progpu_native_scene_frame frame{};
    frame.struct_size = sizeof(frame);
    frame.width = picture.width;
    frame.height = picture.height;
    frame.dpi_scale = picture.dpi_scale;
    frame.clear_color = picture.clear_color;
    frame.target_view = target_view;
    frame.scene_id = header.scene_id;
    frame.generation = header.generation;
    progpu_native_scene_frame_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    require(progpu_native_engine_render_scene(engine, &frame, &metrics) == PROGPU_NATIVE_STATUS_SUCCESS &&
        metrics.command_count == header.command_count && metrics.submission_count == 1U,
        "sampler capture draw/command/submission count differs");
}

template<class Capture, class Require>
auto capture_original_shader_sampler(const std::vector<std::byte>& scene,
    bool four_load, Capture capture, Require require) {
    const auto read = [&scene, &require]<class T>(std::size_t offset, T& value) {
        require(offset <= scene.size() && sizeof(T) <= scene.size() - offset,
            "sampler capture record is outside its retained scene");
        std::memcpy(&value, scene.data() + offset, sizeof(T));
    };
    progpu_native_scene_header outer{};
    read(0U, outer);
    std::array<std::vector<std::uint8_t>, 2U> result;
    std::uint32_t pictures = 0U;
    for (std::uint32_t i = 0U; i < outer.resource_count; ++i) {
        progpu_native_scene_resource resource{};
        read(outer.resource_offset + static_cast<std::size_t>(i) * outer.resource_stride, resource);
        if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_IMAGE ||
            (resource.flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U) continue;
        require(++pictures == 1U, "sampler capture has more than one retained picture");
        progpu_native_scene_picture_image picture{};
        read(resource.payload_offset, picture);
        require(picture.flags == 0U && picture.width == 32U && picture.height == 24U &&
            picture.dpi_scale == 1.0F && picture.clear_color.r == 0.0F &&
            picture.clear_color.g == 0.0F && picture.clear_color.b == 0.0F && picture.clear_color.a == 0.0F,
            "sampler capture changed its original physical frame or clear color");
        require(resource.auxiliary_offset <= scene.size() &&
            resource.auxiliary_size <= scene.size() - resource.auxiliary_offset,
            "sampler capture bytes are outside their retained scene");
        const std::vector<std::byte> nested(scene.begin() + resource.auxiliary_offset,
            scene.begin() + resource.auxiliary_offset + resource.auxiliary_size);
        progpu_native_scene_header header{};
        require(nested.size() >= sizeof(header), "sampler capture header missing");
        std::memcpy(&header, nested.data(), sizeof(header));
        result = capture(four_load, nested, header, picture);
    }
    require(pictures == 1U && result[0].size() == 32U * 24U * 4U && result[0] == result[1],
        "sampler capture cold/warm size or pixels differ");
    return result[0];
}

template<class Render, class Require, class Capture, class NativeReference>
void verify_original_shader_sampler_pixels(Render render, Require require,
    Capture capture, NativeReference native_reference) {
    std::array<std::vector<std::byte>, 20U> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
            "original ImageBrush shader channel rejected");
        mil_clip_channel owner(raw);
        // Scene 0x9494 retains one source owner. Mutate its actual resources so
        // every bitmap/brush revision comes from that owner's live channel,
        // rather than recycling another channel's handle/generation namespace.
        for (std::uint32_t variant = 0U; variant < scenes.size(); ++variant)
            require(build_original_shader_sampler_scene(raw, variant, scenes[variant]),
                "original ImageBrush shader scene or resource revision rejected");
    }
    // All immutable captures outlive the channel and caller-owned bitmap bytes.
    std::vector<std::uint8_t> absolute_viewbox_pixels;
    std::vector<std::uint8_t> before_reset_pixels;
    // Default/native retains cases 0–12 verbatim. Cases 13–19 additionally run
    // the exact original rational oracle on explicitly selected four-load
    // engines; no native result is used to fit that arithmetic expectation.
    for (std::uint32_t policy = 0U; policy < 2U; ++policy) {
    const bool four_load = policy == 1U;
    for (std::uint32_t variant = four_load ? 13U : 0U; variant < scenes.size(); ++variant) {
        const auto image_extent = variant == 11U || variant == 12U ? 128U : 64U;
        const auto& scene = scenes[variant];
        progpu_native_scene_header header{};
        require(scene.size() >= sizeof(header), "ImageBrush shader scene header missing");
        std::memcpy(&header, scene.data(), sizeof(header));
        std::array<std::vector<std::uint8_t>, 3U> images;
        for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            const auto submissions = replay == 1U ? 1U : 2U;
            images[replay] = render(replay == 2U, scene, variant + 1U, submissions,
                header.command_count, image_extent, layers, frame, four_load);
            require(frame.submission_count == submissions && frame.command_count == header.command_count,
                "owned sampler capture submission or source command count differs");
            require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                layers.effect_cache_hit == (replay == 1U ? 1U : 0U), "owned sampler effect retention differs");
        }
        require(images[0] == images[1] && images[0] == images[2] && images[0].size() == image_extent * image_extent * 4U,
            "owned sampler cold/warm/independent pixels differ");
        if (variant == 7U || variant == 9U) before_reset_pixels = images[0];
        if (variant == 8U || variant == 10U) require(images[0] == before_reset_pixels,
            "owned sampler flagged Unspecified reset lost the parent's Nearest mode");
        if (variant == 11U) absolute_viewbox_pixels = images[0];
        if (variant == 12U) require(images[0] == absolute_viewbox_pixels,
            "owned sampler absolute/relative full-source viewbox pixels differ");
        std::vector<std::uint8_t> native_pixels;
        if (variant >= 13U && !four_load) {
            native_pixels = native_reference(variant);
            require(native_pixels.size() == 32U * 24U * 4U,
                "independent native sampler reference size differs");
            const auto captured = capture_original_shader_sampler(scene, false, capture, require);
            for (std::size_t i = 0U; i < captured.size(); i += 4U) {
                const bool same = std::equal(captured.data() + i, captured.data() + i + 4U, native_pixels.data() + i);
                if (!same) std::fprintf(stderr,
                    "Native sampler capture variant=%u pixel=(%zu,%zu) product=(%u,%u,%u,%u) raw=(%u,%u,%u,%u)\n",
                    variant, (i / 4U) % 32U, (i / 4U) / 32U,
                    captured[i], captured[i + 1U], captured[i + 2U], captured[i + 3U],
                    native_pixels[i], native_pixels[i + 1U], native_pixels[i + 2U], native_pixels[i + 3U]);
                require(same, "native sampler capture differs from independent raw GPU filtering");
            }
        }
        for (unsigned y = 0U; y < image_extent; ++y) for (unsigned x = 0U; x < image_extent; ++x) {
            std::array<std::uint8_t, 4U> expected{0U, 0U, 0U, 255U};
            if (variant == 11U || variant == 12U) {
                // 400x200 pixels at 192/384 DPI are 200x50 DIPs. The
                // centered Stretch.None viewbox maps the FULL source to
                // (-50,30,200,50), then clips to the 100x100 viewport.
                // Source viewbox cropping would incorrectly leave 20 rows.
                if (x >= 8U && x < 108U && y >= 40U && y < 90U)
                    expected[x < 58U ? 0U : 1U] = 64U;
            } else if (x >= 16U && x < 32U && y >= 12U && y < 28U) {
                const auto source_variant = variant < 4U ? variant
                    : variant == 6U || variant == 9U || variant == 10U ? 1U : 0U;
                const auto offset = source_variant == 2U ? 8U : 0U;
                if (variant >= 13U) {
                    // Independent four-neighbour interpolation over original
                    // two-texel axes. Repeating a clamped enlarged page has a
                    // different neighbourhood at every seam.
                    const auto axis_weight = [](int numerator, int denominator, bool mirror) {
                        const int lower = numerator >= 0 ? numerator / denominator
                            : -((-numerator + denominator - 1) / denominator);
                        const int fraction = numerator - lower * denominator;
                        const auto color = [mirror](int index) {
                            const int period = mirror ? 4 : 2;
                            const int wrapped = (index % period + period) % period;
                            return mirror ? (wrapped == 1 || wrapped == 2 ? 1 : 0) : wrapped;
                        };
                        return color(lower) * (denominator - fraction) + color(lower + 1) * fraction;
                    };
                    const bool two_axis = variant >= 16U;
                    const int translation = variant == 14U || variant == 19U ? 8 : 0;
                    const bool mirror_x = variant == 16U || variant == 18U || variant == 19U;
                    const bool mirror_y = variant == 17U || variant == 18U;
                    const int horizontal = axis_weight((static_cast<int>(x) - 8 - translation) * 2 - 7, 16, mirror_x);
                    const int vertical = two_axis ? axis_weight((static_cast<int>(y) - 10) * 2 - 11, 24, mirror_y) : 0;
                    const int green = horizontal * 24 + vertical * 16 - 2 * horizontal * vertical;
                    const int opacity_divisor = variant == 15U ? 1 : 2;
                    const int denominator = 384 * opacity_divisor;
                    expected[1] = static_cast<std::uint8_t>((green * 255 + denominator / 2) / denominator);
                    expected[variant == 15U ? 2U : 0U] = static_cast<std::uint8_t>(((384 - green) * 255 + denominator / 2) / denominator);
                    if (!four_load) {
                        const auto at = ((y - 10U) * 32U + x - 8U) * 4U;
                        std::copy_n(native_pixels.data() + at, 3U, expected.data());
                    }
                } else if (variant == 4U) {
                    // Two texel centers, clamped at the source image edge.
                    // Work in exact thirty-second-texel numerators; opacity is
                    // applied once when the capture is quantized to UNORM8.
                    const int green = std::clamp((static_cast<int>(x) - 8) * 2 - 15, 0, 32);
                    expected[1] = static_cast<std::uint8_t>((green * 255 + 32) / 64);
                    expected[0] = static_cast<std::uint8_t>(((32 - green) * 255 + 32) / 64);
                } else {
                    const auto stripe = ((x - 8U + 32U - offset) / (source_variant == 0U ? 16U : 8U)) & 1U;
                    expected[stripe == 1U ? 1U : source_variant == 3U ? 2U : 0U] = source_variant == 3U ? 255U : 128U;
                }
            }
            const auto* actual = images[0].data() + (y * image_extent + x) * 4U;
            const bool matches = std::equal(expected.begin(), expected.end(), actual);
            if (!matches) {
                std::fprintf(stderr,
                    "Original shader sampler policy=%s variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                    four_load ? "explicit-four-load" : "native", variant, x, y,
                    static_cast<unsigned>(actual[0]), static_cast<unsigned>(actual[1]),
                    static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]),
                    static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
                    static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]));
                if (variant >= 13U && variant <= 19U)
                    diagnose_original_shader_sampler_capture(scene, variant, x, y, expected, images[0],
                        [&](const auto& nested, const auto& capture_header, const auto& picture) {
                            return capture(four_load, nested, capture_header, picture);
                        }, require);
            }
            require(matches,
                "original sampler color/opacity/physical normalization/tile transform/final clip differs");
        }
    }
    }
    std::fprintf(stderr,
        "Original shader samplers passed: 13 unchanged cases, 7 native-reference cases, 7 strict four-load cases; 81 effect replays\n");
}
} // namespace progpu::native::tests
