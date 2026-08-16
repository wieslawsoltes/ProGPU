#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont.GetKerning at
// repository checkpoint 34b76eeb. This public font query intentionally keeps
// that API's Microsoft format-0 behavior; shaping has the wider fallback path.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;
using detail::read_u32;

constexpr auto kern_tag = open_type_tag::from_chars('k', 'e', 'r', 'n');

} // namespace

bool sfnt_font_view::try_get_design_kerning(
    std::uint32_t left_code_point,
    std::uint32_t right_code_point,
    std::int32_t& result) const noexcept {
    result = 0;
    std::uint16_t left = 0U;
    std::uint16_t right = 0U;
    if (!try_get_glyph_index(left_code_point, left) ||
        !try_get_glyph_index(right_code_point, right)) {
        return false;
    }
    sfnt_table_view kern{};
    if (!try_get_table(kern_tag, kern)) return true;
    const auto data = kern.bytes;
    if (!can_read(data, 0U, 4U)) return true;
    const auto subtable_count = read_u16(data, 2U);
    std::size_t subtable = 4U;
    const auto key = (static_cast<std::uint32_t>(left) << 16U) | right;
    for (std::uint16_t index = 0U; index < subtable_count; ++index) {
        if (!can_read(data, subtable, 6U)) break;
        const auto length = read_u16(data, subtable + 2U);
        const auto coverage = read_u16(data, subtable + 4U);
        if (length < 6U || !can_read(data, subtable, length)) break;
        if ((coverage >> 8U) == 0U && (coverage & 1U) != 0U &&
            length >= 14U) {
            const auto pair_count = read_u16(data, subtable + 6U);
            const auto records = subtable + 14U;
            if (records <= subtable + length &&
                static_cast<std::size_t>(pair_count) <=
                    (subtable + length - records) / 6U) {
                std::uint16_t low = 0U;
                std::uint16_t high = pair_count;
                while (low < high) {
                    const auto middle = static_cast<std::uint16_t>(
                        low + static_cast<std::uint16_t>((high - low) / 2U));
                    const auto record = records +
                        static_cast<std::size_t>(middle) * 6U;
                    const auto candidate = read_u32(data, record);
                    if (key < candidate) {
                        high = middle;
                    } else if (key > candidate) {
                        low = static_cast<std::uint16_t>(middle + 1U);
                    } else {
                        result = read_i16(data, record + 4U);
                        return true;
                    }
                }
            }
        }
        subtable += length;
    }
    return true;
}

} // namespace progpu::native::text
