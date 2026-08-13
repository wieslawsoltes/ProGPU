#include "progpu_native_semantic_image_tests.hpp"

#include "progpu_native.h"
#include "progpu_native_semantic_image.hpp"

#include <array>
#include <cstddef>
#include <cstring>
#include <limits>

namespace progpu::native::tests {

bool semantic_image_sampling_payload_is_exact_and_bounded() {
    static_assert(sizeof(progpu_native_scene_image_sampling_options) == 16U);
    constexpr auto base_size = sizeof(progpu_native_scene_image_draw);
    std::array<std::byte,
        base_size + sizeof(progpu_native_scene_image_sampling_options)> bytes{};
    progpu_native_scene_image_draw image{};
    image.struct_size = sizeof(image);
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {1.0F, 2.0F, 8.0F, 8.0F};
    image.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    image.opacity = 1.0F;
    std::memcpy(bytes.data(), &image, sizeof(image));

    progpu_native_scene_image_sampling_options source{};
    source.struct_size = sizeof(source);
    source.cubic_b = 1.0F / 3.0F;
    source.cubic_c = 1.0F / 3.0F;
    std::memcpy(bytes.data() + base_size, &source, sizeof(source));

    progpu_native_scene_command command{};
    command.payload_size = bytes.size();
    semantic::semantic_image_sampling_options parsed{};
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        parsed.cubic_b != source.cubic_b ||
        parsed.cubic_c != source.cubic_c) {
        return false;
    }

    command.payload_size = base_size;
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    command.payload_size = bytes.size();
    source.cubic_b = std::numeric_limits<float>::quiet_NaN();
    std::memcpy(bytes.data() + base_size, &source, sizeof(source));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }

    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    command.payload_size = base_size;
    return semantic::validate_image_draw_payload(
        bytes.data(), command, image, 16U, parsed);
}

} // namespace progpu::native::tests
