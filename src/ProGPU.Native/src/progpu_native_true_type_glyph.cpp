#include "progpu_native_text.hpp"
#include "progpu_native_font_bytes.hpp"

#include <limits>

// Direct native port of the ProGPU-owned simple-glyph parsing contract in
// TtfFont.cs. The native API uses a two-pass caller-buffer contract so the
// decoder remains allocation-free across the managed/native boundary.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;

constexpr std::uint8_t x_short_flag = 0x02U;
constexpr std::uint8_t y_short_flag = 0x04U;
constexpr std::uint8_t repeat_flag = 0x08U;
constexpr std::uint8_t x_same_or_positive_flag = 0x10U;
constexpr std::uint8_t y_same_or_positive_flag = 0x20U;

struct simple_glyph_layout final {
    std::uint16_t contour_count = 0U;
    std::uint32_t point_count = 0U;
    std::uint32_t path_segment_count = 0U;
    std::uint16_t instruction_bytes = 0U;
    std::size_t contour_offset = 10U;
    std::size_t flag_offset = 0U;
    std::size_t x_offset = 0U;
    std::size_t y_offset = 0U;
};

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool checked_add(
    std::size_t value,
    std::size_t increment,
    std::size_t& result) noexcept {
    if (increment > std::numeric_limits<std::size_t>::max() - value) {
        return false;
    }
    result = value + increment;
    return true;
}

bool inspect_simple_glyph(
    const sfnt_glyph_data_view& glyph,
    simple_glyph_layout& layout) noexcept {
    layout = {};
    if (glyph.contour_count <= 0 || glyph.bytes.size() < 10U) {
        return false;
    }

    layout.contour_count = static_cast<std::uint16_t>(glyph.contour_count);
    const auto contour_bytes =
        static_cast<std::size_t>(layout.contour_count) * 2U;
    std::size_t instruction_length_offset = 0U;
    if (!checked_add(layout.contour_offset, contour_bytes,
            instruction_length_offset) ||
        !can_read(glyph.bytes, layout.contour_offset, contour_bytes) ||
        !can_read(glyph.bytes, instruction_length_offset, 2U)) {
        return false;
    }

    std::uint16_t previous_end = 0U;
    for (std::uint16_t index = 0U; index < layout.contour_count; ++index) {
        const auto end = read_u16(
            glyph.bytes,
            layout.contour_offset + static_cast<std::size_t>(index) * 2U);
        if (index > 0U && end <= previous_end) {
            return false;
        }
        previous_end = end;
    }
    layout.point_count = static_cast<std::uint32_t>(previous_end) + 1U;
    layout.instruction_bytes =
        read_u16(glyph.bytes, instruction_length_offset);

    std::size_t instruction_offset = 0U;
    if (!checked_add(instruction_length_offset, 2U, instruction_offset) ||
        !can_read(glyph.bytes, instruction_offset, layout.instruction_bytes) ||
        !checked_add(instruction_offset, layout.instruction_bytes,
            layout.flag_offset)) {
        return false;
    }

    auto cursor = layout.flag_offset;
    std::uint32_t expanded = 0U;
    std::uint16_t contour_index = 0U;
    std::uint32_t contour_start = 0U;
    std::uint32_t off_to_on_transitions = 0U;
    bool first_on_curve = false;
    bool previous_on_curve = false;
    std::size_t x_bytes = 0U;
    std::size_t y_bytes = 0U;
    while (expanded < layout.point_count) {
        if (!can_read(glyph.bytes, cursor, 1U)) {
            return false;
        }
        const auto flag = std::to_integer<std::uint8_t>(glyph.bytes[cursor++]);
        std::uint32_t copies = 1U;
        if ((flag & repeat_flag) != 0U) {
            if (!can_read(glyph.bytes, cursor, 1U)) {
                return false;
            }
            copies += std::to_integer<std::uint8_t>(glyph.bytes[cursor++]);
        }
        if (copies > layout.point_count - expanded) {
            return false;
        }
        const auto x_per_point = (flag & x_short_flag) != 0U
            ? 1U
            : ((flag & x_same_or_positive_flag) != 0U ? 0U : 2U);
        const auto y_per_point = (flag & y_short_flag) != 0U
            ? 1U
            : ((flag & y_same_or_positive_flag) != 0U ? 0U : 2U);
        const auto x_increment = static_cast<std::size_t>(copies) * x_per_point;
        const auto y_increment = static_cast<std::size_t>(copies) * y_per_point;
        if (!checked_add(x_bytes, x_increment, x_bytes) ||
            !checked_add(y_bytes, y_increment, y_bytes)) {
            return false;
        }
        for (std::uint32_t copy = 0U; copy < copies; ++copy) {
            const auto on_curve = (flag & 0x01U) != 0U;
            if (expanded == contour_start) {
                first_on_curve = on_curve;
                previous_on_curve = on_curve;
                off_to_on_transitions = 0U;
            } else {
                if (!previous_on_curve && on_curve) {
                    ++off_to_on_transitions;
                }
                previous_on_curve = on_curve;
            }
            ++expanded;
            const auto contour_end = read_u16(
                glyph.bytes,
                layout.contour_offset +
                    static_cast<std::size_t>(contour_index) * 2U);
            if (expanded - 1U == contour_end) {
                const auto contour_points = expanded - contour_start;
                if (contour_points >= 2U) {
                    if (!previous_on_curve && first_on_curve) {
                        ++off_to_on_transitions;
                    }
                    layout.path_segment_count +=
                        contour_points - off_to_on_transitions;
                }
                contour_start = expanded;
                ++contour_index;
            }
        }
    }

    layout.x_offset = cursor;
    if (!can_read(glyph.bytes, layout.x_offset, x_bytes) ||
        !checked_add(layout.x_offset, x_bytes, layout.y_offset) ||
        !can_read(glyph.bytes, layout.y_offset, y_bytes)) {
        return false;
    }
    return true;
}

bool add_delta(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::uint8_t flag,
    std::uint8_t short_mask,
    std::uint8_t same_or_positive_mask,
    std::int32_t& coordinate) noexcept {
    if ((flag & short_mask) != 0U) {
        if (!can_read(bytes, cursor, 1U)) {
            return false;
        }
        const auto magnitude =
            static_cast<std::int32_t>(std::to_integer<std::uint8_t>(
                bytes[cursor++]));
        coordinate += (flag & same_or_positive_mask) != 0U
            ? magnitude
            : -magnitude;
        return true;
    }
    if ((flag & same_or_positive_mask) != 0U) {
        return true;
    }
    if (!can_read(bytes, cursor, 2U)) {
        return false;
    }
    coordinate += static_cast<std::int16_t>(read_u16(bytes, cursor));
    cursor += 2U;
    return true;
}

} // namespace

bool sfnt_font_view::try_get_glyph_decode_requirements(
    std::uint16_t glyph_index,
    sfnt_glyph_decode_requirements& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_glyph_data_view glyph{};
    if (!try_get_glyph_data(glyph_index, glyph)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    if (glyph.empty() || glyph.contour_count == 0) {
        result.kind = sfnt_glyph_kind::empty;
        return true;
    }
    if (glyph.contour_count < 0) {
        result.kind = sfnt_glyph_kind::composite;
        return true;
    }
    simple_glyph_layout layout{};
    if (!inspect_simple_glyph(glyph, layout)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = sfnt_glyph_decode_requirements{
        sfnt_glyph_kind::simple,
        layout.contour_count,
        layout.point_count,
        layout.path_segment_count,
        layout.instruction_bytes};
    return true;
}

bool sfnt_font_view::try_decode_simple_glyph(
    std::uint16_t glyph_index,
    std::span<std::uint16_t> contour_end_points,
    std::span<sfnt_outline_point> points,
    font_error* error) const noexcept {
    set_error(error, font_error::none);
    sfnt_glyph_data_view glyph{};
    if (!try_get_glyph_data(glyph_index, glyph) ||
        glyph.empty() || glyph.contour_count <= 0) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    simple_glyph_layout layout{};
    if (!inspect_simple_glyph(glyph, layout)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    if (contour_end_points.size() < layout.contour_count ||
        points.size() < layout.point_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (std::uint16_t index = 0U; index < layout.contour_count; ++index) {
        contour_end_points[index] = read_u16(
            glyph.bytes,
            layout.contour_offset + static_cast<std::size_t>(index) * 2U);
    }

    auto flag_cursor = layout.flag_offset;
    std::uint32_t point_index = 0U;
    while (point_index < layout.point_count) {
        const auto flag =
            std::to_integer<std::uint8_t>(glyph.bytes[flag_cursor++]);
        std::uint32_t copies = 1U;
        if ((flag & repeat_flag) != 0U) {
            copies += std::to_integer<std::uint8_t>(
                glyph.bytes[flag_cursor++]);
        }
        for (std::uint32_t copy = 0U; copy < copies; ++copy) {
            points[point_index++] = sfnt_outline_point{0, 0, flag};
        }
    }

    auto x_cursor = layout.x_offset;
    std::int32_t x = 0;
    for (std::uint32_t index = 0U; index < layout.point_count; ++index) {
        if (!add_delta(
                glyph.bytes,
                x_cursor,
                points[index].flags,
                x_short_flag,
                x_same_or_positive_flag,
                x)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        points[index].x = x;
    }

    auto y_cursor = layout.y_offset;
    std::int32_t y = 0;
    for (std::uint32_t index = 0U; index < layout.point_count; ++index) {
        if (!add_delta(
                glyph.bytes,
                y_cursor,
                points[index].flags,
                y_short_flag,
                y_same_or_positive_flag,
                y)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        points[index].y = y;
    }
    return true;
}

} // namespace progpu::native::text
