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
    std::uint32_t code_point,
    font_provider_face& face,
    sfnt_font_view& font) noexcept {
    face = {};
    font = {};
    if (!provider.try_get_face(provider.context, index, face) ||
        face.data.empty() ||
        (family != 0U && face.family_identity != family)) {
        return false;
    }
    font_error ignored = font_error::none;
    if (!sfnt_font_view::try_create(
            face.data, face.face_index, font, &ignored)) {
        return false;
    }
    std::uint16_t glyph = 0U;
    return font.try_get_glyph_index(code_point, glyph) && glyph != 0U;
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
        stretch == 0U || stretch > 9U ||
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
        if (try_open_candidate(provider, entry.face_index, family_identity,
                code_point, face, font)) {
            result = font_provider_result{face, entry.face_index, true};
            set_error(error, font_error::none);
            return true;
        }
        break;
    }

    const std::uint32_t count = provider.get_face_count(provider.context);
    std::uint64_t best_score = std::numeric_limits<std::uint64_t>::max();
    std::uint32_t best_index = 0U;
    font_provider_face best_face{};
    bool found = false;
    for (std::uint32_t index = 0U; index < count; ++index) {
        font_provider_face face{};
        sfnt_font_view font{};
        if (!try_open_candidate(provider, index, family_identity, code_point,
                face, font)) {
            continue;
        }
        const auto score = style_score(face, weight, stretch, slant);
        if (!found || score < best_score) {
            found = true;
            best_score = score;
            best_index = index;
            best_face = face;
            if (score == 0U) {
                break;
            }
        }
    }
    store_cache(provider, family_identity, weight, stretch, slant, code_point,
        best_index, found, cache, replacement_cursor);
    if (found) {
        result = font_provider_result{best_face, best_index, true};
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
