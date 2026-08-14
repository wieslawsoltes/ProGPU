#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned raw GSUB lookup execution in
// OpenTypeTextShaper.cs at checkpoint ecb5b2cd. Font bytes remain borrowed;
// mutable glyph capacity and ownership stay with the caller.

namespace progpu::native::text {
namespace {

enum class apply_result : std::uint8_t {
    no_match,
    applied,
    malformed,
    insufficient_buffer
};

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

std::int16_t read_i16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::int16_t>(read_u16(bytes, offset));
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(read_u16(bytes, offset)) << 16U) |
        read_u16(bytes, offset + 2U);
}

bool is_eligible(
    const shaping_glyph& glyph,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    if (gdef == nullptr || glyph.glyph_id > 0xFFFFU) {
        return glyph.glyph_id <= 0xFFFFU;
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
    const std::uint16_t attachment_type =
        static_cast<std::uint16_t>(flags >> 8U);
    return attachment_type == 0U ||
        gdef->mark_attachment_class(
            static_cast<std::uint16_t>(glyph.glyph_id)) == attachment_type;
}

std::uint32_t next_eligible(
    std::span<const shaping_glyph> glyphs,
    std::uint32_t start,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    for (std::uint32_t index = start; index < glyphs.size(); ++index) {
        if (is_eligible(glyphs[index], flags, mark_filtering_set, gdef)) {
            return index;
        }
    }
    return static_cast<std::uint32_t>(glyphs.size());
}

std::uint32_t previous_eligible(
    std::span<const shaping_glyph> glyphs,
    std::uint32_t before,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    while (before != 0U) {
        --before;
        if (is_eligible(glyphs[before], flags, mark_filtering_set, gdef)) {
            return before;
        }
    }
    return static_cast<std::uint32_t>(glyphs.size());
}

apply_result match_coverage_at(
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
    return apply_result::no_match;
}

apply_result replace_multiple(
    std::span<const std::byte> table,
    std::size_t sequence,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position) noexcept {
    if (!can_read(table, sequence, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t replacement_count = read_u16(table, sequence);
    if (!can_read(table, sequence + 2U,
            static_cast<std::size_t>(replacement_count) * 2U)) {
        return apply_result::malformed;
    }
    const std::uint64_t next_count =
        static_cast<std::uint64_t>(count) - 1U + replacement_count;
    if (next_count > storage.size()) {
        return apply_result::insufficient_buffer;
    }
    const shaping_glyph original = storage[position];
    if (replacement_count > 1U) {
        const std::uint32_t growth = replacement_count - 1U;
        for (std::uint32_t read = count; read > position + 1U; --read) {
            storage[read - 1U + growth] = storage[read - 1U];
        }
    } else if (replacement_count == 0U) {
        for (std::uint32_t read = position + 1U; read < count; ++read) {
            storage[read - 1U] = storage[read];
        }
    }
    for (std::uint16_t index = 0U; index < replacement_count; ++index) {
        shaping_glyph replacement = original;
        replacement.glyph_id = read_u16(table, sequence + 2U + index * 2U);
        replacement.flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(replacement.flags) |
            static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
        storage[position + index] = replacement;
    }
    count = static_cast<std::uint32_t>(next_count);
    return apply_result::applied;
}

apply_result replace_ligature(
    std::span<const std::byte> table,
    std::size_t ligature,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef) noexcept {
    if (!can_read(table, ligature, 4U)) {
        return apply_result::malformed;
    }
    const std::uint16_t ligature_glyph = read_u16(table, ligature);
    const std::uint16_t component_count = read_u16(table, ligature + 2U);
    if (component_count == 0U ||
        !can_read(table, ligature + 4U,
            static_cast<std::size_t>(component_count - 1U) * 2U)) {
        return apply_result::malformed;
    }
    std::uint32_t candidate = position;
    for (std::uint16_t component = 1U;
         component < component_count;
         ++component) {
        candidate = next_eligible(
            std::span<const shaping_glyph>{storage.data(), count},
            candidate + 1U,
            lookup_flags,
            mark_filtering_set,
            gdef);
        if (candidate >= count || storage[candidate].glyph_id !=
                read_u16(table, ligature + 4U + (component - 1U) * 2U)) {
            return apply_result::no_match;
        }
    }

    shaping_glyph& first = storage[position];
    first.glyph_id = ligature_glyph;
    first.flags = static_cast<shaping_glyph_flags>(
        static_cast<std::uint32_t>(first.flags) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
    std::uint32_t write = position + 1U;
    std::uint16_t component = 1U;
    for (std::uint32_t read = position + 1U; read < count; ++read) {
        const bool remove = component < component_count &&
            is_eligible(
                storage[read], lookup_flags, mark_filtering_set, gdef) &&
            storage[read].glyph_id == read_u16(
                table, ligature + 4U + (component - 1U) * 2U);
        if (remove) {
            ++component;
        } else {
            storage[write++] = storage[read];
        }
    }
    count = write;
    return apply_result::applied;
}

apply_result apply_subtable(
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options) noexcept {
    if (type == 7U) {
        if (!can_read(table, subtable, 8U) || read_u16(table, subtable) != 1U) {
            return apply_result::malformed;
        }
        const std::uint16_t extension_type = read_u16(table, subtable + 2U);
        const std::uint32_t relative = read_u32(table, subtable + 4U);
        std::size_t extension = 0U;
        if (extension_type == 7U || relative == 0U ||
            !try_add(subtable, relative, extension) ||
            !can_read(table, extension, 2U)) {
            return apply_result::malformed;
        }
        return apply_subtable(
            table,
            extension_type,
            extension,
            storage,
            count,
            position,
            lookup_flags,
            mark_filtering_set,
            options);
    }
    if (type == 8U) {
        if (!can_read(table, subtable, 6U) ||
            read_u16(table, subtable) != 1U) {
            return apply_result::malformed;
        }
        const std::uint16_t coverage_relative = read_u16(table, subtable + 2U);
        std::size_t coverage_offset = 0U;
        open_type_coverage_view coverage{};
        if (coverage_relative == 0U ||
            !try_add(subtable, coverage_relative, coverage_offset) ||
            !open_type_coverage_view::try_create(
                table, coverage_offset, coverage)) {
            return apply_result::malformed;
        }
        const std::int32_t coverage_index = coverage.find(
            static_cast<std::uint16_t>(storage[position].glyph_id));
        if (coverage_index < 0) {
            return apply_result::no_match;
        }
        const std::uint16_t backtrack_count = read_u16(table, subtable + 4U);
        const std::size_t backtrack_offsets = subtable + 6U;
        if (!can_read(table, backtrack_offsets,
                static_cast<std::size_t>(backtrack_count) * 2U)) {
            return apply_result::malformed;
        }
        std::size_t cursor =
            backtrack_offsets + static_cast<std::size_t>(backtrack_count) * 2U;
        if (!can_read(table, cursor, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t lookahead_count = read_u16(table, cursor);
        cursor += 2U;
        const std::size_t lookahead_offsets = cursor;
        if (!can_read(table, lookahead_offsets,
                static_cast<std::size_t>(lookahead_count) * 2U)) {
            return apply_result::malformed;
        }
        cursor += static_cast<std::size_t>(lookahead_count) * 2U;
        if (!can_read(table, cursor, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t substitute_count = read_u16(table, cursor);
        cursor += 2U;
        if (static_cast<std::uint32_t>(coverage_index) >= substitute_count ||
            !can_read(table, cursor,
                static_cast<std::size_t>(substitute_count) * 2U)) {
            return apply_result::malformed;
        }

        std::uint32_t match = position;
        for (std::uint16_t index = 0U; index < backtrack_count; ++index) {
            match = previous_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= count) {
                return apply_result::no_match;
            }
            bool matches = false;
            const apply_result coverage_result = match_coverage_at(
                table,
                subtable,
                read_u16(table, backtrack_offsets + index * 2U),
                storage[match].glyph_id,
                matches);
            if (coverage_result == apply_result::malformed) {
                return coverage_result;
            }
            if (!matches) {
                return apply_result::no_match;
            }
        }
        match = position;
        for (std::uint16_t index = 0U; index < lookahead_count; ++index) {
            match = next_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (match >= count) {
                return apply_result::no_match;
            }
            bool matches = false;
            const apply_result coverage_result = match_coverage_at(
                table,
                subtable,
                read_u16(table, lookahead_offsets + index * 2U),
                storage[match].glyph_id,
                matches);
            if (coverage_result == apply_result::malformed) {
                return coverage_result;
            }
            if (!matches) {
                return apply_result::no_match;
            }
        }
        storage[position].glyph_id = read_u16(
            table, cursor + static_cast<std::size_t>(coverage_index) * 2U);
        storage[position].flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(storage[position].flags) |
            static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
        return apply_result::applied;
    }
    if (type < 1U || type > 4U || !can_read(table, subtable, 6U)) {
        return apply_result::malformed;
    }
    const std::uint16_t format = read_u16(table, subtable);
    const std::size_t coverage_relative = read_u16(table, subtable + 2U);
    std::size_t coverage_offset = 0U;
    open_type_coverage_view coverage{};
    if (coverage_relative == 0U ||
        !try_add(subtable, coverage_relative, coverage_offset) ||
        !open_type_coverage_view::try_create(
            table, coverage_offset, coverage)) {
        return apply_result::malformed;
    }
    const std::int32_t coverage_index = coverage.find(
        static_cast<std::uint16_t>(storage[position].glyph_id));
    if (coverage_index < 0) {
        return apply_result::no_match;
    }

    if (type == 1U && format == 1U) {
        const std::int32_t replacement =
            static_cast<std::int32_t>(storage[position].glyph_id) +
            read_i16(table, subtable + 4U);
        storage[position].glyph_id = static_cast<std::uint16_t>(replacement);
        storage[position].flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(storage[position].flags) |
            static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
        return apply_result::applied;
    }
    if (type == 1U && format == 2U) {
        const std::uint16_t substitute_count = read_u16(table, subtable + 4U);
        if (static_cast<std::uint32_t>(coverage_index) >= substitute_count ||
            !can_read(table, subtable + 6U,
                static_cast<std::size_t>(substitute_count) * 2U)) {
            return apply_result::malformed;
        }
        storage[position].glyph_id = read_u16(
            table, subtable + 6U + coverage_index * 2U);
        storage[position].flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(storage[position].flags) |
            static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
        return apply_result::applied;
    }
    if ((type == 2U || type == 3U || type == 4U) && format == 1U) {
        const std::uint16_t set_count = read_u16(table, subtable + 4U);
        if (static_cast<std::uint32_t>(coverage_index) >= set_count ||
            !can_read(table, subtable + 6U,
                static_cast<std::size_t>(set_count) * 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t relative = read_u16(
            table, subtable + 6U + coverage_index * 2U);
        std::size_t set = 0U;
        if (relative == 0U || !try_add(subtable, relative, set)) {
            return apply_result::malformed;
        }
        if (type == 2U) {
            return replace_multiple(table, set, storage, count, position);
        }
        if (!can_read(table, set, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t member_count = read_u16(table, set);
        if (!can_read(table, set + 2U,
                static_cast<std::size_t>(member_count) * 2U)) {
            return apply_result::malformed;
        }
        if (type == 3U) {
            if (member_count == 0U || options.alternate_value == 0U) {
                return apply_result::no_match;
            }
            const std::uint32_t selected =
                std::min(options.alternate_value, static_cast<std::uint32_t>(member_count)) - 1U;
            storage[position].glyph_id = read_u16(table, set + 2U + selected * 2U);
            storage[position].flags = static_cast<shaping_glyph_flags>(
                static_cast<std::uint32_t>(storage[position].flags) |
                static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break));
            return apply_result::applied;
        }
        for (std::uint16_t index = 0U; index < member_count; ++index) {
            const std::uint16_t ligature_relative =
                read_u16(table, set + 2U + index * 2U);
            std::size_t ligature = 0U;
            if (ligature_relative == 0U ||
                !try_add(set, ligature_relative, ligature)) {
                return apply_result::malformed;
            }
            const apply_result result = replace_ligature(
                table,
                ligature,
                storage,
                count,
                position,
                lookup_flags,
                mark_filtering_set,
                options.gdef);
            if (result != apply_result::no_match) {
                return result;
            }
        }
        return apply_result::no_match;
    }
    return apply_result::malformed;
}

bool is_reverse_lookup(const open_type_lookup_view& lookup) noexcept {
    if (lookup.type == 8U) {
        return true;
    }
    if (lookup.type != 7U || lookup.subtable_count == 0U) {
        return false;
    }
    std::size_t subtable = 0U;
    return lookup.try_get_subtable(0U, subtable) &&
        can_read(lookup.table, subtable, 4U) &&
        read_u16(lookup.table, subtable) == 1U &&
        read_u16(lookup.table, subtable + 2U) == 8U;
}

} // namespace

bool try_apply_open_type_gsub_lookup(
    const open_type_layout_table_view& gsub,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gsub_apply_options& options,
    bool& applied,
    font_error* error) noexcept {
    applied = false;
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    open_type_lookup_view lookup{};
    if (!gsub.try_get_lookup(lookup_index, lookup, error)) {
        return false;
    }
    if (lookup.type < 1U ||
        (lookup.type > 4U && lookup.type != 7U && lookup.type != 8U)) {
        set_error(error, font_error::none);
        return true;
    }

    const bool reverse = is_reverse_lookup(lookup);
    std::uint32_t iteration = 0U;
    while (iteration < glyph_count) {
        const std::uint32_t position = reverse
            ? glyph_count - iteration - 1U
            : iteration;
        ++iteration;
        if (!is_eligible(
                glyph_storage[position],
                lookup.flags,
                lookup.mark_filtering_set,
                options.gdef)) {
            continue;
        }
        const std::uint32_t count_before = glyph_count;
        for (std::uint16_t subtable_index = 0U;
             subtable_index < lookup.subtable_count;
             ++subtable_index) {
            std::size_t subtable = 0U;
            if (!lookup.try_get_subtable(subtable_index, subtable, error)) {
                return false;
            }
            const apply_result result = apply_subtable(
                lookup.table,
                lookup.type,
                subtable,
                glyph_storage,
                glyph_count,
                position,
                lookup.flags,
                lookup.mark_filtering_set,
                options);
            if (result == apply_result::malformed) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (result == apply_result::insufficient_buffer) {
                set_error(error, font_error::insufficient_buffer);
                return false;
            }
            if (result == apply_result::applied) {
                applied = true;
                if (glyph_count > count_before) {
                    iteration += glyph_count - count_before;
                }
                break;
            }
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
