#include "progpu_native_cbdt_internal.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned TtfFont CBDT formats 17-19 at
// checkpoint 873593a7. Image lookup is O(1), uses O(1) storage, and returns a
// borrowed PNG payload plus the exact horizontal bitmap bearings.
namespace progpu::native::text::detail {

bool try_read_cbdt_image(
    std::span<const std::byte> cbdt,
    cbdt_image_range range,
    std::uint16_t image_format,
    std::uint16_t pixels_per_em,
    std::uint8_t strike_flags,
    sfnt_bitmap_glyph_data_view& result) noexcept {
    result = {};
    if (range.start < 4U || range.start >= range.end ||
        range.end > cbdt.size() ||
        range.start > std::numeric_limits<std::size_t>::max()) {
        return false;
    }
    const auto image_start = static_cast<std::size_t>(range.start);
    const auto image_end = static_cast<std::size_t>(range.end);
    cbdt_glyph_metrics metrics{};
    std::size_t data_offset = 0U;
    std::uint32_t data_length = 0U;
    switch (image_format) {
    case 17U:
        if ((strike_flags & 0x01U) == 0U ||
            image_end - image_start < 9U) {
            return false;
        }
        metrics = read_small_cbdt_metrics(cbdt, image_start);
        data_length = read_u32(cbdt, image_start + 5U);
        data_offset = image_start + 9U;
        break;
    case 18U:
        if (image_end - image_start < 12U) {
            return false;
        }
        metrics = read_big_cbdt_metrics(cbdt, image_start);
        data_length = read_u32(cbdt, image_start + 8U);
        data_offset = image_start + 12U;
        break;
    case 19U:
        if (!range.index_metrics.valid() ||
            image_end - image_start < 4U) {
            return false;
        }
        metrics = range.index_metrics;
        data_length = read_u32(cbdt, image_start);
        data_offset = image_start + 4U;
        break;
    default:
        return false;
    }
    if (!metrics.valid() || data_length == 0U ||
        data_offset > image_end || data_length > image_end - data_offset) {
        return false;
    }
    result = {
        cbdt.subspan(data_offset, data_length),
        open_type_tag::from_chars('p', 'n', 'g', ' '),
        pixels_per_em,
        72U,
        0,
        0,
        true,
        metrics.bearing_x,
        metrics.bearing_y};
    return true;
}

} // namespace progpu::native::text::detail
