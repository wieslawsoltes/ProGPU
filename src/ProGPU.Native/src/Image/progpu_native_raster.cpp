#include "progpu_native_raster_internal.hpp"

#include <array>
#include <new>

namespace progpu::native::image {

namespace {

bool starts_with(
    std::span<const std::byte> input,
    std::span<const std::uint8_t> signature) noexcept {
    if (input.size() < signature.size()) {
        return false;
    }
    for (std::size_t index = 0U; index < signature.size(); ++index) {
        if (static_cast<std::uint8_t>(input[index]) != signature[index]) {
            return false;
        }
    }
    return true;
}

void set_error(image_error* target, image_error value) noexcept {
    if (target != nullptr) {
        *target = value;
    }
}

} // namespace

image_format detect_image_format(std::span<const std::byte> input) noexcept {
    static constexpr std::array<std::uint8_t, 8U> png{
        0x89U, 0x50U, 0x4EU, 0x47U, 0x0DU, 0x0AU, 0x1AU, 0x0AU};
    static constexpr std::array<std::uint8_t, 3U> jpeg{0xFFU, 0xD8U, 0xFFU};
    static constexpr std::array<std::uint8_t, 6U> gif87{
        0x47U, 0x49U, 0x46U, 0x38U, 0x37U, 0x61U};
    static constexpr std::array<std::uint8_t, 6U> gif89{
        0x47U, 0x49U, 0x46U, 0x38U, 0x39U, 0x61U};
    static constexpr std::array<std::uint8_t, 2U> bmp{0x42U, 0x4DU};
    if (starts_with(input, png)) {
        return image_format::png;
    }
    if (starts_with(input, jpeg)) {
        return image_format::jpeg;
    }
    if (starts_with(input, gif87) || starts_with(input, gif89)) {
        return image_format::gif;
    }
    if (starts_with(input, bmp)) {
        return image_format::bmp;
    }
    return image_format::unknown;
}

bool try_get_raster_image_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error* error) noexcept {
    result = {};
    image_error failure = image_error::none;
    bool read = false;
    const image_format format = detect_image_format(input);
    switch (format) {
    case image_format::jpeg:
        read = detail::read_jpeg_size(input, result, failure);
        break;
    case image_format::gif:
        read = detail::read_gif_size(input, result, failure);
        break;
    case image_format::bmp:
        read = detail::read_bmp_size(input, result, failure);
        break;
    case image_format::png:
        failure = image_error::unsupported_format;
        break;
    case image_format::unknown:
        failure = image_error::invalid_signature;
        break;
    }
    if (!read) {
        result = {};
        set_error(error, failure);
        return false;
    }
    result.format = format;
    set_error(error, image_error::none);
    return true;
}

bool try_decode_raster_image_rgba(
    std::span<const std::byte> input,
    std::span<std::byte> rgba_output,
    image_error* error) noexcept {
    raster_image_size size{};
    image_error failure = image_error::none;
    if (!try_get_raster_image_size(input, size, &failure)) {
        set_error(error, failure);
        return false;
    }
    if (rgba_output.size() < size.rgba_bytes) {
        set_error(error, image_error::insufficient_buffer);
        return false;
    }
    const auto rgba = rgba_output.first(size.rgba_bytes);
    bool decoded = false;
    try {
        switch (size.format) {
        case image_format::jpeg:
            decoded = detail::decode_jpeg(input, rgba, failure);
            break;
        case image_format::gif:
            decoded = detail::decode_gif(input, rgba, failure);
            break;
        case image_format::bmp:
            decoded = detail::decode_bmp(input, rgba, failure);
            break;
        case image_format::png:
        case image_format::unknown:
            failure = image_error::unsupported_format;
            break;
        }
    } catch (const std::bad_alloc&) {
        failure = image_error::out_of_memory;
        decoded = false;
    } catch (...) {
        failure = image_error::invalid_compressed_data;
        decoded = false;
    }
    set_error(error, decoded ? image_error::none : failure);
    return decoded;
}

} // namespace progpu::native::image
