#pragma once

#include "progpu_native_scene_builder.hpp"

#include <array>
#include <cstring>
#include <vector>

namespace progpu::native::tests {

// Shared wgpu-native/Dawn pixel control. All cases retain identical nested
// bytes, scene identity and legacy dpi_scale: only the optional axes differ.
// The independent oracle describes integer physical pixels, not rendered bounds.
template<class Render, class Require>
void verify_picture_axis_presentation(Render render, Require require) {
    semantic_scene_builder nested(0x9490U, 1U);
    std::uint32_t brush{};
    progpu_native_analytic_primitive rectangle{};
    rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
    rectangle.x = rectangle.y = rectangle.height = 1.0F;
    rectangle.width = 2.0F;
    rectangle.color = {1, 1, 1, 1};
    rectangle.transform = semantic_scene_builder::identity_transform();
    std::vector<std::byte> nested_bytes;
    require(nested.add_solid_brush({1, 1, 1, 1}, 1, brush) &&
        nested.draw_analytic({&rectangle, 1U}, {&brush, 1U}, {1, 1, 2, 1}) &&
        nested.build(nested_bytes), "axis picture nested source");
    constexpr std::array<std::array<unsigned, 2U>, 5U> cases{{{1, 2}, {1, 3}, {2, 1}, {1, 1}, {1, 2}}};
    std::uint64_t generation = 0U;
    for (const auto& axes : cases) {
        semantic_scene_builder parent(0x9491U, ++generation);
        progpu_native_scene_picture_image picture{};
        picture.struct_size = sizeof(picture);
        picture.width = picture.height = 8U;
        picture.dpi_scale = 1.0F; // Must not override either explicit axis.
        const bool uniform_wire = axes[0] == 1U && axes[1] == 1U;
        if (!uniform_wire) picture.flags = PROGPU_NATIVE_SCENE_PICTURE_IMAGE_PRESENTATION;
        const progpu_native_scene_presentation presentation{sizeof(presentation), 0U, 0U, 8U, 8U,
            static_cast<float>(axes[0]), static_cast<float>(axes[1]), 0U};
        std::uint32_t resource{};
        require(parent.add_picture_image(picture, uniform_wire ? nullptr : &presentation,
            nested_bytes, resource), "axis picture resource");
        progpu_native_scene_image_draw draw{};
        draw.image_width = draw.image_height = 8U;
        draw.row_bytes = 32U;
        draw.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
        draw.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
        draw.opacity = 1.0F;
        draw.transform = semantic_scene_builder::identity_transform();
        draw.source_rect = {0, 0, 8, 8};
        draw.destination_rect = {4, 4, 8, 8};
        require(parent.draw_image(resource, draw, draw.destination_rect), "axis picture full draw");
        draw.source_rect = {1, 1, 6, 6};
        draw.destination_rect = {24, 4, 6, 6};
        require(parent.draw_image(resource, draw, draw.destination_rect), "axis picture cropped draw");
        std::vector<std::byte> stream;
        require(parent.build(stream), "axis picture parent capture");
        // Ordinary adjacent draws of the same image are canonically one patch
        // batch. Prove both draws survive in that batch before checking pixels.
        progpu_native_scene_header header{};
        require(stream.size() >= sizeof(header), "axis picture header size");
        std::memcpy(&header, stream.data(), sizeof(header));
        require(header.command_count == 1U && header.resource_count == 1U &&
            header.command_offset <= stream.size() && sizeof(progpu_native_scene_command) <= stream.size() - header.command_offset,
            "axis picture canonical single image batch");
        progpu_native_scene_command command{};
        std::memcpy(&command, stream.data() + header.command_offset, sizeof(command));
        require(command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE && command.resource_index == resource &&
            command.payload_size == sizeof(draw) + sizeof(progpu_native_scene_image_patch_batch) +
                2U * sizeof(progpu_native_scene_image_patch) &&
            command.payload_offset <= stream.size() && command.payload_size <= stream.size() - command.payload_offset,
            "axis picture complete two-patch payload");
        progpu_native_scene_image_draw batched{};
        progpu_native_scene_image_patch_batch batch{};
        std::array<progpu_native_scene_image_patch, 2U> patches{};
        const auto* payload = stream.data() + command.payload_offset;
        std::memcpy(&batched, payload, sizeof(batched));
        std::memcpy(&batch, payload + sizeof(batched), sizeof(batch));
        std::memcpy(patches.data(), payload + sizeof(batched) + sizeof(batch), sizeof(patches));
        require(batched.flags == (PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED | PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH) &&
            batch.struct_size == sizeof(batch) && batch.patch_count == 2U,
            "axis picture batch retains both draws");
        const std::array<progpu_native_image_rect, 2U> sources{{{0, 0, 8, 8}, {1, 1, 6, 6}}};
        const std::array<progpu_native_image_rect, 2U> destinations{{{4, 4, 8, 8}, {24, 4, 6, 6}}};
        for (std::size_t i = 0U; i < patches.size(); ++i)
            require(patches[i].struct_size == sizeof(patches[i]) && patches[i].kind == PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE &&
                std::memcmp(&patches[i].source_rect, &sources[i], sizeof(sources[i])) == 0 &&
                std::memcmp(&patches[i].destination_rect, &destinations[i], sizeof(destinations[i])) == 0,
                "axis picture batch changed original full/cropped placement");
        const auto cold = render(false, stream, generation, 2U);
        const auto warm = render(false, stream, generation, 1U);
        const auto independent = render(true, stream, generation, 2U);
        require(cold == warm && cold == independent && cold.size() == 64U * 64U * 4U,
            "axis picture cold/warm/independent replay differs");
        for (unsigned y = 0U; y < 64U; ++y) {
            for (unsigned x = 0U; x < 64U; ++x) {
                const auto ink = [&](unsigned left, unsigned top, unsigned source_x, unsigned source_y,
                                     unsigned extent) {
                    if (x < left || y < top || x >= left + extent || y >= top + extent) return false;
                    const auto sx = x - left + source_x, sy = y - top + source_y;
                    return sx >= axes[0] && sx < 3U * axes[0] && sy >= axes[1] && sy < 2U * axes[1];
                };
                const auto expected = ink(4U, 4U, 0U, 0U, 8U) || ink(24U, 4U, 1U, 1U, 6U) ? 255U : 0U;
                const auto* pixel = cold.data() + (y * 64U + x) * 4U;
                require(pixel[0] == expected && pixel[1] == expected && pixel[2] == expected && pixel[3] == 255U,
                    "axis picture physical crop, destination or retained cache identity differs");
            }
        }
    }
}

} // namespace progpu::native::tests
