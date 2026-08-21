#include "progpu_native_open_type_gpos_internal.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned contextual GPOS execution in
// OpenTypeTextShaper.cs. Kept separate from primitive positioning so complex
// rule matching does not grow the common lookup translation unit.

namespace progpu::native::text {
namespace {

using apply_result = detail::gpos_apply_result;

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

bool is_eligible(
    const shaping_glyph& glyph,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    if (glyph.glyph_id > 0xFFFFU) {
        return false;
    }
    if (gdef == nullptr) {
        return true;
    }
    const auto glyph_class =
        gdef->glyph_class(static_cast<std::uint16_t>(glyph.glyph_id));
    if (glyph_class == open_type_glyph_class::base &&
        (flags & 0x0002U) != 0U) {
        return false;
    }
    if (glyph_class == open_type_glyph_class::ligature &&
        (flags & 0x0004U) != 0U) {
        return false;
    }
    if (glyph_class != open_type_glyph_class::mark) {
        return true;
    }
    if ((flags & 0x0008U) != 0U) {
        return false;
    }
    if ((flags & 0x0010U) != 0U) {
        return mark_filtering_set != 0xFFFFU &&
            gdef->is_in_mark_set(
                mark_filtering_set,
                static_cast<std::uint16_t>(glyph.glyph_id));
    }
    const std::uint16_t attachment =
        static_cast<std::uint16_t>(flags >> 8U);
    return attachment == 0U ||
        gdef->mark_attachment_class(
            static_cast<std::uint16_t>(glyph.glyph_id)) == attachment;
}

std::size_t next_eligible(
    std::span<const shaping_glyph> glyphs,
    std::size_t start,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    for (std::size_t index = start; index < glyphs.size(); ++index) {
        if (is_eligible(glyphs[index], flags, mark_filtering_set, gdef)) {
            return index;
        }
    }
    return glyphs.size();
}

std::size_t previous_eligible(
    std::span<const shaping_glyph> glyphs,
    std::size_t before,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    while (before != 0U) {
        --before;
        if (is_eligible(glyphs[before], flags, mark_filtering_set, gdef)) {
            return before;
        }
    }
    return glyphs.size();
}

std::size_t eligible_sequence_position(
    std::span<const shaping_glyph> glyphs,
    std::size_t first,
    std::uint16_t sequence_index,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    std::size_t result = first;
    for (std::uint16_t index = 0U; index < sequence_index; ++index) {
        result = next_eligible(
            glyphs, result + 1U, flags, mark_filtering_set, gdef);
        if (result >= glyphs.size()) {
            return glyphs.size();
        }
    }
    return result;
}

void mark_unsafe(
    std::span<shaping_glyph> glyphs,
    std::size_t first,
    std::size_t last) noexcept {
    detail::mark_gpos_dependency(glyphs, first, last);
}

apply_result match_coverage(
    std::span<const std::byte> table,
    std::size_t parent,
    std::uint16_t relative,
    std::uint32_t glyph_id,
    bool& matches) noexcept {
    matches = false;
    std::size_t offset = 0U;
    open_type_coverage_view coverage{};
    if (glyph_id > 0xFFFFU || relative == 0U ||
        !try_add(parent, relative, offset) ||
        !open_type_coverage_view::try_create(table, offset, coverage)) {
        return apply_result::malformed;
    }
    matches = coverage.find(static_cast<std::uint16_t>(glyph_id)) >= 0;
    return matches ? apply_result::applied : apply_result::no_match;
}

apply_result apply_position_records(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::size_t records,
    std::uint16_t record_count,
    std::uint16_t input_count,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    if (!can_read(table, records,
            static_cast<std::size_t>(record_count) * 4U)) {
        return apply_result::malformed;
    }
    bool changed = false;
    for (std::uint16_t record = 0U; record < record_count; ++record) {
        const std::size_t offset = records + record * 4U;
        const std::uint16_t sequence_index = read_u16(table, offset);
        const std::uint16_t nested_lookup = read_u16(table, offset + 2U);
        if (sequence_index >= input_count) {
            return apply_result::malformed;
        }
        const std::size_t target = eligible_sequence_position(
            glyphs,
            position,
            sequence_index,
            lookup_flags,
            mark_filtering_set,
            options.gdef);
        if (target >= glyphs.size()) {
            return apply_result::no_match;
        }
        const apply_result nested = detail::apply_gpos_lookup_at(
            gpos,
            nested_lookup,
            glyphs,
            target,
            options,
            depth + 1U);
        if (nested == apply_result::malformed ||
            nested == apply_result::invalid_argument) {
            return nested;
        }
        changed |= nested == apply_result::applied;
    }
    return changed || record_count == 0U
        ? apply_result::applied
        : apply_result::no_match;
}

apply_result apply_context_format3(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    if (!can_read(table, subtable, 6U) || read_u16(table, subtable) != 3U) {
        return apply_result::malformed;
    }
    const std::uint16_t input_count = read_u16(table, subtable + 2U);
    const std::uint16_t record_count = read_u16(table, subtable + 4U);
    const std::size_t coverages = subtable + 6U;
    if (input_count == 0U ||
        !can_read(table, coverages,
            static_cast<std::size_t>(input_count) * 2U +
                static_cast<std::size_t>(record_count) * 4U)) {
        return apply_result::malformed;
    }
    std::size_t match = position;
    for (std::uint16_t index = 0U; index < input_count; ++index) {
        if (index != 0U) {
            match = next_eligible(
                glyphs,
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= glyphs.size()) {
                return apply_result::no_match;
            }
        }
        bool matches = false;
        const apply_result coverage_result = match_coverage(
            table,
            subtable,
            read_u16(table, coverages + index * 2U),
            glyphs[match].glyph_id,
            matches);
        if (coverage_result == apply_result::malformed) {
            return coverage_result;
        }
        if (!matches) {
            return apply_result::no_match;
        }
    }
    mark_unsafe(glyphs, position, match);
    return apply_position_records(
        gpos,
        table,
        coverages + static_cast<std::size_t>(input_count) * 2U,
        record_count,
        input_count,
        glyphs,
        position,
        lookup_flags,
        mark_filtering_set,
        options,
        depth);
}

apply_result apply_context_format1_or2(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::uint16_t format,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    const std::size_t header_size = format == 1U ? 6U : 8U;
    if (!can_read(table, subtable, header_size)) {
        return apply_result::malformed;
    }
    const std::uint16_t coverage_relative = read_u16(table, subtable + 2U);
    std::size_t coverage_offset = 0U;
    open_type_coverage_view coverage{};
    if (coverage_relative == 0U ||
        !try_add(subtable, coverage_relative, coverage_offset) ||
        !open_type_coverage_view::try_create(table, coverage_offset, coverage)) {
        return apply_result::malformed;
    }
    const std::int32_t coverage_index = coverage.find(
        static_cast<std::uint16_t>(glyphs[position].glyph_id));
    if (coverage_index < 0) {
        return apply_result::no_match;
    }

    open_type_class_definition_view classes{};
    std::uint32_t set_index = static_cast<std::uint32_t>(coverage_index);
    std::uint16_t set_count = 0U;
    std::size_t set_offsets = 0U;
    if (format == 1U) {
        set_count = read_u16(table, subtable + 4U);
        set_offsets = subtable + 6U;
    } else {
        const std::uint16_t class_relative = read_u16(table, subtable + 4U);
        std::size_t class_offset = 0U;
        if (class_relative == 0U ||
            !try_add(subtable, class_relative, class_offset) ||
            !open_type_class_definition_view::try_create(
                table, class_offset, classes)) {
            return apply_result::malformed;
        }
        set_count = read_u16(table, subtable + 6U);
        set_offsets = subtable + 8U;
        set_index = classes.get(
            static_cast<std::uint16_t>(glyphs[position].glyph_id));
    }
    if (!can_read(table, set_offsets,
            static_cast<std::size_t>(set_count) * 2U)) {
        return apply_result::malformed;
    }
    if (set_index >= set_count) {
        return apply_result::no_match;
    }
    const std::uint16_t set_relative =
        read_u16(table, set_offsets + set_index * 2U);
    if (set_relative == 0U) {
        return apply_result::no_match;
    }
    std::size_t set = 0U;
    if (!try_add(subtable, set_relative, set) || !can_read(table, set, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t rule_count = read_u16(table, set);
    if (!can_read(table, set + 2U,
            static_cast<std::size_t>(rule_count) * 2U)) {
        return apply_result::malformed;
    }
    for (std::uint16_t rule_index = 0U;
         rule_index < rule_count;
         ++rule_index) {
        const std::uint16_t rule_relative =
            read_u16(table, set + 2U + rule_index * 2U);
        std::size_t rule = 0U;
        if (rule_relative == 0U || !try_add(set, rule_relative, rule) ||
            !can_read(table, rule, 4U)) {
            return apply_result::malformed;
        }
        const std::uint16_t input_count = read_u16(table, rule);
        const std::uint16_t record_count = read_u16(table, rule + 2U);
        if (input_count == 0U ||
            !can_read(table, rule + 4U,
                static_cast<std::size_t>(input_count - 1U) * 2U +
                    static_cast<std::size_t>(record_count) * 4U)) {
            return apply_result::malformed;
        }
        std::size_t match = position;
        bool matches = true;
        for (std::uint16_t input = 1U; input < input_count; ++input) {
            match = next_eligible(
                glyphs,
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= glyphs.size()) {
                matches = false;
                break;
            }
            const std::uint16_t expected =
                read_u16(table, rule + 4U + (input - 1U) * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(glyphs[match].glyph_id)
                : classes.get(
                    static_cast<std::uint16_t>(glyphs[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        mark_unsafe(glyphs, position, match);
        return apply_position_records(
            gpos,
            table,
            rule + 4U + static_cast<std::size_t>(input_count - 1U) * 2U,
            record_count,
            input_count,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    return apply_result::no_match;
}

apply_result apply_chain_context_format1_or2(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::uint16_t format,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    const std::size_t header_size = format == 1U ? 6U : 12U;
    if (!can_read(table, subtable, header_size)) {
        return apply_result::malformed;
    }
    const std::uint16_t coverage_relative = read_u16(table, subtable + 2U);
    std::size_t coverage_offset = 0U;
    open_type_coverage_view coverage{};
    if (coverage_relative == 0U ||
        !try_add(subtable, coverage_relative, coverage_offset) ||
        !open_type_coverage_view::try_create(table, coverage_offset, coverage)) {
        return apply_result::malformed;
    }
    const std::int32_t coverage_index = coverage.find(
        static_cast<std::uint16_t>(glyphs[position].glyph_id));
    if (coverage_index < 0) {
        return apply_result::no_match;
    }

    open_type_class_definition_view backtrack_classes{};
    open_type_class_definition_view input_classes{};
    open_type_class_definition_view lookahead_classes{};
    std::uint32_t set_index = static_cast<std::uint32_t>(coverage_index);
    std::uint16_t set_count = 0U;
    std::size_t set_offsets = 0U;
    if (format == 1U) {
        set_count = read_u16(table, subtable + 4U);
        set_offsets = subtable + 6U;
    } else {
        const std::uint16_t backtrack_relative =
            read_u16(table, subtable + 4U);
        const std::uint16_t input_relative =
            read_u16(table, subtable + 6U);
        const std::uint16_t lookahead_relative =
            read_u16(table, subtable + 8U);
        std::size_t backtrack_offset = 0U;
        std::size_t input_offset = 0U;
        std::size_t lookahead_offset = 0U;
        if (backtrack_relative == 0U || input_relative == 0U ||
            lookahead_relative == 0U ||
            !try_add(subtable, backtrack_relative, backtrack_offset) ||
            !try_add(subtable, input_relative, input_offset) ||
            !try_add(subtable, lookahead_relative, lookahead_offset) ||
            !open_type_class_definition_view::try_create(
                table, backtrack_offset, backtrack_classes) ||
            !open_type_class_definition_view::try_create(
                table, input_offset, input_classes) ||
            !open_type_class_definition_view::try_create(
                table, lookahead_offset, lookahead_classes)) {
            return apply_result::malformed;
        }
        set_count = read_u16(table, subtable + 10U);
        set_offsets = subtable + 12U;
        set_index = input_classes.get(
            static_cast<std::uint16_t>(glyphs[position].glyph_id));
    }
    if (!can_read(table, set_offsets,
            static_cast<std::size_t>(set_count) * 2U)) {
        return apply_result::malformed;
    }
    if (set_index >= set_count) {
        return apply_result::no_match;
    }
    const std::uint16_t set_relative =
        read_u16(table, set_offsets + set_index * 2U);
    if (set_relative == 0U) {
        return apply_result::no_match;
    }
    std::size_t set = 0U;
    if (!try_add(subtable, set_relative, set) || !can_read(table, set, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t rule_count = read_u16(table, set);
    if (!can_read(table, set + 2U,
            static_cast<std::size_t>(rule_count) * 2U)) {
        return apply_result::malformed;
    }

    for (std::uint16_t rule_index = 0U;
         rule_index < rule_count;
         ++rule_index) {
        const std::uint16_t rule_relative =
            read_u16(table, set + 2U + rule_index * 2U);
        std::size_t rule = 0U;
        if (rule_relative == 0U || !try_add(set, rule_relative, rule) ||
            !can_read(table, rule, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t backtrack_count = read_u16(table, rule);
        std::size_t cursor = rule + 2U;
        if (!can_read(table, cursor,
                static_cast<std::size_t>(backtrack_count) * 2U)) {
            return apply_result::malformed;
        }
        const std::size_t backtrack_values = cursor;
        cursor += static_cast<std::size_t>(backtrack_count) * 2U;
        if (!can_read(table, cursor, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t input_count = read_u16(table, cursor);
        cursor += 2U;
        if (input_count == 0U ||
            !can_read(table, cursor,
                static_cast<std::size_t>(input_count - 1U) * 2U)) {
            return apply_result::malformed;
        }
        const std::size_t input_values = cursor;
        cursor += static_cast<std::size_t>(input_count - 1U) * 2U;
        if (!can_read(table, cursor, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t lookahead_count = read_u16(table, cursor);
        cursor += 2U;
        if (!can_read(table, cursor,
                static_cast<std::size_t>(lookahead_count) * 2U)) {
            return apply_result::malformed;
        }
        const std::size_t lookahead_values = cursor;
        cursor += static_cast<std::size_t>(lookahead_count) * 2U;
        if (!can_read(table, cursor, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t record_count = read_u16(table, cursor);
        cursor += 2U;
        if (!can_read(table, cursor,
                static_cast<std::size_t>(record_count) * 4U)) {
            return apply_result::malformed;
        }

        bool matches = true;
        std::size_t match = position;
        std::size_t match_start = position;
        for (std::uint16_t index = 0U; index < backtrack_count; ++index) {
            match = previous_eligible(
                glyphs,
                match,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= glyphs.size()) {
                matches = false;
                break;
            }
            match_start = match;
            const std::uint16_t expected =
                read_u16(table, backtrack_values + index * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(glyphs[match].glyph_id)
                : backtrack_classes.get(
                    static_cast<std::uint16_t>(glyphs[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        match = position;
        for (std::uint16_t index = 1U; index < input_count; ++index) {
            match = next_eligible(
                glyphs,
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= glyphs.size()) {
                matches = false;
                break;
            }
            const std::uint16_t expected =
                read_u16(table, input_values + (index - 1U) * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(glyphs[match].glyph_id)
                : input_classes.get(
                    static_cast<std::uint16_t>(glyphs[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        const std::size_t input_end = match;
        std::size_t match_end = input_end;
        for (std::uint16_t index = 0U; index < lookahead_count; ++index) {
            match = next_eligible(
                glyphs,
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= glyphs.size()) {
                matches = false;
                break;
            }
            match_end = match;
            const std::uint16_t expected =
                read_u16(table, lookahead_values + index * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(glyphs[match].glyph_id)
                : lookahead_classes.get(
                    static_cast<std::uint16_t>(glyphs[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        mark_unsafe(glyphs, match_start, match_end);
        return apply_position_records(
            gpos,
            table,
            cursor,
            record_count,
            input_count,
            glyphs,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    return apply_result::no_match;
}

apply_result apply_chain_context_format3(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    if (!can_read(table, subtable, 4U) || read_u16(table, subtable) != 3U) {
        return apply_result::malformed;
    }
    const std::uint16_t backtrack_count = read_u16(table, subtable + 2U);
    std::size_t cursor = subtable + 4U;
    if (!can_read(table, cursor,
            static_cast<std::size_t>(backtrack_count) * 2U)) {
        return apply_result::malformed;
    }
    const std::size_t backtrack_coverages = cursor;
    cursor += static_cast<std::size_t>(backtrack_count) * 2U;
    if (!can_read(table, cursor, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t input_count = read_u16(table, cursor);
    cursor += 2U;
    if (input_count == 0U ||
        !can_read(table, cursor, static_cast<std::size_t>(input_count) * 2U)) {
        return apply_result::malformed;
    }
    const std::size_t input_coverages = cursor;
    cursor += static_cast<std::size_t>(input_count) * 2U;
    if (!can_read(table, cursor, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t lookahead_count = read_u16(table, cursor);
    cursor += 2U;
    if (!can_read(table, cursor,
            static_cast<std::size_t>(lookahead_count) * 2U)) {
        return apply_result::malformed;
    }
    const std::size_t lookahead_coverages = cursor;
    cursor += static_cast<std::size_t>(lookahead_count) * 2U;
    if (!can_read(table, cursor, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t record_count = read_u16(table, cursor);
    cursor += 2U;
    if (!can_read(table, cursor,
            static_cast<std::size_t>(record_count) * 4U)) {
        return apply_result::malformed;
    }

    std::size_t match = position;
    std::size_t match_start = position;
    for (std::uint16_t index = 0U; index < backtrack_count; ++index) {
        match = previous_eligible(
            glyphs,
            match,
            lookup_flags,
            mark_filtering_set,
            options.gdef);
        if (match >= glyphs.size()) {
            return apply_result::no_match;
        }
        match_start = match;
        bool matches = false;
        const apply_result coverage_result = match_coverage(
            table,
            subtable,
            read_u16(table, backtrack_coverages + index * 2U),
            glyphs[match].glyph_id,
            matches);
        if (coverage_result == apply_result::malformed) {
            return coverage_result;
        }
        if (!matches) {
            return apply_result::no_match;
        }
    }

    match = position;
    for (std::uint16_t index = 0U; index < input_count; ++index) {
        if (index != 0U) {
            match = next_eligible(
                glyphs,
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= glyphs.size()) {
                return apply_result::no_match;
            }
        }
        bool matches = false;
        const apply_result coverage_result = match_coverage(
            table,
            subtable,
            read_u16(table, input_coverages + index * 2U),
            glyphs[match].glyph_id,
            matches);
        if (coverage_result == apply_result::malformed) {
            return coverage_result;
        }
        if (!matches) {
            return apply_result::no_match;
        }
    }
    const std::size_t input_end = match;
    std::size_t match_end = input_end;
    for (std::uint16_t index = 0U; index < lookahead_count; ++index) {
        match = next_eligible(
            glyphs,
            match + 1U,
            lookup_flags,
            mark_filtering_set,
            options.gdef);
        if (match >= glyphs.size()) {
            return apply_result::no_match;
        }
        match_end = match;
        bool matches = false;
        const apply_result coverage_result = match_coverage(
            table,
            subtable,
            read_u16(table, lookahead_coverages + index * 2U),
            glyphs[match].glyph_id,
            matches);
        if (coverage_result == apply_result::malformed) {
            return coverage_result;
        }
        if (!matches) {
            return apply_result::no_match;
        }
    }
    mark_unsafe(glyphs, match_start, match_end);
    return apply_position_records(
        gpos,
        table,
        cursor,
        record_count,
        input_count,
        glyphs,
        position,
        lookup_flags,
        mark_filtering_set,
        options,
        depth);
}

} // namespace

detail::gpos_apply_result detail::apply_gpos_context_subtable(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept {
    if (!can_read(table, subtable, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t format = read_u16(table, subtable);
    if (type == 7U) {
        if (format == 1U || format == 2U) {
            return apply_context_format1_or2(
                gpos, table, subtable, format, glyphs, position,
                lookup_flags, mark_filtering_set, options, depth);
        }
        return format == 3U
            ? apply_context_format3(
                gpos, table, subtable, glyphs, position,
                lookup_flags, mark_filtering_set, options, depth)
            : apply_result::malformed;
    }
    if (type == 8U) {
        if (format == 1U || format == 2U) {
            return apply_chain_context_format1_or2(
                gpos, table, subtable, format, glyphs, position,
                lookup_flags, mark_filtering_set, options, depth);
        }
        return format == 3U
            ? apply_chain_context_format3(
                gpos, table, subtable, glyphs, position,
                lookup_flags, mark_filtering_set, options, depth)
            : apply_result::malformed;
    }
    return apply_result::no_match;
}

} // namespace progpu::native::text
