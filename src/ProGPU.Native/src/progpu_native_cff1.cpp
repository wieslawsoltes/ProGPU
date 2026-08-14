#include "progpu_native_text.hpp"

#include <cstddef>

// Direct native port provenance: ProGPU-owned Cff1OutlineSource.TryCreate at
// checkpoint 2f152ddd. The native view borrows the SFNT CFF table and retains
// encoded INDEX offsets, so opening a face is bounded and allocation-free.
namespace progpu::native::text {
namespace {

constexpr auto cff_tag = open_type_tag::from_chars('C', 'F', 'F', ' ');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_font_view::try_get_cff1_font(
    std::uint16_t expected_glyph_count,
    sfnt_cff1_font_view& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);

    sfnt_table_view table{};
    if (!try_get_table(cff_tag, table) || table.bytes.size() < 4U ||
        std::to_integer<std::uint8_t>(table.bytes[0]) != 1U) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    std::size_t cursor = std::to_integer<std::uint8_t>(table.bytes[2]);
    if (cursor < 4U || cursor > table.bytes.size()) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    sfnt_cff_index_view names{};
    sfnt_cff_index_view top_dictionaries{};
    sfnt_cff_index_view strings{};
    sfnt_cff_index_view global_subroutines{};
    if (!sfnt_cff_data::try_read_index(table.bytes, cursor, names, error) ||
        !sfnt_cff_data::try_read_index(
            table.bytes, cursor, top_dictionaries, error) ||
        top_dictionaries.count != 1U ||
        !sfnt_cff_data::try_read_index(table.bytes, cursor, strings, error) ||
        !sfnt_cff_data::try_read_index(
            table.bytes, cursor, global_subroutines, error)) {
        result = {};
        set_error(error, font_error::invalid_face);
        return false;
    }

    std::span<const std::byte> top_bytes{};
    sfnt_cff1_top_dictionary top{};
    if (!sfnt_cff_data::try_get_index_item(
            top_dictionaries, 0U, top_bytes, error) ||
        !sfnt_cff_data::try_get_top_dictionary(top_bytes, top, error) ||
        top.char_strings_offset >= table.bytes.size()) {
        result = {};
        set_error(error, font_error::invalid_face);
        return false;
    }

    std::size_t char_strings_cursor = top.char_strings_offset;
    sfnt_cff_index_view char_strings{};
    if (!sfnt_cff_data::try_read_index(
            table.bytes, char_strings_cursor, char_strings, error) ||
        char_strings.count == 0U ||
        (expected_glyph_count != 0U &&
            char_strings.count != expected_glyph_count) ||
        ((top.font_dictionary_offset == 0U) !=
            (top.fd_select_offset == 0U))) {
        result = {};
        set_error(error, font_error::invalid_face);
        return false;
    }

    result = {table.bytes, char_strings, global_subroutines, top};
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
