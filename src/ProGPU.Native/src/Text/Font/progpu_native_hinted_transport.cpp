#include "progpu_native_hinted_transport.hpp"

#include <array>
#include <cstddef>
#include <cstring>
#include <limits>
#include <type_traits>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

namespace progpu::native::text {
namespace {
static_assert(sizeof(long) == sizeof(std::int32_t) || sizeof(long) == sizeof(std::int64_t));
static_assert(sizeof(int) == sizeof(std::uint32_t));
static_assert(sizeof(progpu_native_hinted_point) == 16U);
static_assert(offsetof(progpu_native_hinted_point, y_26_6) == 8U);
static_assert(sizeof(progpu_native_hinted_glyph) == 136U);
static_assert(sizeof(progpu_native_hinted_batch_counts) == 12U);
static_assert(offsetof(progpu_native_hinted_glyph, advance_x_26_6) == 24U);
static_assert(std::is_standard_layout_v<progpu_native_hinted_glyph>);
static_assert(std::is_trivially_copyable_v<hinted_outline_point>);
static_assert(sizeof(hinted_outline_point) == 2U * sizeof(long));
static_assert(offsetof(hinted_outline_point, y_26_6) == sizeof(long));

struct memory_range final {
    std::uintptr_t start = 0U;
    std::uintptr_t end = 0U;
};

bool range(const void* data, std::size_t count, std::size_t width, std::size_t alignment,
    memory_range& result) noexcept
{
    const auto start = reinterpret_cast<std::uintptr_t>(data);
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    if (width == 0U || count > maximum / width ||
        (count != 0U && (data == nullptr || start % alignment != 0U)) ||
        count * width > maximum - start) return false;
    result = {start, start + count * width};
    return true;
}

template<class T>
bool range(std::span<T> values, memory_range& result) noexcept
{
    return range(values.data(), values.size(), sizeof(T), alignof(T), result);
}

bool overlaps(memory_range a, memory_range b) noexcept
{
    return a.start != a.end && b.start != b.end && a.start < b.end && b.start < a.end;
}

template<std::size_t N, class T>
bool aliases(const std::array<memory_range, N>& output, const T* data, std::size_t count) noexcept
{
    memory_range input{};
    if (!range(data, count, sizeof(T), alignof(T), input)) return true;
    for (const auto value : output) if (overlaps(value, input)) return true;
    return false;
}

template<std::size_t N>
bool aliases_batch(const std::array<memory_range, N>& output, const hinted_glyph_batch& batch) noexcept
{
    if (aliases(output, &batch, 1U) || aliases(output, batch.glyphs.data(), batch.glyphs.size())) return true;
    if (batch.identity != nullptr && (aliases(output, batch.identity.get(), 1U) ||
        aliases(output, batch.identity->variation_coordinates_16_16.data(),
            batch.identity->variation_coordinates_16_16.size()))) return true;
    if (batch.identity != nullptr && batch.identity->source != nullptr &&
        (aliases(output, batch.identity->source.get(), 1U) ||
            aliases(output, batch.identity->source->bytes.data(), batch.identity->source->bytes.size()))) return true;
    for (const auto& glyph : batch.glyphs)
        if (aliases(output, glyph.points.data(), glyph.points.size()) ||
            aliases(output, glyph.tags.data(), glyph.tags.size()) ||
            aliases(output, glyph.contour_ends.data(), glyph.contour_ends.size())) return true;
    return false;
}

constexpr bool has_intrinsics =
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    true;
#else
    false;
#endif

void copy_points(std::span<const hinted_outline_point> input,
    progpu_native_hinted_point* output, hinted_transport_path path) noexcept
{
    if constexpr (sizeof(long) == sizeof(std::int64_t)) {
        if (path == hinted_transport_path::bulk_copy) {
            if (!input.empty()) std::memcpy(output, input.data(), input.size_bytes());
            return;
        }
    }
    std::size_t index = 0U;
    if (path == hinted_transport_path::intrinsic_simd) {
        if constexpr (sizeof(long) == sizeof(std::int32_t)) {
            // Windows native-long pairs widen exactly, with no float stage.
            for (; input.size() - index >= 2U; index += 2U) {
#if defined(__aarch64__) || defined(_M_ARM64)
                int32x4_t lanes{};
                std::memcpy(&lanes, input.data() + index, sizeof(lanes));
                const auto low = vmovl_s32(vget_low_s32(lanes));
                const auto high = vmovl_s32(vget_high_s32(lanes));
                std::memcpy(output + index, &low, sizeof(low));
                std::memcpy(output + index + 1U, &high, sizeof(high));
#elif defined(__SSE2__) || defined(_M_X64)
                const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
                const auto sign = _mm_srai_epi32(lanes, 31);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(output + index), _mm_unpacklo_epi32(lanes, sign));
                _mm_storeu_si128(reinterpret_cast<__m128i*>(output + index + 1U), _mm_unpackhi_epi32(lanes, sign));
#endif
            }
        } else {
            for (; index < input.size(); ++index) {
#if defined(__aarch64__) || defined(_M_ARM64)
                int64x2_t lanes{};
                std::memcpy(&lanes, input.data() + index, sizeof(lanes));
                std::memcpy(output + index, &lanes, sizeof(lanes));
#elif defined(__SSE2__) || defined(_M_X64)
                const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
                _mm_storeu_si128(reinterpret_cast<__m128i*>(output + index), lanes);
#endif
            }
        }
    }
    for (; index < input.size(); ++index)
        output[index] = {static_cast<std::int64_t>(input[index].x_26_6), static_cast<std::int64_t>(input[index].y_26_6)};
}

void copy_contours(std::span<const std::int16_t> input, std::int32_t* output,
    hinted_transport_path path) noexcept
{
    std::size_t index = 0U;
    if (path != hinted_transport_path::scalar_reference) {
        for (; input.size() - index >= 8U; index += 8U) {
#if defined(__aarch64__) || defined(_M_ARM64)
            const auto lanes = vld1q_s16(input.data() + index);
            vst1q_s32(output + index, vmovl_s16(vget_low_s16(lanes)));
            vst1q_s32(output + index + 4U, vmovl_s16(vget_high_s16(lanes)));
#elif defined(__SSE2__) || defined(_M_X64)
            const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
            const auto sign = _mm_srai_epi16(lanes, 15);
            _mm_storeu_si128(reinterpret_cast<__m128i*>(output + index), _mm_unpacklo_epi16(lanes, sign));
            _mm_storeu_si128(reinterpret_cast<__m128i*>(output + index + 4U), _mm_unpackhi_epi16(lanes, sign));
#else
            for (std::size_t lane = 0U; lane < 8U; ++lane) output[index + lane] = input[index + lane];
#endif
        }
    }
    for (; index < input.size(); ++index) output[index] = input[index];
}
} // namespace

hinted_transport_error get_hinted_batch_counts(const hinted_glyph_batch& batch,
    progpu_native_hinted_batch_counts& counts) noexcept
{
    std::array<memory_range, 1> output{};
    if (!range(&counts, 1U, sizeof(counts), alignof(progpu_native_hinted_batch_counts), output[0]) ||
        aliases_batch(output, batch)) return hinted_transport_error::invalid_argument;
    if (batch.identity == nullptr || batch.identity->source == nullptr ||
        batch.glyphs.size() > std::numeric_limits<std::uint32_t>::max())
        return hinted_transport_error::invalid_batch;
    progpu_native_hinted_batch_counts candidate{static_cast<std::uint32_t>(batch.glyphs.size()), 0U, 0U};
    // Prefix offsets and topology depend on each preceding contour/glyph.
    for (const auto& glyph : batch.glyphs) {
        if (glyph.tags.size() != glyph.points.size() ||
            glyph.points.size() > std::numeric_limits<std::uint32_t>::max() - candidate.points ||
            glyph.contour_ends.size() > std::numeric_limits<std::uint32_t>::max() - candidate.contours ||
            (glyph.points.empty() != glyph.contour_ends.empty()))
            return hinted_transport_error::invalid_batch;
        std::int32_t previous = -1;
        for (const auto end : glyph.contour_ends) {
            if (end <= previous || static_cast<std::size_t>(end) >= glyph.points.size())
                return hinted_transport_error::invalid_batch;
            previous = end;
        }
        if (!glyph.points.empty() && static_cast<std::size_t>(previous) != glyph.points.size() - 1U)
            return hinted_transport_error::invalid_batch;
        candidate.points += static_cast<std::uint32_t>(glyph.points.size());
        candidate.contours += static_cast<std::uint32_t>(glyph.contour_ends.size());
    }
    counts = candidate;
    return hinted_transport_error::none;
}

hinted_transport_result copy_hinted_batch(const hinted_glyph_batch& batch,
    std::span<progpu_native_hinted_glyph> glyphs, std::span<progpu_native_hinted_point> points,
    std::span<std::uint8_t> tags, std::span<std::int32_t> contour_ends,
    hinted_transport_policy policy) noexcept
{
    if (policy != hinted_transport_policy::automatic && policy != hinted_transport_policy::intrinsic_simd &&
        policy != hinted_transport_policy::scalar_reference) return {hinted_transport_error::invalid_argument};
    if (policy == hinted_transport_policy::intrinsic_simd && !has_intrinsics)
        return {hinted_transport_error::unsupported_policy};
    progpu_native_hinted_batch_counts counts{};
    const auto count_error = get_hinted_batch_counts(batch, counts);
    if (count_error != hinted_transport_error::none) return {count_error};
    if (glyphs.size() < counts.glyphs || points.size() < counts.points || tags.size() < counts.points ||
        contour_ends.size() < counts.contours) return {hinted_transport_error::insufficient_capacity};
    std::array<memory_range, 4> output{};
    if (!range(glyphs, output[0]) || !range(points, output[1]) || !range(tags, output[2]) ||
        !range(contour_ends, output[3])) return {hinted_transport_error::invalid_argument};
    for (std::size_t first = 0U; first < output.size(); ++first)
        for (std::size_t next = first + 1U; next < output.size(); ++next)
            if (overlaps(output[first], output[next])) return {hinted_transport_error::invalid_argument};
    if (aliases_batch(output, batch)) return {hinted_transport_error::invalid_argument};
    const auto selected = policy == hinted_transport_policy::scalar_reference ? hinted_transport_path::scalar_reference :
        (policy == hinted_transport_policy::intrinsic_simd || sizeof(long) != sizeof(std::int64_t)) && has_intrinsics ?
            hinted_transport_path::intrinsic_simd :
            sizeof(long) == sizeof(std::int64_t) ? hinted_transport_path::bulk_copy : hinted_transport_path::scalar_reference;
    std::size_t glyph_offset = 0U;
    std::uint32_t point_offset = 0U, contour_offset = 0U;
    for (const auto& glyph : batch.glyphs) {
        // All capacities/aliases/topology are proven before the first write.
        glyphs[glyph_offset++] = {glyph.glyph_index, point_offset, static_cast<std::uint32_t>(glyph.points.size()),
            contour_offset, static_cast<std::uint32_t>(glyph.contour_ends.size()), static_cast<std::uint32_t>(glyph.outline_flags),
            glyph.advance_x_26_6, glyph.advance_y_26_6, glyph.horizontal_bearing_x_26_6, glyph.horizontal_bearing_y_26_6,
            glyph.width_26_6, glyph.height_26_6, glyph.horizontal_advance_26_6, glyph.vertical_bearing_x_26_6,
            glyph.vertical_bearing_y_26_6, glyph.vertical_advance_26_6, glyph.linear_horizontal_advance_16_16,
            glyph.linear_vertical_advance_16_16, glyph.left_side_bearing_delta_26_6, glyph.right_side_bearing_delta_26_6};
        if (!glyph.points.empty()) {
            copy_points(glyph.points, points.data() + point_offset, selected);
            std::memcpy(tags.data() + point_offset, glyph.tags.data(), glyph.tags.size());
        }
        if (!glyph.contour_ends.empty()) copy_contours(glyph.contour_ends, contour_ends.data() + contour_offset, selected);
        point_offset += static_cast<std::uint32_t>(glyph.points.size());
        contour_offset += static_cast<std::uint32_t>(glyph.contour_ends.size());
    }
    return {hinted_transport_error::none, selected};
}

} // namespace progpu::native::text
