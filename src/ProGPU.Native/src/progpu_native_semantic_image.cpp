#include "progpu_native_semantic_image.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <cmath>
#include <cstring>

namespace progpu::native::semantic {

static_assert(sizeof(progpu_native_scene_image_sampling_options) == 16U);
static_assert(sizeof(progpu_native_scene_image_color_matrix) == 96U);

static bool valid_matrix_component(float value) noexcept {
    return std::isfinite(value) && std::abs(value) <= 1024.0F;
}

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
        if (source.struct_size != sizeof(source) || source.flags != 0U ||
            !std::isfinite(source.cubic_b) ||
            !std::isfinite(source.cubic_c) ||
            std::abs(source.cubic_b) > 16.0F ||
            std::abs(source.cubic_c) > 16.0F) {
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
        if (matrix.struct_size != sizeof(matrix) || matrix.flags != 0U ||
            matrix.reserved[0] != 0U || matrix.reserved[1] != 0U) {
            return false;
        }
        const auto valid_row = [](const float (&row)[4]) noexcept {
            for (float value : row) {
                if (!valid_matrix_component(value)) {
                    return false;
                }
            }
            return true;
        };
        if (!valid_row(matrix.red) || !valid_row(matrix.green) ||
            !valid_row(matrix.blue) || !valid_row(matrix.alpha) ||
            !valid_row(matrix.offset)) {
            return false;
        }
        options.has_color_matrix = true;
    }
    return command.payload_size == expected_size;
}

} // namespace progpu::native::semantic
