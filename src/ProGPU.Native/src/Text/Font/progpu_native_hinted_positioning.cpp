#include "progpu_native_hinted_positioning.hpp"
#include "progpu_native_hinted_shaper.hpp"
#include "progpu_native_hinted_gpos.hpp"
#include "../Interop/progpu_native_owned_allocation_internal.hpp"
#include "../Shaping/progpu_native_open_type_feature_values_internal.hpp"
#include "../Shaping/progpu_native_legacy_kern_internal.hpp"

#include <algorithm>
#include <array>
#include <limits>
#include <new>

namespace progpu::native::text {
namespace {
constexpr auto gpos_tag = open_type_tag::from_chars('G', 'P', 'O', 'S');
constexpr auto kern_tag = open_type_tag::from_chars('k', 'e', 'r', 'n');
constexpr auto distance_tag = open_type_tag::from_chars('d', 'i', 's', 't');
void set_error(font_error* error, font_error value) noexcept { if (error != nullptr) *error = value; }

#if defined(PROGPU_NATIVE_FONT_HINTING)
constexpr auto gdef_tag = open_type_tag::from_chars('G', 'D', 'E', 'F');
bool same_glyph(const shaping_glyph& a, const shaping_glyph& b) noexcept {
    return a.glyph_id == b.glyph_id && a.code_point == b.code_point && a.cluster == b.cluster && a.flags == b.flags &&
        a.advance_x == b.advance_x && a.advance_y == b.advance_y && a.offset_x == b.offset_x && a.offset_y == b.offset_y;
}

bool safe_edge(std::span<const shaping_glyph> glyphs, std::size_t edge) noexcept {
    return edge == 0U || edge == glyphs.size() || (edge < glyphs.size() &&
        glyphs[edge - 1U].cluster != glyphs[edge].cluster &&
        (static_cast<std::uint32_t>(glyphs[edge].flags) &
            static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break)) == 0U);
}
#endif

// An extension remains admitted only when every subtable is exactly one of the
// same single/pair families. No partial parse can turn a later form into a no-op.
bool single_or_pair(const open_type_lookup_view& lookup, bool& supported) noexcept {
    supported = lookup.type == 1U || lookup.type == 2U;
    if (lookup.type != 9U) return true;
    supported = true;
    for (std::uint16_t i = 0U; i < lookup.subtable_count; ++i) {
        std::size_t offset = 0U;
        if (!lookup.try_get_subtable(i, offset) || offset > lookup.table.size() || lookup.table.size() - offset < 8U) return false;
        const auto u16 = [&](std::size_t at) noexcept { return static_cast<std::uint16_t>(
            (std::to_integer<std::uint16_t>(lookup.table[at]) << 8U) | std::to_integer<std::uint16_t>(lookup.table[at + 1U])); };
        if (u16(offset) != 1U) return false;
        const auto type = u16(offset + 2U);
        const auto relative = (static_cast<std::uint32_t>(u16(offset + 4U)) << 16U) | u16(offset + 6U);
        if (relative == 0U || relative > lookup.table.size() - offset || lookup.table.size() - offset - relative < 2U) return false;
        supported = supported && (type == 1U || type == 2U);
    }
    return true;
}
} // namespace

open_type_shape_run_options hinted_positioning_recipe::options() const noexcept {
    auto result = configuration;
    result.requested_features = requested_features;
    result.explicit_features = explicit_features;
    result.feature_settings = feature_settings;
    result.normalized_coordinates = normalized_coordinates;
    return result;
}

bool hinted_positioning_recipe::allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    const owned_output_range range{output, bytes};
    return range.overlaps(this, sizeof(*this)) || range.overlaps(prepared_glyphs) || range.overlaps(requested_features) ||
        range.overlaps(explicit_features) || range.overlaps(feature_settings) || range.overlaps(normalized_coordinates) || range.overlaps(lookups);
}

bool try_capture_hinted_positioning(const sfnt_font_view& font,
    const open_type_shape_run_options& options, std::span<const shaping_glyph> prepared,
    hinted_projection_policy projection, std::shared_ptr<const hinted_positioning_recipe>& result, font_error* error) noexcept {
    try {
        if (prepared.size() > UINT32_MAX || options.complex_script != open_type_complex_script::none ||
            options.cluster_level != shaping_cluster_level::monotone_graphemes ||
            (options.direction != shaping_direction::left_to_right && options.direction != shaping_direction::right_to_left)) {
            result.reset(); set_error(error, font_error::none); return true;
        }
        auto recipe = std::make_shared<hinted_positioning_recipe>();
        recipe->configuration = options;
        recipe->configuration.requested_features = {}; recipe->configuration.explicit_features = {};
        recipe->configuration.feature_settings = {}; recipe->configuration.normalized_coordinates = {};
        recipe->configuration.pre_context = {}; recipe->configuration.post_context = {};
        recipe->configuration.normalization_data = nullptr;
        recipe->projection = projection;
        recipe->requested_features.assign(options.requested_features.begin(), options.requested_features.end());
        recipe->explicit_features.assign(options.explicit_features.begin(), options.explicit_features.end());
        recipe->feature_settings.assign(options.feature_settings.begin(), options.feature_settings.end());
        recipe->normalized_coordinates.assign(options.normalized_coordinates.begin(), options.normalized_coordinates.end());
        recipe->prepared_glyphs.assign(prepared.begin(), prepared.end());
        open_type_glyph_set_digest glyph_digest{};
        for (std::size_t i = 0U; i < prepared.size(); ++i) {
            if (prepared[i].glyph_id > UINT16_MAX || prepared[i].cluster < 0 ||
                (i != 0U && prepared[i].cluster < prepared[i - 1U].cluster)) {
                result.reset(); set_error(error, font_error::none); return true;
            }
            glyph_digest.add(static_cast<std::uint16_t>(prepared[i].glyph_id));
        }
        bool has_gpos_kerning = false;
        sfnt_table_view table{};
        if (font.try_get_table(gpos_tag, table)) {
            open_type_layout_table_view gpos{};
            if (!open_type_layout_table_view::try_create(table.bytes, gpos, error)) return false;
            std::vector<std::uint16_t> selected(gpos.lookup_count());
            std::array<open_type_tag, 3U> excluded_storage{};
            const auto excluded = feature_detail::inactive_fraction_features(options, excluded_storage);
            std::uint32_t count = 0U;
            const bool selected_ok = excluded.empty()
                ? gpos.try_select_lookups(options.script, options.language, options.requested_features,
                    options.normalized_coordinates, selected, count, error)
                : gpos.try_select_lookups_excluding(options.script, options.language, options.requested_features,
                    excluded, options.normalized_coordinates, selected, count, error);
            if (!selected_ok) return false;
            for (std::uint32_t i = 0U; i < count; ++i) {
                feature_detail::lookup_feature_resolution resolution{};
                if (!feature_detail::try_resolve_lookup_feature(gpos, options, selected[i], resolution, error)) return false;
                has_gpos_kerning |= (resolution.required || resolution.found) &&
                    (resolution.feature == kern_tag || resolution.feature == distance_tag);
                open_type_glyph_set_digest lookup_digest{};
                bool has_digest = false;
                if (!gpos.try_get_lookup_digest(selected[i], 9U, lookup_digest, has_digest, error)) return false;
                // Negative-only original-glyph proof; a subset cannot introduce
                // a missing glyph. Positive digests never admit an opcode.
                if (has_digest && !lookup_digest.may_intersect(glyph_digest)) continue;
                open_type_lookup_view lookup{};
                bool supported = false;
                if (!gpos.try_get_lookup(selected[i], lookup, error) || !single_or_pair(lookup, supported)) {
                    set_error(error, font_error::invalid_face); return false;
                }
                if (!supported) { result.reset(); set_error(error, font_error::none); return true; }
                recipe->lookups.push_back(selected[i]);
            }
        }
        // Same feature-value decision as the original shaper, before legacy
        // kerning. Cluster-specific GPOS feature values remain owned above.
        recipe->legacy_kerning = !has_gpos_kerning && feature_detail::is_run_feature_enabled(options, kern_tag);
        result = std::move(recipe); set_error(error, font_error::none); return true;
    } catch (const std::bad_alloc&) { set_error(error, font_error::insufficient_buffer); }
    catch (...) { set_error(error, font_error::invalid_argument); }
    return false;
}

bool try_recompose_hinted_positioning(std::shared_ptr<const hinted_shaped_run> original,
    std::uint32_t prepared_start, std::uint32_t prepared_count,
    std::shared_ptr<const hinted_positioned_slice>& result, font_error* error) noexcept {
#if !defined(PROGPU_NATIVE_FONT_HINTING)
    (void)original; (void)prepared_start; (void)prepared_count; (void)result;
    set_error(error, font_error::invalid_argument);
    return false;
#else
    const auto invalid = [&]() noexcept { set_error(error, font_error::invalid_argument); return false; };
    if (original == nullptr || original->positioning == nullptr || original->batch == nullptr || original->batch->identity == nullptr ||
        original->batch->identity->source == nullptr || prepared_count == 0U) return invalid();
    const auto& recipe = *original->positioning;
    const auto count = recipe.prepared_glyphs.size();
    if (count != original->glyphs.size() || count != original->descriptor_indices.size() ||
        count != original->source_descriptor_count || count > UINT32_MAX || prepared_start > count ||
        prepared_count > count - prepared_start || !safe_edge(recipe.prepared_glyphs, prepared_start) ||
        !safe_edge(recipe.prepared_glyphs, static_cast<std::size_t>(prepared_start) + prepared_count) ||
        recipe.normalized_coordinates != original->normalized_coordinates) return invalid();
    try {
        const auto& source = *original->batch->identity->source;
        sfnt_font_view font{};
        if (!sfnt_font_view::try_create(source.bytes, source.face_index, font, error)) return false;
        const auto frame = bind_hinted_gpos_frame(*original->batch, font, recipe.projection, recipe.normalized_coordinates);
        if (frame.error != hinted_projection_error::none) return invalid();
        auto candidate = std::make_shared<hinted_positioned_slice>();
        candidate->original = std::move(original);
        candidate->prepared_start = prepared_start; candidate->prepared_count = prepared_count;
        candidate->glyphs.assign(recipe.prepared_glyphs.begin() + prepared_start,
            recipe.prepared_glyphs.begin() + prepared_start + prepared_count);
        std::vector<shaping_attachment> attachments(prepared_count);
        sfnt_table_view table{};
        open_type_gdef_view gdef{};
        const open_type_gdef_view* gdef_pointer = nullptr;
        if (font.try_get_table(gdef_tag, table)) {
            if (!open_type_gdef_view::try_create(table.bytes, gdef, error)) return false;
            gdef_pointer = &gdef;
        }
        const auto options = recipe.options();
        if (!recipe.lookups.empty()) {
            open_type_layout_table_view gpos{};
            if (!font.try_get_table(gpos_tag, table) || !open_type_layout_table_view::try_create(table.bytes, gpos, error)) return false;
            const open_type_gpos_apply_options apply{gdef_pointer, options.direction, attachments, &font, recipe.normalized_coordinates};
            for (const auto lookup : recipe.lookups)
                if (!feature_detail::apply_gpos_lookup_with_feature_values(gpos, options, lookup,
                    candidate->glyphs, apply, error, nullptr, &frame.frame)) return false;
        }
        if (recipe.legacy_kerning && !detail::try_apply_device_legacy_kern(font, candidate->glyphs, gdef_pointer, frame.frame, error))
            return false;
        const bool rtl = options.direction == shaping_direction::right_to_left;
        if (rtl) std::reverse(candidate->glyphs.begin(), candidate->glyphs.end());
        candidate->original_glyph_indices.reserve(prepared_count);
        for (std::uint32_t i = 0U; i < prepared_count; ++i) {
            const auto descriptor = rtl ? prepared_start + prepared_count - 1U - i : prepared_start + i;
            const auto original_index = rtl ? static_cast<std::uint32_t>(count) - 1U - descriptor : descriptor;
            if (candidate->original->descriptor_indices[original_index] != descriptor ||
                candidate->original->glyphs[original_index].glyph_id != candidate->glyphs[i].glyph_id ||
                candidate->original->glyphs[original_index].cluster != candidate->glyphs[i].cluster) return invalid();
            candidate->original_glyph_indices.push_back(original_index);
        }
        // The complete recipe must reproduce the original run exactly. Smaller
        // slices may differ in placement only; raw original flags/metrics remain
        // immutable in candidate->original for verification and diagnostics.
        if (prepared_start == 0U && prepared_count == count)
            for (std::size_t i = 0U; i < count; ++i)
                if (!same_glyph(candidate->glyphs[i], candidate->original->glyphs[i])) {
                    set_error(error, font_error::verification_failed); return false;
                }
        result = std::move(candidate); set_error(error, font_error::none); return true;
    } catch (const std::bad_alloc&) { set_error(error, font_error::insufficient_buffer); }
    catch (...) { set_error(error, font_error::invalid_argument); }
    return false;
#endif
}
} // namespace progpu::native::text
