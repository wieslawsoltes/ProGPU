#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned Cff1OutlineSource.FdSelect at
// checkpoint 006069ab. Formats 0, 3, and 4 stay encoded in borrowed storage;
// lookup is O(1) for format 0 and O(log R) for R ranges without heap storage.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;
using detail::read_u32;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_get_range(
    sfnt_cff_fd_select_view view,
    std::uint32_t range,
    std::uint32_t& first_glyph,
    std::uint32_t& dictionary) noexcept {
    if (range >= view.range_count) {
        return false;
    }
    if (view.format == 3U) {
        const auto offset = view.records_offset +
            static_cast<std::size_t>(range) * 3U;
        if (!can_read(view.bytes, offset, 3U)) {
            return false;
        }
        first_glyph = read_u16(view.bytes, offset);
        dictionary = std::to_integer<std::uint8_t>(view.bytes[offset + 2U]);
        return true;
    }
    if (view.format == 4U) {
        const auto offset = view.records_offset +
            static_cast<std::size_t>(range) * 6U;
        if (!can_read(view.bytes, offset, 6U)) {
            return false;
        }
        first_glyph = read_u32(view.bytes, offset);
        dictionary = read_u16(view.bytes, offset + 4U);
        return true;
    }
    return false;
}

} // namespace

bool sfnt_cff_data::try_read_fd_select(
    std::span<const std::byte> bytes,
    std::uint32_t offset,
    std::uint32_t glyph_count,
    std::uint32_t font_dictionary_count,
    sfnt_cff_fd_select_view& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    const auto start = static_cast<std::size_t>(offset);
    if (start >= bytes.size() || font_dictionary_count == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto format = std::to_integer<std::uint8_t>(bytes[start]);
    const auto records_offset = start + 1U;
    if (format == 0U) {
        if (!can_read(bytes, records_offset, glyph_count)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        for (std::uint32_t glyph = 0U; glyph < glyph_count; ++glyph) {
            if (std::to_integer<std::uint8_t>(
                    bytes[records_offset + glyph]) >=
                font_dictionary_count) {
                set_error(error, font_error::invalid_face);
                return false;
            }
        }
        result = {bytes, records_offset, glyph_count, glyph_count,
            font_dictionary_count, format};
        return true;
    }
    if (format == 3U) {
        if (!can_read(bytes, records_offset, 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto range_count = read_u16(bytes, records_offset);
        const auto ranges_offset = records_offset + 2U;
        const auto encoded_size = static_cast<std::size_t>(range_count) * 3U;
        if (range_count == 0U ||
            !can_read(bytes, ranges_offset, encoded_size + 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::uint32_t previous = 0U;
        for (std::uint32_t range = 0U; range < range_count; ++range) {
            const auto range_offset = ranges_offset +
                static_cast<std::size_t>(range) * 3U;
            const auto first = read_u16(bytes, range_offset);
            const auto dictionary = std::to_integer<std::uint8_t>(
                bytes[range_offset + 2U]);
            if ((range == 0U && first != 0U) ||
                (range != 0U && first <= previous) ||
                dictionary >= font_dictionary_count) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            previous = first;
        }
        if (read_u16(bytes, ranges_offset + encoded_size) != glyph_count) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        result = {bytes, ranges_offset, glyph_count, range_count,
            font_dictionary_count, format};
        return true;
    }
    if (format == 4U) {
        if (!can_read(bytes, records_offset, 4U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto range_count = read_u32(bytes, records_offset);
        const auto ranges_offset = records_offset + 4U;
        if (range_count == 0U || ranges_offset > bytes.size() ||
            bytes.size() - ranges_offset < 4U ||
            static_cast<std::size_t>(range_count) >
                (bytes.size() - ranges_offset - 4U) / 6U) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto encoded_size = static_cast<std::size_t>(range_count) * 6U;
        if (!can_read(bytes, ranges_offset, encoded_size) ||
            !can_read(bytes, ranges_offset + encoded_size, 4U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::uint32_t previous = 0U;
        for (std::uint32_t range = 0U; range < range_count; ++range) {
            const auto range_offset = ranges_offset +
                static_cast<std::size_t>(range) * 6U;
            const auto first = read_u32(bytes, range_offset);
            const auto dictionary = read_u16(bytes, range_offset + 4U);
            if ((range == 0U && first != 0U) ||
                (range != 0U && first <= previous) ||
                dictionary >= font_dictionary_count) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            previous = first;
        }
        if (read_u32(bytes, ranges_offset + encoded_size) != glyph_count) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        result = {bytes, ranges_offset, glyph_count, range_count,
            font_dictionary_count, format};
        return true;
    }
    set_error(error, font_error::invalid_face);
    return false;
}

bool sfnt_cff_data::try_get_font_dictionary(
    sfnt_cff_fd_select_view fd_select,
    std::uint32_t glyph_index,
    std::uint32_t& result,
    font_error* error) noexcept {
    result = 0U;
    set_error(error, font_error::none);
    if (glyph_index >= fd_select.glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (fd_select.format == 0U) {
        if (!detail::can_read(
                fd_select.bytes,
                fd_select.records_offset + glyph_index,
                1U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        result = std::to_integer<std::uint8_t>(
            fd_select.bytes[fd_select.records_offset + glyph_index]);
        if (result >= fd_select.font_dictionary_count) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        return true;
    }

    std::uint32_t low = 0U;
    std::uint32_t high = fd_select.range_count;
    while (low < high) {
        const auto middle = low + ((high - low) >> 1U);
        std::uint32_t first = 0U;
        std::uint32_t dictionary = 0U;
        if (!try_get_range(fd_select, middle, first, dictionary)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (glyph_index < first) {
            high = middle;
        } else {
            low = middle + 1U;
        }
    }
    if (low == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    std::uint32_t first = 0U;
    if (!try_get_range(fd_select, low - 1U, first, result) ||
        first > glyph_index || result >= fd_select.font_dictionary_count) {
        result = 0U;
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

} // namespace progpu::native::text
