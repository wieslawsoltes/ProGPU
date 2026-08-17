#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstdint>
#include <limits>
#include <span>

// Platform-neutral provider/cache port of ProGPU-owned FontManager discovery
// boundaries. OS/browser adapters retain byte ownership and discovery policy;
// native selection performs no allocation, file access, or managed crossing.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool key_matches(
    const font_provider_cache_entry& entry,
    const font_provider_view& provider,
    std::uint64_t family,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant,
    std::uint32_t code_point) noexcept {
    return entry.occupied && entry.generation == provider.generation &&
        entry.family_identity == family && entry.weight == weight &&
        entry.stretch == stretch && entry.slant == slant &&
        entry.code_point == code_point;
}

std::uint64_t style_score(
    const font_provider_face& face,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant) noexcept {
    const auto weight_delta = face.weight > weight
        ? face.weight - weight
        : weight - face.weight;
    const auto stretch_delta = face.stretch > stretch
        ? face.stretch - stretch
        : stretch - face.stretch;
    const bool face_italic = face.slant != font_provider_slant::normal;
    const bool requested_italic = slant != font_provider_slant::normal;
    const std::uint64_t slant_penalty =
        face_italic == requested_italic ? 0U : 10000U;
    return slant_penalty + static_cast<std::uint64_t>(stretch_delta) * 1000U +
        weight_delta;
}

bool try_open_candidate(
    const font_provider_view& provider,
    std::uint32_t index,
    std::uint64_t family,
    std::uint64_t excluded_identity,
    std::uint32_t code_point,
    font_provider_face& face,
    sfnt_font_view& font,
    std::uint16_t& glyph) noexcept {
    face = {};
    font = {};
    glyph = 0U;
    if (!provider.try_get_face(provider.context, index, face) ||
        face.data.empty() ||
        (family != 0U && face.family_identity != family) ||
        (excluded_identity != 0U && face.identity == excluded_identity)) {
        return false;
    }
    font_error ignored = font_error::none;
    if (!sfnt_font_view::try_create(
            face.data, face.face_index, font, &ignored)) {
        return false;
    }
    if (!font.try_get_glyph_index(code_point, glyph) || glyph == 0U) {
        return false;
    }

    sfnt_glyph_data_view true_type{};
    if (font.try_get_glyph_data(glyph, true_type) && !true_type.empty()) {
        return true;
    }
    sfnt_glyph_outline_bounds_requirements outline{};
    ignored = font_error::none;
    if (font.try_get_outline_bounds_requirements(glyph, {}, outline, &ignored) &&
        (outline.source == sfnt_glyph_outline_source::cff1 ||
            outline.source == sfnt_glyph_outline_source::cff2) &&
        outline.path_segment_count != 0U) {
        return true;
    }
    std::uint16_t layer_count = 0U;
    if (font.try_get_colr_layer_count(glyph, layer_count, &ignored) &&
        layer_count != 0U) {
        return true;
    }
    sfnt_svg_glyph_document_view svg{};
    if (font.try_get_svg_glyph_document(glyph, svg, &ignored) &&
        !svg.bytes.empty()) {
        return true;
    }
    sfnt_bitmap_glyph_data_view bitmap{};
    if (font.try_get_sbix_glyph(glyph, 64.0F, bitmap, &ignored) ||
        font.try_get_cbdt_glyph(glyph, 64.0F, bitmap, &ignored)) {
        return true;
    }
    const auto category = get_unicode_general_category(code_point);
    return category == unicode_general_category::control ||
        category == unicode_general_category::format ||
        category == unicode_general_category::space_separator ||
        category == unicode_general_category::line_separator ||
        category == unicode_general_category::paragraph_separator;
}

void store_cache(
    const font_provider_view& provider,
    std::uint64_t family,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant,
    std::uint32_t code_point,
    std::uint32_t face_index,
    bool found,
    std::span<font_provider_cache_entry> cache,
    std::uint32_t& cursor) noexcept {
    if (cache.empty()) {
        return;
    }
    const auto slot = cursor % static_cast<std::uint32_t>(cache.size());
    cache[slot] = font_provider_cache_entry{
        provider.generation,
        family,
        code_point,
        face_index,
        weight,
        stretch,
        slant,
        found,
        true};
    cursor = (slot + 1U) % static_cast<std::uint32_t>(cache.size());
}

} // namespace

bool try_resolve_font_provider_face(
    const font_provider_view& provider,
    std::uint64_t family_identity,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant,
    std::uint32_t code_point,
    std::span<font_provider_cache_entry> cache,
    std::uint32_t& replacement_cursor,
    font_provider_result& result,
    font_error* error) noexcept {
    result = {};
    if (provider.get_face_count == nullptr || provider.try_get_face == nullptr ||
        stretch == 0U || stretch > 9U || code_point > 0x10FFFFU ||
        static_cast<std::uint8_t>(slant) >
            static_cast<std::uint8_t>(font_provider_slant::oblique)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (const auto& entry : cache) {
        if (!key_matches(entry, provider, family_identity, weight, stretch,
                slant, code_point)) {
            continue;
        }
        if (!entry.found) {
            set_error(error, font_error::none);
            return true;
        }
        font_provider_face face{};
        sfnt_font_view font{};
        std::uint16_t glyph = 0U;
        if (try_open_candidate(provider, entry.face_index, family_identity, 0U,
                code_point, face, font, glyph)) {
            result = font_provider_result{
                face, entry.face_index, glyph, true};
            set_error(error, font_error::none);
            return true;
        }
        break;
    }

    const std::uint32_t count = provider.get_face_count(provider.context);
    std::uint64_t best_score = std::numeric_limits<std::uint64_t>::max();
    std::uint32_t best_index = 0U;
    std::uint16_t best_glyph = 0U;
    font_provider_face best_face{};
    bool found = false;
    for (std::uint32_t index = 0U; index < count; ++index) {
        font_provider_face face{};
        sfnt_font_view font{};
        std::uint16_t glyph = 0U;
        if (!try_open_candidate(provider, index, family_identity, 0U,
                code_point, face, font, glyph)) {
            continue;
        }
        const auto score = style_score(face, weight, stretch, slant);
        if (!found || score < best_score) {
            found = true;
            best_score = score;
            best_index = index;
            best_face = face;
            best_glyph = glyph;
            if (score == 0U) {
                break;
            }
        }
    }
    store_cache(provider, family_identity, weight, stretch, slant, code_point,
        best_index, found, cache, replacement_cursor);
    if (found) {
        result = font_provider_result{
            best_face, best_index, best_glyph, true};
    }
    set_error(error, font_error::none);
    return true;
}

bool try_resolve_font_provider_fallback_face(
    const font_provider_view& provider,
    std::span<const std::uint64_t> ordered_family_identities,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant,
    std::uint32_t code_point,
    std::uint64_t excluded_face_identity,
    font_provider_result& result,
    font_error* error) noexcept {
    result = {};
    if (provider.get_face_count == nullptr || provider.try_get_face == nullptr ||
        stretch == 0U || stretch > 9U || code_point > 0x10FFFFU ||
        static_cast<std::uint8_t>(slant) >
            static_cast<std::uint8_t>(font_provider_slant::oblique)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }

    std::uint8_t best_tier = 3U;
    std::size_t best_family_rank = ordered_family_identities.size();
    std::uint64_t best_style = std::numeric_limits<std::uint64_t>::max();
    const auto count = provider.get_face_count(provider.context);
    for (std::uint32_t index = 0U; index < count; ++index) {
        font_provider_face face{};
        sfnt_font_view font{};
        std::uint16_t glyph = 0U;
        if (!try_open_candidate(
                provider,
                index,
                0U,
                excluded_face_identity,
                code_point,
                face,
                font,
                glyph)) {
            continue;
        }

        std::size_t family_rank = ordered_family_identities.size();
        for (std::size_t candidate = 0U;
             candidate < ordered_family_identities.size(); ++candidate) {
            if (ordered_family_identities[candidate] != 0U &&
                ordered_family_identities[candidate] == face.family_identity) {
                family_rank = candidate;
                break;
            }
        }
        const std::uint8_t tier = family_rank < ordered_family_identities.size()
            ? 0U
            : face.is_fallback ? 1U : 2U;
        const auto score = style_score(face, weight, stretch, slant);
        const bool better = tier < best_tier ||
            (tier == best_tier && tier == 0U &&
                family_rank < best_family_rank) ||
            (tier == best_tier &&
                (tier != 0U || family_rank == best_family_rank) &&
                score < best_style);
        if (!better) continue;
        best_tier = tier;
        best_family_rank = family_rank;
        best_style = score;
        result = font_provider_result{face, index, glyph, true};
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
