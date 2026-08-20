#include "progpu_native_text.hpp"

#include "progpu_native_open_type_gsub_internal.hpp"

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

constexpr std::uint32_t maximum_lookup_nesting_depth = 64U;
constexpr std::uint32_t complex_syllable_mask = 0x001FE000U;
constexpr std::uint32_t complex_syllable_shift = 13U;
constexpr std::uint32_t complex_multiplied_mask = 0x20000000U;
constexpr std::uint32_t complex_ligated_mask = 0x40000000U;
constexpr std::uint32_t complex_substituted_mask = 0x80000000U;
constexpr std::int32_t substituted_advance_sentinel =
    std::numeric_limits<std::int32_t>::min();

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

void mark_gsub_dependency(
    std::span<shaping_glyph> glyphs,
    std::uint32_t first,
    std::uint32_t last) noexcept {
    if (first >= glyphs.size() || last >= glyphs.size() || first >= last) {
        return;
    }
    std::int32_t minimum_cluster = glyphs[first].cluster;
    for (std::uint32_t index = first + 1U; index <= last; ++index) {
        minimum_cluster = std::min(minimum_cluster, glyphs[index].cluster);
    }
    constexpr auto dependency_flags =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    for (std::uint32_t index = first; index <= last; ++index) {
        if (glyphs[index].cluster != minimum_cluster) {
            glyphs[index].flags = static_cast<shaping_glyph_flags>(
                static_cast<std::uint32_t>(glyphs[index].flags) |
                dependency_flags);
        }
    }
}

void record_context_match(
    const open_type_gsub_apply_options& options,
    std::uint32_t exclusive_end) noexcept {
    if (options.context_match_end != nullptr &&
        exclusive_end > *options.context_match_end) {
        *options.context_match_end = exclusive_end;
    }
}

std::uint32_t adjust_context_match_end_after_substitution(
    std::uint32_t exclusive_end,
    std::uint32_t count_before,
    std::uint32_t count_after) noexcept {
    if (exclusive_end == 0U || count_before == count_after) {
        return std::min(exclusive_end, count_after);
    }

    // Context match boundaries are captured before nested substitutions run.
    // Translate that boundary into the mutated glyph buffer so a contracting
    // lookup does not skip the next candidate and an expanding lookup does not
    // feed newly inserted output back through the same top-level lookup.
    const auto adjusted = static_cast<std::int64_t>(exclusive_end) +
        static_cast<std::int64_t>(count_after) -
        static_cast<std::int64_t>(count_before);
    return static_cast<std::uint32_t>(std::clamp<std::int64_t>(
        adjusted,
        0,
        static_cast<std::int64_t>(count_after)));
}

void mark_substitution(
    shaping_glyph& glyph,
    bool enabled,
    bool track_provenance,
    bool ligated = false,
    bool multiplied = false) noexcept {
    if (track_provenance) {
        glyph.advance_x = substituted_advance_sentinel;
    }
    if (enabled) {
        auto flags = static_cast<std::uint32_t>(glyph.flags) |
            complex_substituted_mask;
        if (ligated) {
            flags |= complex_ligated_mask;
        }
        if (multiplied) {
            flags |= complex_multiplied_mask;
        }
        glyph.flags = static_cast<shaping_glyph_flags>(flags);
    }
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
    if (glyph.glyph_id > 0xFFFFU) {
        return false;
    }
    // Port of GlyphSubstitutionBuffer.IsGlyphClassIgnored's Latin hot path.
    // RightToLeft alone cannot filter a glyph, and the common zero-filter
    // lookup must not resolve GDEF or Unicode classes per glyph and lookup.
    if ((flags & 0xFF1EU) == 0U) {
        return true;
    }
    auto glyph_class = gdef == nullptr
        ? open_type_glyph_class::unclassified
        : gdef->glyph_class(static_cast<std::uint16_t>(glyph.glyph_id));
    if (glyph_class == open_type_glyph_class::unclassified) {
        glyph_class = is_unicode_mark(glyph.code_point)
            ? open_type_glyph_class::mark
            : open_type_glyph_class::base;
    }
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
        return gdef != nullptr && mark_filtering_set != 0xFFFFU &&
            gdef->is_in_mark_set(
                mark_filtering_set,
                static_cast<std::uint16_t>(glyph.glyph_id));
    }
    const std::uint16_t attachment_type =
        static_cast<std::uint16_t>(flags >> 8U);
    return attachment_type == 0U ||
        (gdef != nullptr && gdef->mark_attachment_class(
            static_cast<std::uint16_t>(glyph.glyph_id)) == attachment_type);
}

bool context_subtable_may_start(
    const open_type_gsub_apply_options& options,
    std::uint16_t subtable_index,
    std::uint16_t subtable_count,
    std::uint32_t glyph_id) noexcept {
    if (glyph_id > 0xFFFFU ||
        options.lookup_context_subtables.size() != subtable_count) {
        return true;
    }
    const auto& subtable =
        options.lookup_context_subtables[subtable_index];
    const std::uint64_t first_input =
        static_cast<std::uint64_t>(subtable.coverage_offset) +
        subtable.backtrack_count;
    if (subtable.input_count == 0U ||
        first_input >= options.lookup_context_coverages.size()) {
        return true;
    }
    const auto& coverage = options.lookup_context_coverages[
        static_cast<std::size_t>(first_input)];
    const auto glyph = static_cast<std::uint16_t>(glyph_id);
    return coverage.digest.may_have(glyph) &&
        coverage.coverage.find(glyph) >= 0;
}

std::uint32_t next_eligible(
    std::span<const shaping_glyph> glyphs,
    std::uint32_t start,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef,
    std::uint8_t restricted_syllable = 0U) noexcept {
    for (std::uint32_t index = start; index < glyphs.size(); ++index) {
        if (restricted_syllable != 0U &&
            ((static_cast<std::uint32_t>(glyphs[index].flags) &
                complex_syllable_mask) >> complex_syllable_shift) !=
                restricted_syllable) {
            return static_cast<std::uint32_t>(glyphs.size());
        }
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
    const open_type_gdef_view* gdef,
    std::uint8_t restricted_syllable = 0U) noexcept {
    while (before != 0U) {
        --before;
        if (restricted_syllable != 0U &&
            ((static_cast<std::uint32_t>(glyphs[before].flags) &
                complex_syllable_mask) >> complex_syllable_shift) !=
                restricted_syllable) {
            return static_cast<std::uint32_t>(glyphs.size());
        }
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
    if (glyph_id > 0xFFFFU || relative == 0U ||
        !try_add(parent, relative, offset) || !can_read(table, offset, 4U)) {
        return apply_result::malformed;
    }

    // Port of ProGPU-owned OpenTypeTextShaper.FindCoverage: these lookup
    // tables were validated when the retained layout/shape plan was built.
    // Revalidating every sorted coverage record for every candidate glyph
    // changes contextual shaping from bounded binary search to O(G * C).
    // Keep local bounds/format validation and search the retained bytes
    // directly in O(log C), while the public coverage parser stays strict.
    const std::uint16_t format = read_u16(table, offset);
    const std::uint16_t count = read_u16(table, offset + 2U);
    const std::size_t stride = format == 1U ? 2U : format == 2U ? 6U : 0U;
    if (stride == 0U || count >
            (table.size() - std::min(table.size(), offset + 4U)) / stride) {
        return apply_result::malformed;
    }
    const std::uint16_t glyph = static_cast<std::uint16_t>(glyph_id);
    std::uint32_t low = 0U;
    std::uint32_t high = count;
    while (low < high) {
        const std::uint32_t middle = low + (high - low) / 2U;
        const std::size_t record = offset + 4U + middle * stride;
        const std::uint16_t start = read_u16(table, record);
        const std::uint16_t end = format == 1U
            ? start
            : read_u16(table, record + 2U);
        if (glyph < start) {
            high = middle;
        } else if (glyph > end) {
            low = middle + 1U;
        } else {
            matches = true;
            break;
        }
    }
    return apply_result::no_match;
}

apply_result replace_multiple(
    std::span<const std::byte> table,
    std::size_t sequence,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    bool track_arabic_stretch_metadata,
    bool mark_substituted,
    bool track_substitution_provenance) noexcept {
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
        mark_substitution(
            replacement,
            mark_substituted,
            track_substitution_provenance,
            false,
            true);
        if (track_arabic_stretch_metadata) {
            detail::set_arabic_stretch_component(replacement, index);
        }
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
    const open_type_gdef_view* gdef,
    bool track_fallback_mark_metadata,
    std::uint8_t restricted_syllable,
    bool mark_substituted,
    bool track_substitution_provenance) noexcept {
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
            gdef,
            restricted_syllable);
        if (candidate >= count || storage[candidate].glyph_id !=
                read_u16(table, ligature + 4U + (component - 1U) * 2U)) {
            return apply_result::no_match;
        }
    }

    // Directly preserve GlyphSubstitutionBuffer.ReplaceLigature's cluster
    // contract: include skipped components and the complete adjacent source
    // clusters at both ends before removing matched glyphs. This is required
    // for Indic marks that retain the last component's original cluster.
    std::uint32_t merge_start = position;
    std::uint32_t merge_end = candidate + 1U;
    const auto first_cluster = storage[merge_start].cluster;
    const auto last_cluster = storage[merge_end - 1U].cluster;
    while (merge_start > 0U &&
        storage[merge_start - 1U].cluster == first_cluster) {
        --merge_start;
    }
    while (merge_end < count &&
        storage[merge_end].cluster == last_cluster) {
        ++merge_end;
    }
    auto merged_cluster = std::numeric_limits<std::int32_t>::max();
    for (std::uint32_t index = merge_start; index < merge_end; ++index) {
        merged_cluster = std::min(merged_cluster, storage[index].cluster);
    }
    for (std::uint32_t index = merge_start; index < merge_end; ++index) {
        storage[index].cluster = merged_cluster;
    }

    shaping_glyph& first = storage[position];
    first.glyph_id = ligature_glyph;
    mark_substitution(
        first,
        mark_substituted,
        track_substitution_provenance,
        component_count > 1U,
        false);
    if (track_fallback_mark_metadata) {
        detail::set_fallback_ligature_count(first, component_count);
    }
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
            if (track_fallback_mark_metadata && component < component_count) {
                detail::set_fallback_ligature_component(
                    storage[read],
                    static_cast<std::uint16_t>(component - 1U));
            }
            storage[write++] = storage[read];
        }
    }
    count = write;
    return apply_result::applied;
}

apply_result apply_lookup_at(
    const open_type_layout_table_view& gsub,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    const open_type_gsub_apply_options& options,
    std::uint32_t depth) noexcept;

std::uint32_t eligible_sequence_position(
    std::span<const shaping_glyph> glyphs,
    std::uint32_t first,
    std::uint16_t sequence_index,
    std::uint16_t flags,
    std::uint16_t mark_filtering_set,
    const open_type_gdef_view* gdef,
    std::uint8_t restricted_syllable) noexcept {
    std::uint32_t result = first;
    for (std::uint16_t index = 0U; index < sequence_index; ++index) {
        result = next_eligible(
            glyphs,
            result + 1U,
            flags,
            mark_filtering_set,
            gdef,
            restricted_syllable);
        if (result >= glyphs.size()) {
            return static_cast<std::uint32_t>(glyphs.size());
        }
    }
    return result;
}

apply_result apply_context_records(
    const open_type_layout_table_view& gsub,
    std::span<const std::byte> table,
    std::size_t records,
    std::uint16_t record_count,
    std::uint16_t input_count,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options,
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
        const std::uint32_t target = eligible_sequence_position(
            std::span<const shaping_glyph>{storage.data(), count},
            position,
            sequence_index,
            lookup_flags,
            mark_filtering_set,
            options.gdef,
            options.restricted_syllable);
        if (target >= count) {
            return apply_result::no_match;
        }
        const apply_result nested = apply_lookup_at(
            gsub,
            nested_lookup,
            storage,
            count,
            target,
            options,
            depth + 1U);
        if (nested == apply_result::malformed ||
            nested == apply_result::insufficient_buffer) {
            return nested;
        }
        changed |= nested == apply_result::applied;
    }
    return changed ? apply_result::applied : apply_result::no_match;
}

apply_result apply_context_format3(
    const open_type_layout_table_view& gsub,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options,
    std::uint32_t depth) noexcept {
    if (!can_read(table, subtable, 6U)) {
        return apply_result::malformed;
    }
    const std::uint16_t input_count = read_u16(table, subtable + 2U);
    const std::uint16_t record_count = read_u16(table, subtable + 4U);
    if (input_count == 0U ||
        !can_read(table, subtable + 6U,
            static_cast<std::size_t>(input_count) * 2U)) {
        return apply_result::malformed;
    }
    std::uint32_t match = position;
    for (std::uint16_t index = 0U; index < input_count; ++index) {
        if (index != 0U) {
            match = next_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                return apply_result::no_match;
            }
        }
        bool matches = false;
        const apply_result coverage_result = match_coverage_at(
            table,
            subtable,
            read_u16(table, subtable + 6U + index * 2U),
            storage[match].glyph_id,
            matches);
        if (coverage_result == apply_result::malformed) {
            return coverage_result;
        }
        if (!matches) {
            return apply_result::no_match;
        }
    }
    mark_gsub_dependency(storage, position, match);
    record_context_match(options, match + 1U);
    return apply_context_records(
        gsub,
        table,
        subtable + 6U + static_cast<std::size_t>(input_count) * 2U,
        record_count,
        input_count,
        storage,
        count,
        position,
        lookup_flags,
        mark_filtering_set,
        options,
        depth);
}

apply_result apply_context_format1_or2(
    const open_type_layout_table_view& gsub,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::uint16_t format,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options,
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
        static_cast<std::uint16_t>(storage[position].glyph_id));
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
            static_cast<std::uint16_t>(storage[position].glyph_id));
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
                static_cast<std::size_t>(input_count - 1U) * 2U)) {
            return apply_result::malformed;
        }
        std::uint32_t match = position;
        bool matches = true;
        for (std::uint16_t input = 1U; input < input_count; ++input) {
            match = next_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                matches = false;
                break;
            }
            const std::uint16_t expected =
                read_u16(table, rule + 4U + (input - 1U) * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(storage[match].glyph_id)
                : classes.get(static_cast<std::uint16_t>(storage[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        mark_gsub_dependency(storage, position, match);
        record_context_match(options, match + 1U);
        return apply_context_records(
            gsub,
            table,
            rule + 4U + static_cast<std::size_t>(input_count - 1U) * 2U,
            record_count,
            input_count,
            storage,
            count,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    return apply_result::no_match;
}

apply_result apply_chain_context_format3(
    const open_type_layout_table_view& gsub,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options,
    std::uint32_t depth) noexcept {
    if (!can_read(table, subtable, 4U)) {
        return apply_result::malformed;
    }
    const std::uint16_t backtrack_count = read_u16(table, subtable + 2U);
    std::size_t cursor = subtable + 4U;
    if (!can_read(table, cursor,
            static_cast<std::size_t>(backtrack_count) * 2U)) {
        return apply_result::malformed;
    }
    const std::size_t backtrack_offsets = cursor;
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
    const std::size_t input_offsets = cursor;
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
    const std::size_t lookahead_offsets = cursor;
    cursor += static_cast<std::size_t>(lookahead_count) * 2U;
    if (!can_read(table, cursor, 2U)) {
        return apply_result::malformed;
    }
    const std::uint16_t record_count = read_u16(table, cursor);
    cursor += 2U;

    std::uint32_t match = position;
    std::uint32_t match_start = position;
    std::uint32_t match_end = position + 1U;
    for (std::uint16_t index = 0U; index < backtrack_count; ++index) {
        match = previous_eligible(
            std::span<const shaping_glyph>{storage.data(), count},
            match,
            lookup_flags,
            mark_filtering_set,
            options.gdef,
            options.restricted_syllable);
        if (match >= count) {
            return apply_result::no_match;
        }
        match_start = std::min(match_start, match);
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
    for (std::uint16_t index = 0U; index < input_count; ++index) {
        if (index != 0U) {
            match = next_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                return apply_result::no_match;
            }
        }
        bool matches = false;
        const apply_result coverage_result = match_coverage_at(
            table,
            subtable,
            read_u16(table, input_offsets + index * 2U),
            storage[match].glyph_id,
            matches);
        if (coverage_result == apply_result::malformed) {
            return coverage_result;
        }
        if (!matches) {
            return apply_result::no_match;
        }
    }
    const std::uint32_t input_end = match;
    match_end = std::max(match_end, input_end + 1U);
    for (std::uint16_t index = 0U; index < lookahead_count; ++index) {
        match = next_eligible(
            std::span<const shaping_glyph>{storage.data(), count},
            match + 1U,
            lookup_flags,
            mark_filtering_set,
            options.gdef,
            options.restricted_syllable);
        if (match >= count) {
            return apply_result::no_match;
        }
        match_end = std::max(match_end, match + 1U);
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
    mark_gsub_dependency(storage, match_start, match_end - 1U);
    record_context_match(options, input_end + 1U);
    return apply_context_records(
        gsub,
        table,
        cursor,
        record_count,
        input_count,
        storage,
        count,
        position,
        lookup_flags,
        mark_filtering_set,
        options,
        depth);
}

apply_result apply_chain_context_format1_or2(
    const open_type_layout_table_view& gsub,
    std::span<const std::byte> table,
    std::size_t subtable,
    std::uint16_t format,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options,
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
        static_cast<std::uint16_t>(storage[position].glyph_id));
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
        std::size_t backtrack_offset = 0U;
        std::size_t input_offset = 0U;
        std::size_t lookahead_offset = 0U;
        const std::uint16_t backtrack_relative = read_u16(table, subtable + 4U);
        const std::uint16_t input_relative = read_u16(table, subtable + 6U);
        const std::uint16_t lookahead_relative = read_u16(table, subtable + 8U);
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
            static_cast<std::uint16_t>(storage[position].glyph_id));
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

        bool matches = true;
        std::uint32_t match = position;
        std::uint32_t match_start = position;
        std::uint32_t match_end = position + 1U;
        for (std::uint16_t index = 0U; index < backtrack_count; ++index) {
            match = previous_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                matches = false;
                break;
            }
            match_start = std::min(match_start, match);
            const std::uint16_t expected =
                read_u16(table, backtrack_values + index * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(storage[match].glyph_id)
                : backtrack_classes.get(
                    static_cast<std::uint16_t>(storage[match].glyph_id));
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
                std::span<const shaping_glyph>{storage.data(), count},
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                matches = false;
                break;
            }
            const std::uint16_t expected =
                read_u16(table, input_values + (index - 1U) * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(storage[match].glyph_id)
                : input_classes.get(
                    static_cast<std::uint16_t>(storage[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        const std::uint32_t input_end = match;
        match_end = std::max(match_end, input_end + 1U);
        for (std::uint16_t index = 0U; index < lookahead_count; ++index) {
            match = next_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match + 1U,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                matches = false;
                break;
            }
            match_end = std::max(match_end, match + 1U);
            const std::uint16_t expected =
                read_u16(table, lookahead_values + index * 2U);
            const std::uint16_t actual = format == 1U
                ? static_cast<std::uint16_t>(storage[match].glyph_id)
                : lookahead_classes.get(
                    static_cast<std::uint16_t>(storage[match].glyph_id));
            if (actual != expected) {
                matches = false;
                break;
            }
        }
        if (!matches) {
            continue;
        }
        mark_gsub_dependency(storage, match_start, match_end - 1U);
        record_context_match(options, input_end + 1U);
        return apply_context_records(
            gsub,
            table,
            cursor,
            record_count,
            input_count,
            storage,
            count,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    return apply_result::no_match;
}

apply_result apply_subtable(
    const open_type_layout_table_view& gsub,
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gsub_apply_options& options,
    std::uint32_t depth) noexcept {
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
            gsub,
            table,
            extension_type,
            extension,
            storage,
            count,
            position,
            lookup_flags,
            mark_filtering_set,
            options,
            depth);
    }
    if (type == 5U) {
        if (!can_read(table, subtable, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t format = read_u16(table, subtable);
        if (format == 1U || format == 2U) {
            return apply_context_format1_or2(
                gsub,
                table,
                subtable,
                format,
                storage,
                count,
                position,
                lookup_flags,
                mark_filtering_set,
                options,
                depth);
        }
        return format == 3U ? apply_context_format3(
                gsub,
                table,
                subtable,
                storage,
                count,
                position,
                lookup_flags,
                mark_filtering_set,
                options,
                depth) : apply_result::malformed;
    }
    if (type == 6U) {
        if (!can_read(table, subtable, 2U)) {
            return apply_result::malformed;
        }
        const std::uint16_t format = read_u16(table, subtable);
        if (format == 1U || format == 2U) {
            return apply_chain_context_format1_or2(
                gsub,
                table,
                subtable,
                format,
                storage,
                count,
                position,
                lookup_flags,
                mark_filtering_set,
                options,
                depth);
        }
        return format == 3U ? apply_chain_context_format3(
                gsub,
                table,
                subtable,
                storage,
                count,
                position,
                lookup_flags,
                mark_filtering_set,
                options,
                depth) : apply_result::malformed;
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
        std::uint32_t match_start = position;
        std::uint32_t match_end = position + 1U;
        for (std::uint16_t index = 0U; index < backtrack_count; ++index) {
            match = previous_eligible(
                std::span<const shaping_glyph>{storage.data(), count},
                match,
                lookup_flags,
                mark_filtering_set,
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                return apply_result::no_match;
            }
            match_start = std::min(match_start, match);
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
                options.gdef,
                options.restricted_syllable);
            if (match >= count) {
                return apply_result::no_match;
            }
            match_end = std::max(match_end, match + 1U);
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
        mark_substitution(
            storage[position],
            options.mark_substituted,
            options.track_substitution_provenance);
        if (match_end > match_start) {
            mark_gsub_dependency(storage, match_start, match_end - 1U);
        }
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
        mark_substitution(
            storage[position],
            options.mark_substituted,
            options.track_substitution_provenance);
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
        mark_substitution(
            storage[position],
            options.mark_substituted,
            options.track_substitution_provenance);
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
            return replace_multiple(
                table,
                set,
                storage,
                count,
                position,
                options.track_arabic_stretch_metadata,
                options.mark_substituted,
                options.track_substitution_provenance);
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
            std::uint32_t selected =
                std::min(options.alternate_value,
                    static_cast<std::uint32_t>(member_count)) - 1U;
            if (depth == 0U && options.random_alternate &&
                options.random_state != nullptr) {
                mark_gsub_dependency(storage, 0U, count - 1U);
                *options.random_state =
                    (*options.random_state * 48271U) % 2147483647U;
                selected = *options.random_state % member_count;
            }
            storage[position].glyph_id = read_u16(table, set + 2U + selected * 2U);
            mark_substitution(
                storage[position],
                options.mark_substituted,
                options.track_substitution_provenance);
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
                options.gdef,
                options.track_fallback_mark_metadata,
                options.restricted_syllable,
                options.mark_substituted,
                options.track_substitution_provenance);
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

apply_result apply_lookup_at(
    const open_type_layout_table_view& gsub,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::uint32_t position,
    const open_type_gsub_apply_options& options,
    std::uint32_t depth) noexcept {
    if (depth >= maximum_lookup_nesting_depth || position >= count) {
        return apply_result::no_match;
    }
    open_type_lookup_view lookup{};
    if (!gsub.try_get_lookup(lookup_index, lookup)) {
        return apply_result::malformed;
    }
    if (!is_eligible(
            storage[position],
            lookup.flags,
            lookup.mark_filtering_set,
            options.gdef)) {
        return apply_result::no_match;
    }
    for (std::uint16_t index = 0U; index < lookup.subtable_count; ++index) {
        if (depth == 0U && !context_subtable_may_start(
                options,
                index,
                lookup.subtable_count,
                storage[position].glyph_id)) {
            continue;
        }
        std::size_t subtable = 0U;
        if (!lookup.try_get_subtable(index, subtable)) {
            return apply_result::malformed;
        }
        const apply_result result = apply_subtable(
            gsub,
            lookup.table,
            lookup.type,
            subtable,
            storage,
            count,
            position,
            lookup.flags,
            lookup.mark_filtering_set,
            options,
            depth);
        if (result != apply_result::no_match) {
            return result;
        }
    }
    return apply_result::no_match;
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
        (lookup.type > 8U || lookup.type == 0U)) {
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
        if (options.lookup_digest != nullptr &&
            glyph_storage[position].glyph_id <= 0xFFFFU &&
            !options.lookup_digest->may_have(static_cast<std::uint16_t>(
                glyph_storage[position].glyph_id))) {
            continue;
        }
        if (options.lookup_coverage != nullptr &&
            glyph_storage[position].glyph_id <= 0xFFFFU &&
            options.lookup_coverage->find(static_cast<std::uint16_t>(
                glyph_storage[position].glyph_id)) < 0) {
            continue;
        }
        if (options.required_glyph_flags != 0U &&
            (static_cast<std::uint32_t>(glyph_storage[position].flags) &
                options.required_glyph_flags) !=
                options.required_glyph_flags) {
            continue;
        }
        if (!is_eligible(
                glyph_storage[position],
                lookup.flags,
                lookup.mark_filtering_set,
                options.gdef)) {
            continue;
        }
        std::uint32_t context_match_end = 0U;
        open_type_gsub_apply_options effective_options = options;
        effective_options.context_match_end = &context_match_end;
        if (effective_options.restrict_to_syllable) {
            effective_options.restricted_syllable =
                static_cast<std::uint8_t>(
                    (static_cast<std::uint32_t>(
                        glyph_storage[position].flags) &
                        complex_syllable_mask) >>
                    complex_syllable_shift);
        }
        const std::uint32_t count_before = glyph_count;
        for (std::uint16_t subtable_index = 0U;
             subtable_index < lookup.subtable_count;
             ++subtable_index) {
            if (!context_subtable_may_start(
                    effective_options,
                    subtable_index,
                    lookup.subtable_count,
                    glyph_storage[position].glyph_id)) {
                continue;
            }
            std::size_t subtable = 0U;
            if (!lookup.try_get_subtable(subtable_index, subtable, error)) {
                return false;
            }
            const apply_result result = apply_subtable(
                gsub,
                lookup.table,
                lookup.type,
                subtable,
                glyph_storage,
                glyph_count,
                position,
                lookup.flags,
                lookup.mark_filtering_set,
                effective_options,
                0U);
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
                context_match_end = adjust_context_match_end_after_substitution(
                    context_match_end,
                    count_before,
                    glyph_count);
                if (glyph_count > count_before) {
                    iteration += glyph_count - count_before;
                }
                if (!reverse && context_match_end > iteration) {
                    iteration = context_match_end;
                }
                if (options.context_match_end != nullptr &&
                    context_match_end > *options.context_match_end) {
                    *options.context_match_end = context_match_end;
                }
                break;
            }
        }
    }
    set_error(error, font_error::none);
    return true;
}

bool try_apply_open_type_gsub_lookup_at(
    const open_type_layout_table_view& gsub,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::uint32_t position,
    const open_type_gsub_apply_options& options,
    bool& applied,
    font_error* error) noexcept {
    applied = false;
    if (glyph_count > glyph_storage.size() || position >= glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (options.required_glyph_flags != 0U &&
        (static_cast<std::uint32_t>(glyph_storage[position].flags) &
            options.required_glyph_flags) != options.required_glyph_flags) {
        set_error(error, font_error::none);
        return true;
    }
    if (options.lookup_coverage != nullptr &&
        glyph_storage[position].glyph_id <= 0xFFFFU &&
        options.lookup_coverage->find(static_cast<std::uint16_t>(
            glyph_storage[position].glyph_id)) < 0) {
        set_error(error, font_error::none);
        return true;
    }
    auto effective_options = options;
    std::uint32_t context_match_end = 0U;
    if (options.context_match_end != nullptr) {
        effective_options.context_match_end = &context_match_end;
    }
    if (effective_options.restrict_to_syllable) {
        effective_options.restricted_syllable =
            static_cast<std::uint8_t>(
                (static_cast<std::uint32_t>(
                    glyph_storage[position].flags) &
                    complex_syllable_mask) >>
                complex_syllable_shift);
    }
    const std::uint32_t count_before = glyph_count;
    const apply_result result = apply_lookup_at(
        gsub,
        lookup_index,
        glyph_storage,
        glyph_count,
        position,
        effective_options,
        0U);
    if (result == apply_result::malformed) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (result == apply_result::insufficient_buffer) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    applied = result == apply_result::applied;
    if (applied && options.context_match_end != nullptr) {
        const auto adjusted_context_match_end =
            adjust_context_match_end_after_substitution(
                context_match_end,
                count_before,
                glyph_count);
        if (adjusted_context_match_end > *options.context_match_end) {
            *options.context_match_end = adjusted_context_match_end;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
