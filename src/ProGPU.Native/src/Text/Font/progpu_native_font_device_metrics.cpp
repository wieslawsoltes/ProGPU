#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>

// Original implementation from the OpenType hdmx field/validation contract:
// https://learn.microsoft.com/en-us/typography/opentype/spec/hdmx
namespace progpu::native::text {

bool sfnt_font_view::try_get_horizontal_device_metrics(
    std::uint16_t pixels_per_em,
    sfnt_horizontal_device_metrics& result,
    bool& available,
    font_error* error) const noexcept {
    result = {};
    available = false;
    if (error != nullptr) *error = font_error::none;
    const auto fail = [error](font_error reason = font_error::invalid_face) {
        if (error != nullptr) *error = reason;
        return false;
    };
    if (pixels_per_em == 0U) return fail(font_error::invalid_argument);
    std::uint16_t glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count) || glyph_count == 0U) return fail();

    // The general table accessor skips invalid ranges. Here a declared but
    // damaged record must remain distinguishable from an absent optional table.
    constexpr auto tag = open_type_tag::from_chars('h', 'd', 'm', 'x');
    std::span<const std::byte> bytes{};
    bool found_table = false;
    for (std::size_t index = 0U; index < table_count_; ++index) {
        const auto entry = directory_offset_ + index * 16U;
        if (detail::read_u32(data_, entry) != tag.value) continue;
        const auto offset = detail::read_u32(data_, entry + 8U);
        const auto length = detail::read_u32(data_, entry + 12U);
        if (found_table || !detail::can_read(data_, offset, length)) return fail();
        found_table = true;
        bytes = data_.subspan(offset, length);
    }
    if (!found_table) return true;
    if (bytes.size() < 8U || detail::read_u16(bytes, 0U) != 0U) return fail();
    const auto count = detail::read_u16(bytes, 2U);
    const std::size_t stride = detail::read_u32(bytes, 4U);
    const std::size_t payload = static_cast<std::size_t>(glyph_count) + 2U;
    if (stride < payload || stride % 4U != 0U ||
        count > (bytes.size() - 8U) / stride ||
        bytes.size() - 8U != static_cast<std::size_t>(count) * stride) return fail();

    sfnt_horizontal_device_metrics selected{};
    std::uint8_t previous_size = 0U;
    for (std::size_t index = 0U; index < count; ++index) {
        const auto record = bytes.subspan(8U + index * stride, stride);
        const auto size = std::to_integer<std::uint8_t>(record[0U]);
        const auto maximum = std::to_integer<std::uint8_t>(record[1U]);
        if (size <= previous_size) return fail();
        previous_size = size;
        const auto widths = record.subspan(2U, glyph_count);
        std::uint8_t actual_maximum = 0U;
        for (const auto width : widths)
            actual_maximum = std::max(actual_maximum, std::to_integer<std::uint8_t>(width));
        if (maximum != actual_maximum) return fail();
        for (const auto padding : record.subspan(payload))
            if (padding != std::byte{0}) return fail();
        if (size == pixels_per_em) selected = {widths, size, maximum};
    }
    result = selected;
    available = !selected.glyph_widths.empty();
    return true;
}

} // namespace progpu::native::text
