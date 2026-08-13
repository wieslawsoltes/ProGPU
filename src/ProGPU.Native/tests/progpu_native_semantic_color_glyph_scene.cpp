#include "progpu_native_semantic_color_glyph_scene.hpp"

#include "progpu_native.h"

#include <algorithm>
#include <array>
#include <cstring>

namespace progpu::native::tests {
namespace {

template<typename T>
std::uint32_t append_payload(
    std::vector<std::byte>& stream,
    const T* values,
    std::size_t count) {
    stream.resize((stream.size() + 7U) & ~7U);
    const auto offset = static_cast<std::uint32_t>(stream.size());
    const auto* source = reinterpret_cast<const std::byte*>(values);
    stream.insert(stream.end(), source, source + sizeof(T) * count);
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_color_glyph_scene_stream(
    std::uint32_t width,
    std::uint32_t height) {
    constexpr std::uint32_t command_count = 3U;
    constexpr std::uint32_t resource_count = 5U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    constexpr std::array<std::uint8_t, 16U> pixels{{
        255U, 40U, 20U, 255U,
        20U, 220U, 60U, 255U,
        20U, 80U, 255U, 255U,
        255U, 220U, 20U, 128U
    }};
    const float glyph_size = static_cast<float>(
        std::min(width, height)) * 0.5F;
    const progpu_native_scene_color_glyph_bitmap bitmap{
        0U, 2U, 2U, 8U, 0U,
        0.0F, 0.0F, glyph_size, glyph_size, 0U, 0U};
    const progpu_native_positioned_glyph glyph{
        0U, 0U,
        {static_cast<float>(width) * 0.25F,
            static_cast<float>(height) * 0.25F},
        {1.0F, 0.0F}, {0.0F, 1.0F},
        {1.0F, 1.0F, 1.0F, 1.0F},
        1.0F, 0.0F, 0.0F, 0.0F};
    const progpu_native_scene_text_style style{
        {1.0F, 1.0F, 1.0F, 0.75F},
        PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE,
        0U, 0U, 0U};
    const progpu_native_scene_glyph_draw draw{
        sizeof(progpu_native_scene_glyph_draw), 1U, 0U, 1U, 0U, 0U};

    constexpr float vector_size = 10.0F;
    constexpr float vector_inset = 3.0F;
    const float vector_x = static_cast<float>(width) - 19.0F;
    const float vector_y = static_cast<float>(height) * 0.25F;
    const std::array<progpu_native_path_segment, 8U> vector_segments{{
        {{0.0F, 0.0F}, {vector_size, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{vector_size, 0.0F}, {vector_size, vector_size}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{vector_size, vector_size}, {0.0F, vector_size}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{0.0F, vector_size}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{vector_inset, vector_inset},
            {vector_size - vector_inset, vector_inset}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{vector_size - vector_inset, vector_inset},
            {vector_size - vector_inset, vector_size - vector_inset}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{vector_size - vector_inset, vector_size - vector_inset},
            {vector_inset, vector_size - vector_inset}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{vector_inset, vector_size - vector_inset},
            {vector_inset, vector_inset}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}
    }};
    const std::array<progpu_native_scene_path_fill, 2U> vector_layers{{
        {0U, 4U, 0.0F, 0.0F, vector_size, vector_size,
            {1.0F, 0.0F, 1.0F, 1.0F},
            {1.0F, 0.0F, 0.0F, 1.0F, vector_x, vector_y},
            PROGPU_NATIVE_FILL_RULE_NON_ZERO, 8U},
        {4U, 4U, vector_inset, vector_inset,
            vector_size - vector_inset, vector_size - vector_inset,
            {0.0F, 1.0F, 1.0F, 1.0F},
            {1.0F, 0.0F, 0.0F, 1.0F, vector_x, vector_y},
            PROGPU_NATIVE_FILL_RULE_NON_ZERO, 8U}
    }};
    const float decoration_width = static_cast<float>(width) - 16.0F;
    const std::array<progpu_native_analytic_primitive, 3U> decorations{{
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            2.0F, 2.0F, 12.0F, 7.0F, 0.0F, 0.0F,
            {1.0F, 0.0F, 1.0F, 1.0F},
            {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}},
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            8.0F, static_cast<float>(height) * 0.54F,
            decoration_width, 1.5F, 0.0F, 0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}},
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            8.0F, static_cast<float>(height) - 8.0F,
            decoration_width, 1.5F, 0.0F, 0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}}
    }};
    std::array<progpu_native_scene_brush, 4U> brushes{};
    for (auto& brush : brushes) {
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
        brush.opacity = 1.0F;
        brush.coordinate_transform0[0] = 1.0F;
        brush.coordinate_transform1[1] = 1.0F;
    }
    brushes[0].type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    brushes[0].end_point = {5.0F, 0.0F};
    brushes[0].stop_count = 2U;
    brushes[0].coordinate_transform0[0] = 0.5F;
    brushes[1].colors[0] = {0.0F, 1.0F, 1.0F, 1.0F};
    brushes[2].colors[0] = {1.0F, 1.0F, 1.0F, 0.85F};
    brushes[3].type = PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE;
    brushes[3].start_point = {0.18F, 0.23F};
    brushes[3].center = {12.0F, 7.0F};
    brushes[3].radius = 17.0F;
    brushes[3].stop_count = 3U;
    brushes[3].coordinate_transform0[0] = 0.75F;
    brushes[3].coordinate_transform0[2] = 1.0F;
    brushes[3].coordinate_transform1[1] = 1.25F;
    brushes[3].coordinate_transform1[2] = -0.5F;
    const std::array<progpu_native_scene_gradient_stop, 2U> vector_stops{{
        {{1.0F, 0.0F, 0.0F, 1.0F}, 0.0F, 0U, 0U, 0U},
        {{0.0F, 0.0F, 1.0F, 1.0F}, 1.0F, 0U, 0U, 0U}
    }};
    const progpu_native_scene_draw_brushes vector_draw_brushes{
        sizeof(progpu_native_scene_draw_brushes), 4U, 2U, 0U};
    constexpr std::array<std::uint32_t, 2U> vector_brush_indices{0U, 1U};
    const progpu_native_scene_draw_brushes decoration_draw_brushes{
        sizeof(progpu_native_scene_draw_brushes), 4U, 3U, 0U};
    constexpr std::array<std::uint32_t, 3U>
        decoration_brush_indices{3U, 2U, 2U};

    const auto bitmap_offset = append_payload(stream, &bitmap, 1U);
    const auto pixel_offset = append_payload(
        stream, pixels.data(), pixels.size());
    const auto style_offset = append_payload(stream, &style, 1U);
    const auto draw_offset = append_payload(stream, &draw, 1U);
    append_payload(stream, &glyph, 1U);
    const auto vector_layer_offset = append_payload(
        stream, vector_layers.data(), vector_layers.size());
    const auto vector_segment_offset = append_payload(
        stream, vector_segments.data(), vector_segments.size());
    const auto decoration_offset = append_payload(
        stream, decorations.data(), decorations.size());
    const auto brush_offset = append_payload(
        stream, brushes.data(), brushes.size());
    const auto vector_stop_offset = append_payload(
        stream, vector_stops.data(), vector_stops.size());
    const auto vector_brush_offset = append_payload(
        stream, &vector_draw_brushes, 1U);
    append_payload(
        stream, vector_brush_indices.data(), vector_brush_indices.size());
    const auto decoration_brush_offset = append_payload(
        stream, &decoration_draw_brushes, 1U);
    append_payload(
        stream,
        decoration_brush_indices.data(),
        decoration_brush_indices.size());

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 97U;
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
            0U, 9701U, 1U,
            bitmap_offset, sizeof(bitmap),
            pixel_offset, static_cast<std::uint32_t>(pixels.size())},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, 9702U, 1U,
            style_offset, sizeof(style), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, 9703U, 1U,
            vector_layer_offset, sizeof(vector_layers),
            vector_segment_offset, sizeof(vector_segments)},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, 9704U, 1U,
            decoration_offset, sizeof(decorations), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, 9705U, 1U,
            brush_offset, sizeof(brushes),
            vector_stop_offset, sizeof(vector_stops)}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
                PROGPU_NATIVE_SCENE_GLYPH_STYLED,
            0U, 9710U, PROGPU_NATIVE_SCENE_NO_INDEX, 0U,
            draw_offset, sizeof(draw) + sizeof(glyph),
            static_cast<float>(width) * 0.25F,
            static_cast<float>(height) * 0.25F,
            glyph_size, glyph_size, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, 9711U, PROGPU_NATIVE_SCENE_NO_INDEX, 2U,
            vector_brush_offset,
            sizeof(vector_draw_brushes) + sizeof(vector_brush_indices),
            vector_x, vector_y, vector_size, vector_size, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
            0U, 9712U, PROGPU_NATIVE_SCENE_NO_INDEX, 3U,
            decoration_brush_offset,
            sizeof(decoration_draw_brushes) +
                sizeof(decoration_brush_indices),
            2.0F, 2.0F,
            static_cast<float>(width) - 4.0F,
            static_cast<float>(height) - 8.5F,
            0U, 0U}
    }};
    std::memcpy(
        stream.data() + command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}

} // namespace progpu::native::tests
