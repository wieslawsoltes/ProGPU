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
using progpu::native::text::sfnt_font_view;
using progpu::native::text::sfnt_glyph_data_view;
using progpu::native::text::sfnt_glyph_decode_requirements;
using progpu::native::text::sfnt_glyph_kind;
using progpu::native::text::sfnt_header_metrics;
using progpu::native::text::sfnt_horizontal_glyph_metrics;
using progpu::native::text::sfnt_horizontal_header_metrics;
using progpu::native::text::sfnt_outline_point;
using progpu::native::text::sfnt_simple_glyph_path;
using progpu::native::text::sfnt_table_view;

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
    table_data loca{open_type_tag::from_chars('l', 'o', 'c', 'a'),
        std::vector<std::byte>(36U)};
    write_u32(loca.bytes, 20U, 22U);
    write_u32(loca.bytes, 24U, 22U);
    write_u32(loca.bytes, 28U, 22U);
    write_u32(loca.bytes, 32U, 22U);
    tables.push_back(std::move(loca));
    table_data glyf{open_type_tag::from_chars('g', 'l', 'y', 'f'),
        std::vector<std::byte>(22U)};
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
    require(sfnt_font_view::try_create(composite, 0U, font));
    require(font.try_get_glyph_decode_requirements(
        4U, requirements, &error));
    require(requirements.kind == sfnt_glyph_kind::composite);
    require(!font.try_decode_simple_glyph(
        4U, contour_ends, points, &error));
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
}

} // namespace

int main() {
    borrowed_sfnt_view_reads_tables_metrics_and_cmap();
    collection_and_failure_paths_are_bounded();
    table_directory_preserves_managed_duplicate_and_bounds_rules();
    simple_glyph_repeat_composite_and_malformed_paths_are_explicit();
    simple_glyph_path_preserves_implicit_midpoints_and_is_transactional();
    production_inter_font_decodes_real_simple_outline();
    return 0;
}
