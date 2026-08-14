#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <span>
#include <string_view>
#include <utility>
#include <vector>

namespace {

using progpu::native::text::font_error;
using progpu::native::text::open_type_tag;
using progpu::native::text::sfnt_container_requirements;
using progpu::native::text::try_get_sfnt_container_requirements;
using progpu::native::text::try_normalize_sfnt_container;
using progpu::native::text::sfnt_composite_component;
using progpu::native::text::sfnt_composite_glyph_decode_requirements;
using progpu::native::text::sfnt_composite_glyph_variation_requirements;
using progpu::native::text::sfnt_composite_glyph_variation_scratch;
using progpu::native::text::sfnt_cff_data;
using progpu::native::text::sfnt_cff_fd_select_view;
using progpu::native::text::sfnt_cff_index_view;
using progpu::native::text::sfnt_cff1_font_view;
using progpu::native::text::sfnt_cff1_outline_requirements;
using progpu::native::text::sfnt_cff1_top_dictionary;
using progpu::native::text::sfnt_cff2_font_view;
using progpu::native::text::sfnt_cff2_outline_requirements;
using progpu::native::text::sfnt_cff2_top_dictionary;
using progpu::native::text::sfnt_bitmap_glyph_data_view;
using progpu::native::text::sfnt_color_glyph_layer;
using progpu::native::text::sfnt_svg_glyph_document_view;
using progpu::native::text::try_decode_svg_glyph_document;
using progpu::native::text::try_get_svg_glyph_document_size;
using progpu::native::text::sfnt_expanded_glyph_requirements;
using progpu::native::text::sfnt_font_view;
using progpu::native::text::sfnt_glyph_data_view;
using progpu::native::text::sfnt_glyph_decode_requirements;
using progpu::native::text::sfnt_glyph_kind;
using progpu::native::text::sfnt_glyph_variation_data_view;
using progpu::native::text::sfnt_glyph_phantom_variation_requirements;
using progpu::native::text::sfnt_glyph_phantom_variation_scratch;
using progpu::native::text::sfnt_gvar_header;
using progpu::native::text::sfnt_gvar_deltas;
using progpu::native::text::sfnt_gvar_tuple_data;
using progpu::native::text::sfnt_gvar_tuple_header;
using progpu::native::text::sfnt_gvar_tuple_requirements;
using progpu::native::text::sfnt_header_metrics;
using progpu::native::text::sfnt_horizontal_glyph_metrics;
using progpu::native::text::sfnt_horizontal_header_metrics;
using progpu::native::text::sfnt_item_variation_data;
using progpu::native::text::sfnt_item_variation_store_view;
using progpu::native::text::sfnt_delta_set_index_map_view;
using progpu::native::text::sfnt_outline_point;
using progpu::native::text::sfnt_packed_delta_requirements;
using progpu::native::text::sfnt_packed_point_requirements;
using progpu::native::text::sfnt_packed_variation_data;
using progpu::native::text::sfnt_simple_glyph_path;
using progpu::native::text::sfnt_simple_glyph_variation_requirements;
using progpu::native::text::sfnt_simple_glyph_variation_scratch;
using progpu::native::text::sfnt_varied_glyph_requirements;
using progpu::native::text::sfnt_varied_glyph_scratch;
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

std::uint64_t hash_complete_path_segments(
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
        append(std::bit_cast<std::uint32_t>(segment.p3.x));
        append(std::bit_cast<std::uint32_t>(segment.p3.y));
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

std::vector<std::byte> make_woff1_fixture() {
    constexpr std::array<unsigned char, 26U> compressed_values{
        0x78U, 0x01U, 0x4BU, 0xCBU, 0xACU, 0x48U, 0x4DU, 0xD1U,
        0xCDU, 0x28U, 0x4DU, 0x4BU, 0xCBU, 0x4DU, 0xCCU, 0xD3U,
        0x4DU, 0x1BU, 0xE5U, 0x41U, 0x79U, 0x00U, 0x62U, 0xC2U,
        0x6AU, 0x2DU};
    constexpr std::size_t first_source = 84U;
    constexpr std::size_t second_source = 112U;
    constexpr std::size_t declared_length = 116U;
    constexpr std::size_t normalized_size = 328U;
    std::vector<std::byte> result(declared_length);
    write_u32(result, 0U, 0x774F4646U);
    write_u32(result, 4U, 0x00010000U);
    write_u32(result, 8U, static_cast<std::uint32_t>(declared_length));
    write_u16(result, 12U, 2U);
    write_u32(result, 16U, static_cast<std::uint32_t>(normalized_size));
    write_u32(result, 44U,
        open_type_tag::from_chars('T', 'E', 'S', 'T').value);
    write_u32(result, 48U, first_source);
    write_u32(result, 52U,
        static_cast<std::uint32_t>(compressed_values.size()));
    write_u32(result, 56U, 280U);
    write_u32(result, 60U, 0x10203040U);
    write_u32(result, 64U,
        open_type_tag::from_chars('D', 'A', 'T', 'A').value);
    write_u32(result, 68U, second_source);
    write_u32(result, 72U, 4U);
    write_u32(result, 76U, 4U);
    write_u32(result, 80U, 0x50607080U);
    for (std::size_t index = 0U; index < compressed_values.size(); ++index) {
        result[first_source + index] =
            static_cast<std::byte>(compressed_values[index]);
    }
    result[second_source] = std::byte{1U};
    result[second_source + 1U] = std::byte{2U};
    result[second_source + 2U] = std::byte{3U};
    result[second_source + 3U] = std::byte{4U};
    return result;
}

void woff1_normalization_is_bounded_and_transactional() {
    auto woff = make_woff1_fixture();
    sfnt_container_requirements requirements{};
    font_error error = font_error::invalid_container;
    require(try_get_sfnt_container_requirements(
        woff, requirements, &error));
    require(error == font_error::none &&
        requirements.requires_normalization &&
        requirements.table_count == 2U &&
        requirements.normalized_bytes == 328U &&
        requirements.table_scratch_bytes == 280U);
    std::vector<std::byte> scratch(requirements.table_scratch_bytes);
    std::vector<std::byte> normalized(
        requirements.normalized_bytes, std::byte{0xA5U});
    sfnt_container_requirements normalized_requirements{};
    require(try_normalize_sfnt_container(
        woff,
        scratch,
        normalized,
        normalized_requirements,
        &error));
    require(normalized_requirements.normalized_bytes == normalized.size());
    require(normalized[0U] == std::byte{0U} &&
        normalized[1U] == std::byte{1U} &&
        normalized[4U] == std::byte{0U} &&
        normalized[5U] == std::byte{2U} &&
        normalized[6U] == std::byte{0U} &&
        normalized[7U] == std::byte{32U} &&
        normalized[8U] == std::byte{0U} &&
        normalized[9U] == std::byte{1U});
    constexpr std::string_view pattern = "fixed-huffman-";
    for (std::size_t index = 0U; index < 280U; ++index) {
        require(normalized[44U + index] ==
            static_cast<std::byte>(pattern[index % pattern.size()]));
    }
    require(normalized[324U] == std::byte{1U} &&
        normalized[327U] == std::byte{4U});
    sfnt_font_view face{};
    require(sfnt_font_view::try_create(normalized, 0U, face, &error));
    sfnt_table_view table{};
    require(face.try_get_table(
        open_type_tag::from_chars('T', 'E', 'S', 'T'), table));
    require(table.bytes.size() == 280U &&
        table.checksum == 0x10203040U);

    std::array<std::byte, 4U> raw{
        std::byte{0U}, std::byte{1U}, std::byte{0U}, std::byte{0U}};
    std::array<std::byte, 4U> raw_copy{};
    require(try_normalize_sfnt_container(
        raw, {}, raw_copy, normalized_requirements, &error));
    require(!normalized_requirements.requires_normalization &&
        raw == raw_copy);

    auto invalid = woff;
    invalid[84U] ^= std::byte{1U};
    std::fill(normalized.begin(), normalized.end(), std::byte{0xA5U});
    require(!try_normalize_sfnt_container(
        invalid,
        scratch,
        normalized,
        normalized_requirements,
        &error));
    require(error == font_error::invalid_compressed_data &&
        std::all_of(normalized.begin(), normalized.end(), [](std::byte value) {
            return value == std::byte{0xA5U};
        }));

    invalid = woff;
    write_u32(invalid, 0U, 0x774F4632U);
    require(!try_get_sfnt_container_requirements(
        invalid, requirements, &error));
    require(error == font_error::unsupported_container);
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
    std::vector<std::byte> result(72U);
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 0U);
    write_u16(result, 4U, 2U);
    write_u16(result, 6U, 1U);
    write_u32(result, 8U, 38U);
    write_u16(result, 12U, 8U);
    write_u16(result, 14U, 0U);
    write_u32(result, 16U, 42U);
    for (std::size_t index = 5U; index <= 8U; ++index) {
        write_u16(result, 20U + index * 2U, 15U);
    }
    write_i16(result, 38U, -16384);
    write_i16(result, 40U, 8192);
    write_u16(result, 42U, 1U);
    write_u16(result, 44U, 20U);
    write_u16(result, 46U, 10U);
    write_u16(result, 48U, 0xE000U);
    write_i16(result, 50U, 8192);
    write_i16(result, 52U, -4096);
    write_i16(result, 54U, 0);
    write_i16(result, 56U, -8192);
    write_i16(result, 58U, 16384);
    write_i16(result, 60U, 0);
    result[62U] = std::byte{0U};
    result[63U] = std::byte{6U};
    result[64U] = std::byte{2U};
    result[65U] = std::byte{0U};
    result[66U] = std::byte{0U};
    result[67U] = std::byte{4U};
    result[68U] = std::byte{10U};
    result[69U] = std::byte{0U};
    result[70U] = std::byte{0U};
    result[71U] = std::byte{0x86U};
    return result;
}

std::vector<std::byte> make_sbix_strike(
    std::uint16_t pixels_per_em,
    std::int16_t origin_x,
    std::int16_t origin_y,
    std::array<std::byte, 3U> image) {
    constexpr std::uint32_t data_start = 40U;
    constexpr std::uint32_t duplicate_start = data_start + 11U;
    constexpr std::uint32_t end = duplicate_start + 10U;
    std::vector<std::byte> result(end);
    write_u16(result, 0U, pixels_per_em);
    write_u16(result, 2U, 72U);
    write_u32(result, 4U, data_start);
    write_u32(result, 8U, data_start);
    write_u32(result, 12U, duplicate_start);
    for (std::size_t glyph = 3U; glyph <= 8U; ++glyph) {
        write_u32(result, 4U + glyph * 4U, end);
    }
    write_i16(result, data_start, origin_x);
    write_i16(result, data_start + 2U, origin_y);
    write_u32(result, data_start + 4U,
        open_type_tag::from_chars('p', 'n', 'g', ' ').value);
    result[data_start + 8U] = image[0U];
    result[data_start + 9U] = image[1U];
    result[data_start + 10U] = image[2U];
    write_i16(result, duplicate_start, 7);
    write_i16(result, duplicate_start + 2U, 8);
    write_u32(result, duplicate_start + 4U,
        open_type_tag::from_chars('d', 'u', 'p', 'e').value);
    write_u16(result, duplicate_start + 8U, 1U);
    return result;
}

std::vector<std::byte> make_sbix() {
    const auto strike_20 = make_sbix_strike(
        20U, -2, 6, {std::byte{20U}, std::byte{21U}, std::byte{22U}});
    const auto strike_40 = make_sbix_strike(
        40U, -4, 12, {std::byte{40U}, std::byte{41U}, std::byte{42U}});
    std::vector<std::byte> result(16U + strike_20.size() + strike_40.size());
    write_u16(result, 0U, 1U);
    write_u16(result, 2U, 1U);
    write_u32(result, 4U, 2U);
    write_u32(result, 8U, 16U);
    write_u32(result, 12U,
        static_cast<std::uint32_t>(16U + strike_20.size()));
    std::copy(strike_20.begin(), strike_20.end(), result.begin() + 16);
    std::copy(
        strike_40.begin(),
        strike_40.end(),
        result.begin() + static_cast<std::ptrdiff_t>(16U + strike_20.size()));
    return result;
}

std::vector<std::byte> make_svg_glyph_table(bool gzip) {
    const std::array<std::byte, 6U> plain{
        std::byte{0x3CU}, std::byte{0x73U}, std::byte{0x76U},
        std::byte{0x67U}, std::byte{0x2FU}, std::byte{0x3EU}};
    const std::array<std::byte, 26U> compressed{
        std::byte{0x1FU}, std::byte{0x8BU}, std::byte{0x08U},
        std::byte{0x00U}, std::byte{0x9CU}, std::byte{0x67U},
        std::byte{0x7FU}, std::byte{0x6AU}, std::byte{0x00U},
        std::byte{0x03U}, std::byte{0xB3U}, std::byte{0x29U},
        std::byte{0x2EU}, std::byte{0x4BU}, std::byte{0xD7U},
        std::byte{0xB7U}, std::byte{0x03U}, std::byte{0x00U},
        std::byte{0x49U}, std::byte{0xFBU}, std::byte{0xB9U},
        std::byte{0xACU}, std::byte{0x06U}, std::byte{0x00U},
        std::byte{0x00U}, std::byte{0x00U}};
    const std::span<const std::byte> document = gzip
        ? std::span<const std::byte>(compressed)
        : std::span<const std::byte>(plain);
    std::vector<std::byte> result(24U + document.size());
    write_u16(result, 0U, 0U);
    write_u32(result, 2U, 10U);
    write_u32(result, 6U, 0U);
    write_u16(result, 10U, 1U);
    write_u16(result, 12U, 1U);
    write_u16(result, 14U, 2U);
    write_u32(result, 16U, 14U);
    write_u32(result, 20U, static_cast<std::uint32_t>(document.size()));
    std::copy(document.begin(), document.end(), result.begin() + 24);
    return result;
}

struct cbdt_tables final {
    std::vector<std::byte> cblc{};
    std::vector<std::byte> cbdt{};
};

cbdt_tables make_cbdt_tables(
    std::uint16_t index_format,
    std::uint16_t image_format = 0U) {
    const auto metrics_in_index = index_format == 2U || index_format == 5U;
    if (image_format == 0U) {
        image_format = metrics_in_index ? 19U : 17U;
    }
    const std::array image{
        std::byte{0x89U}, std::byte{0x50U}, std::byte{0x4EU}};
    const auto metrics_size = image_format == 17U
        ? 5U
        : image_format == 18U ? 8U : 0U;
    std::vector<std::byte> cbdt(4U + metrics_size + 4U + image.size());
    write_u16(cbdt, 0U, 3U);
    write_u16(cbdt, 2U, 0U);
    if (metrics_size != 0U) {
        cbdt[4U] = std::byte{1U};
        cbdt[5U] = std::byte{1U};
        cbdt[6U] = std::byte{3U};
        cbdt[7U] = std::byte{4U};
        cbdt[8U] = std::byte{5U};
        if (metrics_size == 8U) {
            cbdt[9U] = std::byte{0U};
            cbdt[10U] = std::byte{0U};
            cbdt[11U] = std::byte{5U};
        }
    }
    const auto length_offset = 4U + metrics_size;
    write_u32(cbdt, length_offset,
        static_cast<std::uint32_t>(image.size()));
    std::copy(
        image.begin(), image.end(),
        cbdt.begin() + static_cast<std::ptrdiff_t>(length_offset + 4U));

    const auto bitmap_data_length =
        static_cast<std::uint32_t>(cbdt.size() - 4U);
    std::size_t subtable_size = 0U;
    switch (index_format) {
    case 1U:
        subtable_size = 16U;
        break;
    case 2U:
        subtable_size = 20U;
        break;
    case 3U:
        subtable_size = 12U;
        break;
    case 4U:
        subtable_size = 20U;
        break;
    case 5U:
        subtable_size = 28U;
        break;
    default:
        std::abort();
    }
    std::vector<std::byte> subtable(subtable_size);
    write_u16(subtable, 0U, index_format);
    write_u16(subtable, 2U, image_format);
    write_u32(subtable, 4U, 4U);
    switch (index_format) {
    case 1U:
        write_u32(subtable, 8U, 0U);
        write_u32(subtable, 12U, bitmap_data_length);
        break;
    case 2U:
        write_u32(subtable, 8U, bitmap_data_length);
        break;
    case 3U:
        write_u16(subtable, 8U, 0U);
        write_u16(subtable, 10U,
            static_cast<std::uint16_t>(bitmap_data_length));
        break;
    case 4U:
        write_u32(subtable, 8U, 1U);
        write_u16(subtable, 12U, 1U);
        write_u16(subtable, 14U, 0U);
        write_u16(subtable, 16U, 0xFFFFU);
        write_u16(subtable, 18U,
            static_cast<std::uint16_t>(bitmap_data_length));
        break;
    case 5U:
        write_u32(subtable, 8U, bitmap_data_length);
        write_u32(subtable, 20U, 1U);
        write_u16(subtable, 24U, 1U);
        break;
    default:
        std::abort();
    }
    if (metrics_in_index) {
        subtable[12U] = std::byte{1U};
        subtable[13U] = std::byte{1U};
        subtable[14U] = std::byte{3U};
        subtable[15U] = std::byte{4U};
        subtable[16U] = std::byte{5U};
        subtable[19U] = std::byte{5U};
    }

    std::vector<std::byte> cblc(64U + subtable.size());
    write_u16(cblc, 0U, 3U);
    write_u16(cblc, 2U, 0U);
    write_u32(cblc, 4U, 1U);
    write_u32(cblc, 8U, 56U);
    write_u32(cblc, 12U,
        static_cast<std::uint32_t>(8U + subtable.size()));
    write_u32(cblc, 16U, 1U);
    write_u16(cblc, 48U, 1U);
    write_u16(cblc, 50U, 1U);
    cblc[52U] = std::byte{20U};
    cblc[53U] = std::byte{20U};
    cblc[54U] = std::byte{32U};
    cblc[55U] = std::byte{1U};
    write_u16(cblc, 56U, 1U);
    write_u16(cblc, 58U, 1U);
    write_u32(cblc, 60U, 8U);
    std::copy(subtable.begin(), subtable.end(), cblc.begin() + 64);
    return {std::move(cblc), std::move(cbdt)};
}

std::vector<std::byte> make_colr() {
    std::vector<std::byte> result(32U);
    write_u16(result, 0U, 0U);
    write_u16(result, 2U, 1U);
    write_u32(result, 4U, 14U);
    write_u32(result, 8U, 20U);
    write_u16(result, 12U, 3U);
    write_u16(result, 14U, 1U);
    write_u16(result, 16U, 0U);
    write_u16(result, 18U, 3U);
    write_u16(result, 20U, 2U);
    write_u16(result, 22U, 0U);
    write_u16(result, 24U, 3U);
    write_u16(result, 26U, 1U);
    write_u16(result, 28U, 4U);
    write_u16(result, 30U, 0xFFFFU);
    return result;
}

std::vector<std::byte> make_cpal() {
    std::vector<std::byte> result(32U);
    write_u16(result, 0U, 0U);
    write_u16(result, 2U, 2U);
    write_u16(result, 4U, 2U);
    write_u16(result, 6U, 4U);
    write_u32(result, 8U, 16U);
    write_u16(result, 12U, 0U);
    write_u16(result, 14U, 2U);
    result[16U] = std::byte{0U};
    result[17U] = std::byte{0U};
    result[18U] = std::byte{255U};
    result[19U] = std::byte{255U};
    result[20U] = std::byte{255U};
    result[21U] = std::byte{0U};
    result[22U] = std::byte{0U};
    result[23U] = std::byte{255U};
    result[24U] = std::byte{0U};
    result[25U] = std::byte{255U};
    result[26U] = std::byte{0U};
    result[27U] = std::byte{255U};
    result[28U] = std::byte{255U};
    result[29U] = std::byte{255U};
    result[30U] = std::byte{255U};
    result[31U] = std::byte{128U};
    return result;
}

std::vector<std::byte> make_cff2_table() {
    constexpr std::size_t top_size = 13U;
    constexpr std::size_t char_strings_offset = 22U;
    constexpr std::size_t font_dictionaries_offset = 116U;
    constexpr std::array<std::byte, 10U> char_string{
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0x8B}, std::byte{0x8B},
        std::byte{0xEF}, std::byte{0x27}, std::byte{0x8B},
        std::byte{0x05}};
    std::vector<std::byte> result(126U);
    result[0U] = std::byte{2U};
    result[1U] = std::byte{0U};
    result[2U] = std::byte{5U};
    write_u16(result, 3U, static_cast<std::uint16_t>(top_size));
    result[5U] = std::byte{29U};
    write_u32(result, 6U, static_cast<std::uint32_t>(char_strings_offset));
    result[10U] = std::byte{17U};
    result[11U] = std::byte{29U};
    write_u32(
        result, 12U, static_cast<std::uint32_t>(font_dictionaries_offset));
    result[16U] = std::byte{12U};
    result[17U] = std::byte{36U};
    write_u32(result, 18U, 0U);

    write_u32(result, char_strings_offset, 8U);
    result[char_strings_offset + 4U] = std::byte{1U};
    for (std::size_t index = 0U; index <= 8U; ++index) {
        result[char_strings_offset + 5U + index] =
            static_cast<std::byte>(1U + index * char_string.size());
    }
    auto char_cursor = char_strings_offset + 14U;
    for (std::size_t glyph = 0U; glyph < 8U; ++glyph) {
        std::copy(
            char_string.begin(), char_string.end(),
            result.begin() + static_cast<std::ptrdiff_t>(char_cursor));
        char_cursor += char_string.size();
    }

    write_u32(result, font_dictionaries_offset, 1U);
    result[font_dictionaries_offset + 4U] = std::byte{1U};
    result[font_dictionaries_offset + 5U] = std::byte{1U};
    result[font_dictionaries_offset + 6U] = std::byte{4U};
    result[font_dictionaries_offset + 7U] = std::byte{0x8BU};
    result[font_dictionaries_offset + 8U] = std::byte{0x8BU};
    result[font_dictionaries_offset + 9U] = std::byte{18U};
    return result;
}

std::vector<std::byte> make_font(
    std::size_t face_offset = 0U,
    std::size_t glyph_size = 22U,
    std::size_t second_glyph_size = 0U,
    bool include_variations = false,
    bool include_axis_mapping = false,
    bool include_glyph_variations = false,
    std::span<const table_data> extra_tables = {}) {
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
    for (const auto& table : extra_tables) {
        tables.push_back(table);
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
        4U, requirements, &error));
    require(error == font_error::none);
    require(requirements.tuple_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 5U> short_coordinates{};
    std::uint16_t headers_written = 99U;
    std::uint32_t coordinates_written = 99U;
    require(!font.try_decode_glyph_variation_tuple_headers(
        4U,
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
        4U,
        headers,
        coordinates,
        headers_written,
        coordinates_written,
        &error));
    require(headers_written == 1U && coordinates_written == 6U);
    require(headers[0].serialized_data_size == 10U);
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

void untouched_glyph_deltas_interpolate_without_allocation() {
    const std::array<progpu_native_point, 5U> points{{
        {0.0F, 0.0F},
        {10.0F, 10.0F},
        {20.0F, 20.0F},
        {30.0F, 30.0F},
        {40.0F, 40.0F}}};
    const std::array<std::uint16_t, 1U> contour_ends{4U};
    std::array<float, 5U> x{0.0F, 2.0F, 0.0F, 6.0F, 0.0F};
    std::array<float, 5U> y{0.0F, 10.0F, 0.0F, 20.0F, 0.0F};
    const std::array<std::uint8_t, 5U> touched{0U, 1U, 0U, 1U, 0U};
    font_error error = font_error::none;
    require(sfnt_gvar_deltas::try_infer_untouched(
        points, contour_ends, x, y, touched, &error));
    require(error == font_error::none);
    require(x == std::array<float, 5U>{2.0F, 2.0F, 4.0F, 6.0F, 6.0F});
    require(y ==
        std::array<float, 5U>{10.0F, 10.0F, 15.0F, 20.0F, 20.0F});

    x = {0.0F, 0.0F, 7.0F, 0.0F, 0.0F};
    y = {0.0F, 0.0F, -3.0F, 0.0F, 0.0F};
    const std::array<std::uint8_t, 5U> one_touched{0U, 0U, 1U, 0U, 0U};
    require(sfnt_gvar_deltas::try_infer_untouched(
        points, contour_ends, x, y, one_touched, &error));
    require(x == std::array<float, 5U>{7.0F, 7.0F, 7.0F, 7.0F, 7.0F});
    require(y ==
        std::array<float, 5U>{-3.0F, -3.0F, -3.0F, -3.0F, -3.0F});

    const std::array<std::uint16_t, 1U> invalid_contour{5U};
    const auto original_x = x;
    require(!sfnt_gvar_deltas::try_infer_untouched(
        points, invalid_contour, x, y, one_touched, &error));
    require(error == font_error::invalid_glyph && x == original_x);
    std::array<float, 4U> short_x{};
    require(!sfnt_gvar_deltas::try_infer_untouched(
        points, contour_ends, short_x, y, one_touched, &error));
    require(error == font_error::insufficient_buffer);
}

void simple_glyph_variations_apply_packed_tuple_deltas() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_glyph_decode_requirements glyph_requirements{};
    require(font.try_get_glyph_decode_requirements(
        4U, glyph_requirements));
    require(glyph_requirements.point_count == 3U);
    std::array<std::uint16_t, 1U> contour_ends{};
    std::array<sfnt_outline_point, 3U> original_points{};
    require(font.try_decode_simple_glyph(
        4U, contour_ends, original_points));

    sfnt_simple_glyph_variation_requirements requirements{};
    require(font.try_get_simple_glyph_variation_requirements(
        4U, 3U, requirements));
    require(requirements.tuple_header_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    require(requirements.point_number_count == 7U);
    require(requirements.delta_count == 7U);
    require(requirements.tuple_point_count == 3U);

    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 6U> regions{};
    std::array<std::uint32_t, 7U> shared_points{};
    std::array<std::uint32_t, 7U> private_points{};
    std::array<std::int16_t, 7U> x_deltas{};
    std::array<std::int16_t, 7U> y_deltas{};
    std::array<float, 3U> tuple_x{};
    std::array<float, 3U> tuple_y{};
    std::array<std::uint8_t, 3U> touched{};
    sfnt_simple_glyph_variation_scratch scratch{
        headers,
        regions,
        shared_points,
        private_points,
        x_deltas,
        y_deltas,
        tuple_x,
        tuple_y,
        touched};
    const std::array<std::int16_t, 2U> normalized{4096, -4096};
    std::array<progpu_native_point, 3U> varied{};
    font_error error = font_error::none;
    require(font.try_apply_simple_glyph_variations(
        4U,
        normalized,
        contour_ends,
        original_points,
        varied,
        scratch,
        &error));
    require(error == font_error::none);
    require(varied[0].x == 11.0F && varied[0].y == 0.0F);
    require(varied[1].x == 30.0F && varied[1].y == 30.0F);
    require(varied[2].x == 25.0F && varied[2].y == 40.0F);

    std::array<progpu_native_point, 2U> short_varied{{
        {99.0F, 99.0F}, {99.0F, 99.0F}}};
    require(!font.try_apply_simple_glyph_variations(
        4U,
        normalized,
        contour_ends,
        original_points,
        short_varied,
        scratch,
        &error));
    require(error == font_error::insufficient_buffer);
    require(short_varied[0].x == 99.0F && short_varied[0].y == 99.0F);
}

void composite_glyph_variations_apply_component_offsets() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_composite_glyph_variation_requirements requirements{};
    require(font.try_get_composite_glyph_variation_requirements(
        4U, 3U, requirements));
    require(requirements.tuple_header_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    require(requirements.point_number_count == 7U);
    require(requirements.delta_count == 7U);

    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 6U> regions{};
    std::array<std::uint32_t, 7U> shared_points{};
    std::array<std::uint32_t, 7U> private_points{};
    std::array<std::int16_t, 7U> x_deltas{};
    std::array<std::int16_t, 7U> y_deltas{};
    sfnt_composite_glyph_variation_scratch scratch{
        headers,
        regions,
        shared_points,
        private_points,
        x_deltas,
        y_deltas};
    const std::array<std::int16_t, 2U> normalized{4096, -4096};
    std::array<progpu_native_point, 3U> offsets{{
        {99.0F, 99.0F}, {99.0F, 99.0F}, {99.0F, 99.0F}}};
    font_error error = font_error::none;
    require(font.try_get_composite_glyph_variation_offsets(
        4U,
        normalized,
        3U,
        offsets,
        scratch,
        &error));
    require(error == font_error::none);
    require(offsets[0].x == 1.0F && offsets[0].y == 0.0F);
    require(offsets[1].x == 0.0F && offsets[1].y == 0.0F);
    require(offsets[2].x == 0.0F && offsets[2].y == 0.0F);

    std::array<progpu_native_point, 2U> short_offsets{{
        {99.0F, 99.0F}, {99.0F, 99.0F}}};
    require(!font.try_get_composite_glyph_variation_offsets(
        4U,
        normalized,
        3U,
        short_offsets,
        scratch,
        &error));
    require(error == font_error::insufficient_buffer);
    require(short_offsets[0].x == 99.0F && short_offsets[0].y == 99.0F);
}

void phantom_glyph_variations_apply_advance_delta() {
    const auto data = make_font(0U, 22U, 0U, true, true, true);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_glyph_phantom_variation_requirements requirements{};
    require(font.try_get_glyph_phantom_variation_requirements(
        4U, 7U, requirements));
    require(requirements.tuple_header_count == 1U);
    require(requirements.region_coordinate_count == 6U);
    require(requirements.point_number_count == 7U);
    require(requirements.delta_count == 7U);

    std::array<sfnt_gvar_tuple_header, 1U> headers{};
    std::array<std::int16_t, 6U> regions{};
    std::array<std::uint32_t, 7U> shared_points{};
    std::array<std::uint32_t, 7U> private_points{};
    std::array<std::int16_t, 7U> x_deltas{};
    std::array<std::int16_t, 7U> y_deltas{};
    sfnt_glyph_phantom_variation_scratch scratch{
        headers,
        regions,
        shared_points,
        private_points,
        x_deltas,
        y_deltas};
    const std::array<std::int16_t, 2U> normalized{4096, -4096};
    float delta = 99.0F;
    font_error error = font_error::none;
    require(font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 7U, delta, scratch, &error));
    require(error == font_error::none && delta == 3.0F);

    delta = 99.0F;
    require(font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 3U, delta, {}, &error));
    require(error == font_error::none && delta == 0.0F);

    auto short_scratch = scratch;
    short_scratch.x_deltas = std::span<std::int16_t>{x_deltas}.first(6U);
    delta = 99.0F;
    require(!font.try_get_glyph_phantom_advance_delta(
        4U, normalized, 7U, delta, short_scratch, &error));
    require(error == font_error::insufficient_buffer && delta == 0.0F);

    bool uses_hvar = true;
    delta = 99.0F;
    require(font.try_get_horizontal_advance_variation(
        4U, normalized, delta, uses_hvar, &error));
    require(error == font_error::none && !uses_hvar && delta == 0.0F);
}

void item_variation_store_and_index_map_are_bounded() {
    std::vector<std::byte> data(46U);
    write_u16(data, 0U, 1U);
    write_u32(data, 2U, 12U);
    write_u16(data, 6U, 1U);
    write_u32(data, 8U, 28U);
    write_u16(data, 12U, 1U);
    write_u16(data, 14U, 1U);
    write_i16(data, 16U, 0);
    write_i16(data, 18U, 8192);
    write_i16(data, 20U, 16384);
    write_u16(data, 28U, 2U);
    write_u16(data, 30U, 1U);
    write_u16(data, 32U, 1U);
    write_u16(data, 34U, 0U);
    write_i16(data, 36U, 20);
    write_i16(data, 38U, -10);
    data[40U] = std::byte{0U};
    data[41U] = std::byte{0U};
    write_u16(data, 42U, 2U);
    data[44U] = std::byte{0U};
    data[45U] = std::byte{1U};

    sfnt_item_variation_store_view store{};
    font_error error = font_error::none;
    require(sfnt_item_variation_data::try_get_store(
        data, 0U, 1U, store, &error));
    require(error == font_error::none);
    const std::array<std::int16_t, 1U> normalized{8192};
    float delta = 99.0F;
    require(sfnt_item_variation_data::try_get_delta(
        store, normalized, 0U, 0U, delta, &error));
    require(delta == 20.0F);
    require(sfnt_item_variation_data::try_get_delta(
        store, normalized, 0U, 1U, delta, &error));
    require(delta == -10.0F);

    sfnt_delta_set_index_map_view map{};
    require(sfnt_item_variation_data::try_get_delta_set_index_map(
        data, 40U, map, &error));
    std::uint16_t outer = 99U;
    std::uint16_t inner = 99U;
    sfnt_item_variation_data::get_delta_set_index(
        map, 1U, outer, inner);
    require(outer == 0U && inner == 1U);
    sfnt_item_variation_data::get_delta_set_index(
        map, 99U, outer, inner);
    require(outer == 0U && inner == 1U);

    auto truncated = data;
    truncated.resize(38U);
    require(!sfnt_item_variation_data::try_get_store(
        truncated, 0U, 1U, store, &error));
    require(error == font_error::invalid_face);
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

void cff1_indexes_and_dictionaries_are_borrowed_and_bounded() {
    const std::array<std::byte, 9U> index_bytes{
        std::byte{0x00}, std::byte{0x02}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x03}, std::byte{0x04},
        std::byte{0xAA}, std::byte{0xBB}, std::byte{0xCC}};
    std::size_t cursor = 0U;
    sfnt_cff_index_view index{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_index(
        index_bytes, cursor, index, &error));
    require(error == font_error::none);
    require(index.count == 2U && index.offset_size == 1U);
    require(cursor == index_bytes.size());
    std::span<const std::byte> item{};
    require(sfnt_cff_data::try_get_index_item(index, 0U, item, &error));
    require(item.size() == 2U && item[0] == std::byte{0xAA} &&
        item[1] == std::byte{0xBB});
    require(sfnt_cff_data::try_get_index_item(index, 1U, item, &error));
    require(item.size() == 1U && item[0] == std::byte{0xCC});
    require(!sfnt_cff_data::try_get_index_item(index, 2U, item, &error));
    require(error == font_error::invalid_argument && item.empty());

    const std::array<std::byte, 2U> empty_index{
        std::byte{0x00}, std::byte{0x00}};
    cursor = 0U;
    require(sfnt_cff_data::try_read_index(
        empty_index, cursor, index, &error));
    require(index.count == 0U && cursor == empty_index.size());

    const std::array<std::byte, 6U> descending_offsets{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x02}, std::byte{0x01}, std::byte{0xAA}};
    cursor = 0U;
    require(!sfnt_cff_data::try_read_index(
        descending_offsets, cursor, index, &error));
    require(error == font_error::invalid_face && index.count == 0U);

    const std::array<std::byte, 6U> truncated_data{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x03}, std::byte{0xAA}};
    cursor = 0U;
    require(!sfnt_cff_data::try_read_index(
        truncated_data, cursor, index, &error));
    require(error == font_error::invalid_face && index.count == 0U);

    const std::array<std::byte, 3U> real_number{
        std::byte{0x1E}, std::byte{0x1A}, std::byte{0x5F}};
    cursor = 1U;
    double decoded = 0.0;
    require(sfnt_cff_data::try_read_dictionary_number(
        real_number, cursor, 30U, decoded));
    require(decoded == 1.5 && cursor == real_number.size());

    const std::array<std::byte, 5U> exponent_number{
        std::byte{0x1E}, std::byte{0xE1}, std::byte{0xA2},
        std::byte{0x5C}, std::byte{0x2F}};
    cursor = 1U;
    require(sfnt_cff_data::try_read_dictionary_number(
        exponent_number, cursor, 30U, decoded));
    require(decoded < -0.01249 && decoded > -0.01251 &&
        cursor == exponent_number.size());

    const std::array<std::byte, 2U> reserved_real_nibble{
        std::byte{0x1E}, std::byte{0x1D}};
    cursor = 1U;
    require(!sfnt_cff_data::try_read_dictionary_number(
        reserved_real_nibble, cursor, 30U, decoded));

    const std::array<std::byte, 16U> dictionary{
        std::byte{0xF7}, std::byte{0xC0}, std::byte{0x11},
        std::byte{0x95}, std::byte{0xF8}, std::byte{0x24}, std::byte{0x12},
        std::byte{0xF8}, std::byte{0x88}, std::byte{0x0C}, std::byte{0x24},
        std::byte{0xF8}, std::byte{0xEC}, std::byte{0x0C}, std::byte{0x25},
        std::byte{0x00}};
    sfnt_cff1_top_dictionary top{};
    require(sfnt_cff_data::try_get_top_dictionary(
        dictionary, top, &error));
    require(error == font_error::none);
    require(top.char_strings_offset == 300U);
    require(top.private_size == 10U && top.private_offset == 400U);
    require(top.font_dictionary_offset == 500U);
    require(top.fd_select_offset == 600U);

    const std::array<std::byte, 8U> private_and_subroutines{
        std::byte{0x8D}, std::byte{0x13},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x02}, std::byte{0x0B}};
    sfnt_cff_index_view local_subroutines{};
    require(sfnt_cff_data::try_read_local_subroutines(
        private_and_subroutines,
        0U,
        2U,
        local_subroutines,
        &error));
    require(local_subroutines.count == 1U);
    require(sfnt_cff_data::try_get_index_item(
        local_subroutines, 0U, item, &error));
    require(item.size() == 1U && item[0] == std::byte{0x0B});
    require(!sfnt_cff_data::try_read_local_subroutines(
        private_and_subroutines,
        7U,
        2U,
        local_subroutines,
        &error));
    require(error == font_error::invalid_face &&
        local_subroutines.count == 0U);
}

void cff1_fd_select_formats_are_borrowed_and_searchable() {
    const std::array<std::byte, 5U> format_zero{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x00}};
    sfnt_cff_fd_select_view view{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_fd_select(
        format_zero, 0U, 4U, 2U, view, &error));
    require(view.format == 0U && view.range_count == 4U);
    std::uint32_t dictionary = 0U;
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 0U, dictionary, &error));
    require(dictionary == 0U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 2U, dictionary, &error));
    require(dictionary == 1U);

    const std::array<std::byte, 11U> format_three{
        std::byte{0x03}, std::byte{0x00}, std::byte{0x02},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x02}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x04}};
    require(sfnt_cff_data::try_read_fd_select(
        format_three, 0U, 4U, 2U, view, &error));
    require(view.format == 3U && view.range_count == 2U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 1U, dictionary, &error));
    require(dictionary == 0U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 3U, dictionary, &error));
    require(dictionary == 1U);

    const std::array<std::byte, 21U> format_four{
        std::byte{0x04},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x02},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x02},
        std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x04}};
    require(sfnt_cff_data::try_read_fd_select(
        format_four, 0U, 4U, 2U, view, &error));
    require(view.format == 4U && view.range_count == 2U);
    require(sfnt_cff_data::try_get_font_dictionary(
        view, 2U, dictionary, &error));
    require(dictionary == 1U);
    require(!sfnt_cff_data::try_get_font_dictionary(
        view, 4U, dictionary, &error));
    require(error == font_error::invalid_argument);

    auto invalid = format_three;
    invalid[8] = std::byte{0x02};
    require(!sfnt_cff_data::try_read_fd_select(
        invalid, 0U, 4U, 2U, view, &error));
    require(error == font_error::invalid_face && view.bytes.empty());
}

void cff1_type2_outline_is_transactional_and_closes_figures() {
    const std::array<std::byte, 16U> encoded{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x0C},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0x8B}, std::byte{0x8B},
        std::byte{0xEF}, std::byte{0x27}, std::byte{0x8B},
        std::byte{0x05}, std::byte{0x0E}};
    std::size_t cursor = 0U;
    sfnt_cff_index_view char_strings{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_index(
        encoded, cursor, char_strings, &error));
    sfnt_cff1_font_view font{};
    font.bytes = encoded;
    font.char_strings = char_strings;

    sfnt_cff1_outline_requirements requirements{};
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 0U, requirements, &error));
    require(error == font_error::none &&
        requirements.path_segment_count == 4U);
    std::array<progpu_native_path_segment, 4U> segments{};
    std::uint32_t written = 0U;
    require(sfnt_cff_data::try_decode_outline(
        font, 0U, segments, written, &error));
    require(error == font_error::none && written == segments.size());
    require(segments[0].p0.x == 0.0F && segments[0].p0.y == 0.0F);
    require(segments[0].p1.x == 100.0F && segments[0].p1.y == 0.0F);
    require(segments[2].p1.x == 0.0F && segments[2].p1.y == 100.0F);
    require(segments[3].p1.x == 0.0F && segments[3].p1.y == 0.0F);

    std::array<progpu_native_path_segment, 3U> short_segments{};
    short_segments[0].kind = 99U;
    written = 99U;
    require(!sfnt_cff_data::try_decode_outline(
        font, 0U, short_segments, written, &error));
    require(error == font_error::insufficient_buffer && written == 0U);
    require(short_segments[0].kind == 99U);
}

void cff2_indexes_blends_and_outlines_are_borrowed_and_bounded() {
    const std::array<std::byte, 17U> static_char_strings{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x0B},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0x8B}, std::byte{0x8B},
        std::byte{0xEF}, std::byte{0x27}, std::byte{0x8B},
        std::byte{0x05}};
    const std::array<std::byte, 10U> font_dictionaries{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x04},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x12}};
    std::size_t cursor = 0U;
    sfnt_cff_index_view char_strings{};
    sfnt_cff_index_view dictionaries{};
    font_error error = font_error::none;
    require(sfnt_cff_data::try_read_cff2_index(
        static_char_strings, cursor, char_strings, &error));
    require(cursor == static_char_strings.size() &&
        char_strings.count == 1U);
    cursor = 0U;
    require(sfnt_cff_data::try_read_cff2_index(
        font_dictionaries, cursor, dictionaries, &error));

    const std::array<std::byte, 8U> top_dictionary{
        std::byte{0xBD}, std::byte{0x11},
        std::byte{0xD1}, std::byte{0x0C}, std::byte{0x24},
        std::byte{0xE5}, std::byte{0x18}, std::byte{0x00}};
    sfnt_cff2_top_dictionary top{};
    require(!sfnt_cff_data::try_get_cff2_top_dictionary(
        top_dictionary, top, &error));
    const auto valid_top =
        std::span<const std::byte>{top_dictionary}.first(7U);
    require(sfnt_cff_data::try_get_cff2_top_dictionary(
        valid_top, top, &error));
    require(top.char_strings_offset == 50U &&
        top.font_dictionary_offset == 70U &&
        top.variation_store_offset == 90U &&
        !top.has_font_matrix);

    sfnt_cff2_font_view font{};
    font.char_strings = char_strings;
    font.font_dictionaries = dictionaries;
    sfnt_cff2_outline_requirements requirements{};
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 0U, {}, requirements, &error));
    require(error == font_error::none &&
        requirements.path_segment_count == 4U);
    std::array<progpu_native_path_segment, 4U> static_segments{};
    std::uint32_t written = 0U;
    require(sfnt_cff_data::try_decode_outline(
        font, 0U, {}, static_segments, written, &error));
    require(written == static_segments.size());
    require(static_segments[0U].p1.x == 100.0F &&
        static_segments[1U].p1.y == 100.0F &&
        static_segments[3U].p1.x == 0.0F &&
        static_segments[3U].p1.y == 0.0F);

    std::vector<std::byte> variation_bytes(30U);
    write_u16(variation_bytes, 0U, 1U);
    write_u32(variation_bytes, 2U, 12U);
    write_u16(variation_bytes, 6U, 1U);
    write_u32(variation_bytes, 8U, 22U);
    write_u16(variation_bytes, 12U, 1U);
    write_u16(variation_bytes, 14U, 1U);
    write_i16(variation_bytes, 16U, 0);
    write_i16(variation_bytes, 18U, 8192);
    write_i16(variation_bytes, 20U, 16384);
    write_u16(variation_bytes, 22U, 0U);
    write_u16(variation_bytes, 24U, 0U);
    write_u16(variation_bytes, 26U, 1U);
    write_u16(variation_bytes, 28U, 0U);
    sfnt_item_variation_store_view store{};
    require(sfnt_item_variation_data::try_get_store(
        variation_bytes, 0U, 1U, store, &error));
    std::uint16_t scalar_count = 0U;
    require(sfnt_item_variation_data::try_get_region_scalar_count(
        store, 0U, scalar_count, &error));
    require(scalar_count == 1U);

    const std::array<std::byte, 19U> varied_char_strings{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x0C},
        std::byte{0x8B}, std::byte{0x0F},
        std::byte{0x8B}, std::byte{0x8B}, std::byte{0x15},
        std::byte{0xEF}, std::byte{0xB3}, std::byte{0x8C},
        std::byte{0x10}, std::byte{0x8B}, std::byte{0x05},
        std::byte{0x00}};
    cursor = 0U;
    require(sfnt_cff_data::try_read_cff2_index(
        std::span<const std::byte>{varied_char_strings}.first(18U),
        cursor,
        char_strings,
        &error));
    font.char_strings = char_strings;
    font.variation_store = store;
    font.axis_count = 1U;
    const std::array<std::int16_t, 1U> default_coordinates{0};
    const std::array<std::int16_t, 1U> peak_coordinates{8192};
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 0U, default_coordinates, requirements, &error));
    require(requirements.path_segment_count == 2U);
    std::array<progpu_native_path_segment, 2U> default_segments{};
    std::array<progpu_native_path_segment, 2U> peak_segments{};
    require(sfnt_cff_data::try_decode_outline(
        font,
        0U,
        default_coordinates,
        default_segments,
        written,
        &error));
    require(default_segments[0U].p1.x == 100.0F);
    require(sfnt_cff_data::try_decode_outline(
        font,
        0U,
        peak_coordinates,
        peak_segments,
        written,
        &error));
    require(peak_segments[0U].p1.x == 140.0F);

    auto forbidden_endchar = varied_char_strings;
    forbidden_endchar[17U] = std::byte{0x0E};
    forbidden_endchar[6U] = std::byte{0x0C};
    cursor = 0U;
    require(sfnt_cff_data::try_read_cff2_index(
        std::span<const std::byte>{forbidden_endchar}.first(18U),
        cursor,
        char_strings,
        &error));
    font.char_strings = char_strings;
    require(!sfnt_cff_data::try_get_outline_requirements(
        font, 0U, peak_coordinates, requirements, &error));
    require(error == font_error::invalid_glyph);

    const std::array<table_data, 1U> extra{
        table_data{
            open_type_tag::from_chars('C', 'F', 'F', '2'),
            make_cff2_table()}};
    const auto sfnt = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view face{};
    require(sfnt_font_view::try_create(sfnt, 0U, face, &error));
    require(face.try_get_cff2_font(8U, font, &error));
    require(error == font_error::none && font.char_strings.count == 8U &&
        font.font_dictionaries.count == 1U && font.axis_count == 0U);
    require(sfnt_cff_data::try_get_outline_requirements(
        font, 7U, {}, requirements, &error));
    require(requirements.path_segment_count == 4U);
    sfnt_cff2_font_view mismatch{};
    require(!face.try_get_cff2_font(7U, mismatch, &error));
    require(error == font_error::invalid_face && mismatch.bytes.empty());
}

void sbix_strikes_and_duplicates_remain_borrowed() {
    const std::array<table_data, 1U> extra{
        table_data{
            open_type_tag::from_chars('s', 'b', 'i', 'x'), make_sbix()}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_bitmap_glyph_data_view glyph{};
    font_error error = font_error::none;
    require(font.try_get_sbix_glyph(1U, 35.0F, glyph, &error));
    require(error == font_error::none);
    require(glyph.pixels_per_em == 40U && glyph.pixels_per_inch == 72U);
    require(glyph.origin_offset_x == -4 && glyph.origin_offset_y == 12);
    require(glyph.graphic_type ==
        open_type_tag::from_chars('p', 'n', 'g', ' '));
    require(glyph.bytes.size() == 3U &&
        glyph.bytes[0U] == std::byte{40U} &&
        glyph.bytes[1U] == std::byte{41U} &&
        glyph.bytes[2U] == std::byte{42U});

    require(font.try_get_sbix_glyph(2U, 19.0F, glyph, &error));
    require(glyph.pixels_per_em == 20U);
    require(glyph.origin_offset_x == 7 && glyph.origin_offset_y == 8);
    require(glyph.bytes.size() == 3U &&
        glyph.bytes[0U] == std::byte{20U});

    require(font.try_get_sbix_glyph(1U, 30.0F, glyph, &error));
    require(glyph.pixels_per_em == 40U);

    require(!font.try_get_sbix_glyph(8U, 20.0F, glyph, &error));
    require(error == font_error::invalid_argument && glyph.bytes.empty());
}

void svg_glyph_documents_remain_borrowed_and_bounded() {
    for (const auto gzip : {false, true}) {
        const std::array<table_data, 1U> extra{
            table_data{
                open_type_tag::from_chars('S', 'V', 'G', ' '),
                make_svg_glyph_table(gzip)}};
        const auto data = make_font(
            0U, 22U, 0U, false, false, false, extra);
        sfnt_font_view font{};
        require(sfnt_font_view::try_create(data, 0U, font));
        sfnt_svg_glyph_document_view document{};
        font_error error = font_error::none;
        require(font.try_get_svg_glyph_document(1U, document, &error));
        require(error == font_error::none &&
            document.bytes.size() == (gzip ? 26U : 6U));
        require(document.first_glyph == 1U && document.last_glyph == 2U);
        require(document.gzip_compressed == gzip);
        std::size_t document_size = 0U;
        require(try_get_svg_glyph_document_size(
            document, document_size, &error));
        require(document_size == 6U && error == font_error::none);
        std::array<std::byte, 6U> decoded{};
        std::size_t written = 0U;
        require(try_decode_svg_glyph_document(
            document, decoded, written, &error));
        require(written == decoded.size() &&
            decoded == std::array{
                std::byte{0x3CU}, std::byte{0x73U}, std::byte{0x76U},
                std::byte{0x67U}, std::byte{0x2FU}, std::byte{0x3EU}});
        std::array<std::byte, 5U> short_output{};
        require(!try_decode_svg_glyph_document(
            document, short_output, written, &error));
        require(error == font_error::insufficient_buffer && written == 0U);
        require(!font.try_get_svg_glyph_document(3U, document, &error));
        require(error == font_error::invalid_glyph && document.bytes.empty());
    }
}

void cbdt_index_and_image_formats_remain_borrowed_and_bounded() {
    for (std::uint16_t index_format = 1U; index_format <= 5U;
        ++index_format) {
        auto tables = make_cbdt_tables(index_format);
        const std::array<table_data, 2U> extra{
            table_data{
                open_type_tag::from_chars('C', 'B', 'L', 'C'),
                std::move(tables.cblc)},
            table_data{
                open_type_tag::from_chars('C', 'B', 'D', 'T'),
                std::move(tables.cbdt)}};
        const auto data = make_font(
            0U, 22U, 0U, false, false, false, extra);
        sfnt_font_view font{};
        require(sfnt_font_view::try_create(data, 0U, font));
        sfnt_bitmap_glyph_data_view glyph{};
        font_error error = font_error::none;
        require(font.try_get_cbdt_glyph(1U, 30.0F, glyph, &error));
        require(error == font_error::none);
        require(glyph.pixels_per_em == 20U &&
            glyph.pixels_per_inch == 72U);
        require(glyph.uses_horizontal_metrics &&
            glyph.bearing_x == 3 && glyph.bearing_y == 4);
        require(glyph.origin_offset_x == 0 && glyph.origin_offset_y == 0);
        require(glyph.graphic_type ==
            open_type_tag::from_chars('p', 'n', 'g', ' '));
        require(glyph.bytes.size() == 3U &&
            glyph.bytes[0U] == std::byte{0x89U} &&
            glyph.bytes[1U] == std::byte{0x50U} &&
            glyph.bytes[2U] == std::byte{0x4EU});
    }

    auto format_18 = make_cbdt_tables(1U, 18U);
    const std::array<table_data, 2U> extra{
        table_data{
            open_type_tag::from_chars('C', 'B', 'L', 'C'),
            std::move(format_18.cblc)},
        table_data{
            open_type_tag::from_chars('C', 'B', 'D', 'T'),
            std::move(format_18.cbdt)}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    sfnt_bitmap_glyph_data_view glyph{};
    font_error error = font_error::none;
    require(font.try_get_cbdt_glyph(1U, 20.0F, glyph, &error));
    require(glyph.uses_horizontal_metrics && glyph.bearing_x == 3 &&
        glyph.bearing_y == 4 && glyph.bytes.size() == 3U);
    require(!font.try_get_cbdt_glyph(8U, 20.0F, glyph, &error));
    require(error == font_error::invalid_argument && glyph.bytes.empty());

    auto malformed = make_cbdt_tables(1U);
    write_u32(malformed.cblc, 68U, 0xFFFFFFF0U);
    const std::array<table_data, 2U> malformed_extra{
        table_data{
            open_type_tag::from_chars('C', 'B', 'L', 'C'),
            std::move(malformed.cblc)},
        table_data{
            open_type_tag::from_chars('C', 'B', 'D', 'T'),
            std::move(malformed.cbdt)}};
    const auto malformed_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_extra);
    sfnt_font_view malformed_font{};
    require(sfnt_font_view::try_create(
        malformed_data, 0U, malformed_font));
    require(!malformed_font.try_get_cbdt_glyph(
        1U, 20.0F, glyph, &error));
    require(error == font_error::invalid_glyph && glyph.bytes.empty());
}

void colr_layers_and_cpal_palettes_are_transactional() {
    const std::array<table_data, 2U> extra{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'), make_colr()},
        table_data{
            open_type_tag::from_chars('C', 'P', 'A', 'L'), make_cpal()}};
    const auto data = make_font(
        0U, 22U, 0U, false, false, false, extra);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    font_error error = font_error::none;
    std::uint16_t count = 0U;
    require(font.try_get_colr_layer_count(1U, count, &error));
    require(error == font_error::none && count == 3U);

    std::array<sfnt_color_glyph_layer, 3U> layers{};
    std::uint16_t written = 0U;
    require(font.try_decode_colr_layers(
        1U, 0U, layers, written, &error));
    require(written == 3U);
    require(layers[0U].glyph_index == 2U &&
        layers[0U].palette_entry_index == 0U &&
        layers[0U].color.red == 255U &&
        layers[0U].color.green == 0U &&
        layers[0U].color.blue == 0U &&
        layers[0U].color.alpha == 255U);
    require(layers[1U].glyph_index == 3U &&
        layers[1U].color.red == 0U &&
        layers[1U].color.blue == 255U);
    require(layers[2U].glyph_index == 4U &&
        layers[2U].uses_foreground_color &&
        layers[2U].color.red == 255U);

    require(font.try_decode_colr_layers(
        1U, 1U, layers, written, &error));
    require(layers[0U].color.red == 0U &&
        layers[0U].color.green == 255U &&
        layers[0U].color.blue == 0U);
    require(layers[1U].color.red == 255U &&
        layers[1U].color.green == 255U &&
        layers[1U].color.blue == 255U &&
        layers[1U].color.alpha == 128U);
    require(font.try_decode_colr_layers(
        1U, 9U, layers, written, &error));
    require(layers[0U].color.red == 255U &&
        layers[0U].color.green == 0U);

    std::array<sfnt_color_glyph_layer, 2U> short_layers{};
    short_layers[0U].glyph_index = 99U;
    written = 99U;
    require(!font.try_decode_colr_layers(
        1U, 0U, short_layers, written, &error));
    require(error == font_error::insufficient_buffer && written == 0U &&
        short_layers[0U].glyph_index == 99U);
    require(!font.try_get_colr_layer_count(7U, count, &error));
    require(error == font_error::invalid_glyph && count == 0U);

    const std::array<table_data, 1U> colr_only{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'), make_colr()}};
    const auto colr_only_data = make_font(
        0U, 22U, 0U, false, false, false, colr_only);
    sfnt_font_view colr_only_font{};
    require(sfnt_font_view::try_create(
        colr_only_data, 0U, colr_only_font));
    require(colr_only_font.try_decode_colr_layers(
        1U, 0U, layers, written, &error));
    require(written == 3U && layers[0U].color.red == 255U &&
        layers[0U].color.green == 255U &&
        layers[0U].color.blue == 255U &&
        !layers[0U].uses_foreground_color &&
        layers[2U].uses_foreground_color);

    auto malformed_cpal = make_cpal();
    write_u16(malformed_cpal, 12U, 4U);
    const std::array<table_data, 2U> malformed_palette_tables{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'), make_colr()},
        table_data{
            open_type_tag::from_chars('C', 'P', 'A', 'L'),
            std::move(malformed_cpal)}};
    const auto malformed_palette_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_palette_tables);
    sfnt_font_view malformed_palette_font{};
    require(sfnt_font_view::try_create(
        malformed_palette_data, 0U, malformed_palette_font));
    layers[0U].glyph_index = 99U;
    require(!malformed_palette_font.try_decode_colr_layers(
        1U, 0U, layers, written, &error));
    require(error == font_error::invalid_face && written == 0U &&
        layers[0U].glyph_index == 99U);

    auto malformed_colr = make_colr();
    write_u16(malformed_colr, 16U, 2U);
    const std::array<table_data, 1U> malformed_layer_tables{
        table_data{
            open_type_tag::from_chars('C', 'O', 'L', 'R'),
            std::move(malformed_colr)}};
    const auto malformed_layer_data = make_font(
        0U, 22U, 0U, false, false, false, malformed_layer_tables);
    sfnt_font_view malformed_layer_font{};
    require(sfnt_font_view::try_create(
        malformed_layer_data, 0U, malformed_layer_font));
    count = 99U;
    require(!malformed_layer_font.try_get_colr_layer_count(
        1U, count, &error));
    require(error == font_error::invalid_face && count == 0U);
}

void production_noto_cff1_container_matches_sfnt_glyph_count() {
    std::ifstream stream(PROGPU_NATIVE_TEST_NOTO_CFF_FONT, std::ios::binary);
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
    std::uint16_t glyph_count = 0U;
    require(font.try_get_glyph_count(glyph_count));
    sfnt_cff1_font_view cff{};
    font_error error = font_error::none;
    require(font.try_get_cff1_font(glyph_count, cff, &error));
    require(error == font_error::none);

    constexpr std::array<std::uint32_t, 2U> codepoints{0x41U, 0x65E5U};
    constexpr std::array<std::uint16_t, 2U> glyphs{34U, 20220U};
    constexpr std::array<std::uint32_t, 2U> segment_counts{14U, 16U};
    constexpr std::array<std::uint64_t, 2U> hashes{
        1714381338565491643ULL,
        5620540281806238275ULL};
    for (std::size_t checkpoint = 0U;
        checkpoint < codepoints.size();
        ++checkpoint) {
        const auto codepoint = codepoints[checkpoint];
        std::uint16_t glyph = 0U;
        require(font.try_get_glyph_index(codepoint, glyph));
        require(glyph == glyphs[checkpoint]);
        sfnt_cff1_outline_requirements requirements{};
        require(sfnt_cff_data::try_get_outline_requirements(
            cff, glyph, requirements, &error));
        require(requirements.path_segment_count == segment_counts[checkpoint]);
        std::vector<progpu_native_path_segment> segments(
            requirements.path_segment_count);
        std::uint32_t written = 0U;
        require(sfnt_cff_data::try_decode_outline(
            cff, glyph, segments, written, &error));
        require(written == segments.size());
        require(hash_complete_path_segments(segments) == hashes[checkpoint]);
    }
    require(cff.char_strings.count == glyph_count);
    require(!cff.bytes.empty() && cff.top_dictionary.char_strings_offset > 0U);
    require(cff.font_dictionaries.count > 0U &&
        !cff.fd_select.bytes.empty());
    std::span<const std::byte> notdef{};
    require(sfnt_cff_data::try_get_index_item(
        cff.char_strings, 0U, notdef, &error));
    require(error == font_error::none && !notdef.empty());
    std::uint32_t dictionary = 0U;
    require(sfnt_cff_data::try_get_font_dictionary(
        cff.fd_select, 0U, dictionary, &error));
    require(dictionary < cff.font_dictionaries.count);
    sfnt_cff_index_view local_subroutines{};
    require(sfnt_cff_data::try_get_local_subroutines(
        cff, 0U, local_subroutines, &error));
    require(error == font_error::none);

    sfnt_cff1_font_view mismatch{};
    require(!font.try_get_cff1_font(
        static_cast<std::uint16_t>(glyph_count - 1U), mismatch, &error));
    require(error == font_error::invalid_face && mismatch.bytes.empty());
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

    sfnt_glyph_decode_requirements outline_requirements{};
    require(font.try_get_glyph_decode_requirements(
        397U, outline_requirements));
    std::vector<std::uint16_t> contour_ends(
        outline_requirements.contour_count);
    std::vector<sfnt_outline_point> original_points(
        outline_requirements.point_count);
    require(font.try_decode_simple_glyph(
        397U, contour_ends, original_points));
    sfnt_simple_glyph_variation_requirements variation_requirements{};
    require(font.try_get_simple_glyph_variation_requirements(
        397U,
        outline_requirements.point_count,
        variation_requirements));
    std::vector<sfnt_gvar_tuple_header> varied_headers(
        variation_requirements.tuple_header_count);
    std::vector<std::int16_t> varied_regions(
        variation_requirements.region_coordinate_count);
    std::vector<std::uint32_t> shared_point_numbers(
        variation_requirements.point_number_count);
    std::vector<std::uint32_t> private_point_numbers(
        variation_requirements.point_number_count);
    std::vector<std::int16_t> varied_x(
        variation_requirements.delta_count);
    std::vector<std::int16_t> varied_y(
        variation_requirements.delta_count);
    std::vector<float> tuple_x(variation_requirements.tuple_point_count);
    std::vector<float> tuple_y(variation_requirements.tuple_point_count);
    std::vector<std::uint8_t> touched(
        variation_requirements.tuple_point_count);
    sfnt_simple_glyph_variation_scratch variation_scratch{
        varied_headers,
        varied_regions,
        shared_point_numbers,
        private_point_numbers,
        varied_x,
        varied_y,
        tuple_x,
        tuple_y,
        touched};
    std::vector<progpu_native_point> varied_points(
        outline_requirements.point_count);
    const std::array<std::int16_t, 2U> optical_coordinates{8192, 0};
    float horizontal_advance_delta = 99.0F;
    bool uses_hvar = false;
    require(font.try_get_horizontal_advance_variation(
        397U,
        optical_coordinates,
        horizontal_advance_delta,
        uses_hvar));
    require(uses_hvar && horizontal_advance_delta == -28.0F);
    float x_height_delta = 99.0F;
    bool has_x_height_record = false;
    require(font.try_get_metric_variation(
        open_type_tag::from_chars('x', 'h', 'g', 't'),
        optical_coordinates,
        x_height_delta,
        has_x_height_record));
    require(has_x_height_record && x_height_delta == -31.0F);
    float layout_delta = 99.0F;
    bool uses_layout_store = false;
    require(font.try_get_layout_variation(
        0U,
        0U,
        optical_coordinates,
        layout_delta,
        uses_layout_store));
    require(uses_layout_store);
    require(font.try_apply_simple_glyph_variations(
        397U,
        optical_coordinates,
        contour_ends,
        original_points,
        varied_points,
        variation_scratch));
    std::vector<progpu_native_path_segment> varied_segments(
        outline_requirements.path_segment_count);
    std::uint32_t varied_written = 0U;
    require(sfnt_simple_glyph_path::try_write_varied_segments(
        contour_ends,
        original_points,
        varied_points,
        varied_segments,
        varied_written));
    require(varied_written == 39U);
    require(varied_segments[0].p0.x == 648.5F);
    require(varied_segments[0].p0.y == -25.0F);
    require(hash_path_segments(varied_segments) ==
        12343280691057163238ULL);

    sfnt_composite_glyph_decode_requirements composite_requirements{};
    require(font.try_get_composite_glyph_decode_requirements(
        618U, composite_requirements));
    require(composite_requirements.component_count == 2U);
    sfnt_composite_glyph_variation_requirements component_variations{};
    require(font.try_get_composite_glyph_variation_requirements(
        618U, 2U, component_variations));
    std::vector<sfnt_gvar_tuple_header> component_headers(
        component_variations.tuple_header_count);
    std::vector<std::int16_t> component_regions(
        component_variations.region_coordinate_count);
    std::vector<std::uint32_t> component_shared_points(
        component_variations.point_number_count);
    std::vector<std::uint32_t> component_private_points(
        component_variations.point_number_count);
    std::vector<std::int16_t> component_x(component_variations.delta_count);
    std::vector<std::int16_t> component_y(component_variations.delta_count);
    sfnt_composite_glyph_variation_scratch component_scratch{
        component_headers,
        component_regions,
        component_shared_points,
        component_private_points,
        component_x,
        component_y};
    std::array<progpu_native_point, 2U> component_offsets{};
    require(font.try_get_composite_glyph_variation_offsets(
        618U,
        optical_coordinates,
        2U,
        component_offsets,
        component_scratch));
    require(component_offsets[0].x == 0.0F &&
        component_offsets[0].y == 0.0F &&
        component_offsets[1].x == 15.0F &&
        component_offsets[1].y == 0.0F);

    sfnt_varied_glyph_requirements varied_composite_requirements{};
    require(font.try_get_varied_glyph_requirements(
        618U, varied_composite_requirements));
    require(varied_composite_requirements.component_offset_count == 2U);
    const auto& simple_variation =
        varied_composite_requirements.simple_variation;
    const auto& composite_variation =
        varied_composite_requirements.composite_variation;
    std::vector<std::uint16_t> varied_contours(
        varied_composite_requirements.outline.simple_contour_scratch_count);
    std::vector<sfnt_outline_point> varied_original_points(
        varied_composite_requirements.outline.simple_point_scratch_count);
    std::vector<progpu_native_point> varied_point_scratch(
        varied_composite_requirements.varied_simple_point_count);
    std::vector<progpu_native_point> varied_component_offsets(
        varied_composite_requirements.component_offset_count);
    std::vector<sfnt_gvar_tuple_header> varied_simple_headers(
        simple_variation.tuple_header_count);
    std::vector<std::int16_t> varied_simple_regions(
        simple_variation.region_coordinate_count);
    std::vector<std::uint32_t> varied_simple_shared(
        simple_variation.point_number_count);
    std::vector<std::uint32_t> varied_simple_private(
        simple_variation.point_number_count);
    std::vector<std::int16_t> varied_simple_x(simple_variation.delta_count);
    std::vector<std::int16_t> varied_simple_y(simple_variation.delta_count);
    std::vector<float> varied_tuple_x(simple_variation.tuple_point_count);
    std::vector<float> varied_tuple_y(simple_variation.tuple_point_count);
    std::vector<std::uint8_t> varied_touched(
        simple_variation.tuple_point_count);
    std::vector<sfnt_gvar_tuple_header> varied_composite_headers(
        composite_variation.tuple_header_count);
    std::vector<std::int16_t> varied_composite_regions(
        composite_variation.region_coordinate_count);
    std::vector<std::uint32_t> varied_composite_shared(
        composite_variation.point_number_count);
    std::vector<std::uint32_t> varied_composite_private(
        composite_variation.point_number_count);
    std::vector<std::int16_t> varied_composite_x(
        composite_variation.delta_count);
    std::vector<std::int16_t> varied_composite_y(
        composite_variation.delta_count);
    sfnt_varied_glyph_scratch varied_scratch{
        varied_contours,
        varied_original_points,
        varied_point_scratch,
        varied_component_offsets,
        sfnt_simple_glyph_variation_scratch{
            varied_simple_headers,
            varied_simple_regions,
            varied_simple_shared,
            varied_simple_private,
            varied_simple_x,
            varied_simple_y,
            varied_tuple_x,
            varied_tuple_y,
            varied_touched},
        sfnt_composite_glyph_variation_scratch{
            varied_composite_headers,
            varied_composite_regions,
            varied_composite_shared,
            varied_composite_private,
            varied_composite_x,
            varied_composite_y}};
    std::vector<progpu_native_point> varied_composite_points(
        varied_composite_requirements.outline.point_count);
    std::vector<progpu_native_path_segment> varied_composite_segments(
        varied_composite_requirements.outline.path_segment_count);
    std::uint32_t varied_composite_points_written = 0U;
    std::uint32_t varied_composite_segments_written = 0U;
    require(font.try_decode_varied_glyph_outline(
        618U,
        optical_coordinates,
        varied_scratch,
        varied_composite_points,
        varied_composite_segments,
        varied_composite_points_written,
        varied_composite_segments_written));
    require(varied_composite_points_written ==
        varied_composite_requirements.outline.point_count);
    require(varied_composite_segments_written == 36U);
    require(varied_composite_segments[0].p0.x == 595.0F);
    require(varied_composite_segments[0].p0.y == -24.0F);
    require(hash_path_segments(varied_composite_segments) ==
        12064242707506207632ULL);

    auto short_varied_scratch = varied_scratch;
    short_varied_scratch.component_offsets =
        std::span<progpu_native_point>{varied_component_offsets}.first(1U);
    std::vector<progpu_native_point> untouched_points(
        varied_composite_requirements.outline.point_count,
        progpu_native_point{99.0F, 99.0F});
    std::uint32_t short_points_written = 99U;
    std::uint32_t short_segments_written = 99U;
    font_error short_error = font_error::none;
    require(!font.try_decode_varied_glyph_outline(
        618U,
        optical_coordinates,
        short_varied_scratch,
        untouched_points,
        varied_composite_segments,
        short_points_written,
        short_segments_written,
        &short_error));
    require(short_error == font_error::insufficient_buffer);
    require(short_points_written == 0U && short_segments_written == 0U);
    require(untouched_points[0].x == 99.0F &&
        untouched_points[0].y == 99.0F);
}

} // namespace

int main() {
    woff1_normalization_is_bounded_and_transactional();
    borrowed_sfnt_view_reads_tables_metrics_and_cmap();
    variation_axes_are_borrowed_bounded_and_transactional();
    variation_coordinates_apply_bounded_avar_mapping();
    packed_variation_streams_are_transactional_and_exact();
    glyph_variation_tuple_headers_are_bounded_and_exact();
    untouched_glyph_deltas_interpolate_without_allocation();
    simple_glyph_variations_apply_packed_tuple_deltas();
    composite_glyph_variations_apply_component_offsets();
    phantom_glyph_variations_apply_advance_delta();
    item_variation_store_and_index_map_are_bounded();
    collection_and_failure_paths_are_bounded();
    table_directory_preserves_managed_duplicate_and_bounds_rules();
    simple_glyph_repeat_composite_and_malformed_paths_are_explicit();
    simple_glyph_path_preserves_implicit_midpoints_and_is_transactional();
    expanded_composite_glyphs_preserve_transforms_and_point_attachment();
    cff1_indexes_and_dictionaries_are_borrowed_and_bounded();
    cff1_fd_select_formats_are_borrowed_and_searchable();
    cff1_type2_outline_is_transactional_and_closes_figures();
    cff2_indexes_blends_and_outlines_are_borrowed_and_bounded();
    sbix_strikes_and_duplicates_remain_borrowed();
    svg_glyph_documents_remain_borrowed_and_bounded();
    cbdt_index_and_image_formats_remain_borrowed_and_bounded();
    colr_layers_and_cpal_palettes_are_transactional();
    production_noto_cff1_container_matches_sfnt_glyph_count();
    production_inter_font_decodes_real_simple_outline();
    production_inter_variable_font_matches_fvar_axes();
    return 0;
}
