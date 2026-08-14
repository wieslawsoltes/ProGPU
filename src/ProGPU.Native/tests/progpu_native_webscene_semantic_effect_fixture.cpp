#include "progpu_native_webscene_semantic_effect_fixture.hpp"

#include "progpu_native_dawn.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace progpu::native::tests {
namespace {

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr,
        "ProGPU WebScene semantic effect fixture failed: %s\n",
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
    const T* values,
    std::size_t count) {
    const std::size_t aligned_size = (stream.size() + 7U) & ~7U;
    stream.resize(aligned_size);
    const std::uint32_t offset = static_cast<std::uint32_t>(stream.size());
    const std::size_t byte_count = sizeof(T) * count;
    const auto* source = reinterpret_cast<const std::byte*>(values);
    stream.insert(stream.end(), source, source + byte_count);
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_masked_effect_layer_scene_stream() {
    constexpr std::uint32_t command_count = 6U;
    constexpr std::uint32_t resource_count = 4U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    constexpr progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const progpu_native_analytic_primitive red{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        12.0F, 12.0F, 32.0F, 24.0F, 0.0F, 0.0F,
        {1.0F, 0.0F, 0.0F, 1.0F}, identity};
    const progpu_native_analytic_primitive green{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        46.0F, 36.0F, 8.0F, 8.0F, 0.0F, 0.0F,
        {0.0F, 1.0F, 0.0F, 1.0F}, identity};
    const std::uint32_t red_offset = append_scene_payload(
        stream,
        &red,
        1U);
    const std::uint32_t green_offset = append_scene_payload(
        stream,
        &green,
        1U);

    progpu_native_scene_layer_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    mask.bounds = {0.0F, 0.0F, 32.0F, 24.0F};
    mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 12.0F, 12.0F};
    std::fill_n(mask.corner_radii_x, 4U, 8.0F);
    std::fill_n(mask.corner_radii_y, 4U, 8.0F);
    mask.opacity = 1.0F;
    const std::uint32_t mask_offset = append_scene_payload(
        stream,
        &mask,
        1U);

    const std::array<progpu_native_group_effect, 2U> effects{{
        {sizeof(progpu_native_group_effect),
            PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR,
            0U, 81U, 1.25F, 1.25F, 0U, 0U,
            0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F},
        {sizeof(progpu_native_group_effect),
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW,
            0U, 82U, 1.0F, 1.0F, 0U, 0U,
            2.0F, 2.0F, 0.0F, 0.0F, 1.0F, 0.85F}
    }};
    const progpu_native_scene_effect_chain effect_chain{
        sizeof(progpu_native_scene_effect_chain),
        static_cast<std::uint32_t>(effects.size()),
        91U,
        0U};
    const std::uint32_t effect_chain_offset = append_scene_payload(
        stream,
        &effect_chain,
        1U);
    const std::uint32_t effects_offset = append_scene_payload(
        stream,
        effects.data(),
        effects.size());

    const progpu_native_scene_layer outer_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {8.0F, 8.0F, 48.0F, 40.0F},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        92U,
        93U,
        0U,
        0U};
    const progpu_native_scene_layer effected_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {12.0F, 12.0F, 32.0F, 24.0F},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        2U,
        3U,
        94U,
        95U,
        0U,
        0U};
    const std::uint32_t outer_layer_offset = append_scene_payload(
        stream,
        &outer_layer,
        1U);
    const std::uint32_t effected_layer_offset = append_scene_payload(
        stream,
        &effected_layer,
        1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 96U;
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
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 901U, 1U,
            red_offset, sizeof(red), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 902U, 1U,
            green_offset, sizeof(green), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 903U, 1U,
            mask_offset, sizeof(mask), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 904U, 1U,
            effect_chain_offset, sizeof(effect_chain),
            effects_offset, sizeof(effects)}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const progpu_native_scene_command commands[]{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 911U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            outer_layer_offset, sizeof(outer_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 912U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            effected_layer_offset, sizeof(effected_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 913U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            12.0F, 12.0F, 32.0F, 24.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 914U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 915U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 1U, 0U, 0U,
            46.0F, 36.0F, 8.0F, 8.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 916U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    };
    std::memcpy(
        stream.data() + command_offset,
        commands,
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_semantic_root_effect_layer_scene_stream() {
    auto stream = create_semantic_masked_effect_layer_scene_stream();
    progpu_native_scene_header header{};
    std::memcpy(&header, stream.data(), sizeof(header));

    std::array<progpu_native_scene_command, 3U> commands{};
    for (std::size_t index = 0U; index < commands.size(); ++index) {
        std::memcpy(
            &commands[index],
            stream.data() + header.command_offset +
                (index + 1U) * header.command_stride,
            sizeof(commands[index]));
    }
    header.scene_id = 98U;
    header.command_count = static_cast<std::uint32_t>(commands.size());
    std::memcpy(stream.data(), &header, sizeof(header));
    std::memcpy(
        stream.data() + header.command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}


void verify_semantic_masked_effect_layer_scene(
    IOSurfaceRef surface,
    const char* output_path) {
    require(surface != nullptr,
        "semantic mask/effect layer scene has no IOSurface");
    require(IOSurfaceLock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not lock semantic mask/effect layer IOSurface");
    const auto* bytes = static_cast<const std::uint8_t*>(
        IOSurfaceGetBaseAddress(surface));
    const std::size_t width = IOSurfaceGetWidth(surface);
    const std::size_t height = IOSurfaceGetHeight(surface);
    const std::size_t row_bytes = IOSurfaceGetBytesPerRow(surface);
    require(bytes != nullptr && width == 64U && height == 48U &&
        row_bytes >= width * 4U,
        "unexpected semantic mask/effect IOSurface storage");
    const auto pixel = [bytes, row_bytes](std::size_t x, std::size_t y) {
        return bytes + y * row_bytes + x * 4U;
    };
    const auto* clear = pixel(2U, 2U);
    const auto* rounded_corner = pixel(13U, 13U);
    const auto* top_center = pixel(28U, 13U);
    const auto* center = pixel(28U, 24U);
    const auto* continuation = pixel(50U, 40U);
    std::fprintf(
        stderr,
        "semantic-mask-effect clear=%u,%u,%u,%u corner=%u,%u,%u,%u top=%u,%u,%u,%u center=%u,%u,%u,%u continuation=%u,%u,%u,%u\n",
        clear[0], clear[1], clear[2], clear[3],
        rounded_corner[0], rounded_corner[1], rounded_corner[2],
        rounded_corner[3], top_center[0], top_center[1], top_center[2],
        top_center[3], center[0], center[1], center[2], center[3],
        continuation[0], continuation[1], continuation[2],
        continuation[3]);
    require(clear[0] <= 34U && clear[1] <= 32U && clear[2] <= 29U &&
        clear[3] >= 240U,
        "semantic mask/effect clear color is missing");
    require(rounded_corner[0] <= 40U && rounded_corner[1] <= 40U &&
        rounded_corner[2] <= 40U,
        "semantic mask was not applied after the effect chain");
    require(top_center[2] >= 120U && top_center[3] >= 120U,
        "semantic effect chain removed the masked top-center source");
    require(center[2] >= 220U && center[3] >= 240U,
        "semantic effect chain removed the source center");
    require(continuation[1] >= 220U && continuation[0] <= 32U &&
        continuation[2] <= 32U && continuation[3] >= 240U,
        "semantic parent target was not restored after the effected child");

    if (output_path != nullptr && output_path[0] != '\0') {
        std::FILE* output = std::fopen(output_path, "wb");
        require(output != nullptr,
            "could not create semantic mask/effect capture");
        std::fprintf(output, "P6\n%zu %zu\n255\n", width, height);
        for (std::size_t y = 0; y < height; ++y) {
            for (std::size_t x = 0; x < width; ++x) {
                const std::uint8_t* source = pixel(x, y);
                const std::uint8_t rgb[]{
                    source[2], source[1], source[0]};
                require(std::fwrite(rgb, sizeof(rgb), 1U, output) == 1U,
                    "semantic mask/effect capture write failed");
            }
        }
        require(std::fclose(output) == 0,
            "semantic mask/effect capture close failed");
    }
    require(IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, nullptr) ==
        kIOReturnSuccess,
        "could not unlock semantic mask/effect layer IOSurface");
}
} // namespace progpu::native::tests
