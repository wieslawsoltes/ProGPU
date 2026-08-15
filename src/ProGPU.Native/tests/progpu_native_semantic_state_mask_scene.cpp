#include "progpu_native_semantic_state_mask_scene.hpp"

#include "progpu_native_dawn.h"

#include <algorithm>
#include <array>
#include <cstring>

namespace progpu::native::tests {
namespace {

template<typename T>
std::uint32_t append(
    std::vector<std::byte>& stream,
    const T* values,
    std::size_t count) {
    stream.resize((stream.size() + 7U) & ~7U);
    const auto offset = static_cast<std::uint32_t>(stream.size());
    const auto* first = reinterpret_cast<const std::byte*>(values);
    stream.insert(stream.end(), first, first + sizeof(T) * count);
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_state_mask_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    constexpr std::uint32_t command_count = 3U;
    constexpr std::uint32_t resource_count = 3U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);
    const float scale_x = static_cast<float>(target_width) / 64.0F;
    const float scale_y = static_cast<float>(target_height) / 48.0F;
    const progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const std::array<progpu_native_analytic_primitive, 2U> content{{
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            7.0F * scale_x, 7.0F * scale_y,
            34.0F * scale_x, 30.0F * scale_y,
            0.0F, 0.0F, {0.0F, 0.8F, 1.0F, 0.65F}, identity},
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            23.0F * scale_x, 11.0F * scale_y,
            34.0F * scale_x, 30.0F * scale_y,
            0.0F, 0.0F, {1.0F, 0.2F, 0.55F, 0.65F}, identity}
    }};
    const std::uint32_t content_offset = append(
        stream,
        content.data(),
        content.size());

    progpu_native_scene_layer_mask_chain mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    mask.mask_count = 2U;
    auto& outer_mask = mask.masks[0];
    outer_mask.struct_size = sizeof(outer_mask);
    outer_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    outer_mask.bounds = {0.0F, 0.0F, 44.0F, 32.0F};
    outer_mask.transform = {
        scale_x, 0.12F * scale_y,
        -0.10F * scale_x, scale_y,
        11.0F * scale_x, 5.0F * scale_y};
    std::fill_n(outer_mask.corner_radii_x, 4U, 9.0F);
    std::fill_n(outer_mask.corner_radii_y, 4U, 7.0F);
    outer_mask.opacity = 0.8F;
    auto& inner_mask = mask.masks[1];
    inner_mask.struct_size = sizeof(inner_mask);
    inner_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    inner_mask.bounds = {0.0F, 0.0F, 36.0F, 26.0F};
    inner_mask.transform = {
        scale_x, -0.06F * scale_y,
        0.08F * scale_x, scale_y,
        15.0F * scale_x, 8.0F * scale_y};
    std::fill_n(inner_mask.corner_radii_x, 4U, 5.0F);
    std::fill_n(inner_mask.corner_radii_y, 4U, 6.0F);
    inner_mask.opacity = 1.0F;
    const std::uint32_t mask_offset = append(stream, &mask, 1U);

    progpu_native_scene_state state{};
    state.struct_size = sizeof(state);
    state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
    state.transform = identity;
    state.opacity = 1.0F;
    state.mask_resource_index = 1U;
    const std::uint32_t state_offset = append(stream, &state, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 102U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const std::array<progpu_native_scene_resource, resource_count> resources{{
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2101U, 1U,
            content_offset, sizeof(content), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2102U, 1U,
            mask_offset, sizeof(mask), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_STATE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2103U, 1U,
            state_offset, sizeof(state), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_SAVE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2111U,
            2U, PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U,
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2112U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            7.0F * scale_x, 7.0F * scale_y,
            50.0F * scale_x, 34.0F * scale_y, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_RESTORE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2113U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    }};
    std::memcpy(
        stream.data() + command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_semantic_state_mask_media_scene_stream_impl(
    std::uint32_t target_width,
    std::uint32_t target_height,
    bool analytic_chain) {
    constexpr std::uint32_t command_count = 2U;
    constexpr std::uint32_t resource_count = 4U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);
    const std::uint64_t resource_id_base = analytic_chain ? 2300U : 2200U;
    const std::uint64_t command_id_base = analytic_chain ? 2310U : 2210U;

    constexpr std::array<std::uint8_t, 16U> glyph_pixels{{
        255U, 32U, 192U, 255U,
        255U, 32U, 192U, 255U,
        255U, 32U, 192U, 255U,
        255U, 32U, 192U, 255U
    }};
    const float glyph_size = static_cast<float>(
        std::min(target_width, target_height)) * 0.35F;
    const progpu_native_scene_color_glyph_bitmap glyph_bitmap{
        0U, 2U, 2U, 8U, 0U,
        0.0F, 0.0F, glyph_size, glyph_size, 0U, 0U};
    const progpu_native_positioned_glyph glyph{
        0U, 0U,
        {static_cast<float>(target_width) * 0.08F,
            static_cast<float>(target_height) * 0.30F},
        {1.0F, 0.0F}, {0.0F, 1.0F},
        {1.0F, 1.0F, 1.0F, 1.0F},
        1.0F, 0.0F, 0.0F, 0.0F};
    const std::uint32_t glyph_bitmap_offset = append(
        stream, &glyph_bitmap, 1U);
    const std::uint32_t glyph_pixel_offset = append(
        stream, glyph_pixels.data(), glyph_pixels.size());
    const std::uint32_t glyph_offset = append(stream, &glyph, 1U);

    constexpr std::array<std::uint8_t, 16U> image_pixels{{
        16U, 224U, 96U, 255U,
        16U, 224U, 96U, 255U,
        16U, 224U, 96U, 255U,
        16U, 224U, 96U, 255U
    }};
    const std::uint32_t image_pixel_offset = append(
        stream, image_pixels.data(), image_pixels.size());
    const progpu_native_scene_image_draw image{
        sizeof(progpu_native_scene_image_draw),
        analytic_chain
            ? PROGPU_NATIVE_SCENE_IMAGE_EFFECT
            : PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX,
        2U,
        2U,
        8U,
        PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST,
        {0.0F, 0.0F, 2.0F, 2.0F},
        {0.0F, 0.0F, static_cast<float>(target_width),
            static_cast<float>(target_height)},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        1.0F,
        0U};
    const std::uint32_t image_draw_offset = append(stream, &image, 1U);
    const progpu_native_scene_image_color_matrix color_matrix{
        sizeof(progpu_native_scene_image_color_matrix),
        0U,
        {0.0F, 1.0F, 0.0F, 0.0F},
        {1.0F, 0.0F, 0.0F, 0.0F},
        {0.0F, 0.0F, 1.0F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {0.0F, 0.0F, 0.0F, 0.0F},
        {0U, 0U}};
    if (!analytic_chain) {
        append(stream, &color_matrix, 1U);
    }
    const progpu_native_scene_image_effect image_effect{
        {0.0F, 1.0F, 0.0F, 0.0F},
        {1.0F, 0.0F, 0.0F, 0.0F},
        {0.0F, 0.0F, 1.0F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {},
        {0.0F, 1.0F, 1.0F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {2.0F, 2.0F, 0.0F, 0.0F},
        {0.0F, 0.0F, 1.0F, 0.0F},
        {}, {}, {}, {}, {}, {}, {}, {}, {},
        sizeof(progpu_native_scene_image_effect), 0U, 0U, 0U};
    if (analytic_chain) {
        append(stream, &image_effect, 1U);
    }

    progpu_native_scene_layer_coverage_mask coverage_mask{};
    coverage_mask.struct_size = sizeof(coverage_mask);
    coverage_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP;
    coverage_mask.width = 8U;
    coverage_mask.height = 8U;
    coverage_mask.row_bytes = 8U;
    coverage_mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    coverage_mask.bounds = {0.0F, 0.0F, 8.0F, 8.0F};
    coverage_mask.transform = {
        static_cast<float>(target_width) / 8.0F, 0.0F,
        0.0F, static_cast<float>(target_height) / 8.0F,
        0.0F, 0.0F};
    coverage_mask.opacity = 1.0F;
    progpu_native_scene_layer_mask_chain mask_chain{};
    mask_chain.struct_size = sizeof(mask_chain);
    mask_chain.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    mask_chain.mask_count = 2U;
    for (std::uint32_t index = 0U; index < mask_chain.mask_count; ++index) {
        auto& item = mask_chain.masks[index];
        item.struct_size = sizeof(item);
        item.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
        item.bounds = index == 0U
            ? progpu_native_image_rect{0.0F, 0.0F,
                static_cast<float>(target_width) * 0.5F,
                static_cast<float>(target_height)}
            : progpu_native_image_rect{0.0F, 0.0F,
                static_cast<float>(target_width),
                static_cast<float>(target_height)};
        item.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
        item.opacity = 1.0F;
    }
    const std::uint32_t mask_offset = analytic_chain
        ? append(stream, &mask_chain, 1U)
        : append(stream, &coverage_mask, 1U);
    std::array<std::uint8_t, 64U> coverage{};
    for (std::uint32_t y = 0U; y < 8U; ++y) {
        for (std::uint32_t x = 0U; x < 8U; ++x) {
            coverage[y * 8U + x] = x < 4U ? 255U : 0U;
        }
    }
    const std::uint32_t coverage_offset = analytic_chain
        ? 0U
        : append(stream, coverage.data(), coverage.size());

    progpu_native_scene_state state{};
    state.struct_size = sizeof(state);
    state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
    state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    state.opacity = 1.0F;
    state.mask_resource_index = 2U;
    const std::uint32_t state_offset = append(stream, &state, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = analytic_chain ? 104U : 103U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const std::array<progpu_native_scene_resource, resource_count> resources{{
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
                PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS,
            0U, resource_id_base + 1U, 1U,
            glyph_bitmap_offset, sizeof(glyph_bitmap),
            glyph_pixel_offset,
            static_cast<std::uint32_t>(glyph_pixels.size())},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, resource_id_base + 2U, 1U,
            image_pixel_offset,
            static_cast<std::uint32_t>(image_pixels.size()), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, resource_id_base + 3U, 1U,
            mask_offset,
            static_cast<std::uint32_t>(analytic_chain
                ? sizeof(mask_chain)
                : sizeof(coverage_mask)),
            coverage_offset,
            analytic_chain
                ? 0U
                : static_cast<std::uint32_t>(coverage.size())},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_STATE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, resource_id_base + 4U, 1U,
            state_offset, sizeof(state), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, command_id_base + 1U, 3U, 1U,
            image_draw_offset,
            static_cast<std::uint32_t>(
                sizeof(image) + (analytic_chain
                    ? sizeof(image_effect)
                    : sizeof(color_matrix))),
            0.0F, 0.0F,
            static_cast<float>(target_width),
            static_cast<float>(target_height), 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, command_id_base + 2U, 3U, 0U,
            glyph_offset, sizeof(glyph),
            glyph.position.x, glyph.position.y,
            glyph_size, glyph_size, 0U, 0U}
    }};
    std::memcpy(
        stream.data() + command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_semantic_state_mask_media_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    return create_semantic_state_mask_media_scene_stream_impl(
        target_width,
        target_height,
        false);
}

std::vector<std::byte> create_semantic_state_mask_chain_media_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    return create_semantic_state_mask_media_scene_stream_impl(
        target_width,
        target_height,
        true);
}

} // namespace progpu::native::tests
