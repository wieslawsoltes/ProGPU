#include "progpu_native_text.hpp"

#include "progpu_native_compression.hpp"
#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont SVG document lookup at
// checkpoint ad2d2c43. The table and selected XML/gzip payload remain borrowed;
// lookup is O(R) for R document records and uses O(1) internal storage.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;
using detail::read_u32;

constexpr auto svg_tag = open_type_tag::from_chars('S', 'V', 'G', ' ');
constexpr std::uint32_t maximum_document_bytes = 16U * 1024U * 1024U;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool try_get_svg_glyph_document_size(
    const sfnt_svg_glyph_document_view& document,
    std::size_t& result,
    font_error* error) noexcept {
    result = 0U;
    set_error(error, font_error::none);
    if (document.bytes.empty()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (!document.gzip_compressed) {
        result = document.bytes.size();
        return true;
    }
    compression::compression_error compression_error{};
    if (!compression::try_get_gzip_uncompressed_size(
            document.bytes, result, &compression_error)) {
        set_error(error, font_error::invalid_glyph);
        result = 0U;
        return false;
    }
    return true;
}

bool try_decode_svg_glyph_document(
    const sfnt_svg_glyph_document_view& document,
    std::span<std::byte> output,
    std::size_t& written,
    font_error* error) noexcept {
    written = 0U;
    set_error(error, font_error::none);
    if (document.bytes.empty()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (!document.gzip_compressed) {
        if (output.size() < document.bytes.size()) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
        std::copy(document.bytes.begin(), document.bytes.end(), output.begin());
        written = document.bytes.size();
        return true;
    }
    compression::compression_error compression_error{};
    if (!compression::try_inflate_gzip(
            document.bytes, output, written, &compression_error)) {
        set_error(error,
            compression_error ==
                    compression::compression_error::insufficient_buffer
                ? font_error::insufficient_buffer
                : font_error::invalid_glyph);
        written = 0U;
        return false;
    }
    return true;
}

bool sfnt_font_view::try_get_svg_glyph_document(
    std::uint16_t glyph_index,
    sfnt_svg_glyph_document_view& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_table_view table{};
    if (!try_get_table(svg_tag, table) || table.bytes.size() < 12U ||
        read_u16(table.bytes, 0U) != 0U) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    const auto list_offset =
        static_cast<std::size_t>(read_u32(table.bytes, 2U));
    if (list_offset < 10U || !can_read(table.bytes, list_offset, 2U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto record_count = read_u16(table.bytes, list_offset);
    const auto records_offset = list_offset + 2U;
    if (record_count == 0U ||
        static_cast<std::size_t>(record_count) >
            (table.bytes.size() - records_offset) / 12U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    for (std::uint16_t record = 0U; record < record_count; ++record) {
        const auto record_offset = records_offset +
            static_cast<std::size_t>(record) * 12U;
        const auto first_glyph = read_u16(table.bytes, record_offset);
        const auto last_glyph = read_u16(table.bytes, record_offset + 2U);
        if (glyph_index < first_glyph || glyph_index > last_glyph) {
            continue;
        }
        const auto document_offset = read_u32(table.bytes, record_offset + 4U);
        const auto document_length = read_u32(table.bytes, record_offset + 8U);
        if (document_offset == 0U || document_length == 0U ||
            document_length > maximum_document_bytes ||
            document_offset > table.bytes.size() - list_offset) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto document_start = list_offset + document_offset;
        if (!can_read(table.bytes, document_start, document_length)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto document = table.bytes.subspan(
            document_start, document_length);
        const auto gzip = document.size() >= 3U &&
            document[0U] == std::byte{0x1FU} &&
            document[1U] == std::byte{0x8BU} &&
            document[2U] == std::byte{0x08U};
        result = {document, first_glyph, last_glyph, gzip};
        return true;
    }
    set_error(error, font_error::invalid_glyph);
    return false;
}

} // namespace progpu::native::text
