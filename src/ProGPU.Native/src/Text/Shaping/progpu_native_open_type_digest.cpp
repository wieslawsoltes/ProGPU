#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct C++20 port of ProGPU-owned OpenTypeTextShaper.GlyphSetDigest and
// TryCreateRawLookupDigest at checkpoint e7374eb1. The digest is a
// negative-only accelerator; exact coverage parsing remains authoritative.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool can_read(
    std::span<const std::byte> bytes,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= bytes.size() && length <= bytes.size() - offset;
}

bool try_add(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

std::uint16_t read_u16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(bytes[offset]) << 8U) |
        std::to_integer<std::uint16_t>(bytes[offset + 1U]));
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(read_u16(bytes, offset)) << 16U) |
        read_u16(bytes, offset + 2U);
}

void add_mask_range(
    std::uint64_t& mask,
    std::uint16_t first,
    std::uint16_t last,
    std::uint32_t shift) noexcept {
    const std::uint32_t start = first >> shift;
    const std::uint32_t end = last >> shift;
    if (end - start >= 63U) {
        mask = std::numeric_limits<std::uint64_t>::max();
        return;
    }
    const std::uint64_t first_bit = 1ULL << (start & 63U);
    const std::uint64_t last_bit = 1ULL << (end & 63U);
    mask |= last_bit + (last_bit - first_bit) -
        (last_bit < first_bit ? 1ULL : 0ULL);
}

bool collect_coverage_digest(
    std::span<const std::byte> table,
    std::size_t coverage,
    open_type_glyph_set_digest& digest) noexcept {
    if (!can_read(table, coverage, 4U)) {
        return false;
    }
    const std::uint16_t format = read_u16(table, coverage);
    const std::uint16_t count = read_u16(table, coverage + 2U);
    if (format == 1U) {
        if (!can_read(
                table,
                coverage + 4U,
                static_cast<std::size_t>(count) * 2U)) {
            return false;
        }
        for (std::uint16_t index = 0U; index < count; ++index) {
            digest.add(read_u16(
                table,
                coverage + 4U + static_cast<std::size_t>(index) * 2U));
        }
        return true;
    }
    if (format != 2U ||
        !can_read(
            table,
            coverage + 4U,
            static_cast<std::size_t>(count) * 6U)) {
        return false;
    }
    for (std::uint16_t index = 0U; index < count; ++index) {
        const std::size_t range = coverage + 4U +
            static_cast<std::size_t>(index) * 6U;
        const std::uint16_t first = read_u16(table, range);
        const std::uint16_t last = read_u16(table, range + 2U);
        if (last < first) {
            return false;
        }
        digest.add_range(first, last);
    }
    return true;
}

} // namespace

void open_type_glyph_set_digest::add(std::uint16_t glyph) noexcept {
    shift4 |= 1ULL << ((glyph >> 4U) & 63U);
    shift0 |= 1ULL << (glyph & 63U);
    shift6 |= 1ULL << ((glyph >> 6U) & 63U);
}

void open_type_glyph_set_digest::add_range(
    std::uint16_t first,
    std::uint16_t last) noexcept {
    if (last < first) {
        return;
    }
    add_mask_range(shift4, first, last, 4U);
    add_mask_range(shift0, first, last, 0U);
    add_mask_range(shift6, first, last, 6U);
}

bool open_type_glyph_set_digest::may_have(std::uint16_t glyph) const noexcept {
    return (shift4 & (1ULL << ((glyph >> 4U) & 63U))) != 0U &&
        (shift0 & (1ULL << (glyph & 63U))) != 0U &&
        (shift6 & (1ULL << ((glyph >> 6U) & 63U))) != 0U;
}

bool open_type_glyph_set_digest::may_intersect(
    const open_type_glyph_set_digest& other) const noexcept {
    return (shift4 & other.shift4) != 0U &&
        (shift0 & other.shift0) != 0U &&
        (shift6 & other.shift6) != 0U;
}

bool open_type_layout_table_view::try_get_lookup_digest(
    std::uint16_t index,
    std::uint16_t extension_lookup_type,
    open_type_glyph_set_digest& result,
    bool& has_digest,
    font_error* error) const noexcept {
    result = {};
    has_digest = false;
    open_type_lookup_view lookup{};
    if (!try_get_lookup(index, lookup, error)) {
        return false;
    }
    if (lookup.subtable_count == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    for (std::uint16_t subtable_index = 0U;
         subtable_index < lookup.subtable_count;
         ++subtable_index) {
        std::size_t subtable = 0U;
        if (!lookup.try_get_subtable(subtable_index, subtable) ||
            !can_read(lookup.table, subtable, 4U)) {
            result = {};
            set_error(error, font_error::none);
            return true;
        }
        if (lookup.type == extension_lookup_type) {
            if (!can_read(lookup.table, subtable, 8U) ||
                read_u16(lookup.table, subtable) != 1U ||
                !try_add(
                    subtable,
                    read_u32(lookup.table, subtable + 4U),
                    subtable) ||
                !can_read(lookup.table, subtable, 4U)) {
                result = {};
                set_error(error, font_error::none);
                return true;
            }
        }
        std::size_t coverage = 0U;
        if (!try_add(
                subtable,
                read_u16(lookup.table, subtable + 2U),
                coverage) ||
            !collect_coverage_digest(lookup.table, coverage, result)) {
            result = {};
            set_error(error, font_error::none);
            return true;
        }
    }
    has_digest = true;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
