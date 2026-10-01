#include "progpu_native_hinted_run_metrics.hpp"
#include "progpu_native_hinted_transport.hpp"

#include <array>
#include <cstring>
#include <limits>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

namespace progpu::native::text {
namespace {
bool i32(std::int64_t value, std::int32_t& output) noexcept {
    if (value < std::numeric_limits<std::int32_t>::min() ||
        value > std::numeric_limits<std::int32_t>::max()) return false;
    output = static_cast<std::int32_t>(value);
    return true;
}

bool metrics(const hinted_glyph& glyph, bool vertical,
    std::array<std::int32_t, 4>& output) noexcept {
    output = {};
    if (!vertical) return i32(glyph.advance_x_26_6, output[0]) &&
        i32(glyph.advance_y_26_6, output[1]);
    // Prove operands before signed64 differences/negation on LP64. The public
    // slot's vertical Y bearing is downward, unlike horizontal Y bearing.
    std::int32_t advance = 0, hx = 0, hy = 0, vx = 0, vy = 0;
    if (!i32(glyph.vertical_advance_26_6, advance) ||
        !i32(glyph.horizontal_bearing_x_26_6, hx) ||
        !i32(glyph.horizontal_bearing_y_26_6, hy) ||
        !i32(glyph.vertical_bearing_x_26_6, vx) ||
        !i32(glyph.vertical_bearing_y_26_6, vy)) return false;
    return i32(-static_cast<std::int64_t>(advance), output[1]) &&
        i32(static_cast<std::int64_t>(vx) - hx, output[2]) &&
        i32(-static_cast<std::int64_t>(hy) - vy, output[3]);
}

void publish(shaping_glyph& glyph, const std::array<std::int32_t, 4>& values,
    detail::gpos_arithmetic_path path) noexcept {
    static_assert(sizeof(shaping_glyph) == 32U);
    static_assert(offsetof(shaping_glyph, advance_x) == 16U);
    static_assert(offsetof(shaping_glyph, advance_y) == 20U);
    static_assert(offsetof(shaping_glyph, offset_x) == 24U);
    static_assert(offsetof(shaping_glyph, offset_y) == 28U);
    auto* destination = reinterpret_cast<std::byte*>(&glyph) + offsetof(shaping_glyph, advance_x);
    if (path == detail::gpos_arithmetic_path::intrinsic_simd) {
#if defined(__aarch64__) || defined(_M_ARM64)
        const auto lanes = vld1q_s32(values.data());
        std::memcpy(destination, &lanes, sizeof(lanes));
        return;
#elif defined(__SSE2__) || defined(_M_X64)
        const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(values.data()));
        _mm_storeu_si128(reinterpret_cast<__m128i*>(destination), lanes);
        return;
#endif
    }
    std::memcpy(destination, values.data(), sizeof(values));
}
} // namespace

hinted_gpos_frame_result initialize_hinted_run_metrics(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, shaping_direction direction, std::span<shaping_glyph> glyphs,
    hinted_projection_policy policy, std::span<const std::int16_t> normalized_coordinates) noexcept
{
    return initialize_hinted_run_metrics(batch, font, direction, glyphs,
        batch.glyphs.size(), policy, normalized_coordinates);
}

hinted_gpos_frame_result initialize_hinted_run_metrics(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, shaping_direction direction, std::span<shaping_glyph> glyphs,
    std::size_t source_descriptor_count, hinted_projection_policy policy,
    std::span<const std::int16_t> normalized_coordinates) noexcept
{
    if (direction != shaping_direction::left_to_right && direction != shaping_direction::right_to_left &&
        direction != shaping_direction::top_to_bottom && direction != shaping_direction::bottom_to_top)
        return {hinted_projection_error::invalid_argument, {}};
    if (source_descriptor_count > batch.glyphs.size())
        return {hinted_projection_error::invalid_argument, {}};
    const auto frame = bind_hinted_gpos_frame(batch, font, policy, normalized_coordinates);
    if (frame.error != hinted_projection_error::none) return frame;
    const auto start = reinterpret_cast<std::uintptr_t>(glyphs.data());
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    if (glyphs.size() > maximum / sizeof(shaping_glyph) ||
        (!glyphs.empty() && (glyphs.data() == nullptr || start % alignof(shaping_glyph) != 0U)) ||
        glyphs.size_bytes() > maximum - start)
        return {hinted_projection_error::invalid_argument, {}};
    if (glyphs.size() < source_descriptor_count) return {hinted_projection_error::insufficient_capacity, {}};
    if (hinted_batch_output_aliases(batch, std::as_writable_bytes(glyphs)))
        return {hinted_projection_error::invalid_argument, {}};
    const auto normalized_start = reinterpret_cast<std::uintptr_t>(normalized_coordinates.data());
    const auto normalized_bytes = normalized_coordinates.size_bytes();
    if (normalized_bytes > maximum - normalized_start ||
        (!glyphs.empty() && !normalized_coordinates.empty() &&
         start < normalized_start + normalized_bytes && normalized_start < start + glyphs.size_bytes()))
        return {hinted_projection_error::invalid_argument, {}};
    const bool vertical = direction == shaping_direction::top_to_bottom || direction == shaping_direction::bottom_to_top;
    std::array<std::int32_t, 4> values{};
    for (std::size_t index = 0U; index < source_descriptor_count; ++index) {
        if (glyphs[index].glyph_id != batch.glyphs[index].glyph_index || !metrics(batch.glyphs[index], vertical, values))
            return {hinted_projection_error::unsupported_frame, {}};
    }
    for (std::size_t index = 0U; index < source_descriptor_count; ++index) {
        (void)metrics(batch.glyphs[index], vertical, values);
        publish(glyphs[index], values, frame.frame.arithmetic_path);
    }
    return frame;
}
} // namespace progpu::native::text
