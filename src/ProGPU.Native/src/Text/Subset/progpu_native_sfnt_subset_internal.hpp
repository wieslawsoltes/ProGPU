#pragma once

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::text::sfnt_subset_detail {

struct table_data final {
    std::uint32_t tag = 0U;
    std::vector<std::byte> bytes;
};

struct face_data final {
    std::uint32_t sfnt_version = 0U;
    std::vector<table_data> tables;
};

struct glyph_table_subset final {
    std::vector<std::byte> glyf;
    std::vector<std::byte> loca;
};

struct compact_subset_result final {
    std::vector<std::byte> font;
    std::vector<sfnt_glyph_remap> glyph_map;
};

struct subset_failure final {};

constexpr std::uint32_t tag(char a, char b, char c, char d) noexcept {
    return open_type_tag::from_chars(a, b, c, d).value;
}

bool can_read(
    std::span<const std::byte> data,
    std::size_t offset,
    std::size_t length) noexcept;
std::uint16_t read_u16(
    std::span<const std::byte> data,
    std::size_t offset);
std::int16_t read_i16(
    std::span<const std::byte> data,
    std::size_t offset);
std::uint32_t read_u32(
    std::span<const std::byte> data,
    std::size_t offset);
void write_u16(
    std::span<std::byte> data,
    std::size_t offset,
    std::uint16_t value);
void write_i16(
    std::span<std::byte> data,
    std::size_t offset,
    std::int16_t value);
void write_u32(
    std::span<std::byte> data,
    std::size_t offset,
    std::uint32_t value);
std::size_t align4(std::size_t value);

face_data parse_face(
    std::span<const std::byte> font_data,
    std::size_t directory_offset);
const table_data* find_table(
    const face_data& face,
    std::uint32_t table_tag) noexcept;
std::vector<std::byte> build_sfnt(
    std::uint32_t sfnt_version,
    std::span<const table_data> tables);
std::vector<std::byte> build_glyph_id_preserving_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs);
std::vector<std::uint32_t> read_loca(
    std::span<const std::byte> loca,
    std::uint16_t glyph_count,
    std::int16_t format);
void include_composite_dependencies(
    std::span<const std::byte> glyf,
    std::span<const std::uint32_t> offsets,
    std::vector<bool>& included);
compact_subset_result build_compact_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs);

} // namespace progpu::native::text::sfnt_subset_detail
