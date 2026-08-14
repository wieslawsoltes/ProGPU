#include "progpu_native_text.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <span>
#include <utility>
#include <vector>

namespace {

using progpu::native::text::font_error;
using progpu::native::text::open_type_tag;
using progpu::native::text::sfnt_font_view;
using progpu::native::text::sfnt_header_metrics;
using progpu::native::text::sfnt_horizontal_glyph_metrics;
using progpu::native::text::sfnt_horizontal_header_metrics;
using progpu::native::text::sfnt_table_view;

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

void write_u16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint16_t value) {
    destination[offset] = static_cast<std::byte>(value >> 8U);
    destination[offset + 1U] = static_cast<std::byte>(value);
}

void write_i16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::int16_t value) {
    write_u16(destination, offset, static_cast<std::uint16_t>(value));
}

void write_u32(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint32_t value) {
    destination[offset] = static_cast<std::byte>(value >> 24U);
    destination[offset + 1U] = static_cast<std::byte>(value >> 16U);
    destination[offset + 2U] = static_cast<std::byte>(value >> 8U);
    destination[offset + 3U] = static_cast<std::byte>(value);
}

struct table_data final {
    open_type_tag tag{};
    std::vector<std::byte> bytes{};
};

std::vector<std::byte> make_cmap() {
    std::vector<std::byte> result(80U);
    write_u16(result, 2U, 2U);
    write_u16(result, 4U, 3U);
    write_u16(result, 6U, 1U);
    write_u32(result, 8U, 20U);
    write_u16(result, 12U, 3U);
    write_u16(result, 14U, 10U);
    write_u32(result, 16U, 52U);

    write_u16(result, 20U, 4U);
    write_u16(result, 22U, 32U);
    write_u16(result, 26U, 4U);
    write_u16(result, 34U, 0x0041U);
    write_u16(result, 36U, 0xFFFFU);
    write_u16(result, 40U, 0x0041U);
    write_u16(result, 42U, 0xFFFFU);
    write_i16(result, 44U, -62);
    write_i16(result, 46U, 1);

    write_u16(result, 52U, 12U);
    write_u32(result, 56U, 28U);
    write_u32(result, 64U, 1U);
    write_u32(result, 68U, 0x1F600U);
    write_u32(result, 72U, 0x1F600U);
    write_u32(result, 76U, 7U);
    return result;
}

std::vector<std::byte> make_font(std::size_t face_offset = 0U) {
    std::vector<table_data> tables{};
    table_data head{open_type_tag::from_chars('h', 'e', 'a', 'd'),
        std::vector<std::byte>(54U)};
    write_u16(head.bytes, 18U, 1000U);
    write_i16(head.bytes, 36U, -20);
    write_i16(head.bytes, 38U, -200);
    write_i16(head.bytes, 40U, 900);
    write_i16(head.bytes, 42U, 800);
    write_i16(head.bytes, 50U, 1);
    tables.push_back(std::move(head));

    table_data hhea{open_type_tag::from_chars('h', 'h', 'e', 'a'),
        std::vector<std::byte>(36U)};
    write_i16(hhea.bytes, 4U, 800);
    write_i16(hhea.bytes, 6U, -200);
    write_i16(hhea.bytes, 8U, 40);
    write_u16(hhea.bytes, 10U, 1200U);
    write_u16(hhea.bytes, 34U, 2U);
    tables.push_back(std::move(hhea));

    table_data hmtx{open_type_tag::from_chars('h', 'm', 't', 'x'),
        std::vector<std::byte>(20U)};
    write_u16(hmtx.bytes, 0U, 500U);
    write_i16(hmtx.bytes, 2U, 10);
    write_u16(hmtx.bytes, 4U, 600U);
    write_i16(hmtx.bytes, 6U, 20);
    write_i16(hmtx.bytes, 10U, 30);
    tables.push_back(std::move(hmtx));

    table_data maxp{open_type_tag::from_chars('m', 'a', 'x', 'p'),
        std::vector<std::byte>(6U)};
    write_u16(maxp.bytes, 4U, 8U);
    tables.push_back(std::move(maxp));
    tables.push_back(table_data{
        open_type_tag::from_chars('c', 'm', 'a', 'p'), make_cmap()});

    const auto directory_size = 12U + tables.size() * 16U;
    std::size_t cursor = face_offset + directory_size;
    for (const auto& table : tables) {
        cursor += table.bytes.size();
    }
    std::vector<std::byte> result(cursor);
    const auto face = std::span<std::byte>(result).subspan(face_offset);
    write_u32(face, 0U, 0x00010000U);
    write_u16(face, 4U, static_cast<std::uint16_t>(tables.size()));
    cursor = face_offset + directory_size;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto record = face_offset + 12U + index * 16U;
        write_u32(result, record, tables[index].tag.value);
        write_u32(result, record + 4U, 0x1000U +
            static_cast<std::uint32_t>(index));
        write_u32(result, record + 8U, static_cast<std::uint32_t>(cursor));
        write_u32(result, record + 12U,
            static_cast<std::uint32_t>(tables[index].bytes.size()));
        for (const auto value : tables[index].bytes) {
            result[cursor++] = value;
        }
    }
    return result;
}

void borrowed_sfnt_view_reads_tables_metrics_and_cmap() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    require(error == font_error::none);
    require(font.face_index() == 0U);
    require(font.face_offset() == 0U);
    require(font.table_count() == 5U);
    require(!font.uses_symbol_character_map());
    require(font.data().data() == data.data());

    sfnt_table_view cmap{};
    require(font.try_get_table(
        open_type_tag::from_chars('c', 'm', 'a', 'p'), cmap));
    require(cmap.bytes.size() == 80U);
    require(cmap.checksum == 0x1004U);

    sfnt_header_metrics head{};
    require(font.try_get_header_metrics(head));
    require(head.units_per_em == 1000U);
    require(head.x_min == -20 && head.y_min == -200);
    require(head.x_max == 900 && head.y_max == 800);
    require(head.index_to_loc_format == 1);

    sfnt_horizontal_header_metrics horizontal{};
    require(font.try_get_horizontal_header_metrics(horizontal));
    require(horizontal.ascender == 800);
    require(horizontal.descender == -200);
    require(horizontal.line_gap == 40);
    require(horizontal.advance_width_max == 1200U);
    require(horizontal.number_of_horizontal_metrics == 2U);

    sfnt_horizontal_glyph_metrics glyph_metrics{};
    require(font.try_get_horizontal_glyph_metrics(3U, glyph_metrics));
    require(glyph_metrics.advance_width == 600U);
    require(glyph_metrics.left_side_bearing == 30);
    std::uint16_t glyph_count = 0U;
    require(font.try_get_glyph_count(glyph_count));
    require(glyph_count == 8U);

    std::uint16_t glyph = 0U;
    require(font.try_get_glyph_index(0x41U, glyph));
    require(glyph == 3U);
    require(font.try_get_glyph_index(0x1F600U, glyph));
    require(glyph == 7U);
    require(font.try_get_glyph_index(0x42U, glyph));
    require(glyph == 0U);
}

void collection_and_failure_paths_are_bounded() {
    auto collection = make_font(16U);
    write_u32(collection, 0U, 0x74746366U);
    write_u32(collection, 4U, 0x00010000U);
    write_u32(collection, 8U, 1U);
    write_u32(collection, 12U, 16U);

    std::uint32_t face_count = 0U;
    font_error error = font_error::none;
    require(sfnt_font_view::try_get_face_count(
        collection, face_count, &error));
    require(face_count == 1U);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(collection, 0U, font, &error));
    require(font.face_offset() == 16U);
    require(!sfnt_font_view::try_create(collection, 1U, font, &error));
    require(error == font_error::invalid_argument);

    const std::array<std::byte, 11U> short_data{};
    require(!sfnt_font_view::try_get_face_count(
        short_data, face_count, &error));
    require(error == font_error::invalid_face);

    auto truncated = make_font();
    write_u16(truncated, 4U, 0xFFFFU);
    require(!sfnt_font_view::try_create(truncated, 0U, font, &error));
    require(error == font_error::truncated_directory);

    auto invalid_collection = collection;
    write_u32(invalid_collection, 8U, 0U);
    require(!sfnt_font_view::try_get_face_count(
        invalid_collection, face_count, &error));
    require(error == font_error::invalid_collection);

    std::array<std::byte, 44U> woff{};
    write_u32(woff, 0U, 0x774F4646U);
    require(!sfnt_font_view::try_get_face_count(woff, face_count, &error));
    require(error == font_error::unsupported_container);
}

void table_directory_preserves_managed_duplicate_and_bounds_rules() {
    auto duplicate = make_font();
    const auto last_record = 12U + 4U * 16U;
    write_u32(duplicate, last_record,
        open_type_tag::from_chars('h', 'e', 'a', 'd').value);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(duplicate, 0U, font));
    sfnt_table_view head{};
    require(font.try_get_table(
        open_type_tag::from_chars('h', 'e', 'a', 'd'), head));
    require(head.checksum == 0x1004U);
    require(head.bytes.size() == 80U);

    auto invalid_record = make_font();
    write_u32(invalid_record, last_record + 8U, 0xFFFFFFF0U);
    require(sfnt_font_view::try_create(invalid_record, 0U, font));
    sfnt_table_view cmap{};
    require(!font.try_get_table(
        open_type_tag::from_chars('c', 'm', 'a', 'p'), cmap));
    std::uint16_t glyph = 99U;
    require(!font.try_get_glyph_index(0x41U, glyph));
    require(glyph == 0U);
}

} // namespace

int main() {
    borrowed_sfnt_view_reads_tables_metrics_and_cmap();
    collection_and_failure_paths_are_bounded();
    table_directory_preserves_managed_duplicate_and_bounds_rules();
    return 0;
}
