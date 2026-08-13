#include "progpu_native_semantic_text_scene.hpp"

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

std::vector<std::byte> create_semantic_text_scene_stream(
    std::uint32_t width,
    std::uint32_t height) {
    constexpr std::uint32_t command_count = 1U;
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    const float glyph_size = static_cast<float>(
        std::min(width, height)) * 0.5F;
    const std::array<progpu_native_path_segment, 4U> segments{{
        {{0.0F, 0.0F}, {glyph_size, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{glyph_size, 0.0F}, {glyph_size, glyph_size}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{glyph_size, glyph_size}, {0.0F, glyph_size}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        {{0.0F, glyph_size}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}
    }};
    const progpu_native_scene_glyph_outline outline{
        0U,
        segments.size(),
        0.0F,
        0.0F,
        glyph_size,
        glyph_size,
        1.0F,
        0.0F};
    const progpu_native_positioned_glyph glyph{
        0U,
        0U,
        {static_cast<float>(width) * 0.25F,
            static_cast<float>(height) * 0.25F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {0.0F, 0.0F, 1.0F, 1.0F},
        1.0F,
        0.0F,
        0.0F,
        0.0F};
    const progpu_native_scene_text_style style{
        {1.0F, 0.25F, 0.05F, 0.8F},
        PROGPU_NATIVE_SCENE_TEXT_ALIASED,
        0U,
        0U,
        0U};
    const progpu_native_scene_glyph_draw draw{
        sizeof(progpu_native_scene_glyph_draw), 1U, 0U, 1U, 0U, 0U};

    const auto outline_offset = append_payload(stream, &outline, 1U);
    const auto segment_offset = append_payload(
        stream, segments.data(), segments.size());
    const auto style_offset = append_payload(stream, &style, 1U);
    const auto draw_offset = append_payload(stream, &draw, 1U);
    append_payload(stream, &glyph, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 99U;
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
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 1001U, 1U,
            outline_offset, sizeof(outline),
            segment_offset, sizeof(segments)},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 1002U, 1U,
            style_offset, sizeof(style), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const progpu_native_scene_command command{
        sizeof(progpu_native_scene_command),
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
            PROGPU_NATIVE_SCENE_GLYPH_STYLED,
        0U,
        1003U,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        0U,
        draw_offset,
        sizeof(draw) + sizeof(glyph),
        static_cast<float>(width) * 0.25F,
        static_cast<float>(height) * 0.25F,
        glyph_size,
        glyph_size,
        0U,
        0U};
    std::memcpy(
        stream.data() + command_offset,
        &command,
        sizeof(command));
    return stream;
}

} // namespace progpu::native::tests
