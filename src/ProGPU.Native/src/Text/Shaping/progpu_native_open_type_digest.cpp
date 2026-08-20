#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct C++20 port of ProGPU-owned OpenTypeTextShaper.GlyphSetDigest,
// TryCreateRawLookupDigest, and format-3 RawContextRequirements at checkpoint
// e7374eb1. These are negative-only accelerators; exact lookup execution
// remains authoritative.

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

bool try_resolve_subtable(
    const open_type_lookup_view& lookup,
    std::uint16_t subtable_index,
    std::uint16_t extension_lookup_type,
    std::size_t& subtable,
    std::uint16_t& effective_type) noexcept {
    subtable = 0U;
    effective_type = lookup.type;
    if (!lookup.try_get_subtable(subtable_index, subtable) ||
        !can_read(lookup.table, subtable, 4U)) {
        return false;
    }
    if (lookup.type != extension_lookup_type) {
        return true;
    }
    if (!can_read(lookup.table, subtable, 8U) ||
        read_u16(lookup.table, subtable) != 1U) {
        return false;
    }
    effective_type = read_u16(lookup.table, subtable + 2U);
    return try_add(
            subtable,
            read_u32(lookup.table, subtable + 4U),
            subtable) &&
        can_read(lookup.table, subtable, 4U);
}

bool try_collect_context_coverage(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::size_t relative_record,
    std::span<open_type_context_coverage_requirement> output,
    std::uint32_t& written) noexcept {
    if (!can_read(table, relative_record, 2U)) {
        return false;
    }
    const auto relative = read_u16(table, relative_record);
    std::size_t coverage_offset = 0U;
    open_type_context_coverage_requirement requirement{};
    if (relative == 0U || !try_add(subtable, relative, coverage_offset) ||
        !open_type_coverage_view::try_create(
            table, coverage_offset, requirement.coverage) ||
        !collect_coverage_digest(
            table, coverage_offset, requirement.digest)) {
        return false;
    }
    if (!output.empty()) {
        if (written >= output.size()) {
            return false;
        }
        output[written] = requirement;
    }
    ++written;
    return true;
}

bool try_parse_context_subtable(
    std::span<const std::byte> table,
    std::size_t subtable,
    std::uint16_t effective_type,
    std::uint16_t context_type,
    std::uint16_t chain_context_type,
    std::span<open_type_context_coverage_requirement> coverage_output,
    open_type_context_subtable_requirement& result) noexcept {
    result = {};
    if ((effective_type != context_type &&
            effective_type != chain_context_type) ||
        !can_read(table, subtable, 6U) ||
        read_u16(table, subtable) != 3U) {
        return false;
    }

    std::uint32_t coverage_count = 0U;
    if (effective_type == context_type) {
        const auto input_count = read_u16(table, subtable + 2U);
        const auto record_count = read_u16(table, subtable + 4U);
        const std::size_t coverage_records = subtable + 6U;
        const std::size_t coverage_bytes =
            static_cast<std::size_t>(input_count) * 2U;
        const std::size_t lookup_records = coverage_records + coverage_bytes;
        if (input_count == 0U ||
            !can_read(table, coverage_records, coverage_bytes) ||
            !can_read(
                table,
                lookup_records,
                static_cast<std::size_t>(record_count) * 4U)) {
            return false;
        }
        for (std::uint16_t index = 0U; index < input_count; ++index) {
            if (!try_collect_context_coverage(
                    table,
                    subtable,
                    coverage_records + static_cast<std::size_t>(index) * 2U,
                    coverage_output,
                    coverage_count)) {
                return false;
            }
        }
        result.coverage_count = coverage_count;
        result.input_count = input_count;
        return true;
    }

    std::size_t cursor = subtable + 2U;
    std::uint16_t section_counts[3]{};
    for (std::size_t section = 0U; section < 3U; ++section) {
        if (!can_read(table, cursor, 2U)) {
            return false;
        }
        const auto count = read_u16(table, cursor);
        section_counts[section] = count;
        cursor += 2U;
        if ((section == 1U && count == 0U) ||
            !can_read(
                table, cursor, static_cast<std::size_t>(count) * 2U)) {
            return false;
        }
        for (std::uint16_t index = 0U; index < count; ++index) {
            if (!try_collect_context_coverage(
                    table,
                    subtable,
                    cursor + static_cast<std::size_t>(index) * 2U,
                    coverage_output,
                    coverage_count)) {
                return false;
            }
        }
        cursor += static_cast<std::size_t>(count) * 2U;
    }
    if (!can_read(table, cursor, 2U)) {
        return false;
    }
    const auto record_count = read_u16(table, cursor);
    if (!can_read(
            table,
            cursor + 2U,
            static_cast<std::size_t>(record_count) * 4U)) {
        return false;
    }
    result.coverage_count = coverage_count;
    result.backtrack_count = section_counts[0];
    result.input_count = section_counts[1];
    return true;
}

} // namespace

void open_type_glyph_set_digest::add(std::uint16_t glyph) noexcept {
    shift2 |= 1ULL << ((glyph >> 2U) & 63U);
    shift4 |= 1ULL << ((glyph >> 4U) & 63U);
    shift0 |= 1ULL << (glyph & 63U);
    shift6 |= 1ULL << ((glyph >> 6U) & 63U);
    shift10 |= 1ULL << ((glyph >> 10U) & 63U);
}

void open_type_glyph_set_digest::add_range(
    std::uint16_t first,
    std::uint16_t last) noexcept {
    if (last < first) {
        return;
    }
    add_mask_range(shift2, first, last, 2U);
    add_mask_range(shift4, first, last, 4U);
    add_mask_range(shift0, first, last, 0U);
    add_mask_range(shift6, first, last, 6U);
    add_mask_range(shift10, first, last, 10U);
}

bool open_type_glyph_set_digest::may_have(std::uint16_t glyph) const noexcept {
    return (shift2 & (1ULL << ((glyph >> 2U) & 63U))) != 0U &&
        (shift4 & (1ULL << ((glyph >> 4U) & 63U))) != 0U &&
        (shift0 & (1ULL << (glyph & 63U))) != 0U &&
        (shift6 & (1ULL << ((glyph >> 6U) & 63U))) != 0U &&
        (shift10 & (1ULL << ((glyph >> 10U) & 63U))) != 0U;
}

bool open_type_glyph_set_digest::may_intersect(
    const open_type_glyph_set_digest& other) const noexcept {
    return (shift2 & other.shift2) != 0U &&
        (shift4 & other.shift4) != 0U &&
        (shift0 & other.shift0) != 0U &&
        (shift6 & other.shift6) != 0U &&
        (shift10 & other.shift10) != 0U;
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

bool open_type_layout_table_view::try_get_single_subtable_coverage(
    std::uint16_t index,
    std::uint16_t extension_lookup_type,
    open_type_lookup_view& lookup,
    open_type_coverage_view& result,
    bool& has_coverage,
    font_error* error) const noexcept {
    lookup = {};
    result = {};
    has_coverage = false;
    if (!try_get_lookup(index, lookup, error)) {
        return false;
    }
    if (lookup.subtable_count != 1U) {
        set_error(error, font_error::none);
        return true;
    }

    std::size_t subtable = 0U;
    std::uint16_t effective_type = 0U;
    if (!try_resolve_subtable(
            lookup, 0U, extension_lookup_type, subtable, effective_type) ||
        (extension_lookup_type != 7U && extension_lookup_type != 9U) ||
        effective_type == 0U || effective_type > 8U ||
        effective_type == extension_lookup_type ||
        !can_read(lookup.table, subtable, 4U)) {
        set_error(error, font_error::none);
        return true;
    }

    const std::uint16_t format = read_u16(lookup.table, subtable);
    const bool is_context =
        (extension_lookup_type == 7U &&
            (effective_type == 5U || effective_type == 6U)) ||
        (extension_lookup_type == 9U &&
            (effective_type == 7U || effective_type == 8U));
    if (is_context && format == 3U) {
        set_error(error, font_error::none);
        return true;
    }

    const std::uint16_t relative = read_u16(lookup.table, subtable + 2U);
    std::size_t coverage = 0U;
    if (relative == 0U || !try_add(subtable, relative, coverage) ||
        !open_type_coverage_view::try_create(
            lookup.table, coverage, result)) {
        result = {};
        set_error(error, font_error::none);
        return true;
    }
    has_coverage = true;
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::
    try_get_lookup_context_accelerator_requirements(
        std::uint16_t index,
        std::uint16_t extension_lookup_type,
        open_type_context_accelerator_requirements& result,
        font_error* error) const noexcept {
    result = {};
    open_type_lookup_view lookup{};
    if (!try_get_lookup(index, lookup, error)) {
        return false;
    }
    if (lookup.subtable_count == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    const auto context_type = static_cast<std::uint16_t>(
        extension_lookup_type == 7U ? 5U : 7U);
    const auto chain_context_type = static_cast<std::uint16_t>(
        extension_lookup_type == 7U ? 6U : 8U);
    std::uint32_t coverage_capacity = 0U;
    for (std::uint16_t subtable_index = 0U;
         subtable_index < lookup.subtable_count;
         ++subtable_index) {
        std::size_t subtable = 0U;
        std::uint16_t effective_type = 0U;
        open_type_context_subtable_requirement subtable_requirement{};
        if (!try_resolve_subtable(
                lookup,
                subtable_index,
                extension_lookup_type,
                subtable,
                effective_type) ||
            !try_parse_context_subtable(
                lookup.table,
                subtable,
                effective_type,
                context_type,
                chain_context_type,
                {},
                subtable_requirement) ||
            subtable_requirement.coverage_count >
                std::numeric_limits<std::uint32_t>::max() -
                    coverage_capacity) {
            result = {};
            set_error(error, font_error::none);
            return true;
        }
        coverage_capacity += subtable_requirement.coverage_count;
    }
    result.subtable_capacity = lookup.subtable_count;
    result.coverage_capacity = coverage_capacity;
    result.supported = true;
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_build_lookup_context_accelerator(
    std::uint16_t index,
    std::uint16_t extension_lookup_type,
    std::span<open_type_context_subtable_requirement> subtable_storage,
    std::span<open_type_context_coverage_requirement> coverage_storage,
    std::uint16_t& lookup_flags,
    bool& has_context,
    font_error* error) const noexcept {
    lookup_flags = 0U;
    has_context = false;
    open_type_context_accelerator_requirements requirements{};
    if (!try_get_lookup_context_accelerator_requirements(
            index, extension_lookup_type, requirements, error)) {
        return false;
    }
    if (!requirements.supported) {
        set_error(error, font_error::none);
        return true;
    }
    if (subtable_storage.size() < requirements.subtable_capacity ||
        coverage_storage.size() < requirements.coverage_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    open_type_lookup_view lookup{};
    if (!try_get_lookup(index, lookup, error)) {
        return false;
    }
    const auto context_type = static_cast<std::uint16_t>(
        extension_lookup_type == 7U ? 5U : 7U);
    const auto chain_context_type = static_cast<std::uint16_t>(
        extension_lookup_type == 7U ? 6U : 8U);
    std::uint32_t coverage_cursor = 0U;
    for (std::uint16_t subtable_index = 0U;
         subtable_index < lookup.subtable_count;
         ++subtable_index) {
        std::size_t subtable = 0U;
        std::uint16_t effective_type = 0U;
        auto& subtable_result = subtable_storage[subtable_index];
        if (!try_resolve_subtable(
                lookup,
                subtable_index,
                extension_lookup_type,
                subtable,
                effective_type) ||
            !try_parse_context_subtable(
                lookup.table,
                subtable,
                effective_type,
                context_type,
                chain_context_type,
                coverage_storage.subspan(coverage_cursor),
                subtable_result)) {
            lookup_flags = 0U;
            has_context = false;
            set_error(error, font_error::none);
            return true;
        }
        subtable_result.coverage_offset = coverage_cursor;
        coverage_cursor += subtable_result.coverage_count;
    }
    lookup_flags = lookup.flags;
    has_context = true;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
