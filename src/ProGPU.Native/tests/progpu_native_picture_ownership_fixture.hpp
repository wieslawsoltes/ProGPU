#pragma once

#include "progpu_native_scene_builder.hpp"

#include <array>
#include <vector>

namespace progpu::native::tests {

// Shared wgpu-native/Dawn control. All resource records and generations have
// identical shape; only the real owner and grayscale payload change. Opaque
// integer grayscale keeps the independent RGBA/BGRA oracle exact.
template<class Render, class Require>
void verify_picture_resource_ownership(Render render, Require require) {
    struct capture_case { std::uint64_t owner; unsigned channel; unsigned submissions; };
    constexpr std::array cases{
        capture_case{0x94A0U, 255U, 2U}, capture_case{0x94A1U, 128U, 2U},
        capture_case{0x94A1U, 64U, 2U}, capture_case{0x94A0U, 255U, 1U},
        capture_case{0x94A0U, 192U, 2U}};
    std::uint64_t generation = 100U;
    for (const auto& capture : cases) {
        semantic_scene_builder nested(capture.owner, 1U);
        std::uint32_t brush{};
        progpu_native_analytic_primitive rectangle{};
        rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        rectangle.width = rectangle.height = 8.0F;
        rectangle.color = {1, 1, 1, 1};
        rectangle.transform = semantic_scene_builder::identity_transform();
        const float channel = static_cast<float>(capture.channel) / 255.0F;
        std::vector<std::byte> nested_bytes;
        require(nested.add_solid_brush({channel, channel, channel, 1}, 1, brush) &&
            nested.draw_analytic({&rectangle, 1U}, {&brush, 1U}, {0, 0, 8, 8}) &&
            nested.build(nested_bytes), "picture resource owner capture");
        semantic_scene_builder parent(0x9491U, ++generation);
        const progpu_native_scene_picture_image picture{sizeof(picture), 0U, 8U, 8U,
            1, {0U, 0U, 0U}, {0, 0, 0, 0}};
        std::uint32_t resource{};
        require(parent.add_picture_image(picture, nested_bytes, resource), "picture resource owner image");
        progpu_native_scene_image_draw draw{};
        draw.image_width = draw.image_height = 8U;
        draw.row_bytes = 32U;
        draw.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
        draw.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
        draw.max_anisotropy = 1U;
        draw.source_rect = {0, 0, 8, 8};
        draw.destination_rect = {4, 4, 8, 8};
        draw.transform = semantic_scene_builder::identity_transform();
        draw.opacity = 1;
        std::vector<std::byte> stream;
        require(parent.draw_image(resource, draw, draw.destination_rect) && parent.build(stream),
            "picture resource owner parent");
        const auto cold = render(false, stream, generation, capture.submissions);
        const auto warm = render(false, stream, generation, 1U);
        const auto independent = render(true, stream, generation, capture.submissions);
        require(cold == warm && cold == independent && cold.size() == 64U * 64U * 4U,
            "picture resource owner cold/warm/independent mismatch");
        for (unsigned y = 0U; y < 64U; ++y) {
            for (unsigned x = 0U; x < 64U; ++x) {
                const auto expected = x >= 4U && x < 12U && y >= 4U && y < 12U ? capture.channel : 0U;
                const auto* pixel = cold.data() + (y * 64U + x) * 4U;
                require(pixel[0] == expected && pixel[1] == expected && pixel[2] == expected && pixel[3] == 255U,
                    "picture resource owner reused a different payload");
            }
        }
    }
}

} // namespace progpu::native::tests
