#include "progpu_native_text.hpp"

#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <span>
#include <utility>
#include <vector>

namespace {

using progpu::native::text::font_error;
using progpu::native::text::open_type_tag;
using progpu::native::text::sfnt_composite_component;
using progpu::native::text::sfnt_composite_glyph_decode_requirements;
using progpu::native::text::sfnt_expanded_glyph_requirements;
using progpu::native::text::sfnt_font_view;
using progpu::native::text::sfnt_glyph_data_view;
using progpu::native::text::sfnt_glyph_decode_requirements;
using progpu::native::text::sfnt_glyph_kind;
using progpu::native::text::sfnt_glyph_variation_data_view;
using progpu::native::text::sfnt_gvar_header;
using progpu::native::text::sfnt_gvar_tuple_data;
using progpu::native::text::sfnt_gvar_tuple_header;
using progpu::native::text::sfnt_gvar_tuple_requirements;
using progpu::native::text::sfnt_header_metrics;
using progpu::native::text::sfnt_horizontal_glyph_metrics;
using progpu::native::text::sfnt_horizontal_header_metrics;
using progpu::native::text::sfnt_outline_point;
using progpu::native::text::sfnt_packed_delta_requirements;
using progpu::native::text::sfnt_packed_point_requirements;
using progpu::native::text::sfnt_packed_variation_data;
using progpu::native::text::sfnt_simple_glyph_path;
using progpu::native::text::sfnt_table_view;
using progpu::native::text::sfnt_variation_axis;

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

std::uint64_t hash_path_segments(
    std::span<const progpu_native_path_segment> segments) {
    std::uint64_t hash = 1469598103934665603ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    const auto append = [&](std::uint32_t value) {
        hash = (hash ^ value) * prime;
    };
    for (const auto& segment : segments) {
        append(segment.kind);
        append(std::bit_cast<std::uint32_t>(segment.p0.x));
        append(std::bit_cast<std::uint32_t>(segment.p0.y));
        append(std::bit_cast<std::uint32_t>(segment.p1.x));
        append(std::bit_cast<std::uint32_t>(segment.p1.y));
        append(std::bit_cast<std::uint32_t>(segment.p2.x));
        append(std::bit_cast<std::uint32_t>(segment.p2.y));
    }
    return hash;
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

std::vector<std::byte> make_fvar() {
    std::vector<std::byte> result(56U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 16U);
    write_u16(result, 6U, 2U);
    write_u16(result, 8U, 2U);
    write_u16(result, 10U, 20U);
    write_u16(result, 12U, 0U);
    write_u16(result, 14U, 0U);
    write_u32(result, 16U,
        open_type_tag::from_chars('o', 'p', 's', 'z').value);
    write_u32(result, 20U, 14U << 16U);
    write_u32(result, 24U, 14U << 16U);
    write_u32(result, 28U, 32U << 16U);
    write_u16(result, 32U, 1U);
    write_u16(result, 34U, 256U);
    write_u32(result, 36U,
        open_type_tag::from_chars('w', 'g', 'h', 't').value);
    write_u32(result, 40U, 100U << 16U);
    write_u32(result, 44U, 400U << 16U);
    write_u32(result, 48U, 900U << 16U);
    write_u16(result, 52U, 0U);
    write_u16(result, 54U, 257U);
    return result;
}

std::vector<std::byte> make_avar() {
    std::vector<std::byte> result(44U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 0U);
    write_u16(result, 6U, 2U);
    write_u16(result, 8U, 3U);
    write_i16(result, 10U, -16384);
    write_i16(result, 12U, -16384);
    write_i16(result, 14U, 0);
    write_i16(result, 16U, 0);
    write_i16(result, 18U, 16384);
    write_i16(result, 20U, 16384);
    write_u16(result, 22U, 5U);
    write_i16(result, 24U, -16384);
    write_i16(result, 26U, -16384);
    write_i16(result, 28U, 0);
    write_i16(result, 30U, 0);
    write_i16(result, 32U, 3277);
    write_i16(result, 34U, 2949);
    write_i16(result, 36U, 9830);
    write_i16(result, 38U, 8847);
    write_i16(result, 40U, 16384);
    write_i16(result, 42U, 16384);
    return result;
}

std::vector<std::byte> make_gvar() {
    std::vector<std::byte> result(64U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 2U);
    write_u16(result, 6U, 1U);
    write_u32(result, 8U, 38U);
    write_u16(result, 12U, 8U);
    write_u16(result, 14U, 0U);
    write_u32(result, 16U, 42U);
    for (std::size_t index = 1U; index <= 8U; ++index) {
        write_u16(result, 20U + index * 2U, 11U);
    }
    write_i16(result, 38U, -16384);
    write_i16(result, 40U, 8192);
    write_u16(result, 42U, 1U);
    write_u16(result, 44U, 20U);
    write_u16(result, 46U, 2U);
    write_u16(result, 48U, 0xE000U);
    write_i16(result, 50U, 8192);
    write_i16(result, 52U, -4096);
    write_i16(result, 54U, 0);
    write_i16(result, 56U, -8192);
    write_i16(result, 58U, 16384);
    write_i16(result, 60U, 0);
    result[62U] = std::byte{0U};
    result[63U] = std::byte{0U};
    return result;
}

std::vector<std::byte> make_font(
    std::size_t face_offset = 0U,
    std::size_t glyph_size = 22U,
    std::size_t second_glyph_size = 0U,
    bool include_variations = false,
    bool include_axis_mapping = false,
    bool include_glyph_variations = false) {
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
    table_data loca{open_type_tag::from_chars('l', 'o', 'c', 'a'),
        std::vector<std::byte>(36U)};
    write_u32(loca.bytes, 20U, static_cast<std::uint32_t>(glyph_size));
    const auto complete_glyph_size = glyph_size + second_glyph_size;
    write_u32(loca.bytes, 24U,
        static_cast<std::uint32_t>(complete_glyph_size));
    write_u32(loca.bytes, 28U,
        static_cast<std::uint32_t>(complete_glyph_size));
    write_u32(loca.bytes, 32U,
        static_cast<std::uint32_t>(complete_glyph_size));
    tables.push_back(std::move(loca));
    table_data glyf{open_type_tag::from_chars('g', 'l', 'y', 'f'),
        std::vector<std::byte>(complete_glyph_size)};
    write_i16(glyf.bytes, 0U, 1);
    write_i16(glyf.bytes, 2U, 10);
    write_i16(glyf.bytes, 4U, 0);
    write_i16(glyf.bytes, 6U, 30);
    write_i16(glyf.bytes, 8U, 40);
    write_u16(glyf.bytes, 10U, 2U);
    write_u16(glyf.bytes, 12U, 0U);
    glyf.bytes[14U] = static_cast<std::byte>(0x33U);
    glyf.bytes[15U] = static_cast<std::byte>(0x37U);
    glyf.bytes[16U] = static_cast<std::byte>(0x26U);
    glyf.bytes[17U] = static_cast<std::byte>(10U);
    glyf.bytes[18U] = static_cast<std::byte>(20U);
    glyf.bytes[19U] = static_cast<std::byte>(5U);
    glyf.bytes[20U] = static_cast<std::byte>(30U);
    glyf.bytes[21U] = static_cast<std::byte>(10U);
    tables.push_back(std::move(glyf));
    tables.push_back(table_data{
        open_type_tag::from_chars('c', 'm', 'a', 'p'), make_cmap()});
    if (include_variations) {
        tables.push_back(table_data{
            open_type_tag::from_chars('f', 'v', 'a', 'r'), make_fvar()});
    }
    if (include_axis_mapping) {
        tables.push_back(table_data{
            open_type_tag::from_chars('a', 'v', 'a', 'r'), make_avar()});
    }
    if (include_glyph_variations) {
        tables.push_back(table_data{
            open_type_tag::from_chars('g', 'v', 'a', 'r'), make_gvar()});
    }

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

void variation_axes_are_borrowed_bounded_and_transactional() {
    const auto data = make_font(0U, 22U, 0U, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::uint16_t count = 0U;
    require(font.try_get_variation_axis_count(count));
    require(count == 2U);
    std::array<sfnt_variation_axis, 1U> short_axes{};
    std::uint16_t written = 99U;
    font_error error = font_error::none;
    require(!font.try_decode_variation_axes(short_axes, written, &error));
    require(error == font_error::insufficient_buffer);
    require(written == 0U);
    require(short_axes[0].tag.value == 0U);

    std::array<sfnt_variation_axis, 2U> axes{};
    require(font.try_decode_variation_axes(axes, written, &error));
    require(error == font_error::none && written == 2U);
    require(axes[0].tag ==
        open_type_tag::from_chars('o', 'p', 's', 'z'));
    require(axes[0].minimum() == 14.0F);
    require(axes[0].default_value() == 14.0F);
    require(axes[0].maximum() == 32.0F);
    require(axes[0].hidden());
    require(axes[0].name_id == 256U);
    require(axes[1].tag ==
        open_type_tag::from_chars('w', 'g', 'h', 't'));
    require(axes[1].minimum() == 100.0F);
    require(axes[1].default_value() == 400.0F);
    require(axes[1].maximum() == 900.0F);
    require(!axes[1].hidden());
    require(axes[1].name_id == 257U);

    auto truncated = data;
    const auto table_count = static_cast<std::size_t>(
        (std::to_integer<std::uint16_t>(truncated[4U]) << 8U) |
        std::to_integer<std::uint16_t>(truncated[5U]));
    const auto fvar_record = 12U + (table_count - 1U) * 16U;
    write_u32(truncated, fvar_record + 12U, 20U);
    require(sfnt_font_view::try_create(truncated, 0U, font));
    require(!font.try_get_variation_axis_count(count, &error));
    require(error == font_error::invalid_face && count == 0U);
}

void variation_coordinates_apply_bounded_avar_mapping() {
    const auto data = make_font(0U, 22U, 0U, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::int16_t normalized = 99;
    font_error error = font_error::none;
    require(font.try_normalize_variation_coordinate(
        0U, 23 * 65536, normalized, &error));
    require(error == font_error::none && normalized == 8192);
    require(font.try_normalize_variation_coordinate(
        1U, 500 * 65536, normalized, &error));
    require(normalized == 2949);
    require(font.try_normalize_variation_coordinate(
        1U, 700 * 65536, normalized, &error));
    require(normalized == 8847);
    require(font.try_normalize_variation_coordinate(
        1U, 1000 * 65536, normalized, &error));
    require(normalized == 16384);
    require(!font.try_normalize_variation_coordinate(
        2U, 0, normalized, &error));
    require(error == font_error::invalid_argument && normalized == 0);

    auto truncated = data;
    truncated.pop_back();
    require(sfnt_font_view::try_create(truncated, 0U, font));
    require(font.try_normalize_variation_coordinate(
        1U, 700 * 65536, normalized, &error));
    require(error == font_error::none && normalized == 9830);
}

void packed_variation_streams_are_transactional_and_exact() {
    const std::array point_bytes{
        std::byte{4U},
        std::byte{3U},
        std::byte{1U},
        std::byte{2U},
        std::byte{0U},
        std::byte{5U}};
    sfnt_packed_point_requirements point_requirements{};
    font_error error = font_error::none;
    require(sfnt_packed_variation_data::try_get_point_requirements(
        point_bytes, point_requirements, &error));
    require(point_requirements.point_count == 4U);
    require(point_requirements.bytes_consumed == point_bytes.size());
    require(!point_requirements.all_points);
    std::array<std::uint32_t, 3U> short_points{};
    std::uint32_t written = 99U;
    std::size_t consumed = 99U;
    require(!sfnt_packed_variation_data::try_decode_points(
        point_bytes, short_points, written, consumed, &error));
    require(error == font_error::insufficient_buffer);
    require(written == 0U && consumed == 0U);
    require(short_points[0] == 0U);
    std::array<std::uint32_t, 4U> points{};
    require(sfnt_packed_variation_data::try_decode_points(
        point_bytes, points, written, consumed, &error));
    require(written == 4U && consumed == point_bytes.size());
    require(points == std::array<std::uint32_t, 4U>{1U, 3U, 3U, 8U});

    const std::array all_points{std::byte{0U}};
    require(sfnt_packed_variation_data::try_get_point_requirements(
        all_points, point_requirements, &error));
    require(point_requirements.all_points);
    require(point_requirements.point_count == 0U);
    require(point_requirements.bytes_consumed == 1U);

    const std::array delta_bytes{
        std::byte{0x81U},
        std::byte{0x41U},
        std::byte{0x00U},
        std::byte{0x64U},
        std::byte{0xffU},
        std::byte{0xfeU},
        std::byte{0x01U},
        std::byte{0x03U},
        std::byte{0xfcU}};
    sfnt_packed_delta_requirements delta_requirements{};
    require(sfnt_packed_variation_data::try_get_delta_requirements(
        delta_bytes, 6U, delta_requirements, &error));
    require(delta_requirements.delta_count == 6U);
    require(delta_requirements.bytes_consumed == delta_bytes.size());
    std::array<std::int16_t, 6U> deltas{};
    require(sfnt_packed_variation_data::try_decode_deltas(
        delta_bytes,
        deltas,
        6U,
        written,
        consumed,
        &error));
    require(written == 6U && consumed == delta_bytes.size());
    require(deltas == std::array<std::int16_t, 6U>{0, 0, 100, -2, 3, -4});

    const std::array invalid_points{
        std::byte{2U}, std::byte{2U}, std::byte{1U}};
    require(!sfnt_packed_variation_data::try_get_point_requirements(
        invalid_points, point_requirements, &error));
    require(error == font_error::invalid_glyph);
    const std::array invalid_deltas{std::byte{0x03U}, std::byte{1U}};
    require(!sfnt_packed_variation_data::try_get_delta_requirements(
        invalid_deltas, 2U, delta_requirements, &error));
    require(error == font_error::invalid_glyph);
}

void glyph_variation_tuple_headers_are_bounded_and_exact() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_gvar_tuple_requirements requirements{};
    font_error error = font_error::none;
    require(font.try_get_glyph_variation_tuple_requirements(
        0U, requirements, &error));
    require(error == font_error::none);
    require(requirements.tuple_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 5U> short_coordinates{};
    std::uint16_t headers_written = 99U;
    std::uint32_t coordinates_written = 99U;
    require(!font.try_decode_glyph_variation_tuple_headers(
        0U,
        headers,
        short_coordinates,
        headers_written,
        coordinates_written,
        &error));
    require(error == font_error::insufficient_buffer);
    require(headers_written == 0U && coordinates_written == 0U);
    require(headers[0].flags == 0U && short_coordinates[0] == 0);
    std::array<std::int16_t, 6U> coordinates{};
    require(font.try_decode_glyph_variation_tuple_headers(
        0U,
        headers,
        coordinates,
        headers_written,
        coordinates_written,
        &error));
    require(headers_written == 1U && coordinates_written == 6U);
    require(headers[0].serialized_data_size == 2U);
    require(headers[0].flags == 0xE000U);
    require(headers[0].has_private_point_numbers());
    require(coordinates ==
        std::array<std::int16_t, 6U>{0, -8192, 8192, -4096, 16384, 0});
    const std::array<std::int16_t, 2U> rising{4096, -4096};
    require(sfnt_gvar_tuple_data::calculate_scalar(rising, coordinates) ==
        0.5F);
    const std::array<std::int16_t, 2U> falling{8192, -2048};
    require(sfnt_gvar_tuple_data::calculate_scalar(falling, coordinates) ==
        0.5F);
    const std::array<std::int16_t, 2U> outside{8192, 4096};
    require(sfnt_gvar_tuple_data::calculate_scalar(outside, coordinates) ==
        0.0F);
}

void borrowed_sfnt_view_reads_tables_metrics_and_cmap() {
    const auto data = make_font();
    sfnt_font_view font{};
    font_error error = font_error::invalid_argument;
    require(sfnt_font_view::try_create(data, 0U, font, &error));
    require(error == font_error::none);
    require(font.face_index() == 0U);
    require(font.face_offset() == 0U);
    require(font.table_count() == 7U);
    require(!font.uses_symbol_character_map());
    require(font.data().data() == data.data());

    sfnt_table_view cmap{};
    require(font.try_get_table(
        open_type_tag::from_chars('c', 'm', 'a', 'p'), cmap));
    require(cmap.bytes.size() == 80U);
    require(cmap.checksum == 0x1006U);

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

    sfnt_glyph_data_view empty_glyph{};
    require(font.try_get_glyph_data(3U, empty_glyph));
    require(empty_glyph.empty());
    sfnt_glyph_data_view glyph_data{};
    require(font.try_get_glyph_data(4U, glyph_data));
    require(!glyph_data.empty());
    require(glyph_data.bytes.size() == 22U);
    require(glyph_data.contour_count == 1);
    require(glyph_data.x_min == 10 && glyph_data.y_min == 0);
    require(glyph_data.x_max == 30 && glyph_data.y_max == 40);

    sfnt_glyph_decode_requirements requirements{};
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::none);
    require(requirements.kind == sfnt_glyph_kind::simple);
    require(requirements.contour_count == 1U);
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 2U);
    require(requirements.instruction_bytes == 0U);
    std::array<std::uint16_t, 1U> contour_ends{};
    std::array<sfnt_outline_point, 3U> outline_points{};
    require(font.try_decode_simple_glyph(
        4U, contour_ends, outline_points, &error));
    require(contour_ends[0] == 2U);
    require(outline_points[0].x == 10 && outline_points[0].y == 0);
    require(outline_points[0].on_curve());
    require(outline_points[1].x == 30 && outline_points[1].y == 30);
    require(outline_points[1].on_curve());
    require(outline_points[2].x == 25 && outline_points[2].y == 40);
    require(!outline_points[2].on_curve());
    std::uint32_t segment_count = 0U;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contour_ends, outline_points, segment_count, &error));
    require(segment_count == 2U);
    std::array<progpu_native_path_segment, 2U> path_segments{};
    std::uint32_t written = 0U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contour_ends, outline_points, path_segments, written, &error));
    require(written == 2U);
    require(path_segments[0].kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
    require(path_segments[0].p0.x == 10.0F &&
        path_segments[0].p0.y == 0.0F);
    require(path_segments[0].p1.x == 30.0F &&
        path_segments[0].p1.y == 30.0F);
    require(path_segments[1].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(path_segments[1].p0.x == 30.0F &&
        path_segments[1].p0.y == 30.0F);
    require(path_segments[1].p1.x == 25.0F &&
        path_segments[1].p1.y == 40.0F);
    require(path_segments[1].p2.x == 10.0F &&
        path_segments[1].p2.y == 0.0F);
    require(!font.try_decode_simple_glyph(
        4U,
        contour_ends,
        std::span<sfnt_outline_point>{},
        &error));
    require(error == font_error::insufficient_buffer);

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
    const auto last_record = 12U + 6U * 16U;
    write_u32(duplicate, last_record,
        open_type_tag::from_chars('h', 'e', 'a', 'd').value);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(duplicate, 0U, font));
    sfnt_table_view head{};
    require(font.try_get_table(
        open_type_tag::from_chars('h', 'e', 'a', 'd'), head));
    require(head.checksum == 0x1006U);
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

void simple_glyph_repeat_composite_and_malformed_paths_are_explicit() {
    auto repeated = make_font();
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(repeated, 0U, font));
    sfnt_table_view glyf{};
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf));
    const auto glyph_offset = static_cast<std::size_t>(
        glyf.bytes.data() - repeated.data());
    repeated[glyph_offset + 14U] = static_cast<std::byte>(0x39U);
    repeated[glyph_offset + 15U] = static_cast<std::byte>(2U);
    require(sfnt_font_view::try_create(repeated, 0U, font));
    sfnt_glyph_decode_requirements requirements{};
    font_error error = font_error::none;
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 3U);
    std::array<std::uint16_t, 1U> contour_ends{};
    std::array<sfnt_outline_point, 3U> points{};
    require(font.try_decode_simple_glyph(
        4U, contour_ends, points, &error));
    for (const auto& point : points) {
        require(point.x == 0 && point.y == 0 && point.on_curve());
    }

    std::uint32_t segment_count = 0U;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contour_ends, points, segment_count, &error));
    require(segment_count == 3U);
    std::array<progpu_native_path_segment, 3U> repeated_segments{};
    std::uint32_t written = 0U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contour_ends, points, repeated_segments, written, &error));
    require(written == 3U);
    for (const auto& segment : repeated_segments) {
        require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE);
    }

    auto excessive_repeat = repeated;
    excessive_repeat[glyph_offset + 15U] = static_cast<std::byte>(3U);
    require(sfnt_font_view::try_create(excessive_repeat, 0U, font));
    require(!font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::invalid_glyph);

    auto decreasing_ends = make_font();
    write_i16(decreasing_ends, glyph_offset, 2);
    write_u16(decreasing_ends, glyph_offset + 10U, 2U);
    write_u16(decreasing_ends, glyph_offset + 12U, 2U);
    require(sfnt_font_view::try_create(decreasing_ends, 0U, font));
    require(!font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::invalid_glyph);

    auto zero_contours = make_font();
    write_i16(zero_contours, glyph_offset, 0);
    require(sfnt_font_view::try_create(zero_contours, 0U, font));
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.kind == sfnt_glyph_kind::empty);

    auto composite = make_font();
    write_i16(composite, glyph_offset, -1);
    write_u16(composite, glyph_offset + 10U, 0x000BU);
    write_u16(composite, glyph_offset + 12U, 4U);
    write_i16(composite, glyph_offset + 14U, 12);
    write_i16(composite, glyph_offset + 16U, -7);
    write_i16(composite, glyph_offset + 18U, 8192);
    require(sfnt_font_view::try_create(composite, 0U, font));
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.kind == sfnt_glyph_kind::composite);
    require(!font.try_decode_simple_glyph(
        4U, contour_ends, points, &error));
    require(error == font_error::invalid_glyph);
    sfnt_composite_glyph_decode_requirements composite_requirements{};
    require(font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(composite_requirements.component_count == 1U);
    require(composite_requirements.instruction_bytes == 0U);
    std::array<sfnt_composite_component, 1U> component{};
    require(font.try_decode_composite_glyph(
        4U, component, &error));
    require(component[0].flags == 0x000BU);
    require(component[0].glyph_index == 4U);
    require(component[0].argument1 == 12);
    require(component[0].argument2 == -7);
    require(component[0].m00 == 0.5F && component[0].m11 == 0.5F);
    require(component[0].m01 == 0.0F && component[0].m10 == 0.0F);
    require(!font.try_decode_composite_glyph(
        4U, std::span<sfnt_composite_component>{}, &error));
    require(error == font_error::insufficient_buffer);

    auto two_components = make_font();
    write_i16(two_components, glyph_offset, -1);
    write_u16(two_components, glyph_offset + 10U, 0x0020U);
    write_u16(two_components, glyph_offset + 12U, 4U);
    two_components[glyph_offset + 14U] = static_cast<std::byte>(3U);
    two_components[glyph_offset + 15U] = static_cast<std::byte>(0xFEU);
    write_u16(two_components, glyph_offset + 16U, 0x0002U);
    write_u16(two_components, glyph_offset + 18U, 5U);
    two_components[glyph_offset + 20U] = static_cast<std::byte>(0xFDU);
    two_components[glyph_offset + 21U] = static_cast<std::byte>(4U);
    require(sfnt_font_view::try_create(two_components, 0U, font));
    require(font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(composite_requirements.component_count == 2U);
    std::array<sfnt_composite_component, 2U> decoded_components{};
    require(font.try_decode_composite_glyph(
        4U, decoded_components, &error));
    require(decoded_components[0].argument1 == 3);
    require(decoded_components[0].argument2 == 254);
    require(decoded_components[1].glyph_index == 5U);
    require(decoded_components[1].argument1 == -3);
    require(decoded_components[1].argument2 == 4);

    auto instructed_composite = make_font();
    write_i16(instructed_composite, glyph_offset, -1);
    write_u16(instructed_composite, glyph_offset + 10U, 0x0102U);
    write_u16(instructed_composite, glyph_offset + 12U, 4U);
    instructed_composite[glyph_offset + 14U] = static_cast<std::byte>(1U);
    instructed_composite[glyph_offset + 15U] = static_cast<std::byte>(2U);
    write_u16(instructed_composite, glyph_offset + 16U, 4U);
    require(sfnt_font_view::try_create(instructed_composite, 0U, font));
    require(font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(composite_requirements.component_count == 1U);
    require(composite_requirements.instruction_bytes == 4U);

    auto truncated_instructions = instructed_composite;
    write_u16(truncated_instructions, glyph_offset + 16U, 5U);
    require(sfnt_font_view::try_create(truncated_instructions, 0U, font));
    require(!font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(error == font_error::invalid_glyph);

    auto axis_composite = make_font();
    write_i16(axis_composite, glyph_offset, -1);
    write_u16(axis_composite, glyph_offset + 10U, 0x0043U);
    write_u16(axis_composite, glyph_offset + 12U, 4U);
    write_i16(axis_composite, glyph_offset + 14U, 1);
    write_i16(axis_composite, glyph_offset + 16U, 2);
    write_i16(axis_composite, glyph_offset + 18U, 8192);
    write_i16(axis_composite, glyph_offset + 20U, -8192);
    require(sfnt_font_view::try_create(axis_composite, 0U, font));
    require(font.try_decode_composite_glyph(4U, component, &error));
    require(component[0].m00 == 0.5F && component[0].m11 == -0.5F);

    auto matrix_composite = make_font(0U, 24U);
    require(sfnt_font_view::try_create(matrix_composite, 0U, font));
    sfnt_table_view matrix_glyf{};
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), matrix_glyf));
    const auto matrix_glyph_offset = static_cast<std::size_t>(
        matrix_glyf.bytes.data() - matrix_composite.data());
    write_i16(matrix_composite, matrix_glyph_offset, -1);
    write_u16(matrix_composite, matrix_glyph_offset + 10U, 0x0082U);
    write_u16(matrix_composite, matrix_glyph_offset + 12U, 4U);
    matrix_composite[matrix_glyph_offset + 14U] = static_cast<std::byte>(0U);
    matrix_composite[matrix_glyph_offset + 15U] = static_cast<std::byte>(0U);
    write_i16(matrix_composite, matrix_glyph_offset + 16U, 8192);
    write_i16(matrix_composite, matrix_glyph_offset + 18U, 4096);
    write_i16(matrix_composite, matrix_glyph_offset + 20U, -4096);
    write_i16(matrix_composite, matrix_glyph_offset + 22U, 16384);
    require(sfnt_font_view::try_create(matrix_composite, 0U, font));
    require(font.try_decode_composite_glyph(4U, component, &error));
    require(component[0].m00 == 0.5F && component[0].m01 == 0.25F);
    require(component[0].m10 == -0.25F && component[0].m11 == 1.0F);

    auto truncated_composite = composite;
    sfnt_table_view composite_loca{};
    require(sfnt_font_view::try_create(truncated_composite, 0U, font));
    require(font.try_get_table(
        open_type_tag::from_chars('l', 'o', 'c', 'a'), composite_loca));
    const auto composite_loca_offset = static_cast<std::size_t>(
        composite_loca.bytes.data() - truncated_composite.data());
    write_u32(truncated_composite, composite_loca_offset + 20U, 15U);
    require(sfnt_font_view::try_create(truncated_composite, 0U, font));
    require(!font.try_get_composite_glyph_decode_requirements(
        4U, composite_requirements, &error));
    require(error == font_error::invalid_glyph);

    auto truncated_coordinates = make_font();
    sfnt_table_view loca{};
    require(sfnt_font_view::try_create(
        truncated_coordinates, 0U, font));
    require(font.try_get_table(
        open_type_tag::from_chars('l', 'o', 'c', 'a'), loca));
    const auto loca_offset = static_cast<std::size_t>(
        loca.bytes.data() - truncated_coordinates.data());
    write_u32(truncated_coordinates, loca_offset + 20U, 21U);
    require(sfnt_font_view::try_create(
        truncated_coordinates, 0U, font));
    require(!font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(error == font_error::invalid_glyph);
}

void simple_glyph_path_preserves_implicit_midpoints_and_is_transactional() {
    const std::array<std::uint16_t, 1U> contour_ends{{2U}};
    const std::array<sfnt_outline_point, 3U> points{{
        {0, 0, 0U},
        {20, 0, 0U},
        {20, 20, 1U}}};
    std::uint32_t count = 0U;
    font_error error = font_error::none;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contour_ends, points, count, &error));
    require(count == 2U);
    std::array<progpu_native_path_segment, 2U> segments{};
    std::uint32_t written = 99U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contour_ends, points, segments, written, &error));
    require(written == 2U);
    require(segments[0].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(segments[0].p0.x == 20.0F && segments[0].p0.y == 20.0F);
    require(segments[0].p1.x == 0.0F && segments[0].p1.y == 0.0F);
    require(segments[0].p2.x == 10.0F && segments[0].p2.y == 0.0F);
    require(segments[1].kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC);
    require(segments[1].p0.x == 10.0F && segments[1].p0.y == 0.0F);
    require(segments[1].p1.x == 20.0F && segments[1].p1.y == 0.0F);
    require(segments[1].p2.x == 20.0F && segments[1].p2.y == 20.0F);

    written = 99U;
    require(!sfnt_simple_glyph_path::try_write_segments(
        contour_ends,
        points,
        std::span<progpu_native_path_segment>{},
        written,
        &error));
    require(written == 0U);
    require(error == font_error::insufficient_buffer);

    const std::array<std::uint16_t, 1U> invalid_ends{{1U}};
    require(!sfnt_simple_glyph_path::try_get_segment_count(
        invalid_ends, points, count, &error));
    require(count == 0U);
    require(error == font_error::invalid_argument);

    const std::array<std::uint16_t, 1U> singleton_end{{0U}};
    const std::array<sfnt_outline_point, 1U> singleton{{{4, 9, 1U}}};
    require(sfnt_simple_glyph_path::try_get_segment_count(
        singleton_end, singleton, count, &error));
    require(count == 0U);

    const std::array<std::uint16_t, 2U> two_contours{{1U, 3U}};
    const std::array<sfnt_outline_point, 4U> contour_points{{
        {0, 0, 1U},
        {5, 0, 1U},
        {20, 20, 1U},
        {25, 20, 1U}}};
    require(sfnt_simple_glyph_path::try_get_segment_count(
        two_contours, contour_points, count, &error));
    require(count == 4U);
    std::array<progpu_native_path_segment, 4U> contour_segments{};
    require(sfnt_simple_glyph_path::try_write_segments(
        two_contours,
        contour_points,
        contour_segments,
        written,
        &error));
    require(written == 4U);
    require(contour_segments[2].p0.x == 20.0F);
    require(contour_segments[2].p0.y == 20.0F);
}

void expanded_composite_glyphs_preserve_transforms_and_point_attachment() {
    auto scaled = make_font(0U, 22U, 20U);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(scaled, 0U, font));
    sfnt_table_view glyf{};
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf));
    const auto composite_offset = static_cast<std::size_t>(
        glyf.bytes.data() - scaled.data()) + 22U;
    write_i16(scaled, composite_offset, -1);
    write_u16(scaled, composite_offset + 10U, 0x000BU);
    write_u16(scaled, composite_offset + 12U, 4U);
    write_i16(scaled, composite_offset + 14U, 12);
    write_i16(scaled, composite_offset + 16U, -7);
    write_i16(scaled, composite_offset + 18U, 8192);
    require(sfnt_font_view::try_create(scaled, 0U, font));

    sfnt_expanded_glyph_requirements requirements{};
    font_error error = font_error::none;
    require(font.try_get_expanded_glyph_requirements(
        5U, requirements, &error));
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 2U);
    require(requirements.simple_point_scratch_count == 3U);
    require(requirements.simple_contour_scratch_count == 1U);
    std::array<std::uint16_t, 1U> contour_scratch{};
    std::array<sfnt_outline_point, 3U> point_scratch{};
    std::array<progpu_native_point, 3U> points{};
    std::array<progpu_native_path_segment, 2U> segments{};
    std::uint32_t points_written = 0U;
    std::uint32_t segments_written = 0U;
    require(font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        points,
        segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 3U && segments_written == 2U);
    require(points[0].x == 17.0F && points[0].y == -7.0F);
    require(points[1].x == 27.0F && points[1].y == 8.0F);
    require(points[2].x == 24.5F && points[2].y == 13.0F);
    require(segments[0].p0.x == 17.0F && segments[0].p0.y == -7.0F);
    require(segments[1].p2.x == 17.0F && segments[1].p2.y == -7.0F);

    points_written = 99U;
    segments_written = 99U;
    require(!font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        std::span<progpu_native_point>{},
        segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 0U && segments_written == 0U);
    require(error == font_error::insufficient_buffer);

    auto attached = make_font(0U, 22U, 24U);
    require(sfnt_font_view::try_create(attached, 0U, font));
    require(font.try_get_table(
        open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf));
    const auto attached_offset = static_cast<std::size_t>(
        glyf.bytes.data() - attached.data()) + 22U;
    write_i16(attached, attached_offset, -1);
    write_u16(attached, attached_offset + 10U, 0x0022U);
    write_u16(attached, attached_offset + 12U, 4U);
    attached[attached_offset + 14U] = static_cast<std::byte>(0U);
    attached[attached_offset + 15U] = static_cast<std::byte>(0U);
    write_u16(attached, attached_offset + 16U, 0x0001U);
    write_u16(attached, attached_offset + 18U, 4U);
    write_u16(attached, attached_offset + 20U, 1U);
    write_u16(attached, attached_offset + 22U, 0U);
    require(sfnt_font_view::try_create(attached, 0U, font));
    require(font.try_get_expanded_glyph_requirements(
        5U, requirements, &error));
    require(requirements.point_count == 6U);
    require(requirements.path_segment_count == 4U);
    std::array<progpu_native_point, 6U> attached_points{};
    std::array<progpu_native_path_segment, 4U> attached_segments{};
    require(font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        attached_points,
        attached_segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 6U && segments_written == 4U);
    require(attached_points[1].x == attached_points[3].x);
    require(attached_points[1].y == attached_points[3].y);
    require(attached_points[4].x == 50.0F && attached_points[4].y == 60.0F);

    write_u16(attached, attached_offset + 18U, 5U);
    require(sfnt_font_view::try_create(attached, 0U, font));
    require(font.try_get_expanded_glyph_requirements(
        5U, requirements, &error));
    require(requirements.point_count == 3U);
    require(requirements.path_segment_count == 2U);
    require(font.try_decode_glyph_outline(
        5U,
        contour_scratch,
        point_scratch,
        attached_points,
        attached_segments,
        points_written,
        segments_written,
        &error));
    require(points_written == 3U && segments_written == 2U);
}

void production_inter_font_decodes_real_simple_outline() {
    std::ifstream stream(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_header_metrics header{};
    require(font.try_get_header_metrics(header));
    require(header.units_per_em == 2048U);
    require(header.x_min == -1546 && header.y_min == -668);
    require(header.x_max == 5290 && header.y_max == 2272);
    sfnt_horizontal_header_metrics horizontal{};
    require(font.try_get_horizontal_header_metrics(horizontal));
    require(horizontal.ascender == 1984);
    require(horizontal.descender == -494);
    require(horizontal.line_gap == 0);
    std::uint16_t glyph_index = 0U;
    require(font.try_get_glyph_index(0x53U, glyph_index));
    require(glyph_index == 397U);
    std::uint16_t glyph_count = 0U;
    require(font.try_get_glyph_count(glyph_count));
    require(glyph_count == 2937U);
    sfnt_horizontal_glyph_metrics glyph_metrics{};
    require(font.try_get_horizontal_glyph_metrics(
        glyph_index, glyph_metrics));
    require(glyph_metrics.advance_width == 1323U);
    require(glyph_metrics.left_side_bearing == 106);
    sfnt_glyph_data_view glyph_data{};
    require(font.try_get_glyph_data(glyph_index, glyph_data));
    require(glyph_data.x_min == 106 && glyph_data.y_min == -25);
    require(glyph_data.x_max == 1217 && glyph_data.y_max == 1510);
    sfnt_glyph_decode_requirements requirements{};
    require(font.try_get_glyph_decode_requirements(
        glyph_index, requirements));
    require(requirements.kind == sfnt_glyph_kind::simple);
    require(requirements.contour_count == 1U);
    require(requirements.point_count == 46U);
    require(requirements.path_segment_count == 34U);
    require(requirements.instruction_bytes == 59U);
    std::vector<std::uint16_t> contours(requirements.contour_count);
    std::vector<sfnt_outline_point> points(requirements.point_count);
    require(font.try_decode_simple_glyph(
        glyph_index, contours, points));
    require(contours.back() + 1U == points.size());
    bool has_on_curve = false;
    bool has_off_curve = false;
    for (const auto& point : points) {
        has_on_curve = has_on_curve || point.on_curve();
        has_off_curve = has_off_curve || !point.on_curve();
    }
    require(has_on_curve && has_off_curve);
    std::uint32_t segment_count = 0U;
    require(sfnt_simple_glyph_path::try_get_segment_count(
        contours, points, segment_count));
    require(segment_count == 34U);
    std::vector<progpu_native_path_segment> segments(segment_count);
    std::uint32_t written = 0U;
    require(sfnt_simple_glyph_path::try_write_segments(
        contours, points, segments, written));
    require(written == segment_count);
    require(segments.front().p0.x == 665.0F);
    require(segments.front().p0.y == -25.0F);
    require(hash_path_segments(segments) == 13245664145576799719ULL);
    const auto last_end = segments.back().kind ==
            PROGPU_NATIVE_PATH_SEGMENT_LINE
        ? segments.back().p1
        : segments.back().p2;
    require(segments.front().p0.x == last_end.x);
    require(segments.front().p0.y == last_end.y);

    std::uint16_t composite_index = 0U;
    require(font.try_get_glyph_index(0x00E9U, composite_index));
    sfnt_glyph_decode_requirements composite_kind{};
    require(font.try_get_glyph_decode_requirements(
        composite_index, composite_kind));
    sfnt_composite_glyph_decode_requirements composite_requirements{};
    require(composite_index == 618U);
    require(composite_kind.kind == sfnt_glyph_kind::composite);
    require(font.try_get_composite_glyph_decode_requirements(
        composite_index, composite_requirements));
    require(composite_requirements.component_count == 2U);
    require(composite_requirements.instruction_bytes == 0U);
    std::array<sfnt_composite_component, 2U> decoded{};
    require(font.try_decode_composite_glyph(composite_index, decoded));
    require(decoded[0].flags == 550U);
    require(decoded[0].glyph_index == 614U);
    require(decoded[0].argument1 == 0 && decoded[0].argument2 == 0);
    require(decoded[0].m00 == 1.0F && decoded[0].m11 == 1.0F);
    require(decoded[1].flags == 7U);
    require(decoded[1].glyph_index == 1770U);
    require(decoded[1].argument1 == 349 && decoded[1].argument2 == 0);
    sfnt_expanded_glyph_requirements expanded{};
    require(font.try_get_expanded_glyph_requirements(
        composite_index, expanded));
    std::vector<std::uint16_t> expanded_contours(
        expanded.simple_contour_scratch_count);
    std::vector<sfnt_outline_point> expanded_scratch(
        expanded.simple_point_scratch_count);
    std::vector<progpu_native_point> expanded_points(expanded.point_count);
    std::vector<progpu_native_path_segment> expanded_segments(
        expanded.path_segment_count);
    std::uint32_t expanded_points_written = 0U;
    std::uint32_t expanded_segments_written = 0U;
    require(font.try_decode_glyph_outline(
        composite_index,
        expanded_contours,
        expanded_scratch,
        expanded_points,
        expanded_segments,
        expanded_points_written,
        expanded_segments_written));
    require(expanded.point_count == 35U);
    require(expanded.path_segment_count == 27U);
    require(expanded.simple_point_scratch_count == 31U);
    require(expanded.simple_contour_scratch_count == 2U);
    require(expanded_points_written == 35U);
    require(expanded_segments_written == 27U);
    require(expanded_segments.front().p0.x == 630.0F);
    require(expanded_segments.front().p0.y == -23.0F);
    require(hash_path_segments(expanded_segments) ==
        5543379682355176128ULL);
}

void production_inter_variable_font_matches_fvar_axes() {
    std::ifstream stream(
        PROGPU_NATIVE_TEST_INTER_VARIABLE_FONT,
        std::ios::binary);
    require(stream.good());
    const std::vector<char> source{
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> data(source.size());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        data[index] = static_cast<std::byte>(source[index]);
    }
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    std::uint16_t count = 0U;
    require(font.try_get_variation_axis_count(count));
    require(count == 2U);
    std::array<sfnt_variation_axis, 2U> axes{};
    std::uint16_t written = 0U;
    require(font.try_decode_variation_axes(axes, written));
    require(written == axes.size());
    require(axes[0].tag ==
        open_type_tag::from_chars('o', 'p', 's', 'z'));
    require(axes[0].minimum_fixed == 14 * 65536);
    require(axes[0].default_fixed == 14 * 65536);
    require(axes[0].maximum_fixed == 32 * 65536);
    require(axes[0].flags == 0U && axes[0].name_id == 256U);
    require(axes[1].tag ==
        open_type_tag::from_chars('w', 'g', 'h', 't'));
    require(axes[1].minimum_fixed == 100 * 65536);
    require(axes[1].default_fixed == 400 * 65536);
    require(axes[1].maximum_fixed == 900 * 65536);
    require(axes[1].flags == 0U && axes[1].name_id == 257U);
    std::int16_t normalized = 0;
    require(font.try_normalize_variation_coordinate(
        0U, 23 * 65536, normalized));
    require(normalized == 8192);
    require(font.try_normalize_variation_coordinate(
        1U, 500 * 65536, normalized));
    require(normalized == 2949);
    require(font.try_normalize_variation_coordinate(
        1U, 700 * 65536, normalized));
    require(normalized == 8847);

    sfnt_gvar_header gvar{};
    require(font.try_get_gvar_header(gvar));
    require(gvar.axis_count == 2U);
    require(gvar.shared_tuple_count == 5U);
    require(gvar.glyph_count == 2937U);
    require(gvar.uses_long_offsets);
    std::array<std::int16_t, 2U> tuple{};
    std::uint16_t tuple_written = 0U;
    require(font.try_decode_gvar_shared_tuple(0U, tuple, tuple_written));
    require(tuple_written == 2U);
    require(tuple == std::array<std::int16_t, 2U>{16384, 0});
    require(font.try_decode_gvar_shared_tuple(4U, tuple, tuple_written));
    require(tuple == std::array<std::int16_t, 2U>{0, -16384});

    sfnt_glyph_variation_data_view glyph_variation{};
    require(font.try_get_glyph_variation_data(397U, glyph_variation));
    require(glyph_variation.bytes.size() == 594U);
    require(glyph_variation.tuple_count == 5U);
    require(glyph_variation.serialized_data_offset == 24U);
    require(glyph_variation.has_shared_point_numbers);
    sfnt_gvar_tuple_requirements tuple_requirements{};
    require(font.try_get_glyph_variation_tuple_requirements(
        397U, tuple_requirements));
    require(tuple_requirements.tuple_count == 5U);
    require(tuple_requirements.region_coordinate_count == 30U);
    std::array<sfnt_gvar_tuple_header, 4U> short_headers{};
    std::array<std::int16_t, 30U> tuple_coordinates{};
    std::uint16_t headers_written = 99U;
    std::uint32_t coordinates_written = 99U;
    require(!font.try_decode_glyph_variation_tuple_headers(
        397U,
        short_headers,
        tuple_coordinates,
        headers_written,
        coordinates_written));
    require(headers_written == 0U && coordinates_written == 0U);
    std::array<sfnt_gvar_tuple_header, 5U> tuple_headers{};
    require(font.try_decode_glyph_variation_tuple_headers(
        397U,
        tuple_headers,
        tuple_coordinates,
        headers_written,
        coordinates_written));
    require(headers_written == 5U && coordinates_written == 30U);
    require(tuple_headers[0].serialized_data_size == 108U);
    require(tuple_headers[0].flags == 0U);
    require(tuple_headers[0].region_coordinate_offset == 0U);
    require(!tuple_headers[0].has_private_point_numbers());
    require(tuple_coordinates[0] == 0 && tuple_coordinates[1] == 0);
    require(tuple_coordinates[2] == 16384 && tuple_coordinates[3] == 0);
    require(tuple_coordinates[4] == 16384 && tuple_coordinates[5] == 0);
    require(tuple_headers[1].serialized_data_size == 111U);
    require(tuple_headers[1].flags == 4U);
    require(tuple_headers[4].serialized_data_size == 107U);
    require(tuple_headers[4].flags == 1U);
    const std::array<std::int16_t, 2U> half_opsz{8192, 0};
    require(sfnt_gvar_tuple_data::calculate_scalar(
        half_opsz,
        std::span<const std::int16_t>(tuple_coordinates).first(6U)) ==
        0.5F);
    const std::array<std::int16_t, 2U> outside_opsz{-8192, 0};
    require(sfnt_gvar_tuple_data::calculate_scalar(
        outside_opsz,
        std::span<const std::int16_t>(tuple_coordinates).first(6U)) ==
        0.0F);
    sfnt_packed_point_requirements shared_points{};
    require(sfnt_packed_variation_data::try_get_point_requirements(
        glyph_variation.bytes.subspan(
            glyph_variation.serialized_data_offset),
        shared_points));
    require(shared_points.all_points && shared_points.bytes_consumed == 1U);
    require(font.try_get_glyph_variation_data(618U, glyph_variation));
    require(glyph_variation.bytes.size() == 60U);
    require(glyph_variation.tuple_count == 5U);
    require(glyph_variation.serialized_data_offset == 24U);
}

} // namespace

int main() {
    borrowed_sfnt_view_reads_tables_metrics_and_cmap();
    variation_axes_are_borrowed_bounded_and_transactional();
    variation_coordinates_apply_bounded_avar_mapping();
    packed_variation_streams_are_transactional_and_exact();
    glyph_variation_tuple_headers_are_bounded_and_exact();
    collection_and_failure_paths_are_bounded();
    table_directory_preserves_managed_duplicate_and_bounds_rules();
    simple_glyph_repeat_composite_and_malformed_paths_are_explicit();
    simple_glyph_path_preserves_implicit_midpoints_and_is_transactional();
    expanded_composite_glyphs_preserve_transforms_and_point_attachment();
    production_inter_font_decodes_real_simple_outline();
    production_inter_variable_font_matches_fvar_axes();
    return 0;
}
