#include "progpu_native_semantic_image.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <cmath>
#include <cstring>

namespace progpu::native::semantic {

static_assert(sizeof(progpu_native_scene_image_sampling_options) == 16U);

bool validate_image_draw_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes,
    semantic_image_sampling_options& options) noexcept {
    options = {};
    if (bytes == nullptr || image.struct_size != sizeof(image) ||
        !is_valid_semantic_image(image, pixel_bytes)) {
        return false;
    }
    if (image.sampling != PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC) {
        return command.payload_size == sizeof(image);
    }
    if (command.payload_size != sizeof(image) +
            sizeof(progpu_native_scene_image_sampling_options)) {
        return false;
    }
    progpu_native_scene_image_sampling_options source{};
    std::memcpy(
        &source,
        bytes + command.payload_offset + sizeof(image),
        sizeof(source));
    if (source.struct_size != sizeof(source) || source.flags != 0U ||
        !std::isfinite(source.cubic_b) ||
        !std::isfinite(source.cubic_c) ||
        std::abs(source.cubic_b) > 16.0F ||
        std::abs(source.cubic_c) > 16.0F) {
        return false;
    }
    options.cubic_b = source.cubic_b;
    options.cubic_c = source.cubic_c;
    return true;
}

} // namespace progpu::native::semantic
