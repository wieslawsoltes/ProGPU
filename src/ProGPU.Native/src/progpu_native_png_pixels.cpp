#include "progpu_native_png_internal.hpp"

// Direct native port provenance: ProGPU-owned managed PNG pixel conversion.
// Conversion is one bounded O(W * H) pass with O(1) extra storage.
namespace progpu::native::image::detail {
namespace {

std::uint16_t read_u16(std::span<const std::byte> bytes) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(
             std::to_integer<std::uint8_t>(bytes[0U])) << 8U) |
        std::to_integer<std::uint8_t>(bytes[1U]));
}

} // namespace

bool try_convert_png_to_rgba(
    const png_metadata& metadata,
    std::span<const std::byte> filtered,
    std::span<std::byte> rgba_output) noexcept {
    const auto& requirements = metadata.requirements;
    const auto source_row_bytes =
        static_cast<std::size_t>(requirements.width) *
        requirements.channel_count;
    const auto source_stride = source_row_bytes + 1U;
    if (requirements.color_type == 3U) {
        const auto palette_entries = metadata.palette.size() / 3U;
        for (std::size_t row = 0U; row < requirements.height; ++row) {
            const auto source = filtered.subspan(
                row * source_stride + 1U, source_row_bytes);
            for (const auto index : source) {
                if (std::to_integer<std::uint8_t>(index) >= palette_entries) {
                    return false;
                }
            }
        }
    }
    std::size_t destination = 0U;
    for (std::size_t row = 0U; row < requirements.height; ++row) {
        const auto source = filtered.subspan(row * source_stride + 1U,
            source_row_bytes);
        for (std::size_t column = 0U; column < requirements.width; ++column) {
            const auto pixel = column * requirements.channel_count;
            std::uint8_t red = 0U;
            std::uint8_t green = 0U;
            std::uint8_t blue = 0U;
            std::uint8_t alpha = 255U;
            switch (requirements.color_type) {
            case 0U: {
                red = green = blue =
                    std::to_integer<std::uint8_t>(source[pixel]);
                if (metadata.transparency.size() == 2U &&
                    read_u16(metadata.transparency) == red) {
                    alpha = 0U;
                }
                break;
            }
            case 2U: {
                red = std::to_integer<std::uint8_t>(source[pixel]);
                green = std::to_integer<std::uint8_t>(source[pixel + 1U]);
                blue = std::to_integer<std::uint8_t>(source[pixel + 2U]);
                if (metadata.transparency.size() == 6U &&
                    read_u16(metadata.transparency) == red &&
                    read_u16(metadata.transparency.subspan(2U)) == green &&
                    read_u16(metadata.transparency.subspan(4U)) == blue) {
                    alpha = 0U;
                }
                break;
            }
            case 3U: {
                const auto palette_index =
                    std::to_integer<std::uint8_t>(source[pixel]);
                const auto palette_offset =
                    static_cast<std::size_t>(palette_index) * 3U;
                red = std::to_integer<std::uint8_t>(
                    metadata.palette[palette_offset]);
                green = std::to_integer<std::uint8_t>(
                    metadata.palette[palette_offset + 1U]);
                blue = std::to_integer<std::uint8_t>(
                    metadata.palette[palette_offset + 2U]);
                if (palette_index < metadata.transparency.size()) {
                    alpha = std::to_integer<std::uint8_t>(
                        metadata.transparency[palette_index]);
                }
                break;
            }
            case 4U:
                red = green = blue =
                    std::to_integer<std::uint8_t>(source[pixel]);
                alpha = std::to_integer<std::uint8_t>(source[pixel + 1U]);
                break;
            case 6U:
                red = std::to_integer<std::uint8_t>(source[pixel]);
                green = std::to_integer<std::uint8_t>(source[pixel + 1U]);
                blue = std::to_integer<std::uint8_t>(source[pixel + 2U]);
                alpha = std::to_integer<std::uint8_t>(source[pixel + 3U]);
                break;
            default:
                break;
            }
            rgba_output[destination++] = static_cast<std::byte>(red);
            rgba_output[destination++] = static_cast<std::byte>(green);
            rgba_output[destination++] = static_cast<std::byte>(blue);
            rgba_output[destination++] = static_cast<std::byte>(alpha);
        }
    }
    return true;
}

} // namespace progpu::native::image::detail
