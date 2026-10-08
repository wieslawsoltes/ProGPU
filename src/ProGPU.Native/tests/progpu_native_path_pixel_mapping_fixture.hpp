#pragma once
#include "progpu_native_scene_builder.hpp"

#include <array>
#include <cstdio>
#include <vector>

namespace progpu::native::tests {

// Original ProGPU fixture, paired with CompositorClipTests. A scalar rectangle
// membership oracle checks every channel, independent of atlas/Boolean shaders.
inline bool build_path_pixel_mapping_fixture(bool clip, std::vector<std::byte>& stream)
{
    semantic_scene_builder builder(clip ? 0x9482U : 0x9481U, 1U);
    const auto identity = semantic_scene_builder::identity_transform();
    std::array<progpu_native_path_segment, 8> segments{};
    constexpr std::array<progpu_native_point, 8> points{{
        {0, 0}, {32, 0}, {32, 24}, {0, 24},
        {8, 4}, {40, 4}, {40, 20}, {8, 20}}};
    for (std::size_t i = 0; i < points.size(); ++i)
        segments[i] = {points[i], points[(i / 4) * 4 + (i + 1) % 4], {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U};
    std::array<progpu_native_scene_path_boolean_node, 3> nodes{};
    nodes[0] = {0U, 4U, 0, 0, 32, 24, PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U};
    nodes[1] = {4U, 4U, 8, 4, 40, 20, PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U};
    nodes[2].kind = PROGPU_NATIVE_PATH_BOOLEAN_UNION;
    progpu_native_analytic_primitive rectangle{};
    rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
    rectangle.width = rectangle.height = 64;
    rectangle.color = {1, 1, 1, 1};
    rectangle.transform = identity;
    constexpr progpu_native_image_rect bounds{0, 0, 64, 64};
    std::uint32_t white{}, red{};
    if (!builder.add_solid_brush({1, 1, 1, 1}, 1, white) ||
        !builder.add_solid_brush({1, 0, 0, 1}, 1, red) ||
        !builder.draw_analytic({&rectangle, 1U}, {&white, 1U}, bounds)) return false;
    auto transform = identity;
    transform.m31 = 8;
    transform.m32 = 40;
    if (clip) {
        const progpu_native_scene_clip_path path{0U, 8U, 0U, 3U,
            0, 0, 40, 24, transform, PROGPU_NATIVE_FILL_RULE_NON_ZERO, 4U,
            PROGPU_NATIVE_CLIP_INTERSECT, 0U};
        auto state = semantic_scene_builder::identity_state();
        state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
        std::uint32_t state_index{};
        if (!builder.add_vector_clip_mask({&path, 1U}, segments, nodes, 1,
                state.mask_resource_index) || !builder.add_state(state, state_index) ||
            !builder.draw_analytic({&rectangle, 1U}, {&red, 1U}, bounds, state_index)) return false;
    } else {
        const progpu_native_scene_path_fill path{0U, 8U, 0U, 3U,
            0, 0, 40, 24, {1, 1, 1, 1}, transform, PROGPU_NATIVE_FILL_RULE_NON_ZERO, 4U};
        if (!builder.draw_paths({&path, 1U}, segments, {&red, 1U}, bounds,
                PROGPU_NATIVE_SCENE_NO_INDEX, nodes)) return false;
    }
    return builder.build(stream);
}

template<typename Render, typename Require>
void verify_path_pixel_mapping(Render render, Require require)
{
    for (bool clip : {false, true}) {
        std::vector<std::byte> stream;
        require(build_path_pixel_mapping_fixture(clip, stream), "pixel mapping fixture construction failed");
        for (const float dpi : {1.0F, 2.0F}) for (unsigned frame = 0; frame < 2; ++frame) {
            const auto extent = static_cast<unsigned>(64.0F * dpi);
            progpu_native_scene_frame_metrics metrics{};
            const auto pixels = render(clip, stream, dpi, extent, metrics);
            require(pixels.size() == extent * extent * 4U, "pixel mapping readback size changed");
            if (frame != 0U)
                require(metrics.coverage_staging_bytes == 0U && metrics.vertex_upload_bytes == 0U &&
                    metrics.index_upload_bytes == 0U, "warm pixel mapping rebuilt retained geometry");
            for (unsigned y = 0; y < extent; ++y) {
                for (unsigned x = 0; x < extent; ++x) {
                    const auto px = (static_cast<float>(x) + .5F) / dpi;
                    const auto py = (static_cast<float>(y) + .5F) / dpi;
                    const bool inside = (px >= 8 && px < 40 && py >= 40 && py < 64) ||
                        (px >= 16 && px < 48 && py >= 44 && py < 60);
                    const std::array<std::uint8_t, 4> expected{{255,
                        static_cast<std::uint8_t>(inside ? 0 : 255),
                        static_cast<std::uint8_t>(inside ? 0 : 255), 255}};
                    for (unsigned channel = 0; channel < 4; ++channel) {
                        const auto actual = pixels[(y * extent + x) * 4U + channel];
                        if (actual != expected[channel]) {
                            std::fprintf(stderr, "Pixel mapping clip=%u dpi=%g frame=%u (%u,%u) channel=%u actual=%u expected=%u\n",
                                clip ? 1U : 0U, static_cast<double>(dpi), frame, x, y, channel, actual, expected[channel]);
                            require(false, "integer-translated union changed a pixel");
                        }
                    }
                }
            }
            std::fprintf(stderr, "Pixel mapping clip=%u dpi=%g frame=%u submissions=%llu coverage=%llu exact pixels passed\n",
                clip ? 1U : 0U, static_cast<double>(dpi), frame, static_cast<unsigned long long>(metrics.submission_count),
                static_cast<unsigned long long>(metrics.coverage_staging_bytes));
        }
    }
}
} // namespace progpu::native::tests
