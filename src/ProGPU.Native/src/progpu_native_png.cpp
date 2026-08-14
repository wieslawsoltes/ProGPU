#include "progpu_native_png_internal.hpp"

#include "progpu_native_compression.hpp"

#include <algorithm>
#include <limits>

// Direct native port provenance: ProGPU-owned managed PNG decode contracts at
// checkpoint 97754aa2, constrained by W3C PNG Third Edition. Parsing is O(C+B)
// for C chunks and B encoded bytes. Decode is O(B+W*H), uses only caller-owned
// compressed/filtered/RGBA spans, and performs no heap allocation.
namespace progpu::native::image {
namespace {

using compression::compression_error;
using detail::png_metadata;

void set_error(image_error* destination, image_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

image_error map_compression_error(compression_error error) noexcept {
    return error == compression_error::insufficient_buffer
        ? image_error::insufficient_buffer
        : image_error::invalid_compressed_data;
}

} // namespace

bool try_get_png_decode_requirements(
    std::span<const std::byte> input,
    png_decode_requirements& result,
    image_error* error) noexcept {
    result = {};
    set_error(error, image_error::none);
    png_metadata metadata{};
    image_error parse_error = image_error::none;
    if (!detail::try_parse_png(
            input, metadata, {}, false, parse_error)) {
        set_error(error, parse_error);
        return false;
    }
    result = metadata.requirements;
    return true;
}

bool try_decode_png_rgba(
    std::span<const std::byte> input,
    std::span<std::byte> compressed_scratch,
    std::span<std::byte> filtered_scratch,
    std::span<std::byte> rgba_output,
    png_decode_requirements& result,
    image_error* error) noexcept {
    result = {};
    set_error(error, image_error::none);
    png_metadata metadata{};
    image_error parse_error = image_error::none;
    if (!detail::try_parse_png(
            input, metadata, {}, false, parse_error)) {
        set_error(error, parse_error);
        return false;
    }
    const auto& requirements = metadata.requirements;
    if (compressed_scratch.size() < requirements.compressed_bytes ||
        filtered_scratch.size() < requirements.filtered_bytes ||
        rgba_output.size() < requirements.rgba_bytes) {
        set_error(error, image_error::insufficient_buffer);
        return false;
    }
    if (!detail::try_parse_png(
            input,
            metadata,
            compressed_scratch.first(requirements.compressed_bytes),
            true,
            parse_error)) {
        set_error(error, parse_error);
        return false;
    }
    std::size_t inflated_bytes = 0U;
    compression_error compression_failure = compression_error::none;
    auto filtered = filtered_scratch.first(requirements.filtered_bytes);
    if (!compression::try_inflate_zlib(
            compressed_scratch.first(requirements.compressed_bytes),
            filtered,
            inflated_bytes,
            &compression_failure) ||
        inflated_bytes != requirements.filtered_bytes) {
        set_error(error, map_compression_error(compression_failure));
        return false;
    }
    detail::png_layout layout{};
    if (!detail::try_build_png_layout(requirements, layout) ||
        !detail::try_unfilter_png(layout, filtered)) {
        set_error(error, image_error::invalid_compressed_data);
        return false;
    }
    if (!detail::try_convert_png_to_rgba(
            metadata,
            filtered,
            rgba_output.first(requirements.rgba_bytes))) {
        set_error(error, image_error::invalid_compressed_data);
        return false;
    }
    result = requirements;
    return true;
}

} // namespace progpu::native::image

namespace progpu::native::image::detail {
namespace {

constexpr std::array<std::byte, 8U> png_signature{
    std::byte{0x89U}, std::byte{'P'}, std::byte{'N'}, std::byte{'G'},
    std::byte{0x0DU}, std::byte{0x0AU}, std::byte{0x1AU}, std::byte{0x0AU}};

constexpr auto ihdr = std::uint32_t{0x49484452U};
constexpr auto plte = std::uint32_t{0x504C5445U};
constexpr auto idat = std::uint32_t{0x49444154U};
constexpr auto iend = std::uint32_t{0x49454E44U};
constexpr auto trns = std::uint32_t{0x74524E53U};

bool checked_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (left != 0U && right >
        std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

bool checked_add(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

bool is_critical(std::uint32_t type) noexcept {
    return ((type >> 24U) & 0x20U) == 0U;
}

} // namespace

bool try_parse_png(
    std::span<const std::byte> input,
    png_metadata& metadata,
    std::span<std::byte> compressed_output,
    bool copy_compressed,
    image_error& error) noexcept {
    metadata = {};
    error = image_error::none;
    if (input.size() < png_signature.size() ||
        !std::equal(png_signature.begin(), png_signature.end(), input.begin())) {
        error = image_error::invalid_signature;
        return false;
    }
    std::size_t offset = png_signature.size();
    std::size_t compressed_bytes = 0U;
    bool saw_header = false;
    bool saw_palette = false;
    bool saw_transparency = false;
    bool saw_data = false;
    bool data_ended = false;
    bool saw_end = false;
    while (offset < input.size()) {
        if (input.size() - offset < 12U) {
            error = image_error::invalid_chunk;
            return false;
        }
        const auto length = static_cast<std::size_t>(read_u32(input, offset));
        const auto type = read_u32(input, offset + 4U);
        if (length > input.size() - offset - 12U) {
            error = image_error::invalid_chunk;
            return false;
        }
        const auto data = input.subspan(offset + 8U, length);
        const auto expected_crc = read_u32(input, offset + 8U + length);
        auto crc = update_crc32(
            0xFFFFFFFFU, input.subspan(offset + 4U, 4U));
        crc = update_crc32(crc, data) ^ 0xFFFFFFFFU;
        if (crc != expected_crc) {
            error = image_error::checksum_mismatch;
            return false;
        }
        if (!saw_header && type != ihdr) {
            error = image_error::invalid_chunk;
            return false;
        }
        if (type == ihdr) {
            if (saw_header || length != 13U) {
                error = image_error::invalid_chunk;
                return false;
            }
            saw_header = true;
            const auto width = read_u32(data, 0U);
            const auto height = read_u32(data, 4U);
            const auto bit_depth = std::to_integer<std::uint8_t>(data[8U]);
            const auto color_type = std::to_integer<std::uint8_t>(data[9U]);
            const auto compression = std::to_integer<std::uint8_t>(data[10U]);
            const auto filter = std::to_integer<std::uint8_t>(data[11U]);
            const auto interlace = std::to_integer<std::uint8_t>(data[12U]);
            std::uint8_t channels = 0U;
            bool supported_depth = false;
            switch (color_type) {
            case 0U:
                channels = 1U;
                supported_depth = bit_depth == 1U || bit_depth == 2U ||
                    bit_depth == 4U || bit_depth == 8U || bit_depth == 16U;
                break;
            case 2U:
                channels = 3U;
                supported_depth = bit_depth == 8U || bit_depth == 16U;
                break;
            case 3U:
                channels = 1U;
                supported_depth = bit_depth == 1U || bit_depth == 2U ||
                    bit_depth == 4U || bit_depth == 8U;
                break;
            case 4U:
                channels = 2U;
                supported_depth = bit_depth == 8U || bit_depth == 16U;
                break;
            case 6U:
                channels = 4U;
                supported_depth = bit_depth == 8U || bit_depth == 16U;
                break;
            default: break;
            }
            if (width == 0U || height == 0U || !supported_depth ||
                channels == 0U || compression != 0U || filter != 0U ||
                interlace > 1U) {
                error = image_error::unsupported_format;
                return false;
            }
            std::size_t pixels = 0U;
            std::size_t rgba_bytes = 0U;
            if (!checked_multiply(width, height, pixels) ||
                !checked_multiply(pixels, 4U, rgba_bytes)) {
                error = image_error::unsupported_format;
                return false;
            }
            metadata.requirements = {
                width,
                height,
                0U,
                0U,
                rgba_bytes,
                bit_depth,
                color_type,
                channels,
                interlace};
            png_layout layout{};
            if (!try_build_png_layout(metadata.requirements, layout)) {
                error = image_error::unsupported_format;
                return false;
            }
            metadata.requirements.filtered_bytes = layout.filtered_bytes;
        } else if (type == plte) {
            if (saw_palette || saw_data || length == 0U ||
                length > 768U || length % 3U != 0U ||
                metadata.requirements.color_type == 0U ||
                metadata.requirements.color_type == 4U ||
                (metadata.requirements.color_type == 3U &&
                    length / 3U >
                        (1U << metadata.requirements.bit_depth))) {
                error = image_error::invalid_chunk;
                return false;
            }
            saw_palette = true;
            metadata.palette = data;
        } else if (type == trns) {
            if (saw_transparency || saw_data) {
                error = image_error::invalid_chunk;
                return false;
            }
            const auto color_type = metadata.requirements.color_type;
            const auto valid = (color_type == 0U && length == 2U) ||
                (color_type == 2U && length == 6U) ||
                (color_type == 3U && saw_palette && length != 0U &&
                    length <= metadata.palette.size() / 3U);
            if (!valid) {
                error = image_error::invalid_chunk;
                return false;
            }
            saw_transparency = true;
            metadata.transparency = data;
        } else if (type == idat) {
            if (data_ended) {
                error = image_error::invalid_chunk;
                return false;
            }
            saw_data = true;
            std::size_t next_compressed = 0U;
            if (!checked_add(compressed_bytes, length, next_compressed)) {
                error = image_error::unsupported_format;
                return false;
            }
            if (copy_compressed) {
                if (next_compressed > compressed_output.size()) {
                    error = image_error::insufficient_buffer;
                    return false;
                }
                std::copy(data.begin(), data.end(),
                    compressed_output.begin() +
                        static_cast<std::ptrdiff_t>(compressed_bytes));
            }
            compressed_bytes = next_compressed;
        } else if (type == iend) {
            if (!saw_data || saw_end || length != 0U) {
                error = image_error::invalid_chunk;
                return false;
            }
            saw_end = true;
        } else if (is_critical(type)) {
            error = image_error::unsupported_format;
            return false;
        }
        if (saw_data && type != idat && type != iend) {
            data_ended = true;
        }
        offset += length + 12U;
        if (saw_end) {
            break;
        }
    }
    if (!saw_header || !saw_data || !saw_end || offset != input.size() ||
        compressed_bytes == 0U) {
        error = image_error::invalid_chunk;
        return false;
    }
    if (metadata.requirements.color_type == 3U &&
        (!saw_palette || metadata.transparency.size() >
            metadata.palette.size() / 3U)) {
        error = image_error::invalid_chunk;
        return false;
    }
    metadata.requirements.compressed_bytes = compressed_bytes;
    return true;
}

} // namespace progpu::native::image::detail
