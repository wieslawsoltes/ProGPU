#include "progpu_native_text.hpp"

#include "progpu_native_fallback_marks_internal.hpp"
#include "progpu_native_open_type_complex_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned
// GlyphPositionBuffer.ApplyFallbackMarkPositioning and helpers at repository
// checkpoint 2b871936. Glyph/font storage remains borrowed and caller-owned.

namespace progpu::native::text {
namespace {

struct glyph_extents final {
    std::int32_t x_bearing = 0;
    std::int32_t y_bearing = 0;
    std::int32_t width = 0;
    std::int32_t height = 0;
};

struct metadata_view final {
    std::span<const fallback_mark_metadata> direct{};
    std::span<const shaping_attachment> attachments{};

    bool empty() const noexcept {
        return direct.empty() && attachments.empty();
    }

    std::uint8_t component_count(std::size_t index) const noexcept {
        return !direct.empty()
            ? direct[index].ligature_component_count
            : attachments[index].reserved0;
    }

    std::uint8_t component(std::size_t index) const noexcept {
        return !direct.empty()
            ? direct[index].ligature_component
            : attachments[index].reserved1;
    }

    bool positioned(std::size_t index) const noexcept {
        return !direct.empty()
            ? direct[index].positioned
            : attachments[index].reserved2 != 0U;
    }
};

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

std::int32_t clamp_i16(std::int64_t value) noexcept {
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(
        value,
        std::numeric_limits<std::int16_t>::min(),
        std::numeric_limits<std::int16_t>::max()));
}

std::int32_t add_clamped_i16(
    std::int32_t left,
    std::int64_t right) noexcept {
    return clamp_i16(static_cast<std::int64_t>(left) + right);
}

std::int32_t round_to_even(float value) noexcept {
    const auto lower = std::floor(value);
    const auto fraction = value - lower;
    if (fraction < 0.5F) return static_cast<std::int32_t>(lower);
    if (fraction > 0.5F) return static_cast<std::int32_t>(lower + 1.0F);
    return static_cast<std::int32_t>(
        std::fmod(lower, 2.0F) == 0.0F ? lower : lower + 1.0F);
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

bool is_unicode_mark(std::uint32_t code_point) noexcept {
    const auto grapheme = get_unicode_grapheme_break_class(code_point);
    return get_unicode_bidi_class(code_point) ==
            unicode_bidi_class::nonspacing_mark ||
        grapheme == unicode_grapheme_break_class::spacing_mark ||
        get_unicode_canonical_combining_class(code_point) != 0U;
}

std::int32_t recategorize_combining_class(
    std::uint32_t code_point,
    std::int32_t combining_class) noexcept {
    if (combining_class >= 200) return combining_class;
    if ((code_point & ~0xFFU) == 0x0E00U) {
        if (combining_class == 0) {
            switch (code_point) {
                case 0x0E31U: case 0x0E34U: case 0x0E35U:
                case 0x0E36U: case 0x0E37U: case 0x0E47U:
                case 0x0E4CU: case 0x0E4DU: case 0x0E4EU:
                    combining_class = 232;
                    break;
                case 0x0EB1U: case 0x0EB4U: case 0x0EB5U:
                case 0x0EB6U: case 0x0EB7U: case 0x0EBBU:
                case 0x0ECCU: case 0x0ECDU:
                    combining_class = 230;
                    break;
                case 0x0EBCU:
                    combining_class = 220;
                    break;
                default:
                    break;
            }
        } else if (code_point == 0x0E3AU) {
            combining_class = 222;
        }
    }
    switch (combining_class) {
        case 22: case 15: case 16: case 17: case 23: case 18: case 19:
        case 20: case 21: case 24: case 25: case 30: case 33: case 118:
        case 129: case 132:
            return 220;
        case 13:
            return 214;
        case 10: case 103: case 107:
            return 232;
        case 11: case 14:
            return 228;
        case 26: case 28: case 29: case 31: case 32: case 27: case 34:
        case 35: case 36: case 122: case 130:
            return 230;
        default:
            return combining_class;
    }
}

bool try_get_extents(
    const sfnt_font_view& font,
    std::uint16_t glyph,
    std::span<const std::int16_t> coordinates,
    fallback_mark_positioning_scratch* scratch,
    glyph_extents& result,
    bool& found,
    font_error* error) noexcept {
    sfnt_glyph_bounds bounds{};
    found = false;
    if (scratch == nullptr) {
        found = font.try_get_glyph_bounds(glyph, bounds);
    } else if (!font.try_get_outline_bounds(
            glyph,
            coordinates,
            scratch->outline_bounds,
            bounds,
            found,
            error)) {
        result = {};
        return false;
    }
    if (!found) {
        result = {};
        return true;
    }
    result = glyph_extents{
        bounds.x_min,
        bounds.y_max,
        static_cast<std::int32_t>(bounds.x_max) - bounds.x_min,
        static_cast<std::int32_t>(bounds.y_min) - bounds.y_max};
    return true;
}

void mark_unsafe_to_break(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    if (end - start < 2U) return;
    std::int32_t minimum = glyphs[start].cluster;
    for (std::size_t index = start + 1U; index < end; ++index) {
        minimum = std::min(minimum, glyphs[index].cluster);
    }
    constexpr auto dependency =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    for (std::size_t index = start; index < end; ++index) {
        if (glyphs[index].cluster != minimum) {
            glyphs[index].flags = static_cast<shaping_glyph_flags>(
                static_cast<std::uint32_t>(glyphs[index].flags) |
                dependency);
        }
    }
}

std::int32_t position_above(
    glyph_extents& base,
    const glyph_extents& mark,
    std::int32_t gap) noexcept {
    std::int32_t offset = base.y_bearing -
        (mark.y_bearing + mark.height);
    if ((gap > 0) != (offset > 0)) {
        const std::int32_t correction = -offset / 2;
        base.y_bearing += correction;
        base.height -= correction;
        offset += correction;
    }
    base.y_bearing -= mark.height;
    base.height += mark.height;
    return offset;
}

bool position_mark(
    const sfnt_font_view& font,
    shaping_glyph& glyph,
    std::int32_t combining_class,
    glyph_extents& base,
    shaping_direction direction,
    std::int32_t units_per_em,
    std::span<const std::int16_t> coordinates,
    fallback_mark_positioning_scratch* scratch,
    font_error* error) noexcept {
    glyph_extents mark{};
    bool found = false;
    if (!try_get_extents(
            font,
            static_cast<std::uint16_t>(glyph.glyph_id),
            coordinates,
            scratch,
            mark,
            found,
            error)) {
        return false;
    }
    if (!found) return true;
    std::int32_t offset_x = 0;
    if ((combining_class == 233 || combining_class == 234) &&
        direction == shaping_direction::left_to_right) {
        offset_x = base.x_bearing + base.width - mark.width / 2 -
            mark.x_bearing;
    } else if ((combining_class == 233 || combining_class == 234) &&
        direction == shaping_direction::right_to_left) {
        offset_x = base.x_bearing - mark.width / 2 - mark.x_bearing;
    } else if (combining_class == 200 || combining_class == 218 ||
        combining_class == 228) {
        offset_x = base.x_bearing - mark.x_bearing;
    } else if (combining_class == 216 || combining_class == 222 ||
        combining_class == 232) {
        offset_x = base.x_bearing + base.width - mark.width -
            mark.x_bearing;
    } else {
        offset_x = base.x_bearing + (base.width - mark.width) / 2 -
            mark.x_bearing;
    }

    const std::int32_t gap = units_per_em / 16;
    std::int32_t offset_y = 0;
    if (combining_class == 233 || combining_class == 218 ||
        combining_class == 220 || combining_class == 222) {
        base.height -= gap;
    }
    if (combining_class == 200 || combining_class == 202 ||
        combining_class == 218 || combining_class == 220 ||
        combining_class == 222 || combining_class == 233) {
        offset_y = base.y_bearing + base.height - mark.y_bearing;
        if ((gap > 0) == (offset_y > 0)) {
            base.height -= offset_y;
            offset_y = 0;
        }
        base.height += mark.height;
    } else if (combining_class == 228 || combining_class == 230 ||
        combining_class == 232 || combining_class == 234) {
        base.y_bearing += gap;
        base.height -= gap;
        offset_y = position_above(base, mark, gap);
    } else if (combining_class == 214 || combining_class == 216) {
        offset_y = position_above(base, mark, gap);
    }
    glyph.offset_x = clamp_i16(offset_x);
    glyph.offset_y = clamp_i16(offset_y);
    return true;
}

bool try_position_base_marks(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    metadata_view metadata,
    std::span<const std::int16_t> coordinates,
    std::size_t base_index,
    std::size_t end,
    std::int32_t units_per_em,
    fallback_mark_positioning_scratch* scratch,
    font_error* error) noexcept {
    glyph_extents base{};
    bool found = false;
    if (!try_get_extents(
            font,
            static_cast<std::uint16_t>(glyphs[base_index].glyph_id),
            coordinates,
            scratch,
            base,
            found,
            error)) {
        return false;
    }
    if (!found) {
        return true;
    }
    float advance_width = 0.0F;
    const auto glyph =
        static_cast<std::uint16_t>(glyphs[base_index].glyph_id);
    const bool has_advance = scratch == nullptr
        ? font.try_get_design_advance_width(
            glyph, coordinates, advance_width, error)
        : font.try_get_design_advance_width(
            glyph,
            coordinates,
            advance_width,
            scratch->advance_width,
            error);
    if (!has_advance) {
        return false;
    }
    mark_unsafe_to_break(glyphs, base_index, end);
    base.y_bearing += glyphs[base_index].offset_y;
    base.x_bearing = 0;
    base.width = round_to_even(advance_width);

    std::int64_t x_offset = 0;
    std::int64_t y_offset = 0;
    const bool forward = direction == shaping_direction::left_to_right ||
        direction == shaping_direction::top_to_bottom;
    if (forward) {
        x_offset -= glyphs[base_index].advance_x;
        y_offset -= glyphs[base_index].advance_y;
    }

    std::int32_t last_class = 255;
    std::int32_t last_component = -1;
    glyph_extents class_extents = base;
    glyph_extents component_extents = base;
    const std::uint8_t component_count = metadata.empty()
        ? 0U
        : metadata.component_count(base_index);
    for (std::size_t index = base_index + 1U; index < end; ++index) {
        if (!metadata.empty() && metadata.positioned(index)) continue;
        const std::int32_t combining_class = recategorize_combining_class(
            glyphs[index].code_point,
            complex_detail::modified_combining_class(
                glyphs[index].code_point));
        if (combining_class == 0) {
            const std::int32_t sign = forward ? -1 : 1;
            x_offset += static_cast<std::int64_t>(sign) *
                glyphs[index].advance_x;
            y_offset += static_cast<std::int64_t>(sign) *
                glyphs[index].advance_y;
            continue;
        }
        if (component_count > 1U) {
            const std::uint8_t raw_component = metadata.component(index);
            const std::int32_t component = raw_component == 0xFFU
                ? component_count - 1
                : std::min<std::int32_t>(
                    raw_component, component_count - 1);
            if (last_component != component) {
                last_component = component;
                last_class = 255;
                component_extents = base;
                if (direction == shaping_direction::left_to_right) {
                    component_extents.x_bearing +=
                        component * component_extents.width / component_count;
                } else {
                    component_extents.x_bearing +=
                        (component_count - 1 - component) *
                        component_extents.width / component_count;
                }
                component_extents.width /= component_count;
            }
        }
        if (last_class != combining_class) {
            last_class = combining_class;
            class_extents = component_extents;
        }
        if (!position_mark(
            font,
            glyphs[index],
            combining_class,
            class_extents,
            direction,
            units_per_em,
            coordinates,
            scratch,
            error)) {
            return false;
        }
        glyphs[index].advance_x = 0;
        glyphs[index].advance_y = 0;
        glyphs[index].offset_x = add_clamped_i16(
            glyphs[index].offset_x, x_offset);
        glyphs[index].offset_y = add_clamped_i16(
            glyphs[index].offset_y, y_offset);
    }
    return true;
}

bool try_apply_fallback_mark_positioning_core(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    metadata_view metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    if (direction == shaping_direction::unspecified) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (const auto& glyph : glyphs) {
        if (glyph.glyph_id > 0xFFFFU) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
    }
    sfnt_header_metrics header{};
    if (!font.try_get_header_metrics(header) || header.units_per_em == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    std::size_t cluster_start = 0U;
    for (std::size_t index = 1U; index <= glyphs.size(); ++index) {
        if (index < glyphs.size() &&
            (is_unicode_mark(glyphs[index].code_point) ||
                is_default_ignorable(glyphs[index].code_point))) {
            continue;
        }
        if (index - cluster_start >= 2U) {
            for (std::size_t base = cluster_start; base < index;) {
                if (is_unicode_mark(glyphs[base].code_point)) {
                    ++base;
                    continue;
                }
                std::size_t mark_end = base + 1U;
                while (mark_end < index &&
                    (is_unicode_mark(glyphs[mark_end].code_point) ||
                        is_default_ignorable(
                            glyphs[mark_end].code_point))) {
                    ++mark_end;
                }
                if (!try_position_base_marks(
                        font,
                        glyphs,
                        direction,
                        metadata,
                        normalized_coordinates,
                        base,
                        mark_end,
                        header.units_per_em,
                        scratch,
                        error)) {
                    return false;
                }
                base = mark_end;
            }
        }
        cluster_start = index;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace

bool try_apply_fallback_mark_positioning(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const fallback_mark_metadata> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    font_error* error) noexcept {
    if (!metadata.empty() && metadata.size() < glyphs.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    return try_apply_fallback_mark_positioning_core(
        font,
        glyphs,
        direction,
        metadata_view{metadata, {}},
        normalized_coordinates,
        nullptr,
        error);
}

bool try_apply_fallback_mark_positioning(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const fallback_mark_metadata> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch& scratch,
    font_error* error) noexcept {
    if (!metadata.empty() && metadata.size() < glyphs.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    return try_apply_fallback_mark_positioning_core(
        font,
        glyphs,
        direction,
        metadata_view{metadata, {}},
        normalized_coordinates,
        &scratch,
        error);
}

bool detail::try_apply_fallback_mark_positioning_from_attachments(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const shaping_attachment> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    font_error* error) noexcept {
    if (metadata.size() < glyphs.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    return try_apply_fallback_mark_positioning_core(
        font,
        glyphs,
        direction,
        metadata_view{{}, metadata},
        normalized_coordinates,
        scratch,
        error);
}

} // namespace progpu::native::text
