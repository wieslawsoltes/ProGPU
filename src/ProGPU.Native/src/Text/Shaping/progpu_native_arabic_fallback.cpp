#include "progpu_native_arabic_fallback_internal.hpp"

#include "progpu_native_arabic_actions_internal.hpp"
#include "progpu_native_open_type_gsub_internal.hpp"
#include "progpu_native_unicode_data.generated.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

// Exact allocation-free port of ProGPU-owned Arabic presentation-form and
// required-ligature fallback in ProGPU.Text/OpenTypeTextShaper.cs at repository
// checkpoint b24aabb3. Generated tables come from ArabicFallbackData.Generated.cs.
// Form replacement is O(G); the bounded fallback ligature scan is O(G * R)
// for G glyphs and fixed generated row count R, with O(1) internal storage.

namespace progpu::native::text::detail {
namespace {

constexpr std::uint32_t first_code_point = 0x0621U;
constexpr std::uint32_t last_code_point = 0x06D3U;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool is_default_ignorable(std::uint32_t value) noexcept {
    return value == 0x00ADU || value == 0x034FU || value == 0x061CU ||
        value == 0x115FU || value == 0x1160U || value == 0x17B4U ||
        value == 0x17B5U || (value >= 0x180BU && value <= 0x180FU) ||
        (value >= 0x200BU && value <= 0x200FU) ||
        (value >= 0x202AU && value <= 0x202EU) ||
        (value >= 0x2060U && value <= 0x206FU) || value == 0x3164U ||
        value == 0xFEFFU || value == 0xFFA0U ||
        (value >= 0xFFF0U && value <= 0xFFF8U) ||
        (value >= 0xFE00U && value <= 0xFE0FU) ||
        (value >= 0x1BCA0U && value <= 0x1BCAFU) ||
        (value >= 0x1D173U && value <= 0x1D17AU) ||
        (value >= 0xE0000U && value <= 0xE0FFFU);
}

bool is_mark(
    const shaping_glyph& glyph,
    const open_type_gdef_view* gdef) noexcept {
    if (gdef != nullptr && glyph.glyph_id <= 0xFFFFU) {
        const auto glyph_class = gdef->glyph_class(
            static_cast<std::uint16_t>(glyph.glyph_id));
        if (glyph_class != open_type_glyph_class::unclassified) {
            return glyph_class == open_type_glyph_class::mark;
        }
    }
    const auto category = get_unicode_general_category(glyph.code_point);
    return category == unicode_general_category::nonspacing_mark ||
        category == unicode_general_category::spacing_combining_mark ||
        category == unicode_general_category::enclosing_mark;
}

bool is_component_boundary(const shaping_glyph& glyph) noexcept {
    return glyph.code_point == 0x034FU || glyph.code_point == 0x200CU ||
        glyph.code_point == 0x200DU ||
        (glyph.code_point >= 0x180BU && glyph.code_point <= 0x180EU) ||
        (glyph.code_point >= 0xE0000U && glyph.code_point <= 0xE007FU);
}

std::uint32_t next_component(
    std::span<const shaping_glyph> glyphs,
    std::uint32_t index,
    bool ignore_marks,
    const open_type_gdef_view* gdef) noexcept {
    while (index < glyphs.size()) {
        const auto& glyph = glyphs[index];
        if (is_default_ignorable(glyph.code_point) &&
            !is_component_boundary(glyph)) {
            ++index;
            continue;
        }
        if (ignore_marks && is_mark(glyph, gdef)) {
            ++index;
            continue;
        }
        return index;
    }
    return static_cast<std::uint32_t>(glyphs.size());
}

void replace_ligature(
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::span<const std::uint32_t> components,
    std::uint16_t ligature,
    bool track_fallback_marks) noexcept {
    const std::uint32_t first = components.front();
    const std::uint32_t last = components.back();
    std::int32_t cluster = storage[first].cluster;
    for (std::uint32_t index = first; index <= last; ++index) {
        cluster = std::min(cluster, storage[index].cluster);
    }
    for (std::uint32_t index = first; index <= last; ++index) {
        storage[index].cluster = cluster;
    }
    storage[first].glyph_id = ligature;
    if (track_fallback_marks) {
        set_fallback_ligature_count(
            storage[first], static_cast<std::uint16_t>(components.size()));
        for (std::size_t component = 0U;
             component + 1U < components.size();
             ++component) {
            for (std::uint32_t index = components[component] + 1U;
                 index < components[component + 1U];
                 ++index) {
                set_fallback_ligature_component(
                    storage[index], static_cast<std::uint16_t>(component));
            }
        }
    }
    std::uint32_t write = first + 1U;
    std::size_t component = 1U;
    for (std::uint32_t read = first + 1U; read < count; ++read) {
        if (component < components.size() && read == components[component]) {
            ++component;
            continue;
        }
        storage[write++] = storage[read];
    }
    count = write;
}

bool try_apply_ligatures(
    const sfnt_font_view& font,
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::span<const std::uint16_t> rows,
    std::uint32_t component_count,
    bool ignore_marks,
    const open_type_gdef_view* gdef,
    bool track_fallback_marks,
    font_error* error) noexcept {
    const std::size_t stride = component_count + 2U;
    std::array<std::uint32_t, 3U> components{};
    for (std::uint32_t position = 0U; position < count; ++position) {
        const auto first_glyph = storage[position].glyph_id;
        for (std::size_t row = 0U; row + stride <= rows.size();
             row += stride) {
            std::uint16_t expected_first = 0U;
            if (!font.try_get_glyph_index(rows[row], expected_first)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (expected_first == 0U || first_glyph != expected_first) continue;
            components[0U] = position;
            std::uint32_t candidate = position;
            bool matched = true;
            for (std::uint32_t component = 0U;
                 component < component_count;
                 ++component) {
                std::uint16_t expected = 0U;
                if (!font.try_get_glyph_index(
                        rows[row + 1U + component], expected)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                if (expected == 0U) {
                    matched = false;
                    break;
                }
                candidate = next_component(
                    std::span<const shaping_glyph>{storage.data(), count},
                    candidate + 1U,
                    ignore_marks,
                    gdef);
                if (candidate >= count || storage[candidate].glyph_id != expected) {
                    matched = false;
                    break;
                }
                components[component + 1U] = candidate;
            }
            std::uint16_t ligature = 0U;
            if (!font.try_get_glyph_index(
                    rows[row + 1U + component_count], ligature)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (!matched || ligature == 0U) continue;
            replace_ligature(
                storage,
                count,
                std::span<const std::uint32_t>{components}.first(
                    component_count + 1U),
                ligature,
                track_fallback_marks);
            break;
        }
    }
    return true;
}

} // namespace

bool try_apply_arabic_fallback(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    arabic_fallback_options options,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    auto glyphs = glyph_storage.first(glyph_count);
    for (auto& glyph : glyphs) {
        int form = -1;
        switch (get_arabic_action(glyph)) {
            case open_type_arabic_action::initial:
                if (options.initial) form = 0;
                break;
            case open_type_arabic_action::medial:
                if (options.medial) form = 1;
                break;
            case open_type_arabic_action::final:
                if (options.final) form = 2;
                break;
            case open_type_arabic_action::isolated:
                if (options.isolated) form = 3;
                break;
            default:
                break;
        }
        if (form < 0 || glyph.code_point < first_code_point ||
            glyph.code_point > last_code_point) {
            continue;
        }
        const auto table_index =
            (glyph.code_point - first_code_point) * 4U +
            static_cast<std::uint32_t>(form);
        const auto presentation =
            detail::arabic_fallback_shaping_forms[table_index];
        if (presentation == 0U) continue;
        std::uint16_t original = 0U;
        std::uint16_t replacement = 0U;
        if (!font.try_get_glyph_index(glyph.code_point, original) ||
            !font.try_get_glyph_index(presentation, replacement)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (glyph.glyph_id == original && replacement != 0U &&
            replacement != original) {
            glyph.glyph_id = replacement;
        }
    }

    if (options.required_ligatures) {
        if (!try_apply_ligatures(
                font,
                glyph_storage,
                glyph_count,
                detail::arabic_fallback_three_component_ligatures,
                2U,
                true,
                gdef,
                options.track_fallback_marks,
                error) ||
            !try_apply_ligatures(
                font,
                glyph_storage,
                glyph_count,
                detail::arabic_fallback_two_component_ligatures,
                1U,
                true,
                gdef,
                options.track_fallback_marks,
                error) ||
            !try_apply_ligatures(
                font,
                glyph_storage,
                glyph_count,
                detail::arabic_fallback_mark_ligatures,
                1U,
                false,
                gdef,
                options.track_fallback_marks,
                error)) {
            return false;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::detail
