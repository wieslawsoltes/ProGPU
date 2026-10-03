#pragma once

#include "progpu_native_shader_drawing_image_fixture.hpp"

namespace progpu::native::tests {

inline constexpr std::array<std::uint32_t, 3U> drawing_image_brush_cases{0U, 2U, 7U};

inline bool initialize_ordinary_drawing_image_brush(progpu_native_mil_channel* channel) {
    if (!initialize_shader_drawing_image(channel)) return false;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    std::vector<std::byte> batch, content;
    packet(batch, mil::command::visual_set_effect, 1U, 0U);
    packet(batch, mil::command::visual_set_offset, 1U, 8.0, 10.0);
    packet(content, mil::command::draw_rectangle, 0.0, 0.0, 32.0, 24.0, 5U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(mil::command::render_data));
    append(batch, 2U); append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    return progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U, 0.0, 0.0, 32.0, 24.0) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

template<class Require>
void build_ordinary_drawing_image_brush(progpu_native_mil_channel* channel, bool explicit_bounds,
    std::uint32_t index, std::vector<std::byte>& scene, Require require) {
    if (explicit_bounds)
        require(progpu_native_mil_channel_set_drawing_image_bounds(channel, 40U,
            index == 7U ? -6.0 : 10.0, index == 7U ? 9.0 : 20.0, 8.0, 6.0) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
            "ordinary DrawingImage exact source bounds rejected");
    const progpu_native_mil_scene_build_request request{sizeof(request), 0U, 4U, 0U,
        explicit_bounds ? 0xD2A73U : 0xD2A72U, index + 1U, 1.0, 1.0, 0U, index + 1U};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    progpu_native_mil_scene_metrics source{}; source.struct_size = sizeof(source);
    std::size_t written{};
    require(progpu_native_mil_channel_build_scene_with_request(channel, &request, nullptr, 0U, &written, &source, &result) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS, "ordinary DrawingImage scene measure failed");
    scene.resize(written);
    require(progpu_native_mil_channel_build_scene_with_request(channel, &request, scene.data(), scene.size(), &written,
        &source, &result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size(),
        "ordinary DrawingImage immutable scene copy failed");
    require(source.visual_count == 1U && source.rectangle_count == 3U && source.brush_count == 2U,
        "ordinary DrawingImage original visual/paint/two-child ownership changed");
}

template<class Render, class Require>
void verify_ordinary_drawing_image_brush_pixels(Render render, Require require) {
    std::array<std::vector<std::uint8_t>, drawing_image_brush_cases.size()> inferred;
    for (const bool explicit_bounds : {false, true}) {
        std::array<std::vector<std::byte>, drawing_image_brush_cases.size()> scenes;
        {
            progpu_native_mil_channel* raw{};
            require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "ordinary DrawingImage channel creation failed");
            mil_clip_channel owner(raw);
            require(initialize_ordinary_drawing_image_brush(raw), "ordinary DrawingImage source initialization failed");
            std::size_t capture{};
            // Traverse the real same-owner null/empty/refill sequence even
            // though only the three required nonempty generations are painted.
            for (std::uint32_t index = 0U; index <= 7U; ++index) {
                require(update_shader_drawing_image(raw, index), "ordinary DrawingImage retained mutation failed");
                if (index == drawing_image_brush_cases[capture]) {
                    build_ordinary_drawing_image_brush(raw, explicit_bounds, index, scenes[capture], require);
                    ++capture;
                }
            }
            require(capture == scenes.size(), "ordinary DrawingImage capture inventory changed");
        }
        // Both source graphs and all mutable Drawing resources are retired.
        for (std::size_t capture = 0U; capture < scenes.size(); ++capture) {
            const auto index = drawing_image_brush_cases[capture];
            const auto& scene = scenes[capture];
            require(scene.size() >= sizeof(progpu_native_scene_header), "ordinary DrawingImage header missing");
            progpu_native_scene_header header{};
            std::memcpy(&header, scene.data(), sizeof(header));
            // Source-order saves: visual, DrawingImage mapping, DrawingGroup;
            // one isolated vector tile and two original geometry draws.
            constexpr std::array kinds{
                PROGPU_NATIVE_SCENE_COMMAND_SAVE, PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
                PROGPU_NATIVE_SCENE_COMMAND_SAVE, PROGPU_NATIVE_SCENE_COMMAND_SAVE,
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC, PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
                PROGPU_NATIVE_SCENE_COMMAND_RESTORE, PROGPU_NATIVE_SCENE_COMMAND_RESTORE,
                PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER, PROGPU_NATIVE_SCENE_COMMAND_RESTORE};
            require(header.command_count == kinds.size() && header.command_offset <= scene.size() &&
                kinds.size() * sizeof(progpu_native_scene_command) <= scene.size() - header.command_offset,
                "ordinary DrawingImage source command framing changed");
            for (std::size_t command = 0U; command < kinds.size(); ++command) {
                progpu_native_scene_command record{};
                std::memcpy(&record, scene.data() + header.command_offset + command * sizeof(record), sizeof(record));
                require(record.kind == static_cast<std::uint32_t>(kinds[command]),
                    "ordinary DrawingImage source order/isolation changed");
            }
            std::array<std::vector<std::uint8_t>, 3U> images;
            for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
                progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
                images[replay] = render(explicit_bounds, replay == 2U, scene, header, frame);
                // Adjacent analytic children share one actual draw; restoring
                // the forced vector tile adds one composite. There is no
                // picture/effect subengine or separate source submission.
                require(frame.command_count == 10U && frame.draw_call_count == 2U && frame.submission_count == 1U,
                    "ordinary DrawingImage actual draw/submission count changed");
            }
            require(images[0].size() == 64U * 64U * 4U && images[0] == images[1] && images[0] == images[2],
                "ordinary DrawingImage cold/warm/independent frames differ");
            if (explicit_bounds)
                require(images[0] == inferred[capture], "ordinary DrawingImage source/inferred bounds differ");
            else inferred[capture] = images[0];
            for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
                std::array<std::uint8_t, 4U> expected{0U, 0U, 0U, 255U};
                if (x >= 8U && x < 40U && y >= 10U && y < 34U) {
                    const auto column = x - 8U;
                    if (index != 2U || (column >= 8U && column < 24U))
                        expected[index == 2U || column >= 16U ? 1U : index == 7U ? 2U : 0U] = 255U;
                }
                const auto* actual = images[0].data() + (y * 64U + x) * 4U;
                if (!std::equal(expected.begin(), expected.end(), actual))
                    std::fprintf(stderr, "Ordinary DrawingImage explicit=%u case=%u pixel=(%u,%u) RGBA=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                        static_cast<unsigned>(explicit_bounds), index, x, y, static_cast<unsigned>(actual[0]),
                        static_cast<unsigned>(actual[1]), static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]),
                        static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
                        static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]));
                require(std::equal(expected.begin(), expected.end(), actual), "ordinary DrawingImage independent full-frame pixels differ");
            }
        }
    }
}

} // namespace progpu::native::tests
