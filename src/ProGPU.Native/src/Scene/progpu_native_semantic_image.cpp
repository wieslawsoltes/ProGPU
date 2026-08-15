#include "progpu_native_semantic_image.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <cstring>

namespace progpu::native::semantic {

static_assert(sizeof(progpu_native_scene_image_sampling_options) == 16U);
static_assert(sizeof(progpu_native_scene_image_color_matrix) == 96U);

bool validate_image_draw_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes,
    semantic_image_options& options) noexcept {
    options = {};
    if (bytes == nullptr || image.struct_size != sizeof(image) ||
        !is_valid_semantic_image(image, pixel_bytes)) {
        return false;
    }
    std::uint64_t cursor = command.payload_offset + sizeof(image);
    std::uint64_t expected_size = sizeof(image);
    if (image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC) {
        expected_size += sizeof(progpu_native_scene_image_sampling_options);
        if (command.payload_size < expected_size) {
            return false;
        }
        progpu_native_scene_image_sampling_options source{};
        std::memcpy(&source, bytes + cursor, sizeof(source));
        if (!is_valid_semantic_image_sampling_options(source)) {
            return false;
        }
        options.cubic_b = source.cubic_b;
        options.cubic_c = source.cubic_c;
        cursor += sizeof(source);
    }

    if ((image.flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) != 0U) {
        expected_size += sizeof(progpu_native_scene_image_color_matrix);
        if (command.payload_size < expected_size) {
            return false;
        }
        std::memcpy(&options.color_matrix, bytes + cursor,
            sizeof(options.color_matrix));
        const auto& matrix = options.color_matrix;
        if (!is_valid_semantic_image_color_matrix(matrix)) {
            return false;
        }
        options.has_color_matrix = true;
        options.luminance_to_alpha =
            (matrix.flags &
                PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA) !=
            0U;
        cursor += sizeof(matrix);
    }
    if ((image.flags & PROGPU_NATIVE_SCENE_IMAGE_EFFECT) != 0U) {
        expected_size += sizeof(progpu_native_scene_image_effect);
        if (command.payload_size < expected_size) {
            return false;
        }
        std::memcpy(&options.effect, bytes + cursor, sizeof(options.effect));
        if (!is_valid_semantic_image_effect(options.effect)) {
            return false;
        }
        options.has_effect = true;
    }
    return command.payload_size == expected_size;
}

} // namespace progpu::native::semantic
