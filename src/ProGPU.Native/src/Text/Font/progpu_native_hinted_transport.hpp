#ifndef PROGPU_NATIVE_HINTED_TRANSPORT_HPP
#define PROGPU_NATIVE_HINTED_TRANSPORT_HPP

#include "progpu_native_hinted_font.hpp"
#include "progpu_native_text_hinting.h"

namespace progpu::native::text {

enum class hinted_transport_policy { automatic, intrinsic_simd, scalar_reference };
enum class hinted_transport_path { bulk_copy, intrinsic_simd, scalar_reference };
enum class hinted_transport_error { none, invalid_argument, invalid_batch, insufficient_capacity, unsupported_policy };
struct hinted_transport_result final {
    hinted_transport_error error = hinted_transport_error::none;
    hinted_transport_path path = hinted_transport_path::scalar_reference;
    explicit operator bool() const noexcept { return error == hinted_transport_error::none; }
};

// Requirement output is unchanged if any later glyph is invalid or overflows.
hinted_transport_error get_hinted_batch_counts(const hinted_glyph_batch& batch,
    progpu_native_hinted_batch_counts& counts) noexcept;

// Full output-capacity guard shared with native design-vector projection.
bool hinted_batch_output_aliases(const hinted_glyph_batch& batch,
    std::span<hinted_outline_point> output) noexcept;
bool hinted_batch_output_aliases(const hinted_glyph_batch& batch,
    std::span<std::byte> output) noexcept;

// One retained generation, one atomic preflight, no allocation or font/GPU work.
// Tags share the point offsets. Contour ends remain glyph-local original indices.
// Every unused caller slot remains untouched, including successful empty input.
hinted_transport_result copy_hinted_batch(const hinted_glyph_batch& batch,
    std::span<progpu_native_hinted_glyph> glyphs,
    std::span<progpu_native_hinted_point> points,
    std::span<std::uint8_t> tags, std::span<std::int32_t> contour_ends,
    hinted_transport_policy policy) noexcept;

} // namespace progpu::native::text

#endif
