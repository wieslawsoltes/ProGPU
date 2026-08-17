#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned GetRawLanguageFeatureIndices and
// GetRawEnabledLookupIndices in OpenTypeTextShaper.cs at checkpoint 0a134f77.

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

bool contains_tag(
    std::span<const open_type_tag> tags,
    open_type_tag tag) noexcept {
    for (const auto candidate : tags) {
        if (candidate == tag) {
            return true;
        }
    }
    return false;
}

bool find_tagged_offset(
    std::span<const std::byte> table,
    std::size_t list,
    std::size_t offset_base,
    open_type_tag tag,
    std::size_t& result) noexcept {
    result = 0U;
    if (!can_read(table, list, 2U)) {
        return false;
    }
    const std::uint16_t count = read_u16(table, list);
    if (!can_read(table, list + 2U,
            static_cast<std::size_t>(count) * 6U)) {
        return false;
    }
    for (std::uint16_t index = 0U; index < count; ++index) {
        const std::size_t record = list + 2U + index * 6U;
        if (read_u32(table, record) != tag.value) {
            continue;
        }
        const std::uint16_t relative = read_u16(table, record + 4U);
        return relative != 0U && try_add(offset_base, relative, result) &&
            can_read(table, result, 2U);
    }
    return true;
}

struct selection_result final {
    std::uint32_t capacity = 0U;
    std::uint32_t written = 0U;
    open_type_tag target_feature{};
    bool contains_target = false;
};

bool matches_feature_variation_conditions(
    std::span<const std::byte> table,
    std::size_t condition_set,
    std::span<const std::int16_t> normalized_coordinates) noexcept {
    if (!can_read(table, condition_set, 2U)) {
        return false;
    }
    const std::uint16_t count = read_u16(table, condition_set);
    if (!can_read(
            table,
            condition_set + 2U,
            static_cast<std::size_t>(count) * 4U)) {
        return false;
    }
    for (std::uint16_t index = 0U; index < count; ++index) {
        const std::uint32_t relative = read_u32(
            table,
            condition_set + 2U + static_cast<std::size_t>(index) * 4U);
        std::size_t condition = 0U;
        if (!try_add(condition_set, relative, condition) ||
            !can_read(table, condition, 8U) ||
            read_u16(table, condition) != 1U) {
            return false;
        }
        const std::uint16_t axis = read_u16(table, condition + 2U);
        if (axis >= normalized_coordinates.size()) {
            return false;
        }
        const auto minimum = std::bit_cast<std::int16_t>(
            read_u16(table, condition + 4U));
        const auto maximum = std::bit_cast<std::int16_t>(
            read_u16(table, condition + 6U));
        const auto coordinate = normalized_coordinates[axis];
        if (coordinate < minimum || coordinate > maximum) {
            return false;
        }
    }
    return true;
}

std::size_t find_feature_substitution_table(
    std::span<const std::byte> table,
    std::size_t feature_variations,
    std::span<const std::int16_t> normalized_coordinates) noexcept {
    if (feature_variations == 0U ||
        !can_read(table, feature_variations, 8U) ||
        read_u16(table, feature_variations) != 1U ||
        read_u16(table, feature_variations + 2U) != 0U) {
        return 0U;
    }
    const std::uint32_t count = read_u32(table, feature_variations + 4U);
    if constexpr (sizeof(std::size_t) < sizeof(std::uint64_t)) {
        if (count > std::numeric_limits<std::size_t>::max() / 8U) {
            return 0U;
        }
    }
    if (!can_read(
            table,
            feature_variations + 8U,
            static_cast<std::size_t>(count) * 8U)) {
        return 0U;
    }
    for (std::uint32_t index = 0U; index < count; ++index) {
        const std::size_t record = feature_variations + 8U +
            static_cast<std::size_t>(index) * 8U;
        const std::uint32_t condition_relative = read_u32(table, record);
        std::size_t condition_set = 0U;
        const bool conditions_match = condition_relative == 0U ||
            (try_add(
                    feature_variations,
                    condition_relative,
                    condition_set) &&
                matches_feature_variation_conditions(
                    table,
                    condition_set,
                    normalized_coordinates));
        if (!conditions_match) {
            continue;
        }
        const std::uint32_t substitution_relative =
            read_u32(table, record + 4U);
        if (substitution_relative == 0U) {
            return 0U;
        }
        std::size_t substitutions = 0U;
        if (!try_add(
                feature_variations,
                substitution_relative,
                substitutions) ||
            !can_read(table, substitutions, 6U)) {
            return 0U;
        }
        if (read_u16(table, substitutions) != 1U ||
            read_u16(table, substitutions + 2U) != 0U) {
            continue;
        }
        const std::uint16_t substitution_count =
            read_u16(table, substitutions + 4U);
        return can_read(
            table,
            substitutions + 6U,
            static_cast<std::size_t>(substitution_count) * 6U)
            ? substitutions
            : 0U;
    }
    return 0U;
}

enum class alternate_feature_result : std::uint8_t {
    not_found,
    found,
    invalid
};

alternate_feature_result find_alternate_feature(
    std::span<const std::byte> table,
    std::size_t substitutions,
    std::uint16_t feature_index,
    std::size_t& result) noexcept {
    result = 0U;
    if (substitutions == 0U) {
        return alternate_feature_result::not_found;
    }
    const std::uint16_t count = read_u16(table, substitutions + 4U);
    for (std::uint16_t index = 0U; index < count; ++index) {
        const std::size_t record = substitutions + 6U +
            static_cast<std::size_t>(index) * 6U;
        if (read_u16(table, record) != feature_index) {
            continue;
        }
        return (try_add(
                    substitutions,
                    read_u32(table, record + 2U),
                    result) &&
                can_read(table, result, 4U))
            ? alternate_feature_result::found
            : alternate_feature_result::invalid;
    }
    return alternate_feature_result::not_found;
}

bool select_layout_lookups(
    std::span<const std::byte> table,
    std::size_t script_list,
    std::size_t feature_list,
    std::size_t feature_variations,
    std::uint16_t layout_lookup_count,
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    std::span<const open_type_tag> excluded_features,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<std::uint16_t> output,
    bool include_required,
    bool write,
    std::uint16_t target_lookup,
    selection_result& result) noexcept {
    result = {};
    const std::size_t substitutions = find_feature_substitution_table(
        table,
        feature_variations,
        normalized_coordinates);
    std::size_t script_table = 0U;
    if (!find_tagged_offset(
            table, script_list, script_list, script, script_table)) {
        return false;
    }
    if (script_table == 0U &&
        script != open_type_tag::from_chars('D', 'F', 'L', 'T') &&
        !find_tagged_offset(
            table,
            script_list,
            script_list,
            open_type_tag::from_chars('D', 'F', 'L', 'T'),
            script_table)) {
        return false;
    }
    if (script_table == 0U) {
        return true;
    }
    if (!can_read(table, script_table, 4U)) {
        return false;
    }
    const std::uint16_t default_language_relative =
        read_u16(table, script_table);
    const std::uint16_t language_count = read_u16(table, script_table + 2U);
    if (!can_read(table, script_table + 4U,
            static_cast<std::size_t>(language_count) * 6U)) {
        return false;
    }
    std::size_t language_table = 0U;
    if (language.value != 0U &&
        !find_tagged_offset(
            table,
            script_table + 2U,
            script_table,
            language,
            language_table)) {
        return false;
    }
    if (language_table == 0U && default_language_relative != 0U &&
        (!try_add(script_table, default_language_relative, language_table) ||
            !can_read(table, language_table, 6U))) {
        return false;
    }
    if (language_table == 0U) {
        return true;
    }
    if (!can_read(table, language_table, 6U)) {
        return false;
    }
    const std::uint16_t required_feature = read_u16(table, language_table + 2U);
    const std::uint16_t feature_index_count =
        read_u16(table, language_table + 4U);
    if (!can_read(table, language_table + 6U,
            static_cast<std::size_t>(feature_index_count) * 2U) ||
        !can_read(table, feature_list, 2U)) {
        return false;
    }
    const std::uint16_t feature_count = read_u16(table, feature_list);
    if (!can_read(table, feature_list + 2U,
            static_cast<std::size_t>(feature_count) * 6U) ||
        (required_feature != 0xFFFFU && required_feature >= feature_count)) {
        return false;
    }

    const auto append_feature = [&](std::uint16_t feature_index) noexcept {
        const std::size_t record = feature_list + 2U + feature_index * 6U;
        std::size_t feature = 0U;
        const auto alternate = find_alternate_feature(
            table,
            substitutions,
            feature_index,
            feature);
        if (alternate == alternate_feature_result::invalid) {
            return true;
        }
        if (alternate == alternate_feature_result::not_found) {
            const std::uint16_t relative = read_u16(table, record + 4U);
            if (relative == 0U ||
                !try_add(feature_list, relative, feature) ||
                !can_read(table, feature, 4U)) {
                return false;
            }
        }
        const std::uint16_t count = read_u16(table, feature + 2U);
        if (!can_read(table, feature + 4U,
                static_cast<std::size_t>(count) * 2U) ||
            count > std::numeric_limits<std::uint32_t>::max() - result.capacity) {
            return false;
        }
        result.capacity += count;
        if (!write && target_lookup == 0xFFFFU) {
            return true;
        }
        for (std::uint16_t index = 0U; index < count; ++index) {
            const std::uint16_t lookup = read_u16(table, feature + 4U + index * 2U);
            if (lookup >= layout_lookup_count) {
                return false;
            }
            if (lookup == target_lookup) {
                result.contains_target = true;
                result.target_feature = open_type_tag{
                    read_u32(table, record)};
            }
            if (!write) {
                continue;
            }
            bool duplicate = false;
            for (std::uint32_t existing = 0U;
                 existing < result.written;
                 ++existing) {
                duplicate |= output[existing] == lookup;
            }
            if (!duplicate) {
                output[result.written++] = lookup;
            }
        }
        return true;
    };

    if (include_required && required_feature != 0xFFFFU &&
        !append_feature(required_feature)) {
        return false;
    }
    for (std::uint16_t index = 0U; index < feature_index_count; ++index) {
        const std::uint16_t feature_index =
            read_u16(table, language_table + 6U + index * 2U);
        if (feature_index >= feature_count) {
            return false;
        }
        if (feature_index == required_feature) {
            continue;
        }
        const std::size_t record = feature_list + 2U + feature_index * 6U;
        const open_type_tag tag{read_u32(table, record)};
        if (contains_tag(requested_features, tag) &&
            !contains_tag(excluded_features, tag) &&
            !append_feature(feature_index)) {
            return false;
        }
    }
    return true;
}

} // namespace

bool open_type_layout_table_view::try_get_lookup_selection_requirements(
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    lookup_selection_requirements& result,
    font_error* error) const noexcept {
    return try_get_lookup_selection_requirements(
        script,
        language,
        requested_features,
        {},
        result,
        error);
}

bool open_type_layout_table_view::try_get_lookup_selection_requirements(
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    std::span<const std::int16_t> normalized_coordinates,
    lookup_selection_requirements& result,
    font_error* error) const noexcept {
    result = {};
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            requested_features,
            {},
            normalized_coordinates,
            {},
            true,
            false,
            0xFFFFU,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result.lookup_capacity = std::min<std::uint32_t>(
        selected.capacity, lookup_count_);
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_select_lookups(
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    std::span<std::uint16_t> output,
    std::uint32_t& written,
    font_error* error) const noexcept {
    return try_select_lookups(
        script,
        language,
        requested_features,
        {},
        output,
        written,
        error);
}

bool open_type_layout_table_view::try_select_lookups(
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<std::uint16_t> output,
    std::uint32_t& written,
    font_error* error) const noexcept {
    written = 0U;
    lookup_selection_requirements requirements{};
    if (!try_get_lookup_selection_requirements(
            script,
            language,
            requested_features,
            normalized_coordinates,
            requirements,
            error)) {
        return false;
    }
    if (output.size() < requirements.lookup_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            requested_features,
            {},
            normalized_coordinates,
            output,
            true,
            true,
            0xFFFFU,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    written = selected.written;
    std::sort(output.begin(), output.begin() + written);
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_select_lookups_excluding(
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    std::span<const open_type_tag> excluded_features,
    std::span<std::uint16_t> output,
    std::uint32_t& written,
    font_error* error) const noexcept {
    return try_select_lookups_excluding(
        script,
        language,
        requested_features,
        excluded_features,
        {},
        output,
        written,
        error);
}

bool open_type_layout_table_view::try_select_lookups_excluding(
    open_type_tag script,
    open_type_tag language,
    std::span<const open_type_tag> requested_features,
    std::span<const open_type_tag> excluded_features,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<std::uint16_t> output,
    std::uint32_t& written,
    font_error* error) const noexcept {
    written = 0U;
    lookup_selection_requirements requirements{};
    if (!try_get_lookup_selection_requirements(
            script,
            language,
            requested_features,
            normalized_coordinates,
            requirements,
            error)) {
        return false;
    }
    if (output.size() < requirements.lookup_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            requested_features,
            excluded_features,
            normalized_coordinates,
            output,
            true,
            true,
            0xFFFFU,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    written = selected.written;
    std::sort(output.begin(), output.begin() + written);
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_select_feature_lookups(
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    std::span<std::uint16_t> output,
    std::uint32_t& written,
    font_error* error) const noexcept {
    return try_select_feature_lookups(
        script,
        language,
        feature,
        {},
        output,
        written,
        error);
}

bool open_type_layout_table_view::try_select_feature_lookups(
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<std::uint16_t> output,
    std::uint32_t& written,
    font_error* error) const noexcept {
    written = 0U;
    if (output.size() < lookup_count_) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    const std::array requested{feature};
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            requested,
            {},
            normalized_coordinates,
            output,
            false,
            true,
            0xFFFFU,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    written = selected.written;
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_feature_contains_lookup(
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    std::uint16_t lookup,
    bool& contains,
    font_error* error) const noexcept {
    return try_feature_contains_lookup(
        script,
        language,
        feature,
        lookup,
        {},
        contains,
        error);
}

bool open_type_layout_table_view::try_feature_contains_lookup(
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    std::uint16_t lookup,
    std::span<const std::int16_t> normalized_coordinates,
    bool& contains,
    font_error* error) const noexcept {
    contains = false;
    if (lookup >= lookup_count_) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::array requested{feature};
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            requested,
            {},
            normalized_coordinates,
            {},
            false,
            false,
            lookup,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    contains = selected.contains_target;
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_required_feature_contains_lookup(
    open_type_tag script,
    open_type_tag language,
    std::uint16_t lookup,
    bool& contains,
    font_error* error) const noexcept {
    return try_required_feature_contains_lookup(
        script,
        language,
        lookup,
        {},
        contains,
        error);
}

bool open_type_layout_table_view::try_required_feature_contains_lookup(
    open_type_tag script,
    open_type_tag language,
    std::uint16_t lookup,
    std::span<const std::int16_t> normalized_coordinates,
    bool& contains,
    font_error* error) const noexcept {
    contains = false;
    if (lookup >= lookup_count_) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            {},
            {},
            normalized_coordinates,
            {},
            true,
            false,
            lookup,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    contains = selected.contains_target;
    set_error(error, font_error::none);
    return true;
}

bool open_type_layout_table_view::try_required_feature_for_lookup(
    open_type_tag script,
    open_type_tag language,
    std::uint16_t lookup,
    open_type_tag& feature,
    bool& contains,
    font_error* error) const noexcept {
    return try_required_feature_for_lookup(
        script,
        language,
        lookup,
        {},
        feature,
        contains,
        error);
}

bool open_type_layout_table_view::try_required_feature_for_lookup(
    open_type_tag script,
    open_type_tag language,
    std::uint16_t lookup,
    std::span<const std::int16_t> normalized_coordinates,
    open_type_tag& feature,
    bool& contains,
    font_error* error) const noexcept {
    feature = {};
    contains = false;
    if (lookup >= lookup_count_) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    selection_result selected{};
    if (!select_layout_lookups(
            table_,
            script_list_offset_,
            feature_list_offset_,
            feature_variations_offset_,
            lookup_count_,
            script,
            language,
            {},
            {},
            normalized_coordinates,
            {},
            true,
            false,
            lookup,
            selected)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    feature = selected.target_feature;
    contains = selected.contains_target;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
