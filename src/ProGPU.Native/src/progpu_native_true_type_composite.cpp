#include "progpu_native_text.hpp"
#include "progpu_native_font_bytes.hpp"

// Direct native port of the component-record parser in
// ProGPU.Text.TtfFont.ParseCompositeGlyphOutline. Recursive outline expansion
// is deliberately a separate slice; this layer only validates and decodes the
// caller-owned fixed records without allocating.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;

constexpr std::uint16_t arguments_are_words = 0x0001U;
constexpr std::uint16_t arguments_are_xy_values = 0x0002U;
constexpr std::uint16_t we_have_scale = 0x0008U;
constexpr std::uint16_t more_components = 0x0020U;
constexpr std::uint16_t we_have_x_and_y_scale = 0x0040U;
constexpr std::uint16_t we_have_two_by_two = 0x0080U;
constexpr std::uint16_t we_have_instructions = 0x0100U;

struct composite_layout final {
    std::uint32_t component_count = 0U;
    std::uint16_t instruction_bytes = 0U;
};

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

float read_f2_dot_14(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<float>(read_i16(bytes, offset)) / 16384.0F;
}

std::int32_t read_i8(std::byte value) noexcept {
    const auto unsigned_value = std::to_integer<std::uint8_t>(value);
    return unsigned_value < 0x80U
        ? static_cast<std::int32_t>(unsigned_value)
        : static_cast<std::int32_t>(unsigned_value) - 0x100;
}

bool read_component(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    sfnt_composite_component* destination) noexcept {
    if (!can_read(bytes, cursor, 4U)) {
        return false;
    }
    sfnt_composite_component component{};
    component.flags = read_u16(bytes, cursor);
    component.glyph_index = read_u16(bytes, cursor + 2U);
    cursor += 4U;

    const auto argument_bytes =
        (component.flags & arguments_are_words) != 0U ? 4U : 2U;
    if (!can_read(bytes, cursor, argument_bytes)) {
        return false;
    }
    if ((component.flags & arguments_are_words) != 0U) {
        if ((component.flags & arguments_are_xy_values) != 0U) {
            component.argument1 = read_i16(bytes, cursor);
            component.argument2 = read_i16(bytes, cursor + 2U);
        } else {
            component.argument1 = read_u16(bytes, cursor);
            component.argument2 = read_u16(bytes, cursor + 2U);
        }
    } else if ((component.flags & arguments_are_xy_values) != 0U) {
        component.argument1 = read_i8(bytes[cursor]);
        component.argument2 = read_i8(bytes[cursor + 1U]);
    } else {
        component.argument1 = std::to_integer<std::uint8_t>(bytes[cursor]);
        component.argument2 =
            std::to_integer<std::uint8_t>(bytes[cursor + 1U]);
    }
    cursor += argument_bytes;

    if ((component.flags & we_have_scale) != 0U) {
        if (!can_read(bytes, cursor, 2U)) {
            return false;
        }
        component.m00 = read_f2_dot_14(bytes, cursor);
        component.m11 = component.m00;
        cursor += 2U;
    } else if ((component.flags & we_have_x_and_y_scale) != 0U) {
        if (!can_read(bytes, cursor, 4U)) {
            return false;
        }
        component.m00 = read_f2_dot_14(bytes, cursor);
        component.m11 = read_f2_dot_14(bytes, cursor + 2U);
        cursor += 4U;
    } else if ((component.flags & we_have_two_by_two) != 0U) {
        if (!can_read(bytes, cursor, 8U)) {
            return false;
        }
        component.m00 = read_f2_dot_14(bytes, cursor);
        component.m01 = read_f2_dot_14(bytes, cursor + 2U);
        component.m10 = read_f2_dot_14(bytes, cursor + 4U);
        component.m11 = read_f2_dot_14(bytes, cursor + 6U);
        cursor += 8U;
    }
    if (destination != nullptr) {
        *destination = component;
    }
    return true;
}

bool inspect_composite(
    const sfnt_glyph_data_view& glyph,
    composite_layout& result,
    std::span<sfnt_composite_component> components) noexcept {
    result = {};
    if (glyph.empty() || glyph.contour_count >= 0 || glyph.bytes.size() < 10U) {
        return false;
    }
    auto cursor = static_cast<std::size_t>(10U);
    std::uint16_t flags = more_components;
    while ((flags & more_components) != 0U) {
        sfnt_composite_component component{};
        if (!read_component(glyph.bytes, cursor, &component)) {
            return false;
        }
        flags = component.flags;
        if (!components.empty()) {
            if (result.component_count >= components.size()) {
                return false;
            }
            components[result.component_count] = component;
        }
        ++result.component_count;
    }
    if ((flags & we_have_instructions) != 0U) {
        if (!can_read(glyph.bytes, cursor, 2U)) {
            return false;
        }
        result.instruction_bytes = read_u16(glyph.bytes, cursor);
        cursor += 2U;
        if (!can_read(glyph.bytes, cursor, result.instruction_bytes)) {
            return false;
        }
    }
    return true;
}

} // namespace

bool sfnt_font_view::try_get_composite_glyph_decode_requirements(
    std::uint16_t glyph_index,
    sfnt_composite_glyph_decode_requirements& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_glyph_data_view glyph{};
    composite_layout layout{};
    if (!try_get_glyph_data(glyph_index, glyph) ||
        !inspect_composite(glyph, layout, {})) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = sfnt_composite_glyph_decode_requirements{
        layout.component_count,
        layout.instruction_bytes};
    return true;
}

bool sfnt_font_view::try_decode_composite_glyph(
    std::uint16_t glyph_index,
    std::span<sfnt_composite_component> components,
    font_error* error) const noexcept {
    set_error(error, font_error::none);
    sfnt_composite_glyph_decode_requirements requirements{};
    if (!try_get_composite_glyph_decode_requirements(
            glyph_index, requirements, error)) {
        return false;
    }
    if (components.size() < requirements.component_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_glyph_data_view glyph{};
    composite_layout layout{};
    if (!try_get_glyph_data(glyph_index, glyph) ||
        !inspect_composite(
            glyph,
            layout,
            components.first(requirements.component_count))) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    return true;
}

} // namespace progpu::native::text
