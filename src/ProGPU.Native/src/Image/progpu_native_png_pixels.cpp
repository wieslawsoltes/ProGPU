#include "progpu_native_png_internal.hpp"

#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned managed PNG pixel contracts at
// checkpoint 93259231, constrained by PNG Third Edition sections 6.1 and 6.5.
// Validation and conversion are two bounded O(W * H * C) passes with O(1)
// storage; packed and Adam7 samples are read directly from caller scratch.
namespace progpu::native::image::detail {
namespace {

std::uint16_t read_sample(
    std::span<const std::byte> row,
    std::size_t sample_index,
    std::uint8_t bit_depth) noexcept {
    if (bit_depth == 16U) {
        const auto offset = sample_index * 2U;
        return static_cast<std::uint16_t>(
            (static_cast<std::uint16_t>(
                 std::to_integer<std::uint8_t>(row[offset])) << 8U) |
            std::to_integer<std::uint8_t>(row[offset + 1U]));
    }
    if (bit_depth == 8U) {
        return std::to_integer<std::uint8_t>(row[sample_index]);
    }
    const auto bit_offset = sample_index * bit_depth;
    const auto shift = 8U - bit_depth -
        static_cast<std::uint8_t>(bit_offset & 7U);
    const auto mask = static_cast<std::uint8_t>((1U << bit_depth) - 1U);
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint8_t>(row[bit_offset / 8U]) >> shift) & mask);
}

std::uint8_t scale_sample(
    std::uint16_t value,
    std::uint8_t bit_depth) noexcept {
    if (bit_depth == 8U) {
        return static_cast<std::uint8_t>(value);
    }
    const auto maximum = bit_depth == 16U
        ? 65535U
        : (1U << bit_depth) - 1U;
    return static_cast<std::uint8_t>(
        (static_cast<std::uint32_t>(value) * 255U + maximum / 2U) /
        maximum);
}

std::uint16_t read_transparent_sample(
    std::span<const std::byte> transparency,
    std::size_t index) noexcept {
    const auto offset = index * 2U;
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(
             std::to_integer<std::uint8_t>(transparency[offset])) << 8U) |
        std::to_integer<std::uint8_t>(transparency[offset + 1U]));
}

template <typename Visitor>
bool visit_pixels(
    const png_layout& layout,
    std::span<const std::byte> filtered,
    Visitor&& visitor) noexcept {
    std::size_t pass_offset = 0U;
    for (std::size_t pass_index = 0U;
         pass_index < layout.pass_count;
         ++pass_index) {
        const auto& pass = layout.passes[pass_index];
        const auto stride = pass.row_bytes + 1U;
        for (std::size_t row_index = 0U;
             row_index < pass.height;
             ++row_index) {
            const auto row = filtered.subspan(
                pass_offset + row_index * stride + 1U,
                pass.row_bytes);
            for (std::size_t column = 0U; column < pass.width; ++column) {
                const auto x = pass.start_x +
                    static_cast<std::uint32_t>(column) * pass.step_x;
                const auto y = pass.start_y +
                    static_cast<std::uint32_t>(row_index) * pass.step_y;
                if (!visitor(row, column, x, y)) {
                    return false;
                }
            }
        }
        pass_offset += pass.filtered_bytes;
    }
    return pass_offset == filtered.size();
}

bool validate_palette_indexes(
    const png_metadata& metadata,
    const png_layout& layout,
    std::span<const std::byte> filtered) noexcept {
    if (metadata.requirements.color_type != 3U) {
        return true;
    }
    const auto palette_entries = metadata.palette.size() / 3U;
    return visit_pixels(layout, filtered,
        [&](std::span<const std::byte> row,
            std::size_t column,
            std::uint32_t,
            std::uint32_t) noexcept {
            return read_sample(
                row, column, metadata.requirements.bit_depth) <
                palette_entries;
        });
}

} // namespace

bool try_convert_png_to_rgba(
    const png_metadata& metadata,
    std::span<const std::byte> filtered,
    std::span<std::byte> rgba_output) noexcept {
    const auto& requirements = metadata.requirements;
    png_layout layout{};
    if (!try_build_png_layout(requirements, layout) ||
        filtered.size() != layout.filtered_bytes ||
        rgba_output.size() != requirements.rgba_bytes ||
        !validate_palette_indexes(metadata, layout, filtered)) {
        return false;
    }
    return visit_pixels(layout, filtered,
        [&](std::span<const std::byte> row,
            std::size_t column,
            std::uint32_t x,
            std::uint32_t y) noexcept {
            const auto first_sample = column * requirements.channel_count;
            std::uint16_t raw_red = 0U;
            std::uint16_t raw_green = 0U;
            std::uint16_t raw_blue = 0U;
            std::uint16_t raw_alpha = requirements.bit_depth == 16U
                ? 65535U
                : static_cast<std::uint16_t>(
                    (1U << requirements.bit_depth) - 1U);
            std::uint8_t red = 0U;
            std::uint8_t green = 0U;
            std::uint8_t blue = 0U;
            std::uint8_t alpha = 255U;
            switch (requirements.color_type) {
            case 0U:
                raw_red = read_sample(row, first_sample,
                    requirements.bit_depth);
                red = green = blue = scale_sample(
                    raw_red, requirements.bit_depth);
                if (metadata.transparency.size() == 2U &&
                    read_transparent_sample(metadata.transparency, 0U) ==
                        raw_red) {
                    alpha = 0U;
                }
                break;
            case 2U:
                raw_red = read_sample(row, first_sample,
                    requirements.bit_depth);
                raw_green = read_sample(row, first_sample + 1U,
                    requirements.bit_depth);
                raw_blue = read_sample(row, first_sample + 2U,
                    requirements.bit_depth);
                red = scale_sample(raw_red, requirements.bit_depth);
                green = scale_sample(raw_green, requirements.bit_depth);
                blue = scale_sample(raw_blue, requirements.bit_depth);
                if (metadata.transparency.size() == 6U &&
                    read_transparent_sample(metadata.transparency, 0U) ==
                        raw_red &&
                    read_transparent_sample(metadata.transparency, 1U) ==
                        raw_green &&
                    read_transparent_sample(metadata.transparency, 2U) ==
                        raw_blue) {
                    alpha = 0U;
                }
                break;
            case 3U: {
                const auto palette_index = read_sample(
                    row, column, requirements.bit_depth);
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
                raw_red = read_sample(row, first_sample,
                    requirements.bit_depth);
                raw_alpha = read_sample(row, first_sample + 1U,
                    requirements.bit_depth);
                red = green = blue = scale_sample(
                    raw_red, requirements.bit_depth);
                alpha = scale_sample(raw_alpha, requirements.bit_depth);
                break;
            case 6U:
                raw_red = read_sample(row, first_sample,
                    requirements.bit_depth);
                raw_green = read_sample(row, first_sample + 1U,
                    requirements.bit_depth);
                raw_blue = read_sample(row, first_sample + 2U,
                    requirements.bit_depth);
                raw_alpha = read_sample(row, first_sample + 3U,
                    requirements.bit_depth);
                red = scale_sample(raw_red, requirements.bit_depth);
                green = scale_sample(raw_green, requirements.bit_depth);
                blue = scale_sample(raw_blue, requirements.bit_depth);
                alpha = scale_sample(raw_alpha, requirements.bit_depth);
                break;
            default:
                return false;
            }
            const auto destination =
                (static_cast<std::size_t>(y) * requirements.width + x) * 4U;
            rgba_output[destination] = static_cast<std::byte>(red);
            rgba_output[destination + 1U] = static_cast<std::byte>(green);
            rgba_output[destination + 2U] = static_cast<std::byte>(blue);
            rgba_output[destination + 3U] = static_cast<std::byte>(alpha);
            return true;
        });
}

} // namespace progpu::native::image::detail
