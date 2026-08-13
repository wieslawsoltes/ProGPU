#include "progpu_native_webscene_advanced_blend_fixture.hpp"

#include "progpu_native_dawn.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace progpu::native::tests {
namespace {

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr,
        "ProGPU WebScene advanced-blend fixture failed: %s\n",
        message);
    std::abort();
}

void require(bool condition, const char* message) {
    if (!condition) {
        fail(message);
    }
}

template<typename T>
std::uint32_t append_scene_payload(
    std::vector<std::byte>& stream,
    const T& value) {
    const std::size_t aligned_size = (stream.size() + 7U) & ~7U;
    stream.resize(aligned_size);
    const auto offset = static_cast<std::uint32_t>(stream.size());
    const auto* source = reinterpret_cast<const std::byte*>(&value);
    stream.insert(stream.end(), source, source + sizeof(value));
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_advanced_blend_scene_stream() {
    constexpr std::uint32_t command_count = 4U;
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    constexpr progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const progpu_native_analytic_primitive destination{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        4.0F, 4.0F, 40.0F, 32.0F, 0.0F, 0.0F,
        {0.2F, 0.8F, 0.4F, 1.0F}, identity};
    const progpu_native_analytic_primitive source{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        12.0F, 12.0F, 24.0F, 16.0F, 0.0F, 0.0F,
        {0.5F, 0.5F, 0.5F, 1.0F}, identity};
    const std::uint32_t destination_offset = append_scene_payload(
        stream,
        destination);
    const std::uint32_t source_offset = append_scene_payload(
        stream,
        source);
    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {12.0F, 12.0F, 24.0F, 16.0F},
        1.0F,
        PROGPU_NATIVE_BLEND_MULTIPLY,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        101U,
        102U,
        0U,
        0U};
    const std::uint32_t layer_offset = append_scene_payload(stream, layer);

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
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 971U, 1U,
            destination_offset, sizeof(destination), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 972U, 1U,
            source_offset, sizeof(source), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 981U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            4.0F, 4.0F, 40.0F, 32.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 982U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            layer_offset, sizeof(layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 983U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 1U, 0U, 0U,
            12.0F, 12.0F, 24.0F, 16.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 984U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    }};
    std::memcpy(
        stream.data() + command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}

void verify_semantic_advanced_blend_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr,
        "semantic advanced-blend scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not lock semantic advanced-blend IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected semantic advanced-blend IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto near = [](std::uint8_t actual, std::uint8_t expected) {
        return std::abs(static_cast<int>(actual) - expected) <= 20;
    };
    const auto* destination = pixel(8U, 8U);
    const auto* blended = pixel(20U, 20U);
    require(near(destination[2], 51U) &&
            near(destination[1], 204U) &&
            near(destination[0], 102U) && destination[3] >= 240U,
        "semantic advanced blend lost the rendered parent destination");
    require(near(blended[2], 26U) &&
            near(blended[1], 102U) &&
            near(blended[0], 51U) && blended[3] >= 240U,
        "semantic multiply did not sample the rendered parent destination");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr,
            "could not create semantic advanced-blend capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0U; y < height; ++y) {
            for (std::size_t x = 0U; x < width; ++x) {
                const auto* value = pixel(x, y);
                const std::uint8_t rgb[]{value[2], value[1], value[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic advanced-blend capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic advanced-blend capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not unlock semantic advanced-blend IOSurface");
}

} // namespace progpu::native::tests
