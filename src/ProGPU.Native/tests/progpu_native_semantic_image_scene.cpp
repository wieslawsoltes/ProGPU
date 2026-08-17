#include "progpu_native_semantic_image_scene.hpp"

#include "progpu_native.h"
#include "progpu_native_scene_builder.hpp"

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

std::vector<std::byte> create_semantic_cubic_image_scene_stream(
    std::uint32_t width,
    std::uint32_t height) {
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    constexpr std::array<std::uint8_t, 16U> pixels{{
        255U, 0U, 0U, 255U, 0U, 255U, 0U, 255U,
        0U, 0U, 255U, 255U, 255U, 255U, 255U, 255U
    }};
    const auto pixel_offset = append_payload(
        stream, pixels.data(), pixels.size());
    const progpu_native_scene_image_draw image{
        sizeof(progpu_native_scene_image_draw),
        PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX,
        2U,
        2U,
        8U,
        PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC,
        {0.0F, 0.0F, 2.0F, 2.0F},
        {0.0F, 0.0F, static_cast<float>(width),
            static_cast<float>(height)},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        1.0F,
        0U};
    const auto draw_offset = append_payload(stream, &image, 1U);
    const progpu_native_scene_image_sampling_options sampling{
        sizeof(progpu_native_scene_image_sampling_options),
        0U,
        1.0F / 3.0F,
        1.0F / 3.0F};
    append_payload(stream, &sampling, 1U);
    const progpu_native_scene_image_color_matrix color_matrix{
        sizeof(progpu_native_scene_image_color_matrix),
        0U,
        {0.2126F, 0.7152F, 0.0722F, 0.0F},
        {0.2126F, 0.7152F, 0.0722F, 0.0F},
        {0.2126F, 0.7152F, 0.0722F, 0.0F},
        {0.0F, 0.0F, 0.0F, 1.0F},
        {0.0F, 0.0F, 0.0F, 0.0F},
        {0U, 0U}};
    append_payload(stream, &color_matrix, 1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 96U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = 1U;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = 1U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const progpu_native_scene_resource resource{
        sizeof(progpu_native_scene_resource),
        PROGPU_NATIVE_SCENE_RESOURCE_IMAGE,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        9601U,
        1U,
        pixel_offset,
        pixels.size(),
        0U,
        0U};
    std::memcpy(
        stream.data() + resource_offset,
        &resource,
        sizeof(resource));
    const progpu_native_scene_command command{
        sizeof(progpu_native_scene_command),
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        9602U,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        0U,
        draw_offset,
        sizeof(image) + sizeof(sampling) + sizeof(color_matrix),
        0.0F,
        0.0F,
        static_cast<float>(width),
        static_cast<float>(height),
        0U,
        0U};
    std::memcpy(
        stream.data() + command_offset,
        &command,
        sizeof(command));
    return stream;
}

std::vector<std::byte> create_semantic_image_patch_scene_stream(
    std::uint32_t width,
    std::uint32_t height) {
    constexpr std::array<std::byte, 16U> pixels{
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    semantic_scene_builder builder(95U, 1U);
    std::uint32_t image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_rgba8_image(2U, 2U, 8U, pixels, image_index)) {
        return {};
    }
    progpu_native_scene_image_draw image{};
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED |
        PROGPU_NATIVE_SCENE_IMAGE_SNAP_TO_PIXELS;
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {0.0F, 0.0F, 1.0F, 1.0F};
    image.transform = semantic_scene_builder::identity_transform();
    image.opacity = 1.0F;
    const float third = static_cast<float>(width) / 3.0F;
    std::array<progpu_native_scene_image_patch, 3U> patches{};
    for (auto& patch : patches) {
        patch.struct_size = sizeof(patch);
        patch.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
        patch.transform = semantic_scene_builder::identity_transform();
    }
    patches[0].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE;
    patches[0].destination_rect = {
        0.0F, 0.0F, third, static_cast<float>(height)};
    patches[1].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR;
    patches[1].source_rect = {};
    patches[1].destination_rect = {
        third, 0.0F, third, static_cast<float>(height)};
    patches[1].color[0] = 1.0F;
    patches[1].color[3] = 1.0F;
    patches[2].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR;
    patches[2].color_blend_mode = 2U;
    patches[2].destination_rect = {
        2.0F * third,
        0.0F,
        static_cast<float>(width) - 2.0F * third,
        static_cast<float>(height)};
    patches[2].transform.m31 = -0.25F;
    patches[2].color[0] = 1.0F;
    patches[2].color[1] = 1.0F;
    patches[2].color[2] = 1.0F;
    patches[2].color[3] = 1.0F;
    if (!builder.draw_image_patches(
            image_index,
            image,
            patches,
            {0.0F, 0.0F, static_cast<float>(width),
                static_cast<float>(height)})) {
        return {};
    }
    std::vector<std::byte> stream;
    return builder.build(stream) ? stream : std::vector<std::byte>{};
}

} // namespace progpu::native::tests
