#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Reusable borrowed shaping-plan ownership around the allocation-free GSUB and
// GPOS executors. Font bytes and lookup arrays stay caller-owned; building a
// plan parses and selects once, while compatible run shaping reuses the views.

namespace progpu::native::text {
namespace {

constexpr open_type_tag gdef_tag =
    open_type_tag::from_chars('G', 'D', 'E', 'F');
constexpr open_type_tag gsub_tag =
    open_type_tag::from_chars('G', 'S', 'U', 'B');
constexpr open_type_tag gpos_tag =
    open_type_tag::from_chars('G', 'P', 'O', 'S');
constexpr std::uint64_t fnv_offset = 14695981039346656037ULL;
constexpr std::uint64_t fnv_prime = 1099511628211ULL;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

std::uint64_t hash_features(
    std::span<const open_type_tag> features) noexcept {
    std::uint64_t hash = fnv_offset;
    for (const auto feature : features) {
        std::uint32_t value = feature.value;
        for (std::uint32_t shift = 0U; shift < 32U; shift += 8U) {
            hash ^= static_cast<std::uint8_t>(value >> shift);
            hash *= fnv_prime;
        }
    }
    hash ^= features.size();
    hash *= fnv_prime;
    return hash;
}

std::uint64_t hash_coordinates(
    std::span<const std::int16_t> coordinates) noexcept {
    std::uint64_t hash = fnv_offset;
    for (const auto coordinate : coordinates) {
        const auto value = static_cast<std::uint16_t>(coordinate);
        hash ^= static_cast<std::uint8_t>(value);
        hash *= fnv_prime;
        hash ^= static_cast<std::uint8_t>(value >> 8U);
        hash *= fnv_prime;
    }
    hash ^= coordinates.size();
    hash *= fnv_prime;
    return hash;
}

bool try_get_layout(
    const sfnt_font_view& font,
    open_type_tag tag,
    open_type_layout_table_view& result,
    std::size_t& length,
    font_error* error) noexcept {
    result = {};
    length = 0U;
    sfnt_table_view table{};
    if (!font.try_get_table(tag, table)) {
        return true;
    }
    length = table.bytes.size();
    return open_type_layout_table_view::try_create(table.bytes, result, error);
}

bool try_get_selection_capacity(
    const open_type_layout_table_view& layout,
    const open_type_shape_run_options& options,
    std::uint32_t& result,
    font_error* error) noexcept {
    result = 0U;
    if (layout.lookup_count() == 0U) {
        return true;
    }
    open_type_layout_table_view::lookup_selection_requirements requirements{};
    if (!layout.try_get_lookup_selection_requirements(
            options.script,
            options.language,
            options.requested_features,
            options.normalized_coordinates,
            requirements,
            error)) {
        return false;
    }
    result = requirements.lookup_capacity;
    return true;
}

bool try_get_context_capacities(
    const open_type_layout_table_view& layout,
    std::uint16_t extension_lookup_type,
    std::uint32_t& subtable_capacity,
    std::uint32_t& coverage_capacity,
    font_error* error) noexcept {
    subtable_capacity = 0U;
    coverage_capacity = 0U;
    for (std::uint16_t index = 0U;
         index < layout.lookup_count();
         ++index) {
        open_type_context_accelerator_requirements requirements{};
        if (!layout.try_get_lookup_context_accelerator_requirements(
                index, extension_lookup_type, requirements, error)) {
            return false;
        }
        if (!requirements.supported) {
            continue;
        }
        if (requirements.subtable_capacity >
                std::numeric_limits<std::uint32_t>::max() -
                    subtable_capacity ||
            requirements.coverage_capacity >
                std::numeric_limits<std::uint32_t>::max() -
                    coverage_capacity) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        subtable_capacity += requirements.subtable_capacity;
        coverage_capacity += requirements.coverage_capacity;
    }
    return true;
}

bool try_build_accelerators(
    const open_type_layout_table_view& layout,
    std::span<const std::uint16_t> lookups,
    std::uint16_t extension_lookup_type,
    std::span<open_type_lookup_accelerator> storage,
    std::span<open_type_context_subtable_requirement> context_subtables,
    std::span<open_type_context_coverage_requirement> context_coverages,
    bool build_context,
    std::uint32_t& context_subtable_count,
    std::uint32_t& context_coverage_count,
    font_error* error) noexcept {
    context_subtable_count = 0U;
    context_coverage_count = 0U;
    for (std::size_t index = 0U; index < lookups.size(); ++index) {
        storage[index] = {};
        if (!layout.try_get_lookup_digest(
                lookups[index],
                extension_lookup_type,
                storage[index].digest,
                storage[index].has_digest,
                error)) {
            return false;
        }
        if (!build_context) {
            continue;
        }
        open_type_context_accelerator_requirements requirements{};
        if (!layout.try_get_lookup_context_accelerator_requirements(
                lookups[index],
                extension_lookup_type,
                requirements,
                error)) {
            return false;
        }
        if (!requirements.supported) {
            continue;
        }
        if (context_subtable_count > context_subtables.size() ||
            requirements.subtable_capacity >
                context_subtables.size() - context_subtable_count ||
            context_coverage_count > context_coverages.size() ||
            requirements.coverage_capacity >
                context_coverages.size() - context_coverage_count) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
        auto subtables = context_subtables.subspan(
            context_subtable_count,
            requirements.subtable_capacity);
        auto coverages = context_coverages.subspan(
            context_coverage_count,
            requirements.coverage_capacity);
        if (!layout.try_build_lookup_context_accelerator(
                lookups[index],
                extension_lookup_type,
                subtables,
                coverages,
                storage[index].lookup_flags,
                storage[index].has_context,
                error)) {
            return false;
        }
        if (!storage[index].has_context) {
            continue;
        }
        storage[index].context_subtable_offset = context_subtable_count;
        storage[index].context_subtable_count =
            requirements.subtable_capacity;
        for (auto& subtable : subtables) {
            subtable.coverage_offset += context_coverage_count;
        }
        context_subtable_count += requirements.subtable_capacity;
        context_coverage_count += requirements.coverage_capacity;
    }
    return true;
}

bool lookup_may_match_context(
    const open_type_lookup_accelerator& accelerator,
    std::span<const open_type_context_subtable_requirement> subtables,
    std::span<const open_type_context_coverage_requirement> coverages,
    std::span<const shaping_glyph> glyphs,
    const open_type_glyph_set_digest& buffer_digest) noexcept {
    if (!accelerator.has_context ||
        (accelerator.lookup_flags & 0xFF1EU) != 0U) {
        return true;
    }
    if (accelerator.context_subtable_offset > subtables.size() ||
        accelerator.context_subtable_count >
            subtables.size() - accelerator.context_subtable_offset) {
        return true;
    }
    const auto lookup_subtables = subtables.subspan(
        accelerator.context_subtable_offset,
        accelerator.context_subtable_count);
    for (const auto& subtable : lookup_subtables) {
        if (subtable.coverage_offset > coverages.size() ||
            subtable.coverage_count >
                coverages.size() - subtable.coverage_offset ||
            subtable.input_count == 0U ||
            static_cast<std::uint32_t>(subtable.backtrack_count) +
                    subtable.input_count >
                subtable.coverage_count) {
            return true;
        }
        const auto required = coverages.subspan(
            subtable.coverage_offset,
            subtable.coverage_count);
        bool all_present = true;
        for (const auto& coverage : required) {
            if (!coverage.digest.may_intersect(buffer_digest)) {
                all_present = false;
                break;
            }
        }
        if (!all_present) {
            continue;
        }
        for (std::size_t position = 0U; position < glyphs.size(); ++position) {
            std::size_t match = position;
            bool matches = true;
            for (std::size_t index = 0U;
                 index < subtable.backtrack_count;
                 ++index) {
                if (match == 0U) {
                    matches = false;
                    break;
                }
                --match;
                const auto glyph = glyphs[match].glyph_id;
                if (glyph > 0xFFFFU ||
                    required[index].coverage.find(
                        static_cast<std::uint16_t>(glyph)) < 0) {
                    matches = false;
                    break;
                }
            }
            if (!matches) {
                continue;
            }
            match = position;
            const std::size_t input_end =
                static_cast<std::size_t>(subtable.backtrack_count) +
                subtable.input_count;
            for (std::size_t index = subtable.backtrack_count;
                 index < input_end;
                 ++index) {
                if (index != subtable.backtrack_count) {
                    ++match;
                }
                if (match >= glyphs.size() ||
                    glyphs[match].glyph_id > 0xFFFFU ||
                    required[index].coverage.find(static_cast<std::uint16_t>(
                        glyphs[match].glyph_id)) < 0) {
                    matches = false;
                    break;
                }
            }
            if (!matches) {
                continue;
            }
            for (std::size_t index = input_end;
                 index < required.size();
                 ++index) {
                ++match;
                if (match >= glyphs.size() ||
                    glyphs[match].glyph_id > 0xFFFFU ||
                    required[index].coverage.find(static_cast<std::uint16_t>(
                        glyphs[match].glyph_id)) < 0) {
                    matches = false;
                    break;
                }
            }
            if (matches) {
                return true;
            }
        }
    }
    return false;
}

} // namespace

bool open_type_shape_plan::matches(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options) const noexcept {
    const auto bytes = font.data();
    return font_data == bytes.data() && font_size == bytes.size() &&
        face_index == font.face_index() && script == options.script &&
        language == options.language &&
        feature_hash == hash_features(options.requested_features) &&
        coordinate_hash == hash_coordinates(options.normalized_coordinates);
}

bool open_type_shape_plan::gsub_lookup_may_match_context(
    std::size_t accelerator_index,
    std::span<const shaping_glyph> glyphs,
    const open_type_glyph_set_digest& buffer_digest) const noexcept {
    return accelerator_index >= gsub_accelerators.size() ||
        lookup_may_match_context(
            gsub_accelerators[accelerator_index],
            gsub_context_subtables,
            gsub_context_coverages,
            glyphs,
            buffer_digest);
}

bool open_type_shape_plan::gpos_lookup_may_match_context(
    std::size_t accelerator_index,
    std::span<const shaping_glyph> glyphs,
    const open_type_glyph_set_digest& buffer_digest) const noexcept {
    return accelerator_index >= gpos_accelerators.size() ||
        lookup_may_match_context(
            gpos_accelerators[accelerator_index],
            gpos_context_subtables,
            gpos_context_coverages,
            glyphs,
            buffer_digest);
}

bool try_get_open_type_shape_plan_requirements(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    open_type_shape_plan_requirements& result,
    font_error* error) noexcept {
    result = {};
    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    std::size_t gsub_length = 0U;
    std::size_t gpos_length = 0U;
    if (!try_get_layout(font, gsub_tag, gsub, gsub_length, error) ||
        !try_get_layout(font, gpos_tag, gpos, gpos_length, error) ||
        !try_get_selection_capacity(
            gsub, options, result.gsub_lookup_capacity, error) ||
        !try_get_selection_capacity(
            gpos, options, result.gpos_lookup_capacity, error) ||
        !try_get_context_capacities(
            gsub,
            7U,
            result.gsub_context_subtable_capacity,
            result.gsub_context_coverage_capacity,
            error) ||
        !try_get_context_capacities(
            gpos,
            9U,
            result.gpos_context_subtable_capacity,
            result.gpos_context_coverage_capacity,
            error)) {
        result = {};
        return false;
    }
    result.gsub_accelerator_capacity = result.gsub_lookup_capacity;
    result.gpos_accelerator_capacity = result.gpos_lookup_capacity;
    set_error(error, font_error::none);
    return true;
}

namespace {

bool try_build_shape_plan(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> gsub_lookup_storage,
    std::span<std::uint16_t> gpos_lookup_storage,
    std::span<open_type_lookup_accelerator> gsub_accelerator_storage,
    std::span<open_type_lookup_accelerator> gpos_accelerator_storage,
    std::span<open_type_context_subtable_requirement>
        gsub_context_subtable_storage,
    std::span<open_type_context_coverage_requirement>
        gsub_context_coverage_storage,
    std::span<open_type_context_subtable_requirement>
        gpos_context_subtable_storage,
    std::span<open_type_context_coverage_requirement>
        gpos_context_coverage_storage,
    bool build_accelerators,
    bool build_context,
    open_type_shape_plan& result,
    font_error* error) noexcept {
    result = {};
    open_type_shape_plan_requirements requirements{};
    if (!try_get_open_type_shape_plan_requirements(
            font, options, requirements, error)) {
        return false;
    }
    if (gsub_lookup_storage.size() < requirements.gsub_lookup_capacity ||
        gpos_lookup_storage.size() < requirements.gpos_lookup_capacity ||
        (build_accelerators &&
            (gsub_accelerator_storage.size() <
                    requirements.gsub_accelerator_capacity ||
                gpos_accelerator_storage.size() <
                    requirements.gpos_accelerator_capacity)) ||
        (build_context &&
            (gsub_context_subtable_storage.size() <
                    requirements.gsub_context_subtable_capacity ||
                gsub_context_coverage_storage.size() <
                    requirements.gsub_context_coverage_capacity ||
                gpos_context_subtable_storage.size() <
                    requirements.gpos_context_subtable_capacity ||
                gpos_context_coverage_storage.size() <
                    requirements.gpos_context_coverage_capacity))) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    std::size_t gsub_length = 0U;
    std::size_t gpos_length = 0U;
    if (!try_get_layout(font, gsub_tag, gsub, gsub_length, error) ||
        !try_get_layout(font, gpos_tag, gpos, gpos_length, error)) {
        return false;
    }
    std::uint32_t gsub_count = 0U;
    std::uint32_t gpos_count = 0U;
    if ((gsub.lookup_count() != 0U &&
            !gsub.try_select_lookups(
                options.script,
                options.language,
                options.requested_features,
                options.normalized_coordinates,
                gsub_lookup_storage,
                gsub_count,
                error)) ||
        (gpos.lookup_count() != 0U &&
            !gpos.try_select_lookups(
                options.script,
                options.language,
                options.requested_features,
                options.normalized_coordinates,
                gpos_lookup_storage,
                gpos_count,
                error))) {
        return false;
    }
    const auto gsub_lookups = gsub_lookup_storage.first(gsub_count);
    const auto gpos_lookups = gpos_lookup_storage.first(gpos_count);
    auto gsub_accelerators = build_accelerators
        ? gsub_accelerator_storage.first(gsub_count)
        : std::span<open_type_lookup_accelerator>{};
    auto gpos_accelerators = build_accelerators
        ? gpos_accelerator_storage.first(gpos_count)
        : std::span<open_type_lookup_accelerator>{};
    std::uint32_t gsub_context_subtable_count = 0U;
    std::uint32_t gsub_context_coverage_count = 0U;
    std::uint32_t gpos_context_subtable_count = 0U;
    std::uint32_t gpos_context_coverage_count = 0U;
    if (build_accelerators &&
        (!try_build_accelerators(
                gsub,
                gsub_lookups,
                7U,
                gsub_accelerators,
                gsub_context_subtable_storage,
                gsub_context_coverage_storage,
                build_context,
                gsub_context_subtable_count,
                gsub_context_coverage_count,
                error) ||
            !try_build_accelerators(
                gpos,
                gpos_lookups,
                9U,
                gpos_accelerators,
                gpos_context_subtable_storage,
                gpos_context_coverage_storage,
                build_context,
                gpos_context_subtable_count,
                gpos_context_coverage_count,
                error))) {
        return false;
    }

    open_type_gdef_view gdef{};
    bool has_gdef = false;
    sfnt_table_view gdef_table{};
    if (font.try_get_table(gdef_tag, gdef_table) &&
        !is_open_type_gdef_blocklisted(
            gdef_table.bytes.size(), gsub_length, gpos_length)) {
        if (!open_type_gdef_view::try_create(
                gdef_table.bytes, gdef, error)) {
            return false;
        }
        has_gdef = true;
    }

    const auto bytes = font.data();
    result = open_type_shape_plan{
        gsub,
        gpos,
        gdef,
        gsub_lookups,
        gpos_lookups,
        gsub_accelerators,
        gpos_accelerators,
        gsub_context_subtable_storage.first(gsub_context_subtable_count),
        gsub_context_coverage_storage.first(gsub_context_coverage_count),
        gpos_context_subtable_storage.first(gpos_context_subtable_count),
        gpos_context_coverage_storage.first(gpos_context_coverage_count),
        bytes.data(),
        bytes.size(),
        hash_features(options.requested_features),
        hash_coordinates(options.normalized_coordinates),
        font.face_index(),
        options.script,
        options.language,
        has_gdef};
    set_error(error, font_error::none);
    return true;
}

} // namespace

bool try_build_open_type_shape_plan(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> gsub_lookup_storage,
    std::span<std::uint16_t> gpos_lookup_storage,
    std::span<open_type_lookup_accelerator> gsub_accelerator_storage,
    std::span<open_type_lookup_accelerator> gpos_accelerator_storage,
    std::span<open_type_context_subtable_requirement>
        gsub_context_subtable_storage,
    std::span<open_type_context_coverage_requirement>
        gsub_context_coverage_storage,
    std::span<open_type_context_subtable_requirement>
        gpos_context_subtable_storage,
    std::span<open_type_context_coverage_requirement>
        gpos_context_coverage_storage,
    open_type_shape_plan& result,
    font_error* error) noexcept {
    return try_build_shape_plan(
        font,
        options,
        gsub_lookup_storage,
        gpos_lookup_storage,
        gsub_accelerator_storage,
        gpos_accelerator_storage,
        gsub_context_subtable_storage,
        gsub_context_coverage_storage,
        gpos_context_subtable_storage,
        gpos_context_coverage_storage,
        true,
        true,
        result,
        error);
}

bool try_build_open_type_shape_plan(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> gsub_lookup_storage,
    std::span<std::uint16_t> gpos_lookup_storage,
    std::span<open_type_lookup_accelerator> gsub_accelerator_storage,
    std::span<open_type_lookup_accelerator> gpos_accelerator_storage,
    open_type_shape_plan& result,
    font_error* error) noexcept {
    return try_build_shape_plan(
        font,
        options,
        gsub_lookup_storage,
        gpos_lookup_storage,
        gsub_accelerator_storage,
        gpos_accelerator_storage,
        {},
        {},
        {},
        {},
        true,
        false,
        result,
        error);
}

bool try_build_open_type_shape_plan(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> gsub_lookup_storage,
    std::span<std::uint16_t> gpos_lookup_storage,
    open_type_shape_plan& result,
    font_error* error) noexcept {
    return try_build_shape_plan(
        font,
        options,
        gsub_lookup_storage,
        gpos_lookup_storage,
        {},
        {},
        {},
        {},
        {},
        {},
        false,
        false,
        result,
        error);
}

} // namespace progpu::native::text
