// The BMP reader: the Windows and OS/2 core headers, 1 to 32 bits a pixel, bit
// fields, RLE4 and RLE8. Rows are read bottom up unless the height is negative.

#include "progpu_native_raster_internal.hpp"

#include <algorithm>
#include <array>
#include <bit>

namespace progpu::native::image::detail {

namespace {

constexpr std::uint32_t compression_rgb = 0U;
constexpr std::uint32_t compression_rle8 = 1U;
constexpr std::uint32_t compression_rle4 = 2U;
constexpr std::uint32_t compression_bitfields = 3U;
constexpr std::uint32_t compression_alpha_bitfields = 6U;
constexpr std::size_t file_header_bytes = 14U;

struct channel_mask final {
    std::uint32_t mask = 0U;
    std::uint32_t shift = 0U;
    std::uint32_t maximum = 0U;

    static channel_mask of(std::uint32_t value) noexcept {
        channel_mask channel{};
        channel.mask = value;
        if (value != 0U) {
            channel.shift = static_cast<std::uint32_t>(std::countr_zero(value));
            channel.maximum = value >> channel.shift;
        }
        return channel;
    }

    std::uint8_t read(std::uint32_t pixel, std::uint8_t absent) const noexcept {
        if (maximum == 0U) {
            return absent;
        }
        const std::uint64_t value = (pixel & mask) >> shift;
        return static_cast<std::uint8_t>(
            (value * 255U + maximum / 2U) / maximum);
    }
};

struct bmp_header final {
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    bool top_down = false;
    std::uint32_t bits = 0U;
    std::uint32_t compression = compression_rgb;
    std::size_t pixels = 0U;
    std::size_t palette = 0U;
    std::uint32_t palette_entries = 0U;
    std::size_t palette_entry_bytes = 4U;
    channel_mask red{};
    channel_mask green{};
    channel_mask blue{};
    channel_mask alpha{};
};

bool parse(
    std::span<const std::byte> input,
    bmp_header& header,
    image_error& error) noexcept {
    error = image_error::invalid_chunk;
    if (input.size() < file_header_bytes + 12U) {
        return false;
    }
    if (byte_at(input, 0U) != 0x42U || byte_at(input, 1U) != 0x4DU) {
        error = image_error::invalid_signature;
        return false;
    }
    header.pixels = read_u32_le(input, 10U);
    const std::size_t info = read_u32_le(input, file_header_bytes);
    if (info > input.size() - file_header_bytes) {
        return false;
    }
    std::int64_t height = 0;
    std::uint32_t used = 0U;
    if (info == 12U) {
        header.width = read_u16_le(input, 18U);
        height = read_u16_le(input, 20U);
        if (read_u16_le(input, 22U) != 1U) {
            return false;
        }
        header.bits = read_u16_le(input, 24U);
        header.palette_entry_bytes = 3U;
    } else if (info >= 40U) {
        const auto width = static_cast<std::int32_t>(read_u32_le(input, 18U));
        if (width <= 0) {
            return false;
        }
        header.width = static_cast<std::uint32_t>(width);
        height = static_cast<std::int32_t>(read_u32_le(input, 22U));
        if (read_u16_le(input, 26U) != 1U) {
            return false;
        }
        header.bits = read_u16_le(input, 28U);
        header.compression = read_u32_le(input, 30U);
        used = read_u32_le(input, 46U);
    } else {
        error = image_error::unsupported_format;
        return false;
    }
    if (height == 0 || height == INT32_MIN) {
        return false;
    }
    header.top_down = height < 0;
    header.height = static_cast<std::uint32_t>(height < 0 ? -height : height);

    const bool valid_bits = header.bits == 1U || header.bits == 4U ||
        header.bits == 8U || header.bits == 16U || header.bits == 24U ||
        header.bits == 32U;
    if (!valid_bits) {
        error = image_error::unsupported_format;
        return false;
    }
    const std::uint32_t compression = header.compression;
    const bool rle = compression == compression_rle8 ||
        compression == compression_rle4;
    const bool fields = compression == compression_bitfields ||
        compression == compression_alpha_bitfields;
    if ((compression != compression_rgb && !rle && !fields) ||
        (compression == compression_rle8 && header.bits != 8U) ||
        (compression == compression_rle4 && header.bits != 4U) ||
        (fields && header.bits != 16U && header.bits != 32U) ||
        (rle && header.top_down)) {
        error = image_error::unsupported_format;
        return false;
    }

    std::size_t after_info = file_header_bytes + info;
    if (fields) {
        const std::size_t mask_count =
            compression == compression_alpha_bitfields ? 4U : 3U;
        std::size_t masks = file_header_bytes + 40U;
        if (info == 40U) {
            after_info += mask_count * 4U;
        } else if (info < 40U + mask_count * 4U) {
            return false;
        }
        if (masks + mask_count * 4U > input.size()) {
            return false;
        }
        header.red = channel_mask::of(read_u32_le(input, masks));
        header.green = channel_mask::of(read_u32_le(input, masks + 4U));
        header.blue = channel_mask::of(read_u32_le(input, masks + 8U));
        // A header of 56 bytes or more has the alpha mask whatever the compression says.
        if (mask_count == 4U || info >= 56U) {
            header.alpha = channel_mask::of(read_u32_le(input, masks + 12U));
        }
    } else if (header.bits == 16U) {
        header.red = channel_mask::of(0x7C00U);
        header.green = channel_mask::of(0x03E0U);
        header.blue = channel_mask::of(0x001FU);
    } else if (header.bits == 24U || header.bits == 32U) {
        header.red = channel_mask::of(0x00FF0000U);
        header.green = channel_mask::of(0x0000FF00U);
        header.blue = channel_mask::of(0x000000FFU);
    }

    header.palette = after_info;
    if (header.bits <= 8U) {
        const std::uint32_t most = 1U << header.bits;
        header.palette_entries = used == 0U ? most : std::min(used, most);
        const std::size_t bytes =
            header.palette_entries * header.palette_entry_bytes;
        if (header.palette > input.size() ||
            bytes > input.size() - header.palette) {
            return false;
        }
    }
    if (header.pixels == 0U || header.pixels >= input.size()) {
        return false;
    }
    return true;
}

std::array<std::uint8_t, 4U> palette_entry(
    std::span<const std::byte> input,
    const bmp_header& header,
    std::uint32_t index) noexcept {
    if (index >= header.palette_entries) {
        return {0U, 0U, 0U, 255U};
    }
    const std::size_t at = header.palette + index * header.palette_entry_bytes;
    return {byte_at(input, at + 2U), byte_at(input, at + 1U), byte_at(input, at), 255U};
}

bool decode_rle(
    std::span<const std::byte> input,
    const bmp_header& header,
    std::span<std::byte> rgba,
    image_error& error) noexcept {
    const bool four = header.compression == compression_rle4;
    std::size_t at = header.pixels;
    std::uint32_t x = 0U;
    std::uint32_t y = 0U;
    const auto put = [&](std::uint32_t index) noexcept {
        if (x < header.width && y < header.height) {
            const auto color = palette_entry(input, header, index);
            const std::size_t row = header.height - 1U - y;
            write_rgba(rgba, row * header.width + x, color[0], color[1], color[2], color[3]);
        }
        ++x;
    };
    while (at + 1U < input.size()) {
        const std::uint32_t count = byte_at(input, at);
        const std::uint32_t value = byte_at(input, at + 1U);
        at += 2U;
        if (count != 0U) {
            for (std::uint32_t index = 0U; index < count; ++index) {
                if (four) {
                    put((index & 1U) == 0U ? value >> 4U : value & 0x0FU);
                } else {
                    put(value);
                }
            }
            continue;
        }
        if (value == 0U) {
            x = 0U;
            ++y;
        } else if (value == 1U) {
            return true;
        } else if (value == 2U) {
            if (at + 1U >= input.size()) {
                break;
            }
            x += byte_at(input, at);
            y += byte_at(input, at + 1U);
            at += 2U;
        } else {
            const std::size_t bytes = four ? (value + 1U) / 2U : value;
            if (bytes > input.size() - at) {
                error = image_error::invalid_compressed_data;
                return false;
            }
            for (std::uint32_t index = 0U; index < value; ++index) {
                if (four) {
                    const std::uint32_t packed = byte_at(input, at + index / 2U);
                    put((index & 1U) == 0U ? packed >> 4U : packed & 0x0FU);
                } else {
                    put(byte_at(input, at + index));
                }
            }
            // A run of literals is padded to a whole number of 16-bit words.
            at += bytes + (bytes & 1U);
        }
    }
    // A stream without its end marker keeps what it drew.
    return true;
}

} // namespace

bool read_bmp_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error& error) noexcept {
    bmp_header header{};
    if (!parse(input, header, error)) {
        return false;
    }
    if (!raster_bytes(header.width, header.height, result.rgba_bytes)) {
        error = image_error::unsupported_format;
        return false;
    }
    result.width = header.width;
    result.height = header.height;
    return true;
}

bool decode_bmp(
    std::span<const std::byte> input,
    std::span<std::byte> rgba,
    image_error& error) {
    bmp_header header{};
    if (!parse(input, header, error)) {
        return false;
    }
    std::fill(rgba.begin(), rgba.end(), std::byte{0});
    if (header.compression == compression_rle8 ||
        header.compression == compression_rle4) {
        return decode_rle(input, header, rgba, error);
    }
    const std::uint64_t row_bits =
        static_cast<std::uint64_t>(header.width) * header.bits;
    const std::uint64_t stride = ((row_bits + 31U) / 32U) * 4U;
    const std::uint64_t needed = stride * header.height;
    if (needed > input.size() - header.pixels) {
        error = image_error::invalid_compressed_data;
        return false;
    }
    for (std::uint32_t file_row = 0U; file_row < header.height; ++file_row) {
        const std::uint32_t row =
            header.top_down ? file_row : header.height - 1U - file_row;
        const std::size_t source =
            header.pixels + static_cast<std::size_t>(stride) * file_row;
        const std::size_t target = static_cast<std::size_t>(row) * header.width;
        for (std::uint32_t x = 0U; x < header.width; ++x) {
            if (header.bits <= 8U) {
                std::uint32_t index = 0U;
                if (header.bits == 8U) {
                    index = byte_at(input, source + x);
                } else if (header.bits == 4U) {
                    const std::uint32_t packed = byte_at(input, source + x / 2U);
                    index = (x & 1U) == 0U ? packed >> 4U : packed & 0x0FU;
                } else {
                    const std::uint32_t packed = byte_at(input, source + x / 8U);
                    index = (packed >> (7U - (x & 7U))) & 1U;
                }
                const auto color = palette_entry(input, header, index);
                write_rgba(rgba, target + x, color[0], color[1], color[2], color[3]);
                continue;
            }
            std::uint32_t pixel = 0U;
            if (header.bits == 16U) {
                pixel = read_u16_le(input, source + std::size_t{x} * 2U);
            } else if (header.bits == 24U) {
                const std::size_t at = source + std::size_t{x} * 3U;
                pixel = static_cast<std::uint32_t>(byte_at(input, at)) |
                    (static_cast<std::uint32_t>(byte_at(input, at + 1U)) << 8U) |
                    (static_cast<std::uint32_t>(byte_at(input, at + 2U)) << 16U);
            } else {
                pixel = read_u32_le(input, source + std::size_t{x} * 4U);
            }
            write_rgba(
                rgba,
                target + x,
                header.red.read(pixel, 0U),
                header.green.read(pixel, 0U),
                header.blue.read(pixel, 0U),
                header.alpha.read(pixel, 255U));
        }
    }
    return true;
}

} // namespace progpu::native::image::detail
