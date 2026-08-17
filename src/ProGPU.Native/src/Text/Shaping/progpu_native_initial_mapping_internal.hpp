#ifndef PROGPU_NATIVE_INITIAL_MAPPING_INTERNAL_HPP
#define PROGPU_NATIVE_INITIAL_MAPPING_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct initial_mapping final {
    std::uint32_t prefix = 0U;
    std::uint32_t single = 0U;
    std::span<const std::byte> decomposition{};

    std::size_t size() const noexcept;
    std::uint32_t code_point_at(std::size_t index) const noexcept;
};

bool try_resolve_initial_mapping(
    const sfnt_font_view& font,
    std::uint32_t code_point,
    open_type_complex_script complex_script,
    const unicode_normalization_data* normalization,
    initial_mapping& result,
    font_error* error) noexcept;

bool try_get_initial_mapping_count(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    open_type_complex_script complex_script,
    const unicode_normalization_data* normalization,
    std::uint32_t& result,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
