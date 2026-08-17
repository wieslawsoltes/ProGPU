#pragma once

#include "progpu_native_text.hpp"

#include <array>
#include <cstdint>
#include <span>

namespace progpu::native::text::feature_detail {

enum class fraction_feature_kind : std::uint8_t {
    none,
    fraction,
    numerator,
    denominator
};

struct lookup_feature_resolution final {
    open_type_tag feature{};
    bool found = false;
    bool required = false;
};

bool has_feature_settings(
    const open_type_shape_run_options& options,
    open_type_tag feature) noexcept;

std::uint32_t get_feature_value(
    const open_type_shape_run_options& options,
    open_type_tag feature,
    std::int32_t cluster) noexcept;

bool try_resolve_lookup_feature(
    const open_type_layout_table_view& layout,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    lookup_feature_resolution& result,
    font_error* error) noexcept;

std::span<const open_type_tag> inactive_fraction_features(
    const open_type_shape_run_options& options,
    std::array<open_type_tag, 3U>& storage) noexcept;

bool apply_fraction_lookup(
    const open_type_layout_table_view& gsub,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    fraction_feature_kind kind,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept;

bool apply_fraction_features(
    const open_type_layout_table_view& gsub,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept;

bool apply_gsub_lookup_with_feature_values(
    const open_type_layout_table_view& gsub,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error,
    std::uint32_t* random_state = nullptr) noexcept;

bool apply_gpos_lookup_with_feature_values(
    const open_type_layout_table_view& gpos,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    std::span<shaping_glyph> glyphs,
    const open_type_gpos_apply_options& apply_options,
    font_error* error) noexcept;

} // namespace progpu::native::text::feature_detail
