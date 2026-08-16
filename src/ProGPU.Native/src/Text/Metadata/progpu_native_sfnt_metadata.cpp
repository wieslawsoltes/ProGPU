#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>

// Direct native port provenance: ProGPU-owned
// SfntFontMetadataReader.ReadFaceStyle and SfntFontFace.TryGetEmbeddingRights
// at repository checkpoint 654fbd97. These fixed-work queries borrow the
// already validated SFNT tables and never allocate.

namespace progpu::native::text {
namespace {

using detail::read_u16;

constexpr auto os2_tag = open_type_tag::from_chars('O', 'S', '/', '2');
constexpr auto head_tag = open_type_tag::from_chars('h', 'e', 'a', 'd');

} // namespace

bool sfnt_font_view::try_get_face_style(sfnt_face_style& result) const noexcept {
    result = {};
    sfnt_table_view table{};
    if (try_get_table(os2_tag, table) && table.bytes.size() >= 64U) {
        result.weight = std::clamp<std::uint16_t>(
            read_u16(table.bytes, 4U), 1U, 1000U);
        result.width = std::clamp<std::uint16_t>(
            read_u16(table.bytes, 6U), 1U, 9U);
        result.italic = (read_u16(table.bytes, 62U) & 0x0001U) != 0U;
    }
    if (try_get_table(head_tag, table) && table.bytes.size() >= 46U) {
        result.italic |= (read_u16(table.bytes, 44U) & 0x0002U) != 0U;
    }
    return true;
}

bool sfnt_font_view::try_get_embedding_rights(
    std::uint16_t& result) const noexcept {
    result = 0U;
    sfnt_table_view table{};
    if (!try_get_table(os2_tag, table) || table.bytes.size() < 10U) {
        return false;
    }
    result = read_u16(table.bytes, 8U);
    return true;
}

} // namespace progpu::native::text
