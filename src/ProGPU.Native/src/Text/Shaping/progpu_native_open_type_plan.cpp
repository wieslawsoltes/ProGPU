#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
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
            gpos, options, result.gpos_lookup_capacity, error)) {
        result = {};
        return false;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_build_open_type_shape_plan(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> gsub_lookup_storage,
    std::span<std::uint16_t> gpos_lookup_storage,
    open_type_shape_plan& result,
    font_error* error) noexcept {
    result = {};
    open_type_shape_plan_requirements requirements{};
    if (!try_get_open_type_shape_plan_requirements(
            font, options, requirements, error)) {
        return false;
    }
    if (gsub_lookup_storage.size() < requirements.gsub_lookup_capacity ||
        gpos_lookup_storage.size() < requirements.gpos_lookup_capacity) {
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
        gsub_lookup_storage.first(gsub_count),
        gpos_lookup_storage.first(gpos_count),
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

} // namespace progpu::native::text
