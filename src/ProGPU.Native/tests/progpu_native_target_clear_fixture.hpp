#pragma once

#include "progpu_native_scene.hpp"
#include "progpu_native_scene_builder.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace progpu::native::tests {

template<class Require>
std::vector<std::byte> target_clear_scene(unsigned variant, Require require) {
    semantic_scene_builder builder(0x96D8U + variant, 1U);
    const auto rectangle = [&](progpu_native_color color, progpu_native_image_rect bounds) {
        progpu_native_analytic_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        primitive.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED;
        primitive.x = bounds.x; primitive.y = bounds.y;
        primitive.width = bounds.width; primitive.height = bounds.height;
        primitive.color = color; primitive.transform = semantic_scene_builder::identity_transform();
        require(builder.draw_analytic({&primitive, 1U}, {}, bounds), "target Clear ordered rectangle");
    };
    const auto layer = [&](bool opaque, progpu_native_image_rect bounds) {
        progpu_native_scene_layer value{};
        value.struct_size = sizeof(value);
        value.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
            (opaque ? static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA) : 0U);
        value.bounds = bounds; value.opacity = 1.0F; value.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
        value.mask_resource_index = value.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        require(builder.push_layer(value), "target Clear actual isolated attachment");
    };
    if (variant == 0U || variant == 1U) {
        require(builder.clear_target(variant == 0U ? progpu_native_color{0, 1, 0, 1} : progpu_native_color{}),
            "target Clear resource-free root command");
    } else if (variant == 2U || variant == 3U || variant == 7U || variant >= 9U) {
        if (variant == 2U) rectangle({1, 0, 0, 1}, {0, 0, 64, 64});
        auto state = semantic_scene_builder::identity_state();
        // The collapsed basis, zero source opacity and actual per-point
        // guidelines cannot erase/translate target-storage replacement.
        state.transform = {}; state.opacity = 0.0F;
        state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT | PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
        constexpr std::array guides{.25, 19.75};
        require(builder.add_guideline_set(guides, guides, state.guideline_resource_index, false, true),
            "target Clear original guideline state");
        state.clip_rect = variant == 3U ? progpu_native_image_rect{0, 0, 0, 0} :
            variant == 7U ? progpu_native_image_rect{4, 6, 8, 10} : progpu_native_image_rect{8, 10, 12, 14};
        if (variant == 10U) state.clip_rect = {8.25F, 10.75F, 12, 13};
        if (variant == 11U) state.clip_rect = {8.5F, 10.5F, 12, 14};
        if (variant == 12U) state.clip_rect = {std::nextafter(8.5F, 9.0F), std::nextafter(10.5F, 11.0F), 2, 2};
        if (variant == 13U) state.clip_rect = {std::nextafter(8.5F, 8.0F), std::nextafter(10.5F, 10.0F), 2, 2};
        if (variant == 14U) state.clip_rect = {4.25F, 6.25F, 8, 10};
        if (variant == 9U) {
            progpu_native_scene_layer_mask mask{};
            mask.struct_size = sizeof(mask); mask.bounds = {0, 0, 32, 32}; mask.opacity = 1;
            mask.transform = semantic_scene_builder::identity_transform();
            require(builder.add_rounded_rectangle_mask(mask, state.mask_resource_index), "target Clear rejected mask setup");
            state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK;
        }
        std::uint32_t state_index{};
        require(builder.add_state(state, state_index) && builder.save(state_index) &&
            builder.clear_target(variant == 2U ? progpu_native_color{} : progpu_native_color{0, 1, 0, 1}) &&
            builder.restore(), "target Clear ignores source transform/opacity/guidelines but retains clip");
        if (variant == 2U) rectangle({0, 0, 1, 1}, {10, 12, 4, 4});
    } else if (variant >= 4U && variant <= 6U) {
        rectangle({0, 0, 1, 1}, {0, 0, 64, 64});
        layer(variant != 4U, {8, 10, 16, 12});
        require(builder.clear_target(variant == 6U ? progpu_native_color{1, 1, 1, 1} : progpu_native_color{}),
            "target Clear inner attachment alpha policy");
        if (variant == 6U) {
            layer(false, {12, 14, 8, 4});
            require(builder.clear_target({1, 0, 0, 0}) && builder.pop_layer(),
                "target Clear must not inherit opaque ancestor target identity");
        }
        require(builder.pop_layer(), "target Clear source pop ordering");
    } else {
        require(variant == 8U, "target Clear fixture variant");
        semantic_scene_builder nested(0x96E8U, 1U);
        std::vector<std::byte> child;
        require(nested.clear_target({0, 1, 0, 1}) && nested.build(child), "target Clear-only child capture");
        progpu_native_scene_picture_image picture{};
        picture.struct_size = sizeof(picture); picture.width = picture.height = 16U; picture.dpi_scale = 1.0F;
        std::uint32_t resource{};
        require(builder.add_picture_image(picture, child, resource), "target Clear-only child ownership");
        child.assign(child.size(), std::byte{0xA5});
        progpu_native_scene_image_draw draw{};
        draw.image_width = draw.image_height = 16U; draw.row_bytes = 64U;
        draw.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST; draw.max_anisotropy = 1U;
        draw.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
        draw.opacity = 1; draw.transform = semantic_scene_builder::identity_transform();
        draw.source_rect = {0, 0, 16, 16}; draw.destination_rect = {8, 10, 16, 16};
        require(builder.draw_image(resource, draw, draw.destination_rect), "target Clear-only picture draw");
    }
    std::vector<std::byte> bytes;
    require(builder.build(bytes), "target Clear immutable scene publication");
    return bytes;
}

template<class Render, class Require>
void verify_native_target_clear(Render render, Require require) {
    for (unsigned variant = 0U; variant < 15U; ++variant) {
        const auto bytes = target_clear_scene(variant, require);
        const auto validated = scene::validate(bytes.data(), bytes.size());
        require(validated.status == PROGPU_NATIVE_STATUS_SUCCESS, "target Clear wire validation");
        std::vector<std::uint8_t> cold;
        const progpu_native_scene_presentation mapped{sizeof(mapped), 5U, 7U, 40U, 42U, 1.25F, 1.5F, 0U};
        for (unsigned replay = 0U; replay < 2U; ++replay) {
            progpu_native_scene_frame_metrics metrics{}; metrics.struct_size = sizeof(metrics);
            const auto pixels = render(bytes, validated.header, validated.draw_count,
                variant == 8U && replay == 0U ? 2U : 1U,
                variant == 7U || variant == 14U ? &mapped : nullptr,
                variant == 9U ? PROGPU_NATIVE_STATUS_UNSUPPORTED : PROGPU_NATIVE_STATUS_SUCCESS, metrics);
            if (variant == 9U) {
                require(pixels.empty() && metrics.submission_count == 0U, "per-draw target Clear mask submitted before rejection");
                continue;
            }
            require(pixels.size() == 64U * 64U * 4U, "target Clear full readback size");
            if (replay == 0U) cold = pixels;
            else require(pixels == cold, "target Clear warm replay changed source-order bytes");
            for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 64U; ++x) {
                std::array<std::uint8_t, 4U> expected{0, 0, 0, 255};
                if (variant == 0U) expected = {0, 255, 0, 255};
                if (variant == 1U) expected = {0, 0, 0, 0};
                if (variant == 2U) {
                    expected = {255, 0, 0, 255};
                    if (x >= 8U && x < 20U && y >= 10U && y < 24U) expected = {0, 0, 0, 0};
                    if (x >= 10U && x < 14U && y >= 12U && y < 16U) expected = {0, 0, 255, 255};
                }
                if (variant >= 4U && variant <= 6U) {
                    expected = {0, 0, 255, 255};
                    if (x >= 8U && x < 24U && y >= 10U && y < 22U && variant != 4U)
                        expected = variant == 5U ? std::array<std::uint8_t, 4U>{0, 0, 0, 255} :
                            std::array<std::uint8_t, 4U>{255, 255, 255, 255};
                }
                if (variant == 7U && x >= 10U && x < 20U && y >= 16U && y < 31U) expected = {0, 255, 0, 255};
                if (variant == 8U && x >= 8U && x < 24U && y >= 10U && y < 26U) expected = {0, 255, 0, 255};
                // Literal intervals are independently derived from original
                // pixel-center membership, not the renderer's allocation box.
                if (variant == 10U && x >= 8U && x < 20U && y >= 11U && y < 24U) expected = {0, 255, 0, 255};
                if (variant == 11U && x >= 8U && x < 20U && y >= 10U && y < 24U) expected = {0, 255, 0, 255};
                if (variant == 12U && x >= 9U && x < 11U && y >= 11U && y < 13U) expected = {0, 255, 0, 255};
                if (variant == 13U && x >= 8U && x < 10U && y >= 10U && y < 12U) expected = {0, 255, 0, 255};
                if (variant == 14U && x >= 10U && x < 20U && y >= 16U && y < 31U) expected = {0, 255, 0, 255};
                const auto offset = (y * 64U + x) * 4U;
                if (std::memcmp(pixels.data() + offset, expected.data(), 4U) != 0)
                    std::fprintf(stderr, "Target Clear variant=%u replay=%u xy=%u,%u actual=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                        variant, replay, x, y, pixels[offset], pixels[offset + 1U], pixels[offset + 2U], pixels[offset + 3U],
                        expected[0], expected[1], expected[2], expected[3]);
                require(std::memcmp(pixels.data() + offset, expected.data(), 4U) == 0,
                    "target Clear full bytes differ in coverage, alpha or source order");
            }
            const std::uint32_t draws = variant == 2U || variant == 4U || variant == 5U ? 3U :
                variant == 6U ? 5U : variant == 3U ? 0U : 1U;
            require(metrics.draw_call_count == draws && metrics.command_count == validated.header.command_count &&
                metrics.submission_count == (variant == 8U && replay == 0U ? 2U : 1U),
                "target Clear actual replay counts differ");
            if (variant <= 1U || variant == 7U || variant >= 10U)
                require(metrics.uniform_upload_bytes == 16U, "target Clear must upload its actual original color on every replay");
            if (variant == 3U) require(metrics.uniform_upload_bytes == 0U, "empty target Clear allocated a color uniform");
        }
    }
}

} // namespace progpu::native::tests
