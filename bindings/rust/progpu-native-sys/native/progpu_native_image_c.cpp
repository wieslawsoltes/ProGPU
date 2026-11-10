// The C ABI over the engine's image module (progpu_native_image_c.h).

#include "progpu_native_image_c.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

#include "progpu_native_image.hpp"

namespace image = progpu::native::image;

namespace {

void set_error(std::uint32_t* error, std::uint32_t value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

std::span<const std::byte> input_of(const std::uint8_t* data, std::size_t size) noexcept {
    return {reinterpret_cast<const std::byte*>(data), size};
}

} // namespace

extern "C" {

int progpu_native_image_png_read_size(
    const uint8_t* data,
    size_t size,
    uint32_t* width,
    uint32_t* height,
    size_t* rgba_size,
    uint32_t* error) {
    if (data == nullptr || size == 0U || width == nullptr || height == nullptr ||
        rgba_size == nullptr) {
        set_error(error, PROGPU_NATIVE_IMAGE_ERROR_INVALID_ARGUMENT);
        return 0;
    }
    image::png_decode_requirements requirements{};
    image::image_error failure = image::image_error::none;
    if (!image::try_get_png_decode_requirements(input_of(data, size), requirements, &failure)) {
        set_error(error, static_cast<std::uint32_t>(failure));
        return 0;
    }
    *width = requirements.width;
    *height = requirements.height;
    *rgba_size = requirements.rgba_bytes;
    set_error(error, PROGPU_NATIVE_IMAGE_ERROR_NONE);
    return 1;
}

int progpu_native_image_png_decode_rgba(
    const uint8_t* data,
    size_t size,
    uint8_t* rgba,
    size_t rgba_size,
    uint32_t* error) {
    if (data == nullptr || size == 0U || rgba == nullptr) {
        set_error(error, PROGPU_NATIVE_IMAGE_ERROR_INVALID_ARGUMENT);
        return 0;
    }
    const auto input = input_of(data, size);
    image::png_decode_requirements requirements{};
    image::image_error failure = image::image_error::none;
    if (!image::try_get_png_decode_requirements(input, requirements, &failure)) {
        set_error(error, static_cast<std::uint32_t>(failure));
        return 0;
    }
    if (rgba_size < requirements.rgba_bytes) {
        set_error(error, PROGPU_NATIVE_IMAGE_ERROR_INSUFFICIENT_BUFFER);
        return 0;
    }
    try {
        // The scratch of the decoder: the compressed stream joined, and the filtered rows.
        std::vector<std::byte> compressed(requirements.compressed_bytes);
        std::vector<std::byte> filtered(requirements.filtered_bytes);
        if (!image::try_decode_png_rgba(input, compressed, filtered,
                {reinterpret_cast<std::byte*>(rgba), requirements.rgba_bytes}, requirements,
                &failure)) {
            set_error(error, static_cast<std::uint32_t>(failure));
            return 0;
        }
    } catch (...) {
        set_error(error, PROGPU_NATIVE_IMAGE_ERROR_OUT_OF_MEMORY);
        return 0;
    }
    set_error(error, PROGPU_NATIVE_IMAGE_ERROR_NONE);
    return 1;
}

} // extern "C"
