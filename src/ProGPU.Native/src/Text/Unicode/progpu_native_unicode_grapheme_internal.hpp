#ifndef PROGPU_NATIVE_UNICODE_GRAPHEME_INTERNAL_HPP
#define PROGPU_NATIVE_UNICODE_GRAPHEME_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

// ProGPU's managed shaper currently receives StringInfo boundaries without
// UAX #29 GB9c conjunct joining. Keep this shaping-only compatibility path
// separate from the public Unicode 17 grapheme API, which remains revision 47.
bool try_segment_managed_compatible_graphemes(
    std::span<const unicode_scalar> input,
    std::span<unicode_grapheme_cluster> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

} // namespace progpu::native::text::detail

#endif
