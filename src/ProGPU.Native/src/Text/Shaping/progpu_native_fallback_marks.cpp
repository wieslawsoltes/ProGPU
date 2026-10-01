#include "progpu_native_text.hpp"

#include "progpu_native_fallback_marks_internal.hpp"
#include "progpu_native_open_type_complex_internal.hpp"
#include "progpu_native_open_type_gpos_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

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
    const auto category = get_unicode_general_category(code_point);
    return category == unicode_general_category::nonspacing_mark ||
        category == unicode_general_category::spacing_combining_mark ||
        category == unicode_general_category::enclosing_mark;
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

// The design policy keeps the original helpers, metric queries and int16
// clamping. Only the shared attachment walk below selects a different policy.
struct design_mark_policy final {
    bool try_get_base(
        const sfnt_font_view& font, std::size_t, std::uint16_t glyph,
        std::span<const std::int16_t> coordinates,
        fallback_mark_positioning_scratch* scratch, glyph_extents& base,
        std::int32_t& width, bool& found, font_error* error) const noexcept {
        if (!try_get_extents(font, glyph, coordinates, scratch, base, found,
                error)) return false;
        if (!found) return true;
        float advance_width = 0.0F;
        const bool has_advance = scratch == nullptr
            ? font.try_get_design_advance_width(
                glyph, coordinates, advance_width, error)
            : font.try_get_design_advance_width(
                glyph, coordinates, advance_width, scratch->advance_width,
                error);
        if (!has_advance) return false;
        width = round_to_even(advance_width);
        return true;
    }

    bool prepare_base(glyph_extents& base, const shaping_glyph& glyph,
        std::int32_t width, font_error*) const noexcept {
        base.y_bearing += glyph.offset_y;
        base.x_bearing = 0;
        base.width = width;
        return true;
    }

    bool split_component(glyph_extents& extents, std::int32_t component,
        std::uint8_t count, shaping_direction direction,
        font_error*) const noexcept {
        if (direction == shaping_direction::left_to_right) {
            extents.x_bearing += component * extents.width / count;
        } else {
            extents.x_bearing += (count - 1 - component) * extents.width / count;
        }
        extents.width /= count;
        return true;
    }

    bool accumulate_advance(const shaping_glyph& glyph, std::int32_t sign,
        std::int64_t& x, std::int64_t& y, font_error*) const noexcept {
        x += static_cast<std::int64_t>(sign) * glyph.advance_x;
        y += static_cast<std::int64_t>(sign) * glyph.advance_y;
        return true;
    }

    bool position(const sfnt_font_view& font, shaping_glyph& glyph,
        std::size_t, std::int32_t combining_class, glyph_extents& base,
        shaping_direction direction, std::int32_t units_per_em,
        std::span<const std::int16_t> coordinates,
        fallback_mark_positioning_scratch* scratch, std::int64_t x_offset,
        std::int64_t y_offset, font_error* error) const noexcept {
        if (!position_mark(font, glyph, combining_class, base, direction,
                units_per_em, coordinates, scratch, error)) return false;
        glyph.advance_x = 0;
        glyph.advance_y = 0;
        glyph.offset_x = add_clamped_i16(glyph.offset_x, x_offset);
        glyph.offset_y = add_clamped_i16(glyph.offset_y, y_offset);
        return true;
    }
};

bool checked_i32(std::int64_t value, std::int32_t& output) noexcept {
    if (value < std::numeric_limits<std::int32_t>::min() ||
        value > std::numeric_limits<std::int32_t>::max()) return false;
    output = static_cast<std::int32_t>(value);
    return true;
}

bool checked_add_i64(std::int64_t left, std::int64_t right,
    std::int64_t& output) noexcept {
    if ((right > 0 && left > std::numeric_limits<std::int64_t>::max() - right) ||
        (right < 0 && left < std::numeric_limits<std::int64_t>::min() - right))
        return false;
    output = left + right;
    return true;
}

// Device offsets can exceed int16 and intermediate geometry can exceed int32.
// Two independent signed-64 lanes preserve cancellation before the final
// signed-32 publication check. Neither path publishes a partial pair.
bool add_device_mark_offsets(const std::array<std::int64_t, 2>& values,
    const std::array<std::int64_t, 2>& deltas,
    detail::gpos_arithmetic_path path,
    std::array<std::int32_t, 2>& output) noexcept {
    std::array<std::int64_t, 2> sums{};
    if (path == detail::gpos_arithmetic_path::scalar_reference) {
        for (std::size_t index = 0U; index < sums.size(); ++index)
            if (!checked_add_i64(values[index], deltas[index], sums[index]))
                return false;
    } else {
#if defined(__aarch64__) || defined(_M_ARM64)
        const auto left = vld1q_s64(values.data());
        const auto right = vld1q_s64(deltas.data());
        const auto sum = vaddq_s64(left, right);
        std::array<std::int64_t, 2> overflow{};
        vst1q_s64(overflow.data(), vbicq_s64(veorq_s64(sum, left),
            veorq_s64(left, right)));
        if (overflow[0] < 0 || overflow[1] < 0) return false;
        vst1q_s64(sums.data(), sum);
#elif defined(__SSE2__) || defined(_M_X64)
        const auto left = _mm_loadu_si128(
            reinterpret_cast<const __m128i*>(values.data()));
        const auto right = _mm_loadu_si128(
            reinterpret_cast<const __m128i*>(deltas.data()));
        const auto sum = _mm_add_epi64(left, right);
        std::array<std::int64_t, 2> overflow{};
        _mm_storeu_si128(reinterpret_cast<__m128i*>(overflow.data()),
            _mm_andnot_si128(_mm_xor_si128(left, right),
                _mm_xor_si128(sum, left)));
        if (overflow[0] < 0 || overflow[1] < 0) return false;
        _mm_storeu_si128(reinterpret_cast<__m128i*>(sums.data()), sum);
#else
        return false;
#endif
    }
    std::array<std::int32_t, 2> candidate{};
    if (!checked_i32(sums[0], candidate[0]) ||
        !checked_i32(sums[1], candidate[1])) return false;
    output = candidate;
    return true;
}

struct device_mark_policy final {
    const detail::gpos_device_frame& frame;
    const detail::device_mark_extents& provider;
    std::int32_t gap;

    bool try_get_captured(std::size_t index, std::uint32_t glyph,
        detail::device_mark_bounds& bounds, bool& found,
        font_error* error) const noexcept {
        found = false;
        if (!provider.try_get_extents(provider.owner, index, glyph, bounds,
                found) || (found && (bounds.width < 0 ||
                bounds.negative_height > 0))) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        return true;
    }

    bool try_get_base(const sfnt_font_view&, std::size_t index,
        std::uint16_t glyph, std::span<const std::int16_t>,
        fallback_mark_positioning_scratch*, glyph_extents& base,
        std::int32_t& width, bool& found, font_error* error) const noexcept {
        detail::device_mark_bounds bounds{};
        if (!try_get_captured(index, glyph, bounds, found, error)) return false;
        if (!found) return true;
        base = {bounds.x_bearing, bounds.y_bearing, bounds.width,
            bounds.negative_height};
        width = bounds.horizontal_advance_26_6;
        return true;
    }

    bool prepare_base(glyph_extents& base, const shaping_glyph& glyph,
        std::int32_t width, font_error* error) const noexcept {
        if (!checked_i32(static_cast<std::int64_t>(base.y_bearing) +
                glyph.offset_y, base.y_bearing)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        base.x_bearing = 0;
        base.width = width;
        return true;
    }

    bool split_component(glyph_extents& extents, std::int32_t component,
        std::uint8_t count, shaping_direction direction,
        font_error* error) const noexcept {
        const auto selected = direction == shaping_direction::left_to_right
            ? component : count - 1 - component;
        if (!checked_i32(static_cast<std::int64_t>(extents.x_bearing) +
                static_cast<std::int64_t>(selected) * extents.width / count,
                extents.x_bearing)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        extents.width /= count;
        return true;
    }

    bool accumulate_advance(const shaping_glyph& glyph, std::int32_t sign,
        std::int64_t& x, std::int64_t& y, font_error* error) const noexcept {
        std::int64_t next_x = 0, next_y = 0;
        if (!checked_add_i64(x, static_cast<std::int64_t>(sign) *
                glyph.advance_x, next_x) ||
            !checked_add_i64(y, static_cast<std::int64_t>(sign) *
                glyph.advance_y, next_y)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        x = next_x;
        y = next_y;
        return true;
    }

    bool position(const sfnt_font_view&, shaping_glyph& glyph,
        std::size_t index, std::int32_t combining_class, glyph_extents& base,
        shaping_direction direction, std::int32_t,
        std::span<const std::int16_t>, fallback_mark_positioning_scratch*,
        std::int64_t x_offset, std::int64_t y_offset,
        font_error* error) const noexcept {
        detail::device_mark_bounds mark{};
        bool found = false;
        if (!try_get_captured(index, glyph.glyph_id, mark, found, error))
            return false;
        std::int64_t offset_x = glyph.offset_x;
        std::int64_t offset_y = glyph.offset_y;
        std::int64_t next_y = base.y_bearing;
        std::int64_t next_height = base.height;
        if (found) {
            const std::int64_t base_x = base.x_bearing;
            const std::int64_t base_width = base.width;
            if ((combining_class == 233 || combining_class == 234) &&
                direction == shaping_direction::left_to_right) {
                offset_x = base_x + base_width - mark.width / 2 - mark.x_bearing;
            } else if ((combining_class == 233 || combining_class == 234) &&
                direction == shaping_direction::right_to_left) {
                offset_x = base_x - mark.width / 2 - mark.x_bearing;
            } else if (combining_class == 200 || combining_class == 218 ||
                combining_class == 228) {
                offset_x = base_x - mark.x_bearing;
            } else if (combining_class == 216 || combining_class == 222 ||
                combining_class == 232) {
                offset_x = base_x + base_width - mark.width - mark.x_bearing;
            } else {
                offset_x = base_x + (base_width - mark.width) / 2 - mark.x_bearing;
            }
            offset_y = 0;
            if (combining_class == 233 || combining_class == 218 ||
                combining_class == 220 || combining_class == 222)
                next_height -= gap;
            if (combining_class == 200 || combining_class == 202 ||
                combining_class == 218 || combining_class == 220 ||
                combining_class == 222 || combining_class == 233) {
                offset_y = next_y + next_height - mark.y_bearing;
                if ((gap > 0) == (offset_y > 0)) {
                    next_height -= offset_y;
                    offset_y = 0;
                }
                next_height += mark.negative_height;
            } else if (combining_class == 228 || combining_class == 230 ||
                combining_class == 232 || combining_class == 234 ||
                combining_class == 214 || combining_class == 216) {
                if (combining_class == 228 || combining_class == 230 ||
                    combining_class == 232 || combining_class == 234) {
                    next_y += gap;
                    next_height -= gap;
                }
                offset_y = next_y - (static_cast<std::int64_t>(mark.y_bearing) +
                    mark.negative_height);
                if ((gap > 0) != (offset_y > 0)) {
                    const auto correction = -offset_y / 2;
                    next_y += correction;
                    next_height -= correction;
                    offset_y += correction;
                }
                next_y -= mark.negative_height;
                next_height += mark.negative_height;
            }
        }
        glyph_extents candidate_base = base;
        std::array<std::int32_t, 2> offsets{};
        if (!checked_i32(next_y, candidate_base.y_bearing) ||
            !checked_i32(next_height, candidate_base.height) ||
            !add_device_mark_offsets({offset_x, offset_y}, {x_offset, y_offset},
                frame.arithmetic_path, offsets)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        // Each mark's offsets and zero advances publish together after all its
        // checks. Earlier marks/dependency flags may already have published.
        base = candidate_base;
        glyph.offset_x = offsets[0];
        glyph.offset_y = offsets[1];
        glyph.advance_x = 0;
        glyph.advance_y = 0;
        return true;
    }
};

template<typename PositioningPolicy>
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
    font_error* error,
    const PositioningPolicy& policy) noexcept {
    glyph_extents base{};
    std::int32_t width = 0;
    bool found = false;
    if (!policy.try_get_base(
            font,
            base_index,
            static_cast<std::uint16_t>(glyphs[base_index].glyph_id),
            coordinates,
            scratch,
            base,
            width,
            found,
            error)) {
        return false;
    }
    if (!found) {
        return true;
    }
    mark_unsafe_to_break(glyphs, base_index, end);
    if (!policy.prepare_base(base, glyphs[base_index], width, error)) return false;

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
            if (!policy.accumulate_advance(glyphs[index], sign, x_offset,
                    y_offset, error)) return false;
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
                if (!policy.split_component(component_extents, component,
                        component_count, direction, error)) return false;
            }
        }
        if (last_class != combining_class) {
            last_class = combining_class;
            class_extents = component_extents;
        }
        if (!policy.position(
            font,
            glyphs[index],
            index,
            combining_class,
            class_extents,
            direction,
            units_per_em,
            coordinates,
            scratch,
            x_offset,
            y_offset,
            error)) {
            return false;
        }
    }
    return true;
}

template<typename PositioningPolicy>
bool try_apply_fallback_mark_positioning_with_policy(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    metadata_view metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    font_error* error,
    const PositioningPolicy& policy) noexcept {
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
                        error,
                        policy)) {
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

bool try_apply_fallback_mark_positioning_core(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    metadata_view metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    font_error* error) noexcept {
    return try_apply_fallback_mark_positioning_with_policy(font, glyphs,
        direction, metadata, normalized_coordinates, scratch, error,
        design_mark_policy{});
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

bool detail::try_apply_device_fallback_mark_positioning_from_attachments(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const shaping_attachment> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    const gpos_device_frame& frame,
    const device_mark_extents& extents,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    if (frame.font != &font || font.data().empty() ||
        frame.owner == nullptr || frame.project_design == nullptr ||
        extents.owner == nullptr || extents.try_get_extents == nullptr ||
        metadata.size() < glyphs.size() ||
        normalized_coordinates.size() != frame.normalized_coordinates.size() ||
        (direction != shaping_direction::left_to_right &&
         direction != shaping_direction::right_to_left &&
         direction != shaping_direction::top_to_bottom &&
         direction != shaping_direction::bottom_to_top) ||
        (frame.arithmetic_path != gpos_arithmetic_path::intrinsic_simd &&
         frame.arithmetic_path != gpos_arithmetic_path::scalar_reference)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
    if (frame.arithmetic_path == gpos_arithmetic_path::intrinsic_simd) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
#endif
    const auto valid_coordinates = [](std::span<const std::int16_t> values) noexcept {
        constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
        const auto address = reinterpret_cast<std::uintptr_t>(values.data());
        return values.size() <= maximum / sizeof(std::int16_t) &&
            (values.empty() || (values.data() != nullptr &&
                address % alignof(std::int16_t) == 0U)) &&
            values.size_bytes() <= maximum - address;
    };
    if (!valid_coordinates(normalized_coordinates) ||
        !valid_coordinates(frame.normalized_coordinates)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (std::size_t index = 0U; index < normalized_coordinates.size(); ++index)
        if (normalized_coordinates[index] != frame.normalized_coordinates[index]) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    for (const auto& glyph : glyphs)
        if (glyph.glyph_id > 0xFFFFU) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
    sfnt_header_metrics header{};
    if (!font.try_get_header_metrics(header) || header.units_per_em == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    // The original integer UPM gap is projected exactly once. Device metrics
    // never receive an unprojected design-unit gap or a projected design width.
    const std::array<gpos_metric_vector, 2> design_gap{{
        {0, header.units_per_em / 16}, {}}};
    std::array<gpos_metric_vector, 2> projected_gap{};
    if (!frame.project_design(frame.owner, design_gap, projected_gap) ||
        projected_gap[0].x != 0 || projected_gap[1].x != 0 ||
        projected_gap[1].y != 0) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    return try_apply_fallback_mark_positioning_with_policy(font, glyphs,
        direction, metadata_view{{}, metadata}, normalized_coordinates,
        scratch, error, device_mark_policy{frame, extents, projected_gap[0].y});
}

} // namespace progpu::native::text
