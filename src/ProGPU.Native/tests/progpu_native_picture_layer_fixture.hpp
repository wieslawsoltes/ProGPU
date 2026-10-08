#pragma once

#include "progpu_native_scene_builder.hpp"

#include <array>
#include <cstring>
#include <vector>

namespace progpu::native::tests {

template<class Render, class Require>
void verify_picture_layer_rejections(Render render, Require require) {
    for (unsigned variant = 0U; variant < 9U; ++variant) {
        semantic_scene_builder child(0x94C0U + variant, 1U);
        if (variant == 7U) {
            progpu_native_matrix_4x4 identity{};
            identity.m11 = identity.m22 = identity.m33 = identity.m44 = 1.0F;
            progpu_native_scene_camera_3d camera{};
            camera.struct_size = sizeof(camera);
            camera.view = camera.projection = identity;
            progpu_native_scene_line_3d line{};
            line.struct_size = sizeof(line);
            line.start = {-0.5F, 0, 0, 0};
            line.end = {0.5F, 0, 0, 0};
            line.color = {1, 1, 1, 1};
            line.thickness = line.opacity = 1.0F;
            line.transform = identity;
            require(child.draw_lines_3d({&line, 1U}, camera, {0, 0, 4, 4}), "mapped rejected 3D record");
        } else {
            progpu_native_scene_layer layer{};
            layer.struct_size = sizeof(layer);
            layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
            layer.bounds = {0, 0, 4, 4};
            layer.opacity = 1.0F;
            layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            layer.mask_resource_index = layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            if (variant <= 1U) {
                layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT;
                layer.content_revision = layer.composite_revision = 1U;
                if (variant == 1U) layer.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE;
            }
            if (variant == 1U || variant == 2U) {
                if (variant == 2U) layer.flags |= PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE;
                progpu_native_scene_state state{};
                state.struct_size = sizeof(state);
                state.transform = semantic_scene_builder::identity_transform();
                state.opacity = 1.0F;
                state.mask_resource_index = state.guideline_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                require(child.add_state(state, layer.reserved0), "mapped rejected composite state");
            }
            if (variant == 3U) layer.flags |= PROGPU_NATIVE_SCENE_LAYER_BACKDROP;
            if (variant == 4U) {
                progpu_native_group_effect blur{};
                blur.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
                blur.revision = 1U;
                blur.sigma_x = blur.sigma_y = 1.0F;
                require(child.add_effect_chain({&blur, 1U}, 1U, layer.effect_resource_index), "mapped rejected effect");
            }
            if (variant == 5U) {
                progpu_native_scene_layer_mask mask{};
                mask.bounds = layer.bounds;
                mask.transform = semantic_scene_builder::identity_transform();
                mask.opacity = 1.0F;
                require(child.add_rounded_rectangle_mask(mask, layer.mask_resource_index), "mapped rejected layer mask");
            }
            if (variant == 6U) layer.blend_mode = PROGPU_NATIVE_BLEND_MULTIPLY;
            if (variant == 8U) {
                // Direct-root independent DPI admission must not open the
                // still-unqualified mapped picture layer-mask contract.
                layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
                require(child.add_axis_aligned_clip_mask(layer.bounds, layer.mask_resource_index),
                    "mapped rejected explicit axis clip");
            }
            std::uint32_t brush{};
            progpu_native_analytic_primitive rectangle{};
            rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
            rectangle.width = rectangle.height = 4.0F;
            rectangle.color = {1, 1, 1, 1};
            rectangle.transform = semantic_scene_builder::identity_transform();
            require(child.add_solid_brush({1, 1, 1, 1}, 1.0F, brush) && child.push_layer(layer) &&
                child.draw_analytic({&rectangle, 1U}, {&brush, 1U}, layer.bounds) && child.pop_layer(),
                "mapped rejected layer record");
        }
        std::vector<std::byte> nested;
        require(child.build(nested), "mapped rejected nested capture must remain valid");
        const auto generation = 300U + variant;
        semantic_scene_builder parent(0x9491U, generation);
        progpu_native_scene_picture_image picture{};
        picture.struct_size = sizeof(picture);
        picture.flags = PROGPU_NATIVE_SCENE_PICTURE_IMAGE_PRESENTATION;
        picture.width = picture.height = 16U;
        picture.dpi_scale = 1.0F;
        const progpu_native_scene_presentation presentation{sizeof(presentation), 0U, 0U, 16U, 16U, 2.0F, 1.0F, 0U};
        std::uint32_t resource{};
        progpu_native_scene_image_draw image{};
        image.image_width = image.image_height = 16U;
        image.row_bytes = 64U;
        image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
        image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
        image.max_anisotropy = 1U;
        image.opacity = 1.0F;
        image.transform = semantic_scene_builder::identity_transform();
        image.source_rect = image.destination_rect = {0, 0, 16, 16};
        std::vector<std::byte> stream;
        require(parent.add_picture_image(picture, &presentation, nested, resource) &&
            parent.draw_image(resource, image, image.destination_rect) && parent.build(stream),
            "mapped rejected parent capture must remain valid");
        // Zero requests an explicit render rejection, never a relaxed count.
        require(render(false, stream, generation, 0U).empty(), "mapped prohibited contract was admitted");
    }
}

// Actual nested captures with two materialized layers, a per-draw clip and
// transparent SRC replacement. The independent scene records physical geometry
// at uniform DPI; the integer oracle does not use renderer mapping helpers.
template<class Render, class Require>
void verify_picture_layer_presentation(Render render, Require require) {
    semantic_scene_builder leaf(0x94B2U, 1U);
    std::array<std::byte, 4U * 4U * 4U> pixels{};
    for (unsigned y = 0U; y < 4U; ++y) {
        for (unsigned x = 0U; x < 2U; ++x) {
            const auto offset = (y * 4U + x) * 4U;
            pixels[offset] = pixels[offset + 1U] = pixels[offset + 2U] = std::byte{128};
            pixels[offset + 3U] = std::byte{255};
        }
    }
    progpu_native_scene_image_draw image{};
    image.image_width = image.image_height = 4U;
    image.row_bytes = 16U;
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    image.max_anisotropy = 1U;
    image.opacity = 1.0F;
    image.transform = semantic_scene_builder::identity_transform();
    image.source_rect = image.destination_rect = {0, 0, 4, 4};
    std::uint32_t leaf_resource{};
    std::vector<std::byte> leaf_bytes;
    require(leaf.add_rgba8_image(4U, 4U, 16U, pixels, leaf_resource) &&
        leaf.draw_image(leaf_resource, image, image.destination_rect) && leaf.build(leaf_bytes),
        "mapped layer leaf capture");
    constexpr std::array<std::array<unsigned, 3U>, 6U> cases{{
        {2, 1, 1}, {1, 2, 1}, {2, 3, 1}, {1, 1, 1}, {2, 1, 0}, {2, 1, 1}}};
    std::uint64_t generation = 200U;
    for (const auto& axes : cases) {
        ++generation;
        const auto make_parent = [&](bool physical_reference) {
            const float sx = physical_reference ? static_cast<float>(axes[0]) : 1.0F;
            const float sy = physical_reference ? static_cast<float>(axes[1]) : 1.0F;
            const auto scaled = [&](float x, float y, float rectangle_width, float rectangle_height) {
                return progpu_native_image_rect{x * sx, y * sy, rectangle_width * sx, rectangle_height * sy};
            };
            semantic_scene_builder middle(physical_reference ? 0x94B1U : 0x94B0U, generation);
            std::uint32_t white{};
            require(middle.add_solid_brush({1, 1, 1, 1}, 1.0F, white), "mapped layer brush");
            progpu_native_scene_layer layer{};
            layer.struct_size = sizeof(layer);
            layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
            layer.bounds = scaled(1, 1, 6, 6);
            layer.opacity = static_cast<float>(axes[2]);
            layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            layer.mask_resource_index = layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            require(middle.push_layer(layer), "mapped outer isolation");
            progpu_native_analytic_primitive rectangle{};
            rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
            rectangle.x = layer.bounds.x;
            rectangle.y = layer.bounds.y;
            rectangle.width = layer.bounds.width;
            rectangle.height = layer.bounds.height;
            rectangle.color = {1, 1, 1, 1};
            rectangle.transform = semantic_scene_builder::identity_transform();
            require(middle.draw_analytic({&rectangle, 1U}, {&white, 1U}, layer.bounds), "mapped isolated content");
            layer.bounds = scaled(2, 2, 4, 3);
            layer.opacity = 1.0F;
            layer.blend_mode = PROGPU_NATIVE_BLEND_SRC;
            require(middle.push_layer(layer), "mapped SRC replacement");
            progpu_native_scene_state state{};
            state.struct_size = sizeof(state);
            state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
            state.transform = semantic_scene_builder::identity_transform();
            state.opacity = 1.0F;
            state.clip_rect = scaled(3, 2, 3, 2);
            state.mask_resource_index = state.guideline_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            std::uint32_t clip{}, nested{};
            progpu_native_scene_picture_image picture{};
            picture.struct_size = sizeof(picture);
            picture.width = picture.height = 4U;
            picture.dpi_scale = 1.0F;
            auto nested_image = image;
            nested_image.destination_rect = scaled(2, 2, 4, 4);
            require(middle.add_state(state, clip) && middle.add_picture_image(picture, leaf_bytes, nested) &&
                middle.draw_image(nested, nested_image, nested_image.destination_rect, clip) &&
                middle.pop_layer() && middle.pop_layer(), "mapped clipped nested picture");
            std::vector<std::byte> middle_bytes;
            require(middle.build(middle_bytes), "mapped middle capture");
            progpu_native_scene_header middle_header{};
            std::memcpy(&middle_header, middle_bytes.data(), sizeof(middle_header));
            require(middle_header.command_count == 6U, "mapped layers must not be flattened");
            semantic_scene_builder parent(0x9491U, generation);
            picture.width = picture.height = 16U;
            picture.clear_color = {64.0F / 255.0F, 64.0F / 255.0F, 64.0F / 255.0F, 1.0F};
            // Keep the shader raster basis fixed and independent of both axes.
            picture.dpi_scale = physical_reference ? 1.0F : 2.0F;
            picture.flags = physical_reference ? 0U :
                static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_PICTURE_IMAGE_PRESENTATION);
            const progpu_native_scene_presentation presentation{sizeof(presentation), 0U, 0U, 16U, 16U,
                static_cast<float>(axes[0]), static_cast<float>(axes[1]), 0U};
            require(parent.add_picture_image(picture, physical_reference ? nullptr : &presentation,
                middle_bytes, nested), "mapped isolated picture descriptor");
            auto output = image;
            output.image_width = output.image_height = 16U;
            output.row_bytes = 64U;
            output.source_rect = {0, 0, 16, 16};
            output.destination_rect = {4, 4, 16, 16};
            require(parent.draw_image(nested, output, output.destination_rect), "mapped full picture placement");
            output.source_rect = {2, 1, 10, 11};
            output.destination_rect = {30, 4, 10, 11};
            require(parent.draw_image(nested, output, output.destination_rect), "mapped cropped picture placement");
            std::vector<std::byte> stream;
            require(parent.build(stream), "mapped parent capture");
            progpu_native_scene_header header{};
            std::memcpy(&header, stream.data(), sizeof(header));
            require(header.command_count == 1U && header.resource_count == 1U,
                "mapped placements must retain one original picture batch");
            return stream;
        };
        const auto stream = make_parent(false), reference = make_parent(true);
        const auto submissions = generation == 201U ? 3U : 2U;
        const auto cold = render(false, stream, generation, submissions);
        const auto warm = render(false, stream, generation, 1U);
        const auto independent = render(true, reference, generation, submissions);
        require(cold == warm && cold == independent && cold.size() == 64U * 64U * 4U,
            "mapped layer differs from uniform physical replay or warm capture");
        for (unsigned y = 0U; y < 64U; ++y) {
            for (unsigned x = 0U; x < 64U; ++x) {
                const auto sample = [&](unsigned px, unsigned py) {
                    const auto inside = [&](unsigned left, unsigned top, unsigned right, unsigned bottom) {
                        return px >= left * axes[0] && px < right * axes[0] &&
                            py >= top * axes[1] && py < bottom * axes[1];
                    };
                    if (axes[2] == 0U || !inside(1, 1, 7, 7)) return 64U;
                    if (!inside(2, 2, 6, 5)) return 255U;
                    // The clip removes the first column/last row, and the
                    // nested texture's right half is genuinely transparent.
                    return inside(3, 2, 4, 4) ? 128U : 64U;
                };
                unsigned expected = 0U;
                if (x >= 4U && x < 20U && y >= 4U && y < 20U) expected = sample(x - 4U, y - 4U);
                if (x >= 30U && x < 40U && y >= 4U && y < 15U) expected = sample(x - 30U + 2U, y - 4U + 1U);
                const auto* actual = cold.data() + (y * 64U + x) * 4U;
                require(actual[0] == expected && actual[1] == expected && actual[2] == expected && actual[3] == 255U,
                    "mapped layer physical bounds, clip, transparency or source crop differs");
            }
        }
    }
    verify_picture_layer_rejections(render, require);
}

} // namespace progpu::native::tests
