#ifndef PROGPU_NATIVE_TRUE_TYPE_COMPOSITE_INTERNAL_HPP
#define PROGPU_NATIVE_TRUE_TYPE_COMPOSITE_INTERNAL_HPP

#include "progpu_native_text.hpp"

namespace progpu::native::text::detail {

inline constexpr std::uint16_t composite_arguments_are_words = 0x0001U;
inline constexpr std::uint16_t composite_arguments_are_xy_values = 0x0002U;
inline constexpr std::uint16_t composite_round_xy_to_grid = 0x0004U;
inline constexpr std::uint16_t composite_we_have_scale = 0x0008U;
inline constexpr std::uint16_t composite_more_components = 0x0020U;
inline constexpr std::uint16_t composite_we_have_x_and_y_scale = 0x0040U;
inline constexpr std::uint16_t composite_we_have_two_by_two = 0x0080U;
inline constexpr std::uint16_t composite_we_have_instructions = 0x0100U;
inline constexpr std::uint16_t composite_scaled_component_offset = 0x0800U;

bool read_composite_component(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    sfnt_composite_component* destination) noexcept;

} // namespace progpu::native::text::detail

#endif
